using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

internal static class NoticeCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public static void Run(string root, Action<string, bool> check)
    {
        Hub(check);
        LateAndRestart(root, check);
        Coordinator(check);
        History(root, check);
        Geometry(check);
        Motion(check);
        Routing(check);
        Intake(check);
        Pages(check);
        SettingsValues(root, check);
        AuditCoreTests.Run(root, check);
        AuditCore2Tests.Run(root, check);
        AuditR3Tests.Run(root, check);
    }

    private static NoticeInput Input(string instance, string app = "windows:Chat", string title = "Hi", string body = "Body", string? replace = null,
        string? eventId = null, double? progress = null, NoticeCategory category = NoticeCategory.Message, NoticePriority hint = NoticePriority.Normal) => new()
    {
        Origin = NoticeOrigin.Windows, AppKey = app, AppName = app.Split(':')[^1], InstanceKey = instance, Title = title, Body = body,
        ReplaceKey = replace, EventId = eventId, Progress = progress, Category = category, PriorityHint = hint
    };

    private static void Hub(Action<string, bool> check)
    {
        var rules = new Dictionary<string, NoticeAppRule>();
        var hub = new NoticeHub(key => rules.GetValueOrDefault(key));
        int changes = 0, saves = 0; hub.Changed += () => changes++; hub.Persist += _ => saves++;
        var first = hub.Receive(Input("a"), T0);
        check("A new notice is accepted and shown", first.Receipt == NoticeReceipt.Accepted && first.Present && hub.Notices.Count == 1 && hub.UnviewedCount == 1 && changes == 1 && saves == 1);
        hub.Receive(Input("b", eventId: "e1"), T0);
        var retry = hub.Receive(Input("c", eventId: "e1"), T0.AddSeconds(1));
        check("A retry with the same event id is a duplicate", retry.Receipt == NoticeReceipt.Duplicate && hub.Notices.Count == 2);
        check("The same event id from another application is not a duplicate", hub.Receive(Input("d", app: "cli:x", eventId: "e1"), T0).Receipt == NoticeReceipt.Accepted);
        check("A kept notice still recognizes its event id after ten minutes", hub.Receive(Input("e", eventId: "e1"), T0.AddMinutes(11)).Receipt == NoticeReceipt.Duplicate);
        hub.Delete(hub.FindInstance("b")!.Id);
        check("Event ids are forgotten after ten minutes once no kept notice has them", hub.Receive(Input("e", eventId: "e1"), T0.AddMinutes(11)).Receipt == NoticeReceipt.Accepted);

        var task = hub.Receive(Input("t1", app: "cli:make", title: "Build", replace: "build", progress: .1, category: NoticeCategory.Task), T0);
        var progress = hub.Receive(Input("t2", app: "cli:make", title: "Build", replace: "build", progress: .6, category: NoticeCategory.Task), T0.AddSeconds(2));
        check("A progress update changes the notice without showing it again", progress.Receipt == NoticeReceipt.Updated && !progress.Present && progress.Notice == task.Notice
            && task.Notice!.Progress == .6 && task.Notice.Revision == 1);
        var done = hub.Receive(Input("t3", app: "cli:make", title: "Build", replace: "build", category: NoticeCategory.Success), T0.AddSeconds(4));
        check("Finishing the task shows it again", done.Receipt == NoticeReceipt.Updated && done.Present && task.Notice!.Progress is null && task.Notice.Category == NoticeCategory.Success && hub.Notices[0] == task.Notice);
        check("The same replace key from another application is a different notice", hub.Receive(Input("t4", app: "cli:other", replace: "build"), T0).Receipt == NoticeReceipt.Accepted);
        check("The same title and body are not taken for duplicates", hub.Receive(Input("x1"), T0).Receipt == NoticeReceipt.Accepted && hub.Receive(Input("x2"), T0).Receipt == NoticeReceipt.Accepted);

        rules["windows:Muted"] = new NoticeAppRule { Key = "windows:Muted", Muted = true };
        rules["windows:Off"] = new NoticeAppRule { Key = "windows:Off", Enabled = false };
        rules["windows:Vip"] = new NoticeAppRule { Key = "windows:Vip", Priority = NoticePriority.High };
        var muted = hub.Receive(Input("m", app: "windows:Muted"), T0);
        check("A muted application's notice is kept but not shown", muted.Receipt == NoticeReceipt.Accepted && !muted.Present);
        int before = hub.Notices.Count;
        check("A disabled application's notice is filtered out entirely", hub.Receive(Input("o", app: "windows:Off"), T0).Receipt == NoticeReceipt.Filtered && hub.Notices.Count == before);
        check("Only the user's rule makes a notice high priority", hub.Receive(Input("v", app: "windows:Vip"), T0).Notice!.Priority == NoticePriority.High
            && hub.Receive(Input("h", hint: NoticePriority.High), T0).Notice!.Priority == NoticePriority.Normal);
        var low = hub.Receive(Input("l", hint: NoticePriority.Low), T0);
        check("A low priority hint keeps the notice quiet", low.Notice!.Priority == NoticePriority.Low && !low.Present);
        check("A high priority rule ignores a low hint", NoticeHub.EffectivePriority(rules["windows:Vip"], NoticePriority.Low) == NoticePriority.High);

        hub.SourceRemoved("a");
        var gone = hub.FindInstance("a")!;
        check("A notice removed at its source is kept, marked as gone", !gone.SourcePresent && hub.Notices.Contains(gone));
        hub.MarkViewed(gone.Id);
        int unviewed = hub.UnviewedCount;
        check("Marking viewed lowers the count", gone.Viewed && unviewed == hub.Notices.Count(n => !n.Viewed));
        hub.MarkAllViewed();
        check("Marking all viewed clears the count", hub.UnviewedCount == 0);
        check("Deleting removes one notice", hub.Delete(gone.Id) && hub.Find(gone.Id) is null && !hub.Delete("missing"));
        check("Known applications are remembered", hub.KnownApps.Any(app => app.Key == "cli:make"));

        var small = new NoticeHub { MaxCount = 3, Retention = TimeSpan.FromHours(1) };
        for (int i = 0; i < 5; i++) small.Receive(Input("n" + i), T0.AddMinutes(i));
        check("The list keeps at most MaxCount notices, newest first", small.Notices.Count == 3 && small.Notices[0].InstanceKey == "n4");
        check("Pruning drops notices past the retention", small.Prune(T0.AddMinutes(63)) && small.Notices.Count == 2);
        small.Clear();
        check("Clearing empties the list", small.Notices.Count == 0 && small.UnviewedCount == 0);

        var loaded = new NoticeHub();
        var saved = new Notice { InstanceKey = "k", AppKey = "windows:A", Title = "t", ReceivedAt = T0, Capabilities = NoticeCapabilities.Actions | NoticeCapabilities.OpenApp };
        loaded.Load([saved, saved, new Notice { Id = "", ReceivedAt = T0 }, new Notice { InstanceKey = "old", ReceivedAt = T0.AddDays(-3) }], [], T0.AddMinutes(1));
        check("Loaded notices are deduplicated, pruned and no longer present at their source", loaded.Notices.Count == 1 && !loaded.Notices[0].SourcePresent
            && loaded.Notices[0].Capabilities == NoticeCapabilities.OpenApp);

        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        var run = NoticeHub.Words(new NoticeInput { Origin = NoticeOrigin.Cli, AppKey = "cli:x", AppName = "x", InstanceKey = "r", ExitCode = 1, DurationMs = 65_000, Command = "make" });
        check("A run result is worded in Chinese", run.Title == "make 失败" && run.Body == "退出码 1 · 用时 1 分 5 秒");
        Loc.Use(Loc.English);
        var ok = NoticeHub.Words(new NoticeInput { Origin = NoticeOrigin.Cli, AppKey = "cli:x", AppName = "x", InstanceKey = "r", ExitCode = 0, DurationMs = 1500 });
        check("A run result is worded in English", ok.Title == "Command finished" && ok.Body == "Exit code 0 · took 1.5 s");
        var bodyOnly = NoticeHub.Words(Input("b", title: "", body: "First line\nSecond"));
        check("A body without title gives its first line as the title", bodyOnly.Title == "First line" && bodyOnly.Body == "Second");
        Loc.Use(language);
    }

    // A Claude Code hook in WSL: brim-notify with one replace key per session, through the distribution's agent.
    private static NoticeMessage Hook(string eventId, string body, DateTimeOffset created, long? ageMs, string category = "success") => new()
    {
        Origin = "cli", AppName = "Claude Code", Title = "Claude Code · demo", Body = body, Category = category, ReplaceId = "claude-code:s1",
        EventId = eventId, CreatedAt = created, AgeMs = ageMs
    };

    // BrimDeck restarted: the saved notices come back, then a hook keeps sending through brim-notify, early or late.
    private static void LateAndRestart(string root, Action<string, bool> check)
    {
        var before = new NoticeHub();
        var first = before.Receive(NoticeIntake.FromPipe(Hook("ev1", "done", T0, 0)), T0);
        check("A fresh hook notice is shown and keeps its event id", first.Present && first.Notice!.EventId == "ev1");
        var history = new NoticeHistory(Path.Combine(root, "notices-restart"), new NoticeHistory.PlainProtector());
        history.Save(before.Notices, before.KnownApps);
        history.Load(out var saved, out var apps);
        check("A notice's event id is saved with it", saved.Single().EventId == "ev1");
        history.Delete();

        var now = T0.AddMinutes(38);
        var hub = new NoticeHub();
        hub.Load(saved, apps, now);
        hub.MarkAllViewed();
        var again = hub.Receive(NoticeIntake.FromPipe(Hook("ev1", "done", T0, 0)), now);
        check("A message recorded before the restart is a duplicate after it", again.Receipt == NoticeReceipt.Duplicate && !again.Present
            && hub.Notices.Count == 1 && hub.UnviewedCount == 0);

        var late = hub.Receive(NoticeIntake.FromPipe(Hook("ev2", "waiting", T0, 38 * 60_000L, "info")), now);
        check("A message that waited 38 minutes is recorded, unviewed, but not shown", late.Receipt == NoticeReceipt.Updated && !late.Present && late.Reason == "late"
            && hub.Notices.Single().Body == "waiting" && hub.UnviewedCount == 1);

        // The sender's own age says the message is fresh, whatever its creation time says.
        now = now.AddMinutes(1);
        var fresh = hub.Receive(NoticeIntake.FromPipe(Hook("ev3", "done again", now.AddMinutes(-10), 0)), now);
        check("A fresh message is shown by the sender's own age", fresh.Present && fresh.Receipt == NoticeReceipt.Updated);
        check("An update shown now reads as just arrived, not as old as the notice it replaced", fresh.Notice!.ReceivedAt == now
            && fresh.Notice.EventId == "ev3" && NoticeText.Relative(fresh.Notice.ReceivedAt, now) == Loc.T("刚刚", "Just now"));

        var windowsCli = hub.Receive(NoticeIntake.FromPipe(new NoticeMessage { Title = "late", EventId = "w1", CreatedAt = now.AddMinutes(-38) }), now);
        check("A late message from a sender on Windows' clock is judged by its creation time", windowsCli.Receipt == NoticeReceipt.Accepted && !windowsCli.Present
            && hub.Receive(NoticeIntake.FromPipe(new NoticeMessage { Title = "now", EventId = "w2", CreatedAt = now.AddSeconds(-2) }), now).Present);

        var input = NoticeIntake.FromPipe(Hook("ev5", "x", now, 2 * 60_000L));
        check("A high priority message may be later than a normal one", NoticeHub.Late(input, NoticePriority.Normal, now) && !NoticeHub.Late(input, NoticePriority.High, now)
            && NoticeHub.Late(input with { Age = TimeSpan.FromMinutes(6) }, NoticePriority.High, now));
        check("Time-kind notices and Windows' notices keep their own rules", !NoticeHub.Late(input with { Timing = new NoticeTiming { Kind = NoticeTimeKind.Countdown } }, NoticePriority.Normal, now)
            && NoticeHub.Late(input with { Timing = new NoticeTiming { Kind = NoticeTimeKind.Mail } }, NoticePriority.Normal, now)
            && !NoticeHub.Late(Input("w") with { CreatedAt = now.AddHours(-1) }, NoticePriority.Normal, now));

        var message = new NoticeMessage { Title = "t", CreatedAt = now.AddSeconds(-90) };
        NoticeProtocol.Stamp(message, now);
        var stamped = new NoticeMessage { Title = "t" };
        NoticeProtocol.Stamp(stamped, now);
        check("A sender stamps a message's age by its own clock", message.AgeMs == 90_000 && stamped.CreatedAt == now && stamped.AgeMs == 0
            && NoticeProtocol.Deserialize(NoticeProtocol.Serialize(message))!.AgeMs == 90_000);
        check("A negative age is refused", NoticeProtocol.Validate(new NoticeMessage { Title = "t", AgeMs = -1 }) == "bad-age");
    }

    private static AttentionItem Item(string lane, AttentionKind kind, string key, string group = "", NoticePriority priority = NoticePriority.Normal, double seconds = 6, bool refresh = false)
        => new() { Lane = lane, Kind = kind, Key = key, Group = group, Priority = priority, Duration = TimeSpan.FromSeconds(seconds), Refresh = refresh };

    private static void Coordinator(Action<string, bool> check)
    {
        var c = new AttentionCoordinator();
        var started = c.Submit(Item("main", AttentionKind.Notice, "n1", "A"), T0);
        check("An item on a free lane starts at once", started is [{ Started.Key: "n1" }] && c.Current("main")?.Key == "n1" && c.NextDeadline(T0) == T0.AddSeconds(6));
        var merged = c.Submit(Item("main", AttentionKind.Notice, "n2", "A"), T0.AddMilliseconds(500));
        check("A notice from the same application within 800 ms merges with a count", merged is [{ Updated.Key: "n2" }] && c.Current("main")!.Count == 2);
        c.Submit(Item("main", AttentionKind.Notice, "n3", "A"), T0.AddMilliseconds(1200));
        check("Within 800 ms of the last merge it merges again", c.Current("main")!.Count == 3);
        c.Submit(Item("main", AttentionKind.Notice, "n4", "A"), T0.AddMilliseconds(2500));
        check("Later notices from it wait instead", c.Current("main")!.Count == 3 && c.Queued("main").Count == 1);
        c.Submit(Item("main", AttentionKind.Quota, "q"), T0.AddSeconds(3));
        c.Submit(Item("main", AttentionKind.Notice, "hp", "B", NoticePriority.High, 8), T0.AddSeconds(3));
        check("A high priority notice takes over; the one it replaced waits with its remaining time", c.Current("main")?.Key == "hp"
            && c.Queued("main").Select(i => i.Key).SequenceEqual(["n3", "n4", "q"]));
        var trackDropped = c.Submit(Item("main", AttentionKind.Track, "t", seconds: 4), T0.AddSeconds(3));
        check("A track change that cannot show at once is dropped", trackDropped.Count == 0 && c.Queued("main").All(i => i.Kind != AttentionKind.Track));
        c.Hold("main", true, T0.AddSeconds(4));
        check("While held, the only wake-up is for waiting items going stale", c.NextDeadline(T0.AddSeconds(4)) == T0.AddSeconds(30));
        c.Submit(Item("main", AttentionKind.Notice, "hp2", "C", NoticePriority.High), T0.AddSeconds(5));
        check("Nothing replaces a held item", c.Current("main")?.Key == "hp");
        c.Tick(T0.AddSeconds(60));
        check("A held item does not end on time", c.Current("main")?.Key == "hp");
        c.Hold("main", false, T0.AddSeconds(60));
        check("Released, it keeps its remaining time", c.NextDeadline(T0.AddSeconds(60)) == T0.AddSeconds(67));
        var ended = c.Tick(T0.AddSeconds(67));
        check("Stale normal notices and alerts are not shown after 30 s; high priority ones still are", ended.Any(ch => ch.Ended?.Key == "hp") && c.Current("main")?.Key == "hp2"
            && c.Queued("main").Count == 0);

        // The song changes while its title still shows: the new title gets its whole time, counted from the change.
        var songs = new AttentionCoordinator();
        songs.Submit(Item("m", AttentionKind.Track, "track", seconds: 4), T0);
        var next = songs.Submit(Item("m", AttentionKind.Track, "track", seconds: 4), T0.AddSeconds(3));
        check("A title shown again in place runs its whole time from the change", next is [{ Updated.Key: "track" }] && songs.NextDeadline(T0.AddSeconds(3)) == T0.AddSeconds(7));

        var rate = new AttentionCoordinator();
        for (int i = 0; i < 3; i++) { rate.Submit(Item("r", AttentionKind.Notice, "x" + i, "G" + i, seconds: 1), T0.AddSeconds(i * 2)); rate.Tick(T0.AddSeconds(i * 2 + 1)); }
        rate.Submit(Item("r", AttentionKind.Notice, "x3", "G3", seconds: 1), T0.AddSeconds(6));
        check("At most three notices show in ten seconds", rate.Current("r") is null && rate.Queued("r").Count == 1 && rate.NextDeadline(T0.AddSeconds(6)) == T0.AddSeconds(10));
        rate.Submit(Item("r", AttentionKind.Quota, "quota", seconds: 5), T0.AddSeconds(6));
        check("The limit does not hold back other kinds", rate.Current("r")?.Key == "quota");
        rate.Tick(T0.AddSeconds(11));
        check("The waiting notice shows once the window allows", rate.Current("r")?.Key == "x3");

        var busy = new AttentionCoordinator();
        busy.Submit(Item("b", AttentionKind.Quota, "q1", seconds: 5), T0);
        busy.SetBusy("b", true, T0.AddSeconds(1));
        check("Opening the panel ends the item; a quota alert waits", busy.Current("b") is null && busy.Queued("b").Single().Key == "q1");
        busy.Submit(Item("b", AttentionKind.Notice, "n", "A"), T0.AddSeconds(2));
        check("Notices are not queued while the panel is open", busy.Queued("b").Count == 1);
        var resumed = busy.SetBusy("b", false, T0.AddSeconds(3));
        check("Closing the panel resumes the alert with its remaining time", resumed.Single().Started?.Key == "q1" && busy.NextDeadline(T0.AddSeconds(3)) == T0.AddSeconds(7));
        busy.Submit(Item("b", AttentionKind.Notice, "n2", "B"), T0.AddSeconds(4));
        check("A notice takes over a quota alert", busy.Current("b")?.Key == "n2");
        busy.Dismiss("b", T0.AddSeconds(5));
        check("Dismissing shows what waits next", busy.Current("b")?.Key == "q1");
        busy.Withdraw("q1", T0.AddSeconds(5));
        check("A withdrawn item ends", busy.Current("b") is null);

        // N50-4: a notice that arrives once the pointer has left the open panel shows from the island after it has closed.
        var leaving = new AttentionCoordinator();
        leaving.SetLeaving("l", true);
        leaving.SetBusy("l", true, T0);
        leaving.Submit(Item("l", AttentionKind.Notice, "open", "A"), T0.AddSeconds(1));
        check("Leaving a closed panel changes nothing; an open one keeps its notices to its page", leaving.Queued("l").Count == 0);
        leaving.SetLeaving("l", true);
        leaving.Submit(Item("l", AttentionKind.Notice, "left", "B"), T0.AddSeconds(2));
        check("A notice that arrives once the pointer has left the open panel waits for it to close", leaving.Queued("l").Single().Key == "left");
        var closed = leaving.SetBusy("l", false, T0.AddSeconds(2.3));
        check("Once the panel has closed, that notice shows from the island", closed.Single().Started?.Key == "left");
        leaving.SetBusy("l", true, T0.AddSeconds(20));
        leaving.SetLeaving("l", true);
        leaving.Submit(Item("l", AttentionKind.Notice, "back", "C"), T0.AddSeconds(21));
        leaving.Submit(Item("l", AttentionKind.Quota, "quota", seconds: 5), T0.AddSeconds(21));
        leaving.SetLeaving("l", false);
        check("Coming back to the panel, the waiting notices are on its page and no longer wait; a quota alert still does",
            leaving.Queued("l").Single().Key == "quota");
        leaving.SetLeaving("l", true);
        leaving.SetBusy("l", false, T0.AddSeconds(22));
        leaving.SetBusy("l", true, T0.AddSeconds(23));
        leaving.Submit(Item("l", AttentionKind.Notice, "late", "D"), T0.AddSeconds(24));
        check("A panel opened anew keeps its notices to its page until the pointer leaves it again", leaving.Queued("l").Single().Key == "quota");

        var quiet = new AttentionCoordinator();
        quiet.Submit(Item("m", AttentionKind.Notice, "a", "A"), T0);
        quiet.Submit(Item("m", AttentionKind.Notice, "b", "B"), T0);
        var silenced = quiet.SetSuppressed(true, T0.AddSeconds(1), out _);
        quiet.Submit(Item("m", AttentionKind.Notice, "c", "C"), T0.AddSeconds(2));
        quiet.Submit(Item("m", AttentionKind.Quota, "q"), T0.AddSeconds(2));
        quiet.SetSuppressed(false, T0.AddSeconds(3), out int arrived);
        check("Suppression ends what shows and counts waiting and arriving notices for one summary", silenced.Single().Ended?.Key == "a" && arrived == 2 && quiet.Current("m") is null && quiet.Queued("m").Count == 0);

        // A source sending every half second for 20 seconds: one merged notice may not hold the island all that time.
        var chatty = new AttentionCoordinator();
        int starts = 0;
        DateTimeOffset? firstEnded = null;
        for (int i = 0; i <= 40; i++)
        {
            var at = T0.AddMilliseconds(500 * i);
            foreach (var change in chatty.Tick(at).Concat(chatty.Submit(Item("c", AttentionKind.Notice, "m" + i, "Chat", seconds: 6), at)))
            {
                if (change.Started is not null) starts++;
                if (change.Ended is not null) firstEnded ??= at;
            }
        }
        check("A source that keeps sending gives up the island within twice the duration plus the minimum", starts >= 2 && firstEnded <= T0.AddSeconds(13.5));
        var update = new AttentionCoordinator();
        update.Submit(Item("u", AttentionKind.Notice, "task", "T"), T0);
        var refreshed = update.Submit(Item("u", AttentionKind.Notice, "task", "T", refresh: true), T0.AddSeconds(5.5));
        check("An update to the shown notice refreshes it in place and keeps it a moment longer", refreshed.Single().Updated?.Key == "task" && update.NextDeadline(T0.AddSeconds(5.5)) == T0.AddSeconds(7));
        // Switching notices off or clearing the list cancels what shows and everything waiting; nothing waiting starts.
        var cancel = new AttentionCoordinator();
        cancel.Submit(Item("c", AttentionKind.Notice, "n1", "a"), T0);
        cancel.Submit(Item("c", AttentionKind.Notice, "n2", "b", NoticePriority.High), T0.AddSeconds(1));
        cancel.Submit(Item("c", AttentionKind.Notice, "n3", "c"), T0.AddSeconds(2));
        cancel.Submit(Item("c", AttentionKind.Quota, "q"), T0.AddSeconds(2));
        var cancelled = cancel.Remove(item => item.IsNotice, T0.AddSeconds(3));
        check("Removing notices cancels the shown one and all waiting ones at once", cancelled.All(c => c.Started?.IsNotice != true)
            && cancel.Queued("c").All(i => !i.IsNotice) && cancel.Current("c")?.Kind is null or AttentionKind.Quota);
        // The last check refuses items that may no longer show; the next allowed one starts instead.
        var gated = new AttentionCoordinator();
        var deleted = new HashSet<string> { "g2" };
        gated.CanShow = item => !deleted.Contains(item.Key);
        gated.Submit(Item("g", AttentionKind.Notice, "g1", "a", seconds: 2), T0);
        gated.Submit(Item("g", AttentionKind.Notice, "g2", "b", seconds: 2), T0.AddSeconds(1));
        gated.Submit(Item("g", AttentionKind.Notice, "g3", "c", seconds: 2), T0.AddSeconds(1.5));
        gated.Tick(T0.AddSeconds(2.5));
        check("An item refused by the last check is skipped", gated.Current("g")?.Key == "g3" && gated.Queued("g").Count == 0);
        // Queues are bounded for every priority, and high priority notices stop waiting after a while.
        var flood = new AttentionCoordinator();
        flood.Submit(Item("f", AttentionKind.Notice, "first", "x", NoticePriority.High, seconds: 3600), T0);
        for (int i = 0; i < 2000; i++) flood.Submit(Item("f", AttentionKind.Notice, "h" + i, "g" + i, NoticePriority.High), T0.AddMilliseconds(i));
        check("The waiting queue never grows past its limit", flood.Queued("f").Count == AttentionCoordinator.QueueLimit);
        flood.Dismiss("f", T0.AddHours(8));
        check("High priority notices that waited for hours are no longer shown", flood.Current("f") is null && flood.Queued("f").Count == 0);
        update.RemoveLane("u");
        check("A removed lane is forgotten", update.Current("u") is null && update.NextDeadline(T0) is null);
    }

    private static void History(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "notices");
        var history = new NoticeHistory(folder, new NoticeHistory.PlainProtector());
        check("A missing history file is empty", history.Load(out var none, out _) == NoticeHistoryState.Empty && none.Count == 0);
        var notice = new Notice { InstanceKey = "k", AppKey = "wsl:Ubuntu:make", AppName = "make", Title = "构建", Body = "完成", ReceivedAt = T0, Category = NoticeCategory.Success, Progress = .5,
            Actions = [new("open", "Open")], Viewed = true };
        history.Save([notice], [new NoticeKnownApp { Key = "wsl:Ubuntu:make", Name = "make", LastSeen = T0 }]);
        var raw = File.ReadAllBytes(history.FilePath);
        check("The history file is written through the protector", raw.AsSpan(0, 4).SequenceEqual("BDN1"u8) && raw.AsSpan(4, 5).SequenceEqual("PLAIN"u8) && !File.Exists(history.FilePath + ".tmp"));
        var state = history.Load(out var notices, out var apps);
        check("Saved notices load back", state == NoticeHistoryState.Loaded && notices.Single().Title == "构建" && notices[0].Category == NoticeCategory.Success
            && notices[0].Actions.Single().Key == "open" && notices[0].Viewed && apps.Single().Name == "make");
        File.WriteAllBytes(history.FilePath, [.. "BDN1"u8, 1, 2, 3]);
        check("A file that cannot be decrypted is reported, not thrown", history.Load(out _, out _) == NoticeHistoryState.Unreadable);
        File.WriteAllBytes(history.FilePath, "garbage"u8.ToArray());
        check("A file of another format is reported", history.Load(out _, out _) == NoticeHistoryState.Unreadable);
        File.WriteAllBytes(history.FilePath, [.. "BDN1"u8, .. "PLAIN"u8, .. "{not json"u8]);
        check("A file with broken contents is reported", history.Load(out _, out _) == NoticeHistoryState.Unreadable);
        history.Delete();
        check("Deleting removes the file", !File.Exists(history.FilePath));
        history.Save([notice], [], wanted: () => false);
        check("A save no longer wanted when it writes leaves no file", !File.Exists(history.FilePath) && !File.Exists(history.FilePath + ".tmp"));
        var writers = Enumerable.Range(0, 8).Select(i => Task.Run(() => history.Save([notice], []))).ToArray();
        bool together = true;
        try { Task.WaitAll(writers); } catch (AggregateException) { together = false; }
        check("Saves at the same time take turns instead of failing", together && history.Load(out var last, out _) == NoticeHistoryState.Loaded && last.Count == 1);
        // A background save whose list was copied before the exit save, but which reaches the file after it.
        var stale = notice.Copy(); stale.Title = "旧";
        var fresh = notice.Copy(); fresh.Title = "新";
        long copiedFirst = history.NextOrder();
        history.Save([fresh], []);
        history.Save([stale], [], order: copiedFirst);
        check("A list copied earlier never replaces one copied later", history.Load(out var kept, out _) == NoticeHistoryState.Loaded && kept.Single().Title == "新");
        history.Save([stale], []);
        check("A list copied after that is written again", history.Load(out kept, out _) == NoticeHistoryState.Loaded && kept.Single().Title == "旧");
        history.Delete();
    }

    private static void Geometry(Action<string, bool> check)
    {
        var monitor = new DipRect(0, 0, 1920, 1080);
        var work = new DipRect(0, 0, 1920, 1032);
        var settings = new DeckSettings();
        var notch = CollapsedGeometry.Resolve(settings, CompactStyle.Notch, monitor, work);
        var toast = NoticeGeometry.Frame(notch, monitor, work, NoticeForm.Toast, 360, 50);
        check("A notch toast stays attached with its shoulders, centred on the notch", toast.Rect.Y == 0 && toast.Shoulder == notch.Shoulder && toast.Rect.Width == 360 && toast.Rect.Height == 50
            && Math.Abs(toast.Rect.X + toast.Rect.Width / 2 - (notch.Surface.X + notch.Surface.Width / 2)) < .001 && toast.TopRadius == 0);
        var tiny = NoticeGeometry.Frame(notch, monitor, work, NoticeForm.Toast, 10, 10);
        check("A toast is never smaller than its range or the surface it grows from", tiny.Rect.Width == 280 && tiny.Rect.Height >= Math.Max(44, notch.Surface.Height));
        var huge = NoticeGeometry.Frame(notch, monitor, work, NoticeForm.Toast, 5000, 500);
        check("A toast is never larger than its range", huge.Rect.Width == 420 && huge.Rect.Height == 64);
        settings.NotchWidth = 600;
        var wide = CollapsedGeometry.Resolve(settings, CompactStyle.Notch, monitor, work);
        check("A notch wider than the toast range keeps its width", NoticeGeometry.Frame(wide, monitor, work, NoticeForm.Toast, 300, 50).Rect.Width == wide.Surface.Width);
        var full = CollapsedGeometry.Resolve(new DeckSettings { NotchWidth = 5000 }, CompactStyle.Notch, monitor, work);
        var fullToast = NoticeGeometry.Frame(full, monitor, work, NoticeForm.Toast, 300, 50).Rect;
        check("A toast over an island as wide as the screen covers it exactly (sync L7)", full.Surface.Width > monitor.Width - 2 * NoticeGeometry.ScreenMargin
            && fullToast.X == full.Surface.X && fullToast.Width == full.Surface.Width);
        settings.NotchOffsetX = 4000;
        var edge = CollapsedGeometry.Resolve(settings, CompactStyle.Notch, monitor, work);
        var edgeToast = NoticeGeometry.Frame(edge, monitor, work, NoticeForm.Detail, 460, 300);
        check("A notice near the screen edge stays on the monitor", edgeToast.Rect.X >= 0 && edgeToast.Rect.X + edgeToast.Rect.Width <= monitor.Width);
        var capsule = CollapsedGeometry.Resolve(new DeckSettings(), CompactStyle.Capsule, monitor, work);
        var pill = NoticeGeometry.Frame(capsule, monitor, work, NoticeForm.Toast, 300, 48);
        check("A capsule toast is a pill anchored at the capsule's top", pill.Rect.Y == capsule.Surface.Y && pill.TopRadius == pill.Rect.Height / 2 && pill.Shoulder == 0);
        var low = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 4000 }, CompactStyle.Capsule, monitor, work);
        var lowDetail = NoticeGeometry.Frame(low, monitor, work, NoticeForm.Detail, 400, 300);
        check("Near the bottom of the work area a card grows upward", lowDetail.Rect.Y + lowDetail.Rect.Height <= work.Height - NoticeGeometry.ScreenMargin + .001 && lowDetail.Rect.Height == 300);
        var line = CollapsedGeometry.Resolve(new DeckSettings(), CompactStyle.Line, monitor, work);
        var lineToast = NoticeGeometry.Frame(line, monitor, work, NoticeForm.Toast, 260, 46);
        check("The indicator becomes a short pill at its place", lineToast.Rect.Y == line.Surface.Y && lineToast.Rect.Width == 260 && lineToast.Rect.Height == 46);
        var detail = NoticeGeometry.Frame(notch, monitor, work, NoticeForm.Detail, 100, 2000);
        check("Details keep to their range", detail.Rect.Width == NoticeGeometry.DetailMinWidth && detail.Rect.Height == NoticeGeometry.DetailMaxHeight && detail.BottomRadius == NoticeGeometry.DetailRadius);
        var narrow = NoticeGeometry.Frame(notch, new DipRect(0, 0, 300, 600), new DipRect(0, 0, 300, 560), NoticeForm.Detail, 460, 200);
        check("On a narrow monitor a card fits within the margins", narrow.Rect.Width == 284 && narrow.Rect.X >= 8);
        var nan = NoticeGeometry.Frame(notch, monitor, work, NoticeForm.Toast, double.NaN, double.PositiveInfinity);
        check("Unknown sizes fall back to the range", double.IsFinite(nan.Rect.Width) && double.IsFinite(nan.Rect.Height));
        check("The collapsed frame is the surface itself", NoticeGeometry.Collapsed(notch).Rect == notch.Surface);
        // At the panel's scale (150%) the island is drawn larger and the notice with it: its range, the card's corners and the margin to the
        // screen's edge are those of 100% times the scale, and it still grows and shrinks about the island's middle.
        const double big = 1.5;
        var notch15 = CollapsedGeometry.Resolve(new DeckSettings(), CompactStyle.Notch, monitor, work, panelScale: big);
        var tiny15 = NoticeGeometry.Frame(notch15, monitor, work, NoticeForm.Toast, 10, 10, big);
        var huge15 = NoticeGeometry.Frame(notch15, monitor, work, NoticeForm.Toast, 5000, 500, big);
        check("At 150% a toast's range is the 100% range times 1.5", tiny15.Rect.Width == 280 * big && huge15.Rect.Width == 420 * big && huge15.Rect.Height == 64 * big);
        check("At 150% a notch toast keeps the scaled island's shoulders and its middle", huge15.Shoulder == notch15.Shoulder && huge15.Rect.Y == 0
            && Math.Abs(huge15.Rect.X + huge15.Rect.Width / 2 - (notch15.Surface.X + notch15.Surface.Width / 2)) < .001 && huge15.BottomRadius == 20 * big);
        var detail15 = NoticeGeometry.Frame(notch15, monitor, work, NoticeForm.Detail, 100, 2000, big);
        check("At 150% the details keep to their range times 1.5, with corners times 1.5", detail15.Rect.Width == NoticeGeometry.DetailMinWidth * big
            && detail15.Rect.Height == NoticeGeometry.DetailMaxHeight * big && detail15.BottomRadius == NoticeGeometry.DetailRadius * big);
        var narrow15 = NoticeGeometry.Frame(notch15, new DipRect(0, 0, 300, 600), new DipRect(0, 0, 300, 560), NoticeForm.Detail, 460 * big, 200 * big, big);
        check("At 150% the margin to the screen's edge is scaled too", narrow15.Rect.Width == 300 - 2 * 8 * big && narrow15.Rect.X >= 8 * big - .001);
        var low15 = CollapsedGeometry.Resolve(new DeckSettings { CapsuleTop = 4000 }, CompactStyle.Capsule, monitor, work, panelScale: big);
        var lowDetail15 = NoticeGeometry.Frame(low15, monitor, work, NoticeForm.Detail, 400 * big, 300 * big, big);
        check("At 150% a card near the bottom still grows upward within the scaled margin", lowDetail15.Rect.Y + lowDetail15.Rect.Height <= work.Height - NoticeGeometry.ScreenMargin * big + .001);
        check("A scale that is not a number is taken as 100%", NoticeGeometry.Frame(notch, monitor, work, NoticeForm.Toast, 10, 10, double.NaN).Rect.Width == 280
            && NoticeGeometry.ToastLimits(CompactStyle.Capsule, 0).MaxWidth == 400);
        // A notice ends exactly on the island it returns to, in every collapsed form, so the island takes over without a jump.
        foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Capsule, CompactStyle.Line })
        {
            var island = CollapsedGeometry.Resolve(new DeckSettings(), style, monitor, work);
            var end = NoticeGeometry.Collapsed(island);
            check($"A notice returns onto the {style} island's outline", end.Rect == island.Surface && end.TopRadius == island.TopRadius
                && end.BottomRadius == island.BottomRadius && end.Shoulder == island.Shoulder);
            var start = NoticeGeometry.Frame(island, monitor, work, NoticeForm.Toast, 300, 48);
            check($"A {style} notice grows and shrinks about the island's middle", Math.Abs(start.Rect.X + start.Rect.Width / 2 - (island.Surface.X + island.Surface.Width / 2)) < .001);
        }
    }

    private static void Motion(Action<string, bool> check)
    {
        NoticeMotion.Track[] all = [NoticeMotion.Surface, NoticeMotion.ArriveContent, NoticeMotion.ArriveTint, NoticeMotion.ArriveGlow, NoticeMotion.FollowContent,
            NoticeMotion.ReturnContent, NoticeMotion.ReturnSurface, NoticeMotion.ReturnGlow, NoticeMotion.ReturnTint, NoticeMotion.Landing, NoticeMotion.OpenOut, NoticeMotion.OpenIn];
        check("Notice motion: every track lies within its move", all.All(t => t.Start >= 0 && t.End <= 1 && t.Start < t.End));
        check("Notice motion: a move takes the panel's own duration", NoticeMotion.Length(360, true) == 360 && NoticeMotion.Length(720, true) == 720);
        check("Notice motion: nothing moves without animations", NoticeMotion.Length(360, false) == 0);
        check("Notice motion: a duration outside the settings' range is kept to it", NoticeMotion.Length(5, true) == 100 && NoticeMotion.Length(9000, true) == 1000);
        check("Notice motion: the surface returns to the island when its move ends", NoticeMotion.ReturnSurface.End == 1 && NoticeMotion.Surface.End == 1);
        check("Notice motion: what leaves has gone before what arrives starts",
            NoticeMotion.OpenOut.End <= NoticeMotion.OpenIn.Start && NoticeMotion.ReturnContent.End <= NoticeMotion.Landing.Start && NoticeMotion.ReturnContent.End <= NoticeMotion.FollowContent.Start);
        check("Notice motion: the content leaves before the surface starts to settle", NoticeMotion.ReturnContent.End > NoticeMotion.ReturnSurface.Start && NoticeMotion.ReturnContent.Start == 0);
        // The indicator's flat color shows only at the line's size: gone within the opening spring's first frames, back only once
        // the settling surface is nearly the line (the panel's own indicator tracks).
        check("Notice motion: the indicator's color never shows as a large block", NoticeMotion.ArriveTint.End <= .12 && NoticeMotion.ReturnTint.Start >= .85
            && Curves.Bezier(.45, 0, .12, 1, (NoticeMotion.ReturnTint.Start - NoticeMotion.ReturnSurface.Start) / NoticeMotion.ReturnSurface.Length) > .9);
        check("Notice motion: the island's content lands as the panel's compact content does", Math.Abs(NoticeMotion.RevealLength - .38) < 1e-9);
    }

    private static void Routing(Action<string, bool> check)
    {
        NoticeRouting.Candidate[] panels = [new("main", 1, true, true), new("second", 2, false, true), new("hidden", 3, false, false)];
        check("A notice shows on the foreground window's monitor", NoticeRouting.Choose(panels, 2, NoticeScreen.Foreground) == "second");
        check("Without a panel there, the main panel shows it", NoticeRouting.Choose(panels, 9, NoticeScreen.Foreground) == "main" && NoticeRouting.Choose(panels, null, NoticeScreen.Foreground) == "main");
        check("A hidden panel on the foreground monitor keeps the notice back", NoticeRouting.Choose(panels, 3, NoticeScreen.Foreground) is null);
        check("The main-monitor choice ignores the foreground", NoticeRouting.Choose(panels, 2, NoticeScreen.Main) == "main");
        check("No panels, no lane", NoticeRouting.Choose([], 1, NoticeScreen.Foreground) is null);
    }

    private static void Intake(Action<string, bool> check)
    {
        var pipe = NoticeIntake.FromPipe(new NoticeMessage { Title = "x", AppName = " make ", Icon = "https://example.com/a.png", EventId = "e", Category = "error", Priority = "high", Distro = "Ubuntu" });
        check("A pipe message is a command-line notice; web icons and a named distribution are ignored", pipe.Origin == NoticeOrigin.Cli && pipe.AppKey == "cli:make" && pipe.Distro is null && pipe.IconRef is null
            && pipe.Category == NoticeCategory.Error && pipe.PriorityHint == NoticePriority.High && pipe.Capabilities == NoticeCapabilities.None && pipe.EventId == "e");
        check("File URLs become paths", NoticeIntake.IconRef("file:///C:/icons/a.png", NoticeOrigin.Cli, null) == @"file:C:\icons\a.png");
    }

    private static void Pages(Action<string, bool> check)
    {
        var settings = new DeckSettings();
        check("The notices page is hidden while notices are off", !DeckPages.Shown(settings, DeckPage.Notices) && DeckPages.Resolve(settings, DeckPage.Notices) == DeckPage.Usage);
        settings.NoticesEnabled = true;
        check("The notices page shows once notices are on, last in the switch", DeckPages.Visible(settings).SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Notices]));
        settings.UsagePage = false; settings.MusicPage = false; settings.MonitorPage = false;
        check("With only the notices page it is the one shown", DeckPages.Resolve(settings, DeckPage.Usage) == DeckPage.Notices && DeckPages.Largest(settings) == (settings.NoticesWidth, settings.NoticesHeight));
        check("The notices page opens by choice", DeckPages.Opening(DefaultDeckPage.Notices) == DeckPage.Notices && DeckPages.Opening(DefaultDeckPage.Last) is null);
        check("The notices page is saved as number 3, after the monitor page", (int)DeckPage.Monitor == 2 && (int)DeckPage.Notices == 3 && (int)DefaultDeckPage.Notices == 4);
        DeckPages.SetSize(settings, DeckPage.Notices, 100, 9999);
        check("Dragging the notices page keeps it within the page limits", settings.NoticesWidth == DeckPages.MinWidth && settings.NoticesHeight == DeckPages.MaxHeight);
    }

    private static void SettingsValues(string root, Action<string, bool> check)
    {
        var defaults = new DeckSettings();
        check("Notices are off by default", !defaults.NoticesEnabled && defaults.NoticeWindowsSource
            && defaults.NoticeHistory && defaults.NoticeHistoryHours == 24 && defaults.NoticeHotkey == "" && defaults.NoticeDuration == 6 && defaults.NoticeHighDuration == 8);
        var odd = new DeckSettings
        {
            NoticeScreen = (NoticeScreen)42, NoticeDuration = 0, NoticeHistoryHours = 100_000, NoticesWidth = double.PositiveInfinity,
            NoticeHistoryFolder = @"relative\notices",
            NoticeApps = [new() { Key = "a", AccentColor = "ff0000", Priority = (NoticePriority)9, Privacy = (NoticePrivacy)9 }, new() { Key = "a" }, new() { Key = " " }]
        };
        odd.Normalize();
        check("Notice settings are normalized", odd.NoticeScreen == NoticeScreen.Foreground && odd.NoticeDuration == 2 && odd.NoticeHistoryHours == 168
            && odd.NoticesWidth == 460 && odd.NoticeHistoryFolder == "" && odd.NoticeApps.Count == 1 && odd.NoticeApps[0].AccentColor == "#FF0000"
            && odd.NoticeApps[0].Priority == NoticePriority.Normal && odd.NoticeApps[0].Privacy is null);
        var copy = odd.Copy();
        copy.NoticeApps[0].Muted = true;
        check("Copies do not share notice rules", !odd.NoticeApps[0].Muted);
        var store = new SettingsStore(Path.Combine(root, "notice-settings"), "1.2.0");
        var saved = new DeckSettings { NoticesEnabled = true, NoticePrivacy = NoticePrivacy.TitleOnly, LastPage = DeckPage.Notices, NoticesPausedUntil = T0, NoticeApps = [new() { Key = "windows:X", Name = "X", Priority = NoticePriority.High }] };
        store.Save(saved);
        var json = File.ReadAllText(store.FilePath);
        var back = store.Load();
        check("Notice settings survive saving", back.NoticesEnabled && back.NoticePrivacy == NoticePrivacy.TitleOnly && back.LastPage == DeckPage.Notices && back.NoticesPausedUntil == T0
            && back.NoticeRule("windows:X")?.Priority == NoticePriority.High && JsonDocument.Parse(json).RootElement.GetProperty("LastPage").GetInt32() == 3);
    }
}
