using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;

namespace WinPalm.UI;

/// Tracks each child control's own intended visibility independently of
/// WinForms' Control.Visible getter, which returns false whenever an ancestor
/// Form is not yet shown or is hidden in the notification area.
internal static class ControlVisibility
{
  private static readonly ConditionalWeakTable<Control, VisibilityState> States = new();
  private sealed class VisibilityState { internal bool Hidden; }

  internal static void SetShown(this Control control, bool shown)
  {
    States.GetOrCreateValue(control).Hidden = !shown;
    control.Visible = shown;
  }

  internal static bool IsShown(this Control control) =>
    !States.TryGetValue(control, out var state) || !state.Hidden;
}

internal interface ISizableBlock
{
  int GetHeightForWidth(int width);
}

internal enum ButtonTone
{
  Secondary,
  Primary,
  Subtle,
  Danger
}

internal enum BadgeTone
{
  Neutral,
  Accent,
  Good,
  Warning,
  Danger
}

internal static class TonePalette
{
  internal static (Color Foreground, Color Surface, Color Border) Resolve(BadgeTone tone) => tone switch
  {
    BadgeTone.Accent => (Theme.Accent, Theme.AccentSurface, Theme.AccentBorder),
    BadgeTone.Good => (Theme.Good, Theme.GoodSurface, Theme.GoodBorder),
    BadgeTone.Warning => (Theme.Warning, Theme.WarningSurface, Theme.WarningBorder),
    BadgeTone.Danger => (Theme.Danger, Theme.DangerSurface, Theme.DangerBorder),
    _ => (Theme.Muted, Theme.SurfaceRaised, Theme.BorderStrong)
  };
}

/// Scrollable centered column page container.
internal sealed class PageScrollView : Panel
{
  private readonly List<(Control Control, int Gap)> blocks = [];
  private bool inLayout;

  internal PageScrollView()
  {
    Dock = DockStyle.Fill;
    AutoScroll = true;
    BackColor = Theme.Canvas;
    ForeColor = Theme.Text;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
  }

  internal void AddBlock(Control control, int gap = 16)
  {
    blocks.Add((control, gap));
    Controls.Add(control);
  }

  internal void ClearBlocks()
  {
    inLayout = true;
    SuspendLayout();
    try
    {
      AutoScrollPosition = Point.Empty;
      AutoScrollMinSize = Size.Empty;
      foreach (var (control, _) in blocks) control.Dispose();
      blocks.Clear();
      Controls.Clear();
    }
    finally
    {
      ResumeLayout(true);
      inLayout = false;
    }
  }

  internal void Relayout()
  {
    if (inLayout || IsDisposed || Width <= 0) return;
    inLayout = true;
    try
    {
      for (var pass = 0; pass < 2; pass++)
      {
        var viewW = ClientSize.Width;
        if (viewW <= 0) break;
        SuspendLayout();
        try
        {
          var padX = Theme.Px(34);
          var padTop = Theme.Px(26);
          var padBottom = Theme.Px(34);
          var colW = Math.Clamp(viewW - padX * 2, Theme.Px(460), Theme.Px(748));
          var scroll = AutoScrollPosition;
          var colX = Math.Max(padX, (viewW - colW) / 2) + scroll.X;
          var y = padTop;
          var lastGap = 0;
          foreach (var (control, gap) in blocks)
          {
            if (!control.IsShown()) continue;
            var h = control is ISizableBlock block ? block.GetHeightForWidth(colW) : control.Height;
            control.SetBounds(colX, y + scroll.Y, colW, h);
            lastGap = Theme.Px(gap);
            y += h + lastGap;
          }
          var contentHeight = Math.Max(0, y - lastGap + padBottom);
          AutoScrollMinSize = new Size(0, contentHeight);
        }
        finally
        {
          // AutoScrollMinSize queues a WinForms layout. Run it before checking
          // the viewport width, so native scrollbars and the next pass agree.
          ResumeLayout(true);
        }
        if (ClientSize.Width == viewW) break;
      }
    }
    finally
    {
      inLayout = false;
    }
    Invalidate();
  }

  protected override void OnResize(EventArgs eventargs)
  {
    base.OnResize(eventargs);
    Relayout();
  }
}

/// Page title, wrapped subtitle, optional status pill, and right-aligned quick actions.
internal sealed class PageHeader : Control, ISizableBlock
{
  private readonly string title;
  private readonly string subtitle;
  private readonly PalmButton[] actions;
  private string badgeText = "";
  private BadgeTone badgeTone = BadgeTone.Neutral;
  private int measuredTitleWidth;
  private int subtitleTop;

  internal PageHeader(string title, string subtitle, params PalmButton[] actions)
  {
    this.title = title;
    this.subtitle = subtitle;
    this.actions = actions;
    BackColor = Theme.Canvas;
    ForeColor = Theme.Text;
    TabStop = false;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    foreach (var action in actions) Controls.Add(action);
  }

  internal void SetBadge(string text, BadgeTone tone)
  {
    if (badgeText == text && badgeTone == tone) return;
    badgeText = text;
    badgeTone = tone;
    if (Width > 0) _ = GetHeightForWidth(Width);
    Invalidate();
  }

  public int GetHeightForWidth(int width)
  {
    var gap = Theme.Px(8);
    var actionsWidth = 0;
    foreach (var btn in actions)
    {
      if (!btn.IsShown()) continue;
      btn.PerformAutoSize();
      actionsWidth += btn.Width + gap;
    }
    if (actionsWidth > 0) actionsWidth -= gap;

    var pillH = string.IsNullOrEmpty(badgeText) ? 0 : Theme.Px(24) + Theme.Px(8);
    var inlineActions = actionsWidth > 0 && width - actionsWidth >= Theme.Px(280);
    var textW = inlineActions ? width - actionsWidth - Theme.Px(16) : width;
    measuredTitleWidth = Math.Max(Theme.Px(120), textW);

    var titleSize = Theme.MeasureText(title, Theme.Display(20f, true), measuredTitleWidth);
    var subSize = string.IsNullOrEmpty(subtitle) ? Size.Empty : Theme.MeasureText(subtitle, Theme.Font(9.75f), width);

    var y = pillH + titleSize.Height;
    if (!inlineActions && actionsWidth > 0)
    {
      var ax = 0;
      var ay = y + Theme.Px(10);
      var rowH = 0;
      foreach (var btn in actions)
      {
        if (!btn.IsShown()) continue;
        if (ax > 0 && ax + btn.Width > width) { ax = 0; ay += rowH + gap; rowH = 0; }
        btn.Location = new Point(ax, ay);
        ax += btn.Width + gap;
        rowH = Math.Max(rowH, btn.Height);
      }
      y = ay + rowH;
    }
    else if (inlineActions)
    {
      var ax = width - actionsWidth;
      var ay = pillH + Math.Max(0, (titleSize.Height - Theme.Px(34)) / 2);
      foreach (var btn in actions)
      {
        if (!btn.IsShown()) continue;
        btn.Location = new Point(ax, ay);
        ax += btn.Width + gap;
      }
    }

    subtitleTop = y + Theme.Px(6);
    if (subSize.Height > 0) y += Theme.Px(6) + subSize.Height;
    return y + Theme.Px(2);
  }

