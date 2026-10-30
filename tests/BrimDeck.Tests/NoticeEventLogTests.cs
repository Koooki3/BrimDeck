using BrimDeck.Core.Notices;

// The push notification log as the Windows source's trigger: which entries wake it, how a batch is read one notification at a
// time, when the whole list is read, which trigger is chosen and how often the list is read under each. The loop tests drive
// the real WindowsNoticeLoop with a fake Windows (FakeHost) and count the list readings it really starts.
internal static class NoticeEventLogTests
{
    private const string App = "Vendor.Chat";

    internal static void Run(Action<string, bool> check)
    {
        Filter(check);
        Query(check);
        Parsing(check);
        Batching(check);
        Reading(check);
        ListObservations(check);
        LoopStartup(check);
        LoopSteadyState(check);
        LoopResume(check);
        LoopReplacement(check);
        LoopFailures(check);
        CompletionAndRefresh(check);
        Coalescing(check);
        Choice(check);
        Pace(check);
    }

    private static void Filter(Action<string, bool> check)
    {
        check("Event log: a delivered toast wakes the source", WindowsNoticeTriggers.Relevant(3052, null));
        check("Event log: cleared toasts wake the source", WindowsNoticeTriggers.Relevant(3055, null));
        check("Event log: a submitted toast wakes the source", WindowsNoticeTriggers.Relevant(3053, "toast") && WindowsNoticeTriggers.Relevant(3053, "Toast"));
        check("Event log: a submitted tile or badge does not", !WindowsNoticeTriggers.Relevant(3053, "tile") && !WindowsNoticeTriggers.Relevant(3053, "badge")
            && !WindowsNoticeTriggers.Relevant(3053, null));
        check("Event log: other entries do not", !WindowsNoticeTriggers.Relevant(3153, null) && !WindowsNoticeTriggers.Relevant(2418, "toast")
            && !WindowsNoticeTriggers.Relevant(3049, null));
    }

    private static void Query(Action<string, bool> check)
    {
        const string sid = "S-1-5-21-2134607150-862690008-3709084658-1001";
        var query = WindowsNoticeTriggers.Query(sid);
        check("Event log: the query asks for the three entries", query.Contains("EventID=3052") && query.Contains("EventID=3055") && query.Contains("EventID=3053"));
        check("Event log: the query keeps to this user", query.Contains($"Security[@UserID='{sid}']"));
        check("Event log: submitted entries only for toasts", query.Contains("Data[@Name='NotificationType']='toast'"));
        var loose = WindowsNoticeTriggers.Query("S-1-5-21' or '1'='1");
        check("Event log: a user id that is not a SID is left out, not quoted in", !loose.Contains("UserID") && !loose.Contains("'1'='1"));
        check("Event log: no user id means no user filter", !WindowsNoticeTriggers.Query(null).Contains("UserID"));
    }

    private static void Parsing(Action<string, bool> check)
    {
        var delivered = WindowsNoticeTriggers.Parse(3052, null, App, 42u);
        check("Event log: a delivered toast keeps its id and app identity",
            delivered.Kind == WindowsNoticeLogEventKind.Notification && delivered.NotificationId == 42 && delivered.AppUserModelId == App);
        // The log's template declares TrackingId as win:Int32, so the real value arrives as a boxed int.
        var logged = WindowsNoticeTriggers.Parse(3052, null, App, (object)23617);
        check("Event log: the id as the log stores it (Int32) is read", logged.Kind == WindowsNoticeLogEventKind.Notification && logged.NotificationId == 23617);
        check("Event log: an Int32 past the signed range keeps the listener's unsigned id",
            WindowsNoticeTriggers.Parse(3052, null, App, (object)unchecked((int)3_000_000_000u)).NotificationId == 3_000_000_000u);
        var submitted = WindowsNoticeTriggers.Parse(3053, "Toast", App, "43");
        check("Event log: an older submitted toast accepts a numeric id", submitted.Kind == WindowsNoticeLogEventKind.Notification && submitted.NotificationId == 43);
        check("Event log: a tile submission is ignored", WindowsNoticeTriggers.Parse(3053, "tile", App, 44u).Kind == WindowsNoticeLogEventKind.Ignored);
        var noId = WindowsNoticeTriggers.Parse(3052, null, App, "not-an-id");
        check("Event log: a missing id requests immediate reconciliation", noId.Kind == WindowsNoticeLogEventKind.Reconcile && noId.ImmediateReconcile);
        check("Event log: a zero id requests immediate reconciliation", WindowsNoticeTriggers.Parse(3052, null, App, 0).ImmediateReconcile);
        check("Event log: a missing app identity requests reconciliation", WindowsNoticeTriggers.Parse(3052, null, null, 42u).ImmediateReconcile);
        var cleared = WindowsNoticeTriggers.Parse(3055, null, null, null);
        check("Event log: a clear requests bounded reconciliation", cleared.Kind == WindowsNoticeLogEventKind.Reconcile && !cleared.ImmediateReconcile);
    }

