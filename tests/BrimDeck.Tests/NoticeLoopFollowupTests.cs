using BrimDeck.Core.Notices;

// Follow-ups to the notice loop (pr-plan PLAN-notice-latency-followups B and C, REVIEW-notice-loop E and F). Every case
// drives the real WindowsNoticeLoop with a fake Windows, in the order that used to go wrong:
//  E: the log entry of a toast arrives after the startup baseline (which already lists the toast) was applied.
//  F: Stop and Start land between a step applying its result and handing it over.
//  B: Windows wrote the log entry but the listener cannot return the notification yet.
//  C: access is revoked with log ids still queued, and granted again.
internal static class NoticeLoopFollowupTests
{
    private const string App = "Vendor.Chat";

    internal static void Run(Action<string, bool> check)
    {
        LateLogAfterBaseline(check);
        LateLogLookupFails(check);
        StopStartBetweenApplyAndPublish(check);
        MissingNotificationReconciliation(check);
        AccessRevokedWithQueuedIds(check);
    }

    // E. The log names only toasts delivered after BrimDeck subscribed. A toast the baseline listed but that the log names
    // afterwards arrived after the subscription, so it is shown; one the log never names was already there and is not.
    private static void LateLogAfterBaseline(Action<string, bool> check)
    {
        var (loop, run, host) = Started(10, 11);
        host.Listed[50] = Input(50);
        host.Finish([10u, 11u, 50u]);
        loop.Step(run, host, default);
        check("Loop followup E: a baseline listing a toast arrived after subscribing shows nothing by itself", host.Arrived.Count == 0 && host.Missed.Count == 0);
        Log(loop, run, 50);
        loop.Step(run, host, default);
        check("Loop followup E: the log entry reaching the loop after that baseline still shows the toast",
            Keys(host.Arrived).SequenceEqual([Key(50)]) && host.Lookups == 1);
        Log(loop, run, 50);
        loop.Step(run, host, default);
        check("Loop followup E: a second entry for the same toast (submitted, then delivered) does not show it twice",
            Keys(host.Arrived).SequenceEqual([Key(50)]) && host.Lookups == 1);
        check("Loop followup E: the toasts that were already there at startup are never shown", Keys(host.Arrived).All(key => key == Key(50)) && host.Missed.Count == 0);

        // The toast leaves Windows' list later: the row the baseline recorded for it is the one removed.
        host.Listed.Remove(50);
        loop.Refresh(WindowsNoticeBaseline.None, host.Now);
        loop.Step(run, host, default);
        host.Finish();
        loop.Step(run, host, default);
        check("Loop followup E: a toast shown from the late entry is removed when Windows drops it", host.Removed.SequenceEqual([Key(50)]));

        // The reading after the baseline settles which ids are old news: an entry for a baseline id after it is not trusted.
        var (laterLoop, laterRun, later) = Started(10);
        later.Listed[51] = Input(51);
        later.Finish([10u, 51u]);
        laterLoop.Step(laterRun, later, default);
        laterLoop.Refresh(WindowsNoticeBaseline.None, later.Now);
        laterLoop.Step(laterRun, later, default);
        later.Finish();
        laterLoop.Step(laterRun, later, default);
        Log(laterLoop, laterRun, 51);
        laterLoop.Step(laterRun, later, default);
        check("Loop followup E: an entry that comes after a later reading has settled the baseline ids is not shown", later.Arrived.Count == 0 && later.Lookups == 0);

        // Not the startup baseline: a reading that finds a toast first shows it, and the entry after it is the same toast.
        var (steadyLoop, steadyRun, steady) = Settled(10);
        steady.Listed[60] = Input(60);
        steadyLoop.Refresh(WindowsNoticeBaseline.None, steady.Now);
        steadyLoop.Step(steadyRun, steady, default);
        steady.Finish();
        steadyLoop.Step(steadyRun, steady, default);
        Log(steadyLoop, steadyRun, 60);
        steadyLoop.Step(steadyRun, steady, default);
        check("Loop followup E: a toast a later reading found and showed is not shown again by its entry", Keys(steady.Arrived).SequenceEqual([Key(60)]) && steady.Lookups == 0);
    }

