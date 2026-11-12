using System.Net.WebSockets;
using System.Text.Json;
using WinPalm.Services;
namespace WinPalm;
/// Explicit opt-in native input check. A disposable local editor owns focus
/// before every send. No shell, external app, or clipboard writes are used.
internal static class InputTest
{
  private const string TestRevision = "20261112-input-r3";

  internal static int Run(string[] args)
  {
    using var form = new Form { Text = "WinPalm · Native input test", ClientSize = new Size(700, 420), StartPosition = FormStartPosition.CenterScreen, TopMost = true };
    using var editor = new TextBox { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 16), ScrollBars = ScrollBars.Vertical };
    using var status = new Label { Text = "Testing in this temporary editor. Leave the mouse and keyboard idle for a few seconds.", Dock = DockStyle.Top, Height = 64, Padding = new Padding(12) };
    form.Controls.Add(editor); form.Controls.Add(status);
    var checks = new List<string>(); var result = 1;
    object? pauseObservation = null;
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
        await ReceiveMatching(socket, message => message.GetProperty("t").GetString() == "welcome", "Local pairing", timeout.Token);
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
        var spacesBeforeHold = editor.Text.Count(c => c == ' ');
        await Submit(new { t = "key", code = 0x31, down = true });
        await Expect(() => editor.Text.Count(c => c == ' ') >= spacesBeforeHold + 3, "Held key and repeat");
        await Submit(new { t = "key", code = 0x31, down = false });
        // Sending a key-up only queues it. Wait for both native release and a
        // later text marker to reach the editor before recording the baseline;
        // otherwise an already-posted Space repeat can look like paused input.
        const string repeatEnd = "[repeat-ended]";
        await Submit(new { t = "text", text = repeatEnd });
        await Expect(() => (Win32.GetAsyncKeyState(0x20) & 0x8000) == 0 && editor.Text.EndsWith(repeatEnd, StringComparison.Ordinal), "Held Space released and queued text settled");
        await Submit(new { t = "key", code = 0x38, down = true });
        await Expect(() => (Win32.GetAsyncKeyState(0xa0) & 0x8000) != 0, "Remote Shift held before pause");
        CoreBridge.Call(new { op = "pause", paused = true });
        if (!CoreBridge.Call(new { op = "status" }).GetProperty("paused").GetBoolean()) throw new InvalidOperationException("Host did not enter the paused state");
        await ReceiveMatching(socket, message => message.GetProperty("t").GetString() == "paused" && message.GetProperty("paused").GetBoolean(), "Phone pause notification", timeout.Token);
        await Expect(() => (Win32.GetAsyncKeyState(0xa0) & 0x8000) == 0, "Pause releases held Shift");
        var before = editor.Text;
        const string blockedText = "[BLOCKED-WHILE-PAUSED]";
        await Send(socket, new { t = "text", text = blockedText }, timeout.Token);
        await ReceiveMatching(socket, message => message.GetProperty("t").GetString() == "notice" && message.GetProperty("code").GetString() == "paused", "Paused text rejection", timeout.Token);
        // Let posted Windows messages run after the server has handled the
        // attempted text. Do not discard or reset any editor changes here.
        await Task.Delay(250, timeout.Token);
        Focus();
        pauseObservation = new
        {
          beforeLength = before.Length,
          afterLength = editor.Text.Length,
          blockedMarkerObserved = editor.Text.Contains(blockedText, StringComparison.Ordinal),
          spaceHeld = (Win32.GetAsyncKeyState(0x20) & 0x8000) != 0,
          shiftHeld = (Win32.GetAsyncKeyState(0xa0) & 0x8000) != 0
        };
        if (editor.Text != before) throw new InvalidOperationException("Editor changed after confirmed pause and text rejection; see pauseObservation");
        checks.Add("Pause rejects phone text without changing the editor");
        CoreBridge.Call(new { op = "pause", paused = false });
        await Submit(new { t = "text", text = "after" }); await Expect(() => editor.Text.EndsWith("after", StringComparison.Ordinal), "Resume after pause");
        // Pointer stays within this temporary editor.
        Focus(); var point = editor.PointToScreen(new Point(100, 60)); Cursor.Position = point;
        var motion = new byte[9]; motion[0] = 1; BitConverter.GetBytes(20f).CopyTo(motion, 1); BitConverter.GetBytes(15f).CopyTo(motion, 5);
        await socket.SendAsync(motion, WebSocketMessageType.Binary, true, timeout.Token);
        await Expect(() => Cursor.Position.X > point.X && Cursor.Position.Y > point.Y, "Relative pointer movement");
        var clicks = 0; editor.MouseDown += (_, _) => clicks++;
        await Submit(new { t = "button", button = "left", op = "click" }); await Expect(() => clicks > 0, "Native mouse click");
        await Submit(new { t = "key", code = 0x3b, down = true });
        await Expect(() => (Win32.GetAsyncKeyState(0xa2) & 0x8000) != 0, "Remote Ctrl held before disconnect");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Finished", timeout.Token);
        checks.Add("WebSocket close handshake");
        await Expect(() => (Win32.GetAsyncKeyState(0x11) & 0x8000) == 0, "Disconnect releases modifiers");
        result = 0;
        Write(args, new { ok = true, checks, nativeInputTested = true, testRevision = TestRevision, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), pauseObservation });
      }
      catch (Exception error) { Write(args, new { ok = false, checks, error = error.Message, nativeInputTested = true, testRevision = TestRevision, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), pauseObservation }); }
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
  private static async Task ReceiveMatching(ClientWebSocket socket, Func<JsonElement, bool> matches, string step, CancellationToken token)
  {
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
    deadline.CancelAfter(TimeSpan.FromSeconds(3));
    var buffer = new byte[65536];
    try
    {
      while (true)
      {
        var count = 0;
        WebSocketReceiveResult received;
        do
        {
          if (count == buffer.Length) throw new InvalidOperationException("The test received an oversized server message");
          received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), deadline.Token);
          if (received.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("The test connection closed or returned a non-text message");
          count += received.Count;
        } while (!received.EndOfMessage);
        using var message = JsonDocument.Parse(buffer.AsMemory(0, count));
        if (matches(message.RootElement)) return;
      }
    }
    catch (OperationCanceledException) when (!token.IsCancellationRequested)
    {
      throw new InvalidOperationException(step + " was not confirmed by the server");
    }
  }
  private static void Write(string[] args, object result)
  {
    var index = Array.IndexOf(args, "--report");
    File.WriteAllText(index >= 0 && index + 1 < args.Length ? args[index + 1] : Path.Combine(Path.GetTempPath(), "WinPalm-input-test.json"), JsonSerializer.Serialize(result, Preferences.Json));
  }
}
