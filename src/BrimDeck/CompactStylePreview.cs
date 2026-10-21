using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// A miniature of the collapsed surface on a desktop, for the collapsed view's style cards and the panel theme cards. Its content is
// fixed (four quota rings, and the clock when asked), so it reads as a style or a theme rather than as live data. Without a
// palette it always takes the built-in dark theme's colors: the style cards show the shapes and must not change when another
// panel theme is chosen. The theme cards pass each theme's palette and ask for focus: the same scene with the surface enlarged
// in the middle, so its colors can be told apart.
internal static class CompactStylePreview
{
    private const double Width = 240, Height = 136;
    private static readonly DrawingBrush DarkDesktop = CreateDesktop(true);
    private static readonly DrawingBrush LightDesktop = CreateDesktop(false);
    private static PanelPalette? _fixed;
    private static PanelPalette Fixed => _fixed ??= PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!);
    private const double Focus = 2.6;

    public static FrameworkElement Create(CompactStyle style, bool dark = true, bool clock = false, PanelPalette? palette = null, bool focus = false)
    {
        palette ??= Fixed;
        string Hex(string key) => palette[key].ToString();
        // Every option uses the same scene and scale so only the panel's shape changes.
        var screen = new Grid
        {
            Width = Width, Height = Height, Background = dark ? DarkDesktop : LightDesktop, IsHitTestVisible = false,
            Clip = new RectangleGeometry(new Rect(0, 0, Width, Height), 9, 9)
        };
        if (style == CompactStyle.Line)
        {
            screen.Children.Add(new Border { Background = UI.Brush(Hex("indicator")), Width = 29, Height = 2.5,
                CornerRadius = new CornerRadius(1.25), Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top });
        }
        else
        {
            bool notch = style == CompactStyle.Notch;
            IslandBorder Shape() => new()
            {
                Background = UI.Brush(Hex("surface")), MaterialOpacity = 1,
                Width = notch ? 100 : 82, Height = 14, ShoulderRadius = notch ? 6.7 : 0,
                CornerRadius = notch ? new CornerRadius(0, 0, 5, 5) : new CornerRadius(7),
                Margin = new Thickness(0, notch ? 0 : 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top
            };
            // The surface, its layers, the rim and the shadow or glow come from the palette, as on the real island. A card never
            // changes, so the blur is kept as a bitmap instead of being drawn again whenever the page scrolls.
            var glow = new System.Windows.Media.Effects.DropShadowEffect { Direction = 270 };
            PanelTheme.Dress(glow, palette); glow.Freeze();
            var shadow = Shape(); shadow.Effect = glow; shadow.CacheMode = new BitmapCache { SnapsToDevicePixels = true, RenderAtScale = focus ? Focus : 1 };
            // Enlarged about the top centre and brought down to the middle of the scene.
            var surface = new Grid();
            if (focus) surface.RenderTransform = new TransformGroup { Children = { new ScaleTransform(Focus, Focus, Width / 2, 0), new TranslateTransform(0, 42) } };
            surface.Children.Add(shadow);
            screen.Children.Add(surface);
            var island = Shape();
            PanelTheme.Dress(island, palette);
            var detail = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (app, percent) in new[] { (ProviderId.Claude, 47d), (ProviderId.Codex, 23), (ProviderId.Antigravity, 16), (ProviderId.Cursor, 37) })
            {
                var ring = UI.Ring(percent, palette.AppColor(app).ToString(), 4.6, .9, Hex("ringTrack")); ring.Margin = new Thickness(0, 0, 3.4, 0); detail.Children.Add(ring);
            }
            detail.Margin = new Thickness(3.4, 0, 0, 0);
            if (clock)
            {
                // With the clock on, the time sits at the right end and the rings take the rest, as on the real island.
                var row = new DockPanel { Margin = new Thickness(notch ? 11 : 7, 0, notch ? 11 : 7, 0), VerticalAlignment = VerticalAlignment.Center };
                var time = new TextBlock { Text = "14:32", FontFamily = UI.PanelFont, FontSize = 5.5, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(Hex("clock")), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
                DockPanel.SetDock(time, Dock.Right); row.Children.Add(time); row.Children.Add(detail);
                island.Child = row;
            }
            else island.Child = detail;
            surface.Children.Add(island);
        }
        return new Viewbox { Child = screen, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private static DrawingBrush CreateDesktop(bool dark)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Gradient(dark ? "#8495A5" : "#D7E4ED", dark ? "#344658" : "#AEC5D7", new Point(0, 0), new Point(1, 1)), null, new Rect(0, 0, Width, Height));
            context.DrawGeometry(Gradient(dark ? "#A8B5BF" : "#EFF5F8", dark ? "#52687C" : "#C2D6E4", new Point(0, 0), new Point(.8, 1)), null,
                Geometry.Parse("M -20,124 C 28,66 68,113 112,63 C 155,13 184,38 258,-18 L 260,142 L -20,142 Z"));
            context.DrawGeometry(Gradient(dark ? "#718798" : "#BDD2E1", dark ? "#354B60" : "#9FBBD0", new Point(0, 0), new Point(1, 1)), null,
                Geometry.Parse("M -20,148 C 42,89 74,118 115,102 C 162,84 186,68 220,72 C 240,74 253,60 265,48 L 265,148 Z"));

            // A quiet, cropped window gives the miniature a desktop context.
            context.DrawRoundedRectangle(UI.Brush("#1A000000"), null, new Rect(85, 71, 149, 92), 9, 9);
            context.DrawRoundedRectangle(Gradient(dark ? "#E52E3741" : "#F2FBFCFE", dark ? "#ED1B232C" : "#F5EDF2F6", new Point(0, 0), new Point(0, 1)),
                new Pen(UI.Brush(dark ? "#36FFFFFF" : "#80A9B7C4"), 1), new Rect(88, 67, 149, 92), 8, 8);
            context.DrawLine(new Pen(UI.Brush("#16FFFFFF"), 1), new Point(88, 84), new Point(237, 84));
            context.DrawLine(new Pen(UI.Brush("#689AA7B5"), 1), new Point(201, 75), new Point(207, 75));
            context.DrawRectangle(null, new Pen(UI.Brush("#689AA7B5"), 1), new Rect(216, 73, 4, 4));
            context.DrawRoundedRectangle(UI.Brush("#33495C6D"), null, new Rect(94, 90, 32, 9), 2, 2);
            context.DrawRoundedRectangle(UI.Brush("#44C4CED6"), null, new Rect(100, 94, 19, 1.5), .75, .75);
            foreach (double y in new[] { 107d, 118, 129 })
                context.DrawRoundedRectangle(UI.Brush("#339CA9B5"), null, new Rect(100, y, 18, 1.5), .75, .75);
            context.DrawRoundedRectangle(UI.Brush(dark ? "#88C5D0D9" : "#8890A4B4"), null, new Rect(140, 95, 39, 3), 1.5, 1.5);
            context.DrawRoundedRectangle(UI.Brush(dark ? "#30BECBD6" : "#6698ACBC"), null, new Rect(140, 106, 70, 2), 1, 1);
            context.DrawRoundedRectangle(UI.Brush(dark ? "#30BECBD6" : "#6698ACBC"), null, new Rect(140, 113, 51, 2), 1, 1);
            context.DrawRoundedRectangle(UI.Brush("#706D94B4"), null, new Rect(140, 124, 30, 9), 3, 3);
            context.DrawLine(new Pen(UI.Brush("#40FFFFFF"), 1), new Point(0, .5), new Point(Width, .5));
        }
        var brush = new DrawingBrush(drawing) { Viewbox = new Rect(0, 0, Width, Height), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        brush.Freeze(); return brush;
    }

    private static LinearGradientBrush Gradient(string first, string last, Point start, Point end) => new()
    {
        StartPoint = start, EndPoint = end,
        GradientStops = [new GradientStop((Color)ColorConverter.ConvertFromString(first), 0), new GradientStop((Color)ColorConverter.ConvertFromString(last), 1)]
    };
}
