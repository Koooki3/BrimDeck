using BrimDeck.Core;

// The persistent glass graph's arithmetic (GlassRecipe) and the text swap it drives (GlassReadability), offscreen.
internal static class GlassGraphTests
{
    internal static void Run(Action<string, bool> check)
    {
        // Exactness: any luminosity opacity is the cross-fade of the two fixed branches where neither branch leaves 0..1 (greys,
        // soft colours). A vivid colour can push the high branch past 0..1 in one channel; that branch is then clipped before the
        // cross-fade (if the compositor keeps 8-bit intermediates), which the offscreen mirror models: bounded, and measured here.
        double Worst(IEnumerable<(double, double, double)> colours, double[] veils)
        {
            double worst = 0;
            foreach (bool dark in new[] { true, false })
                foreach (double veil in veils)
                    foreach (var c in colours)
                    {
                        var direct = GlassRecipe.Apply(GlassRecipe.Matrix(GlassRecipe.For(dark) with { LuminosityOpacity = veil }), c);
                        var graph = GlassRecipe.Shown(c, veil, veil, dark ? 0 : 1);
                        worst = Math.Max(worst, Math.Max(Math.Abs(direct.R - graph.R), Math.Max(Math.Abs(direct.G - graph.G), Math.Abs(direct.B - graph.B))));
                    }
            return worst;
        }
        double[] veils = [0, .2, .3, .35, .55, .8, .95];
        double soft = Worst([(.1, .1, .1), (.5, .5, .5), (.9, .9, .9), (.45, .5, .55), (.6, .5, .45), (.3, .35, .4)], veils);
        double vividUsual = Worst([(.2, .4, .6), (.7, .3, .2), (.9, .1, .1), (.1, .2, .9)], [.2, .3, .35, .45]);
        double vividThick = Worst([(.2, .4, .6), (.7, .3, .2), (.9, .1, .1), (.1, .2, .9)], veils);
        check(FormattableString.Invariant($"The persistent glass graph reproduces the recipe over greys and soft colours ({soft:E1})"), soft < 1e-5);
        check(FormattableString.Invariant($"Over vivid colours it stays within {vividUsual:0.000} of the recipe at the usual veils and {vividThick:0.000} at the thickest"),
            vividUsual <= .06 && vividThick <= .12);

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

        // The floors keep the body text readable over the backdrop they were asked for (sRGB greys, WCAG contrast).
        double least = double.MaxValue;
        foreach (double b in Enumerable.Range(0, 21).Select(i => i / 20d))
        {
            double darkVeil = GlassMaterial.LuminosityOpacity(GlassRecipe.LuminosityFor(true, b, b), 1, 50);
            double lightVeil = GlassMaterial.LuminosityOpacity(GlassRecipe.LuminosityFor(false, b, b), 1, 50);
            least = Math.Min(least, GlassReadability.Weakest(true, GlassRecipe.Shown((b, b, b), darkVeil, lightVeil, 0)));
            least = Math.Min(least, GlassReadability.Weakest(false, GlassRecipe.Shown((b, b, b), darkVeil, lightVeil, 1)));
        }
        check(FormattableString.Invariant($"At rest each look's body text reads at 4.5:1 or more over any grey it adapted to ({least:0.00}:1)"), least >= 4.5);

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
