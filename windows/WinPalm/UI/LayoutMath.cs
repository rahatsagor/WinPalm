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

  /// Places a stacked overlay popup near the bottom-right corner of the working
  /// area while keeping its entire frame inside visible screen bounds.
  internal static (int X, int Y) ClampOverlayOrigin(
    int areaLeft,
    int areaTop,
    int areaRight,
    int areaBottom,
    int width,
    int height,
    int margin,
    int stackIndex,
    int stackStep)
  {
    var offset = Math.Clamp(stackIndex, 0, 4) * Math.Max(0, stackStep);
    var minX = areaLeft + margin;
    var minY = areaTop + margin;
    var maxX = Math.Max(minX, areaRight - width - margin);
    var maxY = Math.Max(minY, areaBottom - height - margin);
    var desiredX = areaRight - width - margin - offset;
    var desiredY = areaBottom - height - margin - offset;
    return (Math.Clamp(desiredX, minX, maxX), Math.Clamp(desiredY, minY, maxY));
  }
}
