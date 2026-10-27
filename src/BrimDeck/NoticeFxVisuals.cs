using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// The drawing of the notice's liquid motion (NoticeLiquid): the droplets, necks and bulges behind the notice's surface, in the
// surface's own fill and rim so that they read as part of it, and the edge glow over it. Both are drawn only while the motion
// plays, from the frame the window's player samples (NoticeWindow.Fx.cs); nothing is kept between notices but the look, which is
// cached per theme, scale and plan.
//
// The capsule's and the notch's motion is the design "胶囊通知动效" that the maintainer MoFeng2223 provided for BrimDeck
// (https://claude.ai/artifact/JU9bvJp5PPNY85vpGgUX6C); the indicator's motion, the adaptation to every kind of notice and the
// look of each theme (colors, edges, the pixel theme's blocks, high contrast, no animations) are ours.
internal sealed class NoticeFxState
{
    public required NoticeLiquid Liquid { get; init; }
    public required NoticeFxLook Look { get; init; }
    public required NoticeFxPlan Plan { get; init; }
    public required LiquidForm Form { get; init; }
    public LiquidFrame Frame { get; } = new();
    // How many seconds of real time one second of the motion takes (NoticeFx.Pace).
    public double Pace { get; init; } = 1;
    // When it began (Eased.Now), and the surface as it stands now in the stage, as the player last read it.
    public double Start;
    public double Left, Top, Width, Height;
}

// How the motion looks on the theme in use: the surface's own color and rim for the droplets, the glow's color for the notice's
// tone and theme, and for a theme drawn in blocks, the step its squares keep.
internal sealed class NoticeFxLook
{
    private readonly Dictionary<(int Kind, int Alpha), Brush> _brushes = [];
    private readonly Pen?[] _rims;
    private readonly Brush[] _fills;
    private NoticeFxLook(Brush[] fills, Pen?[] rims, Color glow, double step, bool glows, bool translucent) { _fills = fills; _rims = rims; Glow = glow; Step = step; Glows = glows; Translucent = translucent; }

    // Whether the surface lets what is behind it show (a theme file may make it so; the glass themes do): its droplets are then
    // filled as one shape, so that where two overlap they are not drawn twice as dense, and nothing is drawn over the surface.
    public bool Translucent { get; }
    // The surface's fill at a place down its height (0 at its top, 1 at its bottom): its color with the theme's gradient over it,
    // which runs from top to bottom (PanelTheme.SurfaceGradient), so a droplet is the color of the surface where it leaves it.
    public Brush Fill(double down) => _fills[(int)Math.Round(Math.Clamp(down, 0, 1) * (_fills.Length - 1))];
    // Whether the theme has a rim.
    public bool HasRim => _rims[0] is not null;
    // The rim at a place along the surface's width (0 at its left end, 1 at its right): twice as thick as the theme's, as the
    // surface's own pen is (the surface clips off the outer half; here it is the part under the surface that is hidden), in the
    // color the theme's rim has there. A gradient rim runs from the surface's left to its right (PanelTheme.RimBrush), so a droplet
    // at an end has that end's color. None when the theme has no rim.
    public Pen? Rim(double along) => _rims[(int)Math.Round(Math.Clamp(along, 0, 1) * (_rims.Length - 1))];
    public Color Glow { get; }
    // The side of a block for the pixel theme, or 0 for a smooth theme.
    public double Step { get; }
    // False where the glow is not drawn (high contrast).
    public bool Glows { get; }

    private static Color Parse(string text) => (Color)ColorConverter.ConvertFromString(text);

