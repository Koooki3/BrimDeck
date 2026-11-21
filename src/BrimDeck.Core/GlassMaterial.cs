namespace BrimDeck.Core;

// The acrylic's luminosity veil for the user's clarity (stored as the glass transparency, 0..100, default 50): the clarity's own
// veil weighed against what the backdrop asks for the text (GlassClarityPolicy: at 50 each counts half, at 100 the text no longer
// thickens the glass, at 0 it does in full).
public static class GlassMaterial
{
    public const int DefaultTransparency = 50;
    public const double DefaultCollapsedVeil = .30, DefaultExpandedVeil = .35;
    public const double MaximumVeil = .95;

    public static int NormalizeTransparency(int percent) => Math.Clamp(percent, 0, 100);

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

    // Required: the luminosity opacity the text asks for (GlassRecipe.LuminosityFor, through GlassOpening while the panel opens).
    // Force: the readability is the user's explicit choice (the whole of it, whatever the clarity).
    public static double LuminosityOpacity(double required, double openness, int transparency, bool force = false)
        => GlassClarityPolicy.Veil(GlassClarityPolicy.Unit(transparency), required, openness, force);
}
