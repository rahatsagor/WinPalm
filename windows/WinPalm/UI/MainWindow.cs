using System.Runtime.InteropServices;
using System.Text.Json;
using WinPalm.Services;

namespace WinPalm.UI;

internal sealed class MainWindow : Form
{
  private const string WebsiteUrl = "https://winpalm.rsapplication.com";
  private const string DocsUrl = "https://winpalm.rsapplication.com/docs/";
  private const string RepoUrl = "https://github.com/rahatsagor/WinPalm";

  private readonly HostContext host;
  private readonly uint installerQuitMessage = Win32.RegisterWindowMessageW("RSApplication.WinPalm.QuitForInstall");
  private readonly PageScrollView page = new();
  private readonly System.Windows.Forms.Timer pinTimer = new() { Interval = 1000 };

  private SidebarChrome? sidebar;
  private string section = "Connect";
  private bool connectHttps;
  private bool quitting;
  private bool modalOpen;
  private string lastDevicesDigest = "";

  // Connect live references
  private PageHeader? connectHeader;
  private PalmButton? connectPauseBtn;
  private PairingHeroCard? pairingCard;

  // Advanced live references
  private SettingActionRow? serverStatusRow;
  private PalmButton? startBtn, stopBtn;

  // Updates live references
  private SettingActionRow? updateStatusRow;
  private PalmButton? updateCheck, updateInstall, updateCancel;
  private StyledProgressBar? updateProgress;
  private StyledTextBox? updateNotes;

  internal MainWindow(HostContext host)
  {
    this.host = host;
    Text = "WinPalm";
    BackColor = Theme.Canvas;
    ForeColor = Theme.Text;
    AutoScaleMode = AutoScaleMode.None;
    StartPosition = FormStartPosition.CenterScreen;
    var iconFile = Path.Combine(AppContext.BaseDirectory, "Resources", "WinPalm.ico");
    if (File.Exists(iconFile)) Icon = new Icon(iconFile);
    else if (Environment.ProcessPath is { } exe) Icon = Icon.ExtractAssociatedIcon(exe);
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

    Controls.Add(page);
    Theme.Scale = DeviceDpi / 96f;
    ClientSize = Theme.Px(1040, 740);
    BuildChrome();

    pinTimer.Tick += (_, _) =>
    {
      if (section == "Connect" && pairingCard is not null && Visible && WindowState != FormWindowState.Minimized)
        pairingCard.TickCountdown();
    };

    Activated += (_, _) => { if (section == "Connect") host.CheckNetwork(); };
    FormClosing += (_, e) =>
    {
      if (!quitting && e.CloseReason == CloseReason.UserClosing)
      {
        e.Cancel = true;
        Hide();
        PinVisibility();
        host.ExplainTray();
      }
      else if (!quitting)
      {
        host.Quit();
      }
    };
    VisibleChanged += (_, _) =>
    {
      if (Visible && WindowState != FormWindowState.Minimized) page.Relayout();
      PinVisibility();
    };
    Resize += (_, _) => PinVisibility();
    Shown += (_, _) =>
    {
      Theme.ApplyWindowChrome(this, Theme.Sidebar, roundCorners: true);
      page.Relayout();
    };

    Navigate("Connect");
  }

  private string VersionTag => $"v{UpdateController.CurrentVersion.ToString(3)} · {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

  private void BuildChrome()
  {
    Font = Theme.Font(9.75f);
    MinimumSize = Theme.Px(880, 620);
    var previous = sidebar;
    sidebar = new SidebarChrome(
      section,
      Navigate,
      () =>
      {
        var running = host.Snapshot.ValueKind == JsonValueKind.Object && host.Snapshot.GetProperty("running").GetBoolean();
        if (!running) Action(host.Start);
        else host.TogglePause();
      },
      VersionTag);

    SuspendLayout();
    if (previous is not null)
    {
      Controls.Remove(previous);
      previous.Dispose();
    }
    Controls.Add(sidebar);
    page.BringToFront();
    ResumeLayout(true);
    RefreshSidebarState();
  }

  private void Rescale()
  {
    if (IsDisposed || Math.Abs(DeviceDpi / 96f - Theme.Scale) < 0.01f) return;
    Theme.Scale = DeviceDpi / 96f;
    BuildChrome();
    Navigate(section);
  }

  protected override void OnHandleCreated(EventArgs e)
  {
    base.OnHandleCreated(e);
    Theme.ApplyWindowChrome(this, Theme.Sidebar, roundCorners: true);
    Rescale();
    page.Relayout();
  }

  protected override void OnDpiChanged(DpiChangedEventArgs e)
  {
    base.OnDpiChanged(e);
    BeginInvoke(Rescale);
  }

  internal void NetworkChanged()
  {
    if (section == "Connect" && !IsDisposed) Navigate("Connect");
  }

  protected override void WndProc(ref Message message)
  {
    if (installerQuitMessage != 0 && message.Msg == installerQuitMessage) { host.Quit(); return; }
    if (message.Msg == 0x312 && message.WParam == 1) host.TogglePause();
    base.WndProc(ref message);
  }

  internal void Shutdown()
  {
    quitting = true;
    pinTimer.Stop();
    pinTimer.Dispose();
    Close();
  }

  private void PinVisibility()
  {
    var shown = Visible && WindowState != FormWindowState.Minimized && section == "Connect";
    pinTimer.Enabled = shown;
    CoreBridge.TryCall(new { op = "pinShown", shown });
  }

  private void Navigate(string name)
  {
    section = name;
    sidebar?.SetActiveSection(name);

    connectHeader = null;
    connectPauseBtn = null;
    pairingCard = null;
    serverStatusRow = null;
    startBtn = stopBtn = null;
    updateStatusRow = null;
    updateCheck = updateInstall = updateCancel = null;
    updateProgress = null;
    updateNotes = null;

    page.ClearBlocks();
    try
    {
      switch (name)
      {
        case "Connect": BuildConnect(); break;
        case "General": BuildGeneral(); break;
        case "Devices": BuildDevices(); break;
        case "Deck": BuildDeck(); break;
        case "Automations": BuildAutomations(); break;
        case "Phone setup": BuildPhoneSetup(); break;
        case "Advanced": BuildAdvanced(); break;
        case "Updates": BuildUpdates(); break;
        case "About": BuildAbout(); break;
      }
      page.Relayout();
      UpdateState(host.Snapshot);
    }
    catch (Exception error)
    {
      Diagnostics.Record(error);
      page.AddBlock(new BannerCard(IconKind.Warning, BadgeTone.Danger, "Couldn't load this page", error.Message));
      page.Relayout();
    }
    PinVisibility();
  }

