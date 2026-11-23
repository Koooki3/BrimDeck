using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

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
    // The surface's own opacity (DeckSettings.SurfaceTransparency): the base color and the theme's layers are drawn this opaque,
    // the rim and the content as they are.
    public static readonly DependencyProperty SurfaceOpacityProperty = DependencyProperty.Register(
        nameof(SurfaceOpacity), typeof(double), typeof(IslandBorder),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    // The theme's layers over the base color: a gradient and a pattern (scan lines, or a pixel theme's texture). Like the rim
    // they fade with MaterialOpacity, so the indicator, which has neither, keeps its plain color.
    public static readonly DependencyProperty GradientProperty = DependencyProperty.Register(
        nameof(Gradient), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PatternProperty = DependencyProperty.Register(
        nameof(Pattern), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    // The theme's motifs (IslandDecor), between the gradient and the pattern, fading with the material like them.
    public static readonly DependencyProperty DecorProperty = DependencyProperty.Register(
        nameof(Decor), typeof(IslandDecor), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
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
    // Liquid glass's optical edge (GlassEdge), drawn in place of the rim; MainWindow.Glass sets it while the glass shows.
    private GlassEdge? _edge;
    internal GlassEdge? Edge
    {
        get => _edge;
        set { if (ReferenceEquals(_edge, value)) return; _edge = value; _rings = null; InvalidateVisual(); }
    }

    private Pen? _rim;
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
    public double SurfaceOpacity { get => (double)GetValue(SurfaceOpacityProperty); set => SetValue(SurfaceOpacityProperty, value); }
    public Brush? Gradient { get => (Brush?)GetValue(GradientProperty); set => SetValue(GradientProperty, value); }
    public Brush? Pattern { get => (Brush?)GetValue(PatternProperty); set => SetValue(PatternProperty, value); }
    internal IslandDecor? Decor { get => (IslandDecor?)GetValue(DecorProperty); set => SetValue(DecorProperty, value); }
    public Brush RimBrush { get => (Brush)GetValue(RimBrushProperty); set => SetValue(RimBrushProperty, value); }
    public double RimWidth { get => (double)GetValue(RimWidthProperty); set => SetValue(RimWidthProperty, value); }
    public double Inset { get => (double)GetValue(InsetProperty); set => SetValue(InsetProperty, value); }
    public double Pixel { get => (double)GetValue(PixelProperty); set => SetValue(PixelProperty, value); }
    public Brush? BevelBrush { get => (Brush?)GetValue(BevelBrushProperty); set => SetValue(BevelBrushProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var outline = Outline();
        double material = Math.Clamp(MaterialOpacity, 0, 1), surface = Math.Clamp(SurfaceOpacity, 0, 1);
        // Only a fading material needs the opacity layer; fully shown, the layers draw straight onto the surface.
        bool fading = material < 1;
        if (surface >= 1)
        {
            drawingContext.DrawGeometry(Background, null, outline);
            if (material <= 0) return;
            if (fading) drawingContext.PushOpacity(material);
            Layers(drawingContext, outline);
            Rim(drawingContext, outline);
            if (fading) drawingContext.Pop();
            return;
        }
        // See-through: the base color and the layers together take the surface's opacity, and the rim keeps its own strength so
        // the outline stays readable.
        drawingContext.PushOpacity(surface);
        // The base is drawn even fully clear (liquid glass): its outline is what takes the pointer. The layers are not, as nothing
        // of them would show.
        drawingContext.DrawGeometry(Background, null, outline);
        if (material > 0 && surface > 0)
        {
            if (fading) drawingContext.PushOpacity(material);
            Layers(drawingContext, outline);
            if (fading) drawingContext.Pop();
        }
        drawingContext.Pop();
        if (material <= 0) return;
        if (fading) drawingContext.PushOpacity(material);
        Rim(drawingContext, outline);
        if (fading) drawingContext.Pop();
    }

    private void Layers(DrawingContext drawingContext, Geometry outline)
    {
        if (Gradient is { } gradient) drawingContext.DrawGeometry(gradient, null, outline);
        Decor?.Draw(drawingContext, RenderSize, outline);
        if (Pattern is { } pattern) drawingContext.DrawGeometry(pattern, null, outline);
    }

    private void Rim(DrawingContext drawingContext, Geometry outline)
    {
        if (_edge is { Count: > 0 } edge && Pixel <= 0) { DrawEdge(drawingContext, edge); return; }
        if (RimWidth <= 0) return;
        if (_bevel is { } bevel && BevelBrush is { } shade)
        {
            drawingContext.DrawGeometry(RimBrush, null, bevel.Light);
            drawingContext.DrawGeometry(shade, null, bevel.Shade);
        }
        else if (_settledClip) drawingContext.DrawGeometry(RimBrush, null, Ring(outline));
        else
        {
            if (_rim is null) { _rim = new Pen(RimBrush, RimWidth * 2); _rim.Freeze(); }
            drawingContext.DrawGeometry(null, _rim, outline);
        }
    }

    // The optical edge as its rings (GlassEdge): ring k lies between the outline drawn k and k + 1 steps inside, so each pixel of
    // the edge is covered by one ring once. Each ring is the two outlines written into one StreamGeometry filled even-odd (BuildRing):
    // no widened or combined geometry to compute, and its bounds, which the render pass reads, need no conversion to a path (a
    // GeometryGroup's cost about 18 KB of path objects per ring on every frame of an animation).
    private void DrawEdge(DrawingContext drawingContext, GlassEdge edge)
    {
        // The rings for this frame's size, before they are drawn: an animation's frame is drawn once, with its own rings.
        edge.Fit?.Invoke(RenderSize.Height);
        var key = (_outline, edge.Count, edge.Step, edge.Width);
        if (_rings is null || _ringsKey != key)
        {
            _ringsKey = key;
            _rings ??= new Geometry[GlassEdge.MaxBands];
            double outer = Inset;
            for (int k = 0; k < edge.Count; k++)
            {
                double inner = Inset + Math.Min((k + 1) * edge.Step, edge.Width);
                _rings[k] = BuildRing(RenderSize.Width, RenderSize.Height, ShoulderRadius, CornerRadius, outer, inner);
                outer = inner;
            }
        }
        for (int k = 0; k < edge.Count; k++) drawingContext.DrawGeometry(edge.Brushes[k], null, _rings[k]);
    }
    private Geometry[]? _rings;
    private (Geometry Outline, int Count, double Step, double Width)? _ringsKey;
    // The rings drawn last and the edge width they were made for (for the checks).
    internal Geometry? Ring(int k) => _rings is { } rings && _ringsKey is { } key && k < key.Count ? rings[k] : null;
    internal double RingsWidth => _ringsKey?.Width ?? double.NaN;

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
    // The rim's inner half as a filled ring, for the outline it was made from; made only at rest, so never on an animation's frames.
    private Geometry? _ring, _ringOutline;
    private double _ringWidth;
    private Geometry Ring(Geometry outline)
    {
        if (_ring is not null && ReferenceEquals(_ringOutline, outline) && _ringWidth == RimWidth) return _ring;
        var widened = outline.GetWidenedPathGeometry(new Pen(Brushes.Black, RimWidth * 2));
        var ring = Geometry.Combine(outline, widened, GeometryCombineMode.Intersect, null);
        ring.Freeze();
        _ring = ring; _ringOutline = outline; _ringWidth = RimWidth;
        return ring;
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
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip, step = PixelStairs.Step(Pixel, dpi);
        var key = (RenderSize, ShoulderRadius, CornerRadius, Inset, step, RimWidth);
        if (_outlineKey == key) return _outline;
        _outlineKey = key;
        // Stepped, the inset (the shadow caster's) is whole device pixels too, so its steps stay on them at 125 % or 175 %.
        double inset = step > 0 ? Math.Round(Inset * dpi, MidpointRounding.AwayFromZero) / dpi : Inset;
        (_outline, _bevel) = Shape(RenderSize.Width, RenderSize.Height, ShoulderRadius, CornerRadius, inset, step, RimWidth, dpi);
        return _outline;
    }

    // The outline, stepped for a pixel theme, and then its bevel, a whole number of device pixels thick and never thinner than one.
    private static (Geometry Outline, (Geometry Light, Geometry Shade)? Bevel) Shape(double w, double h, double shoulder, CornerRadius corners, double inset, double step, double rimWidth, double dpi)
    {
        if (step <= 0) return (BuildOutline(w, h, shoulder, corners, inset), null);
        var stairs = BuildStairs(w, h, shoulder, corners, inset, step);
        return (Polygon(stairs), rimWidth > 0 && stairs.Count >= 3 ? Bevel(stairs, PixelStairs.Step(rimWidth, dpi)) : null);
    }

    // The smooth outline flattened into straight edges, turned into steps (PixelStairs.Build).
    private static IReadOnlyList<Point> BuildStairs(double w, double h, double shoulderRadius, CornerRadius corners, double insetBy, double step)
    {
        var smooth = BuildOutline(w, h, shoulderRadius, corners, insetBy);
        if (smooth.IsEmpty()) return [];
        // The flattened copy keeps the inset as its own transform (or has it applied already, leaving none): only that transform
        // is applied, as applying the smooth outline's as well would move the shadow's copy in twice.
        var flat = smooth.GetFlattenedPathGeometry(.02, ToleranceType.Absolute);
        var matrix = flat.Transform?.Value ?? Matrix.Identity;
        var edges = new List<((double X, double Y) A, (double X, double Y) B)>();
        foreach (var figure in flat.Figures)
        {
            var points = new List<Point> { figure.StartPoint };
            foreach (var segment in figure.Segments)
                if (segment is PolyLineSegment poly) points.AddRange(poly.Points); else if (segment is LineSegment line) points.Add(line.Point);
            for (int i = 0; i < points.Count; i++)
            {
                Point a = matrix.Transform(points[i]), b = matrix.Transform(points[(i + 1) % points.Count]);
                edges.Add(((a.X, a.Y), (b.X, b.Y)));
            }
        }
        var bounds = smooth.Bounds;
        double curve = Math.Max(Math.Max(Math.Max(corners.TopLeft, corners.TopRight), Math.Max(corners.BottomLeft, corners.BottomRight)), shoulderRadius);
        return [.. PixelStairs.Build(edges, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, curve, step).Select(p => new Point(p.X, p.Y))];
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
    private static (Geometry Light, Geometry Shade) Bevel(IReadOnlyList<Point> points, double thickness)
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

    // The band between the outline drawn outerInset and innerInset inside the bounds: both outlines as two figures of one
    // StreamGeometry, filled even-odd, each placed where BuildOutline's own transform puts it. Frozen.
    internal static Geometry BuildRing(double w, double h, double shoulderRadius, CornerRadius corners, double outerInset, double innerInset)
    {
        if (!(w > 0) || !(h > 0)) return Geometry.Empty;
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var path = geometry.Open())
        {
            WriteOutline(path, GlassBoundaryMath.Measure(w, h, shoulderRadius, corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft, outerInset), true);
            WriteOutline(path, GlassBoundaryMath.Measure(w, h, shoulderRadius, corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft, innerInset), true);
        }
        geometry.Freeze();
        return geometry;
    }

    // The island's smooth outline for a size, shoulder and corners; insetBy draws it that far inside the bounds (Inset). Frozen.
    // The parts are GlassBoundaryMath's (Measure, RightShoulder), where the clamps and the shoulder curves are tested.
    internal static Geometry BuildOutline(double w, double h, double shoulderRadius, CornerRadius corners, double insetBy)
    {
        if (!(w > 0) || !(h > 0)) return Geometry.Empty;
        var o = GlassBoundaryMath.Measure(w, h, shoulderRadius, corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft, insetBy);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open()) WriteOutline(path, o, false);
        if (o.Inset > 0) geometry.Transform = new TranslateTransform(o.Inset, o.Top);
        geometry.Freeze();
        return geometry;
    }

    // One outline as a closed figure (GlassBoundaryMath.WriteOutline, which the glass layer's clip is written from too); placed:
    // moved by the inset in its points, where BuildOutline gives the geometry that move as its transform instead.
    private static void WriteOutline(StreamGeometryContext path, GlassBoundaryMath.Layout o, bool placed)
    {
        var sink = new StreamSink(path);
        GlassBoundaryMath.WriteOutline(o, placed, ref sink);
    }

    private readonly struct StreamSink(StreamGeometryContext path) : GlassBoundaryMath.IOutlineSink
    {
        public void Begin(double x, double y) => path.BeginFigure(new(x, y), true, true);
        public void Line(double x, double y) => path.LineTo(new(x, y), true, false);
        public void Curve(double x1, double y1, double x2, double y2, double x3, double y3)
            => path.BezierTo(new(x1, y1), new(x2, y2), new(x3, y3), true, true);
    }
}

// Liquid glass's optical edge (GlassBoundaryMath.Bands): up to MaxBands rings a step (one device pixel) wide inward from the outline,
// ring k drawn with Brushes[k]. The brushes are the light's gradient across the island at each ring's strength; they stay mutable,
// so the light's direction, the opening and the palette move them in place on any frame without a new brush. IslandBorder draws
// anew only for new rings (Shape returns true); a new height is shaped for (Fit) as it is drawn.
internal sealed class GlassEdge
{
    public const int MaxBands = 8;
    public readonly LinearGradientBrush[] Brushes = new LinearGradientBrush[MaxBands];
    private readonly GradientStopCollection _stops = [];
    private readonly double[] _alphas = new double[MaxBands];
    public int Count { get; private set; }
    public double Step { get; private set; }
    public double Width { get; private set; }
    // Shapes the edge (Shape) for the island's height as it is drawn; MainWindow.Glass sets it, as the bevel's width follows the
    // opening shown. IslandBorder calls it as it draws the rings, so a frame's new size never has to be drawn again for its rings.
    internal Action<double>? Fit { get; set; }

    public GlassEdge()
    {
        for (int k = 0; k < MaxBands; k++) Brushes[k] = new LinearGradientBrush { GradientStops = _stops, Opacity = 0 };
    }

    // widthDip: the bevel's width; step: one device pixel in DIP; strength scales the whole edge (RimStrength). True when the
    // rings changed (the island has to be drawn anew); the strengths alone change in place.
    public bool Shape(double widthDip, double step, double peak, double baseline, double strength)
    {
        if (!(widthDip > 0) || !(step > 0)) widthDip = 0;
        int count = widthDip > 0 ? GlassBoundaryMath.Bands(widthDip / step, peak, baseline, _alphas) : 0;
        // The width is kept to a hundredth of a step, so a light or palette frame at rest never redraws the rings.
        double width = Math.Min(Math.Round(widthDip / step * 100) / 100, count) * step;
        bool changed = count != Count || step != Step || Math.Abs(width - Width) > 1e-9;
        Count = count; Step = step; Width = width;
        for (int k = 0; k < MaxBands; k++)
        {
            double opacity = k < count ? Math.Clamp(_alphas[k] * strength, 0, 1) : 0;
            if (Brushes[k].Opacity != opacity) Brushes[k].Opacity = opacity;
        }
        return changed;
    }

    // The light's shape around the island: the theme's rim gradient, each stop's alpha relative to the brightest (the edge's
    // strength is the profile's, not the theme's).
    public void Stops(IReadOnlyList<Color> colors)
    {
        double brightest = 0;
        foreach (var c in colors) brightest = Math.Max(brightest, c.A);
        if (colors.Count < 2 || brightest <= 0) { colors = [Colors.White, Colors.White]; brightest = 255; }
        if (_stops.Count != colors.Count)
        {
            _stops.Clear();
            for (int i = 0; i < colors.Count; i++) _stops.Add(new GradientStop(Colors.Transparent, (double)i / (colors.Count - 1)));
        }
        for (int i = 0; i < colors.Count; i++)
        {
            var c = colors[i];
            var color = Color.FromArgb((byte)Math.Round(c.A / brightest * 255), c.R, c.G, c.B);
            if (_stops[i].Color != color) _stops[i].Color = color;
        }
    }

    public void Light((double X, double Y) start, (double X, double Y) end)
    {
        Point a = new(start.X, start.Y), b = new(end.X, end.Y);
        foreach (var brush in Brushes)
        {
            if (brush.StartPoint != a) brush.StartPoint = a;
            if (brush.EndPoint != b) brush.EndPoint = b;
        }
    }
}
