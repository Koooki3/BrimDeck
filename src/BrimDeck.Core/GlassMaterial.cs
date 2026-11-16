namespace BrimDeck.Core;

// Controls the acrylic's base luminosity veil. The backdrop's readability requirement can
// raise this floor, and is independent of the user's preference.
public static class GlassMaterial
{
    public const int DefaultTransparency = 50;
    public const double DefaultCollapsedVeil = .30, DefaultExpandedVeil = .35;
    public const double MaximumVeil = .95;

    public static int NormalizeTransparency(int percent) => Math.Clamp(percent, 0, 100);

    public static double LuminosityOpacity(double required, double openness, int transparency)
    {
        double amount = NormalizeTransparency(transparency) / 100d;
        double baseVeil = amount <= .5 ? DefaultCollapsedVeil + (.5 - amount) * .50
            : DefaultCollapsedVeil - (amount - .5) * .20;
        double openingVeil = (DefaultExpandedVeil - DefaultCollapsedVeil) * Math.Clamp(openness, 0, 1);
        return Math.Ceiling(Math.Clamp(Math.Max(required, baseVeil + openingVeil), 0, MaximumVeil) * 20 - 1e-9) / 20;
    }
}
