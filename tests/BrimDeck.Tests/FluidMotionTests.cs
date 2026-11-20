using BrimDeck.Core;

// The material's spring (FluidSpring): exact at any frame rate, continuous in position and velocity across a retarget, bounded,
// at rest exactly on its target, and indifferent to broken input. The fixed-seed sequences stand in for a long session of
// backdrop changes at irregular frame times.
internal static class FluidMotionTests
{
    internal static void Run(Action<string, bool> check)
    {
        // Frame-rate independence: the same leg sampled at 8 ms and at 33 ms meets at the same times.
        var reference = new FluidSpring(0, 0); reference.Retarget(1, 0, 22);
        FluidSpring fine = reference, coarse = reference;
        double worst = 0;
        for (long t = 0; t <= 600; t += 8) worst = Math.Max(worst, Math.Abs(fine.Sample(t) - reference.Evaluate(t).Position));
        for (long t = 0; t <= 600; t += 33) worst = Math.Max(worst, Math.Abs(coarse.Sample(t) - reference.Evaluate(t).Position));
        check(FormattableString.Invariant($"The glass spring gives the same curve at 8 ms and 33 ms frames (only the snap at rest differs: {worst:E2})"), worst <= FluidSpring.Epsilon);

        // Settles near the documented time and comes to rest exactly on the target.
        var step = new FluidSpring(0, 0); step.Retarget(1, 0, 22);
        long within5 = -1;
        for (long t = 0; t <= 2000; t += 1) if (within5 < 0 && Math.Abs(1 - step.Evaluate(t).Position) <= .05) within5 = t;
        double end = step.Sample(3000);
        check(FormattableString.Invariant($"The glass spring is within 5 % of a step after about 4.74 / w ({within5} ms) and rests on the target"),
            Math.Abs(within5 - 4.74 / 22 * 1000) < 6 && end == 1 && !step.Moving);
        check("A critically damped step never overshoots", Enumerable.Range(0, 400).All(t => step.Evaluate(t * 5).Position <= 1 + 1e-12));

        // Retarget keeps position and velocity: no jump in either at the moment of the new goal.
        var turn = new FluidSpring(0, 0); turn.Retarget(1, 0, 22);
        var before = turn.Evaluate(120);
        turn.Retarget(0, 120, 30);
        var after = turn.Evaluate(120);
        check("A new goal mid-way keeps the material where it is and as fast as it was",
            Math.Abs(before.Position - after.Position) < 1e-12 && Math.Abs(before.Velocity - after.Velocity) < 1e-9 && turn.Moving);
        check("Reversed mid-way it turns back without first running on to the old goal",
            Enumerable.Range(121, 600).All(t => turn.Evaluate(t).Position <= before.Position + before.Velocity * 0.05));

        // Hitches, resumes and broken input.
        var hitch = new FluidSpring(.3, 0); hitch.Retarget(.9, 0, 20);
        double afterHitch = hitch.Sample(250), afterSleep = hitch.Sample(3_600_000);
        check("A 250 ms hitch lands on the same curve and a resume from sleep rests on the target",
            Math.Abs(afterHitch - new FluidSpring(.3, 0).With(.9, 0, 20).Evaluate(250).Position) < 1e-12 && afterSleep == .9);
        var broken = new FluidSpring(.5, 100); broken.Retarget(double.NaN, 110, 20); broken.Retarget(.7, 120, double.PositiveInfinity);
        broken.Retarget(.7, 130, 0);
        var past = new FluidSpring(.2, 1000); past.Retarget(.8, 1000, 20);
        check("Broken targets or stiffness change nothing, and a clock that runs back counts as no time",
            !broken.Moving && broken.Sample(140) == .5 && Math.Abs(past.Evaluate(900).Position - .2) < 1e-12);

        // A long irregular session: bounded, finite, continuous between frames, at rest when left alone.
        var rng = new Random(20261120);
        var spring = new FluidSpring(.35, 0);
        long now = 0;
        double lo = .35, hi = .35, maxJump = 0, last = .35;
        bool finite = true;
        int[] frames = [8, 16, 24, 33, 100, 500, 0, 16, 16];
        for (int i = 0; i < 10_000; i++)
        {
            now += frames[rng.Next(frames.Length)];
            if (rng.NextDouble() < .2)
            {
                double goal = rng.NextDouble() * .95;
                lo = Math.Min(lo, goal); hi = Math.Max(hi, goal);
                var held = spring.Evaluate(now).Position;
                spring.Retarget(goal, now, 14 + rng.NextDouble() * 16);
                maxJump = Math.Max(maxJump, Math.Abs(spring.Evaluate(now).Position - held));
            }
            double x = spring.Sample(now);
            finite &= double.IsFinite(x);
            lo = Math.Min(lo, x); hi = Math.Max(hi, x);
            last = x;
        }
        double rest = spring.Sample(now + 5000);
        check("10,000 irregular frames and retargets stay finite, never jump at a retarget and come to rest on the last goal",
            finite && maxJump < 1e-12 && rest == spring.Target && !spring.Moving && lo >= -.05 && hi <= 1.0);
    }

    private static FluidSpring With(this FluidSpring spring, double target, long now, double omega) { spring.Retarget(target, now, omega); return spring; }
}
