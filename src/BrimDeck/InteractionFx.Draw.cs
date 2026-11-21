using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// How a theme draws its hover mark (ThemeMotion). Every mark lies on the control's visible shape (FxMark.Box and Radius), on the
// layer above the content:
//   reveal (Fluent): a faint accent fill and an edge lit from the top.
//   lift (light themes): a soft shadow outside the shape and a light top edge.
//   underline (Primer): a subtle fill in the second color and a 2 px bar along the bottom, which widens as the mark arrives.
// Brushes and pens are made when the theme dresses the layer and frozen; strengths are applied as opacity, so a frame only draws.
internal sealed partial class FxSurface
{
    private Brush _main = Brushes.Transparent, _second = Brushes.Transparent, _liftBrush = Brushes.Transparent, _revealEdgeBrush = Brushes.Transparent;
    private Pen _liftLine = new(), _revealEdge = new(), _lightLine = new();
    private double _line = 1, _dressedDpi;
    private bool _darkSurface;

    private void Dress(PanelPalette palette)
    {
        Color main = PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, _motion.Color));
        Color second = PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, _motion.Secondary));
        _darkSurface = palette.IsDark;
        _main = Solid(main); _second = Solid(second);
        _revealEdgeBrush = Vertical(main, 1, .3);
        _liftBrush = Vertical(second, 0, 1);
        DressPens();
    }

    // Lines on whole device pixels: a 1 px line is one pixel at any scale.
    private void DressPens()
    {
        double scale = Math.Max(1, Dpi.DpiScaleX);
        _dressedDpi = Dpi.DpiScaleX;
        _line = Math.Max(1, Math.Round(scale)) / scale;
        _revealEdge = Pen(_revealEdgeBrush, _line);
        // The light top edge of a raised card (lift), on light themes only.
        _lightLine = Pen(Brushes.White, _line);
        // A shadow falls below: its edge fades out toward the top, so it never reads as an outline.
        _liftLine = Pen(_liftBrush, _line);
    }

    private static Color Alpha(Color color, double alpha) => Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), color.R, color.G, color.B);
    private static Brush Solid(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Pen Pen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Miter };
        pen.Freeze();
        return pen;
    }
    private static Brush Vertical(Color color, double top, double bottom)
    {
        var brush = new LinearGradientBrush(Alpha(color, top), Alpha(color, bottom), 90);
        brush.Freeze();
        return brush;
    }

    // ----- Timelines -----

    // Where a mark is drawn: on its control, or on the way there from the previous control.
    private Rect Drawn(FxMark mark, double now, out double radius)
    {
        radius = mark.Radius;
        if (double.IsNaN(mark.GlideAt) || mark.GlideFrom.IsEmpty) return mark.Box;
        double t = MotionCurve.Linear(now - mark.GlideAt, _timing.Glide / 1000.0);
        if (t >= 1) { mark.GlideAt = double.NaN; return mark.Box; }
        double e = Curves.Follow(t);
        var from = mark.GlideFrom;
        radius = mark.GlideFromRadius + (mark.Radius - mark.GlideFromRadius) * e;
        // On the way it stays on device pixels too, so its edges never blur as it moves.
        return Grid(new Rect(from.X + (mark.Box.X - from.X) * e, from.Y + (mark.Box.Y - from.Y) * e,
            Math.Max(0, from.Width + (mark.Box.Width - from.Width) * e), Math.Max(0, from.Height + (mark.Box.Height - from.Height) * e)));
    }

    // ----- Drawing -----

    private void Draw(DrawingContext dc, double now)
    {
        if (Math.Abs(Dpi.DpiScaleX - _dressedDpi) > .001) DressPens();
        foreach (var mark in _leaving)
        {
            if (mark is not { Valid: true } leaving) continue;
            var box = Drawn(leaving, now, out double radius);
            DrawMark(dc, leaving, box, radius, leaving.Timeline.Presence(now, _timing));
        }
        if (_hover is { Valid: true } hover)
        {
            var box = Drawn(hover, now, out double radius);
            DrawMark(dc, hover, box, radius, hover.Timeline.Presence(now, _timing));
        }
    }

    private void DrawMark(DrawingContext dc, FxMark mark, Rect r, double radius, double h)
    {
        if (h <= 0 || r.IsEmpty || r.Width < 1 || r.Height < 1) return;
        double hs = _motion.HoverStrength;
        bool clip = !mark.Clip.IsEmpty;
        if (clip) dc.PushClip(mark.ClipGeometry);
        dc.PushOpacity(mark.Opacity);
        bool ellipse = mark.Ellipse;
        switch (_motion.Hover)
        {
            case HoverFx.Reveal:
                Fill(dc, _main, r, radius, ellipse, hs * .5 * h);
                Edge(dc, _revealEdge, r, radius, ellipse, Math.Min(1, hs * 3.2) * h);
                break;
            case HoverFx.Lift:
                // The control rises from the surface: a shadow outside its shape, a light top edge.
                if (h > .001)
                {
                    for (int i = 1; i <= 3; i++)
                    {
                        var ring = r; ring.Inflate(Px(i), Px(i)); ring.Offset(0, Px(.4 + .5 * i * h));
                        Edge(dc, _liftLine, ring, radius + i, ellipse, hs * (i == 1 ? 1.6 : i == 2 ? .9 : .4) * h);
                    }
                    if (!_darkSurface && !ellipse) Line(dc, _lightLine, r.Left + Px(Math.Max(radius, 2)), r.Top + _line / 2, r.Right - Px(Math.Max(radius, 2)), r.Top + _line / 2, HoverMarkOpacity.LiftHighlight(hs, h));
                }
                break;
            case HoverFx.Underline:
                Fill(dc, _second, r, radius, ellipse, hs * .3 * h);
                Box(dc, _second, Bar(r, radius, ellipse, .45 + .55 * h), Math.Min(1, hs * 3.6) * h);
                break;
        }
        dc.Pop();
        if (clip) dc.Pop();
    }

    // ----- Primitives -----

    private static void Fill(DrawingContext dc, Brush brush, Rect r, double radius, bool ellipse, double alpha)
    {
        if (alpha <= .001) return;
        dc.PushOpacity(Math.Min(1, alpha));
        if (ellipse) dc.DrawEllipse(brush, null, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
        else dc.DrawRoundedRectangle(brush, null, r, radius, radius);
        dc.Pop();
    }

    // An edge just inside the shape.
    private static void Edge(DrawingContext dc, Pen pen, Rect r, double radius, bool ellipse, double alpha)
    {
        if (alpha <= .001) return;
        double inset = pen.Thickness / 2;
        if (r.Width <= pen.Thickness || r.Height <= pen.Thickness) return;
        r.Inflate(-inset, -inset);
        dc.PushOpacity(Math.Min(1, alpha));
        if (ellipse) dc.DrawEllipse(null, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
        else dc.DrawRoundedRectangle(null, pen, r, Math.Max(0, radius - inset), Math.Max(0, radius - inset));
        dc.Pop();
    }

    private static void Box(DrawingContext dc, Brush brush, Rect r, double alpha)
    {
        if (alpha <= .001 || r.Width <= 0 || r.Height <= 0) return;
        dc.PushOpacity(Math.Min(1, alpha));
        dc.DrawRectangle(brush, null, r);
        dc.Pop();
    }

    private static void Line(DrawingContext dc, Pen pen, double x1, double y1, double x2, double y2, double alpha)
    {
        if (alpha <= .001) return;
        dc.PushOpacity(Math.Min(1, alpha));
        dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
        dc.Pop();
    }

    // The bottom bar: 2 px along the inside of the bottom edge, clear of the corners; a round shape's sits just under it.
    private Rect Bar(Rect r, double radius, bool ellipse, double share)
    {
        double height = 2 * _line;
        double inset = Math.Max(radius, 3);
        double full = ellipse ? r.Width * .5 : Math.Max(4, r.Width - 2 * inset);
        double width = Px(full * Math.Clamp(share, 0, 1));
        double y = ellipse ? r.Bottom + Px(1) : r.Bottom - height;
        return new Rect(r.X + Px((r.Width - width) / 2), y, width, height);
    }

    // A length in whole device pixels, and a box with its edges on them: the marks' boxes are snapped (FxMark.Update), so
    // whatever is placed from them by these stays on the pixel grid at 125, 150 or 175 percent as at 100.
    private double Px(double dip) { double scale = Math.Max(.5, _dressedDpi); return FxShapes.Pixel(dip * scale) / scale; }
    private Rect Grid(Rect r)
    {
        double scale = Math.Max(.5, _dressedDpi);
        var snapped = FxShapes.Snap(new DipRect(r.X, r.Y, r.Width, r.Height), scale, scale);
        return new Rect(snapped.X, snapped.Y, snapped.Width, snapped.Height);
    }
}
