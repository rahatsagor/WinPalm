using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinPalm.Services;

/// The current single-file host is copied to private staging and acts as a
/// short-lived helper. It waits for the tray app to exit before replacing it.
internal static class UpdateInstaller
{
  private sealed record Plan(string Target, int ProcessId, long StartTicks, string CurrentHash, string PackageHash, string Architecture, string Version);
  internal static string StagingRoot => Path.Combine(Preferences.DataDirectory, "updates");

  internal static string NewStage()
  {
    Directory.CreateDirectory(StagingRoot);
    UpdatePackage.EnsureNoLinks(StagingRoot);
    var stage = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(stage); return stage;
  }

  internal static void Prepare(string archive, string stage, ReleaseUpdates.Release release, string architecture, CancellationToken cancellation)
  {
    var target = Path.GetFullPath(AppContext.BaseDirectory);
    if (Preferences.LocationProblem(Environment.ProcessPath) is { } problem) throw new IOException(problem);
    if (target.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Use the release package to update a copy on a network drive.");
    UpdatePackage.EnsureNoLinks(target);
    var probe = Path.Combine(target, ".winpalm-write-check-" + Guid.NewGuid().ToString("N"));
    using (File.Create(probe)) { } File.Delete(probe);
    var payload = Path.Combine(stage, "payload");
    UpdatePackage.Extract(archive, payload, cancellation);
    UpdatePackage.ValidateBinaries(payload, architecture, release.Version);
    using var current = Process.GetCurrentProcess();
    var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the running WinPalm.");
    var plan = new Plan(target, current.Id, current.StartTime.ToUniversalTime().Ticks, Hash(executable), Hash(archive), architecture, release.Version.ToString(3));
    File.Copy(executable, Path.Combine(stage, "updater.exe"));
    File.WriteAllText(Path.Combine(stage, "plan.json"), JsonSerializer.Serialize(plan));
  }

  /// Start the helper and wait until it has validated the handoff. Until this
  /// succeeds, the running tray app and its server remain fully usable.
  internal static async Task StartHelperAsync(string stage, CancellationToken cancellation)
  {
    var info = new ProcessStartInfo(Path.Combine(stage, "updater.exe")) { UseShellExecute = false, WorkingDirectory = stage };
    info.ArgumentList.Add("--apply-update"); info.ArgumentList.Add(Path.GetFileName(stage));
    using var helper = Process.Start(info) ?? throw new IOException("Could not start the update helper.");
    for (var i = 0; i < 150; i++)
    {
      cancellation.ThrowIfCancellationRequested();
      if (File.Exists(Path.Combine(stage, "error"))) throw new IOException(File.ReadAllText(Path.Combine(stage, "error")));
      if (File.Exists(Path.Combine(stage, "ready"))) { File.WriteAllText(Path.Combine(stage, "apply"), "ready"); return; }
      if (helper.HasExited) break;
      await Task.Delay(100, cancellation);
    }
    throw new IOException("The update helper did not start. WinPalm is still running; try again or use the release package.");
  }

  internal static int Apply(string id)
  {
    var stage = Stage(id);
    if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.Combine(stage, "updater.exe"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An update helper must run from its private staging folder.");
    UpdatePackage.EnsureNoLinks(stage);
    var planFile = Path.Combine(stage, "plan.json");
    if (new FileInfo(planFile).Length > 16384) throw new InvalidDataException("The update handoff is invalid.");
    var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(planFile)) ?? throw new InvalidDataException("The update handoff is missing.");
    var target = Path.GetFullPath(plan.Target);
    var currentExe = Path.Combine(target, "WinPalm.exe");
    var data = Path.GetFullPath(Preferences.DataDirectory) + Path.DirectorySeparatorChar;
    if (target.StartsWith(data, StringComparison.OrdinalIgnoreCase) || !Hash(currentExe).Equals(plan.CurrentHash, StringComparison.Ordinal)
        || !Hash(Environment.ProcessPath!).Equals(plan.CurrentHash, StringComparison.Ordinal)) throw new InvalidDataException("The installed WinPalm changed before the update could start.");
    var archive = Path.Combine(stage, "package.zip");
    if (!Hash(archive).Equals(plan.PackageHash, StringComparison.Ordinal)) throw new InvalidDataException("The staged update changed. Download it again.");
    var payload = Path.Combine(stage, "payload");
    // Re-extract the verified ZIP so an interrupted or damaged staging copy
    // cannot be installed in place of the downloaded package.
    Directory.Delete(payload, true); UpdatePackage.Extract(archive, payload);
    UpdatePackage.ValidateBinaries(payload, plan.Architecture, Version.Parse(plan.Version));
    using var previous = Process.GetProcessById(plan.ProcessId);
    if (previous.StartTime.ToUniversalTime().Ticks != plan.StartTicks) throw new InvalidDataException("The app being updated has changed.");
    File.WriteAllText(Path.Combine(stage, "ready"), "ready");
    for (var i = 0; i < 200 && !File.Exists(Path.Combine(stage, "apply")); i++)
    {
      if (previous.HasExited) return 1;
      Thread.Sleep(100);
    }
    if (!File.Exists(Path.Combine(stage, "apply"))) return 1;
    if (!previous.WaitForExit(45000)) throw new IOException("WinPalm is still running. Close it and try the update again.");
    var backup = Path.Combine(target, ".winpalm-backup-" + id);
    Process? restarted = null;
    try
    {
      UpdatePackage.Install(payload, target, backup);
      var info = new ProcessStartInfo(currentExe) { UseShellExecute = false, WorkingDirectory = target };
      info.ArgumentList.Add("--complete-update"); info.ArgumentList.Add(id);
      restarted = Process.Start(info) ?? throw new IOException("WinPalm could not restart.");
      for (var i = 0; i < 450; i++)
      {
        if (File.Exists(Path.Combine(stage, "success")))
        {
          try { Directory.Delete(backup, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Diagnostics.Record(error); }
          File.WriteAllText(Path.Combine(stage, "finished"), "success"); return 0;
        }
        if (restarted.HasExited) throw new IOException("The updated WinPalm could not start. The previous version has been restored.");
        Thread.Sleep(100);
      }
      // Do not kill a live app which may already be controlling input.
      throw new TimeoutException("WinPalm restarted but did not confirm the update. Its previous files are kept in " + backup);
    }
    catch (Exception error) when (error is not TimeoutException && (restarted is null || restarted.HasExited))
    {
      if (Directory.Exists(backup)) UpdatePackage.RollBack(target, backup);
      Process.Start(new ProcessStartInfo(currentExe) { UseShellExecute = true, WorkingDirectory = target });
      throw;
    }
    finally { restarted?.Dispose(); }
  }

  internal static void Complete(string id)
  {
    var stage = Stage(id);
    File.WriteAllText(Path.Combine(stage, "success"), "success");
  }
  internal static bool ReportFailure(string id, string message)
  {
    try
    {
      var stage = Stage(id);
      File.WriteAllText(Path.Combine(stage, "error"), message);
      // Before handoff, the still-running app shows the error itself.
      return File.Exists(Path.Combine(stage, "apply"));
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Diagnostics.Record(error); return true; }
  }

  private static string Stage(string id) => Guid.TryParseExact(id, "N", out _) ? Path.Combine(StagingRoot, id) : throw new InvalidDataException("The update identifier is invalid.");
  private static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }

  /// Never delete a helper's working files while it is installing. Completed
  /// stages and abandoned downloads are reclaimed on a later normal launch.
  internal static void CleanOldStages()
  {
    if (!Directory.Exists(StagingRoot)) return;
    try
    {
      UpdatePackage.EnsureNoLinks(StagingRoot);
      foreach (var path in Directory.EnumerateDirectories(StagingRoot))
      {
        if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _) || Directory.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-7)) continue;
        if (File.Exists(Path.Combine(path, "apply")) && !File.Exists(Path.Combine(path, "finished"))) continue;
        try { Directory.Delete(path, true); } catch (IOException) { }
      }
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Diagnostics.Record(error); }
  }
}
