using System.Text.Json;
using QRCoder;
using WinPalm.Services;
namespace WinPalm.UI;

internal sealed class MainWindow : Form
{
  private readonly HostContext host;
  private readonly uint installerQuitMessage = Win32.RegisterWindowMessageW("RSApplication.WinPalm.QuitForInstall");
  private readonly Panel page = new() { Dock = DockStyle.Fill, AutoScroll = true };
  private Panel? sidebar;
  private Label status = new();
  private FlowLayoutPanel? content;
  private string section = "Connect";
  private PictureBox? qr;
  private Label? pin, address, expires, summary;
  private CheckBox? encrypted;
  private Label? updateStatus;
  private Button? updateCheck, updateInstall, updateCancel;
  private ProgressBar? updateProgress;
  private TextBox? updateNotes;
  private bool quitting;
  private string lastLink = "";
  internal MainWindow(HostContext host)
  {
    this.host = host;
    Text = "WinPalm"; BackColor = Theme.Background; ForeColor = Theme.Text;
    AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.CenterScreen;
    Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Resources", "WinPalm.ico"));
    Controls.Add(page);
    Theme.Scale = DeviceDpi / 96f; ClientSize = Theme.Px(960, 740); BuildChrome();
    Activated += (_, _) => { if (section == "Connect") host.CheckNetwork(); };
    FormClosing += (_, e) => { if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); CoreBridge.TryCall(new { op = "pinShown", shown = false }); host.ExplainTray(); } else if (!quitting) host.Quit(); };
    VisibleChanged += (_, _) => PinVisibility(); Resize += (_, _) => PinVisibility();
    Shown += (_, _) => { var dark = 1; Win32.DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); };
    Navigate("Connect");
  }
  /// The sidebar, at the current DPI.
  private void BuildChrome()
  {
    Font = Theme.Font(10); MinimumSize = Theme.Px(840, 640); page.Padding = Theme.Px(30, 30, 30, 30);
    var previous = sidebar;
    sidebar = new Panel { Dock = DockStyle.Left, Width = Theme.Px(200), BackColor = Theme.Surface, Padding = Theme.Px(20, 28, 16, 20) };
    var navigation = Theme.Stack(165); navigation.Dock = DockStyle.Top;
    navigation.Controls.Add(Theme.Label("WinPalm", 23, bold: true)); navigation.Controls.Add(Theme.Label("Your PC, in your palm.", 9, Theme.Muted));
    var text = previous is null ? "Starting…" : status.Text; var color = previous is null ? Theme.Muted : status.ForeColor;
    status = Theme.Label(text, 10, color); status.MaximumSize = Theme.Px(160, 0); navigation.Controls.Add(status);
    foreach (var name in new[] { "Connect", "General", "Devices", "Deck", "Automations", "Phone setup", "Advanced", "Updates", "About" })
    {
      var button = Theme.Button(name, () => Navigate(name)); button.AutoSize = false; button.Size = Theme.Px(160, 38); button.TextAlign = ContentAlignment.MiddleLeft; button.FlatAppearance.BorderSize = 0;
      navigation.Controls.Add(button);
    }
    sidebar.Controls.Add(navigation);
    SuspendLayout(); if (previous is not null) { Controls.Remove(previous); previous.Dispose(); } Controls.Add(sidebar); ResumeLayout(true);
  }
  /// Pages are built at runtime, so rebuild them for a new DPI rather than
  /// relying on WinForms' load-time autoscaling.
  private void Rescale()
  {
    if (IsDisposed || Math.Abs(DeviceDpi / 96f - Theme.Scale) < 0.01f) return;
    Theme.Scale = DeviceDpi / 96f; BuildChrome(); Navigate(section);
  }
  protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Rescale(); }
  protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); BeginInvoke(Rescale); }
  internal void NetworkChanged() { if (section == "Connect" && !IsDisposed) Navigate(section); }
  protected override void WndProc(ref Message message)
  {
    if (installerQuitMessage != 0 && message.Msg == installerQuitMessage) { host.Quit(); return; }
    if (message.Msg == 0x312 && message.WParam == 1) host.TogglePause();
    base.WndProc(ref message);
  }
  internal void Shutdown() { quitting = true; Close(); }
  private void PinVisibility() => CoreBridge.TryCall(new { op = "pinShown", shown = Visible && WindowState != FormWindowState.Minimized && section == "Connect" });
  private void Navigate(string name)
  {
    section = name; qr?.Image?.Dispose(); qr = null; pin = address = expires = summary = null; encrypted = null; lastLink = "";
    foreach (Control child in page.Controls.Cast<Control>().ToArray()) child.Dispose();
    content = Theme.Stack(); content.Location = new Point(Theme.Px(30), Theme.Px(28)); page.Controls.Add(content);
    content.Controls.Add(Theme.Label(name == "Connect" ? "A familiar remote. Now for your PC." : name, 22, bold: true));
    try
    {
      switch (name)
      {
        case "Connect": Connect(); break;
        case "General": General(); break;
        case "Devices": Devices(); break;
        case "Deck": Deck(); break;
        case "Automations": Automations(); break;
        case "Phone setup": PhoneSetup(); break;
        case "Advanced": Advanced(); break;
        case "Updates": Updates(); break;
        case "About": About(); break;
      }
      UpdateState(host.Snapshot);
    }
    catch (Exception error) { Diagnostics.Record(error); content.Controls.Add(Theme.Label(error.Message, color: Color.Salmon)); }
    PinVisibility();
  }
  private void Add(Control control) => content!.Controls.Add(control);
  private void Hint(string text) => Add(Theme.Label(text, 10, Theme.Muted));
  private void Action(Action work)
  {
    try { work(); } catch (Exception error) { Diagnostics.Record(error); MessageBox.Show(this, error.Message, "WinPalm", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
  }
  private void Save() { host.Preferences.Save(); host.Services.Update(host.Preferences); CoreBridge.Call(new { op = "config", config = host.Services.Config() }); }
  private void Connect()
  {
    Hint("Open your phone's camera and scan. Keep both devices on the same private Wi-Fi.");
    if (host.NetworkProblem is { } problem)
    {
      Add(Theme.Label("⚠ " + problem.Message, 10, Theme.Warning));
      Add(Theme.Button(problem.ButtonText, () => Action(() => DesktopServices.OpenSetting(problem.Target))));
    }
    summary = Theme.Label("", 11); Add(summary);
    qr = new PictureBox { Size = Theme.Px(230, 230), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Padding = Theme.Px(10, 10, 10, 10), Margin = Theme.Px(0, 4, 0, 18), AccessibleName = "Pairing QR code" }; Add(qr);
    address = Theme.Label("", 12, Theme.Accent); Add(address);
    pin = Theme.Label("", 27, bold: true); pin.Font = Theme.Font(27, true, "Consolas"); Add(pin);
    expires = Theme.Label("", 9, Theme.Muted); Add(expires);
    encrypted = Theme.Toggle("Use an encrypted HTTPS connection", false, _ => { lastLink = ""; UpdateState(host.Snapshot); }); Add(encrypted);
    Add(Theme.Row(Theme.Button("Copy address", () => Action(() => Clipboard.SetText(CurrentAddress()))), Theme.Button("New QR & PIN", () => Action(() => CoreBridge.Call(new { op = "renew" }))), Theme.Button("Pause / resume", host.TogglePause)));
    Hint("Trackpad · Keyboard · Custom keyboards · Deck · Apps · Media · Local voice typing");
    Add(Theme.Button("HTTPS & microphone setup", () => Navigate("Phone setup")));
  }
  private string CurrentAddress() => host.Snapshot.ValueKind == JsonValueKind.Object ? host.Snapshot.GetProperty("pairing").GetProperty(encrypted?.Checked == true ? "secureAddress" : "address").GetString()! : "";
  internal void UpdateState(JsonElement state)
  {
    if (state.ValueKind != JsonValueKind.Object) return;
    var running = state.GetProperty("running").GetBoolean(); var paused = state.GetProperty("paused").GetBoolean();
    var count = state.GetProperty("connected").GetArrayLength();
    status.Text = !running ? "● Sharing stopped" : paused ? "● Control paused" : count > 0 ? $"● {count} connected" : "● Ready to pair";
    status.ForeColor = running && !paused ? Theme.Accent : Theme.Muted;
    if (section != "Connect" || qr is null) return;
    var pairing = state.GetProperty("pairing");
    summary!.Text = !running ? "Sharing is stopped. Start it from Advanced." : paused ? "Control is paused. Your devices remain connected." : count > 0 ? $"{count} device(s) connected. You can pair another." : "Ready when you are.";
    var link = pairing.GetProperty("link").GetString()!;
    if (encrypted?.Checked == true) link = pairing.GetProperty("secureAddress").GetString()! + "/" + new Uri(link).Fragment;
    if (lastLink != link)
    {
      using var data = QRCodeGenerator.GenerateQrCode(link, QRCodeGenerator.ECCLevel.M);
      using var code = new PngByteQRCode(data); using var stream = new MemoryStream(code.GetGraphic(8));
      var image = Image.FromStream(stream); qr.Image?.Dispose(); qr.Image = new Bitmap(image); image.Dispose(); lastLink = link;
    }
    address!.Text = CurrentAddress(); pin!.Text = "PIN  " + pairing.GetProperty("pin").GetString();
    expires!.Text = $"Refreshes in {pairing.GetProperty("expiresInSecs").GetUInt32()}s · PIN works while this screen is shown and briefly after.";
  }
  private void General()
  {
    Hint("WinPalm runs quietly in the notification area. Closing this window keeps sharing.");
    Add(Theme.Toggle("Launch WinPalm when I sign in", Preferences.StartsAtLogin, value =>
    {
      Action(() => Preferences.StartsAtLogin = value);
      if (Preferences.StartsAtLogin != value) BeginInvoke(() => Navigate(section));
    }));
    Add(Theme.Toggle("Enable local voice typing", host.Preferences.Dictation, value => Action(() => { host.Preferences.Dictation = value; Save(); })));
    Add(Theme.Toggle("Check GitHub for updates automatically", host.Preferences.CheckForUpdates, value => Action(() => { host.Preferences.CheckForUpdates = value; Save(); if (value) host.Updates.CheckWhenDue(); })));
    Hint(SpeechService.Available ? "An offline Windows speech recognizer is installed. Voice typing uses the PC, with no transcription service." : "No offline speech recognizer is installed. Add a speech language in Windows Settings → Time & language → Speech.");
    Hint(SpeechService.InstalledLanguages);
    if (SpeechService.LastStartupProblem is { } speechProblem) Hint(speechProblem);
    Add(Theme.Button("Windows speech settings", () => DesktopServices.Open("ms-settings:speech")));
    Add(Theme.Button("Check speech setup", async () =>
    {
      var result = await Task.Run(SpeechService.CheckSetup);
      if (!IsDisposed) MessageBox.Show(this, result, "WinPalm speech setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }));
    Add(Theme.Label("Extra app folders", 13, bold: true));
    Hint("Include portable apps or apps on an external drive. Start Menu and Microsoft Store apps are discovered automatically.");
    foreach (var path in host.Preferences.AppFolders.ToArray()) Add(Theme.Row(Theme.Label(path, 9), Theme.Button("Remove", () => Action(() => { host.Preferences.AppFolders.Remove(path); Save(); host.Services.Apps.Invalidate(); Navigate(section); }))));
    Add(Theme.Button("Add folder…", () => Action(() => { using var dialog = new FolderBrowserDialog(); if (dialog.ShowDialog(this) == DialogResult.OK) { if (!host.Preferences.AppFolders.Contains(dialog.SelectedPath)) host.Preferences.AppFolders.Add(dialog.SelectedPath); Save(); host.Services.Apps.Invalidate(); Navigate(section); } })));
    Add(Theme.Label("Emergency pause", 13, bold: true)); Hint("Ctrl + Alt + Pause toggles control globally. The tray menu always offers Pause too.");
    Hint("Windows protects UAC prompts, the lock screen, and elevated applications. Return to a normal desktop app to resume remote input.");
  }
  private void Devices()
  {
    Hint("Removing a device revokes its token and deletes its saved phone settings.");
    Add(Theme.Button("Refresh devices", () => { host.RefreshSoon(); Navigate(section); }));
    if (host.Snapshot.ValueKind != JsonValueKind.Object) return;
    var connected = host.Snapshot.GetProperty("connected").EnumerateArray().ToArray();
    var devices = host.Snapshot.GetProperty("trusted").EnumerateArray().ToArray();
    if (devices.Length + connected.Length == 0) Hint("No paired devices yet. Scan the QR code on Connect.");
    foreach (var device in devices)
    {
      var id = device.GetProperty("id").GetString()!; var name = device.GetProperty("name").GetString()!;
      Add(Theme.Label(name, 13, bold: true));
      var live = connected.Where(d => d.GetProperty("deviceId").GetString() == id).ToArray();
      Hint($"{device.GetProperty("model").GetString()} · Last used {DateTimeOffset.FromUnixTimeSeconds(device.GetProperty("lastSeen").GetInt64()).ToLocalTime():g}" + (live.Length > 0 ? " · Connected" : ""));
      Add(Theme.Row(Theme.Button("Rename", () => { var next = Prompt("Device name", name); if (next is not null) Action(() => { CoreBridge.Call(new { op = "rename", deviceId = id, name = next }); host.RefreshSoon(); Navigate(section); }); }), Theme.Button("Remove", () => { if (Confirm($"Remove {name}? It will need to pair again.")) Action(() => { CoreBridge.Call(new { op = "forget", deviceId = id }); host.RefreshSoon(); Navigate(section); }); })));
    }
    foreach (var device in connected)
    {
      var id = device.GetProperty("session").GetUInt64();
      Add(Theme.Row(Theme.Label($"{device.GetProperty("name").GetString()} · {(device.GetProperty("secure").GetBoolean() ? "HTTPS" : "HTTP")}", 10), Theme.Button("Disconnect", () => Action(() => { CoreBridge.Call(new { op = "disconnect", id }); host.RefreshSoon(); }))));
    }
    if (devices.Length > 0) Add(Theme.Button("Remove all paired devices…", () => { if (Confirm("Remove every paired device and its saved phone settings?")) Action(() => { CoreBridge.Call(new { op = "forgetAll" }); host.RefreshSoon(); Navigate(section); }); }));
  }
  private void Deck()
  {
    Hint("Your button pages are shared by every paired phone. Edit them directly on the phone.");
    var deck = CoreBridge.Call(new { op = "deck" }); var pages = deck.GetProperty("pages").EnumerateArray().ToArray();
    Add(Theme.Label($"{pages.Length} pages · {pages.Sum(p => p.GetProperty("tiles").GetArrayLength())} buttons", 14, bold: true));
    foreach (var item in pages) Hint($"{item.GetProperty("name").GetString()} — {item.GetProperty("tiles").GetArrayLength()} buttons");
    Add(Theme.Row(Theme.Button("Export deck…", () => Action(() => { using var dialog = new SaveFileDialog { Filter = "WinPalm deck (*.json)|*.json", FileName = "WinPalm-deck.json" }; if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dialog.FileName, CoreBridge.Call(new { op = "deck" }).GetRawText()); })), Theme.Button("Import deck…", () => Action(() => { using var dialog = new OpenFileDialog { Filter = "WinPalm deck (*.json)|*.json" }; if (dialog.ShowDialog(this) != DialogResult.OK) return; if (new FileInfo(dialog.FileName).Length > 1_048_576) throw new InvalidOperationException("That deck file is too large."); using var document = JsonDocument.Parse(File.ReadAllText(dialog.FileName)); if (Confirm("Replace the shared deck with this file?")) { CoreBridge.Call(new { op = "setDeck", deck = document.RootElement }); Navigate(section); } }))));
    Add(Theme.Button("Reset to Windows starter deck…", () => { if (Confirm("Replace every shared deck page with the starter deck?")) Action(() => { CoreBridge.Call(new { op = "resetDeck" }); Navigate(section); }); }));
  }
  private void Automations()
  {
    Hint("Create named PowerShell commands here, then choose Run automation on a phone button.");
    Add(Theme.Toggle("Allow PowerShell commands and automations", host.Preferences.AllowShell, value =>
    {
      if (value && !Confirm("Allow paired devices to run PowerShell with your Windows user permissions? Only pair devices you trust. Commands can read, change, and delete your files.")) { Navigate(section); return; }
      Action(() => { host.Preferences.AllowShell = value; Save(); });
    }));
    Hint("Commands run without a profile. Pause or disconnect ends a running command and everything it started, and each run has a five-minute limit. Apps a finished command opened, for example with Start-Process, stay open. A button whose command fails shows the error on the phone.");
    foreach (var pair in host.Preferences.Automations.ToArray()) Add(Theme.Row(Theme.Label(pair.Key, 11), Theme.Button("Edit", () => EditAutomation(pair.Key)), Theme.Button("Remove", () => Action(() => { host.Preferences.Automations.Remove(pair.Key); Save(); Navigate(section); }))));
    Add(Theme.Button("Add automation…", () => EditAutomation(null)));
  }
  private void EditAutomation(string? existing)
  {
    var name = Prompt("Automation name", existing ?? ""); if (string.IsNullOrWhiteSpace(name)) return;
    var command = Prompt("PowerShell command", existing is not null ? host.Preferences.Automations[existing] : "", true); if (string.IsNullOrWhiteSpace(command)) return;
    if (name.Length > 100 || command.Length > 4096) { MessageBox.Show(this, "Use a name under 100 characters and a command under 4096 characters.", "WinPalm"); return; }
    Action(() => { if (existing is not null) host.Preferences.Automations.Remove(existing); host.Preferences.Automations[name.Trim()] = command; Save(); Navigate(section); });
  }
  private void PhoneSetup()
  {
    Hint("Scan the QR code, enter the PIN, or request approval from your phone.");
    Add(Theme.Toggle("Allow PIN pairing", host.Preferences.AllowPin, value => Action(() => { host.Preferences.AllowPin = value; Save(); })));
    Add(Theme.Toggle("Allow pairing approval requests", host.Preferences.AllowApproval, value => Action(() => { host.Preferences.AllowApproval = value; Save(); })));
    Add(Theme.Label("Encrypted connection & voice typing", 13, bold: true));
    Hint("1. Export the local CA certificate below. Transfer it privately to your phone.\n2. Install and trust that certificate on the phone. On iPhone also enable full trust in Settings → General → About → Certificate Trust Settings.\n3. Enable HTTPS on Connect, then scan its QR code. Your browser needs a trusted secure connection for the microphone.");
    Add(Theme.Button("Export local certificate…", () => Action(() => { using var dialog = new SaveFileDialog { FileName = "WinPalm.crt", Filter = "Certificate (*.crt)|*.crt" }; if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllBytes(dialog.FileName, Convert.FromBase64String(CoreBridge.Call(new { op = "certificate" }).GetString()!)); })));
    if (host.Snapshot.ValueKind == JsonValueKind.Object) { Hint("SHA-256 fingerprint:"); Add(Theme.Label(host.Snapshot.GetProperty("pairing").GetProperty("caFingerprint").GetString() ?? "", 9, Theme.Accent)); }
    Hint("Android browsers vary in whether they trust user-installed certificates. If the microphone remains unavailable, check browser trust before enabling dictation.");
    Add(Theme.Button("Open Windows Firewall settings", () => DesktopServices.Open("ms-settings:network-advancedsettings")));
    Hint("Allow WinPalm only on Private networks when Windows asks. The default TCP port is 8790. Guest Wi-Fi or client isolation may block phone-to-PC connections.");
  }
  private void Advanced()
  {
    Hint("Server and network settings apply to this PC.");
    Add(Theme.Label("Port", 11)); var port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = Math.Clamp(host.Preferences.Port, 1024, 65535), Width = Theme.Px(180), Font = Theme.Font(10), BackColor = Theme.Surface, ForeColor = Theme.Text }; Add(port);
    Add(Theme.Label("QR address", 11)); var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Theme.Px(350), Font = Theme.Font(10) }; mode.Items.AddRange(["LAN IP (recommended)", "Computer name (.local)", "Custom address / VPN"]); mode.SelectedIndex = host.Preferences.AddressMode switch { "hostName" => 1, "custom" => 2, _ => 0 }; Add(mode);
    var custom = Theme.Input(host.Preferences.CustomAddress); custom.PlaceholderText = "192.168.1.10 or https://pc.example.com"; Add(custom);
    var proxy = Theme.Toggle("Trust forwarding headers from a local reverse proxy", host.Preferences.AllowLocalProxy, _ => { }); Add(proxy);
    Add(Theme.Button("Save & restart server", () => Action(() =>
    {
      if (mode.SelectedIndex == 2 && (!Uri.TryCreate(custom.Text.Contains("://") ? custom.Text : "http://" + custom.Text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo))) throw new InvalidOperationException("Enter a host, IP address, or HTTP(S) address without a path or credentials.");
      host.Preferences.Port = (int)port.Value; host.Preferences.AddressMode = mode.SelectedIndex switch { 1 => "hostName", 2 => "custom", _ => "ipAddress" }; host.Preferences.CustomAddress = custom.Text.Trim(); host.Preferences.AllowLocalProxy = proxy.Checked; Save(); host.Restart();
    }), true));
    Add(Theme.Row(Theme.Button("Start sharing", host.Start), Theme.Button("Stop sharing", host.Stop), Theme.Button("Open data folder", () => DesktopServices.Open(Preferences.DataDirectory)), Theme.Button("Open log", () => Action(() => { if (!File.Exists(Diagnostics.FilePath)) File.WriteAllText(Diagnostics.FilePath, "WinPalm diagnostics\n"); DesktopServices.Open(Diagnostics.FilePath); }))));
    Hint("Logs contain error types and codes. Input, speech, clipboard contents, and pairing secrets stay out of the log.");
    if (host.Snapshot.ValueKind == JsonValueKind.Object) foreach (var ip in host.Snapshot.GetProperty("pairing").GetProperty("addresses").EnumerateArray()) Hint(ip.GetString()!);
  }
  private void About()
  {
    Add(Theme.Label("Your PC, in your palm.", 15, Theme.Accent));
    Hint($"WinPalm {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\nNative Windows host · Rust input engine · No phone app required.");
    Hint("Built by RS Application. Based on CmdPalm, licensed under AGPL-3.0-only. No analytics, account, or cloud service. Voice recognition uses installed Windows speech languages.");
    Hint("This port is in preview. See the included README for tested status, hardware requirements, and Windows validation steps.");
    Add(Theme.Button("Open license", () => DesktopServices.Open(Path.Combine(AppContext.BaseDirectory, "LICENSE"))));
    Add(Theme.Button("Check for updates…", async () => { ShowUpdates(); await host.Updates.CheckAsync(); }));
  }
  internal void ShowUpdates() => Navigate("Updates");
  private void Updates()
  {
    Hint($"Installed: WinPalm {UpdateController.CurrentVersion.ToString(3)} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    Add(Theme.Toggle("Check GitHub for updates automatically", host.Preferences.CheckForUpdates, value => Action(() => { host.Preferences.CheckForUpdates = value; Save(); if (value) host.Updates.CheckWhenDue(); })));
    Hint("Checks once a day and lets you know in the notification area. Downloads start when you choose Download and restart.");
    updateStatus = Theme.Label("", 11); Add(updateStatus);
    updateProgress = new ProgressBar { Minimum = 0, Maximum = 100, Size = Theme.Px(510, 12), Margin = Theme.Px(0, 8, 0, 12) }; Add(updateProgress);
    updateCheck = Theme.Button("Check now", async () => await host.Updates.CheckAsync());
    updateInstall = Theme.Button("Download and restart", async () => await host.Updates.InstallAsync(), true);
    updateCancel = Theme.Button("Cancel download", host.Updates.Cancel);
    Add(Theme.Row(updateCheck, updateInstall, updateCancel));
    Add(Theme.Button("Open GitHub releases", () => Action(() => DesktopServices.Open(host.Updates.Available?.Page.AbsoluteUri ?? ReleaseUpdates.ReleasesUrl))));
    Hint("The package is verified before replacing this copy. WinPalm briefly disconnects phones during restart. Your paired devices, settings and Deck are kept.");
    Add(Theme.Label("Release notes", 13, bold: true));
    updateNotes = Theme.Input("", true); updateNotes.ReadOnly = true; updateNotes.Size = Theme.Px(510, 200); updateNotes.ScrollBars = ScrollBars.Vertical; Add(updateNotes);
    UpdateUpdates();
  }
  internal void UpdateUpdates()
  {
    if (section != "Updates" || IsDisposed || updateStatus is null) return;
    var updates = host.Updates;
    updateStatus.Text = updates.Message;
    updateCheck!.Enabled = !updates.Busy;
    updateInstall!.Visible = updates.Available is { Package: not null, Checksum: not null };
    updateInstall.Enabled = !updates.Busy;
    updateCancel!.Visible = updates.Installing;
    updateProgress!.Visible = updates.Installing;
    updateProgress.Value = Math.Clamp(updates.Progress, 0, 100);
    var notes = updates.Available is { } release
        ? (release.Package is null || release.Checksum is null ? "This release does not include an in-app update for this processor. Open GitHub releases for its downloads.\r\n\r\n" : "") + release.Notes.Replace("\n", "\r\n")
        : "Release notes will appear here when an update is available.";
    if (updateNotes!.Text != notes) updateNotes.Text = notes;
  }
  private bool Confirm(string text) => MessageBox.Show(this, text, "WinPalm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
  private string? Prompt(string title, string initial, bool multiline = false)
  {
    using var dialog = new Form { Text = title, AutoScaleMode = AutoScaleMode.None, ClientSize = Theme.Px(600, multiline ? 260 : 140), BackColor = Theme.Background, ForeColor = Theme.Text, Font = Font, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
    var input = Theme.Input(initial, multiline); input.Location = new Point(Theme.Px(20), Theme.Px(20)); input.Height = Theme.Px(multiline ? 160 : 30); dialog.Controls.Add(input);
    var okay = Theme.Button("Save", () => dialog.DialogResult = DialogResult.OK, true); okay.Location = new Point(Theme.Px(360), Theme.Px(multiline ? 205 : 80)); dialog.Controls.Add(okay);
    var cancel = Theme.Button("Cancel", () => dialog.DialogResult = DialogResult.Cancel); cancel.Location = new Point(Theme.Px(480), Theme.Px(multiline ? 205 : 80)); dialog.Controls.Add(cancel);
    dialog.AcceptButton = multiline ? null : okay; dialog.CancelButton = cancel; return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
  }
}
