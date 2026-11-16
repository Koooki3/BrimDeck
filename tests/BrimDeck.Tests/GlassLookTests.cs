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

        // After a turn the look holds for the dwell.
        var dwell = Settled(true, true, .1);
        long up = TurnAt(dwell, .95, 3000, 6000), down = TurnAt(dwell, .05, up + 16, up + 6000);
        check($"Automatic liquid glass holds a look for the dwell after turning ({down - up} ms)", up > 0 && down - up >= GlassAppearance.Dwell);

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
            for (long now = 0; now < 60_000; now += 33)
            {
                double luma = now / 500 % 2 == 0 ? .05 : .95;
                video.Changed(.9, now);
                if (video.Feed(luma, now)) turns++;
                if (plain.Feed(luma, now)) plainTurns++;
            }
            check($"Automatic liquid glass holds its look over a flashing video ({(appDark ? "dark" : "light")} app mode: {turns} turns a minute; {plainTurns} without the video detected)",
                turns == 0 && video.Video && plainTurns <= 12);
        }
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
    }
}
