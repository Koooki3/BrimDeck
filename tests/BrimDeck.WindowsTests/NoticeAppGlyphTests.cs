using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

// The application-type glyph shown on a notice's plate when the application has no icon (NoticeBadges.AppGlyph): the plate is
// the application's color, faint, over whatever the plate lies on, so the glyph is measured against that plate as WPF draws it
// (NoticeLetter.Plate) on each ground it is shown on: the surface, a list row in each of its states, and the glow behind the
// focus. A graphic keeps 3:1 there in every built-in theme.
internal static class NoticeAppGlyphTests
{
    // WPF blends in premultiplied bytes: a drawn pixel can be a level or two from the exact blend.
    private const double Rounding = .05;

    public static void Run(Action<string, bool> check)
    {
        var looks = PanelThemes.BuiltIns.SelectMany(theme => theme.File.Night is null
            ? new[] { (Theme: theme, Night: false) } : [(theme, false), (theme, true)]).ToList();
        var apps = AppPresets.Palette.Take(9).Select(ThemeColor.Parse).ToList();
        // A mail application without an icon: it shows the mail glyph.
        var notice = new Notice { AppKey = "outlook", AppName = "Outlook", Origin = NoticeOrigin.Windows };
        var failures = new List<(string Theme, double Ratio)>();
        int measured = 0;
        foreach (var (theme, night) in looks)
        {
            var palette = PanelThemes.Resolve(theme, night);
            string name = theme.Id + (night ? " (night)" : "");
            var grounds = new List<ThemeColor>(palette.SurfaceStops);
            grounds.AddRange(new[] { "fill", "fillSelected", "hover", "pressed" }.Select(key => palette[key].Over(palette.Surface)));
            foreach (var app in apps.Append(palette["accent"]))
            {
                var accent = PanelTheme.ToColor(palette.Readable(app));
                if (NoticeBadges.AppGlyph(notice, 56, accent, palette) is not NoticeGlyphElement glyph) continue;
                var glow = new ThemeColor(NoticeLines.GlowAlpha, accent.R, accent.G, accent.B);
                var plates = grounds.Concat(palette.SurfaceStops.Select(stop => glow.Over(stop))).Select(ground => PlateOn(ground, accent)).ToList();
                double ratio = new ThemeColor(255, glyph.Ink.R, glyph.Ink.G, glyph.Ink.B).MinimumContrast(plates);
                measured++;
                if (ratio < PanelThemes.GraphicContrast - Rounding) failures.Add((name, ratio));
            }
        }
        var shortThemes = failures.GroupBy(f => f.Theme).Select(g => $"{g.Key} {g.Min(f => f.Ratio):0.00}");
        check($"notice app glyph: the glyph keeps 3:1 on its plate as drawn on every ground, in every built-in theme ({measured} measured"
            + (failures.Count > 0 ? $", {failures.Count} short: {string.Join(", ", shortThemes)})" : ")"), measured > 0 && failures.Count == 0);
    }

    // The colour WPF draws in the middle of a notice's plate (NoticeLetter.Plate, without its glyph) lying on a ground of one colour.
    private static ThemeColor PlateOn(ThemeColor ground, Color accent)
    {
        var host = new Grid { Width = 56, Height = 56, Background = new SolidColorBrush(PanelTheme.ToColor(ground)) };
        host.Children.Add(NoticeLetter.Plate("Outlook", 56, accent, icon: new Border()));
        host.Measure(new Size(56, 56)); host.Arrange(new Rect(0, 0, 56, 56));
        var bitmap = new RenderTargetBitmap(56, 56, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(28, 28, 1, 1), pixel, 4, 0);
        return new ThemeColor(255, pixel[2], pixel[1], pixel[0]);
    }
}
