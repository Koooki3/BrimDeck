using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;

// The glow behind the notices page's focus column (NoticeGlow), drawn by WPF over each built-in theme's surface: the text that
// stands on it keeps the body-text minimum where the glow is strongest, measured on the pixels WPF draws.
internal static class NoticeGlowTests
{
    // WPF blends the glow in premultiplied bytes, so a drawn pixel can be a level or two from the exact blend: a few hundredths of
    // the ratio on these surfaces.
    private const double Rounding = .05;

    public static void Run(Action<string, bool> check)
    {
        var looks = PanelThemes.BuiltIns.SelectMany(theme => theme.File.Night is null
            ? new[] { (Theme: theme, Night: false) } : [(theme, false), (theme, true)]).ToList();
        // The colors an application without one of its own is given (NoticeIcons.Fallback), and the theme's accent.
        var apps = AppPresets.Palette.Take(9).Select(ThemeColor.Parse).ToList();
        var failures = new List<string>();
        int moved = 0, kept = 0;
        foreach (var (theme, night) in looks)
        {
            var palette = PanelThemes.Resolve(theme, night);
            string name = theme.Id + (night ? " (night)" : "");
            var token = palette["textTertiary"];
            foreach (var app in apps.Append(palette["accent"]))
            {
                var accent = PanelTheme.ToColor(palette.Readable(app));
                var ink = ThemeColor.Parse(NoticeGlow.Ink(palette, "textTertiary", accent, glow: true));
                // The glow's centre, where the category line lies, over every stop of the surface; and the surface alone.
                var grounds = palette.SurfaceStops.Select(stop => GlowCentre(PanelTheme.ToColor(stop), accent)).Concat(palette.SurfaceStops).ToList();
                double ratio = ink.MinimumContrast(grounds);
                if (ratio < PanelThemes.TextContrast - Rounding) failures.Add($"{name} on {app}: {ratio:0.00}");
                // A color that reads there already is the theme's own, unchanged.
                if (token.MinimumContrast(grounds) >= PanelThemes.TextContrast + Rounding) { if (ink == token) kept++; else moved++; }
            }
        }
        // Short ones are named by theme, with the lowest ratio among its application colors.
        var shortThemes = failures.Select(f => f.Split(" on ")).GroupBy(f => f[0]).Select(g => $"{g.Key} {g.Min(f => f[1].Split(": ")[1])}");
        check("notice glow: the category line under the focus's title keeps 4.5:1 on the drawn glow in every built-in theme"
            + (failures.Count > 0 ? $" ({failures.Count} short: {string.Join(", ", shortThemes)})" : ""), failures.Count == 0);
        check($"notice glow: a theme whose tertiary text already reads on the glow keeps its own color ({kept} kept, {moved} moved)", kept > 0 && moved == 0);
    }

    // The colour WPF draws at the centre of the focus's glow (NoticeGlow.Brush) over a surface of one colour.
    private static ThemeColor GlowCentre(Color surface, Color accent)
    {
        var host = new Grid { Width = 101, Height = 101, Background = new SolidColorBrush(surface) };
        host.Children.Add(new Border { Background = NoticeGlow.Brush(accent) });
        host.Measure(new Size(101, 101)); host.Arrange(new Rect(0, 0, 101, 101));
        var bitmap = new RenderTargetBitmap(101, 101, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(50, 50, 1, 1), pixel, 4, 0);
        return new ThemeColor(255, pixel[2], pixel[1], pixel[0]);
    }
}
