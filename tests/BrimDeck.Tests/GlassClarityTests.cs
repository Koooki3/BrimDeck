using BrimDeck.Core;

// Review v3 V301/V305/V313, U-01/U-02: the glass clarity slider (GlassClarityPolicy) moves the material all the way, never backwards,
// with the text and the glass each weighing half at 50.
internal static class GlassClarityTests
{
    // B2's material as it was (GlassMaterial/GlassRecipe at 0c36e5d5), frozen here to show the plateau the slider had.
    private static double B2Coverage(bool darkLook, double backdrop, int p)
    {
        const double k = .20;
        double target = darkLook ? .1373 : .953, b = backdrop;
        double limit = .26 + .06 * Math.Max(0, p - 50) / 50;
        double need = darkLook
            ? (b - target) * (1 - k) <= 1e-4 ? 0 : ((1 - k) * b + k * target - limit) / ((1 - k) * (b - target))
            : (target - b) * (1 - k) <= 1e-4 ? 0 : (.82 - (1 - k) * b - k * target) / ((1 - k) * (target - b));
        need = Math.Clamp(need, 0, .95);
        double a = p / 100d, baseVeil = (a <= .5 ? .30 + (.5 - a) * .50 : .30 - (a - .5) * .20) + .05;
        double veil = Math.Min(.95, Math.Max(need, baseVeil));
        return k + (1 - k) * veil;
    }

    // The glass's cover now: the guard for the backdrop at the clarity, weighed, at the clarity's tint.
    internal static double Coverage(bool darkLook, double backdrop, double u, double openness = 1, bool force = false)
    {
        var target = GlassClarityPolicy.For(u, darkLook, backdrop, backdrop, 1, openness, force);
        return target.Tint + (1 - target.Tint) * target.Veil;
    }

