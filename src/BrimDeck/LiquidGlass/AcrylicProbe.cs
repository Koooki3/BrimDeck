using System.Runtime.InteropServices;

namespace BrimDeck.LiquidGlass;

// Round 16: what lies around the acrylic island, looked at a couple of times a second (GlassBackdrop's acrylic body). DWM blurs
// the desktop for the body itself on every frame, with nothing of ours in that path; this only tells the body how bright the
// desktop around it is, for the luminosity layer the text needs (GlassEffect.LuminosityFor) and for automatic liquid glass's
// look (GlassAppearance). No capture device: GDI shrinks a ring around the island - left, right and below it, from Gap to
// Gap + Band outside its outline, past its own shadow - to a few dozen pixels (StretchBlt, HALFTONE: an area average), so the
// island and its shadow are never in it and none of our windows needs to be left out of anything.
internal static class AcrylicProbe
{
    public readonly record struct Reading(double Mean, double Bright, double Dark, double Changed, double Median)
    {
        // The brightest and darkest the glass has to answer for, by its openness: the collapsed island's ring is small, so one
        // bright neighbour would hold its 90th percentile up; the capsule answers to the ring's middle, the open panel (a
        // ring as large as its sides) to its tenths.
        public double BrightFor(double openness) => Median + (Bright - Median) * Math.Clamp(openness, 0, 1);
        public double DarkFor(double openness) => Median + (Dark - Median) * Math.Clamp(openness, 0, 1);
    }
    private const int Cells = 24;
    private static byte[]? _last;

    // gap and band in device pixels; island in screen pixels. Null when nothing could be read.
    // Called from the thread pool (GlassBackdrop.ProbeTick); one read at a time, as _last holds the previous one.
    private static readonly object Gate = new();
    public static Reading? Read(RECT island, int gap, int band)
    {
        lock (Gate) return ReadLocked(island, gap, band);
    }

    private static Reading? ReadLocked(RECT island, int gap, int band)
    {
        var strips = new[]
        {
            new RECT(island.Left - gap - band, island.Top, island.Left - gap, island.Bottom + gap),
            new RECT(island.Right + gap, island.Top, island.Right + gap + band, island.Bottom + gap),
            new RECT(island.Left - gap - band, island.Bottom + gap, island.Right + gap + band, island.Bottom + gap + band),
        };
        var lumas = new List<double>(Cells * Cells);
        var pixels = new List<byte>(Cells * Cells * 3);
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr memory = CreateCompatibleDC(screen);
        try
        {
            foreach (var strip in strips)
            {
                if (strip.Right - strip.Left < 2 || strip.Bottom - strip.Top < 2) continue;
                int w = Math.Min(Cells, strip.Right - strip.Left), h = Math.Min(Cells, strip.Bottom - strip.Top);
                IntPtr bitmap = CreateCompatibleBitmap(screen, w, h);
                IntPtr old = SelectObject(memory, bitmap);
                SetStretchBltMode(memory, Halftone);
                SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
                bool ok = StretchBlt(memory, 0, 0, w, h, screen, strip.Left, strip.Top, strip.Right - strip.Left, strip.Bottom - strip.Top, SrcCopy);
                SelectObject(memory, old);
                if (ok)
                {
                    var info = new BitmapInfo { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
                    var data = new byte[w * h * 4];
                    if (GetDIBits(memory, bitmap, 0, (uint)h, data, ref info, 0) == h)
                        for (int i = 0; i < w * h; i++)
                        {
                            byte b = data[i * 4], g = data[i * 4 + 1], r = data[i * 4 + 2];
                            lumas.Add((.2126 * r + .7152 * g + .0722 * b) / 255);
                            pixels.Add(r); pixels.Add(g); pixels.Add(b);
                        }
                }
                DeleteObject(bitmap);
            }
        }
        finally { DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen); }
        if (lumas.Count == 0) return null;
        var now = pixels.ToArray();
        double changed = 0;
        if (_last is { } last && last.Length == now.Length)
        {
            int moved = 0;
            for (int i = 0; i < now.Length; i += 3)
                if (Math.Abs(now[i] - last[i]) + Math.Abs(now[i + 1] - last[i + 1]) + Math.Abs(now[i + 2] - last[i + 2]) > 24) moved++;
            changed = moved / (now.Length / 3.0);
        }
        _last = now;
        lumas.Sort();
        return new Reading(lumas.Average(), lumas[(int)(lumas.Count * .9)], lumas[(int)(lumas.Count * .1)], changed, lumas[lumas.Count / 2]);
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
