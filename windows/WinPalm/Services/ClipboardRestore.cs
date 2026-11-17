namespace WinPalm.Services;

/// Ownership is independent of the original clipboard having any data.
/// The caller serializes access on its clipboard STA.
internal sealed class ClipboardRestore
{
  private uint? sequence;
  internal void Begin(uint ownedSequence) => sequence = ownedSequence;
  internal void End(uint currentSequence, Action restore)
  {
    if (sequence is not { } owned) return;
    if (currentSequence == owned) restore();
    // Keep ownership if restore throws, so the shutdown path can retry.
    sequence = null;
  }
}
