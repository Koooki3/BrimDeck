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
    public static readonly DependencyProperty RimWidthProperty = DependencyProperty.Register(
        nameof(RimWidth), typeof(double), typeof(IslandBorder), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((IslandBorder)d)._rim = null));

    private Pen? _rim;
    private (Size Size, double Shoulder, CornerRadius Corners)? _outlineKey;
    private Geometry _outline = Geometry.Empty;

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
        if (RimWidth > 0)
        {
            if (_rim is null) { _rim = new Pen(RimBrush, RimWidth * 2); _rim.Freeze(); }
            drawingContext.DrawGeometry(null, _rim, outline);
        }
        if (fading) drawingContext.Pop();
    }

    // The visible surface owns input; its decorative shadow is a separate, non-interactive sibling.
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => Outline();

    private static Brush CreateRim()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        brush.Freeze();
        return brush;
    }

    private Geometry Outline()
    {
        var key = (RenderSize, ShoulderRadius, CornerRadius);
        if (_outlineKey == key) return _outline;
        _outlineKey = key;
        double w = RenderSize.Width, h = RenderSize.Height;
        if (w <= 0 || h <= 0) return _outline = Geometry.Empty;
        double shoulder = Math.Clamp(ShoulderRadius, 0, Math.Min(w / 4, h / 2));
        double tl = Math.Clamp(CornerRadius.TopLeft, 0, Math.Min(w, h) / 2);
        double tr = Math.Clamp(CornerRadius.TopRight, 0, Math.Min(w, h) / 2);
        // A shoulder curves out into the screen edge and a top corner curves in, so they cannot share a corner. While one shape
        // turns into the other, each gives way to the other by its size: the outline passes through a square corner instead
        // of jumping from one curve to the other when the shoulder reaches zero.
        (shoulder, tl, tr) = (Math.Max(0, shoulder - Math.Max(tl, tr)), Math.Max(0, tl - shoulder), Math.Max(0, tr - shoulder));
        double shoulderDepth = shoulder * (7d / 16);
        double left = shoulder, right = w - shoulder;
        double limit = Math.Min(shoulder > 0 ? h - shoulderDepth : h / 2, (right - left) / 2);
        double bl = Math.Clamp(CornerRadius.BottomLeft, 0, limit);
        double br = Math.Clamp(CornerRadius.BottomRight, 0, limit);
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
        geometry.Freeze();
        return _outline = geometry;
    }
}
