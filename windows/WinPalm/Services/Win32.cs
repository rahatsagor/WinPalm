using System.Runtime.InteropServices;
using System.Text;
namespace WinPalm.Services;

internal static class Win32
{
  internal delegate bool EnumWindowsProc(nint window, nint data);
  [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, nint data);
  [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
  [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
  [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
  [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
  [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
  [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(nint window, int command);
  [DllImport("user32.dll")] internal static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
  [DllImport("user32.dll")] internal static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextW(nint window, StringBuilder text, int length);
  [DllImport("user32.dll")] internal static extern bool LockWorkStation();
  [DllImport("powrprof.dll", SetLastError = true)] internal static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
  [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint thread);
  [DllImport("user32.dll")] internal static extern uint MapVirtualKeyExW(uint code, uint type, nint layout);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder output, int capacity, uint flags, nint layout);
  [DllImport("user32.dll")] internal static extern uint GetDoubleClickTime();
  [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
  [DllImport("user32.dll")] internal static extern bool SystemParametersInfoW(uint action, uint parameter, out uint value, uint flags);
  [DllImport("user32.dll")] internal static extern uint SendInput(uint count, Input[] events, int size);
  [StructLayout(LayoutKind.Sequential)] internal struct Input { internal uint Type; internal InputUnion Data; }
  [StructLayout(LayoutKind.Explicit)]
  internal struct InputUnion
  {
    [FieldOffset(0)] internal KeyboardInput Keyboard;
    [FieldOffset(0)] internal MouseInput Mouse;
  }
  [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { internal ushort Key, Scan; internal uint Flags, Time; internal nuint Extra; }
  [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int length);
  [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
  [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
  [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(int process);
  [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
  private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow"];
  internal static string ClassName(nint window) { var name = new StringBuilder(256); return GetClassNameW(window, name, name.Capacity) > 0 ? name.ToString() : ""; }
  /// The desktop, taskbar and system UI. WM_CLOSE to the desktop opens the
  /// Shut Down Windows dialog, so never treat these as app windows.
  internal static bool IsShellWindow(nint window) => ShellClasses.Contains(ClassName(window), StringComparer.Ordinal);
  /// A window that Alt+Tab would show: visible, uncloaked, unowned and not a
  /// tool window, unless it explicitly asks for a taskbar button.
  internal static bool IsAppWindow(nint window)
  {
    if (!IsWindowVisible(window) || IsShellWindow(window)) return false;
    if (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
    var style = (long)GetWindowLongPtr(window, -20);
    if ((style & 0x40000) != 0) return true; // WS_EX_APPWINDOW
    return (style & 0x80) == 0 && GetWindow(window, 4) == 0; // WS_EX_TOOLWINDOW, GW_OWNER
  }
  /// Windows only lets the process that received the last input take the
  /// foreground. A zero-motion mouse input from this process satisfies that
  /// rule without moving or clicking anything.
  internal static bool Activate(nint window)
  {
    if (IsIconic(window)) ShowWindowAsync(window, 9);
    if (SetForegroundWindow(window)) return true;
    Input[] nudge = [new() { Type = 0 }];
    SendInput(1, nudge, Marshal.SizeOf<Input>());
    return SetForegroundWindow(window);
  }
  [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
  [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);
  [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges state, int length, nint previous, nint returned);
  [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
  [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
  [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct TokenPrivileges { internal uint Count; internal long Luid; internal uint Attributes; }
  /// Sleep needs SeShutdownPrivilege, which standard and administrator
  /// tokens hold but leave disabled.
  internal static bool EnableShutdownPrivilege()
  {
    if (!OpenProcessToken(GetCurrentProcess(), 0x28, out var token)) return false; // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY
    try
    {
      if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid)) return false;
      var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 }; // SE_PRIVILEGE_ENABLED
      return AdjustTokenPrivileges(token, false, ref state, 0, 0, 0) && Marshal.GetLastWin32Error() == 0;
    }
    finally { CloseHandle(token); }
  }
  internal static bool Key(ushort key)
  {
    Input[] keys = [new() { Type = 1, Data = new() { Keyboard = new() { Key = key } } }, new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = 2 } } }];
    return SendInput(2, keys, Marshal.SizeOf<Input>()) == 2;
  }
  [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
  [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
  [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
