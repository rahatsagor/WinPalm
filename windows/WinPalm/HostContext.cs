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
  private readonly Dictionary<ulong, ApprovalWindow> displayApprovals = [];
  /// Lock and sleep arrive on the SystemEvents thread, unlock on the UI thread.
  private readonly object lockGate = new();
  private readonly ControlPauseState automaticPause = new();
  private bool AutomaticallyPaused { get { lock (lockGate) return automaticPause.Active; } }
  private readonly ToolStripMenuItem statusItem;
  private readonly ToolStripMenuItem pauseItem;
  private readonly ToolStripMenuItem updateItem;
  private bool busy, quitting;
  private nint lastLayout, lastForeground;
  private int ticks, checkingNetwork;
  internal HostContext(Preferences preferences, bool minimized)
  {
    Preferences = preferences; Services = new(preferences); Updates = new(preferences); CoreBridge.Desktop = Services;
    CoreBridge.Call(new { op = "create", config = Services.Config() });
    window = new MainWindow(this); _ = window.Handle;
    CoreBridge.Event = value => { if (!quitting && !window.IsDisposed) window.BeginInvoke(() => OnEvent(value)); };
    var menu = new ContextMenuStrip { Font = Theme.Font(9.5f) };
    Theme.StyleContextMenu(menu);
    menu.Opening += (_, _) => { menu.Font = Theme.Font(9.5f); menu.Padding = Theme.Px(6, 6, 6, 6); };
    statusItem = new ToolStripMenuItem("WinPalm · Starting") { Enabled = false };
    menu.Items.Add(statusItem);
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("Open WinPalm", null, (_, _) => ShowWindow());
    pauseItem = new("Pause control", null, (_, _) => TogglePause()); menu.Items.Add(pauseItem);
    menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Restart server", null, (_, _) => Restart());
    updateItem = new("Check for updates…", null, async (_, _) => { ShowWindow(); window.ShowUpdates(); await Updates.CheckAsync(); }); menu.Items.Add(updateItem);
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("Quit WinPalm", null, (_, _) => ExitThread());
    tray = new NotifyIcon { Text = "WinPalm · Starting", Icon = window.Icon, ContextMenuStrip = menu, Visible = true };
    tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
    tray.BalloonTipClicked += (_, _) => { if (Updates.Available is not null) { ShowWindow(); window.ShowUpdates(); } };
    Updates.Changed += () => { if (!quitting) { updateItem.Text = Updates.Available is { } release ? $"WinPalm {release.Version.ToString(3)} available…" : "Check for updates…"; window.UpdateUpdates(); } };
    Updates.UpdateFound += () => { if (!quitting) tray.ShowBalloonTip(10000, "WinPalm update available", $"WinPalm {Updates.Available!.Version.ToString(3)} is ready. Open Updates to download and restart.", ToolTipIcon.Info); };
    Updates.RestartRequested += Quit;
    if (!Win32.RegisterHotKey(window.Handle, 1, 0x4003, 0x13)) tray.ShowBalloonTip(5000, "WinPalm", "Ctrl + Alt + Pause is already used by another app. Pause is available in the tray menu.", ToolTipIcon.Info);
    SystemEvents.SessionSwitch += SessionChanged; SystemEvents.PowerModeChanged += PowerChanged;
    SystemEvents.DisplaySettingsChanged += DisplaysChanged;
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
  internal void QuitFromAnotherThread() => OnWindowThread(Quit);
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
    catch (Exception error) { Diagnostics.Record(error); PalmDialog.Info(window.Visible ? window : null, "WinPalm couldn't start", error.Message, BadgeTone.Danger); }
  }
  internal void Stop() { CoreBridge.TryCall(new { op = "stop" }); RefreshSoon(); }
  internal void Restart() { Stop(); Start(); }
  internal void TogglePause()
  {
    lock (lockGate)
    {
      if (quitting || automaticPause.Active) return;
      var state = CoreBridge.Call(new { op = "status" });
      CoreBridge.TryCall(new { op = "pause", paused = !state.GetProperty("paused").GetBoolean() });
    }
    RefreshSoon();
  }
  internal void RefreshSoon()
  {
    try { Snapshot = CoreBridge.Call(new { op = "status" }); window.UpdateState(Snapshot); UpdateTray(); }
    catch (Exception error) { Diagnostics.Record(error); }
  }
  private void UpdateTray()
  {
    var paused = Snapshot.GetProperty("paused").GetBoolean(); var running = Snapshot.GetProperty("running").GetBoolean(); var count = Snapshot.GetProperty("connected").GetArrayLength();
    var summary = !running ? "WinPalm · Sharing stopped" : paused ? "WinPalm · Paused" : count > 0 ? $"WinPalm · {count} connected" : "WinPalm · Ready to pair";
    tray.Text = summary;
    statusItem.Text = summary;
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
        if (AutomaticallyPaused) { CoreBridge.TryCall(new { op = "approve", id, allow = false }); break; }
        if (approvals.ContainsKey(id)) break;
        var approval = new ApprovalWindow(request, approvals.Count); approvals[id] = approval; approval.Show();
        tray.ShowBalloonTip(8000, "WinPalm pairing request", "A nearby device is asking to control this PC. Review its name and address in WinPalm.", ToolTipIcon.Info); break;
      case "approvalEnded":
        if (approvals.Remove(value.GetProperty("id").GetUInt64(), out var ended)) { ended.End(); ended.Dispose(); }
        break;
      case "displayApprovalRequested":
      case "displayApprovalEnded":
        try { SyncDisplayApprovals(); }
        catch (Exception error)
        {
          Diagnostics.Record(error);
          foreach (var prompt in displayApprovals.Values.ToArray()) { prompt.End(); prompt.Dispose(); }
          displayApprovals.Clear();
        }
        break;
      case "failed": tray.ShowBalloonTip(8000, "WinPalm server", value.GetProperty("message").GetString() ?? "See the WinPalm log for details.", ToolTipIcon.Warning); break;
      case "notice": tray.ShowBalloonTip(5000, "WinPalm", value.GetProperty("message").GetString() ?? "See the WinPalm log for details.", ToolTipIcon.Info); break;
    }
    RefreshSoon();
  }
  private void SyncDisplayApprovals()
  {
    // Events may have been queued before a cancellation. Only show prompts
    // still present in the core's authoritative, expiring request list.
    var pending = CoreBridge.Call(new { op = "displayApprovals" }).EnumerateArray().ToArray();
    var ids = pending.Select(request => request.GetProperty("id").GetUInt64()).ToHashSet();
    foreach (var id in displayApprovals.Keys.Where(id => !ids.Contains(id)).ToArray())
      if (displayApprovals.Remove(id, out var ended)) { ended.End(); ended.Dispose(); }
    foreach (var request in pending)
    {
      var id = request.GetProperty("id").GetUInt64();
      if (AutomaticallyPaused) { CoreBridge.TryCall(new { op = "displayApprove", id, allow = false }); continue; }
      if (displayApprovals.ContainsKey(id)) continue;
      var approval = new ApprovalWindow(request, approvals.Count + displayApprovals.Count, screenRequest: true, response: RespondDisplayApproval);
      displayApprovals[id] = approval;
      approval.Show();
      tray.ShowBalloonTip(8000, "WinPalm screen viewing request", "A connected device is asking to view your screen. Review its name and address in WinPalm.", ToolTipIcon.Info);
    }
  }
  private void RespondDisplayApproval(ulong id, bool allow)
  {
    var previous = Preferences.DisplayEnabled;
    try
    {
      if (allow && !previous)
      {
        // Persist global opt-in before granting the phone access. A failed
        // settings write must not leave behind an unnoticed device grant.
        if (!CoreBridge.Call(new { op = "displayApprovals" }).EnumerateArray().Any(request => request.GetProperty("id").GetUInt64() == id)) return;
        SetDisplayEnabled(true);
      }
      var valid = CoreBridge.Call(new { op = "displayApprove", id, allow }).GetBoolean();
      if (!valid && allow && !previous) SetDisplayEnabled(false);
    }
    catch (Exception error)
    {
      CoreBridge.TryCall(new { op = "displayApprove", id, allow = false });
      if (allow && !previous)
        try { SetDisplayEnabled(false); } catch (Exception restoreError) { Diagnostics.Record(restoreError); }
      Diagnostics.Record(error);
      tray.ShowBalloonTip(8000, "WinPalm screen viewing", "The screen-viewing settings could not be saved. Check General and try again.", ToolTipIcon.Warning);
    }
    RefreshSoon();
  }
  internal void SetDisplayEnabled(bool enabled)
  {
    try
    {
      DisplayPreference.Apply(enabled, Preferences.DisplayEnabled,
        value => Preferences.DisplayEnabled = value,
        Preferences.Save,
        () => { Services.Update(Preferences); CoreBridge.Call(new { op = "config", config = Services.Config() }); },
        Diagnostics.Record);
    }
    finally { RefreshSoon(); }
  }
  private void SessionChanged(object sender, SessionSwitchEventArgs args)
  {
    if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
      SuspendControl(ControlPauseReason.SessionLocked);
    else if (args.Reason == SessionSwitchReason.RemoteDisconnect)
      SuspendControl(ControlPauseReason.SessionDisconnected);
    else if (args.Reason == SessionSwitchReason.SessionUnlock)
      OnWindowThread(() => ResumeControl(ControlPauseReason.SessionLocked));
    else if (args.Reason == SessionSwitchReason.RemoteConnect)
      OnWindowThread(() => ResumeControl(ControlPauseReason.SessionDisconnected));
  }
  private void DisplaysChanged(object? sender, EventArgs args)
  {
    if (!quitting) CoreBridge.TryCall(new { op = "displaysChanged" });
  }
  internal void DisplayPowerChanged(bool awake)
  {
    lock (lockGate)
    {
      if (quitting) return;
      automaticPause.DisplayAwake = awake;
      CoreBridge.TryCall(new { op = "displayAwake", awake = automaticPause.CanView });
    }
  }
  private void PowerChanged(object sender, PowerModeChangedEventArgs args)
  {
    // Pause right away on suspend; a queued call might not run before sleep.
    if (args.Mode == PowerModes.Suspend) SuspendControl(ControlPauseReason.Sleep);
    else if (args.Mode == PowerModes.Resume) OnWindowThread(() => ResumeControl(ControlPauseReason.Sleep));
  }
  private void SuspendControl(ControlPauseReason reason)
  {
    lock (lockGate)
    {
      if (quitting) return;
      CoreBridge.TryCall(new { op = "displayAwake", awake = false });
      var paused = true; // A failed status read must never resume control later.
      try { paused = CoreBridge.Call(new { op = "status" }).GetProperty("paused").GetBoolean(); }
      catch (InvalidOperationException error) { Diagnostics.Record(error); }
      if (automaticPause.Suspend(reason, paused)) CoreBridge.TryCall(new { op = "pause", paused = true });
    }
  }
  private void ResumeControl(ControlPauseReason reason)
  {
    lock (lockGate)
    {
      if (quitting) return;
      if (automaticPause.Resume(reason) is { } paused) CoreBridge.TryCall(new { op = "pause", paused });
      CoreBridge.TryCall(new { op = "displayAwake", awake = automaticPause.CanView });
    }
    RefreshSoon();
  }
  protected override void ExitThreadCore()
  {
    if (quitting) return; quitting = true; timer.Stop(); timer.Dispose();
    Updates.Dispose();
    SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
    SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
    System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
    CoreBridge.Event = null; CoreBridge.TryCall(new { op = "pause", paused = true }); CoreBridge.TryCall(new { op = "stop" });
    foreach (var approval in approvals.Values.ToArray()) { approval.End(); approval.Dispose(); }
    approvals.Clear(); Win32.UnregisterHotKey(window.Handle, 1); tray.Visible = false; tray.Dispose();
    foreach (var approval in displayApprovals.Values.ToArray()) { approval.End(); approval.Dispose(); }
    displayApprovals.Clear();
    Services.Dispose(); window.Shutdown(); base.ExitThreadCore();
  }
}
