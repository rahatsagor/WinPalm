using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
namespace WinPalm.Services;

internal static class ShellIcons
{
  internal static Bitmap? Read(string parsingName, int size)
  {
    IShellItemImageFactory? factory = null; nint bitmap = 0;
    try
    {
      var iid = typeof(IShellItemImageFactory).GUID;
      if (SHCreateItemFromParsingName(parsingName, 0, ref iid, out factory) < 0) return null;
      // ICONONLY | BIGGERSIZEOK: ask the shell for the real per-app icon,
      // including packaged apps, then resize with a high quality filter.
      if (factory.GetImage(new Size(size, size), 5, out bitmap) < 0 || bitmap == 0) return null;
      return WithAlpha(bitmap);
    }
    catch (Exception error) when (error is COMException or ArgumentException or ExternalException or IOException or UnauthorizedAccessException) { return null; }
    finally { if (bitmap != 0) DeleteObject(bitmap); if (factory is not null) Marshal.FinalReleaseComObject(factory); }
  }

  /// A shortcut may point at a launcher but carry its real icon separately,
  /// for example "C:\\Games\\game.dll,-101" or an environment-expanded .ico.
  internal static Bitmap? Resource(string location, int size)
  {
    var path = Environment.ExpandEnvironmentVariables(location.Trim());
    var index = 0;
    var comma = path.LastIndexOf(',');
    if (comma >= 0 && int.TryParse(path[(comma + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) path = path[..comma].Trim();
    path = path.Trim('"');
    if (!File.Exists(path)) return null;
    if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return Read(path, size);
    var handles = new nint[1];
    try
    {
      var count = PrivateExtractIconsW(path, index, size, size, handles, null, 1, 0);
      if (count is 0 or uint.MaxValue || handles[0] == 0) return null;
      using var icon = System.Drawing.Icon.FromHandle(handles[0]);
      return icon.ToBitmap();
    }
    catch (Exception error) when (error is ArgumentException or ExternalException) { return null; }
    finally { if (handles[0] != 0) DestroyIcon(handles[0]); }
  }

  /// Image.FromHbitmap discards the alpha channel. Store logos and modern
  /// shortcut icons need the original BGRA pixels to avoid black squares.
  private static Bitmap? WithAlpha(nint handle)
  {
    if (GetObjectW(handle, Marshal.SizeOf<NativeBitmap>(), out var native) == 0 || native.Width is < 1 or > 1024 || native.Height is < 1 or > 1024) return null;
    var pixels = new byte[checked(native.Width * native.Height * 4)];
    var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = native.Width, Height = -native.Height, Planes = 1, BitCount = 32 };
    var dc = CreateCompatibleDC(0);
    try { if (dc == 0 || GetDIBits(dc, handle, 0, (uint)native.Height, pixels, ref info, 0) != native.Height) return null; }
    finally { if (dc != 0) DeleteDC(dc); }
    var hasAlpha = false;
    for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
    // Legacy RGB bitmaps have unused, zero alpha bytes.
    if (!hasAlpha) for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
    var result = new Bitmap(native.Width, native.Height, PixelFormat.Format32bppPArgb);
    var data = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
    try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
    finally { result.UnlockBits(data); }
    return result;
  }

  [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { internal int Type, Width, Height, WidthBytes; internal ushort Planes, BitsPixel; internal nint Bits; }
  [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { internal uint Size; internal int Width, Height; internal ushort Planes, BitCount; internal uint Compression, SizeImage; internal int XPels, YPels; internal uint Used, Important; }
  [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IShellItemImageFactory { [PreserveSig] int GetImage(Size size, uint flags, out nint bitmap); }
  [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string name, nint context, ref Guid iid, out IShellItemImageFactory factory);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint PrivateExtractIconsW(string file, int index, int width, int height, [Out] nint[] icons, [Out] uint[]? ids, uint count, uint flags);
  [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
  [DllImport("gdi32.dll")] private static extern int GetObjectW(nint handle, int size, out NativeBitmap bitmap);
  [DllImport("gdi32.dll")] private static extern uint GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] pixels, ref BitmapInfo info, uint usage);
  [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
  [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
  [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
