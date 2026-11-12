using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WinPalm.Services;
using WinPalm.UI;
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
      VerifyDesktopUi(); checks.Add("desktop UI layout, stepper and badge wrapping");
      VerifyPageScrolling(); checks.Add("desktop page scrolling after navigation, resize and tray reopen");
      if (desktop.Apps.List().Length < 6 || desktop.Apps.Action("not-a-catalog-id", "open")) throw new InvalidOperationException("App catalog validation failed");
      checks.Add("Windows app catalog and identifier validation");
      var nativeIcons = 0;
      foreach (var app in new[] { "explorer", "browser", "terminal", "notepad", "calculator", "settings" })
      {
        var png = desktop.Apps.Icon("builtin:" + app, 64);
        if (png is null) continue; // The phone has a specific fallback for these.
        using var stream = new MemoryStream(Convert.FromBase64String(png));
        using var image = Image.FromStream(stream);
        if (image.Width != 64 || image.Height != 64) throw new InvalidOperationException("Unexpected app icon dimensions");
        nativeIcons++;
      }
      if (nativeIcons == 0) throw new InvalidOperationException("No native app icons could be extracted");
      checks.Add($"native app icons decoded: {nativeIcons}/6 built-ins (others use phone fallbacks)");
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

  private static void VerifyDesktopUi()
  {
    using var hostForm = new Form { AutoScaleMode = AutoScaleMode.None, ClientSize = new Size(1040, 740) };
    using var page = new PageScrollView();
    using var sidebar = new SidebarChrome("Connect", _ => { }, () => { }, "v0.2.0");
    hostForm.Controls.Add(page);
    hostForm.Controls.Add(sidebar);
    page.BringToFront();
    hostForm.PerformLayout();

    var pauseBtn = new PalmButton("Pause control", () => { }, ButtonTone.Secondary, IconKind.Pause, compact: true);
    var renewBtn = new PalmButton("New QR & PIN", () => { }, ButtonTone.Secondary, IconKind.Refresh, compact: true);
    var header = new PageHeader("Your PC, in your palm.", "Scan on the same private Wi-Fi.", pauseBtn, renewBtn);
    header.SetBadge("Ready to pair", BadgeTone.Accent);

    var hero = new PairingHeroCard(false, _ => { }, () => { }, () => { }, () => { });
    hero.UpdatePairing(true, true, "http://127.0.0.1:8790/#pair=selftest", "http://127.0.0.1:8790", "123456", 55);

    var stepper = new NumericStepper(8790, 1024, 65535);
    if (stepper.Value != 8790) throw new InvalidOperationException("NumericStepper initial value mismatch");

    var card = new SurfaceCard("Network & Binding", "Verify unshown layout and row measurement.");
    card.AddRow(new SettingActionRow("Preferred TCP port", "Default is 8790.", IconKind.Advanced, actions: stepper), dividerAbove: false);
    var hiddenProgress = new StyledProgressBar();
    hiddenProgress.SetShown(false);
    card.AddRow(hiddenProgress, dividerAbove: false);

    page.AddBlock(header);
    page.AddBlock(hero);
    page.AddBlock(card);
    page.AddBlock(new CapabilitiesGrid(), 0);
    page.Relayout();

    if (header.Height <= 0 || hero.Top < header.Bottom || card.Top < hero.Bottom || pauseBtn.Left <= 0)
      throw new InvalidOperationException("Unshown PageScrollView failed to lay out blocks or header actions");

    var longName = "Personal Windows Workstation Studio Phone Pro Max Edition";
    using var rowWithoutBadge = new SettingActionRow(longName, "Last used today", IconKind.Devices, BadgeTone.Good);
    using var rowWithBadge = new SettingActionRow(longName, "Last used today", IconKind.Devices, BadgeTone.Good, "Connected · HTTPS", BadgeTone.Good);
    if (rowWithBadge.GetHeightForWidth(Theme.Px(340)) <= rowWithoutBadge.GetHeightForWidth(Theme.Px(340)))
      throw new InvalidOperationException("SettingActionRow did not reserve width for status pill");
  }

  private static void VerifyPageScrolling()
  {
    using var hostForm = new Form
    {
      AutoScaleMode = AutoScaleMode.None,
      ClientSize = Theme.Px(800, 480),
      StartPosition = FormStartPosition.Manual,
      Location = new Point(-32000, -32000),
      ShowInTaskbar = false
    };
    using var page = new PageScrollView();
    hostForm.Controls.Add(page);
    hostForm.Show();

    Control LoadPage(int height)
    {
      page.ClearBlocks();
      var content = new ScrollTestBlock(height);
      page.AddBlock(content, 0);
      page.Relayout();
      if (page.AutoScrollPosition != Point.Empty || content.Top != Theme.Px(26))
        throw new InvalidOperationException("Desktop page navigation did not reset the scroll position");
      return content;
    }

    void VerifyScrollable(Control content)
    {
      if (!page.VerticalScroll.Visible || page.HorizontalScroll.Visible)
        throw new InvalidOperationException("Long desktop page is missing its vertical scrollbar");
      var expectedWidth = Math.Clamp(page.ClientSize.Width - Theme.Px(68), Theme.Px(460), Theme.Px(748));
      if (content.Width != expectedWidth)
        throw new InvalidOperationException("Desktop page did not account for the native scrollbar width");

      var originalTop = content.Top;
      // Exercise the real desktop wheel message, independently of phone input.
      Win32.SendMessageW(page.Handle, 0x020A, -120 << 16, 0); // WM_MOUSEWHEEL
      var scroll = page.AutoScrollPosition;
      if (scroll.Y >= 0 || content.Top >= originalTop)
        throw new InvalidOperationException("Mouse wheel did not scroll the desktop page");
      page.Relayout();
      if (page.AutoScrollPosition != scroll)
        throw new InvalidOperationException("Desktop page refresh lost the scroll position");

      Win32.SendMessageW(page.Handle, 0x0115, 7, 0); // WM_VSCROLL, SB_BOTTOM
      if (content.Bottom > page.ClientSize.Height - Theme.Px(34))
        throw new InvalidOperationException("Desktop page scrollbar cannot reach the final content");
    }

    // Rebuild the shared container without resizing the window, including two
    // pages with identical heights and transitions through non-scrolling pages.
    foreach (var height in new[] { 1100, 1100, 120, 1400, 120, 1100 })
    {
      var content = LoadPage(height);
      if (height > 480) VerifyScrollable(content);
      else if (page.VerticalScroll.Visible || page.HorizontalScroll.Visible)
        throw new InvalidOperationException("Short desktop page retained a stale scrollbar");
    }

    // Stay within the runner's desktop bounds. WinForms clamps top-level
    // windows to MaxWindowTrackSize, so an oversized form cannot prove this.
    var resizedContent = LoadPage(300);
    hostForm.ClientSize = Theme.Px(800, 240);
    VerifyScrollable(resizedContent);
    hostForm.ClientSize = Theme.Px(800, 480);
    if (page.VerticalScroll.Visible || page.AutoScrollPosition != Point.Empty)
      throw new InvalidOperationException($"Desktop page scrollbar did not clear after enlarging the window: viewport={page.ClientSize}, content={page.AutoScrollMinSize}, offset={page.AutoScrollPosition}");
    hostForm.ClientSize = Theme.Px(800, 240);
    VerifyScrollable(resizedContent);
    hostForm.ClientSize = Theme.Px(800, 480);

    hostForm.Hide();
    _ = LoadPage(120);
    var reopenedContent = LoadPage(1100);
    hostForm.Show();
    page.Relayout();
    VerifyScrollable(reopenedContent);
  }

  // Like the app's cards, start without bounds and measure during Relayout.
  // A pre-sized panel would trigger WinForms scrolling as soon as it is added,
  // masking the missing layout pass that broke page navigation.
  private sealed class ScrollTestBlock(int height) : Control, ISizableBlock
  {
    public int GetHeightForWidth(int width) => Theme.Px(height);
  }
}