  protected override void OnResize(EventArgs e)
  {
    base.OnResize(e);
    if (actions is not null && Width > 0) _ = GetHeightForWidth(Width);
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(BackColor);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    if (measuredTitleWidth <= 0 && Width > 0) _ = GetHeightForWidth(Width);

    var y = 0;
    if (!string.IsNullOrEmpty(badgeText))
    {
      var (fg, bg, border) = TonePalette.Resolve(badgeTone);
      var badgeFont = Theme.Font(8.75f, true);
      var textSize = Theme.MeasureText(badgeText, badgeFont, Width, singleLine: true);
      var pillW = textSize.Width + Theme.Px(28);
      var pillH = Theme.Px(24);
      var pillRect = new RectangleF(0.5f, 0.5f, pillW, pillH);
      using (var path = Theme.RoundedRect(pillRect, pillH / 2f))
      using (var fill = new SolidBrush(bg))
      using (var pen = new Pen(border, 1f))
      {
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
      }
      var dotSize = Theme.PxF(7f);
      using (var dotBrush = new SolidBrush(fg))
        g.FillEllipse(dotBrush, Theme.PxF(10f), (pillH - dotSize) / 2f + 0.5f, dotSize, dotSize);
      Theme.DrawText(g, badgeText, badgeFont, new Rectangle(Theme.Px(22), 0, textSize.Width + Theme.Px(4), pillH + 1), fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
      y += pillH + Theme.Px(8);
    }

    var titleW = measuredTitleWidth > 0 ? measuredTitleWidth : Math.Max(Theme.Px(120), Width);
    var titleFont = Theme.Display(20f, true);
    var titleSize = Theme.MeasureText(title, titleFont, titleW);
    Theme.DrawText(g, title, titleFont, new Rectangle(0, y, titleW, titleSize.Height + Theme.Px(2)), Theme.Text);

    if (!string.IsNullOrEmpty(subtitle))
    {
      var subY = subtitleTop > 0 ? subtitleTop : y + titleSize.Height + Theme.Px(6);
      var subFont = Theme.Font(9.75f);
      Theme.DrawText(g, subtitle, subFont, new Rectangle(0, subY, Width, Math.Max(0, Height - subY)), Theme.Muted);
    }
  }
}

/// Custom rounded button with vector icon, hover/press states, focus ring, and flash confirmation.
internal sealed class PalmButton : Control
{
  private readonly Action onClick;
  private readonly bool compact;
  private string baseText;
  private IconKind baseIcon;
  private ButtonTone tone;
  private string? flashText;
  private IconKind flashIcon;
  private bool hovered, pressed;
  private System.Windows.Forms.Timer? flashTimer;

  internal PalmButton(string text, Action onClick, ButtonTone tone = ButtonTone.Secondary, IconKind icon = IconKind.None, bool compact = false)
  {
    baseText = text;
    baseIcon = icon;
    this.onClick = onClick;
    this.tone = tone;
    this.compact = compact;
    AccessibleName = text;
    AccessibleRole = AccessibleRole.PushButton;
    Cursor = Cursors.Hand;
    TabStop = true;
    BackColor = Theme.Surface;
    ForeColor = Theme.Text;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.StandardClick, true);
    PerformAutoSize();
  }

  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  internal string LabelText
  {
    get => baseText;
    set { if (baseText == value) return; baseText = value; AccessibleName = value; PerformAutoSize(); Invalidate(); }
  }

  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  internal IconKind Icon
  {
    get => baseIcon;
    set { if (baseIcon == value) return; baseIcon = value; PerformAutoSize(); Invalidate(); }
  }

  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  internal ButtonTone Tone
  {
    get => tone;
    set { if (tone == value) return; tone = value; Invalidate(); }
  }

  internal void PerformAutoSize()
  {
    var font = Theme.Font(compact ? 9.25f : 9.75f, tone == ButtonTone.Primary);
    var activeLabel = flashText ?? baseText;
    var normalW = MeasureLabelWidth(baseText, baseIcon, font);
    var activeW = MeasureLabelWidth(activeLabel, flashText is not null ? flashIcon : baseIcon, font);
    var h = Theme.Px(compact ? 32 : 36);
    var minW = string.IsNullOrEmpty(baseText) ? h : Theme.Px(compact ? 74 : 92);
    Size = new Size(Math.Max(minW, Math.Max(normalW, activeW)), h);
  }

  private int MeasureLabelWidth(string label, IconKind icon, Font font)
  {
    var padX = Theme.Px(compact ? 12 : 15);
    var iconW = icon == IconKind.None ? 0 : Theme.Px(15) + (string.IsNullOrEmpty(label) ? 0 : Theme.Px(7));
    var textW = string.IsNullOrEmpty(label) ? 0 : Theme.MeasureText(label, font, Theme.Px(800), singleLine: true).Width;
    return padX * 2 + iconW + textW + Theme.Px(2);
  }

  internal void Flash(string text, IconKind icon = IconKind.Check, int durationMs = 1500)
  {
    flashText = text;
    flashIcon = icon;
    PerformAutoSize();
    Invalidate();
    flashTimer?.Stop();
    flashTimer ??= new System.Windows.Forms.Timer();
    flashTimer.Interval = durationMs;
    flashTimer.Tick -= ClearFlash;
    flashTimer.Tick += ClearFlash;
    flashTimer.Start();
  }

  private void ClearFlash(object? sender, EventArgs e)
  {
    flashTimer?.Stop();
    if (flashTimer is not null) flashTimer.Tick -= ClearFlash;
    flashText = null;
    if (!IsDisposed) { PerformAutoSize(); Invalidate(); }
  }

  protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
  protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; pressed = false; Invalidate(); }
  protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left && Enabled) { pressed = true; Focus(); Invalidate(); } }
  protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (pressed) { pressed = false; Invalidate(); } }
  protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
  protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
  protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
  protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) onClick(); }

  protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);
  protected override void OnKeyDown(KeyEventArgs e)
  {
    base.OnKeyDown(e);
    if (Enabled && e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; onClick(); }
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(BackColor);
    if (Width <= 2 || Height <= 2) return;
    g.SmoothingMode = SmoothingMode.AntiAlias;

    var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
    var radius = Theme.PxF(8f);
    var isFlashing = flashText is not null;

    Color topFill, bottomFill, border, fg;
    if (isFlashing)
    {
      topFill = bottomFill = Theme.GoodSurface;
      border = Theme.GoodBorder;
      fg = Theme.Good;
    }
    else if (!Enabled)
    {
      topFill = bottomFill = Theme.Surface;
      border = Theme.Border;
      fg = Theme.Faint;
    }
    else
    {
      switch (tone)
      {
        case ButtonTone.Primary:
          topFill = pressed ? Theme.AccentDeep : hovered ? Theme.AccentLight : Theme.Blend(Theme.Accent, Theme.AccentLight, 0.35f);
          bottomFill = pressed ? Theme.AccentDeep : hovered ? Theme.Accent : Theme.AccentDeep;
          border = hovered ? Theme.AccentLight : Theme.Accent;
          fg = Theme.AccentInk;
          break;
        case ButtonTone.Danger:
          topFill = bottomFill = pressed ? Theme.Blend(Theme.DangerSurfaceHover, Theme.Danger, 0.2f) : hovered ? Theme.DangerSurfaceHover : Theme.DangerSurface;
          border = hovered ? Theme.Danger : Theme.DangerBorder;
          fg = Theme.Danger;
          break;
        case ButtonTone.Subtle:
          topFill = bottomFill = pressed ? Theme.SurfacePressed : hovered ? Theme.SurfaceRaised : BackColor;
          border = hovered || pressed ? Theme.Border : Color.Transparent;
          fg = hovered ? Theme.Text : Theme.Muted;
          break;
        default:
          topFill = pressed ? Theme.SurfacePressed : hovered ? Theme.SurfaceHover : Theme.SurfaceRaised;
          bottomFill = pressed ? Theme.SurfacePressed : hovered ? Theme.SurfaceHover : Theme.Blend(Theme.SurfaceRaised, Theme.Surface, 0.35f);
          border = hovered ? Theme.BorderStrong : Theme.Border;
          fg = Theme.Text;
          break;
      }
    }

    using (var path = Theme.RoundedRect(rect, radius))
    {
      if (topFill == bottomFill)
      {
        using var brush = new SolidBrush(topFill);
        g.FillPath(brush, path);
      }
      else
      {
        using var brush = new LinearGradientBrush(RectangleF.Inflate(rect, 1f, 1f), topFill, bottomFill, LinearGradientMode.Vertical)
        {
          WrapMode = WrapMode.TileFlipXY
        };
        g.FillPath(brush, path);
      }
      if (border.A > 0)
      {
        using var pen = new Pen(Focused && ShowFocusCues ? Theme.Accent : border, Focused && ShowFocusCues ? 1.5f : 1f);
        g.DrawPath(pen, path);
      }
    }

    var label = flashText ?? baseText;
    var activeIcon = isFlashing ? flashIcon : baseIcon;
    var font = Theme.Font(compact ? 9.25f : 9.75f, tone == ButtonTone.Primary || isFlashing);
    var iconPx = activeIcon == IconKind.None ? 0 : Theme.Px(15);
    var gap = activeIcon != IconKind.None && !string.IsNullOrEmpty(label) ? Theme.Px(7) : 0;
    var textSize = string.IsNullOrEmpty(label) ? Size.Empty : Theme.MeasureText(label, font, Width, singleLine: true);
    var contentW = iconPx + gap + textSize.Width;
    var startX = (Width - contentW) / 2;

    if (activeIcon != IconKind.None)
    {
      Icons.Draw(g, activeIcon, new RectangleF(startX, (Height - iconPx) / 2f, iconPx, iconPx), fg, tone == ButtonTone.Primary ? 2.1f : 1.85f);
      startX += iconPx + gap;
    }
    if (!string.IsNullOrEmpty(label))
    {
      Theme.DrawText(g, label, font, new Rectangle(startX, 0, textSize.Width + 4, Height), fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing && flashTimer is not null) { flashTimer.Stop(); flashTimer.Dispose(); flashTimer = null; }
    base.Dispose(disposing);
  }
}

