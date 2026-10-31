using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// A notice in the panel theme the user chose: it is the island itself, dressed as PanelTheme dresses the island (surface color,
// gradient, scan lines, rim, shadow or glow), with the theme's text, fill, hover and accent colors for its content. Each theme
// therefore gets its own notices without a setting of their own: a glowing theme's notice arrives with one flash of its glow,
// a theme with depth casts its shadow, and colors chosen outside the theme (an application's color, the status marks) are
// moved just far enough to read on its surface. Glass is the island's tint and edge light (with the blur of GlassBackdrop under
// the notice window), pixel art its steps, bevel and grain. High contrast replaces everything with system colors and drops glow.
internal sealed record NoticeLook(
    Color Surface, Brush? Gradient, Brush? Pattern, Brush Rim, double RimWidth,
    Color Shadow, double ShadowBlur, double ShadowDepth, double ShadowOpacity,
    string Primary, string Secondary, string Tertiary, string Fill, string Hover, string Pressed, string Accent, string OnAccent, string Danger,
    bool Glow, bool HighContrast)
{
    // The island's material beyond its colors: a pixel theme's step (PanelThemeEffectValues.Pixel) and bevel, a glass's edge light.
    public double Pixel { get; init; }
    // Cut corners (PanelThemeEffectValues.Cut) and the notice's own marks (a hatching strip and the signal bar), from the theme's decor.
    public double Cut { get; init; }
    public PanelDecor? Decor { get; init; }
    public Brush? Bevel { get; init; }
    public Brush? Edge { get; init; }
    // Glass shown (PanelTheme.GlassShown): the surface is a tint over the desktop, so the text cannot use ClearType.
    public bool Glass { get; init; }
    private static (int Generation, bool Glass, double Dpi, NoticeLook Look)? _cached;

    public static NoticeLook Current(double dpiScale = 1)
    {
        if (SystemParameters.HighContrast) return HighContrastLook();
        bool glass = PanelTheme.GlassShown;
        if (_cached is { } cached && cached.Generation == PanelTheme.Generation && cached.Glass == glass && cached.Dpi == dpiScale) return cached.Look;
        var palette = PanelTheme.Current;
        var effects = palette.Effects;
        var look = new NoticeLook(
            PanelTheme.SurfaceColor, PanelTheme.SurfaceGradient(palette), PanelTheme.Texture(palette, dpiScale) ?? PanelTheme.Scanlines(palette, dpiScale), PanelTheme.RimBrush(palette), effects.RimWidth,
            PanelTheme.ColorOf("shadow"), effects.ShadowBlur, effects.ShadowDepth, effects.ShadowOpacity,
            PanelTheme.Hex("textPrimary"), PanelTheme.Hex("textSecondary"), PanelTheme.Hex("textTertiary"),
            PanelTheme.Hex("fill"), PanelTheme.Hex("hover"), PanelTheme.Hex("pressed"), PanelTheme.Hex("accent"), PanelTheme.Hex("onAccent"), PanelTheme.Hex("danger"),
            // A theme without depth whose shadow is drawn anyway gives its surface a glow rather than a shadow.
            Glow: effects.ShadowDepth <= 0 && effects.ShadowOpacity > 0, HighContrast: false)
        {
            Pixel = effects.Pixel, Cut = effects.Cut, Decor = effects.Decor is { Hatch: not null } or { Tab: not null } or { Frame: not null } ? effects.Decor : null, Bevel = PanelTheme.BevelBrush(palette), Edge = glass ? PanelTheme.EdgeBrush(palette) : null, Glass = glass
        };
        _cached = (PanelTheme.Generation, glass, dpiScale, look);
        return look;
    }

    private static NoticeLook HighContrastLook()
    {
        static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        string text = Hex(SystemColors.WindowTextColor);
        return new(SystemColors.WindowColor, null, null, SystemColors.WindowTextBrush, 2, Colors.Transparent, 0, 0, 0,
            text, text, Hex(SystemColors.GrayTextColor), Hex(SystemColors.ControlColor), Hex(SystemColors.HighlightColor), Hex(SystemColors.HighlightColor),
            Hex(SystemColors.HighlightColor), Hex(SystemColors.HighlightTextColor), text, Glow: false, HighContrast: true);
    }

    // An application's color as drawn on this theme.
    public Color AppColor(Color color) => HighContrast ? SystemColors.HighlightColor : PanelTheme.Readable(color);
}
