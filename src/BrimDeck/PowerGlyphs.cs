using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The colors and shapes of the power and battery readings on the panel theme in use: the theme's look (PowerLooks) resolved against
// the monitor's color scheme and the theme's tokens. Made once per theme and scheme.
internal sealed class PowerStyle
{
    public PowerLook Look { get; }
    // Normal fills a battery as it is; Charge while charging; Saver outlines Battery saver; Draw is the power curve and the bolt badge.
    public string Normal { get; }
    public string Charge { get; }
    public string Saver { get; }
    public string Draw { get; }
    public string Warning { get; }
    public string Critical { get; }

    private PowerStyle(PowerLook look, MonitorColors colors)
    {
        Look = look;
        string Resolve(string name) => name switch
        {
            "cpu" => colors.Performance, "efficiency" => colors.Efficiency, "memory" => colors.Memory, "gpu" => colors.Gpu,
            "warning" => colors.Warning, "critical" => colors.Critical, _ => PanelTheme.Hex(name)
        };
        Normal = Resolve(look.Normal); Charge = Resolve(look.Charge); Saver = Resolve(look.Saver); Draw = Resolve(look.Draw);
        Warning = colors.Warning; Critical = colors.Critical;
    }

    private static (int Generation, bool HighContrast, MonitorColors Colors, PowerStyle Style)? _last;
    public static PowerStyle For(MonitorColors colors)
    {
        if (_last is { } known && known.Generation == PanelTheme.Generation && known.HighContrast == PanelTheme.HighContrast && known.Colors == colors) return known.Style;
        var style = new PowerStyle(PowerLooks.For(PanelTheme.Current, PanelTheme.HighContrast), colors);
        _last = (PanelTheme.Generation, PanelTheme.HighContrast, colors, style);
        return style;
    }

    // The color a battery is filled with at a level: the level's own colors when it is low, else the charging or the normal color.
    public string Fill(MemoryLevel level, BatteryState state) => level switch
    {
        MemoryLevel.Critical => Critical, MemoryLevel.Elevated => Warning, _ => state == BatteryState.Charging ? Charge : Normal
    };
}

// What a battery drawing needs of a reading.
internal readonly record struct BatteryVisual(double Percent, BatteryState State, bool Saver, MemoryLevel Level)
{
    public bool Charging => State == BatteryState.Charging;
    public static BatteryVisual Of(BatterySample sample, int low) => new(sample.Percent, sample.State, sample.BatterySaver, BatteryModel.Level(sample, low));
}

