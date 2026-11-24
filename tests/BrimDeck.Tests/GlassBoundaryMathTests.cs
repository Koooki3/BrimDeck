using BrimDeck.Core;

// The island's boundary as numbers (GlassBoundaryMath): the shoulders' joins, the clamps at the extremes, the glass layer's rounded
// clip, and the optical edge's profile.
internal static class GlassBoundaryMathTests
{
    internal static void Run(Action<string, bool> check)
    {
        Shoulders(check);
        Extremes(check);
        Clips(check);
        Inside(check);
        Profile(check);
        LightStops(check);
        Origins(check);
    }

    // The light around the island is held within LightLeast of its brightest, in the theme's order, its mean the theme's.
    private static void LightStops(Action<string, bool> check)
    {
        double[] theme = [1, .39, .18, .23, .70], even = [1, 1], dark = [0, 1, 0];
        foreach (var relative in new[] { theme, even, dark })
        {
            var shown = new double[relative.Length];
            GlassBoundaryMath.LightStops(relative, shown);
            bool order = Enumerable.Range(0, relative.Length).All(i => Enumerable.Range(0, relative.Length).All(j => relative[i] <= relative[j] || shown[i] >= shown[j]));
            check($"The edge's light ({string.Join("/", relative)}) spans at most 1/{GlassBoundaryMath.LightLeast} ({string.Join("/", shown.Select(s => s.ToString("0.000")))}), in order, no brighter on average",
                shown.Max() <= shown.Min() / GlassBoundaryMath.LightLeast + 1e-12 && order && Math.Abs(shown.Average() - relative.Average()) < 1e-12);
        }
    }

    // The panel's origin on the screen is on whole pixels (the window's layout rounds), so the glass's straight edges fall on
    // pixel boundaries with no antialiased line; a fraction left by a transform is told apart from float noise.
    private static void Origins(Action<string, bool> check)
    {
        check("A panel origin on whole pixels, float noise included, is on whole pixels",
            GlassBoundaryMath.OnWholePixels((240, 0)) && GlassBoundaryMath.OnWholePixels((239.9999, 1e-5)) && GlassBoundaryMath.OnWholePixels((-1, -0.00001)));
        check("A panel origin a fraction of a pixel off is not",
            !GlassBoundaryMath.OnWholePixels((240.25, 0)) && !GlassBoundaryMath.OnWholePixels((240, .5)) && !GlassBoundaryMath.OnWholePixels((double.NaN, 0)));
    }

