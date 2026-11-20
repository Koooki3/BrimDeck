using System.Runtime.InteropServices;
using System.Windows;
using BrimDeck.Core;

namespace BrimDeck.LiquidGlass;

// Round 16: what lies around the acrylic island, looked at a couple of times a second (GlassBackdrop's acrylic body). DWM blurs
// the desktop for the body itself on every frame, with nothing of ours in that path; this only tells the body how bright the
// desktop around it is, for the luminosity layer the text needs (GlassRecipe.LuminosityFor) and for automatic liquid glass's
// look (GlassAppearance). No capture device: GDI shrinks a ring around the island, from Gap to
// Gap + Band outside its outline, past its own shadow - to a few dozen pixels (StretchBlt, HALFTONE: an area average), so the
// island and its shadow are never in it and none of our windows needs to be left out of anything.
// Resources (review v2 U12): each probe (one per panel, read by one worker at a time) keeps its memory DC and a 32-bit DIB section
// selected into it for its life, and its buffers; only the screen DC is taken per reading. Every GDI call's result is checked
// (SelectObject's HGDI_ERROR included); a failure releases what was made and the next reading makes it again. Dispose releases
// at once, or after the reading under way when one is (never waiting for it on the caller's thread).
internal sealed class AcrylicProbe : IDisposable
{
    // Sides: the mean luma of the left, right, top and bottom strips (NaN for a strip off the display), for the rim's light.
    public readonly record struct Reading(double Mean, double Bright, double Dark, double Changed, double Median, double MiddleMean,
        (double Left, double Right, double Top, double Bottom) Sides = default)
    {
        // The brightest and darkest the glass has to answer for, by its openness: the collapsed island's ring is small, so one
        // bright neighbour would hold its 90th percentile up; the capsule answers to the ring's middle, the open panel (a
        // ring as large as its sides) to its tenths.
        public double BrightFor(double openness) => Median + (Bright - Median) * Math.Clamp(openness, 0, 1);
        public double DarkFor(double openness) => Median + (Dark - Median) * Math.Clamp(openness, 0, 1);
    }
    private const int Cells = 24;
    // The brightest and darkest the open panel answers for: the ring's 95th and 5th percentiles (user 10-27, "再透一点": with the
    // floors lowered the 90th left a moving wallpaper's bright streak, which reaches the body under the panel, unanswered).
    internal const double Tail = .95;
    private const int MaxSamples = 4 * Cells * Cells;
    private sealed class Buffers
    {
        public readonly double[] Luma = new double[MaxSamples];
        public readonly byte[] Pixels = new byte[MaxSamples * 3];
        public readonly byte[] Dib = new byte[Cells * Cells * 4];
        public readonly double[] SideSum = new double[4];
        public readonly int[] SideCount = new int[4];
    }

    private Buffers? _buffers;
    private IntPtr _memory, _bitmap, _bits, _old;
    // 0 idle, 1 reading, 2 released for good; and a release asked for while reading.
    private int _state;
    private volatile bool _disposeAsked;
    private int _lastCount;
    private (Int32Rect Island, Int32Rect Display, int Gap, int Band, int Valid) _layout;

    // The cells a strip is shrunk to: at most Cells along a side. (Coarser cells, about the body's blur, were tried for user
    // 10-27's "再透一点": they missed bright detail that does reach the body over a moving wallpaper - the dark look's secondary
    // text fell to 4.47:1 - so the ring keeps its fine grain; the clearer glass comes from the lower floors, GlassEffect.)
    internal static (int Width, int Height) Grid(int width, int height) => (Math.Min(Cells, width), Math.Min(Cells, height));

    internal static Int32Rect[] Strips(Int32Rect island, Int32Rect display, int gap, int band)
    {
        int right = island.X + island.Width, bottom = island.Y + island.Height;
        int outer = gap + band;
        Int32Rect Clip(int x, int y, int r, int b)
        {
            int left = Math.Max(x, display.X), top = Math.Max(y, display.Y);
            int width = Math.Min(r, display.X + display.Width) - left;
            int height = Math.Min(b, display.Y + display.Height) - top;
            return width < 2 || height < 2 ? Int32Rect.Empty : new Int32Rect(left, top, width, height);
        }
        return [
            Clip(island.X - outer, island.Y, island.X - gap, bottom),
            Clip(right + gap, island.Y, right + outer, bottom),
            Clip(island.X - outer, island.Y - outer, right + outer, island.Y - gap),
            Clip(island.X - outer, bottom + gap, right + outer, bottom + outer)
        ];
    }

