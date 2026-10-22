using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// How an application's color shows in a theme: a dot by the icon (classic), a tint behind the icon (glass), the outline and
// one pulse of light (neon), a paper tag at the side (paper), or a square frame around the icon (pixel).
internal enum NoticeAccentStyle { Dot, Mist, Edge, Tag, Frame }
// How the shape moves: eased (classic, glass), eased with one pulse on arrival (neon), eased with the content sliding a
// short way (paper), or in whole steps (pixel).
internal enum NoticeMotion { Smooth, Pulse, Slide, Steps }

// The colors and materials of one notice theme in one appearance. Every theme shows the same parts in the same places;
// only these values differ. High contrast replaces them all with system colors and drops transparency and glow.
internal sealed record NoticeLook(
    NoticeTheme Theme, bool Dark, Brush Fill, Brush? Sheen, Brush? Pattern, Brush Rim, double RimWidth,
    Color Shadow, double ShadowBlur, double ShadowDepth, double ShadowOpacity,
    string Primary, string Secondary, string Tertiary, Brush Plate, Brush Button, Brush ButtonHover, Brush PrimaryButton, string PrimaryButtonText,
    NoticeAccentStyle Accent, NoticeMotion Motion, bool Stepped, bool HighContrast)
{
    // The real island under a notice: black, or the indicator's grey. A notice starts and ends in this color.
    public static readonly Color Island = Colors.Black;

    public static bool IsDark(NoticeAppearance appearance, SettingsTheme settingsTheme) => appearance switch
    {
        NoticeAppearance.Dark => true, NoticeAppearance.Light => false, _ => SettingsPalette.IsDark(settingsTheme)
    };

    public static NoticeLook For(NoticeTheme theme, bool dark)
    {
        if (SystemParameters.HighContrast) return HighContrastLook(theme);
        return theme switch
        {
            NoticeTheme.Glass => dark
                ? new(theme, true, Solid("#CC1F2026"), GlassSheen("#26FFFFFF"), null, Solid("#38FFFFFF"), 1, Colors.Black, 26, 6, .34,
                    "#F5F5F7", "#B3EBEBF5", "#73EBEBF5", Solid("#1FFFFFFF"), Solid("#1AFFFFFF"), Solid("#2EFFFFFF"), Solid("#E6F5F5F7"), "#1C1C1E", NoticeAccentStyle.Mist, NoticeMotion.Smooth, false, false)
                : new(theme, false, Solid("#D9F6F6F9"), GlassSheen("#B3FFFFFF"), null, Solid("#80FFFFFF"), 1, Color.FromRgb(0x20, 0x24, 0x30), 26, 6, .2,
                    "#1C1C1E", "#9E1C1C1E", "#6B1C1C1E", Solid("#14000000"), Solid("#12000000"), Solid("#21000000"), Solid("#E61C1C1E"), "#F5F5F7", NoticeAccentStyle.Mist, NoticeMotion.Smooth, false, false),
            NoticeTheme.Neon => dark
                ? new(theme, true, Solid("#F5060912"), null, ScanLines("#0AFFFFFF"), Solid("#5C7CF3FF"), 1.5, Color.FromRgb(0x5C, 0xE1, 0xE6), 18, 0, .5,
                    "#EAF6FF", "#A6BFD6E8", "#7380A4BC", Solid("#14FFFFFF"), Solid("#147CF3FF"), Solid("#2E7CF3FF"), Solid("#E65CE1E6"), "#04131A", NoticeAccentStyle.Edge, NoticeMotion.Pulse, false, false)
                : new(theme, false, Solid("#F7F3F6FB"), null, ScanLines("#08000000"), Solid("#663A6BFF"), 1.5, Color.FromRgb(0x3A, 0x6B, 0xFF), 16, 0, .28,
                    "#0B1220", "#A60B1220", "#730B1220", Solid("#0F0B1220"), Solid("#123A6BFF"), Solid("#243A6BFF"), Solid("#E61F4FD8"), "#FFFFFF", NoticeAccentStyle.Edge, NoticeMotion.Pulse, false, false),
            NoticeTheme.Paper => dark
                ? new(theme, true, Solid("#FF2A2724"), null, Grain("#0DFFFFFF"), Solid("#1AFFFFFF"), 1, Colors.Black, 14, 4, .4,
                    "#F1EBE2", "#ADF1EBE2", "#78F1EBE2", Solid("#14FFFFFF"), Solid("#14FFFFFF"), Solid("#24FFFFFF"), Solid("#FFE8DCC8"), "#2A2724", NoticeAccentStyle.Tag, NoticeMotion.Slide, false, false)
                : new(theme, false, Solid("#FFFBF7EE"), null, Grain("#0F6B5B45"), Solid("#226B5B45"), 1, Color.FromRgb(0x5A, 0x46, 0x32), 14, 4, .22,
                    "#2B2520", "#A32B2520", "#702B2520", Solid("#0F6B5B45"), Solid("#106B5B45"), Solid("#1F6B5B45"), Solid("#FF3D342C"), "#FBF7EE", NoticeAccentStyle.Tag, NoticeMotion.Slide, false, false),
            NoticeTheme.Pixel => dark
                ? new(theme, true, Solid("#FF14141C"), null, null, Solid("#FF4A4A66"), 2, Colors.Black, 0, 3, .65,
                    "#F0F0F4", "#FFA8A8B8", "#FF70708A", Solid("#FF22222E"), Solid("#FF262634"), Solid("#FF34344A"), Solid("#FFE8E8F0"), "#14141C", NoticeAccentStyle.Frame, NoticeMotion.Steps, true, false)
                : new(theme, false, Solid("#FFF4F1E6"), null, null, Solid("#FF2B2B30"), 2, Color.FromRgb(0x2B, 0x2B, 0x30), 0, 3, .35,
                    "#1E1E22", "#FF55555E", "#FF85858E", Solid("#FFE4E0D2"), Solid("#FFE8E4D6"), Solid("#FFD8D4C4"), Solid("#FF1E1E22"), "#F4F1E6", NoticeAccentStyle.Frame, NoticeMotion.Steps, true, false),
            // Classic: the island's own black in either appearance; the island is black whatever the theme of the panel.
            _ => new(NoticeTheme.Classic, true, Solid("#FF000000"), null, null, Solid("#12FFFFFF"), 1, Colors.Black, 14, 4, .28,
                UI.Primary, UI.Secondary, UI.Tertiary, Solid("#14FFFFFF"), Solid("#14FFFFFF"), Solid("#24FFFFFF"), Solid("#E6F5F5F7"), "#000000", NoticeAccentStyle.Dot, NoticeMotion.Smooth, false, false)
        };
    }

    private static NoticeLook HighContrastLook(NoticeTheme theme)
    {
        static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        var window = SystemColors.WindowColor;
        bool dark = (window.R * 299 + window.G * 587 + window.B * 114) / 1000 < 128;
        return new(theme, dark, SystemColors.WindowBrush, null, null, SystemColors.WindowTextBrush, 2, Colors.Transparent, 0, 0, 0,
            Hex(SystemColors.WindowTextColor), Hex(SystemColors.WindowTextColor), Hex(SystemColors.GrayTextColor),
            SystemColors.WindowBrush, SystemColors.ControlBrush, SystemColors.HighlightBrush, SystemColors.HighlightBrush, Hex(SystemColors.HighlightTextColor),
            NoticeAccentStyle.Dot, NoticeMotion.Smooth, theme == NoticeTheme.Pixel, true);
    }

    private static SolidColorBrush Solid(string color) => (SolidColorBrush)UI.Brush(color);

    // A soft highlight across the top third, as light falling on glass.
    private static Brush GlassSheen(string color)
    {
        var top = (Color)ColorConverter.ConvertFromString(color);
        var brush = new LinearGradientBrush(new GradientStopCollection { new(top, 0), new(Color.FromArgb(0, top.R, top.G, top.B), .45) }, new Point(0, 0), new Point(0, 1));
        brush.Freeze(); return brush;
    }

    // Fine horizontal lines, as on an instrument display.
    private static Brush ScanLines(string color)
    {
        var drawing = new GeometryDrawing(UI.Brush(color), null, new RectangleGeometry(new Rect(0, 0, 4, 1)));
        var brush = new DrawingBrush(new DrawingGroup { Children = { new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 4, 3))), drawing } })
        { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 4, 3), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        brush.Freeze(); return brush;
    }

    // A faint, irregular grain, as on paper. The tile is large enough that its repetition does not show.
    private static Brush Grain(string color)
    {
        var dots = new GeometryGroup();
        var random = new Random(7);
        for (int i = 0; i < 90; i++) dots.Children.Add(new EllipseGeometry(new Point(random.NextDouble() * 48, random.NextDouble() * 48), .45 + random.NextDouble() * .4, .45 + random.NextDouble() * .4));
        var group = new DrawingGroup { Children = { new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 48, 48))), new GeometryDrawing(UI.Brush(color), null, dots) } };
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 48, 48), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        brush.Freeze(); return brush;
    }

    public static string CategoryColor(Core.Notices.NoticeCategory category) => category switch
    {
        Core.Notices.NoticeCategory.Success => "#34C759", Core.Notices.NoticeCategory.Error => "#FF453A",
        Core.Notices.NoticeCategory.Warning => "#FFB340", Core.Notices.NoticeCategory.Task => "#0A84FF", _ => ""
    };
}