    // E, when the lookup fails. The entry names a toast the baseline absorbed, but Windows cannot return it yet (or the read fails):
    // the id keeps its place, so the reading that follows shows the toast instead of taking it for one the baseline already had.
    private static void LateLogLookupFails(Action<string, bool> check)
    {
        var (loop, run, host) = Started(10);
        host.Listed[50] = Input(50);
        host.Finish([10u, 50u]);
        loop.Step(run, host, default);
        var at = host.Now;
        host.Lookup = _ => WindowsNoticeNotificationRead.Missing;
        Log(loop, run, 50);
        loop.Step(run, host, default);
        check("Loop followup E (lookup fails): the failed lookup shows nothing", host.Arrived.Count == 0 && host.Lookups == 1);
        int reads = host.ListReads;
        host.Now = at + WindowsNoticeLogBatch.FailedReadReconcileInterval;
        loop.Step(run, host, default);
        check("Loop followup E (lookup fails): a full reading follows within the failed-read interval", host.ListReads == reads + 1);
        host.Finish();
        loop.Step(run, host, default);
        check("Loop followup E (lookup fails): that reading shows the toast the lookup could not return, once", Keys(host.Arrived).SequenceEqual([Key(50)]) && host.Missed.Count == 0);
        host.Lookup = null;
        Log(loop, run, 50);
        loop.Step(run, host, default);
        check("Loop followup E (lookup fails): a later entry for it does not show it again", Keys(host.Arrived).SequenceEqual([Key(50)]));

        // A reading that cannot read it either shows nothing and leaves it for the next one.
        var (laterLoop, laterRun, later) = Started(10);
        later.Listed[51] = Input(51);
        later.Finish([10u, 51u]);
        laterLoop.Step(laterRun, later, default);
        later.Lookup = _ => WindowsNoticeNotificationRead.Missing;
        Log(laterLoop, laterRun, 51);
        laterLoop.Step(laterRun, later, default);
        laterLoop.Refresh(WindowsNoticeBaseline.None, later.Now);
        laterLoop.Step(laterRun, later, default);
        later.Listed.Remove(51);
        later.Finish([10u, 51u]);
        laterLoop.Step(laterRun, later, default);
        check("Loop followup E (lookup fails): a reading that cannot read it shows nothing", later.Arrived.Count == 0);
        later.Listed[51] = Input(51);
        laterLoop.Refresh(WindowsNoticeBaseline.None, later.Now);
        laterLoop.Step(laterRun, later, default);
        later.Finish();
        laterLoop.Step(laterRun, later, default);
        check("Loop followup E (lookup fails): the next reading that can read it shows it", Keys(later.Arrived).SequenceEqual([Key(51)]));

        // The toast leaves Windows before anything could read it: nothing is kept for it.
        var (goneLoop, goneRun, gone) = Started(10);
        gone.Listed[52] = Input(52);
        gone.Finish([10u, 52u]);
        goneLoop.Step(goneRun, gone, default);
        gone.Lookup = _ => WindowsNoticeNotificationRead.Missing;
        Log(goneLoop, goneRun, 52);
        goneLoop.Step(goneRun, gone, default);
        gone.Listed.Remove(52);
        goneLoop.Refresh(WindowsNoticeBaseline.None, gone.Now);
        goneLoop.Step(goneRun, gone, default);
        gone.Finish([10u]);
        goneLoop.Step(goneRun, gone, default);
        gone.Listed[52] = Input(52);
        goneLoop.Refresh(WindowsNoticeBaseline.None, gone.Now);
        goneLoop.Step(goneRun, gone, default);
        gone.Finish();
        goneLoop.Step(goneRun, gone, default);
        check("Loop followup E (lookup fails): a toast that left before it could be read keeps no claim on a later one with the same id", gone.Arrived.Count == 1 && Keys(gone.Arrived).Single() == Key(52));
    }

    // F. The step checked that its run is current and applied its result under the loop's lock, then handed the changes over
    // outside it. A Stop and Start between those two points ends the run and begins another with a new baseline. The changes
    // are real notices that arrived while notices were on: they are handed over once, the new run's baseline does not repeat
    // them, and the ended run does nothing more. (Dropping them would lose the notice: the new baseline takes it in as old.)
    private static void StopStartBetweenApplyAndPublish(Action<string, bool> check)
    {
        var (loop, run, host) = Settled(10);
        var next = new WindowsNoticeRun();
        host.Listed[70] = Input(70);
        host.OnPublish = () =>
        {
            host.OnPublish = null;
            loop.End(run);
            loop.Begin(next, host.Now);
        };
        Log(loop, run, 70);
        loop.Step(run, host, default);
        check("Loop followup F: changes applied before Stop and Start are handed over once", Keys(host.Arrived).SequenceEqual([Key(70)]) && !loop.IsCurrent(run) && loop.IsCurrent(next));

        int reads = host.ListReads;
        loop.Step(next, host, default);
        host.Finish([10u, 70u]);
        loop.Step(next, host, default);
        check("Loop followup F: the new run's baseline takes the toast in without showing it again",
            host.ListReads == reads + 1 && Keys(host.Arrived).SequenceEqual([Key(70)]) && host.Missed.Count == 0);

        int arrived = host.Arrived.Count;
        host.Listed[71] = Input(71);
        Log(loop, run, 71);
        bool oldStepped = loop.Step(run, host, default);
        check("Loop followup F: the ended run takes no more steps", !oldStepped && host.Arrived.Count == arrived && host.Lookups == 1);

        Log(loop, next, 71);
        loop.Step(next, host, default);
        check("Loop followup F: the new run shows what arrives after it began", Keys(host.Arrived).SequenceEqual([Key(70), Key(71)]));
    }

