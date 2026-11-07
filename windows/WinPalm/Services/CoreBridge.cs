using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinPalm.Services;

internal static class CoreBridge
{
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Callback(nint request);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Release(nint value);
  private static readonly Callback InvokeRoot = Invoke;
  private static readonly Release ReleaseRoot = Marshal.FreeCoTaskMem;
  internal static DesktopServices? Desktop { get; set; }
  internal static Action<JsonElement>? Event { get; set; }
  [DllImport("winpalm_core", CallingConvention = CallingConvention.Cdecl)]
  [return: MarshalAs(UnmanagedType.I1)] private static extern bool winpalm_initialize(Callback invoke, Release release);
  [DllImport("winpalm_core", CallingConvention = CallingConvention.Cdecl)] private static extern nint winpalm_call([MarshalAs(UnmanagedType.LPUTF8Str)] string request);
  [DllImport("winpalm_core", CallingConvention = CallingConvention.Cdecl)] private static extern void winpalm_free(nint value);
  internal static void Initialize()
  {
    NativeLibrary.SetDllImportResolver(typeof(CoreBridge).Assembly, (name, assembly, path) => name == "winpalm_core"
        ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "winpalm_core.dll")) : 0);
    if (!winpalm_initialize(InvokeRoot, ReleaseRoot)) throw new InvalidOperationException("Core callbacks were already initialized");
  }
  internal static JsonElement Call(object request)
  {
    var result = winpalm_call(JsonSerializer.Serialize(request, Preferences.Json));
    if (result == 0) throw new InvalidOperationException("No response from WinPalm core");
    try
    {
      using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(result)!);
      var root = document.RootElement;
      if (!root.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(root.GetProperty("error").GetString());
      return root.GetProperty("value").Clone();
    }
    finally { winpalm_free(result); }
  }
  internal static void TryCall(object request)
  {
    try { Call(request); } catch (Exception error) { Diagnostics.Record(error); }
  }
  private static nint Invoke(nint request)
  {
    object? result = null;
    try
    {
      using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(request)!);
      var op = document.RootElement.GetProperty("op").GetString();
      var value = document.RootElement.GetProperty("value");
      if (op == "onEvent") Event?.Invoke(value.Clone());
      else result = Desktop?.Invoke(op ?? "", value);
    }
    catch (Exception error) { Diagnostics.Record(error); }
    return Marshal.StringToCoTaskMemUTF8(JsonSerializer.Serialize(result, Preferences.Json));
  }
}

internal static class Diagnostics
{
  private static readonly object Gate = new();
  internal static string FilePath => Path.Combine(Preferences.DataDirectory, "WinPalm.log");
  internal static void Record(Exception error)
  {
    // Do not log messages, arguments, URLs, clipboard, PINs or speech text.
    lock (Gate) try
      {
        if (System.IO.File.Exists(FilePath) && new FileInfo(FilePath).Length > 512_000) System.IO.File.Move(FilePath, FilePath + ".previous", true);
        System.IO.File.AppendAllText(FilePath, $"{DateTimeOffset.UtcNow:O} {error.GetType().Name} HRESULT=0x{error.HResult:X8}\n");
      }
      catch (IOException) { }
  }
}
