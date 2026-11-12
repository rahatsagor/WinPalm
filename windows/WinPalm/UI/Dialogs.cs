namespace WinPalm.UI;

/// Custom dark Windows 11 modal dialogs matching WinPalm's surface and typography system.
internal static class PalmDialog
{
  internal static bool Confirm(IWin32Window? owner, string title, string message, string confirmLabel = "Confirm", bool destructive = false)
  {
    using var dialog = CreateShell(title, 460);
    var pad = Theme.Px(22);
    var innerW = dialog.ClientSize.Width - pad * 2;

    var header = new BannerCard(
      destructive ? IconKind.Warning : IconKind.Shield,
      destructive ? BadgeTone.Danger : BadgeTone.Warning,
      title,
      message)
    {
      BackColor = Theme.Canvas
    };
    var cardH = header.GetHeightForWidth(innerW);
    header.SetBounds(pad, pad, innerW, cardH);
    dialog.Controls.Add(header);

    var cancel = new PalmButton("Cancel", () => dialog.DialogResult = DialogResult.Cancel, ButtonTone.Secondary) { BackColor = Theme.Canvas };
    var confirm = new PalmButton(confirmLabel, () => dialog.DialogResult = DialogResult.OK, destructive ? ButtonTone.Danger : ButtonTone.Primary) { BackColor = Theme.Canvas };
    cancel.PerformAutoSize();
    confirm.PerformAutoSize();

    var btnY = pad + cardH + Theme.Px(18);
    confirm.Location = new Point(dialog.ClientSize.Width - pad - confirm.Width, btnY);
    cancel.Location = new Point(confirm.Left - Theme.Px(8) - cancel.Width, btnY);
    dialog.Controls.Add(cancel);
    dialog.Controls.Add(confirm);

    dialog.ClientSize = new Size(dialog.ClientSize.Width, btnY + confirm.Height + pad);
    return dialog.ShowDialog(owner) == DialogResult.OK;
  }

  internal static void Info(IWin32Window? owner, string title, string message, BadgeTone tone = BadgeTone.Accent)
  {
    using var dialog = CreateShell(title, 480);
    var pad = Theme.Px(22);
    var innerW = dialog.ClientSize.Width - pad * 2;

    var card = new BannerCard(
      tone == BadgeTone.Warning ? IconKind.Warning : IconKind.Check,
      tone,
      title,
      message)
    {
      BackColor = Theme.Canvas
    };
    var cardH = card.GetHeightForWidth(innerW);
    card.SetBounds(pad, pad, innerW, cardH);
    dialog.Controls.Add(card);

    var ok = new PalmButton("Done", () => dialog.DialogResult = DialogResult.OK, ButtonTone.Primary) { BackColor = Theme.Canvas };
    ok.PerformAutoSize();
    var btnY = pad + cardH + Theme.Px(18);
    ok.Location = new Point(dialog.ClientSize.Width - pad - ok.Width, btnY);
    dialog.Controls.Add(ok);

    dialog.ClientSize = new Size(dialog.ClientSize.Width, btnY + ok.Height + pad);
    _ = dialog.ShowDialog(owner);
  }

