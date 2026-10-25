using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// How each theme draws its answers (ThemeMotion). Every mark lies on the
// control's visible shape (FxMark.Box and Radius), on the layer above the content:
//   reveal / shade (dark, system): a faint accent fill and an edge lit from the top; a press shades the shape and firms its edge.
//   lift / sink (light): a soft shadow outside the shape and a light top edge; a press collapses the shadow and shades the shape.
//   underline / indicator (GitHub): a subtle neutral fill and a 2 px bar along the bottom; a press turns the bar the highlight.
//   caret / select (One Dark Pro): a line highlight and a caret bar at the left; a press sweeps a selection out behind the caret.
//   brackets / lock (cyberpunk): HUD corner brackets that snap on in hard steps; a press clamps them onto the edge with a
//     magenta ghost, a yellow corner and a glitch slice of the control itself, and a completed press ticks a scan line down it.
//   sheen / sparkle (Dracula): a purple-to-pink tint and fine gradient edge; a press deepens it, then stars twinkle (particles).
//   glow / ember (Night Owl): a warm glow inside the shape and a glint at its corner; a press swells it, then fireflies rise.
//   lens / jelly (liquid glass): light gathered at the top of the shape as on a glass bead, and a rim bright at the top; a press
//     squashes that light wider and lower (1.05 x 0.93), shades only the bottom edge, and springs back once on release
//     (1 - e^-5t cos 9t, after xiaofangstudio/liquid-glass-web, MIT).
//   wave (island holiday): a faint tint and a gentle wave that rolls in along the bottom and settles.
//   slot / bevel (pixel blocks): a square highlight with a solid inner edge, as an inventory slot; a press turns the block's
//     bevel over, shaded at the top left and lit at the bottom right, as a pixel-art button pushed in.
// Brushes and pens are made when the theme dresses the layer and frozen; strengths are applied as opacity, so a frame only
// draws.
internal sealed partial class FxSurface
{
    private Brush _main = Brushes.Transparent, _second = Brushes.Transparent, _high = Brushes.Transparent;
    private Brush _liftBrush = Brushes.Transparent, _revealEdgeBrush = Brushes.Transparent, _sheen = Brushes.Transparent, _glow = Brushes.Transparent, _ember = Brushes.Transparent, _glint = Brushes.Transparent;
    private Pen _liftLine = new(), _mainLine = new(), _mainBold = new(), _secondLine = new(), _secondBold = new(), _highLine = new(), _highBold = new(), _revealEdge = new(), _sheenEdge = new(), _lightLine = new();
    private Brush _lens = Brushes.Transparent, _lensEdgeBrush = Brushes.Transparent, _shadeBrush = Brushes.Transparent;
    private Pen _lensEdge = new(), _shadeBold = new();
    private double _line = 1, _bold = 1.5, _dressedDpi;
    private bool _darkSurface;
    // The glitch slice: the pressed control itself, a thin band of it drawn a few pixels aside for a moment.
    private readonly VisualBrush _slice = new() { ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
    private Rect _sliceBand = Rect.Empty;

    private void Dress(PanelPalette palette, bool system)
    {
        var accent = _motion.Color == ThemeMotion.SystemAccent || _motion.Secondary == ThemeMotion.SystemAccent || _motion.Highlight == ThemeMotion.SystemAccent ? WindowsAccent() : null;
        Color main = PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, _motion.Color, accent));
        Color second = PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, _motion.Secondary, accent));
        Color high = PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, _motion.Highlight, accent));
        _darkSurface = palette.IsDark;
        _main = Solid(main); _second = Solid(second); _high = Solid(high);
        _revealEdgeBrush = Vertical(main, 1, .3);
        _sheen = Diagonal(main, second);
        _liftBrush = Vertical(second, 0, 1);
        _glow = Radial(second, new Point(.5, 1), .75, 1.25);
        _ember = Radial(second, new Point(.5, .5), .65, .9);
        _glint = Radial(second, new Point(.5, .5), .5, .5);
        // A glass bead: light pooled at the top centre, the rim brightest at the top and faint at the bottom.
        _lens = Radial(main, new Point(.5, 0), .75, 1.1);
        _lensEdgeBrush = Vertical(main, 1, .15);
        // The shaded side of a pixel bevel.
        _shadeBrush = Solid(Alpha(Colors.Black, _darkSurface ? .85 : .55));
        DressPens();
        DressParticles(main, second, high);
    }

    // Lines on whole device pixels: a 1 px line is one pixel at any scale, and the brackets' 1.5 px is two at 100 percent.
    private void DressPens()
    {
        double scale = Math.Max(1, Dpi.DpiScaleX);
        _dressedDpi = Dpi.DpiScaleX;
        _line = Math.Max(1, Math.Round(scale)) / scale;
        _bold = Math.Max(2, Math.Round(1.5 * scale)) / scale;
        _mainLine = Pen(_main, _line); _mainBold = Pen(_main, _bold, PenLineCap.Square);
        _secondLine = Pen(_second, _line); _secondBold = Pen(_second, _bold, PenLineCap.Square);
        _highLine = Pen(_high, _line); _highBold = Pen(_high, _bold, PenLineCap.Square);
        _revealEdge = Pen(_revealEdgeBrush, _line);
        _lensEdge = Pen(_lensEdgeBrush, _line);
        _shadeBold = Pen(_shadeBrush, _bold, PenLineCap.Square);
        _sheenEdge = Pen(_sheen, _line);
        // The light top edge of a raised card (lift), on light themes only.
        _lightLine = Pen(Brushes.White, _line);
        // A shadow falls below: its edge fades out toward the top, so it never reads as an outline.
        _liftLine = Pen(_liftBrush, _line);
        // ui-5: the focus ring's pen is made on first use; a new scale makes it again with the new line width.
        _pen2 = null;
    }

    // The Windows accent color, for the "system" theme; null where Windows does not say.
    private static ThemeColor? WindowsAccent()
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            return new ThemeColor(c.A, c.R, c.G, c.B);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static Color Alpha(Color color, double alpha) => Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), color.R, color.G, color.B);
    private static Brush Solid(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Pen Pen(Brush brush, double thickness, PenLineCap cap = PenLineCap.Flat)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Miter };
        pen.Freeze();
        return pen;
    }
    private static Brush Vertical(Color color, double top, double bottom)
    {
        var brush = new LinearGradientBrush(Alpha(color, top), Alpha(color, bottom), 90);
        brush.Freeze();
        return brush;
    }
    private static Brush Diagonal(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }
    private static Brush Radial(Color color, Point center, double radiusX, double radiusY)
    {
        var brush = new RadialGradientBrush(new GradientStopCollection { new GradientStop(color, 0), new GradientStop(Alpha(color, .45), .45), new GradientStop(Alpha(color, 0), 1) })
        {
            Center = center, GradientOrigin = center, RadiusX = radiusX, RadiusY = radiusY
        };
        brush.Freeze();
        return brush;
    }

    // ----- Timelines -----

    private int Steps => _motion.Steps;

    private double HoverPresence(FxMark mark, double now)
    {
        if (!double.IsNaN(mark.LeftAt)) return LeavingPresence(mark, now);
        if (double.IsNaN(mark.ShownAt)) return 0;
        double t = MotionCurve.Linear(now - mark.ShownAt, _timing.HoverIn / 1000.0);
        return mark.ShownFrom + (1 - mark.ShownFrom) * MotionCurve.Ease(t, Steps);
    }

    private double LeavingPresence(FxMark mark, double now)
    {
        // A mechanical theme drops its mark in half the time.
        double length = _timing.HoverOut / 1000.0 * (Steps > 0 ? .5 : 1);
        return mark.LeftFrom * (1 - MotionCurve.HoverOut(MotionCurve.Linear(now - mark.LeftAt, length), Steps));
    }

    // Where a mark is drawn: on its control, or on the way there from the previous control.
    private Rect Drawn(FxMark mark, double now, out double radius)
    {
        radius = mark.Radius;
        if (double.IsNaN(mark.GlideAt) || mark.GlideFrom.IsEmpty) return mark.Box;
        double t = MotionCurve.Linear(now - mark.GlideAt, _timing.Glide / 1000.0);
        if (t >= 1) { mark.GlideAt = double.NaN; return mark.Box; }
        double e = Steps > 0 ? MotionCurve.Step(t, Steps) : Curves.Follow(t);
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
        var press = _pressed is not null ? _press.At(now, _timing, Steps) : PressFrame.None;
        bool pressedLeaving = false;
        foreach (var mark in _leaving)
        {
            if (mark is not { Valid: true } leaving) continue;
            pressedLeaving |= ReferenceEquals(leaving, _pressed);
            var box = Drawn(leaving, now, out double radius);
            DrawMark(dc, leaving, box, radius, LeavingPresence(leaving, now), ReferenceEquals(leaving, _pressed) ? press : PressFrame.None);
        }
        if (_hover is { Valid: true } hover)
        {
            var box = Drawn(hover, now, out double radius);
            DrawMark(dc, hover, box, radius, HoverPresence(hover, now), ReferenceEquals(hover, _pressed) ? press : PressFrame.None);
        }
        if (_pressed is { Valid: true } pressed && !ReferenceEquals(pressed, _hover) && !pressedLeaving)
            DrawMark(dc, pressed, pressed.Box, pressed.Radius, 0, press);
        if (_focus is { Valid: true } focus) DrawFocus(dc, focus, now);
        DrawParticles(dc, now);
    }

    private void DrawMark(DrawingContext dc, FxMark mark, Rect r, double radius, double h, in PressFrame press)
    {
        if (r.IsEmpty || r.Width < 1 || r.Height < 1) return;
        double k = press.Held, hs = _motion.HoverStrength, ps = _motion.PressStrength;
        bool clip = !mark.Clip.IsEmpty;
        if (clip) dc.PushClip(mark.ClipGeometry);
        dc.PushOpacity(mark.Opacity);
        bool ellipse = mark.Ellipse;
        var hover = _motion.Hover; var style = _motion.Press;
        if (h > 0 || k > 0 || press.Release >= 0)
        {
            switch (hover)
            {
                case HoverFx.Reveal:
                    Fill(dc, _main, r, radius, ellipse, hs * .5 * h);
                    Edge(dc, _revealEdge, r, radius, ellipse, Math.Min(1, hs * 3.2) * h);
                    break;
                case HoverFx.Lift:
                {
                    // The control rises from the surface: a shadow outside its shape, a light top edge; pressed, it is flat again.
                    double lift = h * (style == PressFx.Sink ? 1 - k : 1);
                    if (lift > .001)
                    {
                        for (int i = 1; i <= 3; i++)
                        {
                            var ring = r; ring.Inflate(Px(i), Px(i)); ring.Offset(0, Px(.4 + .5 * i * lift));
                            Edge(dc, _liftLine, ring, radius + i, ellipse, hs * (i == 1 ? 1.6 : i == 2 ? .9 : .4) * lift);
                        }
                        if (!_darkSurface && !ellipse) Line(dc, _lightLine, r.Left + Px(Math.Max(radius, 2)), r.Top + _line / 2, r.Right - Px(Math.Max(radius, 2)), r.Top + _line / 2, .9 * lift);
                    }
                    break;
                }
                case HoverFx.Underline:
                {
                    Fill(dc, _second, r, radius, ellipse, hs * .3 * h);
                    var bar = Bar(r, radius, ellipse, .45 + .55 * h);
                    Box(dc, _second, bar, 1, Math.Min(1, hs * 3.6) * h);
                    break;
                }
                case HoverFx.Caret:
                {
                    Fill(dc, _main, r, radius, ellipse, hs * .45 * h);
                    // Caret pressed with a selection: it travels with the selection's end.
                    double travel = style == PressFx.Select ? k : 0;
                    Box(dc, _main, Caret(r, travel, ellipse), 0, Math.Max(h, k > 0 ? 1 : 0));
                    break;
                }
                case HoverFx.Brackets:
                    Fill(dc, _main, r, radius, ellipse, hs * .22 * h);
                    break;
                case HoverFx.Sheen:
                    Fill(dc, _sheen, r, radius, ellipse, hs * .55 * h);
                    Edge(dc, _sheenEdge, r, radius, ellipse, Math.Min(1, hs * 2.4) * h);
                    break;
                case HoverFx.Glow:
                    Fill(dc, _glow, r, radius, ellipse, Math.Min(1, hs * 1.9) * h);
                    Edge(dc, _mainLine, r, radius, ellipse, hs * 1.1 * h);
                    // A single warm glint at the top corner, as a firefly settled there.
                    double gx = ellipse ? r.Left + r.Width * .82 : r.Right - Math.Max(radius, 3) - 1.5, gy = ellipse ? r.Top + r.Height * .18 : r.Top + 3.5;
                    Dot(dc, _glint, gx, gy, 3.8, h);
                    break;
                case HoverFx.Lens:
                    Fill(dc, _lens, r, radius, ellipse, Math.Min(1, hs * 1.8) * h);
                    Edge(dc, _lensEdge, r, radius, ellipse, Math.Min(1, hs * 4) * h);
                    break;
                case HoverFx.Wave:
                    Fill(dc, _main, r, radius, ellipse, hs * .35 * h);
                    DrawWave(dc, r, radius, ellipse, h, Math.Min(1, hs * 4.5) * h);
                    break;
                case HoverFx.Slot:
                {
                    // Square whatever the control's corners: a slot is a block.
                    Box(dc, _main, r, 0, hs * .6 * h);
                    var inner = r; inner.Inflate(-_bold / 2, -_bold / 2);
                    if (inner.Width > 0 && inner.Height > 0) { dc.PushOpacity(Math.Min(1, hs * 3.5) * h); dc.DrawRectangle(null, _mainBold, inner); dc.Pop(); }
                    break;
                }
            }
            DrawPress(dc, mark, r, radius, ellipse, h, press, ps);
        }
        dc.Pop();
        if (clip) dc.Pop();
    }

    private void DrawPress(DrawingContext dc, FxMark mark, Rect r, double radius, bool ellipse, double h, in PressFrame press, double ps)
    {
        double k = press.Held;
        switch (_motion.Press)
        {
            case PressFx.Shade:
                Fill(dc, _main, r, radius, ellipse, ps * .55 * k);
                Edge(dc, _mainLine, r, radius, ellipse, Math.Min(1, ps * 2.2) * k);
                break;
            case PressFx.Sink:
                // Pushed in: a light shade, and the top edge in shadow (inside the shape, fading down).
                Fill(dc, _second, r, radius, ellipse, ps * .25 * k);
                if (!ellipse)
                {
                    double inset = Px(Math.Max(radius, 3));
                    Line(dc, _secondLine, r.Left + inset, r.Top + _line / 2, r.Right - inset, r.Top + _line / 2, ps * .9 * k);
                    Line(dc, _secondLine, r.Left + inset, r.Top + _line * 1.5, r.Right - inset, r.Top + _line * 1.5, ps * .35 * k);
                }
                break;
            case PressFx.Indicator:
                Fill(dc, _second, r, radius, ellipse, ps * .35 * k);
                Box(dc, _high, Bar(r, radius, ellipse, 1), 1, k);
                break;
            case PressFx.Select:
            {
                // A selection sweeps out from the left edge, as text selected from the caret.
                // A round button has no line to select across: it fills as a whole.
                if (ellipse) Fill(dc, _high, r, radius, true, ps * .9 * k);
                else if (k > .001 && Px(r.Width * k) is var swept and > 0) Box(dc, _high, new Rect(r.X, r.Y, swept, r.Height), Math.Min(radius, swept / 2), ps * .9);
                if (_motion.Hover != HoverFx.Caret) Box(dc, _main, Caret(r, k, ellipse), 0, k > 0 ? 1 : 0);
                break;
            }
            case PressFx.Lock:
                break;
            case PressFx.Sparkle:
                Fill(dc, _sheen, r, radius, ellipse, ps * .6 * k);
                Edge(dc, _sheenEdge, r, radius, ellipse, Math.Min(1, ps * 2.6) * k);
                break;
            case PressFx.Ember:
                Fill(dc, _ember, r, radius, ellipse, Math.Min(1, ps * 1.5) * k);
                break;
            case PressFx.Jelly:
            {
                // Held, the squash follows the press; let go, a damped spring takes it back and once past its shape.
                double squash = press.Release >= 0 ? Math.Exp(-5 * press.Release) * Math.Cos(9 * press.Release) * Math.Max(k, .6) : k;
                if (Math.Abs(squash) < .002 && k <= .001) break;
                double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, w = r.Width * (1 + .05 * squash), hgt = r.Height * (1 - .07 * squash);
                var drop = Grid(new Rect(cx - w / 2, cy - hgt / 2, w, hgt));
                double presence = Math.Min(1, Math.Abs(squash) + k);
                Fill(dc, _lens, drop, radius, ellipse, Math.Min(1, ps * 2.2) * presence);
                Edge(dc, _lensEdge, drop, radius, ellipse, Math.Min(1, ps * 3) * presence);
                // Pressed down: a shade along the bottom only; the top keeps its light.
                if (!ellipse && k > .001)
                {
                    double inset = Px(Math.Max(radius, 3));
                    Line(dc, _secondLine, drop.Left + inset, drop.Bottom - _line / 2, drop.Right - inset, drop.Bottom - _line / 2, ps * 1.4 * k);
                }
                break;
            }
            case PressFx.Bevel:
                if (k > .001)
                {
                    // Pushed in: darker, shaded along the top and left inside edges, lit along the bottom and right ones.
                    Box(dc, _shadeBrush, r, 0, ps * .6 * k);
                    double t = _bold / 2, left = r.Left + t, top = r.Top + t, right = r.Right - t, bottom = r.Bottom - t;
                    Line(dc, _shadeBold, left, top, right, top, k); Line(dc, _shadeBold, left, top, left, bottom, k);
                    Line(dc, _mainBold, left, bottom, right, bottom, Math.Min(1, ps * 2.4) * k); Line(dc, _mainBold, right, top, right, bottom, Math.Min(1, ps * 2.4) * k);
                }
                break;
        }
        // The brackets belong to both the hover and the lock: one set, which snaps on with the hover, clamps with the press and
        // kicks out as a completed press lets go.
        if (_motion.Hover == HoverFx.Brackets || _motion.Press == PressFx.Lock) DrawBrackets(dc, mark, r, h, press);
    }

    private void DrawBrackets(DrawingContext dc, FxMark mark, Rect r, double h, in PressFrame press)
    {
        bool lockStyle = _motion.Press == PressFx.Lock;
        double k = lockStyle ? press.Held : 0;
        bool committed = lockStyle && _press.Committed && ReferenceEquals(mark, _pressed) && press.Release >= 0;
        double kick = committed ? 3 * MotionCurve.Step(1 - press.Release, Math.Max(2, Steps)) * (press.Release < .5 ? 1 : 0) : 0;
        double presence = _motion.Hover == HoverFx.Brackets ? Math.Max(h, k) : k;
        if (presence <= .001 && kick <= 0) return;
        double outset = 1 + 4 * (1 - h) * (1 - k) - 1.5 * k + kick;
        double arm = Px(Math.Clamp(Math.Min(r.Width, r.Height) * .3, 4, 9) + 2 * k);
        if (k > .001)
        {
            // The chromatic ghost: the same brackets in the second color, a pixel or two aside.
            Brackets(dc, _secondBold, r, outset, arm, -1.5 * k, 1 * k, .8 * k);
        }
        Brackets(dc, _mainBold, r, outset, arm, 0, 0, Math.Max(presence, kick > 0 ? 1 : 0));
        if (k >= .5)
        {
            // Locked: the top left corner turns the highlight.
            double left = Snap(r.Left - outset, _bold), top = Snap(r.Top - outset, _bold);
            Line(dc, _highBold, left, top, left + arm, top, 1);
            Line(dc, _highBold, left, top, left, top + arm, 1);
        }
        if (press.GlitchOn && ReferenceEquals(mark, _pressed) && !_sliceBand.IsEmpty) DrawGlitch(dc, r);
        if (press.ScanAt >= 0 && ReferenceEquals(mark, _pressed))
        {
            double y = Snap(r.Top + 1 + (r.Height - 2) * press.ScanAt, _line);
            Line(dc, _highLine, r.Left, y, r.Right, y, .95);
            Line(dc, _mainLine, r.Left, Snap(y - 2, _line), r.Right, Snap(y - 2, _line), .35);
        }
    }

    // A gentle wave along the inside of the bottom edge, clear of the corners: it rolls in from the left as the hover arrives
    // (its phase runs out with the presence) and then lies still. A round shape's wave sits across its lower third.
    private void DrawWave(DrawingContext dc, Rect r, double radius, bool ellipse, double presence, double alpha)
    {
        if (alpha <= .001 || r.Width < 12 || r.Height < 8) return;
        double inset = ellipse ? r.Width * .22 : Math.Max(radius, 4), left = r.Left + inset, right = r.Right - inset;
        double y = ellipse ? r.Top + r.Height * .68 : r.Bottom - Math.Max(3, Math.Min(5, r.Height * .2));
        double wavelength = Math.Clamp((right - left) / 3, 6, 14), amplitude = Math.Min(1.4, r.Height * .06), phase = (1 - presence) * Math.PI * 2;
        var wave = new StreamGeometry();
        using (var path = wave.Open())
        {
            path.BeginFigure(new Point(left, y + amplitude * Math.Sin(phase)), false, false);
            for (double x = left + 1; x <= right; x += 1) path.LineTo(new Point(x, y + amplitude * Math.Sin(phase + (x - left) / wavelength * Math.PI * 2)), true, true);
        }
        wave.Freeze();
        dc.PushOpacity(alpha);
        dc.DrawGeometry(null, _mainLine, wave);
        dc.Pop();
    }

    // Four L-shaped corners around the shape, `outset` outside it, each arm `arm` long.
    private void Brackets(DrawingContext dc, Pen pen, Rect r, double outset, double arm, double dx, double dy, double alpha)
    {
        if (alpha <= .001) return;
        double w = pen.Thickness;
        double left = Snap(r.Left - outset + dx, w), right = Snap(r.Right + outset + dx, w), top = Snap(r.Top - outset + dy, w), bottom = Snap(r.Bottom + outset + dy, w);
        // Whole device pixels long, so the arms' square ends lie on pixel edges too.
        arm = Px(Math.Min(arm, Math.Min(right - left, bottom - top) / 2 - 1));
        dc.PushOpacity(alpha);
        dc.DrawLine(pen, new Point(left, top), new Point(left + arm, top)); dc.DrawLine(pen, new Point(left, top), new Point(left, top + arm));
        dc.DrawLine(pen, new Point(right, top), new Point(right - arm, top)); dc.DrawLine(pen, new Point(right, top), new Point(right, top + arm));
        dc.DrawLine(pen, new Point(left, bottom), new Point(left + arm, bottom)); dc.DrawLine(pen, new Point(left, bottom), new Point(left, bottom - arm));
        dc.DrawLine(pen, new Point(right, bottom), new Point(right - arm, bottom)); dc.DrawLine(pen, new Point(right, bottom), new Point(right, bottom - arm));
        dc.Pop();
    }

    // The glitch: a thin band of the control, drawn three pixels aside, edged in the two neon colors a pixel or two apart.
    private void PrepareGlitch(FxMark mark)
    {
        var box = mark.Box;
        if (box.IsEmpty || box.Height < 6) { _sliceBand = Rect.Empty; return; }
        double band = Px(Math.Clamp(Math.Round(box.Height * .22), 3, 6));
        // A different place on each press, but the same for the same press: a preview is repeatable.
        double place = .28 + .44 * ((mark.Serial * 0.6180339887) % 1);
        _sliceBand = new Rect(0, Px(box.Height * place - band / 2), box.Width, band);
        // The band in the control's own coordinates, through the same transform the mark was placed with.
        try
        {
            var toControl = TransformToVisual(mark.Control);
            var band0 = toControl.TransformBounds(new Rect(box.X, box.Y + _sliceBand.Y, box.Width, band));
            _slice.Visual = mark.Control;
            _slice.Viewbox = band0;
        }
        catch (InvalidOperationException) { _sliceBand = Rect.Empty; }
    }

    private void DrawGlitch(DrawingContext dc, Rect r)
    {
        var band = new Rect(r.X + Px(3), r.Y + _sliceBand.Y, r.Width, _sliceBand.Height);
        if (_slice.Viewport != band) _slice.Viewport = band;
        dc.DrawRectangle(_slice, null, band);
        Line(dc, _mainLine, r.X - Px(2), Snap(band.Top, _line), r.Right - Px(2), Snap(band.Top, _line), .9);
        Line(dc, _secondLine, r.X + Px(4), Snap(band.Bottom, _line), r.Right + Px(4), Snap(band.Bottom, _line), .9);
    }

    // A short accent where keyboard focus lands: it rises quickly, then fades, over the control's own focus outline.
    private void DrawFocus(DrawingContext dc, FxMark mark, double now)
    {
        double t = MotionCurve.Linear(now - _focusAt, _timing.Focus / 1000.0);
        double a = t < .25 ? MotionCurve.Ease(t / .25, Steps) : 1 - MotionCurve.EaseOut((t - .25) / .75, Steps);
        if (a <= .001) return;
        var r = mark.Box; double radius = mark.Radius;
        bool clip = !mark.Clip.IsEmpty;
        if (clip) dc.PushClip(mark.ClipGeometry);
        dc.PushOpacity(mark.Opacity);
        switch (_motion.Focus)
        {
            case FocusFx.Ring:
            {
                var ring = r; ring.Inflate(Px(2), Px(2));
                Edge(dc, Pen2, ring, radius + 2, mark.Ellipse, .8 * a);
                break;
            }
            case FocusFx.Glow:
                for (int i = 1; i <= 3; i++)
                {
                    var ring = r; ring.Inflate(Px(i), Px(i));
                    Edge(dc, _mainLine, ring, radius + i, mark.Ellipse, (i == 1 ? .5 : i == 2 ? .25 : .1) * a);
                }
                break;
            case FocusFx.Brackets:
                Brackets(dc, _mainBold, r, 3 - 2 * MotionCurve.Step(Math.Min(1, t / .3), Math.Max(2, Steps)), Math.Clamp(Math.Min(r.Width, r.Height) * .3, 4, 9), 0, 0, a);
                break;
            case FocusFx.Caret:
                Box(dc, _main, Caret(r, 0, mark.Ellipse), 0, a);
                break;
        }
        dc.Pop();
        if (clip) dc.Pop();
    }

    private Pen? _pen2;
    private Pen Pen2 => _pen2 is { } pen && ReferenceEquals(pen.Brush, _main) ? pen : _pen2 = Pen(_main, 2 * _line);

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

    private static void Box(DrawingContext dc, Brush brush, Rect r, double radius, double alpha)
    {
        if (alpha <= .001 || r.Width <= 0 || r.Height <= 0) return;
        dc.PushOpacity(Math.Min(1, alpha));
        if (radius > 0) dc.DrawRoundedRectangle(brush, null, r, radius, radius); else dc.DrawRectangle(brush, null, r);
        dc.Pop();
    }

    private static void Line(DrawingContext dc, Pen pen, double x1, double y1, double x2, double y2, double alpha)
    {
        if (alpha <= .001) return;
        dc.PushOpacity(Math.Min(1, alpha));
        dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
        dc.Pop();
    }

    private static void Dot(DrawingContext dc, Brush brush, double x, double y, double radius, double alpha)
    {
        if (alpha <= .001) return;
        dc.PushOpacity(Math.Min(1, alpha));
        dc.DrawEllipse(brush, null, new Point(x, y), radius, radius);
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

    // The caret: 2 px wide at the left inside edge, two thirds of the height; `travel` moves it across to the right edge.
    private Rect Caret(Rect r, double travel, bool ellipse = false)
    {
        // Inside a round button the caret stays within the circle, where its chord is tall enough.
        double width = 2 * _line, height = Px(Math.Max(8, r.Height * (ellipse ? .5 : .62)));
        double margin = ellipse ? r.Width * .2 : 3;
        double left = r.X + margin, right = r.Right - margin - width;
        double x = Snap(left + (right - left) * Math.Clamp(travel, 0, 1), width);
        return new Rect(x - width / 2, r.Y + Px((r.Height - height) / 2), width, height);
    }

    private double Snap(double at, double width) => FxShapes.SnapLine(at, width, Math.Max(.5, _dressedDpi));

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