    // What the glass is cut by while a notch's path is not to be had: a rounded rectangle that stays inside the outline on the
    // surface, at every dock's turn, and covers the body to its corners.
    private static void Inside(Action<string, bool> check)
    {
        var notch = GlassBoundaryMath.Measure(160, 30, 14, 0, 0, 12, 12, 0);
        // Top, right, bottom and left docks at 125 %: the surface's pixels run from 0 to its size.
        (string Name, System.Numerics.Matrix3x2 Place, double W, double H, GlassBoundaryMath.RoundedClip Want)[] docks =
        [
            ("top", new(1.25f, 0, 0, 1.25f, 0, 0), 200, 37.5, new(17.5, -15, 165, 52.5, 15)),
            ("bottom", new(1.25f, 0, 0, -1.25f, 0, 37.5f), 200, 37.5, new(17.5, 0, 165, 52.5, 15)),
            ("left", new(0, 1.25f, 1.25f, 0, 0, 0), 37.5, 200, new(-15, 17.5, 52.5, 165, 15)),
            ("right", new(0, 1.25f, -1.25f, 0, 37.5f, 0), 37.5, 200, new(0, 17.5, 52.5, 165, 15)),
        ];
        foreach (var (name, place, w, h, want) in docks)
        {
            var clip = GlassBoundaryMath.RoundedInside(notch, place);
            check($"A notch docked {name} without its path is its body, the edge's corners square ({clip})",
                Math.Abs(clip.X - want.X) < .01 && Math.Abs(clip.Y - want.Y) < .01 && Math.Abs(clip.Width - want.Width) < .01
                && Math.Abs(clip.Height - want.Height) < .01 && Math.Abs(clip.Radius - want.Radius) < .01);
        }
        // Unequal corners: the largest radius, so it never passes the outline; none: a plain rectangle.
        var uneven = GlassBoundaryMath.RoundedInside(GlassBoundaryMath.Measure(160, 40, 0, 4, 8, 12, 2, 0), System.Numerics.Matrix3x2.Identity);
        var plain = GlassBoundaryMath.RoundedInside(GlassBoundaryMath.Measure(160, 40, 0, 0, 0, 0, 0, 0), System.Numerics.Matrix3x2.Identity);
        check($"Unequal corners take the largest radius, no corners none ({uneven}; {plain})",
            uneven == new GlassBoundaryMath.RoundedClip(0, 0, 160, 40, 12) && plain == new GlassBoundaryMath.RoundedClip(0, 0, 160, 40, 0));
        // An opening's frames, the shoulders and corners shrinking: always of a size, its radius within half of it.
        bool sound = true;
        for (int i = 0; i <= 200; i++)
        {
            double u = i / 200d;
            var o = GlassBoundaryMath.Measure(160 + 400 * u, 30 + 300 * u, 14 * (1 - u), 20 * u, 20 * u, 12 + 8 * u, 12 + 8 * u, 0);
            var clip = GlassBoundaryMath.RoundedInside(o, System.Numerics.Matrix3x2.CreateScale(1.25f));
            sound &= clip.Width > 0 && clip.Height > 0 && clip.Radius >= 0 && 2 * clip.Radius <= Math.Min(clip.Width, clip.Height) + .01
                && clip.X >= -.01 && clip.X + clip.Width <= 1.25 * o.Width + .01;
        }
        check("Through an opening it keeps within the outline's width with a radius of at most half its size", sound);
    }