    private static void Batching(Action<string, bool> check)
    {
        var batch = new WindowsNoticeLogBatch();
        var entry = WindowsNoticeTriggers.Parse(3052, null, App, 52u);
        batch.Add(entry); batch.Add(entry);
        check("Event log: duplicate ids coalesce into one targeted read",
            batch.Snapshot().Ids == 1 && batch.TryNext(out uint only, out _) && only == 52);
        check("Event log: a delivered id alone does not schedule a list read",
            !WindowsNoticeLogBatch.ReconcileDue(batch.Snapshot(), At(100), At(0), TimeSpan.FromSeconds(5)));

        var burst = new WindowsNoticeLogBatch();
        foreach (uint id in new uint[] { 103, 101, 102 }) burst.Add(WindowsNoticeTriggers.Parse(3052, null, App, id));
        var order = new List<uint>();
        while (burst.TryNext(out uint next, out _)) { order.Add(next); burst.CompleteIds(next); }
        check("Event log: a burst delivered out of order is taken oldest id first", order.SequenceEqual([101u, 102u, 103u]));

        var conflict = new WindowsNoticeLogBatch();
        conflict.Add(entry);
        conflict.Add(WindowsNoticeTriggers.Parse(3052, null, "Different.App", 52u));
        conflict.Add(entry);
        check("Event log: conflicting app identities never choose one notification",
            conflict.Snapshot() is { Ids: 0, ImmediateReconcile: true } && !conflict.TryNext(out _, out _));

        var full = new WindowsNoticeLogBatch();
        for (uint id = 1; id <= WindowsNoticeLogBatch.MaximumIds + 10; id++) full.Add(WindowsNoticeTriggers.Parse(3052, null, App, id));
        check("Event log: a huge batch keeps a bounded number of ids and reconciles the rest",
            full.Snapshot() is { Ids: WindowsNoticeLogBatch.MaximumIds, ImmediateReconcile: true });

        var clears = new WindowsNoticeLogBatch();
        for (int i = 0; i < 50; i++) clears.Add(WindowsNoticeLogEvent.Reconcile);
        var clearSnapshot = clears.Snapshot();
        check("Event log: a clear burst is one throttled reconciliation request",
            clearSnapshot.Reconcile && !clearSnapshot.ImmediateReconcile
            && !WindowsNoticeLogBatch.ReconcileDue(clearSnapshot, At(29), At(0), WindowsNoticeLoop.ReconcileInterval)
            && WindowsNoticeLogBatch.ReconcileDue(clearSnapshot, At(30), At(0), WindowsNoticeLoop.ReconcileInterval));
        var beforeRead = clears.Snapshot();
        clears.Add(WindowsNoticeLogEvent.Reconcile);
        clears.CompleteReconcile(beforeRead.ReconcileVersion);
        check("Event log: a clear arriving during a list read remains pending", clears.Snapshot().Reconcile);

        var failed = new WindowsNoticeLogBatch();
        failed.RequestReconcile(immediate: true);
        check("Event log: failed reads bypass the clear throttle but remain bounded",
            !WindowsNoticeLogBatch.ReconcileDue(failed.Snapshot(), At(0), At(0), WindowsNoticeLoop.ReconcileInterval)
            && WindowsNoticeLogBatch.ReconcileDue(failed.Snapshot(), At(1), At(0), WindowsNoticeLoop.ReconcileInterval));
    }

    private static void Reading(Action<string, bool> check)
    {
        int lookups = 0;
        WindowsNoticeNotificationRead Found(uint id) { lookups++; return new(true, App, Input(id)); }
        var direct = WindowsNoticeLogReader.Read(52, App, known: false, Found);
        check("Event log: an event reads only the matching notification", lookups == 1 && direct.Observed && direct.Input?.InstanceKey == Input(52).InstanceKey && !direct.Reconcile);
        var duplicate = WindowsNoticeLogReader.Read(52, App, known: true, Found);
        check("Event log: an already-known id is not read or emitted twice", lookups == 1 && !duplicate.Observed && !duplicate.Reconcile);
        var missing = WindowsNoticeLogReader.Read(52, App, false, _ => WindowsNoticeNotificationRead.Missing);
        check("Event log: an unavailable single item requests throttled reconciliation", !missing.Observed && missing.Reconcile && !missing.ImmediateReconcile);
        var failed = WindowsNoticeLogReader.Read(52, App, false, _ => throw new InvalidOperationException("single read failed"));
        check("Event log: a failed lookup falls back to an immediate full read", !failed.Observed && failed.ImmediateReconcile);
        var mismatch = WindowsNoticeLogReader.Read(52, App, false, id => new(true, "Another.App", Input(id, "Another.App")));
        check("Event log: an id from another app is not delivered", !mismatch.Observed && mismatch.ImmediateReconcile);
        bool passedOn = false;
        try { WindowsNoticeLogReader.Read(52, App, false, _ => throw new TimeoutException("hung")); }
        catch (TimeoutException) { passedOn = true; }
        check("Event log: a lookup Windows did not answer is passed on as a failure", passedOn);
    }

