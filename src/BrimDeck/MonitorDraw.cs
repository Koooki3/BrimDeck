using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// Drawing shared by the system monitor's page and its collapsed strip. The page draws itself in a few elements that render
// directly instead of a tree of text blocks and borders: a new reading changes what they draw, never the layout, so it costs
// one redraw of the panel and no measuring. The look follows ftop by Nongfsq (https://github.com/Nongfsq/ftop, MIT License;
// see ThirdPartyNotices.txt). The page and the collapsed strip lead their figures alike: CPU, memory and GPU with a dial
// carrying the reading's letter (Dial), the rates with an arrow (Arrow).
internal enum MonitorGlyph { Down, Up }

internal static class MonitorDraw
{
    // The arrows on a 16 unit grid, stroked.
    private static readonly Dictionary<MonitorGlyph, Geometry> Glyphs = new()
    {
        [MonitorGlyph.Down] = Frozen("M8,2.5 V13 M3.8,8.8 L8,13 L12.2,8.8"),
        [MonitorGlyph.Up] = Frozen("M8,13.5 V3 M3.8,7.2 L8,3 L12.2,7.2"),
    };
    private static Geometry Frozen(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

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

    // A dial: three quarters of a circle, from lower left over the top to lower right, on the quota rings' track, filled clockwise
    // by the share, with the reading's letter inside. The gap tells it from the full quota rings beside it in the collapsed row,
    // and a letter stays legible at that size where a glyph does not. Its centre sits a thirty-second of its size below the
    // centre given, so the open circle looks centred; the letter is centred on its own outline, not on the font's box.
    private const double DialStart = Math.PI * 1.25, DialSweep = Math.PI * 1.5;
    public static void Dial(DrawingContext drawing, Point center, double size, double ring, double letterSize, double? share, string color, string letter, double ppd)
    {
        double radius = size / 2 - ring / 2;
        var c = new Point(center.X, center.Y + size / 32);
        DialArc(drawing, c, radius, ring, 1, PanelTheme.Hex("ringTrack"));
        if (share is { } value && value > .002) DialArc(drawing, c, radius, ring, Math.Min(1, value), color);
        var text = Text(letter, letterSize, UI.Primary, ppd, strong: true);
        // Filled as its outline rather than drawn as text: WPF puts a glyph run's baseline on whole device pixels and hints the
        // glyph to them, which at a fractional scale left the letter up to a pixel above the ring's centre (0.7 px at 150 %). The
        // outline is rasterised exactly where it lies, as the ring is, so the two share their centre at any scale. Unhinted, it
        // comes out lighter than the text did (about two thirds of its ink at 150 %); a stroke around it gives the weight back
        // evenly on every side, so the centre stays. The text renderer darkens a letter by a fraction of a pixel that grows much
        // more slowly than the letter, so the stroke follows the square root of its size in device pixels (about half a pixel at
        // 150 %, and close to the text's weight up to 300 %); below 8 px it shrinks with the letter, which keeps the
        // counters of a 100 % letter open.
        var glyph = text.BuildGeometry(new Point());
        var bounds = glyph.Bounds;
        if (bounds.IsEmpty) return;
        double pixels = letterSize * ppd;
        var weight = new Pen(UI.Brush(UI.Primary), Math.Min(.06 * pixels, .17 * Math.Sqrt(pixels)) / ppd) { LineJoin = PenLineJoin.Round };
        weight.Freeze();
        drawing.PushTransform(new TranslateTransform(c.X - bounds.X - bounds.Width / 2, c.Y - bounds.Y - bounds.Height / 2));
        drawing.DrawGeometry(UI.Brush(UI.Primary), weight, glyph);
        drawing.Pop();
    }

    private static void DialArc(DrawingContext drawing, Point c, double radius, double thickness, double fraction, string color)
    {
        Point at(double angle) => new(c.X + radius * Math.Sin(angle), c.Y - radius * Math.Cos(angle));
        double sweep = DialSweep * fraction;
        var figure = new PathFigure { StartPoint = at(DialStart), IsClosed = false };
        figure.Segments.Add(new ArcSegment(at(DialStart + sweep), new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]); geometry.Freeze();
        var pen = new Pen(UI.Brush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
    }

    // A rate's arrow in the color, stroked, in a square of the size with its top left corner at the point.
    public static void Arrow(DrawingContext drawing, Point at, double size, double thickness, MonitorGlyph glyph, string color)
    {
        double scale = size / 16;
        var pen = new Pen(UI.Brush(color), thickness / scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, at.X, at.Y));
        drawing.DrawGeometry(null, pen, Glyphs[glyph]);
        drawing.Pop();
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