  private void Action(Action work)
  {
    try { work(); }
    catch (Exception error)
    {
      Diagnostics.Record(error);
      ShowModal(() => PalmDialog.Info(this, "WinPalm", error.Message, BadgeTone.Warning));
    }
  }

  private T ShowModal<T>(Func<T> dialogCall)
  {
    modalOpen = true;
    try { return dialogCall(); }
    finally { modalOpen = false; }
  }

  private void ShowModal(Action dialogCall)
  {
    modalOpen = true;
    try { dialogCall(); }
    finally { modalOpen = false; }
  }

  private void Save()
  {
    host.Preferences.Save();
    host.Services.Update(host.Preferences);
    CoreBridge.Call(new { op = "config", config = host.Services.Config() });
  }

  private void BuildConnect()
  {
    connectPauseBtn = new PalmButton(
      "Pause control",
      () =>
      {
        var running = host.Snapshot.ValueKind == JsonValueKind.Object && host.Snapshot.GetProperty("running").GetBoolean();
        if (!running) Action(host.Start);
        else host.TogglePause();
      },
      ButtonTone.Secondary,
      IconKind.Pause,
      compact: true)
    {
      BackColor = Theme.Canvas
    };

    var renewBtn = new PalmButton(
      "New QR & PIN",
      () => Action(() => { CoreBridge.Call(new { op = "renew" }); host.RefreshSoon(); }),
      ButtonTone.Secondary,
      IconKind.Refresh,
      compact: true)
    {
      BackColor = Theme.Canvas
    };

    connectHeader = new PageHeader(
      "Your PC, in your palm.",
      "Open your phone's camera and scan on the same private Wi-Fi. No phone app or account required.",
      connectPauseBtn,
      renewBtn);
    connectHeader.SetBadge("Ready to pair", BadgeTone.Accent);
    page.AddBlock(connectHeader, 16);

    if (host.NetworkProblem is { } problem)
    {
      var fixBtn = new PalmButton(
        problem.ButtonText,
        () => Action(() => DesktopServices.OpenSetting(problem.Target)),
        ButtonTone.Primary,
        IconKind.External,
        compact: true);
      page.AddBlock(new BannerCard(IconKind.Warning, BadgeTone.Warning, "Network profile or firewall attention needed", problem.Message, fixBtn), 16);
    }

    pairingCard = new PairingHeroCard(
      connectHttps,
      useHttps =>
      {
        connectHttps = useHttps;
        page.Relayout();
        UpdateState(host.Snapshot);
      },
      () => Action(() =>
      {
        var addr = CurrentAddress();
        if (!string.IsNullOrEmpty(addr)) Clipboard.SetText(addr);
      }),
      () => Navigate("Phone setup"),
      () => Action(host.Start));
    page.AddBlock(pairingCard, 16);
    page.AddBlock(new CapabilitiesGrid(), 0);
  }

  private string CurrentAddress()
  {
    if (host.Snapshot.ValueKind != JsonValueKind.Object) return "";
    var pairing = host.Snapshot.GetProperty("pairing");
    return pairing.GetProperty(connectHttps ? "secureAddress" : "address").GetString() ?? "";
  }

  private void RefreshSidebarState()
  {
    if (sidebar is null) return;
    if (host.Snapshot.ValueKind != JsonValueKind.Object)
    {
      sidebar.UpdateStatus(true, false, 0, host.Preferences.Port, host.Updates.Available is not null);
      return;
    }
    var running = host.Snapshot.GetProperty("running").GetBoolean();
    var paused = host.Snapshot.GetProperty("paused").GetBoolean();
    var count = host.Snapshot.GetProperty("connected").GetArrayLength();
    var port = host.Snapshot.GetProperty("pairing").GetProperty("port").GetInt32();
    sidebar.UpdateStatus(running, paused, count, port, host.Updates.Available is not null);
  }

  internal void UpdateState(JsonElement state)
  {
    if (state.ValueKind != JsonValueKind.Object) return;
    var running = state.GetProperty("running").GetBoolean();
    var paused = state.GetProperty("paused").GetBoolean();
    var connected = state.GetProperty("connected");
    var count = connected.GetArrayLength();
    var pairing = state.GetProperty("pairing");
    var port = pairing.GetProperty("port").GetInt32();

    sidebar?.UpdateStatus(running, paused, count, port, host.Updates.Available is not null);

    if (section == "Connect" && pairingCard is not null && connectHeader is not null && connectPauseBtn is not null)
    {
      var (badgeText, badgeTone) = !running
        ? ("Sharing stopped · Start server below", BadgeTone.Danger)
        : paused
          ? ("Control paused · Connected phones stay paired", BadgeTone.Warning)
          : count > 0
            ? ($"{count} {(count == 1 ? "device" : "devices")} connected · Ready to pair another", BadgeTone.Good)
            : ($"Listening on port {port} · Ready when you are", BadgeTone.Accent);
      var prevHeaderH = connectHeader.Height;
      connectHeader.SetBadge(badgeText, badgeTone);

      connectPauseBtn.LabelText = !running ? "Start sharing" : paused ? "Resume control" : "Pause control";
      connectPauseBtn.Icon = !running || paused ? IconKind.Play : IconKind.Pause;
      connectPauseBtn.Tone = !running || paused ? ButtonTone.Primary : ButtonTone.Secondary;
      if (connectHeader.Width > 0 && connectHeader.GetHeightForWidth(connectHeader.Width) != prevHeaderH)
        page.Relayout();
      else
        connectHeader.Invalidate();

      var link = pairing.GetProperty("link").GetString() ?? "";
      if (connectHttps && !string.IsNullOrEmpty(link))
        link = pairing.GetProperty("secureAddress").GetString()! + "/" + new Uri(link).Fragment;

      pairingCard.UpdatePairing(
        running,
        host.Preferences.AllowPin,
        link,
        CurrentAddress(),
        pairing.GetProperty("pin").GetString() ?? "------",
        pairing.GetProperty("expiresInSecs").GetUInt32());
    }
    else if (section == "Devices" && !modalOpen)
    {
      var digest = connected.GetRawText() + "|" + state.GetProperty("trusted").GetRawText();
      if (digest != lastDevicesDigest)
      {
        lastDevicesDigest = digest;
        Navigate("Devices");
      }
    }
    else if (section == "Advanced" && serverStatusRow is not null)
    {
      UpdateAdvancedServerRow(running, paused, port);
    }
  }

