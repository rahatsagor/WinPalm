using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace WinPalm.Services;

internal sealed class AppCatalog(StaWorker worker, Func<IReadOnlyList<string>> folders)
{
  /// `AppId` is the package's AppUserModelID, which also identifies its
  /// windows; `Executable` identifies desktop app windows.
  private sealed record Entry(string Id, string Name, string Launch, string? Executable, string? AppId = null, string? IconLocation = null);
  private readonly record struct Window(nint Handle, string Path, string? AppId, string Title);
  private readonly object gate = new();
  private readonly object refreshGate = new();
  private Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
  private DateTime refreshed;
  /// Bumped when apps or folders may have changed, so a scan that was already
  /// running then doesn't count as fresh.
  private int generation, scans;
  internal void Invalidate() { lock (gate) { refreshed = DateTime.MinValue; generation++; } }
  private bool Fresh => DateTime.UtcNow - refreshed < TimeSpan.FromMinutes(2);
  internal object[] List()
  {
    Refresh(); var windows = Windows(); var foreground = Win32.GetForegroundWindow();
    lock (gate)
    {
      foreach (var window in windows)
      {
        if (entries.Values.Any(entry => Matches(window, entry))) continue;
        var entry = FromWindow(window); entries.TryAdd(entry.Id, entry);
      }
      return entries.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).Take(3000).Select(entry =>
      {
        var window = windows.FirstOrDefault(w => Matches(w, entry));
        return (object)new { id = entry.Id, name = entry.Name, running = window.Handle != 0, active = window.Handle != 0 && window.Handle == foreground, hidden = window.Handle != 0 && Win32.IsIconic(window.Handle) };
      }).ToArray();
    }
  }
  private static bool Matches(Window window, Entry entry) => Same(window.Path, entry.Executable) || Same(window.AppId, entry.AppId);
  private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
  private static string Id(string path) => "app:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24];
  private static Entry FromWindow(Window window)
  {
    // Packaged windows are matched by identity alone; a frame host's path is
    // shared by every UWP window.
    if (window.AppId is not null) return new("aumid:" + window.AppId, IsFrameHost(window.Path) ? window.Title : DisplayName(window.Path), "shell:AppsFolder\\" + window.AppId, null, window.AppId);
    return new(Id(window.Path), DisplayName(window.Path), window.Path, window.Path);
  }
  /// "Google Chrome" rather than "chrome" for apps found only by their window.
  private static string DisplayName(string path)
  {
    try
    {
      var info = FileVersionInfo.GetVersionInfo(path);
      var name = string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription;
      if (!string.IsNullOrWhiteSpace(name) && name.Length <= 80) return name.Trim();
    }
    catch (Exception error) when (error is FileNotFoundException or IOException or UnauthorizedAccessException) { }
    return Path.GetFileNameWithoutExtension(path);
  }
  private static bool IsFrameHost(string path) => Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
  private static bool IsUninstaller(string name, string target)
  {
    var file = Path.GetFileName(target);
    return name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) || file.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
        || file.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase) || target.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase);
  }
  /// The executable registered for http links, so "Browser" opens the
  /// person's default browser rather than always Edge.
  private static string? DefaultBrowser()
  {
    try
    {
      // Windows 11 24H2 and later record the choice in UserChoiceLatest.
      const string associations = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\";
      string? progId = null;
      foreach (var name in new[] { @"UserChoiceLatest\ProgId", "UserChoiceLatest", "UserChoice" })
      {
        using var key = Registry.CurrentUser.OpenSubKey(associations + name);
        if (key?.GetValue("ProgId") is string value && value.Length > 0) { progId = value; break; }
      }
      if (progId is null) return null;
      using var command = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
      if (command?.GetValue(null) is not string line) return null;
      line = line.Trim();
      var executable = line.StartsWith('"') ? line[1..Math.Max(1, line.IndexOf('"', 1))] : line.Split(' ')[0];
      return executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(executable) ? executable : null;
    }
    catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
  }
  private void Refresh()
  {
    int scansBefore;
    lock (gate) { if (Fresh) return; scansBefore = scans; }
    // The app list and many icons are asked for at once after an app
    // launches; they wait for one scan instead of each running their own.
    lock (refreshGate)
    {
      int started;
      lock (gate) { if (Fresh || scans != scansBefore) return; started = generation; }
      var found = worker.Run(() =>
      {
        var list = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The first entry for an executable or package wins, so built-ins
        // stand in for their Start Menu and AppsFolder duplicates.
        void Add(string name, string launch, string? executable = null, string? id = null, string? appId = null, string? iconLocation = null)
        {
          if ((executable is not null && executables.Contains(executable)) || (appId is not null && appIds.Contains(appId)))
          {
            // Keep stable built-in IDs while retaining a shortcut's custom
            // icon and the Store identity discovered later in the scan.
            var existing = list.Values.First(e => Same(e.Executable, executable) || Same(e.AppId, appId));
            list[existing.Id] = existing with { AppId = existing.AppId ?? appId, IconLocation = existing.IconLocation ?? iconLocation };
            if (appId is not null) appIds.Add(appId);
            return;
          }
          id ??= Id(launch);
          if (!list.TryAdd(id, new Entry(id, name, launch, executable, appId, iconLocation))) return;
          if (executable is not null) executables.Add(executable);
          if (appId is not null) appIds.Add(appId);
        }
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        Add("File Explorer", Path.Combine(windowsFolder, "explorer.exe"), Path.Combine(windowsFolder, "explorer.exe"), "builtin:explorer");
        var browser = DefaultBrowser();
        if (browser is not null) Add(DisplayName(browser), browser, browser, "builtin:browser");
        else
        {
          var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
          Add("Microsoft Edge", "microsoft-edge:about:blank", File.Exists(edge) ? edge : null, "builtin:browser");
        }
        var terminal = Path.Combine(apps, "wt.exe");
        if (File.Exists(terminal)) Add("Windows Terminal", terminal, null, "builtin:terminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App");
        else
        {
          var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
          Add("Windows PowerShell", powershell, powershell, "builtin:terminal");
        }
        // Windows 11's Notepad is packaged; System32\notepad.exe starts it.
        Add("Notepad", Path.Combine(system, "notepad.exe"), Path.Combine(system, "notepad.exe"), "builtin:notepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");
        Add("Calculator", "calculator:", null, "builtin:calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        Add("Settings", "ms-settings:", null, "builtin:settings", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel");
        object? shell = null;
        try
        {
          shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
          foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) }.Concat(folders()))
          {
            if (!Directory.Exists(folder)) continue;
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
            foreach (var path in Directory.EnumerateFiles(folder, "*", options).Where(p => p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Take(3000))
            {
              var name = Path.GetFileNameWithoutExtension(path);
              if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
              {
                object? link = null;
                try
                {
                  link = ((dynamic)shell!).CreateShortcut(path);
                  string target = ((dynamic)link).TargetPath;
                  if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsUninstaller(name, target) || !File.Exists(target)) continue;
                  string iconLocation = ((dynamic)link).IconLocation;
                  Add(name, path, target, iconLocation: string.IsNullOrWhiteSpace(iconLocation) ? path : iconLocation);
                }
                catch (COMException) { }
                finally { if (link is not null) Marshal.FinalReleaseComObject(link); }
              }
              else if (!IsUninstaller(name, path)) Add(name, path, path);
            }
          }
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException) { Diagnostics.Record(error); }
        finally { if (shell is not null) Marshal.FinalReleaseComObject(shell); }
        // AppsFolder includes packaged / Microsoft Store apps.
        object? explorer = null; object? folderObject = null; object? items = null;
        try
        {
          explorer = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
          folderObject = ((dynamic)explorer!).NameSpace("shell:AppsFolder"); items = ((dynamic)folderObject!).Items();
          var count = Math.Min((int)((dynamic)items).Count, 2000);
          for (var i = 0; i < count; i++)
          {
            object? item = null;
            try
            {
              item = ((dynamic)items).Item(i); string name = ((dynamic)item).Name; string path = ((dynamic)item).Path;
              if (path.Contains('!')) Add(name, "shell:AppsFolder\\" + path, null, "aumid:" + path, path);
            }
            finally { if (item is not null) Marshal.FinalReleaseComObject(item); }
          }
        }
        catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { Diagnostics.Record(error); }
        finally { foreach (var com in new[] { items, folderObject, explorer }) if (com is not null) Marshal.FinalReleaseComObject(com); }
        return list;
      });
      foreach (var window in Windows()) if (!found.Values.Any(e => Matches(window, e))) { var entry = FromWindow(window); found.TryAdd(entry.Id, entry); }
      lock (gate) { entries = found; scans++; if (generation == started) refreshed = DateTime.UtcNow; }
    }
  }
  internal bool Action(string id, string operation, Func<bool>? cancelled = null)
  {
    cancelled ??= () => false;
    if (cancelled()) return false;
    Refresh(); Entry? entry; lock (gate) entries.TryGetValue(id, out entry);
    if (entry is null || cancelled()) return false;
    var windows = Windows().Where(w => Matches(w, entry)).ToArray();
    if (operation == "quit") { foreach (var window in windows) Win32.PostMessageW(window.Handle, 0x10, 0, 0); return windows.Length != 0; }
    if (operation == "hide") { foreach (var window in windows) Win32.ShowWindowAsync(window.Handle, 6); return windows.Length != 0; }
    var existing = windows.FirstOrDefault();
    if (existing.Handle != 0)
    {
      if (operation == "toggle" && existing.Handle == Win32.GetForegroundWindow()) return Win32.ShowWindowAsync(existing.Handle, 6);
      return WaitForActivation(entry, cancelled);
    }
    try
    {
      if (entry.Launch.StartsWith("shell:", StringComparison.Ordinal)) Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false, ArgumentList = { entry.Launch } });
      else if (id == "builtin:terminal")
      {
        var terminal = entry.Launch;
        if (!File.Exists(terminal))
        {
          terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
          entry = entry with { Executable = terminal, AppId = null };
        }
        Process.Start(new ProcessStartInfo(terminal) { UseShellExecute = true });
      }
      else Process.Start(new ProcessStartInfo(entry.Launch) { UseShellExecute = true });
      // Packaged launchers may return before they create any window.
      Win32.AllowSetForegroundWindow(-1);
      Invalidate(); return WaitForActivation(entry, cancelled);
    }
    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Diagnostics.Record(error); return false; }
  }
  private bool WaitForActivation(Entry entry, Func<bool> cancelled) => ActivationWait.Run(() =>
  {
    var matches = Windows().Where(window => Matches(window, entry)).ToArray();
    var foreground = Win32.GetForegroundWindow();
    if (matches.Any(window => window.Handle == foreground)) return true;
    var window = matches.FirstOrDefault();
    if (window.Handle == 0 || cancelled()) return false;
    Win32.Activate(window.Handle);
    // SetForegroundWindow can return before activation has completed.
    return Win32.GetForegroundWindow() == window.Handle;
  }, cancelled);
  internal string? Icon(string id, int size)
  {
    Refresh(); Entry? entry; lock (gate) entries.TryGetValue(id, out entry);
    if (entry is null) return null;
    return worker.Run(() =>
    {
      // Try each source independently. A launcher/stub with no embedded icon
      // must not prevent reading its Store icon or shortcut icon.
      var edge = Math.Clamp(size, 16, 256);
      var sources = new[] {
        entry.AppId is null ? null : "shell:AppsFolder\\" + entry.AppId,
        entry.AppId is null ? null : "::{4234d49b-0245-4df3-b780-3893943456e1}\\" + entry.AppId,
        File.Exists(entry.Launch) ? entry.Launch : null,
        entry.Executable,
      }.Where(path => path is not null).Distinct(StringComparer.OrdinalIgnoreCase);
      Bitmap? bitmap = null;
      if (entry.IconLocation is not null) bitmap = ShellIcons.Resource(entry.IconLocation, edge);
      foreach (var source in sources)
      {
        if (bitmap is not null) break;
        bitmap = ShellIcons.Read(source!, edge);
      }
      if (bitmap is null) return null; // The phone supplies a legible, colored fallback.
      using (bitmap)
      {
        using var resized = new Bitmap(edge, edge, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(resized))
        {
          graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
          var scale = Math.Min((float)edge / bitmap.Width, (float)edge / bitmap.Height);
          var width = bitmap.Width * scale; var height = bitmap.Height * scale;
          graphics.DrawImage(bitmap, (edge - width) / 2, (edge - height) / 2, width, height);
        }
        using var stream = new MemoryStream(); resized.Save(stream, System.Drawing.Imaging.ImageFormat.Png); return Convert.ToBase64String(stream.ToArray());
      }
    });
  }
  /// App windows, enumerated on the STA worker because packaged apps are
  /// identified through the shell's window property store.
  private List<Window> Windows() => worker.Run(() =>
  {
    var result = new List<Window>();
    Win32.EnumWindows((window, _) =>
    {
      if (!Win32.IsAppWindow(window)) return true;
      var title = new StringBuilder(512); if (Win32.GetWindowTextW(window, title, title.Capacity) == 0) return true;
      Win32.GetWindowThreadProcessId(window, out var id);
      var process = OpenProcess(0x1000 | 0x100000, false, id); // PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE
      if (process != 0) try
        {
          // An app that quit can leave its windows behind for a moment while
          // it is torn down; it is no longer open.
          if (WaitForSingleObject(process, 0) == 0) return true;
          var path = new StringBuilder(4096); var length = (uint)path.Capacity;
          if (QueryFullProcessImageNameW(process, 0, path, ref length))
          {
            uint appLength = 0; GetApplicationUserModelId(process, ref appLength, null);
            string? appId = null;
            if (appLength is > 0 and < 1024) { var app = new StringBuilder((int)appLength); if (GetApplicationUserModelId(process, ref appLength, app) == 0) appId = app.ToString(); }
            var image = path.ToString();
            // UWP apps draw inside ApplicationFrameHost; the frame window
            // carries the hosted app's identity.
            if (appId is null && IsFrameHost(image))
            {
              appId = WindowAppId(window);
              if (appId is null) return true;
            }
            result.Add(new(window, image, appId, title.ToString()));
          }
        }
        finally { CloseHandle(process); }
      return true;
    }, 0);
    return result;
  });
  private static string? WindowAppId(nint window)
  {
    var iid = typeof(IPropertyStore).GUID; IPropertyStore? store = null; var value = new PropVariant();
    try
    {
      if (SHGetPropertyStoreForWindow(window, ref iid, out store) < 0) return null;
      var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 }; // PKEY_AppUserModel_ID
      if (store.GetValue(ref key, out value) < 0 || value.Type != 31) return null; // VT_LPWSTR
      return Marshal.PtrToStringUni(value.Pointer);
    }
    catch (COMException) { return null; }
    finally { PropVariantClear(ref value); if (store is not null) Marshal.FinalReleaseComObject(store); }
  }
  [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { internal Guid FormatId; internal uint PropertyId; }
  [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant { [FieldOffset(0)] internal ushort Type; [FieldOffset(8)] internal nint Pointer; }
  [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IPropertyStore
  {
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
  }
  [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
  [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
  [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint id);
  [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
  [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder path, ref uint size);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder? id);
}
