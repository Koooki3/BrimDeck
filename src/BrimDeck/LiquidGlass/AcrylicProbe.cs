using System.Runtime.InteropServices;
using System.Windows;
using BrimDeck.Core;

namespace BrimDeck.LiquidGlass;

// Round 16: what lies around the acrylic island, looked at a couple of times a second (GlassBackdrop's acrylic body). DWM blurs
// the desktop for the body itself on every frame, with nothing of ours in that path; this only tells the body how bright the
// desktop around it is, for the luminosity layer the text needs (GlassEffect.LuminosityFor) and for automatic liquid glass's
// look (GlassAppearance). No capture device: GDI shrinks a ring around the island, from Gap to
// Gap + Band outside its outline, past its own shadow - to a few dozen pixels (StretchBlt, HALFTONE: an area average), so the
// island and its shadow are never in it and none of our windows needs to be left out of anything.
internal sealed class AcrylicProbe
{
    public readonly record struct Reading(double Mean, double Bright, double Dark, double Changed, double Median, double MiddleMean)
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
    }

    private Buffers? _buffers;
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

    // Each panel owns its pixel history and runs only one worker read at a time.
    public Reading? Read(Int32Rect island, Int32Rect display, int gap, int band)
    {
        var strips = Strips(island, display, gap, band);
        int valid = 0, count = 0, moved = 0;
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        var buffers = _buffers ??= new Buffers();
        var data = buffers.Dib;
        var pixels = buffers.Pixels;
        bool sameGeometry = _layout.Island == island && _layout.Display == display
            && _layout.Gap == gap && _layout.Band == band;
        IntPtr memory = CreateCompatibleDC(screen);
        try
        {
            for (int i = 0; i < strips.Length; i++)
            {
                var strip = strips[i];
                if (strip.IsEmpty) continue;
                var (w, h) = Grid(strip.Width, strip.Height);
                IntPtr bitmap = CreateCompatibleBitmap(screen, w, h);
                IntPtr old = SelectObject(memory, bitmap);
                SetStretchBltMode(memory, Halftone);
                SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
                bool ok = StretchBlt(memory, 0, 0, w, h, screen, strip.X, strip.Y, strip.Width, strip.Height, SrcCopy);
                SelectObject(memory, old);
                if (ok)
                {
                    var info = new BitmapInfo { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
                    if (GetDIBits(memory, bitmap, 0, (uint)h, data, ref info, 0) == h)
                    {
                        valid |= 1 << i;
                        for (int cell = 0; cell < w * h; cell++)
                        {
                            byte b = data[cell * 4], g = data[cell * 4 + 1], r = data[cell * 4 + 2];
                            int at = count * 3;
                            if (sameGeometry && count < _lastCount
                                && Math.Abs(r - pixels[at]) + Math.Abs(g - pixels[at + 1]) + Math.Abs(b - pixels[at + 2]) > 24)
                                moved++;

                            pixels[at] = r;
                            pixels[at + 1] = g;
                            pixels[at + 2] = b;
                            buffers.Luma[count++] = (.2126 * r + .7152 * g + .0722 * b) / 255;
                        }
                    }
                }
                DeleteObject(bitmap);
            }
        }
        finally { DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen); }
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
        return new Reading(sum / count, lumas[(int)(count * Tail)], lumas[(int)(count * (1 - Tail))],
            changed, lumas[count / 2], GlassAppearance.SortedRangeMean(lumas.AsSpan(0, count)));
    }

    private const int Halftone = 4, SrcCopy = 0x00CC0020;
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        public int Colors;
    }
    [DllImport("user32")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("gdi32")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32")] private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);
}
