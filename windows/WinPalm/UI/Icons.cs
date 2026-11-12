using System.Drawing.Drawing2D;

namespace WinPalm.UI;

internal enum IconKind
{
  None,
  Connect,
  Devices,
  Deck,
  Automations,
  General,
  PhoneSetup,
  Advanced,
  Updates,
  About,
  Copy,
  Check,
  Refresh,
  Pause,
  Play,
  Stop,
  Lock,
  Globe,
  Mic,
  Folder,
  Plus,
  Edit,
  Trash,
  External,
  Warning,
  Shield,
  Key
}

internal static class Icons
{
  /// Draws crisp vector icons on a normalized 24x24 coordinate grid.
  internal static void Draw(Graphics g, IconKind kind, RectangleF bounds, Color color, float strokeWidth = 1.85f)
  {
    if (kind == IconKind.None || bounds.Width <= 2 || bounds.Height <= 2) return;
    var state = g.Save();
    try
    {
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;
      var scale = Math.Min(bounds.Width, bounds.Height) / 24f;
      g.TranslateTransform(bounds.X + (bounds.Width - 24f * scale) / 2f, bounds.Y + (bounds.Height - 24f * scale) / 2f);
      g.ScaleTransform(scale, scale);
      using var pen = new Pen(color, strokeWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
      using var fill = new SolidBrush(color);
      using var softFill = new SolidBrush(Color.FromArgb(Math.Max(28, color.A / 4), color));

      switch (kind)
      {
        case IconKind.Connect:
          DrawRoundedBox(g, pen, 3.2f, 3.2f, 6.8f, 6.8f, 1.5f);
          DrawRoundedBox(g, pen, 14f, 3.2f, 6.8f, 6.8f, 1.5f);
          DrawRoundedBox(g, pen, 3.2f, 14f, 6.8f, 6.8f, 1.5f);
          g.FillRectangle(fill, 5.7f, 5.7f, 1.8f, 1.8f);
          g.FillRectangle(fill, 16.5f, 5.7f, 1.8f, 1.8f);
          g.FillRectangle(fill, 5.7f, 16.5f, 1.8f, 1.8f);
          g.DrawLine(pen, 14.2f, 14.2f, 14.2f, 17.2f);
          g.DrawLine(pen, 14.2f, 20.5f, 16.8f, 20.5f);
          g.DrawLine(pen, 17.6f, 14.2f, 20.6f, 14.2f);
          g.DrawLine(pen, 20.6f, 17.4f, 20.6f, 20.5f);
          g.FillEllipse(fill, 16.7f, 16.7f, 2.1f, 2.1f);
          break;

        case IconKind.Devices:
          DrawRoundedBox(g, pen, 6.5f, 2.5f, 11f, 19f, 2.4f);
          g.DrawLine(pen, 10.4f, 5.4f, 13.6f, 5.4f);
          g.FillEllipse(fill, 11f, 17.6f, 2f, 2f);
          break;

        case IconKind.Deck:
          DrawRoundedBox(g, pen, 3.4f, 3.4f, 7.1f, 7.1f, 1.8f);
          using (var accentPath = Theme.RoundedRect(new RectangleF(13.5f, 3.4f, 7.1f, 7.1f), 1.8f))
          {
            g.FillPath(softFill, accentPath);
            g.DrawPath(pen, accentPath);
          }
          DrawRoundedBox(g, pen, 3.4f, 13.5f, 7.1f, 7.1f, 1.8f);
          DrawRoundedBox(g, pen, 13.5f, 13.5f, 7.1f, 7.1f, 1.8f);
          break;

        case IconKind.Automations:
          DrawRoundedBox(g, pen, 3f, 4f, 18f, 16f, 2.6f);
          g.DrawLines(pen, [new PointF(7.4f, 9.4f), new PointF(10.6f, 12f), new PointF(7.4f, 14.6f)]);
          g.DrawLine(pen, 12.6f, 14.8f, 16.6f, 14.8f);
          break;

        case IconKind.General:
          g.DrawLine(pen, 3.5f, 6.5f, 6.8f, 6.5f);
          g.DrawLine(pen, 11.6f, 6.5f, 20.5f, 6.5f);
          g.DrawEllipse(pen, 7f, 4.3f, 4.4f, 4.4f);
          g.DrawLine(pen, 3.5f, 12f, 12.8f, 12f);
          g.DrawLine(pen, 17.6f, 12f, 20.5f, 12f);
          g.DrawEllipse(pen, 13f, 9.8f, 4.4f, 4.4f);
          g.DrawLine(pen, 3.5f, 17.5f, 8.3f, 17.5f);
          g.DrawLine(pen, 13.1f, 17.5f, 20.5f, 17.5f);
          g.DrawEllipse(pen, 8.5f, 15.3f, 4.4f, 4.4f);
          break;

        case IconKind.PhoneSetup:
        case IconKind.Shield:
          using (var shield = new GraphicsPath())
          {
            shield.AddLines([new PointF(12f, 2.8f), new PointF(19.4f, 5.8f), new PointF(19.4f, 11.6f)]);
            shield.AddBezier(new PointF(19.4f, 11.6f), new PointF(19.4f, 16.6f), new PointF(15.8f, 19.6f), new PointF(12f, 21.2f));
            shield.AddBezier(new PointF(12f, 21.2f), new PointF(8.2f, 19.6f), new PointF(4.6f, 16.6f), new PointF(4.6f, 11.6f));
            shield.AddLines([new PointF(4.6f, 11.6f), new PointF(4.6f, 5.8f), new PointF(12f, 2.8f)]);
            shield.CloseFigure();
            g.FillPath(softFill, shield);
            g.DrawPath(pen, shield);
          }
          if (kind == IconKind.PhoneSetup)
            g.DrawLines(pen, [new PointF(9f, 12.1f), new PointF(11.1f, 14.2f), new PointF(15.2f, 9.9f)]);
          break;

        case IconKind.Advanced:
          DrawRoundedBox(g, pen, 3.2f, 4.2f, 17.6f, 6.4f, 2f);
          DrawRoundedBox(g, pen, 3.2f, 13.4f, 17.6f, 6.4f, 2f);
          g.FillEllipse(fill, 6.3f, 6.4f, 2f, 2f);
          g.FillEllipse(fill, 6.3f, 15.6f, 2f, 2f);
          g.DrawLine(pen, 12.5f, 7.4f, 17.4f, 7.4f);
          g.DrawLine(pen, 12.5f, 16.6f, 17.4f, 16.6f);
          break;

        case IconKind.Updates:
          g.DrawEllipse(pen, 3.2f, 3.2f, 17.6f, 17.6f);
          g.DrawLine(pen, 12f, 16.2f, 12f, 8.2f);
          g.DrawLines(pen, [new PointF(8.6f, 11.4f), new PointF(12f, 8f), new PointF(15.4f, 11.4f)]);
          break;

        case IconKind.About:
          g.DrawEllipse(pen, 3.2f, 3.2f, 17.6f, 17.6f);
          g.DrawLine(pen, 12f, 11.2f, 12f, 16.4f);
          g.FillEllipse(fill, 10.9f, 7.1f, 2.2f, 2.2f);
          break;

        case IconKind.Copy:
          DrawRoundedBox(g, pen, 8.5f, 8.5f, 11.5f, 11.5f, 2f);
          using (var back = new GraphicsPath())
          {
            back.AddLines([new PointF(5.5f, 15f), new PointF(4f, 15f), new PointF(4f, 4f), new PointF(15f, 4f), new PointF(15f, 5.5f)]);
            g.DrawPath(pen, back);
          }
          break;

        case IconKind.Check:
          g.DrawLines(pen, [new PointF(4.8f, 12.6f), new PointF(9.6f, 17.4f), new PointF(19.2f, 7.2f)]);
          break;

        case IconKind.Refresh:
          g.DrawArc(pen, 4f, 4f, 16f, 16f, 35f, 290f);
          g.DrawLines(pen, [new PointF(15.4f, 3.8f), new PointF(19.6f, 4.4f), new PointF(18.8f, 8.6f)]);
          break;

        case IconKind.Pause:
          using (var left = Theme.RoundedRect(new RectangleF(6.8f, 5f, 3.5f, 14f), 1.2f)) g.FillPath(fill, left);
          using (var right = Theme.RoundedRect(new RectangleF(13.7f, 5f, 3.5f, 14f), 1.2f)) g.FillPath(fill, right);
          break;

        case IconKind.Play:
          using (var tri = new GraphicsPath())
          {
            tri.AddPolygon([new PointF(7.6f, 5.2f), new PointF(18.6f, 12f), new PointF(7.6f, 18.8f)]);
            g.FillPath(fill, tri);
          }
          break;

        case IconKind.Stop:
          using (var sq = Theme.RoundedRect(new RectangleF(6.4f, 6.4f, 11.2f, 11.2f), 2.2f)) g.FillPath(fill, sq);
          break;

        case IconKind.Lock:
          DrawRoundedBox(g, pen, 5.4f, 10.8f, 13.2f, 9.6f, 2.2f);
          g.DrawArc(pen, 8f, 4.2f, 8f, 9.2f, 180f, 180f);
          g.DrawLine(pen, 8f, 8.8f, 8f, 10.8f);
          g.DrawLine(pen, 16f, 8.8f, 16f, 10.8f);
          g.FillEllipse(fill, 10.9f, 14.4f, 2.2f, 2.2f);
          break;

        case IconKind.Globe:
          g.DrawEllipse(pen, 3.2f, 3.2f, 17.6f, 17.6f);
          g.DrawLine(pen, 3.6f, 12f, 20.4f, 12f);
          g.DrawEllipse(pen, 8f, 3.2f, 8f, 17.6f);
          break;

        case IconKind.Mic:
          DrawRoundedBox(g, pen, 9f, 3.2f, 6f, 10.6f, 3f);
          g.DrawArc(pen, 5.8f, 7.2f, 12.4f, 10.2f, 0f, 180f);
          g.DrawLine(pen, 12f, 17.4f, 12f, 20.6f);
          g.DrawLine(pen, 8.8f, 20.6f, 15.2f, 20.6f);
          break;

        case IconKind.Folder:
          using (var folder = new GraphicsPath())
          {
            folder.AddLines([
              new PointF(3.2f, 6.2f), new PointF(9.4f, 6.2f), new PointF(11.4f, 8.5f),
              new PointF(20.8f, 8.5f), new PointF(20.8f, 18.8f), new PointF(3.2f, 18.8f)
            ]);
            folder.CloseFigure();
            g.FillPath(softFill, folder);
            g.DrawPath(pen, folder);
          }
          break;

        case IconKind.Plus:
          g.DrawLine(pen, 12f, 5.2f, 12f, 18.8f);
          g.DrawLine(pen, 5.2f, 12f, 18.8f, 12f);
          break;

        case IconKind.Edit:
          using (var pencil = new GraphicsPath())
          {
            pencil.AddPolygon([new PointF(15.8f, 4.2f), new PointF(19.8f, 8.2f), new PointF(8.6f, 19.4f), new PointF(4.2f, 19.8f), new PointF(4.6f, 15.4f)]);
            g.DrawPath(pen, pencil);
          }
          break;

        case IconKind.Trash:
          g.DrawLine(pen, 4.5f, 6.8f, 19.5f, 6.8f);
          g.DrawLines(pen, [new PointF(9.4f, 6.8f), new PointF(9.4f, 4.4f), new PointF(14.6f, 4.4f), new PointF(14.6f, 6.8f)]);
          g.DrawLines(pen, [new PointF(6.6f, 6.8f), new PointF(7.6f, 19.8f), new PointF(16.4f, 19.8f), new PointF(17.4f, 6.8f)]);
          g.DrawLine(pen, 10.3f, 10.4f, 10.6f, 16.4f);
          g.DrawLine(pen, 13.7f, 10.4f, 13.4f, 16.4f);
          break;

        case IconKind.External:
          g.DrawLines(pen, [new PointF(13f, 4.5f), new PointF(19.5f, 4.5f), new PointF(19.5f, 11f)]);
          g.DrawLine(pen, 10.5f, 13.5f, 19.2f, 4.8f);
          g.DrawLines(pen, [new PointF(10f, 6.5f), new PointF(5f, 6.5f), new PointF(5f, 19f), new PointF(17.5f, 19f), new PointF(17.5f, 14f)]);
          break;

        case IconKind.Warning:
          using (var tri = new GraphicsPath())
          {
            tri.AddPolygon([new PointF(12f, 3.6f), new PointF(21f, 19.4f), new PointF(3f, 19.4f)]);
            g.FillPath(softFill, tri);
            g.DrawPath(pen, tri);
          }
          g.DrawLine(pen, 12f, 9.4f, 12f, 13.8f);
          g.FillEllipse(fill, 10.9f, 15.8f, 2.2f, 2.2f);
          break;

        case IconKind.Key:
          g.DrawEllipse(pen, 3.5f, 8.2f, 7.6f, 7.6f);
          g.DrawLine(pen, 11.1f, 12f, 20.5f, 12f);
          g.DrawLine(pen, 16.8f, 12f, 16.8f, 15.4f);
          g.DrawLine(pen, 19.8f, 12f, 19.8f, 14.8f);
          break;
      }
    }
    finally
    {
      g.Restore(state);
    }
  }

  private static void DrawRoundedBox(Graphics g, Pen pen, float x, float y, float w, float h, float r)
  {
    using var path = Theme.RoundedRect(new RectangleF(x, y, w, h), r);
    g.DrawPath(pen, path);
  }

  /// Vector rendering of Resources/icon.svg so brand badges scale cleanly at any DPI.
  internal static void DrawBrandMark(Graphics g, RectangleF bounds)
  {
    if (bounds.Width <= 4 || bounds.Height <= 4) return;
    var state = g.Save();
    try
    {
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;
      var scale = Math.Min(bounds.Width, bounds.Height) / 512f;
      g.TranslateTransform(bounds.X + (bounds.Width - 512f * scale) / 2f, bounds.Y + (bounds.Height - 512f * scale) / 2f);
      g.ScaleTransform(scale, scale);

      using (var bgPath = Theme.RoundedRect(new RectangleF(0, 0, 512, 512), 114f))
      using (var bgBrush = new LinearGradientBrush(new PointF(64, 0), new PointF(464, 512), Color.FromArgb(255, 180, 95), Color.FromArgb(207, 79, 40)))
      {
        bgBrush.InterpolationColors = new ColorBlend
        {
          Colors = [Color.FromArgb(255, 180, 95), Color.FromArgb(249, 129, 61), Color.FromArgb(207, 79, 40)],
          Positions = [0f, 0.48f, 1f]
        };
        g.FillPath(bgBrush, bgPath);
        using var rim = new Pen(Color.FromArgb(52, 255, 241, 215), 8f);
        g.DrawPath(rim, bgPath);
      }

      var phoneState = g.Save();
      g.TranslateTransform(256f, 256f);
      g.RotateTransform(-9f);
      g.TranslateTransform(-256f, -256f);

      using (var shadowPath = Theme.RoundedRect(new RectangleF(147, 118, 218, 304), 52f))
      using (var shadowBrush = new SolidBrush(Color.FromArgb(68, 118, 43, 27)))
        g.FillPath(shadowBrush, shadowPath);

      using (var baseLip = Theme.RoundedRect(new RectangleF(147, 106, 218, 302), 52f))
      using (var lipBrush = new SolidBrush(Color.FromArgb(239, 208, 166)))
        g.FillPath(lipBrush, baseLip);

      using (var facePath = Theme.RoundedRect(new RectangleF(147, 101, 218, 302), 52f))
      using (var faceBrush = new LinearGradientBrush(new PointF(163, 118), new PointF(344, 410), Color.FromArgb(255, 254, 248), Color.FromArgb(242, 212, 173)))
      {
        g.FillPath(faceBrush, facePath);
        using var faceStroke = new Pen(Color.FromArgb(168, 255, 255, 255), 5f);
        g.DrawPath(faceStroke, facePath);
      }

      using (var bezel = Theme.RoundedRect(new RectangleF(163, 117, 186, 270), 37f))
      using (var bezelPen = new Pen(Color.FromArgb(72, 214, 166, 119), 4f))
        g.DrawPath(bezelPen, bezel);

      using (var speakerPen = new Pen(Color.FromArgb(130, 214, 152, 96), 8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        g.DrawLine(speakerPen, 234f, 137f, 278f, 137f);

      var logoState = g.Save();
      g.TranslateTransform(256f, 272f);
      g.RotateTransform(-6f);
      using (var keyBrush = new LinearGradientBrush(new RectangleF(-72, -78, 146, 156), Color.FromArgb(218, 103, 51), Color.FromArgb(186, 63, 32), LinearGradientMode.ForwardDiagonal))
      {
        g.FillPolygon(keyBrush, [new PointF(-67, -57), new PointF(-7, -65), new PointF(-7, -7), new PointF(-67, -7)]);
        g.FillPolygon(keyBrush, [new PointF(7, -67), new PointF(69, -76), new PointF(69, -7), new PointF(7, -7)]);
        g.FillPolygon(keyBrush, [new PointF(-67, 7), new PointF(-7, 7), new PointF(-7, 65), new PointF(-67, 57)]);
        g.FillPolygon(keyBrush, [new PointF(7, 7), new PointF(69, 7), new PointF(69, 76), new PointF(7, 67)]);
      }
      g.Restore(logoState);

      using (var ripplePen = new Pen(Color.FromArgb(110, 255, 249, 237), 5f))
        g.DrawEllipse(ripplePen, 309f, 96f, 86f, 86f);

      using (var touchBrush = new LinearGradientBrush(new PointF(333, 114), new PointF(375, 158), Color.FromArgb(255, 170, 85), Color.FromArgb(236, 104, 46)))
      {
        g.FillEllipse(touchBrush, 321f, 108f, 62f, 62f);
        using var touchStroke = new Pen(Color.FromArgb(255, 249, 237), 9f);
        g.DrawEllipse(touchStroke, 321f, 108f, 62f, 62f);
      }
      using (var glint = new SolidBrush(Color.FromArgb(120, 255, 226, 175)))
        g.FillEllipse(glint, 335f, 121f, 16f, 16f);

      g.Restore(phoneState);
    }
    finally
    {
      g.Restore(state);
    }
  }
}
