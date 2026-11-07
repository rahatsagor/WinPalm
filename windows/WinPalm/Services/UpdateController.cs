using System.Net;
using System.Runtime.InteropServices;

namespace WinPalm.Services;

internal sealed class UpdateController(Preferences preferences) : IDisposable
{
  private readonly ReleaseUpdates releases = new();
  private readonly CancellationTokenSource lifetime = new();
  private CancellationTokenSource? download;
  private DateTime nextAutomatic = preferences.LastUpdateCheckUtc <= DateTime.UtcNow && preferences.LastUpdateCheckUtc >= DateTime.UtcNow.AddDays(-1)
      ? preferences.LastUpdateCheckUtc.AddDays(1) : DateTime.MinValue;
  internal event Action? Changed;
  internal event Action? UpdateFound;
  internal event Action? RestartRequested;
  internal ReleaseUpdates.Release? Available { get; private set; }
  internal bool Checking { get; private set; }
  internal bool Installing { get; private set; }
  internal bool Busy => Checking || Installing;
  internal int Progress { get; private set; }
  internal string Message { get; private set; } = "Check GitHub for a newer WinPalm release.";
  internal static Version CurrentVersion => typeof(UpdateController).Assembly.GetName().Version is { } v ? new(v.Major, v.Minor, v.Build) : new(0, 1, 0);
  private static string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

  internal void CheckWhenDue()
  {
    if (!preferences.CheckForUpdates || Busy || DateTime.UtcNow < nextAutomatic || lifetime.IsCancellationRequested) return;
    _ = CheckAsync();
  }

  internal async Task CheckAsync()
  {
    if (Busy || lifetime.IsCancellationRequested) return;
    Checking = true; Message = "Checking GitHub…"; Changed?.Invoke();
    nextAutomatic = DateTime.UtcNow.AddHours(6);
    try
    {
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
      Available = await releases.CheckAsync(CurrentVersion, Architecture, timeout.Token);
      preferences.LastUpdateCheckUtc = DateTime.UtcNow; nextAutomatic = DateTime.UtcNow.AddDays(1);
      Message = Available is { } release ? $"WinPalm {release.Version.ToString(3)} is available." : "No newer published release is available.";
      if (Available is { } found && preferences.NotifiedUpdateVersion != found.Version.ToString(3))
      {
        preferences.NotifiedUpdateVersion = found.Version.ToString(3); UpdateFound?.Invoke();
      }
      try { preferences.Save(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Diagnostics.Record(error); }
    }
    catch (OperationCanceledException) { Message = "Couldn’t reach GitHub in time. Try again when you’re online."; }
    catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
    {
      Diagnostics.Record(error);
      Message = error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }
          ? "GitHub is limiting update checks. Try again later."
          : "Couldn’t check for updates. Check your connection or open GitHub releases.";
    }
    finally { Checking = false; if (!lifetime.IsCancellationRequested) Changed?.Invoke(); }
  }

  internal async Task InstallAsync()
  {
    if (Busy || Available is not { Package: not null, Checksum: not null } release || lifetime.IsCancellationRequested) return;
    Installing = true; Progress = 0; Message = "Downloading update…"; Changed?.Invoke();
    string? stage = null; var handedOff = false;
    using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); operation.CancelAfter(TimeSpan.FromMinutes(10)); download = operation;
    try
    {
      stage = UpdateInstaller.NewStage();
      var progress = new Progress<int>(value => { Progress = value; if (!lifetime.IsCancellationRequested) Changed?.Invoke(); });
      var archive = await releases.DownloadAsync(release, stage, progress, operation.Token);
      Message = "Verifying update package…"; Changed?.Invoke();
      await Task.Run(() => UpdateInstaller.Prepare(archive, stage, release, Architecture, operation.Token), operation.Token);
      Message = "Restarting WinPalm to finish the update…"; Changed?.Invoke();
      await UpdateInstaller.StartHelperAsync(stage, operation.Token);
      handedOff = true; RestartRequested?.Invoke();
    }
    catch (OperationCanceledException) { Message = "Update cancelled. WinPalm is still on the current version."; }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
    {
      Diagnostics.Record(error); Message = "Couldn’t install the update. " + error.Message;
    }
    finally
    {
      download = null; Installing = false;
      if (!handedOff && stage is not null)
      {
        try { Directory.Delete(stage, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Diagnostics.Record(error); }
      }
      if (!lifetime.IsCancellationRequested) Changed?.Invoke();
    }
  }

  internal void Cancel() => download?.Cancel();
  public void Dispose() { lifetime.Cancel(); download?.Cancel(); releases.Dispose(); lifetime.Dispose(); }
}
