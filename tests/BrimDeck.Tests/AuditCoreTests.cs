using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

// The fixes of the 2026-10-24 audit in the notices core (group "core"): retention after reminders, instance matching,
// history file states and moves, rate limits for urgent notices, text cleaning. Fixed clocks only.
internal static class AuditCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 6, 0, 0, TimeSpan.Zero);

    public static void Run(string root, Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try
        {
            Retention(check);
            Instances(check);
            LateAndMissed(check);
            Pending(check);
            Persisting(check);
            Loading(check);
            Urgent(check);
            Summary(check);
            Text(check);
            HistoryFile(root, check);
            Plan(check);
        }
        finally { Loc.Use(language); }
    }

    private static NoticeInput Cli(string name, string? eventId, string title = "t", NoticeTiming? timing = null) => NoticeIntake.FromPipe(
        new NoticeMessage { AppName = name, EventId = eventId, Title = title }, T0) with { Timing = timing };

    private static NoticeInput Wsl(string app, string native, string title = "t") => new()
    {
        Origin = NoticeOrigin.Wsl, AppKey = "wsl:Ubuntu:" + app, AppName = app, Distro = "Ubuntu", InstanceKey = "wsl:Ubuntu:server:" + native, Title = title
    };

    private static void Retention(Action<string, bool> check)
    {
        var hub = new NoticeHub { Retention = TimeSpan.FromHours(1) };
        var watch = hub.Receive(Cli("w", "w1", timing: new NoticeTiming { Kind = NoticeTimeKind.Stopwatch, StartsAt = T0, Native = true }), T0).Notice!;
        hub.Prune(T0.AddDays(2));
        hub.StopTimer(watch.Id, T0.AddDays(2));
        hub.Prune(T0.AddDays(2).AddMinutes(1));
        check("core-H1: a stopwatch stopped after days stays for the retention from when it stopped", hub.Find(watch.Id) is not null);
        hub.Prune(T0.AddDays(2) + hub.Retention + TimeSpan.FromSeconds(1));
        check("core-H1: and then ages out", hub.Find(watch.Id) is null);

        var timer = hub.Receive(Cli("c", "c1", timing: new NoticeTiming { Kind = NoticeTimeKind.Countdown, StartsAt = T0, EndsAt = T0.AddHours(2), Native = true }), T0).Notice!;
        var rang = hub.FireDue(T0.AddHours(2));
        hub.Prune(T0.AddHours(2).AddMinutes(30));
        check("core-H1: a two-hour timer that rang is kept (its snooze too) under a one-hour retention", rang.Contains(timer) && hub.Find(timer.Id) is not null
            && timer.ReceivedAt == T0.AddHours(2) && hub.Snooze(timer.Id, NoticeClock.Snooze, T0.AddHours(2).AddMinutes(30)));
    }

    private static void Instances(Action<string, bool> check)
    {
        var hub = new NoticeHub();
        var a = hub.Receive(Cli("A", "job1", "构建完成"), T0).Notice!;
        var b = hub.Receive(Cli("B", "job1", "部署失败"), T0.AddSeconds(1));
        check("core-M1: the same --id from another -s is another notice", b is { Receipt: NoticeReceipt.Accepted } && a.Title == "构建完成" && a.AppKey == "cli:A" && hub.Notices.Count == 2);
        var blank1 = NoticeIntake.FromPipe(new NoticeMessage { AppName = "A", EventId = "" });
        var blank2 = NoticeIntake.FromPipe(new NoticeMessage { AppName = "A", EventId = " " });
        check("core-M1: a blank --id is no id: each notice is its own", blank1.InstanceKey != blank2.InstanceKey && blank1.EventId is null && blank2.EventId is null);
        hub.Receive(blank1, T0.AddSeconds(2)); hub.Receive(blank2, T0.AddSeconds(3));
        check("core-M1: notices with blank ids do not replace each other", hub.Notices.Count == 4);

        var wsl = new NoticeHub();
        var thunderbird = wsl.Receive(Wsl("Thunderbird", "1", "昨天的邮件"), T0).Notice!;
        var slack = wsl.Receive(Wsl("Slack", "1", "新消息"), T0.AddMinutes(1));
        check("core-H2: the same notification number from another application is another notice", slack.Receipt == NoticeReceipt.Accepted
            && thunderbird.Title == "昨天的邮件" && slack.Notice!.AppKey == "wsl:Ubuntu:Slack");
        var update = wsl.Receive(Wsl("Slack", "1", "改了"), T0.AddMinutes(2));
        check("core-H2: a notification still present is updated by its number", update.Receipt == NoticeReceipt.Updated && update.Notice == slack.Notice);
        var restarted = new NoticeHub();
        restarted.Load(wsl.Notices.Select(n => n.Copy()), [], T0.AddMinutes(3));
        var again = restarted.Receive(Wsl("Thunderbird", "1", "今天的邮件"), T0.AddMinutes(4));
        check("core-H2: after a restart the same number names a new notification", again.Receipt == NoticeReceipt.Accepted && restarted.Notices.Count(n => n.AppName == "Thunderbird") == 2);

        var pipe = NoticeIntake.FromPipe(new NoticeMessage { Title = "x", Distro = @"..\server" });
        check("sec-L4: a pipe sender's distribution label must be a WSL name", pipe.Distro is null && pipe.AppKey == "cli:brim-notify");
    }

    private static void LateAndMissed(Action<string, bool> check)
    {
        var hub = new NoticeHub();
        var china = TimeZoneInfo.CreateCustomTimeZone("Test/China2", TimeSpan.FromHours(8), "Test", "Test");
        var past = NoticeIntake.FromPipe(new NoticeMessage { Kind = "event", Title = "站会", At = "2026-10-24 09:00" }, T0.AddHours(6), china);
        var outcome = hub.Receive(past, T0.AddHours(6));
        check("core-L1: an event entered long after its start is an ordinary notice", outcome.Notice!.Priority == NoticePriority.Normal
            && !NoticeClock.Alerting(outcome.Notice.Timing, T0.AddHours(6)) && NoticeClock.Alerting(outcome.Notice.Timing, outcome.Notice.Timing!.StartsAt!.Value.AddMinutes(5)));

        var missed = hub.Receive(new NoticeInput { Origin = NoticeOrigin.Windows, AppKey = "windows:Teams", AppName = "Teams", InstanceKey = "w9", Title = "离线消息", Missed = true }, T0);
        check("core-M5: a notification that arrived during sleep is recorded unviewed, not shown", missed is { Receipt: NoticeReceipt.Accepted, Present: false, Reason: "late" }
            && !missed.Notice!.Viewed);
    }

    private static void Pending(Action<string, bool> check)
    {
        var hub = new NoticeHub { MaxCount = 3 };
        var later = hub.Receive(Cli("x", "e", timing: new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = T0.AddDays(2), RemindAt = T0.AddDays(2), Native = true }), T0).Notice!;
        for (int i = 0; i < 5; i++) hub.Receive(Cli("x", "n" + i), T0.AddMinutes(i + 1));
        check("core-L2: the count limit keeps a reminder still to come", hub.Notices.Count == 3 && hub.Notices.Contains(later) && hub.Find(later.Id) == later);
        hub.ClearDone(T0.AddHours(1));
        check("core-L2: the scheduled clearing keeps it too", hub.Notices.SequenceEqual([later]));
        hub.Clear();
        check("Clearing by hand empties the list, and the index with it", hub.Notices.Count == 0 && hub.Find(later.Id) is null);
    }

    private static void Persisting(Action<string, bool> check)
    {
        var hub = new NoticeHub();
        var saves = new List<bool>();
        hub.Persist += minor => saves.Add(minor);
        var task = hub.Receive(Cli("make", null, "构建") with { ReplaceKey = "build", Progress = .1 }, T0).Notice!;
        hub.Receive(Cli("make", null, "构建") with { ReplaceKey = "build", Progress = .2 }, T0.AddSeconds(1));
        hub.MarkAllViewed();
        hub.Receive(Cli("make", null, "构建完成") with { ReplaceKey = "build" }, T0.AddSeconds(2));
        check("perf-7: progress and viewed marks may wait for a later save; a new notice and a finished task may not", saves.SequenceEqual([false, true, true, false])
            && hub.Notices.Single() == task);
    }

    private static void Loading(Action<string, bool> check)
    {
        var hub = new NoticeHub();
        var fresh = hub.Receive(Cli("a", "1", "新的"), T0.AddMinutes(5)).Notice!;
        var savedOld = new Notice { AppKey = "cli:a", AppName = "a", Title = "旧的", ReceivedAt = T0, UpdatedAt = T0 };
        var savedCopy = fresh.Copy(); savedCopy.Title = "磁盘上的旧版本";
        hub.Load([savedOld, savedCopy], [new NoticeKnownApp { Key = "cli:old", Name = "old", LastSeen = T0 }], T0.AddMinutes(6));
        check("perf-9: notices read later join those that arrived meanwhile, which win", hub.Notices.Count == 2 && hub.Notices[0] == fresh && fresh.Title == "新的"
            && hub.Find(savedOld.Id) == savedOld && !savedOld.SourcePresent && hub.KnownApps.Any(app => app.Key == "cli:old") && hub.KnownApps.Any(app => app.Key == "cli:a"));
    }

    private static AttentionItem Alarm(string key) => new()
    {
        // One script naming each timer differently (core2 S4: command-line senders share one urgent limit).
        Lane = "main", Kind = AttentionKind.Notice, Key = key, Group = "cli:g" + key, Priority = NoticePriority.High, Urgent = true, Duration = TimeSpan.FromSeconds(1)
    };

    private static void Urgent(Action<string, bool> check)
    {
        var c = new AttentionCoordinator();
        var now = T0;
        int shown = 0;
        for (int i = 0; i < 6; i++)
        {
            foreach (var change in c.Submit(Alarm("a" + i), now)) if (change.Started is not null) shown++;
            now += TimeSpan.FromSeconds(2);
            foreach (var change in c.Tick(now)) if (change.Started is not null) shown++;
        }
        check("sec-L5: a flood of alarms rings at most UrgentLimit times a minute", shown == AttentionCoordinator.UrgentLimit && c.Queued("main").Count == 3
            && c.NextDeadline(now) == T0 + AttentionCoordinator.UrgentWindow);
        var single = new AttentionCoordinator();
        for (int i = 0; i < AttentionCoordinator.RateLimit; i++) single.Submit(new AttentionItem { Lane = "x" + i, Kind = AttentionKind.Notice, Key = "n" + i, Duration = TimeSpan.FromSeconds(5) }, T0);
        check("sec-L5: a single real alarm still rings past the ordinary rate limit", single.Submit(Alarm("ring"), T0) is [{ Started.Key: "ring" }]);
    }

    private static void Summary(Action<string, bool> check)
    {
        var c = new AttentionCoordinator();
        c.SetSuppressed(true, T0, out _);
        var item = new AttentionItem { Lane = "main", Kind = AttentionKind.Notice, Key = "build", Duration = TimeSpan.FromSeconds(5) };
        c.Submit(item, T0); c.Submit(item, T0.AddSeconds(30));
        c.Submit(new AttentionItem { Lane = "main", Kind = AttentionKind.Notice, Key = "other", Duration = TimeSpan.FromSeconds(5) }, T0.AddSeconds(40));
        c.SetSuppressed(false, T0.AddMinutes(1), out int arrived);
        check("core-L7: a notice updated while quiet counts once in the summary", arrived == 2);
    }

    private static void Text(Action<string, bool> check)
    {
        check("sec-L4: format characters are dropped, line separators become breaks", NoticeText.Clean("a\u200Bb\u2066c\u2069\uFEFF\u2028d") == "abc\nd"
            && NoticeText.Clean("x\u2029y", singleLine: true) == "x y" && NoticeText.Clean("tag\U000E0041\U000E007F") == "tag");
        check("sec-L4: emoji sequences keep their joiner; plain text is unchanged", NoticeText.Clean("👩\u200D💻") == "👩\u200D💻" && NoticeText.Clean("普通 text") == "普通 text");
        var words = NoticeHub.Words(new NoticeInput { Origin = NoticeOrigin.Cli, AppKey = "cli:x", AppName = "x", InstanceKey = "i", Title = "Out\u2028look\u200B", Body = "b" });
        check("sec-L4: a title cannot be stretched to fake lines", words.Title == "Out look");
        check("sec-L4: application names are cleaned", NoticeIntake.FromPipe(new NoticeMessage { AppName = "Out\u200Blook" }).AppName == "Outlook"
            && NoticeIntake.FromPipe(new NoticeMessage { AppName = "\u200B" }).AppName == "brim-notify");
    }

    private static void HistoryFile(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "audit-history-" + Guid.NewGuid().ToString("N"));
        var history = new NoticeHistory(folder, new NoticeHistory.PlainProtector());
        check("core-M4: a new folder on a drive that exists is simply empty", history.Load(out _, out _) == NoticeHistoryState.Empty);
        Directory.CreateDirectory(folder);
        var planted = Path.Combine(folder, "notices.dat.tmp");
        File.WriteAllText(planted, "someone else's");
        var notice = new Notice { AppKey = "cli:a", Title = "x", ReceivedAt = T0, UpdatedAt = T0 };
        check("Save reports that it wrote", history.Save([notice], []));
        check("sec-M1: saving neither writes through a file at the old fixed name nor leaves a temporary file", File.ReadAllText(planted) == "someone else's"
            && Directory.EnumerateFiles(folder, ".notices.*.tmp").Count() == 0);
        check("A save skipped as unwanted reports so", !history.Save([notice], [], wanted: () => false));
        using (new FileStream(history.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            check("core-M4: a file in use is unavailable for now, not damaged", history.Load(out _, out _) == NoticeHistoryState.Unavailable);
        check("The file reads again once it is free", history.Load(out var back, out _) == NoticeHistoryState.Loaded && back.Single().Id == notice.Id);
        File.WriteAllText(Path.Combine(folder, ".notices.0123.tmp"), "left by a crash");
        history.Delete();
        check("Deleting also removes temporary files a crash left behind", !File.Exists(history.FilePath) && !Directory.EnumerateFiles(folder, ".notices.*.tmp").Any());

        using (var big = new FileStream(history.FilePath, FileMode.Create)) big.SetLength(NoticeHistory.MaxFileSize + 1);
        check("sec-L6: an oversized file is not read into memory", history.Load(out _, out _) == NoticeHistoryState.Unreadable);
        File.Delete(history.FilePath);

        var letter = "ZYXWVUTSRQPONMLKJIHGFED".FirstOrDefault(d => !Directory.Exists(d + @":\"));
        if (letter != default)
            check("core-M4: a folder whose drive is missing is unavailable, so nothing is written over its file",
                new NoticeHistory(letter + @":\notices", new NoticeHistory.PlainProtector()).Load(out _, out _) == NoticeHistoryState.Unavailable && !NoticeHistory.Reachable(letter + @":\notices"));
        check("Each state that keeps BrimDeck from writing says why", NoticeHistory.Describe(NoticeHistoryState.Unavailable).Length > 0 && NoticeHistory.Describe(NoticeHistoryState.Refused).Length > 0);
    }

    private static void Plan(Action<string, bool> check)
    {
        string a = @"C:\A", b = @"C:\B";
        check("life-L1: keeping switched off and on again writes the list again", NoticeHistoryPlan.For(a, a, true, false, true).SequenceEqual([NoticeHistoryStep.DeleteOld])
            && NoticeHistoryPlan.For(a, a, false, true, true).SequenceEqual([NoticeHistoryStep.Save]));
        check("life-L1, core-L4: a read list moves: the new folder's file is added, then written, then the old one goes",
            NoticeHistoryPlan.For(a, b, true, true, true).SequenceEqual([NoticeHistoryStep.Load, NoticeHistoryStep.Save, NoticeHistoryStep.DeleteOld]));
        check("life-L1: a file never read moves with the folder instead of being deleted", NoticeHistoryPlan.For(a, b, true, true, false).SequenceEqual([NoticeHistoryStep.MoveOld, NoticeHistoryStep.Load]));
        check("core-L4: a file that could not be read stays where it is", NoticeHistoryPlan.For(a, b, true, true, null).SequenceEqual([NoticeHistoryStep.Load]));
        check("life-L1: changing the folder while not keeping writes nothing", !NoticeHistoryPlan.For(a, b, false, false, true).Contains(NoticeHistoryStep.Save)
            && NoticeHistoryPlan.For(null, b, false, true, false).Count == 0 && NoticeHistoryPlan.For(@"c:\a\", a, true, true, true).Count == 0);
    }
}
