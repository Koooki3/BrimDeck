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
        Coordinator(check);
        History(root, check);
        Geometry(check);
        Routing(check);
        Intake(check);
        Pages(check);
        SettingsValues(root, check);
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
        int changes = 0, saves = 0; hub.Changed += () => changes++; hub.Persist += () => saves++;
        var first = hub.Receive(Input("a"), T0);
        check("A new notice is accepted and shown", first.Receipt == NoticeReceipt.Accepted && first.Present && hub.Notices.Count == 1 && hub.UnviewedCount == 1 && changes == 1 && saves == 1);
        hub.Receive(Input("b", eventId: "e1"), T0);
        var retry = hub.Receive(Input("c", eventId: "e1"), T0.AddSeconds(1));
        check("A retry with the same event id is a duplicate", retry.Receipt == NoticeReceipt.Duplicate && hub.Notices.Count == 2);
        check("The same event id from another application is not a duplicate", hub.Receive(Input("d", app: "cli:x", eventId: "e1"), T0).Receipt == NoticeReceipt.Accepted);
        check("Event ids are forgotten after ten minutes", hub.Receive(Input("e", eventId: "e1"), T0.AddMinutes(11)).Receipt == NoticeReceipt.Accepted);

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
        check("A pipe message is a command-line notice; web icons are ignored", pipe.Origin == NoticeOrigin.Cli && pipe.AppKey == "cli:Ubuntu:make" && pipe.IconRef is null
            && pipe.Category == NoticeCategory.Error && pipe.PriorityHint == NoticePriority.High && pipe.Capabilities == NoticeCapabilities.None && pipe.EventId == "e");
        var dbus = new NoticeMessage { Origin = "dbus", AppName = "Thunderbird", NativeId = "4", Session = "server", Title = "Mail", Actions = [new() { Key = "open", Label = "Open" }], Icon = "/usr/share/icons/a.png", Distro = "Spoofed" };
        var server = NoticeIntake.FromAgent(dbus, "Ubuntu", agentIsServer: true);
        check("A desktop notice from a serving agent can be acted on and dismissed", server.Origin == NoticeOrigin.Wsl && server.AppKey == "wsl:Ubuntu:Thunderbird" && server.Distro == "Ubuntu"
            && server.InstanceKey == "wsl:Ubuntu:server:4" && server.Capabilities == (NoticeCapabilities.Actions | NoticeCapabilities.ReportDismissal) && server.Actions.Count == 1
            && server.IconRef == "wsl:Ubuntu:/usr/share/icons/a.png");
        var watched = NoticeIntake.FromAgent(dbus, "Ubuntu", agentIsServer: false);
        check("A watched desktop notice offers nothing that cannot be done", watched.Capabilities == NoticeCapabilities.None && watched.Actions.Count == 0);
        var cli = NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", Title = "t" }, "Debian", true);
        check("brim-notify through the agent is keyed by the agent's distribution", cli.Origin == NoticeOrigin.Cli && cli.AppKey == "cli:Debian:brim-notify" && cli.Distro == "Debian");
        check("Distribution names are WSL's own characters only", NoticeIntake.IsDistroName("Ubuntu-24.04") && NoticeIntake.IsDistroName("my_distro")
            && !NoticeIntake.IsDistroName("..") && !NoticeIntake.IsDistroName(@"..\server") && !NoticeIntake.IsDistroName("a/b") && !NoticeIntake.IsDistroName("Ubuntu 24")
            && !NoticeIntake.IsDistroName("") && !NoticeIntake.IsDistroName(new string('a', 65)));
        check("File URLs become paths", NoticeIntake.IconRef("file:///C:/icons/a.png", NoticeOrigin.Cli, null) == "file:C:/icons/a.png" || NoticeIntake.IconRef("file:///C:/icons/a.png", NoticeOrigin.Cli, null) == @"file:C:\icons\a.png");
    }

    private static void Pages(Action<string, bool> check)
    {
        var settings = new DeckSettings();
        check("The notices page is hidden while notices are off", !DeckPages.Shown(settings, DeckPage.Notices) && DeckPages.Resolve(settings, DeckPage.Notices) == DeckPage.Usage);
        settings.NoticesEnabled = true;
        check("The notices page shows once notices are on, last in the switch", DeckPages.Visible(settings).SequenceEqual([DeckPage.Usage, DeckPage.Monitor, DeckPage.Music, DeckPage.Notices]));
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
        check("Notices are off by default, with no WSL distribution", !defaults.NoticesEnabled && defaults.NoticeWslDistros.Count == 0 && defaults.NoticeWindowsSource
            && defaults.NoticeHistory && defaults.NoticeHistoryHours == 24 && defaults.NoticeHotkey == "" && defaults.NoticeDuration == 6 && defaults.NoticeHighDuration == 8);
        var odd = new DeckSettings
        {
            NoticeScreen = (NoticeScreen)42, NoticeDuration = 0, NoticeHistoryHours = 100_000, NoticesWidth = double.PositiveInfinity,
            NoticeWslDistros = ["Ubuntu", " ubuntu ", "", "Debian", @"..\..\server\share", "a b"], NoticeHistoryFolder = @"relative\notices",
            NoticeApps = [new() { Key = "a", AccentColor = "ff0000", Priority = (NoticePriority)9, Privacy = (NoticePrivacy)9 }, new() { Key = "a" }, new() { Key = " " }]
        };
        odd.Normalize();
        check("Notice settings are normalized", odd.NoticeScreen == NoticeScreen.Foreground && odd.NoticeDuration == 2 && odd.NoticeHistoryHours == 168
            && odd.NoticesWidth == 460 && odd.NoticeWslDistros.SequenceEqual(["Ubuntu", "Debian"]) && odd.NoticeHistoryFolder == "" && odd.NoticeApps.Count == 1 && odd.NoticeApps[0].AccentColor == "#FF0000"
            && odd.NoticeApps[0].Priority == NoticePriority.Normal && odd.NoticeApps[0].Privacy is null);
        var copy = odd.Copy();
        copy.NoticeApps[0].Muted = true; copy.NoticeWslDistros.Add("Arch");
        check("Copies do not share notice rules or distributions", !odd.NoticeApps[0].Muted && odd.NoticeWslDistros.Count == 2);
        var store = new SettingsStore(Path.Combine(root, "notice-settings"), "1.2.0");
        var saved = new DeckSettings { NoticesEnabled = true, NoticePrivacy = NoticePrivacy.TitleOnly, LastPage = DeckPage.Notices, NoticesPausedUntil = T0, NoticeApps = [new() { Key = "windows:X", Name = "X", Priority = NoticePriority.High }] };
        store.Save(saved);
        var json = File.ReadAllText(store.FilePath);
        var back = store.Load();
        check("Notice settings survive saving", back.NoticesEnabled && back.NoticePrivacy == NoticePrivacy.TitleOnly && back.LastPage == DeckPage.Notices && back.NoticesPausedUntil == T0
            && back.NoticeRule("windows:X")?.Priority == NoticePriority.High && JsonDocument.Parse(json).RootElement.GetProperty("LastPage").GetInt32() == 3);
    }
}
