using System.Net.WebSockets;
using System.Text.Json;
using WinPalm.Services;
namespace WinPalm;
/// Explicit opt-in native input check. A disposable local editor owns focus
/// before every send. No shell, external app, or clipboard writes are used.
internal static class InputTest
{
  internal static int Run(string[] args)
  {
    using var form = new Form { Text = "WinPalm · Native input test", ClientSize = new Size(700, 420), StartPosition = FormStartPosition.CenterScreen, TopMost = true };
    using var editor = new TextBox { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 16), ScrollBars = ScrollBars.Vertical };
    using var status = new Label { Text = "Testing in this temporary editor. Leave the mouse and keyboard idle for a few seconds.", Dock = DockStyle.Top, Height = 64, Padding = new Padding(12) };
    form.Controls.Add(editor); form.Controls.Add(status);
    var checks = new List<string>(); var result = 1;
    var directory = Path.Combine(Path.GetTempPath(), "WinPalm-input-test-" + Guid.NewGuid());
    var originalCursor = Cursor.Position;
    form.Shown += async (_, _) =>
    {
      ClientWebSocket? socket = null;
      try
      {
        Preferences.UseTestDirectory(directory); Preferences.Load(); CoreBridge.Initialize();
        using var desktop = new DesktopServices(new Preferences { Port = 0, Dictation = false, AllowApproval = false, AllowPin = false });
        CoreBridge.Desktop = desktop;
        CoreBridge.Call(new { op = "create", config = desktop.Config(loopbackOnly: true) });
        var port = CoreBridge.Call(new { op = "start" }).GetInt32();
        CoreBridge.Call(new { op = "layout", layout = KeyboardLayouts.Read() });
        CoreBridge.Call(new { op = "timing", delay = 250, interval = 50, doubleClick = 500 });
        var state = CoreBridge.Call(new { op = "status" });
        var link = new Uri(state.GetProperty("pairing").GetProperty("link").GetString()!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{port}");
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), timeout.Token);
        await Send(socket, new { t = "hello", v = 1, device = new { id = "native-input-test", name = "Disposable input test", model = "Windows" }, auth = new { method = "pair", code = link.Fragment["#pair=".Length..] }, remember = false }, timeout.Token);
        var buffer = new byte[65536]; var message = await socket.ReceiveAsync(buffer, timeout.Token);
        using var welcome = JsonDocument.Parse(buffer.AsMemory(0, message.Count));
        if (welcome.RootElement.GetProperty("t").GetString() != "welcome") throw new InvalidOperationException("Local pairing failed.");
        form.Activate(); editor.Focus(); await Task.Delay(150, timeout.Token);
        void Focus() { if (Win32.GetForegroundWindow() != form.Handle || !editor.Focused) throw new InvalidOperationException("The test window lost focus; input was stopped. Run again while leaving the PC idle."); }
        async Task Submit(object value) { Focus(); await Send(socket, value, timeout.Token); }
        async Task Expect(Func<bool> condition, string name)
        {
          for (var i = 0; i < 30 && !condition(); i++) await Task.Delay(50, timeout.Token);
          Focus(); if (!condition()) throw new InvalidOperationException(name + " failed"); checks.Add(name);
        }
        await Submit(new { t = "text", text = "WinPalm café 🌴" });
        await Expect(() => editor.Text == "WinPalm café 🌴", "Unicode and surrogate pairs");
        await Submit(new { t = "action", id = 1, action = new { kind = "system", name = "selectAll" } });
        await Task.Delay(100, timeout.Token);
        await Submit(new { t = "text", text = "replaced" });
        await Expect(() => editor.Text == "replaced", "Ctrl+A Windows shortcut");
        await Submit(new { t = "key", code = 0x3b, down = true }); await Task.Delay(100, timeout.Token);
        await Submit(new { t = "text", text = " safe" });
        await Submit(new { t = "key", code = 0x3b, down = false });
        await Expect(() => editor.Text == "replaced safe", "Unicode ignores held remote Ctrl");
        await Submit(new { t = "tap", code = 0x24 });
        await Expect(() => editor.Text.EndsWith("\r\n", StringComparison.Ordinal), "Enter scan code");
        await Submit(new { t = "key", code = 0x31, down = true }); await Task.Delay(450, timeout.Token);
        await Submit(new { t = "key", code = 0x31, down = false });
        await Expect(() => editor.Text.Count(c => c == ' ') >= 4, "Held key and repeat");
        var before = editor.Text;
        await Submit(new { t = "key", code = 0x38, down = true }); await Task.Delay(80, timeout.Token);
        CoreBridge.Call(new { op = "pause", paused = true });
        await Send(socket, new { t = "text", text = "BLOCKED" }, timeout.Token); await Task.Delay(150, timeout.Token);
        if (editor.Text != before) throw new InvalidOperationException("Pause allowed input");
        checks.Add("Pause blocks input and releases held keys");
        CoreBridge.Call(new { op = "pause", paused = false });
        await Submit(new { t = "text", text = "after" }); await Expect(() => editor.Text.EndsWith("after", StringComparison.Ordinal), "Resume after pause");
        // Pointer stays within this temporary editor.
        Focus(); var point = editor.PointToScreen(new Point(100, 60)); Cursor.Position = point;
        var motion = new byte[9]; motion[0] = 1; BitConverter.GetBytes(20f).CopyTo(motion, 1); BitConverter.GetBytes(15f).CopyTo(motion, 5);
        await socket.SendAsync(motion, WebSocketMessageType.Binary, true, timeout.Token);
        await Expect(() => Cursor.Position.X > point.X && Cursor.Position.Y > point.Y, "Relative pointer movement");
        var clicks = 0; editor.MouseDown += (_, _) => clicks++;
        await Submit(new { t = "button", button = "left", op = "click" }); await Expect(() => clicks > 0, "Native mouse click");
        await Submit(new { t = "key", code = 0x3b, down = true }); await Task.Delay(80, timeout.Token);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Finished", timeout.Token);
        await Task.Delay(200, timeout.Token);
        if ((Win32.GetAsyncKeyState(0x11) & 0x8000) != 0) throw new InvalidOperationException("Ctrl remained held after disconnect");
        checks.Add("Disconnect releases modifiers"); result = 0;
        Write(args, new { ok = true, checks, nativeInputTested = true, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() });
      }
      catch (Exception error) { Write(args, new { ok = false, checks, error = error.Message, nativeInputTested = true }); }
      finally
      {
        socket?.Abort(); socket?.Dispose(); CoreBridge.TryCall(new { op = "pause", paused = true }); CoreBridge.TryCall(new { op = "stop" });
        if (form.Bounds.Contains(Cursor.Position)) Cursor.Position = originalCursor;
        try { Directory.Delete(directory, true); } catch (IOException) { }
        form.Close();
      }
    };
    Application.Run(form); return result;
  }
  private static Task Send(ClientWebSocket socket, object message, CancellationToken token) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, token);
  private static void Write(string[] args, object result)
  {
    var index = Array.IndexOf(args, "--report");
    File.WriteAllText(index >= 0 && index + 1 < args.Length ? args[index + 1] : Path.Combine(Path.GetTempPath(), "WinPalm-input-test.json"), JsonSerializer.Serialize(result, Preferences.Json));
  }
}
