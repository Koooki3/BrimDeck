using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// A subscription to Windows' push notification log (WindowsNoticeTriggers): Arrived carries the toast id and app id for
// targeted reads, or a reconciliation request for clear/unusable entries. Broke is raised when the service stops delivering.
// Both are called on a thread-pool thread, never after Dispose.
internal sealed class WindowsNoticeLog : IDisposable
{
    private static readonly EventLogPropertySelector s_noticeData = new([
        "Event/EventData/Data[@Name='NotificationType']",
        "Event/EventData/Data[@Name='AppUserModelId']",
        "Event/EventData/Data[@Name='TrackingId']"]);
    private readonly EventLogWatcher _watcher;
    private readonly Action<WindowsNoticeLogEvent> _arrived;
    private readonly Action _broke;
    // Whether the subscription has started, and an error the log service reported while it was being made; both under _gate,
    // since that report comes on another thread and may race the end of Start.
    private readonly object _gate = new();
    private Exception? _startError;
    private volatile bool _closed, _started;

    private WindowsNoticeLog(Action<WindowsNoticeLogEvent> arrived, Action broke)
    {
        _arrived = arrived; _broke = broke;
        string? sid = null;
        try { using var identity = WindowsIdentity.GetCurrent(); sid = identity.User?.Value; }
        catch (System.Security.SecurityException) { }
        _watcher = new EventLogWatcher(new EventLogQuery(WindowsNoticeTriggers.Channel, PathType.LogName, WindowsNoticeTriggers.Query(sid)));
        _watcher.EventRecordWritten += Written;
    }

    // Whether the log is turned on. A turned-off log takes a subscription without complaint and then delivers nothing.
    public static bool Enabled()
    {
        try { using var config = new EventLogConfiguration(WindowsNoticeTriggers.Channel); return config.IsEnabled; }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException) { return false; }
    }

    // The subscription, or null when Windows refuses it. A refused subscription is reported to the handler while it is being
    // made, before Enabled returns, not by an exception.
    public static WindowsNoticeLog? Start(Action<WindowsNoticeLogEvent> arrived, Action broke)
    {
        WindowsNoticeLog? log = null;
        try
        {
            log = new WindowsNoticeLog(arrived, broke);
            log._watcher.Enabled = true;
            lock (log._gate) if (log._startError is null) { log._started = true; return log; }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException or ArgumentException) { }
        log?.Dispose();
        return null;
    }

    private void Written(object? sender, EventRecordWrittenEventArgs e)
    {
        if (_closed) return;
        if (e.EventException is { } error)
        {
            lock (_gate) if (!_started) { _startError = error; return; }
            _broke();
            return;
        }
        using var record = e.EventRecord;
        if (record is null) return;

        WindowsNoticeLogEvent change;
        if (record.Id == WindowsNoticeTriggers.Cleared)
            change = WindowsNoticeLogEvent.Reconcile;
        else if (record is EventLogRecord entry)
        {
            string? type = null, appUserModelId = null;
            object? trackingId = null;
            try
            {
                var values = entry.GetPropertyValues(s_noticeData);
                if (values.Count > 0) type = values[0] as string;
                if (values.Count > 1) appUserModelId = values[1] as string;
                if (values.Count > 2) trackingId = values[2];
            }
            catch (Exception ex) when (ex is EventLogException or InvalidOperationException or UnauthorizedAccessException) { }
            change = WindowsNoticeTriggers.Parse(record.Id, type, appUserModelId, trackingId);
        }
        else return;

        if (change.Kind != WindowsNoticeLogEventKind.Ignored && !_closed) _arrived(change);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _watcher.EventRecordWritten -= Written;
        try { _watcher.Enabled = false; } catch (Exception ex) when (ex is EventLogException or InvalidOperationException) { }
        _watcher.Dispose();
    }
}
