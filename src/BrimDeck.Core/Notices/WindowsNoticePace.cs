namespace BrimDeck.Core.Notices;

// How often WindowsNoticeSource reads Windows' list while it cannot rely on its probe, and how long it waits after a failure.
public static class WindowsNoticePace
{
    // win2 C-L8: BrimDeck is unpackaged, so Windows raises no change event and the first notice after starting waits for a full
    // reading. Those come every FirstUnknownWait (as before perf-6) until nothing has happened for QuietAfter: no notice, no
    // waking, unlocking or refresh, no pointer on the island. Only then does the reading back off (WindowsNoticeList.UnknownWait).
    public static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(5);

    public static TimeSpan Unconfirmed(DateTimeOffset now, DateTimeOffset lastActivity, TimeSpan quietWait)
        => now - lastActivity < QuietAfter ? WindowsNoticeList.FirstUnknownWait : quietWait;

    // win2 P2: a source that keeps failing (a listener stuck after waking, COM errors) waits twice as long after each failure
    // in a row, from FirstRetry up to LongestRetry; a step that succeeds starts over.
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2.5), LongestRetry = TimeSpan.FromSeconds(60);

    public static TimeSpan Retry(int failures)
        => failures <= 1 ? FirstRetry : TimeSpan.FromTicks(Math.Min(LongestRetry.Ticks, FirstRetry.Ticks << Math.Min(failures - 1, 16)));
}