  internal static string? PromptText(IWin32Window? owner, string title, string label, string initial, string saveLabel = "Save")
  {
    using var dialog = CreateShell(title, 480);
    var pad = Theme.Px(22);
    var innerW = dialog.ClientSize.Width - pad * 2;

    var field = new StyledTextBox(initial, label: label) { BackColor = Theme.Canvas };
    dialog.Controls.Add(field);

    var cancel = new PalmButton("Cancel", () => dialog.DialogResult = DialogResult.Cancel) { BackColor = Theme.Canvas };
    PalmButton? save = null;

    void LayoutPrompt()
    {
      var fieldH = field.GetHeightForWidth(innerW);
      field.SetBounds(pad, pad, innerW, fieldH);
      cancel.PerformAutoSize();
      save!.PerformAutoSize();
      var btnY = pad + fieldH + Theme.Px(18);
      save.Location = new Point(dialog.ClientSize.Width - pad - save.Width, btnY);
      cancel.Location = new Point(save.Left - Theme.Px(8) - cancel.Width, btnY);
      dialog.ClientSize = new Size(dialog.ClientSize.Width, btnY + save.Height + pad);
    }

    save = new PalmButton(saveLabel, () =>
    {
      if (string.IsNullOrWhiteSpace(field.Value)) { field.SetHint("Enter a name before saving."); LayoutPrompt(); return; }
      dialog.DialogResult = DialogResult.OK;
    }, ButtonTone.Primary, IconKind.Check) { BackColor = Theme.Canvas };

    dialog.Controls.Add(cancel);
    dialog.Controls.Add(save);
    LayoutPrompt();

    field.Inner.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; dialog.DialogResult = string.IsNullOrWhiteSpace(field.Value) ? DialogResult.None : DialogResult.OK; } };
    dialog.Shown += (_, _) => { field.Inner.Focus(); field.Inner.SelectAll(); };
    return dialog.ShowDialog(owner) == DialogResult.OK ? field.Value.Trim() : null;
  }

  internal static (string Name, string Command)? PromptAutomation(IWin32Window? owner, string? initialName, string? initialCommand)
  {
    using var dialog = CreateShell(initialName is null ? "Add automation" : "Edit automation", 560);
    var pad = Theme.Px(22);
    var innerW = dialog.ClientSize.Width - pad * 2;

    var nameField = new StyledTextBox(initialName ?? "", label: "Automation name", placeholder: "Open workspace") { BackColor = Theme.Canvas };
    dialog.Controls.Add(nameField);

    var cmdField = new StyledTextBox(
      initialCommand ?? "",
      multiline: true,
      monospace: true,
      multilineHeight: 136,
      label: "PowerShell command",
      hint: "Runs without a profile under your Windows user account (5-minute limit).",
      placeholder: "Start-Process code")
    {
      BackColor = Theme.Canvas
    };
    dialog.Controls.Add(cmdField);

    var cancel = new PalmButton("Cancel", () => dialog.DialogResult = DialogResult.Cancel) { BackColor = Theme.Canvas };
    PalmButton? save = null;

    void LayoutDialog()
    {
      var nameH = nameField.GetHeightForWidth(innerW);
      nameField.SetBounds(pad, pad, innerW, nameH);
      var cmdY = pad + nameH + Theme.Px(10);
      var cmdH = cmdField.GetHeightForWidth(innerW);
      cmdField.SetBounds(pad, cmdY, innerW, cmdH);
      cancel.PerformAutoSize();
      save!.PerformAutoSize();
      var btnY = cmdY + cmdH + Theme.Px(18);
      save.Location = new Point(dialog.ClientSize.Width - pad - save.Width, btnY);
      cancel.Location = new Point(save.Left - Theme.Px(8) - cancel.Width, btnY);
      dialog.ClientSize = new Size(dialog.ClientSize.Width, btnY + save.Height + pad);
    }

    save = new PalmButton("Save automation", () =>
    {
      var n = nameField.Value.Trim();
      var c = cmdField.Value.Trim();
      if (string.IsNullOrEmpty(n) || string.IsNullOrEmpty(c))
      {
        cmdField.SetHint("Both an automation name and a PowerShell command are required.");
        LayoutDialog();
        return;
      }
      if (n.Length > 100 || c.Length > 4096)
      {
        cmdField.SetHint("Keep the name under 100 characters and the command under 4096 characters.");
        LayoutDialog();
        return;
      }
      dialog.DialogResult = DialogResult.OK;
    }, ButtonTone.Primary, IconKind.Check) { BackColor = Theme.Canvas };

    dialog.Controls.Add(cancel);
    dialog.Controls.Add(save);
    LayoutDialog();

    dialog.Shown += (_, _) =>
    {
      if (string.IsNullOrEmpty(initialName)) nameField.Inner.Focus();
      else cmdField.Inner.Focus();
    };
    return dialog.ShowDialog(owner) == DialogResult.OK ? (nameField.Value.Trim(), cmdField.Value.Trim()) : null;
  }

  private static Form CreateShell(string title, int widthPx)
  {
    var dialog = new Form
    {
      Text = title,
      AutoScaleMode = AutoScaleMode.None,
      ClientSize = new Size(Theme.Px(widthPx), Theme.Px(200)),
      BackColor = Theme.Canvas,
      ForeColor = Theme.Text,
      Font = Theme.Font(9.75f),
      FormBorderStyle = FormBorderStyle.FixedDialog,
      StartPosition = FormStartPosition.CenterParent,
      MinimizeBox = false,
      MaximizeBox = false,
      ShowInTaskbar = false,
      KeyPreview = true
    };
    dialog.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; dialog.DialogResult = DialogResult.Cancel; } };
    dialog.HandleCreated += (_, _) => Theme.ApplyWindowChrome(dialog, Theme.Canvas, roundCorners: true);
    return dialog;
  }
}
