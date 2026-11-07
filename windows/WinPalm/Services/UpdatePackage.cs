using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace WinPalm.Services;

internal static class UpdatePackage
{
  internal static readonly string[] OwnedItems = ["WinPalm.exe", "winpalm_core.dll", "web", "Resources", "LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md", "README.md", "SECURITY.md", "Test-WinPalm.cmd"];
  private static readonly string[] RequiredFiles = ["WinPalm.exe", "winpalm_core.dll", "web/index.html", "web/source.zip", "web/LICENSE.txt", "Resources/WinPalm.ico", "LICENSE"];

  internal static void Extract(string archivePath, string destination, CancellationToken cancellation = default)
  {
    if (Directory.Exists(destination)) throw new IOException("The update staging folder already exists.");
    using var archive = ZipFile.OpenRead(archivePath);
    if (archive.Entries.Count is < 1 or > 10000) throw new InvalidDataException("The update package has an invalid number of files.");
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    long total = 0;
    // Validate the entire archive before creating any files.
    foreach (var entry in archive.Entries)
    {
      cancellation.ThrowIfCancellationRequested();
      var path = entry.FullName.TrimEnd('/');
      ValidateName(path);
      if (!seen.Add(path)) throw new InvalidDataException("The update package contains duplicate paths.");
      var directory = entry.FullName.EndsWith('/');
      if (directory && path is not ("web" or "Resources") && !path.StartsWith("web/", StringComparison.Ordinal) && !path.StartsWith("Resources/", StringComparison.Ordinal)) throw new InvalidDataException("The update contains an unexpected directory.");
      if (!directory && path is "web" or "Resources") throw new InvalidDataException("An update folder was supplied as a file.");
      var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
      if (unixType is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The update package contains a link or special file.");
      if (entry.Length < 0 || entry.Length > 300L * 1024 * 1024 || (total += entry.Length) > 1024L * 1024 * 1024) throw new InvalidDataException("The expanded update is too large.");
    }
    foreach (var file in RequiredFiles) if (!seen.Contains(file)) throw new InvalidDataException("The update package is incomplete: " + file);
    Directory.CreateDirectory(destination);
    try
    {
      foreach (var entry in archive.Entries)
      {
        cancellation.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
        if (!path.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("The update path escapes its staging folder.");
        if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(path);
        else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path, false); }
      }
    }
    catch { Directory.Delete(destination, true); throw; }
  }

  private static void ValidateName(string name)
  {
    var parts = name.Split('/');
    if (parts.Length == 0 || !OwnedItems.Contains(parts[0], StringComparer.Ordinal)
        || (parts.Length > 1 && parts[0] is not ("web" or "Resources"))) throw new InvalidDataException("The update contains an unexpected file.");
    foreach (var part in parts)
    {
      if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32 || "\\:*?\"<>|".Contains(c))
          || Regex.IsMatch(part.Split('.')[0], @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw new InvalidDataException("The update contains an unsafe Windows path.");
    }
  }

  internal static void ValidateBinaries(string payload, string architecture, Version version)
  {
    ValidateArchitecture(payload, architecture);
    var info = FileVersionInfo.GetVersionInfo(Path.Combine(payload, "WinPalm.exe"));
    if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != version) throw new InvalidDataException("The update's version does not match its release.");
  }

  internal static void ValidateArchitecture(string payload, string architecture)
  {
    var machine = architecture switch { "x64" => 0x8664, "arm64" => 0xaa64, _ => throw new InvalidDataException("Unknown update architecture.") };
    foreach (var name in new[] { "WinPalm.exe", "winpalm_core.dll" })
    {
      using var file = File.OpenRead(Path.Combine(payload, name));
      using var reader = new BinaryReader(file);
      if (file.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("The update contains an invalid Windows binary.");
      file.Position = 0x3c; var offset = reader.ReadUInt32();
      if (offset > file.Length - 6) throw new InvalidDataException("The update binary header is invalid.");
      file.Position = offset;
      if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != machine) throw new InvalidDataException("The update is for a different processor.");
    }
  }

  internal static void EnsureNoLinks(string path)
  {
    if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("The app folder contains a linked file or folder. Use the release package instead.");
    for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
      if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("In-app updates cannot replace files through a linked folder. Use the release package instead.");
    if (!Directory.Exists(path)) return;
    foreach (var child in Directory.EnumerateFileSystemEntries(path))
    {
      if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("The app folder contains a linked file or folder. Use the release package instead.");
      if (Directory.Exists(child)) EnsureNoLinks(child);
    }
  }

  /// The caller has stopped WinPalm. Keep the old package until restart succeeds.
  internal static void Install(string payload, string target, string backup, Action<string, string>? copyFile = null)
  {
    EnsureNoLinks(payload);
    foreach (var item in OwnedItems) EnsureNoLinks(Path.Combine(target, item));
    if (Directory.Exists(backup)) throw new IOException("An update backup already exists.");
    Directory.CreateDirectory(backup);
    var moved = new List<string>(); var installed = new List<string>();
    try
    {
      foreach (var item in OwnedItems)
      {
        var path = Path.Combine(target, item);
        if (!Path.Exists(path)) continue;
        Move(path, Path.Combine(backup, item)); moved.Add(item);
      }
      foreach (var item in OwnedItems)
      {
        var path = Path.Combine(payload, item);
        if (!Path.Exists(path)) continue;
        installed.Add(item); Copy(path, Path.Combine(target, item), copyFile);
      }
    }
    catch
    {
      foreach (var item in installed) Remove(Path.Combine(target, item));
      foreach (var item in moved) Move(Path.Combine(backup, item), Path.Combine(target, item));
      Directory.Delete(backup); throw;
    }
  }

  internal static void RollBack(string target, string backup)
  {
    foreach (var item in OwnedItems)
    {
      Remove(Path.Combine(target, item));
      var old = Path.Combine(backup, item);
      if (Path.Exists(old)) Move(old, Path.Combine(target, item));
    }
    Directory.Delete(backup);
  }
  private static void Copy(string from, string to, Action<string, string>? copyFile)
  {
    if (!Directory.Exists(from)) { if (copyFile is null) File.Copy(from, to); else copyFile(from, to); return; }
    Directory.CreateDirectory(to);
    foreach (var child in Directory.EnumerateFileSystemEntries(from)) Copy(child, Path.Combine(to, Path.GetFileName(child)), copyFile);
  }
  private static void Move(string from, string to) { if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to); }
  private static void Remove(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); }
}
