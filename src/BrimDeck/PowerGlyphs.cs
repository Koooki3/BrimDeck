using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The color and shapes of the power reading on the panel theme in use: the theme's look (PowerLooks) resolved against
// the monitor's color scheme and the theme's tokens. Made once per theme and scheme.
internal sealed class PowerStyle
{
    public PowerLook Look { get; }
    // The color of the power curve and the bolt badge.
    public string Draw { get; }

    private PowerStyle(PowerLook look, MonitorColors colors)
    {
        Look = look;
        Draw = look.Draw switch
        {
            "cpu" => colors.Performance, "efficiency" => colors.Efficiency, "memory" => colors.Memory, "gpu" => colors.Gpu,
            "warning" => colors.Warning, "critical" => colors.Critical, var name => PanelTheme.Hex(name)
        };
    }

    private static (int Generation, bool HighContrast, MonitorColors Colors, PowerStyle Style)? _last;
    public static PowerStyle For(MonitorColors colors)
    {
        if (_last is { } known && known.Generation == PanelTheme.Generation && known.HighContrast == PanelTheme.HighContrast && known.Colors == colors) return known.Style;
        var style = new PowerStyle(PowerLooks.For(PanelTheme.Current, PanelTheme.HighContrast), colors);
        _last = (PanelTheme.Generation, PanelTheme.HighContrast, colors, style);
        return style;
    }
}

// The drawings of the monitor page: the bolt badge and the power curve, each following the look of the theme (PowerLook).
internal static class PowerGlyphs
{
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

    // The badge before the power figure: a bolt in a ring (a hexagon for the HUD look, a square for blocks), on the quota rings' track.
    public static void Badge(DrawingContext drawing, Point center, double size, string color, bool known, PowerStyle style)
    {
        double ring = Math.Max(1.2, size / 10), r = size / 2 - ring / 2;
        var track = Outline(PanelTheme.Hex("ringTrack"), ring, PenLineJoin.Round);
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
