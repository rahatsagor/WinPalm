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
