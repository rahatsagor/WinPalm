using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinPalm.Services;

if (args is ["--package", var archive, var architecture, var version])
{
  var temporary = Path.Combine(Path.GetTempPath(), "WinPalm-package-check-" + Guid.NewGuid().ToString("N"));
  try
  {
    UpdatePackage.Extract(archive, temporary);
    if (OperatingSystem.IsWindows())
    {
      UpdatePackage.ValidateBinaries(temporary, architecture, Version.Parse(version));
      Console.WriteLine($"PASS updater accepts {architecture} package {version}");
    }
    else
    {
      // FileVersionInfo on Unix cannot read a native Windows apphost's
      // version resource. The full version gate runs in the Windows build.
      UpdatePackage.ValidateArchitecture(temporary, architecture);
      Console.WriteLine($"PASS {architecture} archive and PE architecture; Win32 version check requires Windows");
    }
  }
  finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
  return;
}

var suite = new Tests();
await suite.RunAsync();

sealed class Tests
{
  private int passed;
  private int failed;
  // /var is a macOS symlink; the production updater intentionally rejects
  // linked ancestors, so use the canonical temporary directory there.
  private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "winpalm-update-tests", Guid.NewGuid().ToString("N"));

  internal async Task RunAsync()
  {
    Directory.CreateDirectory(root);
    try
    {
      Sync("release chooses exact architecture assets", ReleaseSelection);
      Sync("release rejects draft prerelease old and malformed versions", InvalidReleases);
      await Async("release rejects unavailable and foreign URLs", InvalidAssetUrls);
      Sync("checksum enforces filename and 64 hexadecimal characters", Checksums);
      await Async("download verifies expected bytes and hash", ValidDownload);
      await Async("download rejects altered checksum", BadHashDownload);
      await Async("download rejects truncated and oversized bodies", InvalidSizeDownloads);
      await Async("download rejects non-GitHub redirects", ForeignRedirect);
      await Async("download accepts GitHub CDN redirects", GitHubRedirect);
      await Async("download bounds streamed bodies without Content-Length", StreamedSizeLimits);
      await Async("latest-release checks and cancellation", LatestReleaseCheck);
      Sync("extract accepts complete package", ValidExtraction);
      Sync("extract requires all required package files", MissingRequiredFile);
      Sync("extract rejects traversal ADS reserved and unknown roots", UnsafeArchivePaths);
      Sync("extract rejects case-insensitive duplicates and symlinks", DuplicateAndLinkEntries);
      Sync("install preserves unrelated files and replaces managed files", InstallManagedFiles);
      Sync("install failure restores previous package", InstallFailureRestores);
      Sync("partial copy failure restores every previous file", PartialCopyFailure);
      Sync("rollback restores previous package", RollbackRestores);
      Sync("speech stream provides live length and position to System.Speech", SpeechStreamContract);
      await Async("speech reads fill across phone packets until end of input", SpeechPacketReads);
      await Async("stopping speech releases a blocked reader", SpeechStopReleasesRead);
    }
    finally { try { Directory.Delete(root, true); } catch { } }
    Console.WriteLine($"{passed} passed, {failed} failed");
    if (failed != 0) Environment.ExitCode = 1;
  }

  private void ReleaseSelection()
  {
    foreach (var architecture in new[] { "x64", "arm64" })
    {
      var release = Parse(ReleaseJson("v2.3.4", architecture), architecture: architecture);
      Equal(new Version(2, 3, 4), release?.Version);
      Equal($"WinPalm-2.3.4-win-{architecture}.zip", release?.Package?.Name);
      Equal($"WinPalm-2.3.4-win-{architecture}.zip.sha256", release?.Checksum?.Name);
      True(release?.Package?.Url.AbsolutePath.EndsWith("/" + release.Package.Name, StringComparison.Ordinal) == true);
    }
    Throws<InvalidDataException>(() => Parse(ReleaseJson("v2.3.4", "x86"), architecture: "x86"));
  }

  private void SpeechStreamContract()
  {
    using var stream = new PcmAudioStream();
    Equal(long.MaxValue, stream.Length); Equal(0L, stream.Position);
    Equal(0L, stream.Seek(0, SeekOrigin.Current));
    Equal(0L, stream.Seek(0, SeekOrigin.Begin));
    False(stream.CanSeek);
    False(stream.Append([1])); // 16-bit samples must be complete.
    True(stream.Append([1, 2, 3, 4])); stream.Complete();
    var bytes = new byte[4]; Equal(4, stream.Read(bytes, 0, bytes.Length));
    Sequence([1, 2, 3, 4], bytes); Equal(4L, stream.Position);
    Equal(4L, stream.Seek(0, SeekOrigin.Current));
    Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
    Equal(0, stream.Read(bytes, 0, bytes.Length)); False(stream.Append([5, 6]));
    Parallel.For(0, 8, _ => stream.Complete()); // Repeated cancellation is harmless.
  }

  private async Task SpeechPacketReads()
  {
    using var stream = new PcmAudioStream();
    var bytes = new byte[8];
    var reading = Task.Run(() => stream.Read(bytes, 0, bytes.Length));
    True(stream.Append([1, 2, 3, 4]));
    await Task.Delay(30); False(reading.IsCompleted); // No false EOF at the packet boundary.
    True(stream.Append([5, 6, 7, 8]));
    Equal(8, await reading.WaitAsync(TimeSpan.FromSeconds(2)));
    Sequence([1, 2, 3, 4, 5, 6, 7, 8], bytes); Equal(8L, stream.Position);
    True(stream.Append([9, 10])); stream.Complete();
    Equal(2, stream.Read(bytes, 0, bytes.Length)); Equal(10L, stream.Position);
  }

  private async Task SpeechStopReleasesRead()
  {
    using var stream = new PcmAudioStream();
    var reading = Task.Run(() => stream.Read(new byte[4096], 0, 4096));
    stream.Complete();
    Equal(0, await reading.WaitAsync(TimeSpan.FromSeconds(2)));
  }

  private void InvalidReleases()
  {
    True(Parse(ReleaseJson("v2.3.4", "x64", draft: true)) is null);
    True(Parse(ReleaseJson("v2.3.4", "x64", prerelease: true)) is null);
    True(Parse(ReleaseJson("v1.0.0", "x64"), new Version(1, 0, 0)) is null);
    foreach (var tag in new[] { "v1.2", "v1.2.3-rc.1", "v01.2.3", "v1.2.3/evil", "latest", "v1.2.3.4" })
      True(Parse(ReleaseJson(tag, "x64")) is null, $"accepted malformed tag {tag}");
  }

  private async Task InvalidAssetUrls()
  {
    var missing = Parse(ReleaseJson("v2.3.4", "x64", omitPackage: true));
    True(missing is not null && missing.Package is null);
    foreach (var url in new[] { "http://github.com/rahatsagor/WinPalm/releases/download/v2.3.4/WinPalm-2.3.4-win-x64.zip", "https://evil.example/file", "https://user@github.com/rahatsagor/WinPalm/releases/download/v2.3.4/WinPalm-2.3.4-win-x64.zip", "https://github.com/other/WinPalm/releases/download/v2.3.4/WinPalm-2.3.4-win-x64.zip" })
    {
      var release = Parse(ReleaseJson("v2.3.4", "x64", packageUrl: url));
      True(release?.Package is null, $"accepted {url}");
    }
    var handler = new FakeHandler((_, _) => throw new Exception("foreign package must not be requested"));
    using var updates = new ReleaseUpdates(handler);
    var good = ManualRelease(Encoding.UTF8.GetBytes("abc"));
    var foreignPackage = good with { Package = good.Package! with { Url = new Uri("https://evil.example/file") } };
    var foreignChecksum = good with { Checksum = good.Checksum! with { Url = new Uri("https://evil.example/file") } };
    var missingPackage = good with { Package = null };
    var missingChecksum = good with { Checksum = null };
    foreach (var invalid in new[] { foreignPackage, foreignChecksum })
      await ThrowsAsync<InvalidDataException>(() => updates.DownloadAsync(invalid, root, null, default));
    foreach (var invalid in new[] { missingPackage, missingChecksum })
      await ThrowsAsync<InvalidOperationException>(() => updates.DownloadAsync(invalid, root, null, default));
    Equal(0, handler.Requests.Count);
  }

  private void Checksums()
  {
    var filename = "WinPalm-2.3.4-win-x64.zip";
    var hash = new string('a', 64);
    Equal(hash, ReleaseUpdates.ParseChecksum(hash + "  " + filename + "\n", filename));
    Equal(hash.ToUpperInvariant(), ReleaseUpdates.ParseChecksum(hash.ToUpperInvariant() + " *" + filename, filename));
    foreach (var value in new[] { hash + "  other.zip", hash[..63] + "  " + filename, new string('g', 64) + "  " + filename, hash + "  " + filename + "\n" + hash + "  " + filename, hash + "  ../" + filename })
      Throws<InvalidDataException>(() => ReleaseUpdates.ParseChecksum(value, filename));
  }

  private async Task ValidDownload()
  {
    var bytes = Encoding.UTF8.GetBytes("verified package bytes");
    var release = ManualRelease(bytes);
    var handler = AssetHandler(release, bytes, HashLine(bytes, release.Package!.Name));
    using var updates = new ReleaseUpdates(handler);
    var file = await updates.DownloadAsync(release, Path.Combine(root, "download-valid"), null, default);
    Sequence(bytes, await File.ReadAllBytesAsync(file));
    Equal(2, handler.Requests.Count);
  }

  private async Task BadHashDownload()
  {
    var bytes = Encoding.UTF8.GetBytes("package");
    var release = ManualRelease(bytes);
    var handler = AssetHandler(release, bytes, new string('0', 64) + "  " + release.Package!.Name);
    using var updates = new ReleaseUpdates(handler);
    var dir = Path.Combine(root, "download-hash");
    await ThrowsAsync<InvalidDataException>(() => updates.DownloadAsync(release, dir, null, default));
    False(File.Exists(Path.Combine(dir, "package.zip")));
  }

  private async Task InvalidSizeDownloads()
  {
    var bytes = Encoding.UTF8.GetBytes("right size");
    var release = ManualRelease(bytes);
    var truncated = AssetHandler(release, bytes[..^1], HashLine(bytes, release.Package!.Name));
    using (var updates = new ReleaseUpdates(truncated))
      await ThrowsAsync<InvalidDataException>(() => updates.DownloadAsync(release, Path.Combine(root, "download-short"), null, default));
    var oversized = AssetHandler(release, [.. bytes, 1], HashLine(bytes, release.Package!.Name));
    using (var updates = new ReleaseUpdates(oversized))
      await ThrowsAsync<InvalidDataException>(() => updates.DownloadAsync(release, Path.Combine(root, "download-long"), null, default));
  }

  private async Task ForeignRedirect()
  {
    var bytes = Encoding.UTF8.GetBytes("package");
    var release = ManualRelease(bytes);
    var handler = new FakeHandler((request, _) => request.RequestUri == release.Checksum!.Url
      ? new(HttpStatusCode.OK) { Content = new StringContent(HashLine(bytes, release.Package!.Name)) }
      : new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/file") } });
    using var updates = new ReleaseUpdates(handler);
    await ThrowsAsync<InvalidDataException>(() => updates.DownloadAsync(release, Path.Combine(root, "download-redirect"), null, default));
    Equal(2, handler.Requests.Count);
  }

  private async Task GitHubRedirect()
  {
    var bytes = Encoding.UTF8.GetBytes("package"); var release = ManualRelease(bytes);
    var cdn = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/package?token=test");
    var handler = new FakeHandler((request, _) => request.RequestUri == release.Checksum!.Url
      ? new(HttpStatusCode.OK) { Content = new StringContent(HashLine(bytes, release.Package!.Name)) }
      : request.RequestUri == cdn ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
      : new(HttpStatusCode.Redirect) { Headers = { Location = cdn } });
    using var updates = new ReleaseUpdates(handler);
    var file = await updates.DownloadAsync(release, Path.Combine(root, "cdn"), null, default);
    Sequence(bytes, File.ReadAllBytes(file)); Equal(3, handler.Requests.Count);
    False(ReleaseUpdates.AllowedDownloadHost(new Uri("https://release-assets.githubusercontent.com.evil.example/file")));
    False(ReleaseUpdates.AllowedDownloadHost(new Uri("http://objects.githubusercontent.com/file")));
  }

  private async Task StreamedSizeLimits()
  {
    var bytes = Encoding.UTF8.GetBytes("package"); var release = ManualRelease(bytes);
    foreach (var actual in new[] { bytes[..^1], new byte[65536] })
    {
      var handler = new FakeHandler((request, _) => request.RequestUri == release.Checksum!.Url
        ? new(HttpStatusCode.OK) { Content = new StringContent(HashLine(bytes, release.Package!.Name)) }
        : new(HttpStatusCode.OK) { Content = new StreamContent(new UnseekableStream(actual)) });
      using var updates = new ReleaseUpdates(handler);
      var dir = Path.Combine(root, "stream-" + actual.Length);
      await ThrowsAsync<InvalidDataException>(() => updates.DownloadAsync(release, dir, null, default));
      False(File.Exists(Path.Combine(dir, "package.zip")));
    }
  }

  private async Task LatestReleaseCheck()
  {
    var handler = new FakeHandler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson("v2.3.4", "x64")) });
    using (var updates = new ReleaseUpdates(handler))
      Equal(new Version(2, 3, 4), (await updates.CheckAsync(new Version(1, 0, 0), "x64", default))?.Version);
    Equal("https://api.github.com/repos/rahatsagor/WinPalm/releases/latest", handler.Requests.Single()?.AbsoluteUri);
    using (var updates = new ReleaseUpdates(new FakeHandler((_, _) => new(HttpStatusCode.NotFound))))
      True(await updates.CheckAsync(new Version(1, 0, 0), "x64", default) is null);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    using var service = new ReleaseUpdates(new FakeHandler((_, token) => { token.ThrowIfCancellationRequested(); return new(HttpStatusCode.OK); }));
    await ThrowsAsync<OperationCanceledException>(() => service.CheckAsync(new Version(1, 0, 0), "x64", cancelled.Token));
  }

  private void ValidExtraction()
  {
    var archive = MakeZip("extract-valid", RequiredEntries());
    var destination = Path.Combine(root, "extracted-valid");
    UpdatePackage.Extract(archive, destination);
    True(File.Exists(Path.Combine(destination, "WinPalm.exe")));
    True(File.Exists(Path.Combine(destination, "web", "index.html")));
  }

  private void MissingRequiredFile()
  {
    var entries = RequiredEntries().Where(e => e.Name != "web/source.zip").ToArray();
    var archive = MakeZip("extract-missing", entries);
    Throws<InvalidDataException>(() => UpdatePackage.Extract(archive, Path.Combine(root, "extracted-missing")));
  }

  private void UnsafeArchivePaths()
  {
    foreach (var path in new[] { "../escape", "web/../../escape", "web/file:stream", "web/CON.txt", "unowned.txt", "other/file" })
    {
      var archive = MakeZip("unsafe-" + Guid.NewGuid().ToString("N"), [.. RequiredEntries(), new ZipEntryData(path, "x")]);
      Throws<InvalidDataException>(() => UpdatePackage.Extract(archive, Path.Combine(root, "unsafe-out-" + Guid.NewGuid().ToString("N"))), path);
    }
  }

  private void DuplicateAndLinkEntries()
  {
    var duplicate = MakeZip("duplicate", [.. RequiredEntries(), new ZipEntryData("web/index.html", "duplicate")]);
    Throws<InvalidDataException>(() => UpdatePackage.Extract(duplicate, Path.Combine(root, "duplicate-out")));
    var caseDuplicate = MakeZip("case-duplicate", [.. RequiredEntries(), new ZipEntryData("web/INDEX.HTML", "duplicate")]);
    Throws<InvalidDataException>(() => UpdatePackage.Extract(caseDuplicate, Path.Combine(root, "case-duplicate-out")));
    var linked = MakeZip("link", [.. RequiredEntries(), new ZipEntryData("web/link", "target", 0xA000)]);
    Throws<InvalidDataException>(() => UpdatePackage.Extract(linked, Path.Combine(root, "link-out")));
  }

  private void InstallManagedFiles()
  {
    var (target, payload, backup) = InstallFixture("install-managed");
    File.WriteAllText(Path.Combine(target, "user-data.txt"), "keep");
    File.WriteAllText(Path.Combine(target, "web", "old.js"), "stale");
    UpdatePackage.Install(payload, target, backup);
    Equal("new app", File.ReadAllText(Path.Combine(target, "WinPalm.exe")));
    Equal("new web", File.ReadAllText(Path.Combine(target, "web", "index.html")));
    False(File.Exists(Path.Combine(target, "web", "old.js")));
    Equal("keep", File.ReadAllText(Path.Combine(target, "user-data.txt")));
    True(File.Exists(Path.Combine(backup, "WinPalm.exe")));
  }

  private void InstallFailureRestores()
  {
    var (target, payload, backup) = InstallFixture("install-failure");
    File.WriteAllText(Path.Combine(target, "unrelated.txt"), "untouched");
    var thrown = false;
    try { UpdatePackage.Install(payload, target, backup, (_, _) => { if (!thrown) { thrown = true; throw new IOException("injected"); } }); }
    catch (IOException) { }
    True(thrown);
    Equal("old app", File.ReadAllText(Path.Combine(target, "WinPalm.exe")));
    Equal("old web", File.ReadAllText(Path.Combine(target, "web", "index.html")));
    Equal("old stale", File.ReadAllText(Path.Combine(target, "web", "old.js")));
    Equal("untouched", File.ReadAllText(Path.Combine(target, "unrelated.txt")));
    False(Directory.Exists(backup));
  }

  private void RollbackRestores()
  {
    var (target, payload, backup) = InstallFixture("rollback");
    UpdatePackage.Install(payload, target, backup);
    UpdatePackage.RollBack(target, backup);
    Equal("old app", File.ReadAllText(Path.Combine(target, "WinPalm.exe")));
    Equal("old web", File.ReadAllText(Path.Combine(target, "web", "index.html")));
    Equal("old stale", File.ReadAllText(Path.Combine(target, "web", "old.js")));
    False(Directory.Exists(backup));
  }

  private void PartialCopyFailure()
  {
    var (target, payload, backup) = InstallFixture("partial-copy");
    var copies = 0;
    Throws<IOException>(() => UpdatePackage.Install(payload, target, backup, (from, to) =>
    {
      if (++copies == 2) { File.WriteAllText(to, "partial new file"); throw new IOException("Disk full during copy"); }
      File.Copy(from, to);
    }));
    Equal(2, copies);
    Equal("old app", File.ReadAllText(Path.Combine(target, "WinPalm.exe")));
    Equal("old web", File.ReadAllText(Path.Combine(target, "web", "index.html")));
    Equal("old stale", File.ReadAllText(Path.Combine(target, "web", "old.js")));
    False(Directory.Exists(backup));
  }

  private (string Target, string Payload, string Backup) InstallFixture(string name)
  {
    var target = Path.Combine(root, name, "target"); var payload = Path.Combine(root, name, "payload"); var backup = Path.Combine(root, name, "backup");
    Directory.CreateDirectory(Path.Combine(target, "web")); Directory.CreateDirectory(Path.Combine(payload, "web"));
    File.WriteAllText(Path.Combine(target, "WinPalm.exe"), "old app");
    File.WriteAllText(Path.Combine(target, "web", "index.html"), "old web");
    File.WriteAllText(Path.Combine(target, "web", "old.js"), "old stale");
    File.WriteAllText(Path.Combine(payload, "WinPalm.exe"), "new app");
    File.WriteAllText(Path.Combine(payload, "web", "index.html"), "new web");
    return (target, payload, backup);
  }

  private string MakeZip(string name, IEnumerable<ZipEntryData> entries)
  {
    var file = Path.Combine(root, name + ".zip");
    using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
    foreach (var item in entries)
    {
      var entry = zip.CreateEntry(item.Name, CompressionLevel.NoCompression);
      if (item.ExternalAttributes != 0) entry.ExternalAttributes = item.ExternalAttributes << 16;
      using var writer = new StreamWriter(entry.Open()); writer.Write(item.Content);
    }
    return file;
  }

  private static ZipEntryData[] RequiredEntries() => [
    new("WinPalm.exe", "app"), new("winpalm_core.dll", "core"), new("web/index.html", "index"),
    new("web/source.zip", "source"), new("web/LICENSE.txt", "license"), new("Resources/WinPalm.ico", "icon"), new("LICENSE", "license")];

  private static ReleaseUpdates.Release? Parse(string json, Version? current = null, string architecture = "x64")
  {
    using var doc = JsonDocument.Parse(json);
    return ReleaseUpdates.Parse(doc.RootElement, current ?? new Version(1, 0, 0), architecture);
  }

  private static string ReleaseJson(string tag, string architecture, bool draft = false, bool prerelease = false, bool omitPackage = false, string? packageUrl = null)
  {
    var version = tag.TrimStart('v'); var package = $"WinPalm-{version}-win-{architecture}.zip";
    var assets = new List<object>();
    if (!omitPackage) assets.Add(new { name = package, size = 10, browser_download_url = packageUrl ?? AssetUri(tag, package) });
    assets.Add(new { name = package + ".sha256", size = 100, browser_download_url = AssetUri(tag, package + ".sha256") });
    return JsonSerializer.Serialize(new { draft, prerelease, tag_name = tag, body = "notes", assets });
  }

  private static ReleaseUpdates.Release ManualRelease(byte[] packageBytes)
  {
    const string tag = "v2.3.4"; var name = "WinPalm-2.3.4-win-x64.zip";
    return new(new Version(2, 3, 4), tag, "", new Uri($"https://github.com/{ReleaseUpdates.Repository}/releases/tag/{tag}"), new(name, new Uri(AssetUri(tag, name)), packageBytes.Length), new(name + ".sha256", new Uri(AssetUri(tag, name + ".sha256")), 100));
  }

  private static FakeHandler AssetHandler(ReleaseUpdates.Release release, byte[] package, string checksum) => new((request, _) =>
  {
    if (request.RequestUri == release.Checksum!.Url) return new(HttpStatusCode.OK) { Content = new StringContent(checksum) };
    if (request.RequestUri == release.Package!.Url) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
    throw new Exception("unexpected URL " + request.RequestUri);
  });

  private static string HashLine(byte[] bytes, string filename) => Convert.ToHexString(SHA256.HashData(bytes)) + "  " + filename;
  private static string AssetUri(string tag, string name) => $"https://github.com/{ReleaseUpdates.Repository}/releases/download/{tag}/{name}";
  private static Uri ChecksumUri(string tag, string name) => new(AssetUri(tag, name));

  private void Sync(string name, Action action)
  {
    try { action(); pass(name); } catch (Exception e) { fail(name, e); }
  }
  private async Task Async(string name, Func<Task> action)
  {
    try { await action(); pass(name); } catch (Exception e) { fail(name, e); }
  }
  private void pass(string name) { passed++; Console.WriteLine("PASS " + name); }
  private void fail(string name, Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
  private static void True(bool value, string? message = null) { if (!value) throw new Exception(message ?? "expected true"); }
  private static void False(bool value) => True(!value, "expected false");
  private static void Equal<T>(T expected, T? actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}"); }
  private static void Sequence(byte[] expected, byte[] actual) { if (!expected.SequenceEqual(actual)) throw new Exception("byte sequence differs"); }
  private static void Throws<T>(Action action, string? context = null) where T : Exception
  {
    try { action(); } catch (T) { return; }
    throw new Exception("expected " + typeof(T).Name + (context is null ? "" : ": " + context));
  }
  private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
  {
    try { await action(); } catch (T) { return; }
    throw new Exception("expected " + typeof(T).Name);
  }
  private sealed record ZipEntryData(string Name, string Content, int ExternalAttributes = 0);
}

sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
{
  public override bool CanSeek => false;
}

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
{
  internal List<Uri?> Requests { get; } = [];
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    Requests.Add(request.RequestUri);
    return Task.FromResult(respond(request, cancellationToken));
  }
}
