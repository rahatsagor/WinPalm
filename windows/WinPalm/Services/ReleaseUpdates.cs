using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinPalm.Services;

/// Only the desktop host talks to GitHub. No phone endpoint can install an update.
internal sealed class ReleaseUpdates : IDisposable
{
  internal const string Repository = "rahatsagor/WinPalm";
  internal const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
  internal const long MaxPackageBytes = 300L * 1024 * 1024;
  private readonly HttpClient client;
  internal ReleaseUpdates(HttpMessageHandler? handler = null)
  {
    client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinPalm", typeof(ReleaseUpdates).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
  }

  internal sealed record Release(Version Version, string Tag, string Notes, Uri Page, Asset? Package, Asset? Checksum);
  internal sealed record Asset(string Name, Uri Url, long Size);

  /// GitHub's latest endpoint intentionally excludes draft and prerelease builds.
  internal async Task<Release?> CheckAsync(Version current, string architecture, CancellationToken cancellation)
  {
    using var response = await GetAsync(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"), false, cancellation);
    if (response.StatusCode == HttpStatusCode.NotFound) return null; // A new repo may have no releases yet.
    response.EnsureSuccessStatusCode();
    var data = await ReadLimitedAsync(response.Content, 1024 * 1024, cancellation);
    using var json = JsonDocument.Parse(data);
    return Parse(json.RootElement, current, architecture);
  }

  internal static Release? Parse(JsonElement data, Version current, string architecture)
  {
    if (architecture is not ("x64" or "arm64")) throw new InvalidDataException("This processor is not supported by WinPalm updates.");
    if (data.GetProperty("draft").GetBoolean() || data.GetProperty("prerelease").GetBoolean()) return null;
    var tag = data.GetProperty("tag_name").GetString() ?? "";
    // A stable numeric tag also prevents paths or command text in release names.
    var match = Regex.Match(tag, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant);
    if (!match.Success || !Version.TryParse(tag.TrimStart('v'), out var version) || version <= current) return null;
    var page = new Uri($"https://github.com/{Repository}/releases/tag/{tag}");
    var name = $"WinPalm-{version.ToString(3)}-win-{architecture}.zip";
    Asset? Find(string expected)
    {
      var matches = data.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == expected).ToArray();
      if (matches.Length != 1) return null;
      var asset = matches[0];
      var size = asset.GetProperty("size").GetInt64();
      if (size <= 0 || size > (expected.EndsWith(".zip", StringComparison.Ordinal) ? MaxPackageBytes : 4096)) return null;
      if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url) || !IsReleaseAsset(url, tag, expected)) return null;
      return new Asset(expected, url, size);
    }
    var notes = data.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
    return new(version, tag, notes[..Math.Min(notes.Length, 16000)], page, Find(name), Find(name + ".sha256"));
  }

  private static bool IsReleaseAsset(Uri url, string tag, string name) =>
      url.Scheme == "https" && url.IsDefaultPort && url.Host == "github.com" && url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0
      && url.AbsolutePath == $"/{Repository}/releases/download/{tag}/{name}";

  internal async Task<string> DownloadAsync(Release release, string directory, IProgress<int>? progress, CancellationToken cancellation)
  {
    if (release.Package is not { } package || release.Checksum is not { } checksum) throw new InvalidOperationException("This release has no verified package for this PC. Open its release page instead.");
    if (!IsReleaseAsset(package.Url, release.Tag, package.Name) || !IsReleaseAsset(checksum.Url, release.Tag, checksum.Name)) throw new InvalidDataException("The update is not from the WinPalm release repository.");
    Directory.CreateDirectory(directory);
    var file = Path.Combine(directory, "package.zip");
    try
    {
      using var checksumResponse = await GetAsync(checksum.Url, true, cancellation);
      checksumResponse.EnsureSuccessStatusCode();
      var expected = ParseChecksum(Encoding.UTF8.GetString(await ReadLimitedAsync(checksumResponse.Content, 4096, cancellation)), package.Name);
      using var response = await GetAsync(package.Url, true, cancellation);
      response.EnsureSuccessStatusCode();
      if (response.Content.Headers.ContentLength is { } length && length != package.Size) throw new InvalidDataException("The update size does not match its release.");
      await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
      await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
      {
        var buffer = new byte[65536]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
        {
          total += count;
          if (total > package.Size || total > MaxPackageBytes) throw new InvalidDataException("The update download is too large.");
          await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
          progress?.Report((int)(total * 100 / package.Size));
        }
        if (total != package.Size) throw new InvalidDataException("The update download was incomplete.");
      }
      await using var downloaded = File.OpenRead(file);
      var actual = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellation));
      if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update checksum did not match. Your current WinPalm has not changed.");
      return file;
    }
    catch { if (File.Exists(file)) File.Delete(file); throw; }
  }

  internal static string ParseChecksum(string text, string filename)
  {
    var lines = text.TrimStart('\uFEFF').Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (lines.Length != 1) throw new InvalidDataException("The release checksum is invalid.");
    var parts = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 2 || parts[1].TrimStart('*') != filename || !Regex.IsMatch(parts[0], @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("The release checksum is invalid.");
    return parts[0];
  }

  private async Task<HttpResponseMessage> GetAsync(Uri uri, bool asset, CancellationToken cancellation)
  {
    for (var redirect = 0; redirect < 5; redirect++)
    {
      var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
      if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
      var location = response.Headers.Location;
      response.Dispose();
      if (!asset || location is null) throw new InvalidDataException("The update server returned an unexpected redirect.");
      uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
      if (!AllowedDownloadHost(uri)) throw new InvalidDataException("The update redirected outside GitHub's download servers.");
    }
    throw new InvalidDataException("The update download redirected too many times.");
  }

  internal static bool AllowedDownloadHost(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
      && uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

  private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximum, CancellationToken cancellation)
  {
    await using var stream = await content.ReadAsStreamAsync(cancellation);
    using var output = new MemoryStream();
    var buffer = new byte[8192]; int count;
    while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
    {
      if (output.Length + count > maximum) throw new InvalidDataException("The update response is too large.");
      output.Write(buffer, 0, count);
    }
    return output.ToArray();
  }
  public void Dispose() => client.Dispose();
}
