namespace BrimDeck.Core;

// The island's boundary as numbers: the outline's layout (corners and shoulders clamped to the size), the shoulder curves, the
// rounded clip the glass layer draws its edge with, and liquid glass's optical edge, one continuous profile from the outline inward.
public static class GlassBoundaryMath
{
    // The outline's parts for a size: the body between Left and Right below the shoulders (ShoulderDepth deep), its four corner
    // radii, and the inset, all in the outline's own units. Top is where the outline starts: 0 with shoulders (they reach the
    // screen edge), else the inset.
    public readonly record struct Layout(double Width, double Height, double Inset, double Top, double Shoulder, double ShoulderDepth,
        double Left, double Right, double TopLeft, double TopRight, double BottomRight, double BottomLeft);

    public static Layout Measure(double w, double h, double shoulderRadius, double topLeft, double topRight, double bottomRight, double bottomLeft, double insetBy)
    {
        // The shoulders are sized on the full bounds, so an inset outline has the same shoulders moved in by the inset: its
        // curves then lie inside the full outline's. The corners keep their centres and lose the inset from their radii, which
        // makes an inset outline the full one's line at that distance (the corners' arcs are concentric).
        double shoulder = Math.Clamp(shoulderRadius, 0, Math.Min(w / 4, h / 2));
        double depth = shoulder * (7d / 16);
        double inset = Math.Clamp(insetBy, 0, Math.Min(w, h) / 4), top = shoulder > 0 ? 0 : inset;
        w -= 2 * inset; h -= top + inset;
        double left = shoulder, right = w - shoulder;
        // With shoulders the bottom corners also stay within half the height, so the limit does not jump when a shoulder
        // shrinking to nothing on a low surface (a notch becoming the line) ends; a notch at rest never reaches either bound.
        double limit = Math.Max(0, Math.Min(shoulder > 0 ? Math.Min(h - depth, h / 2) : h / 2, (right - left) / 2));
        double half = Math.Max(0, Math.Min(w, h) / 2);
        return new(w, h, inset, top, shoulder, depth, left, right,
            Math.Clamp(topLeft - inset, 0, half), Math.Clamp(topRight - inset, 0, half),
            Math.Clamp(bottomRight - inset, 0, limit), Math.Clamp(bottomLeft - inset, 0, limit));
    }

    // The right shoulder as two cubics from the top edge's end (w, 0) down to the body's side (w - shoulder, depth); the left one
    // is its mirror. The middle point and the handles on either side of it lie on one line at equal distances, and the second
    // differences match there, so the join is curvature-continuous (C2); both ends have zero curvature, meeting the straight
    // edges without a kink in the light.
    public static ((double X, double Y) A1, (double X, double Y) A2, (double X, double Y) Mid, (double X, double Y) B1, (double X, double Y) B2, (double X, double Y) End)
        RightShoulder(double w, double shoulder, double depth)
        => ((w - shoulder * .28, 0), (w - shoulder * .64, 0), (w - shoulder * .82, depth * .18),
            (w - shoulder, depth * .36), (w - shoulder, depth * .72), (w - shoulder, depth));

    // The cubic handle factor for a quarter circle.
    public const double Kappa = .5522847498307936;

    // Where an outline is written: WPF's geometry for the island and its edge, Direct2D's path for the glass layer's clip.
    public interface IOutlineSink
    {
        void Begin(double x, double y);
        void Line(double x, double y);
        void Curve(double x1, double y1, double x2, double y2, double x3, double y3);
    }

    // One outline as a closed figure, clockwise from the top left: the top edge, the right shoulder (or corner), the right side,
    // the bottom corners and edge, the left side and the left shoulder (or corner). Placed: moved by the inset (o.Inset, o.Top)
    // in its points (otherwise a caller moves it as a whole). Every drawing of the island's outline is this one.
    public static void WriteOutline<T>(Layout o, bool placed, ref T sink) where T : struct, IOutlineSink
    {
        double dx = placed && o.Inset > 0 ? o.Inset : 0, dy = placed && o.Inset > 0 ? o.Top : 0;
        double shoulder = o.Shoulder, shoulderDepth = o.ShoulderDepth, left = o.Left, right = o.Right;
        double tl = o.TopLeft, tr = o.TopRight, br = o.BottomRight, bl = o.BottomLeft;
        double w = o.Width, h = o.Height;
        const double k = Kappa;
        sink.Begin((shoulder > 0 ? 0 : tl) + dx, dy);
        sink.Line((shoulder > 0 ? w : w - tr) + dx, dy);
        if (shoulder > 0)
        {
            var s = RightShoulder(w, shoulder, shoulderDepth);
            sink.Curve(s.A1.X + dx, s.A1.Y + dy, s.A2.X + dx, s.A2.Y + dy, s.Mid.X + dx, s.Mid.Y + dy);
            sink.Curve(s.B1.X + dx, s.B1.Y + dy, s.B2.X + dx, s.B2.Y + dy, s.End.X + dx, s.End.Y + dy);
        }
        else
            sink.Curve(w - tr * (1 - k) + dx, dy, w + dx, tr * (1 - k) + dy, w + dx, tr + dy);
        sink.Line(right + dx, h - br + dy);
        sink.Curve(right + dx, h - br * (1 - k) + dy, right - br * (1 - k) + dx, h + dy, right - br + dx, h + dy);
        sink.Line(left + bl + dx, h + dy);
        sink.Curve(left + bl * (1 - k) + dx, h + dy, left + dx, h - bl * (1 - k) + dy, left + dx, h - bl + dy);
        sink.Line(left + dx, (shoulder > 0 ? shoulderDepth : tl) + dy);
        if (shoulder > 0)
        {
            // The right shoulder mirrored, run backwards.
            var s = RightShoulder(w, shoulder, shoulderDepth);
            sink.Curve(w - s.B2.X + dx, s.B2.Y + dy, w - s.B1.X + dx, s.B1.Y + dy, w - s.Mid.X + dx, s.Mid.Y + dy);
            sink.Curve(w - s.A2.X + dx, s.A2.Y + dy, w - s.A1.X + dx, s.A1.Y + dy, dx, dy);
        }
        else
            sink.Curve(dx, tl * (1 - k) + dy, tl * (1 - k) + dx, dy, tl + dx, dy);
    }

