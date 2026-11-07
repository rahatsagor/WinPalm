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
      if (factory.GetImage(new Size(size, size), 4, out bitmap) < 0 || bitmap == 0) return null;
      return Image.FromHbitmap(bitmap);
    }
    catch (COMException) { return null; }
    finally { if (bitmap != 0) DeleteObject(bitmap); if (factory is not null) Marshal.FinalReleaseComObject(factory); }
  }
  [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IShellItemImageFactory { [PreserveSig] int GetImage(Size size, uint flags, out nint bitmap); }
  [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string name, nint context, ref Guid iid, out IShellItemImageFactory factory);
  [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