    // WindowsNoticeList on its own: what Observe and Compare hand over around baselines and replacements.
    private static void ListObservations(Action<string, bool> check)
    {
        // The baseline's snapshot was taken before the toast arrived, or after.
        var start = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        long started = start.BeginRead();
        var live = start.Observe(50, Input(50));
        var staleBaseline = start.Compare([10u], id => Input(id), started);
        var after = start.Compare([10u, 50u], _ => null);
        var seen = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        long seenStarted = seen.BeginRead();
        var seenLive = seen.Observe(50, Input(50));
        var currentBaseline = seen.Compare([10u, 50u], id => Input(id), seenStarted);
        check("Event log: a notice logged during the startup baseline is shown at once and not again by the baseline",
            live.Arrived.SingleOrDefault()?.InstanceKey == Input(50).InstanceKey && start.Contains(50)
            && staleBaseline.Arrived.Count + staleBaseline.Missed.Count + staleBaseline.Removed.Count == 0
            && after.Arrived.Count + after.Missed.Count + after.Removed.Count == 0
            && seenLive.Arrived.Count == 1 && currentBaseline.Arrived.Count + currentBaseline.Missed.Count == 0);
        check("Event log: notices already listed at startup are not replayed",
            start.Contains(10) && seen.Contains(10) && staleBaseline.Arrived.Count == 0 && currentBaseline.Arrived.Count == 0);

        var resumed = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        resumed.Compare([1u], id => Input(id));
        resumed.ExpectBaseline(WindowsNoticeBaseline.Resume);
        var beforeRead = resumed.Observe(2, Input(2));
        long resumeRead = resumed.BeginRead();
        var afterStart = resumed.Observe(3, Input(3));
        var resumeSync = resumed.Compare([1u, 2u, 3u, 4u], id => Input(id), resumeRead);
        check("Event log: after waking, a notice logged before the reading started is missed, one after it is live",
            beforeRead.Missed.Count == 1 && beforeRead.Arrived.Count == 0 && afterStart.Arrived.Count == 1 && afterStart.Missed.Count == 0);
        check("Event log: the resume reading hands over only what it found itself",
            resumeSync.Missed.Select(input => input.InstanceKey).SequenceEqual([Input(4).InstanceKey]) && resumeSync.Arrived.Count == 0);

        var replacement = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        var old = Input(20, title: "same title");
        replacement.Compare([20u], _ => old);
        var newInput = Input(21, title: "same title");
        var immediate = replacement.Observe(21, newInput);
        var sync = replacement.Compare([21u], _ => newInput);
        check("Event log: a later full read pairs a direct arrival with the removed notice and marks it a redelivery",
            immediate.Arrived.Count == 1 && !immediate.Arrived[0].Redelivery
            && sync.Arrived.SingleOrDefault() is { Redelivery: true } paired && paired.Replaces == old.InstanceKey && sync.Removed.Count == 0);

        var replacedDuringRead = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        var oldBeforeRead = Input(55, title: "chat"); var arrivedDuringRead = Input(56, title: "chat");
        replacedDuringRead.Compare([55u], _ => oldBeforeRead);
        long emptyReadStarted = replacedDuringRead.BeginRead();
        replacedDuringRead.Observe(56, arrivedDuringRead);
        var staleEmpty = replacedDuringRead.Compare([], _ => null, emptyReadStarted);
        bool keptNew = replacedDuringRead.Contains(56);
        var nextEmpty = replacedDuringRead.Compare([], _ => null);
        check("Event log: a stale empty snapshot can pair the removed predecessor without falsely removing its new replacement",
            keptNew && staleEmpty.Arrived.SingleOrDefault()?.Replaces == oldBeforeRead.InstanceKey && staleEmpty.Removed.Count == 0
            && nextEmpty.Arrived.Count == 0 && nextEmpty.Removed.SequenceEqual([arrivedDuringRead.InstanceKey]));

        var single = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        single.Compare([], _ => null);
        single.Observe(60, Input(60));
        var vanished = single.Compare([], _ => null);
        check("Event log: a vanished direct notice is removed rather than replacing itself",
            vanished.Arrived.Count == 0 && vanished.Removed.SequenceEqual([Input(60).InstanceKey]));

        var chain = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        var hub = new NoticeHub();
        var a = Input(70, title: "chat"); var b = Input(71, title: "chat"); var c = Input(72, title: "chat");
        chain.Compare([70u], _ => a);
        hub.Receive(a, At(0));
        foreach (var (id, input) in new[] { (71u, b), (72u, c) })
            foreach (var arrived in chain.Observe(id, input).Arrived) hub.Receive(arrived, At(1));
        var final = chain.Compare([], _ => null);
        foreach (var arrived in final.Arrived) hub.Receive(arrived, At(2));
        foreach (var removed in final.Removed) hub.SourceRemoved(removed);
        check("Event log: vanished replacement chains merge only backwards and finish with absent source",
            final.Arrived.Select(input => input.Replaces).SequenceEqual([a.InstanceKey, b.InstanceKey])
            && hub.Notices is { Count: 1 } && hub.Notices[0].InstanceKey == c.InstanceKey && !hub.Notices[0].SourcePresent);

        var earlier = Input(80, title: "chat"); var later = Input(81, title: "chat");
        var noOld = NoticeReplacements.Pair([earlier, later], [new(earlier.InstanceKey!, earlier.AppKey, earlier.Title), new(later.InstanceKey!, later.AppKey, later.Title)]);
        check("Event log: the first direct arrival never replaces a future arrival",
            noOld.Added[0].Replaces is null && noOld.Added[1].Replaces == earlier.InstanceKey && noOld.Removed.SequenceEqual([later.InstanceKey]));
    }