    // The color of a gradient at an offset.
    private static Color At(GradientBrush gradient, double offset)
    {
        var stops = gradient.GradientStops.OrderBy(stop => stop.Offset).ToList();
        if (offset <= stops[0].Offset) return stops[0].Color;
        for (int i = 1; i < stops.Count; i++)
        {
            if (offset > stops[i].Offset) continue;
            double span = stops[i].Offset - stops[i - 1].Offset, p = span <= 0 ? 1 : (offset - stops[i - 1].Offset) / span;
            byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * p);
            return Color.FromArgb(Mix(stops[i - 1].Color.A, stops[i].Color.A), Mix(stops[i - 1].Color.R, stops[i].Color.R), Mix(stops[i - 1].Color.G, stops[i].Color.G), Mix(stops[i - 1].Color.B, stops[i].Color.B));
        }
        return stops[^1].Color;
    }

    public static NoticeFxLook Create(NoticeLook look, NoticeFxPlan plan)
    {
        var palette = PanelTheme.Current;
        double step = palette.Effects.Pixel > 0 ? Math.Max(1, palette.Effects.Pixel) : 0;
        var rims = new Pen?[9];
        for (int i = 0; i < rims.Length; i++)
        {
            Color? color = look.RimWidth <= 0 ? null : look.Rim switch
            {
                SolidColorBrush solid => solid.Color,
                GradientBrush { GradientStops.Count: > 0 } gradient => At(gradient, i / (double)(rims.Length - 1)),
                _ => null
            };
            rims[i] = color is { A: > 0 } c ? Frozen(new Pen(Frozen(new SolidColorBrush(c)), look.RimWidth * 2) { LineJoin = PenLineJoin.Round }) : null;
        }
        var fills = new Brush[9];
        for (int i = 0; i < fills.Length; i++)
        {
            var color = look.Surface;
            if (look.Gradient is GradientBrush { GradientStops.Count: > 0 } over)
            {
                var top = At(over, i / (double)(fills.Length - 1));
                double a = top.A / 255d;
                color = Color.FromRgb((byte)Math.Round(color.R * (1 - a) + top.R * a), (byte)Math.Round(color.G * (1 - a) + top.G * a), (byte)Math.Round(color.B * (1 - a) + top.B * a));
            }
            fills[i] = Frozen(new SolidColorBrush(color));
        }
        return new(fills, rims, GlowColor(look, palette, plan.Tone), step, !look.HighContrast, look.Surface.A < 255);
    }

    // The glow of the theme in the notice's tone. The design's own color for a dark theme; the accent where a theme glows or is
    // drawn in blocks, and on a light theme, where a pale glow would not show; a warning or an error takes the state's color. All
    // of them are moved just far enough to be seen on the surface (NoticeLook.AppColor).
    private static Color GlowColor(NoticeLook look, PanelPalette palette, NoticeFxTone tone)
    {
        if (tone == NoticeFxTone.Danger) return look.AppColor(Parse(look.Danger));
        if (tone == NoticeFxTone.Warning) return look.AppColor(Parse(PanelTheme.WarningDefault));
        if (palette.Effects.Pixel > 0 || look.Glow || !palette.IsDark) return look.AppColor(Parse(look.Accent));
        return Color.FromRgb(0xD8, 0xDD, 0xE6);
    }

    // The glow at an alpha, as a band that fades out towards both its ends (the capsule's edge) or as a spot (the indicator's
    // sweep). Made once per alpha.
    public Brush Fade(double alpha) => Cached(0, alpha, a => new LinearGradientBrush(new GradientStopCollection
    {
        new(WithAlpha(0), 0), new(WithAlpha(a), .22), new(WithAlpha(a), .78), new(WithAlpha(0), 1)
    }, new Point(0, 0), new Point(1, 0)));
    public Brush Spot(double alpha) => Cached(1, alpha, a => new RadialGradientBrush(new GradientStopCollection { new(WithAlpha(a), 0), new(WithAlpha(a * .45), .45), new(WithAlpha(0), 1) }));
    public Brush Plain(double alpha) => Cached(2, alpha, a => new SolidColorBrush(WithAlpha(a)));

    private Brush Cached(int kind, double alpha, Func<double, Brush> make)
    {
        int key = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 100);
        if (_brushes.TryGetValue((kind, key), out var brush)) return brush;
        return _brushes[(kind, key)] = Frozen(make(key / 100d));
    }
    private Color WithAlpha(double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), Glow.R, Glow.G, Glow.B);

    private static T Frozen<T>(T freezable) where T : Freezable { if (freezable.CanFreeze) freezable.Freeze(); return freezable; }
}

