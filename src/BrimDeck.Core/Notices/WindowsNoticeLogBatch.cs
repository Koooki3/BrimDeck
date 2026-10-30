namespace BrimDeck.Core.Notices;

public enum WindowsNoticeLogEventKind { Ignored, Notification, Reconcile }

// AppUserModelId is carried with the TrackingId and verified against the returned listener item before delivery.
public readonly record struct WindowsNoticeLogEvent(
    WindowsNoticeLogEventKind Kind, uint? NotificationId, string? AppUserModelId, bool ImmediateReconcile)
{
    public static WindowsNoticeLogEvent Ignored => new(WindowsNoticeLogEventKind.Ignored, null, null, false);
    public static WindowsNoticeLogEvent Reconcile => new(WindowsNoticeLogEventKind.Reconcile, null, null, false);
    public static WindowsNoticeLogEvent Fallback => new(WindowsNoticeLogEventKind.Reconcile, null, null, true);
}

public readonly record struct WindowsNoticeLogBatchSnapshot(int Ids, bool Reconcile, bool ImmediateReconcile, long ReconcileVersion);

// Coalesces duplicate event ids and limits memory if Windows sends a large batch. Only the source loop consumes ids;
// log callbacks may add more while a reading is in progress.
public sealed class WindowsNoticeLogBatch
{
    public const int MaximumIds = 256;
    public static readonly TimeSpan FailedReadReconcileInterval = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private readonly Dictionary<uint, string?> _ids = [];
    // Ids the log named for two different applications: neither is trusted, and later entries for them wait for the full
    // reading that settles them.
    private readonly HashSet<uint> _ambiguous = [];
    private long _nextVersion, _reconcileVersion, _immediateVersion;

    public bool Add(WindowsNoticeLogEvent entry)
    {
        if (entry.Kind == WindowsNoticeLogEventKind.Ignored) return false;
        lock (_gate)
        {
            if (entry.Kind == WindowsNoticeLogEventKind.Reconcile)
            {
                RequestReconcileLocked(entry.ImmediateReconcile);
                return true;
            }

            if (entry.NotificationId is not { } id || id == 0 || string.IsNullOrWhiteSpace(entry.AppUserModelId))
            {
                RequestReconcileLocked(immediate: true);
                return true;
            }
            if (_ambiguous.Contains(id)) return true;
            if (_ids.TryGetValue(id, out var knownApp))
            {
                if (!string.Equals(knownApp, entry.AppUserModelId, StringComparison.OrdinalIgnoreCase))
                {
                    _ids.Remove(id);
                    _ambiguous.Add(id);
                    RequestReconcileLocked(immediate: true);
                }
                return true;
            }
            if (_ids.Count >= MaximumIds)
            {
                RequestReconcileLocked(immediate: true);
                return true;
            }
            _ids.Add(id, entry.AppUserModelId);
            return true;
        }
    }

    public bool HasIds { get { lock (_gate) return _ids.Count > 0; } }

    // The oldest queued id: Windows numbers notifications in the order it hands them out, so a burst is read and shown in
    // that order whatever order the dictionary keeps.
    public bool TryNext(out uint id, out string? appUserModelId)
    {
        lock (_gate)
        {
            id = 0; appUserModelId = null;
            if (_ids.Count == 0) return false;
            id = _ids.Keys.Min();
            appUserModelId = _ids[id];
            return true;
        }
    }

    public WindowsNoticeLogBatchSnapshot Snapshot()
    {
        lock (_gate) return new(_ids.Count, _reconcileVersion != 0, _immediateVersion != 0, _reconcileVersion);
    }

    public static bool ReconcileDue(WindowsNoticeLogBatchSnapshot batch, DateTimeOffset now, DateTimeOffset lastFull, TimeSpan minimumInterval)
        => batch.Reconcile && now - lastFull >= (batch.ImmediateReconcile ? FailedReadReconcileInterval : minimumInterval);

    public void CompleteIds(params uint[] ids)
    {
        lock (_gate) foreach (var id in ids) _ids.Remove(id);
    }

    // A full read only clears reconciliation work that began before it. Events arriving during that read stay pending.
    public void CompleteReconcile(long version)
    {
        if (version == 0) return;
        lock (_gate)
        {
            if (_immediateVersion != 0 && _immediateVersion <= version) _immediateVersion = 0;
            if (_reconcileVersion != version) return;
            _reconcileVersion = 0;
            _ambiguous.Clear();
        }
    }

    public void RequestReconcile(bool immediate = false)
    {
        lock (_gate) RequestReconcileLocked(immediate);
    }

    // B: an id the log named could not be read (Windows wrote the entry before the listener can return the notification). The
    // reading that settles it follows within FailedReadReconcileInterval, as for any entry that could not be trusted, unless a
    // reconciliation is already pending (a clear entry in the same batch: QQ clears its previous toast with every message): it
    // rides with that one, and a busy chat is not read once a second.
    public void RequestReconcileForMissing()
    {
        lock (_gate) RequestReconcileLocked(immediate: _reconcileVersion == 0);
    }

    // C: access was lost; what is queued came before the Start baseline that follows, which takes in what is listed.
    public void Reset()
    {
        lock (_gate) { _ids.Clear(); _ambiguous.Clear(); _reconcileVersion = _immediateVersion = 0; }
    }

    private void RequestReconcileLocked(bool immediate)
    {
        _reconcileVersion = ++_nextVersion;
        if (immediate) _immediateVersion = _reconcileVersion;
    }
}

public readonly record struct WindowsNoticeNotificationRead(bool Found, string? AppUserModelId, NoticeInput? Input)
{
    public static WindowsNoticeNotificationRead Missing => new(false, null, null);
}

// The outcome of reading one notification the log named. Observed is false when it was already known, gone or not trusted.
public sealed record WindowsNoticeLogReadResult(uint Id, bool Observed, NoticeInput? Input, bool Reconcile, bool ImmediateReconcile)
{
    private static readonly WindowsNoticeList.Changes None = new([], [], []);

    public WindowsNoticeList.Changes Apply(WindowsNoticeList list) => Observed ? list.Observe(Id, Input) : None;
}

// Reads the notification a log entry named through the injected lookup. It accepts only a matching source identity and
// falls back to a full read on any other uncertainty. A TimeoutException (Windows did not answer) is passed on: the loop
// treats it as a failure and starts over with a new listener (WindowsNoticeLoop.Step).
public static class WindowsNoticeLogReader
{
    public static WindowsNoticeLogReadResult Read(uint id, string? expectedApp, bool known, Func<uint, WindowsNoticeNotificationRead> read)
    {
        if (known) return new(id, false, null, false, false);
        WindowsNoticeNotificationRead item;
        try { item = read(id); }
        catch (Exception ex) when (ex is not TimeoutException) { return new(id, false, null, true, true); }

        if (!item.Found) return new(id, false, null, true, false);
        if (string.IsNullOrWhiteSpace(expectedApp) || string.IsNullOrWhiteSpace(item.AppUserModelId)
            || !string.Equals(expectedApp, item.AppUserModelId, StringComparison.OrdinalIgnoreCase))
            return new(id, false, null, true, true);
        return new(id, true, item.Input, false, false);
    }
}
