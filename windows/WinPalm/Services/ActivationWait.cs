using System.Diagnostics;

namespace WinPalm.Services;

/// An app launch is complete only when its window is actually in front.
internal static class ActivationWait
{
  internal static bool Run(Func<bool> activate, Func<bool> cancelled, TimeSpan? timeout = null)
  {
    var watch = Stopwatch.StartNew();
    var limit = timeout ?? TimeSpan.FromSeconds(5);
    while (!cancelled())
    {
      if (activate()) return !cancelled();
      if (watch.Elapsed >= limit) return false;
      Thread.Sleep(25);
    }
    return false;
  }
}