// The drawings shared by the monitor page, the collapsed row and the settings preview: a battery in the theme's shape, the bolt, the
// leaf of Battery saver, and the power curve. Each shape follows the look of the theme (PowerLook); the marks that tell a state
// (a bolt, an exclamation mark, a leaf) are drawn in every look, so color is never the only sign.
internal static class PowerGlyphs
{
    private static readonly Geometry LeafShape = Frozen("M3,13 C3,6.5 7.5,3 13,3 C13,9 9.5,13 3,13 Z M3,13 L9,7");
    private static readonly Geometry BoltBadge = Frozen("M9,1.5 L3.8,9 H7.6 L7,14.5 L12.2,7 H8.4 Z");
    private static Geometry Frozen(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    // Pens by color, thickness, join and dash, made once and frozen: the strip draws the battery on every reading.
    private static readonly Dictionary<(string Color, double Thickness, PenLineJoin Join, bool Dashed), Pen> Outlines = [];
    public static Pen Outline(string color, double thickness, PenLineJoin join, bool dashed = false)
    {
        if (Outlines.TryGetValue((color, thickness, join, dashed), out var known)) return known;
        var pen = new Pen(UI.Brush(color), thickness) { LineJoin = join, MiterLimit = 2 };
        if (dashed) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        if (Outlines.Count > 96) Outlines.Clear();
        return Outlines[(color, thickness, join, dashed)] = pen;
    }

    // The leaf, stroked, in a square of the size with its top left corner at the point.
    public static void Leaf(DrawingContext drawing, Point at, double size, string color)
    {
        double scale = size / 16;
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, at.X, at.Y));
        drawing.DrawGeometry(null, PowerMarks.Stroke(color, Math.Max(1, 1.4 / Math.Max(scale, .5)) * (scale < .6 ? .8 : 1)), LeafShape);
        drawing.Pop();
    }

    // The badge before the power figure: a bolt in a ring (a hexagon for the HUD look, a square for blocks), on the quota rings' track.
    public static void Badge(DrawingContext drawing, Point center, double size, string color, bool known, PowerStyle style)
    {
        double ring = Math.Max(1.2, size / 10), r = size / 2 - ring / 2;
        var track = PowerMarks.Stroke(PanelTheme.Hex("ringTrack"), ring);
        switch (style.Look.Gauge)
        {
            case PowerGauge.Blocks:
                drawing.DrawRectangle(null, Outline(PanelTheme.Hex("ringTrack"), ring, PenLineJoin.Miter), new Rect(center.X - r, center.Y - r, 2 * r, 2 * r));
                break;
            case PowerGauge.Chamfer:
                {
                    double c = r * .5;
                    var figure = new PathFigure(new Point(center.X - r + c, center.Y - r), [], true);
                    foreach (var p in new[] { new Point(center.X + r, center.Y - r), new Point(center.X + r, center.Y + r - c), new Point(center.X + r - c, center.Y + r),
                        new Point(center.X - r, center.Y + r), new Point(center.X - r, center.Y - r + c) }) figure.Segments.Add(new LineSegment(p, true));
                    var hex = new PathGeometry([figure]); hex.Freeze();
                    drawing.DrawGeometry(null, Outline(PanelTheme.Hex("ringTrack"), ring, PenLineJoin.Miter), hex);
                    break;
                }
            default:
                drawing.DrawEllipse(null, track, center, r, r);
                break;
        }
        double scale = size * .72 / 16;
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, center.X - 8 * scale, center.Y - 8 * scale));
        drawing.DrawGeometry(UI.Brush(known ? color : UI.Tertiary), null, BoltBadge);
        drawing.Pop();
    }

    // A battery lying on its side in the area (the tip included): the body in the look's shape, filled to the share (fill, 0 to 1,
    // easing to the reading). compact: too small to carry a mark inside; the bolt then replaces the tip while charging, the leaf
    // while Battery saver is on, and an exclamation mark shows in the empty part when low. unknown: no reading; outlined and empty.
    public static void Battery(DrawingContext drawing, Rect area, BatteryVisual battery, PowerStyle style, double ppd, double fill, bool compact, bool unknown = false)
    {
        double Snap(double value) => Math.Round(value * ppd, MidpointRounding.AwayFromZero) / ppd;
        var gauge = style.Look.Gauge;
        double h = Snap(area.Height), stroke = Math.Max(1, Math.Round(Math.Max(1, h * .11) * ppd)) / ppd;
        bool charging = battery.Charging && !unknown, saver = battery.Saver && battery.State == BatteryState.Discharging && battery.Level == MemoryLevel.Normal && !unknown;
        // The tip is a mark's place in the compact drawing: the bolt (charging) or the leaf (saver) takes it.
        bool tipMark = compact && (charging || saver);
        double tipWidth = tipMark ? Snap(h * (charging ? 15 / 23.0 : .9)) : gauge == PowerGauge.Blocks ? Snap(Math.Max(2, h * .2)) : Snap(Math.Max(1, h * .16));
        string fillColor = style.Fill(battery.Level, battery.State);
        string ink = unknown ? UI.Tertiary : saver ? style.Saver : fillColor;
        var body = new Rect(Snap(area.X) + stroke / 2, Snap(area.Y) + stroke / 2, Math.Max(2, Snap(area.Width - tipWidth - 1) - stroke), Math.Max(2, h - stroke));
        double inset = stroke + 1 / ppd;
        var inner = new Rect(body.X - stroke / 2 + inset, body.Y - stroke / 2 + inset, Math.Max(0, body.Width + stroke - 2 * inset), Math.Max(0, body.Height + stroke - 2 * inset));
        double share = unknown ? 0 : Math.Clamp(fill, 0, 1);
        var pen = Outline(ink, stroke, gauge is PowerGauge.Chamfer or PowerGauge.Blocks ? PenLineJoin.Miter : PenLineJoin.Round);
        double tipX = Snap(body.Right + stroke / 2 + 1 / ppd);
        double lit = inner.Width * share;

        switch (gauge)
        {
            case PowerGauge.Blocks:
                {
                    drawing.DrawRectangle(null, pen, body);
                    int cells = compact || inner.Width < 40 ? 5 : 10;
                    double gap = Math.Max(1 / ppd, Snap(Math.Max(1, h * .08))), cell = Math.Max(1 / ppd, Snap((inner.Width - (cells - 1) * gap) / cells));
                    int on = share <= 0 ? 0 : (int)Math.Ceiling(share * cells - 1e-9);
                    var unlit = UI.Brush(MonitorDraw.Faint(ink, .22)); var lightOn = UI.Brush(fillColor);
                    for (int i = 0; i < cells; i++)
                    {
                        var rect = new Rect(inner.X + i * (cell + gap), inner.Y, cell, inner.Height);
                        if (rect.Right > inner.Right + .01) break;
                        drawing.DrawRectangle(i < on ? lightOn : unlit, null, rect);
                    }
                    break;
                }
            case PowerGauge.Chamfer:
                {
                    double cut = Snap(Math.Max(2, h * .3));
                    var outline = Cut(body, cut); var inside = Cut(inner, Math.Max(1, cut - inset));
                    drawing.DrawGeometry(null, pen, outline);
                    if (lit > 0)
                    {
                        drawing.PushClip(inside);
                        drawing.DrawRectangle(UI.Brush(fillColor), null, new Rect(inner.X, inner.Y, lit, inner.Height));
                        drawing.Pop();
                    }
                    break;
                }
            default:
                {
                    double radius = gauge == PowerGauge.Round ? body.Height / 2 : PanelTheme.Corner(Math.Max(1.5, h * .22));
                    drawing.DrawRoundedRectangle(null, pen, body, radius, radius);
                    if (lit > 0)
                    {
                        double innerRadius = Math.Max(0, radius - inset);
                        drawing.PushClip(new RectangleGeometry(inner, innerRadius, innerRadius));
                        drawing.DrawRectangle(UI.Brush(fillColor), null, new Rect(inner.X, inner.Y, lit, inner.Height));
                        drawing.Pop();
                    }
                    break;
                }
        }

        // The tip: a bump, or a mark's place (the bolt, the leaf).
        var tip = new Rect(tipX, Snap(area.Y + (h - h * .4) / 2), tipWidth, Snap(h * .4));
        if (tipMark && charging) PowerMarks.Bolt(drawing, new Rect(tipX, Snap(area.Y), tipWidth, h), ink);
        else if (tipMark) Leaf(drawing, new Point(tipX - h * .05, Snap(area.Y)), h, ink);
        else drawing.DrawRectangle(UI.Brush(ink), null, tip);

        if (unknown) return;
        // Marks inside a body big enough for them, in the ink that reads on the fill.
        if (!compact && inner.Height >= 8 && inner.Width >= 14)
        {
            string onFill = Ink(fillColor);
            double markSize = Math.Floor(inner.Height * .9);
            if (charging) PowerMarks.Bolt(drawing, new Rect(inner.X + (inner.Width - markSize * .7) / 2, inner.Y + (inner.Height - markSize) / 2, markSize * .7, markSize), lit > inner.Width * .5 ? onFill : fillColor);
            else if (battery.Level != MemoryLevel.Normal)
            {
                var empty = new Rect(inner.X + lit, inner.Y, Math.Max(0, inner.Width - lit), inner.Height);
                PowerMarks.Exclamation(drawing, empty.Width >= markSize * .6 ? empty : inner, empty.Width >= markSize * .6 ? fillColor : onFill, null, ppd);
            }
            else if (saver) Leaf(drawing, new Point(inner.X + inner.Width / 2 - markSize / 2, inner.Y + (inner.Height - markSize) / 2), markSize, lit > inner.Width * .5 ? onFill : style.Saver);
        }
        else if (compact && battery.Level != MemoryLevel.Normal && !charging)
        {
            double left = inner.X + lit;
            PowerMarks.Exclamation(drawing, new Rect(left, body.Y + stroke / 2, Math.Max(0, inner.Width - lit), Math.Max(0, body.Height - stroke)), fillColor, null, ppd);
        }
    }

    // A rectangle with the top left and the bottom right corners cut, as the HUD look cuts its panels.
    private static readonly Dictionary<(Rect, double), Geometry> Cuts = [];
    private static Geometry Cut(Rect r, double cut)
    {
        if (Cuts.TryGetValue((r, cut), out var known)) return known;
        if (Cuts.Count > 48) Cuts.Clear();
        return Cuts[(r, cut)] = Make(r, cut);
    }
    private static Geometry Make(Rect r, double cut)
    {
        cut = Math.Min(cut, Math.Min(r.Width, r.Height) / 2);
        var figure = new PathFigure(new Point(r.X + cut, r.Y), [], true);
        foreach (var p in new[] { new Point(r.Right, r.Y), new Point(r.Right, r.Bottom - cut), new Point(r.Right - cut, r.Bottom), new Point(r.X, r.Bottom), new Point(r.X, r.Y + cut) })
            figure.Segments.Add(new LineSegment(p, true));
        var geometry = new PathGeometry([figure]); geometry.Freeze();
        return geometry;
    }

    // Black or white, whichever reads on the color.
    public static string Ink(string fill) => ThemeColor.Parse(fill).Luminance > .4 ? "#101418" : "#FFFFFF";

    // The last minute of the power draw in the look's curve, newest at the right edge; top is the reading that reaches the top of the area.
    public static void Curve(DrawingContext drawing, Rect area, IReadOnlyList<double> history, double top, PowerStyle style)
    {
        string color = style.Draw;
        switch (style.Look.Curve)
        {
            case PowerCurve.Wave: MonitorDraw.Sparkline(drawing, area, history, top, color, .26, 1.6); break;
            case PowerCurve.Fine: MonitorDraw.Sparkline(drawing, area, history, top, color, .06, 1); break;
            case PowerCurve.Sharp: Polyline(drawing, area, history, top, color, steps: false); break;
            case PowerCurve.Steps: Polyline(drawing, area, history, top, color, steps: true); break;
            default: MonitorDraw.Sparkline(drawing, area, history, top, color, .14, 1.4); break;
        }
    }

    // Straight segments (a scope), or stairs (pixels), over a faint fill; a scope also lights its newest reading.
    private static void Polyline(DrawingContext drawing, Rect area, IReadOnlyList<double> history, double top, string color, bool steps)
    {
        int count = history.Count;
        if (count < 3 || area.Width < 8 || area.Height < 4 || !(top > 0)) return;
        double step = area.Width / (Native.SystemSampler.HistoryLength - 1);
        var points = new Point[count];
        for (int i = 0; i < count; i++)
            points[i] = new Point(area.Right - (count - 1 - i) * step, area.Bottom - 1 - Math.Clamp(history[i] / top, 0, 1) * (area.Height - 2));
        var line = new StreamGeometry(); var under = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(points[0], false, false);
            for (int i = 1; i < count; i++)
            {
                if (steps) context.LineTo(new Point(points[i].X, points[i - 1].Y), true, false);
                context.LineTo(points[i], true, false);
            }
        }
        using (var context = under.Open())
        {
            context.BeginFigure(new Point(points[0].X, area.Bottom), true, true);
            context.LineTo(points[0], false, false);
            for (int i = 1; i < count; i++)
            {
                if (steps) context.LineTo(new Point(points[i].X, points[i - 1].Y), false, false);
                context.LineTo(points[i], false, false);
            }
            context.LineTo(new Point(points[^1].X, area.Bottom), false, false);
        }
        line.Freeze(); under.Freeze();
        drawing.DrawGeometry(UI.Brush(MonitorDraw.Faint(color, steps ? .18 : .1)), null, under);
        drawing.DrawGeometry(null, Outline(color, steps ? 2 : 1.3, PenLineJoin.Miter), line);
        if (steps) return;
        var head = points[^1];
        drawing.DrawEllipse(UI.Brush(MonitorDraw.Faint(color, .3)), null, head, 4, 4);
        drawing.DrawEllipse(UI.Brush(color), null, head, 1.8, 1.8);
    }
}

