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

    // The ids a probe looks up after newest, in order (WindowsNoticeList.SkippedProbe): the next one, and for WideFor after a
    // notice skipped it, the next WideIds. Each lookup costs about as much as the next (some ms of CPU), so the wide probe stops
    // at the first hit and only runs while an application is seen to skip ids.
    public static readonly TimeSpan WideFor = TimeSpan.FromMinutes(10);
    public const int WideIds = 3;

    public static IEnumerable<uint> ProbeIds(uint newest, DateTimeOffset now, DateTimeOffset wideUntil)
    {
        int count = now < wideUntil ? WideIds : 1;
        for (uint i = 1; i <= count && newest <= uint.MaxValue - i; i++) yield return newest + i;
    }

    // A reading of the list that Windows never finishes (seen after waking) blocks new ones, but not for ever (audit L4): once
    // the oldest still out has hung for StuckAfter, one more may start, up to MostOutstanding hung readings in all.
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);
    public const int MostOutstanding = 2;
    // A reading still out after this is no longer counted: two hung readings would otherwise block the source for good again.
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(30);

    public static bool MayRead(IEnumerable<DateTimeOffset> outstanding, DateTimeOffset now)
    {
        var started = outstanding.Where(at => now - at < ForgetAfter).ToList();
        return started.Count == 0 || started.Count < MostOutstanding && started.All(at => now - at >= StuckAfter);
    }

    public static TimeSpan Retry(int failures)
        => failures <= 1 ? FirstRetry : TimeSpan.FromTicks(Math.Min(LongestRetry.Ticks, FirstRetry.Ticks << Math.Min(failures - 1, 16)));
}
