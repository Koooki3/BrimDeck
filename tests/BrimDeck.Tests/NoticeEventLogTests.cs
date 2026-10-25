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
        check("Event log: identity's change event wins", WindowsNoticeTriggers.Choose(identityEvents: true, logEnabled: true, logFailures: 0) == WindowsNoticeTrigger.Events);
        check("Event log: without identity the log is used", WindowsNoticeTriggers.Choose(false, true, 0) == WindowsNoticeTrigger.EventLog);
        check("Event log: a disabled log falls back to polling", WindowsNoticeTriggers.Choose(false, false, 0) == WindowsNoticeTrigger.Polling);
        check("Event log: a log that keeps failing falls back to polling",
            WindowsNoticeTriggers.Choose(false, true, WindowsNoticeTriggers.MostFailures - 1) == WindowsNoticeTrigger.EventLog
            && WindowsNoticeTriggers.Choose(false, true, WindowsNoticeTriggers.MostFailures) == WindowsNoticeTrigger.Polling);
    }

    private static void Pace(Action<string, bool> check)
    {
        TimeSpan quiet = TimeSpan.FromSeconds(30), safety = TimeSpan.FromSeconds(60), polling = TimeSpan.FromSeconds(3);
        check("Event log: with the log only the safety reading runs on a timer", WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.EventLog, false, quiet, safety, polling) == safety);
        check("Event log: the change event uses the same safety reading", WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.Events, false, quiet, safety, polling) == safety);
        check("Event log: polling keeps its own pace", WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.Polling, false, quiet, safety, polling) == polling);
        check("Event log: quiet wins for every trigger", WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.EventLog, true, quiet, safety, polling) == quiet
            && WindowsNoticeTriggers.Interval(WindowsNoticeTrigger.Polling, true, quiet, safety, polling) == quiet);
    }
}