    // Starting: the baseline reading is still out when a toast is logged.
    private static void LoopStartup(Action<string, bool> check)
    {
        var (loop, run, host) = Started(10, 11);
        check("Loop: starting takes the listener, subscribes and starts one baseline reading",
            host.Listening && host.LogOpen && host.ListReads == 1 && run.Reading && host.States.LastOrDefault() == WindowsNoticeLoopState.EventLog);

        host.Listed[50] = Input(50);
        Log(loop, run, 50);
        loop.Step(run, host, default);
        check("Loop: a log id is read and handed over while the baseline reading is still out",
            host.Lookups == 1 && Keys(host.Arrived).SequenceEqual([Key(50)]) && host.ListReads == 1 && run.Reading);

        // The baseline snapshot was taken before the toast arrived: it must neither drop nor repeat it.
        host.Finish([10u, 11u]);
        loop.Step(run, host, default);
        check("Loop: the baseline shows nothing that was already there and keeps the toast it did not see",
            host.Arrived.Count == 1 && host.Removed.Count == 0 && host.Syncs.SequenceEqual([0]) && !run.Reading);
        for (int i = 0; i < 3; i++) loop.Step(run, host, default);
        check("Loop: a log entry never asks for a list reading of its own", host.ListReads == 1);

        var (otherLoop, otherRun, other) = Started(10, 11);
        other.Listed[50] = Input(50);
        Log(otherLoop, otherRun, 50);
        otherLoop.Step(otherRun, other, default);
        other.Finish([10u, 11u, 50u]);
        otherLoop.Step(otherRun, other, default);
        check("Loop: a baseline that already lists the logged toast does not show it again",
            Keys(other.Arrived).SequenceEqual([Key(50)]) && other.Missed.Count == 0 && other.ListReads == 1);
    }

    private static void LoopSteadyState(Action<string, bool> check)
    {
        var (loop, run, host) = Settled(10, 11);
        int reads = host.ListReads;
        // Different senders (titles), so a cleared one is not taken as replaced by a later one (NoticeReplacements).
        foreach (uint id in new uint[] { 103, 101, 102 }) host.Listed[id] = Input(id, title: "sender " + id);
        foreach (uint id in new uint[] { 103, 101, 102 }) WindowsNoticeLoop.Arrive(run, WindowsNoticeTriggers.Parse(3052, null, App, id));
        loop.Fired(run);
        int wakes = host.Wakes;
        for (int i = 0; i < 3; i++) loop.Step(run, host, default);
        check("Loop: a burst is handed over one per turn, oldest first, each turn asking for the next",
            Keys(host.Arrived).SequenceEqual([Key(101), Key(102), Key(103)]) && host.Wakes - wakes >= 2);
        check("Loop: delivered notices read no list", host.ListReads == reads && host.Lookups == 3);

        // QQ clears its previous toast with every message: the clears wait for ReconcileInterval after the last full reading.
        var settledAt = host.Now;
        host.Listed.Remove(101);
        WindowsNoticeLoop.Arrive(run, WindowsNoticeLogEvent.Reconcile);
        host.Listed[104] = Input(104, title: "sender 104");
        Log(loop, run, 104);
        host.Now = settledAt.AddSeconds(5);
        loop.Step(run, host, default);
        host.Now = settledAt + WindowsNoticeLoop.ReconcileInterval - TimeSpan.FromSeconds(1);
        loop.Step(run, host, default);
        check("Loop: a clear does not pull messages back into a full reading at once",
            host.ListReads == reads && Keys(host.Arrived).Last() == Key(104));
        host.Now = settledAt + WindowsNoticeLoop.ReconcileInterval;
        loop.Step(run, host, default);
        check("Loop: a clear is reconciled once the interval has passed", host.ListReads == reads + 1);
        host.Finish();
        loop.Step(run, host, default);
        check("Loop: the reconciliation reports the cleared notice as removed",
            host.Removed.SequenceEqual([Key(101)]) && !WindowsNoticeLogBatch.ReconcileDue(run.Batch.Snapshot(), host.Now.AddHours(1), host.Now, TimeSpan.Zero));

        host.Listed[105] = Input(105);
        host.Access = WindowsNoticeAccess.Denied;
        Log(loop, run, 105);
        loop.Step(run, host, default);
        check("Loop: access revoked before a lookup stops the run and forgets the list",
            host.States.Last() == WindowsNoticeLoopState.Denied && !host.Listening && !host.LogOpen && host.Lookups == 4
            && Keys(host.Arrived).Last() == Key(104));
    }