// The droplets, necks and bulges: drawn behind the surface, so wherever they overlap it the surface covers them, and in front of
// the window's transparent background, so they stand free of it. Each is drawn twice, first with the rim and then with the fill
// over it, so that only the outer half of the rim shows and neighbours merge into one outline.
internal sealed class NoticeFxBody : FrameworkElement
{
    public NoticeFxState? State { get; set; }

    protected override void OnRender(DrawingContext drawing)
    {
        if (State is not { } state || !state.Frame.Alive) return;
        var frame = state.Frame; var look = state.Look;
        double step = look.Step;
        if (state.Form == LiquidForm.Notch) DrawNotch(drawing, state, frame, look, step);
        else DrawPill(drawing, state, frame, look, step);
    }

    private static double Snap(double value, double step) => step > 0 ? Math.Round(value / step) * step : value;

    private static void DrawPill(DrawingContext drawing, NoticeFxState state, LiquidFrame frame, NoticeFxLook look, double step)
    {
        double left = state.Left, right = state.Left + state.Width, mid = state.Top + state.Height / 2;
        Point At(int side, LiquidPoint p) => new(side > 0 ? right + p.X : left - p.X, mid + p.Y);
        double Along(double x) => (x - left) / Math.Max(1, right - left);
        for (int pass = 0; pass < 2; pass++)
        {
            bool rim = pass == 0;
            if (rim && !look.HasRim) continue;
            if (look.Translucent && step <= 0) { DrawTogether(drawing, frame, look, At, rim); continue; }
            foreach (var disc in frame.Discs)
            {
                var center = At(disc.Side, new LiquidPoint(disc.X, disc.Y));
                var pen = rim ? look.Rim(Along(center.X)) : null;
                if (step > 0) { var square = Block(center, disc.R, step); drawing.DrawRectangle(rim ? null : look.Fill(.5), pen, square); }
                else drawing.DrawEllipse(rim ? null : look.Fill(.5), pen, center, disc.R, disc.R);
            }
            foreach (var neck in frame.Necks)
            {
                if (step > 0) { if (!rim) DrawBridge(drawing, look, neck, At, step, left, right); continue; }
                var p1 = At(neck.Side, neck.P1); var h1 = At(neck.Side, neck.H1); var h3 = At(neck.Side, neck.H3); var p3 = At(neck.Side, neck.P3);
                var p4 = At(neck.Side, neck.P4); var h4 = At(neck.Side, neck.H4); var h2 = At(neck.Side, neck.H2); var p2 = At(neck.Side, neck.P2);
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    if (rim)
                    {
                        // Only the two sides of the neck, not the ends: they lie inside the circles they join.
                        path.BeginFigure(p1, false, false); path.BezierTo(h1, h3, p3, true, true);
                        path.BeginFigure(p4, false, false); path.BezierTo(h4, h2, p2, true, true);
                    }
                    else
                    {
                        path.BeginFigure(p1, true, true); path.BezierTo(h1, h3, p3, true, false); path.LineTo(p4, true, false); path.BezierTo(h4, h2, p2, true, false);
                    }
                }
                drawing.DrawGeometry(rim ? null : look.Fill(.5), rim ? look.Rim(Along(p1.X)) : null, geometry);
            }
        }
    }

    // A translucent surface's droplets and necks as one shape, filled once and with one rim round the whole, so that where droplets
    // overlap nothing is drawn twice and no outline shows inside the union. Every figure runs clockwise on the screen, so that where
    // figures overlap the nonzero rule fills the union once.
    private static void DrawTogether(DrawingContext drawing, LiquidFrame frame, NoticeFxLook look, Func<int, LiquidPoint, Point> at, bool rim)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var path = geometry.Open())
        {
            foreach (var disc in frame.Discs)
            {
                var c = at(disc.Side, new LiquidPoint(disc.X, disc.Y));
                path.BeginFigure(new Point(c.X - disc.R, c.Y), true, true);
                path.ArcTo(new Point(c.X + disc.R, c.Y), new Size(disc.R, disc.R), 0, false, SweepDirection.Clockwise, true, false);
                path.ArcTo(new Point(c.X - disc.R, c.Y), new Size(disc.R, disc.R), 0, false, SweepDirection.Clockwise, true, false);
            }
            foreach (var neck in frame.Necks)
            {
                Point p1 = at(neck.Side, neck.P1), h1 = at(neck.Side, neck.H1), h3 = at(neck.Side, neck.H3), p3 = at(neck.Side, neck.P3);
                Point p4 = at(neck.Side, neck.P4), h4 = at(neck.Side, neck.H4), h2 = at(neck.Side, neck.H2), p2 = at(neck.Side, neck.P2);
                double area = (p1.X * p3.Y - p3.X * p1.Y) + (p3.X * p4.Y - p4.X * p3.Y) + (p4.X * p2.Y - p2.X * p4.Y) + (p2.X * p1.Y - p1.X * p2.Y);
                if (area >= 0) { path.BeginFigure(p1, true, true); path.BezierTo(h1, h3, p3, true, false); path.LineTo(p4, true, false); path.BezierTo(h4, h2, p2, true, false); }
                else { path.BeginFigure(p2, true, true); path.BezierTo(h2, h4, p4, true, false); path.LineTo(p3, true, false); path.BezierTo(h3, h1, p1, true, false); }
            }
        }
        if (rim) drawing.DrawGeometry(null, look.Rim(.5), geometry.GetOutlinedPathGeometry());
        else { geometry.Freeze(); drawing.DrawGeometry(look.Fill(.5), null, geometry); }
    }

    // A block as the pixel theme draws a droplet: as wide as the droplet, in whole steps, on the grid.
    private static Rect Block(Point center, double radius, double step)
    {
        double side = Math.Max(step, Math.Round(radius * 2 / step) * step);
        return new Rect(Snap(center.X - side / 2, step), Snap(center.Y - side / 2, step), side, side);
    }

    // A neck of one block: a bar from the end to the droplet, as thick as the neck is at its thinnest, on the grid (the pixel theme
    // has no curves). It thins to nothing as the neck does, and the droplet is left a block alone.
    private static void DrawBridge(DrawingContext drawing, NoticeFxLook look, LiquidNeck neck, Func<int, LiquidPoint, Point> at, double step, double left, double right)
    {
        Point a = Mid(at(neck.Side, neck.P1), at(neck.Side, neck.P2)), b = Mid(at(neck.Side, neck.P3), at(neck.Side, neck.P4));
        double thickness = Math.Round(Math.Min(Distance(at(neck.Side, neck.P1), at(neck.Side, neck.P2)), Distance(at(neck.Side, neck.P3), at(neck.Side, neck.P4))) * .7 / step) * step;
        if (thickness < step) return;
        double x0 = Math.Round(Math.Min(a.X, b.X) / step) * step, x1 = Math.Round(Math.Max(a.X, b.X) / step) * step, y = Math.Round(((a.Y + b.Y) / 2 - thickness / 2) / step) * step;
        if (x1 - x0 < step) return;
        var bar = new Rect(x0, y, x1 - x0, thickness);
        if (look.Rim((Math.Min(a.X, b.X) - left) / Math.Max(1, right - left)) is { } rim) drawing.DrawRectangle(null, rim, bar);
        drawing.DrawRectangle(look.Fill(.5), null, bar);
    }
    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static void DrawNotch(DrawingContext drawing, NoticeFxState state, LiquidFrame frame, NoticeFxLook look, double step)
    {
        double ox = state.Left, oy = state.Top;
        Point P(LiquidPoint p) => new(Snap(ox + p.X, step), Snap(oy + p.Y, step));
        int at = 0;
        var fills = new StreamGeometry();
        double Along(double x) => (x - ox) / Math.Max(1, state.Width);
        using (var fill = fills.Open())
        {
            foreach (int count in frame.BumpRuns)
            {
                int half = count / 2;
                fill.BeginFigure(P(frame.BumpPoints[at]), true, true);
                for (int i = 1; i < count; i++) fill.LineTo(P(frame.BumpPoints[at + i]), true, false);
                // The rim runs along the outer edge only, in the theme's color where the bulge stands.
                if (look.HasRim)
                {
                    var rim = new StreamGeometry();
                    using (var path = rim.Open())
                    {
                        path.BeginFigure(P(frame.BumpPoints[at]), false, false);
                        for (int i = 1; i < half; i++) path.LineTo(P(frame.BumpPoints[at + i]), true, true);
                    }
                    drawing.DrawGeometry(null, look.Rim(Along(ox + frame.BumpPoints[at + half / 2].X)), rim);
                }
                at += count;
            }
        }
        drawing.DrawGeometry(look.Fill(1), null, fills);
        foreach (var disc in frame.Discs)
        {
            var center = new Point(ox + disc.X, oy + disc.Y);
            var pen = look.Rim(Along(center.X));
            var fill = look.Fill(disc.Y / Math.Max(1, state.Height));
            if (step > 0) { var square = Block(center, disc.R, step); if (pen is not null) drawing.DrawRectangle(null, pen, square); drawing.DrawRectangle(fill, null, square); }
            else { if (pen is not null) drawing.DrawEllipse(null, pen, center, disc.R, disc.R); drawing.DrawEllipse(fill, null, center, disc.R, disc.R); }
        }
    }
}