/// Rounded surface card grouping settings rows with optional header and crisp 1px dividers.
internal sealed class SurfaceCard : Panel, ISizableBlock
{
  private readonly string? title;
  private readonly string? subtitle;
  private readonly Control? headerAction;
  private readonly List<(Control Control, bool ShowDividerAbove)> rows = [];
  private readonly List<int> dividerYs = [];

  internal SurfaceCard(string? title = null, string? subtitle = null, Control? headerAction = null)
  {
    this.title = title;
    this.subtitle = subtitle;
    this.headerAction = headerAction;
    BackColor = Theme.Surface;
    ForeColor = Theme.Text;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    if (headerAction is not null) Controls.Add(headerAction);
  }

  internal T AddRow<T>(T control, bool dividerAbove = true) where T : Control
  {
    control.BackColor = Theme.Surface;
    rows.Add((control, dividerAbove));
    Controls.Add(control);
    return control;
  }

  public int GetHeightForWidth(int width)
  {
    var padX = Theme.Px(18);
    var innerW = Math.Max(Theme.Px(160), width - padX * 2);
    var y = Theme.Px(14);
    dividerYs.Clear();

    if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(subtitle) || headerAction is not null)
    {
      var actionW = 0;
      if (headerAction is not null && headerAction.IsShown())
      {
        if (headerAction is PalmButton pb) pb.PerformAutoSize();
        actionW = headerAction.Width + Theme.Px(12);
      }
      var headerTextW = Math.Max(Theme.Px(120), innerW - actionW);
      var headerH = 0;
      if (!string.IsNullOrEmpty(title))
        headerH += Theme.MeasureText(title, Theme.Font(11f, true), headerTextW).Height;
      if (!string.IsNullOrEmpty(subtitle))
        headerH += (headerH > 0 ? Theme.Px(3) : 0) + Theme.MeasureText(subtitle, Theme.Font(9.25f), headerTextW).Height;
      if (headerAction is not null && headerAction.IsShown())
      {
        headerAction.Location = new Point(width - padX - headerAction.Width, y + Math.Max(0, (headerH - headerAction.Height) / 2));
        headerH = Math.Max(headerH, headerAction.Height);
      }
      y += headerH + Theme.Px(12);
      if (rows.Any(r => r.Control.IsShown()))
      {
        dividerYs.Add(y);
        y += Theme.Px(6);
      }
    }
    else
    {
      y = Theme.Px(8);
    }

    var visibleIndex = 0;
    foreach (var (control, showDivider) in rows)
    {
      if (!control.IsShown()) continue;
      if (visibleIndex > 0 && showDivider)
      {
        y += Theme.Px(4);
        dividerYs.Add(y);
        y += Theme.Px(5);
      }
      else if (visibleIndex > 0)
      {
        y += Theme.Px(6);
      }
      var rowH = control is ISizableBlock block ? block.GetHeightForWidth(innerW) : control.Height;
      control.SetBounds(padX, y, innerW, rowH);
      y += rowH;
      visibleIndex++;
    }

    return y + Theme.Px(visibleIndex > 0 ? 10 : 14);
  }

  protected override void OnResize(EventArgs eventargs)
  {
    base.OnResize(eventargs);
    if (rows is not null && Width > 0) _ = GetHeightForWidth(Width);
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(Parent?.BackColor ?? Theme.Canvas);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    Theme.PaintCardFrame(g, ClientRectangle, Theme.PxF(12f), Theme.Surface, Theme.Surface, Theme.Border);

    var padX = Theme.Px(18);
    if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(subtitle))
    {
      var actionW = headerAction is not null && headerAction.IsShown() ? headerAction.Width + Theme.Px(12) : 0;
      var textW = Math.Max(Theme.Px(120), Width - padX * 2 - actionW);
      var hy = Theme.Px(14);
      if (!string.IsNullOrEmpty(title))
      {
        var tFont = Theme.Font(11f, true);
        var tSize = Theme.MeasureText(title, tFont, textW);
        Theme.DrawText(g, title, tFont, new Rectangle(padX, hy, textW, tSize.Height + 2), Theme.Text);
        hy += tSize.Height + Theme.Px(3);
      }
      if (!string.IsNullOrEmpty(subtitle))
      {
        var sFont = Theme.Font(9.25f);
        var sSize = Theme.MeasureText(subtitle, sFont, textW);
        Theme.DrawText(g, subtitle, sFont, new Rectangle(padX, hy, textW, sSize.Height + 2), Theme.Muted);
      }
    }

    using var divPen = new Pen(Theme.Divider, 1f);
    foreach (var dy in dividerYs)
      g.DrawLine(divPen, padX, dy, Width - padX, dy);
  }
}

/// Tinted advisory or status banner with icon badge, wrapped copy, and optional action button.
internal sealed class BannerCard : Control, ISizableBlock
{
  private readonly IconKind icon;
  private readonly BadgeTone tone;
  private readonly string headline;
  private readonly string message;
  private readonly PalmButton? action;
  private int measuredTextWidth;

  internal BannerCard(IconKind icon, BadgeTone tone, string headline, string message, PalmButton? action = null)
  {
    this.icon = icon;
    this.tone = tone;
    this.headline = headline;
    this.message = message;
    this.action = action;
    BackColor = Theme.Canvas;
    TabStop = false;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    if (action is not null)
    {
      var (_, surface, _) = TonePalette.Resolve(tone);
      action.BackColor = surface;
      Controls.Add(action);
    }
  }

