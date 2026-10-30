using System.Runtime.ExceptionServices;

namespace BrimDeck.Core.Notices;

public enum WindowsNoticeAccess { Allowed, NeedsAccess, Denied }

// What a loop step reports: receiving by the push notification log or by polling, or stopped for want of access.
public enum WindowsNoticeLoopState { EventLog, Polling, NeedsAccess, Denied }

// What the loop needs from Windows for one run. WindowsNoticeSource implements it with UserNotificationListener and the push
// notification log (WindowsNoticeLog); the tests drive the loop with a fake one. Called from the loop's thread only, but
// for Now and LastInteraction.
public interface IWindowsNoticeHost
{
    DateTimeOffset Now { get; }
    // When the pointer was last on an island (WindowsNoticeSource.Interacted).
    DateTimeOffset LastInteraction { get; }
    WindowsNoticeAccess Access();
    // Takes Windows' listener for this run, and lets go of it.
    void OpenListener();
    void CloseListener();
    // The notification with this id. Gives up after a bounded wait with a TimeoutException (the loop then fails and starts
    // over, life-L5); any other exception falls back to a full reading.
    WindowsNoticeNotificationRead ReadOne(uint id);
    // The polling probe: whether one of these ids names a notification, or the lookup failed (either asks for a full reading).
    bool Probe(IEnumerable<uint> ids);
    // The whole list, with the notices of the ids not in known read too. Runs off the loop thread and gives up after a
    // bounded wait.
    Task<WindowsNoticeReadSnapshot> ReadAll(IReadOnlySet<uint> known, CancellationToken token);
    bool LogEnabled();
    bool LogOpen { get; }
    // Subscribes to the log; false when Windows refused.
    bool OpenLog();
    void CloseLog();
    // Asks this run's loop for another step soon.
    void Wake();
    void Report(WindowsNoticeLoopState state);
    void Publish(WindowsNoticeList.Changes changes);
    // A full reading has been applied; covers is the number of the last Refresh it covers.
    void Synced(int covers);
}

// One Start's state. Only its loop steps it; the log's thread adds entries (WindowsNoticeLoop.Arrive, LogBroke) and Refresh
// and Detach raise ResultGeneration.
public sealed class WindowsNoticeRun
{
    internal readonly WindowsNoticeLogBatch Batch = new();
    internal readonly WindowsNoticeCoalescer Coalescer = new();
    internal readonly WindowsNoticeReadScheduler<WindowsNoticeReadSnapshot> FullRead = new();
    internal readonly WindowsNoticeLogRetry LogRetry = new();
    // A full reading started before the last Refresh or Detach is stale: its result and its failure are dropped.
    internal long ResultGeneration;
    internal bool HasListener;
    // The log broke and is to be subscribed to again; kept until that is done, even while a full reading is under way.
    internal bool RetryLog;
    internal volatile bool LogBroken;
    internal DateTimeOffset LastFull = DateTimeOffset.MinValue;
    // Until when the probe looks a few ids ahead (WindowsNoticePace.ProbeIds).
    internal DateTimeOffset WideUntil = DateTimeOffset.MinValue;

    public bool Reading => FullRead.IsRunning;
}

