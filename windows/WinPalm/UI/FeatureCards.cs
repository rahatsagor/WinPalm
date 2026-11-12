using System.ComponentModel;
using System.Drawing.Drawing2D;
using QRCoder;

namespace WinPalm.UI;

/// Left navigation rail with WinPalm brand mark, live server status card, and grouped nav items.
internal sealed class SidebarChrome : Panel
{
  private readonly SidebarStatusCard statusCard;
  private readonly Dictionary<string, SidebarNavItem> items = new(StringComparer.Ordinal);
  private readonly string versionFooter;

  internal SidebarChrome(
    string activeSection,
    Action<string> onNavigate,
    Action onQuickAction,
    string versionFooter)
  {
    this.versionFooter = versionFooter;
    Dock = DockStyle.Left;
    Width = Theme.Px(236);
    BackColor = Theme.Sidebar;
    ForeColor = Theme.Text;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

    var padX = Theme.Px(14);
    var innerW = Width - padX * 2;

    statusCard = new SidebarStatusCard(onQuickAction)
    {
      Bounds = new Rectangle(padX, Theme.Px(74), innerW, Theme.Px(56))
    };
    Controls.Add(statusCard);

    var groups = new (string Header, (string Name, IconKind Icon)[] Entries)[]
    {
      ("WORKSPACE", [("Connect", IconKind.Connect), ("Devices", IconKind.Devices), ("Deck", IconKind.Deck), ("Automations", IconKind.Automations)]),
      ("SETTINGS", [("General", IconKind.General), ("Phone setup", IconKind.PhoneSetup), ("Advanced", IconKind.Advanced)]),
      ("SYSTEM", [("Updates", IconKind.Updates), ("About", IconKind.About)])
    };

    var y = Theme.Px(144);
    foreach (var (header, entries) in groups)
    {
      var groupLabel = new SidebarGroupHeader(header)
      {
        Bounds = new Rectangle(padX, y, innerW, Theme.Px(18))
      };
      Controls.Add(groupLabel);
      y += Theme.Px(20);

      foreach (var (name, icon) in entries)
      {
        var nav = new SidebarNavItem(name, icon, name == activeSection, () => onNavigate(name))
        {
          Bounds = new Rectangle(padX, y, innerW, Theme.Px(35))
        };
        items[name] = nav;
        Controls.Add(nav);
        y += Theme.Px(37);
      }
      y += Theme.Px(8);
    }
  }

  internal void SetActiveSection(string section)
  {
    foreach (var (name, item) in items)
      item.SetSelected(name == section);
  }

