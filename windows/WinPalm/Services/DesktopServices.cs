using System.Diagnostics;
using System.Text.Json;
namespace WinPalm.Services;

internal sealed class DesktopServices : IDisposable
{
  private readonly StaWorker worker = new("WinPalm clipboard");
  /// Separate from the clipboard, so a paste never waits behind a scan of
  /// the Start Menu and Store apps.
  private readonly StaWorker appWorker = new("WinPalm app catalog");
  private readonly ClipboardService clipboard;
  private readonly SpeechService speech = new();
  internal readonly AppCatalog Apps;
  private Preferences preferences;
  internal DesktopServices(Preferences preferences)
  {
    this.preferences = Clone(preferences); clipboard = new(worker);
    Apps = new(appWorker, () => Volatile.Read(ref this.preferences).AppFolders.ToArray());
  }
  internal void Update(Preferences updated) => Volatile.Write(ref preferences, Clone(updated));
  private static Preferences Clone(Preferences value) => JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(value, Preferences.Json), Preferences.Json)!;
  /// "desktop-abc.local": Windows answers mDNS for its DNS host name, so
  /// phones find it without a DNS server and HTTPS can certify the name.
  private static string LocalHostName()
  {
    var name = System.Net.Dns.GetHostName().Trim().ToLowerInvariant();
    return name.Length is > 0 and <= 63 && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') && name[0] != '-' && name[^1] != '-'
        ? name + ".local" : Environment.MachineName;
  }
  internal object Config(string? webRoot = null, bool loopbackOnly = false) => new
  {
    port = preferences.Port,
    dataDir = Preferences.DataDirectory,
    webRoot = webRoot ?? Path.Combine(AppContext.BaseDirectory, "web"),
    hostName = LocalHostName(),
    computerName = Environment.MachineName,
    appVersion = typeof(DesktopServices).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
    linkHost = preferences.AddressMode == "custom" ? (object)new { kind = "custom", address = preferences.CustomAddress } : new { kind = preferences.AddressMode },
    allowLocalProxy = preferences.AllowLocalProxy,
    allowPin = preferences.AllowPin,
    allowApproval = preferences.AllowApproval,
    allowShell = preferences.AllowShell,
    voiceAvailable = preferences.Dictation && SpeechService.Available,
    loopbackOnly
  };
  internal object? Invoke(string op, JsonElement value) => op switch
  {
    "listApps" => Apps.List(),
    "appIcon" => Apps.Icon(value.GetProperty("id").GetString()!, value.GetProperty("size").GetInt32()),
    "appAction" => Apps.Action(value.GetProperty("id").GetString()!, value.GetProperty("op").GetString()!),
    "openUrl" => Open(value.GetString()!),
    "native" => Native(value.GetString()!),
    "automationCommand" => preferences.Automations.TryGetValue(value.GetString()!, out var command) ? command : null,
    "platformMedia" => value.GetString() switch { "brightnessUp" => Brightness.Step(true), "brightnessDown" => Brightness.Step(false), "fastForward" => MediaCommand(49), "rewind" => MediaCommand(50), _ => false },
    "setVolume" => SetVolume(value),
    "clipboardText" => clipboard.Read(),
    "setClipboardText" => WriteClipboard(value.GetString()!),
    "beginPaste" => clipboard.Begin(value.GetString()!),
    "endPaste" => EndPaste(),
    "voiceStart" => speech.Start(value.GetProperty("session").GetUInt64(), value.GetProperty("locale").GetString()),
    "voiceAudio" => Audio(value),
    "voiceStop" => StopVoice(value),
    _ => null
  };
  private object? Audio(JsonElement value) { speech.Audio(value.GetProperty("session").GetUInt64(), Convert.FromBase64String(value.GetProperty("pcm").GetString()!)); return null; }
  private object? StopVoice(JsonElement value) { speech.Stop(value.GetProperty("session").GetUInt64(), value.GetProperty("cancel").GetBoolean()); return null; }
  private object? SetVolume(JsonElement value)
  {
    AudioVolume.Set(value.GetProperty("value").ValueKind == JsonValueKind.Number ? value.GetProperty("value").GetSingle() : null,
        value.GetProperty("muted").ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetProperty("muted").GetBoolean() : null);
    return null;
  }
  private object? WriteClipboard(string text) { clipboard.Write(text); return null; }
  private object? EndPaste() { clipboard.End(); return null; }
  internal static bool Open(string url)
  {
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Diagnostics.Record(error); return false; }
  }
  /// A Settings URI, or "firewall" for the firewall's allowed-apps list.
  internal static bool OpenSetting(string target)
  {
    if (target != "firewall") return Open(target);
    try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "control.exe")) { UseShellExecute = false, ArgumentList = { "/name", "Microsoft.WindowsFirewall", "/page", "pageConfigureApps" } }); return true; }
    catch (System.ComponentModel.Win32Exception error) { Diagnostics.Record(error); return false; }
  }
  private static bool MediaCommand(int command) { var window = Win32.GetForegroundWindow(); return window != 0 && !Win32.IsShellWindow(window) && Win32.PostMessageW(window, 0x319, window, command << 16); }
  private static bool Native(string command)
  {
    // Minimize and close act only on app windows: WM_CLOSE to the desktop
    // opens the Shut Down Windows dialog.
    var window = Win32.GetForegroundWindow();
    if (window != 0 && Win32.IsShellWindow(window)) window = 0;
    switch (command)
    {
      case "Launchpad": return Win32.Key(0x5b);
      case "LockScreen": return Win32.LockWorkStation();
      case "SleepDisplay": Win32.PostMessageW((nint)0xffff, 0x112, 0xf170, 2); return true;
      case "Sleep": return Win32.EnableShutdownPrivilege() && Win32.SetSuspendState(false, false, false);
      case "Minimize": return window != 0 && Win32.ShowWindowAsync(window, 6);
      case "CloseWindow": return window != 0 && Win32.PostMessageW(window, 0x10, 0, 0);
      case "Screenshot": return Win32.Key(0x2c);
      default: return false;
    }
  }
  public void Dispose()
  {
    try { clipboard.EndPending(); } catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.ExternalException) { Diagnostics.Record(error); }
    speech.Dispose(); appWorker.Dispose(); worker.Dispose();
  }
}
