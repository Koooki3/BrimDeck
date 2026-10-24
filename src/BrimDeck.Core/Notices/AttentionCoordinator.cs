namespace BrimDeck.Core.Notices;

// What may briefly take the collapsed island: an arriving notice, a summary of notices that arrived while BrimDeck kept
// quiet, a quota alert, or a track change.
public enum AttentionKind { Notice, Summary, Quota, Track }

public sealed class AttentionItem
{
    public required string Lane { get; init; }
    public required AttentionKind Kind { get; init; }
    // The same key names the same item: submitting it again updates it in place.
    public required string Key { get; set; }
    // Notices of one application share a group, so that a burst from it is shown as one notice with a count.
    public string Group { get; init; } = "";
    public NoticePriority Priority { get; init; } = NoticePriority.Normal;
    public TimeSpan Duration { get; init; }
    public object? Payload { get; set; }
    // How many notices this one stands for: 1, or more after a burst from one application merged into it.
    public int Count { get; set; } = 1;
    // An update that should keep the item on screen a little longer, such as a task's progress.
    public bool Refresh { get; init; }
    // An alarm, a countdown that ended or an event's reminder (NoticeClock.Alerting): it is not held back by the rate limit,
    // only by the wider UrgentLimit.
    public bool Urgent { get; init; }
    internal DateTimeOffset SubmittedAt { get; set; }
    internal DateTimeOffset LastMerge { get; set; }
    internal DateTimeOffset StartedAt { get; set; }
    internal TimeSpan Remaining { get; set; }
    // Lower comes first: high priority notices, then other notices and summaries, then quota alerts, then track changes.
    // The user's own interaction (rank 1 in the design) is not an item: it holds the lane instead.
    public int Rank => Kind switch
    {
        AttentionKind.Notice or AttentionKind.Summary => Priority == NoticePriority.High ? 2 : 3,
        AttentionKind.Quota => 4, _ => 5
    };
    // A track change is only worth showing as it happens; everything else can wait its turn.
    public bool Queueable => Kind != AttentionKind.Track;
    public bool IsNotice => Kind is AttentionKind.Notice or AttentionKind.Summary;
}

// One step for a lane's presenter: an item ended, started or changed. A lane is one panel (one monitor).
public readonly record struct AttentionChange(string Lane, AttentionItem? Ended, AttentionItem? Started, AttentionItem? Updated);

