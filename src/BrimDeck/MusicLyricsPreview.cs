using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// Diagrams for the cards that choose the open page's layout ("展开时"). Each card draws its own page, as the cover and the lyrics
// are shown now: the record and the lyric lines come and go with them, and without the record the stacked layout's column
// stands at the page's left edge or in the middle. The parts keep the page's own proportions: three lyric lines under the
// titles with the current one in the middle, and in the column layout a cover that takes the larger part of the column's
// height (MusicLayout.Columns). Drawn at
// the arrangement cards' size (ShelfArrangementPreview) in the settings' own colors, and standing still.
internal static class MusicLyricsPreview
{
    private const double Width = 372, Height = 136, Left = 24, Right = 348, InfoWidth = 115;
    private const double DividerX = 151, LyricsX = 175, CoverAbove = 58, CoverBeside = 88;

    // Ground: the diagram's background. Album, AlbumEdge: the record. Line: a lyric line that is not the current one. Current:
    // the current lyric. Rule: the divider. Primary, Secondary: the title and the artist.
    private sealed record Tones(string Ground, string Album, string AlbumEdge, string Line, string Current, string Rule, string Primary, string Secondary)
    {
        public static Tones For(SettingsPalette palette) => palette.Dark
            ? new("#151517", "#2C2C31", "#46464D", "#45454D", "#7FB2FF", "#5A5A63", palette.Primary, palette.Secondary)
            : new("#ECEDF2", "#FFFFFF", "#D3D6DE", "#CFD2DA", "#2F74DA", "#B9BCC6", palette.Primary, palette.Secondary);
    }

    // The stacked layout with the cover: the cover left, the title and artist beside it, the lyrics under them (the line
    // before, the current one, the line after), the last line even with the cover's bottom.
    public static FrameworkElement StackedWithCover(SettingsPalette palette)
    {
        var tones = Tones.For(palette);
        var scene = Scene(tones, out var canvas);
        Album(canvas, tones, Left, 20, CoverBeside);
        double x = Left + CoverBeside + 16;
        Label(canvas, tones, Loc.T("歌名", "Song title"), x, 26, Right - x, primary: true, align: TextAlignment.Left);
        Label(canvas, tones, Loc.T("歌手", "Artist"), x, 48, Right - x, primary: false, align: TextAlignment.Left);
        Bar(canvas, tones, x, 76, 120);
        Bar(canvas, tones, x, 90, 150, current: true);
        Bar(canvas, tones, x, 104, 168);
        return Box(scene);
    }

    // The columns layout with the cover: the cover above the title in the left column, the lyric lines right of the divider.
    public static FrameworkElement ColumnsWithCover(SettingsPalette palette)
    {
        var tones = Tones.For(palette);
        var scene = Scene(tones, out var canvas);
        Album(canvas, tones, Left + (InfoWidth - CoverAbove) / 2, 19, CoverAbove);
        Label(canvas, tones, Loc.T("歌名", "Song title"), Left, 84, InfoWidth, primary: true, align: TextAlignment.Center);
        Label(canvas, tones, Loc.T("歌手", "Artist"), Left, 104, InfoWidth, primary: false, align: TextAlignment.Center);
        Divider(canvas, tones);
        Bar(canvas, tones, LyricsX, 22, 128);
        Bar(canvas, tones, LyricsX, 44, 144);
        Bar(canvas, tones, LyricsX, 66, 166, current: true);
        Bar(canvas, tones, LyricsX, 88, 144);
        Bar(canvas, tones, LyricsX, 110, 128);
        return Box(scene);
    }

    // The columns layout without the cover: no record, the title and artist centred in the left column, the lyrics right.
    public static FrameworkElement ColumnsWithoutCover(SettingsPalette palette)
    {
        var tones = Tones.For(palette);
        var scene = Scene(tones, out var canvas);
        Label(canvas, tones, Loc.T("歌名", "Song title"), Left, 40, InfoWidth, primary: true, align: TextAlignment.Center, scale: 1.25);
        Label(canvas, tones, Loc.T("歌手", "Artist"), Left, 60, InfoWidth, primary: false, align: TextAlignment.Center, scale: 1.25);
        Divider(canvas, tones);
        Bar(canvas, tones, LyricsX, 22, 110);
        Bar(canvas, tones, LyricsX, 44, 144);
        Bar(canvas, tones, LyricsX, 66, 166, current: true);
        Bar(canvas, tones, LyricsX, 88, 144);
        Bar(canvas, tones, LyricsX, 110, 110);
        return Box(scene);
    }

