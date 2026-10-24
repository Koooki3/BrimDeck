using BrimDeck.Core;

// The audit's performance fixes (group "perf") that live in the core: the marquee waiting out its rests without frames, the
// core columns sharing out many cores, the display-off gate and the one animation rule and length.
internal static class AuditPerfTests
{
    public static void Run(Action<string, bool> check)
    {
        const double Speed = 24;
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        // Every frame at 144 Hz from start to end, as the marquee drew before.
        static double Play(MarqueeScroll scroll, double start, double end)
        {
            double offset = 0;
            for (double t = start; t <= end + 1e-9; t += 1 / 144d) offset = scroll.Advance(t, 100, Speed);
            return offset;
        }

        // perf-3: the rests are waited out without frames and the line still leaves them on time.
        var every = new MarqueeScroll(); every.Follow("line");
        var waited = new MarqueeScroll(); waited.Follow("line");
        waited.Advance(0, 100, Speed);
        check("A line at rest says how long its rest still lasts", Near(waited.RestLeft, MarqueeScroll.Hold));
        waited.Wait();
        // The same 144 Hz frames for both; the waiting line skips those of its rest until a tenth of a second before its end.
        double reference = 0, frames = 0;
        for (int k = 0; k <= 144 * (MarqueeScroll.Hold + .5); k++)
        {
            double t = k / 144d;
            reference = every.Advance(t, 100, Speed);
            if (k > 0 && t >= MarqueeScroll.Hold - .1) frames = waited.Advance(t, 100, Speed);
        }
        check("A rest waited out without frames ends where one drawn every frame does", Near(frames, reference) && frames > 0);
        check("A gliding line has no rest left", waited.RestLeft == 0);
        var unwaited = new MarqueeScroll(); unwaited.Follow("line");
        unwaited.Advance(0, 100, Speed);
        check("Without Wait a gap in the frames still counts as a stall (one short step)", Near(unwaited.Advance(MarqueeScroll.Hold, 100, Speed), 0) && unwaited.RestLeft > 0);
        var late = new MarqueeScroll(); late.Follow("line");
        late.Advance(0, 100, Speed); late.Wait();
        check("A wake far too late still moves the line by at most one short step past its rest",
            Near(late.Advance(30, 100, Speed), MarqueeScroll.MaxStep * Speed));
        var paused = new MarqueeScroll(); paused.Follow("line");
        paused.Advance(0, 100, Speed); paused.Wait(); paused.Pause();
        check("Pause after Wait counts no time", Near(paused.Advance(10, 100, Speed), 0) && Near(paused.RestLeft, MarqueeScroll.Hold));
        var atEnd = new MarqueeScroll(); atEnd.Follow("line");
        Play(atEnd, 0, MarqueeScroll.Hold + 100 / Speed + .2);
        check("The rest at the end is counted too", atEnd.RestLeft > 0 && atEnd.RestLeft < MarqueeScroll.Hold && Near(atEnd.At(100), 100));

        // correctness-ui #9: core columns never overflow their reading.
        foreach (var (cores, width, gap) in new[] { (8, 56d, 1d), (28, 56d, 1d), (32, 56d, 1d), (64, 56d, 1d), (128, 84d, 1.5), (256, 112d, 2d), (12, 28d, .5) })
        {
            int columns = CompactSpacing.CoreColumns(cores, width, gap);
            double column = (width - (columns - 1) * gap) / columns;
            var ranges = Enumerable.Range(0, columns).Select(i => CompactSpacing.CoreRange(i, columns, cores)).ToArray();
            bool covered = ranges[0].First == 0 && ranges[^1].End == cores && ranges.Zip(ranges.Skip(1)).All(p => p.First.End == p.Second.First) && ranges.All(r => r.End > r.First);
            check($"{cores} cores in {width} DIPs: columns of at least 1 DIP within the width, every core in one", column >= 1 - 1e-9 && columns * column + (columns - 1) * gap <= width + 1e-9 && covered);
        }
        check("Cores that fit keep one column each", CompactSpacing.CoreColumns(16, 56, 1) == 16 && CompactSpacing.CoreRange(5, 16, 16) == (5, 6));
        check("32 cores at 100 % share 28 columns", CompactSpacing.CoreColumns(32, 56, 1) == 28);
        check("No cores, no columns", CompactSpacing.CoreColumns(0, 56, 1) == 0);

        // perf-4: the display being off stops the ambient light, nothing else.
        var on = new MotionConditions(true, true, false, true, false, false, false, false, true);
        var dark = on with { DisplayOff = true };
        var cyber = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Demo)!).Motion;
        check("The ambient light runs with the display on", MotionGate.Ambient(on, cyber));
        check("The display off stops the ambient light", !MotionGate.Ambient(dark, cyber));
        check("The display off leaves the answers to the pointer alone", MotionGate.Interaction(dark, cyber) == MotionGate.Interaction(on, cyber));

        // q-P1.3: one rule for whether anything moves.
        check("Motion needs BrimDeck's animations, Windows' and no high contrast",
            MotionConditions.Allowed(true, true, false) && !MotionConditions.Allowed(false, true, false) && !MotionConditions.Allowed(true, false, false) && !MotionConditions.Allowed(true, true, true));
        check("The gate uses the same rule", !MotionGate.Interaction(on with { HighContrast = true }, cyber) && !MotionGate.Ambient(on with { SystemAnimations = false }, cyber));

        // q-P1.6: one place for the animation length.
        check("The animation length's default and range live in one place",
            new DeckSettings().AnimationDuration == AnimationLength.Default && AnimationLength.Clamp(50) == AnimationLength.Min && AnimationLength.Clamp(5000) == AnimationLength.Max);
        check("Timings clamp the length as the settings do", MotionTiming.From(50, 1) == MotionTiming.From(AnimationLength.Min, 1) && MotionTiming.From(9999, 1) == MotionTiming.From(AnimationLength.Max, 1));
    }
}
