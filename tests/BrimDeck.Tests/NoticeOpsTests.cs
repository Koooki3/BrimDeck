using BrimDeck.Core.Notices;

// Follow-up work on the notices after the audits: how the Windows source probes for new notices when tiles and badges take
// ids from the same count.
internal static class NoticeOpsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    internal static void Run(Action<string, bool> check)
    {
        Probe(check);
        check("Notices ops: Arabic and Hebrew texts read right to left", NoticeText.RightToLeft("مرحبا بالعالم") && NoticeText.RightToLeft("שלום עולם")
            && NoticeText.RightToLeft("123 - مرحبا"));
        check("Notices ops: Latin, Chinese and texts without letters read left to right", !NoticeText.RightToLeft("Hello مرحبا") && !NoticeText.RightToLeft("你好 שלום")
            && !NoticeText.RightToLeft("12345 !?") && !NoticeText.RightToLeft(""));
        Attention(check);
        ProgressOrder(check);
    }

    private static AttentionItem Item(string key, string group = "g", bool refresh = false, bool urgent = false, double seconds = 6) => new()
    {
        Lane = "main", Kind = AttentionKind.Notice, Key = key, Group = group, Refresh = refresh, Urgent = urgent,
        Priority = urgent ? NoticePriority.High : NoticePriority.Normal, Duration = TimeSpan.FromSeconds(seconds)
    };

    private static void Attention(Action<string, bool> check)
    {
        // Review 2: a task's progress keeps the island at most twice the notice's duration.
        var coordinator = new AttentionCoordinator();
        coordinator.Submit(Item("task"), T0);
        var t = T0;
        // As NoticeCenter.RefreshShown: progress is passed on only while the notice shows.
        for (int i = 0; i < 100; i++) { t = t.AddSeconds(1); if (coordinator.Current("main") is not null) coordinator.Submit(Item("task", refresh: true), t); coordinator.Tick(t); }
        check("Notices ops: progress does not keep the island for as long as the task runs", coordinator.Current("main") is null);
        var held = new AttentionCoordinator();
        held.Submit(Item("task"), T0);
        held.Submit(Item("task", refresh: true), T0.AddSeconds(5));
        held.Tick(T0.AddSeconds(6));
        check("Notices ops: progress near the end still keeps it a little longer", held.Current("main")?.Key == "task");

        // Review 3: a timer that comes due while its own "started" notice shows rings anew, urgent and for its whole duration.
        var timer = new AttentionCoordinator();
        timer.Submit(Item("timer", seconds: 6), T0);
        var rang = timer.Submit(Item("timer", urgent: true, seconds: 8), T0.AddSeconds(5));
        check("Notices ops: a timer due while its start notice shows starts again as urgent", timer.Current("main") is { Urgent: true } ringing
            && rang.Any(c => c.Started is { Urgent: true }) && rang.Any(c => c.Ended is { Urgent: false }));
        timer.Tick(T0.AddSeconds(12));
        check("Notices ops: the ringing timer stays its whole duration", timer.Current("main")?.Key == "timer");
        var hovered = new AttentionCoordinator();
        hovered.Submit(Item("timer"), T0);
        hovered.Hold("main", true, T0.AddSeconds(1));
        hovered.Submit(Item("timer", urgent: true), T0.AddSeconds(2));
        check("Notices ops: a card the user holds changes in place when its timer rings", hovered.Current("main") is { Key: "timer", Urgent: false });
        var spent = new AttentionCoordinator();
        for (int i = 0; i < AttentionCoordinator.UrgentLimit; i++) { spent.Submit(Item("a" + i, urgent: true), T0.AddSeconds(i * 10)); spent.Dismiss("main", T0.AddSeconds(i * 10 + 1)); }
        spent.Submit(Item("timer"), T0.AddSeconds(40));
        spent.Submit(Item("timer", urgent: true), T0.AddSeconds(41));
        check("Notices ops: a timer past its urgent limit keeps its card instead of leaving the island empty", spent.Current("main")?.Key == "timer");
        var queued = new AttentionCoordinator();
        queued.Submit(Item("other", group: "o"), T0);
        queued.Submit(Item("timer", seconds: 6), T0);
        queued.Submit(Item("timer", urgent: true), T0.AddSeconds(1));
        check("Notices ops: a waiting start notice gives way to its ringing", queued.Queued("main").All(q => q.Key != "timer" || q.Urgent));
        var burst = new AttentionCoordinator();
        burst.Submit(Item("a", group: "cli"), T0);
        burst.Submit(Item("b", group: "cli", urgent: true), T0.AddMilliseconds(100));
        check("Notices ops: an alarm is not merged into a burst of ordinary notices", burst.Current("main")?.Key == "b" && burst.Current("main")!.Count == 1);
    }

    private static void ProgressOrder(Action<string, bool> check)
    {
        // Review 4: progress alone does not move a notice to the top of the recent list.
        var hub = new NoticeHub();
        NoticeInput Task(string key, double progress) => new()
        {
            Origin = NoticeOrigin.Cli, AppKey = "cli:build", AppName = "build", InstanceKey = "cli:" + key, ReplaceKey = key, Title = key, Progress = progress
        };
        hub.Receive(Task("a", .1), T0);
        hub.Receive(Task("b", .1), T0.AddSeconds(1));
        var step = hub.Receive(Task("a", .5), T0.AddSeconds(2));
        check("Notices ops: a progress step keeps the list's order", step.Receipt == NoticeReceipt.Updated && hub.Notices[0].Title == "b" && hub.Notices[1].Title == "a"
            && hub.Notices[1].ReceivedAt == T0 && hub.Notices[1].UpdatedAt == T0.AddSeconds(2));
        hub.Receive(Task("a", 1), T0.AddSeconds(3));
        check("Notices ops: a finished task moves to the top", hub.Notices[0].Title == "a" && hub.Notices[0].ReceivedAt == T0.AddSeconds(3));

        // Review 9: pruning keeps the index right without rebuilding it.
        var full = new NoticeHub { MaxCount = 3 };
        var ids = new List<string>();
        for (int i = 0; i < 6; i++)
            ids.Add(full.Receive(new NoticeInput { Origin = NoticeOrigin.Cli, AppKey = "cli:x", AppName = "x", InstanceKey = "cli:n" + i, Title = "n" + i }, T0.AddSeconds(i)).Notice!.Id);
        check("Notices ops: notices dropped over the limit leave the index, kept ones stay", full.Notices.Count == 3
            && ids.Take(3).All(id => full.Find(id) is null) && ids.Skip(3).All(id => full.Find(id) is not null));
        full.Prune(T0.AddDays(2));
        check("Notices ops: notices past the retention leave the index", full.Notices.Count == 0 && ids.All(id => full.Find(id) is null));
    }

    private static NoticeInput Toast(uint id) => new()
    {
        Origin = NoticeOrigin.Windows, AppKey = "windows:App", AppName = "App", InstanceKey = "w:" + id, Title = "t" + id, CreatedAt = T0.AddSeconds(id)
    };

    private static void Probe(Action<string, bool> check)
    {
        check("Notices ops: the probe looks at the next id only", WindowsNoticePace.ProbeIds(100, T0, DateTimeOffset.MinValue).SequenceEqual([101u]));
        check("Notices ops: after a skipped id the probe looks a few ids ahead", WindowsNoticePace.ProbeIds(100, T0, T0.AddMinutes(1)).SequenceEqual([101u, 102u, 103u]));
        check("Notices ops: the wide probe ends", WindowsNoticePace.ProbeIds(100, T0.AddMinutes(1), T0.AddMinutes(1)).SequenceEqual([101u]));
        check("Notices ops: the probe never wraps past the largest id", WindowsNoticePace.ProbeIds(uint.MaxValue - 1, T0, T0.AddMinutes(1)).SequenceEqual([uint.MaxValue]));

        var list = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        list.Compare([10u], id => Toast(id));
        check("Notices ops: the first reading says nothing about skipped ids", !list.SkippedProbe);
        list.Compare([10u, 11u], id => Toast(id));
        check("Notices ops: a notice right after the newest skips nothing", !list.SkippedProbe && list.Confirmed && list.Newest == 11);
        list.Compare([10u, 11u, 13u], id => Toast(id));
        check("Notices ops: a notice past a taken id is seen to skip it", list.SkippedProbe && list.Newest == 13);
        list.Compare([10u, 11u, 13u], id => Toast(id));
        check("Notices ops: a reading with nothing new clears it", !list.SkippedProbe);
        list.Compare([10u, 11u, 13u, 14u, 16u], id => Toast(id));
        check("Notices ops: two notices in a row starting at the next id skip nothing", !list.SkippedProbe);

        var unconfirmed = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        unconfirmed.Compare([10u], id => Toast(id));
        unconfirmed.Compare([10u, 20u], id => Toast(id));
        check("Notices ops: before the newest id is confirmed a gap means nothing", !unconfirmed.SkippedProbe);
        list.ExpectBaseline(WindowsNoticeBaseline.Resume);
        list.Compare([10u, 11u, 13u, 14u, 16u, 30u], id => Toast(id));
        check("Notices ops: what arrived during sleep is no skipped id", !list.SkippedProbe);

        check("Notices ops: a reading may start when none is out", WindowsNoticePace.MayRead([], T0));
        check("Notices ops: a reading waits for one still out", !WindowsNoticePace.MayRead([T0.AddMinutes(-1)], T0));
        check("Notices ops: a reading hung for minutes no longer blocks (L4)", WindowsNoticePace.MayRead([T0 - WindowsNoticePace.StuckAfter], T0));
        check("Notices ops: hung readings never pile up", !WindowsNoticePace.MayRead([T0.AddMinutes(-25), T0.AddMinutes(-10)], T0)
            &&!WindowsNoticePace.MayRead([T0.AddMinutes(-20), T0.AddMinutes(-1)], T0.AddMinutes(1)));
        check("Notices ops: readings hung for half an hour no longer count", WindowsNoticePace.MayRead([T0.AddHours(-2), T0.AddHours(-1)], T0));
    }
}
