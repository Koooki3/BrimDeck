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
    private static readonly TimeSpan EventMemory = TimeSpan.FromMinutes(10);
    // Senders choose application names freely; at most this many are remembered, the least recently seen go first.
    public const int MaxKnownApps = 200;
    private const int EventMemoryCount = 512;
    private readonly List<Notice> _notices = [];
    private readonly Dictionary<string, NoticeKnownApp> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _events = new(StringComparer.Ordinal);
    private readonly Func<string, NoticeAppRule?> _rule;

    public NoticeHub(Func<string, NoticeAppRule?>? rule = null) => _rule = rule ?? (_ => null);

    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
    public int MaxCount { get; set; } = DefaultMaxCount;
    // Newest first, by when BrimDeck received them.
    public IReadOnlyList<Notice> Notices => _notices;
    public IEnumerable<NoticeKnownApp> KnownApps => _apps.Values.OrderByDescending(app => app.LastSeen);
    public int UnviewedCount { get; private set; }
    // Raised after every change: a notice added, updated, removed or marked viewed, or the list cleared or loaded.
    public event Action? Changed;
    // Raised for changes worth saving; marking notices viewed is included, so a restart keeps that state.
    public event Action? Persist;

    public Notice? Find(string id) => _notices.FirstOrDefault(n => n.Id == id);
    public Notice? FindInstance(string instanceKey) => _notices.FirstOrDefault(n => n.InstanceKey == instanceKey);

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
            var key = input.AppKey + "\n" + eventId;
            if (_events.ContainsKey(key)) return new(NoticeReceipt.Duplicate, null, false);
            _events[key] = now;
        }
        if (input.StopTiming) return StopByKey(input, now);
        RememberApp(input, now);
        var (title, body) = Words(input);
        var priority = NoticeClock.Alerting(input.Timing) ? AlertPriority(rule, input.PriorityHint) : EffectivePriority(rule, input.PriorityHint);
        bool quiet = rule?.Muted == true || priority == NoticePriority.Low;
        var existing = FindInstance(input.InstanceKey)
            ?? (input.Replaces is { Length: > 0 } replaced && FindInstance(replaced) is { } previous && previous.AppKey == input.AppKey ? previous : null)
            ?? (input.ReplaceKey is { Length: > 0 } replace ? _notices.FirstOrDefault(n => n.AppKey == input.AppKey && n.ReplaceKey == replace) : null);
        if (existing is not null)
        {
            bool significant = existing.Title != title || existing.Category != input.Category || existing.TimeKind != (input.Timing?.Kind ?? NoticeTimeKind.None)
                || existing.Progress is not null && (input.Progress is null || input.Progress >= 1) || existing.Body != body && input.Progress is null;
            existing.InstanceKey = input.InstanceKey;
            existing.AppName = input.AppName; existing.Title = title; existing.Body = body;
            existing.IconRef = input.IconRef ?? existing.IconRef; existing.Category = input.Category; existing.Priority = priority;
            existing.Progress = input.Progress; existing.Actions = [.. input.Actions]; existing.Capabilities = input.Capabilities;
            existing.Timing = input.Timing?.Copy();
            existing.UpdatedAt = now; existing.SourcePresent = true; existing.Revision++;
            if (significant) existing.Viewed = false;
            // An update moves the notice to the top, as a new one would arrive there.
            _notices.Remove(existing); _notices.Insert(0, existing);
            Recount(); Changed?.Invoke(); Persist?.Invoke();
            return new(NoticeReceipt.Updated, existing, significant && !quiet);
        }
        var notice = new Notice
        {
            InstanceKey = input.InstanceKey, AppKey = input.AppKey, AppName = input.AppName, AppId = input.AppId, Origin = input.Origin,
            Distro = input.Distro, ReplaceKey = input.ReplaceKey, Title = title, Body = body, IconRef = input.IconRef,
            CreatedAt = input.CreatedAt ?? now, ReceivedAt = now, UpdatedAt = now, Category = input.Category, Priority = priority,
            Progress = input.Progress, Actions = [.. input.Actions], Capabilities = input.Capabilities, Timing = input.Timing?.Copy()
        };
        _notices.Insert(0, notice);
        Prune(now, notify: false);
        Recount(); Changed?.Invoke(); Persist?.Invoke();
        return new(NoticeReceipt.Accepted, notice, !quiet);
    }

    // brim-notify stop: ends the earlier timer, stopwatch or event with the same key. Nothing new shows.
    private NoticeOutcome StopByKey(NoticeInput input, DateTimeOffset now)
    {
        var target = input.ReplaceKey is { Length: > 0 } key ? _notices.FirstOrDefault(n => n.AppKey == input.AppKey && n.ReplaceKey == key) : null;
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
            // A reminder moves the notice to the top, as an update does.
            _notices.Remove(notice); _notices.Insert(0, notice);
            if (rule is not { Enabled: false } && rule?.Muted != true && notice.Priority != NoticePriority.Low) shown.Add(notice);
        }
        if (changed) { Recount(); Changed?.Invoke(); Persist?.Invoke(); }
        return shown;
    }

    // Stops a stopwatch (keeping the time it ran) or cancels a countdown or event. False when there was nothing to stop.
    public bool StopTimer(string id, DateTimeOffset now)
    {
        if (Find(id) is not { Timing: { Stopped: false } timing } notice) return false;
        if (timing.Kind == NoticeTimeKind.Stopwatch && timing.EndsAt is null) timing.EndsAt = now;
        timing.Stopped = true;
        notice.UpdatedAt = now; notice.Revision++;
        Changed?.Invoke(); Persist?.Invoke();
        return true;
    }

    // A countdown that ended reminds again after the span.
    public bool Snooze(string id, TimeSpan span, DateTimeOffset now)
    {
        if (Find(id) is not { Timing: { Kind: NoticeTimeKind.Countdown, Native: true, Stopped: false, Stage: >= 1 } timing } notice) return false;
        timing.StartsAt = now; timing.EndsAt = now + span; timing.Stage = 0;
        notice.UpdatedAt = now; notice.Revision++;
        Changed?.Invoke(); Persist?.Invoke();
        return true;
    }

    // The source no longer has the notice (removed from the notification center, closed by its application). BrimDeck keeps
    // it among the recent notices; its source actions are no longer offered.
    public void SourceRemoved(string instanceKey)
    {
        if (FindInstance(instanceKey) is not { SourcePresent: true } notice) return;
        notice.SourcePresent = false; notice.Capabilities &= ~(NoticeCapabilities.Actions | NoticeCapabilities.ReportDismissal); notice.Revision++;
        Changed?.Invoke(); Persist?.Invoke();
    }

    public void MarkViewed(string id)
    {
        if (Find(id) is not { Viewed: false } notice) return;
        notice.Viewed = true; Recount(); Changed?.Invoke(); Persist?.Invoke();
    }
    public void MarkAllViewed()
    {
        if (UnviewedCount == 0) return;
        foreach (var notice in _notices) notice.Viewed = true;
        Recount(); Changed?.Invoke(); Persist?.Invoke();
    }
    public bool Delete(string id)
    {
        int index = _notices.FindIndex(n => n.Id == id);
        if (index < 0) return false;
        _notices.RemoveAt(index); Recount(); Changed?.Invoke(); Persist?.Invoke();
        return true;
    }
    // Clears BrimDeck's own list only; nothing is removed from Windows' notification center or any other source.
    public void Clear()
    {
        if (_notices.Count == 0) return;
        _notices.Clear(); Recount(); Changed?.Invoke(); Persist?.Invoke();
    }
    public void ForgetApps() { _apps.Clear(); Persist?.Invoke(); }

    // Drops notices older than the retention and beyond the count limit. Returns whether anything was dropped.
    public bool Prune(DateTimeOffset now, bool notify = true)
    {
        int before = _notices.Count;
        var limit = now - Retention;
        // A reminder BrimDeck still has to give (an event next week) and a stopwatch still running stay past the retention; the
        // count limit still applies.
        _notices.RemoveAll(n => n.ReceivedAt < limit && NoticeClock.NextDue(n.Timing) is null && !NoticeClock.IsLive(n.Timing, now));
        if (_notices.Count > MaxCount) _notices.RemoveRange(MaxCount, _notices.Count - MaxCount);
        foreach (var stale in _apps.Values.Where(app => app.LastSeen < now - TimeSpan.FromDays(30)).Select(app => app.Key).ToList()) _apps.Remove(stale);
        bool changed = _notices.Count != before;
        if (changed && notify) { Recount(); Changed?.Invoke(); Persist?.Invoke(); }
        return changed;
    }

    // Replaces the list with saved notices (see NoticeHistory). Notices from before are kept as not present at their sources:
    // BrimDeck cannot know what happened to them while it was not running.
    public void Load(IEnumerable<Notice> notices, IEnumerable<NoticeKnownApp> apps, DateTimeOffset now)
    {
        _notices.Clear();
        foreach (var notice in notices.Where(n => n is not null && n.Id.Length > 0).DistinctBy(n => n.Id).OrderByDescending(n => n.ReceivedAt))
        {
            // BrimDeck is the source of the timers it keeps; every other source may have changed meanwhile.
            notice.SourcePresent = notice.Timing is { Native: true };
            notice.Capabilities &= ~(NoticeCapabilities.Actions | NoticeCapabilities.ReportDismissal);
            notice.Title ??= ""; notice.Body ??= ""; notice.AppName ??= ""; notice.AppKey ??= ""; notice.Actions ??= [];
            _notices.Add(notice);
        }
        _apps.Clear();
        foreach (var app in apps.Where(a => a is not null && !string.IsNullOrEmpty(a.Key))) _apps[app.Key] = app;
        Prune(now, notify: false);
        Recount(); Changed?.Invoke();
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
        string title = input.Title.Trim(), body = input.Body.Trim();
        if (input.ExitCode is { } code)
        {
            string command = string.IsNullOrWhiteSpace(input.Command) ? Loc.T("命令", "Command") : input.Command!;
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
