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
    // Cut corners (a theme's "cut" effect): the outline's curves become 45-degree cuts of at most this many DIP, a notch's shoulders
    // straight slants. 0 keeps the curves. Not with Pixel, whose steps win.
    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(
        nameof(Cut), typeof(double), typeof(IslandBorder), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty BevelBrushProperty = DependencyProperty.Register(
        nameof(BevelBrush), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    // Liquid glass's inner optical edge: a soft glow inside the outline, brightest where the rim catches the light, fading inward
    // over BevelGlowWidth (MainWindow.Glass sets both; null elsewhere).
    public static readonly DependencyProperty BevelGlowProperty = DependencyProperty.Register(
        nameof(BevelGlow), typeof(Brush), typeof(IslandBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((IslandBorder)d)._glowPens = null));
    public static readonly DependencyProperty BevelGlowWidthProperty = DependencyProperty.Register(
        nameof(BevelGlowWidth), typeof(double), typeof(IslandBorder), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((IslandBorder)d)._glowPens = null));
    public Brush? BevelGlow { get => (Brush?)GetValue(BevelGlowProperty); set => SetValue(BevelGlowProperty, value); }
    public double BevelGlowWidth { get => (double)GetValue(BevelGlowWidthProperty); set => SetValue(BevelGlowWidthProperty, value); }
    private Pen[]? _glowPens;
    // Windows sends a click on this layered window to whatever lies below wherever its pixels are fully transparent. Under
    // liquid glass the surface steps aside (opacity 0) and the glass is drawn by a window of its own beneath (GlassSurface), so
    // without this the open panel took clicks only on its text and glyphs: elsewhere they fell through to the glass window or
    // to the application under the panel. The faintest fill under the surface keeps the whole outline the panel's own, as an
    // opaque surface does; at 1/255 black it changes no visible pixel.
    private static readonly Brush InputCatch = CreateCatch();
    private static Brush CreateCatch() { var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); brush.Freeze(); return brush; }

    private Pen? _rim;
    private (Size Size, double Shoulder, CornerRadius Corners, double Inset, double Step, double Rim, double Cut)? _outlineKey;
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
    public double Cut { get => (double)GetValue(CutProperty); set => SetValue(CutProperty, value); }
    public Brush? BevelBrush { get => (Brush?)GetValue(BevelBrushProperty); set => SetValue(BevelBrushProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var outline = Outline();
        double material = Math.Clamp(MaterialOpacity, 0, 1), surface = Math.Clamp(SurfaceOpacity, 0, 1);
        // The faintest fill under the surface keeps the whole outline the panel's own for input (see InputCatch), whatever the surface shows.
        drawingContext.DrawGeometry(InputCatch, null, outline);
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
        drawingContext.DrawGeometry(Background, null, outline);
        if (material > 0)
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
        Decor?.Draw(drawingContext, RenderSize, CornerRadius, ShoulderRadius, outline);
        if (Pattern is { } pattern) drawingContext.DrawGeometry(pattern, null, outline);
    }

    private void Rim(DrawingContext drawingContext, Geometry outline)
    {
        Glow(drawingContext, outline);
        if (RimWidth <= 0) return;
        if (_bevel is { } bevel && BevelBrush is { } shade)
        {
            drawingContext.DrawGeometry(RimBrush, null, bevel.Light);
            drawingContext.DrawGeometry(shade, null, bevel.Shade);
        }
        else
        {
            // A rim brush the ambient light animates (AmbientGlow) stays unfrozen, and so does its pen: freezing the pen would
            // freeze the brush. Its changes then reach the screen without this method running again.
            // The pen is twice the rim and the layout clip takes its outer half; at rest the rim is that inner half as a ring.
            if (_settledClip) drawingContext.DrawGeometry(RimBrush, null, Ring(outline));
            else
            {
                if (_rim is null) { _rim = new Pen(RimBrush, RimWidth * 2); if (RimBrush.IsFrozen) _rim.Freeze(); }
                drawingContext.DrawGeometry(null, _rim, outline);
            }
        }
    }

    // The inner glow as three nested strokes along the outline (only their inner halves show, inside the outline): together a falloff
    // from the edge inward, without making a widened geometry on an animation's frames.
    private void Glow(DrawingContext drawingContext, Geometry outline)
    {
        if (BevelGlow is not { } glow || BevelGlowWidth <= 0 || Pixel > 0) return;
        if (_glowPens is null)
        {
            _glowPens = new Pen[3];
            for (int i = 0; i < 3; i++) { _glowPens[i] = new Pen(glow, 2 * BevelGlowWidth * (3 - i) / 3); _glowPens[i].Freeze(); }
        }
        // At rest the layout clip is the bounds (SettledClip), so the strokes' outer halves are clipped here.
        drawingContext.PushClip(outline);
        foreach (var pen in _glowPens) drawingContext.DrawGeometry(null, pen, outline);
        drawingContext.Pop();
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
        var key = (RenderSize, ShoulderRadius, CornerRadius, Inset, step, RimWidth, step > 0 ? 0 : Cut);
        if (_outlineKey == key) return _outline;
        _outlineKey = key;
        // Stepped, the inset (the shadow caster's) is whole device pixels too, so its steps stay on them at 125 % or 175 %.
        double inset = step > 0 ? Math.Round(Inset * dpi, MidpointRounding.AwayFromZero) / dpi : Inset;
        (_outline, _bevel) = Shape(RenderSize.Width, RenderSize.Height, ShoulderRadius, CornerRadius, inset, step, RimWidth, dpi, Cut);
        return _outline;
    }

    // A surface drawn as the island (the island, a notice): its outline, stepped for a pixel theme, and then its bevel, a whole
    // number of device pixels thick and never thinner than one.
    internal static (Geometry Outline, (Geometry Light, Geometry Shade)? Bevel) Shape(double w, double h, double shoulder, CornerRadius corners, double inset, double step, double rimWidth, double dpi, double cut = 0)
    {
        if (step <= 0) return (cut > 0 ? BuildCut(w, h, shoulder, corners, inset, cut) : BuildOutline(w, h, shoulder, corners, inset), null);
        var stairs = BuildStairs(w, h, shoulder, corners, inset, step);
        return (Polygon(stairs), rimWidth > 0 && stairs.Count >= 3 ? Bevel(stairs, PixelStairs.Step(rimWidth, dpi)) : null);
    }

    // The smooth outline flattened into straight edges, turned into steps (PixelStairs.Build).
    internal static IReadOnlyList<Point> BuildStairs(double w, double h, double shoulderRadius, CornerRadius corners, double insetBy, double step)
    {
        var smooth = BuildOutline(w, h, shoulderRadius, corners, insetBy);
        if (smooth.IsEmpty() || !(step > 0)) return [];
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
    internal static (Geometry Light, Geometry Shade) Bevel(IReadOnlyList<Point> points, double thickness)
    {
        var light = new StreamGeometry { FillRule = FillRule.EvenOdd };
        var shade = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var lit = light.Open())
        using (var shaded = shade.Open())
        {
            for (int i = 0; i < points.Count; i++)
            {
                Point a = points[i], b = points[(i + 1) % points.Count];
                if (Math.Abs(a.Y - b.Y) < 1e-9)
                {
                    if (a.X < b.X) Rectangle(lit, a.X, a.Y, b.X, a.Y + thickness);
                    else Rectangle(shaded, b.X, a.Y - thickness, a.X, a.Y);
                }
                else if (a.Y < b.Y) Rectangle(shaded, a.X - thickness, a.Y, a.X, b.Y);
                else Rectangle(lit, a.X, b.Y, a.X + thickness, a.Y);
            }
        }
        light.Freeze(); shade.Freeze();
        return (light, shade);

        static void Rectangle(StreamGeometryContext context, double left, double top, double right, double bottom)
        {
            context.BeginFigure(new Point(left, top), isFilled: true, isClosed: true);
            context.LineTo(new Point(right, top), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(right, bottom), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(left, bottom), isStroked: false, isSmoothJoin: false);
        }
    }

    // The cut outline: the island's shape with every curve replaced by a straight line. A corner of radius r is cut off by a 45-degree
    // line, each leg min(cut, r) long, and a notch's shoulder is the straight slant between its ends. The measures are those of
    // BuildOutline (shoulders on the full bounds, corners losing the inset from their radii), so the cut shape lies where the curved
    // one would and the content keeps clear of it alike. A leg shortens by (2 - sqrt 2) of the inset, as an offset diagonal does.
    // A capsule (every corner as round as its height allows) is a pill and keeps its round ends, as the game's resource readouts
    // are pills; the corners turn to cuts as they fall short of half the height, over PillRun DIP, so a capsule that opens into a
    // panel changes its shape continuously: each point of a corner is blended between the arc and the cut.
    internal const double PillRun = 8;
    internal static Geometry BuildCut(double w, double h, double shoulderRadius, CornerRadius corners, double insetBy, double cut)
    {
        if (!(w > 0) || !(h > 0)) return Geometry.Empty;
        double shoulder = Math.Clamp(shoulderRadius, 0, Math.Min(w / 4, h / 2));
        double shoulderDepth = shoulder * (7d / 16);
        double inset = Math.Clamp(insetBy, 0, Math.Min(w, h) / 4), top = shoulder > 0 ? 0 : inset;
        double shortfall = shoulder > 0 ? double.PositiveInfinity : h / 2 - Math.Min(Math.Min(corners.TopLeft, corners.TopRight), Math.Min(corners.BottomLeft, corners.BottomRight));
        double cutting = Math.Clamp((shortfall - .5) / PillRun, 0, 1);
        cutting = cutting * cutting * (3 - 2 * cutting);
        w -= 2 * inset; h -= top + inset;
        double left = shoulder, right = w - shoulder;
        double limit = Math.Max(0, Math.Min(shoulder > 0 ? h - shoulderDepth : h / 2, (right - left) / 2));
        double bl = Math.Clamp(corners.BottomLeft - inset, 0, limit), br = Math.Clamp(corners.BottomRight - inset, 0, limit);
        double tl = Math.Clamp(corners.TopLeft - inset, 0, Math.Min(w, h) / 2), tr = Math.Clamp(corners.TopRight - inset, 0, Math.Min(w, h) / 2);
        double legLoss = inset * (2 - Math.Sqrt(2));
        var points = new List<Point>(40);
        // One corner, from the end of the horizontal edge (u 0) to the end of the vertical one (u 1) or back: x and y measured inward
        // from the corner (ex, ey: which way is inward), the arc blended with the cut by `cutting`. A square corner is one point.
        void Corner(Point corner, double ex, double ey, double radius, bool fromHorizontal)
        {
            if (radius <= 0) { points.Add(corner); return; }
            double leg = Math.Max(0, Math.Min(cut - legLoss, radius));
            int n = cutting >= 1 ? 1 : 12;
            for (int i = 0; i <= n; i++)
            {
                double u = fromHorizontal ? (double)i / n : 1 - (double)i / n, angle = u * Math.PI / 2;
                double x = radius - radius * Math.Sin(angle), y = radius - radius * Math.Cos(angle);
                x += (leg * (1 - u) - x) * cutting; y += (leg * u - y) * cutting;
                points.Add(new Point(corner.X + ex * x, corner.Y + ey * y));
            }
        }
        if (shoulder > 0) { points.Add(new Point(0, 0)); points.Add(new Point(w, 0)); points.Add(new Point(right, shoulderDepth)); }
        else
        {
            // Clockwise from the left edge's top: the top left corner (vertical edge to horizontal), then the top right (horizontal to vertical).
            Corner(new Point(0, 0), 1, 1, tl, fromHorizontal: false);
            Corner(new Point(w, 0), -1, 1, tr, fromHorizontal: true);
        }
        Corner(new Point(right, h), -1, -1, br, fromHorizontal: false);
        Corner(new Point(left, h), 1, -1, bl, fromHorizontal: true);
        if (shoulder > 0) points.Add(new Point(left, shoulderDepth));
        if (inset > 0) for (int i = 0; i < points.Count; i++) points[i] = new Point(points[i].X + inset, points[i].Y + top);
        return Polygon(points);
    }

    // The island's smooth outline for a size, shoulder and corners; insetBy draws it that far inside the bounds (Inset). Dynamic
    // notices draw the same outline (IslandNotice), so that they start and end exactly on the island. Frozen.
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
        // With shoulders the bottom corners also stay within half the height, so the limit does not jump when a shoulder
        // shrinking to nothing on a low surface (a notch becoming the line) ends; a notch at rest never reaches either bound.
        double limit = Math.Max(0, Math.Min(shoulder > 0 ? Math.Min(h - shoulderDepth, h / 2) : h / 2, (right - left) / 2));
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