// Decides what each panel's collapsed island briefly shows. One item shows at a time per lane, for its duration; others wait
// in rank order. A higher-ranked item takes over unless the user is interacting with the current one (Hold). Notices are
// limited to RateLimit shows per RateWindow (urgent ones, such as an alarm, still count but are held back only beyond
// UrgentLimit per UrgentWindow); a notice that has waited longer than Staleness is not shown at all (it stays in
// the recent notices). While suppressed (locked, paused, in a focus session) nothing shows and arriving notices are only
// counted, so that one summary can follow. The coordinator keeps no timer: the caller ticks it at NextDeadline.
public sealed class AttentionCoordinator
{
    public static readonly TimeSpan AggregationWindow = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);
    public const int RateLimit = 3;
    // Urgent notices are not held back by RateLimit, so that an alarm rings when it is due; a sender that sets off many at once
    // still cannot keep the island ringing (sec-L5). The limit is per source (UrgentBucket): a flood of timers from one script
    // does not hold back another application's alarm (S4).
    public static readonly TimeSpan UrgentWindow = TimeSpan.FromMinutes(1);
    public const int UrgentLimit = 3;
    public static readonly TimeSpan Staleness = TimeSpan.FromSeconds(30);
    // High priority notices wait longer, but not for ever: after this they stay in the recent notices only.
    public static readonly TimeSpan HighStaleness = TimeSpan.FromMinutes(5);
    // Items one lane keeps waiting; beyond it the least important and oldest leave the queue (a notice stays in the recent list).
    public const int QueueLimit = 32;
    // A resumed or refreshed item stays at least this long, so it does not vanish the moment the pointer leaves.
    public static readonly TimeSpan MinimumRemaining = TimeSpan.FromMilliseconds(1500);

    private sealed class Lane
    {
        public AttentionItem? Current;
        public DateTimeOffset Ends;
        public bool Held, Busy;
        public readonly List<AttentionItem> Queue = [];
    }

    private readonly Dictionary<string, Lane> _lanes = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _noticeShows = [];
    private readonly List<(DateTimeOffset At, string Group)> _urgentShows = [];
    // The notices counted for the summary: a notice updated while suppressed counts once (core-L7).
    private readonly HashSet<string> _suppressedKeys = new(StringComparer.Ordinal);

    public bool Suppressed { get; private set; }
    // The last word before anything starts: an item it refuses is dropped instead (notices switched off, a notice deleted
    // from the recent list meanwhile). Null lets everything through.
    public Func<AttentionItem, bool>? CanShow { get; set; }
    // Notices that arrived while suppressed, for the summary shown afterwards.
    public int SuppressedCount { get; private set; }

    public AttentionItem? Current(string lane) => _lanes.TryGetValue(lane, out var state) ? state.Current : null;
    public IReadOnlyList<AttentionItem> Queued(string lane) => _lanes.TryGetValue(lane, out var state) ? state.Queue : [];

    private Lane Get(string lane) { if (!_lanes.TryGetValue(lane, out var state)) _lanes[lane] = state = new Lane(); return state; }

    public IReadOnlyList<AttentionChange> Submit(AttentionItem item, DateTimeOffset now)
    {
        var changes = new List<AttentionChange>();
        if (Suppressed) { if (item.Kind == AttentionKind.Notice && _suppressedKeys.Add(item.Key)) SuppressedCount++; return changes; }
        var lane = Get(item.Lane);
        // A notice that starts ringing (a timer that came due while its own "started" notice still shows or waits) is shown
        // anew, urgent and for its whole duration, instead of only changing what the island says for the time left.
        if (item.Urgent)
        {
            // Only when it can start now and the user is not working with the card: else it changes in place, as before.
            if (lane.Current is { Urgent: false } quiet && quiet.Key == item.Key && !lane.Held && CanStart(item, now)) End(lane, now, changes, requeue: false);
            lane.Queue.RemoveAll(q => q.Key == item.Key && !q.Urgent);
        }
        if (lane.Current is { } current && current.Key == item.Key)
        {
            current.Payload = item.Payload;
            // Progress keeps the island a little longer, at most twice the notice's duration like a merged burst: a long
            // task does not keep the island (or the notices waiting behind it) for as long as it runs.
            if (item.Refresh && !lane.Held && lane.Ends - now < MinimumRemaining && now - current.StartedAt < current.Duration * 2) lane.Ends = now + MinimumRemaining;
            changes.Add(new(item.Lane, null, null, current));
            return changes;
        }
        if (lane.Queue.FirstOrDefault(q => q.Key == item.Key) is { } waiting) { waiting.Payload = item.Payload; return changes; }
        if (item.Kind == AttentionKind.Notice && item.Group.Length > 0)
        {
            // A burst from one application becomes one notice that counts the others and shows the latest.
            if (!item.Urgent && lane.Current is { Kind: AttentionKind.Notice } shown && shown.Group == item.Group && now - shown.LastMerge <= AggregationWindow)
            {
                Merge(shown, item, now);
                // A source that keeps sending does not keep the island: a merged notice stays at most twice its duration.
                if (!lane.Held && lane.Ends - now < MinimumRemaining && now - shown.StartedAt < shown.Duration * 2) lane.Ends = now + MinimumRemaining;
                changes.Add(new(item.Lane, null, null, shown));
                return changes;
            }
            if (!item.Urgent && lane.Queue.FirstOrDefault(q => q.Kind == AttentionKind.Notice && q.Group == item.Group && now - q.LastMerge <= AggregationWindow) is { } queued)
            { Merge(queued, item, now); return changes; }
        }
        item.SubmittedAt = now; item.LastMerge = now; item.Remaining = item.Duration;
        if (lane.Busy)
        {
            // The open panel shows notices on its own page; a quota alert waits for the panel to close.
            if (item.Kind == AttentionKind.Quota) Enqueue(lane, item);
            return changes;
        }
        if (lane.Current is null)
        {
            if (CanStart(item, now)) Start(lane, item, now, changes);
            else if (item.Queueable) Enqueue(lane, item);
            return changes;
        }
        if (!lane.Held && item.Rank < lane.Current.Rank && CanStart(item, now))
        {
            End(lane, now, changes, requeue: true);
            Start(lane, item, now, changes);
            return changes;
        }
        if (item.Queueable) Enqueue(lane, item);
        return changes;
    }

    // Ends items whose time is up, drops stale waiting ones and starts the next. Call at NextDeadline (or any time later).
    public IReadOnlyList<AttentionChange> Tick(DateTimeOffset now)
    {
        var changes = new List<AttentionChange>();
        Forget(now);
        foreach (var (name, lane) in _lanes)
        {
            if (lane.Current is not null && !lane.Held && now >= lane.Ends) End(lane, now, changes, requeue: false);
            lane.Queue.RemoveAll(item => Stale(item, now));
            if (lane.Current is null && !lane.Busy) StartNext(lane, now, changes);
        }
        return changes;
    }

    // The earliest time Tick has something to do, or null while nothing is pending.
    public DateTimeOffset? NextDeadline(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        void Consider(DateTimeOffset time) { if (next is null || time < next) next = time; }
        foreach (var lane in _lanes.Values)
        {
            if (lane.Current is not null && !lane.Held) Consider(lane.Ends);
            // Waiting only for a rate limit: wake when the oldest show leaves its window.
            if (lane.Current is null && !lane.Busy)
                foreach (var item in lane.Queue) Consider(StartsFrom(item, now));
            foreach (var item in lane.Queue) if (StaleAfter(item) is { } limit) Consider(item.SubmittedAt + limit);
        }
        return next;
    }

    // While the user hovers the item, has opened its details or works it from the keyboard, its time stands still and
    // nothing takes its place. Released, it keeps what was left, at least MinimumRemaining.
    public IReadOnlyList<AttentionChange> Hold(string laneName, bool held, DateTimeOffset now)
    {
        var changes = new List<AttentionChange>();
        var lane = Get(laneName);
        if (lane.Held == held) return changes;
        lane.Held = held;
        if (lane.Current is { } current)
        {
            if (held) current.Remaining = Positive(lane.Ends - now);
            else lane.Ends = now + (current.Remaining > MinimumRemaining ? current.Remaining : MinimumRemaining);
        }
        if (!held && lane.Current is null && !lane.Busy) StartNext(lane, now, changes);
        return changes;
    }

    // The lane's panel is open. Its notices are on the panel's notices page; what was showing ends (a quota alert waits).
    public IReadOnlyList<AttentionChange> SetBusy(string laneName, bool busy, DateTimeOffset now)
    {
        var changes = new List<AttentionChange>();
        var lane = Get(laneName);
        if (lane.Busy == busy) return changes;
        lane.Busy = busy;
        if (busy)
        {
            lane.Held = false;
            if (lane.Current is not null) End(lane, now, changes, requeue: lane.Current.Kind == AttentionKind.Quota);
            lane.Queue.RemoveAll(item => item.Kind != AttentionKind.Quota);
        }
        else StartNext(lane, now, changes);
        return changes;
    }

    // Ends the current item at the user's request (close button, Escape). It is not shown again.
    public IReadOnlyList<AttentionChange> Dismiss(string laneName, DateTimeOffset now)
    {
        var changes = new List<AttentionChange>();
        if (!_lanes.TryGetValue(laneName, out var lane) || lane.Current is null) return changes;
        lane.Held = false;
        End(lane, now, changes, requeue: false);
        if (!lane.Busy) StartNext(lane, now, changes);
        return changes;
    }

    // Removes one waiting or shown item, for example a notice the user deleted from the recent list.
    public IReadOnlyList<AttentionChange> Withdraw(string key, DateTimeOffset now) => Remove(item => item.Key == key, now);

    // Removes every waiting or shown item that matches, in all lanes, before anything else may start: switching notices off or
    // clearing the recent list cancels all that waits as well as what shows. Nothing removed is counted for a summary.
    public IReadOnlyList<AttentionChange> Remove(Func<AttentionItem, bool> match, DateTimeOffset now)
    {
        var changes = new List<AttentionChange>();
        foreach (var lane in _lanes.Values)
        {
            lane.Queue.RemoveAll(item => match(item));
            if (lane.Current is { } current && match(current)) { lane.Held = false; End(lane, now, changes, requeue: false); if (!lane.Busy) StartNext(lane, now, changes); }
        }
        return changes;
    }

    // Suppressed: everything showing ends and nothing waits. Ending suppression returns the number of notices that arrived
    // meanwhile, for the caller to show one summary; the count starts again from zero.
    public IReadOnlyList<AttentionChange> SetSuppressed(bool suppressed, DateTimeOffset now, out int arrived)
    {
        arrived = 0;
        var changes = new List<AttentionChange>();
        if (Suppressed == suppressed) return changes;
        Suppressed = suppressed;
        if (suppressed)
        {
            SuppressedCount = 0; _suppressedKeys.Clear();
            foreach (var lane in _lanes.Values)
            {
                lane.Held = false;
                if (lane.Current is not null) End(lane, now, changes, requeue: false);
                // Notices that were waiting count toward the summary; nothing else is kept.
                foreach (var item in lane.Queue.Where(item => item.Kind == AttentionKind.Notice && _suppressedKeys.Add(item.Key))) SuppressedCount += item.Count;
                lane.Queue.Clear();
            }
        }
        else { arrived = SuppressedCount; SuppressedCount = 0; _suppressedKeys.Clear(); }
        return changes;
    }

    // A panel closed for good (its monitor was removed). Its items are dropped without being shown elsewhere.
    public void RemoveLane(string laneName) => _lanes.Remove(laneName);

    private static void Merge(AttentionItem target, AttentionItem item, DateTimeOffset now)
    {
        target.Key = item.Key; target.Payload = item.Payload; target.Count += item.Count; target.LastMerge = now;
    }

    private static void Enqueue(Lane lane, AttentionItem item)
    {
        int index = lane.Queue.FindIndex(queued => queued.Rank > item.Rank);
        if (index < 0) lane.Queue.Add(item); else lane.Queue.Insert(index, item);
        // Full: an item of the least important rank leaves, the one that has waited longest.
        while (lane.Queue.Count > QueueLimit)
        {
            int worst = lane.Queue.Max(queued => queued.Rank);
            var oldest = lane.Queue.Where(queued => queued.Rank == worst).MinBy(queued => queued.SubmittedAt)!;
            lane.Queue.Remove(oldest);
        }
    }

    private bool CanStart(AttentionItem item, DateTimeOffset now) => StartsFrom(item, now) <= now;

    // The earliest moment the rate limits let the item start.
    private DateTimeOffset StartsFrom(AttentionItem item, DateTimeOffset now)
    {
        if (!item.IsNotice) return now;
        Forget(now);
        if (!item.Urgent) return _noticeShows.Count < RateLimit ? now : _noticeShows.Min() + RateWindow;
        string bucket = UrgentBucket(item.Group);
        var own = _urgentShows.Where(show => show.Group == bucket).Select(show => show.At).ToList();
        return own.Count < UrgentLimit ? now : own.Min() + UrgentWindow;
    }

    // Each Windows application has its own urgent limit; senders that name themselves freely (brim-notify, WSL applications)
    // share one per kind, so a script cannot get round the limit by changing its name (S4).
    private static string UrgentBucket(string group)
    {
        if (group.StartsWith("windows:", StringComparison.Ordinal)) return group;
        int colon = group.IndexOf(':');
        return colon < 0 ? group : group[..colon];
    }

    private void Forget(DateTimeOffset now)
    {
        _noticeShows.RemoveAll(time => now - time >= RateWindow);
        _urgentShows.RemoveAll(show => now - show.At >= UrgentWindow);
    }

    private void Start(Lane lane, AttentionItem item, DateTimeOffset now, List<AttentionChange> changes)
    {
        // Refused at the last moment: the next one that may show takes its turn (StartNext only offers items CanShow allows).
        if (CanShow?.Invoke(item) == false) { StartNext(lane, now, changes); return; }
        lane.Current = item;
        item.StartedAt = now;
        lane.Ends = now + Positive(item.Remaining > TimeSpan.Zero ? item.Remaining : item.Duration);
        if (item.IsNotice) _noticeShows.Add(now);
        if (item.IsNotice && item.Urgent) _urgentShows.Add((now, UrgentBucket(item.Group)));
        changes.Add(new(item.Lane, null, item, null));
    }

    private static void End(Lane lane, DateTimeOffset now, List<AttentionChange> changes, bool requeue)
    {
        var item = lane.Current!;
        lane.Current = null; lane.Held = false;
        changes.Add(new(item.Lane, item, null, null));
        if (!requeue || !item.Queueable) return;
        item.Remaining = Positive(lane.Ends - now);
        // Taken over with time left: it resumes later with that time, ahead of others of its rank.
        if (item.Remaining >= TimeSpan.FromSeconds(1))
        {
            int index = lane.Queue.FindIndex(queued => queued.Rank >= item.Rank);
            if (index < 0) lane.Queue.Add(item); else lane.Queue.Insert(index, item);
        }
    }

    private void StartNext(Lane lane, DateTimeOffset now, List<AttentionChange> changes)
    {
        lane.Queue.RemoveAll(item => Stale(item, now) || CanShow?.Invoke(item) == false);
        foreach (var item in lane.Queue)
        {
            if (!CanStart(item, now)) continue;
            lane.Queue.Remove(item);
            Start(lane, item, now, changes);
            return;
        }
    }

    // How long an item may wait before it is no longer worth showing: normal notices and quota alerts briefly, high priority
    // notices longer. A summary of notices held back waits as long as needed (there is only ever one).
    private static TimeSpan? StaleAfter(AttentionItem item) => item.Kind switch
    {
        AttentionKind.Notice => item.Priority == NoticePriority.High ? HighStaleness : Staleness,
        AttentionKind.Quota => Staleness,
        _ => null
    };
    private static bool Stale(AttentionItem item, DateTimeOffset now) => StaleAfter(item) is { } limit && now - item.SubmittedAt > limit;
    private static TimeSpan Positive(TimeSpan span) => span > TimeSpan.Zero ? span : TimeSpan.Zero;
}
