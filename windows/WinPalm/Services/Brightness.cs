using System.Management;
using System.Runtime.InteropServices;
namespace WinPalm.Services;

internal static class Brightness
{
  private static readonly object Gate = new();
  internal static bool Step(bool up)
  {
    lock (Gate)
    {
      // Laptop panel: Windows exposes brightness through the WMI provider.
      try
      {
        using var query = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE");
        using var monitors = query.Get();
        foreach (ManagementObject monitor in monitors)
          using (monitor)
          {
            var current = Convert.ToInt32(monitor["CurrentBrightness"]);
            var instance = monitor["InstanceName"]?.ToString();
            using var methodsQuery = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
            using var methods = methodsQuery.Get();
            foreach (ManagementObject method in methods)
              using (method) if (method["InstanceName"]?.ToString() == instance)
              {
                method.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(current + (up ? 5 : -5), 0, 100)]);
                return true;
              }
          }
      }
      // WMI can be missing or broken (for example without the .NET Framework
      // helper it loads); fall through to DDC/CI either way.
      catch (Exception error) when (error is ManagementException or COMException or PlatformNotSupportedException or TypeInitializationException or DllNotFoundException or UnauthorizedAccessException) { }
      // External monitor: DDC/CI, using the display under the cursor.
      Win32Point point; GetCursorPos(out point);
      var handle = MonitorFromPoint(point, 2);
      if (!GetNumberOfPhysicalMonitorsFromHMONITOR(handle, out var count) || count is 0 or > 16) return false;
      var physical = new PhysicalMonitor[count];
      if (!GetPhysicalMonitorsFromHMONITOR(handle, count, physical)) return false;
      var changed = false;
      try
      {
        foreach (var monitor in physical)
          if (GetMonitorBrightness(monitor.Handle, out var min, out var current, out var max))
          {
            var step = Math.Max(1, (int)(max - min) / 20);
            changed |= SetMonitorBrightness(monitor.Handle, (uint)Math.Clamp((int)current + (up ? step : -step), (int)min, (int)max));
          }
      }
      finally { DestroyPhysicalMonitors(count, physical); }
      return changed;
    }
  }
  [StructLayout(LayoutKind.Sequential)] private struct Win32Point { internal int X, Y; }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct PhysicalMonitor { internal nint Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description; }
  [DllImport("user32.dll")] private static extern bool GetCursorPos(out Win32Point point);
  [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Win32Point point, uint flags);
  [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);
  [DllImport("dxva2.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PhysicalMonitor[] physical);
  [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetMonitorBrightness(nint monitor, out uint min, out uint current, out uint max);
  [DllImport("dxva2.dll", SetLastError = true)] private static extern bool SetMonitorBrightness(nint monitor, uint value);
  [DllImport("dxva2.dll", SetLastError = true)] private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] physical);
}
