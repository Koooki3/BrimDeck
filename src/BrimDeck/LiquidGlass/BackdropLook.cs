using System.Diagnostics;
using System.Runtime.InteropServices;
using BrimDeck.Core;
using Vortice.Direct3D11;

namespace BrimDeck.LiquidGlass;

// Automatic liquid glass's look from what lies behind the island, as the live host runs it (GlassBackdrop, on every frame drawn
// and every tick of its poll): the renderer's latest measure of the backdrop (LiquidRenderer.TryBackdrop, which only has a new
// one after a frame was drawn) is kept, and the last one known is fed to the decision on every step, so a backdrop that holds
// still (a window that stays under the island, which draws no new frame) still gets its look once the confirmation has passed.
// Only a new measure counts as one toward a jump (GlassAppearance.Feed). The first measure decides at once (instant: shown
// without a fade); the look the app chose before the island was shown (Glance) is usually the same, so nothing turns then.
internal sealed class BackdropLook
{
    private GlassAppearance? _look;
    // The last measure (NaN before the first), and how many have come in.
    public double Luma { get; private set; } = double.NaN;
    public int Measures { get; private set; }
    public bool? Dark => _look?.Dark;

    // One step at now (milliseconds), the look shown now being current. True when the look should change: to Dark, at once
    // when instant.
    public bool Step(LiquidRenderer renderer, long now, bool current, out bool instant)
    {
        instant = false;
        bool fresh = renderer.TryBackdrop(out double luma);
        if (fresh) { Luma = luma; Measures++; }
        if (double.IsNaN(Luma)) return false;
        if (_look is null)
        {
            bool dark = GlassAppearance.FirstLook(Luma);
            _look = new GlassAppearance(dark);
            _look.Reset(dark, now);
            instant = true;
            return dark != current;
        }
        // The theme was turned by someone else meanwhile (its own hours, a reload): the decision starts from what is shown.
        if (_look.Dark != current) _look.Reset(current, now);
        return _look.Feed(Luma, now, fresh);
    }

    // Automatic liquid glass is not chosen now: the next time it is, its first measure decides at once again.
    public void Forget() => _look = null;

    // Where the island will be, before it is shown: the top middle of its monitor, GlanceWidth by GlanceHeight DIPs. The island's
    // collapsed forms are narrower than this and the open panel larger, but the look is decided by the mean over the area, and
    // this covers what lies behind the island in all of them closely enough for the first look; the live measure corrects it.
    public const double GlanceWidth = 500, GlanceHeight = 60;
    private const long GlanceMs = 100;
    private const int GlanceCaptures = 3;

    // Where the island will be on a monitor (bounds in device pixels, handle its HMONITOR): the top middle, GlanceWidth by
    // GlanceHeight DIPs at the monitor's scale, in device pixels.
    public static RECT Region(System.Windows.Rect monitor, IntPtr handle)
    {
        double scale = handle != IntPtr.Zero && GetDpiForMonitor(handle, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        int width = (int)Math.Round(Math.Min(GlanceWidth * scale, monitor.Width)), height = (int)Math.Round(Math.Min(GlanceHeight * scale, monitor.Height));
        int left = (int)Math.Round(monitor.Left + (monitor.Width - width) / 2), top = (int)Math.Round(monitor.Top);
        return new RECT(left, top, left + width, top + height);
    }

    // The mean brightness (luma 0..1, as LiquidRenderer measures it) of the desktop at the top middle of a monitor (bounds in
    // device pixels), taken once, synchronously, on the calling thread (the tests; BrimDeck measures off the UI thread,
    // SourceStart). Null when the desktop cannot be captured. Excluded windows (ours, already shown) are left out of the
    // capture, as they are from the live glass.
    public static double? Glance(System.Windows.Rect monitor, IntPtr handle, params IntPtr[] exclude)
    {
        var region = Region(monitor, handle);
        if (region.IsEmpty) return null;
        foreach (var window in exclude) DesktopSource.Exclude(window);
        try
        {
            using var source = DesktopSource.TryCreate();
            return source is null ? null : Measure(source, region);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException or SharpGen.Runtime.SharpGenException or DllNotFoundException)
        {
            return null;
        }
    }

    // The mean brightness of the desktop under region, captured with source (on a worker, SourceStart, for automatic liquid
    // glass's first look). Null when nothing is captured within GlanceMs (no capture now, a secure desktop, protected
    // content). The pixels are only averaged, never kept.
    public static double? Measure(DesktopSource source, RECT region)
    {
        if (region.IsEmpty) return null;
        // Duplication may need a moment to hand out its first frame, and that first frame can be black (a frame with no image
        // yet, seen on a desktop that was not black): only a capture after it is trusted.
        var clock = Stopwatch.StartNew();
        int captured = 0;
        while (!(source.Update(region, out _) && ++captured >= GlanceCaptures))
        {
            if (clock.ElapsedMilliseconds >= GlanceMs) return null;
            Thread.Sleep(5);
        }
        return source.Texture is { } texture ? MeanLuma(source.Device, texture) : null;
    }

    // The texture's mean luma, read back through a staging copy (Rec. 709 weights on the sRGB values, as the renderer's measure).
    private static double MeanLuma(ID3D11Device device, ID3D11Texture2D texture)
    {
        var description = texture.Description;
        // Vortice hands out one wrapper for the immediate context: it is not disposed here.
        var context = device.ImmediateContext;
        using var staging = device.CreateTexture2D(description with { Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None });
        context.CopyResource(staging, texture);
        var map = context.Map(staging, 0, MapMode.Read);
        try
        {
            int width = (int)description.Width, height = (int)description.Height;
            var row = new byte[width * 4];
            double sum = 0;
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), row, 0, row.Length);
                for (int o = 0; o < row.Length; o += 4) sum += .0722 * row[o] + .7152 * row[o + 1] + .2126 * row[o + 2];
            }
            return sum / 255 / Math.Max(1, width * height);
        }
        finally { context.Unmap(staging, 0); }
    }

    [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
