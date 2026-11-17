namespace WinPalm.Services;

[Flags]
internal enum ControlPauseReason { SessionLocked = 1, SessionDisconnected = 2, Sleep = 4 }

/// Automatic pauses overlap. Only the last matching resume may restore control.
/// The caller serializes access with its pause lock.
internal sealed class ControlPauseState
{
  private ControlPauseReason reasons;
  private bool previouslyPaused;
  internal bool Active => reasons != 0;
  // Display power affects viewing independently of keyboard/trackpad control.
  internal bool DisplayAwake { get; set; } = true;
  internal bool CanView => DisplayAwake && !Active;

  internal bool? ReconcileSession(ControlPauseReason session, bool paused)
  {
    var changed = false;
    ControlPauseReason[] reasons = [ControlPauseReason.SessionLocked, ControlPauseReason.SessionDisconnected];
    // Add new blockers before removing old ones. A transition between lock
    // and disconnect must never briefly restore control or screen access.
    foreach (var reason in reasons)
      if ((session & reason) != 0) changed |= Suspend(reason, paused);
    bool? restore = null;
    foreach (var reason in reasons)
      if ((session & reason) == 0 && Resume(reason) is { } previous) restore = previous;
    return Active && changed ? true : restore;
  }

  internal bool Suspend(ControlPauseReason reason, bool paused)
  {
    if ((reasons & reason) != 0) return false;
    if (!Active) previouslyPaused = paused;
    reasons |= reason;
    return true;
  }

  internal bool? Resume(ControlPauseReason reason)
  {
    if ((reasons & reason) == 0) return null;
    reasons &= ~reason;
    return Active ? null : previouslyPaused;
  }
}
