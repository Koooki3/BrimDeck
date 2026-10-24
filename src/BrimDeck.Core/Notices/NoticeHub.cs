namespace BrimDeck.Core.Notices;

public enum NoticeReceipt { Accepted, Updated, Duplicate, Filtered }

// What Receive did. Present asks for the notice to be shown as it arrives; an update that only moves a task's progress
// does not, and refreshes a notice that is already shown instead.
public sealed record NoticeOutcome(NoticeReceipt Receipt, Notice? Notice, bool Present, string? Reason = null);

// The notices BrimDeck keeps: what arrived in the retention period, newest first, at most MaxCount. It applies the user's
// rules, recognizes retries and updates, and words the result of "brim-notify run". It runs on one thread (the UI
// thread); sources hand their notices to it there.
public sealed class NoticeHub
{
    public const int DefaultMaxCount = 500;
    // Reminders still to come and running stopwatches stay past MaxCount, but not without end: a script that starts timers
    // in a loop would otherwise grow the list (and the file) for ever. Beyond this the application holding the most loses
    // the one received longest ago (C-M3, M1).
    public const int MaxPending = 200;
    private static readonly TimeSpan EventMemory = TimeSpan.FromMinutes(10);
    // Senders choose application names freely; at most this many are remembered, the least recently seen go first.
    public const int MaxKnownApps = 200;
    private const int EventMemoryCount = 512;
    private readonly List<Notice> _notices = [];
    // The same notices by Id: a task's progress finds its notice without walking the list (perf-7).
    private readonly Dictionary<string, Notice> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NoticeKnownApp> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _events = new(StringComparer.Ordinal);
    private readonly Func<string, NoticeAppRule?> _rule;
    // What was deleted or cleared before the saved file was read in (C-L2): a file read later must not bring it back.
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private DateTimeOffset? _clearedAt, _doneClearedAt;
    private bool _trackRemovals;

    public NoticeHub(Func<string, NoticeAppRule?>? rule = null) => _rule = rule ?? (_ => null);

    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
    public int MaxCount { get; set; } = DefaultMaxCount;
    // Newest first, by when BrimDeck received them.
    public IReadOnlyList<Notice> Notices => _notices;
    public IEnumerable<NoticeKnownApp> KnownApps => _apps.Values.OrderByDescending(app => app.LastSeen);
    public int UnviewedCount { get; private set; }
    // Raised after every change: a notice added, updated, removed or marked viewed, or the list cleared or loaded.
    public event Action? Changed;
    // Raised for changes worth saving; marking notices viewed is included, so a restart keeps that state. The argument is
    // true for a change that may wait for a later save: a task's progress, notices marked viewed (perf-7).
    public event Action<bool>? Persist;
    // Raised with how many reminders or stopwatches went beyond MaxPending, for the log.
    public event Action<int>? PendingDropped;

    // True while the saved file has not been read into the list (still reading, unreachable, damaged): deletions and
    // clearings are remembered so that Load leaves them out (C-L2). Setting it false forgets them.
    public bool TrackRemovals
    {
        get => _trackRemovals;
        set
        {
            _trackRemovals = value;
            if (!value) { _deleted.Clear(); _clearedAt = _doneClearedAt = null; }
        }
    }

    public Notice? Find(string id) => _byId.GetValueOrDefault(id);
    public Notice? FindInstance(string instanceKey) => _notices.FirstOrDefault(n => n.InstanceKey == instanceKey);

    // The notice an input updates by its instance at the source: only one of the same application (core-M1), and for a
    // WSL desktop notification only one still present at its source. A notification service numbers its notifications from
    // 1 again when it restarts, so a number seen before BrimDeck or the agent restarted names another notification (core-H2).
    private Notice? SameInstance(string instanceKey, NoticeInput input)
        => _notices.FirstOrDefault(n => n.InstanceKey == instanceKey && n.AppKey == input.AppKey && (n.Origin != NoticeOrigin.Wsl || n.SourcePresent));

    // Whether two command-line notices came over the same link. Any Windows process can name a distribution on the pipe
    // (NoticeIntake.FromPipe), so its notices share that distribution's application rules but never its --id, -r or stop
    // keys with what the distribution's agent delivered (S3).
    private static bool SameLink(Notice notice, NoticeInput input) => NoticeIntake.ViaPipe(notice.InstanceKey) == NoticeIntake.ViaPipe(input.InstanceKey);