    // B. Windows wrote the log entry before the listener can return the notification. Waiting for the clear interval would
    // show the toast up to ReconcileInterval late; the full reading comes about a second after the last one instead, unless a
    // clear entry already asked for one (QQ clears its previous toast with every message: a busy chat keeps its pace).
    private static void MissingNotificationReconciliation(Action<string, bool> check)
    {
        var (loop, run, host) = Settled(10);
        int reads = host.ListReads;
        var settledAt = host.Now;
        host.Lookup = _ => WindowsNoticeNotificationRead.Missing;
        Log(loop, run, 80);
        loop.Step(run, host, default);
        host.Now = settledAt + TimeSpan.FromMilliseconds(500);
        loop.Step(run, host, default);
        check("Loop followup B: a notification the listener cannot return yet waits no longer than the failed-read interval",
            host.ListReads == reads && host.Lookups == 1);
        host.Now = settledAt + WindowsNoticeLogBatch.FailedReadReconcileInterval;
        loop.Step(run, host, default);
        check("Loop followup B: the full reading comes about a second after the last one, not ReconcileInterval", host.ListReads == reads + 1);
        host.Listed[80] = Input(80);
        host.Finish();
        loop.Step(run, host, default);
        check("Loop followup B: that reading shows the notification", Keys(host.Arrived).SequenceEqual([Key(80)]));

        // A clear entry is in the same batch: the missing id rides with it.
        var (clearLoop, clearRun, clear) = Settled(10);
        clearRun.Batch.Add(WindowsNoticeLogEvent.Reconcile);
        clear.Lookup = _ => WindowsNoticeNotificationRead.Missing;
        var clearSettled = clear.Now;
        int clearReads = clear.ListReads;
        Log(clearLoop, clearRun, 81);
        clearLoop.Step(clearRun, clear, default);
        clear.Now = clearSettled + WindowsNoticeLoop.ReconcileInterval - TimeSpan.FromSeconds(1);
        clearLoop.Step(clearRun, clear, default);
        check("Loop followup B: with a clear entry in the batch the missing id keeps the clear interval", clear.ListReads == clearReads);
        clear.Now = clearSettled + WindowsNoticeLoop.ReconcileInterval;
        clearLoop.Step(clearRun, clear, default);
        check("Loop followup B: and is read with the clear once that interval has passed", clear.ListReads == clearReads + 1);

        // An id that is found needs no reading at all.
        var (foundLoop, foundRun, found) = Settled(10);
        found.Listed[82] = Input(82);
        int foundReads = found.ListReads;
        Log(foundLoop, foundRun, 82);
        foundLoop.Step(foundRun, found, default);
        found.Now += WindowsNoticeLogBatch.FailedReadReconcileInterval + TimeSpan.FromSeconds(1);
        foundLoop.Step(foundRun, found, default);
        check("Loop followup B: a notification that is found reads no list", found.ListReads == foundReads && Keys(found.Arrived).SequenceEqual([Key(82)]));
    }

    // C. Access was revoked while ids the log named were still queued. After access is granted again the reading is a Start
    // baseline, which takes in what is listed without showing it: the queued ids must not be read and shown as new then.
    private static void AccessRevokedWithQueuedIds(Action<string, bool> check)
    {
        var (loop, run, host) = Settled(10);
        host.Listed[90] = Input(90);
        host.Listed[91] = Input(91);
        host.Access = WindowsNoticeAccess.Denied;
        Log(loop, run, 90);
        Log(loop, run, 91);
        loop.Step(run, host, default);
        check("Loop followup C: access revoked stops the run and drops the ids still queued",
            host.States.Last() == WindowsNoticeLoopState.Denied && !host.Listening && run.Batch.Snapshot().Ids == 0 && !run.Batch.HasIds);

        host.Access = WindowsNoticeAccess.Allowed;
        loop.Refresh(WindowsNoticeBaseline.Start, host.Now);
        loop.Step(run, host, default);
        loop.Step(run, host, default);
        host.Finish([10u, 90u, 91u]);
        loop.Step(run, host, default);
        check("Loop followup C: after access is granted again the listed notices are not shown as new",
            host.Arrived.Count == 0 && host.Missed.Count == 0 && host.Lookups == 0 && host.Listening);

        Log(loop, run, 92);
        host.Listed[92] = Input(92);
        loop.Step(run, host, default);
        check("Loop followup C: a notification that arrives after that is still shown", Keys(host.Arrived).SequenceEqual([Key(92)]));
    }

