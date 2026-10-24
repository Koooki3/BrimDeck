using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// Draws the notice glyphs (NoticeGlyphs in the core library) in the current panel theme: a glyph alone, an application-type
// glyph on an icon plate, and the small round badges on a plate's corners for a notice's state and its source. Colors come
// from NoticeGlyphColors for the theme in use, or from the system colors in high contrast, and are kept until the theme
// changes (PanelTheme.Generation); geometries, brushes and pens are frozen and shared, so a notice builds no new drawing
// resources once they exist. On a theme that glows rather than casts a shadow, a state badge gets a faint halo of its own
// color, drawn as a soft disc (no bitmap effect).
internal static class NoticeBadges
{
    private static readonly Dictionary<(NoticeGlyph, bool), Geometry> Geometries = [];
    private static readonly Dictionary<Color, SolidColorBrush> Brushes = [];
    private static readonly Dictionary<(Color, double), Pen> Pens = [];
    private static readonly Dictionary<Color, Brush> Halos = [];
    private static (int Generation, bool HighContrast, Color Highlight, NoticeGlyphColors Colors)? _colors;

    public static Geometry Geometry(NoticeGlyph glyph, bool badge = false)
    {
        if (Geometries.TryGetValue((glyph, badge), out var known)) return known;
        var geometry = System.Windows.Media.Geometry.Parse(badge ? NoticeGlyphs.BadgeData(glyph, out _) : NoticeGlyphs.PathData(glyph));
        geometry.Freeze();
        return Geometries[(glyph, badge)] = geometry;
    }

    // The glyph colors of the theme on screen, or of the high contrast scheme.
    public static NoticeGlyphColors Colors
    {
        get
        {
            bool high = SystemParameters.HighContrast;
            var highlight = SystemColors.HighlightColor;
            if (_colors is { } known && known.Generation == PanelTheme.Generation && known.HighContrast == high && (!high || known.Highlight == highlight)) return known.Colors;
            if (Brushes.Count > 96) { Brushes.Clear(); Pens.Clear(); Halos.Clear(); }
            var colors = high
                ? NoticeGlyphColors.FromSystem(Theme(SystemColors.WindowColor), Theme(SystemColors.WindowTextColor), Theme(SystemColors.ControlColor), Theme(highlight), Theme(SystemColors.HighlightTextColor))
                : NoticeGlyphColors.FromPalette(PanelTheme.Current);
            _colors = (PanelTheme.Generation, high, highlight, colors);
            return colors;
        }
    }

    private static ThemeColor Theme(Color color) => new(color.A, color.R, color.G, color.B);

    internal static SolidColorBrush Brush(Color color)
    {
        if (Brushes.TryGetValue(color, out var brush)) return brush;
        brush = new SolidColorBrush(color); brush.Freeze();
        return Brushes[color] = brush;
    }