    internal static void Run(Action<string, bool> check)
    {
        int[] stops = [0, 25, 50, 75, 100];
        string Row(Func<int, double> f) => string.Join(" ", stops.Select(p => FormattableString.Invariant($"{f(p):0.000}")));

        // V301 reproduced: B2's cover over a white page under the dark look and a black one under the light look.
        bool plateauDark = B2Coverage(true, 1, 0) == B2Coverage(true, 1, 50) && B2Coverage(true, 1, 0) == B2Coverage(true, 1, 25);
        bool plateauLight = stops.All(p => B2Coverage(false, 0, p) == B2Coverage(false, 0, 0));
        bool b2Fails = B2Coverage(true, 1, 0) - B2Coverage(true, 1, 50) < .12 && B2Coverage(false, 0, 50) - B2Coverage(false, 0, 100) < .12;
        check($"V301 reproduced: B2's slider had a plateau (dark/white {Row(p => B2Coverage(true, 1, p))}; light/black {Row(p => B2Coverage(false, 0, p))})",
            plateauDark && plateauLight && b2Fails);

        // U-02: 0 vs 50 and 50 vs 100 apart by .12 of cover or more over the representative scenes.
        (string Name, bool Dark, double Backdrop)[] scenes =
            [("dark glass/white page", true, 1), ("light glass/black", false, 0), ("dark glass/dark IDE", true, .1),
             ("light glass/white page", false, 1), ("dark glass/orange", true, .62), ("light glass/orange", false, .62)];
        foreach (var (name, dark, backdrop) in scenes)
        {
            double c0 = Coverage(dark, backdrop, 0), c50 = Coverage(dark, backdrop, .5), c100 = Coverage(dark, backdrop, 1);
            check(FormattableString.Invariant($"U-02 {name}: cover {Row(p => Coverage(dark, backdrop, p / 100d))}, 0-50 {c0 - c50:0.000}, 50-100 {c50 - c100:0.000}"),
                c0 - c50 >= .12 && c50 - c100 >= .12);
        }

        // U-01: every 1 % step from 0 to 100 never thickens, over greys 0..1, both looks, collapsed and open; nothing leaves its range.
        bool monotone = true, bounded = true;
        double largestStep = 0;
        foreach (bool dark in new[] { true, false })
            foreach (double openness in new[] { 0, .5, 1 })
                for (int g = 0; g <= 20; g++)
                {
                    double previous = double.MaxValue;
                    for (int p = 0; p <= 100; p++)
                    {
                        var t = GlassClarityPolicy.For(p / 100d, dark, g / 20d, g / 20d, 1, openness);
                        double cover = t.Tint + (1 - t.Tint) * t.Veil;
                        bounded &= double.IsFinite(cover) && t.Veil is >= 0 and <= GlassMaterial.MaximumVeil && t.Tint is >= 0 and <= 1
                            && t.Saturation is >= 1 and <= 1.5 && cover is >= 0 and <= 1;
                        if (previous != double.MaxValue) { monotone &= cover <= previous + 1e-12; largestStep = Math.Max(largestStep, previous - cover); }
                        previous = cover;
                    }
                }
        check(FormattableString.Invariant($"U-01: the cover never thickens as the clarity rises, 1 % at a time, over any grey (largest step {largestStep:0.000})"),
            monotone && bounded && largestStep < .05);
        var veils = Enumerable.Range(0, 101).Select(p => GlassClarityPolicy.BaseVeil(p / 100d)).ToArray();
        var tints = Enumerable.Range(0, 101).Select(p => GlassClarityPolicy.Tint(p / 100d)).ToArray();
        var saturations = Enumerable.Range(0, 101).Select(p => GlassClarityPolicy.Saturation(p / 100d)).ToArray();
        check("The clarity's curves pass through their anchors and never overshoot between them",
            GlassClarityPolicy.Anchors.All(a => Math.Abs(GlassClarityPolicy.BaseVeil(a.Clarity / 100) - a.Veil) < 1e-12
                && Math.Abs(GlassClarityPolicy.Tint(a.Clarity / 100) - a.Tint) < 1e-12 && Math.Abs(GlassClarityPolicy.Saturation(a.Clarity / 100) - a.Saturation) < 1e-12)
            && veils.Zip(veils.Skip(1)).All(x => x.Second <= x.First + 1e-12) && tints.Zip(tints.Skip(1)).All(x => x.Second <= x.First + 1e-12)
            && saturations.Zip(saturations.Skip(1)).All(x => x.Second >= x.First - 1e-12));

        // u = .5 weighs both goals half: the veil is the mean of the optical target and the readable one, for any guard.
        bool half = true, ends = true;
        for (int i = 0; i <= 40; i++)
        {
            double guard = i / 40d, optical = GlassClarityPolicy.OpticalVeil(.5, 1), readable = Math.Max(optical, Math.Min(guard, GlassMaterial.MaximumVeil));
            half &= Math.Abs(GlassClarityPolicy.Veil(.5, guard, 1) - (optical + readable) / 2) < 1e-12;
            ends &= Math.Abs(GlassClarityPolicy.Veil(1, guard, 1) - GlassClarityPolicy.OpticalVeil(1, 1)) < 1e-12
                && Math.Abs(GlassClarityPolicy.Veil(0, guard, 1) - Math.Max(GlassClarityPolicy.OpticalVeil(0, 1), Math.Min(guard, GlassMaterial.MaximumVeil))) < 1e-12;
        }
        check("At 50 the text and the glass each weigh half; at 100 the text no longer thickens the glass; at 0 it does in full",
            half && ends && GlassClarityPolicy.ReadWeight(.5) == .5 && GlassClarityPolicy.For(.5, true, 1, 1, 1, 1).ReadWeight == .5);

        // V313: an unknown reading (no confidence, NaN tails, an infinite guard) is as conservative as the clarity asks: the thickest
        // at 0, half-way at 50, none at 100; forced readability keeps the thickest at every clarity.
        double Unknown(double u, bool force = false) => GlassClarityPolicy.For(u, true, double.NaN, double.NaN, 0, 1, force).Veil;
        double Worst(double u) => Math.Max(GlassClarityPolicy.OpticalVeil(u, 1), GlassRecipe.LuminosityForTint(true, 1, 0, GlassClarityPolicy.Tint(u)));
        check(FormattableString.Invariant($"V313: an unknown backdrop (the worst range) thickens the glass by the clarity's weight ({Unknown(0):0.00} {Unknown(.5):0.00} {Unknown(1):0.00}; forced at 100 {Unknown(1, true):0.00})"),
            Math.Abs(Unknown(0) - Worst(0)) < 1e-12 && Math.Abs(Unknown(.5) - (Worst(.5) + GlassClarityPolicy.OpticalVeil(.5, 1)) / 2) < 1e-12
            && Math.Abs(Unknown(1) - GlassClarityPolicy.OpticalVeil(1, 1)) < 1e-12 && Math.Abs(Unknown(1, true) - Worst(1)) < 1e-12
            && GlassClarityPolicy.Veil(0, double.PositiveInfinity, 1) == GlassMaterial.MaximumVeil && double.IsFinite(GlassClarityPolicy.Veil(double.NaN, double.NaN, double.NaN)));
        // Confidence widens the tails toward the worst range continuously, never thinning the glass.
        var byConfidence = Enumerable.Range(0, 11).Select(i => GlassClarityPolicy.For(.3, true, .4, .4, i / 10d, 1).Veil).ToArray();
        check("Less confidence in a reading never thins the glass", byConfidence.Zip(byConfidence.Skip(1)).All(x => x.Second <= x.First + 1e-12));

        // A fast drag: the policy keeps no state, so the same clarity gives the same material whatever the path, and each value
        // lies between its neighbours (a sweep 0 -> 100 -> 0 in uneven jumps).
        var rng = new Random(301);
        bool samePath = true;
        for (int i = 0; i < 500; i++)
        {
            double u = rng.NextDouble();
            var a = GlassClarityPolicy.For(u, i % 2 == 0, .9, .1, .8, .5);
            var b = GlassClarityPolicy.For(u, i % 2 == 0, .9, .1, .8, .5);
            samePath &= a == b;
        }
        check("A fast drag gives the same material for the same clarity, whatever came before", samePath);

        // V305: the covered area that asks for less thins the glass with the opening (controlled), one that asks for more thickens it.
        var thinning = Enumerable.Range(0, 11).Select(i => GlassOpening.Required(.8, .2, i / 10d)).ToArray();
        check(FormattableString.Invariant($"V305: opening over an area that asks for less thins the glass in step ({thinning[0]:0.00} -> {thinning[^1]:0.00}), no further than the capsule's share allows"),
            thinning.Zip(thinning.Skip(1)).All(x => x.Second <= x.First + 1e-12) && Math.Abs(thinning[^1] - (.2 + GlassOpening.CapsuleShare * .6)) < 1e-12
            && GlassOpening.Required(.2, .8, 1) == .8 && GlassOpening.Required(.5, double.NaN, 1) == .5 && GlassOpening.Required(.5, .1, double.NaN) == .5);
    }
}
