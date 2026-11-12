namespace WinPalm.UI;

internal readonly record struct HeaderTextLayout(
  int TitleMaxWidth,
  int TitleWidth,
  int TitleHeight,
  int PillWidth,
  bool PillBelowTitle,
  int HeaderHeight);

internal static class LayoutMath
{
  /// Computes title wrapping width and total header height when an optional
  /// status pill shares the header row, falling back to stacking the pill
  /// below the title when horizontal space is tight.
  internal static HeaderTextLayout MeasureHeader(
    int availableWidth,
    int pillWidth,
    int pillHeight,
    int pillGap,
    int belowGap,
    int minInlineTitleWidth,
    Func<int, (int Width, int Height)> measureTitle)
  {
    var safeWidth = Math.Max(16, availableWidth);
    if (pillWidth <= 0)
    {
      var (tw, th) = measureTitle(safeWidth);
      return new HeaderTextLayout(safeWidth, tw, th, 0, false, th);
    }

    var inlineTitleWidth = safeWidth - pillWidth - pillGap;
    if (inlineTitleWidth >= minInlineTitleWidth)
    {
      var (tw, th) = measureTitle(inlineTitleWidth);
      return new HeaderTextLayout(inlineTitleWidth, tw, th, pillWidth, false, Math.Max(th, pillHeight));
    }

    var clampedPill = Math.Min(pillWidth, safeWidth);
    var (fullW, fullH) = measureTitle(safeWidth);
    return new HeaderTextLayout(safeWidth, fullW, fullH, clampedPill, true, fullH + belowGap + pillHeight);
  }
}
