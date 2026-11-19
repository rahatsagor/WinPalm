namespace WinPalm.Services;

/// Mirrors `link_problem` in the Rust core as defense-in-depth before passing
/// a phone-supplied URL to ShellExecute.
internal static class LinkPolicy
{
  internal static bool IsAllowed(string? rawUrl, bool allowShell)
  {
    if (string.IsNullOrWhiteSpace(rawUrl)) return false;
    var trimmed = rawUrl.Trim();
    var colon = trimmed.IndexOf(':');
    if (colon <= 0) return false;
    var scheme = trimmed[..colon].ToLowerInvariant();
    foreach (var ch in scheme)
    {
      if (ch is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '-' or '.')) return false;
    }
    if (scheme is "javascript" or "data") return false;
    if (scheme is "http" or "https" or "mailto") return true;
    return allowShell;
  }
}