// The marks drawn inside and beside the battery: round-capped pens made once, the bolt and the exclamation mark.
internal static class PowerMarks
{
    private static readonly Geometry BoltShape = Frozen("M13.5,0.5 L4.5,13.5 H11 L10,23.5 L19.5,9.5 H13 Z");
    private static Geometry Frozen(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    private static readonly Dictionary<(string Color, double Thickness), Pen> Pens = [];
    public static Pen Stroke(string color, double thickness)
    {
        if (Pens.TryGetValue((color, thickness), out var pen)) return pen;
        pen = new Pen(UI.Brush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        if (Pens.Count > 64) Pens.Clear();
        return Pens[(color, thickness)] = pen;
    }

    // A bolt as tall as the area, centred in it.
    public static void Bolt(DrawingContext drawing, Rect area, string color)
    {
        double scale = area.Height / 23;
        if (scale <= 0) return;
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, area.X + (area.Width - 15 * scale) / 2 - 4.5 * scale, area.Y - .5 * scale));
        drawing.DrawGeometry(UI.Brush(color), null, BoltShape);
        drawing.Pop();
    }

    // An exclamation mark centred in the area: a bar and a square dot of the same thickness, at least 1.5 DIP, all on whole device
    // pixels so the dot is as dark as the bar. halo: a device pixel of this color around them, setting them off the fill.
    public static void Exclamation(DrawingContext drawing, Rect area, string color, string? halo, double ppd)
    {
        double Snap(double value) => Math.Round(value * ppd) / ppd;
        double h = area.Height, thickness = Math.Max(Math.Ceiling(1.5 * ppd), Math.Round(h * .2 * ppd)) / ppd;
        double left = Snap(area.X + area.Width / 2 - thickness / 2), dotBottom = Snap(area.Y + h * .85), dotTop = dotBottom - thickness;
        double barBottom = dotTop - Math.Max(1 / ppd, Snap(h * .1)), barTop = Math.Min(Snap(area.Y + h * .15), barBottom - thickness);
        var bar = new Rect(left, barTop, thickness, barBottom - barTop);
        var dot = new Rect(left, dotTop, thickness, thickness);
        if (halo is not null)
        {
            var around = UI.Brush(halo);
            drawing.DrawRectangle(around, null, Rect.Inflate(bar, 1 / ppd, 1 / ppd));
            drawing.DrawRectangle(around, null, Rect.Inflate(dot, 1 / ppd, 1 / ppd));
        }
        var ink = UI.Brush(color);
        drawing.DrawRectangle(ink, null, bar);
        drawing.DrawRectangle(ink, null, dot);
    }
}
