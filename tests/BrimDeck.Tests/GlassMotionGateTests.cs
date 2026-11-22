using BrimDeck.Core;

// V3 C4: liquid glass's ring reads (GDI screen reads on a worker) stretch the display's frames while the island opens, closes or
// resizes, and a ring read for a size that is still changing is for a place the panel has left anyway. The gate holds them while
// the island moves and owes one catch-up read when it settles; the readings' validity does not count a pause for motion as age.
internal static class GlassMotionGateTests
{
    internal static void Run(Action<string, bool> check)
    {
        // An opening: the island's size changes every frame for 400 ms. No ring read while it moves, one catch-up once it has
        // been still for Quiet, and only one.
        var gate = new GlassMotionGate();
        bool starts = gate.Moved(0), again = false, readMidway = false;
        for (long t = 16; t <= 400; t += 16) { again |= gate.Moved(t); readMidway |= gate.MayRead(t + 8); }
        long settles = gate.SettlesAt;
        bool earlyCatchUp = gate.CatchUp(settles - 1), readBeforeSettle = gate.MayRead(settles - 1);
        bool catchUp = gate.CatchUp(settles), secondCatchUp = gate.CatchUp(settles + 200);
        check($"Liquid glass reads no ring while the island moves and catches up once when it settles (settles at {settles} ms)",
            starts && !again && !readMidway && !earlyCatchUp && !readBeforeSettle && catchUp && !secondCatchUp && gate.MayRead(settles)
            && settles == 400 + GlassMotionGate.Quiet);

        // At rest nothing is held and nothing is owed.
        var still = new GlassMotionGate();
        check("Liquid glass reads at rest and owes no catch-up without motion", still.MayRead(0) && still.MayRead(5000) && !still.CatchUp(5000));

        // A motion that goes on (rapid cycles, a long drag of the size) holds the reads for at most MaxPause, then the ring is read
        // again at its interval; the catch-up still comes when it settles.
        var long_ = new GlassMotionGate();
        long last = 0;
        for (long t = 0; t <= 3000; t += 16) { long_.Moved(t); last = t; }
        bool heldThen = !long_.MayRead(GlassMotionGate.MaxPause - 1), readsAgain = long_.MayRead(GlassMotionGate.MaxPause + 16);
        check("Liquid glass holds ring reads for at most MaxPause of continuous motion",
            heldThen && readsAgain && !long_.CatchUp(last + 1) && long_.CatchUp(last + GlassMotionGate.Quiet));

        // A new motion after a settled one starts a new pause and owes a new catch-up.
        var twice = new GlassMotionGate();
        twice.Moved(0); twice.CatchUp(GlassMotionGate.Quiet);
        bool restarts = twice.Moved(1000);
        check("Liquid glass owes one catch-up per motion", restarts && !twice.MayRead(1010) && twice.CatchUp(1000 + GlassMotionGate.Quiet));

        // Validity: a reading accepted at 10 ms, a burst from 100 ms (an opening's hint) and the reads paused for its motion until
        // 600 ms. Without the pause the burst TTL would raise the protection at 350 ms; with it, the age stops while paused and the
        // protection rises only a burst TTL (plus the 10 ms pace of that reading) after the catch-up could have come (shifted by the
        // pause).
        (double, double) calm = (.3, .1);
        GlassSampleValidity Calm()
        {
            var v = new GlassSampleValidity(0);
            var ticket = v.Begin(0, 1)!.Value;
            v.Complete(ticket, 10, 1, true, calm);
            return v;
        }
        var paused = Calm();
        paused.Mode(true, 100);
        paused.Pause(100);
        double during = paused.Protection(599);
        long deadline = paused.NextDeadline(200);
        paused.Resume(600);
        double rightAfter = paused.Protection(640);
        bool calmAfter = !paused.Protecting && paused.SafeTails(640) == calm;
        double later = paused.Protection(861);
        var control = Calm();
        control.Mode(true, 100);
        check(FormattableString.Invariant($"Liquid glass does not count a pause for motion as a stale reading (control {control.Protection(599):0.000} at 599 ms, paused {during:0.000}; next look {deadline} ms)"),
            control.Protection(599) > 0 && during == 0 && rightAfter == 0 && later > 0 && deadline > 599 && calmAfter);

        // A pause has a ceiling: one that goes on past MaxPause lets the protection rise as if the readings had stopped then.
        var endless = Calm();
        endless.Mode(true, 100);
        endless.Pause(100);
        long rise = 100 + GlassSampleValidity.ActiveTtl + 10 + GlassMotionGate.MaxPause;
        long endlessDeadline = endless.NextDeadline(200);
        check($"Liquid glass's motion pause lasts at most MaxPause (rises after {rise} ms, watchdog at {endlessDeadline} ms)",
            endless.Protection(rise - 1) == 0 && endless.Protection(rise + 50) > 0 && endlessDeadline <= rise + 1 && endlessDeadline > 200);

        // A capture that began before the motion and never answers is still a stuck capture: no credit for it.
        var stuck = Calm();
        stuck.Begin(50, 1);
        stuck.Pause(60);
        check("Liquid glass's motion pause does not hide a stuck capture",
            stuck.Protection(50 + GlassSampleValidity.HardSafety + 1) > 0);

        // A protection already risen is not undone by a pause; the level holds while paused.
        var risen = Calm();
        double before = risen.Protection(900);
        risen.Pause(900);
        double held = risen.Protection(1100);
        check(FormattableString.Invariant($"Liquid glass's motion pause keeps a protection already risen ({before:0.000} -> {held:0.000})"),
            before > 0 && risen.Protecting && Math.Abs(held - before) < 1e-9);
    }
}
