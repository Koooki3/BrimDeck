using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;

// Liquid glass's optical edge (IslandBorder.DrawEdge with a GlassEdge) on an animation's frames: each ring is one geometry whose
// bounds cost no conversion to a path, and it fills the same pixels as the two outlines it lies between.
internal static class EdgeRingChecks
{
    private static readonly (string Name, double W, double H, double Shoulder, CornerRadius Corners)[] Shapes =
    [
        ("notch", 300, 36, 16, new CornerRadius(0, 0, 12, 12)),
        ("capsule", 300, 36, 0, new CornerRadius(18)),
        ("open panel", 420, 260, 16, new CornerRadius(0, 0, 24, 24)),
        ("open capsule", 420, 260, 0, new CornerRadius(24)),
    ];

    public static void Run(Action<string, bool> check)
    {
        Rings(check);
    }

    private static void Rings(Action<string, bool> check)
    {
        foreach (var (name, w, h, shoulder, corners) in Shapes)
            foreach (double scale in new[] { 1d, 1.25, 1.5 })
                foreach (double inset in new[] { 0d, 1.5 })
                {
                    double step = 1 / scale;
                    int worst = 0, differing = 0;
                    long bytes = 0;
                    bool single = true;
                    for (int k = 0; k < 4; k++)
                    {
                        double outer = inset + k * step, inner = inset + Math.Min((k + 1) * step, 3.2);
                        var ring = IslandBorder.BuildRing(w, h, shoulder, corners, outer, inner);
                        single &= ring is StreamGeometry { FillRule: FillRule.EvenOdd, IsFrozen: true };
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        _ = ring.Bounds;
                        bytes += GC.GetAllocatedBytesForCurrentThread() - before;
                        var reference = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = new GeometryCollection(2)
                            { IslandBorder.BuildOutline(w, h, shoulder, corners, outer), IslandBorder.BuildOutline(w, h, shoulder, corners, inner) } };
                        reference.Freeze();
                        byte[] a = Draw(ring, w, h, scale), b = Draw(reference, w, h, scale);
                        for (int i = 3; i < a.Length; i += 4)
                        {
                            int d = Math.Abs(a[i] - b[i]);
                            if (d > 1) differing++;
                            worst = Math.Max(worst, d);
                        }
                    }
                    string at = $"the {name} at {scale * 100:0}%{(inset > 0 ? ", inset" : "")}";
                    check($"{at}: each edge ring is one frozen even-odd StreamGeometry", single);
                    check($"{at}: reading 4 rings' bounds allocates {bytes / 1024.0:0.0} KB (no path conversion)", bytes < 4 * 2048);
                    check($"{at}: the rings fill the pixels the two outlines did (max alpha diff {worst}, {differing} pixels over 1)", worst <= 3 && differing <= 4);
                }
    }

    private static byte[] Draw(Geometry geometry, double w, double h, double scale)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawGeometry(Brushes.White, null, geometry);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
}
