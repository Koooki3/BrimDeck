using BrimDeck.Core;

// Review v2 U05: liquid glass's readings are trusted by their capture's start, dropped when late or misplaced, and an independent
// watchdog raises the text's protection to the worst range whatever the worker does (never answers, answers after 2 s, finds no
// area, reads a place the panel has left). Recovery needs two fresh readings that agree.
internal static class GlassSampleValidityTests
{
    // A fake probe: a capture that answers after Delay with these tails (null: never answers), on a hand-driven clock.
    private sealed class FakeProbe(GlassSampleValidity validity)
    {
        public long Now;
        private (GlassSampleValidity.Ticket Ticket, long At, (double, double)? Tails, bool Matches)? _running;
        public bool Start(long epoch, long? delay, (double, double)? tails, bool matches = true)
        {
            if (validity.Begin(Now, epoch) is not { } ticket) return false;
            _running = delay is { } d ? (ticket, Now + d, tails, matches) : null;
            return true;
        }
        // Advance the clock in watchdog steps; the capture answers when due. Returns the verdict if one came.
        public GlassSampleValidity.Verdict? Advance(long to, long epoch, List<(long At, double Level)>? watch = null)
        {
            GlassSampleValidity.Verdict? verdict = null;
            while (Now < to)
            {
                long next = Math.Min(to, validity.NextDeadline(Now));
                if (_running is { } r && r.At < next) next = r.At;
                Now = Math.Max(Now + 1, next);
                if (_running is { } due && due.At <= Now) { _running = null; verdict = validity.Complete(due.Ticket, Now, epoch, due.Matches, due.Tails); }
                watch?.Add((Now, validity.Protection(Now)));
            }
            return verdict;
        }
    }

