namespace BrimDeck.Core;

// Controls the acrylic's base luminosity veil. The backdrop's readability requirement can
// raise this floor, and is independent of the user's preference.
public static class GlassMaterial
{
    public const int DefaultTransparency = 50;
    public const double DefaultCollapsedVeil = .30, DefaultExpandedVeil = .35;
    public const double MaximumVeil = .95;

    public static int NormalizeTransparency(int percent) => Math.Clamp(percent, 0, 100);

    public static double DarkBackdropLimit(int transparency) => .26 + .06 * Math.Max(0, NormalizeTransparency(transparency) - 50) / 50;

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


    // The readability floor a new reading leaves: a reading that asks for more thickens the glass to it at once (the spring
    // then carries the material there); one that asks for less thins it only as far as the reading before also allows, so one
    // dark frame of a video or a scroll does not clear the glass. Continuous: no fixed step per reading.
    public static double Settle(double wanted, double previousWanted, double current)
    {
        if (!double.IsFinite(wanted)) return current;
        if (wanted >= current || !double.IsFinite(current)) return wanted;
        return Math.Min(current, Math.Max(wanted, double.IsFinite(previousWanted) ? previousWanted : current));
    }

    public static double LuminosityOpacity(double required, double openness, int transparency)
    {
        double amount = NormalizeTransparency(transparency) / 100d;
        double baseVeil = amount <= .5 ? DefaultCollapsedVeil + (.5 - amount) * .50
            : DefaultCollapsedVeil - (amount - .5) * .20;
        double openingVeil = (DefaultExpandedVeil - DefaultCollapsedVeil) * Math.Clamp(openness, 0, 1);
        return Math.Clamp(Math.Max(required, baseVeil + openingVeil), 0, MaximumVeil);
    }
}