    private static void LoopResume(Action<string, bool> check)
    {
        var (loop, run, host) = Settled(10);
        int reads = host.ListReads;
        loop.Refresh(WindowsNoticeBaseline.Resume, host.Now);
        host.Listed[60] = Input(60);
        Log(loop, run, 60);
        loop.Step(run, host, default);
        check("Loop: after waking, a toast logged before the resume reading starts is recorded as missed",
            Keys(host.Missed).SequenceEqual([Key(60)]) && host.Arrived.Count == 0 && host.ListReads == reads + 1 && run.Reading);
        host.Listed[61] = Input(61);
        Log(loop, run, 61);
        loop.Step(run, host, default);
        check("Loop: one logged while the resume reading is out is live at once", Keys(host.Arrived).SequenceEqual([Key(61)]));
        host.Listed[12] = Input(12);
        host.Finish([10u, 12u, 60u]);
        loop.Step(run, host, default);
        check("Loop: the resume reading hands over what arrived asleep, without repeating the logged ones",
            Keys(host.Missed).SequenceEqual([Key(60), Key(12)]) && host.Arrived.Count == 1 && host.Removed.Count == 0);
    }

    private static void LoopReplacement(Action<string, bool> check)
    {
        var (loop, run, host) = Settled();
        var hub = new NoticeHub();
        host.Listed[20] = Input(20, title: "same title");
        Log(loop, run, 20);
        loop.Step(run, host, default);
        Pump(host, hub, At(0));
        hub.MarkAllViewed();

        loop.Refresh(WindowsNoticeBaseline.None, host.Now);
        loop.Step(run, host, default);
        int reads = host.ListReads;
        host.Listed.Remove(20);
        host.Listed[21] = Input(21, title: "same title");
        WindowsNoticeLoop.Arrive(run, WindowsNoticeLogEvent.Reconcile);
        Log(loop, run, 21);
        loop.Step(run, host, default);
        Pump(host, hub, At(1));
        check("Loop: the replacing toast is shown at once while a full reading is out",
            run.Reading && hub.Notices.Count == 2 && hub.Notices[0].InstanceKey == Key(21) && hub.UnviewedCount == 1);

        host.Finish([20u]);
        loop.Step(run, host, default);
        Pump(host, hub, At(2));
        check("Loop: the older snapshot neither removes nor pairs the toast it did not see",
            hub.Notices.Count == 2 && host.Removed.Count == 0 && host.ListReads == reads);

        host.Now += WindowsNoticeLoop.ReconcileInterval;
        loop.Step(run, host, default);
        host.Finish();
        loop.Step(run, host, default);
        Pump(host, hub, At(3));
        check("Loop: the next reading merges the replaced row into the delivered one without showing it again",
            host.ListReads == reads + 1 && hub.Notices.Count == 1 && hub.Notices[0].InstanceKey == Key(21) && hub.UnviewedCount == 1
            && host.Shown == 2);
    }