    // Each panel owns its pixel history and runs only one worker read at a time (a second one at once reads nothing).
    public Reading? Read(Int32Rect island, Int32Rect display, int gap, int band)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return null;
        try { return ReadOnce(island, display, gap, band); }
        finally
        {
            Volatile.Write(ref _state, 0);
            if (_disposeAsked) Dispose();
        }
    }

    public void Dispose()
    {
        _disposeAsked = true;
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) Release();
    }

    private Reading? ReadOnce(Int32Rect island, Int32Rect display, int gap, int band)
    {
        var strips = Strips(island, display, gap, band);
        int valid = 0, count = 0, moved = 0;
        var buffers = _buffers ??= new Buffers();
        var sideSum = buffers.SideSum;
        var sideCount = buffers.SideCount;
        Array.Clear(sideSum); Array.Clear(sideCount);
        var data = buffers.Dib;
        var pixels = buffers.Pixels;
        bool sameGeometry = _layout.Island == island && _layout.Display == display
            && _layout.Gap == gap && _layout.Band == band;
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) { _lastCount = 0; return null; }
        try
        {
            if (!Prepare(screen)) { _lastCount = 0; return null; }
            for (int i = 0; i < strips.Length; i++)
            {
                var strip = strips[i];
                if (strip.IsEmpty) continue;
                var (w, h) = Grid(strip.Width, strip.Height);
                // StretchBlt writes at the top left of the DIB section; the pixels outside this strip's grid are ignored.
                if (!StretchBlt(_memory, 0, 0, w, h, screen, strip.X, strip.Y, strip.Width, strip.Height, SrcCopy)) continue;
                if (!GdiFlush()) continue;
                Marshal.Copy(_bits, data, 0, h * Cells * 4);
                valid |= 1 << i;
                for (int row = 0; row < h; row++)
                for (int x = 0, offset = row * Cells * 4; x < w; x++, offset += 4)
                {
                    byte b = data[offset], g = data[offset + 1], r = data[offset + 2];
                    int at = count * 3;
                    if (sameGeometry && count < _lastCount
                        && Math.Abs(r - pixels[at]) + Math.Abs(g - pixels[at + 1]) + Math.Abs(b - pixels[at + 2]) > 24)
                        moved++;

                    pixels[at] = r;
                    pixels[at + 1] = g;
                    pixels[at + 2] = b;
                    double luma = (.2126 * r + .7152 * g + .0722 * b) / 255;
                    buffers.Luma[count++] = luma;
                    sideSum[i] += luma; sideCount[i]++;
                }
            }
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        if (count == 0) { _lastCount = 0; return null; }
        // The earlier pixels are replaced as we read. Only compare complete samples with matching strip coverage.
        var layout = (island, display, gap, band, valid);
        double changed = _layout == layout && _lastCount == count ? moved / (double)count : 0;
        _lastCount = count;
        _layout = layout;
        var lumas = buffers.Luma;
        Array.Sort(lumas, 0, count);
        double sum = 0;
        for (int i = 0; i < count; i++) sum += lumas[i];
        double Side(int i) => sideCount[i] > 0 ? sideSum[i] / sideCount[i] : double.NaN;
        return new Reading(sum / count, lumas[(int)(count * Tail)], lumas[(int)(count * (1 - Tail))],
            changed, lumas[count / 2], GlassAppearance.SortedRangeMean(lumas.AsSpan(0, count)), (Side(0), Side(1), Side(2), Side(3)));
    }

    // The memory DC with the DIB section selected, made once; false (and nothing kept) when any step fails.
    private bool Prepare(IntPtr screen)
    {
        if (_memory != IntPtr.Zero) return true;
        _memory = CreateCompatibleDC(screen);
        if (_memory == IntPtr.Zero) return false;
        var info = new BitmapInfo { Size = 40, Width = Cells, Height = -Cells, Planes = 1, BitCount = 32 };
        _bitmap = CreateDIBSection(_memory, ref info, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero) { Release(); return false; }
        _old = SelectObject(_memory, _bitmap);
        if (_old == IntPtr.Zero || _old == HgdiError) { _old = IntPtr.Zero; Release(); return false; }
        if (SetStretchBltMode(_memory, Halftone) == 0 || !SetBrushOrgEx(_memory, 0, 0, IntPtr.Zero)) { Release(); return false; }
        return true;
    }

    private void Release()
    {
        if (_memory != IntPtr.Zero && _old != IntPtr.Zero) SelectObject(_memory, _old);
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        if (_memory != IntPtr.Zero) DeleteDC(_memory);
        _memory = _bitmap = _bits = _old = IntPtr.Zero;
    }

    private const int Halftone = 4, SrcCopy = 0x00CC0020;
    private static readonly IntPtr HgdiError = new(-1);
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        public int Colors;
    }
    [DllImport("user32")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32")] private static extern bool GdiFlush();
    [DllImport("gdi32")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("gdi32")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32")] private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sx, int sy, int sw, int sh, int rop);
}
