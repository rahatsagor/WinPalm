using System.Text.Json;
using WinPalm.Services;
namespace WinPalm.UI;

internal sealed class ApprovalWindow : Form
{
  private readonly ulong requestId;
  private bool answered;
  protected override bool ShowWithoutActivation => true;
  protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000080; return value; } }
  internal ApprovalWindow(JsonElement request)
  {
    requestId = request.GetProperty("id").GetUInt64();
    Text = "WinPalm · Pairing request"; FormBorderStyle = FormBorderStyle.FixedToolWindow; ShowInTaskbar = false; TopMost = true;
    AutoScaleMode = AutoScaleMode.None; ClientSize = Theme.Px(400, 215); BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Font(10);
    var body = Theme.Stack(355); body.Location = new Point(Theme.Px(22), Theme.Px(20));
    body.Controls.Add(Theme.Label("Allow this device to control your PC?", 13, bold: true));
    body.Controls.Add(Theme.Label(request.GetProperty("deviceName").GetString() ?? "New device", 11));
    body.Controls.Add(Theme.Label(request.GetProperty("address").GetString() ?? "", 9, Theme.Muted));
    body.Controls.Add(Theme.Row(Theme.Button("Don't allow", () => Answer(false)), Theme.Button("Allow", () => Answer(true), true)));
    Controls.Add(body); StartPosition = FormStartPosition.Manual;
    var area = Screen.PrimaryScreen!.WorkingArea; Location = new Point(area.Right - Width - Theme.Px(20), area.Bottom - Height - Theme.Px(20));
    FormClosing += (_, _) => { if (!answered) Answer(false); };
  }
  private void Answer(bool allow) { if (answered) return; answered = true; CoreBridge.TryCall(new { op = "approve", id = requestId, allow }); Close(); }
  internal void End() { answered = true; Close(); }
}