    // The stacked layout without the cover: the title, artist and lyrics at the page's left edge or in the middle.
    public static FrameworkElement StackedWithoutCover(SettingsPalette palette, MusicLyricsAlign align)
    {
        var tones = Tones.For(palette);
        var scene = Scene(tones, out var canvas);
        var text = align == MusicLyricsAlign.Center ? TextAlignment.Center : TextAlignment.Left;
        Label(canvas, tones, Loc.T("歌名", "Song title"), Left, 30, Right - Left, primary: true, align: text, scale: 1.25);
        Label(canvas, tones, Loc.T("歌手", "Artist"), Left, 52, Right - Left, primary: false, align: text, scale: 1.25);
        double First(double width) => align == MusicLyricsAlign.Center ? (Width - width) / 2 : Left;
        Bar(canvas, tones, First(170), 80, 170);
        Bar(canvas, tones, First(130), 96, 130, current: true);
        Bar(canvas, tones, First(170), 112, 170);
        return Box(scene);
    }

    // The columns layout without the lyrics: the cover and the title centred across the page, no divider.
    public static FrameworkElement ColumnsWithoutLyrics(SettingsPalette palette)
    {
        var tones = Tones.For(palette);
        var scene = Scene(tones, out var canvas);
        Album(canvas, tones, (Width - CoverAbove) / 2, 19, CoverAbove);
        Label(canvas, tones, Loc.T("歌名", "Song title"), Left, 84, Right - Left, primary: true, align: TextAlignment.Center);
        Label(canvas, tones, Loc.T("歌手", "Artist"), Left, 104, Right - Left, primary: false, align: TextAlignment.Center);
        return Box(scene);
    }

    // The stacked layout without the lyrics: the cover left, the title and artist beside it at the middle of its height.
    public static FrameworkElement StackedWithoutLyrics(SettingsPalette palette)
    {
        var tones = Tones.For(palette);
        var scene = Scene(tones, out var canvas);
        Album(canvas, tones, Left, 24, CoverBeside);
        double x = Left + CoverBeside + 16;
        Label(canvas, tones, Loc.T("歌名", "Song title"), x, 50, Right - x, primary: true, align: TextAlignment.Left);
        Label(canvas, tones, Loc.T("歌手", "Artist"), x, 70, Right - x, primary: false, align: TextAlignment.Left);
        return Box(scene);
    }

    // The field, clipped to its rounded frame, and the canvas drawn on it.
    private static Grid Scene(Tones tones, out Canvas canvas)
    {
        var scene = new Grid
        {
            Width = Width, Height = Height, Background = UI.Brush(tones.Ground), IsHitTestVisible = false,
            Clip = new RectangleGeometry(new Rect(0, 0, Width, Height), 9, 9), UseLayoutRounding = false, SnapsToDevicePixels = false
        };
        // Drawn small and enlarged: ideal text layout keeps the names sharp (CompactStylePreview).
        TextOptions.SetTextFormattingMode(scene, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(scene, TextRenderingMode.Grayscale);
        canvas = new Canvas { Width = Width, Height = Height };
        scene.Children.Add(canvas);
        return scene;
    }
    private static FrameworkElement Box(Grid scene) => new Viewbox { Child = scene, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static void Put(Canvas canvas, FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x); Canvas.SetTop(element, y); canvas.Children.Add(element);
    }
    private static void Label(Canvas canvas, Tones tones, string value, double x, double y, double width, bool primary, TextAlignment align, double scale = 1) => Put(canvas, new TextBlock
    {
        Text = value, Width = width, FontFamily = UI.PanelFont, FontSize = (primary ? 13 : 11) * scale, TextAlignment = align,
        FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, Foreground = UI.Brush(primary ? tones.Primary : tones.Secondary),
        TextWrapping = TextWrapping.NoWrap
    }, x, y);
    private static void Bar(Canvas canvas, Tones tones, double x, double y, double width, bool current = false) => Put(canvas, new Rectangle
    {
        Width = width, Height = current ? 5 : 4, RadiusX = 2, RadiusY = 2, Fill = UI.Brush(current ? tones.Current : tones.Line)
    }, x, y);
    private static void Divider(Canvas canvas, Tones tones) => Put(canvas,
        new Rectangle { Width = 1, Height = 96, Fill = UI.Brush(tones.Rule) }, DividerX, 20);
    // The record, its sleeve and its grooves.
    private static void Album(Canvas canvas, Tones tones, double x, double y, double size) => Put(canvas, new Border
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(8), Background = UI.Brush(tones.Album),
        BorderBrush = UI.Brush(tones.AlbumEdge), BorderThickness = new Thickness(1),
        Child = new Grid
        {
            Children =
            {
                new Ellipse { Width = size * .54, Height = size * .54, Stroke = UI.Brush(tones.AlbumEdge), StrokeThickness = 1.5,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                new Ellipse { Width = Math.Max(3, size * .1), Height = Math.Max(3, size * .1), Fill = UI.Brush(tones.AlbumEdge),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            }
        }
    }, x, y);
}