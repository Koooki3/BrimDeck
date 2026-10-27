namespace BrimDeck.Core;

// The Windows accent color, when it can stand for one. Tools that tint Windows to the wallpaper (Wallpaper Engine's
// "adjust DWM color") can set it to pure black, and a gray or near-black accent drawn as a highlight reads as no color at all,
// so such an accent gives way to the theme's own.
public static class AccentColors
{
    // Below this relative luminance (WCAG's, as ThemeColor measures contrast) the accent is too dark to show on a dark panel.
    public const double MinLuminance = .05;
    // Below this HSL saturation the accent is a gray: white, black or anything between.
    public const double MinSaturation = .1;

    public static ThemeColor Usable(ThemeColor? system, ThemeColor fallback)
        => system is { } color && color.Luminance >= MinLuminance && ThemeColor.ToHsl(color).S >= MinSaturation ? color : fallback;
}
