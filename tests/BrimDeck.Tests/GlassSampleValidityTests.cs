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

        // No area to read (no capture starts at all): the watchdog still runs from the last accepted capture (its TTL plus the
        // 10 ms that capture took, the pace).
        var noArea = new GlassSampleValidity(0);
        var noAreaProbe = new FakeProbe(noArea);
        noAreaProbe.Start(1, 10, calm);
        noAreaProbe.Advance(10, 1);
        long deadline = noArea.NextDeadline(10);
        noAreaProbe.Advance(1600, 1);
        check($"Liquid glass guards the text when there is no area to read (watchdog first at {deadline} ms)",
            deadline == GlassSampleValidity.IdleTtl + 10 + 1 && noArea.Protection(760) == 0 && noArea.Protection(762) > 0 && noArea.Protection(1600) == 1);

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

        // Recovery: one fresh reading that differs from the last one kept only starts it; two in a row that agree end it; two that
        // disagree do not.
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

        // A gap that ends on the same scene: the first fresh reading agrees with the last one kept, so the two agree and the
        // protection ends at once (V3 C4: a busy machine's stalled timer otherwise keeps the veils thick for one more reading).
        var same = new GlassSampleValidity(0);
        var sameProbe = new FakeProbe(same);
        sameProbe.Start(1, 10, calm);
        sameProbe.Advance(10, 1);
        sameProbe.Advance(1300, 1);
        double sameBefore = same.Protection(1300);
        sameProbe.Now = 1300; sameProbe.Start(1, 150, (.35, .05));
        var sameVerdict = sameProbe.Advance(1450, 1);
        check(FormattableString.Invariant($"Liquid glass ends the protection on one fresh reading that agrees with the last one kept ({sameBefore:0.000} -> {sameVerdict})"),
            sameBefore > 0 && sameVerdict == GlassSampleValidity.Verdict.Accepted && same.Protection(1450) == 0 && same.Tails == (.35, .05));

        // The age is the capture's start: a 600 ms idle capture that answers within the TTL is kept, but counts from when it began;
        // the protection allows the next reading as long as this one took (the pace, at most HardSafety).
        var slow = new GlassSampleValidity(0);
        var slowProbe = new FakeProbe(slow);
        slowProbe.Start(1, 600, calm);
        var slowVerdict = slowProbe.Advance(600, 1);
        long slowDeadline = slow.NextDeadline(600);
        check($"Liquid glass counts a reading's age from its capture's start ({slowVerdict}, next look at {slowDeadline} ms)",
            slowVerdict == GlassSampleValidity.Verdict.Accepted && slowDeadline == GlassSampleValidity.IdleTtl + GlassSampleValidity.HardSafety + 1
            && slow.Protection(1250) == 0 && slow.Protection(1260) > 0);

        // V3 C4 slow mode: on a busy machine a ring read takes 150-490 ms. A capture that began in time (the next one at rest, 500 ms
        // after the last) and answers 400 ms later is no gap: the protection waits for it, so the veils do not pulse at rest.
        var timely = new GlassSampleValidity(0);
        var timelyProbe = new FakeProbe(timely);
        timelyProbe.Start(1, 10, calm);
        timelyProbe.Advance(10, 1);
        timelyProbe.Now = 520;
        timelyProbe.Start(1, 400, (.31, .1));
        var timelyWatch = new List<(long At, double Level)>();
        var timelyVerdict = timelyProbe.Advance(920, 1, timelyWatch);
        check($"Liquid glass waits for a slow capture that began in time at rest ({timelyVerdict}, {timelyWatch.Count(w => w.Level > 0)} looks with protection)",
            timelyVerdict == GlassSampleValidity.Verdict.Accepted && timelyWatch.All(w => w.Level == 0) && !timely.Protecting && timely.Tails == (.31, .1));
        // The same in a burst (80 ms apart, a 200 ms read).
        var timelyBurst = new GlassSampleValidity(0);
        timelyBurst.Mode(true, 0);
        var timelyBurstProbe = new FakeProbe(timelyBurst);
        timelyBurstProbe.Start(1, 10, calm);
        timelyBurstProbe.Advance(10, 1);
        timelyBurstProbe.Now = 90;
        timelyBurstProbe.Start(1, 200, calm);
        var timelyBurstVerdict = timelyBurstProbe.Advance(290, 1);
        check($"Liquid glass waits for a slow capture that began in time in a burst ({timelyBurstVerdict})",
            timelyBurstVerdict == GlassSampleValidity.Verdict.Accepted && !timelyBurst.Protecting && timelyBurst.Protection(290) == 0);
        // A burst on a busy machine: every read takes 300 ms (the first after fast ones 400 ms) and the next begins 80 ms after the
        // last answered. Each is kept and the protection never rises: the TTL allows for the pace.
        var busy = new GlassSampleValidity(0);
        busy.Mode(true, 0);
        var busyProbe = new FakeProbe(busy);
        busyProbe.Start(1, 30, calm);
        busyProbe.Advance(30, 1);
        var busyWatch = new List<(long At, double Level)>();
        var busyVerdicts = new List<GlassSampleValidity.Verdict?>();
        for (int i = 0; i < 6; i++)
        {
            busyProbe.Now += 80;
            busyProbe.Start(1, i == 0 ? 400 : 300, calm);
            busyVerdicts.Add(busyProbe.Advance(busyProbe.Now + (i == 0 ? 400 : 300), 1, busyWatch));
        }
        check($"Liquid glass keeps a busy machine's slow readings in a burst ({string.Join(", ", busyVerdicts.Distinct())}, {busyWatch.Count(w => w.Level > 0)} looks with protection)",
            busyVerdicts.All(v => v == GlassSampleValidity.Verdict.Accepted) && busyWatch.All(w => w.Level == 0) && busy.Dropped == 0);
        // A capture that began in time but never answers is still not hidden: protection from HardSafety after it began, the worst
        // range by WorstBy after the last reading.
        var timelyStuck = new GlassSampleValidity(0);
        var timelyStuckProbe = new FakeProbe(timelyStuck);
        timelyStuckProbe.Start(1, 10, calm);
        timelyStuckProbe.Advance(10, 1);
        timelyStuckProbe.Now = 520;
        timelyStuckProbe.Start(1, null, null);
        timelyStuckProbe.Advance(1510, 1);
        check(FormattableString.Invariant($"Liquid glass guards the text when a capture that began in time never answers ({timelyStuck.Protection(1020):0.000} at 1020 ms)"),
            timelyStuck.Protection(1019) == 0 && timelyStuck.Protection(1021) > 0 && timelyStuck.Protection(1510) == 1);

        // A burst's shorter TTL counts from the burst: a hint after a quiet 400 ms does not raise the protection at once.
        var burst = new GlassSampleValidity(0);
        var burstProbe = new FakeProbe(burst);
        burstProbe.Start(1, 10, calm);
        burstProbe.Advance(10, 1);
        burst.Mode(true, 400);
        check("Liquid glass's burst TTL counts from the burst's start", burst.Protection(500) == 0 && burst.Protection(660) == 0 && burst.Protection(661) > 0);

        // Before the first reading the glass answers for the worst range.
        var fresh = new GlassSampleValidity(0);
        check("Liquid glass answers for the worst range before its first reading", fresh.SafeTails(0) == (1, 0) && fresh.First);

        // Review v2 U12: at most two captures outstanding in the whole app. A capture given up (stuck on its worker) still counts
        // until its own Leave: the panel's replacement is refused while the other slot is taken, and is let in once one frees.
        var budget = new GlassReadBudget<object>();
        object one = new(), two = new(), three = new();
        bool firstTwo = budget.TryEnter(one, 1) && budget.TryEnter(two, 1), third = budget.TryEnter(three, 1);
        bool replacement = budget.TryEnter(one, 2);
        bool capped = budget.Running == 2;
        budget.Leave(one, 2);
        bool unknownFreesNothing = budget.Running == 2;
        budget.Leave(one, 1);
        bool replacedLater = budget.Running == 1 && budget.TryEnter(one, 2) && budget.Running == 2 && !budget.TryEnter(three, 1);
        budget.Leave(one, 1);
        bool lateFreesNothing = budget.Running == 2;
        check($"Liquid glass runs at most two captures at once in the app, a given-up capture counted until it answers ({budget.Refused} refused)",
            firstTwo && !third && !replacement && capped && unknownFreesNothing && replacedLater && lateFreesNothing && budget.Refused == 3);
        // A panel stuck alone recovers: its replacement takes the free slot, the stuck capture still counted, and a third capture
        // waits for one of them to answer.
        var alone = new GlassReadBudget<object>();
        bool stuckThenReplaced = alone.TryEnter(one, 1) && alone.TryEnter(one, 2) && alone.Running == 2 && !alone.TryEnter(two, 1);
        alone.Leave(one, 2);
        bool slotFreed = alone.Running == 1 && alone.TryEnter(two, 1);
        alone.Leave(one, 1);
        alone.Leave(two, 1);
        check($"Liquid glass lets a stuck panel read again beside its given-up capture without passing the cap ({alone.Running} outstanding)",
            stuckThenReplaced && slotFreed && alone.Running == 0);

        // The same with real work in flight, counted apart from the budget: a capture runs on a worker task and is held until
        // released; the budget is entered before it starts and left once it answers, on this thread, as the window does (the
        // capture's continuation comes back to the window's thread). Panel A's first capture is given up as stuck while panel B's
        // runs; A's replacement is refused, and the captures actually running never pass two.
        var tasks = new GlassReadBudget<object>();
        int live = 0, most = 0;
        var gates = new Dictionary<(object, long), TaskCompletionSource>();
        var workers = new Dictionary<(object, long), Task>();
        bool Start(object holder, long id)
        {
            if (!tasks.TryEnter(holder, id)) return false;
            var gate = gates[(holder, id)] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var entered = new ManualResetEventSlim();
            workers[(holder, id)] = Task.Run(async () =>
            {
                int now = Interlocked.Increment(ref live);
                int seen; while ((seen = Volatile.Read(ref most)) < now && Interlocked.CompareExchange(ref most, now, seen) != seen) { }
                entered.Set();
                try { await gate.Task; } finally { Interlocked.Decrement(ref live); }
            });
            return entered.Wait(TimeSpan.FromSeconds(5));
        }
        bool Answer(object holder, long id)
        {
            gates[(holder, id)].SetResult();
            bool done = workers[(holder, id)].Wait(5000);
            tasks.Leave(holder, id);
            return done;
        }
        object a = new(), b = new();
        bool bothRunning = Start(a, 1) && Start(b, 1) && Volatile.Read(ref live) == 2;
        bool replacementRefused = !Start(a, 2) && Volatile.Read(ref live) == 2 && tasks.Running == 2;
        bool stuckAnswered = Answer(a, 1) && Volatile.Read(ref live) == 1 && tasks.Running == 1;
        bool replacementLater = Start(a, 2) && Volatile.Read(ref live) == 2 && !Start(new object(), 1);
        bool allDone = Answer(a, 2) && Answer(b, 1) && Volatile.Read(ref live) == 0 && tasks.Running == 0;
        check($"Liquid glass never has more than two captures actually running, a stuck one's replacement refused until it answers (most {most})",
            bothRunning && replacementRefused && stuckAnswered && replacementLater && allDone && most == 2);
    }
}
