using System.Text.Json;
using WinPalm.Services;
namespace WinPalm.UI;

internal sealed class ApprovalWindow : Form
{
  private readonly ulong requestId;
  private readonly string deviceName;
  private readonly string model;
  private readonly string address;
  private bool answered;

  protected override bool ShowWithoutActivation => true;
  protected override CreateParams CreateParams
  {
    get
    {
      var value = base.CreateParams;
      value.ExStyle |= 0x08000088; // WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW
      return value;
    }
  }

  internal ApprovalWindow(JsonElement request, int stackIndex = 0)
  {
    requestId = request.GetProperty("id").GetUInt64();
    deviceName = request.GetProperty("deviceName").GetString() ?? "New device";
    model = request.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
    address = request.GetProperty("address").GetString() ?? "";

    Text = "WinPalm · Pairing request";
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = true;
    AutoScaleMode = AutoScaleMode.None;
    BackColor = Theme.Surface;
    ForeColor = Theme.Text;
    Font = Theme.Font(9.75f);
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

    var width = Theme.Px(392);
    var pad = Theme.Px(18);
    var textX = pad + Theme.Px(42) + Theme.Px(12);
    var textW = width - textX - pad;

    var prompt = $"Allow \"{deviceName}\" to control this PC?";
    var detail = $"{(string.IsNullOrWhiteSpace(model) ? "Device" : model)} at {address} is requesting access. Only allow devices you recognize.";
    var promptH = Theme.MeasureText(prompt, Theme.Font(11f, true), textW).Height;
    var detailH = Theme.MeasureText(detail, Theme.Font(9f), textW).Height;
    var contentBottom = pad + Theme.Px(20) + promptH + Theme.Px(4) + detailH;
    var btnY = Math.Max(pad + Theme.Px(52), contentBottom) + Theme.Px(16);

    var deny = new PalmButton("Don't allow", () => Answer(false), ButtonTone.Secondary, compact: true) { BackColor = Theme.Surface };
    var allow = new PalmButton("Allow device", () => Answer(true), ButtonTone.Primary, IconKind.Check, compact: true) { BackColor = Theme.Surface };
    deny.PerformAutoSize();
    allow.PerformAutoSize();
    allow.Location = new Point(width - pad - allow.Width, btnY);
    deny.Location = new Point(allow.Left - Theme.Px(8) - deny.Width, btnY);
    Controls.Add(deny);
    Controls.Add(allow);

    ClientSize = new Size(width, btnY + allow.Height + pad);
    StartPosition = FormStartPosition.Manual;
    var area = (Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault())?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
    var offset = Math.Min(stackIndex, 4) * Theme.Px(18);
    Location = new Point(area.Right - Width - Theme.Px(20) - offset, Math.Max(area.Top + Theme.Px(20), area.Bottom - Height - Theme.Px(20) - offset));

    HandleCreated += (_, _) => Theme.ApplyWindowChrome(this, Theme.Surface, roundCorners: true);
    MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Win32.DragWindow(Handle); };
    FormClosing += (_, _) => { if (!answered) Answer(false); };
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    var g = e.Graphics;
    g.Clear(Theme.Surface);
    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

    Theme.PaintCardFrame(g, ClientRectangle, Theme.PxF(12f), Theme.Surface, Theme.Surface, Theme.AccentBorder);
    using (var topWash = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(0, 0, Width, Theme.PxF(64f)), Color.FromArgb(26, Theme.Accent), Color.FromArgb(0, Theme.Accent), System.Drawing.Drawing2D.LinearGradientMode.Vertical) { WrapMode = System.Drawing.Drawing2D.WrapMode.TileFlipXY })
      g.FillRectangle(topWash, 1, 1, Width - 2, Theme.Px(60));

    var pad = Theme.Px(18);
    var iconSize = Theme.PxF(42f);
    Icons.DrawBrandMark(g, new RectangleF(pad, pad + Theme.Px(2), iconSize, iconSize));

    var textX = pad + (int)iconSize + Theme.Px(12);
    var textW = Width - textX - pad;
    var y = pad;

    Theme.DrawText(g, "PAIRING REQUEST", Theme.Font(7.75f, true), new Rectangle(textX, y, textW, Theme.Px(16)), Theme.Accent, TextFormatFlags.Left | TextFormatFlags.SingleLine);
    y += Theme.Px(18);

    var prompt = $"Allow \"{deviceName}\" to control this PC?";
    var pFont = Theme.Font(11f, true);
    var pSize = Theme.MeasureText(prompt, pFont, textW);
    Theme.DrawText(g, prompt, pFont, new Rectangle(textX, y, textW, pSize.Height + 2), Theme.Text);
    y += pSize.Height + Theme.Px(4);

    var detail = $"{(string.IsNullOrWhiteSpace(model) ? "Device" : model)} at {address} is requesting access. Only allow devices you recognize.";
    Theme.DrawText(g, detail, Theme.Font(9f), new Rectangle(textX, y, textW, Height - y - pad), Theme.Muted);
  }

  private void Answer(bool allow) { if (answered) return; answered = true; CoreBridge.TryCall(new { op = "approve", id = requestId, allow }); Close(); }
  internal void End() { answered = true; Close(); }
}