    // The notice -r or stop names: one from the same link first. The distribution's agent also reaches what came over the pipe
    // under its name, which is where WSL's brim-notify turns while the agent is down (M2); never the other way round, since a
    // pipe sender cannot be told from any Windows process naming the distribution (S3).
    private Notice? Replacing(NoticeInput input, string key)
        => _notices.FirstOrDefault(n => n.AppKey == input.AppKey && n.ReplaceKey == key && SameLink(n, input))
            ?? (input.Distro is null || NoticeIntake.ViaPipe(input.InstanceKey) ? null
                : _notices.FirstOrDefault(n => n.AppKey == input.AppKey && n.ReplaceKey == key && NoticeIntake.ViaPipe(n.InstanceKey)));

    public static NoticePriority EffectivePriority(NoticeAppRule? rule, NoticePriority hint)
    {
        var chosen = rule?.Priority ?? NoticePriority.Normal;
        // A sender can lower its own notice, never raise it: only the user's rule makes an application high priority.
        return chosen == NoticePriority.High ? NoticePriority.High : hint == NoticePriority.Low ? NoticePriority.Low : chosen;
    }

    // An alarm ringing, a countdown that ended, an event's reminder (NoticeClock.Alerting) is high priority by its nature,
    // unless the sender lowered it or the user set the application to low priority.
    public static NoticePriority AlertPriority(NoticeAppRule? rule, NoticePriority hint)
        => hint == NoticePriority.Low || rule?.Priority == NoticePriority.Low ? NoticePriority.Low : NoticePriority.High;

    public NoticeOutcome Receive(NoticeInput input, DateTimeOffset now)
    {
        var rule = _rule(input.AppKey);
        if (rule is { Enabled: false }) return new(NoticeReceipt.Filtered, null, false, "disabled");
        if (input.EventId is { Length: > 0 } eventId)
        {
            ForgetEvents(now);
            var key = (NoticeIntake.ViaPipe(input.InstanceKey) ? "pipe\n" : "") + input.AppKey + "\n" + eventId;
            // A retry of a message already recorded, also one recorded before BrimDeck restarted (the saved notice keeps its id,
            // whatever form its instance key had then: C-L4).
            if (_events.ContainsKey(key) || _notices.Any(n => n.EventId == eventId && n.AppKey == input.AppKey && SameLink(n, input))) return new(NoticeReceipt.Duplicate, null, false);
            _events[key] = now;
        }
        if (input.StopTiming) return StopByKey(input, now);
        RememberApp(input, now);
        var (title, body) = Words(input);
        var priority = NoticeClock.Alerting(input.Timing, now) || input.Call ? AlertPriority(rule, input.PriorityHint) : EffectivePriority(rule, input.PriorityHint);
        bool quiet = rule?.Muted == true || priority == NoticePriority.Low;
        bool late = Late(input, priority, now);
        var existing = SameInstance(input.InstanceKey, input)
            ?? (input.Replaces is { Length: > 0 } replaced ? SameInstance(replaced, input) : null)
            ?? (input.ReplaceKey is { Length: > 0 } replace ? Replacing(input, replace) : null);
        if (existing is not null)
        {
            bool significant = existing.Title != title || existing.Category != input.Category || existing.Call != input.Call || existing.TimeKind != (input.Timing?.Kind ?? NoticeTimeKind.None)
                || existing.Progress is not null && (input.Progress is null || input.Progress >= 1) || existing.Body != body && input.Progress is null;
            existing.InstanceKey = input.InstanceKey;
            existing.AppName = input.AppName; existing.Title = title; existing.Body = body;
            existing.IconRef = input.IconRef ?? existing.IconRef; existing.Category = input.Category; existing.Priority = priority;
            existing.Progress = input.Progress; existing.Actions = [.. input.Actions]; existing.Capabilities = input.Capabilities;
            existing.Timing = input.Timing?.Copy(); existing.Call = input.Call; existing.Transient = input.Transient;
            existing.EventId = input.EventId ?? existing.EventId;
            // The notice now says what the latest message said, so it is as old as that message: an update shown as it arrives
            // must not read "38 min ago" from the first notice it replaced. It also keeps the list's order after a restart.
            // A task's progress alone does neither: two tasks updating in turn would swap places in the recent list several
            // times a second (the retention counts from UpdatedAt).
            if (significant) { existing.CreatedAt = input.CreatedAt ?? now; existing.ReceivedAt = now; }
            existing.UpdatedAt = now; existing.SourcePresent = true; existing.Revision++;
            if (significant)
            {
                existing.Viewed = false;
                // An update moves the notice to the top, as a new one would arrive there.
                _notices.Remove(existing); _notices.Insert(0, existing);
            }
            Recount(); Changed?.Invoke(); Persist?.Invoke(!significant);
            return new(NoticeReceipt.Updated, existing, significant && !quiet && !late, late ? "late" : null);
        }
        var notice = new Notice
        {
            InstanceKey = input.InstanceKey, AppKey = input.AppKey, AppName = input.AppName, AppId = input.AppId, Origin = input.Origin,
            Distro = input.Distro, ReplaceKey = input.ReplaceKey, EventId = input.EventId, Title = title, Body = body, IconRef = input.IconRef,
            CreatedAt = input.CreatedAt ?? now, ReceivedAt = now, UpdatedAt = now, Category = input.Category, Priority = priority,
            Progress = input.Progress, Actions = [.. input.Actions], Capabilities = input.Capabilities, Timing = input.Timing?.Copy(), Call = input.Call,
            Transient = input.Transient
        };
        _notices.Insert(0, notice); _byId[notice.Id] = notice;
        Prune(now, notify: false, keep: notice);
        Recount(); Changed?.Invoke(); Persist?.Invoke(false);
        return new(NoticeReceipt.Accepted, notice, !quiet && !late, late ? "late" : null);
    }

