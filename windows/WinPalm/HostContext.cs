using System.Text.Json;
using Microsoft.Win32;
using WinPalm.Services;
using WinPalm.UI;
namespace WinPalm;

internal sealed class HostContext : ApplicationContext
{
  internal Preferences Preferences { get; }
  internal DesktopServices Services { get; }
  internal UpdateController Updates { get; }
  internal JsonElement Snapshot { get; private set; }
  /// Set on the UI thread from background checks; shown on Connect.
  internal NetworkCheck.Problem? NetworkProblem { get; private set; }
  private readonly MainWindow window;
  private readonly NotifyIcon tray;
  private readonly System.Windows.Forms.Timer timer;
  private readonly Dictionary<ulong, ApprovalWindow> approvals = [];
  /// Lock and sleep arrive on the SystemEvents thread, unlock on the UI thread.
  private readonly object lockGate = new();
  private readonly ToolStripMenuItem pauseItem;
  private readonly ToolStripMenuItem updateItem;
  private bool busy, quitting, locked, wasPaused;
  private nint lastLayout, lastForeground;
  private int ticks, checkingNetwork;
  internal HostContext(Preferences preferences, bool minimized)
  {
    Preferences = preferences; Services = new(preferences); Updates = new(preferences); CoreBridge.Desktop = Services;
    CoreBridge.Call(new { op = "create", config = Services.Config() });
    window = new MainWindow(this); _ = window.Handle;
    CoreBridge.Event = value => { if (!quitting && !window.IsDisposed) window.BeginInvoke(() => OnEvent(value)); };
    var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 10) };
    menu.Items.Add("Open WinPalm", null, (_, _) => ShowWindow());
    pauseItem = new("Pause control", null, (_, _) => TogglePause()); menu.Items.Add(pauseItem);
    menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Restart server", null, (_, _) => Restart());
    updateItem = new("Check for updates…", null, async (_, _) => { ShowWindow(); window.ShowUpdates(); await Updates.CheckAsync(); }); menu.Items.Add(updateItem);
    menu.Items.Add("Quit WinPalm", null, (_, _) => ExitThread());
    tray = new NotifyIcon { Text = "WinPalm · Starting", Icon = window.Icon, ContextMenuStrip = menu, Visible = true };
    tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
    tray.BalloonTipClicked += (_, _) => { if (Updates.Available is not null) { ShowWindow(); window.ShowUpdates(); } };
    Updates.Changed += () => { if (!quitting) { updateItem.Text = Updates.Available is { } release ? $"WinPalm {release.Version.ToString(3)} available…" : "Check for updates…"; window.UpdateUpdates(); } };
    Updates.UpdateFound += () => { if (!quitting) tray.ShowBalloonTip(10000, "WinPalm update available", $"WinPalm {Updates.Available!.Version.ToString(3)} is ready. Open Updates to download and restart.", ToolTipIcon.Info); };
    Updates.RestartRequested += Quit;
    if (!Win32.RegisterHotKey(window.Handle, 1, 0x4003, 0x13)) tray.ShowBalloonTip(5000, "WinPalm", "Ctrl + Alt + Pause is already used by another app. Pause is available in the tray menu.", ToolTipIcon.Info);
    SystemEvents.SessionSwitch += SessionChanged; SystemEvents.PowerModeChanged += PowerChanged;
    System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
    Start(); CheckNetwork();
    timer = new System.Windows.Forms.Timer { Interval = 2000 }; timer.Tick += async (_, _) => await Poll(); timer.Start();
    if (!minimized) ShowWindow();
    _ = Poll();
    Updates.CheckWhenDue();
  }
  internal void Quit() => ExitThread();
  /// Closing the window keeps WinPalm running; say so once, since Windows 11
  /// hides new tray icons in the overflow menu.
  internal void ExplainTray()
  {
    if (Preferences.TrayHintShown) return;
    tray.ShowBalloonTip(8000, "WinPalm is still running", "Phones stay connected. Open or quit WinPalm from its icon in the notification area, by the clock.", ToolTipIcon.Info);
    Preferences.TrayHintShown = true;
    try { Preferences.Save(); } catch (IOException error) { Diagnostics.Record(error); }
  }
  private void NetworkAddressChanged(object? sender, EventArgs args) => CheckNetwork();
  /// Look for a Public network profile or a firewall Block rule in the
  /// background; COM and registry reads can take a moment.
  internal void CheckNetwork()
  {
    if (quitting || Interlocked.Exchange(ref checkingNetwork, 1) == 1) return;
    _ = Task.Run(NetworkCheck.Find).ContinueWith(task =>
    {
      Interlocked.Exchange(ref checkingNetwork, 0);
      var problem = task.IsCompletedSuccessfully ? task.Result : null;
      if (task.Exception is { } error) Diagnostics.Record(error.GetBaseException());
      if (quitting || !window.IsHandleCreated) return;
      window.BeginInvoke(() => { if (!quitting && problem != NetworkProblem) { NetworkProblem = problem; window.NetworkChanged(); } });
    }, TaskScheduler.Default);
  }
  /// Application.OpenForms omits hidden windows, so marshal through the
  /// settings window, whose handle exists for the app's lifetime.
  internal void ShowFromAnotherThread() => OnWindowThread(ShowWindow);
  /// For callbacks on other threads (SystemEvents, waits): an exception
  /// there would end the process, and the window may be closing.
  private void OnWindowThread(Action work)
  {
    try { if (!quitting && window.IsHandleCreated && !window.IsDisposed) window.BeginInvoke(work); }
    catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
  }
  internal void ShowWindow() { window.Show(); if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal; window.Activate(); }
  internal void Start()
  {
    try { CoreBridge.Call(new { op = "start" }); KeyboardLayouts.UpdateTiming(); CoreBridge.Call(new { op = "layout", layout = KeyboardLayouts.Read() }); RefreshSoon(); }
    catch (Exception error) { Diagnostics.Record(error); MessageBox.Show(window, error.Message, "WinPalm couldn't start", MessageBoxButtons.OK, MessageBoxIcon.Error); }
  }
  internal void Stop() { CoreBridge.TryCall(new { op = "stop" }); RefreshSoon(); }
  internal void Restart() { Stop(); Start(); }
  internal void TogglePause()
  {
    var state = CoreBridge.Call(new { op = "status" }); CoreBridge.TryCall(new { op = "pause", paused = !state.GetProperty("paused").GetBoolean() }); RefreshSoon();
  }
  internal void RefreshSoon()
  {
    try { Snapshot = CoreBridge.Call(new { op = "status" }); window.UpdateState(Snapshot); UpdateTray(); }
    catch (Exception error) { Diagnostics.Record(error); }
  }
  private void UpdateTray()
  {
    var paused = Snapshot.GetProperty("paused").GetBoolean(); var running = Snapshot.GetProperty("running").GetBoolean(); var count = Snapshot.GetProperty("connected").GetArrayLength();
    tray.Text = !running ? "WinPalm · Sharing stopped" : paused ? "WinPalm · Paused" : $"WinPalm · {count} connected";
    pauseItem.Text = paused ? "Resume control" : "Pause control";
  }
  private async Task Poll()
  {
    if (busy || quitting) return; busy = true;
    try
    {
      var snapshot = await Task.Run(() =>
      {
        var current = CoreBridge.Call(new { op = "status" });
        var layout = KeyboardLayouts.Current; var foreground = Win32.GetForegroundWindow();
        if (layout != lastLayout) { lastLayout = layout; CoreBridge.Call(new { op = "layout", layout = KeyboardLayouts.Read() }); }
        // An administrator window in front blocks input; update the phone's badge.
        if (foreground != lastForeground) { lastForeground = foreground; CoreBridge.Call(new { op = "appsChanged" }); CoreBridge.Call(new { op = "permissionChanged" }); }
        if (current.GetProperty("connected").GetArrayLength() > 0)
        {
          var volume = AudioVolume.Read(); CoreBridge.Call(new { op = "volume", value = volume?.Value, muted = volume?.Muted });
        }
        // Running apps are read fresh for every list; installed apps are
        // rescanned at most every two minutes, or after a launch.
        if (++ticks % 15 == 0) { KeyboardLayouts.UpdateTiming(); CoreBridge.Call(new { op = "appsChanged" }); CoreBridge.Call(new { op = "permissionChanged" }); }
        return current;
      });
      if (!quitting) { Snapshot = snapshot; window.UpdateState(snapshot); UpdateTray(); if (ticks % 15 == 0) { CheckNetwork(); Updates.CheckWhenDue(); } }
    }
    catch (Exception error) { Diagnostics.Record(error); }
    finally { busy = false; }
  }
  private void OnEvent(JsonElement value)
  {
    if (quitting) return;
    switch (value.GetProperty("event").GetString())
    {
      case "approvalRequested":
        var request = value.GetProperty("request"); var id = request.GetProperty("id").GetUInt64();
        if (locked) { CoreBridge.TryCall(new { op = "approve", id, allow = false }); break; }
        if (approvals.ContainsKey(id)) break;
        var approval = new ApprovalWindow(request); approvals[id] = approval; approval.Show();
        tray.ShowBalloonTip(8000, "WinPalm pairing request", "A nearby device is asking to control this PC. Review its name and address in WinPalm.", ToolTipIcon.Info); break;
      case "approvalEnded":
        if (approvals.Remove(value.GetProperty("id").GetUInt64(), out var ended)) { ended.End(); ended.Dispose(); }
        break;
      case "failed": tray.ShowBalloonTip(8000, "WinPalm server", value.GetProperty("message").GetString() ?? "See the WinPalm log for details.", ToolTipIcon.Warning); break;
      case "notice": tray.ShowBalloonTip(5000, "WinPalm", value.GetProperty("message").GetString() ?? "See the WinPalm log for details.", ToolTipIcon.Info); break;
    }
    RefreshSoon();
  }
  private void SessionChanged(object sender, SessionSwitchEventArgs args)
  {
    if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.SessionLogoff)
      OnWindowThread(SuspendControl);
    else if (args.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect)
      OnWindowThread(ResumeControl);
  }
  private void PowerChanged(object sender, PowerModeChangedEventArgs args)
  {
    // Pause right away on suspend; a queued call might not run before sleep.
    if (args.Mode == PowerModes.Suspend) SuspendControl();
    else if (args.Mode == PowerModes.Resume) OnWindowThread(ResumeControl);
  }
  private void SuspendControl()
  {
    lock (lockGate)
    {
      if (quitting || locked) return; locked = true;
      try { wasPaused = CoreBridge.Call(new { op = "status" }).GetProperty("paused").GetBoolean(); }
      catch (InvalidOperationException error) { Diagnostics.Record(error); wasPaused = false; }
      CoreBridge.TryCall(new { op = "pause", paused = true });
    }
  }
  private void ResumeControl()
  {
    lock (lockGate) { if (quitting || !locked) return; locked = false; if (!wasPaused) CoreBridge.TryCall(new { op = "pause", paused = false }); }
    RefreshSoon();
  }
  protected override void ExitThreadCore()
  {
    if (quitting) return; quitting = true; timer.Stop(); timer.Dispose();
    Updates.Dispose();
    SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
    System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
    CoreBridge.Event = null; CoreBridge.TryCall(new { op = "pause", paused = true }); CoreBridge.TryCall(new { op = "stop" });
    foreach (var approval in approvals.Values.ToArray()) { approval.End(); approval.Dispose(); }
    approvals.Clear(); Win32.UnregisterHotKey(window.Handle, 1); tray.Visible = false; tray.Dispose();
    Services.Dispose(); window.Shutdown(); base.ExitThreadCore();
  }
}
