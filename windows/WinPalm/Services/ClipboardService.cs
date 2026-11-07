namespace WinPalm.Services;

internal sealed class ClipboardService(StaWorker worker)
{
  /// Password managers add this format so clipboard tools ignore the data;
  /// it also keeps WinPalm's temporary paste text out of clipboard history.
  private const string Excluded = "ExcludeClipboardContentFromMonitorProcessing";
  private IDataObject? saved;
  private uint sequence;
  [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
  [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
  [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
  private static readonly uint ExcludedFormat = RegisterClipboardFormatW(Excluded);
  /// Nil for passwords and other contents apps ask clipboard tools to skip.
  internal string? Read() => worker.Run(() => Retry(() => !IsClipboardFormatAvailable(ExcludedFormat) && Clipboard.ContainsText() ? Clipboard.GetText() : null));
  internal void Write(string text) => worker.Run(() => Retry(() => { Clipboard.SetText(text); return true; }));
  internal bool Begin(string text) => worker.Run(() => Retry(() =>
  {
    saved = Snapshot();
    var data = new DataObject();
    data.SetData(DataFormats.UnicodeText, false, text);
    // Keep the temporary text out of Win+V history, cloud clipboard sync and
    // clipboard managers, like CmdPalm's transient pasteboard type.
    data.SetData(Excluded, false, new MemoryStream(new byte[4]));
    data.SetData("CanIncludeInClipboardHistory", false, new MemoryStream(new byte[4]));
    data.SetData("CanUploadToCloudClipboard", false, new MemoryStream(new byte[4]));
    Clipboard.SetDataObject(data, true); sequence = GetClipboardSequenceNumber(); return true;
  }));
  internal void End() => worker.Run(() => Retry(() =>
  {
    // Never overwrite something the person copied during the paste.
    if (GetClipboardSequenceNumber() == sequence)
    {
      if (saved is not null) Clipboard.SetDataObject(saved, true);
      else Clipboard.Clear();
    }
    saved = null; return true;
  }));
  /// Puts back the clipboard of a paste that hasn't ended, for when WinPalm
  /// quits before the core's delayed restore.
  internal void EndPending() => worker.Run(() => { if (saved is not null) End(); return true; });
  /// Materialize every format before taking clipboard ownership; delayed
  /// rendering objects otherwise become unreadable after SetText. A format
  /// that can't be read (large images from some apps, or formats .NET no
  /// longer deserializes) is skipped rather than failing the paste.
  private static DataObject? Snapshot()
  {
    var current = Clipboard.GetDataObject();
    if (current is null) return null;
    var snapshot = new DataObject(); var any = false;
    foreach (var format in current.GetFormats(false))
    {
      try
      {
        var value = current.GetData(format, false);
        if (value is null or Exception) continue;
        snapshot.SetData(format, false, value); any = true;
      }
      catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or OutOfMemoryException or NotSupportedException or ArgumentException or InvalidOperationException) { }
    }
    if (!any) return null;
    // The original is already in Win+V history; restoring it shouldn't add
    // a duplicate entry or sync it again.
    snapshot.SetData("CanIncludeInClipboardHistory", false, new MemoryStream(new byte[4]));
    snapshot.SetData("CanUploadToCloudClipboard", false, new MemoryStream(new byte[4]));
    return snapshot;
  }
  private static T Retry<T>(Func<T> action)
  {
    for (var attempt = 0; ; attempt++) try { return action(); }
      catch (System.Runtime.InteropServices.ExternalException) when (attempt < 4) { Thread.Sleep(25); }
  }
}
