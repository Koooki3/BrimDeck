namespace BrimDeck.Core.Notices;

// What may briefly take the collapsed island: an arriving notice, a summary of notices that arrived while BrimDeck kept
// quiet, a quota alert, or a track change.
public enum AttentionKind { Notice, Summary, Quota, Track }

public sealed class AttentionItem
{
    public required string Lane { get; init; }
    public required AttentionKind Kind { get; init; }
    // The same key names the same item: submitting it again updates it in place. The same notice shown on several panels
    // (NoticeScreen.All) has one item per lane, all with its key.
    public required string Key { get; set; }
    // Notices of one conversation share a group (AttentionCoordinator.GroupOf), so that its messages show as one notice
    // with a count.
    public string Group { get; init; } = "";
    public NoticePriority Priority { get; init; } = NoticePriority.Normal;
    public TimeSpan Duration { get; init; }
    public object? Payload { get; set; }
    // How many notices this one stands for: 1, or more after others of its conversation merged into it.
    public int Count { get; set; } = 1;
    // An update that should keep the item on screen a little longer, such as a task's progress.
    public bool Refresh { get; init; }
    // An alarm, a countdown that ended or an event's reminder (NoticeClock.Alerting): it never shows subdued, and is held
    // back only by UrgentLimit.
    public bool Urgent { get; init; }
    // How strongly a notice shows (NoticeLevels.Resolve); an update in place brings the notice's level of then.
    public NoticeLevel Level { get; set; } = NoticeLevel.Lively;
    // Stays until it is handled: it ends only when the user clicks or closes it, the panel opens, it is withdrawn (its
    // source no longer has it), or after PersistentLimit.
    public bool Persistent => Level == NoticeLevel.Persistent;
    // Started beyond the rate limit: it shows at once all the same, but as a plain notice (no backlight, no ripples).
    public bool Subdued { get; internal set; }
    internal DateTimeOffset SubmittedAt { get; set; }
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

// Decides what each panel's collapsed island briefly shows. One item shows at a time per lane, for its duration.
//
// Notices show as they arrive. One of the conversation that already shows or waits merges into it (the latest words, a
// count). Any other takes the island over once the notice that shows has been up for MinimumShown (an urgent one at
// once), unless that one is persistent, more important (a lower Rank) or held by the user (Hold); what it replaces is not
// shown again. Several that wait take over in the order they came. A notice that waited longer than Staleness is not shown
// at all (it stays in the recent notices).
//
// More than RateLimit notices per RateWindow still show at once, but subdued: as plain notices, so the island does not
// keep flashing. Urgent ones (an alarm) are never subdued and are held back beyond UrgentLimit per UrgentWindow. A notice
// shown on several lanes counts once.
//
// A notice takes over a quota alert or a track change at once; the alert resumes later. While suppressed (locked, paused,
// in a focus session) nothing shows and arriving notices are only counted, so that one summary can follow. The coordinator
// keeps no timer: the caller ticks it at NextDeadline.
public sealed class AttentionCoordinator
{
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);
    public const int RateLimit = 3;
    // Urgent notices are not subdued by RateLimit, so that an alarm rings when it is due; a sender that sets off many at once
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
    // A notice shows at least this long before another takes its place, so that each can be read.
    public static readonly TimeSpan MinimumShown = TimeSpan.FromMilliseconds(1500);
    // A persistent notice nobody handles ends after this, so that a call or an alarm does not hold the island for ever.
    public static readonly TimeSpan PersistentLimit = TimeSpan.FromMinutes(3);
    // While a persistent notice shows, its presenter plays the notice's motion again this often.
    public static readonly TimeSpan PersistentReplay = TimeSpan.FromSeconds(3);

    private sealed class Lane
    {
        public AttentionItem? Current;
        public DateTimeOffset Ends;
        // From when another notice may take the current one's place.
        public DateTimeOffset Yields;
        // Leaving: the panel is still open but on its way to closing (the pointer has left it, the closing has begun).
        public bool Held, Busy, Leaving;
        public readonly List<AttentionItem> Queue = [];
    }

