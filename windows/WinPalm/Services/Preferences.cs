using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace WinPalm.Services;

internal sealed class Preferences
{
  public int Port { get; set; } = 8790;
  public string AddressMode { get; set; } = "ipAddress";
  public string CustomAddress { get; set; } = "";
  public bool AllowPin { get; set; } = true;
  public bool AllowApproval { get; set; } = true;
  public bool AllowShell { get; set; }
  public bool AllowLocalProxy { get; set; }
  public bool Dictation { get; set; } = true;
  public bool TrayHintShown { get; set; }
  public bool CheckForUpdates { get; set; } = true;
  public DateTime LastUpdateCheckUtc { get; set; }
  public string NotifiedUpdateVersion { get; set; } = "";
  public List<string> AppFolders { get; set; } = [];
  // Populate keeps the case-insensitive dictionary when loading.
  [System.Text.Json.Serialization.JsonObjectCreationHandling(System.Text.Json.Serialization.JsonObjectCreationHandling.Populate)]
  public Dictionary<string, string> Automations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
  public static string DataDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RS Application", "WinPalm");
  private static readonly object Gate = new();
  public static void UseTestDirectory(string directory) => DataDirectory = directory;
  public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
  public static Preferences Load()
  {
    Directory.CreateDirectory(DataDirectory);
    // Inherit no broad ACL: pairing hashes and the local CA key are private.
    var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No Windows user identity");
    var acl = new DirectorySecurity();
    acl.SetAccessRuleProtection(true, false);
    foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
      acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    new DirectoryInfo(DataDirectory).SetAccessControl(acl);
    var file = Path.Combine(DataDirectory, "preferences.json");
    try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(file), Json) ?? new(); }
    catch (FileNotFoundException) { return new(); }
    catch (JsonException)
    {
      File.Move(file, file + ".corrupt-" + DateTime.UtcNow.Ticks);
      return new();
    }
  }
  public void Save()
  {
    lock (Gate)
    {
      var file = Path.Combine(DataDirectory, "preferences.json");
      var temporary = file + ".tmp";
      File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
      File.Move(temporary, file, true);
    }
  }
  private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
  private static string LoginCommand => $"\"{Environment.ProcessPath}\" --start-minimized";
  /// Opened from inside the ZIP, Windows runs a temporary copy that is gone
  /// after a restart, so a sign-in entry would start nothing.
  internal static string? LocationProblem(string? path) =>
      path is not null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
          ? "WinPalm is running from inside its ZIP file. Extract the whole ZIP to a permanent folder, open WinPalm from there, then turn this on."
          : null;
  public static bool StartsAtLogin
  {
    get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("WinPalm") is string; }
    set
    {
      if (value && LocationProblem(Environment.ProcessPath) is { } problem) throw new InvalidOperationException(problem);
      using var key = Registry.CurrentUser.CreateSubKey(RunKey);
      if (value) key.SetValue("WinPalm", LoginCommand);
      else key.DeleteValue("WinPalm", false);
    }
  }
  /// After the WinPalm folder moves, sign-in should start this copy.
  internal static void KeepLoginPathCurrent()
  {
    try
    {
      using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
      if (key?.GetValue("WinPalm") is string command && command != LoginCommand && LocationProblem(Environment.ProcessPath) is null) key.SetValue("WinPalm", LoginCommand);
    }
    catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { Diagnostics.Record(error); }
  }
  /// Portable updates also work for the per-user install. Keep the
  /// version Windows displays in Installed apps in sync with that copy.
  internal static void KeepInstalledVersionCurrent()
  {
    try
    {
      using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinPalm", true);
      if (key?.GetValue("InstallLocation") is not string directory) return;
      if (string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        key.SetValue("DisplayVersion", UpdateController.CurrentVersion.ToString(3));
    }
    catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { Diagnostics.Record(error); }
  }
}
