using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

// Round two of the 2026-10-24 audit in the notices core (group "core2"): text cleaning that keeps what writing needs, the
// limit on pending reminders, deletions while the file was not read, reminders read after waking, pipe and agent keys, the
// urgent limit per source, and the history file's link and permission states. Fixed clocks only.
internal static class AuditCore2Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 6, 0, 0, TimeSpan.Zero);

    public static void Run(string root, Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try
        {
            Cleaning(check);
            PendingLimit(check);
            Removals(check);
            WakeReminders(check);
            Links(check);
            UrgentPerSource(check);
            HistoryLinks(root, check);
        }
        finally { Loc.Use(language); }
    }

    private static NoticeInput Watch(string name, string key, NoticeMessage? message = null) => NoticeIntake.FromPipe(
        message ?? new NoticeMessage { AppName = name, ReplaceId = key, Title = key }, T0) with
    { Timing = new NoticeTiming { Kind = NoticeTimeKind.Stopwatch, StartsAt = T0, Native = true } };

    private static void Cleaning(Action<string, bool> check)
    {
        check("C-L11: the zero-width non-joiner, LRM, RLM and ALM stay", NoticeText.Clean("می\u200Cخواهم") == "می\u200Cخواهم"
            && NoticeText.Clean("a\u200Eb\u200Fc\u061Cd") == "a\u200Eb\u200Fc\u061Cd");
        check("C-L11: bidi embeddings, overrides and isolates go", NoticeText.Clean("a\u202Ab\u202Bc\u202Cd\u202De\u202Ef\u2066g\u2067h\u2068i\u2069j") == "abcdefghij");
        const string England = "\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F";
        check("C-L11: a subdivision flag keeps its tags; stray tags go", NoticeText.Clean(England) == England && NoticeText.Clean("x" + England + "\U000E0041y") == "x" + England + "y"
            && NoticeText.Clean("tag\U000E0041\U000E007F") == "tag" && NoticeProtocol.Clip(England, 100) == England);
        check("S3: Hangul fillers go, and a name of nothing but one falls back", NoticeText.Clean("a\u3164b\uFFA0c\u115Fd\u1160e") == "abcde"
            && NoticeIntake.FromPipe(new NoticeMessage { AppName = "\u3164" }).AppName == "brim-notify");
        string zalgo = "e" + new string('\u0301', 12) + "x";
        check("S3: a letter keeps at most four combining marks", NoticeText.Clean(zalgo) == "e" + new string('\u0301', NoticeProtocol.MaxMarks) + "x"
            && NoticeProtocol.Clip(zalgo, 100) == NoticeText.Clean(zalgo));
        check("S3: ordinary marks, a keycap and emoji variation stay", NoticeText.Clean("Tiếng Việt ở đây") == "Tiếng Việt ở đây" && NoticeText.Clean("1\uFE0F\u20E3 ❤\uFE0F") == "1\uFE0F\u20E3 ❤\uFE0F"
            && NoticeText.Clean("नमस्ते") == "नमस्ते");
        check("S3: a single line turns line breaks, tabs and other controls into spaces", NoticeText.Clean("a\nb\r\nc\td\u0085e\u000Bf", singleLine: true) == "a b c d e f"
            && NoticeText.Clean("a\nb") == "a\nb" && NoticeProtocol.Clip("a\nb", 10) == "a\nb");
        var titled = NoticeIntake.FromPipe(new NoticeMessage { Title = "Outlook\n已付款", AppName = "pay\nme" });
        check("S3: titles and names given over the wire stay on one line", NoticeHub.Words(titled).Title == "Outlook 已付款" && titled.AppName == "pay me");
        var run = new NoticeInput { Origin = NoticeOrigin.Cli, AppKey = "cli:x", AppName = "x", InstanceKey = "r", ExitCode = 0, Command = "ma\u200Bke\u202E\nall" };
        check("C-L11: the run command is cleaned before it words the title", NoticeHub.Words(run).Title == "make all 已完成");
        var actions = NoticeIntake.FromAgent(new NoticeMessage { Origin = "dbus", AppName = "app", NativeId = "1", Actions = [new() { Key = "ok", Label = "O\u202Ek\nyes" }, new() { Key = "go", Label = "\u200B" }] }, "Ubuntu", agentIsServer: true);
        check("C-L11: WSL action labels are cleaned, and a label of nothing shows its key", actions.Actions.Select(a => a.Label).SequenceEqual(["Ok yes", "go"]));
    }

    private static void PendingLimit(Action<string, bool> check)
    {
        var hub = new NoticeHub();
        int dropped = 0;
        hub.PendingDropped += count => dropped += count;
        var first = new List<Notice>();
        for (int i = 0; i < NoticeHub.MaxPending + 5; i++)
        {
            var notice = hub.Receive(Watch("loop", "w" + i) with { CreatedAt = T0.AddSeconds(i) }, T0.AddSeconds(i)).Notice!;
            if (i < 5) first.Add(notice);
        }
        check("C-M3: running stopwatches stop growing at MaxPending; the oldest created go, and the log hears of it", hub.Notices.Count == NoticeHub.MaxPending
            && first.All(n => hub.Find(n.Id) is null) && dropped == 5);
    }

    private static void Removals(Action<string, bool> check)
    {
        var earlier = new NoticeHub();
        var kept = earlier.Receive(new NoticeInput { Origin = NoticeOrigin.Windows, AppKey = "windows:Mail", AppName = "Mail", InstanceKey = "m1", Title = "旧邮件" }, T0).Notice!;
        var other = earlier.Receive(new NoticeInput { Origin = NoticeOrigin.Windows, AppKey = "windows:Mail", AppName = "Mail", InstanceKey = "m2", Title = "另一封" }, T0).Notice!;
        var saved = earlier.Notices.Select(n => n.Copy()).ToList();

        // The file could not be read at start-up; the same notices were in memory (moved from another folder) and one was deleted.
        var hub = new NoticeHub { TrackRemovals = true };
        hub.Load([kept.Copy()], [], T0.AddMinutes(1));
        hub.Delete(kept.Id);
        hub.Load(saved.Select(n => n.Copy()), [], T0.AddMinutes(2));
        check("C-L2: a notice deleted before the file was read does not come back with it", hub.Find(kept.Id) is null && hub.Find(other.Id) is not null);

        var cleared = new NoticeHub { TrackRemovals = true };
        var arrived = cleared.Receive(new NoticeInput { Origin = NoticeOrigin.Windows, AppKey = "windows:Chat", AppName = "Chat", InstanceKey = "c1", Title = "清空前" }, T0.AddMinutes(5)).Notice!;
        cleared.Clear(T0.AddMinutes(10));
        var after = cleared.Receive(new NoticeInput { Origin = NoticeOrigin.Windows, AppKey = "windows:Chat", AppName = "Chat", InstanceKey = "c2", Title = "清空后" }, T0.AddMinutes(11)).Notice!;
        cleared.Load(saved.Select(n => n.Copy()), [], T0.AddMinutes(12));
        check("C-L2: a clearing before the file was read keeps the file's notices out", cleared.Notices.SequenceEqual([after]) && cleared.Find(arrived.Id) is null);

        var timer = earlier.Receive(Watch("w", "keep"), T0).Notice!;
        var done = new NoticeHub { TrackRemovals = true };
        done.ClearDone(T0.AddHours(1));
        done.Load(earlier.Notices.Select(n => n.Copy()), [], T0.AddHours(1).AddMinutes(1));
        check("C-L2: the scheduled clearing keeps a running stopwatch from the file, as it would have", done.Notices.Count == 1 && done.Notices[0].Id == timer.Id);

        var read = new NoticeHub { TrackRemovals = true };
        read.Clear(T0.AddHours(2));
        read.TrackRemovals = false;
        read.Load(saved.Select(n => n.Copy()), [], T0.AddHours(2).AddMinutes(1));
        check("C-L2: once the file has been read, earlier removals are forgotten", read.Notices.Count == 2);
    }

    private static void WakeReminders(Action<string, bool> check)
    {
        var now = T0.AddHours(3);
        NoticeInput Reminder(string id, DateTimeOffset created, NoticeTiming? timing) => new()
        {
            Origin = NoticeOrigin.Windows, AppKey = "windows:Outlook", AppName = "Outlook", InstanceKey = id, Title = "会议", CreatedAt = created, Missed = true, Timing = timing
        };
        var hub = new NoticeHub();
        var fresh = hub.Receive(Reminder("r1", now.AddMinutes(-1), new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = now.AddMinutes(14) }), now);
        var starting = hub.Receive(Reminder("r2", now.AddMinutes(-15), new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = now }), now);
        var alarm = hub.Receive(Reminder("r3", now.AddMinutes(-2), new NoticeTiming { Kind = NoticeTimeKind.Alarm }), now);
        check("C-L14: a reminder read on waking that is due now still alerts", fresh.Present && starting.Present && alarm.Present && fresh.Reason is null);
        var old = hub.Receive(Reminder("r4", now.AddHours(-2), new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = now.AddHours(-1) }), now);
        var oldAlarm = hub.Receive(Reminder("r5", now.AddMinutes(-40), new NoticeTiming { Kind = NoticeTimeKind.Alarm }), now);
        var message = hub.Receive(Reminder("r6", now.AddMinutes(-1), null), now);
        check("C-L14: old reminders and other notices from the sleep are only recorded", old is { Present: false, Reason: "late" } && oldAlarm is { Present: false, Reason: "late" }
            && message is { Present: false, Reason: "late" });
    }

    private static void Links(Action<string, bool> check)
    {
        var hub = new NoticeHub();
        var piped = hub.Receive(Watch("b", "t1", new NoticeMessage { AppName = "b", Distro = "Ubuntu", ReplaceId = "t1" }), T0).Notice!;
        var agentStop = NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", AppName = "b", ReplaceId = "t1", Stop = true }, "Ubuntu", false, T0);
        check("S3: a pipe sender naming a distribution keeps its rules but marks its keys", piped.AppKey == "cli:Ubuntu:b" && NoticeIntake.ViaPipe(piped.InstanceKey)
            && !NoticeIntake.ViaPipe(agentStop.InstanceKey));
        // r3 M2: the other way round now: WSL's brim-notify started it over the pipe while the agent was down.
        check("S3: the agent's stop reaches a timer the pipe started under its distribution (r3 M2)", hub.Receive(agentStop, T0.AddSeconds(1)).Receipt == NoticeReceipt.Updated && piped.Timing!.Stopped);
        var real = hub.Receive(NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", AppName = "b", ReplaceId = "t2", Kind = "stopwatch" }, "Ubuntu", false, T0) with
        { Timing = new NoticeTiming { Kind = NoticeTimeKind.Stopwatch, StartsAt = T0, Native = true } }, T0.AddSeconds(2)).Notice!;
        var spoof = hub.Receive(NoticeIntake.FromPipe(new NoticeMessage { AppName = "b", Distro = "Ubuntu", ReplaceId = "t2", Title = "伪造" }, T0), T0.AddSeconds(3));
        var spoofStop = hub.Receive(NoticeIntake.FromPipe(new NoticeMessage { AppName = "b", Distro = "Ubuntu", ReplaceId = "t2", Stop = true }, T0), T0.AddSeconds(4));
        check("S3: and a pipe sender cannot replace or stop the distribution's own", spoof.Receipt == NoticeReceipt.Accepted && spoof.Notice != real && real.Title != "伪造"
            && spoofStop.Reason == "no-timer" && !real.Timing!.Stopped);
        var byAgent = hub.Receive(NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", AppName = "b", EventId = "job", Title = "a" }, "Ubuntu", false, T0), T0.AddSeconds(5));
        var byPipe = hub.Receive(NoticeIntake.FromPipe(new NoticeMessage { AppName = "b", Distro = "Ubuntu", EventId = "job", Title = "p" }, T0), T0.AddSeconds(6));
        check("S3: an --id over the pipe does not mark the distribution's message a retry", byAgent.Receipt == NoticeReceipt.Accepted && byPipe.Receipt == NoticeReceipt.Accepted);
        var windows = NoticeIntake.FromPipe(new NoticeMessage { AppName = "b", EventId = "job" });
        check("S3: a Windows sender without a distribution keeps the plain key", !NoticeIntake.ViaPipe(windows.InstanceKey) && windows.InstanceKey == "cli::b:job");

        // A notice saved before the key took the sender's name (cli:{distro}:{id}).
        var restarted = new NoticeHub();
        restarted.Load([new Notice { Origin = NoticeOrigin.Cli, AppKey = "cli:Ubuntu:b", AppName = "b", Distro = "Ubuntu", InstanceKey = "cli:Ubuntu:build", EventId = "build", Title = "构建中", ReceivedAt = T0, UpdatedAt = T0 }], [], T0.AddMinutes(1));
        var again = restarted.Receive(NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", AppName = "b", EventId = "build", Title = "构建中" }, "Ubuntu", false, T0), T0.AddMinutes(2));
        check("C-L4: the same --id after the upgrade is still recognized, not a second notice", again.Receipt == NoticeReceipt.Duplicate && restarted.Notices.Count == 1);
    }

    private static void UrgentPerSource(Action<string, bool> check)
    {
        AttentionItem Alarm(string lane, string key, string group) => new()
        {
            Lane = lane, Kind = AttentionKind.Notice, Key = key, Group = group, Priority = NoticePriority.High, Urgent = true, Duration = TimeSpan.FromSeconds(1)
        };
        var c = new AttentionCoordinator();
        var now = T0;
        int shown = 0;
        for (int i = 0; i < 8; i++)
        {
            foreach (var change in c.Submit(Alarm("main", "t" + i, "cli:Ubuntu:job" + i), now)) if (change.Started is not null) shown++;
            now += TimeSpan.FromSeconds(2);
            foreach (var change in c.Tick(now)) if (change.Started is not null) shown++;
        }
        check("S4: a script's timers, under any names, ring at most UrgentLimit times a minute", shown == AttentionCoordinator.UrgentLimit);
        check("S4: an alarm of Windows' Clock rings at once all the same", c.Submit(Alarm("second", "clock", "windows:Microsoft.WindowsAlarms"), now) is [{ Started.Key: "clock" }]);
    }

    private static void HistoryLinks(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "audit2-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var history = new NoticeHistory(folder, new NoticeHistory.PlainProtector());
        check("C-L3: no file in a folder that is there is empty", history.Load(out _, out _) == NoticeHistoryState.Empty);
        check("C-L3: a folder whose share is gone is unavailable, not empty", new NoticeHistory(@"\\brimdeck-no-such-host.invalid\share\notices", new NoticeHistory.PlainProtector()).Load(out _, out _) == NoticeHistoryState.Unavailable);
        check("Each new state says why", NoticeHistory.Describe(NoticeHistoryState.Denied).Length > 0);

        // Links need the right to make them (developer mode or an administrator); without it these checks have nothing to test.
        var elsewhere = Path.Combine(root, "audit2-elsewhere-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        var target = new NoticeHistory(elsewhere, new NoticeHistory.PlainProtector());
        target.Save([new Notice { AppKey = "cli:a", Title = "别人的", ReceivedAt = T0, UpdatedAt = T0 }], []);
        bool fileLink = Try(() => File.CreateSymbolicLink(history.FilePath, target.FilePath));
        if (fileLink)
        {
            check("S1: a notices file that is a link is not followed", history.Load(out var none, out _) == NoticeHistoryState.Refused && none.Count == 0);
            history.Delete();
            check("S1: clearing removes the link, not what it leads to", !File.Exists(history.FilePath) && !NoticeHistory.Linked(history.FilePath) && File.Exists(target.FilePath));
        }
        var linkedFolder = Path.Combine(root, "audit2-linked-" + Guid.NewGuid().ToString("N"));
        if (Try(() => Directory.CreateSymbolicLink(linkedFolder, elsewhere)))
        {
            var through = new NoticeHistory(linkedFolder, new NoticeHistory.PlainProtector());
            check("S1: a folder that became a link is neither read, written nor cleared", NoticeHistory.Linked(linkedFolder) && through.Load(out _, out _) == NoticeHistoryState.Refused
                && !through.Save([new Notice { AppKey = "cli:a", Title = "x" }], []) && Act(through.Delete) && target.Load(out var still, out _) == NoticeHistoryState.Loaded && still.Single().Title == "别人的");
            Directory.Delete(linkedFolder);
        }
        Directory.Delete(folder, true); Directory.Delete(elsewhere, true);
    }

    private static bool Try(Action action)
    {
        try { action(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Act(Action action) { action(); return true; }
}