    private static void LoopFailures(Action<string, bool> check)
    {
        // The log breaks while a full reading is out: it is subscribed to again at once, not at the next timed reading.
        var (loop, run, host) = Settled(10);
        loop.Refresh(WindowsNoticeBaseline.None, host.Now);
        loop.Step(run, host, default);
        int opens = host.LogOpens, reads = host.ListReads;
        loop.LogBroke(run);
        loop.Step(run, host, default);
        check("Loop: a broken log is subscribed to again while a full reading is out",
            host.LogOpens == opens + 1 && host.LogOpen && host.States.Last() == WindowsNoticeLoopState.EventLog && host.ListReads == reads);
        host.Finish();
        loop.Step(run, host, default);
        check("Loop: the reading the break asked for follows the one that was out", host.ListReads == reads + 1 && run.Reading);
        host.Finish();
        loop.Step(run, host, default);

        // Windows stops answering a lookup: the step fails, the id is left to a full reading with a new listener.
        host.Listed[70] = Input(70);
        host.Lookup = _ => throw new TimeoutException("hung");
        Log(loop, run, 70);
        bool failed = false;
        try { loop.Step(run, host, default); }
        catch (TimeoutException) { failed = true; loop.Failed(run, host); }
        check("Loop: a lookup Windows does not answer fails the step and lets go of the listener",
            failed && !host.Listening && !host.LogOpen && run.Batch.Snapshot() is { Ids: 0, ImmediateReconcile: true });
        host.Lookup = null;
        int listeners = host.ListenerOpens;
        reads = host.ListReads;
        loop.Step(run, host, default);
        host.Finish();
        loop.Step(run, host, default);
        check("Loop: the next step reads the whole list with a new listener and hands the notice over",
            host.ListenerOpens == listeners + 1 && host.ListReads == reads + 1 && Keys(host.Arrived).Last() == Key(70));

        // A reading made stale by a Refresh fails: its failure is dropped and a new reading starts.
        loop.Refresh(WindowsNoticeBaseline.None, host.Now);
        loop.Step(run, host, default);
        reads = host.ListReads;
        loop.Refresh(WindowsNoticeBaseline.None, host.Now);
        loop.Step(run, host, default);
        bool single = host.ListReads == reads;
        host.Fail(new InvalidOperationException("stale read failed"));
        bool threw = false;
        try { loop.Step(run, host, default); } catch (InvalidOperationException) { threw = true; }
        check("Loop: one full reading at a time, and a stale one's failure is dropped for a new reading",
            single && !threw && host.ListReads == reads + 1);
        host.Finish();
        loop.Step(run, host, default);

        reads = host.ListReads;
        loop.Refresh(WindowsNoticeBaseline.None, host.Now);
        loop.Step(run, host, default);
        host.Fail(new InvalidOperationException("current read failed"));
        threw = false;
        try { loop.Step(run, host, default); } catch (InvalidOperationException) { threw = true; }
        check("Loop: a current reading's failure fails the step", threw && host.ListReads == reads + 1);
    }

