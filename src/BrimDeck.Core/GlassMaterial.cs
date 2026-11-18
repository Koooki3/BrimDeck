namespace BrimDeck.Core;

// Controls the acrylic's base luminosity veil. The backdrop's readability requirement can
// raise this floor, and is independent of the user's preference.
public static class GlassMaterial
{
    public const int DefaultTransparency = 50;
    public const double DefaultCollapsedVeil = .30, DefaultExpandedVeil = .35;
    public const double MaximumVeil = .95;

    public static int NormalizeTransparency(int percent) => Math.Clamp(percent, 0, 100);

    // Quantize the grain so a whole slider drag uses a small set of frozen WPF brushes.
    public static double GrainStrength(int transparency)
    {
        double amount = NormalizeTransparency(transparency) / 100d;
        return Math.Round((1 + (.5 - amount)) * 10) / 10;
    }

    public static double RimStrength(int transparency, double openness)
    {
        double amount = NormalizeTransparency(transparency) / 100d;
        return (.85 + .30 * amount) * (1 - .20 * Math.Clamp(openness, 0, 1));
    }

    // Respond on the existing probe cadence. Two adverse readings permit a faster attack;
    // the stronger of the last two readings governs release, so alternating frames cannot pump the veil.
    public static double FollowBackdrop(double shown, double required, double previous)
    {
        if (required > shown)
            return Math.Min(required, shown + (previous > shown ? .10 : .025));
        return Math.Max(shown - .025, Math.Min(shown, Math.Max(required, previous)));
    }


    public static double LuminosityOpacity(double required, double openness, int transparency)
    {
        double amount = NormalizeTransparency(transparency) / 100d;
        double baseVeil = amount <= .5 ? DefaultCollapsedVeil + (.5 - amount) * .50
            : DefaultCollapsedVeil - (amount - .5) * .20;
        double openingVeil = (DefaultExpandedVeil - DefaultCollapsedVeil) * Math.Clamp(openness, 0, 1);
        return Math.Ceiling(Math.Clamp(Math.Max(required, baseVeil + openingVeil), 0, MaximumVeil) * 20 - 1e-9) / 20;
    }
}
