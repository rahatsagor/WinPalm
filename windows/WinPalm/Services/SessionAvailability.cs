using System.Runtime.InteropServices;
using System.Text;
namespace WinPalm.Services;

internal static class SessionAvailability
{
  [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool WTSQuerySessionInformationW(nint server, int session, int information, out nint buffer, out uint bytes);
  [DllImport("wtsapi32.dll")]
  private static extern void WTSFreeMemory(nint buffer);
  [DllImport("user32.dll", SetLastError = true)]
  private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
  [DllImport("user32.dll")]
  private static extern bool CloseDesktop(nint desktop);
  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool GetUserObjectInformationW(nint desktop, int index, StringBuilder text, uint bytes, out uint needed);
  [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
  private static extern bool GetInputState(nint desktop, int index, out int receivingInput, uint bytes, out uint needed);

  internal static ControlPauseReason Read() => Read(QuerySession, InteractiveDesktop);

  internal static ControlPauseReason Read(Func<ControlPauseReason?> querySession, Func<bool> interactiveDesktop) =>
    querySession() ?? (interactiveDesktop() ? 0 : ControlPauseReason.SessionLocked);

  private static ControlPauseReason? QuerySession()
  {
    // WTS_CURRENT_SESSION, WTSSessionInfoEx. Query our own session, including
    // before the first lock/unlock event after launch. Both supported RIDs
    // align the WTSINFOEX_LEVEL1 union to 8 bytes.
    if (!WTSQuerySessionInformationW(0, -1, 25, out var buffer, out var bytes))
      return null;
    try { return Parse(buffer, bytes); }
    finally { WTSFreeMemory(buffer); }
  }

  private static bool InteractiveDesktop()
  {
    // WTS queries fail when Remote Desktop Services is stopped. Do not make
    // local control depend on that service. Opening the input desktop alone
    // is insufficient: a disconnected session can return its future desktop.
    var desktop = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
    if (desktop == 0) return false;
    try
    {
      var name = new StringBuilder(256);
      if (!GetUserObjectInformationW(desktop, 2, name, (uint)name.Capacity * sizeof(char), out _)) return false;
      if (!GetInputState(desktop, 6, out var receivingInput, sizeof(int), out _)) return false; // UOI_IO
      return IsInteractiveDesktop(name.ToString(), receivingInput != 0);
    }
    finally { CloseDesktop(desktop); }
  }

  internal static bool IsInteractiveDesktop(string? name, bool receivingInput) =>
    receivingInput && string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);

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
