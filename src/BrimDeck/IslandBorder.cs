using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrimDeck;

public sealed class IslandBorder : Border
{
    public static readonly DependencyProperty ShoulderRadiusProperty = DependencyProperty.Register(
        nameof(ShoulderRadius), typeof(double), typeof(IslandBorder),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty MaterialOpacityProperty = DependencyProperty.Register(
        nameof(MaterialOpacity), typeof(double), typeof(IslandBorder),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    // The surface stays pure black. A faint inner rim keeps the outline readable over dark wallpapers;
    // the pen is twice the rim width because the layout clip removes the outer half of the stroke.
    private static readonly Pen Rim = CreateRim();
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

    protected override void OnRender(DrawingContext drawingContext)
    {
        var outline = Outline();
        drawingContext.DrawGeometry(Background, null, outline);
        if (MaterialOpacity <= 0) return;
        drawingContext.PushOpacity(Math.Clamp(MaterialOpacity, 0, 1));
        drawingContext.DrawGeometry(null, Rim, outline);
        drawingContext.Pop();
    }

    // The visible surface owns input; its decorative shadow is a separate, non-interactive sibling.
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => Outline();

    private static Pen CreateRim()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)), 2);
        pen.Freeze();
        return pen;
    }

    private Geometry Outline()
    {
        var key = (RenderSize, ShoulderRadius, CornerRadius);
        if (_outlineKey == key) return _outline;
        _outlineKey = key;
        return _outline = BuildOutline(RenderSize.Width, RenderSize.Height, ShoulderRadius, CornerRadius);
    }

    // The island's outline for a size, shoulder and corners; dynamic notices draw the same outline, so that they start and end
    // exactly on the island. Frozen.
    internal static Geometry BuildOutline(double w, double h, double shoulderRadius, CornerRadius corners)
    {
        if (!(w > 0) || !(h > 0)) return Geometry.Empty;
        double shoulder = Math.Clamp(shoulderRadius, 0, Math.Min(w / 4, h / 2));
        double shoulderDepth = shoulder * (7d / 16);
        double left = shoulder, right = w - shoulder;
        double limit = Math.Min(shoulder > 0 ? h - shoulderDepth : h / 2, (right - left) / 2);
        double bl = Math.Clamp(corners.BottomLeft, 0, limit);
        double br = Math.Clamp(corners.BottomRight, 0, limit);
        double tl = Math.Clamp(corners.TopLeft, 0, Math.Min(w, h) / 2);
        double tr = Math.Clamp(corners.TopRight, 0, Math.Min(w, h) / 2);
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
        return geometry;
    }
}
