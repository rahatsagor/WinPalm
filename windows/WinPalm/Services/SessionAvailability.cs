using System.Runtime.InteropServices;
namespace WinPalm.Services;

internal static class SessionAvailability
{
  [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool WTSQuerySessionInformationW(nint server, int session, int information, out nint buffer, out uint bytes);
  [DllImport("wtsapi32.dll")]
  private static extern void WTSFreeMemory(nint buffer);

  internal static ControlPauseReason Read()
  {
    // WTS_CURRENT_SESSION, WTSSessionInfoEx. Query our own session, including
    // before the first lock/unlock event after launch. Both supported RIDs
    // align the WTSINFOEX_LEVEL1 union to 8 bytes.
    if (!WTSQuerySessionInformationW(0, -1, 25, out var buffer, out var bytes))
      return ControlPauseReason.SessionLocked;
    try { return Parse(buffer, bytes); }
    finally { WTSFreeMemory(buffer); }
  }

  internal static ControlPauseReason Parse(nint buffer, uint bytes)
  {
    if (buffer == 0 || bytes < 20 || Marshal.ReadInt32(buffer) != 1)
      return ControlPauseReason.SessionLocked;
    var state = Marshal.ReadInt32(buffer, 12); // WTS_CONNECTSTATE_CLASS
    var flags = Marshal.ReadInt32(buffer, 16); // WTS_SESSIONSTATE_*
    var result = flags == 1 ? (ControlPauseReason)0 : ControlPauseReason.SessionLocked;
    if (state != 0) result |= ControlPauseReason.SessionDisconnected; // WTSActive
    return result;
  }
}
