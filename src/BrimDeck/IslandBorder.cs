using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrimDeck;

public sealed class IslandBorder : Border
{
    // A faint inner rim keeps the outline readable over wallpapers of the surface's own tone; the pen is twice the rim width
    // because the layout clip removes the outer half of the stroke.
    private static readonly Brush DefaultRim = CreateRim();
    public static readonly DependencyProperty ShoulderRadiusProperty = DependencyProperty.Register(
        nameof(ShoulderRadius), typeof(double), typeof(IslandBorder),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty MaterialOpacityProperty = DependencyProperty.Register(
        nameof(MaterialOpacity), typeof(double), typeof(IslandBorder),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    // The theme's layers over the base color: a gradient and a pattern (scan lines). Like the rim they fade with MaterialOpacity,
    // so the indicator, which has neither, keeps its plain color.
    public static readonly DependencyProperty GradientProperty = DependencyProperty.Register(
        nameof(Gradient), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PatternProperty = DependencyProperty.Register(
        nameof(Pattern), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RimBrushProperty = DependencyProperty.Register(
        nameof(RimBrush), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(DefaultRim, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((IslandBorder)d)._rim = null));
    // A new width also rebuilds a pixel theme's bevel, which is made with the outline.
    public static readonly DependencyProperty RimWidthProperty = DependencyProperty.Register(
        nameof(RimWidth), typeof(double), typeof(IslandBorder), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => { var island = (IslandBorder)d; island._rim = null; island._outlineKey = null; }));
    // Draws the outline this far inside the element's bounds, for the copy that casts the island's shadow. The shadow effect
    // also draws the copy itself; lying exactly under the island, its edge would double the coverage of the island's
    // antialiased edge pixels and leave a stepped edge. A shape with shoulders reaches the screen edge at its top, where the
    // shoulders flare out, so it keeps its top edge and moves in at the sides and the bottom only.
    public static readonly DependencyProperty InsetProperty = DependencyProperty.Register(
        nameof(Inset), typeof(double), typeof(IslandBorder), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    // Pixel art (a theme's "pixel" effect): the outline's curves become steps of this many DIP, rounded to whole device pixels,
    // and with a BevelBrush the rim becomes a bevel: RimBrush lights the edges that face up and left, BevelBrush shades the
    // ones that face down and right. 0 keeps the smooth outline.
    public static readonly DependencyProperty PixelProperty = DependencyProperty.Register(
        nameof(Pixel), typeof(double), typeof(IslandBorder), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty BevelBrushProperty = DependencyProperty.Register(
        nameof(BevelBrush), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    // Glass (a theme's "glass" effect): light gathered along the inside of the outline, strongest at the edge and fading over a
    // few DIP, as a thick lens gathers it. Null draws none.
    public static readonly DependencyProperty EdgeBrushProperty = DependencyProperty.Register(
        nameof(EdgeBrush), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((IslandBorder)d)._edge = null));

    // The edge light's bands: widths in DIP inside the outline, each adding an equal share of the light, so it fades inward.
    private static readonly double[] EdgeBands = [1.5, 3.5, 7];

    private Pen? _rim;
    private Pen[]? _edge;
    private (Size Size, double Shoulder, CornerRadius Corners, double Inset, double Step, double Rim)? _outlineKey;
    private Geometry _outline = Geometry.Empty;
    private (Geometry Light, Geometry Shade)? _bevel;

    static IslandBorder() => CornerRadiusProperty.OverrideMetadata(typeof(IslandBorder),
        new FrameworkPropertyMetadata(default(CornerRadius), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double ShoulderRadius
    {
        get => (double)GetValue(ShoulderRadiusProperty);
        set => SetValue(ShoulderRadiusProperty, value);
    }

    public double MaterialOpacity
    {
        get => (double)GetValue(MaterialOpacityProperty);
        set => SetValue(MaterialOpacityProperty, value);
    }
    public Brush? Gradient { get => (Brush?)GetValue(GradientProperty); set => SetValue(GradientProperty, value); }
    public Brush? Pattern { get => (Brush?)GetValue(PatternProperty); set => SetValue(PatternProperty, value); }
    public Brush RimBrush { get => (Brush)GetValue(RimBrushProperty); set => SetValue(RimBrushProperty, value); }
    public double RimWidth { get => (double)GetValue(RimWidthProperty); set => SetValue(RimWidthProperty, value); }
    public double Inset { get => (double)GetValue(InsetProperty); set => SetValue(InsetProperty, value); }
    public double Pixel { get => (double)GetValue(PixelProperty); set => SetValue(PixelProperty, value); }
    public Brush? BevelBrush { get => (Brush?)GetValue(BevelBrushProperty); set => SetValue(BevelBrushProperty, value); }
    public Brush? EdgeBrush { get => (Brush?)GetValue(EdgeBrushProperty); set => SetValue(EdgeBrushProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var outline = Outline();
        drawingContext.DrawGeometry(Background, null, outline);
        if (MaterialOpacity <= 0) return;
        // Only a fading material needs the opacity layer; fully shown, the layers draw straight onto the surface.
        bool fading = MaterialOpacity < 1;
        if (fading) drawingContext.PushOpacity(Math.Clamp(MaterialOpacity, 0, 1));
        if (Gradient is { } gradient) drawingContext.DrawGeometry(gradient, null, outline);
        if (Pattern is { } pattern) drawingContext.DrawGeometry(pattern, null, outline);
        if (EdgeBrush is { } edge)
        {
            _edge ??= EdgePens(edge);
            DrawEdge(drawingContext, _edge, outline);
        }
        if (RimWidth > 0)
        {
            if (_bevel is { } bevel && BevelBrush is { } shade)
            {
                drawingContext.DrawGeometry(RimBrush, null, bevel.Light);
                drawingContext.DrawGeometry(shade, null, bevel.Shade);
            }
            else
            {
                // A rim brush the ambient light animates (AmbientGlow) stays unfrozen, and so does its pen: freezing the pen would
                // freeze the brush. Its changes then reach the screen without this method running again.
                if (_rim is null) { _rim = new Pen(RimBrush, RimWidth * 2); if (RimBrush.IsFrozen) _rim.Freeze(); }
                drawingContext.DrawGeometry(null, _rim, outline);
            }
        }
        if (fading) drawingContext.Pop();
    }

    // The visible surface owns input; its decorative shadow is a separate, non-interactive sibling.
    // At rest collapsed (SettledClip, set by the window), the content keeps clear of the corners and shoulders and is clipped to
    // the bounds instead: a rounded clip made each frame of the scrolling title and the bars cost about a quarter more. The surface
    // itself is still drawn as its outline (OnRender).
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => _settledClip ? Bounds() : Outline();
    private bool _settledClip;
    public bool SettledClip
    {
        get => _settledClip;
        set { if (_settledClip == value) return; _settledClip = value; InvalidateArrange(); InvalidateVisual(); }
    }
    private RectangleGeometry? _bounds;
    private Geometry Bounds()
    {
        var rect = new Rect(RenderSize);
        if (_bounds?.Rect != rect) { _bounds = new RectangleGeometry(rect); _bounds.Freeze(); }
        return _bounds;
    }

    // A pixel theme's steps are whole device pixels, so a new scale needs a new outline.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (Pixel > 0) { _outlineKey = null; InvalidateArrange(); InvalidateVisual(); }
    }

    private static Brush CreateRim()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        brush.Freeze();
        return brush;
    }

    private Geometry Outline()
    {
        double step = PixelStep(Pixel, this);
        var key = (RenderSize, ShoulderRadius, CornerRadius, Inset, step, RimWidth);
        if (_outlineKey == key) return _outline;
        _outlineKey = key;
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // Stepped, the inset (the shadow caster's) is whole device pixels too, so its steps stay on them at 125 % or 175 %.
        double inset = step > 0 ? Math.Round(Inset * scale, MidpointRounding.AwayFromZero) / scale : Inset;
        (_outline, _bevel) = Shape(RenderSize.Width, RenderSize.Height, ShoulderRadius, CornerRadius, inset, step, RimWidth, scale);
        return _outline;
    }

    // A surface drawn as the island (the island, a notice): its outline, stepped for a pixel theme, and then its bevel, a whole
    // number of device pixels thick and never thinner than one.
    internal static (Geometry Outline, (Geometry Light, Geometry Shade)? Bevel) Shape(double w, double h, double shoulder, CornerRadius corners, double inset, double step, double rimWidth, double dpi)
    {
        if (step <= 0) return (BuildOutline(w, h, shoulder, corners, inset), null);
        var stairs = BuildStairs(w, h, shoulder, corners, inset, step);
        return (Polygon(stairs), rimWidth > 0 ? Bevel(stairs, Math.Max(1, Math.Round(rimWidth * dpi, MidpointRounding.AwayFromZero)) / dpi) : null);
    }

    // The glass's edge light (EdgeBrush): pens twice each band's width, as the rim's, since the clip removes the outer half; each
    // band adds an equal share of the light.
    internal static Pen[] EdgePens(Brush edge) => [.. EdgeBands.Select(band => { var pen = new Pen(edge, band * 2); if (edge.IsFrozen) pen.Freeze(); return pen; })];
    internal static void DrawEdge(DrawingContext dc, Pen[] pens, Geometry outline)
    {
        dc.PushOpacity(1d / pens.Length);
        foreach (var pen in pens) dc.DrawGeometry(null, pen, outline);
        dc.Pop();
    }

    // A pixel size in DIP as whole device pixels (at least one) on the visual's display, or 0 for none.
    internal static double PixelStep(double pixel, Visual visual)
    {
        if (!(pixel > 0) || !double.IsFinite(pixel)) return 0;
        double dpi = VisualTreeHelper.GetDpi(visual).PixelsPerDip;
        return Math.Max(1, Math.Round(pixel * dpi, MidpointRounding.AwayFromZero)) / dpi;
    }

    // The outline as the island draws it: stepped for a pixel theme (BuildStairs), smooth otherwise. Notices draw the same.
    internal static Geometry BuildOutline(double w, double h, double shoulderRadius, CornerRadius corners, double insetBy, double step)
        => step > 0 ? Polygon(BuildStairs(w, h, shoulderRadius, corners, insetBy, step)) : BuildOutline(w, h, shoulderRadius, corners, insetBy);

    // The outline with its curves turned into steps of `step` DIP: every column of that width keeps the rows whose centres lie
    // inside the smooth outline. The island's outline meets every vertical line in one run (the shoulders flare out at the
    // top, the corners round off the bottom), so a column is a top and a bottom. Columns are counted inward from both sides and
    // rows from the top and from the bottom, so the left and right corners, and the top and bottom ones, mirror each other
    // whatever the size. Returns the corners of the stepped outline, clockwise from the top left; every edge is axis-aligned.
    internal static IReadOnlyList<Point> BuildStairs(double w, double h, double shoulderRadius, CornerRadius corners, double insetBy, double step)
    {
        var smooth = BuildOutline(w, h, shoulderRadius, corners, insetBy);
        if (smooth.IsEmpty() || !(step > 0)) return [];
        // Blocks, not a fine staircase: a corner takes about four steps whatever its radius, each a whole number of pixels.
        double curve = Math.Max(Math.Max(Math.Max(corners.TopLeft, corners.TopRight), Math.Max(corners.BottomLeft, corners.BottomRight)), shoulderRadius);
        step *= Math.Max(1, Math.Round(curve / (4 * step)));
        // The flattened copy keeps the inset as its own transform (or has it applied already, leaving none).
        var flat = smooth.GetFlattenedPathGeometry(.02, ToleranceType.Absolute);
        var matrix = flat.Transform?.Value ?? Matrix.Identity;
        var edges = new List<(Point A, Point B)>();
        foreach (var figure in flat.Figures)
        {
            var points = new List<Point> { figure.StartPoint };
            foreach (var segment in figure.Segments)
                if (segment is PolyLineSegment poly) points.AddRange(poly.Points); else if (segment is LineSegment line) points.Add(line.Point);
            for (int i = 0; i < points.Count; i++) edges.Add((matrix.Transform(points[i]), matrix.Transform(points[(i + 1) % points.Count])));
        }
        var bounds = smooth.Bounds;
        double left = bounds.Left, right = bounds.Right, top = bounds.Top, bottom = bounds.Bottom, middle = (left + right) / 2;
        // Column edges: inward from the left and from the right; whatever is left in the middle is one column.
        var cuts = new SortedSet<double> { left, right };
        for (double x = left + step; x < middle; x += step) cuts.Add(x);
        for (double x = right - step; x > middle; x -= step) cuts.Add(x);
        var columns = cuts.ToList();
        var tops = new double[columns.Count - 1]; var bottoms = new double[columns.Count - 1];
        for (int i = 0; i + 1 < columns.Count; i++)
        {
            double xc = (columns[i] + columns[i + 1]) / 2, lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var (a, b) in edges)
            {
                if (a.X <= xc == b.X <= xc) continue;
                double y = a.Y + (b.Y - a.Y) * (xc - a.X) / (b.X - a.X);
                lo = Math.Min(lo, y); hi = Math.Max(hi, y);
            }
            if (double.IsInfinity(lo)) { lo = top; hi = bottom; }
            tops[i] = lo <= top + 1e-6 ? top : Math.Min(bottom, top + step * Math.Ceiling((lo - top) / step - .5));
            bottoms[i] = hi >= bottom - 1e-6 ? bottom : Math.Max(top, bottom - step * Math.Ceiling((bottom - hi) / step - .5));
        }
        // A notch's shoulders flare thinner than a block at the screen edge: columns left empty at either end are dropped, so
        // the pixel notch meets the edge square.
        int first = 0, last = tops.Length - 1;
        while (first < last && bottoms[first] - tops[first] < 1e-6) first++;
        while (last > first && bottoms[last] - tops[last] < 1e-6) last--;
        var outline = new List<Point>();
        void Add(Point p)
        {
            if (outline.Count > 0 && (outline[^1] - p).LengthSquared < 1e-12) return;
            // Three points on one line: the middle one goes.
            if (outline.Count > 1 && (Math.Abs(outline[^2].X - outline[^1].X) < 1e-9 && Math.Abs(outline[^1].X - p.X) < 1e-9
                || Math.Abs(outline[^2].Y - outline[^1].Y) < 1e-9 && Math.Abs(outline[^1].Y - p.Y) < 1e-9)) outline[^1] = p;
            else outline.Add(p);
        }
        for (int i = first; i <= last; i++) { Add(new Point(columns[i], tops[i])); Add(new Point(columns[i + 1], tops[i])); }
        for (int i = last; i >= first; i--) { Add(new Point(columns[i + 1], bottoms[i])); Add(new Point(columns[i], bottoms[i])); }
        return outline;
    }

    private static Geometry Polygon(IReadOnlyList<Point> points)
    {
        if (points.Count < 3) return Geometry.Empty;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], true, true);
            path.PolyLineTo(points.Skip(1).ToList(), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    // The bevel of a stepped outline: a band `thickness` DIP wide inside every edge, those facing up or left in one geometry and
    // those facing down or right in the other. The outline runs clockwise, so the inside lies to the right of each edge: an edge
    // running right faces up, one running down faces right, and so on.
    internal static (Geometry Light, Geometry Shade) Bevel(IReadOnlyList<Point> points, double thickness)
    {
        var light = new GeometryGroup(); var shade = new GeometryGroup();
        for (int i = 0; i < points.Count; i++)
        {
            Point a = points[i], b = points[(i + 1) % points.Count];
            if (Math.Abs(a.Y - b.Y) < 1e-9)
            {
                if (a.X < b.X) light.Children.Add(new RectangleGeometry(new Rect(a.X, a.Y, b.X - a.X, thickness)));
                else shade.Children.Add(new RectangleGeometry(new Rect(b.X, a.Y - thickness, a.X - b.X, thickness)));
            }
            else if (a.Y < b.Y) shade.Children.Add(new RectangleGeometry(new Rect(a.X - thickness, a.Y, thickness, b.Y - a.Y)));
            else light.Children.Add(new RectangleGeometry(new Rect(a.X, b.Y, thickness, a.Y - b.Y)));
        }
        light.Freeze(); shade.Freeze();
        return (light, shade);
    }

    // The island's outline for a size, shoulder and corners; dynamic notices draw the same outline, so that they start and end
    // exactly on the island. insetBy draws it that far inside the bounds (Inset). Frozen.
    internal static Geometry BuildOutline(double w, double h, double shoulderRadius, CornerRadius corners, double insetBy = 0)
    {
        if (!(w > 0) || !(h > 0)) return Geometry.Empty;
        // The shoulders are sized on the full bounds, so an inset outline has the same shoulders moved in by the inset: its
        // curves then lie inside the full outline's. The corners keep their centres and lose the inset from their radii.
        double shoulder = Math.Clamp(shoulderRadius, 0, Math.Min(w / 4, h / 2));
        double shoulderDepth = shoulder * (7d / 16);
        double inset = Math.Clamp(insetBy, 0, Math.Min(w, h) / 4), top = shoulder > 0 ? 0 : inset;
        w -= 2 * inset; h -= top + inset;
        double left = shoulder, right = w - shoulder;
        double limit = Math.Max(0, Math.Min(shoulder > 0 ? h - shoulderDepth : h / 2, (right - left) / 2));
        double bl = Math.Clamp(corners.BottomLeft - inset, 0, limit);
        double br = Math.Clamp(corners.BottomRight - inset, 0, limit);
        double tl = Math.Clamp(corners.TopLeft - inset, 0, Math.Min(w, h) / 2);
        double tr = Math.Clamp(corners.TopRight - inset, 0, Math.Min(w, h) / 2);
        const double k = .5522847498307936; // Cubic approximation of a circular quadrant.
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(shoulder > 0 ? 0 : tl, 0), true, true);
            path.LineTo(new Point(shoulder > 0 ? w : w - tr, 0), true, false);
            if (shoulder > 0)
            {
                // Two C2-joined cubics: curvature starts and ends at zero, matching
                // the straight edges. Independent width/depth avoids a circular cutout.
                path.BezierTo(new Point(w - shoulder * .28, 0), new Point(w - shoulder * .64, 0),
                    new Point(w - shoulder * .82, shoulderDepth * .18), true, true);
                path.BezierTo(new Point(right, shoulderDepth * .36), new Point(right, shoulderDepth * .72),
                    new Point(right, shoulderDepth), true, true);
            }
            else
                path.BezierTo(new Point(w - tr * (1 - k), 0), new Point(w, tr * (1 - k)),
                    new Point(w, tr), true, true);
            path.LineTo(new Point(right, h - br), true, false);
            path.BezierTo(new Point(right, h - br * (1 - k)), new Point(right - br * (1 - k), h),
                new Point(right - br, h), true, true);
            path.LineTo(new Point(left + bl, h), true, false);
            path.BezierTo(new Point(left + bl * (1 - k), h), new Point(left, h - bl * (1 - k)),
                new Point(left, h - bl), true, true);
            path.LineTo(new Point(left, shoulder > 0 ? shoulderDepth : tl), true, false);
            if (shoulder > 0)
            {
                path.BezierTo(new Point(left, shoulderDepth * .72), new Point(left, shoulderDepth * .36),
                    new Point(shoulder * .82, shoulderDepth * .18), true, true);
                path.BezierTo(new Point(shoulder * .64, 0), new Point(shoulder * .28, 0),
                    new Point(0, 0), true, true);
            }
            else
                path.BezierTo(new Point(0, tl * (1 - k)), new Point(tl * (1 - k), 0),
                    new Point(tl, 0), true, true);
        }
        if (inset > 0) geometry.Transform = new TranslateTransform(inset, top);
        geometry.Freeze();
        return geometry;
    }
}
