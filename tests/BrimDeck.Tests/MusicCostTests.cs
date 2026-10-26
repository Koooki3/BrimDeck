using BrimDeck.Core;

// What the collapsed music row costs while a song plays: the title rests after its laps, the bars draw only what moves a
// half pixel, and the audio is analysed 30 times a second.
internal static class MusicCostTests
{
    private const double Speed = 24;

    public static void Run(Action<string, bool> check)
    {
        static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;
        // Frames at the given rate from start to end, inclusive of the frame at end.
        static double Play(MarqueeScroll scroll, double start, double end, double hertz, double overflow)
        {
            double offset = scroll.At(overflow);
            for (long frame = (long)Math.Ceiling(start * hertz - 1e-9); frame <= (long)Math.Floor(end * hertz + 1e-9); frame++)
                offset = scroll.Advance(frame / hertz, overflow, Speed);
            return offset;
        }

        // The title scrolls Laps whole cycles (rest, glide, rest, back to the start) and then rests at its start.
        const double overflow = 48;
        double period = 2 * MarqueeScroll.Hold + overflow / Speed;
        check("the collapsed title scrolls three laps", MarqueeScroll.Laps == 3);
        var title = new MarqueeScroll { Limited = true }; title.Follow("song");
        double last = Play(title, 0, 3 * period - .05, 144, overflow);
        check("the title is still scrolling in the end rest of its last lap", !title.Rested && title.LapsDone == 2 && Near(last, overflow));
        last = Play(title, 3 * period - .05 + 1 / 144d, 3 * period + .05, 144, overflow);
        var (left, right) = MarqueeScroll.Fades(last, overflow, 6);
        check("after three whole cycles the title rests at its start, no fade on the left and the right fade over the hidden text",
            title.Rested && title.LapsDone == 3 && Near(last, 0) && Near(left, 0) && Near(right, 1));
        check("a rested title moves no more, however much time passes",
            Near(Play(title, 3 * period + .1, 3 * period + 20, 144, overflow), 0) && Near(title.Advance(1e6, overflow, Speed), 0) && title.Rested);
        check("the same title (a row built again) keeps it resting", !title.Follow("song") && title.Rested);
        title.Follow("next line");
        check("a new title scrolls its laps again from the start", !title.Rested && title.LapsDone == 0 && Near(title.At(overflow), 0));

        var replayed = new MarqueeScroll { Limited = true }; replayed.Follow("song");
        Play(replayed, 0, 3 * period + .05, 144, overflow);
        check("replaying a rested title lets it scroll again", replayed.Replay() && !replayed.Rested && replayed.LapsDone == 0
            && Near(Play(replayed, 100, 100 + MarqueeScroll.Hold + 1, 144, overflow), Speed, .2));
        check("replaying a title that has not rested changes nothing", !replayed.Replay());

        // The view waits out the rests without frames (MusicMarquee): the laps still end on time.
        var waited = new MarqueeScroll { Limited = true }; waited.Follow("song");
        double t = 0; int frames = 0;
        while (!waited.Rested && t < 10 * period)
        {
            waited.Advance(t, overflow, Speed); frames++;
            if (waited.Rested) break;
            double rest = waited.RestLeft;
            if (rest > .25) { waited.Wait(); t += rest - .1; } else t += 1 / 144d;
        }
        check("a title whose rests are waited out without frames rests after its three laps", waited.Rested && Near(t, 3 * period, .05) && frames < 3 * period * 144 / 2);

        var unlimited = new MarqueeScroll(); unlimited.Follow("lyric");
        double open = Play(unlimited, 0, 3 * period + MarqueeScroll.Hold + 1, 144, overflow);
        check("the open page's lyric is not limited", !unlimited.Rested && unlimited.LapsDone == 3 && Near(open, Speed, .2));

        var widened = new MarqueeScroll { Limited = true }; widened.Follow("song");
        Play(widened, 0, 2 * period + .05, 144, overflow);
        widened.Advance(2 * period + .1, 0, Speed);
        check("a region that briefly fits the title keeps the laps it has scrolled", widened.LapsDone == 2 && !widened.Rested);

        // The bars stand on half device pixels, so a level that moves them by less draws the same.
        foreach (double dpi in new[] { 1, 1.25, 1.5, 2 })
        {
            double step = .5 / dpi;
            bool snapped = true, same = true;
            for (int i = 0; i < 4; i++)
                for (double level = 0; level <= 1; level += .013)
                {
                    var (x, y, w, h) = EqualizerLayout.Bar(i, 4, level, 20, 14, 3, dpi);
                    snapped &= Near(Math.Round(x / step) * step, x, 1e-9) && Near(Math.Round(y / step) * step, y, 1e-9) && Near(Math.Round(h / step) * step, h, 1e-9);
                    same &= Math.Abs(EqualizerLayout.Bar(i, 4, level + 1e-4, 20, 14, 3, dpi).Height - h) <= step + 1e-9;
                }
            check($"the bars stand on half device pixels at {dpi * 100:0}%", snapped);
            check($"a hair of level moves a bar at most half a pixel at {dpi * 100:0}%", same);
        }
        var low = EqualizerLayout.Bar(0, 4, 0, 20, 14, 3, 1); var high = EqualizerLayout.Bar(3, 4, 1, 20, 14, 3, 1.5);
        check("a silent bar is as tall as it is wide and centred", Near(low.Height, 3) && Near(low.Top, 5.5) && Near(low.Left, 0));
        check("a full bar fills the box at the right end", Near(high.Height, 14) && Near(high.Top, 0) && Near(high.Left, 17));
        check("bad input draws a resting bar", EqualizerLayout.Bar(1, 4, double.NaN, 20, 14, 3, double.NaN).Height == 3);

        // Analysed 30 times a second, a song still lifts the bars and silence lets them fall.
        const int rate = 44100;
        var signal = new EqualizerSignal(rate);
        var block = new float[441 * 2];
        double peak = 0, time = 0;
        for (int n = 0; n < 400; n++)
        {
            time = n / 100d;
            for (int i = 0; i < 441; i++)
            {
                double s = time + i / (double)rate, since = s % .5;
                float v = (float)(.3 * Math.Sin(2 * Math.PI * 220 * s) + .7 * Math.Exp(-since / .08) * Math.Sin(2 * Math.PI * 60 * since));
                block[i * 2] = block[i * 2 + 1] = v;
            }
            signal.Push(block, 2, time + .01);
            if (n % 3 == 0) { signal.Advance(time + .01); peak = Math.Max(peak, signal.Head); }
        }
        for (int n = 1; n <= 30; n++) signal.Advance(time + .01 + n / 30d);
        check("analysed 30 times a second, the bars still rise with the music and fall in silence", peak > .2 && signal.Head < .05);
    }
}
