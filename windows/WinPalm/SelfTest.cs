using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WinPalm.Services;
namespace WinPalm;
/// Disposable Windows integration check. Never touches the owner's pairing data.
internal static class SelfTest
{
  internal static int Run(string[] args)
  {
    var checks = new List<string>();
    var directory = Path.Combine(Path.GetTempPath(), "WinPalm-selftest-" + Guid.NewGuid());
    Preferences.UseTestDirectory(directory);
    try
    {
      Preferences.Load(); checks.Add("private data ACL");
      var preferences = new Preferences { Port = 0, Dictation = false };
      preferences.Save(); preferences.Save(); checks.Add("atomic preference replacement");
      CoreBridge.Initialize(); using var desktop = new DesktopServices(preferences); CoreBridge.Desktop = desktop;
      CoreBridge.Call(new { op = "create", config = desktop.Config(loopbackOnly: true), dryRun = true });
      var port = CoreBridge.Call(new { op = "start" }).GetInt32();
      checks.Add("native DLL ABI and server start");
      CoreBridge.Call(new { op = "layout", layout = KeyboardLayouts.Read() }); KeyboardLayouts.UpdateTiming(); checks.Add("Windows layout and input timing");
      if (System.Runtime.InteropServices.Marshal.SizeOf<Win32.Input>() != 40) throw new InvalidOperationException("Unexpected Win32 INPUT ABI size");
      if (desktop.Apps.List().Length < 6 || desktop.Apps.Action("not-a-catalog-id", "open")) throw new InvalidOperationException("App catalog validation failed");
      checks.Add("Windows app catalog and identifier validation");
      var audio = AudioVolume.Read(); checks.Add(audio is null ? "audio endpoint unavailable (hardware check needed)" : "Core Audio endpoint read");
      using (var client = new HttpClient()) { var html = client.GetStringAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult(); if (!html.Contains("WinPalm")) throw new InvalidOperationException("Bundled phone client missing"); }
      checks.Add("bundled phone client HTTP");
      var state = CoreBridge.Call(new { op = "status" }); var link = state.GetProperty("pairing").GetProperty("link").GetString()!;
      using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{port}");
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
      socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), timeout.Token).GetAwaiter().GetResult();
      var code = new Uri(link).Fragment["#pair=".Length..];
      var hello = JsonSerializer.SerializeToUtf8Bytes(new { t = "hello", v = 1, device = new { id = "windows-selftest", name = "Disposable self-test", model = "Windows" }, auth = new { method = "pair", code }, remember = true });
      socket.SendAsync(hello, WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();
      var buffer = new byte[65536]; var received = socket.ReceiveAsync(buffer, timeout.Token).GetAwaiter().GetResult();
      using var welcome = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
      if (welcome.RootElement.GetProperty("t").GetString() != "welcome" || welcome.RootElement.GetProperty("host").GetProperty("os").GetString() != "Windows") throw new InvalidOperationException("Pairing failed");
      checks.Add("QR pairing and Windows welcome");
      CoreBridge.Call(new { op = "pause", paused = true }); CoreBridge.Call(new { op = "forgetAll" }); checks.Add("pause and device revocation");
      CoreBridge.Call(new { op = "stop" }); checks.Add("graceful shutdown");
      Write(args, new { ok = true, checks, runtime = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), nativeInputTested = false });
      return 0;
    }
    catch (Exception error) { Write(args, new { ok = false, checks, error = error.ToString() }); return 1; }
    finally { try { CoreBridge.TryCall(new { op = "stop" }); Directory.Delete(directory, true); } catch (IOException) { } }
  }
  private static void Write(string[] args, object report)
  {
    var index = Array.IndexOf(args, "--report"); var json = JsonSerializer.Serialize(report, Preferences.Json);
    if (index >= 0 && index + 1 < args.Length) File.WriteAllText(args[index + 1], json);
    else File.WriteAllText(Path.Combine(Path.GetTempPath(), "WinPalm-selftest.json"), json);
  }
}
