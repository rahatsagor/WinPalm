namespace WinPalm.Services;

/// Apply revocation before storage, and persist opt-in before making it live.
/// The owner serializes changes on the UI thread.
internal static class DisplayPreference
{
  internal static void Apply(bool enabled, bool previous, Action<bool> change, Action persist, Action applyLive, Action<Exception> report)
  {
    change(enabled);
    try
    {
      if (!enabled) applyLive();
      persist();
      if (enabled) applyLive();
    }
    catch
    {
      // A failed disable must stay disabled. A failed enable restores the
      // previous preference in memory, in the core and, if possible, on disk.
      if (enabled) change(previous);
      try { applyLive(); } catch (Exception error) { report(error); }
      if (enabled)
        try { persist(); } catch (Exception error) { report(error); }
      throw;
    }
  }
}
