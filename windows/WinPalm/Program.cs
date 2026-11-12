using System.Security.Principal;
using WinPalm.Services;
namespace WinPalm;

internal static class Program
{
  [STAThread]
  private static int Main(string[] args)
  {
    ApplicationConfiguration.Initialize();
    Application.SetColorMode(SystemColorMode.Dark);
    // A tray app should log a failed click handler, not show .NET's crash
    // dialog; actions that need attention already show their own message.
    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    Application.ThreadException += (_, e) => Diagnostics.Record(e.Exception);
    AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception error) Diagnostics.Record(error); };
    try
    {
      if (args is ["--apply-update", var updateId]) return UpdateInstaller.Apply(updateId);
      if (args.Contains("--self-test")) return SelfTest.Run(args);
      if (args.Contains("--input-test")) return InputTest.Run(args);
      var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
      if (args is ["--quit-for-install"]) return QuitForInstall(identity);
      using var mutex = new Mutex(true, "Local\\RSApplication.WinPalm." + identity, out var first);
      using var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\RSApplication.WinPalm.Show." + identity);
      using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\RSApplication.WinPalm.Quit." + identity);
      if (!first)
      {
        // Let the running copy bring its window to the front.
        Win32.AllowSetForegroundWindow(-1); show.Set();
        // A copy that is quitting still holds the mutex for a moment. If it
        // goes, carry on, so quitting and reopening never leaves WinPalm closed.
        try { first = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { first = true; }
        if (!first) return 0;
      }
      var preferences = Preferences.Load(); Preferences.KeepLoginPathCurrent(); CoreBridge.Initialize();
      UpdateInstaller.CleanOldStages();
      using var context = new HostContext(preferences, args.Contains("--start-minimized"));
      Preferences.KeepInstalledVersionCurrent();
      if (args is ["--complete-update", var completedId])
      {
        if (!CoreBridge.Call(new { op = "status" }).GetProperty("running").GetBoolean()) throw new InvalidOperationException("The updated server could not start.");
        UpdateInstaller.Complete(completedId);
      }
      var listener = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => context.ShowFromAnotherThread(), null, Timeout.Infinite, false);
      var quitListener = ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => context.QuitFromAnotherThread(), null, Timeout.Infinite, false);
      try { Application.Run(context); } finally { listener.Unregister(null); quitListener.Unregister(null); }
      return 0;
    }
    catch (Exception error)
    {
      Diagnostics.Record(error);
      if (args is ["--quit-for-install"]) return 1;
      if (args.Contains("--apply-update"))
      {
        if (args.Length == 2 && UpdateInstaller.ReportFailure(args[1], error.Message))
          MessageBox.Show(error.Message, "WinPalm update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return 1;
      }
      // The helper must see a failed new process exit promptly so it can
      // restore the previous package, without waiting for a modal dialog.
      if (args.Contains("--complete-update")) return 1;
      MessageBox.Show($"WinPalm couldn't start.\n\n{error.Message}\n\nMake sure the complete package was extracted, including winpalm_core.dll and the web folder.", "WinPalm", MessageBoxButtons.OK, MessageBoxIcon.Error);
      return 1;
    }
  }

  /// The installer waits for this user's existing instance to release its
  /// mutex. This also works while the settings window is hidden in the tray.
  private static int QuitForInstall(string identity)
  {
    if (!Mutex.TryOpenExisting("Local\\RSApplication.WinPalm." + identity, out var mutex)) return 0;
    using (mutex)
    {
      bool Finished(TimeSpan wait)
      {
        var acquired = false;
        try { acquired = mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { acquired = true; }
        finally { if (acquired) mutex.ReleaseMutex(); }
        return acquired;
      }
      if (Finished(TimeSpan.Zero)) return 0;
      // A first instance can be between creating its mutex and quit event.
      for (var attempt = 0; attempt < 20; attempt++)
      {
        if (EventWaitHandle.TryOpenExisting("Local\\RSApplication.WinPalm.Quit." + identity, out var quit))
        {
          using (quit) { quit.Set(); }
          return Finished(TimeSpan.FromSeconds(45)) ? 0 : 1;
        }
        if (Finished(TimeSpan.FromMilliseconds(100))) return 0;
      }
      return 1;
    }
  }
}
