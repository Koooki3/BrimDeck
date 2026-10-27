using System.Diagnostics;
using System.Runtime.InteropServices;
using BrimDeck.LiquidGlass;
using BrimDeck.Native;

// Automatic liquid glass's first look, taken before the island is shown (BackdropLook.Glance, App.GlanceGlass): one synchronous
// measure of the desktop at the top middle of the primary monitor. Checks that it answers, quickly enough to sit before the first
// paint, and that its answer is a brightness. The pixels are only averaged, never kept. Skipped where Desktop Duplication is not
// available.
internal static class GlanceTests
{
    public static void Run(Action<string, bool> check)
    {
        // Device pixels, as BrimDeck (per-monitor v2) sees them.
        var awareness = SetThreadDpiAwarenessContext(-4);
        try
        {
            var bounds = WindowsHost.PrimaryScreen().Bounds;
            var monitor = MonitorFromPoint(0, 1);
            // The region's brightness frame by frame, just before and just after the first look: a video or a game there changes it
            // between frames, so the look is held to what the desktop showed around it rather than to one average taken later.
            var before = Frames(bounds, monitor);
            var clock = Stopwatch.StartNew();
            var luma = BackdropLook.Glance(bounds, monitor);
            long cold = clock.ElapsedMilliseconds;
            if (luma is null) { Console.WriteLine($"SKIP: first look (no capture in this session, gave up after {cold} ms)."); return; }
            clock.Restart();
            var again = BackdropLook.Glance(bounds, monitor);
            long warm = clock.ElapsedMilliseconds;
            var after = Frames(bounds, monitor);
            var seen = before.Concat(after).ToList();
            double low = seen.Count > 0 ? seen.Min() : double.NaN, high = seen.Count > 0 ? seen.Max() : double.NaN;
            // On a still desktop the frames agree and the look must be within .05 of them, as before; while it changes, within .05 of
            // the range they span.
            check(FormattableString.Invariant($"first look: the desktop where the island will be is measured before it is shown (luma {luma:0.000}, again {again:0.000}, frames {low:0.000}-{high:0.000}, {cold} ms, again {warm} ms)"),
                luma is >= 0 and <= 1 && again is >= 0 and <= 1 && warm < 400 && (seen.Count == 0 || luma >= low - .05 && luma <= high + .05 && again >= low - .05 && again <= high + .05));
        }
        finally { SetThreadDpiAwarenessContext(awareness); }
    }

    // The region's brightness in each frame captured over some 200 ms (as the first look takes it); none without a capture.
    private static List<double> Frames(System.Windows.Rect bounds, IntPtr monitor)
    {
        GetDpiForMonitor(monitor, 0, out uint dpi, out _);
        double scale = dpi > 0 ? dpi / 96.0 : 1;
        int width = (int)Math.Round(Math.Min(BackdropLook.GlanceWidth * scale, bounds.Width)), height = (int)Math.Round(Math.Min(BackdropLook.GlanceHeight * scale, bounds.Height));
        int left = (int)Math.Round(bounds.Left + (bounds.Width - width) / 2), top = (int)Math.Round(bounds.Top);
        var region = new RECT(left, top, left + width, top + height);
        var means = new List<double>();
        using var source = DesktopSource.TryCreate();
        if (source is null) return means;
        for (int i = 0; i < 10; i++)
        {
            // A frame that did not change keeps the last one's brightness; the first capture always gives one.
            if (source.Update(region, out _) && source.Texture is { } texture) means.Add(Mean(source, texture, width, height));
            Thread.Sleep(20);
        }
        return means;
    }
    private static double Mean(DesktopSource source, Vortice.Direct3D11.ID3D11Texture2D texture, int width, int height)
    {
        var description = texture.Description;
        var context = source.Device.ImmediateContext;
        using var staging = source.Device.CreateTexture2D(description with { Usage = Vortice.Direct3D11.ResourceUsage.Staging, BindFlags = Vortice.Direct3D11.BindFlags.None, CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read });
        context.CopyResource(staging, texture);
        var map = context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read);
        var row = new byte[width * 4];
        // The mean of the middle of the brightness, as the first look takes it (GlassAppearance.RangeMean).
        var histogram = new int[256];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), row, 0, row.Length);
            for (int o = 0; o < row.Length; o += 4) histogram[Math.Min(255, (int)(.0722 * row[o] + .7152 * row[o + 1] + .2126 * row[o + 2]))]++;
        }
        context.Unmap(staging, 0);
        return BrimDeck.Core.GlassAppearance.RangeMean(histogram);
    }

    [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    // The primary monitor (the one holding the origin).
    [DllImport("user32")] private static extern IntPtr MonitorFromPoint(long point, uint flags);
}