    // A message that reached BrimDeck long after it was written: it is recorded (unviewed) in the recent notices but not
    // shown as it arrives, by the same limits as a notice waiting its turn (AttentionCoordinator.Staleness). Its age is the
    // sender's own measure (NoticeInput.Age); without one, only a sender on Windows' own clock (no distribution) is judged
    // by its creation time, since a distribution's clock may be off. Windows' notices keep their own rules (the list read at
    // start-up is only a baseline), and time-kind notices their reminders (NoticeClock).
    public static bool Late(NoticeInput input, NoticePriority priority, DateTimeOffset now)
    {
        if (input.Missed) return !DueNow(input, now);
        if (input.Origin is not (NoticeOrigin.Cli or NoticeOrigin.Wsl) || input.Timing is { Kind: not NoticeTimeKind.Mail }) return false;
        var age = input.Age ?? (input.Distro is null && input.CreatedAt is { } created ? now - created : null);
        return age > (priority == NoticePriority.High ? AttentionCoordinator.HighStaleness : AttentionCoordinator.Staleness);
    }

    // A reminder that arrived during sleep and is read on waking still rings when it is due now (C-L14): it was raised, or
    // its event starts, within NoticeClock.MissedLimit. Older ones, and every other kind, are only recorded.
    private static bool DueNow(NoticeInput input, DateTimeOffset now)
    {
        if (!NoticeClock.Alerting(input.Timing, now)) return false;
        bool Near(DateTimeOffset? time) => time is { } at && (now - at).Duration() <= NoticeClock.MissedLimit;
        return Near(input.CreatedAt) || Near(input.Timing!.StartsAt);
    }

    // brim-notify stop: ends the earlier timer, stopwatch or event with the same key. Nothing new shows.
    private NoticeOutcome StopByKey(NoticeInput input, DateTimeOffset now)
    {
        var target = input.ReplaceKey is { Length: > 0 } key ? Replacing(input, key) : null;
        if (target is null || !StopTimer(target.Id, now)) return new(NoticeReceipt.Filtered, null, false, "no-timer");
        return new(NoticeReceipt.Updated, target, false);
    }

    // ----- Time-kind notices BrimDeck keeps time for -----

    // The earliest moment a notice BrimDeck keeps time for is due (NoticeClock.NextDue), or null when none is.
    public DateTimeOffset? NextDue()
    {
        DateTimeOffset? next = null;
        foreach (var notice in _notices)
            if (NoticeClock.NextDue(notice.Timing) is { } due && (next is null || due < next)) next = due;
        return next;
    }

