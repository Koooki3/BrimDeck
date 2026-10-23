using BrimDeck.Core;

// The collapsed row's moving parts outlive the row, which is built again on every change of its width.
internal static class CompactMotionTests
{
    private const double Speed = 24, Edge = 6;

    public static void Run(Action<string, bool> check)
    {
        static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;
        // Frames at the given rate from start to end, inclusive of the frame at end.
        static double Play(MarqueeScroll scroll, double start, double end, double hertz, Func<double, double> overflow)
        {
            double offset = scroll.At(overflow(start));
            for (long frame = (long)Math.Ceiling(start * hertz - 1e-9); frame <= (long)Math.Floor(end * hertz + 1e-9); frame++)
                offset = scroll.Advance(frame / hertz, overflow(frame / hertz), Speed);
            return offset;
        }

        var resting = new MarqueeScroll(); resting.Follow("line");
        check("Marquee rests at its start for the hold", Play(resting, 0, MarqueeScroll.Hold - .01, 144, _ => 100) == 0);
        var gliding = new MarqueeScroll(); gliding.Follow("line");
        check("Marquee glides at its speed after the hold", Near(Play(gliding, 0, MarqueeScroll.Hold + 1, 144, _ => 100), Speed));

        var at60 = new MarqueeScroll(); var at144 = new MarqueeScroll(); at60.Follow("line"); at144.Follow("line");
        double slow = Play(at60, 0, 3.25, 60, _ => 100), fast = Play(at144, 0, 3.25, 144, _ => 100);
        check("Marquee place depends on time, not on the display's rate", Near(slow, fast) && Near(fast, (3.25 - MarqueeScroll.Hold) * Speed));

        var twice = new MarqueeScroll(); twice.Follow("line");
        Play(twice, 0, 2, 144, _ => 100);
        double once = twice.At(100);
        check("Two views of one line in the same frame move it once", Near(twice.Advance(2, 100, Speed), once));

        // A resize drag changes the region every frame: the glide goes on from where it was instead of starting over.
        var dragged = new MarqueeScroll(); dragged.Follow("line");
        double before = Play(dragged, 0, 2.5, 144, _ => 100);
        double previous = before; bool smooth = true;
        for (int frame = 1; frame <= 144; frame++)
        {
            double t = 2.5 + frame / 144d, region = 100 - frame * .1;
            double offset = dragged.Advance(t, region, Speed);
            smooth &= offset >= previous && offset - previous <= Speed / 144 + 1e-9;
            previous = offset;
        }
        check("Marquee keeps its place and pace while the region changes width", Near(before, Speed) && smooth && Near(previous, 2 * Speed));

        var shrunk = new MarqueeScroll(); shrunk.Follow("line");
        Play(shrunk, 0, 3, 144, _ => 100);
        double end = shrunk.Advance(3 + 1 / 144d, 20, Speed);
        bool restsAtEnd = Near(end, 20) && Near(Play(shrunk, 3 + 2 / 144d, 3 + MarqueeScroll.Hold - .02, 144, _ => 30), 30);
        check("A region narrowed past the glide holds the line's end at its edge, then starts again",
            restsAtEnd && Play(shrunk, 3 + MarqueeScroll.Hold, 3 + MarqueeScroll.Hold + .1, 144, _ => 30) == 0);

        // Overflow 48 at 24 DIP/s: rest 1.5 s, glide 2 s, rest 1.5 s, so the cycle is 5 s long.
        var cycle = new MarqueeScroll(); cycle.Follow("line");
        bool ends = Near(Play(cycle, 0, 4.9, 144, _ => 48), 48);
        check("Marquee rests at its end, then starts again at rest", ends && Play(cycle, 4.9 + 1 / 144d, 5.1, 144, _ => 48) == 0
            && Near(Play(cycle, 5.1 + 1 / 144d, 7, 144, _ => 48), (7 - 6.5) * Speed));

        var followed = new MarqueeScroll(); followed.Follow("first");
        Play(followed, 0, 2, 144, _ => 100);
        bool kept = !followed.Follow("first") && followed.At(100) > 0;
        check("The same line keeps its place; a new line starts at rest", kept && followed.Follow("second") && followed.At(100) == 0);

        var paused = new MarqueeScroll(); paused.Follow("line");
        double held = Play(paused, 0, 2, 144, _ => 100);
        paused.Pause();
        check("A paused line resumes where it stood", Near(paused.Advance(30, 100, Speed), held) && Near(paused.Advance(30 + 1 / 144d, 100, Speed), held + Speed / 144));

        var stalled = new MarqueeScroll(); stalled.Follow("line");
        double stalledFrom = Play(stalled, 0, 2, 144, _ => 100);
        check("A stalled frame moves the line by at most one short step", Near(stalled.Advance(4, 100, Speed), stalledFrom + MarqueeScroll.MaxStep * Speed));

        var fits = new MarqueeScroll(); fits.Follow("line");
        Play(fits, 0, 2, 144, _ => 100);
        check("A line that fits rests at its start", fits.Advance(2.1, 0, Speed) == 0 && fits.Advance(2.2, 100, Speed) == 0);

        check("Fades: none on the left at the start, none on the right at the end", MarqueeScroll.Fades(0, 100, Edge) == (0, 1) && MarqueeScroll.Fades(100, 100, Edge) == (1, 0));
        check("Fades: both in full mid-glide", MarqueeScroll.Fades(50, 100, Edge) == (1, 1));
        check("Fades: a short overflow grows them over its own length", MarqueeScroll.Fades(1.5, 3, Edge) == (.5, .5) && MarqueeScroll.Fades(0, 0, Edge) == (0, 0));

        // The bars carried into a rebuilt row are the bars that were shown; a fresh set would start again from rest.
        var carried = new EqualizerBars(); var fresh = new EqualizerBars();
        for (int tick = 0; tick < 45; tick++) carried.Tick(tick % 6 < 3 ? .02f : .004f, true);
        var shown = carried.Levels.ToArray();
        carried.Tick(.02f, true); fresh.Tick(.02f, true);
        bool continued = carried.Levels.ToArray().Zip(shown).Any(pair => Math.Abs(pair.First - pair.Second) < .5) && shown.Any(level => level > .2);
        check("Equalizer bars carried into a new row continue from where they were", continued && carried.Levels.ToArray().Zip(fresh.Levels.ToArray()).Any(pair => Math.Abs(pair.First - pair.Second) > .05));
        carried.Tick(.02f, false);
        check("Equalizer bars rest when playback stops", carried.Levels.ToArray().All(level => level == 0));
        var flowing = new EqualizerBars(); flowing.Show([.2, 1.4, -1, .5], true);
        check("Equalizer bars show captured levels, clamped", flowing.Levels.ToArray().SequenceEqual(new double[] { .2, 1, 0, .5, 0 }));
    }
}