    private static (double X, double Y) Bezier((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3, double t)
    {
        double u = 1 - t;
        return (u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y);
    }

    // Tangent (unit) and signed curvature at t from central differences of the curve itself, not from its control points.
    private static ((double X, double Y) Tangent, double Curvature) Local(Func<double, (double X, double Y)> curve, double t)
    {
        const double h = 1e-4;
        double a = Math.Clamp(t - h, 0, 1), b = Math.Clamp(t + h, 0, 1), m = (a + b) / 2, d = (b - a) / 2;
        var p0 = curve(a); var p1 = curve(m); var p2 = curve(b);
        double dx = (p2.X - p0.X) / (2 * d), dy = (p2.Y - p0.Y) / (2 * d);
        double ddx = (p2.X - 2 * p1.X + p0.X) / (d * d), ddy = (p2.Y - 2 * p1.Y + p0.Y) / (d * d);
        double speed = Math.Sqrt(dx * dx + dy * dy);
        return ((dx / speed, dy / speed), (dx * ddy - dy * ddx) / (speed * speed * speed));
    }

    private static void Shoulders(Action<string, bool> check)
    {
        double worstTangent = 0, worstCurvature = 0, worstEnds = 0, worstSide = 0;
        foreach (var (w, shoulder) in new[] { (200.0, 12.0), (160.0, 6.7), (90.0, 30.0), (300.0, 1.0) })
        {
            double depth = shoulder * 7 / 16;
            var s = GlassBoundaryMath.RightShoulder(w, shoulder, depth);
            (double, double) start = (w, 0);
            Func<double, (double X, double Y)> first = t => Bezier(start, s.A1, s.A2, s.Mid, t);
            Func<double, (double X, double Y)> second = t => Bezier(s.Mid, s.B1, s.B2, s.End, t);
            var end1 = Local(first, 1); var start2 = Local(second, 0);
            worstTangent = Math.Max(worstTangent, Math.Abs(end1.Tangent.X - start2.Tangent.X) + Math.Abs(end1.Tangent.Y - start2.Tangent.Y));
            // Curvature is compared relative to the shoulder's own scale (1 / shoulder).
            worstCurvature = Math.Max(worstCurvature, Math.Abs(end1.Curvature - start2.Curvature) * shoulder);
            worstEnds = Math.Max(worstEnds, Math.Max(Math.Abs(Local(first, 0).Curvature), Math.Abs(Local(second, 1).Curvature)) * shoulder);
            // Into the straight side: heading straight down.
            var side = Local(second, 1).Tangent;
            worstSide = Math.Max(worstSide, Math.Abs(side.X) + Math.Abs(side.Y - 1));
        }
        // The differences sit half a step inside each end, so a smooth join still shows about 1e-4 in the tangent and 1e-3 in the
        // curvature; a kink, or a join that is only tangent-continuous, shows tenths or more.
        check(FormattableString.Invariant($"Shoulder join is tangent-continuous ({worstTangent:0.0e0})"), worstTangent < 1e-3);
        check(FormattableString.Invariant($"Shoulder join is curvature-continuous ({worstCurvature:0.0e0} per shoulder)"), worstCurvature < 1e-2);
        check(FormattableString.Invariant($"Shoulder ends meet the straight edges at zero curvature ({worstEnds:0.0e0})"), worstEnds < 1e-2);
        check(FormattableString.Invariant($"Shoulder runs into the side heading straight down ({worstSide:0.0e0})"), worstSide < 1e-3);

        // A shoulder shrinking to nothing: every part tends to the plain outline's, with no jump at zero.
        var plain = GlassBoundaryMath.Measure(200, 40, 0, 16, 16, 16, 16, 0);
        double jump = 0, collapse = 0;
        foreach (double shoulder in new[] { 1e-3, 1e-5, 1e-7 })
        {
            var tiny = GlassBoundaryMath.Measure(200, 40, shoulder, 16, 16, 16, 16, 0);
            jump = Math.Max(jump, Math.Abs(tiny.Left - plain.Left) + Math.Abs(tiny.Right - plain.Right) + Math.Abs(tiny.BottomLeft - plain.BottomLeft)
                + Math.Abs(tiny.BottomRight - plain.BottomRight));
            var s = GlassBoundaryMath.RightShoulder(200, tiny.Shoulder, tiny.ShoulderDepth);
            foreach (var p in new[] { s.A1, s.A2, s.Mid, s.B1, s.B2, s.End }) collapse = Math.Max(collapse, Math.Abs(p.X - 200) + Math.Abs(p.Y));
        }
        check(FormattableString.Invariant($"A shoulder shrinking to nothing becomes the plain outline ({jump:0.0e0}, curve {collapse:0.0e0})"), jump < 1e-2 && collapse < 1e-2);
    }

    private static void Extremes(Action<string, bool> check)
    {
        bool sane = true;
        foreach (var (w, h, r, shoulder, inset) in new[] { (4.0, 40.0, 100.0, 0.0, 0.0), (40.0, 2.0, 100.0, 0.0, 0.0), (0.5, 0.5, 3.0, 0.0, 0.0),
            (40.0, 3.0, 100.0, 6.7, 0.0), (30.0, 20.0, 10.0, 0.0, 50.0), (1e-6, 10.0, 5.0, 0.0, 0.0) })
        {
            var o = GlassBoundaryMath.Measure(w, h, shoulder, r, r, r, r, inset);
            double[] all = [o.Width, o.Height, o.Inset, o.Top, o.Shoulder, o.ShoulderDepth, o.Left, o.Right, o.TopLeft, o.TopRight, o.BottomRight, o.BottomLeft];
            double half = Math.Min(o.Width, o.Height) / 2 + 1e-12;
            sane &= all.All(double.IsFinite) && o.Width >= 0 && o.Height >= 0 && o.Right >= o.Left
                && new[] { o.TopLeft, o.TopRight, o.BottomRight, o.BottomLeft }.All(c => c >= 0 && c <= half)
                && o.BottomLeft <= (o.Right - o.Left) / 2 + 1e-12;
        }
        check("Extreme sizes (a narrow capsule, a hairline, an inset past the size) keep every part finite and within the shape", sane);
        var thin = GlassBoundaryMath.Measure(4, 40, 0, 100, 100, 100, 100, 0);
        check("A capsule narrower than its radius is a full stadium", thin.TopLeft == 2 && thin.BottomRight == 2);
        // Inset outlines are the outline's lines at that distance: the corners shrink by the inset, the centres stay.
        var outer = GlassBoundaryMath.Measure(120, 32, 0, 16, 16, 16, 16, 0);
        var inner = GlassBoundaryMath.Measure(120, 32, 0, 16, 16, 16, 16, 1.5);
        check("An outline drawn inside keeps the corners' centres (the edge's rings are even in width)",
            inner.TopLeft == outer.TopLeft - 1.5 && inner.Width == outer.Width - 3 && inner.Inset + inner.TopLeft == outer.TopLeft);
    }

    private static void Clips(Action<string, bool> check)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
        var capsule = GlassBoundaryMath.Measure(100, 32, 0, 16, 16, 16, 16, 0);
        bool round = GlassBoundaryMath.TryRoundedClip(capsule, (10.4, 3.25), (135.4, 3.25), (10.4, 43.25), out var cc); var c = cc.First;
        check("A capsule at 125 % is one rounded clip, its fraction of a pixel kept",
            round && Near(c.X, 10.4) && Near(c.Y, 3.25) && Near(c.Width, 125) && Near(c.Height, 40) && Near(c.Radius, 20));
        var panel = GlassBoundaryMath.Measure(300, 120, 0, 0, 0, 20, 20, 0);
        bool open = GlassBoundaryMath.TryRoundedClip(panel, (0, 0), (300, 0), (0, 120), out var pp); var p = pp.First;
        check("An open panel with a square top reaches a radius above the window", open && Near(p.Y, -20) && Near(p.Height, 140) && Near(p.Radius, 20));
        // Turned a quarter (an island on the left edge): the square side follows the turn.
        bool turned = GlassBoundaryMath.TryRoundedClip(panel, (120, 0), (120, 300), (0, 0), out var qq); var q = qq.First;
        check("A panel turned a quarter keeps its square side, now the right", turned && Near(q.X, 0) && Near(q.Width, 140) && Near(q.Height, 300));
        // An opening squaring the top: top corners 8, bottom 18; the larger radius, so the glass stays inside the outline.
        var morph = GlassBoundaryMath.Measure(300, 120, 0, 8, 8, 18, 18, 0);
        bool two = GlassBoundaryMath.TryRoundedClip(morph, (0, 0), (300, 0), (0, 120), out var m);
        check("Top and bottom corners of different radii take the larger radius, never reaching past the outline",
            two && Near(m.First.Radius, 18) && Near(m.First.Y, 0) && Near(m.First.Height, 120));
        var mixed = GlassBoundaryMath.Measure(300, 120, 0, 0, 10, 20, 20, 0);
        var diagonal = GlassBoundaryMath.Measure(300, 120, 0, 20, 0, 20, 0, 0);
        var shoulders = GlassBoundaryMath.Measure(300, 40, 6.7, 0, 0, 12, 12, 0);
        check("Mixed radii, square corners across, shoulders, or a slanted island have no rounded clip",
            !GlassBoundaryMath.TryRoundedClip(mixed, (0, 0), (300, 0), (0, 120), out _)
            && !GlassBoundaryMath.TryRoundedClip(diagonal, (0, 0), (300, 0), (0, 120), out _)
            && !GlassBoundaryMath.TryRoundedClip(shoulders, (0, 0), (300, 0), (0, 40), out _)
            && !GlassBoundaryMath.TryRoundedClip(capsule, (0, 0), (100, 10), (-3, 32), out _));
    }