    // Gives the reminders that are due: a countdown's end, an event's reminder or start. Returns the notices to show; a
    // reminder missed by more than NoticeClock.MissedLimit (BrimDeck was asleep or not running) is only recorded.
    public IReadOnlyList<Notice> FireDue(DateTimeOffset now)
    {
        var shown = new List<Notice>();
        bool changed = false;
        foreach (var notice in _notices.ToList())
        {
            if (notice.Timing is not { } timing || NoticeClock.Advance(timing, now) is not { } due) continue;
            changed = true;
            var rule = _rule(notice.AppKey);
            if (notice.Priority != NoticePriority.Low) notice.Priority = AlertPriority(rule, NoticePriority.Normal);
            notice.UpdatedAt = now; notice.Revision++;
            if (now - due > NoticeClock.MissedLimit) continue;
            notice.Viewed = false;
            // A reminder moves the notice to the top and reads as just arrived, as an update does; it is kept for the
            // retention from now, not from when the timer was set (core-H1).
            notice.ReceivedAt = now;
            _notices.Remove(notice); _notices.Insert(0, notice);
            if (rule is not { Enabled: false } && rule?.Muted != true && notice.Priority != NoticePriority.Low) shown.Add(notice);
        }
        if (changed) { Recount(); Changed?.Invoke(); Persist?.Invoke(false); }
        return shown;
    }

    // Stops a stopwatch (keeping the time it ran) or cancels a countdown or event. False when there was nothing to stop.
    public bool StopTimer(string id, DateTimeOffset now)
    {
        if (Find(id) is not { Timing: { Stopped: false } timing } notice) return false;
        if (timing.Kind == NoticeTimeKind.Stopwatch && timing.EndsAt is null) timing.EndsAt = now;
        timing.Stopped = true;
        notice.UpdatedAt = now; notice.Revision++;
        Changed?.Invoke(); Persist?.Invoke(false);
        return true;
    }

    // A countdown that ended reminds again after the span.
    public bool Snooze(string id, TimeSpan span, DateTimeOffset now)
    {
        if (Find(id) is not { Timing: { Kind: NoticeTimeKind.Countdown, Native: true, Stopped: false, Stage: >= 1 } timing } notice) return false;
        timing.StartsAt = now; timing.EndsAt = now + span; timing.Stage = 0;
        notice.UpdatedAt = now; notice.Revision++;
        Changed?.Invoke(); Persist?.Invoke(false);
        return true;
    }

    // The source no longer has the notice (removed from the notification center, closed by its application). BrimDeck keeps
    // it among the recent notices; its source actions are no longer offered.
    public void SourceRemoved(string instanceKey)
    {
        if (_notices.FirstOrDefault(n => n.InstanceKey == instanceKey && n.SourcePresent) is not { } notice) return;
        notice.SourcePresent = false; notice.Capabilities &= ~(NoticeCapabilities.Actions | NoticeCapabilities.ReportDismissal); notice.Revision++;
        Changed?.Invoke(); Persist?.Invoke(true);
    }

    public void MarkViewed(string id)
    {
        if (Find(id) is not { Viewed: false } notice) return;
        notice.Viewed = true; Recount(); Changed?.Invoke(); Persist?.Invoke(true);
    }
    public void MarkAllViewed()
    {
        if (UnviewedCount == 0) return;
        foreach (var notice in _notices) notice.Viewed = true;
        Recount(); Changed?.Invoke(); Persist?.Invoke(true);
    }
    public bool Delete(string id)
    {
        int index = _notices.FindIndex(n => n.Id == id);
        if (index < 0) return false;
        if (_trackRemovals) _deleted.Add(id);
        _byId.Remove(id); _notices.RemoveAt(index); Recount(); Changed?.Invoke(); Persist?.Invoke(false);
        return true;
    }
    // Clears BrimDeck's own list only; nothing is removed from Windows' notification center or any other source.
    // now: when, for a file read later (TrackRemovals); what it saved from before then stays cleared.
    public void Clear(DateTimeOffset? now = null)
    {
        if (_trackRemovals) _clearedAt = now ?? DateTimeOffset.UtcNow;
        if (_notices.Count == 0) return;
        _notices.Clear(); _byId.Clear(); Recount(); Changed?.Invoke(); Persist?.Invoke(false);
    }

    // The scheduled clearing: everything but a reminder still to come and a stopwatch still running, which would otherwise be
    // lost without a word (core-L2).
    public void ClearDone(DateTimeOffset now)
    {
        if (_trackRemovals) _doneClearedAt = now;
        if (_notices.RemoveAll(n => !Pending(n, now)) == 0) return;
        Reindex(); Recount(); Changed?.Invoke(); Persist?.Invoke(false);
    }