// The edge glow, over the surface: the capsule's top and bottom edges light up twice, the notch's whole outline does (narrowing
// to nothing towards the shoulders, where it meets the screen), and a spot sweeps along the indicator's edges twice. Each is a
// few thin layers of the glow's color, the widest the faintest, which gives the soft edge without a blur (so nothing is
// rendered offscreen); a pixel theme draws two hard layers.
internal sealed class NoticeFxGlow : FrameworkElement
{
    public NoticeFxState? State { get; set; }
    private (LiquidPoint[] Outline, double Width, Geometry Band)[] _bands = [];

    protected override void OnRender(DrawingContext drawing)
    {
        if (State is not { } state || !state.Frame.Alive) return;
        var frame = state.Frame; var look = state.Look;
        if (state.Form != LiquidForm.Notch && !look.Translucent) DrawMouths(drawing, state, frame, look);
        if (!look.Glows) return;
        double strength = Math.Clamp(state.Plan.Glow, 0, 1.25);
        switch (state.Form)
        {
            case LiquidForm.Notch: DrawOutline(drawing, state, frame, look, strength); break;
            case LiquidForm.Line: DrawSweeps(drawing, state, frame, look, strength); break;
            default: DrawEdges(drawing, state, look, Math.Min(1, frame.Glow * strength)); break;
        }
    }

