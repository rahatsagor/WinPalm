namespace WinPalm.UI;

internal static class Theme
{
  internal static readonly Color Background = Color.FromArgb(19, 20, 23), Surface = Color.FromArgb(30, 32, 36), Line = Color.FromArgb(55, 57, 63), Text = Color.FromArgb(243, 243, 245), Muted = Color.FromArgb(167, 170, 179), Accent = Color.FromArgb(249, 129, 61), Warning = Color.FromArgb(255, 196, 92);
  /// Device pixels per 96-DPI pixel for the window being built. Pages are
  /// created at runtime, after WinForms' one-time autoscaling, so every size
  /// here is scaled explicitly and pages are rebuilt when the DPI changes.
  internal static float Scale { get; set; } = 1;
  internal static int Px(int value) => (int)Math.Round(value * Scale);
  internal static Size Px(int width, int height) => new(Px(width), Px(height));
  internal static Padding Px(int left, int top, int right, int bottom) => new(Px(left), Px(top), Px(right), Px(bottom));
  private static readonly Dictionary<(string, float, bool, float), Font> Fonts = [];
  /// Point sizes at the current scale, in pixels so the result doesn't also
  /// depend on the primary monitor's DPI. Cached; controls share fonts.
  internal static Font Font(float size, bool bold = false, string family = "Segoe UI")
  {
    var key = (family, size, bold, Scale);
    if (!Fonts.TryGetValue(key, out var font)) Fonts[key] = font = new Font(family, size * 96f / 72f * Scale, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
    return font;
  }
  internal static Label Label(string text, float size = 10, Color? color = null, bool bold = false) => new() { Text = text, AutoSize = true, MaximumSize = Px(650, 0), Font = Font(size, bold), ForeColor = color ?? Text, Margin = Px(0, 0, 0, 10), UseMnemonic = false };
  internal static Button Button(string text, Action click, bool accent = false)
  {
    var button = new Button { Text = text, AutoSize = true, MinimumSize = Px(110, 36), FlatStyle = FlatStyle.Flat, Font = Font(10), BackColor = accent ? Accent : Surface, ForeColor = accent ? Color.FromArgb(25, 22, 20) : Text, Padding = Px(12, 5, 12, 5), Margin = Px(0, 0, 8, 8), Cursor = Cursors.Hand, UseMnemonic = false };
    button.FlatAppearance.BorderColor = accent ? Accent : Line; button.Click += (_, _) => click(); return button;
  }
  internal static FlowLayoutPanel Stack(int width = 620) => new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = Px(width), Margin = Px(0, 0, 0, 18), BackColor = Color.Transparent };
  internal static FlowLayoutPanel Row(params Control[] controls)
  {
    var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = Px(670, 0), Margin = Px(0, 4, 0, 8), BackColor = Color.Transparent };
    row.Controls.AddRange(controls); return row;
  }
  internal static TextBox Input(string value, bool multiline = false) => new() { Text = value, Width = Px(560), Multiline = multiline, Height = Px(multiline ? 100 : 30), Font = Font(10), BackColor = Surface, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Margin = Px(0, 0, 0, 12), AcceptsReturn = multiline, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None };
  internal static CheckBox Toggle(string text, bool enabled, Action<bool> change)
  {
    var box = new CheckBox { Text = text, Checked = enabled, AutoSize = true, MaximumSize = Px(610, 0), Font = Font(10), ForeColor = Text, Margin = Px(0, 4, 0, 12), UseMnemonic = false };
    box.CheckedChanged += (_, _) => change(box.Checked); return box;
  }
}