  public int GetHeightForWidth(int width)
  {
    var pad = Theme.Px(16);
    var badge = Theme.Px(34);
    var actionW = 0;
    var hasAction = action is not null && action.IsShown();
    if (hasAction)
    {
      action!.PerformAutoSize();
      actionW = action.Width + Theme.Px(14);
    }
    var sideBySide = hasAction && width - pad * 2 - badge - Theme.Px(12) - actionW >= Theme.Px(220);
    var textW = Math.Max(Theme.Px(140), width - pad * 2 - badge - Theme.Px(12) - (sideBySide ? actionW : 0));
    measuredTextWidth = textW;
    var hSize = string.IsNullOrEmpty(headline) ? Size.Empty : Theme.MeasureText(headline, Theme.Font(10f, true), textW);
    var mSize = Theme.MeasureText(message, Theme.Font(9.5f), textW);
    var textH = hSize.Height + (hSize.Height > 0 && mSize.Height > 0 ? Theme.Px(3) : 0) + mSize.Height;
    var contentH = Math.Max(badge, textH);

    if (hasAction)
    {
      if (sideBySide)
      {
        action!.Location = new Point(width - pad - action.Width, pad + Math.Max(0, (contentH - action.Height) / 2));
      }
      else
      {
        action!.Location = new Point(pad + badge + Theme.Px(12), pad + contentH + Theme.Px(10));
        contentH += Theme.Px(10) + action.Height;
      }
    }
    return pad * 2 + contentH;
  }

  protected override void OnResize(EventArgs e)
  {
    base.OnResize(e);
    if (Width > 0) _ = GetHeightForWidth(Width);
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(Parent?.BackColor ?? Theme.Canvas);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    var (fg, surface, border) = TonePalette.Resolve(tone);
    Theme.PaintCardFrame(g, ClientRectangle, Theme.PxF(12f), surface, surface, border, topSheen: false);

    var pad = Theme.Px(16);
    var badgeSize = Theme.Px(34);
    var badgeRect = new RectangleF(pad, pad, badgeSize, badgeSize);
    using (var path = Theme.RoundedRect(badgeRect, Theme.PxF(9f)))
    using (var fill = new SolidBrush(Color.FromArgb(36, fg)))
      g.FillPath(fill, path);
    var iconPx = Theme.PxF(18f);
    Icons.Draw(g, icon, new RectangleF(pad + (badgeSize - iconPx) / 2f, pad + (badgeSize - iconPx) / 2f, iconPx, iconPx), fg);

    var textX = pad + badgeSize + Theme.Px(12);
    var textW = measuredTextWidth > 0 ? measuredTextWidth : Math.Max(Theme.Px(100), Width - pad - textX);
    var y = pad;
    if (!string.IsNullOrEmpty(headline))
    {
      var hFont = Theme.Font(10f, true);
      var hSize = Theme.MeasureText(headline, hFont, textW);
      Theme.DrawText(g, headline, hFont, new Rectangle(textX, y, textW, hSize.Height + 2), fg);
      y += hSize.Height + Theme.Px(3);
    }
    Theme.DrawText(g, message, Theme.Font(9.5f), new Rectangle(textX, y, textW, Height - y - pad + 4), Theme.Text);
  }
}

/// Animated pill switch row with icon badge, title, and multi-line description.
internal sealed class SettingToggleRow : Control, ISizableBlock
{
  private readonly string title;
  private readonly string description;
  private readonly IconKind icon;
  private readonly BadgeTone iconTone;
  private readonly Action<bool> onChanged;
  private bool isChecked;
  private float thumbProgress;
  private bool hovered;
  private System.Windows.Forms.Timer? animTimer;

  internal SettingToggleRow(string title, string description, bool initial, Action<bool> onChanged, IconKind icon = IconKind.None, BadgeTone iconTone = BadgeTone.Accent)
  {
    this.title = title;
    this.description = description;
    this.icon = icon;
    this.iconTone = iconTone;
    this.onChanged = onChanged;
    isChecked = initial;
    thumbProgress = initial ? 1f : 0f;
    AccessibleName = title;
    AccessibleRole = AccessibleRole.CheckButton;
    Cursor = Cursors.Hand;
    TabStop = true;
    BackColor = Theme.Surface;
    ForeColor = Theme.Text;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.StandardClick, true);
  }

  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  internal bool Checked => isChecked;

  internal void SetChecked(bool value, bool notify = false)
  {
    if (isChecked == value) return;
    isChecked = value;
    StartAnimation();
    if (notify) onChanged(isChecked);
  }

  private void StartAnimation()
  {
    animTimer ??= new System.Windows.Forms.Timer { Interval = 16 };
    animTimer.Tick -= StepAnimation;
    animTimer.Tick += StepAnimation;
    animTimer.Start();
    Invalidate();
  }

  private void StepAnimation(object? sender, EventArgs e)
  {
    var target = isChecked ? 1f : 0f;
    var delta = target - thumbProgress;
    if (Math.Abs(delta) < 0.08f)
    {
      thumbProgress = target;
      animTimer?.Stop();
    }
    else
    {
      thumbProgress += Math.Sign(delta) * 0.24f;
    }
    if (!IsDisposed) Invalidate();
  }

  public int GetHeightForWidth(int width)
  {
    var lead = icon == IconKind.None ? 0 : Theme.Px(34) + Theme.Px(12);
    var swX = width - Theme.Px(46);
    var textW = Math.Max(Theme.Px(120), swX - lead - Theme.Px(16));
    var tSize = Theme.MeasureText(title, Theme.Font(10f, true), textW);
    var dSize = string.IsNullOrEmpty(description) ? Size.Empty : Theme.MeasureText(description, Theme.Font(9.25f), textW);
    var textH = tSize.Height + (dSize.Height > 0 ? Theme.Px(3) + dSize.Height : 0);
    return Math.Max(Theme.Px(40), textH + Theme.Px(14));
  }

  protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
  protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
  protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
  protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
  protected override void OnClick(EventArgs e)
  {
    base.OnClick(e);
    if (!Enabled) return;
    Focus();
    SetChecked(!isChecked, notify: true);
  }

  protected override bool IsInputKey(Keys keyData) => keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
  protected override void OnKeyDown(KeyEventArgs e)
  {
    base.OnKeyDown(e);
    if (Enabled && e.KeyCode is Keys.Space or Keys.Enter) { e.Handled = true; SetChecked(!isChecked, notify: true); }
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(BackColor);
    g.SmoothingMode = SmoothingMode.AntiAlias;

    if (hovered && Enabled)
    {
      using var hoverPath = Theme.RoundedRect(new RectangleF(0, 1, Width, Height - 2), Theme.PxF(8f));
      using var hoverBrush = new SolidBrush(Color.FromArgb(14, 255, 255, 255));
      g.FillPath(hoverBrush, hoverPath);
    }

    var x = 0;
    var padY = Theme.Px(7);
    if (icon != IconKind.None)
    {
      var badge = Theme.Px(34);
      var (fg, bg, border) = TonePalette.Resolve(iconTone);
      var badgeRect = new RectangleF(0, padY, badge, badge);
      using (var path = Theme.RoundedRect(badgeRect, Theme.PxF(9f)))
      using (var fill = new SolidBrush(bg))
      using (var pen = new Pen(border, 1f))
      {
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
      }
      var iconPx = Theme.PxF(17f);
      Icons.Draw(g, icon, new RectangleF((badge - iconPx) / 2f, padY + (badge - iconPx) / 2f, iconPx, iconPx), fg);
      x += badge + Theme.Px(12);
    }

    var swW = Theme.PxF(44f);
    var swH = Theme.PxF(24f);
    var swX = (float)(Width - Theme.Px(46));
    var swY = string.IsNullOrEmpty(description) ? (Height - swH) / 2f : Math.Max(padY + 2f, (Height - swH) / 2f);

    var textW = Math.Max(Theme.Px(120), (int)swX - x - Theme.Px(16));
    var tFont = Theme.Font(10f, true);
    var tSize = Theme.MeasureText(title, tFont, textW);
    var ty = string.IsNullOrEmpty(description) ? (Height - tSize.Height) / 2 : padY;
    Theme.DrawText(g, title, tFont, new Rectangle(x, ty, textW, tSize.Height + 2), Enabled ? Theme.Text : Theme.Muted);

    if (!string.IsNullOrEmpty(description))
    {
      var dy = ty + tSize.Height + Theme.Px(3);
      Theme.DrawText(g, description, Theme.Font(9.25f), new Rectangle(x, dy, textW, Height - dy), Theme.Muted);
    }

    var trackColor = Theme.Blend(Theme.SurfaceRaised, Theme.Accent, thumbProgress);
    var borderColor = Focused && ShowFocusCues ? Theme.AccentLight : Theme.Blend(hovered ? Theme.BorderStrong : Theme.Border, Theme.Accent, thumbProgress);
    var trackRect = new RectangleF(swX, swY, swW, swH);
    using (var trackPath = Theme.RoundedRect(trackRect, swH / 2f))
    using (var trackFill = new SolidBrush(trackColor))
    using (var trackPen = new Pen(borderColor, 1.2f))
    {
      g.FillPath(trackFill, trackPath);
      g.DrawPath(trackPen, trackPath);
    }

    var inset = Theme.PxF(3f);
    var knob = swH - inset * 2f;
    var knobX = swX + inset + (swW - swH) * thumbProgress;
    var knobColor = Theme.Blend(Theme.Text, Theme.AccentInk, thumbProgress);
    using (var knobBrush = new SolidBrush(knobColor))
      g.FillEllipse(knobBrush, knobX, swY + inset, knob, knob);
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing && animTimer is not null) { animTimer.Stop(); animTimer.Dispose(); animTimer = null; }
    base.Dispose(disposing);
  }
}