    // Where a neck leaves the end, the surface's own rim crosses its mouth and would close it with a line. A thin bar of the surface's
    // fill across the mouth, 2.5 DIPs to either side of the outline (so it never reaches the content), covers that stretch of rim,
    // and the neck's outline carries on from either side of it.
    private static void DrawMouths(DrawingContext drawing, NoticeFxState state, LiquidFrame frame, NoticeFxLook look)
    {
        if (!look.HasRim) return;
        double left = state.Left, right = state.Left + state.Width, mid = state.Top + state.Height / 2;
        foreach (var neck in frame.Necks)
        {
            if (!neck.Mouth) continue;
            double x = (neck.P1.X + neck.P2.X) / 2, y = (neck.P1.Y + neck.P2.Y) / 2;
            double chord = Math.Sqrt((neck.P1.X - neck.P2.X) * (neck.P1.X - neck.P2.X) + (neck.P1.Y - neck.P2.Y) * (neck.P1.Y - neck.P2.Y)) / 2;
            if (chord < 1) continue;
            var center = new Point(neck.Side > 0 ? right + x : left - x, mid + y);
            double height = chord * 1.7;
            if (look.Step > 0) { height = Math.Round(height / look.Step) * look.Step; if (height >= look.Step) drawing.DrawRectangle(look.Fill(.5), null, new Rect(Math.Round((center.X - 2) / look.Step) * look.Step, Math.Round((center.Y - height / 2) / look.Step) * look.Step, look.Step, height)); }
            else drawing.DrawRoundedRectangle(look.Fill(.5), null, new Rect(center.X - 2.5, center.Y - height / 2, 5, height), 2, 2);
        }
    }

    // The layers: thickness and the share of the glow's alpha. A pixel theme's are whole blocks.
    private static (double Thickness, double Alpha)[] Layers(NoticeFxLook look) => look.Step > 0
        ? [(look.Step * 2, .3), (look.Step, 1)]
        : [(6, .16), (3.4, .32), (1.3, 1)];

