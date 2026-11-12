using System.Drawing.Drawing2D;
using System.Drawing.Text;
using WinPalm.Services;

namespace WinPalm.UI;

internal static class Theme
{
  // Core obsidian & warm-amber palette aligned with WinPalm's phone client and brand mark.
  internal static readonly Color
    Canvas = Color.FromArgb(11, 13, 16),
    Background = Canvas,
    Sidebar = Color.FromArgb(16, 18, 23),
    Surface = Color.FromArgb(22, 25, 32),
    SurfaceTop = Color.FromArgb(26, 30, 38),
    SurfaceRaised = Color.FromArgb(30, 34, 43),
    SurfaceHover = Color.FromArgb(38, 43, 54),
    SurfacePressed = Color.FromArgb(46, 52, 65),
    Border = Color.FromArgb(40, 45, 56),
    Line = Border,
    BorderStrong = Color.FromArgb(58, 65, 80),
    Divider = Color.FromArgb(31, 35, 44),
    Text = Color.FromArgb(238, 240, 244),
    Muted = Color.FromArgb(146, 154, 168),
    Faint = Color.FromArgb(98, 106, 120),
    Accent = Color.FromArgb(255, 138, 61),
    AccentLight = Color.FromArgb(255, 176, 92),
    AccentDeep = Color.FromArgb(224, 103, 34),
    AccentSurface = Color.FromArgb(39, 27, 21),
    AccentSurfaceHover = Color.FromArgb(49, 34, 25),
    AccentBorder = Color.FromArgb(122, 71, 37),
    AccentInk = Color.FromArgb(24, 13, 5),
    Good = Color.FromArgb(62, 207, 142),
    GoodSurface = Color.FromArgb(18, 37, 30),
    GoodBorder = Color.FromArgb(36, 92, 69),
    Warning = Color.FromArgb(245, 185, 66),
    WarningSurface = Color.FromArgb(39, 32, 19),
    WarningBorder = Color.FromArgb(102, 78, 31),
    Danger = Color.FromArgb(255, 95, 87),
    DangerSurface = Color.FromArgb(42, 22, 24),
    DangerSurfaceHover = Color.FromArgb(56, 27, 30),
    DangerBorder = Color.FromArgb(106, 44, 47);

  /// Device pixels per 96-DPI pixel for the window being built. Pages are
  /// created at runtime, after WinForms' one-time autoscaling, so every size
  /// here is scaled explicitly and pages are rebuilt when the DPI changes.
  internal static float Scale { get; set; } = 1;
  internal static int Px(int value) => (int)Math.Round(value * Scale);
  internal static float PxF(float value) => value * Scale;
  internal static Size Px(int width, int height) => new(Px(width), Px(height));
  internal static Padding Px(int left, int top, int right, int bottom) => new(Px(left), Px(top), Px(right), Px(bottom));

  private static readonly string UiFamily = PickFont("Segoe UI Variable Text", "Segoe UI");
  private static readonly string DisplayFamily = PickFont("Segoe UI Variable Display", "Segoe UI");
  private static readonly string MonoFamily = PickFont("Cascadia Mono", "Consolas");
  private static readonly Dictionary<(string, float, FontStyle, float), Font> Fonts = [];

  private static string PickFont(string preferred, string fallback)
  {
    try
    {
      using var installed = new InstalledFontCollection();
      if (installed.Families.Any(f => string.Equals(f.Name, preferred, StringComparison.OrdinalIgnoreCase))) return preferred;
    }
    catch
    {
      // Fall back cleanly if GDI+ font enumeration fails.
    }
    return fallback;
  }

  /// Point sizes at the current scale, in pixels so the result doesn't also
  /// depend on the primary monitor's DPI. Cached; controls share fonts.
  internal static Font Font(float size, bool bold = false, string? family = null)
  {
    var resolved = family ?? UiFamily;
    var style = bold ? FontStyle.Bold : FontStyle.Regular;
    var key = (resolved, size, style, Scale);
    if (!Fonts.TryGetValue(key, out var font))
      Fonts[key] = font = new Font(resolved, size * 96f / 72f * Scale, style, GraphicsUnit.Pixel);
    return font;
  }

  internal static Font Display(float size, bool bold = true) => Font(size, bold, DisplayFamily);
  internal static Font Mono(float size, bool bold = false) => Font(size, bold, MonoFamily);