    // A reminder BrimDeck still has to give (an event next week) or a stopwatch still running.
    private static bool Pending(Notice notice, DateTimeOffset now)
        => NoticeClock.NextDue(notice.Timing) is not null || notice.Timing is { Kind: NoticeTimeKind.Stopwatch } && NoticeClock.IsLive(notice.Timing, now);

    // Drops notices older than the retention and beyond the count limit. Returns whether anything was dropped. keep, the
    // notice just received, stays whatever the limits: its sender is told it was accepted (M1).
    public bool Prune(DateTimeOffset now, bool notify = true, Notice? keep = null)
    {
        int before = _notices.Count;
        var limit = now - Retention;
        // A notice ages from its last change (a timer that rang, a stopwatch stopped: core-H1). A reminder BrimDeck still has
        // to give and a stopwatch still running stay past the retention and past the count limit; beyond the limit the oldest
        // of the others go (core-L2).
        // Each dropped notice leaves the index as it goes (review 9: with a full list every notice of a burst re-indexed it all).
        bool Drop(Notice notice) { _byId.Remove(notice.Id); return true; }
        _notices.RemoveAll(n => LastChange(n) < limit && !Pending(n, now) && Drop(n));
        for (int i = _notices.Count - 1; i >= 0 && _notices.Count > MaxCount; i--)
            if (!Pending(_notices[i], now) && _notices[i] != keep) { Drop(_notices[i]); _notices.RemoveAt(i); }
        // The pending ones have their own limit (C-M3).
        var pending = _notices.Where(n => Pending(n, now)).ToList();
        if (pending.Count > MaxPending)
        {
            var dropped = Excess(pending, pending.Count - MaxPending, keep);
            _notices.RemoveAll(n => dropped.Contains(n) && Drop(n));
            PendingDropped?.Invoke(dropped.Count);
        }
        foreach (var stale in _apps.Values.Where(app => app.LastSeen < now - TimeSpan.FromDays(30)).Select(app => app.Key).ToList()) _apps.Remove(stale);
        bool changed = _notices.Count != before;
        if (changed && notify) { Recount(); Changed?.Invoke(); Persist?.Invoke(false); }
        return changed;
    }

    // The pending notices to drop (M1): one at a time from the application holding the most, the one BrimDeck received longest
    // ago first (BrimDeck's own time: a sender chooses its CreatedAt). A script starting timers in a loop so loses its own, not
    // the reminders other applications set long ago. keep is never among them.
    private static HashSet<Notice> Excess(List<Notice> pending, int count, Notice? keep)
    {
        var sources = pending.GroupBy(n => n.AppKey, StringComparer.Ordinal)
            .Select(g => (Held: g.Count(), Oldest: new Queue<Notice>(g.Where(n => n != keep).OrderBy(n => n.ReceivedAt)))).ToList();
        var dropped = new HashSet<Notice>();
        while (dropped.Count < count)
        {
            int most = -1;
            for (int i = 0; i < sources.Count; i++)
                if (sources[i].Oldest.Count > 0 && (most < 0 || sources[i].Held > sources[most].Held
                    || sources[i].Held == sources[most].Held && sources[i].Oldest.Peek().ReceivedAt < sources[most].Oldest.Peek().ReceivedAt)) most = i;
            if (most < 0) break;
            dropped.Add(sources[most].Oldest.Dequeue());
            sources[most] = (sources[most].Held - 1, sources[most].Oldest);
        }
        return dropped;
    }

    private static DateTimeOffset LastChange(Notice notice) => notice.ReceivedAt > notice.UpdatedAt ? notice.ReceivedAt : notice.UpdatedAt;

    // Whether a saved notice was deleted or cleared away before the file was read (TrackRemovals).
    private bool Removed(Notice notice, DateTimeOffset now)
        => _deleted.Contains(notice.Id) || LastChange(notice) <= _clearedAt || LastChange(notice) <= _doneClearedAt && !Pending(notice, now);

