using BrimDeck.Core.Notices;

// One notification Windows cannot describe must not fail the whole Windows notice source (group "Notice robust"). Seen on the
// deployed build: a toast sent by an unpackaged program under an unusual application id stays in Windows' notification list, its
// UserNotification.AppInfo answers E_NOTIMPL (NotImplementedException), and every reading of the list threw on it, so the source
// stayed Failed and no application's notices were received. Every case drives the real WindowsNoticeLoop with a fake Windows
// whose full reading is converted by WindowsNoticeSnapshot, as WindowsNoticeSource does; the "before" case converts the list
// the way the source did before (no guard around one notification) and shows the loop failing.
internal static class NoticeRobustTests
{
    private const string App = "Vendor.Chat";

    internal static void Run(Action<string, bool> check)
    {
        BadToastInBaseline(check);
        BadToastArrivesLive(check);
        BadToastBeforeTheFix(check);
        BadToastNamedByTheLogAfterBaseline(check);
        SnapshotBuilderLeavesOnlyTheBadOneOut(check);
    }

    // The unreadable toast is in Windows' list when BrimDeck starts: the baseline, the readings after it and the notices that
    // arrive meanwhile all go on, and the unreadable one is never shown.
    private static void BadToastInBaseline(Action<string, bool> check)
    {
        var (loop, run, host) = Started(10, 11, 12);
        host.Bad.Add(11);
        bool baselineApplied = Finished(loop, run, host, out var baselineError);
        check("Notice robust: a baseline listing a toast Windows cannot describe is applied without failing", baselineApplied && baselineError is null);
        check("Notice robust: the baseline shows nothing, the unreadable toast included", host.Arrived.Count == 0 && host.Missed.Count == 0);

        host.Listed[20] = Input(20);
        Log(loop, run, 20);
        loop.Step(run, host, default);
        check("Notice robust: a notice that arrives after that is shown", Keys(host.Arrived).SequenceEqual([Key(20)]));

        int failures = 0;
        for (int round = 0; round < 3; round++)
        {
            host.Now += TimeSpan.FromMinutes(2);
            loop.Refresh(WindowsNoticeBaseline.None, host.Now);
            if (!Finished(loop, run, host, out var error) || error is not null) failures++;
        }
        check("Notice robust: reading the list again and again with that toast in it never fails", failures == 0);
        check("Notice robust: the unreadable toast is read once and then counted as listed, not read on every reading", host.ReadCount(11) == 1);
        check("Notice robust: nothing is shown twice and the unreadable toast is never shown", Keys(host.Arrived).SequenceEqual([Key(20)]) && host.Removed.Count == 0);

        // It leaves Windows' list: no notice of it existed, so nothing is removed and nothing fails.
        host.Listed.Remove(11);
        host.Now += TimeSpan.FromMinutes(2);
        loop.Refresh(WindowsNoticeBaseline.None, host.Now);
        bool afterLeaving = Finished(loop, run, host, out var leavingError);
        check("Notice robust: the unreadable toast leaving the list is taken without failing", afterLeaving && leavingError is null && host.Removed.Count == 0);
    }

    // The unreadable toast arrives while BrimDeck runs: its log entry cannot be matched with an application, so the loop reads
    // the list in full; the notice that arrived next to it is still shown, once.
    private static void BadToastArrivesLive(Action<string, bool> check)
    {
        var (loop, run, host) = Settled(10, 12);
        host.Bad.Add(21);
        host.Listed[21] = Input(21);
        host.Listed[22] = Input(22);
        Log(loop, run, 21);
        loop.Step(run, host, default);
        check("Notice robust: a log entry for a toast Windows cannot describe shows nothing and asks for a full reading",
            host.Arrived.Count == 0 && host.Lookups == 1);
        Log(loop, run, 22);
        loop.Step(run, host, default);
        check("Notice robust: the notice that arrived next to it is shown from its own log entry", Keys(host.Arrived).SequenceEqual([Key(22)]));

        int failures = 0;
        for (int round = 0; round < 3; round++)
        {
            host.Now += TimeSpan.FromMinutes(2);
            if (!Finished(loop, run, host, out var error) || error is not null) failures++;
        }
        check("Notice robust: the full readings that follow succeed with the unreadable toast still in the list", failures == 0);
        check("Notice robust: the notice is not shown again by those readings and the unreadable toast is never shown",
            Keys(host.Arrived).SequenceEqual([Key(22)]) && host.Missed.Count == 0);
        check("Notice robust: the full readings did run (the loop did not just stop reading)", host.ListReads >= 2);
    }

