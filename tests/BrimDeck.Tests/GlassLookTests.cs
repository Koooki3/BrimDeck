using BrimDeck.Core;

// Automatic liquid glass's decision between its light and dark looks (GlassAppearance): the measure (the mean of the middle of
// the backdrop's brightness), the Windows app mode as the prior, the frame-rate-independent smoothing, the Schmitt trigger, the
// dwell, the video state and the panel's lock.
internal static class GlassLookTests
{
    // The first time the look turns, fed every step milliseconds from from to until at a constant measure; -1: never.
    private static long TurnAt(GlassAppearance look, double luma, long from, long until, int step = 16, bool held = false)
    {
        for (long now = from; now <= until; now += step) if (look.Feed(luma, now, held)) return now;
        return -1;
    }

    // A look shown since 0 over a backdrop of luma, settled there by from.
    private static GlassAppearance Settled(bool dark, bool appDark, double luma, long from = 3000, int step = 16)
    {
        var look = new GlassAppearance(dark, appDark);
        look.Reset(dark, 0);
        TurnAt(look, luma, 0, from - step, step);
        return look;
    }

    internal static void Run(Action<string, bool> check)
    {
        // The measure is the mean of the middle of the backdrop's brightness: a bright logo on 30 % of a dark video (bin 9 of 10)
        // does not pull it, where the arithmetic mean (.345) would.
        int[] histogram = [0, 70, 0, 0, 0, 0, 0, 0, 0, 30];
        double middle = GlassAppearance.RangeMean(histogram);
        var split = new double[100];
        Array.Fill(split, 1d, 49, 51);
        double splitMean = GlassAppearance.SortedRangeMean(split);
        check("Automatic liquid glass keeps the Windows dark look with nearly equal black and white coverage",
            Math.Abs(splitMean - 31d / 60) < 1e-9 && GlassAppearance.FirstLook(splitMean, appDark: true)
            && GlassAppearance.SortedRangeMean([.5]) == .5 && double.IsNaN(GlassAppearance.SortedRangeMean([])));
        check(FormattableString.Invariant($"Automatic liquid glass measures the mean of the middle of the backdrop's brightness ({middle:0.000})"),
            Math.Abs(middle - (.5 * .15 + .1 * .95) / .6) < 1e-9 && double.IsNaN(GlassAppearance.RangeMean(new int[8])) && Math.Abs(GlassAppearance.RangeMean([0, 0, 5, 0]) - .625) < 1e-9);
        check("Automatic liquid glass's first look leans toward the Windows app mode", GlassAppearance.FirstLook(.7, appDark: true) && !GlassAppearance.FirstLook(.75, appDark: true)
            && !GlassAppearance.FirstLook(.3, appDark: false) && GlassAppearance.FirstLook(.25, appDark: false));

        // Dark app mode: light glass only over a clearly light backdrop (above .72), dark again only below .55. A white page opened
        // behind the island (a jump from dark) turns it light promptly.
        var grey = Settled(true, true, .2);
        bool greyKept = TurnAt(grey, .68, 3000, 13_000) < 0 && grey.Dark;
        var page = Settled(true, true, .1);
        long white = TurnAt(page, .95, 3000, 6000) - 3000;
        bool kept = TurnAt(page, .6, 6016, 16_000) < 0 && !page.Dark && TurnAt(page, .5, 16_016, 20_000) > 0 && page.Dark;
        check($"Automatic liquid glass in dark app mode stays dark over grey, turns light over a white page within a second ({white} ms) and back only below .55",
            greyKept && white is > 0 and <= 1000 && kept);
        // Light app mode, symmetric: dark glass only below .28, light again above .45.
        var light = Settled(false, false, .9);
        bool lightKept = TurnAt(light, .32, 3000, 13_000) < 0 && !light.Dark && TurnAt(light, .1, 13_016, 15_000) > 0 && light.Dark
            && TurnAt(light, .4, 15_016, 25_000) < 0 && TurnAt(light, .5, 25_016, 29_000) > 0 && !light.Dark;
        check("Automatic liquid glass in light app mode stays light over grey, turns dark only below .28 and back above .45", lightKept);

        // The smoothing is the same at any frame rate: the same step fed at 60, 30 and 10 measures a second turns within a
        // measure's time of each other.
        long[] at = [.. new[] { 16, 33, 100 }.Select(step => TurnAt(Settled(true, true, .1, 3000, step), .95, 3000, 6000, step) - 3000)];
        check($"Automatic liquid glass smooths the same at any frame rate ({string.Join(", ", at)} ms)", at.All(t => t > 0) && at.Max() - at.Min() <= 100);

        // After a turn has arrived the look holds for the dwell (review v2 U06: the hold runs from the arrival, not from the
        // decision; the turn itself is modelled as the palette's 365 ms).
        var dwell = Settled(true, true, .1);
        long up = TurnAt(dwell, .95, 3000, 6000);
        bool flying = TurnAt(dwell, .95, up + 16, up + 365) < 0 && dwell.State == GlassTurnState.InFlight;
        dwell.Arrived(up + 365);
        bool holding = dwell.StateAt(up + 400) == GlassTurnState.SettledHold;
        long down = TurnAt(dwell, .05, up + 381, up + 6000);
        check($"Automatic liquid glass holds a look for the dwell after its turn arrived ({down - up - 365} ms)",
            up > 0 && flying && holding && down - up - 365 >= GlassAppearance.Dwell);

        // Held (the open panel, a press): nothing turns, and once released the look turns after Settle.
        var held = Settled(true, true, .1);
        bool lockedOpen = TurnAt(held, .95, 3000, 9000, held: true) < 0 && held.Dark;
        long released = TurnAt(held, .95, 9016, 11_000) - 9016;
        check($"Automatic liquid glass keeps its look while the panel is open, and turns once it closes ({released} ms)",
            lockedOpen && released >= GlassAppearance.Settle && released <= GlassAppearance.Settle + 50 && !held.Dark);

        // The Windows app mode turned: the thresholds lean the other way, and the look may turn at once.
        var prior = Settled(true, true, .6);
        bool before = TurnAt(prior, .6, 3000, 8000) < 0;
        prior.Prior(false);
        check("Automatic liquid glass leans the other way at once when the Windows app mode turns", before && prior.Feed(.6, 8016) && !prior.Dark && !prior.Feed(double.NaN, 8032));
        // That waiver is for the next measure only: a flash later still waits for the candidate's span.
        var waived = Settled(true, true, .1);
        waived.Prior(false);
        bool waiverUsed = !waived.Feed(.1, 3000) && !waived.Feed(.95, 3300) && waived.Smoothed > waived.ToLight && waived.Dark;
        check("Automatic liquid glass waives the candidate's span only for the measure right after the app mode turned", waiverUsed);

        // A video flashing between black and white every half second for a minute, most of its pixels changing on every frame:
        // a video, over which the look holds (no turn at all), in either app mode. The same brightness without the changes (a
        // backdrop alternating as a whole, nothing seen moving) is held back by the smoothing and the dwell's doubling instead.
        foreach (bool appDark in new[] { true, false })
        {
            var video = new GlassAppearance(appDark, appDark);
            var plain = new GlassAppearance(appDark, appDark);
            // The look decided when the island was shown (BackdropLook's first look), the dwell running from there.
            video.Reset(appDark, 0); plain.Reset(appDark, 0);
            int turns = 0, plainTurns = 0;
            // Each turn's palette arrives 365 ms after it began (review v2 U06: the settled hold runs from there).
            long plainArrives = -1;
            for (long now = 0; now < 60_000; now += 33)
            {
                double luma = now / 500 % 2 == 0 ? .05 : .95;
                video.Changed(.9, now);
                if (video.Feed(luma, now)) turns++;
                if (plainArrives >= 0 && now >= plainArrives) { plain.Arrived(plainArrives); plainArrives = -1; }
                if (plain.Feed(luma, now)) { plainTurns++; plainArrives = now + 365; }
            }
            check($"Automatic liquid glass holds its look over a flashing video ({(appDark ? "dark" : "light")} app mode: {turns} turns a minute; {plainTurns} without the video detected)",
                turns == 0 && video.Video && plainTurns <= 12);
        }
        var sampled = new GlassAppearance(true, true, videoGap: 1000);
        for (long now = 0; now <= 2000; now += 500)
        {
            sampled.Changed(.9, now);
            sampled.Feed(.5, now);
        }
        bool movingAtProbeCadence = sampled.Video;
        sampled.Feed(.5, 3100);
        check("The glass detects motion at the 500 ms capture cadence and expires after a gap",
            movingAtProbeCadence && !sampled.Video);

        // A video that becomes bright for good turns the look once it has stayed past the threshold for VideoEscape; the video
        // stopping ends the video state after VideoGap.
        var escape = new GlassAppearance(true, true);
        escape.Reset(true, 0);
        long turned = -1;
        for (long now = 0; now < 20_000 && turned < 0; now += 33)
        {
            escape.Changed(.9, now);
            if (escape.Feed(now < 3000 ? .1 : .9, now)) turned = now;
        }
        long after = turned - 3000;
        bool stopped = !escape.Feed(.9, turned + GlassAppearance.VideoGap + 100) && !escape.Video;
        check($"Automatic liquid glass over a video turns only once it stayed bright for good ({after} ms), and the video state ends with the video",
            after >= GlassAppearance.VideoEscape && after <= GlassAppearance.VideoEscape + 1000 && stopped);

        // A new scene is taken once its readings have agreed for JumpSpan, at the 2 Hz rest rate (two readings) as in a burst; a lone
        // flash, or a page's 200 ms white flash read in a burst every 80 ms, only nudges the average.
        var scene = Settled(dark: true, appDark: true, luma: .08);
        bool sceneTurned = scene.Feed(.94, 3000) | scene.Feed(.94, 3500);
        var burstScene = Settled(dark: true, appDark: true, luma: .08);
        long burstTurn = -1;
        for (long t = 3000; t <= 3800 && burstTurn < 0; t += 80) if (burstScene.Feed(.94, t)) burstTurn = t - 3000;
        var flash = Settled(dark: true, appDark: true, luma: .08);
        bool flashed = flash.Feed(.94, 3000) | flash.Feed(.08, 3500) | flash.Feed(.08, 4000);
        var pageFlash = Settled(dark: true, appDark: true, luma: .08);
        bool pageFlashed = false;
        for (long t = 3000; t <= 4500; t += 80) pageFlashed |= pageFlash.Feed(t < 3200 ? .94 : .08, t);
        check(FormattableString.Invariant($"Automatic liquid glass takes a new scene once its readings agree (burst: {burstTurn} ms), a lone or a 200 ms page flash does not turn it (smoothed {pageFlash.Smoothed:0.00})"),
            sceneTurned && !scene.Dark && burstTurn >= GlassAppearance.JumpSpan && burstTurn <= GlassAppearance.JumpSpan + 160
            && !flashed && flash.Dark && flash.Smoothed < .3 && !pageFlashed && pageFlash.Dark);

        // Review v2 U06: one bright reading after a grey backdrop moves the average past the threshold at once (.65 then 1.0 80 ms
        // later averages about .75, above .72); the look must still wait for the candidate's span, not turn at 80 ms.
        var early = Settled(dark: true, appDark: true, luma: .65);
        bool earlyTurn = early.Feed(1, 3064);
        long earlyAt = -1;
        for (long t = 3144; t <= 4000 && earlyAt < 0; t += 80) if (early.Feed(1, t)) earlyAt = t - 3064;
        check(FormattableString.Invariant($"Automatic liquid glass waits for the candidate's span before turning, not only for the average (smoothed {early.Smoothed:0.000}, turned after {earlyAt} ms)"),
            !earlyTurn && earlyAt >= 250 && earlyAt <= 250 + 80);

        // Review v2 G07: A→B→A, the backdrop back after 80/160/250 ms (readings every 80 ms): the candidate never matured, nothing turns.
        // After 350 ms it turned; the backdrop back while the turn is still under way reverses it once the reversal is a candidate
        // for its span, without waiting for the settled dwell (the old dwell kept a started turn for 1.75 s).
        foreach (long back in new long[] { 80, 160, 250 })
        {
            var flip = Settled(dark: true, appDark: true, luma: .1);
            bool any = false;
            for (long t = 3000; t <= 7000; t += 80) any |= flip.Feed(t - 3000 < back ? .95 : .1, t);
            check($"Automatic liquid glass does not turn for a backdrop that returns after {back} ms", !any && flip.Dark);
        }
        var turnBack = Settled(dark: true, appDark: true, luma: .1);
        long there = -1, reversed = -1;
        for (long t = 3000; t <= 6000 && reversed < 0; t += 80)
        {
            bool turnedNow = turnBack.Feed(t - 3000 < 350 ? .95 : .1, t);
            if (turnedNow && there < 0) there = t;
            else if (turnedNow) reversed = t;
        }
        check($"Automatic liquid glass reverses a turn under way when the backdrop returns (turned {there - 3000} ms, reversed {reversed - 3000} ms)",
            there > 0 && reversed > there && reversed - 3350 <= 250 + 160 && reversed - there < GlassAppearance.Dwell && turnBack.Dark);

        // User 11-21: a capsule decided dark over a dark tab bar opens over a white page. The area the panel will cover, read as the
        // opening begins, turns the look light with the opening (then the panel's lock holds it), and the shown look's veil rises
        // with the opening, never darkening again after it; the old way kept the dark look and thickened its veil to about .8.
        var opening = Settled(dark: true, appDark: true, luma: .1);
        bool openedLight = opening.Open(.96, 3000) && !opening.Dark;
        bool lockedLight = TurnAt(opening, .1, 3016, 6000, held: true) < 0 && !opening.Dark;
        double Veil(bool darkLook, (double B, double D) capsule, (double B, double D) covered, double o)
            => GlassMaterial.LuminosityOpacity(GlassOpening.Required(GlassRecipe.LuminosityFor(darkLook, capsule.B, capsule.D),
                GlassRecipe.LuminosityFor(darkLook, covered.B, covered.D), o), o, GlassMaterial.DefaultTransparency);
        (double, double) tabBar = (.15, .05), whitePage = (.99, .9), darkPage = (.14, .04);
        var lightVeil = Enumerable.Range(0, 21).Select(i => Veil(false, tabBar, whitePage, i / 20d)).ToArray();
        double oldDark = GlassMaterial.LuminosityOpacity(GlassRecipe.LuminosityFor(true, whitePage.Item1, whitePage.Item2), 1, GlassMaterial.DefaultTransparency);
        bool monotone = lightVeil.Zip(lightVeil.Skip(1)).All(pair => pair.Second >= pair.First - 1e-12);
        check(FormattableString.Invariant($"Liquid glass opening over a white page turns light with the opening and its light veil rises with it ({lightVeil[0]:0.00} -> {lightVeil[^1]:0.00}; the dark look kept by the lock darkened to {oldDark:0.00})"),
            openedLight && lockedLight && monotone);
        // Over a dark area, or a grey one not clearly past the threshold, the dark look stays, its veil as monotone.
        var staying = Settled(dark: true, appDark: true, luma: .1);
        bool keptDark = !staying.Open(.12, 3000) && !staying.Open(.78, 3016) && staying.Dark;
        var darkVeil = Enumerable.Range(0, 21).Select(i => Veil(true, tabBar, darkPage, i / 20d)).ToArray();
        check(FormattableString.Invariant($"Liquid glass opening over a dark area keeps the dark look, its veil monotone ({darkVeil[0]:0.00} -> {darkVeil[^1]:0.00})"),
            keptDark && darkVeil.Zip(darkVeil.Skip(1)).All(pair => pair.Second >= pair.First - 1e-12) && GlassOpening.Required(.3, double.NaN, .5) == .3 && GlassOpening.Required(.3, .8, .5) == .55 && GlassOpening.Required(.3, .1, 1) == .3);

        // Review v2 U05: readings that stopped being trustworthy (a gap, a late or misplaced reading) end a candidate; after the gap
        // the new scene must stay for the candidate's span again, from fresh readings.
        var gap = Settled(dark: true, appDark: true, luma: .1);
        bool beforeGap = gap.Feed(.95, 3000) | gap.Feed(.95, 3080);
        gap.Invalidate();
        bool rightAfter = gap.Feed(.95, 3400);
        long afterGap = -1;
        for (long t = 3480; t <= 5000 && afterGap < 0; t += 80) if (gap.Feed(.95, t)) afterGap = t;
        check($"Automatic liquid glass starts a candidate again after untrustworthy readings (turned {afterGap - 3400} ms after the gap)",
            !beforeGap && !rightAfter && afterGap - 3400 >= GlassAppearance.CandidateSpan);
    }
}