  internal static GraphicsPath RoundedRect(RectangleF bounds, float radius)
  {
    var path = new GraphicsPath();
    if (bounds.Width <= 0 || bounds.Height <= 0) return path;
    var clamped = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f);
    if (clamped <= 0.5f)
    {
      path.AddRectangle(bounds);
      return path;
    }
    var diameter = clamped * 2f;
    path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
    path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
    path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
    path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
  }

  internal static void PaintCardFrame(Graphics g, Rectangle bounds, float radius, Color topColor, Color bottomColor, Color borderColor, bool topSheen = true)
  {
    if (bounds.Width <= 2 || bounds.Height <= 2) return;
    var rect = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1f, bounds.Height - 1f);
    using var path = RoundedRect(rect, radius);
    if (topColor == bottomColor)
    {
      using var fill = new SolidBrush(topColor);
      g.FillPath(fill, path);
    }
    else
    {
      using var fill = new LinearGradientBrush(RectangleF.Inflate(rect, 1f, 1f), topColor, bottomColor, LinearGradientMode.Vertical)
      {
        WrapMode = WrapMode.TileFlipXY
      };
      g.FillPath(fill, path);
    }
    if (topSheen && bounds.Width > 24)
    {
      var inset = Math.Max(radius, 8f);
      if (rect.Right - inset > rect.X + inset)
      {
        using var sheen = new Pen(Color.FromArgb(18, 255, 255, 255), 1f);
        g.DrawLine(sheen, rect.X + inset, rect.Y + 1f, rect.Right - inset, rect.Y + 1f);
      }
    }
    using var pen = new Pen(borderColor, 1f);
    g.DrawPath(pen, path);
  }

  internal static Size MeasureText(string text, Font font, int maxWidth, bool singleLine = false)
  {
    if (string.IsNullOrEmpty(text)) return Size.Empty;
    var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | (singleLine ? TextFormatFlags.SingleLine : TextFormatFlags.WordBreak);
    return TextRenderer.MeasureText(text, font, new Size(Math.Max(16, maxWidth), int.MaxValue), flags);
  }

  internal static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.WordBreak)
  {
    if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return;
    TextRenderer.DrawText(g, text, font, bounds, color, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
  }

  internal static Color Blend(Color a, Color b, float amount)
  {
    var t = Math.Clamp(amount, 0f, 1f);
    return Color.FromArgb(
      (int)Math.Round(a.A + (b.A - a.A) * t),
      (int)Math.Round(a.R + (b.R - a.R) * t),
      (int)Math.Round(a.G + (b.G - a.G) * t),
      (int)Math.Round(a.B + (b.B - a.B) * t));
  }

  internal static void ApplyWindowChrome(Form form, Color? captionColor = null, bool roundCorners = true)
  {
    if (!form.IsHandleCreated) return;
    Win32.ApplyWindowChrome(form.Handle, captionColor ?? Sidebar, Border, roundCorners);
  }

  internal static void StyleContextMenu(ContextMenuStrip menu)
  {
    menu.BackColor = Surface;
    menu.ForeColor = Text;
    menu.ShowImageMargin = false;
    menu.Padding = Px(6, 6, 6, 6);
    menu.Renderer = new DarkMenuRenderer();
  }

  private sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new DarkMenuColors())
  {
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
      e.Graphics.Clear(Surface);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
      var rect = new Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
      using var pen = new Pen(BorderStrong, 1f);
      e.Graphics.DrawRectangle(pen, rect);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
      if (!e.Item.Selected || !e.Item.Enabled) return;
      e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
      var rect = new RectangleF(4f, 1.5f, e.Item.Width - 8f, e.Item.Height - 3f);
      using var path = RoundedRect(rect, 6f);
      using var fill = new SolidBrush(SurfaceHover);
      e.Graphics.FillPath(fill, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
      e.TextColor = !e.Item.Enabled ? Muted : e.Item.ForeColor != SystemColors.ControlText && e.Item.ForeColor != Color.Empty ? e.Item.ForeColor : Text;
      base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
      var y = e.Item.Height / 2;
      using var pen = new Pen(Divider, 1f);
      e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }
  }

  private sealed class DarkMenuColors : ProfessionalColorTable
  {
    public override Color ToolStripDropDownBackground => Surface;
    public override Color MenuBorder => BorderStrong;
    public override Color MenuItemBorder => SurfaceHover;
    public override Color MenuItemSelected => SurfaceHover;
    public override Color ImageMarginGradientBegin => Surface;
    public override Color ImageMarginGradientMiddle => Surface;
    public override Color ImageMarginGradientEnd => Surface;
    public override Color SeparatorDark => Divider;
    public override Color SeparatorLight => Color.Transparent;
  }
}