    // Adds saved notices (see NoticeHistory) to those that arrived before the file was read: it is read in the background,
    // and sometimes only later (a drive that was not there at start-up). What arrived since is newer and wins, and what was
    // deleted or cleared meanwhile stays gone (C-L2). Notices from before are kept as not present at their sources: BrimDeck
    // cannot know what happened to them while it was not running.
    public void Load(IEnumerable<Notice> notices, IEnumerable<NoticeKnownApp> apps, DateTimeOffset now)
    {
        var saved = new List<Notice>();
        foreach (var notice in notices.Where(n => n is not null && n.Id is { Length: > 0 } && !_byId.ContainsKey(n.Id) && !Removed(n, now)).DistinctBy(n => n.Id))
        {
            // BrimDeck is the source of the timers it keeps; every other source may have changed meanwhile.
            notice.SourcePresent = notice.Timing is { Native: true };
            notice.Capabilities &= ~(NoticeCapabilities.Actions | NoticeCapabilities.ReportDismissal);
            notice.Title ??= ""; notice.Body ??= ""; notice.AppName ??= ""; notice.AppKey ??= ""; notice.Actions ??= [];
            notice.InstanceKey ??= ""; notice.AppId ??= "";
            saved.Add(notice);
        }
        var merged = _notices.Concat(saved).OrderByDescending(n => n.ReceivedAt).ToList();
        _notices.Clear(); _notices.AddRange(merged);
        foreach (var app in apps.Where(a => a is not null && !string.IsNullOrEmpty(a.Key))) _apps.TryAdd(app.Key, app);
        while (_apps.Count > MaxKnownApps) _apps.Remove(_apps.Values.MinBy(known => known.LastSeen)!.Key);
        Reindex();
        Prune(now, notify: false);
        Recount(); Changed?.Invoke();
    }

    private void Reindex()
    {
        _byId.Clear();
        foreach (var notice in _notices) _byId[notice.Id] = notice;
    }

    private void RememberApp(NoticeInput input, DateTimeOffset now)
    {
        if (!_apps.TryGetValue(input.AppKey, out var app))
        {
            if (_apps.Count >= MaxKnownApps) _apps.Remove(_apps.Values.MinBy(known => known.LastSeen)!.Key);
            _apps[input.AppKey] = app = new NoticeKnownApp { Key = input.AppKey, Origin = input.Origin };
        }
        app.Name = input.AppName; app.LastSeen = now; app.IconRef = input.IconRef ?? app.IconRef;
    }

    private void ForgetEvents(DateTimeOffset now)
    {
        if (_events.Count < EventMemoryCount && !_events.Values.Any(time => now - time > EventMemory)) return;
        foreach (var old in _events.Where(pair => now - pair.Value > EventMemory).Select(pair => pair.Key).ToList()) _events.Remove(old);
        if (_events.Count >= EventMemoryCount)
            foreach (var oldest in _events.OrderBy(pair => pair.Value).Take(_events.Count - EventMemoryCount + 1).Select(pair => pair.Key).ToList()) _events.Remove(oldest);
    }

    private void Recount() => UnviewedCount = _notices.Count(n => !n.Viewed);

    // The title and body as shown. A run result without its own text is worded from the command, the exit code and the time.
    public static (string Title, string Body) Words(NoticeInput input)
    {
        string title = NoticeText.Clean(input.Title, singleLine: true).Trim(), body = NoticeText.Clean(input.Body).Trim();
        if (input.ExitCode is { } code)
        {
            // The command goes into the title, so it is cleaned as a title is (C-L11).
            string command = NoticeText.Clean(input.Command ?? "", singleLine: true).Trim() is { Length: > 0 } run ? run : Loc.T("命令", "Command");
            if (title.Length == 0) title = code == 0 ? Loc.T($"{command} 已完成", $"{command} finished") : Loc.T($"{command} 失败", $"{command} failed");
            if (body.Length == 0)
            {
                string took = input.DurationMs is { } ms ? NoticeText.Duration(TimeSpan.FromMilliseconds(ms)) : "";
                body = took.Length > 0 ? Loc.T($"退出码 {code} · 用时 {took}", $"Exit code {code} · took {took}") : Loc.T($"退出码 {code}", $"Exit code {code}");
            }
        }
        if (title.Length == 0) { title = body.Split('\n')[0]; body = body.Length > title.Length ? body[title.Length..].TrimStart('\n') : ""; }
        // A timer or stopwatch without text is named by its kind.
        if (title.Length == 0 && input.Timing is { } timing) title = NoticeClock.Name(timing.Kind);
        return (title, body);
    }
}