// The notice's shape: the island's outline (shoulders and corners as IslandBorder draws them), or for the pixel theme the
// same box with stair-stepped corners on whole device pixels. It draws the theme's fill, sheen, pattern and rim, and on top
// of them the island's own color, which fades in and out at the ends of the morph so a notice leaves the island and returns
// to it without a visible seam. Its child is clipped to the outline.
internal sealed class NoticeSurface : System.Windows.Controls.Decorator
{
    public static readonly DependencyProperty TopRadiusProperty = Register(nameof(TopRadius), 0d);
    public static readonly DependencyProperty BottomRadiusProperty = Register(nameof(BottomRadius), 0d);
    public static readonly DependencyProperty ShoulderProperty = Register(nameof(Shoulder), 0d);
    public static readonly DependencyProperty IslandOpacityProperty = Register(nameof(IslandOpacity), 0d);
    public static readonly DependencyProperty IslandColorProperty = DependencyProperty.Register(nameof(IslandColor), typeof(Color), typeof(NoticeSurface),
        new FrameworkPropertyMetadata(Colors.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(name, typeof(double), typeof(NoticeSurface),
        new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double TopRadius { get => (double)GetValue(TopRadiusProperty); set => SetValue(TopRadiusProperty, value); }
    public double BottomRadius { get => (double)GetValue(BottomRadiusProperty); set => SetValue(BottomRadiusProperty, value); }
    public double Shoulder { get => (double)GetValue(ShoulderProperty); set => SetValue(ShoulderProperty, value); }
    public double IslandOpacity { get => (double)GetValue(IslandOpacityProperty); set => SetValue(IslandOpacityProperty, value); }
    public Color IslandColor { get => (Color)GetValue(IslandColorProperty); set => SetValue(IslandColorProperty, value); }
    public NoticeLook? Look { get => _look; set { _look = value; _rim = null; InvalidateVisual(); } }
    // An accent outline replacing the theme's rim (the neon theme's), or null.
    public Brush? AccentRim { get => _accentRim; set { _accentRim = value; _rim = null; InvalidateVisual(); } }

    private NoticeLook? _look;
    private Brush? _accentRim;
    private Pen? _rim;
    private SolidColorBrush? _islandBrush;
    private (Size, double, double, double, bool)? _key;
    private Geometry _outline = Geometry.Empty;

    public Geometry Outline()
    {
        var key = (RenderSize, TopRadius, BottomRadius, Shoulder, _look?.Stepped == true);
        if (_key == key) return _outline;
        _key = key;
        double w = RenderSize.Width, h = RenderSize.Height;
        double dpi = PresentationSource.FromVisual(this) is { } source ? source.CompositionTarget.TransformToDevice.M11 : 1;
        return _outline = key.Item5 ? Stepped(w, h, TopRadius, BottomRadius, Math.Max(2, Math.Round(2 * dpi) / dpi))
            : IslandBorder.BuildOutline(w, h, Shoulder, new CornerRadius(TopRadius, TopRadius, BottomRadius, BottomRadius));
    }

    // A box whose corners are stairs of the given step; a radius of 0 (the top of an attached notch) keeps a square corner.
    internal static Geometry Stepped(double w, double h, double top, double bottom, double step)
    {
        if (!(w > 0) || !(h > 0)) return Geometry.Empty;
        int Steps(double radius) => (int)Math.Clamp(Math.Round(Math.Min(radius, Math.Min(w, h) / 2) / step / 2), 0, 4);
        // The top-left stair, from the left edge up to the top edge; the other corners are its mirror images, walked clockwise.
        static List<Point> Stair(int count, double size)
        {
            var stair = new List<Point> { new(0, count * size) };
            for (int i = 0; i < count; i++) { stair.Add(new(i * size, (count - 1 - i) * size)); stair.Add(new((i + 1) * size, (count - 1 - i) * size)); }
            return stair;
        }
        var t = Stair(Steps(top), step);
        var b = Stair(Steps(bottom), step);
        var points = new List<Point>(t);
        points.AddRange(Enumerable.Reverse(t).Select(p => new Point(w - p.X, p.Y)));
        points.AddRange(b.Select(p => new Point(w - p.X, h - p.Y)));
        points.AddRange(Enumerable.Reverse(b).Select(p => new Point(p.X, h - p.Y)));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            context.PolyLineTo(points.Skip(1).ToList(), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    protected override Geometry GetLayoutClip(Size layoutSlotSize) => Outline();

    protected override void OnRender(DrawingContext drawing)
    {
        var outline = Outline();
        if (outline.IsEmpty() || _look is not { } look) return;
        drawing.DrawGeometry(look.Fill, null, outline);
        if (look.Sheen is { } sheen) drawing.DrawGeometry(sheen, null, outline);
        if (look.Pattern is { } pattern) drawing.DrawGeometry(pattern, null, outline);
        if (look.RimWidth > 0)
        {
            // The clip removes the outer half of the stroke, so the pen is twice the rim's width.
            _rim ??= Freeze(new Pen(_accentRim ?? look.Rim, look.RimWidth * 2));
            drawing.DrawGeometry(null, _rim, outline);
        }
        if (IslandOpacity > 0)
        {
            if (_islandBrush is null || _islandBrush.Color != IslandColor) _islandBrush = (SolidColorBrush)Freeze(new SolidColorBrush(IslandColor));
            drawing.PushOpacity(Math.Clamp(IslandOpacity, 0, 1));
            drawing.DrawGeometry(_islandBrush, null, outline);
            drawing.Pop();
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
}
