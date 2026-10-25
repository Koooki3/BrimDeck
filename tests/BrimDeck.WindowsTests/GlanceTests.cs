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
            var clock = Stopwatch.StartNew();
            var luma = BackdropLook.Glance(bounds, monitor);
            long cold = clock.ElapsedMilliseconds;
            if (luma is null) { Console.WriteLine($"SKIP: first look (no capture in this session, gave up after {cold} ms)."); return; }
            clock.Restart();
            var again = BackdropLook.Glance(bounds, monitor);
            long warm = clock.ElapsedMilliseconds;
            // The same region captured at leisure (several frames, a moment apart) and averaged here.
            double? settled = Settled(bounds, monitor);
            check(FormattableString.Invariant($"first look: the desktop where the island will be is measured before it is shown (luma {luma:0.000}, settled {settled:0.000}, {cold} ms, again {warm} ms)"),
                luma is >= 0 and <= 1 && again is >= 0 and <= 1 && warm < 400 && (settled is null || Math.Abs(luma.Value - settled.Value) < .05));
        }
        finally { SetThreadDpiAwarenessContext(awareness); }
    }

    private static double? Settled(System.Windows.Rect bounds, IntPtr monitor)
    {
        GetDpiForMonitor(monitor, 0, out uint dpi, out _);
        double scale = dpi > 0 ? dpi / 96.0 : 1;
        int width = (int)Math.Round(Math.Min(BackdropLook.GlanceWidth * scale, bounds.Width)), height = (int)Math.Round(Math.Min(BackdropLook.GlanceHeight * scale, bounds.Height));
        int left = (int)Math.Round(bounds.Left + (bounds.Width - width) / 2), top = (int)Math.Round(bounds.Top);
        var region = new RECT(left, top, left + width, top + height);
        using var source = DesktopSource.TryCreate();
        if (source is null) return null;
        bool any = false;
        for (int i = 0; i < 20; i++) { any |= source.Update(region, out _); Thread.Sleep(20); }
        if (!any || source.Texture is not { } texture) return null;
        return Mean(source, texture, width, height);
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
