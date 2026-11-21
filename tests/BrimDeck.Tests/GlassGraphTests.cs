using BrimDeck.Core;

// The persistent glass graph's arithmetic (GlassRecipe) and the text swap it drives (GlassReadability), offscreen.
internal static class GlassGraphTests
{
    internal static void Run(Action<string, bool> check)
    {
        // Exactness (review v3 §4.6): the graph is the recipe's matrix itself for any veil, tint opacity and saturation, vivid
        // colours included (the composite clamps once, as the matrix does).
        double Worst(IEnumerable<(double, double, double)> colours, double[] veils)
        {
            double worst = 0;
            foreach (bool dark in new[] { true, false })
                foreach (double veil in veils)
                    foreach (int clarity in new[] { 0, 25, 50, 75, 100 })
                        foreach (var c in colours)
                        {
                            double k = GlassClarityPolicy.Tint(clarity), s = GlassClarityPolicy.Saturation(clarity);
                            var direct = GlassRecipe.Apply(GlassRecipe.Matrix(GlassRecipe.For(dark, k, s) with { LuminosityOpacity = veil }), c);
                            var graph = GlassRecipe.Shown(c, veil, veil, dark ? 0 : 1, k, s);
                            worst = Math.Max(worst, Math.Max(Math.Abs(direct.R - graph.R), Math.Max(Math.Abs(direct.G - graph.G), Math.Abs(direct.B - graph.B))));
                        }
            return worst;
        }
        double[] veils = [0, .02, .2, .3, .35, .55, .8, .95];
        double soft = Worst([(.1, .1, .1), (.5, .5, .5), (.9, .9, .9), (.45, .5, .55), (.6, .5, .45), (.3, .35, .4)], veils);
        double vivid = Worst([(.2, .4, .6), (.7, .3, .2), (.9, .1, .1), (.1, .2, .9), (1, .5, 0), (0, 1, 0)], veils);
        check(FormattableString.Invariant($"The persistent glass graph reproduces the recipe over greys and soft colours at every clarity ({soft:E1})"), soft < 1e-5);
        check(FormattableString.Invariant($"Over vivid colours too ({vivid:E1})"), vivid < 1e-5);

        // Greys stay grey and the recipe's own luma estimate matches the matrix.
        double lumaError = 0;
        foreach (double g in new[] { 0, .25, .5, .75, 1 })
            foreach (double veil in new[] { .3, .6, .9 })
            {
                var look = GlassRecipe.Dark with { LuminosityOpacity = veil };
                var shown = GlassRecipe.Apply(GlassRecipe.Matrix(look), (g, g, g));
                lumaError = Math.Max(lumaError, Math.Abs(.2126 * shown.R + .7152 * shown.G + .0722 * shown.B - GlassRecipe.Luma(look, g)));
            }
        check("The recipe's luma estimate matches its matrix over greys", lumaError < 1e-5);

        // The readability floor is continuous and monotone in the backdrop.
        var floors = Enumerable.Range(0, 101).Select(i => GlassRecipe.LuminosityFor(true, i / 100d, 0)).ToArray();
        check("The dark look's floor rises smoothly with a brighter backdrop, with no steps",
            floors.Zip(floors.Skip(1)).All(p => p.Second >= p.First - 1e-12) && floors.Distinct().Count() > 60 && floors[^1] <= GlassRecipe.LuminosityMost);
        check("A broken reading asks for the thickest glass", GlassRecipe.LuminosityFor(false, .5, double.NaN) == GlassRecipe.LuminosityMost);

        // The floors keep the body text readable over the backdrop they were asked for (sRGB greys, WCAG contrast) where the text
        // counts whole: clarity 0, or readability forced at any clarity.
        double Least(int clarity, bool force)
        {
            double least = double.MaxValue, k = GlassClarityPolicy.Tint(clarity), s = GlassClarityPolicy.Saturation(clarity);
            foreach (double b in Enumerable.Range(0, 21).Select(i => i / 20d))
            {
                double darkVeil = GlassMaterial.LuminosityOpacity(GlassRecipe.LuminosityFor(true, b, b, clarity), 1, clarity, force);
                double lightVeil = GlassMaterial.LuminosityOpacity(GlassRecipe.LuminosityFor(false, b, b, clarity), 1, clarity, force);
                least = Math.Min(least, GlassReadability.Weakest(true, GlassRecipe.Shown((b, b, b), darkVeil, lightVeil, 0, k, s)));
                least = Math.Min(least, GlassReadability.Weakest(false, GlassRecipe.Shown((b, b, b), darkVeil, lightVeil, 1, k, s)));
            }
            return least;
        }
        double atZero = Least(0, false), forced = Enumerable.Range(0, 11).Min(i => Least(i * 10, true));
        check(FormattableString.Invariant($"At clarity 0, or with readability forced, each look's body text reads at 4.5:1 or more over any grey it adapted to ({atZero:0.00}:1, forced {forced:0.00}:1)"),
            atZero >= 4.5 && forced >= 4.5);
        double atHalf = Least(50, false), atClear = Least(100, false);
        check(FormattableString.Invariant($"Higher clarity trades contrast for clearness, never the other way ({atHalf:0.00}:1 at 50, {atClear:0.00}:1 at 100)"),
            atHalf <= atZero + 1e-9 && atClear <= atHalf + 1e-9 && atHalf >= 3);

        // WCAG arithmetic.
        check("WCAG contrast: black on white is 21:1 and a colour against itself 1:1",
            Math.Abs(GlassReadability.Contrast(GlassReadability.Luminance((0, 0, 0)), GlassReadability.Luminance((1, 1, 1))) - 21) < 1e-9
            && GlassReadability.Contrast(.3, .3) == 1);
        check("Text alpha is laid over the glass before measuring",
            Math.Abs(GlassReadability.Over(new GlassReadability.Rgba(0, 0, 0, .5), (1, 1, 1)).R - .5) < 1e-12);

        // The swap: once, at the frame the goal palette's text reads at least as well; never when nothing changes.
        foreach (bool toLight in new[] { true, false })
        {
            double b = toLight ? .9 : .1;
            double darkVeil = GlassMaterial.LuminosityOpacity(GlassRecipe.LuminosityFor(true, b, b), 0, 50);
            double lightVeil = GlassMaterial.LuminosityOpacity(GlassRecipe.LuminosityFor(false, b, b), 0, 50);
            int swaps = 0; double at = -1, weakest = double.MaxValue;
            bool showingDark = toLight;
            for (int i = 0; i <= 100; i++)
            {
                double palette = toLight ? i / 100d : 1 - i / 100d;
                if (GlassReadability.SwapNow(showingDark, !toLight, darkVeil, lightVeil, palette, b)) { swaps++; at = i; showingDark = !toLight; }
                weakest = Math.Min(weakest, GlassReadability.Weakest(showingDark, GlassRecipe.Shown((b, b, b), darkVeil, lightVeil, palette)));
            }
            double fixedShare = .48;
            check(FormattableString.Invariant($"A turn {(toLight ? "to light" : "to dark")} swaps the text exactly once ({at}% of the way), keeping the weakest frame at {weakest:0.00}:1"),
                swaps == 1 && at > 0 && at < 100 && weakest >= 1.5 && fixedShare > 0);
        }
        check("No swap is asked for when the text already shows the goal",
            !GlassReadability.SwapNow(true, true, .5, .5, .5, .5));
    }
}