    internal static Pen Pen(Color color, double width)
    {
        if (Pens.TryGetValue((color, width), out var pen)) return pen;
        pen = new Pen(Brush(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return Pens[(color, width)] = pen;
    }

    internal static Brush Halo(Color color)
    {
        if (Halos.TryGetValue(color, out var halo)) return halo;
        var brush = new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(140, color.R, color.G, color.B), .55), new(Color.FromArgb(0, color.R, color.G, color.B), 1)
        });
        brush.Freeze();
        return Halos[color] = brush;
    }

    // A glyph alone, size DIP square, in a color; named for screen readers when a name is given.
    public static FrameworkElement Glyph(NoticeGlyph glyph, double size, Color color, string? name = null)
    {
        var element = new NoticeGlyphElement { Glyph = glyph, Ink = color, Width = size, Height = size, IsHitTestVisible = false };
        if (name is not null) AutomationProperties.SetName(element, name);
        return element;
    }

    // The application-type glyph for an application without an icon, or null when its type is unknown (the initial is shown).
    public static FrameworkElement? AppGlyph(Notice notice, double plateSize, Color appColor)
    {
        if (NoticeGlyphs.App(notice.AppKey, notice.AppName, notice.Origin) is not { } glyph) return null;
        var color = PanelTheme.ToColor(Colors.OnPlate(Theme(appColor)));
        var element = Glyph(glyph, Math.Round(plateSize * .66), color);
        element.HorizontalAlignment = HorizontalAlignment.Center; element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    // The state badge for the plate's lower right corner: the state color with its mark in the surface color, set off from
    // the icon by a ring of the surface. Null for a plain message, and for a task, whose progress shows elsewhere.
    public static FrameworkElement? Status(Notice notice, double plateSize, bool glow, string? kind = null)
    {
        // A time notice (alarm, stopwatch, countdown, mail, event) shows its kind; the glyph names match NoticeTimeKind's.
        if (kind is null && notice.TimeKind != NoticeTimeKind.None) kind = notice.TimeKind.ToString();
        if (kind is null && notice.Call) kind = "call";
        if (notice.Category == NoticeCategory.Task && kind is null) return null;
        if (NoticeGlyphs.Status(notice.Category, kind) is not { } glyph) return null;
        var colors = Colors;
        double size = Math.Max(12, Math.Round(plateSize * .4));
        var badge = new NoticeGlyphElement
        {
            Glyph = glyph, Badge = true, Ink = PanelTheme.ToColor(colors.BadgeInk), Disc = PanelTheme.ToColor(colors.Tone(NoticeGlyphs.Tone(glyph))), Ring = PanelTheme.ToColor(colors.Surface),
            Glowing = glow && !colors.HighContrast, Width = size, Height = size, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -size * .3, -size * .3)
        };
        AutomationProperties.SetName(badge, NoticeGlyphs.Name(glyph));
        return badge;
    }

    // Adds the state and source badges to an icon plate. shown is the glyph the plate itself shows, if any.
    public static void Decorate(System.Windows.Controls.Panel plate, Notice notice, double plateSize, bool glow, NoticeGlyph? shown)
    {
        if (Source(notice, plateSize, shown) is { } source) plate.Children.Add(source);
        if (Status(notice, plateSize, glow) is { } status) plate.Children.Add(status);
    }

    // The source badge for the plate's lower left corner, for a notice that did not come from the Windows application itself
    // (WSL, brim-notify, a phone, a web app). Left out when the plate already shows that glyph.
    public static FrameworkElement? Source(Notice notice, double plateSize, NoticeGlyph? shown)
    {
        if (NoticeGlyphs.Source(notice.Origin, notice.AppKey) is not { } glyph || glyph == shown) return null;
        var colors = Colors;
        double size = Math.Max(12, Math.Round(plateSize * .4));
        var badge = new NoticeGlyphElement
        {
            Glyph = glyph, Badge = true, Ink = PanelTheme.ToColor(colors.Secondary), Disc = PanelTheme.ToColor(colors.Plate), Ring = PanelTheme.ToColor(colors.Surface), Width = size, Height = size, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-size * .3, 0, 0, -size * .3)
        };
        AutomationProperties.SetName(badge, NoticeGlyphs.Name(glyph));
        return badge;
    }
}

// One glyph, drawn directly: alone, or as a badge on a disc with a ring and, on a glowing theme, a halo. The stroke keeps at
// least about 1.1 DIP at small sizes so the glyph stays legible, and grows no heavier than 2.6 DIP at large ones.
internal sealed class NoticeGlyphElement : FrameworkElement
{
    public NoticeGlyph Glyph { get; init; }
    public bool Badge { get; init; }
    public Color Ink { get; init; }
    public Color? Disc { get; init; }
    public Color? Ring { get; init; }
    public bool Glowing { get; init; }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsNaN(Width) ? 0 : Width, double.IsNaN(Height) ? 0 : Height);

    protected override void OnRender(DrawingContext drawing)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double radius = size / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        NoticeGlyphs.BadgeData(Glyph, out bool mark);
        bool asMark = Badge && mark;
        double glyphSize = size;
        if (Badge && Disc is { } disc)
        {
            if (Glowing) drawing.DrawEllipse(NoticeBadges.Halo(disc), null, center, radius + 3, radius + 3);
            // The ring is the surface around the disc, so the badge reads apart from the icon under it.
            if (Ring is { } ring) drawing.DrawEllipse(NoticeBadges.Brush(ring), null, center, radius + 1.5, radius + 1.5);
            drawing.DrawEllipse(NoticeBadges.Brush(disc), null, center, radius, radius);
            // A bare mark keeps clear of the disc's edge; a whole glyph (a source) fills more of it to stay legible.
            glyphSize = size * (asMark ? .78 : .84);
        }
        double scale = glyphSize / NoticeGlyphs.Grid;
        double stroke = Math.Clamp((asMark ? NoticeGlyphs.MarkStroke : NoticeGlyphs.Stroke) * scale, Badge ? 1 : 1.1, 2.6);
        drawing.PushTransform(new TranslateTransform(center.X - glyphSize / 2, center.Y - glyphSize / 2));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        drawing.DrawGeometry(null, NoticeBadges.Pen(Ink, Math.Round(stroke / scale, 3)), NoticeBadges.Geometry(Glyph, Badge));
        drawing.Pop(); drawing.Pop();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GlyphPeer(this);

    private sealed class GlyphPeer(NoticeGlyphElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(NoticeGlyphElement);
        protected override bool IsContentElementCore() => !string.IsNullOrEmpty(AutomationProperties.GetName(Owner));
    }
}
