using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

// Checks the notice glyphs as WPF draws them (every geometry parses, is frozen and stays on the grid) and, given a folder,
// renders the whole set on each built-in theme into one PNG per theme: the glyphs at 20 and 14 DIP, icon plates with
// application-type glyphs, the state badges, the source badges and the high contrast colors. It never starts BrimDeck and
// reads no settings or theme files; the colors come from the built-in palettes. Not part of BrimDeck.sln; run it by hand:
//   dotnet run -c Release --project tests/BrimDeck.GlyphPreview -- <output folder>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int failures = 0;
        foreach (var glyph in NoticeGlyphs.All)
            foreach (bool badge in new[] { false, true })
            {
                var geometry = NoticeBadges.Geometry(glyph, badge);
                var bounds = geometry.GetRenderBounds(new Pen(Brushes.Black, NoticeGlyphs.Stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
                bool ok = geometry.IsFrozen && !geometry.IsEmpty() && bounds.Left >= .5 && bounds.Top >= .5 && bounds.Right <= NoticeGlyphs.Grid - .5 && bounds.Bottom <= NoticeGlyphs.Grid - .5;
                if (!ok) { failures++; Console.WriteLine($"FAIL: {glyph}{(badge ? " (badge)" : "")} frozen={geometry.IsFrozen} bounds={bounds}"); }
            }
        Console.WriteLine(failures == 0 ? $"PASS: {NoticeGlyphs.All.Count} notice glyphs parse, are frozen and stay on the grid" : $"{failures} glyph checks failed");
        if (args.Length > 0)
        {
            Directory.CreateDirectory(args[0]);
            foreach (var theme in PanelThemes.BuiltIns)
                Save(Sheet(PanelThemes.Resolve(theme), null), Path.Combine(args[0], "notice-glyphs-" + theme.Id + ".png"));
            var black = NoticeGlyphColors.FromSystem(ThemeColor.Parse("#000000"), ThemeColor.Parse("#FFFFFF"), ThemeColor.Parse("#000000"), ThemeColor.Parse("#1AEBFF"), ThemeColor.Parse("#000000"));
            var white = NoticeGlyphColors.FromSystem(ThemeColor.Parse("#FFFFFF"), ThemeColor.Parse("#000000"), ThemeColor.Parse("#FFFFFF"), ThemeColor.Parse("#37006E"), ThemeColor.Parse("#FFFFFF"));
            var dark = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!);
            Save(Sheet(dark, black), Path.Combine(args[0], "notice-glyphs-high-contrast-black.png"));
            Save(Sheet(dark, white), Path.Combine(args[0], "notice-glyphs-high-contrast-white.png"));
            Console.WriteLine("Wrote previews to " + Path.GetFullPath(args[0]));
        }
        return failures == 0 ? 0 : 1;
    }

    private static Color C(ThemeColor color) => PanelTheme.ToColor(color);

    private static FrameworkElement Sheet(PanelPalette palette, NoticeGlyphColors? system)
    {
        var colors = system ?? NoticeGlyphColors.FromPalette(palette);
        bool glow = system is null && palette.Effects.ShadowDepth <= 0 && palette.Effects.ShadowOpacity > 0;
        var stack = new StackPanel { Margin = new Thickness(18) };
        var title = system is null ? palette.Id + (glow ? "  (glow)" : "") + (palette.IsDark ? "  dark" : "  light") : "high contrast";
        stack.Children.Add(new TextBlock { Text = title, Foreground = new SolidColorBrush(C(colors.Primary)), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });

        foreach (double size in new[] { 20d, 14d, 32d })
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 10), MaxWidth = 760 };
            foreach (var glyph in NoticeGlyphs.All)
            {
                var color = C(colors.Tone(NoticeGlyphs.Tone(glyph)));
                var cell = new Grid { Width = size + 10, Height = size + 10 };
                var element = new NoticeGlyphElement { Glyph = glyph, Ink = color, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                cell.Children.Add(element);
                cell.ToolTip = glyph.ToString();
                row.Children.Add(cell);
            }
            stack.Children.Add(row);
        }

        // Plates as on the recent notices page (28) and in a notice's details (32), with the state and source badges.
        var plates = new WrapPanel { MaxWidth = 760 };
        (NoticeGlyph App, NoticeCategory Category, NoticeOrigin Origin, string Key)[] samples =
        [
            (NoticeGlyph.Chat, NoticeCategory.Message, NoticeOrigin.Windows, "windows:Tencent.WeChat"),
            (NoticeGlyph.Mail, NoticeCategory.Info, NoticeOrigin.Windows, "windows:Outlook"),
            (NoticeGlyph.Calendar, NoticeCategory.Warning, NoticeOrigin.Windows, "windows:Calendar"),
            (NoticeGlyph.Build, NoticeCategory.Success, NoticeOrigin.Cli, "cli:ci"),
            (NoticeGlyph.Terminal, NoticeCategory.Error, NoticeOrigin.Cli, "cli:make"),
            (NoticeGlyph.Code, NoticeCategory.Error, NoticeOrigin.Wsl, "wsl:Ubuntu:code"),
            (NoticeGlyph.Chat, NoticeCategory.Success, NoticeOrigin.Windows, "windows:Microsoft.YourPhone!App"),
            (NoticeGlyph.Music, NoticeCategory.Info, NoticeOrigin.Windows, "windows:Chrome._crx_music"),
            (NoticeGlyph.Assistant, NoticeCategory.Success, NoticeOrigin.Windows, "windows:Claude"),
            (NoticeGlyph.Game, NoticeCategory.Warning, NoticeOrigin.Windows, "windows:Steam"),
            (NoticeGlyph.Cloud, NoticeCategory.Message, NoticeOrigin.Windows, "windows:OneDrive"),
            (NoticeGlyph.Security, NoticeCategory.Error, NoticeOrigin.Windows, "windows:Defender"),
        ];
        var accents = AppPresets.Palette.Take(9).Select(ThemeColor.Parse).ToArray();
        int i = 0;
        foreach (double size in new[] { 28d, 32d, 40d })
            foreach (var sample in samples)
            {
                var plate = new Grid { Width = size, Height = size, Margin = new Thickness(6, 6, 14, 12) };
                plate.Children.Add(new Border { CornerRadius = new CornerRadius(size * .28), Background = new SolidColorBrush(C(colors.Plate)) });
                plate.Children.Add(new NoticeGlyphElement { Glyph = sample.App, Ink = C(colors.OnPlate(accents[i++ % accents.Length])), Width = Math.Round(size * .6), Height = Math.Round(size * .6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                double badge = Math.Max(10, Math.Round(size * .42));
                if (NoticeGlyphs.Source(sample.Origin, sample.Key) is { } source && source != sample.App)
                    plate.Children.Add(new NoticeGlyphElement { Glyph = source, Badge = true, Ink = C(colors.Secondary), Disc = C(colors.Plate), Ring = C(colors.Surface), Width = badge, Height = badge, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-badge * .3, 0, 0, -badge * .3) });
                if (NoticeGlyphs.Status(sample.Category) is { } status)
                    plate.Children.Add(new NoticeGlyphElement { Glyph = status, Badge = true, Ink = C(colors.BadgeInk), Disc = C(colors.Tone(NoticeGlyphs.Tone(status))), Ring = C(colors.Surface), Glowing = glow, Width = badge, Height = badge, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -badge * .3, -badge * .3) });
                plates.Children.Add(plate);
            }
        stack.Children.Add(plates);

        var surface = new Grid { Background = new SolidColorBrush(C(colors.Surface)) };
        if (system is null && PanelTheme.SurfaceGradient(palette) is { } gradient) surface.Children.Add(new Border { Background = gradient });
        if (system is null && PanelTheme.Scanlines(palette, 2) is { } lines) surface.Children.Add(new Border { Background = lines });
        surface.Children.Add(stack);
        return new Border { Child = surface, BorderBrush = system is null ? PanelTheme.RimBrush(palette) : new SolidColorBrush(C(colors.Primary)), BorderThickness = new Thickness(system is null ? Math.Max(palette.Effects.RimWidth, 0) : 2), Width = 800 };
    }

    private static void Save(FrameworkElement element, string path)
    {
        element.Measure(new Size(element.Width, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        const double Scale = 2;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * Scale), (int)Math.Ceiling(element.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
