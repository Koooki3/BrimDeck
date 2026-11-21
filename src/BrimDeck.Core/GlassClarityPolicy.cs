namespace BrimDeck.Core;

// Liquid glass's clarity (the user's 0..100, stored as DeckSettings.GlassTransparency) turned into one material target (review
// v3 §4.2-4.4, V301). u = clarity / 100 weighs the two goals: the text (w_read = 1 - u) and the glass (w_optical = u); at 50 each
// counts exactly half.
//   Optical target: the veil, tint opacity and saturation of the clarity's anchors, joined by a monotone cubic (Fritsch-Carlson):
//     no overshoot between anchors, so each value stays between its two neighbours and the curve never turns back.
//   Readable target: the optical veil raised to what the backdrop asks for the text (the guard, GlassRecipe.LuminosityFor).
//   The veil is the weighted mean of the two: optical + w_read x max(0, guard - optical). Under the recipe the cover the guard asks
//     for does not depend on the tint (the text's limit fixes it), so the glass's cover is u x optical + (1 - u) x readable: it
//     never thickens as the clarity rises (U-01).
//   A reading that could not be trusted (none yet, late, lost) asks for the thickest guard; how much that thickens the glass follows
//     the same weight (V313): all of it at 0, half at 50, none at 100. Confidence (0..1) widens the tails toward the worst range the
//     same way GlassSampleValidity's protection does.
//   ForceReadability (an explicit choice, never the default) takes the readable target whole whatever the clarity. High contrast
//     and Windows' transparency effects switched off turn liquid glass off altogether (PanelTheme.GlassAvailable): that stays.
// The anchors are trial values (v3 §4.3 candidates), to be tuned on screen; 50 keeps the stored default, not the old pixels.
public static class GlassClarityPolicy
{
    public readonly record struct Anchor(double Clarity, double Veil, double Tint, double Saturation);
    public readonly record struct Target(double Veil, double Tint, double Saturation, double ReadWeight);

    public static readonly Anchor[] Anchors =
    [
        new(0, .82, .36, 1.05),
        new(25, .55, .27, 1.18),
        new(50, .30, .17, 1.30),
        new(75, .13, .09, 1.36),
        new(100, .02, .02, 1.40),
    ];
    public const double DefaultTint = .17, DefaultSaturation = 1.30;
    // The opened panel's veil above the capsule's (the old .30 -> .35).
    public const double OpeningVeil = .05;

    public static double Unit(int clarity) => GlassMaterial.NormalizeTransparency(clarity) / 100d;
    public static double ReadWeight(double u) => 1 - Clamp01(u);

    public static double BaseVeil(double u) => Curve(u, VeilKnots);
    public static double Tint(double u) => Curve(u, TintKnots);
    public static double Saturation(double u) => Curve(u, SaturationKnots);
    public static double Tint(int clarity) => Tint(Unit(clarity));
    public static double Saturation(int clarity) => Saturation(Unit(clarity));

    // The optical target's veil at the opening shown (0 collapsed, 1 open).
    public static double OpticalVeil(double u, double openness)
        => Math.Clamp(BaseVeil(u) + OpeningVeil * Clamp01(openness), 0, GlassMaterial.MaximumVeil);

    // The veil for a guard (the luminosity opacity the text asks for; not finite: nothing known, the thickest).
    public static double Veil(double u, double guard, double openness, bool force = false)
    {
        double optical = OpticalVeil(u, openness);
        double readable = Math.Max(optical, double.IsFinite(guard) ? Math.Clamp(guard, 0, GlassMaterial.MaximumVeil) : GlassMaterial.MaximumVeil);
        double veil = force ? readable : optical + ReadWeight(u) * (readable - optical);
        return Math.Clamp(veil, 0, GlassMaterial.MaximumVeil);
    }

    // The whole target for a look over a backdrop whose tails are bright and dark, read with a confidence (1 trusted, 0 unknown).
    public static Target For(double u, bool darkLook, double bright, double dark, double confidence, double openness, bool force = false)
    {
        u = Clamp01(u);
        double level = 1 - (double.IsFinite(confidence) ? Clamp01(confidence) : 0);
        double b = double.IsFinite(bright) ? Clamp01(bright) : 1, d = double.IsFinite(dark) ? Clamp01(dark) : 0;
        b += (1 - b) * level; d *= 1 - level;
        double guard = GlassRecipe.LuminosityForTint(darkLook, b, d, Tint(u));
        return new(Veil(u, guard, openness, force), Tint(u), Saturation(u), force ? 1 : ReadWeight(u));
    }

    private static double Clamp01(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;

    // Monotone cubic Hermite through the anchors (Fritsch-Carlson tangents), on u in 0..1.
    private readonly record struct Knots(double[] X, double[] Y, double[] M);
    private static readonly Knots VeilKnots = Build(a => a.Veil), TintKnots = Build(a => a.Tint), SaturationKnots = Build(a => a.Saturation);

    private static Knots Build(Func<Anchor, double> value)
    {
        int n = Anchors.Length;
        var x = Anchors.Select(a => a.Clarity / 100).ToArray();
        var y = Anchors.Select(value).ToArray();
        var delta = new double[n - 1];
        for (int i = 0; i < n - 1; i++) delta[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
        var m = new double[n];
        m[0] = delta[0]; m[n - 1] = delta[n - 2];
        for (int i = 1; i < n - 1; i++) m[i] = delta[i - 1] * delta[i] <= 0 ? 0 : (delta[i - 1] + delta[i]) / 2;
        for (int i = 0; i < n - 1; i++)
        {
            if (delta[i] == 0) { m[i] = m[i + 1] = 0; continue; }
            double a = m[i] / delta[i], b = m[i + 1] / delta[i], s = a * a + b * b;
            if (s > 9) { double t = 3 / Math.Sqrt(s); m[i] = t * a * delta[i]; m[i + 1] = t * b * delta[i]; }
        }
        return new(x, y, m);
    }

    private static double Curve(double u, Knots k)
    {
        u = Clamp01(u);
        int i = 0;
        while (i < k.X.Length - 2 && u > k.X[i + 1]) i++;
        double h = k.X[i + 1] - k.X[i], t = (u - k.X[i]) / h, t2 = t * t, t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * k.Y[i] + (t3 - 2 * t2 + t) * h * k.M[i] + (-2 * t3 + 3 * t2) * k.Y[i + 1] + (t3 - t2) * h * k.M[i + 1];
    }
}