    // The glass layer's clip as a rounded rectangle in the window's pixels: X, Y, Width, Height and one Radius. Square corners
    // on one side are met by carrying the rectangle a radius past that side. Shapes with other corners have none (the window
    // region draws them instead).
    public readonly record struct RoundedClip(double X, double Y, double Width, double Height, double Radius);
    public readonly record struct GlassClip(RoundedClip First);

    // origin, along, depth: where the outline's (0, 0), (w, 0) and (0, h) fall in the window's pixels (an axis-aligned turn or
    // mirror allowed). The corners are the layout's (Measure).
    public static bool TryRoundedClip(Layout layout, (double X, double Y) origin, (double X, double Y) along, (double X, double Y) depth, out GlassClip clip)
    {
        clip = default;
        if (layout.Shoulder > 0 || !(layout.Width > 0) || !(layout.Height > 0)) return false;
        double ax = along.X - origin.X, ay = along.Y - origin.Y, dx = depth.X - origin.X, dy = depth.Y - origin.Y;
        double lengthA = Math.Sqrt(ax * ax + ay * ay), lengthD = Math.Sqrt(dx * dx + dy * dy);
        if (!(lengthA > 0) || !(lengthD > 0)) return false;
        // Only quarter turns and mirrors: one component of each axis is zero.
        const double Square = 1e-6;
        if (Math.Min(Math.Abs(ax), Math.Abs(ay)) > Square * lengthA || Math.Min(Math.Abs(dx), Math.Abs(dy)) > Square * lengthD) return false;
        double scale = lengthA / layout.Width;
        if (Math.Abs(lengthD / layout.Height - scale) > 1e-6 * scale) return false;
        // The inset outline starts at (inset, top) of the full bounds.
        double ox = origin.X + (ax * layout.Inset) / lengthA * scale + (dx * layout.Top) / lengthD * scale;
        double oy = origin.Y + (ay * layout.Inset) / lengthA * scale + (dy * layout.Top) / lengthD * scale;
        double ux = ax / lengthA, uy = ay / lengthA, vx = dx / lengthD, vy = dy / lengthD;
        double w = layout.Width * scale, h = layout.Height * scale;
        double x0 = Math.Min(ox, ox + ux * w + vx * h), y0 = Math.Min(oy, oy + uy * w + vy * h);
        double width = Math.Abs(ux * w + vx * h), height = Math.Abs(uy * w + vy * h);
        // Each local corner's place on screen: left or right, top or bottom of the box.
        Span<double> radius = [layout.TopLeft * scale, layout.TopRight * scale, layout.BottomRight * scale, layout.BottomLeft * scale];
        Span<(double X, double Y)> local = [(0, 0), (w, 0), (w, h), (0, h)];
        // Screen corners: 0 top left, 1 top right, 2 bottom right, 3 bottom left.
        Span<double> screen = stackalloc double[4];
        for (int i = 0; i < 4; i++)
        {
            double sx = ox + ux * local[i].X + vx * local[i].Y, sy = oy + uy * local[i].X + vy * local[i].Y;
            bool right = sx > x0 + width / 2, bottom = sy > y0 + height / 2;
            screen[bottom ? (right ? 2 : 3) : (right ? 1 : 0)] = radius[i];
        }
        const double Same = .01;
        double r = Math.Max(Math.Max(screen[0], screen[1]), Math.Max(screen[2], screen[3]));
        int square = 0, squareMask = 0;
        bool even = true;
        for (int i = 0; i < 4; i++)
        {
            if (screen[i] <= Same) { square++; squareMask |= 1 << i; }
            else if (Math.Abs(screen[i] - r) > Same) even = false;
        }
        if (even)
        {
            if (square == 4 || r <= Same) { clip = new(new(x0, y0, width, height, 0)); return true; }
            if (square == 0) { clip = new(new(x0, y0, width, height, r)); return true; }
            // Two square corners on one side: the rounded rectangle reaches a radius past it.
            RoundedClip? one = squareMask switch
            {
                0b0011 => new(x0, y0 - r, width, height + r, r),     // top
                0b1100 => new(x0, y0, width, height + r, r),         // bottom
                0b1001 => new(x0 - r, y0, width + r, height, r),     // left
                0b0110 => new(x0, y0, width + r, height, r),         // right
                _ => null
            };
            if (one is not { } single) return false;
            clip = new(single);
            return true;
        }
        // Two pairs of different radii (an opening squaring the top while the bottom rounds): one rounded rectangle with the
        // larger radius. A clip on the glass's container does not reach the backdrop (DWM draws it with the sprite's own clip
        // only), so the exact shape would need a path geometry; the larger radius keeps the glass inside the outline for those
        // few frames, never past it.
        if (Math.Abs(screen[0] - screen[1]) <= Same && Math.Abs(screen[3] - screen[2]) <= Same && screen[0] > Same && screen[3] > Same
            || Math.Abs(screen[0] - screen[3]) <= Same && Math.Abs(screen[1] - screen[2]) <= Same && screen[0] > Same && screen[1] > Same)
        {
            clip = new(new(x0, y0, width, height, Math.Min(r, Math.Min(width, height) / 2)));
            return true;
        }
        return false;
    }

