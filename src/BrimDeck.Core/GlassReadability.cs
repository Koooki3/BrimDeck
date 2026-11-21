namespace BrimDeck.Core;

// The text on liquid glass, checked the way WCAG 2.2 measures contrast: each sRGB channel linearised, the relative luminance
// Y = .2126 R + .7152 G + .0722 B, and (Y1 + .05) / (Y2 + .05). The glass's colour comes from the recipe (GlassRecipe.Shown)
// over the backdrop the probe reports; a text colour with alpha is laid over it first, as WPF draws it (in sRGB).
//   A palette turn swaps the text once, at the frame where the new palette's text reads at least as well as the old one's
// over the glass as it is then (SwapNow): that is the frame where the weaker of the two is best, instead of a fixed share of
// the material's travel. The text never fades between its two colours (a half-way grey reads worse than either).
public static class GlassReadability
{
    public readonly record struct Rgba(double R, double G, double B, double A = 1)
    {
        public static Rgba Hex(uint argb) => new(((argb >> 16) & 0xFF) / 255d, ((argb >> 8) & 0xFF) / 255d, (argb & 0xFF) / 255d, ((argb >> 24) & 0xFF) / 255d);
    }

    // The two looks' text (the themes' textPrimary, textSecondary and textTertiary: liquid-glass-dark.json / -light.json).
    public static readonly Rgba[] DarkLookText = [Rgba.Hex(0xFFF6F7F9), Rgba.Hex(0xFFE5E5EA), Rgba.Hex(0xFFC6C6C9)];
    public static readonly Rgba[] LightLookText = [Rgba.Hex(0xFF101217), Rgba.Hex(0xC6000000), Rgba.Hex(0xA0000000)];
    // The roles that must read as body text (primary and secondary); the tertiary text is decoration-strength.
    public const int BodyRoles = 2;
    public const double BodyMinimum = 4.5;

    public static double Linear(double c) => c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
    public static double Luminance((double R, double G, double B) c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
    public static double Contrast(double y1, double y2) => (Math.Max(y1, y2) + .05) / (Math.Min(y1, y2) + .05);

    // The text's colour as shown over an opaque ground (alpha laid over in sRGB, as WPF composes).
    public static (double R, double G, double B) Over(Rgba text, (double R, double G, double B) ground)
        => (ground.R + (text.R - ground.R) * text.A, ground.G + (text.G - ground.G) * text.A, ground.B + (text.B - ground.B) * text.A);

    public static double Contrast(Rgba text, (double R, double G, double B) ground) => Contrast(Luminance(Over(text, ground)), Luminance(ground));

    // The weakest body text of a palette over a ground.
    public static double Weakest(bool darkLook, (double R, double G, double B) ground)
    {
        var text = darkLook ? DarkLookText : LightLookText;
        double least = double.MaxValue;
        for (int i = 0; i < BodyRoles; i++) least = Math.Min(least, Contrast(text[i], ground));
        return least;
    }

    // The weakest body text of a palette over the glass shown over each of the grey backdrops. Every material frame runs it, so
    // it allocates nothing: the backdrops come as a span (a few arguments stay on the stack) and the composite's amounts are
    // worked out once for all of them (Amounts: the glass's tint opacity and saturation included).
    public static double Worst(bool darkLook, double darkVeil, double lightVeil, double palette, params ReadOnlySpan<double> backdrops)
        => Worst(darkLook, GlassRecipe.Coefficients(darkVeil, lightVeil, palette), backdrops);

    public static double Worst(bool darkLook, GlassRecipe.Amounts amounts, params ReadOnlySpan<double> backdrops)
    {
        if (backdrops.IsEmpty) return Weakest(darkLook, Ground(.5, amounts));
        double least = double.MaxValue;
        foreach (double b in backdrops) least = Math.Min(least, Weakest(darkLook, Ground(b, amounts)));
        return least;
    }

    // Whether the text should now show the goal palette's colours, the glass shown over each of the grey backdrops (the
    // probe's bright and dark tails): the goal's weakest text over its worst backdrop reads at least as well as the current one's.
    public static bool SwapNow(bool showingDark, bool goalDark, double darkVeil, double lightVeil, double palette, params ReadOnlySpan<double> backdrops)
        => SwapNow(showingDark, goalDark, GlassRecipe.Coefficients(darkVeil, lightVeil, palette), backdrops);

    public static bool SwapNow(bool showingDark, bool goalDark, GlassRecipe.Amounts amounts, params ReadOnlySpan<double> backdrops)
    {
        if (showingDark == goalDark) return false;
        if (backdrops.IsEmpty) backdrops = [.5];
        double current = double.MaxValue, goal = double.MaxValue;
        foreach (double b in backdrops)
        {
            var ground = Ground(b, amounts);
            current = Math.Min(current, Weakest(showingDark, ground));
            goal = Math.Min(goal, Weakest(goalDark, ground));
        }
        return goal >= current;
    }

    // The glass over a grey backdrop (an unreadable reading taken as mid grey).
    private static (double R, double G, double B) Ground(double backdrop, GlassRecipe.Amounts amounts)
    {
        double g = double.IsFinite(backdrop) ? Math.Clamp(backdrop, 0, 1) : .5;
        return GlassRecipe.Shown((g, g, g), amounts);
    }
}
