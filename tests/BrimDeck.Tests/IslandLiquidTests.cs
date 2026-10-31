using BrimDeck.Core.Notices;

// The island's own motion when a notice arrives (IslandLiquid): the prototype's random numbers and curves, and the bounds each
// form keeps to.
internal static class IslandLiquidTests
{
    internal static void Run(Action<string, bool> check)
    {
        var rand = IslandLiquid.Random(IslandLiquid.DesignSeed);
        double[] expected = [0.6768464390188456, 0.7959222549106926, 0.6443384203594178, 0.024221934378147125, 0.07822010153904557];
        check("Island liquid: the random numbers of group 4 are the prototype's", expected.All(value => Math.Abs(rand() - value) < 1e-15));
        long seed = 11;
        for (int i = 0; i < 4; i++) seed = (seed * 48271 + 11) % 2147483647;
        check("Island liquid: the design's seed is the fourth step of its sequence from 11", seed == IslandLiquid.DesignSeed);
        check("Island liquid: the glow flashes twice within 0.75 s", IslandLiquid.GlowAt(0) == 0 && Math.Abs(IslandLiquid.GlowAt(.12) - 1) < 1e-9
            && Math.Abs(IslandLiquid.GlowAt(.3) - .12) < 1e-9 && Math.Abs(IslandLiquid.GlowAt(.42) - 1) < 1e-9 && IslandLiquid.GlowAt(.75) == 0);
        check("Island liquid: the indicator breathes twice over 1.8 s", IslandLiquid.BreathAt(0) == 0 && Math.Abs(IslandLiquid.BreathAt(.45) - 1) < 1e-9
            && IslandLiquid.BreathAt(.9) < 1e-9 && IslandLiquid.BreathAt(1.8) == 0);

        Capsule(check);
        Notch(check);
        Line(check);
    }

    private static void Capsule(Action<string, bool> check)
    {
        var capsule = new IslandLiquid(IslandForm.Capsule, 196, 32);
        var outline = capsule.Outline;
        check("Island capsule: it plays for 1.75 s", Math.Abs(capsule.Total - 1.75) < 1e-9);
        check("Island capsule: its outline runs all round, from the middle of its top edge back to it",
            outline.Count > 100 && Math.Abs(outline[0].X - 98) < 1e-9 && outline[0].Y == 0 && Math.Abs(outline[^1].X - 98) < 1e-9 && Math.Abs(outline[^1].Y) < 1e-9
            && Math.Abs(capsule.OutlineLength - (2 * (196 - 32) + Math.PI * 32)) < 1);
        double most = 0;
        bool band = true, ends = false;
        var beads = new List<IslandLiquid.Circle>();
        for (double t = 0; t <= capsule.Total; t += 1 / 120d)
        {
            foreach (var p in outline)
            {
                double o = capsule.EdgeOut(p.S, t);
                most = Math.Max(most, o);
                band &= o >= -1e-9 && o <= IslandLiquid.Band + 1e-9;
                // The middles of the round ends swell.
                if (Math.Abs(p.Y - 16) < 1 && o > 2) ends = true;
            }
            capsule.Beads(t, beads);
            band &= beads.All(b => b.R <= IslandLiquid.Band);
        }
        check("Island capsule: its round ends swell and the bulges stay within 5 DIPs of its outline", most > 2 && band && ends);
        foreach (double width in new[] { 196d, 600, 1200 })
        {
            var wide = new IslandLiquid(IslandForm.Capsule, width, 32);
            var top = wide.Outline[0];
            var bottom = wide.Outline.MinBy(p => Math.Abs(p.S - wide.OutlineLength / 2));
            double peakTop = 0, peakBottom = 0;
            for (double t = 0; t <= wide.Total; t += 1 / 120d) { peakTop = Math.Max(peakTop, wide.EdgeOut(top.S, t)); peakBottom = Math.Max(peakBottom, wide.EdgeOut(bottom.S, t)); }
            check($"Island capsule: on a capsule {width} wide some ripples reach the middles of its top and bottom", peakTop > 1 && peakBottom > 1);
        }
        check("Island capsule: at its end it is still again", outline.All(p => capsule.EdgeOut(p.S, capsule.Total) < .01));
        var mirrored = new IslandLiquid(IslandForm.Capsule, 376, 32);
        bool symmetric = true;
        for (double t = 0; t <= mirrored.Total; t += 1 / 60d)
            foreach (var p in mirrored.Outline) symmetric &= Math.Abs(mirrored.EdgeOut(p.S, t) - mirrored.EdgeOut(mirrored.OutlineLength / 2 - p.S, t)) < 1e-6;
        check("Island capsule: its top and its bottom move alike, mirrored", symmetric);
        var first = new IslandLiquid(IslandForm.Capsule, 376, 32);
        var again = new IslandLiquid(IslandForm.Capsule, 376, 32);
        check("Island capsule: the same seed plays the same motion", first.Outline.All(p => first.EdgeOut(p.S, .3) == again.EdgeOut(p.S, .3)));
        var still = new IslandLiquid(IslandForm.Capsule, 376, 32, liquid: false);
        check("Island capsule: without the liquid effect it keeps still", still.Outline.All(p => still.EdgeOut(p.S, .3) == 0));
    }

