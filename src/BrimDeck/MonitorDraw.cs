using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// Drawing shared by the system monitor's page and its collapsed strip. The page draws itself in a few elements that render
// directly instead of a tree of text blocks and borders: a new reading changes what they draw, never the layout, so it costs
// one redraw of the panel and no measuring. The look follows ftop by Nongfsq (https://github.com/Nongfsq/ftop, MIT License;
// see ThirdPartyNotices.txt): a round badge with a glyph leads every figure, and a badge with an arc is a gauge.
internal enum MonitorGlyph { Cpu, Gpu, Memory, Down, Up }

internal static class MonitorDraw
{
    // The glyphs on a 16 unit grid, stroked.
    private static readonly Dictionary<MonitorGlyph, Geometry> Glyphs = new()
    {
        [MonitorGlyph.Cpu] = Frozen("M5,4 H11 Q12,4 12,5 V11 Q12,12 11,12 H5 Q4,12 4,11 V5 Q4,4 5,4 Z M6.5,1.5 V4 M9.5,1.5 V4 M6.5,12 V14.5 M9.5,12 V14.5 M1.5,6.5 H4 M1.5,9.5 H4 M12,6.5 H14.5 M12,9.5 H14.5"),
        [MonitorGlyph.Gpu] = Frozen("M1.5,4.5 H14.5 V11.5 H1.5 Z M4,11.5 V14 M7,11.5 V14 M10,6 A2,2 0 1 1 9.99,6 M10,8 V8.01"),
        [MonitorGlyph.Memory] = Frozen("M1.5,5 H14.5 V11 H1.5 Z M4,11 V13.5 M7,11 V13.5 M10,11 V13.5 M13,11 V13.5 M4.5,7.5 V8.5 M8,7.5 V8.5 M11.5,7.5 V8.5"),
        [MonitorGlyph.Down] = Frozen("M8,2.5 V13 M3.8,8.8 L8,13 L12.2,8.8"),
        [MonitorGlyph.Up] = Frozen("M8,13.5 V3 M3.8,7.2 L8,3 L12.2,7.2"),
    };
    private static Geometry Frozen(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }
    public static Geometry Glyph(MonitorGlyph glyph) => Glyphs[glyph];

    // Text set once per string, size, weight and color: the same figures come back every second, so they are not laid out again.
    private static readonly Dictionary<(string, double, bool, bool, string, double), FormattedText> Texts = [];
    private static readonly Typeface Regular = new(UI.PanelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Strong = new(UI.PanelFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Display = new(UI.DisplayFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static FormattedText Text(string text, double size, string color, double pixelsPerDip, bool strong = false, bool display = false)
    {
        var key = (text, size, strong, display, color, pixelsPerDip);
        if (Texts.TryGetValue(key, out var known)) return known;
        if (Texts.Count > 768) Texts.Clear();
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, display ? Display : strong ? Strong : Regular,
            size, UI.Brush(color), pixelsPerDip);
        return Texts[key] = formatted;
    }

    // One line cut with an ellipsis to the width; not cached, as the width varies.
    public static FormattedText Trimmed(string text, double size, string color, double width, double pixelsPerDip, bool strong = false)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, strong ? Strong : Regular, size, UI.Brush(color), pixelsPerDip)
        { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

    // The text's baseline placed at y, so figures of different sizes on one line sit on one baseline.
    public static void Baseline(DrawingContext drawing, FormattedText text, double x, double baseline) => drawing.DrawText(text, new Point(x, baseline - text.Baseline));

    // A badge: a faint disc with the glyph in the color, and, as a gauge, a ring around it whose arc is the share, from the top
    // clockwise as on ftop.
    public static void Badge(DrawingContext drawing, Point center, double size, MonitorGlyph glyph, string color, double? share)
    {
        double radius = size / 2;
        drawing.DrawEllipse(UI.Brush(UI.Fill), null, center, radius, radius);
        if (share is { } fraction)
        {
            double ring = Math.Max(1.2, size * .09), r = radius - ring / 2;
            var track = new Pen(UI.Brush(UI.Track), ring); track.Freeze();
            drawing.DrawEllipse(null, track, center, r, r);
            Arc(drawing, center, r, ring, Math.Clamp(fraction, 0, 1), color);
        }
        double glyphSize = size * .52, scale = glyphSize / 16;
        var pen = new Pen(UI.Brush(color), 1.5 / scale * Math.Max(.85, size / 22)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, center.X - glyphSize / 2, center.Y - glyphSize / 2));
        drawing.DrawGeometry(null, pen, Glyphs[glyph]);
        drawing.Pop();
    }

    // An arc of a circle from the top, clockwise, round at its ends.
    public static void Arc(DrawingContext drawing, Point center, double radius, double thickness, double fraction, string color)
    {
        if (fraction <= .002) return;
        var pen = new Pen(UI.Brush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        if (fraction >= .999) { drawing.DrawEllipse(null, pen, center, radius, radius); return; }
        double angle = fraction * Math.PI * 2;
        var figure = new PathFigure { StartPoint = new Point(center.X, center.Y - radius), IsClosed = false };
        figure.Segments.Add(new ArcSegment(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)), new Size(radius, radius), 0,
            fraction > .5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]); geometry.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
    }