    private static void CompletionAndRefresh(Action<string, bool> check)
    {
        var gate = new object();
        var list = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        list.Compare([1u], _ => Input(1));
        int generation = 0;
        var snapshot = new WindowsNoticeReadSnapshot([1u, 2u], new Dictionary<uint, NoticeInput?> { [2u] = Input(2) });
        var completion = new WindowsNoticeReadCompletion<WindowsNoticeReadSnapshot>(snapshot, new(list.BeginRead(), 0, 0, 0), null);
        lock (gate) { generation++; list.ExpectBaseline(WindowsNoticeBaseline.Resume); }
        bool staleApplied = completion.TryApply(gate, () => generation == 0,
            value => list.Compare(value.NotificationIds, id => value.Inputs.GetValueOrDefault(id), completion.Stamp.ObservationGeneration));
        check("Event log: Refresh after taking a full result prevents that result consuming the new baseline",
            !staleApplied && list.Baseline == WindowsNoticeBaseline.Resume);

        var fresh = list.Compare(snapshot.NotificationIds, id => snapshot.Inputs.GetValueOrDefault(id));
        check("Event log: the valid resume read still hands sleep-time notices over as missed", fresh.Arrived.Count == 0 && fresh.Missed.Count == 1);

        // Coordination timeouts only guard against a deadlock; they do not measure speed.
        using var validating = new ManualResetEventSlim();
        using var refreshWaiting = new ManualResetEventSlim();
        bool refreshed = false;
        var apply = Task.Run(() => completion.TryApply(gate, () =>
        {
            validating.Set();
            if (!refreshWaiting.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Refresh did not reach the coordination point.");
            return true;
        }, value => list.Compare(value.NotificationIds, id => value.Inputs.GetValueOrDefault(id))));
        var refresh = Task.Run(() =>
        {
            if (!validating.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Application did not reach validation.");
            // If validation ever runs outside the shared gate, force Refresh into that gap before comparison can continue.
            if (Monitor.TryEnter(gate))
            {
                try { list.ExpectBaseline(WindowsNoticeBaseline.Resume); refreshed = true; }
                finally { Monitor.Exit(gate); }
                refreshWaiting.Set();
            }
            else
            {
                refreshWaiting.Set();
                lock (gate) { list.ExpectBaseline(WindowsNoticeBaseline.Resume); refreshed = true; }
            }
        });
        Task.WhenAll(apply, refresh).GetAwaiter().GetResult();
        check("Event log: validation and comparison cannot be separated by a concurrent Refresh",
            apply.Result && refreshed && list.Baseline == WindowsNoticeBaseline.Resume);

        var scheduler = new WindowsNoticeReadScheduler<WindowsNoticeReadSnapshot>();
        var old = new TaskCompletionSource<WindowsNoticeReadSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Start(new(0, 0, 0, 0), _ => old.Task, CancellationToken.None);
        bool reserved = scheduler.Start(new(1, 1, 0, 0), _ => Task.FromResult(snapshot), CancellationToken.None) is null;
        old.SetException(new InvalidOperationException("stale read failed"));
        bool taken = scheduler.TryTakeCompleted(out var failed);
        bool failedApplied = failed.TryApply(gate, () => true, _ => throw new InvalidOperationException("a failed result was applied"));
        check("Event log: a failed read holds its slot until completion and is never applied",
            reserved && taken && failed.Error is InvalidOperationException && failed.Stamp.ResultGeneration == 0 && !failedApplied && !scheduler.IsRunning);
    }

    private static void Coalescing(Action<string, bool> check)
    {
        var coalescer = new WindowsNoticeCoalescer();
        bool first = coalescer.Arrive();
        int more = Enumerable.Range(0, 9).Count(_ => coalescer.Arrive());
        check("Event log: the first entry of a batch schedules a wake", first);
        check("Event log: the rest of the batch schedules nothing", more == 0);
        coalescer.Fired();
        check("Event log: an entry after the wake fired schedules another", coalescer.Arrive());

        // Entries from many threads at once still schedule exactly one wake.
        var parallel = new WindowsNoticeCoalescer();
        int scheduled = 0;
        Parallel.For(0, 1000, _ => { if (parallel.Arrive()) Interlocked.Increment(ref scheduled); });
        check("Event log: concurrent entries schedule one wake", scheduled == 1);
        check("Event log: coalescing waits briefly", WindowsNoticeTriggers.Coalesce >= TimeSpan.FromMilliseconds(50) && WindowsNoticeTriggers.Coalesce <= TimeSpan.FromMilliseconds(150));
    }

    private static void Choice(Action<string, bool> check)
    {
        check("Event log: a log that is turned on is used", WindowsNoticeTriggers.Choose(logEnabled: true, mayTry: true) == WindowsNoticeTrigger.EventLog);
        check("Event log: a disabled log falls back to polling", WindowsNoticeTriggers.Choose(false, true) == WindowsNoticeTrigger.Polling);
        check("Event log: a log waiting after failures is not tried", WindowsNoticeTriggers.Choose(true, false) == WindowsNoticeTrigger.Polling);

        // Failures injected one after another: the first few are tried again at once, then the wait doubles up to its limit.
        var t = new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
        var retry = new WindowsNoticeLogRetry();
        for (int i = 1; i < WindowsNoticeTriggers.MostFailures; i++) retry.Failed(t);
        check("Event log: the first failures are tried again at once", retry.MayTry(t));
        retry.Failed(t);
        check("Event log: after a few failures the source polls and waits", !retry.MayTry(t) && !retry.MayTry(t.AddSeconds(59)) && retry.MayTry(t.AddMinutes(1)));
        retry.Failed(t.AddMinutes(1));
        check("Event log: each further failure waits twice as long", !retry.MayTry(t.AddMinutes(2.9)) && retry.MayTry(t.AddMinutes(3)));
        for (int i = 0; i < 20; i++) retry.Failed(t);
        check("Event log: the wait stops growing at its limit", WindowsNoticeLogRetry.RetryAfter(100) == WindowsNoticeLogRetry.MostWait && retry.MayTry(t + WindowsNoticeLogRetry.MostWait));
        // A subscription that worked for a while starts the count over when it breaks.
        var lasting = new WindowsNoticeLogRetry();
        for (int i = 0; i < WindowsNoticeTriggers.MostFailures; i++) lasting.Failed(t);
        lasting.Started(t.AddMinutes(1));
        lasting.Failed(t.AddMinutes(1) + WindowsNoticeLogRetry.LastsFor);
        check("Event log: a break after working a while is tried again at once", lasting.Failures == 1 && lasting.MayTry(t.AddMinutes(1) + WindowsNoticeLogRetry.LastsFor));
        var brief = new WindowsNoticeLogRetry();
        for (int i = 0; i < WindowsNoticeTriggers.MostFailures; i++) brief.Failed(t);
        brief.Started(t.AddMinutes(1));
        brief.Failed(t.AddMinutes(2));
        check("Event log: a subscription that breaks again soon keeps waiting longer", brief.Failures == WindowsNoticeTriggers.MostFailures + 1 && !brief.MayTry(t.AddMinutes(3)));
    }

    private static void Pace(Action<string, bool> check)
    {
        TimeSpan quiet = TimeSpan.FromSeconds(30), safety = TimeSpan.FromSeconds(60), polling = TimeSpan.FromSeconds(3);
        check("Event log: with the log only the safety reading runs on a timer", WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.EventLog, false, quiet, safety, polling) == safety);
        check("Event log: polling keeps its own pace", WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.Polling, false, quiet, safety, polling) == polling);
        check("Event log: quiet wins for every trigger", WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.EventLog, true, quiet, safety, polling) == quiet
            && WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.Polling, true, quiet, safety, polling) == quiet);
    }

    // A loop that has taken its first step: the baseline reading of these ids is out.
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

    // A loop whose baseline reading of these ids has been applied.
    private static (WindowsNoticeLoop Loop, WindowsNoticeRun Run, FakeHost Host) Settled(params uint[] listed)
    {
        var (loop, run, host) = Started(listed);
        host.Finish();
        loop.Step(run, host, default);
        return (loop, run, host);
    }

    // The log names a delivered toast and its coalesced wake fires.
    private static void Log(WindowsNoticeLoop loop, WindowsNoticeRun run, uint id)
    {
        WindowsNoticeLoop.Arrive(run, WindowsNoticeTriggers.Parse(3052, null, App, id));
        loop.Fired(run);
    }

    // Hands what the loop published to the hub, as NoticeCenter does.
    private static void Pump(FakeHost host, NoticeHub hub, DateTimeOffset now)
    {
        foreach (var input in host.Arrived.Skip(host.Pumped)) if (hub.Receive(input, now).Present) host.Shown++;
        host.Pumped = host.Arrived.Count;
        foreach (var key in host.Removed.Skip(host.PumpedRemoved)) hub.SourceRemoved(key);
        host.PumpedRemoved = host.Removed.Count;
    }

    private static IEnumerable<string> Keys(IEnumerable<NoticeInput> inputs) => inputs.Select(input => input.InstanceKey);
    private static string Key(uint id) => Input(id).InstanceKey;

    private static NoticeInput Input(uint id, string app = App, string title = "message")
        => new()
        {
            Origin = NoticeOrigin.Windows,
            AppKey = "windows:" + app,
            AppName = app,
            AppId = app,
            InstanceKey = $"windows:{app}:{id}",
            Title = title,
            Body = "body",
            CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(id)
        };

    private static DateTimeOffset At(int seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    // Windows as the loop sees it: Windows holds the listed notifications; a full reading stays out until Finish or Fail.
    private sealed class FakeHost : IWindowsNoticeHost
    {
        public readonly SortedDictionary<uint, NoticeInput> Listed = [];
        private readonly List<(TaskCompletionSource<WindowsNoticeReadSnapshot> Read, IReadOnlySet<uint> Known)> _reads = [];
        private int _wakes;
        public WindowsNoticeAccess Access = WindowsNoticeAccess.Allowed;
        public Func<uint, WindowsNoticeNotificationRead>? Lookup;
        public bool Listening, LogOpen;
        public int ListenerOpens, LogOpens, Lookups, ListReads, Pumped, PumpedRemoved, Shown;
        public readonly List<WindowsNoticeLoopState> States = [];
        public readonly List<NoticeInput> Arrived = [], Missed = [];
        public readonly List<string> Removed = [];
        public readonly List<int> Syncs = [];

        public DateTimeOffset Now { get; set; } = At(1_000_000);
        public DateTimeOffset LastInteraction => DateTimeOffset.MinValue;
        public int Wakes => Volatile.Read(ref _wakes);
        bool IWindowsNoticeHost.LogOpen => LogOpen;

        WindowsNoticeAccess IWindowsNoticeHost.Access() => Access;
        public void OpenListener() { Listening = true; ListenerOpens++; }
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

        // Completes the reading that is out with these ids (what Windows lists now by default).
        public void Finish(IEnumerable<uint>? ids = null)
        {
            var (read, known) = _reads[^1];
            _reads.RemoveAt(_reads.Count - 1);
            var listed = (ids ?? Listed.Keys).ToList();
            read.SetResult(new(listed, listed.Where(id => !known.Contains(id)).ToDictionary(id => id, id => (NoticeInput?)Listed.GetValueOrDefault(id))));
        }

        public void Fail(Exception error)
        {
            var (read, _) = _reads[^1];
            _reads.RemoveAt(_reads.Count - 1);
            read.SetException(error);
        }

        public bool LogEnabled() => true;
        public bool OpenLog() { LogOpens++; LogOpen = true; return true; }
        public void CloseLog() => LogOpen = false;
        public void Wake() => Interlocked.Increment(ref _wakes);
        public void Report(WindowsNoticeLoopState state) => States.Add(state);

        public void Publish(WindowsNoticeList.Changes changes)
        {
            Arrived.AddRange(changes.Arrived);
            Missed.AddRange(changes.Missed);
            Removed.AddRange(changes.Removed);
        }

        public void Synced(int covers) => Syncs.Add(covers);
    }
}