    internal static void Run(Action<string, bool> check)
    {
        (double, double) calm = (.3, .1);

        // Never answers (a capture a hint started 100 ms after the last reading): the watchdog alone raises the protection from
        // HardSafety after that capture began (before the idle TTL) to the worst range by WorstBy, and the stuck capture is given
        // up after Abandon so a new one can start.
        var never = new GlassSampleValidity(0);
        var neverProbe = new FakeProbe(never);
        neverProbe.Start(1, 10, calm);
        neverProbe.Advance(10, 1);
        neverProbe.Now = 100;
        bool started = neverProbe.Start(1, null, null);
        var watched = new List<(long At, double Level)>();
        neverProbe.Advance(1599, 1, watched);
        double before = never.Protection(599), after = never.Protection(601), atWorst = never.Protection(1500);
        bool blocked = never.Begin(1599, 1) is null, freed = never.Begin(1600, 1) is not null;
        check(FormattableString.Invariant($"Liquid glass's watchdog guards the text when a capture never answers ({before:0.000} at 599 ms, {after:0.000} at 601 ms, worst by 1500 ms, {watched.Count} watchdog looks)"),
            started && before == 0 && after > 0 && atWorst == 1 && never.SafeTails(1500) == (1, 0) && watched.Count > 0 && watched.All(w => w.At < 1500 || w.Level == 1)
            && blocked && freed && never.Abandoned == 1);

        // Answers after 2 s: dropped as late (its capture began long ago), and the protection already at worst stays there.
        var late = new GlassSampleValidity(0);
        var lateProbe = new FakeProbe(late);
        lateProbe.Start(1, 10, calm);
        lateProbe.Advance(10, 1);
        lateProbe.Now = 500;
        lateProbe.Start(1, 2000, (.9, .8));
        var lateVerdict = lateProbe.Advance(2600, 1);
        check($"Liquid glass drops a reading that answers 2 s after its capture began ({lateVerdict})",
            lateVerdict == GlassSampleValidity.Verdict.Late && late.Protection(2600) == 1 && late.Tails == calm && late.Dropped == 1);

        // No area to read (no capture starts at all): the watchdog still runs from the last accepted capture.
        var noArea = new GlassSampleValidity(0);
        var noAreaProbe = new FakeProbe(noArea);
        noAreaProbe.Start(1, 10, calm);
        noAreaProbe.Advance(10, 1);
        long deadline = noArea.NextDeadline(10);
        noAreaProbe.Advance(1600, 1);
        check($"Liquid glass guards the text when there is no area to read (watchdog first at {deadline} ms)",
            deadline == GlassSampleValidity.IdleTtl + 1 && noArea.Protection(760) > 0 && noArea.Protection(1600) == 1);

        // A drag: every reading is for a place the panel has left (another epoch, a mismatching area): all dropped, protection rises.
        var drag = new GlassSampleValidity(0);
        drag.Mode(true, 0);
        var dragProbe = new FakeProbe(drag);
        dragProbe.Start(1, 10, calm);
        dragProbe.Advance(10, 1);
        int dropped = 0;
        for (long t = 80; t < 1600; t += 80)
        {
            dragProbe.Now = t;
            if (dragProbe.Start(t / 80, 20, (.95, .9))) dropped += dragProbe.Advance(t + 20, t / 80 + 1) == GlassSampleValidity.Verdict.Mismatch ? 1 : 0;
        }
        check($"Liquid glass drops the readings of a drag and guards the text meanwhile ({dropped} dropped)",
            dropped >= 18 && drag.Protection(1600) == 1 && drag.Tails == calm && drag.Protection(300) > 0);

        // Recovery: one fresh reading only starts it; two in a row that agree end it; two that disagree do not.
        var recover = new GlassSampleValidity(0);
        var recoverProbe = new FakeProbe(recover);
        recoverProbe.Start(1, 10, calm);
        recoverProbe.Advance(10, 1);
        recoverProbe.Advance(2000, 1);
        recoverProbe.Now = 2000; recoverProbe.Start(1, 10, (.6, .2));
        var first = recoverProbe.Advance(2010, 1);
        recoverProbe.Now = 2080; recoverProbe.Start(1, 10, (.9, .5));
        var disagree = recoverProbe.Advance(2090, 1);
        recoverProbe.Now = 2160; recoverProbe.Start(1, 10, (.92, .45));
        var agree = recoverProbe.Advance(2170, 1);
        check($"Liquid glass recovers only from two fresh readings that agree ({first}, {disagree}, {agree})",
            first == GlassSampleValidity.Verdict.Recovering && disagree == GlassSampleValidity.Verdict.Recovering
            && agree == GlassSampleValidity.Verdict.Accepted && recover.Protection(2170) == 0 && recover.Tails == (.92, .45));

        // The age is the capture's start: a 600 ms idle capture that answers within the TTL is kept, but counts from when it began.
        var slow = new GlassSampleValidity(0);
        var slowProbe = new FakeProbe(slow);
        slowProbe.Start(1, 600, calm);
        var slowVerdict = slowProbe.Advance(600, 1);
        long slowDeadline = slow.NextDeadline(600);
        check($"Liquid glass counts a reading's age from its capture's start ({slowVerdict}, next look at {slowDeadline} ms)",
            slowVerdict == GlassSampleValidity.Verdict.Accepted && slowDeadline == GlassSampleValidity.IdleTtl + 1 && slow.Protection(760) > 0);

        // A burst's shorter TTL counts from the burst: a hint after a quiet 400 ms does not raise the protection at once.
        var burst = new GlassSampleValidity(0);
        var burstProbe = new FakeProbe(burst);
        burstProbe.Start(1, 10, calm);
        burstProbe.Advance(10, 1);
        burst.Mode(true, 400);
        check("Liquid glass's burst TTL counts from the burst's start", burst.Protection(500) == 0 && burst.Protection(651) > 0);

        // Before the first reading the glass answers for the worst range.
        var fresh = new GlassSampleValidity(0);
        check("Liquid glass answers for the worst range before its first reading", fresh.SafeTails(0) == (1, 0) && fresh.First);

        // Review v2 U12: at most two captures in the whole app; a panel's stuck capture given up frees its slot for its next one,
        // and the stuck one's late answer frees nothing.
        var budget = new GlassReadBudget<object>();
        object one = new(), two = new(), three = new();
        bool firstTwo = budget.TryEnter(one, 1) && budget.TryEnter(two, 1), third = budget.TryEnter(three, 1);
        bool retake = budget.TryEnter(one, 2);
        budget.Leave(one, 1);
        bool stillHeld = budget.Running == 2;
        budget.Leave(one, 2);
        bool thirdLater = budget.TryEnter(three, 1);
        budget.Drop(two);
        check($"Liquid glass runs at most two captures at once in the app ({budget.Refused} refused)",
            firstTwo && !third && retake && stillHeld && thirdLater && budget.Running == 1 && budget.Refused == 1);
    }
}