// The decisions of WindowsNoticeSource's loop, apart from Windows itself (IWindowsNoticeHost). A push notification log entry
// names a toast by id and application; the loop reads that one notification at its next turn, whether or not a full reading
// is under way, and hands it over at once. The whole list is read, one reading at a time and off the loop thread, for the
// baseline after starting, being granted access or waking, every EventSafetyInterval, ReconcileInterval after a clear (only a
// full reading shows what went), and at once (1 s apart) when an entry or a lookup could not be trusted or the id it named
// could not be read yet, unless a clear is already waiting for its interval. When the log cannot be used the list is read on a
// timer: every ProbeInterval only the id after the newest one is looked up (WindowsNoticePace.ProbeIds), and the whole list is
// read every FullInterval and whenever the probe finds a new notice. Until a reading has confirmed the newest id, the list is
// read every 3 s at first and less often after a quiet stretch (WindowsNoticeList.UnknownWait). Access is checked before
// every reading.
public sealed class WindowsNoticeLoop
{
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2.5), FullInterval = TimeSpan.FromSeconds(15),
        EventSafetyInterval = TimeSpan.FromSeconds(60), QuietInterval = TimeSpan.FromSeconds(30);
    // A clear entry names no notification (it carries only the session id), and some applications (QQ) clear their previous
    // toast with every message they send. A full reading of a long list takes seconds, so clears are reconciled at most this
    // long after the last full reading ended: during a busy chat about one reading in seven instead of one every few seconds.
    public static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);
    private static readonly WindowsNoticeList.Changes None = new([], [], []);
    private readonly WindowsNoticeList _list = new(FullInterval);
    private WindowsNoticeRun? _current;
    private bool _dirty, _quiet;
    private int _refreshes;
    // E: the ids the Start baseline took in without showing them, until the next reading, Refresh or reset (under Gate). The log
    // names only toasts delivered after BrimDeck subscribed, so an entry that names one of these reaches the loop after the
    // baseline's snapshot, which was taken after the toast: it is a new toast and is shown, not skipped as already known.
    private HashSet<uint>? _absorbed;
    // When something last happened that makes a notice likely soon (WindowsNoticePace.Unconfirmed).
    private DateTimeOffset _lastActivity;

    public WindowsNoticeLoop(DateTimeOffset now) => _lastActivity = now;

    // Taken by the source for its own state too; the UI thread takes it for every Refresh and SetQuiet.
    public object Gate { get; } = new();

    // For tests, under Gate.
    internal WindowsNoticeList List => _list;

    public void Begin(WindowsNoticeRun run, DateTimeOffset now)
    {
        lock (Gate) { _current = run; _list.Reset(); _absorbed = null; _lastActivity = now; }
    }

    public void End(WindowsNoticeRun run)
    {
        lock (Gate) if (_current == run) _current = null;
    }

    public bool IsCurrent(WindowsNoticeRun run)
    {
        lock (Gate) return _current == run;
    }

    // Locked or paused: arriving notices are not shown, so the list is read rarely; it is read in full when that ends. True
    // when the loop should be woken.
    public bool SetQuiet(bool quiet, DateTimeOffset now)
    {
        lock (Gate) { if (_quiet == quiet) return false; _quiet = quiet; if (!quiet) { _dirty = true; _lastActivity = now; } }
        return true;
    }

    // Reads the list again soon; a full reading started before cannot be applied any more. Returns the request's number.
    public int Refresh(WindowsNoticeBaseline baseline, DateTimeOffset now)
    {
        lock (Gate)
        {
            _dirty = true; _list.ExpectBaseline(baseline); _lastActivity = now;
            if (baseline != WindowsNoticeBaseline.None) _absorbed = null;
            if (_current is { } run) Interlocked.Increment(ref run.ResultGeneration);
            return ++_refreshes;
        }
    }

    // An entry in the push notification log, on the log's thread: it only queues its id (or a reconciliation), it never asks
    // for a full reading itself. True when the caller should wake the loop after WindowsNoticeTriggers.Coalesce, so a batch
    // is taken at one turn (WindowsNoticeCoalescer).
    public static bool Arrive(WindowsNoticeRun run, WindowsNoticeLogEvent entry) => run.Batch.Add(entry) && run.Coalescer.Arrive();

    // The coalesced wake fired. True when the run is still current and its loop should be woken.
    public bool Fired(WindowsNoticeRun run)
    {
        run.Coalescer.Fired();
        return IsCurrent(run);
    }

    // The log stopped delivering: the loop lets go of the subscription and takes it again (Step), reading in full meanwhile.
    // True when the loop should be woken.
    public bool LogBroke(WindowsNoticeRun run)
    {
        run.LogBroken = true;
        lock (Gate) { if (_current != run) return false; _dirty = true; }
        return true;
    }

    // A step failed: the next starts over with a new listener and a full reading (life-L5).
    public void Failed(WindowsNoticeRun run, IWindowsNoticeHost host)
    {
        lock (Gate) _dirty = true;
        Detach(run, host);
    }

    public static void Detach(WindowsNoticeRun run, IWindowsNoticeHost host)
    {
        Interlocked.Increment(ref run.ResultGeneration);
        host.CloseLog();
        host.CloseListener();
        run.HasListener = false;
    }

    // One turn of the loop. True when a full reading was applied (the run's failures start over).
    public bool Step(WindowsNoticeRun run, IWindowsNoticeHost host, CancellationToken token)
    {
        var now = host.Now;
        // First a notification the log named, whether or not a full reading is under way: one per turn, the oldest first, so
        // a burst is shown in order and each is handed over as soon as it is read.
        if (run.HasListener && run.Batch.TryNext(out uint id, out string? app))
        {
            var access = host.Access();
            if (access != WindowsNoticeAccess.Allowed) { AccessLost(run, host, access); return false; }
            bool known, absorbed;
            lock (Gate) { if (_current != run) return false; absorbed = _absorbed?.Contains(id) == true; known = !absorbed && _list.Contains(id); }
            WindowsNoticeLogReadResult result;
            try { result = WindowsNoticeLogReader.Read(id, app, known, host.ReadOne); }
            catch (TimeoutException)
            {
                // Windows did not answer (seen after waking): the id is left to a full reading with a new listener.
                run.Batch.CompleteIds(id);
                run.Batch.RequestReconcile(immediate: true);
                throw;
            }
            WindowsNoticeList.Changes changes;
            lock (Gate)
            {
                if (_current != run) return false;
                // A baseline id is in the list already, so the list would take the entry for a repeat: the notice is handed over here.
                if (absorbed) { _absorbed?.Remove(id); changes = result is { Observed: true, Input: { } late } ? new([late], [], []) : None; }
                else changes = result.Apply(_list);
                if (changes.Arrived.Count + changes.Missed.Count > 0) _lastActivity = host.Now;
                if (_list.SkippedProbe) run.WideUntil = host.Now + WindowsNoticePace.WideFor;
            }
            run.Batch.CompleteIds(id);
            // Reconcile without ImmediateReconcile is the id Windows could not return (WindowsNoticeLogReader).
            if (result is { Reconcile: true, ImmediateReconcile: false }) run.Batch.RequestReconcileForMissing();
            else if (result.Reconcile) run.Batch.RequestReconcile(result.ImmediateReconcile);
            host.Publish(changes);
            if (run.Batch.HasIds) host.Wake();
        }

        bool synced = false;
        // A reading started before a Refresh or a new listener is dropped, its failure too: what made it stale asked for
        // another (Refresh, Failed).
        if (run.FullRead.TryTakeCompleted(out var completion) && completion.Stamp.ResultGeneration == Volatile.Read(ref run.ResultGeneration))
        {
            // Any failure, a reading that timed out included, is reported and the next step starts over (life-L5).
            if (completion.Error is { } error) ExceptionDispatchInfo.Throw(error);
            else
            {
                // A revoked permission makes the list read as empty; that must not look like every notice being removed.
                var access = host.Access();
                if (access != WindowsNoticeAccess.Allowed) { AccessLost(run, host, access); return false; }
                synced = Sync(completion, run, host);
                if (synced)
                {
                    run.LastFull = host.Now;
                    run.Batch.CompleteReconcile(completion.Stamp.ReconcileVersion);
                }
            }
        }

        bool full, quiet, confirmed;
        uint newest;
        TimeSpan unknownWait;
        DateTimeOffset active;
        lock (Gate)
        {
            full = _dirty || _list.Baseline != WindowsNoticeBaseline.None; quiet = _quiet;
            newest = _list.Newest; confirmed = _list.Confirmed; unknownWait = _list.UnknownWait;
            var touched = host.LastInteraction;
            active = touched > _lastActivity ? touched : _lastActivity;
        }
        // A broken log subscription is taken again at once (LogBroke asked for a full reading); one that keeps breaking leaves
        // the run polling, and is tried again now and then (WindowsNoticeLogRetry).
        if (run.LogBroken) { run.LogBroken = false; host.CloseLog(); run.LogRetry.Failed(now); run.RetryLog = full = true; }

        // One full reading at a time. The subscription is taken again now all the same; the full reading it needs follows
        // the one under way (LogBroke left the list to be read).
        if (run.FullRead.IsRunning)
        {
            if (run.RetryLog && run.HasListener)
            {
                run.RetryLog = false;
                StartLog(run, host, now);
                host.Report(host.LogOpen ? WindowsNoticeLoopState.EventLog : WindowsNoticeLoopState.Polling);
            }
            return synced;
        }

        var trigger = host.LogOpen ? WindowsNoticeTrigger.EventLog : WindowsNoticeTrigger.Polling;
        var interval = WindowsNoticeTriggers.Interval(trigger, quiet, QuietInterval, EventSafetyInterval,
            !confirmed ? WindowsNoticePace.Unconfirmed(now, active, unknownWait) : FullInterval);
        bool timed = false;
        if (!full && WindowsNoticeLogBatch.ReconcileDue(run.Batch.Snapshot(), now, run.LastFull, ReconcileInterval)) full = timed = true;
        if (!full && now - run.LastFull >= interval) full = timed = true;
        // The next id is taken by the next notice for this user, so finding it means something arrived. Without access the
        // lookup finds nothing, and the next full reading reports the change of access.
        if (!full && run.HasListener && trigger == WindowsNoticeTrigger.Polling && !quiet && newest > 0
            && host.Probe(WindowsNoticePace.ProbeIds(newest, now, run.WideUntil)))
            full = true;
        if (!full && run.HasListener) return synced;

        var status = host.Access();
        token.ThrowIfCancellationRequested();
        // A revoked permission makes the list read as empty; that must not look like every notice being removed.
        if (status != WindowsNoticeAccess.Allowed) { AccessLost(run, host, status); return synced; }
        if (!run.HasListener) { host.OpenListener(); run.HasListener = true; run.RetryLog = true; }
        // A log turned off since (by policy, say) keeps the subscription but delivers nothing: checked at each timed reading.
        if (timed && host.LogOpen && !host.LogEnabled()) host.CloseLog();
        // Subscribed before the reading, so a toast arriving during it is not missed. Polling: a timed reading tries the log
        // again once it may (turned on again, or its wait after failures is over).
        if (run.RetryLog || timed && !host.LogOpen) { run.RetryLog = false; StartLog(run, host, now); }
        host.Report(host.LogOpen ? WindowsNoticeLoopState.EventLog : WindowsNoticeLoopState.Polling);
        StartFullRead(run, host, token);
        return synced;
    }

    private void StartFullRead(WindowsNoticeRun run, IWindowsNoticeHost host, CancellationToken token)
    {
        WindowsNoticeReadStamp stamp;
        HashSet<uint> known;
        lock (Gate)
        {
            if (_current != run) return;
            _dirty = false;
            stamp = new(_list.BeginRead(), Volatile.Read(ref run.ResultGeneration), _refreshes, run.Batch.Snapshot().ReconcileVersion);
            known = _list.KnownIds;
        }
        var reading = run.FullRead.Start(stamp, ct => host.ReadAll(known, ct), token);
        // The loop takes the result at its next turn; it is woken for that.
        _ = reading?.ContinueWith(completed =>
        {
            if (completed.IsFaulted) _ = completed.Exception;
            if (IsCurrent(run)) host.Wake();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private bool Sync(WindowsNoticeReadCompletion<WindowsNoticeReadSnapshot> completion, WindowsNoticeRun run, IWindowsNoticeHost host)
    {
        var changes = None;
        bool applied = completion.TryApply(Gate,
            () => _current == run && completion.Stamp.ResultGeneration == Volatile.Read(ref run.ResultGeneration),
            snapshot =>
            {
                // A Start baseline takes in the ids it lists that were not known without showing them (E); any other reading
                // settles them.
                var taken = _list.Baseline == WindowsNoticeBaseline.Start ? _list.KnownIds : null;
                changes = _list.Compare(snapshot.NotificationIds,
                    id => snapshot.Inputs.TryGetValue(id, out var input) ? input : null, completion.Stamp.ObservationGeneration);
                _absorbed = taken is null ? null : snapshot.NotificationIds.Where(id => !taken.Contains(id)).ToHashSet();
                if (changes.Arrived.Count + changes.Missed.Count > 0) _lastActivity = host.Now;
                if (_list.SkippedProbe) run.WideUntil = host.Now + WindowsNoticePace.WideFor;
            });
        if (!applied) return false;
        host.Publish(changes);
        host.Synced(completion.Stamp.Covers);
        return true;
    }

    // Access was revoked: the list is forgotten (it reads as empty, which must not look like every notice being removed).
    private void AccessLost(WindowsNoticeRun run, IWindowsNoticeHost host, WindowsNoticeAccess access)
    {
        lock (Gate) { if (_current == run) { _list.Reset(); _absorbed = null; _dirty = false; } }
        // Ids the log named before access was lost would be read after it is granted again, under the Start baseline, and
        // shown as new notices (C).
        run.Batch.Reset();
        Detach(run, host);
        host.Report(access == WindowsNoticeAccess.NeedsAccess ? WindowsNoticeLoopState.NeedsAccess : WindowsNoticeLoopState.Denied);
    }

    private static void StartLog(WindowsNoticeRun run, IWindowsNoticeHost host, DateTimeOffset now)
    {
        bool mayTry = run.LogRetry.MayTry(now);
        if (WindowsNoticeTriggers.Choose(mayTry && host.LogEnabled(), mayTry) != WindowsNoticeTrigger.EventLog) return;
        if (host.OpenLog()) run.LogRetry.Started(now); else run.LogRetry.Failed(now);
    }
}
