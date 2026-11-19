namespace WinPalm.Services;

internal static class Diagnostics
{
  private static readonly object Gate = new();
  internal static string FilePath => Path.Combine(Preferences.DataDirectory, "WinPalm.log");
  internal static void Record(Exception error)
  {
    // Do not log messages, arguments, URLs, clipboard, PINs or speech text.
    lock (Gate) try
      {
        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512_000) File.Move(FilePath, FilePath + ".previous", true);
        File.AppendAllText(FilePath, $"{DateTimeOffset.UtcNow:O} {error.GetType().Name} HRESULT=0x{error.HResult:X8}\n");
      }
      catch (IOException) { }
  }
}
