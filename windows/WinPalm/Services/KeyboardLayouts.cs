using System.Globalization;
using System.Text;
using System.Text.Json;
namespace WinPalm.Services;

internal static class KeyboardLayouts
{
  private static JsonElement? scans;
  internal static nint Current => Win32.GetKeyboardLayout(Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _));
  internal static object Read()
  {
    scans ??= CoreBridge.Call(new { op = "scanCodes" });
    var layout = Current;
    var language = (int)((long)layout & 0xffff);
    var name = "Windows keyboard";
    try { name = CultureInfo.GetCultureInfo(language).DisplayName; } catch (CultureNotFoundException) { }
    var keys = new List<object>();
    foreach (var item in scans.Value.EnumerateArray())
    {
      var code = item.GetProperty("code").GetUInt32();
      var scan = item.GetProperty("scan").GetUInt32();
      if (item.GetProperty("extended").GetBoolean()) continue;
      var vk = Win32.MapVirtualKeyExW(scan, 3, layout);
      if (vk == 0 || (vk is >= 0xa0 and <= 0xa5)) continue;
      var levels = new string[4]; var dead = 0;
      for (var level = 0; level < 4; level++)
      {
        var state = new byte[256];
        if ((level & 1) != 0) state[0x10] = 0x80;
        if ((level & 2) != 0) { state[0x11] = 0x80; state[0x12] = 0x80; state[0xa5] = 0x80; }
        var output = new StringBuilder(8);
        var length = Win32.ToUnicodeEx(vk, scan, state, output, output.Capacity, 4, layout);
        levels[level] = output.ToString().Trim('\0');
        if (length < 0) dead |= 1 << level;
        if (levels[level].Any(char.IsControl)) levels[level] = "";
      }
      if (levels.All(string.IsNullOrEmpty)) continue;
      keys.Add(new { code, @base = levels[0], shift = levels[1], option = levels[2], optionShift = levels[3], dead });
    }
    // Logical ISO layouts commonly used with Windows European keyboards.
    var iso = new[] { 0x0809, 0x0407, 0x040c, 0x0410, 0x040a, 0x041d, 0x0414, 0x0406, 0x0816, 0x040b }.Contains(language);
    return new { id = ((long)layout).ToString("X"), name, iso, keys };
  }
  internal static void UpdateTiming()
  {
    Win32.SystemParametersInfoW(0x16, 0, out var delay, 0);
    Win32.SystemParametersInfoW(0x0a, 0, out var speed, 0);
    CoreBridge.Call(new { op = "timing", delay = 250 + Math.Min(delay, 3) * 250, interval = (uint)(1000 / (2.5 + Math.Min(speed, 31) * 27.5 / 31)), doubleClick = Win32.GetDoubleClickTime() });
  }
}
