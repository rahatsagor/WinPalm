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
  private MainWindow? window;
  private NotifyIcon? tray;
  private System.Windows.Forms.Timer? timer;
  private readonly Dictionary<ulong, ApprovalWindow> approvals = [];
  private readonly Dictionary<ulong, ApprovalWindow> displayApprovals = [];
  /// Lock and sleep arrive on the SystemEvents thread, unlock on the UI thread.
  private readonly object lockGate = new();
  private readonly ControlPauseState automaticPause = new();
  private bool AutomaticallyPaused { get { lock (lockGate) return automaticPause.Active; } }
  private ToolStripMenuItem? statusItem;
  private ToolStripMenuItem? pauseItem;
  private ToolStripMenuItem? updateItem;
  private bool busy, quitting;
  private nint lastLayout, lastForeground;
  private int checkingNetwork, resyncNeeded;
  private DateTime nextPeriodicCheck = DateTime.UtcNow.AddSeconds(30);
  private DateTime nextTimingSync = DateTime.UtcNow.AddSeconds(30);
  internal HostContext(Preferences preferences, bool minimized, bool completingUpdate = false)
  {
    Preferences = preferences; Updates = new(preferences); Services = new(preferences);
    try
    {
      CoreBridge.Desktop = Services;
      CoreBridge.Call(new { op = "create", config = Services.Config() });
      var liveWindow = new MainWindow(this);
      window = liveWindow;
      _ = liveWindow.Handle;
      CoreBridge.Event = value => { if (!quitting && !liveWindow.IsDisposed) liveWindow.BeginInvoke(() => OnEvent(value)); };
      var menu = new ContextMenuStrip { Font = Theme.Font(9.5f) };
      Theme.StyleContextMenu(menu);
      menu.Opening += (_, _) => { menu.Font = Theme.Font(9.5f); menu.Padding = Theme.Px(6, 6, 6, 6); };
      var status = new ToolStripMenuItem("WinPalm · Starting") { Enabled = false };
      statusItem = status;
      menu.Items.Add(status);
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add("Open WinPalm", null, (_, _) => ShowWindow());
      var pause = new ToolStripMenuItem("Pause control", null, (_, _) => TogglePause());
      pauseItem = pause;
      menu.Items.Add(pause);
      menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Restart server", null, (_, _) => Restart());
      var update = new ToolStripMenuItem("Check for updates…", null, async (_, _) => { ShowWindow(); liveWindow.ShowUpdates(); await Updates.CheckAsync(); });
      updateItem = update;
      menu.Items.Add(update);
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add("Quit WinPalm", null, (_, _) => ExitThread());
      var icon = new NotifyIcon { Text = "WinPalm · Starting", Icon = liveWindow.Icon, ContextMenuStrip = menu, Visible = true };
      tray = icon;
      icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
      icon.BalloonTipClicked += (_, _) => { if (Updates.Available is not null) { ShowWindow(); liveWindow.ShowUpdates(); } };
      Updates.Changed += () => { if (!quitting) { update.Text = Updates.Available is { } release ? $"WinPalm {release.Version.ToString(3)} available…" : "Check for updates…"; liveWindow.UpdateUpdates(); } };
      Updates.UpdateFound += () => { if (!quitting) icon.ShowBalloonTip(10000, "WinPalm update available", $"WinPalm {Updates.Available!.Version.ToString(3)} is ready. Open Updates to download and restart.", ToolTipIcon.Info); };
      Updates.RestartRequested += Quit;
      if (!Win32.RegisterHotKey(liveWindow.Handle, 1, 0x4003, 0x13)) icon.ShowBalloonTip(5000, "WinPalm", "Ctrl + Alt + Pause is already used by another app. Pause is available in the tray menu.", ToolTipIcon.Info);
      SystemEvents.SessionSwitch += SessionChanged; SystemEvents.PowerModeChanged += PowerChanged;
      SystemEvents.DisplaySettingsChanged += DisplaysChanged;
      System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
      RefreshSessionState();
      Start(completingUpdate); CheckNetwork();
      timer = new System.Windows.Forms.Timer { Interval = 2000 }; timer.Tick += async (_, _) => await Poll(); timer.Start();
      if (!minimized && !completingUpdate) ShowWindow();
      _ = Poll();
      Updates.CheckWhenDue();
    }
    catch
    {
      ExitThreadCore();
      throw;
    }
  }
  internal void Quit() => ExitThread();
  /// Closing the window keeps WinPalm running; say so once, since Windows 11
  /// hides new tray icons in the overflow menu.
  internal void ExplainTray()
  {
    if (Preferences.TrayHintShown) return;
    tray?.ShowBalloonTip(8000, "WinPalm is still running", "Phones stay connected. Open or quit WinPalm from its icon in the notification area, by the clock.", ToolTipIcon.Info);
    Preferences.TrayHintShown = true;
    try { Preferences.Save(); } catch (IOException error) { Diagnostics.Record(error); }
  }
  private void NetworkAddressChanged(object? sender, EventArgs args)
  {
    if (!quitting) CoreBridge.TryCall(new { op = "networkChanged" });
    CheckNetwork();
  }
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
      if (quitting || window is not { IsHandleCreated: true, IsDisposed: false } liveWindow) return;
      liveWindow.BeginInvoke(() => { if (!quitting && problem != NetworkProblem) { NetworkProblem = problem; liveWindow.NetworkChanged(); } });
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
    try { if (!quitting && window is { IsHandleCreated: true, IsDisposed: false } liveWindow) liveWindow.BeginInvoke(work); }
    catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
  }
  internal void ShowWindow()
  {
    if (window is null) return;
    window.Show();
    if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
    window.Activate();
    UpdatePollInterval();
  }
  internal void Start() => Start(false);
  private void Start(bool completingUpdate)
  {
    UpdateInstaller.RunStartup(
      () => { CoreBridge.Call(new { op = "start" }); KeyboardLayouts.UpdateTiming(); CoreBridge.Call(new { op = "layout", layout = KeyboardLayouts.Read() }); RefreshSoon(); },
      Diagnostics.Record,
      error => PalmDialog.Info(window?.Visible == true ? window : null, "WinPalm couldn't start", error.Message, BadgeTone.Danger),
      completingUpdate);
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
    try { Snapshot = CoreBridge.Call(new { op = "status" }); window?.UpdateState(Snapshot); UpdateTray(); }
    catch (Exception error) { Diagnostics.Record(error); }
  }
  private void UpdateTray()
  {
    var paused = Snapshot.GetProperty("paused").GetBoolean(); var running = Snapshot.GetProperty("running").GetBoolean(); var count = Snapshot.GetProperty("connected").GetArrayLength();
    var summary = !running ? "WinPalm · Sharing stopped" : paused ? "WinPalm · Paused" : count > 0 ? $"WinPalm · {count} connected" : "WinPalm · Ready to pair";
    if (tray is not null) tray.Text = summary;
    if (statusItem is not null) statusItem.Text = summary;
    if (pauseItem is not null) pauseItem.Text = paused ? "Resume control" : "Pause control";
  }
  internal void WindowVisibilityChanged() => UpdatePollInterval();
  private void UpdatePollInterval(bool? hasConnected = null)
  {
    if (timer is null || quitting) return;
    var connected = hasConnected ?? (Snapshot.ValueKind == JsonValueKind.Object && Snapshot.GetProperty("connected").GetArrayLength() > 0);
    var visible = window is { IsDisposed: false, Visible: true } && window.WindowState != FormWindowState.Minimized;
    var interval = connected || visible ? 2000 : 15000;
    if (timer.Interval != interval) timer.Interval = interval;
  }
  private async Task Poll()
  {
    if (busy || quitting) return; busy = true;
    try
    {
      var snapshot = await Task.Run(() =>
      {
        RefreshSessionState();
        var current = CoreBridge.Call(new { op = "status" });
        var hasConnected = current.GetProperty("connected").GetArrayLength() > 0;
        if (hasConnected)
        {
          if (Interlocked.Exchange(ref resyncNeeded, 0) == 1)
          {
            lastLayout = 0; lastForeground = 0; nextTimingSync = DateTime.MinValue;
          }
          var layout = KeyboardLayouts.Current; var foreground = Win32.GetForegroundWindow();
          if (layout != lastLayout) { lastLayout = layout; CoreBridge.Call(new { op = "layout", layout = KeyboardLayouts.Read() }); }
          // An administrator window in front blocks input; update the phone's badge.
          if (foreground != lastForeground) { lastForeground = foreground; CoreBridge.Call(new { op = "appsChanged" }); CoreBridge.Call(new { op = "permissionChanged" }); }
          var volume = AudioVolume.Read(); CoreBridge.Call(new { op = "volume", value = volume?.Value, muted = volume?.Muted });
          // Running apps are read fresh for every list; installed apps are
          // rescanned at most every two minutes, or after a launch.
          if (DateTime.UtcNow >= nextTimingSync)
          {
            nextTimingSync = DateTime.UtcNow.AddSeconds(30);
            KeyboardLayouts.UpdateTiming();
            CoreBridge.Call(new { op = "appsChanged" });
            CoreBridge.Call(new { op = "permissionChanged" });
          }
        }
        return current;
      });
      if (!quitting)
      {
        Snapshot = snapshot;
        window?.UpdateState(snapshot);
        UpdateTray();
        var hasConnected = snapshot.GetProperty("connected").GetArrayLength() > 0;
        UpdatePollInterval(hasConnected);
        if (DateTime.UtcNow >= nextPeriodicCheck)
        {
          nextPeriodicCheck = DateTime.UtcNow.AddSeconds(30);
          CheckNetwork();
          Updates.CheckWhenDue();
        }
      }
    }
    catch (Exception error) { Diagnostics.Record(error); }
    finally { busy = false; }
  }
  private void OnEvent(JsonElement value)
  {
    if (quitting) return;
    switch (value.GetProperty("event").GetString())
    {
      case "sessionsChanged":
        Volatile.Write(ref resyncNeeded, 1);
        UpdatePollInterval(true);
        _ = Poll();
        break;
      case "approvalRequested":
        var request = value.GetProperty("request"); var id = request.GetProperty("id").GetUInt64();
        if (AutomaticallyPaused) { CoreBridge.TryCall(new { op = "approve", id, allow = false }); break; }
        if (approvals.ContainsKey(id)) break;
        var approval = new ApprovalWindow(request, approvals.Count); approvals[id] = approval; approval.Show();
        tray?.ShowBalloonTip(8000, "WinPalm pairing request", "A nearby device is asking to control this PC. Review its name and address in WinPalm.", ToolTipIcon.Info); break;
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
      case "failed": tray?.ShowBalloonTip(8000, "WinPalm server", value.GetProperty("message").GetString() ?? "See the WinPalm log for details.", ToolTipIcon.Warning); break;
      case "notice": tray?.ShowBalloonTip(5000, "WinPalm", value.GetProperty("message").GetString() ?? "See the WinPalm log for details.", ToolTipIcon.Info); break;
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
      tray?.ShowBalloonTip(8000, "WinPalm screen mirroring request", "A connected device is asking to view your screen. Review its name and address in WinPalm.", ToolTipIcon.Info);
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
      tray?.ShowBalloonTip(8000, "WinPalm screen mirroring", "The screen-mirroring settings could not be saved. Check General and try again.", ToolTipIcon.Warning);
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
    // Stop immediately on a lock/disconnect event. Reconcile unlocks against
    // the current session rather than trusting a possibly queued old event.
    if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
      SuspendControl(ControlPauseReason.SessionLocked);
    else if (args.Reason is SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
      SuspendControl(ControlPauseReason.SessionDisconnected);
    else RefreshSessionState();
    OnWindowThread(RefreshSoon);
  }
  private void RefreshSessionState()
  {
    lock (lockGate)
    {
      if (quitting) return;
      var session = SessionAvailability.Read();
      var paused = true;
      try { paused = CoreBridge.Call(new { op = "status" }).GetProperty("paused").GetBoolean(); }
      catch (InvalidOperationException error) { Diagnostics.Record(error); }
      if (automaticPause.ReconcileSession(session, paused) is { } next)
        CoreBridge.TryCall(new { op = "pause", paused = next });
      CoreBridge.TryCall(new { op = "displayAwake", awake = automaticPause.CanView });
    }
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
      RefreshSessionState();
      if (automaticPause.Resume(reason) is { } paused) CoreBridge.TryCall(new { op = "pause", paused });
      CoreBridge.TryCall(new { op = "displayAwake", awake = automaticPause.CanView });
    }
    RefreshSoon();
  }
  protected override void ExitThreadCore()
  {
    if (quitting) return; quitting = true; timer?.Stop(); timer?.Dispose();
    Updates.Dispose();
    SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
    SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
    System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
    CoreBridge.Event = null; CoreBridge.TryCall(new { op = "pause", paused = true }); CoreBridge.TryCall(new { op = "stop" });
    foreach (var approval in approvals.Values.ToArray()) { approval.End(); approval.Dispose(); }
    approvals.Clear();
    if (window is { IsDisposed: false } liveWindow)
    {
      if (liveWindow.IsHandleCreated) Win32.UnregisterHotKey(liveWindow.Handle, 1);
      liveWindow.Shutdown();
    }
    if (tray is not null) { tray.Visible = false; tray.Dispose(); }
    foreach (var approval in displayApprovals.Values.ToArray()) { approval.End(); approval.Dispose(); }
    displayApprovals.Clear();
    Services.Dispose(); base.ExitThreadCore();
  }
  protected override void Dispose(bool disposing)
  {
    if (disposing) ExitThreadCore();
    base.Dispose(disposing);
  }
}