    private readonly Dictionary<string, Lane> _lanes = new(StringComparer.Ordinal);
    // The notices that started with their full effect in the last RateWindow, and the urgent ones of the last UrgentWindow;
    // by key, so that a notice shown on several lanes counts once.
    private readonly List<(DateTimeOffset At, string Key)> _noticeShows = [];
    private readonly List<(DateTimeOffset At, string Group, string Key)> _urgentShows = [];
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

    // The group of a notice: its application and, when it has one, its title (a chat's notifications are titled with the
    // conversation), so that one conversation's messages merge and another's take the island over.
    public static string GroupOf(string appKey, string title) => title.Length > 0 ? appKey + GroupSeparator + title : appKey;
    private const char GroupSeparator = '\n';

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
            bool persisted = current.Persistent;
            current.Payload = item.Payload;
            if (item.IsNotice) current.Level = item.Level;
            // The next song's title shows for its whole time, not what was left of the one before (the panel times it from now).
            if (item.Kind == AttentionKind.Track)
            {
                if (lane.Held) current.Remaining = item.Duration; else lane.Ends = now + item.Duration;
            }
            // Now to stay until it is handled, or no longer: its time starts again from here.
            if (current.Persistent != persisted)
            {
                var span = current.Persistent ? PersistentLimit : Positive(current.Duration);
                if (lane.Held) current.Remaining = span; else lane.Ends = now + span;
            }
            // Progress keeps the island a little longer, at most twice the notice's duration like a merged burst: a long
            // task does not keep the island (or the notices waiting behind it) for as long as it runs.
            else if (item.Refresh && !current.Persistent) Extend(lane, current, now);
            changes.Add(new(item.Lane, null, null, current));
            return changes;
        }
        if (lane.Queue.FirstOrDefault(q => q.Key == item.Key) is { } waiting)
        {
            waiting.Payload = item.Payload;
            if (item.IsNotice) waiting.Level = item.Level;
            return changes;
        }
        if (Merges(item))
        {
            // Another message of the conversation that shows: the notice counts it and shows the latest, however long ago the
            // one before came.
            if (lane.Current is { } shown && Merges(shown) && shown.Group == item.Group)
            {
                Merge(shown, item);
                // A source that keeps sending does not keep the island: a merged notice stays at most twice its duration.
                Extend(lane, shown, now);
                changes.Add(new(item.Lane, null, null, shown));
                return changes;
            }
            if (lane.Queue.FirstOrDefault(q => Merges(q) && q.Group == item.Group) is { } queued) { Merge(queued, item); return changes; }
        }
        item.SubmittedAt = now; item.Remaining = item.Duration;
        if (lane.Busy)
        {
            // The open panel shows notices on its own page; a quota alert waits for the panel to close. So does a notice that
            // arrives once the pointer has left the panel: no one is looking at the page any more, and it shows from the
            // island once the panel has closed.
            if (item.Kind == AttentionKind.Quota || lane.Leaving && item.IsNotice) Enqueue(lane, item);
            return changes;
        }
        if (lane.Current is null)
        {
            if (CanStart(item, now)) Start(lane, item, now, changes);
            else if (item.Queueable) Enqueue(lane, item);
            return changes;
        }
        if (!lane.Current.IsNotice)
        {
            // A quota alert or a track change gives way at once; the alert resumes later with the time it had left.
            if (!lane.Held && item.Rank < lane.Current.Rank && CanStart(item, now))
            {
                End(lane, now, changes, requeue: true);
                Start(lane, item, now, changes);
            }
            else if (item.Queueable) Enqueue(lane, item);
            return changes;
        }
        if (!item.Queueable) return changes;
        // A notice shows: this one waits for it to have been up long enough, or to end.
        Enqueue(lane, item);
        TakeOver(lane, now, changes);
        return changes;
    }

    // Ends items whose time is up, drops stale waiting ones, and starts or lets take over the next. Call at NextDeadline (or
    // any time later).
    public IReadOnlyList<AttentionChange> Tick(DateTimeOffset now)
    {
        var changes = new List<AttentionChange>();
        Forget(now);
        foreach (var lane in _lanes.Values)
        {
            if (lane.Current is not null && !lane.Held && now >= lane.Ends) End(lane, now, changes, requeue: false);
            lane.Queue.RemoveAll(item => Stale(item, now));
            if (lane.Current is not null) TakeOver(lane, now, changes);
            else if (!lane.Busy) StartNext(lane, now, changes);
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
            // Waiting only for the urgent limit: wake when the oldest show leaves its window.
            if (lane.Current is null && !lane.Busy)
                foreach (var item in lane.Queue) Consider(StartsFrom(item, now));
            // Waiting for the notice that shows to have been up long enough.
            if (lane.Current is { } shown && Yielding(lane))
                foreach (var item in lane.Queue)
                    if (Takes(item, shown)) { var from = StartsFrom(item, now); Consider(item.Urgent || from > lane.Yields ? from : lane.Yields); }
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
            else
            {
                lane.Ends = now + (current.Remaining > MinimumRemaining ? current.Remaining : MinimumRemaining);
                if (lane.Yields < now + MinimumRemaining) lane.Yields = now + MinimumRemaining;
            }
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
        lane.Busy = busy; lane.Leaving = false;
        if (busy)
        {
            lane.Held = false;
            if (lane.Current is not null) End(lane, now, changes, requeue: lane.Current.Kind == AttentionKind.Quota);
            lane.Queue.RemoveAll(item => item.Kind != AttentionKind.Quota);
        }
        else StartNext(lane, now, changes);
        return changes;
    }

    // The open panel is about to close (the pointer left it) or no longer is (the pointer came back). Notices that arrive
    // meanwhile wait for the panel to close (Submit); coming back, the user sees them on the page, so they wait no longer.
    public void SetLeaving(string laneName, bool leaving)
    {
        var lane = Get(laneName);
        if (!lane.Busy || lane.Leaving == leaving) return;
        lane.Leaving = leaving;
        if (!leaving) lane.Queue.RemoveAll(item => item.IsNotice);
    }

    // Ends the lane's current item (its panel cannot show it, or the user closed a quota alert). It is not shown again
    // there; the same notice on another lane stays (Withdraw ends it everywhere).
    public IReadOnlyList<AttentionChange> Dismiss(string laneName, DateTimeOffset now)
    {
        var changes = new List<AttentionChange>();
        if (!_lanes.TryGetValue(laneName, out var lane) || lane.Current is null) return changes;
        lane.Held = false;
        End(lane, now, changes, requeue: false);
        if (!lane.Busy) StartNext(lane, now, changes);
        return changes;
    }

    // Removes one waiting or shown item from every lane, for example a notice the user handled on one screen or deleted
    // from the recent list.
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

    // A plain arriving notice of a conversation: it joins the one of that conversation that shows or waits. An alarm, a call
    // or anything that stays until handled is always its own.
    private static bool Merges(AttentionItem item) => item.Kind == AttentionKind.Notice && item.Group.Length > 0 && !item.Urgent && !item.Persistent;

    private static void Merge(AttentionItem target, AttentionItem item)
    {
        target.Key = item.Key; target.Payload = item.Payload; target.Level = item.Level; target.Count += item.Count;
    }

    // An item that just changed stays long enough to be read, but never much past twice its duration.
    private static void Extend(Lane lane, AttentionItem item, DateTimeOffset now)
    {
        if (!lane.Held && lane.Ends - now < MinimumRemaining && now - item.StartedAt < item.Duration * 2) lane.Ends = now + MinimumRemaining;
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

    // Whether the notice that shows on the lane gives way to a waiting one once it has been up long enough.
    private static bool Yielding(Lane lane) => lane.Current is { IsNotice: true, Persistent: false } && !lane.Held && !lane.Busy;

    // Whether a waiting item may take the place of the notice that shows: a notice that is not less important.
    private static bool Takes(AttentionItem item, AttentionItem shown) => item.IsNotice && item.Rank <= shown.Rank;

    // The notice that shows has been up long enough and another waits: that one takes its place. The one replaced is not
    // shown again (it stays in the recent notices). An alarm or a call does not wait for the notice to have been read.
    private void TakeOver(Lane lane, DateTimeOffset now, List<AttentionChange> changes)
    {
        if (!Yielding(lane)) return;
        var shown = lane.Current!;
        bool Ready(AttentionItem item) => Takes(item, shown) && (item.Urgent || now >= lane.Yields) && CanStart(item, now);
        lane.Queue.RemoveAll(item => Stale(item, now) || Ready(item) && CanShow?.Invoke(item) == false);
        if (lane.Queue.FirstOrDefault(Ready) is not { } next) return;
        lane.Queue.Remove(next);
        End(lane, now, changes, requeue: false);
        Start(lane, next, now, changes);
    }

    private bool CanStart(AttentionItem item, DateTimeOffset now) => StartsFrom(item, now) <= now;

    // The earliest moment the item may start: now, or for an urgent notice once its source's limit allows.
    private DateTimeOffset StartsFrom(AttentionItem item, DateTimeOffset now)
    {
        if (!item.IsNotice || !item.Urgent) return now;
        Forget(now);
        // Counted already: it rings on another lane.
        if (_urgentShows.Any(show => show.Key == item.Key)) return now;
        string bucket = UrgentBucket(item.Group);
        var own = _urgentShows.Where(show => show.Group == bucket).Select(show => show.At).ToList();
        return own.Count < UrgentLimit ? now : own.Min() + UrgentWindow;
    }

    // Each Windows application has its own urgent limit; senders that name themselves freely (brim-notify, WSL applications)
    // share one per kind, so a script cannot get round the limit by changing its name (S4). The title in a group
    // (GroupOf) is left out: an application's alarms count together whatever they are called.
    private static string UrgentBucket(string group)
    {
        int title = group.IndexOf(GroupSeparator);
        if (title >= 0) group = group[..title];
        if (group.StartsWith("windows:", StringComparison.Ordinal)) return group;
        int colon = group.IndexOf(':');
        return colon < 0 ? group : group[..colon];
    }

    private void Forget(DateTimeOffset now)
    {
        _noticeShows.RemoveAll(show => now - show.At >= RateWindow);
        _urgentShows.RemoveAll(show => now - show.At >= UrgentWindow);
    }

    // Counts a notice that starts, once whatever the number of lanes it shows on, and decides whether it shows subdued:
    // beyond RateLimit in RateWindow it does, unless it is urgent or stays until handled. Only notices that show with their
    // full effect count, so the island flashes at most RateLimit times per window.
    private void Count(AttentionItem item, DateTimeOffset now)
    {
        Forget(now);
        if (_noticeShows.Any(show => show.Key == item.Key)) item.Subdued = false;
        else
        {
            item.Subdued = !item.Urgent && !item.Persistent && _noticeShows.Count >= RateLimit;
            if (!item.Subdued) _noticeShows.Add((now, item.Key));
        }
        if (item.Urgent && !_urgentShows.Any(show => show.Key == item.Key)) _urgentShows.Add((now, UrgentBucket(item.Group), item.Key));
    }

    private void Start(Lane lane, AttentionItem item, DateTimeOffset now, List<AttentionChange> changes)
    {
        // Refused at the last moment: the next one that may show takes its turn (StartNext only offers items CanShow allows).
        if (CanShow?.Invoke(item) == false) { StartNext(lane, now, changes); return; }
        lane.Current = item;
        item.StartedAt = now;
        lane.Yields = now + MinimumShown;
        lane.Ends = now + (item.Persistent ? PersistentLimit : Positive(item.Remaining > TimeSpan.Zero ? item.Remaining : item.Duration));
        if (item.IsNotice) Count(item, now);
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
    // notices longer. A summary of notices held back waits as long as needed (there is only ever one), and so does a notice
    // that stays until handled (its owner withdraws it when there is nothing left to handle).
    private static TimeSpan? StaleAfter(AttentionItem item) => item.Kind switch
    {
        AttentionKind.Notice when item.Persistent => null,
        AttentionKind.Notice => item.Priority == NoticePriority.High ? HighStaleness : Staleness,
        AttentionKind.Quota => Staleness,
        _ => null
    };
    private static bool Stale(AttentionItem item, DateTimeOffset now) => StaleAfter(item) is { } limit && now - item.SubmittedAt > limit;
    private static TimeSpan Positive(TimeSpan span) => span > TimeSpan.Zero ? span : TimeSpan.Zero;
}
