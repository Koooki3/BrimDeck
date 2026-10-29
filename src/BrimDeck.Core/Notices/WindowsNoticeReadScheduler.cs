namespace BrimDeck.Core.Notices;

// The converted result of one complete Windows notification-list read. Conversion happens with the read on a worker so the
// source loop can keep servicing the event log while Windows is slow.
public sealed record WindowsNoticeReadSnapshot(
    IReadOnlyList<uint> NotificationIds,
    IReadOnlyDictionary<uint, NoticeInput?> Inputs);

// What a full read was started against: the list's observation generation (WindowsNoticeList.BeginRead), the run's result
// generation (raised by Refresh and Detach, which make the result stale), the Refresh it covers and the reconciliation
// requests it settles (WindowsNoticeLogBatch.CompleteReconcile).
public readonly record struct WindowsNoticeReadStamp(long ObservationGeneration, long ResultGeneration, int Covers, long ReconcileVersion);

public readonly record struct WindowsNoticeReadCompletion<T>(T? Snapshot, WindowsNoticeReadStamp Stamp, Exception? Error)
{
    // Refresh and comparison share the caller's gate. A result taken from the slot may still become stale before it is applied.
    public bool TryApply(object gate, Func<bool> current, Action<T> apply)
    {
        lock (gate)
        {
            if (Error is not null || Snapshot is not { } snapshot || !current()) return false;
            apply(snapshot);
            return true;
        }
    }
}

// Keeps at most one full-list read outstanding. A stale read keeps the slot: an unresponsive WinRT read must finish (or hit
// its timeout) before another full read can begin.
public sealed class WindowsNoticeReadScheduler<T>
{
    private readonly object _gate = new();
    private Task<T>? _task;
    private WindowsNoticeReadStamp _stamp;

    public bool IsRunning { get { lock (_gate) return _task is not null; } }

    public Task<T>? Start(WindowsNoticeReadStamp stamp, Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_task is not null) return null;
            _stamp = stamp;
            try { _task = read(cancellationToken) ?? Task.FromException<T>(new InvalidOperationException("The full-read factory returned no task.")); }
            catch (Exception ex) { _task = Task.FromException<T>(ex); }
            return _task;
        }
    }

    public bool TryTakeCompleted(out WindowsNoticeReadCompletion<T> completion)
    {
        Task<T> task;
        WindowsNoticeReadStamp stamp;
        lock (_gate)
        {
            if (_task is not { IsCompleted: true } finished)
            {
                completion = default;
                return false;
            }
            task = finished;
            stamp = _stamp;
            _task = null;
        }

        if (task.IsCanceled)
            completion = new(default, stamp, new OperationCanceledException("The full notification read was cancelled."));
        else if (task.IsFaulted)
            completion = new(default, stamp, task.Exception?.GetBaseException() ?? new InvalidOperationException("The full notification read failed."));
        else
            completion = new(task.GetAwaiter().GetResult(), stamp, null);
        return true;
    }
}