    private static void Notch(Action<string, bool> check)
    {
        var notch = new IslandLiquid(IslandForm.Notch, 238, 32);
        var outline = notch.Outline;
        check("Island notch: its outline runs from the right shoulder at the screen's edge to the left one",
            outline.Count > 100 && Math.Abs(outline[0].X - 238) < 1e-9 && outline[0].Y == 0 && Math.Abs(outline[^1].X) < 1e-9 && Math.Abs(outline[^1].Y) < 1e-9);
        double most = 0;
        bool band = true, shoulders = true;
        var beads = new List<IslandLiquid.Circle>();
        for (double t = 0; t <= notch.Total; t += 1 / 120d)
        {
            foreach (var p in outline)
            {
                double o = notch.EdgeOut(p.S, t);
                most = Math.Max(most, o);
                band &= o >= -1e-9 && o <= IslandLiquid.Band + 1e-9;
                if (Math.Min(p.S, notch.OutlineLength - p.S) < 1e-9) shoulders &= Math.Abs(o) < 1e-9;
            }
            notch.Beads(t, beads);
            band &= beads.All(b => b.R <= IslandLiquid.Band);
        }
        check("Island notch: its edge swells, within 5 DIPs of its outline, and stays put where it meets the screen", most > 2 && band && shoulders);
        check("Island notch: its backlight band is one closed outline twice as long as the outline", notch.NotchBand(11).Count == outline.Count * 2);
        // However wide the notch, its ripples reach the middle of its bottom.
        foreach (double width in new[] { 238d, 600, 1200 })
        {
            var wide = new IslandLiquid(IslandForm.Notch, width, 32);
            var middle = wide.Outline.MinBy(p => Math.Abs(p.S - wide.OutlineLength / 2));
            double peak = 0;
            for (double t = 0; t <= wide.Total; t += 1 / 120d) peak = Math.Max(peak, wide.EdgeOut(middle.S, t));
            check($"Island notch: on a notch {width} wide some ripples reach the middle of the bottom", peak > 1);
        }
        var still = new IslandLiquid(IslandForm.Notch, 238, 32, liquid: false);
        check("Island notch: without the liquid effect its edge keeps still", outline.All(p => still.EdgeOut(p.S, .3) == 0));
    }

    private static void Line(Action<string, bool> check)
    {
        var line = new IslandLiquid(IslandForm.Line, 56, 4);
        check("Island indicator: it plays for its two breaths, 1.95 s", Math.Abs(line.Total - 1.95) < 1e-9);
        var circles = new List<IslandLiquid.Circle>();
        var necks = new List<IslandLiquid.Neck>();
        bool thin = true, ends = true, near = true;
        for (double t = 0; t <= line.Total; t += 1 / 120d)
        {
            for (double x = -28; x <= 28; x += 1) { double h = line.LineHalf(x, t); thin &= h >= 2 * .45 - 1e-9 && h <= 2 * 1.5 + 1e-9; }
            foreach (int side in (int[])[-1, 1]) { double end = line.LineEnd(t, side); ends &= end >= 28 - 8 - 1e-9 && end <= 28 + 1e-9; }
            line.LineDrops(t, circles, necks);
            near &= circles.All(c => Math.Abs(c.Y) <= 4 + 1e-9 && Math.Abs(c.X) <= 28 + IslandLiquid.LineReach + 4);
        }
        check("Island indicator: it beads without breaking and its ends draw in no more than twice its thickness", thin && ends);
        check("Island indicator: its droplets stay close to it", near);
    }
}
