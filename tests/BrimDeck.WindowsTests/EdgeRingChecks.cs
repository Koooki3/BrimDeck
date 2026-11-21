using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;

// Liquid glass's optical edge (IslandBorder.DrawEdge with a GlassEdge) on an animation's frames: each ring is one geometry whose
// bounds cost no conversion to a path, it fills the same pixels as the two outlines it lies between, and a frame's new size is
// drawn with that frame's rings in its one render pass, so nothing has to draw the island again after the layout.
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
        OnePass(check);
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

    // An opening's frames as the window runs them: the motion sets a new height, the layout pass arranges and draws the island,
    // then the glass's pass (MainWindow.ShowGlass -> ApplyGlassOptics) shapes the edge for the opening shown and draws the island
    // again only when the rings it finds differ from the ones drawn.
    private static void OnePass(Action<string, bool> check)
    {
        const double compact = 36, open = 260;
        var edge = new GlassEdge();
        edge.Stops([Colors.White, Colors.White]);
        double Openness(double height) => Math.Clamp((height - compact) / (open - compact), 0, 1);
        bool Shape(double height) => edge.Shape(GlassBoundaryMath.BevelWidth(Openness(height)), 1, .2, .05, 1);
        edge.Fit = height => Shape(height);
        var island = new IslandBorder { Width = 300, Height = compact, Background = Brushes.Black, CornerRadius = new CornerRadius(0, 0, 12, 12),
            ShoulderRadius = 16, MaterialOpacity = 1 };
        Shape(compact);
        island.Edge = edge;
        island.Measure(new Size(300, open)); island.Arrange(new Rect(0, 0, 300, open)); island.UpdateLayout();
        int frames = 40, again = 0, stale = 0;
        for (int frame = 1; frame <= frames; frame++)
        {
            double height = compact + (open - compact) * frame / frames;
            island.Height = height;
            island.UpdateLayout();
            if (Shape(island.ActualHeight)) { again++; island.InvalidateVisual(); island.UpdateLayout(); }
            if (island.Ring(0) is null || island.RingsWidth != edge.Width) stale++;
        }
        check($"an opening's {frames} frames draw the island once each ({again} drawn again after the layout)", again == 0);
        check($"each frame's one pass draws the rings for its own height ({stale} frames drew stale rings)", stale == 0);
        check("the drawn edge rings are StreamGeometry", island.Ring(0) is StreamGeometry);
    }
}