  private void BuildGeneral()
  {
    page.AddBlock(new PageHeader("General", "Startup behavior, offline voice typing, app discovery, and Windows safety protections."));

    var startupCard = new SurfaceCard("Startup & Background", "WinPalm lives quietly in the notification area by the clock. Closing this window keeps phones connected.");
    SettingToggleRow? loginToggle = null;
    loginToggle = new SettingToggleRow(
      "Launch WinPalm when I sign in",
      "Starts minimized in the notification area whenever you log in to Windows.",
      Preferences.StartsAtLogin,
      value =>
      {
        Action(() => Preferences.StartsAtLogin = value);
        if (Preferences.StartsAtLogin != value) loginToggle?.SetChecked(Preferences.StartsAtLogin);
      },
      IconKind.Play,
      BadgeTone.Accent);
    startupCard.AddRow(loginToggle, dividerAbove: false);
    startupCard.AddRow(new SettingToggleRow(
      "Check GitHub for updates automatically",
      "Checks once a day in the background and notifies you before downloading anything.",
      host.Preferences.CheckForUpdates,
      value => Action(() => { host.Preferences.CheckForUpdates = value; Save(); if (value) host.Updates.CheckWhenDue(); }),
      IconKind.Updates,
      BadgeTone.Neutral));
    page.AddBlock(startupCard);

    var speechCard = new SurfaceCard("Local Voice Typing", "Audio from your phone's microphone streams over HTTPS and is transcribed offline on this PC.");
    speechCard.AddRow(new SettingToggleRow(
      "Enable local voice typing",
      "Uses installed Windows speech recognition languages with no external transcription service.",
      host.Preferences.Dictation,
      value => Action(() => { host.Preferences.Dictation = value; Save(); }),
      IconKind.Mic,
      BadgeTone.Accent), dividerAbove: false);

    var speechReady = SpeechService.Available;
    var speechDetail = SpeechService.InstalledLanguages;
    if (!speechReady)
      speechDetail = "Add an offline speech language in Windows Settings -> Time & language -> Speech. " + speechDetail;
    if (SpeechService.LastStartupProblem is { } problem)
      speechDetail += "\nLast error: " + problem;

    PalmButton? checkSpeechBtn = null;
    checkSpeechBtn = new PalmButton("Check setup", async () =>
    {
      if (checkSpeechBtn is not null) checkSpeechBtn.Enabled = false;
      try
      {
        var result = await Task.Run(SpeechService.CheckSetup);
        if (!IsDisposed) ShowModal(() => PalmDialog.Info(this, "WinPalm speech setup", result, SpeechService.Available ? BadgeTone.Good : BadgeTone.Warning));
      }
      catch (Exception error)
      {
        Diagnostics.Record(error);
        if (!IsDisposed) ShowModal(() => PalmDialog.Info(this, "WinPalm speech setup", error.Message, BadgeTone.Warning));
      }
      finally
      {
        if (checkSpeechBtn is { IsDisposed: false }) checkSpeechBtn.Enabled = true;
      }
    }, ButtonTone.Secondary, IconKind.Check, compact: true);
    var openSpeechBtn = new PalmButton("Speech settings", () => DesktopServices.Open("ms-settings:speech"), ButtonTone.Secondary, IconKind.External, compact: true);

    speechCard.AddRow(new SettingActionRow(
      speechReady ? "Offline speech recognizer available" : "No offline speech recognizer detected",
      speechDetail,
      speechReady ? IconKind.Check : IconKind.Warning,
      speechReady ? BadgeTone.Good : BadgeTone.Warning,
      speechReady ? "Ready" : "Setup needed",
      speechReady ? BadgeTone.Good : BadgeTone.Warning,
      null,
      checkSpeechBtn,
      openSpeechBtn));
    page.AddBlock(speechCard);

    var addFolderBtn = new PalmButton("Add folder…", () => Action(() =>
    {
      using var dialog = new FolderBrowserDialog();
      if (ShowModal(() => dialog.ShowDialog(this)) == DialogResult.OK)
      {
        if (!host.Preferences.AppFolders.Contains(dialog.SelectedPath)) host.Preferences.AppFolders.Add(dialog.SelectedPath);
        Save();
        host.Services.Apps.Invalidate();
        Navigate("General");
      }
    }), ButtonTone.Secondary, IconKind.Plus, compact: true);

    var foldersCard = new SurfaceCard(
      "Extra App Folders",
      "Start Menu and Microsoft Store apps are discovered automatically. Include folders for portable apps or external drives.",
      addFolderBtn);

    var folders = host.Preferences.AppFolders.ToArray();
    if (folders.Length == 0)
    {
      foldersCard.AddRow(new SettingActionRow(
        "No extra folders added",
        "Portable executables inside added folders appear alongside Start Menu apps on your phone.",
        IconKind.Folder,
        BadgeTone.Neutral), dividerAbove: false);
    }
    else
    {
      for (var i = 0; i < folders.Length; i++)
      {
        var path = folders[i];
        var folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderName)) folderName = path;
        var removeBtn = new PalmButton("Remove", () => Action(() =>
        {
          host.Preferences.AppFolders.Remove(path);
          Save();
          host.Services.Apps.Invalidate();
          Navigate("General");
        }), ButtonTone.Danger, IconKind.Trash, compact: true);
        foldersCard.AddRow(new SettingActionRow(folderName, null, IconKind.Folder, BadgeTone.Accent, codePreview: path, actions: removeBtn), dividerAbove: i > 0);
      }
    }
    page.AddBlock(foldersCard);

    var safetyCard = new SurfaceCard("Safety & Windows Protection");
    safetyCard.AddRow(new SettingActionRow(
      "Global emergency pause",
      "Toggle remote input from anywhere in Windows, or use Pause control in the notification area menu.",
      IconKind.Pause,
      BadgeTone.Accent,
      "Ctrl + Alt + Pause",
      BadgeTone.Accent), dividerAbove: false);
    safetyCard.AddRow(new SettingActionRow(
      "Protected Windows screens",
      "Windows blocks synthetic input on UAC prompts, the lock screen, and elevated administrator windows. Return to a standard desktop app to resume control.",
      IconKind.Shield,
      BadgeTone.Neutral));
    page.AddBlock(safetyCard, 0);
  }

  private void BuildDevices()
  {
    var refreshBtn = new PalmButton("Refresh", () => { host.RefreshSoon(); Navigate("Devices"); }, ButtonTone.Secondary, IconKind.Refresh, compact: true)
    {
      BackColor = Theme.Canvas
    };
    page.AddBlock(new PageHeader("Devices", "Inspect active sessions and manage paired phones trusted by this PC.", refreshBtn));

    if (host.Snapshot.ValueKind != JsonValueKind.Object) return;
    var connected = host.Snapshot.GetProperty("connected").EnumerateArray().ToArray();
    var devices = host.Snapshot.GetProperty("trusted").EnumerateArray().ToArray();
    lastDevicesDigest = host.Snapshot.GetProperty("connected").GetRawText() + "|" + host.Snapshot.GetProperty("trusted").GetRawText();

    var activeCard = new SurfaceCard("Connected Now", "Phones currently controlling this PC. Disconnecting closes the live session without revoking trust.");
    if (connected.Length == 0)
    {
      activeCard.AddRow(new SettingActionRow(
        "No active sessions",
        "Open your saved WinPalm tab on a paired phone, or scan the QR code on Connect.",
        IconKind.Devices,
        BadgeTone.Neutral), dividerAbove: false);
    }
    else
    {
      for (var i = 0; i < connected.Length; i++)
      {
        var session = connected[i];
        var sessionId = session.GetProperty("session").GetUInt64();
        var name = session.GetProperty("name").GetString() ?? "Connected device";
        var model = session.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        var addr = session.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "";
        var secure = session.GetProperty("secure").GetBoolean();
        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(model)) detailParts.Add(model);
        if (!string.IsNullOrWhiteSpace(addr)) detailParts.Add(addr);
        var disconnectBtn = new PalmButton("Disconnect", () => Action(() =>
        {
          CoreBridge.Call(new { op = "disconnect", id = sessionId });
          host.RefreshSoon();
          Navigate("Devices");
        }), ButtonTone.Secondary, compact: true);

        activeCard.AddRow(new SettingActionRow(
          name,
          string.Join(" · ", detailParts),
          IconKind.Devices,
          BadgeTone.Good,
          secure ? "HTTPS" : "HTTP",
          secure ? BadgeTone.Good : BadgeTone.Neutral,
          null,
          disconnectBtn), dividerAbove: i > 0);
      }
    }
    page.AddBlock(activeCard);

    PalmButton? forgetAllBtn = null;
    if (devices.Length > 0)
    {
      forgetAllBtn = new PalmButton("Remove all…", () =>
      {
        if (ShowModal(() => PalmDialog.Confirm(this, "Remove all paired devices?", "Every paired phone will be disconnected and its saved phone settings on this PC will be deleted.", "Remove all", destructive: true)))
          Action(() => { CoreBridge.Call(new { op = "forgetAll" }); host.RefreshSoon(); Navigate("Devices"); });
      }, ButtonTone.Danger, IconKind.Trash, compact: true);
    }

    var trustedCard = new SurfaceCard(
      "Paired Devices",
      "Removing a device revokes its pairing token and deletes its custom keyboards and gesture preferences saved on this PC.",
      forgetAllBtn);

    if (devices.Length == 0)
    {
      var openConnect = new PalmButton("Go to Connect", () => Navigate("Connect"), ButtonTone.Primary, IconKind.Connect, compact: true);
      trustedCard.AddRow(new SettingActionRow(
        "No paired devices yet",
        "Pair your phone with a QR scan, PIN, or approval request to reconnect effortlessly next time.",
        IconKind.Devices,
        BadgeTone.Neutral,
        actions: openConnect), dividerAbove: false);
    }
    else
    {
      for (var i = 0; i < devices.Length; i++)
      {
        var device = devices[i];
        var id = device.GetProperty("id").GetString()!;
        var name = device.GetProperty("name").GetString()!;
        var model = device.GetProperty("model").GetString() ?? "";
        var lastSeen = DateTimeOffset.FromUnixTimeSeconds(device.GetProperty("lastSeen").GetInt64()).ToLocalTime();
        var isOnline = connected.Any(d => d.GetProperty("deviceId").GetString() == id);

        var renameBtn = new PalmButton("Rename", () =>
        {
          var next = ShowModal(() => PalmDialog.PromptText(this, "Rename device", "Device name", name));
          if (next is not null)
            Action(() => { CoreBridge.Call(new { op = "rename", deviceId = id, name = next }); host.RefreshSoon(); Navigate("Devices"); });
        }, ButtonTone.Secondary, IconKind.Edit, compact: true);

        var removeBtn = new PalmButton("Remove", () =>
        {
          if (ShowModal(() => PalmDialog.Confirm(this, $"Remove \"{name}\"?", "This device will need to pair again, and its saved phone settings on this PC will be deleted.", "Remove device", destructive: true)))
            Action(() => { CoreBridge.Call(new { op = "forget", deviceId = id }); host.RefreshSoon(); Navigate("Devices"); });
        }, ButtonTone.Danger, IconKind.Trash, compact: true);

        var sub = string.IsNullOrWhiteSpace(model) ? $"Last used {lastSeen:g}" : $"{model} · Last used {lastSeen:g}";
        trustedCard.AddRow(new SettingActionRow(
          name,
          sub,
          IconKind.Devices,
          isOnline ? BadgeTone.Good : BadgeTone.Neutral,
          isOnline ? "Connected" : null,
          BadgeTone.Good,
          null,
          renameBtn,
          removeBtn), dividerAbove: i > 0);
      }
    }
    page.AddBlock(trustedCard, 0);
  }

  private void BuildDeck()
  {
    page.AddBlock(new PageHeader("Deck", "Your shortcut button pages are shared across every paired phone. Tap Edit on the phone's Deck tab to customize tiles."));

    var deck = CoreBridge.Call(new { op = "deck" });
    var pages = deck.GetProperty("pages").EnumerateArray().ToArray();
    var totalTiles = pages.Sum(p => p.GetProperty("tiles").GetArrayLength());

    var pagesCard = new SurfaceCard(
      $"{pages.Length} {(pages.Length == 1 ? "Page" : "Pages")} · {totalTiles} {(totalTiles == 1 ? "Button" : "Buttons")}",
      "Current shared Deck layout synced with connected devices.");

    if (pages.Length == 0)
    {
      pagesCard.AddRow(new SettingActionRow("Empty deck", "Add pages on your phone or reset to the Windows starter deck below.", IconKind.Deck), dividerAbove: false);
    }
    else
    {
      for (var i = 0; i < pages.Length; i++)
      {
        var item = pages[i];
        var pageName = item.GetProperty("name").GetString() ?? $"Page {i + 1}";
        var count = item.GetProperty("tiles").GetArrayLength();
        pagesCard.AddRow(new SettingActionRow(
          pageName,
          $"Page {i + 1}",
          IconKind.Deck,
          BadgeTone.Accent,
          $"{count} {(count == 1 ? "button" : "buttons")}",
          BadgeTone.Neutral), dividerAbove: i > 0);
      }
    }
    page.AddBlock(pagesCard);

    var exportBtn = new PalmButton("Export deck…", () => Action(() =>
    {
      using var dialog = new SaveFileDialog { Filter = "WinPalm deck (*.json)|*.json", FileName = "WinPalm-deck.json" };
      if (ShowModal(() => dialog.ShowDialog(this)) == DialogResult.OK)
        File.WriteAllText(dialog.FileName, CoreBridge.Call(new { op = "deck" }).GetRawText());
    }), ButtonTone.Secondary, IconKind.Folder, compact: true);

    var importBtn = new PalmButton("Import deck…", () => Action(() =>
    {
      using var dialog = new OpenFileDialog { Filter = "WinPalm deck (*.json)|*.json" };
      if (ShowModal(() => dialog.ShowDialog(this)) != DialogResult.OK) return;
      if (new FileInfo(dialog.FileName).Length > 1_048_576) throw new InvalidOperationException("That deck file is too large.");
      using var document = JsonDocument.Parse(File.ReadAllText(dialog.FileName));
      if (ShowModal(() => PalmDialog.Confirm(this, "Replace shared Deck?", "Importing this file replaces the current Deck pages across every paired phone.", "Import deck")))
      {
        CoreBridge.Call(new { op = "setDeck", deck = document.RootElement });
        Navigate("Deck");
      }
    }), ButtonTone.Secondary, IconKind.Plus, compact: true);

    var resetBtn = new PalmButton("Reset deck…", () =>
    {
      if (ShowModal(() => PalmDialog.Confirm(this, "Reset to Windows starter deck?", "Every shared Deck page and button will be replaced with the default Windows layout. Export first if you want a backup.", "Reset deck", destructive: true)))
        Action(() => { CoreBridge.Call(new { op = "resetDeck" }); Navigate("Deck"); });
    }, ButtonTone.Danger, IconKind.Refresh, compact: true);

    var manageCard = new SurfaceCard("Backup & Reset");
    manageCard.AddRow(new SettingActionRow(
      "Export or import Deck file",
      "Save a JSON backup of your pages, or load a Deck file onto this PC.",
      IconKind.Folder,
      BadgeTone.Neutral,
      actions: [exportBtn, importBtn]), dividerAbove: false);
    manageCard.AddRow(new SettingActionRow(
      "Starter layout",
      "Restore the default Windows pages for apps, window snapping, media, and system controls.",
      IconKind.Refresh,
      BadgeTone.Warning,
      actions: resetBtn));
    page.AddBlock(manageCard, 0);
  }

  private void BuildAutomations()
  {
    page.AddBlock(new PageHeader("Automations", "Define named PowerShell commands on this PC, then choose Run automation on any Deck button on your phone."));

    var permCard = new SurfaceCard("PowerShell Access");
    SettingToggleRow? shellToggle = null;
    shellToggle = new SettingToggleRow(
      "Allow PowerShell commands and automations",
      "Commands run without a profile under your Windows account with a 5-minute limit. Pausing or disconnecting terminates running jobs, while apps launched with Start-Process stay open.",
      host.Preferences.AllowShell,
      value =>
      {
        if (value && !ShowModal(() => PalmDialog.Confirm(this, "Allow PowerShell commands?", "Paired devices will be able to trigger PowerShell commands with your Windows user permissions. Commands can read, change, and delete your files. Only pair devices you trust.", "Allow PowerShell", destructive: true)))
        {
          shellToggle?.SetChecked(false);
          return;
        }
        Action(() => { host.Preferences.AllowShell = value; Save(); });
      },
      IconKind.Automations,
      BadgeTone.Warning);
    permCard.AddRow(shellToggle, dividerAbove: false);
    page.AddBlock(permCard);

    var addBtn = new PalmButton("Add automation…", () => EditAutomation(null), ButtonTone.Primary, IconKind.Plus, compact: true);
    var listCard = new SurfaceCard(
      "Saved Automations",
      "If a command fails, its error message is shown right on the phone.",
      addBtn);

    var entries = host.Preferences.Automations.ToArray();
    if (entries.Length == 0)
    {
      listCard.AddRow(new SettingActionRow(
        "No automations saved yet",
        "Create named commands for scripts, developer tools, or multi-step desktop workflows.",
        IconKind.Automations,
        BadgeTone.Neutral), dividerAbove: false);
    }
    else
    {
      for (var i = 0; i < entries.Length; i++)
      {
        var pair = entries[i];
        var editBtn = new PalmButton("Edit", () => EditAutomation(pair.Key), ButtonTone.Secondary, IconKind.Edit, compact: true);
        var removeBtn = new PalmButton("Remove", () => Action(() =>
        {
          host.Preferences.Automations.Remove(pair.Key);
          Save();
          Navigate("Automations");
        }), ButtonTone.Danger, IconKind.Trash, compact: true);
        listCard.AddRow(new SettingActionRow(
          pair.Key,
          null,
          IconKind.Automations,
          BadgeTone.Accent,
          codePreview: pair.Value,
          actions: [editBtn, removeBtn]), dividerAbove: i > 0);
      }
    }
    page.AddBlock(listCard, 0);
  }

  private void EditAutomation(string? existing)
  {
    var initialCmd = existing is not null && host.Preferences.Automations.TryGetValue(existing, out var found) ? found : "";
    var result = ShowModal(() => PalmDialog.PromptAutomation(this, existing, initialCmd));
    if (result is not var (name, command)) return;
    Action(() =>
    {
      if (existing is not null) host.Preferences.Automations.Remove(existing);
      host.Preferences.Automations[name] = command;
      Save();
      Navigate("Automations");
    });
  }

  private void BuildPhoneSetup()
  {
    page.AddBlock(new PageHeader("Phone setup", "Control how phones pair and install WinPalm's local certificate for HTTPS and microphone access."));

    var pairingCard = new SurfaceCard("Pairing Methods", "QR code pairing is always available while sharing is active.");
    pairingCard.AddRow(new SettingToggleRow(
      "Allow PIN pairing",
      "Let phones pair by entering the 6-digit PIN shown on Connect. The PIN works only while Connect is open and briefly afterward.",
      host.Preferences.AllowPin,
      value => Action(() => { host.Preferences.AllowPin = value; Save(); }),
      IconKind.Key,
      BadgeTone.Accent), dividerAbove: false);
    pairingCard.AddRow(new SettingToggleRow(
      "Allow pairing approval requests",
      "Let a nearby phone request access without a code and review an approval prompt on this PC.",
      host.Preferences.AllowApproval,
      value => Action(() => { host.Preferences.AllowApproval = value; Save(); }),
      IconKind.PhoneSetup,
      BadgeTone.Accent));
    page.AddBlock(pairingCard);

    string? certUrl = null;
    string? fingerprint = null;
    if (host.Snapshot.ValueKind == JsonValueKind.Object)
    {
      var running = host.Snapshot.GetProperty("running").GetBoolean();
      var pairing = host.Snapshot.GetProperty("pairing");
      var addr = pairing.GetProperty("address").GetString();
      if (running && !string.IsNullOrEmpty(addr)) certUrl = addr.TrimEnd('/') + "/winpalm.crt";
      fingerprint = pairing.GetProperty("caFingerprint").GetString();
    }

    var certCard = new CertificateSetupCard(
      certUrl,
      fingerprint,
      () => Action(() =>
      {
        using var dialog = new SaveFileDialog { FileName = "WinPalm.crt", Filter = "Certificate (*.crt)|*.crt" };
        if (ShowModal(() => dialog.ShowDialog(this)) == DialogResult.OK)
          File.WriteAllBytes(dialog.FileName, Convert.FromBase64String(CoreBridge.Call(new { op = "certificate" }).GetString()!));
      }),
      () => Action(() =>
      {
        if (!string.IsNullOrEmpty(fingerprint)) Clipboard.SetText(fingerprint);
      }),
      page.Relayout);
    page.AddBlock(certCard);

    var fwCard = new SurfaceCard("Network & Firewall");
    var fwBtn = new PalmButton("Firewall settings", () => DesktopServices.Open("ms-settings:network-advancedsettings"), ButtonTone.Secondary, IconKind.External, compact: true);
    fwCard.AddRow(new SettingActionRow(
      "Private Wi-Fi & port rules",
      $"Allow WinPalm on Private networks only when Windows asks (default TCP port {host.Preferences.Port}). Guest Wi-Fi or AP client isolation blocks phone-to-PC connections.",
      IconKind.Shield,
      BadgeTone.Neutral,
      actions: fwBtn), dividerAbove: false);
    page.AddBlock(fwCard, 0);
  }

  private void BuildAdvanced()
  {
    page.AddBlock(new PageHeader("Advanced", "Server lifecycle, QR address selection, local reverse proxy support, and diagnostic files."));

    var serverCard = new SurfaceCard("Server Status");
    startBtn = new PalmButton("Start sharing", host.Start, ButtonTone.Primary, IconKind.Play, compact: true);
    stopBtn = new PalmButton("Stop sharing", host.Stop, ButtonTone.Danger, IconKind.Stop, compact: true);
    var restartBtn = new PalmButton("Restart", host.Restart, ButtonTone.Secondary, IconKind.Refresh, compact: true);

    serverStatusRow = new SettingActionRow(
      "Local host server",
      "Listening for paired phones on your network.",
      IconKind.Advanced,
      BadgeTone.Good,
      "Running",
      BadgeTone.Good,
      null,
      startBtn,
      stopBtn,
      restartBtn);
    serverCard.AddRow(serverStatusRow, dividerAbove: false);

    if (host.Snapshot.ValueKind == JsonValueKind.Object)
    {
      var pairing = host.Snapshot.GetProperty("pairing");
      var vpn = pairing.TryGetProperty("vpnAddresses", out var vpnArr)
        ? vpnArr.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
        : [];
      var ips = pairing.GetProperty("addresses").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
      if (ips.Length > 0)
      {
        var formatted = string.Join("   ·   ", ips.Select(ip => vpn.Contains(ip) ? $"{ip} (VPN)" : ip));
        serverCard.AddRow(new SettingActionRow(
          ips.Length == 1 ? "Detected network address" : "Detected network addresses",
          null,
          IconKind.Globe,
          BadgeTone.Neutral,
          codePreview: formatted));
      }
      UpdateAdvancedServerRow(
        host.Snapshot.GetProperty("running").GetBoolean(),
        host.Snapshot.GetProperty("paused").GetBoolean(),
        pairing.GetProperty("port").GetInt32());
    }
    page.AddBlock(serverCard);

    var netCard = new SurfaceCard("Network & Binding", "Save & restart applies port, QR address mode, and reverse proxy changes.");
    var portStepper = new NumericStepper(Math.Clamp(host.Preferences.Port, 1024, 65535), 1024, 65535);
    netCard.AddRow(new SettingActionRow(
      "Preferred TCP port",
      "Default is 8790. If busy, WinPalm picks the next free port automatically.",
      IconKind.Advanced,
      BadgeTone.Neutral,
      actions: portStepper), dividerAbove: false);

    var initialModeIdx = host.Preferences.AddressMode switch { "hostName" => 1, "custom" => 2, _ => 0 };
    var customField = new StyledTextBox(
      host.Preferences.CustomAddress,
      monospace: true,
      label: "Custom host or URL",
      hint: "Enter a VPN IP, DNS hostname, or reverse-proxy http(s) URL without a path or credentials.",
      placeholder: "192.168.1.10 or https://pc.example.com");
    customField.SetShown(initialModeIdx == 2 || !string.IsNullOrWhiteSpace(host.Preferences.CustomAddress));

    string ModeDescription(int idx) => idx switch
    {
      1 => "Uses this PC's .local mDNS name so links survive DHCP changes. Choose LAN IP if an Android browser cannot resolve .local.",
      2 => "Uses a custom IP, Tailscale/WireGuard address, or HTTPS reverse proxy address that you manage.",
      _ => "Uses your active LAN IPv4 address for immediate compatibility across iPhone, iPad, and Android."
    };

    var modeInfo = new SettingActionRow("QR link address", ModeDescription(initialModeIdx), IconKind.Globe, BadgeTone.Accent);
    var modeSeg = new SegmentedControl(
      [("LAN IP (Recommended)", IconKind.Globe), ("PC Name (.local)", IconKind.Devices), ("Custom / VPN", IconKind.Lock)],
      initialModeIdx,
      idx =>
      {
        modeInfo.UpdateCopy("QR link address", ModeDescription(idx));
        customField.SetShown(idx == 2 || !string.IsNullOrWhiteSpace(customField.Value));
        page.Relayout();
      });

    netCard.AddRow(modeInfo);
    netCard.AddRow(modeSeg, dividerAbove: false);
    netCard.AddRow(customField, dividerAbove: false);

    var proxyToggle = new SettingToggleRow(
      "Trust forwarding headers from a local reverse proxy",
      "Only enable when running Caddy, nginx, or a VPN HTTPS proxy on this PC.",
      host.Preferences.AllowLocalProxy,
      _ => { },
      IconKind.Shield,
      BadgeTone.Neutral);
    netCard.AddRow(proxyToggle);

    PalmButton? saveRestartBtn = null;
    saveRestartBtn = new PalmButton("Save & restart server", () => Action(() =>
    {
      var trimmed = customField.Value.Trim();
      if (modeSeg.SelectedIndex == 2 && (!Uri.TryCreate(trimmed.Contains("://") ? trimmed : "http://" + trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo)))
        throw new InvalidOperationException("Enter a host, IP address, or HTTP(S) address without a path or credentials.");

      host.Preferences.Port = portStepper.Value;
      host.Preferences.AddressMode = modeSeg.SelectedIndex switch { 1 => "hostName", 2 => "custom", _ => "ipAddress" };
      host.Preferences.CustomAddress = trimmed;
      host.Preferences.AllowLocalProxy = proxyToggle.Checked;
      Save();
      host.Restart();
      saveRestartBtn?.Flash("Saved & restarted", IconKind.Check);
    }), ButtonTone.Primary, IconKind.Check);
    netCard.AddRow(new SettingActionRow(
      "Apply configuration",
      "Connected phones briefly reconnect after the server restarts.",
      actions: saveRestartBtn));
    page.AddBlock(netCard);

    var filesCard = new SurfaceCard("Diagnostics & Local Data");
    var openDataBtn = new PalmButton("Open data folder", () => DesktopServices.Open(Preferences.DataDirectory), ButtonTone.Secondary, IconKind.Folder, compact: true);
    var openLogBtn = new PalmButton("Open log", () => Action(() =>
    {
      if (!File.Exists(Diagnostics.FilePath)) File.WriteAllText(Diagnostics.FilePath, "WinPalm diagnostics\n");
      DesktopServices.Open(Diagnostics.FilePath);
    }), ButtonTone.Secondary, IconKind.External, compact: true);

    filesCard.AddRow(new SettingActionRow(
      "Private storage & diagnostic log",
      "Logs record only error types and codes. Keystrokes, speech, clipboard text, and pairing secrets stay out of the log.",
      IconKind.Folder,
      BadgeTone.Neutral,
      actions: [openDataBtn, openLogBtn]), dividerAbove: false);
    page.AddBlock(filesCard, 0);
  }

  private void UpdateAdvancedServerRow(bool running, bool paused, int port)
  {
    if (serverStatusRow is null) return;
    startBtn?.SetShown(!running);
    stopBtn?.SetShown(running);
    var pill = !running ? "Stopped" : paused ? $"Paused · Port {port}" : $"Running · Port {port}";
    var tone = !running ? BadgeTone.Danger : paused ? BadgeTone.Warning : BadgeTone.Good;
    var sub = !running
      ? "Sharing is stopped. Phones cannot connect until you start the server."
      : paused
        ? "Phones remain connected while remote input is paused."
        : $"Accepting connections from paired devices on TCP port {port}.";
    serverStatusRow.UpdateCopy("Local host server", sub, pill, tone);
    if (serverStatusRow.Width > 0 && serverStatusRow.GetHeightForWidth(serverStatusRow.Width) != serverStatusRow.Height)
      page.Relayout();
    else
      serverStatusRow.Invalidate();
  }

  internal void ShowUpdates() => Navigate("Updates");

  private void BuildUpdates()
  {
    page.AddBlock(new PageHeader("Updates", "Check GitHub for verified releases and update in place while keeping your paired devices, settings, and Deck."));

    var statusCard = new SurfaceCard("Software Update");
    updateCheck = new PalmButton("Check now", async () => await host.Updates.CheckAsync(), ButtonTone.Secondary, IconKind.Refresh, compact: true);
    updateInstall = new PalmButton("Download & restart", async () => await host.Updates.InstallAsync(), ButtonTone.Primary, IconKind.Updates, compact: true);
    updateCancel = new PalmButton("Cancel", host.Updates.Cancel, ButtonTone.Danger, IconKind.Stop, compact: true);
    var releasesBtn = new PalmButton("GitHub releases", () => Action(() => DesktopServices.Open(host.Updates.Available?.Page.AbsoluteUri ?? ReleaseUpdates.ReleasesUrl)), ButtonTone.Secondary, IconKind.External, compact: true);

    var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    updateStatusRow = new SettingActionRow(
      $"Installed: WinPalm {UpdateController.CurrentVersion.ToString(3)}",
      host.Updates.Message,
      IconKind.Updates,
      BadgeTone.Accent,
      arch,
      BadgeTone.Neutral,
      null,
      updateCheck,
      updateInstall,
      updateCancel,
      releasesBtn);
    statusCard.AddRow(updateStatusRow, dividerAbove: false);

    updateProgress = new StyledProgressBar();
    updateProgress.SetShown(false);
    statusCard.AddRow(updateProgress, dividerAbove: false);

    statusCard.AddRow(new SettingToggleRow(
      "Check GitHub for updates automatically",
      "Checks once a day and shows a tray notification when a new release is published. Nothing downloads until you choose Download & restart.",
      host.Preferences.CheckForUpdates,
      value => Action(() => { host.Preferences.CheckForUpdates = value; Save(); if (value) host.Updates.CheckWhenDue(); }),
      IconKind.Updates,
      BadgeTone.Neutral));
    page.AddBlock(statusCard);

    var notesCard = new SurfaceCard("Release Notes", "Packages are SHA-256 verified before replacing this installation.");
    updateNotes = new StyledTextBox("", multiline: true, readOnly: true, multilineHeight: 190);
    notesCard.AddRow(updateNotes, dividerAbove: false);
    page.AddBlock(notesCard, 0);

    UpdateUpdates();
  }

  internal void UpdateUpdates()
  {
    RefreshSidebarState();
    if (section != "Updates" || IsDisposed || updateStatusRow is null || updateCheck is null || updateInstall is null || updateCancel is null || updateProgress is null || updateNotes is null) return;

    var updates = host.Updates;
    var hasPackage = updates.Available is { Package: not null, Checksum: not null };
    var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    var layoutChanged = updateInstall.IsShown() != hasPackage || updateCancel.IsShown() != updates.Installing || updateProgress.IsShown() != updates.Installing;
    updateCheck.Enabled = !updates.Busy;
    updateInstall.SetShown(hasPackage);
    updateInstall.Enabled = !updates.Busy;
    updateCancel.SetShown(updates.Installing);
    updateProgress.SetShown(updates.Installing);
    updateProgress.Value = Math.Clamp(updates.Progress, 0, 100);

    var pill = updates.Available is { } found ? $"Update {found.Version.ToString(3)}" : arch;
    var tone = updates.Available is not null ? BadgeTone.Accent : BadgeTone.Neutral;
    updateStatusRow.UpdateCopy($"Installed: WinPalm {UpdateController.CurrentVersion.ToString(3)}", updates.Message, pill, tone);

    var notes = updates.Available is { } release
      ? (release.Package is null || release.Checksum is null
          ? "This release does not include an in-app update package for this processor. Open GitHub releases to download manually.\r\n\r\n"
          : "") + release.Notes.Replace("\r\n", "\n").Replace("\n", "\r\n")
      : "Release notes appear here whenever a newer WinPalm version is available on GitHub.";
    if (updateNotes.Value != notes) updateNotes.Value = notes;

    if (layoutChanged || (updateStatusRow.Width > 0 && updateStatusRow.GetHeightForWidth(updateStatusRow.Width) != updateStatusRow.Height))
      page.Relayout();
    else
      updateStatusRow.Invalidate();
  }

  private void BuildAbout()
  {
    var arch = RuntimeInformation.ProcessArchitecture.ToString();
    var versionLine = $"Version {UpdateController.CurrentVersion.ToString(3)} · {arch} · Native Windows + Rust Core";

    var siteBtn = new PalmButton("Website", () => DesktopServices.Open(WebsiteUrl), ButtonTone.Secondary, IconKind.Globe, compact: true);
    var docsBtn = new PalmButton("Documentation", () => DesktopServices.Open(DocsUrl), ButtonTone.Secondary, IconKind.External, compact: true);
    var repoBtn = new PalmButton("GitHub", () => DesktopServices.Open(RepoUrl), ButtonTone.Secondary, IconKind.External, compact: true);
    var licenseBtn = new PalmButton("Open license", () => DesktopServices.Open(Path.Combine(AppContext.BaseDirectory, "LICENSE")), ButtonTone.Secondary, IconKind.Folder, compact: true);
    var checkBtn = new PalmButton("Check for updates…", async () => { ShowUpdates(); await host.Updates.CheckAsync(); }, ButtonTone.Primary, IconKind.Updates, compact: true);

    page.AddBlock(new AboutHeroCard(versionLine, siteBtn, docsBtn, repoBtn, licenseBtn, checkBtn));

    var detailsCard = new SurfaceCard();
    detailsCard.AddRow(new SettingActionRow(
      "Private by design",
      "WinPalm runs entirely on your local network with no accounts, analytics, or cloud services. Voice typing uses offline Windows speech recognizers installed on this PC.",
      IconKind.Shield,
      BadgeTone.Good), dividerAbove: false);
    detailsCard.AddRow(new SettingActionRow(
      "Open source & provenance",
      "Built by RS Application. Based on CmdPalm and licensed under GNU AGPL-3.0-only. Corresponding source is bundled with every WinPalm package.",
      IconKind.About,
      BadgeTone.Accent));
    page.AddBlock(detailsCard, 0);
  }
}
