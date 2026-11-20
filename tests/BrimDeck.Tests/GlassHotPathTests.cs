using BrimDeck.Core;

// U11: the readability arithmetic a material frame runs (GlassRecipe.Shown, GlassReadability.Worst / SwapNow) allocates
// nothing and still matches the recipe's matrix implementation.
internal static class GlassHotPathTests
{
    internal static void Run(Action<string, bool> check)
    {
        // The reference: the recipe's matrices as they are built for Composition (GlassRecipe.Matrix through Apply).
        static (double R, double G, double B) Reference((double R, double G, double B) c, double darkVeil, double lightVeil, double palette)
        {
            var a = GlassRecipe.Apply(GlassRecipe.BaseMatrix(), c);
            double g = Math.Clamp(.2126 * c.R + .7152 * c.G + .0722 * c.B, 0, 1);
            var (slope, offset) = GlassRecipe.Coefficients(darkVeil, lightVeil, palette);
            return (Math.Clamp(a.R + slope * g + offset, 0, 1), Math.Clamp(a.G + slope * g + offset, 0, 1), Math.Clamp(a.B + slope * g + offset, 0, 1));
        }
        static double ReferenceWorst(bool darkLook, double darkVeil, double lightVeil, double palette, double[] backdrops)
        {
            double least = double.MaxValue;
            foreach (double b in backdrops)
            {
                double g = double.IsFinite(b) ? Math.Clamp(b, 0, 1) : .5;
                least = Math.Min(least, GlassReadability.Weakest(darkLook, Reference((g, g, g), darkVeil, lightVeil, palette)));
            }
            return least;
        }

        var rng = new Random(321);
        double shownError = 0, worstError = 0;
        int swapMismatch = 0;
        for (int i = 0; i < 20000; i++)
        {
            var c = (rng.NextDouble() * 1.2 - .1, rng.NextDouble() * 1.2 - .1, rng.NextDouble() * 1.2 - .1);
            double dv = rng.NextDouble() * 1.2 - .1, lv = rng.NextDouble() * 1.2 - .1, p = rng.NextDouble() * 1.2 - .1;
            var fast = GlassRecipe.Shown(c, dv, lv, p);
            var slow = Reference(c, dv, lv, p);
            shownError = Math.Max(shownError, Math.Max(Math.Abs(fast.R - slow.R), Math.Max(Math.Abs(fast.G - slow.G), Math.Abs(fast.B - slow.B))));
            double bright = rng.NextDouble(), dark = i % 97 == 0 ? double.NaN : rng.NextDouble();
            bool look = (i & 1) == 0;
            worstError = Math.Max(worstError, Math.Abs(GlassReadability.Worst(look, dv, lv, p, bright, dark) - ReferenceWorst(look, dv, lv, p, [bright, dark])));
            double current = ReferenceWorst(look, dv, lv, p, [bright, dark]), goal = ReferenceWorst(!look, dv, lv, p, [bright, dark]);
            if (GlassReadability.SwapNow(look, !look, dv, lv, p, bright, dark) != (goal >= current)) swapMismatch++;
        }
        check(FormattableString.Invariant($"The closed-form glass colour matches the recipe's matrices over 20000 random seeds ({shownError:E1})"), shownError <= 1e-9);
        check(FormattableString.Invariant($"The readability guard matches its matrix reference ({worstError:E1}, {swapMismatch} swap mismatches)"),
            worstError <= 1e-9 && swapMismatch == 0);
        check("No backdrops still reads as a mid grey", Math.Abs(GlassReadability.Worst(true, .3, .3, 0) - ReferenceWorst(true, .3, .3, 0, [.5])) <= 1e-9);

        // Zero managed allocation per material frame: warm, then 1000 frames of what GlassTurnStep runs.
        double sink = 0;
        void Frame(int i)
        {
            double t = i / 1000d;
            var shown = GlassRecipe.Shown((t, .5, 1 - t), t, 1 - t, t);
            sink += shown.R + GlassReadability.Worst(true, t, 1 - t, t, .9, .1);
            if (GlassReadability.SwapNow(i % 2 == 0, i % 2 != 0, t, 1 - t, t, .9, .1)) sink += 1;
            sink += GlassRecipe.Coefficients(t, 1 - t, t).Slope;
        }
        for (int i = 0; i < 2000; i++) Frame(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Frame(i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        check($"1000 material frames of the readability guard allocate nothing ({allocated} bytes)", allocated == 0 && double.IsFinite(sink));
    }
}
