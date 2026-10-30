using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrimDeck.Native;

// The icon Windows shows for an application in its Apps folder (the Start menu's list), found by the application's user model
// id. Desktop applications that send notifications through a Start menu shortcut (QQ, WeChat's installer, most Win32 programs)
// give no logo through the notification itself; the shell still knows their icon from the shortcut or the program. Read on a
// thread of its own in the single-threaded apartment the shell's objects expect; returned as PNG bytes.
internal static class ShellAppIcon
{
    public static Task<byte[]?> PngAsync(string appUserModelId, int size = 128)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId)) return Task.FromResult<byte[]?>(null);
        var done = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(Read(appUserModelId, size)); }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or IOException or NotSupportedException) { done.SetResult(null); }
        }) { IsBackground = true, Name = "BrimDeck app icon" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static byte[]? Read(string appUserModelId, int size)
    {
        var factoryId = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appUserModelId, IntPtr.Zero, ref factoryId, out var factory) != 0 || factory is null) return null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, IconOnly | BiggerSizeOk, out bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            if (Pixels(bitmap) is not { } source) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            Marshal.ReleaseComObject(factory);
        }
    }

    // The shell gives a 32-bit section with premultiplied alpha, usually bottom-up; its rows are copied as they are, flipped if needed.
    private static BitmapSource? Pixels(IntPtr bitmap)
    {
        var section = new DibSection();
        if (GetObject(bitmap, Marshal.SizeOf<DibSection>(), ref section) == 0) return null;
        int width = section.Width, height = Math.Abs(section.Height);
        if (width <= 0 || height <= 0 || section.BitsPerPixel != 32 || section.Bits == IntPtr.Zero) return null;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        bool bottomUp = section.HeaderHeight > 0;
        for (int row = 0; row < height; row++)
            Marshal.Copy(section.Bits + (bottomUp ? height - 1 - row : row) * stride, pixels, row * stride, stride);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private const int BiggerSizeOk = 0x1, IconOnly = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    // BITMAP followed by the BITMAPINFOHEADER of a DIBSECTION; only what is read here is named.
    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPerPixel;
        public IntPtr Bits;
        public int HeaderSize, HeaderWidth, HeaderHeight;
        public ushort HeaderPlanes, HeaderBitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        public int Mask0, Mask1, Mask2;
        public IntPtr Section;
        public int Offset;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32")] private static extern int GetObject(IntPtr handle, int size, ref DibSection section);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr handle);
}