    private static void Profile(Action<string, bool> check)
    {
        // The bevel: height and slope continuous, flat at both ends.
        const double e = 1e-6;
        check("The bevel's height runs 0 to 1 and is flat at both ends",
            GlassBoundaryMath.Smootherstep(0) == 0 && GlassBoundaryMath.Smootherstep(1) == 1
            && GlassBoundaryMath.Smootherstep(e) / e < 1e-9 && (1 - GlassBoundaryMath.Smootherstep(1 - e)) / e < 1e-9);
        check("Its light is zero at the outline and the inner end, one at the middle",
            GlassBoundaryMath.Slope(0) == 0 && GlassBoundaryMath.Slope(1) == 0 && Math.Abs(GlassBoundaryMath.Slope(.5) - 1) < 1e-12
            && GlassBoundaryMath.Slope(e) / e < 1e-4);
        // Sampled finely, the profile has no step: its largest change over a hundredth of the width is small against the peak.
        var (peak, baseline) = GlassBoundaryMath.Light(0);
        double step = 0, top = 0;
        for (int i = 1; i <= 1000; i++)
        {
            double a = GlassBoundaryMath.Profile((i - 1) / 1000d, peak, baseline), b = GlassBoundaryMath.Profile(i / 1000d, peak, baseline);
            step = Math.Max(step, Math.Abs(b - a)); top = Math.Max(top, b);
        }
        check(FormattableString.Invariant($"The edge profile is continuous ({step / peak:0.0000} of the peak per thousandth) and stays under its peak"),
            step / peak < .01 && top <= peak + 1e-12 && GlassBoundaryMath.Profile(1, peak, baseline) == 0
            && Math.Abs(GlassBoundaryMath.Profile(0, peak, baseline) - baseline) < 1e-12);
        // The rings: one rise and one fall, no narrow double peak, none past the width.
        Span<double> alphas = stackalloc double[8];
        bool single = true;
        foreach (double width in new[] { 1.5, 2.5, 3.0, 4.0, 5.6, 7.2 })
        {
            int n = GlassBoundaryMath.Bands(width, peak, baseline, alphas);
            int turns = 0;
            for (int k = 1; k + 1 < n; k++) if (alphas[k] > alphas[k - 1] && alphas[k] > alphas[k + 1]) turns++;
            single &= n == (int)Math.Ceiling(width) && turns <= 1 && alphas[..n].ToArray().All(a => a >= 0 && a <= peak + 1e-12);
        }
        check("The edge's rings rise once and fall once, a ring per pixel of the width", single);
        check("No width, no rings", GlassBoundaryMath.Bands(0, peak, baseline, alphas) == 0 && GlassBoundaryMath.Bands(double.NaN, peak, baseline, alphas) == 0);
        check("The bevel widens from the capsule to the open panel, eased at both ends",
            GlassBoundaryMath.BevelWidth(0) == GlassBoundaryMath.CollapsedBevel && GlassBoundaryMath.BevelWidth(1) == GlassBoundaryMath.ExpandedBevel
            && GlassBoundaryMath.BevelWidth(-1) == GlassBoundaryMath.CollapsedBevel && (GlassBoundaryMath.BevelWidth(e) - GlassBoundaryMath.CollapsedBevel) / e < 1e-6);
        var dark = GlassBoundaryMath.Light(0); var light = GlassBoundaryMath.Light(1); var mid = GlassBoundaryMath.Light(.5);
        check("The edge's light sits in the plan's ranges for either palette and passes between them on a turn",
            dark.Peak is >= .12 and <= .32 && light.Peak is >= .08 and <= .22 && dark.Baseline is >= .025 and <= .065 && light.Baseline is >= .025 and <= .065
            && mid.Peak < dark.Peak && mid.Peak > light.Peak && GlassBoundaryMath.Light(double.NaN) == dark);
    }
}
