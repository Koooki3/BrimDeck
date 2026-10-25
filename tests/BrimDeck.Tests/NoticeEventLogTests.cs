using BrimDeck.Core.Notices;

// The push notification log as the Windows source's trigger: which entries wake it, how a batch becomes one reading, which
// trigger is chosen and how often the list is read under each.
internal static class NoticeEventLogTests
{
    internal static void Run(Action<string, bool> check)
    {
        Filter(check);
        Query(check);
        Coalescing(check);
        Choice(check);
        Pace(check);
    }

    private static void Filter(Action<string, bool> check)
    {
        check("Event log: a delivered toast wakes the source", WindowsNoticeTriggers.Relevant(3052, null));
        check("Event log: cleared toasts wake the source", WindowsNoticeTriggers.Relevant(3055, null));
        check("Event log: a submitted toast wakes the source", WindowsNoticeTriggers.Relevant(3053, "toast") && WindowsNoticeTriggers.Relevant(3053, "Toast"));
        check("Event log: a submitted tile or badge does not", !WindowsNoticeTriggers.Relevant(3053, "tile") && !WindowsNoticeTriggers.Relevant(3053, "badge")
            && !WindowsNoticeTriggers.Relevant(3053, null));
        check("Event log: other entries do not", !WindowsNoticeTriggers.Relevant(3153, null) && !WindowsNoticeTriggers.Relevant(2418, "toast")
            && !WindowsNoticeTriggers.Relevant(3049, null));
    }

    private static void Query(Action<string, bool> check)
    {
        const string sid = "S-1-5-21-2134607150-862690008-3709084658-1001";
        var query = WindowsNoticeTriggers.Query(sid);
        check("Event log: the query asks for the three entries", query.Contains("EventID=3052") && query.Contains("EventID=3055") && query.Contains("EventID=3053"));
        check("Event log: the query keeps to this user", query.Contains($"Security[@UserID='{sid}']"));
        check("Event log: submitted entries only for toasts", query.Contains("Data[@Name='NotificationType']='toast'"));
        var loose = WindowsNoticeTriggers.Query("S-1-5-21' or '1'='1");
        check("Event log: a user id that is not a SID is left out, not quoted in", !loose.Contains("UserID") && !loose.Contains("'1'='1"));
        check("Event log: no user id means no user filter", !WindowsNoticeTriggers.Query(null).Contains("UserID"));
    }

    private static void Coalescing(Action<string, bool> check)
    {
        var coalescer = new WindowsNoticeCoalescer();
        bool first = coalescer.Arrive();
        int more = Enumerable.Range(0, 9).Count(_ => coalescer.Arrive());
        check("Event log: the first entry of a batch schedules a reading", first);
        check("Event log: the rest of the batch schedules nothing", more == 0);
        coalescer.Fired();
        check("Event log: an entry after the wake fired schedules another reading", coalescer.Arrive());

        // Entries from many threads at once still schedule exactly one wake.
        var parallel = new WindowsNoticeCoalescer();
        int scheduled = 0;
        Parallel.For(0, 1000, _ => { if (parallel.Arrive()) Interlocked.Increment(ref scheduled); });
        check("Event log: concurrent entries schedule one wake", scheduled == 1);
        check("Event log: coalescing waits briefly", WindowsNoticeTriggers.Coalesce >= TimeSpan.FromMilliseconds(50) && WindowsNoticeTriggers.Coalesce <= TimeSpan.FromMilliseconds(150));
    }

    private static void Choice(Action<string, bool> check)
    {
        check("Event log: a log that is turned on is used", WindowsNoticeTriggers.Choose(logEnabled: true, mayTry: true) == WindowsNoticeTrigger.EventLog);
        check("Event log: a disabled log falls back to polling", WindowsNoticeTriggers.Choose(false, true) == WindowsNoticeTrigger.Polling);
        check("Event log: a log waiting after failures is not tried", WindowsNoticeTriggers.Choose(true, false) == WindowsNoticeTrigger.Polling);

        // Failures injected one after another: the first few are tried again at once, then the wait doubles up to its limit.
        var t = new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
        var retry = new WindowsNoticeLogRetry();
        for (int i = 1; i < WindowsNoticeTriggers.MostFailures; i++) retry.Failed(t);
        check("Event log: the first failures are tried again at once", retry.MayTry(t));
        retry.Failed(t);
        check("Event log: after a few failures the source polls and waits", !retry.MayTry(t) && !retry.MayTry(t.AddSeconds(59)) && retry.MayTry(t.AddMinutes(1)));
        retry.Failed(t.AddMinutes(1));
        check("Event log: each further failure waits twice as long", !retry.MayTry(t.AddMinutes(2.9)) && retry.MayTry(t.AddMinutes(3)));
        for (int i = 0; i < 20; i++) retry.Failed(t);
        check("Event log: the wait stops growing at its limit", WindowsNoticeLogRetry.RetryAfter(100) == WindowsNoticeLogRetry.MostWait && retry.MayTry(t + WindowsNoticeLogRetry.MostWait));
        // A subscription that worked for a while starts the count over when it breaks.
        var lasting = new WindowsNoticeLogRetry();
        for (int i = 0; i < WindowsNoticeTriggers.MostFailures; i++) lasting.Failed(t);
        lasting.Started(t.AddMinutes(1));
        lasting.Failed(t.AddMinutes(1) + WindowsNoticeLogRetry.LastsFor);
        check("Event log: a break after working a while is tried again at once", lasting.Failures == 1 && lasting.MayTry(t.AddMinutes(1) + WindowsNoticeLogRetry.LastsFor));
        var brief = new WindowsNoticeLogRetry();
        for (int i = 0; i < WindowsNoticeTriggers.MostFailures; i++) brief.Failed(t);
        brief.Started(t.AddMinutes(1));
        brief.Failed(t.AddMinutes(2));
        check("Event log: a subscription that breaks again soon keeps waiting longer", brief.Failures == WindowsNoticeTriggers.MostFailures + 1 && !brief.MayTry(t.AddMinutes(3)));
    }

    private static void Pace(Action<string, bool> check)
    {
        TimeSpan quiet = TimeSpan.FromSeconds(30), safety = TimeSpan.FromSeconds(60), polling = TimeSpan.FromSeconds(3);
        check("Event log: with the log only the safety reading runs on a timer", WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.EventLog, false, quiet, safety, polling) == safety);
        check("Event log: polling keeps its own pace", WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.Polling, false, quiet, safety, polling) == polling);
        check("Event log: quiet wins for every trigger", WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.EventLog, true, quiet, safety, polling) == quiet
            && WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.Polling, true, quiet, safety, polling) == quiet);
    }
}