    private static void DrawEdges(DrawingContext drawing, NoticeFxState state, NoticeFxLook look, double amount)
    {
        if (amount <= .01) return;
        double radius = state.Height / 2;
        double x0 = state.Left + radius, x1 = state.Left + state.Width - radius, top = state.Top, bottom = state.Top + state.Height;
        if (x1 - x0 < 8) return;
        foreach (var (thickness, alpha) in Layers(look))
        {
            var brush = look.Fade(alpha * amount);
            drawing.DrawRectangle(brush, null, new Rect(x0, top - thickness / 2, x1 - x0, thickness));
            drawing.DrawRectangle(brush, null, new Rect(x0, bottom - thickness / 2, x1 - x0, thickness));
        }
    }

    private static void DrawSweeps(DrawingContext drawing, NoticeFxState state, LiquidFrame frame, NoticeFxLook look, double strength)
    {
        double radius = state.Height / 2, middle = state.Left + state.Width / 2, half = Math.Max(0, (state.Width - 2 * radius) / 2);
        if (half < 4) return;
        for (int i = 0; i < frame.SweepCount; i++)
        {
            var sweep = frame.Sweeps[i];
            double amount = Math.Min(1, sweep.Amount * strength);
            if (amount <= .02) continue;
            foreach (int side in new[] { -1, 1 })
            {
                double x = middle + side * sweep.Position * half;
                foreach (double y in new[] { state.Top, state.Top + state.Height })
                {
                    if (look.Step > 0) { drawing.DrawRectangle(look.Plain(amount), null, new Rect(Math.Round(x / look.Step) * look.Step - look.Step * 2, y - look.Step / 2, look.Step * 4, look.Step)); continue; }
                    // A halo a little wider than the line it lights, and the line itself.
                    drawing.DrawEllipse(look.Spot(amount * .5), null, new Point(x, y), 38, 6);
                    drawing.DrawRectangle(look.Fade(amount), null, new Rect(x - 22, y - .65, 44, 1.3));
                }
            }
        }
    }

    private void DrawOutline(DrawingContext drawing, NoticeFxState state, LiquidFrame frame, NoticeFxLook look, double strength)
    {
        double amount = Math.Min(1, frame.Glow * strength);
        if (amount <= .01 || frame.Outline.Length < 2) return;
        drawing.PushTransform(new TranslateTransform(state.Left, state.Top));
        foreach (var (thickness, alpha) in Layers(look))
            drawing.DrawGeometry(look.Plain(alpha * amount), null, Band(frame, thickness));
        drawing.Pop();
    }

    // A band along the outline that is `thickness` wide where it is full and narrows to nothing over the last 46 DIPs of each
    // end: strongest along the bottom and round the bottom corners, then thinner up the walls and over the shoulders. Made again
    // only when the outline (the notice's size) changes.
    private Geometry Band(LiquidFrame frame, double thickness)
    {
        foreach (var (outline, width, band) in _bands)
            if (ReferenceEquals(outline, frame.Outline) && width == thickness) return band;
        var points = frame.Outline; var normals = frame.Normals; var along = frame.Along;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool outer = pass == 0;
                for (int k = 0; k < points.Length; k++)
                {
                    int i = outer ? k : points.Length - 1 - k;
                    double taper = Math.Clamp(Math.Min(along[i], frame.OutlineLength - along[i]) / 46, 0, 1);
                    double half = thickness / 2 * taper * taper * (3 - 2 * taper), sign = outer ? 1 : -1;
                    var p = new Point(points[i].X + normals[i].X * half * sign, points[i].Y + normals[i].Y * half * sign);
                    if (k == 0 && outer) path.BeginFigure(p, true, true); else path.LineTo(p, true, false);
                }
            }
        }
        geometry.Freeze();
        var next = _bands.Where(entry => ReferenceEquals(entry.Outline, frame.Outline)).Append((frame.Outline, thickness, (Geometry)geometry)).ToArray();
        _bands = next;
        return geometry;
    }

    public void Forget() => _bands = [];
}