    private static (WindowsNoticeLoop Loop, WindowsNoticeRun Run, FakeHost Host) Started(params uint[] listed)
    {
        var host = new FakeHost();
        foreach (var id in listed) host.Listed[id] = Input(id);
        var loop = new WindowsNoticeLoop(host.Now);
        var run = new WindowsNoticeRun();
        loop.Begin(run, host.Now);
        loop.Step(run, host, default);
        return (loop, run, host);
    }

    private static (WindowsNoticeLoop Loop, WindowsNoticeRun Run, FakeHost Host) Settled(params uint[] listed)
    {
        var (loop, run, host) = Started(listed);
        host.Finish();
        loop.Step(run, host, default);
        return (loop, run, host);
    }

    private static void Log(WindowsNoticeLoop loop, WindowsNoticeRun run, uint id)
    {
        WindowsNoticeLoop.Arrive(run, WindowsNoticeTriggers.Parse(3052, null, App, id));
        loop.Fired(run);
    }

    private static IEnumerable<string> Keys(IEnumerable<NoticeInput> inputs) => inputs.Select(input => input.InstanceKey);
    private static string Key(uint id) => Input(id).InstanceKey;

    private static NoticeInput Input(uint id)
        => new()
        {
            Origin = NoticeOrigin.Windows,
            AppKey = "windows:" + App,
            AppName = App,
            AppId = App,
            InstanceKey = $"windows:{App}:{id}",
            Title = "sender " + id,
            Body = "body",
            CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(id)
        };

    // Windows as the loop sees it: Windows holds the listed notifications; a full reading stays out until Finish.
    private sealed class FakeHost : IWindowsNoticeHost
    {
        public readonly SortedDictionary<uint, NoticeInput> Listed = [];
        private readonly List<(TaskCompletionSource<WindowsNoticeReadSnapshot> Read, IReadOnlySet<uint> Known)> _reads = [];
        public WindowsNoticeAccess Access = WindowsNoticeAccess.Allowed;
        public Func<uint, WindowsNoticeNotificationRead>? Lookup;
        // Runs when the loop hands changes over, before they are taken: where the interleaving of another thread is put.
        public Action? OnPublish;
        public bool Listening, LogOpen;
        public int Lookups, ListReads;
        public readonly List<WindowsNoticeLoopState> States = [];
        public readonly List<NoticeInput> Arrived = [], Missed = [];
        public readonly List<string> Removed = [];

        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch.AddSeconds(1_000_000);
        public DateTimeOffset LastInteraction => DateTimeOffset.MinValue;
        bool IWindowsNoticeHost.LogOpen => LogOpen;

        WindowsNoticeAccess IWindowsNoticeHost.Access() => Access;
        public void OpenListener() => Listening = true;
        public void CloseListener() => Listening = false;

        public WindowsNoticeNotificationRead ReadOne(uint id)
        {
            Lookups++;
            if (Lookup is { } lookup) return lookup(id);
            return Listed.TryGetValue(id, out var input) ? new(true, input.AppId, input) : WindowsNoticeNotificationRead.Missing;
        }

        public bool Probe(IEnumerable<uint> ids) => ids.Any(Listed.ContainsKey);

        public Task<WindowsNoticeReadSnapshot> ReadAll(IReadOnlySet<uint> known, CancellationToken token)
        {
            ListReads++;
            var read = new TaskCompletionSource<WindowsNoticeReadSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _reads.Add((read, known));
            return read.Task;
        }

        public void Finish(IEnumerable<uint>? ids = null)
        {
            var (read, known) = _reads[^1];
            _reads.RemoveAt(_reads.Count - 1);
            var listed = (ids ?? Listed.Keys).ToList();
            read.SetResult(new(listed, listed.Where(id => !known.Contains(id)).ToDictionary(id => id, id => (NoticeInput?)Listed.GetValueOrDefault(id))));
        }

        public bool LogEnabled() => true;
        public bool OpenLog() { LogOpen = true; return true; }
        public void CloseLog() => LogOpen = false;
        public void Wake() { }
        public void Report(WindowsNoticeLoopState state) => States.Add(state);

        public void Publish(WindowsNoticeList.Changes changes)
        {
            OnPublish?.Invoke();
            Arrived.AddRange(changes.Arrived);
            Missed.AddRange(changes.Missed);
            Removed.AddRange(changes.Removed);
        }

        public void Synced(int covers) { }
    }
}
