namespace BrimDeck.Core.Notices;

// What tells WindowsNoticeSource to read Windows' list. Windows raises its change event only for a process with package
// identity, which BrimDeck, a plain desktop program, does not have. Windows' push notification platform still writes an entry
// to its Operational event log for every toast it delivers to the current user (3052, with the toast's id as TrackingId; 3053
// on older builds) and when toasts are cleared (3055). Any user may subscribe to that log, so BrimDeck reads the list after
// each entry (EventLog), and only reads it on a timer (Polling) when the log is disabled, cannot be read, or the subscription
// fails. The log service hands entries over in
// batches about once a second, so a toast is seen 30 ms to 1 s after it was shown (measured on Windows 11 26300).
public enum WindowsNoticeTrigger
{
    Polling,
    EventLog
}

public static class WindowsNoticeTriggers
{
    public const string Channel = "Microsoft-Windows-PushNotification-Platform/Operational";
    public const int Delivered = 3052, Submitted = 3053, Cleared = 3055;
    // Entries come in batches (ten toasts shown at once give ten entries within a few ms): one reading covers them all.
    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(100);
    // A subscription that keeps failing gives way to polling; it is tried again later (WindowsNoticeLogRetry).
    public const int MostFailures = 3;

    // The log query: toasts delivered to or cleared for this user only. Another signed-in user's toasts would only cause a
    // reading that finds nothing new. A user id that is not a plain SID is left out of the query rather than quoted into it.
    public static string Query(string? userSid)
    {
        string user = userSid is not null && IsSid(userSid) ? $" and Security[@UserID='{userSid}']" : "";
        return $"*[System[(EventID={Delivered} or EventID={Cleared}){user}] or (System[EventID={Submitted}{user}] and EventData[Data[@Name='NotificationType']='toast'])]";
    }

    // The same filter applied to an entry as it arrives, in case the log service passes on more than was asked for.
    public static bool Relevant(int eventId, string? notificationType)
        => eventId is Delivered or Cleared || eventId == Submitted && string.Equals(notificationType, "toast", StringComparison.OrdinalIgnoreCase);

    // The log while it is turned on and may be tried now (WindowsNoticeLogRetry.MayTry); polling otherwise.
    public static WindowsNoticeTrigger Choose(bool logEnabled, bool mayTry)
        => logEnabled && mayTry ? WindowsNoticeTrigger.EventLog : WindowsNoticeTrigger.Polling;

    // How long a full reading may wait while nothing arrives: a trigger that reports every change only needs the slow safety
    // reading, in case one was missed.
    public static TimeSpan Interval(WindowsNoticeTrigger trigger, bool quiet, TimeSpan quietInterval, TimeSpan safetyInterval, TimeSpan pollingInterval)
        => quiet ? quietInterval : trigger == WindowsNoticeTrigger.Polling ? pollingInterval : safetyInterval;

    private static bool IsSid(string sid)
        => sid.Length is > 4 and <= 184 && sid.StartsWith("S-1-", StringComparison.Ordinal) && sid.Skip(1).All(c => c == '-' || char.IsAsciiDigit(c));
}

// When the push notification log may be subscribed to again (WindowsNoticeSource). The first MostFailures failures in a row
// are tried again at once; after that the source polls and tries the log again after a wait that doubles from a minute up to
// MostWait, so a log that came back (turned on again, its service restarted) is used again. A subscription that lasted
// LastsFor counts as working: its failure starts the count over. Used from the source's loop only.
public sealed class WindowsNoticeLogRetry
{
    public static readonly TimeSpan LastsFor = TimeSpan.FromMinutes(10), MostWait = TimeSpan.FromMinutes(30);
    private DateTimeOffset _started = DateTimeOffset.MaxValue;
    public int Failures { get; private set; }
    public DateTimeOffset NextTry { get; private set; } = DateTimeOffset.MinValue;

    public bool MayTry(DateTimeOffset now) => Failures < WindowsNoticeTriggers.MostFailures || now >= NextTry;
    public void Started(DateTimeOffset now) => _started = now;

    public void Failed(DateTimeOffset now)
    {
        if (_started != DateTimeOffset.MaxValue && now - _started >= LastsFor) Failures = 0;
        _started = DateTimeOffset.MaxValue;
        Failures++;
        NextTry = now + RetryAfter(Failures);
    }

    public static TimeSpan RetryAfter(int failures)
        => failures < WindowsNoticeTriggers.MostFailures ? TimeSpan.Zero
            : TimeSpan.FromMinutes(Math.Min(MostWait.TotalMinutes, Math.Pow(2, Math.Min(failures - WindowsNoticeTriggers.MostFailures, 10))));
}

// Turns a run of log entries into one reading: the first entry asks for a wake after WindowsNoticeTriggers.Coalesce, later
// ones before it fires ask for nothing. An entry after it fired asks again, so a toast arriving while the list is being read
// is read once more. Thread-safe: entries arrive on thread-pool threads.
public sealed class WindowsNoticeCoalescer
{
    private int _pending;

    // True when the caller should schedule the wake.
    public bool Arrive() => Interlocked.Exchange(ref _pending, 1) == 0;

    // Called when the scheduled wake fires, before the reading starts.
    public void Fired() => Volatile.Write(ref _pending, 0);
}