    // The same list read the way WindowsNoticeSource did before: one notification that throws fails the whole reading, every
    // reading after it fails again, and the notice next to it is never received. The loop (not this test) is what turns that
    // into the source's Failed state: WindowsNoticeSource.LoopAsync catches what Step throws.
    private static void BadToastBeforeTheFix(Action<string, bool> check)
    {
        var (loop, run, host) = Started(10, 11, 12);
        host.Bad.Add(11);
        host.Guarded = false;
        bool applied = Finished(loop, run, host, out var error);
        check("Notice robust (before): one notification Windows cannot describe fails the whole reading with its exception",
            !applied && error is NotImplementedException);

        int failed = 0;
        for (int round = 0; round < 3; round++)
        {
            loop.Failed(run, host);
            host.Now += TimeSpan.FromMinutes(2);
            Finished(loop, run, host, out var again);
            if (again is NotImplementedException) failed++;
        }
        check("Notice robust (before): every reading after that fails on the same notification, so the source stays failed", failed == 3);
        check("Notice robust (before): and nothing is received meanwhile", host.Arrived.Count == 0);
    }

    // A baseline id that the log names later cannot be read (the lookup finds no application): it keeps its place for the full
    // reading, which cannot read it either; that must stay quiet and must not hold back the notices that follow.
    private static void BadToastNamedByTheLogAfterBaseline(Action<string, bool> check)
    {
        var (loop, run, host) = Started(10, 11);
        host.Bad.Add(11);
        bool baseline = Finished(loop, run, host, out var baselineError);
        Log(loop, run, 11);
        loop.Step(run, host, default);
        check("Notice robust: the log naming an unreadable baseline toast shows nothing and does not fail",
            baseline && baselineError is null && host.Arrived.Count == 0 && host.Lookups == 1);

        host.Listed[30] = Input(30);
        Log(loop, run, 30);
        loop.Step(run, host, default);
        check("Notice robust: a notice that arrives after that is shown", Keys(host.Arrived).SequenceEqual([Key(30)]));

        int failures = 0;
        for (int round = 0; round < 4; round++)
        {
            host.Now += TimeSpan.FromMinutes(2);
            if (!Finished(loop, run, host, out var error) || error is not null) failures++;
        }
        check("Notice robust: the readings that follow, each asked again for that toast, all succeed", failures == 0);
        check("Notice robust: and still only the readable notice was ever shown", Keys(host.Arrived).SequenceEqual([Key(30)]) && host.Missed.Count == 0);
    }

