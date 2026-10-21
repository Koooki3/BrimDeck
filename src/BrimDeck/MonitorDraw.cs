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
}

// Redraws the elements whose values are easing, while any is, at up to about 60 frames a second. Otherwise it is unhooked and
// the panel costs nothing between readings.
internal static class Motion
{
    private static readonly List<UIElement> Moving = [];
    private static bool _hooked;
    // Whether hover and press feedback eases; off with the app's animations. Readings say so with each update (motion).
    public static bool Enabled { get; set; } = true;
    private static TimeSpan _last, _tick;
    private static readonly Dictionary<UIElement, Func<double, bool>> Steps = [];

    // step(now) redraws the element and returns whether it still moves.
    public static void Start(UIElement element, Func<double, bool> step)
    {
        Steps[element] = step;
        if (!Moving.Contains(element)) Moving.Add(element);
        if (_hooked) return;
        _hooked = true; _last = _tick = TimeSpan.Zero; CompositionTarget.Rendering += Tick;
    }

    private static void Tick(object? sender, EventArgs e)
    {
        // About 60 frames a second, in whole display frames: every frame at 60 to 90 Hz, every other one at 120 or 144 Hz. That is
        // twice the old pace and smooth to the eye, at well under half the cost of following a 144 Hz display (every frame redraws
        // the moving gauges and presents the whole layered window). A frame is drawn when the next one would come later than
        // 60 a second asks, by more than four tenths of a display frame.
        if (e is RenderingEventArgs args)
        {
            var time = args.RenderingTime;
            if (time == _tick) return;
            double frame = Math.Clamp((time - _tick).TotalMilliseconds, 0, 34);
            _tick = time;
            if ((time - _last).TotalMilliseconds < 1000 / 60.0 - frame * .6) return;
            _last = time;
        }
        double now = Eased.Now;
        for (int i = Moving.Count - 1; i >= 0; i--)
        {
            var element = Moving[i];
            if (!Steps.TryGetValue(element, out var step) || !step(now)) { Moving.RemoveAt(i); Steps.Remove(element); }
        }
        if (Moving.Count > 0) return;
        _hooked = false; CompositionTarget.Rendering -= Tick;
    }
}
