using System.Runtime.InteropServices;
namespace WinPalm.Services;

internal static class DisplayPower
{
  // Interactive applications use the power state of their own session.
  internal static readonly Guid Setting = new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
  [DllImport("user32.dll", EntryPoint = "RegisterPowerSettingNotification", SetLastError = true)]
  internal static extern nint Register(nint recipient, ref Guid setting, uint flags);
  [DllImport("user32.dll", EntryPoint = "UnregisterPowerSettingNotification")]
  internal static extern bool Unregister(nint notification);

  [StructLayout(LayoutKind.Sequential)]
  internal struct Header { internal Guid Setting; internal uint DataLength; }
  internal static bool? Read(nint notification)
  {
    if (notification == 0) return null;
    var header = Marshal.PtrToStructure<Header>(notification);
    if (header.Setting != Setting || header.DataLength != sizeof(uint)) return null;
    return Marshal.ReadInt32(notification, Marshal.SizeOf<Header>()) switch { 0 => false, 1 or 2 => true, _ => null };
  }
}