    // A color made fainter, for the second segment of a bar and the fill under a sparkline.
    public static string Faint(string color, double alpha)
    {
        var parsed = ThemeColor.Parse(color);
        return parsed.WithAlpha((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255)).ToString();
    }

    public static double PixelsPerDip(Visual visual) => VisualTreeHelper.GetDpi(visual).PixelsPerDip;

    // A cached drawing, drawn at its origin, placed at a point.
    public static void Place(DrawingContext drawing, Point at, Drawing cached)
    {
        drawing.PushTransform(new TranslateTransform(at.X, at.Y));
        drawing.DrawDrawing(cached);
        drawing.Pop();
    }

    // The last minute of readings as a smooth line over a faint fill, newest at the right edge of the area, each reading's place
    // fixed (SystemSampler.HistoryLength places across the area) so the line moves along as readings come. top is the reading
    // that reaches the top of the area. Colors come from the monitor's palette, so the line suits every theme.
    public static void Sparkline(DrawingContext drawing, Rect area, IReadOnlyList<double> history, double top, string color, double fill, double stroke = 1.4)
    {
        int count = history.Count;
        if (count < 3 || area.Width < 8 || area.Height < 4 || !(top > 0)) return;
        double step = area.Width / (Native.SystemSampler.HistoryLength - 1);
        var points = new Point[count];
        for (int i = 0; i < count; i++)
            points[i] = new Point(area.Right - (count - 1 - i) * step, area.Bottom - 1 - Math.Clamp(history[i] / top, 0, 1) * (area.Height - 2));
        var line = new StreamGeometry();
        var under = new StreamGeometry();
        using (var context = line.Open()) Curve(context, points, false, area.Bottom);
        using (var context = under.Open()) Curve(context, points, true, area.Bottom);
        line.Freeze(); under.Freeze();
        if (fill > 0) drawing.DrawGeometry(UI.Brush(Faint(color, fill)), null, under);
        var pen = new Pen(UI.Brush(color), stroke) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        drawing.DrawGeometry(null, pen, line);
    }

    // Catmull-Rom through the points, as cubic Béziers; closed down to the bottom for the fill.
    private static void Curve(StreamGeometryContext context, Point[] p, bool closed, double bottom)
    {
        context.BeginFigure(closed ? new Point(p[0].X, bottom) : p[0], closed, closed);
        if (closed) context.LineTo(p[0], false, false);
        for (int i = 0; i < p.Length - 1; i++)
        {
            Point a = p[Math.Max(0, i - 1)], b = p[i], c = p[i + 1], d = p[Math.Min(p.Length - 1, i + 2)];
            var c1 = new Point(b.X + (c.X - a.X) / 6, b.Y + (c.Y - a.Y) / 6);
            var c2 = new Point(c.X - (d.X - b.X) / 6, c.Y - (d.Y - b.Y) / 6);
            // A curve may not overshoot the bottom of its area.
            c1.Y = Math.Min(c1.Y, bottom); c2.Y = Math.Min(c2.Y, bottom);
            context.BezierTo(c1, c2, c, true, true);
        }
        if (closed) context.LineTo(new Point(p[^1].X, bottom), false, false);
    }
}

// A drawing kept until what it shows changes. Text is the costly part of a redraw (each DrawText lays its glyphs out again), and
// during a move only the bars and arcs change: the text is drawn once per reading into a frozen drawing, which every frame
// then places as a whole. The key is a value tuple of what the drawing shows, compared without allocating.
internal sealed class DrawingCache<TKey> where TKey : struct, IEquatable<TKey>
{
    private TKey _key;
    private DrawingGroup? _drawing;

    public Drawing Get(TKey key, Action<DrawingContext> draw)
    {
        if (_drawing is not null && key.Equals(_key)) return _drawing;
        var group = new DrawingGroup();
        using (var context = group.Open()) draw(context);
        group.Freeze();
        _drawing = group; _key = key;
        return group;
    }
}
