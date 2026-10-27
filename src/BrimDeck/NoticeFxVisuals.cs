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

    // The glow at an alpha, as a band that fades out towards both its ends (the capsule's edge). Made once per alpha.
    public Brush Fade(double alpha) => Cached(0, alpha, a => new LinearGradientBrush(new GradientStopCollection
    {
        new(WithAlpha(0), 0), new(WithAlpha(a), .22), new(WithAlpha(a), .78), new(WithAlpha(0), 1)
    }, new Point(0, 0), new Point(1, 0)));
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
        // What has parted is drawn as one shape (a cluster of droplets still joined to each other has one boundary, too); what is
        // still joined to the surface is part of its outline (NoticeFxShape), not drawn here.
        if (look.HasRim) DrawTogether(drawing, frame, look, At, true);
        DrawTogether(drawing, frame, look, At, false);
    }

    // The droplets that have parted, and the necks between them, as one shape, filled once and with one rim round the whole, so that
    // where they overlap nothing is drawn twice and no outline shows inside the union. Every figure runs clockwise on the screen, so
    // that where figures overlap the nonzero rule fills the union once.
    private static void DrawTogether(DrawingContext drawing, LiquidFrame frame, NoticeFxLook look, Func<int, LiquidPoint, Point> at, bool rim)
    {
        double step = look.Step;
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        bool any = false;
        using (var path = geometry.Open())
        {
            foreach (var disc in frame.Discs)
            {
                if (disc.Attached) continue;
                NoticeFxShape.AddDisc(path, at(disc.Side, new LiquidPoint(disc.X, disc.Y)), disc.R, step); any = true;
            }
            foreach (var neck in frame.Necks)
            {
                if (neck.Attached) continue;
                any |= NoticeFxShape.AddNeck(path, neck, at, step);
            }
        }
        if (!any) return;
        if (rim) drawing.DrawGeometry(null, look.Rim(.5), geometry.GetOutlinedPathGeometry());
        else { geometry.Freeze(); drawing.DrawGeometry(look.Fill(.5), null, geometry); }
    }

    // A block as the pixel theme draws a droplet: as wide as the droplet, in whole steps, on the grid.
    internal static Rect Block(Point center, double radius, double step)
    {
        double side = Math.Max(step, Math.Round(radius * 2 / step) * step);
        return new Rect(Snap(center.X - side / 2, step), Snap(center.Y - side / 2, step), side, side);
    }

    // The bar of a pixel theme's neck, or null once it has thinned to nothing: a bar from the end to the droplet, as thick as the neck is
    // at its thinnest, on the grid (the pixel theme has no curves).
    internal static Rect? Bridge(LiquidNeck neck, Func<int, LiquidPoint, Point> at, double step)
    {
        Point a = Mid(at(neck.Side, neck.P1), at(neck.Side, neck.P2)), b = Mid(at(neck.Side, neck.P3), at(neck.Side, neck.P4));
        double thickness = Math.Round(Math.Min(Distance(at(neck.Side, neck.P1), at(neck.Side, neck.P2)), Distance(at(neck.Side, neck.P3), at(neck.Side, neck.P4))) * .7 / step) * step;
        if (thickness < step) return null;
        double x0 = Math.Round(Math.Min(a.X, b.X) / step) * step, x1 = Math.Round(Math.Max(a.X, b.X) / step) * step, y = Math.Round(((a.Y + b.Y) / 2 - thickness / 2) / step) * step;
        if (x1 - x0 < step) return null;
        return new Rect(x0, y, x1 - x0, thickness);
    }
    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // The notch's beads: the bulges are part of the surface's own outline (NoticeFxShape), and a bead that has parted from them has its own.
    private static void DrawNotch(DrawingContext drawing, NoticeFxState state, LiquidFrame frame, NoticeFxLook look, double step)
    {
        double ox = state.Left, oy = state.Top;
        double Along(double x) => (x - ox) / Math.Max(1, state.Width);
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

// The shape of what is joined to the notice's surface while the motion plays, in the surface's own coordinates (its top-left corner
// the origin): the capsule's necks and the droplets they have not parted from, the notch's bulges. The surface unions it into its
// outline (NoticeSurface.Attachments), so that what is joined shares one boundary with the body and changes with the motion; once a
// neck has thinned to nothing the droplet is no longer here, and is drawn on its own (NoticeFxBody).
internal static class NoticeFxShape
{
    public static Geometry? Build(LiquidFrame frame, LiquidForm form, double width, double height, double step)
    {
        if (!frame.Alive) return null;
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        bool any = false;
        using (var path = geometry.Open())
        {
            if (form == LiquidForm.Notch)
            {
                int at = 0;
                foreach (int count in frame.BumpRuns)
                {
                    path.BeginFigure(P(frame.BumpPoints[at], step), true, true);
                    for (int i = 1; i < count; i++) path.LineTo(P(frame.BumpPoints[at + i], step), true, false);
                    at += count; any = true;
                }
            }
            else
            {
                Point At(int side, LiquidPoint p) => new(side > 0 ? width + p.X : -p.X, height / 2 + p.Y);
                foreach (var disc in frame.Discs)
                {
                    if (!disc.Attached) continue;
                    AddDisc(path, At(disc.Side, new LiquidPoint(disc.X, disc.Y)), disc.R, step); any = true;
                }
                foreach (var neck in frame.Necks)
                {
                    if (!neck.Attached) continue;
                    any |= AddNeck(path, neck, At, step);
                }
            }
        }
        if (!any) return null;
        geometry.Freeze();
        return geometry;
    }

    // A droplet as a figure: a circle, or for a pixel theme the rows of a circle on the grid (a stepped disc). A droplet smaller than a block
    // is a block.
    internal static void AddDisc(StreamGeometryContext path, Point c, double r, double step)
    {
        if (step <= 0)
        {
            path.BeginFigure(new Point(c.X - r, c.Y), true, true);
            path.ArcTo(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            path.ArcTo(new Point(c.X - r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            return;
        }
        if (r < step * 1.2) { Rectangle(path, NoticeFxBody.Block(c, r, step)); return; }
        double top = Math.Floor((c.Y - r) / step) * step;
        for (double y = top; y < c.Y + r; y += step)
        {
            double dy = y + step / 2 - c.Y;
            if (Math.Abs(dy) >= r) continue;
            double half = Math.Sqrt(r * r - dy * dy);
            double x0 = Math.Round((c.X - half) / step) * step, x1 = Math.Round((c.X + half) / step) * step;
            if (x1 - x0 >= step) Rectangle(path, new Rect(x0, y, x1 - x0, step));
        }
    }

    // A neck as a figure (clockwise): its two curves, or for a pixel theme its bar. False once there is nothing of it.
    internal static bool AddNeck(StreamGeometryContext path, LiquidNeck neck, Func<int, LiquidPoint, Point> at, double step)
    {
        if (step > 0)
        {
            if (NoticeFxBody.Bridge(neck, at, step) is not { } bar) return false;
            Rectangle(path, bar); return true;
        }
        Point p1 = at(neck.Side, neck.P1), h1 = at(neck.Side, neck.H1), h3 = at(neck.Side, neck.H3), p3 = at(neck.Side, neck.P3);
        Point p4 = at(neck.Side, neck.P4), h4 = at(neck.Side, neck.H4), h2 = at(neck.Side, neck.H2), p2 = at(neck.Side, neck.P2);
        double area = (p1.X * p3.Y - p3.X * p1.Y) + (p3.X * p4.Y - p4.X * p3.Y) + (p4.X * p2.Y - p2.X * p4.Y) + (p2.X * p1.Y - p1.X * p2.Y);
        if (area >= 0) { path.BeginFigure(p1, true, true); path.BezierTo(h1, h3, p3, true, false); path.LineTo(p4, true, false); path.BezierTo(h4, h2, p2, true, false); }
        else { path.BeginFigure(p2, true, true); path.BezierTo(h2, h4, p4, true, false); path.LineTo(p3, true, false); path.BezierTo(h3, h1, p1, true, false); }
        return true;
    }

    private static Point P(LiquidPoint p, double step) => step > 0 ? new Point(Math.Round(p.X / step) * step, Math.Round(p.Y / step) * step) : new Point(p.X, p.Y);

    private static void Rectangle(StreamGeometryContext path, Rect r)
    {
        path.BeginFigure(r.TopLeft, true, true); path.LineTo(r.TopRight, true, false); path.LineTo(r.BottomRight, true, false); path.LineTo(r.BottomLeft, true, false);
    }
}

// The edge glow, over the surface: the capsule's top and bottom edges light up twice, the notch's whole outline does (narrowing
// to nothing towards the shoulders, where it meets the screen), and a spot sweeps along the indicator's edges twice. Each is a
// few thin layers of the glow's color, the widest the faintest, which gives the soft edge without a blur (so nothing is
// rendered offscreen); a pixel theme draws two hard layers.
internal sealed class NoticeFxGlow : FrameworkElement
{
    public NoticeFxState? State { get; set; }

    protected override void OnRender(DrawingContext drawing)
    {
        if (State is not { } state || !state.Frame.Alive) return;
        var frame = state.Frame; var look = state.Look;
        if (!look.Glows) return;
        double strength = Math.Clamp(state.Plan.Glow, 0, 1.25);
        switch (state.Form)
        {
            case LiquidForm.Notch: DrawOutline(drawing, state, frame, look, strength); break;
            default: DrawEdges(drawing, state, look, Math.Min(1, frame.Glow * strength)); break;
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
    // end: strongest along the bottom and round the bottom corners, then thinner up the walls and over the shoulders. It runs along the
    // surface's outline as the bulges have moved it (LiquidFrame.Offsets), so the glow is on the outline the surface has, not on another.
    private static Geometry Band(LiquidFrame frame, double thickness)
    {
        var points = frame.Outline; var normals = frame.Normals; var along = frame.Along; var offsets = frame.Offsets;
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
                    double lift = i < offsets.Length ? offsets[i] : 0;
                    var p = new Point(points[i].X + normals[i].X * (half * sign + lift), points[i].Y + normals[i].Y * (half * sign + lift));
                    if (k == 0 && outer) path.BeginFigure(p, true, true); else path.LineTo(p, true, false);
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }

}