/// Structured row with optional icon badge, title, status pill, wrapped description, code preview, and right-side controls.
internal sealed class SettingActionRow : Control, ISizableBlock
{
  private string title;
  private string? subtitle;
  private readonly string? codePreview;
  private string? pillText;
  private BadgeTone pillTone;
  private readonly IconKind icon;
  private readonly BadgeTone iconTone;
  private readonly Control[] actions;

  internal SettingActionRow(
    string title,
    string? subtitle = null,
    IconKind icon = IconKind.None,
    BadgeTone iconTone = BadgeTone.Neutral,
    string? pillText = null,
    BadgeTone pillTone = BadgeTone.Neutral,
    string? codePreview = null,
    params Control[] actions)
  {
    this.title = title;
    this.subtitle = subtitle;
    this.icon = icon;
    this.iconTone = iconTone;
    this.pillText = pillText;
    this.pillTone = pillTone;
    this.codePreview = codePreview;
    this.actions = actions;
    BackColor = Theme.Surface;
    ForeColor = Theme.Text;
    TabStop = false;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    foreach (var action in actions) Controls.Add(action);
  }

  internal void UpdateCopy(string newTitle, string? newSubtitle = null, string? newPill = null, BadgeTone newPillTone = BadgeTone.Neutral)
  {
    title = newTitle;
    subtitle = newSubtitle;
    pillText = newPill;
    pillTone = newPillTone;
    Invalidate();
  }

  private readonly record struct RowMetrics(
    int Lead,
    int TextWidth,
    HeaderTextLayout Header,
    Size SubtitleSize,
    int LeftBlockHeight,
    bool InlineActions,
    int ActionsWidth,
    int MaxActionHeight);

  private RowMetrics ComputeMetrics(int width)
  {
    var lead = icon == IconKind.None ? 0 : Theme.Px(34) + Theme.Px(12);
    var gap = Theme.Px(8);
    var actionsW = 0;
    var maxActionH = 0;
    foreach (var ctrl in actions)
    {
      if (!ctrl.IsShown()) continue;
      if (ctrl is PalmButton pb) pb.PerformAutoSize();
      actionsW += ctrl.Width + gap;
      maxActionH = Math.Max(maxActionH, ctrl.Height);
    }
    if (actionsW > 0) actionsW -= gap;

    var inline = actionsW > 0 && width - lead - actionsW - Theme.Px(16) >= Theme.Px(190);
    var textW = Math.Max(Theme.Px(120), width - lead - (inline ? actionsW + Theme.Px(16) : 0));

    var tFont = Theme.Font(10f, true);
    var pFont = Theme.Font(8.25f, true);
    var pillH = Theme.Px(20);
    var pillW = string.IsNullOrEmpty(pillText)
      ? 0
      : Theme.MeasureText(pillText, pFont, Theme.Px(400), singleLine: true).Width + Theme.Px(16);

    var header = LayoutMath.MeasureHeader(
      textW,
      pillW,
      pillH,
      Theme.Px(8),
      Theme.Px(4),
      Theme.Px(90),
      maxW =>
      {
        var sz = Theme.MeasureText(title, tFont, maxW);
        return (sz.Width, sz.Height);
      });

    var subSize = string.IsNullOrEmpty(subtitle) ? Size.Empty : Theme.MeasureText(subtitle, Theme.Font(9.25f), textW);
    var textH = header.HeaderHeight;
    if (subSize.Height > 0)
      textH += Theme.Px(3) + subSize.Height;
    if (!string.IsNullOrEmpty(codePreview))
      textH += Theme.Px(5) + Theme.Px(24);

    var leftBlockH = Math.Max(icon == IconKind.None ? 0 : Theme.Px(34), textH);
    return new RowMetrics(lead, textW, header, subSize, leftBlockH, inline, actionsW, maxActionH);
  }

  public int GetHeightForWidth(int width)
  {
    var padY = Theme.Px(7);
    var gap = Theme.Px(8);
    var m = ComputeMetrics(width);

    if (m.InlineActions)
    {
      var rowH = Math.Max(m.LeftBlockHeight, m.MaxActionHeight);
      var ax = width - m.ActionsWidth;
      var ay = padY + (rowH - m.MaxActionHeight) / 2;
      foreach (var ctrl in actions)
      {
        if (!ctrl.IsShown()) continue;
        ctrl.Location = new Point(ax, ay + (m.MaxActionHeight - ctrl.Height) / 2);
        ax += ctrl.Width + gap;
      }
      return rowH + padY * 2;
    }

    if (m.ActionsWidth > 0)
    {
      var ax = m.Lead;
      var ay = padY + m.LeftBlockHeight + Theme.Px(10);
      var lineH = 0;
      foreach (var ctrl in actions)
      {
        if (!ctrl.IsShown()) continue;
        if (ax > m.Lead && ax + ctrl.Width > width) { ax = m.Lead; ay += lineH + gap; lineH = 0; }
        ctrl.Location = new Point(ax, ay);
        ax += ctrl.Width + gap;
        lineH = Math.Max(lineH, ctrl.Height);
      }
      return ay + lineH + padY;
    }

    return m.LeftBlockHeight + padY * 2;
  }