    // Liquid glass's optical edge. A bevel of height h(t) = 6t^5 - 15t^4 + 10t^3 (t = distance inward over the bevel width) lifts
    // the edge into the surface; the light it catches follows its slope, h'(t) = 30t^2(1-t)^2, zero at the outline and at the
    // inner end and continuous in its first derivative, so the edge has no line drawn on it and no step inward. A faint contour
    // (baseline) falls off with the same curve from the outline, so the shape still reads where the bevel catches no light.
    public static double Smootherstep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * t * (t * (6 * t - 15) + 10);
    }

    // The slope of the bevel normalised to one at its steepest (t = .5).
    public static double Slope(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double s = t * (1 - t);
        return 16 * s * s;
    }

    // The edge's light at t: the contour plus the bevel's light, never above the peak.
    public static double Profile(double t, double peak, double baseline)
    {
        if (t >= 1) return 0;
        return Math.Clamp(baseline * (1 - Smootherstep(t)) + peak * Slope(t), 0, Math.Max(peak, baseline));
    }

    // The profile as rings one step wide inward from the outline: ring k covers [k, k + 1) steps and takes the profile at its
    // middle, the last one up to the width. Rings tile without overlapping, so every pixel takes one ring's alpha once; the
    // antialiased line between two rings blends their neighbouring values, which reads as the continuous curve. Returns the
    // number of rings written (at most alphas.Length).
    public static int Bands(double widthSteps, double peak, double baseline, Span<double> alphas)
    {
        if (!(widthSteps > 0) || alphas.Length == 0) return 0;
        int count = Math.Min(alphas.Length, (int)Math.Ceiling(widthSteps - 1e-9));
        for (int k = 0; k < count; k++)
        {
            double inner = Math.Min(k + 1, widthSteps), middle = (k + inner) / 2;
            alphas[k] = Profile(middle / widthSteps, peak, baseline);
        }
        return count;
    }

    // The bevel's width in DIP for the opening shown: thin on the capsule, wider on the open panel, eased so it changes no faster
    // at either end than in the middle.
    public const double CollapsedBevel = 2.0, ExpandedBevel = 3.2;
    public static double BevelWidth(double openness) => CollapsedBevel + (ExpandedBevel - CollapsedBevel) * Smootherstep(openness);

    // The light's share around the island: the theme's rim gradient, each stop relative to the brightest, held within
    // [LightLeast, 1] of the brightest.
    public const double LightLeast = .6;

    // The stops' alphas shown (shown[i], 0..1) for the theme's (relative[i], 0..1 of the brightest): moved into [LightLeast, 1]
    // in the same order, then scaled so their mean stays the theme's, so the light is flatter around the island without being
    // brighter overall (the dim side gains what the bright side gives up).
    public static void LightStops(ReadOnlySpan<double> relative, Span<double> shown)
    {
        double theme = 0, flat = 0;
        for (int i = 0; i < relative.Length; i++)
        {
            double a = Math.Clamp(double.IsFinite(relative[i]) ? relative[i] : 0, 0, 1);
            shown[i] = LightLeast + (1 - LightLeast) * a;
            theme += a; flat += shown[i];
        }
        double scale = flat > 0 ? theme / flat : 0;
        for (int i = 0; i < relative.Length; i++) shown[i] *= scale;
    }

    // The bevel's light and contour for a palette (0 dark, 1 light; a turn passes through the values between).
    public const double DarkPeak = .22, LightPeak = .16, DarkBaseline = .05, LightBaseline = .045;
    public static (double Peak, double Baseline) Light(double palette)
    {
        double p = Math.Clamp(double.IsFinite(palette) ? palette : 0, 0, 1);
        return (DarkPeak + (LightPeak - DarkPeak) * p, DarkBaseline + (LightBaseline - DarkBaseline) * p);
    }
}
