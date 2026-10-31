using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// An application without an icon of its own: its first letter in its color, on a plate tinted with that color. The island, the
// notices page's focus and its list all show it this way, and a notice's picture (a contact's avatar) round, as Windows shows it.
internal static class NoticeLetter
{
    public static FrameworkElement Round(ImageSource picture, double size)
    {
        var brush = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        return new System.Windows.Shapes.Ellipse { Width = size, Height = size, Fill = brush };
    }

    // Whether a notice may show its picture: not when the user keeps it to the application's name.
    public static bool ShowsPicture(Notice notice, DeckSettings settings) => notice.Picture is not null && (settings.NoticeRule(notice.AppKey)?.Privacy ?? settings.NoticePrivacy) != NoticePrivacy.AppOnly;

    // bell: a summary of notices, which has no application.
    public static FrameworkElement Plate(string? appName, double size, Color accent, bool bell = false)
    {
        var plate = new Grid { Width = size, Height = size };
        const byte tint = 40;
        plate.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Math.Round(size * .28)),
            Background = new SolidColorBrush(Color.FromArgb(tint, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(46, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(size >= 40 ? 1 : 0)
        });
        if (bell)
        {
            var glyph = NoticeBadges.Glyph(NoticeGlyph.Bell, Math.Round(size * .6), accent);
            glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Center;
            plate.Children.Add(glyph);
            return plate;
        }
        var name = appName?.Trim() ?? "";
        string letter = name.Length == 0 ? "?" : char.IsSurrogate(name[0]) && name.Length > 1 ? name[..2] : name[..1].ToUpper(Loc.Culture);
        // The letter is text on the tinted plate, not on the panel: it is held to the text minimum (3:1 once it is large) against the
        // plate on each ground below, which the application's color alone does not reach on a light theme.
        double fontSize = Math.Round(size * .45);
        // The plate lies on the surface, on a list row in any of its states and, as the focus's cover, on the glow behind the focus.
        var palette = PanelTheme.Current;
        var glow = new ThemeColor(NoticeLines.GlowAlpha, accent.R, accent.G, accent.B);
        var grounds = new List<ThemeColor>(palette.SurfaceStops);
        grounds.AddRange(new[] { "fill", "fillSelected", "hover", "pressed" }.Select(key => palette[key].Over(palette.Surface)));
        grounds.AddRange(palette.SurfaceStops.Select(stop => glow.Over(stop)));
        var plateOver = grounds.Select(ground => new ThemeColor(tint, accent.R, accent.G, accent.B).Over(ground)).ToList();
        var ink = new ThemeColor(255, accent.R, accent.G, accent.B).EnsureContrast(plateOver, fontSize >= 24 ? PanelThemes.GraphicContrast : PanelThemes.TextContrast);
        plate.Children.Add(new TextBlock
        {
            Text = letter, FontFamily = UI.DisplayFont, FontSize = fontSize, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(PanelTheme.ToColor(ink)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        return plate;
    }
}