  protected override void OnResize(EventArgs e)
  {
    base.OnResize(e);
    if (actions is not null && Width > 0) _ = GetHeightForWidth(Width);
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(BackColor);
    if (Width <= 4 || Height <= 4) return;
    g.SmoothingMode = SmoothingMode.AntiAlias;

    var padY = Theme.Px(7);
    var m = ComputeMetrics(Width);
    var x = m.Lead;
    if (icon != IconKind.None)
    {
      var badge = Theme.Px(34);
      var (fg, bg, border) = TonePalette.Resolve(iconTone);
      var badgeRect = new RectangleF(0, padY, badge, badge);
      using (var path = Theme.RoundedRect(badgeRect, Theme.PxF(9f)))
      using (var fill = new SolidBrush(bg))
      using (var pen = new Pen(border, 1f))
      {
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
      }
      var iconPx = Theme.PxF(17f);
      Icons.Draw(g, icon, new RectangleF((badge - iconPx) / 2f, padY + (badge - iconPx) / 2f, iconPx, iconPx), fg);
    }

    var tFont = Theme.Font(10f, true);
    var pFont = Theme.Font(8.25f, true);
    var header = m.Header;
    var singleRowCentered = string.IsNullOrEmpty(subtitle) && string.IsNullOrEmpty(codePreview);
    var y = singleRowCentered ? Math.Max(padY, (Height - header.HeaderHeight) / 2) : padY;

    Theme.DrawText(g, title, tFont, new Rectangle(x, y, header.TitleMaxWidth, header.TitleHeight + 2), Theme.Text);

    if (!string.IsNullOrEmpty(pillText) && header.PillWidth > 0)
    {
      var (pFg, pBg, pBorder) = TonePalette.Resolve(pillTone);
      var ph = Theme.Px(20);
      int px, py;
      if (header.PillBelowTitle)
      {
        px = x;
        py = y + header.TitleHeight + Theme.Px(4);
      }
      else
      {
        var singleLineW = Math.Min(header.TitleMaxWidth, Theme.MeasureText(title, tFont, header.TitleMaxWidth, singleLine: true).Width);
        px = x + singleLineW + Theme.Px(8);
        var firstLineH = Math.Min(header.TitleHeight, tFont.Height);
        py = y + Math.Max(0, (firstLineH - ph) / 2);
      }
      using (var pPath = Theme.RoundedRect(new RectangleF(px, py, header.PillWidth, ph), ph / 2f))
      using (var pFill = new SolidBrush(pBg))
      using (var pPen = new Pen(pBorder, 1f))
      {
        g.FillPath(pFill, pPath);
        g.DrawPath(pPen, pPath);
      }
      Theme.DrawText(g, pillText, pFont, new Rectangle(px, py, header.PillWidth, ph + 1), pFg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    y += header.HeaderHeight;
    if (!string.IsNullOrEmpty(subtitle))
    {
      y += Theme.Px(3);
      var sFont = Theme.Font(9.25f);
      Theme.DrawText(g, subtitle, sFont, new Rectangle(x, y, m.TextWidth, m.SubtitleSize.Height + 2), Theme.Muted);
      y += m.SubtitleSize.Height;
    }

    if (!string.IsNullOrEmpty(codePreview))
    {
      y += Theme.Px(5);
      var codeH = Theme.Px(24);
      var codeRect = new RectangleF(x, y, m.TextWidth, codeH);
      using (var cPath = Theme.RoundedRect(codeRect, Theme.PxF(6f)))
      using (var cFill = new SolidBrush(Theme.SurfaceRaised))
      using (var cPen = new Pen(Theme.Border, 1f))
      {
        g.FillPath(cFill, cPath);
        g.DrawPath(cPen, cPath);
      }
      var clean = codePreview.Replace('\r', ' ').Replace('\n', ' ').Trim();
      Theme.DrawText(g, clean, Theme.Mono(8.75f), new Rectangle(x + Theme.Px(9), y, m.TextWidth - Theme.Px(18), codeH), Theme.AccentLight, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
  }
}

/// Multi-option pill segmented control with optional icons and keyboard navigation.
internal sealed class SegmentedControl : Control, ISizableBlock
{
  private readonly (string Label, IconKind Icon)[] items;
  private readonly Action<int> onChanged;
  private int selectedIndex;
  private int hoveredIndex = -1;

  internal SegmentedControl((string Label, IconKind Icon)[] items, int initialIndex, Action<int> onChanged)
  {
    this.items = items;
    selectedIndex = Math.Clamp(initialIndex, 0, Math.Max(0, items.Length - 1));
    this.onChanged = onChanged;
    AccessibleRole = AccessibleRole.PageTabList;
    Cursor = Cursors.Hand;
    TabStop = true;
    Height = Theme.Px(36);
    BackColor = Theme.Surface;
    ForeColor = Theme.Text;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
  }

  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  internal int SelectedIndex
  {
    get => selectedIndex;
    set
    {
      var clamped = Math.Clamp(value, 0, Math.Max(0, items.Length - 1));
      if (selectedIndex == clamped) return;
      selectedIndex = clamped;
      Invalidate();
    }
  }

  public int GetHeightForWidth(int width) => Theme.Px(36);

  private int HitSegment(int x)
  {
    if (items.Length == 0 || Width <= 8) return -1;
    var pad = Theme.Px(3);
    var segW = (Width - pad * 2f) / items.Length;
    var idx = (int)((x - pad) / segW);
    return idx >= 0 && idx < items.Length ? idx : -1;
  }

  protected override void OnMouseMove(MouseEventArgs e)
  {
    base.OnMouseMove(e);
    var idx = HitSegment(e.X);
    if (idx != hoveredIndex) { hoveredIndex = idx; Invalidate(); }
  }

  protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoveredIndex = -1; Invalidate(); }

  protected override void OnMouseDown(MouseEventArgs e)
  {
    base.OnMouseDown(e);
    if (e.Button != MouseButtons.Left || !Enabled) return;
    Focus();
    var idx = HitSegment(e.X);
    if (idx >= 0 && idx != selectedIndex) { selectedIndex = idx; Invalidate(); onChanged(selectedIndex); }
  }

  protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);
  protected override void OnKeyDown(KeyEventArgs e)
  {
    base.OnKeyDown(e);
    if (!Enabled || items.Length <= 1) return;
    if (e.KeyCode == Keys.Left && selectedIndex > 0) { selectedIndex--; Invalidate(); onChanged(selectedIndex); e.Handled = true; }
    else if (e.KeyCode == Keys.Right && selectedIndex < items.Length - 1) { selectedIndex++; Invalidate(); onChanged(selectedIndex); e.Handled = true; }
  }

  protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
  protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(BackColor);
    if (Width <= 4 || Height <= 4 || items.Length == 0) return;
    g.SmoothingMode = SmoothingMode.AntiAlias;

    var outer = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
    using (var path = Theme.RoundedRect(outer, Theme.PxF(9f)))
    using (var fill = new SolidBrush(Theme.SurfaceRaised))
    using (var pen = new Pen(Focused && ShowFocusCues ? Theme.Accent : Theme.Border, 1f))
    {
      g.FillPath(fill, path);
      g.DrawPath(pen, path);
    }

    var pad = Theme.PxF(3f);
    var segW = (Width - pad * 2f) / items.Length;
    var segH = Height - pad * 2f;

    for (var i = 0; i < items.Length; i++)
    {
      var sx = pad + i * segW;
      var segRect = new RectangleF(sx + 0.5f, pad + 0.5f, segW - 1f, segH - 1f);
      var active = i == selectedIndex;
      var hover = i == hoveredIndex && !active;

      if (active || hover)
      {
        using var sPath = Theme.RoundedRect(segRect, Theme.PxF(6.5f));
        using var sFill = new SolidBrush(active ? Theme.AccentSurface : Theme.SurfaceHover);
        g.FillPath(sFill, sPath);
        if (active)
        {
          using var sPen = new Pen(Theme.AccentBorder, 1f);
          g.DrawPath(sPen, sPath);
        }
      }

      var (label, icon) = items[i];
      var font = Theme.Font(9.25f, active);
      var fg = active ? Theme.Text : hover ? Theme.Text : Theme.Muted;
      var iconColor = active ? Theme.Accent : fg;
      var iconPx = icon == IconKind.None ? 0 : Theme.Px(14);
      var gap = icon == IconKind.None ? 0 : Theme.Px(6);
      var tSize = Theme.MeasureText(label, font, (int)segW, singleLine: true);
      var contentW = iconPx + gap + tSize.Width;
      var cx = (int)(sx + (segW - contentW) / 2f);

      if (icon != IconKind.None)
      {
        Icons.Draw(g, icon, new RectangleF(cx, (Height - iconPx) / 2f, iconPx, iconPx), iconColor);
        cx += iconPx + gap;
      }
      Theme.DrawText(g, label, font, new Rectangle(cx, 0, tSize.Width + 4, Height), fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
  }
}

/// Rounded dark input container with focus glow, optional label/hint, and optional trailing action button.
internal sealed class StyledTextBox : Panel, ISizableBlock
{
  private readonly TextBox inner;
  private readonly string? label;
  private string? hint;
  private readonly bool multiline;
  private readonly int boxHeightPx;
  private readonly PalmButton? trailingButton;
  private Rectangle boxBounds;

  internal StyledTextBox(
    string initial,
    bool multiline = false,
    bool readOnly = false,
    bool monospace = false,
    int multilineHeight = 140,
    string? label = null,
    string? hint = null,
    string? placeholder = null,
    PalmButton? trailingButton = null)
  {
    this.multiline = multiline;
    this.label = label;
    this.hint = hint;
    this.trailingButton = trailingButton;
    boxHeightPx = multiline ? multilineHeight : 38;
    BackColor = Theme.Surface;
    ForeColor = Theme.Text;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

    inner = new TextBox
    {
      Text = initial,
      Multiline = multiline,
      AcceptsReturn = multiline,
      ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
      BorderStyle = BorderStyle.None,
      Font = monospace ? Theme.Mono(multiline ? 9.5f : 9.75f) : Theme.Font(9.75f),
      ReadOnly = readOnly,
      BackColor = Theme.SurfaceRaised,
      ForeColor = readOnly && monospace ? Theme.AccentLight : Theme.Text
    };
    if (!string.IsNullOrEmpty(placeholder)) inner.PlaceholderText = placeholder;
    inner.GotFocus += (_, _) => Invalidate();
    inner.LostFocus += (_, _) => Invalidate();
    Controls.Add(inner);
    if (trailingButton is not null) Controls.Add(trailingButton);
    Click += (_, _) => inner.Focus();
  }

  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  internal string Value
  {
    get => inner.Text;
    set => inner.Text = value;
  }

  internal TextBox Inner => inner;

  internal void SetHint(string? newHint)
  {
    if (hint == newHint) return;
    hint = newHint;
    Invalidate();
  }

  public int GetHeightForWidth(int width)
  {
    var y = Theme.Px(4);
    if (!string.IsNullOrEmpty(label))
      y += Theme.MeasureText(label, Theme.Font(9.5f, true), width).Height + Theme.Px(6);

    var btnW = 0;
    if (trailingButton is not null)
    {
      trailingButton.PerformAutoSize();
      btnW = trailingButton.Width + Theme.Px(8);
    }

    var boxW = Math.Max(Theme.Px(120), width - btnW);
    var boxH = Theme.Px(boxHeightPx);
    boxBounds = new Rectangle(0, y, boxW, boxH);

    var padX = Theme.Px(12);
    if (multiline)
    {
      var padY = Theme.Px(9);
      inner.SetBounds(padX, y + padY, Math.Max(40, boxW - padX * 2), Math.Max(24, boxH - padY * 2));
    }
    else
    {
      var textH = inner.PreferredHeight;
      inner.SetBounds(padX, y + Math.Max(4, (boxH - textH) / 2), Math.Max(40, boxW - padX * 2), textH);
    }

    if (trailingButton is not null)
      trailingButton.Location = new Point(width - trailingButton.Width, y + (boxH - trailingButton.Height) / 2);

    y += boxH;
    if (!string.IsNullOrEmpty(hint))
      y += Theme.Px(5) + Theme.MeasureText(hint, Theme.Font(8.75f), width).Height;
    return y + Theme.Px(4);
  }

  protected override void OnResize(EventArgs eventargs)
  {
    base.OnResize(eventargs);
    if (inner is not null && Width > 0) _ = GetHeightForWidth(Width);
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(BackColor);
    g.SmoothingMode = SmoothingMode.AntiAlias;

    if (!string.IsNullOrEmpty(label))
      Theme.DrawText(g, label, Theme.Font(9.5f, true), new Rectangle(0, Theme.Px(4), Width, boxBounds.Top), Theme.Text);

    if (boxBounds.Width > 2 && boxBounds.Height > 2)
    {
      var rect = new RectangleF(boxBounds.X + 0.5f, boxBounds.Y + 0.5f, boxBounds.Width - 1f, boxBounds.Height - 1f);
      using var path = Theme.RoundedRect(rect, Theme.PxF(8f));
      using var fill = new SolidBrush(Theme.SurfaceRaised);
      using var pen = new Pen(inner.Focused ? Theme.Accent : Theme.BorderStrong, inner.Focused ? 1.4f : 1f);
      g.FillPath(fill, path);
      g.DrawPath(pen, path);
    }

    if (!string.IsNullOrEmpty(hint))
    {
      var hy = boxBounds.Bottom + Theme.Px(5);
      Theme.DrawText(g, hint, Theme.Font(8.75f), new Rectangle(0, hy, Width, Height - hy), Theme.Muted);
    }
  }
}

/// Rounded numeric stepper replacing classic white-bordered NumericUpDown.
internal sealed class NumericStepper : Panel
{
  private readonly TextBox input;
  private readonly int min, max;
  private int hoverBtn; // 0 none, 1 minus, 2 plus

  internal NumericStepper(int value, int min, int max)
  {
    this.min = min;
    this.max = max;
    BackColor = Theme.Surface;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

    input = new TextBox
    {
      Text = Math.Clamp(value, min, max).ToString(),
      BorderStyle = BorderStyle.None,
      Font = Theme.Mono(10f, true),
      BackColor = Theme.SurfaceRaised,
      ForeColor = Theme.Text,
      TextAlign = HorizontalAlignment.Center
    };
    input.GotFocus += (_, _) => Invalidate();
    input.LostFocus += (_, _) => { Value = Value; Invalidate(); };
    input.KeyDown += (_, e) =>
    {
      if (e.KeyCode == Keys.Up) { Value += 1; e.Handled = true; }
      else if (e.KeyCode == Keys.Down) { Value -= 1; e.Handled = true; }
    };
    Controls.Add(input);
    Size = Theme.Px(148, 36);
    PositionInput();
  }

  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  internal int Value
  {
    get => int.TryParse(input.Text.Trim(), out var parsed) ? Math.Clamp(parsed, min, max) : min;
    set => input.Text = Math.Clamp(value, min, max).ToString();
  }

  private void PositionInput()
  {
    if (input is null) return;
    var btnArea = Theme.Px(64);
    var h = input.PreferredHeight;
    input.SetBounds(Theme.Px(8), Math.Max(4, (Height - h) / 2), Math.Max(36, Width - btnArea - Theme.Px(12)), h);
  }

  protected override void OnResize(EventArgs eventargs) { base.OnResize(eventargs); PositionInput(); }

  private int HitButton(Point pt)
  {
    var btnW = Theme.Px(31);
    if (pt.X >= Width - btnW) return 2;
    if (pt.X >= Width - btnW * 2) return 1;
    return 0;
  }

  protected override void OnMouseMove(MouseEventArgs e)
  {
    base.OnMouseMove(e);
    var hit = HitButton(e.Location);
    Cursor = hit > 0 ? Cursors.Hand : Cursors.IBeam;
    if (hit != hoverBtn) { hoverBtn = hit; Invalidate(); }
  }

  protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverBtn = 0; Invalidate(); }

  protected override void OnMouseDown(MouseEventArgs e)
  {
    base.OnMouseDown(e);
    if (e.Button != MouseButtons.Left) return;
    var hit = HitButton(e.Location);
    if (hit == 1) Value -= 1;
    else if (hit == 2) Value += 1;
    else input.Focus();
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(BackColor);
    g.SmoothingMode = SmoothingMode.AntiAlias;

    var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
    using (var path = Theme.RoundedRect(rect, Theme.PxF(8f)))
    using (var fill = new SolidBrush(Theme.SurfaceRaised))
    using (var pen = new Pen(input.Focused ? Theme.Accent : Theme.BorderStrong, input.Focused ? 1.4f : 1f))
    {
      g.FillPath(fill, path);
      g.DrawPath(pen, path);
    }

    var btnW = Theme.Px(31);
    var minusX = Width - btnW * 2;
    var plusX = Width - btnW;

    if (hoverBtn > 0)
    {
      var hx = hoverBtn == 1 ? minusX : plusX;
      using var hBrush = new SolidBrush(Theme.SurfaceHover);
      g.FillRectangle(hBrush, hx + 1, 2, btnW - 2, Height - 4);
    }

    using var divPen = new Pen(Theme.Border, 1f);
    g.DrawLine(divPen, minusX, 5, minusX, Height - 5);
    g.DrawLine(divPen, plusX, 5, plusX, Height - 5);

    using var glyphPen = new Pen(Theme.Text, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
    var cy = Height / 2f;
    var r = Theme.PxF(4.5f);
    var mcx = minusX + btnW / 2f;
    g.DrawLine(glyphPen, mcx - r, cy, mcx + r, cy);
    var pcx = plusX + btnW / 2f;
    g.DrawLine(glyphPen, pcx - r, cy, pcx + r, cy);
    g.DrawLine(glyphPen, pcx, cy - r, pcx, cy + r);
  }
}

/// Sleek rounded progress bar with warm amber gradient fill.
internal sealed class StyledProgressBar : Control, ISizableBlock
{
  private int value;

  internal StyledProgressBar()
  {
    Height = Theme.Px(18);
    BackColor = Theme.Surface;
    TabStop = false;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
  }

  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  internal int Value
  {
    get => value;
    set { var clamped = Math.Clamp(value, 0, 100); if (this.value == clamped) return; this.value = clamped; Invalidate(); }
  }

  public int GetHeightForWidth(int width) => Theme.Px(18);

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(BackColor);
    if (Width <= 4 || Height <= 4) return;
    g.SmoothingMode = SmoothingMode.AntiAlias;

    var barH = Theme.PxF(8f);
    var barY = (Height - barH) / 2f;
    var pctW = Theme.Px(42);
    var trackW = Math.Max(40f, Width - pctW - Theme.Px(8));
    var trackRect = new RectangleF(0.5f, barY, trackW, barH);

    using (var trackPath = Theme.RoundedRect(trackRect, barH / 2f))
    using (var trackFill = new SolidBrush(Theme.SurfaceRaised))
    using (var trackPen = new Pen(Theme.Border, 1f))
    {
      g.FillPath(trackFill, trackPath);
      g.DrawPath(trackPen, trackPath);
    }

    if (value > 0)
    {
      var fillW = Math.Max(barH, trackW * (value / 100f));
      var fillRect = new RectangleF(0.5f, barY, fillW, barH);
      using var fillPath = Theme.RoundedRect(fillRect, barH / 2f);
      using var fillBrush = new LinearGradientBrush(RectangleF.Inflate(fillRect, 1f, 1f), Theme.Accent, Theme.AccentLight, LinearGradientMode.Horizontal)
      {
        WrapMode = WrapMode.TileFlipXY
      };
      g.FillPath(fillBrush, fillPath);
    }

    Theme.DrawText(g, $"{value}%", Theme.Mono(8.5f, true), new Rectangle((int)trackW + Theme.Px(8), 0, pctW, Height), Theme.Accent, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
  }
}

/// Centered brand showcase card for the About page.
internal sealed class AboutHeroCard : Control, ISizableBlock
{
  private readonly string versionLine;
  private readonly PalmButton[] buttons;

  internal AboutHeroCard(string versionLine, params PalmButton[] buttons)
  {
    this.versionLine = versionLine;
    this.buttons = buttons;
    BackColor = Theme.Canvas;
    TabStop = false;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    foreach (var btn in buttons) { btn.BackColor = Theme.Surface; Controls.Add(btn); }
  }

  public int GetHeightForWidth(int width)
  {
    var pad = Theme.Px(26);
    var y = pad + Theme.Px(74) + Theme.Px(14);
    y += Theme.MeasureText("WinPalm", Theme.Display(22f, true), width - pad * 2, singleLine: true).Height + Theme.Px(4);
    y += Theme.MeasureText("Your PC, in your palm.", Theme.Font(11.5f, true), width - pad * 2, singleLine: true).Height + Theme.Px(8);
    y += Theme.Px(26) + Theme.Px(18);

    var gap = Theme.Px(8);
    var totalBtnW = 0;
    var btnH = 0;
    foreach (var btn in buttons) { btn.PerformAutoSize(); totalBtnW += btn.Width + gap; btnH = Math.Max(btnH, btn.Height); }
    if (totalBtnW > 0) totalBtnW -= gap;

    if (totalBtnW <= width - pad * 2)
    {
      var bx = (width - totalBtnW) / 2;
      foreach (var btn in buttons) { btn.Location = new Point(bx, y); bx += btn.Width + gap; }
      y += btnH;
    }
    else
    {
      var bx = pad;
      foreach (var btn in buttons)
      {
        if (bx > pad && bx + btn.Width > width - pad) { bx = pad; y += btnH + gap; }
        btn.Location = new Point(bx, y);
        bx += btn.Width + gap;
      }
      y += btnH;
    }
    return y + pad;
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(Parent?.BackColor ?? Theme.Canvas);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    Theme.PaintCardFrame(g, ClientRectangle, Theme.PxF(14f), Theme.Surface, Theme.Surface, Theme.BorderStrong);

    var glowSize = Theme.PxF(170f);
    var glowRect = new RectangleF((Width - glowSize) / 2f, Theme.PxF(-24f), glowSize, glowSize);
    using (var glowPath = new GraphicsPath())
    {
      glowPath.AddEllipse(glowRect);
      using var glowBrush = new PathGradientBrush(glowPath)
      {
        CenterColor = Color.FromArgb(36, Theme.Accent),
        SurroundColors = [Color.FromArgb(0, Theme.Accent)]
      };
      g.FillPath(glowBrush, glowPath);
    }

    var y = Theme.Px(26);
    var iconSize = Theme.PxF(74f);
    Icons.DrawBrandMark(g, new RectangleF((Width - iconSize) / 2f, y, iconSize, iconSize));
    y += (int)iconSize + Theme.Px(14);

    var titleFont = Theme.Display(22f, true);
    var tSize = Theme.MeasureText("WinPalm", titleFont, Width, singleLine: true);
    Theme.DrawText(g, "WinPalm", titleFont, new Rectangle(0, y, Width, tSize.Height + 2), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
    y += tSize.Height + Theme.Px(4);

    var tagFont = Theme.Font(11.5f, true);
    var tagSize = Theme.MeasureText("Your PC, in your palm.", tagFont, Width, singleLine: true);
    Theme.DrawText(g, "Your PC, in your palm.", tagFont, new Rectangle(0, y, Width, tagSize.Height + 2), Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
    y += tagSize.Height + Theme.Px(10);

    var vFont = Theme.Mono(8.75f);
    var vSize = Theme.MeasureText(versionLine, vFont, Width - Theme.Px(40), singleLine: true);
    var pillW = vSize.Width + Theme.Px(22);
    var pillH = Theme.Px(24);
    var pillRect = new RectangleF((Width - pillW) / 2f, y, pillW, pillH);
    using (var pPath = Theme.RoundedRect(pillRect, pillH / 2f))
    using (var pFill = new SolidBrush(Theme.SurfaceRaised))
    using (var pPen = new Pen(Theme.Border, 1f))
    {
      g.FillPath(pFill, pPath);
      g.DrawPath(pPen, pPath);
    }
    Theme.DrawText(g, versionLine, vFont, new Rectangle((int)pillRect.X, y, (int)pillW, pillH), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
  }
}
