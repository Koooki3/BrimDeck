namespace BrimDeck.Core.Notices;

// How a notice arrives on the island (IslandLiquid, drawn by IslandNotice): one plan from two things. The notice's weight says how
// much of the motion it deserves (what only informs plays lightly, a call or an alarm strongly and in the state's color); the
// theme says how it moves (a firm theme's liquid is tighter, a glowing theme flashes its backlight once, a pixel theme keeps its
// outline still, high contrast and a Windows setting that turns animations off play nothing). The prototype's own motion (fury 5,
// separation 0.6 s, pace 1, two flashes) is what an ordinary notice gets on a theme that asks for nothing else, so the themes
// differ only where they have said they move differently (ThemeMotion).
public enum NoticeTone { Theme, Warning, Danger }

// Intensity: the design's 1 to 10. Separation: how long droplets are thrown, in seconds. Glow: the backlight's strength, 0 to 1.25.
public readonly record struct NoticeWeight(double Intensity, double Separation, double Glow, NoticeTone Tone)
{
    // The design's own defaults: intensity 5 and a separation of 0.6 s.
    public static NoticeWeight Standard => new(5, .6, 1, NoticeTone.Theme);
}

// What the theme asks of the motion: scales for the weight's intensity and separation, the pace of the whole, how often the backlight
// flashes (the prototype's two, or one), whether the outline moves at all (liquid) and whether anything moves (animated).
public readonly record struct ThemeArrival(double FuryScale, double SeparationScale, double Pace, int Flashes, bool Liquid, bool Animated)
{
    public static ThemeArrival Standard => new(1, 1, 1, 2, true, true);
}

public readonly record struct NoticeArrival(double Fury, double Separation, double Pace, double Glow, int Flashes, bool Liquid, bool Animated, NoticeTone Tone);

public static class NoticeArrivals
{
    // The pace never goes above the prototype's by more than a twentieth: the user found a faster one worse (2026-10-30). A theme
    // that moves slowly (a pace under 1 in ThemeMotion) slows the liquid by half as much, to a tenth.
    public const double PaceMin = .9, PaceMax = 1.05;
    public const double FuryMin = 1, FuryMax = 10, SeparationMin = .3, SeparationMax = 1.1, GlowMax = 1.25;
    // A firm theme (SpringTier.Firm) is mechanical: its liquid swells less. A jelly one gives a little more.
    public const double FirmFury = .7, JellyFury = 1.2, FirmSeparation = .9;

    // The notice's own weight. category: the notice's own category (null for a summary); call: an incoming call.
    public static NoticeWeight Weigh(AttentionKind kind, NoticePriority priority, bool urgent, bool call, NoticeCategory? category)
    {
        // What only informs plays lightly.
        if (kind == AttentionKind.Summary) return new(2, .4, .6, NoticeTone.Theme);
        var tone = category switch { NoticeCategory.Error => NoticeTone.Danger, NoticeCategory.Warning => NoticeTone.Warning, _ => NoticeTone.Theme };
        // An alarm, a call, a countdown that ended, an event's reminder.
        if (urgent || call) return new(8, 1, 1.25, tone == NoticeTone.Theme ? NoticeTone.Warning : tone);
        return priority switch
        {
            NoticePriority.High => new(7, .9, 1.15, tone),
            NoticePriority.Low => new(3, .45, .7, tone),
            _ => tone == NoticeTone.Theme ? NoticeWeight.Standard : NoticeWeight.Standard with { Tone = tone, Glow = 1.1 }
        };
    }

    // Whether the theme's surface glows instead of casting a shadow: a theme without depth whose shadow is drawn anyway.
    public static bool Glows(PanelThemeEffectValues effects) => effects.ShadowDepth <= 0 && effects.ShadowOpacity > 0;

    // The theme's way of moving, from what it says about its motion (ThemeMotion; null or a theme without a motion section gives the
    // prototype's own), its material and the environment. pixel: a pixel theme draws its island in steps, so its liquid is the
    // backlight and the words alone. glows: a theme whose surface glows instead of casting a shadow (NoticeLook.Glow).
    // animations: the Windows setting and the app's own (Soft.Enabled).
    public static ThemeArrival Theme(ThemeMotion? motion, bool pixel, bool glows, bool highContrast, bool animations)
    {
        bool animated = animations && !highContrast;
        double fury = motion?.Spring switch { SpringTier.Firm => FirmFury, SpringTier.Jelly => JellyFury, _ => 1 };
        double separation = motion?.Spring == SpringTier.Firm ? FirmSeparation : 1;
        double pace = motion is { } m && double.IsFinite(m.Pace) ? 1 + (m.Pace - 1) / 2 : 1;
        return new(fury, separation, Math.Clamp(pace, PaceMin, PaceMax), glows || pixel ? 1 : 2, animated && !pixel, animated);
    }

    public static NoticeArrival Combine(NoticeWeight weight, ThemeArrival theme) => new(
        Math.Clamp(weight.Intensity * theme.FuryScale, FuryMin, FuryMax),
        Math.Clamp(weight.Separation * theme.SeparationScale, SeparationMin, SeparationMax),
        theme.Pace, Math.Clamp(weight.Glow, 0, GlowMax), theme.Flashes, theme.Liquid, theme.Animated, weight.Tone);
}

// The color of a notice's light: the backlight behind the capsule and the notch, the indicator's bloom and flash. It comes from the
// theme, not from a fixed near-white: the dark themes' primary text color (the prototype's own pale light), the accent where the
// theme glows, is drawn in blocks or is light (a pale glow would not show on paper), the hazard yellow of a cut-corner theme in its
// dark look, and the state's color for a warning or an error. Each is moved just far enough to be seen on the surface.
public static class NoticeLight
{
    public static ThemeColor Of(PanelPalette palette, bool glows, NoticeTone tone)
    {
        if (tone == NoticeTone.Danger) return palette.Readable(palette["danger"]);
        if (tone == NoticeTone.Warning) return palette.WarningColor;
        if (palette.Effects.Cut > 0 && palette.IsDark) return palette.Readable(palette["indicator"]);
        if (palette.Effects.Pixel > 0 || glows || !palette.IsDark) return palette.Readable(palette["accent"]);
        return palette["textPrimary"];
    }

    // The color the indicator brightens toward as it breathes: the light, unless that is the indicator's own color (a theme whose
    // accent is its indicator), when the text color stands in so that the breath still shows.
    public static ThemeColor LineFlash(PanelPalette palette, ThemeColor indicator, bool glows, NoticeTone tone)
    {
        var light = Of(palette, glows, tone);
        return Distance(light, indicator) >= 96 ? light : Distance(palette["textPrimary"], indicator) >= 96 ? palette["textPrimary"] : light;
    }

    private static int Distance(ThemeColor a, ThemeColor b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