  internal void UpdateStatus(bool running, bool paused, int connectedCount, int port, bool updateAvailable)
  {
    statusCard.UpdateState(running, paused, connectedCount, port);
    if (items.TryGetValue("Devices", out var devicesItem))
      devicesItem.SetBadge(connectedCount > 0 ? connectedCount.ToString() : null, BadgeTone.Good);
    if (items.TryGetValue("Updates", out var updatesItem))
      updatesItem.SetBadge(updateAvailable ? "NEW" : null, BadgeTone.Accent);
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(Theme.Sidebar);
    g.SmoothingMode = SmoothingMode.AntiAlias;

    using (var topWash = new LinearGradientBrush(new RectangleF(0, 0, Width, Theme.PxF(72f)), Color.FromArgb(20, Theme.Accent), Color.FromArgb(0, Theme.Accent), LinearGradientMode.Vertical) { WrapMode = WrapMode.TileFlipXY })
      g.FillRectangle(topWash, 0, 0, Width - 1, Theme.Px(70));

    using (var borderPen = new Pen(Theme.Border, 1f))
      g.DrawLine(borderPen, Width - 1, 0, Width - 1, Height);

    var padX = Theme.Px(16);
    var markSize = Theme.PxF(38f);
    var markY = Theme.PxF(20f);
    Icons.DrawBrandMark(g, new RectangleF(padX, markY, markSize, markSize));

    var textX = padX + (int)markSize + Theme.Px(11);
    var textW = Width - textX - Theme.Px(10);
    var nameFont = Theme.Display(15.5f, true);
    var nameSize = Theme.MeasureText("WinPalm", nameFont, textW, singleLine: true);
    Theme.DrawText(g, "WinPalm", nameFont, new Rectangle(textX, Theme.Px(19), textW, nameSize.Height + 2), Theme.Text, TextFormatFlags.Left | TextFormatFlags.SingleLine);
    Theme.DrawText(g, "Your PC, in your palm.", Theme.Font(8.5f), new Rectangle(textX, Theme.Px(19) + nameSize.Height, textW, Theme.Px(18)), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.SingleLine);

    if (Height >= Theme.Px(592))
    {
      Theme.DrawText(g, versionFooter, Theme.Mono(8f), new Rectangle(padX, Height - Theme.Px(26), Width - padX * 2, Theme.Px(18)), Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
  }

  private sealed class SidebarGroupHeader : Control
  {
    internal SidebarGroupHeader(string text)
    {
      Text = text;
      BackColor = Theme.Sidebar;
      TabStop = false;
      SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      e.Graphics.Clear(Theme.Sidebar);
      Theme.DrawText(e.Graphics, Text, Theme.Font(7.5f, true), new Rectangle(Theme.Px(10), 0, Width - Theme.Px(12), Height), Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
  }

  private sealed class SidebarStatusCard : Control
  {
    private readonly Action onQuickAction;
    private readonly ToolTip tip = new();
    private bool running = true, paused;
    private int connectedCount, port = 8790;
    private bool hoverAction;

    internal SidebarStatusCard(Action onQuickAction)
    {
      this.onQuickAction = onQuickAction;
      BackColor = Theme.Sidebar;
      TabStop = true;
      AccessibleName = "Server status";
      AccessibleRole = AccessibleRole.StatusBar;
      SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    internal void UpdateState(bool isRunning, bool isPaused, int count, int activePort)
    {
      if (running == isRunning && paused == isPaused && connectedCount == count && port == activePort) return;
      running = isRunning;
      paused = isPaused;
      connectedCount = count;
      port = activePort;
      tip.SetToolTip(this, !running ? "Start sharing" : paused ? "Resume remote control" : "Pause remote control");
      Invalidate();
    }

    private Rectangle ActionBounds => new(Width - Theme.Px(38), (Height - Theme.Px(28)) / 2, Theme.Px(28), Theme.Px(28));

    protected override void OnMouseMove(MouseEventArgs e)
    {
      base.OnMouseMove(e);
      var over = ActionBounds.Contains(e.Location);
      Cursor = over ? Cursors.Hand : Cursors.Default;
      if (over != hoverAction) { hoverAction = over; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverAction = false; Cursor = Cursors.Default; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
      base.OnMouseDown(e);
      if (e.Button == MouseButtons.Left && ActionBounds.Contains(e.Location)) onQuickAction();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      var g = e.Graphics;
      g.Clear(Theme.Sidebar);
      g.SmoothingMode = SmoothingMode.AntiAlias;

      var dotColor = !running ? Theme.Danger : paused ? Theme.Warning : connectedCount > 0 ? Theme.Good : Theme.Accent;
      var borderColor = !running ? Theme.Border : paused ? Theme.WarningBorder : connectedCount > 0 ? Theme.GoodBorder : Theme.Border;
      Theme.PaintCardFrame(g, ClientRectangle, Theme.PxF(10f), Theme.Surface, Theme.Surface, borderColor, topSheen: false);

      var dotSize = Theme.PxF(8f);
      var dotX = Theme.PxF(12f);
      var dotY = Theme.PxF(15f);
      using (var halo = new SolidBrush(Color.FromArgb(42, dotColor)))
        g.FillEllipse(halo, dotX - 3f, dotY - 3f, dotSize + 6f, dotSize + 6f);
      using (var dot = new SolidBrush(dotColor))
        g.FillEllipse(dot, dotX, dotY, dotSize, dotSize);

      var headline = !running ? "Sharing stopped" : paused ? "Control paused" : connectedCount > 0 ? $"{connectedCount} connected" : "Ready to pair";
      var detail = !running ? "Tap play to start" : paused ? "Phones stay paired" : port > 0 ? $"Port {port} · Wi-Fi" : "Local network";

      var btnRect = ActionBounds;
      var textX = Theme.Px(27);
      var textW = Math.Max(40, btnRect.Left - textX - Theme.Px(6));
      Theme.DrawText(g, headline, Theme.Font(9.25f, true), new Rectangle(textX, Theme.Px(9), textW, Theme.Px(19)), Theme.Text, TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
      Theme.DrawText(g, detail, Theme.Font(8f), new Rectangle(textX, Theme.Px(28), textW, Theme.Px(18)), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

      using (var btnPath = Theme.RoundedRect(btnRect, Theme.PxF(7f)))
      using (var btnFill = new SolidBrush(hoverAction ? Theme.SurfaceHover : Theme.SurfaceRaised))
      using (var btnPen = new Pen(hoverAction ? Theme.BorderStrong : Theme.Border, 1f))
      {
        g.FillPath(btnFill, btnPath);
        g.DrawPath(btnPen, btnPath);
      }
      var actionIcon = !running || paused ? IconKind.Play : IconKind.Pause;
      var iconPx = Theme.PxF(13f);
      Icons.Draw(g, actionIcon, new RectangleF(btnRect.X + (btnRect.Width - iconPx) / 2f, btnRect.Y + (btnRect.Height - iconPx) / 2f, iconPx, iconPx), !running || paused ? Theme.Accent : Theme.Text);
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing) tip.Dispose();
      base.Dispose(disposing);
    }
  }

  private sealed class SidebarNavItem : Control
  {
    private readonly IconKind icon;
    private readonly Action onClick;
    private bool selected, hovered;
    private string? badge;
    private BadgeTone badgeTone;

    internal SidebarNavItem(string label, IconKind icon, bool selected, Action onClick)
    {
      Text = label;
      this.icon = icon;
      this.selected = selected;
      this.onClick = onClick;
      AccessibleName = label;
      AccessibleRole = AccessibleRole.PageTab;
      Cursor = Cursors.Hand;
      TabStop = true;
      BackColor = Theme.Sidebar;
      SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.StandardClick, true);
    }

    internal void SetSelected(bool active)
    {
      if (selected == active) return;
      selected = active;
      Invalidate();
    }

    internal void SetBadge(string? value, BadgeTone tone)
    {
      if (badge == value && badgeTone == tone) return;
      badge = value;
      badgeTone = tone;
      Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); onClick(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
      base.OnKeyDown(e);
      if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; onClick(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      var g = e.Graphics;
      g.Clear(Theme.Sidebar);
      g.SmoothingMode = SmoothingMode.AntiAlias;

      var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
      if (selected || hovered)
      {
        using var bgPath = Theme.RoundedRect(rect, Theme.PxF(8f));
        using var bgFill = new SolidBrush(selected ? Theme.AccentSurface : Theme.Surface);
        g.FillPath(bgFill, bgPath);
        if (selected)
        {
          using var borderPen = new Pen(Color.FromArgb(75, Theme.Accent), 1f);
          g.DrawPath(borderPen, bgPath);
          var barH = Theme.PxF(16f);
          using var barPath = Theme.RoundedRect(new RectangleF(Theme.PxF(3f), (Height - barH) / 2f, Theme.PxF(3f), barH), 2f);
          using var barBrush = new SolidBrush(Theme.Accent);
          g.FillPath(barBrush, barPath);
        }
      }

      if (Focused && ShowFocusCues)
      {
        using var focusPath = Theme.RoundedRect(rect, Theme.PxF(8f));
        using var focusPen = new Pen(Theme.Accent, 1.2f);
        g.DrawPath(focusPen, focusPath);
      }

      var iconPx = Theme.PxF(16f);
      var iconX = Theme.PxF(12f);
      var iconColor = selected ? Theme.Accent : hovered ? Theme.Text : Theme.Muted;
      Icons.Draw(g, icon, new RectangleF(iconX, (Height - iconPx) / 2f, iconPx, iconPx), iconColor);

      var badgeW = 0;
      if (!string.IsNullOrEmpty(badge))
      {
        var (bFg, bBg, bBorder) = TonePalette.Resolve(badgeTone);
        var bFont = Theme.Font(7.75f, true);
        var bTextW = Theme.MeasureText(badge, bFont, 80, singleLine: true).Width;
        badgeW = Math.Max(Theme.Px(22), bTextW + Theme.Px(12));
        var bH = Theme.Px(18);
        var bX = Width - badgeW - Theme.Px(8);
        var bY = (Height - bH) / 2;
        using (var bPath = Theme.RoundedRect(new RectangleF(bX, bY, badgeW, bH), bH / 2f))
        using (var bFill = new SolidBrush(bBg))
        using (var bPen = new Pen(bBorder, 1f))
        {
          g.FillPath(bFill, bPath);
          g.DrawPath(bPen, bPath);
        }
        Theme.DrawText(g, badge, bFont, new Rectangle(bX, bY, badgeW, bH + 1), bFg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
      }

      var labelX = Theme.Px(37);
      var labelW = Math.Max(40, Width - labelX - (badgeW > 0 ? badgeW + Theme.Px(14) : Theme.Px(10)));
      Theme.DrawText(g, Text, Theme.Font(9.5f, selected), new Rectangle(labelX, 0, labelW, Height), selected || hovered ? Theme.Text : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
  }
}

/// Hero Connect card with crisp QR pedestal, HTTP/HTTPS switcher, 6-tile PIN display, and copyable address bar.
internal sealed class PairingHeroCard : Panel, ISizableBlock
{
  private readonly SegmentedControl protocolSwitch;
  private readonly PalmButton copyBtn;
  private readonly PalmButton certGuideBtn;
  private readonly PalmButton startServerBtn;
  private Bitmap? qrImage;
  private string lastQrLink = "";
  private string pin = "------";
  private string address = "";
  private uint expiresSecs = 60;
  private bool running = true;
  private bool allowPin = true;
  private bool encrypted;
  private Rectangle qrBoxBounds;
  private Rectangle rightColBounds;
  private Rectangle addressBoxBounds;

  internal PairingHeroCard(
    bool initialEncrypted,
    Action<bool> onEncryptedChanged,
    Action onCopyAddress,
    Action onOpenHttpsSetup,
    Action onStartServer)
  {
    encrypted = initialEncrypted;
    BackColor = Theme.Surface;
    ForeColor = Theme.Text;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

    certGuideBtn = new PalmButton("HTTPS & mic setup", onOpenHttpsSetup, ButtonTone.Subtle, IconKind.External, compact: true)
    {
      BackColor = Theme.Surface
    };
    certGuideBtn.SetShown(initialEncrypted);

    protocolSwitch = new SegmentedControl(
      [("Standard HTTP", IconKind.Globe), ("Encrypted HTTPS", IconKind.Lock)],
      initialEncrypted ? 1 : 0,
      idx =>
      {
        encrypted = idx == 1;
        certGuideBtn.SetShown(encrypted);
        onEncryptedChanged(encrypted);
      });
    Controls.Add(protocolSwitch);
    Controls.Add(certGuideBtn);

    copyBtn = new PalmButton("Copy", () =>
    {
      onCopyAddress();
      copyBtn?.Flash("Copied", IconKind.Check);
    }, ButtonTone.Secondary, IconKind.Copy, compact: true)
    {
      BackColor = Theme.SurfaceRaised
    };
    Controls.Add(copyBtn);

    startServerBtn = new PalmButton("Start sharing", onStartServer, ButtonTone.Primary, IconKind.Play, compact: true)
    {
      BackColor = Theme.SurfaceRaised
    };
    startServerBtn.SetShown(false);
    Controls.Add(startServerBtn);
  }

  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  internal bool UseHttps => encrypted;

  internal void UpdatePairing(bool isRunning, bool pinEnabled, string qrLink, string displayAddress, string currentPin, uint expiresInSecs)
  {
    running = isRunning;
    allowPin = pinEnabled;
    address = displayAddress;
    pin = currentPin;
    expiresSecs = expiresInSecs;
    startServerBtn.SetShown(!running);
    copyBtn.Enabled = running && !string.IsNullOrEmpty(displayAddress);

    if (running && !string.IsNullOrEmpty(qrLink) && lastQrLink != qrLink)
    {
      using var data = QRCodeGenerator.GenerateQrCode(qrLink, QRCodeGenerator.ECCLevel.M);
      using var code = new PngByteQRCode(data);
      var png = code.GetGraphic(16, [15, 17, 22], [255, 255, 255], false);
      using var stream = new MemoryStream(png);
      using var loaded = Image.FromStream(stream);
      var next = new Bitmap(loaded);
      qrImage?.Dispose();
      qrImage = next;
      lastQrLink = qrLink;
    }
    else if (!running)
    {
      lastQrLink = "";
    }
    Invalidate();
  }

  internal void TickCountdown()
  {
    if (!running || expiresSecs == 0) return;
    expiresSecs--;
    Invalidate();
  }

  public int GetHeightForWidth(int width)
  {
    var pad = Theme.Px(22);
    var qrSize = Theme.Px(204);
    var colGap = Theme.Px(24);
    var rightX = pad + qrSize + colGap;
    var rightW = Math.Max(Theme.Px(200), width - rightX - pad);

    qrBoxBounds = new Rectangle(pad, pad, qrSize, qrSize);
    startServerBtn.PerformAutoSize();
    startServerBtn.Location = new Point(
      qrBoxBounds.X + (qrBoxBounds.Width - startServerBtn.Width) / 2,
      qrBoxBounds.Bottom - startServerBtn.Height - Theme.Px(20));

    var ry = pad;
    protocolSwitch.SetBounds(rightX, ry, rightW, Theme.Px(36));
    ry += Theme.Px(36);

    if (certGuideBtn.IsShown())
    {
      certGuideBtn.PerformAutoSize();
      certGuideBtn.Location = new Point(rightX + rightW - certGuideBtn.Width, ry + Theme.Px(4));
      ry += certGuideBtn.Height + Theme.Px(6);
    }
    else
    {
      ry += Theme.Px(14);
    }

    // Reserve PIN section height (header + 6 keycaps + progress bar)
    var pinSectionH = Theme.Px(20) + Theme.Px(48) + Theme.Px(18);
    ry += pinSectionH;

    // Browser address label + rounded bar
    ry += Theme.Px(18);
    var addrH = Theme.Px(40);
    addressBoxBounds = new Rectangle(rightX, ry, rightW, addrH);
    copyBtn.PerformAutoSize();
    copyBtn.Location = new Point(addressBoxBounds.Right - copyBtn.Width - Theme.Px(5), addressBoxBounds.Y + (addrH - copyBtn.Height) / 2);
    ry += addrH;

    rightColBounds = new Rectangle(rightX, pad, rightW, ry - pad);
    var leftBottom = qrBoxBounds.Bottom + Theme.Px(26);
    return Math.Max(leftBottom, ry) + pad;
  }

  protected override void OnResize(EventArgs eventargs)
  {
    base.OnResize(eventargs);
    if (protocolSwitch is not null && Width > 0) _ = GetHeightForWidth(Width);
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(Parent?.BackColor ?? Theme.Canvas);
    if (Width <= 8 || Height <= 8) return;
    g.SmoothingMode = SmoothingMode.AntiAlias;

    Theme.PaintCardFrame(g, ClientRectangle, Theme.PxF(14f), Theme.Surface, Theme.Surface, Theme.BorderStrong);

    // Subtle radial warm halo behind the QR pedestal
    if (running)
    {
      var haloRect = RectangleF.Inflate(qrBoxBounds, Theme.PxF(34f), Theme.PxF(34f));
      using var haloPath = new GraphicsPath();
      haloPath.AddEllipse(haloRect);
      using var haloBrush = new PathGradientBrush(haloPath)
      {
        CenterColor = Color.FromArgb(30, Theme.Accent),
        SurroundColors = [Color.FromArgb(0, Theme.Accent)]
      };
      g.FillPath(haloBrush, haloPath);
    }

    // Subtle vertical divider between QR pedestal and pairing controls
    var splitX = qrBoxBounds.Right + Theme.Px(12);
    using (var splitPen = new Pen(Theme.Divider, 1f))
      g.DrawLine(splitPen, splitX, qrBoxBounds.Top + Theme.Px(6), splitX, Math.Max(qrBoxBounds.Bottom, addressBoxBounds.Bottom) - Theme.Px(6));

    // QR pedestal
    if (running && qrImage is not null)
    {
      var glowRect = RectangleF.Inflate(qrBoxBounds, Theme.PxF(3f), Theme.PxF(3f));
      using (var glowPath = Theme.RoundedRect(glowRect, Theme.PxF(18f)))
      using (var glowPen = new Pen(Color.FromArgb(48, Theme.Accent), 2f))
        g.DrawPath(glowPen, glowPath);

      using (var qrCardPath = Theme.RoundedRect(qrBoxBounds, Theme.PxF(15f)))
      using (var whiteBrush = new SolidBrush(Color.White))
        g.FillPath(whiteBrush, qrCardPath);

      var quiet = Theme.Px(15);
      var imgRect = new Rectangle(qrBoxBounds.X + quiet, qrBoxBounds.Y + quiet, qrBoxBounds.Width - quiet * 2, qrBoxBounds.Height - quiet * 2);
      var prevInterp = g.InterpolationMode;
      var prevOffset = g.PixelOffsetMode;
      g.InterpolationMode = InterpolationMode.NearestNeighbor;
      g.PixelOffsetMode = PixelOffsetMode.Half;
      g.DrawImage(qrImage, imgRect);
      g.InterpolationMode = prevInterp;
      g.PixelOffsetMode = prevOffset;

      Theme.DrawText(g, "Scan with phone camera", Theme.Font(8.75f, true), new Rectangle(qrBoxBounds.X, qrBoxBounds.Bottom + Theme.Px(7), qrBoxBounds.Width, Theme.Px(20)), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
    }
    else
    {
      using (var stoppedPath = Theme.RoundedRect(qrBoxBounds, Theme.PxF(15f)))
      using (var stoppedFill = new SolidBrush(Theme.SurfaceRaised))
      using (var stoppedPen = new Pen(Theme.Border, 1f))
      {
        g.FillPath(stoppedFill, stoppedPath);
        g.DrawPath(stoppedPen, stoppedPath);
      }
      var iconPx = Theme.PxF(28f);
      Icons.Draw(g, IconKind.Stop, new RectangleF(qrBoxBounds.X + (qrBoxBounds.Width - iconPx) / 2f, qrBoxBounds.Y + Theme.Px(34), iconPx, iconPx), Theme.Muted);
      Theme.DrawText(g, running ? "Preparing QR code…" : "Sharing is stopped", Theme.Font(10f, true), new Rectangle(qrBoxBounds.X + Theme.Px(14), qrBoxBounds.Y + Theme.Px(72), qrBoxBounds.Width - Theme.Px(28), Theme.Px(22)), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
      if (!running)
        Theme.DrawText(g, "Start the local server to accept phone connections.", Theme.Font(8.75f), new Rectangle(qrBoxBounds.X + Theme.Px(16), qrBoxBounds.Y + Theme.Px(96), qrBoxBounds.Width - Theme.Px(32), Theme.Px(42)), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }

    // Right column content
    var rx = rightColBounds.X;
    var rw = rightColBounds.Width;
    if (rw <= 40) return;

    if (certGuideBtn.IsShown())
    {
      Theme.DrawText(g, "Need certificate?", Theme.Font(8.75f), new Rectangle(rx, certGuideBtn.Top, Math.Max(40, certGuideBtn.Left - rx - Theme.Px(6)), certGuideBtn.Height), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    var pinTop = certGuideBtn.IsShown() ? certGuideBtn.Bottom + Theme.Px(6) : protocolSwitch.Bottom + Theme.Px(14);
    Theme.DrawText(g, "PAIRING PIN", Theme.Font(8f, true), new Rectangle(rx, pinTop, rw / 2, Theme.Px(18)), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.SingleLine);

    if (running && allowPin)
    {
      var timerText = $"Refreshes in {expiresSecs}s";
      Theme.DrawText(g, timerText, Theme.Mono(8.25f), new Rectangle(rx + rw / 2, pinTop, rw / 2, Theme.Px(18)), expiresSecs <= 10 ? Theme.Accent : Theme.Muted, TextFormatFlags.Right | TextFormatFlags.SingleLine);

      var tilesY = pinTop + Theme.Px(20);
      var tileH = Theme.Px(46);
      var midGap = Theme.Px(12);
      var smallGap = Theme.Px(6);
      var maxTileW = (rw - midGap - smallGap * 4) / 6;
      var tileW = Math.Clamp(maxTileW, Theme.Px(24), Theme.Px(44));
      var digits = pin.Length == 6 ? pin : pin.PadRight(6, '-')[..6];
      var tx = rx;
      var digitFont = Theme.Mono(16.5f, true);

      for (var i = 0; i < 6; i++)
      {
        var tileRect = new Rectangle(tx, tilesY, tileW, tileH);
        using (var baseShadow = Theme.RoundedRect(new RectangleF(tileRect.X + 0.5f, tileRect.Y + 2f, tileRect.Width - 1f, tileRect.Height - 1.5f), Theme.PxF(8f)))
        using (var shadowFill = new SolidBrush(Color.FromArgb(14, 16, 21)))
          g.FillPath(shadowFill, baseShadow);

        var capFace = new Rectangle(tileRect.X, tileRect.Y, tileRect.Width, tileRect.Height - Theme.Px(2));
        Theme.PaintCardFrame(g, capFace, Theme.PxF(8f), Theme.SurfaceHover, Theme.SurfaceRaised, Theme.BorderStrong);
        Theme.DrawText(g, digits[i].ToString(), digitFont, capFace, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        tx += tileW + (i == 2 ? midGap : smallGap);
      }

      var barW = tx - smallGap - rx;
      var barY = tilesY + tileH + Theme.Px(8);
      var barH = Theme.PxF(3f);
      using (var trackPath = Theme.RoundedRect(new RectangleF(rx, barY, barW, barH), barH / 2f))
      using (var trackFill = new SolidBrush(Theme.SurfaceRaised))
        g.FillPath(trackFill, trackPath);

      var ratio = Math.Clamp(expiresSecs / 60f, 0.03f, 1f);
      using (var progPath = Theme.RoundedRect(new RectangleF(rx, barY, barW * ratio, barH), barH / 2f))
      using (var progFill = new SolidBrush(Theme.Accent))
        g.FillPath(progFill, progPath);
    }
    else
    {
      var noteRect = new Rectangle(rx, pinTop + Theme.Px(20), rw, Theme.Px(48));
      Theme.PaintCardFrame(g, noteRect, Theme.PxF(8f), Theme.SurfaceRaised, Theme.SurfaceRaised, Theme.Border, topSheen: false);
      var note = !running ? "PIN appears while sharing is active." : "PIN pairing is turned off in Phone setup. Scan the QR code to pair.";
      Theme.DrawText(g, note, Theme.Font(9f), new Rectangle(noteRect.X + Theme.Px(12), noteRect.Y, noteRect.Width - Theme.Px(24), noteRect.Height), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    // Browser address box
    var addrLabelY = addressBoxBounds.Y - Theme.Px(18);
    Theme.DrawText(g, "OPEN IN PHONE BROWSER", Theme.Font(8f, true), new Rectangle(rx, addrLabelY, rw, Theme.Px(16)), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.SingleLine);

    Theme.PaintCardFrame(g, addressBoxBounds, Theme.PxF(8f), Theme.SurfaceRaised, Theme.SurfaceRaised, Theme.BorderStrong, topSheen: false);
    var addrIconPx = Theme.PxF(15f);
    Icons.Draw(g, encrypted ? IconKind.Lock : IconKind.Globe, new RectangleF(addressBoxBounds.X + Theme.Px(12), addressBoxBounds.Y + (addressBoxBounds.Height - addrIconPx) / 2f, addrIconPx, addrIconPx), encrypted ? Theme.Good : Theme.Accent);
    var addrTextX = addressBoxBounds.X + Theme.Px(34);
    var addrTextW = Math.Max(40, copyBtn.Left - addrTextX - Theme.Px(8));
    var shownAddress = string.IsNullOrEmpty(address) ? "Unavailable while stopped" : address;
    Theme.DrawText(g, shownAddress, Theme.Mono(9f), new Rectangle(addrTextX, addressBoxBounds.Y, addrTextW, addressBoxBounds.Height), running ? Theme.Text : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing) { qrImage?.Dispose(); qrImage = null; }
    base.Dispose(disposing);
  }
}

/// 2x2 capability overview grid shown beneath the pairing hero on Connect.
internal sealed class CapabilitiesGrid : Control, ISizableBlock
{
  private static readonly (IconKind Icon, string Title, string Detail)[] Features =
  [
    (IconKind.Devices, "Multi-touch Trackpad", "Smooth pointer control, drag, scroll strip, and pinch zoom."),
    (IconKind.Key, "Full PC Keyboard", "Win, Ctrl, Alt shortcuts, sideways mode, and custom layouts."),
    (IconKind.Deck, "Shared Shortcut Deck", "Launch apps, media keys, text snippets, and automations."),
    (IconKind.Mic, "Offline Voice Typing", "Stream phone mic audio to local Windows speech recognition.")
  ];

  internal CapabilitiesGrid()
  {
    BackColor = Theme.Canvas;
    TabStop = false;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
  }

  public int GetHeightForWidth(int width)
  {
    var gap = Theme.Px(10);
    var cellW = (width - gap) / 2;
    var textW = Math.Max(Theme.Px(90), cellW - Theme.Px(14) * 2 - Theme.Px(32) - Theme.Px(10));
    var maxCellH = 0;
    foreach (var (_, title, detail) in Features)
    {
      var h = Theme.MeasureText(title, Theme.Font(9.5f, true), textW).Height + Theme.Px(3) + Theme.MeasureText(detail, Theme.Font(8.75f), textW).Height;
      maxCellH = Math.Max(maxCellH, Math.Max(Theme.Px(32), h) + Theme.Px(30));
    }
    return maxCellH * 2 + gap;
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(BackColor);
    if (Width <= 10 || Height <= 10) return;
    g.SmoothingMode = SmoothingMode.AntiAlias;

    var gap = Theme.Px(10);
    var cellW = (Width - gap) / 2;
    var cellH = (Height - gap) / 2;

    for (var i = 0; i < Features.Length; i++)
    {
      var col = i % 2;
      var row = i / 2;
      var rect = new Rectangle(col * (cellW + gap), row * (cellH + gap), cellW, cellH);
      Theme.PaintCardFrame(g, rect, Theme.PxF(11f), Theme.Surface, Theme.Surface, Theme.Border);

      var (icon, title, detail) = Features[i];
      var pad = Theme.Px(14);
      var badge = Theme.Px(32);
      var bx = rect.X + pad;
      var by = rect.Y + pad;
      using (var bPath = Theme.RoundedRect(new RectangleF(bx, by, badge, badge), Theme.PxF(8f)))
      using (var bFill = new SolidBrush(Theme.AccentSurface))
      using (var bPen = new Pen(Theme.AccentBorder, 1f))
      {
        g.FillPath(bFill, bPath);
        g.DrawPath(bPen, bPath);
      }
      var iconPx = Theme.PxF(15f);
      Icons.Draw(g, icon, new RectangleF(bx + (badge - iconPx) / 2f, by + (badge - iconPx) / 2f, iconPx, iconPx), Theme.Accent);

      var tx = bx + badge + Theme.Px(10);
      var tw = Math.Max(40, rect.Right - pad - tx);
      var tFont = Theme.Font(9.5f, true);
      var tSize = Theme.MeasureText(title, tFont, tw);
      Theme.DrawText(g, title, tFont, new Rectangle(tx, by, tw, tSize.Height + 2), Theme.Text);
      Theme.DrawText(g, detail, Theme.Font(8.75f), new Rectangle(tx, by + tSize.Height + Theme.Px(3), tw, rect.Bottom - pad - (by + tSize.Height)), Theme.Muted);
    }
  }
}

/// Interactive HTTPS certificate onboarding card with platform tabs, certificate QR code, and SHA-256 fingerprint copy.
internal sealed class CertificateSetupCard : Panel, ISizableBlock
{
  private readonly SegmentedControl platformSwitch;
  private readonly PalmButton exportBtn;
  private readonly PalmButton copyFpBtn;
  private Bitmap? certQr;
  private string certUrl = "";
  private string fingerprint = "";
  private int platform; // 0 = iPhone/iPad, 1 = Android
  private Rectangle qrBounds;
  private Rectangle stepsBounds;
  private Rectangle fpBoxBounds;
  private int dividerY;

  internal CertificateSetupCard(string? certDownloadUrl, string? sha256Fingerprint, Action onExportCert, Action onCopyFingerprint, Action onRelayout)
  {
    fingerprint = sha256Fingerprint ?? "";
    BackColor = Theme.Surface;
    ForeColor = Theme.Text;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

    platformSwitch = new SegmentedControl(
      [("iPhone & iPad", IconKind.Devices), ("Android", IconKind.Devices)],
      0,
      idx => { platform = idx; onRelayout(); Invalidate(); });
    Controls.Add(platformSwitch);

    exportBtn = new PalmButton("Export certificate (.crt)…", onExportCert, ButtonTone.Secondary, IconKind.Folder, compact: true)
    {
      BackColor = Theme.Surface
    };
    Controls.Add(exportBtn);

    copyFpBtn = new PalmButton("Copy", () =>
    {
      onCopyFingerprint();
      copyFpBtn?.Flash("Copied", IconKind.Check);
    }, ButtonTone.Secondary, IconKind.Copy, compact: true)
    {
      BackColor = Theme.SurfaceRaised
    };
    copyFpBtn.SetShown(!string.IsNullOrEmpty(fingerprint));
    Controls.Add(copyFpBtn);

    SetCertificateUrl(certDownloadUrl);
  }

  private void SetCertificateUrl(string? url)
  {
    if (string.IsNullOrEmpty(url) || certUrl == url) return;
    certUrl = url;
    using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
    using var code = new PngByteQRCode(data);
    using var stream = new MemoryStream(code.GetGraphic(12, [15, 17, 22], [255, 255, 255], false));
    using var loaded = Image.FromStream(stream);
    certQr?.Dispose();
    certQr = new Bitmap(loaded);
  }

  private string[] Steps()
  {
    var target = string.IsNullOrEmpty(certUrl) ? "the exported WinPalm.crt file" : certUrl;
    return platform == 0
      ? [
          $"Scan the certificate QR code (or open {target} in Safari) and tap Allow.",
          "Open Settings -> General -> VPN & Device Management, select WinPalm, and tap Install.",
          "Open Settings -> General -> About -> Certificate Trust Settings and enable full trust for WinPalm, then switch Connect to Encrypted (HTTPS)."
        ]
      : [
          $"Scan the certificate QR code (or open {target} in Chrome) to download WinPalm.crt.",
          "Open Settings -> Security -> More security settings -> Encryption & credentials -> Install a certificate -> CA certificate.",
          "Select WinPalm.crt, then switch Connect to Encrypted (HTTPS) and scan the pairing QR code. Note that some Android browsers restrict user CA trust."
        ];
  }

  public int GetHeightForWidth(int width)
  {
    var pad = Theme.Px(18);
    var innerW = Math.Max(Theme.Px(200), width - pad * 2);
    var y = pad;

    y += Theme.MeasureText("Encrypted HTTPS & Voice Typing", Theme.Font(11f, true), innerW).Height + Theme.Px(3);
    y += Theme.MeasureText("Browsers require trusted HTTPS on a local network before enabling the microphone or full web-app install. WinPalm's local CA is generated on this PC and covers only local addresses.", Theme.Font(9.25f), innerW).Height + Theme.Px(14);

    platformSwitch.SetBounds(pad, y, innerW, Theme.Px(36));
    y += Theme.Px(36) + Theme.Px(16);

    var qrSize = certQr is not null ? Theme.Px(134) : 0;
    var stepsX = certQr is not null ? pad + qrSize + Theme.Px(18) : pad;
    var stepsW = Math.Max(Theme.Px(160), width - pad - stepsX);
    qrBounds = certQr is not null ? new Rectangle(pad, y, qrSize, qrSize) : Rectangle.Empty;

    var stepsH = 0;
    var stepTextW = Math.Max(Theme.Px(120), stepsW - Theme.Px(28));
    foreach (var step in Steps())
      stepsH += Math.Max(Theme.Px(20), Theme.MeasureText(step, Theme.Font(9.25f), stepTextW).Height) + Theme.Px(10);

    stepsBounds = new Rectangle(stepsX, y, stepsW, stepsH);
    var middleBottom = Math.Max(certQr is not null ? qrBounds.Bottom + Theme.Px(20) : 0, y + stepsH);
    y = middleBottom + Theme.Px(8);
    dividerY = y;
    y += Theme.Px(14);

    if (!string.IsNullOrEmpty(fingerprint))
    {
      y += Theme.Px(18);
      var fpH = Theme.Px(38);
      fpBoxBounds = new Rectangle(pad, y, innerW, fpH);
      copyFpBtn.PerformAutoSize();
      copyFpBtn.Location = new Point(fpBoxBounds.Right - copyFpBtn.Width - Theme.Px(5), y + (fpH - copyFpBtn.Height) / 2);
      y += fpH + Theme.Px(12);
    }
    else
    {
      fpBoxBounds = Rectangle.Empty;
    }

    exportBtn.PerformAutoSize();
    exportBtn.Location = new Point(pad, y);
    y += exportBtn.Height + pad;
    return y;
  }

  protected override void OnResize(EventArgs eventargs)
  {
    base.OnResize(eventargs);
    if (platformSwitch is not null && Width > 0) _ = GetHeightForWidth(Width);
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(Parent?.BackColor ?? Theme.Canvas);
    if (Width <= 8 || Height <= 8) return;
    g.SmoothingMode = SmoothingMode.AntiAlias;

    Theme.PaintCardFrame(g, ClientRectangle, Theme.PxF(12f), Theme.Surface, Theme.Surface, Theme.Border);

    var pad = Theme.Px(18);
    var innerW = Width - pad * 2;
    var y = pad;
    var tFont = Theme.Font(11f, true);
    var tSize = Theme.MeasureText("Encrypted HTTPS & Voice Typing", tFont, innerW);
    Theme.DrawText(g, "Encrypted HTTPS & Voice Typing", tFont, new Rectangle(pad, y, innerW, tSize.Height + 2), Theme.Text);
    y += tSize.Height + Theme.Px(3);
    Theme.DrawText(g, "Browsers require trusted HTTPS on a local network before enabling the microphone or full web-app install. WinPalm's local CA is generated on this PC and covers only local addresses.", Theme.Font(9.25f), new Rectangle(pad, y, innerW, platformSwitch.Top - y), Theme.Muted);

    if (certQr is not null && qrBounds.Width > 0)
    {
      using (var qPath = Theme.RoundedRect(qrBounds, Theme.PxF(12f)))
      using (var white = new SolidBrush(Color.White))
        g.FillPath(white, qPath);

      var quiet = Theme.Px(10);
      var imgRect = new Rectangle(qrBounds.X + quiet, qrBounds.Y + quiet, qrBounds.Width - quiet * 2, qrBounds.Height - quiet * 2);
      var prev = g.InterpolationMode;
      g.InterpolationMode = InterpolationMode.NearestNeighbor;
      g.DrawImage(certQr, imgRect);
      g.InterpolationMode = prev;
      Theme.DrawText(g, "WinPalm.crt", Theme.Font(8f, true), new Rectangle(qrBounds.X, qrBounds.Bottom + Theme.Px(4), qrBounds.Width, Theme.Px(16)), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
    }

    var sy = stepsBounds.Y;
    var steps = Steps();
    var numSize = Theme.Px(20);
    var stepTextX = stepsBounds.X + numSize + Theme.Px(9);
    var stepTextW = Math.Max(60, stepsBounds.Right - stepTextX);
    for (var i = 0; i < steps.Length; i++)
    {
      using (var numBrush = new SolidBrush(Theme.AccentSurface))
      using (var numPen = new Pen(Theme.AccentBorder, 1f))
      {
        g.FillEllipse(numBrush, stepsBounds.X, sy, numSize, numSize);
        g.DrawEllipse(numPen, stepsBounds.X, sy, numSize, numSize);
      }
      Theme.DrawText(g, (i + 1).ToString(), Theme.Font(8.25f, true), new Rectangle(stepsBounds.X, sy, numSize, numSize), Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

      var sSize = Theme.MeasureText(steps[i], Theme.Font(9.25f), stepTextW);
      Theme.DrawText(g, steps[i], Theme.Font(9.25f), new Rectangle(stepTextX, sy + 1, stepTextW, sSize.Height + 4), Theme.Text);
      sy += Math.Max(numSize, sSize.Height) + Theme.Px(10);
    }

    using (var divPen = new Pen(Theme.Divider, 1f))
      g.DrawLine(divPen, pad, dividerY, Width - pad, dividerY);

    if (!fpBoxBounds.IsEmpty)
    {
      Theme.DrawText(g, "LOCAL CA SHA-256 FINGERPRINT", Theme.Font(8f, true), new Rectangle(pad, fpBoxBounds.Y - Theme.Px(18), innerW, Theme.Px(16)), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.SingleLine);
      Theme.PaintCardFrame(g, fpBoxBounds, Theme.PxF(8f), Theme.SurfaceRaised, Theme.SurfaceRaised, Theme.BorderStrong, topSheen: false);
      var fpTextW = Math.Max(40, copyFpBtn.Left - fpBoxBounds.X - Theme.Px(18));
      Theme.DrawText(g, fingerprint, Theme.Mono(8.5f), new Rectangle(fpBoxBounds.X + Theme.Px(12), fpBoxBounds.Y, fpTextW, fpBoxBounds.Height), Theme.AccentLight, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing) { certQr?.Dispose(); certQr = null; }
    base.Dispose(disposing);
  }
}
