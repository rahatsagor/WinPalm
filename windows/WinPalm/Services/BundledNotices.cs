namespace WinPalm.Services;

internal static class BundledNotices
{
  internal const string FileName = "THIRD_PARTY_NOTICES.md";
  internal static void Verify(string directory)
  {
    var text = File.ReadAllText(Path.Combine(directory, FileName));
    foreach (var required in new[] { "WinPalm", "GNU AFFERO GENERAL PUBLIC LICENSE", "CmdPalm", "QRCoder", "System.Speech", "Microsoft.NETCore.App.Runtime.win-x64", "Microsoft.NETCore.App.Runtime.win-arm64", "Microsoft.WindowsDesktop.App.Runtime", "str0m-wincrypto", "vue", "@lucide/vue", "tailwindcss", "vite", "NSIS" })
      if (!text.Contains(required, StringComparison.Ordinal)) throw new InvalidDataException("Bundled license notices are incomplete: " + required);
  }
}