    // The conversion alone: only a notification that cannot be read is left out, the others keep their notices and place.
    private static void SnapshotBuilderLeavesOnlyTheBadOneOut(Action<string, bool> check)
    {
        var list = new List<(uint Id, string Kind)> { (1, "ok"), (2, "notimpl"), (3, "com"), (4, "id"), (5, "ok"), (1, "ok") };
        var snapshot = WindowsNoticeSnapshot.Build(list,
            item => item.Kind == "id" ? throw new System.Runtime.InteropServices.COMException("gone", unchecked((int)0x80004005)) : item.Id,
            (item, id) => item.Kind switch
            {
                "notimpl" => throw new NotImplementedException(),
                "com" => throw new System.Runtime.InteropServices.COMException("no", unchecked((int)0x80004005)),
                _ => Input(id)
            },
            new HashSet<uint> { 5 }, default);
        check("Notice robust: the snapshot lists every notification whose id can be read once, in order", snapshot.NotificationIds.SequenceEqual([1u, 2u, 3u, 5u]));
        check("Notice robust: the snapshot has notices for the readable unknown ones and none for the unreadable ones",
            snapshot.Inputs.Keys.OrderBy(id => id).SequenceEqual([1u, 2u, 3u]) && snapshot.Inputs[1u] is not null && snapshot.Inputs[2u] is null && snapshot.Inputs[3u] is null);

        bool cancelled = false;
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try { WindowsNoticeSnapshot.Build(list, item => item.Id, (_, id) => Input(id), new HashSet<uint>(), stop.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check("Notice robust: cancelling still ends the reading", cancelled);

        bool other = false;
        try { WindowsNoticeSnapshot.Build(list, item => item.Id, (_, _) => throw new TimeoutException("Windows did not answer"), new HashSet<uint>(), default); }
        catch (TimeoutException) { other = true; }
        check("Notice robust: a failure that is not about one notification (a timeout) still fails the reading", other);
    }

    // The loop's next turns after the reading was asked for: the reading is finished and applied. False when applying it threw
    // (the error is returned); the reading is taken from the fake Windows as it is listed now.
    private static bool Finished(WindowsNoticeLoop loop, WindowsNoticeRun run, FakeHost host, out Exception? error)
    {
        error = null;
        try
        {
            loop.Step(run, host, default);
            if (!host.Pending) return false;
            host.Finish();
            return loop.Step(run, host, default);
        }
        catch (Exception ex) { error = ex; return false; }
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

    private static IEnumerable<string> Keys(IEnumerable<NoticeInput> inputs) => inputs.Select(input => input.InstanceKey);
    private static string Key(uint id) => Input(id).InstanceKey;

    // Windows as the loop sees it: Windows holds the listed notifications; a full reading stays out until Finish, then it is
    // converted as WindowsNoticeSource does. The ids in Bad are the notifications whose UserNotification.AppInfo throws
    // NotImplementedException: converting one throws it (Guarded false: out of the conversion, as before the fix), and the lookup
    // of one finds the notification but no application, as the source's lookup does now.
    private sealed class FakeHost : IWindowsNoticeHost
    {
        public readonly SortedDictionary<uint, NoticeInput> Listed = [];
        public readonly HashSet<uint> Bad = [];
        public bool Guarded = true;
        private readonly Dictionary<uint, int> _reads = [];
        private readonly List<(TaskCompletionSource<WindowsNoticeReadSnapshot> Read, IReadOnlySet<uint> Known)> _pending = [];
        public bool Listening, LogOpen;
        public int Lookups, ListReads;
        public readonly List<NoticeInput> Arrived = [], Missed = [];
        public readonly List<string> Removed = [];

        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch.AddSeconds(1_000_000);
        public DateTimeOffset LastInteraction => DateTimeOffset.MinValue;
        bool IWindowsNoticeHost.LogOpen => LogOpen;
        public bool Pending => _pending.Count > 0;
        public int ReadCount(uint id) => _reads.GetValueOrDefault(id);

        WindowsNoticeAccess IWindowsNoticeHost.Access() => WindowsNoticeAccess.Allowed;
        public void OpenListener() => Listening = true;
        public void CloseListener() => Listening = false;

        public WindowsNoticeNotificationRead ReadOne(uint id)
        {
            Lookups++;
            if (!Listed.TryGetValue(id, out var input)) return WindowsNoticeNotificationRead.Missing;
            return Bad.Contains(id) ? new(true, null, null) : new(true, input.AppId, input);
        }

        public bool Probe(IEnumerable<uint> ids) => ids.Any(Listed.ContainsKey);

        public Task<WindowsNoticeReadSnapshot> ReadAll(IReadOnlySet<uint> known, CancellationToken token)
        {
            ListReads++;
            var read = new TaskCompletionSource<WindowsNoticeReadSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add((read, known));
            return read.Task;
        }

        // Windows answers the reading that is out, with the notifications as listed now.
        public void Finish()
        {
            var (read, known) = _pending[^1];
            _pending.RemoveAt(_pending.Count - 1);
            var notifications = Listed.Keys.ToList();
            try
            {
                NoticeInput? Read(uint id, uint _)
                {
                    _reads[id] = _reads.GetValueOrDefault(id) + 1;
                    if (Bad.Contains(id)) throw new NotImplementedException("Not implemented (0x80004001)");
                    return Listed[id];
                }
                if (Guarded) read.SetResult(WindowsNoticeSnapshot.Build(notifications, id => id, Read, known, default));
                else
                {
                    // WindowsNoticeSource.ReadSnapshotAsync before the fix: a notification that throws fails the reading.
                    var inputs = new Dictionary<uint, NoticeInput?>();
                    foreach (uint id in notifications)
                        if (!known.Contains(id)) inputs[id] = Read(id, id);
                    read.SetResult(new(notifications, inputs));
                }
            }
            catch (Exception ex) { read.SetException(ex); }
        }

        public bool LogEnabled() => true;
        public bool OpenLog() { LogOpen = true; return true; }
        public void CloseLog() => LogOpen = false;
        public void Wake() { }
        public void Report(WindowsNoticeLoopState state) { }

        public void Publish(WindowsNoticeList.Changes changes)
        {
            Arrived.AddRange(changes.Arrived);
            Missed.AddRange(changes.Missed);
            Removed.AddRange(changes.Removed);
        }

        public void Synced(int covers) { }
    }
}
