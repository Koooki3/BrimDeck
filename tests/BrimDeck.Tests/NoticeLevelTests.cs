using System.Text;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

// Notice levels, alignment, showing on every screen, and what the protocol and brim-notify carry of them.
internal static class NoticeLevelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    public static void Run(string root, Action<string, bool> check)
    {
        Resolve(check);
        Hub(check);
        Screens(check);
        Look(check);
        SettingsValues(root, check);
        Wire(check);
        CommandLine(check);
    }

    private static Notice Made(NoticeCategory category = NoticeCategory.Message, NoticePriority priority = NoticePriority.Normal, NoticeLevel level = NoticeLevel.Auto,
        bool call = false, NoticeTiming? timing = null) => new() { AppKey = "windows:Chat", Title = "t", Category = category, Priority = priority, Level = level, Call = call, Timing = timing };

    private static NoticeLevel Of(Notice notice, NoticeAppRule? rule = null, NoticeLevel fallback = NoticeLevel.Glow) => NoticeLevels.Resolve(notice, rule, fallback, T0);

    private static void Resolve(Action<string, bool> check)
    {
        check("Level: an ordinary message takes the default level, one of Plain, Glow and Lively", Of(Made()) == NoticeLevel.Glow && Of(Made(), fallback: NoticeLevel.Lively) == NoticeLevel.Lively
            && Of(Made(), fallback: NoticeLevel.Plain) == NoticeLevel.Plain && Of(Made(), fallback: NoticeLevel.Persistent) == NoticeLevel.Glow && Of(Made(), fallback: NoticeLevel.Silent) == NoticeLevel.Glow
            && Of(Made(), fallback: NoticeLevel.Auto) == NoticeLevel.Glow);
        check("Level: high priority, errors, warnings, tasks and results are lively", Of(Made(priority: NoticePriority.High), fallback: NoticeLevel.Plain) == NoticeLevel.Lively
            && new[] { NoticeCategory.Error, NoticeCategory.Warning, NoticeCategory.Task, NoticeCategory.Success }.All(category => Of(Made(category), fallback: NoticeLevel.Plain) == NoticeLevel.Lively));
        check("Level: low priority and plain information are plain", Of(Made(priority: NoticePriority.Low), fallback: NoticeLevel.Lively) == NoticeLevel.Plain
            && Of(Made(NoticeCategory.Info), fallback: NoticeLevel.Lively) == NoticeLevel.Plain && Of(Made(NoticeCategory.Info, NoticePriority.High)) == NoticeLevel.Lively);
        check("Level: the sender's level comes before the category and the default", Of(Made(NoticeCategory.Info, level: NoticeLevel.Lively)) == NoticeLevel.Lively
            && Of(Made(NoticeCategory.Error, level: NoticeLevel.Plain)) == NoticeLevel.Plain && Of(Made(level: NoticeLevel.Silent)) == NoticeLevel.Silent
            && Of(Made(level: NoticeLevel.Glow), fallback: NoticeLevel.Lively) == NoticeLevel.Glow && Of(Made(level: NoticeLevel.Persistent)) == NoticeLevel.Persistent);
        check("Level: a call, a ringing alarm, a countdown that ended and an event's reminder stay until handled, whatever the sender asked",
            Of(Made(call: true, priority: NoticePriority.High)) == NoticeLevel.Persistent
            && Of(Made(timing: new NoticeTiming { Kind = NoticeTimeKind.Alarm }, level: NoticeLevel.Plain)) == NoticeLevel.Persistent
            && Of(Made(timing: new NoticeTiming { Kind = NoticeTimeKind.Countdown, Native = true, Stage = 1 }, level: NoticeLevel.Silent)) == NoticeLevel.Persistent
            && Of(Made(timing: new NoticeTiming { Kind = NoticeTimeKind.Event })) == NoticeLevel.Persistent);
        check("Level: a countdown still running, a stopwatch and new mail are ordinary notices",
            Of(Made(timing: new NoticeTiming { Kind = NoticeTimeKind.Countdown, Native = true, EndsAt = T0.AddMinutes(5) })) == NoticeLevel.Glow
            && Of(Made(timing: new NoticeTiming { Kind = NoticeTimeKind.Stopwatch, Native = true, StartsAt = T0 })) == NoticeLevel.Glow
            && Of(Made(timing: new NoticeTiming { Kind = NoticeTimeKind.Mail })) == NoticeLevel.Glow);
        check("Level: a call or an alarm of an application set to low priority does not stay", Of(Made(call: true, priority: NoticePriority.Low)) == NoticeLevel.Plain
            && Of(Made(timing: new NoticeTiming { Kind = NoticeTimeKind.Alarm }, priority: NoticePriority.Low)) == NoticeLevel.Plain);
        var plain = new NoticeAppRule { Key = "windows:Chat", Level = NoticeLevel.Plain };
        check("Level: the user's level for the application comes first", Of(Made(call: true, priority: NoticePriority.High), plain) == NoticeLevel.Plain
            && Of(Made(level: NoticeLevel.Persistent), plain) == NoticeLevel.Plain && Of(Made(NoticeCategory.Error), new NoticeAppRule { Level = NoticeLevel.Silent }) == NoticeLevel.Silent
            && Of(Made(NoticeCategory.Info), new NoticeAppRule { Level = NoticeLevel.Lively }) == NoticeLevel.Lively && Of(Made(level: NoticeLevel.Silent), new NoticeAppRule { Level = NoticeLevel.Glow }) == NoticeLevel.Glow);
        check("Level: a rule without a level, or with one that is not the user's to choose, decides nothing", Of(Made(NoticeCategory.Error), new NoticeAppRule()) == NoticeLevel.Lively
            && Of(Made(), new NoticeAppRule { Level = NoticeLevel.Persistent }) == NoticeLevel.Glow && new NoticeAppRule().Level == NoticeLevel.Auto);
        var gone = Made(level: NoticeLevel.Persistent); gone.SourcePresent = false;
        var endedCall = Made(call: true); endedCall.SourcePresent = false;
        check("Level: with nothing left to handle (gone from its source, its time stopped) a notice no longer stays", Of(gone) == NoticeLevel.Lively && Of(endedCall) == NoticeLevel.Lively
            && Of(Made(timing: new NoticeTiming { Kind = NoticeTimeKind.Stopwatch, Native = true, Stopped = true }, level: NoticeLevel.Persistent)) == NoticeLevel.Lively
            && Of(Made(timing: new NoticeTiming { Kind = NoticeTimeKind.Alarm, Stopped = true })) == NoticeLevel.Glow);

        // An application's alarms count together toward the urgent limit, whatever their titles.
        var coordinator = new AttentionCoordinator();
        int rang = 0;
        for (int i = 0; i < 6; i++)
            rang += coordinator.Submit(new AttentionItem
            {
                Lane = "lane" + i, Kind = AttentionKind.Notice, Key = "alarm" + i, Group = AttentionCoordinator.GroupOf("windows:Clock", "Alarm " + i), Urgent = true,
                Priority = NoticePriority.High, Duration = TimeSpan.FromSeconds(5)
            }, T0).Count(change => change.Started is not null);
        check("Level: titles in the group do not get an application's alarms round the urgent limit", rang == AttentionCoordinator.UrgentLimit);
    }

    private static NoticeInput Cli(string id, string? replace = null, NoticeLevel level = NoticeLevel.Auto, NoticeAlign? align = null, string title = "Deploy", bool stop = false) => new()
    {
        Origin = NoticeOrigin.Cli, AppKey = "cli:deploy", AppName = "deploy", InstanceKey = "cli::deploy:" + id, ReplaceKey = replace, Title = stop ? "" : title, Level = level, Align = align,
        StopTiming = stop
    };

    private static void Hub(Action<string, bool> check)
    {
        var rules = new Dictionary<string, NoticeAppRule>();
        var hub = new NoticeHub(key => rules.GetValueOrDefault(key));
        var silent = hub.Receive(Cli("s", level: NoticeLevel.Silent), T0);
        check("Hub: a notice sent as silent is listed, unviewed, and not shown", silent.Receipt == NoticeReceipt.Accepted && !silent.Present && hub.UnviewedCount == 1);
        rules["windows:Quiet"] = new NoticeAppRule { Key = "windows:Quiet", Level = NoticeLevel.Silent };
        var quiet = hub.Receive(new NoticeInput { Origin = NoticeOrigin.Windows, AppKey = "windows:Quiet", AppName = "Quiet", InstanceKey = "q", Title = "Hi", Call = true }, T0);
        check("Hub: the Silent level for an application shows as Muted does, also for its calls", quiet.Receipt == NoticeReceipt.Accepted && !quiet.Present);
        rules["cli:deploy"] = new NoticeAppRule { Key = "cli:deploy", Level = NoticeLevel.Lively };
        check("Hub: the user's level for the application comes before a sender's silent", hub.Receive(Cli("s2", level: NoticeLevel.Silent), T0).Present);
        rules.Remove("cli:deploy");

        var stay = hub.Receive(Cli("p1", "job", NoticeLevel.Persistent, NoticeAlign.Right), T0);
        var notice = stay.Notice!;
        check("Hub: the level and the alignment asked for are kept with the notice", stay.Present && notice is { Level: NoticeLevel.Persistent, Align: NoticeAlign.Right }
            && Of(notice) == NoticeLevel.Persistent);
        var same = hub.Receive(Cli("p2", "job", NoticeLevel.Persistent, NoticeAlign.Right), T0.AddSeconds(1));
        check("Hub: the same words sent again as persistent do not show again", same.Receipt == NoticeReceipt.Updated && same.Notice == notice && !same.Present);
        int changes = 0; hub.Changed += () => changes++;
        var closed = hub.Receive(Cli("p3", "job", stop: true), T0.AddSeconds(2));
        check("Hub: closing a notice by its key marks it gone from its sender, so it no longer stays", closed.Receipt == NoticeReceipt.Updated && closed.Notice == notice && !closed.Present
            && !notice.SourcePresent && changes == 1 && Of(notice) == NoticeLevel.Lively && hub.Notices.Contains(notice));
        check("Hub: closing it again, or an unknown key, finds nothing", hub.Receive(Cli("p4", "job", stop: true), T0.AddSeconds(3)) is { Receipt: NoticeReceipt.Filtered, Reason: "no-timer" }
            && hub.Receive(Cli("p5", "nope", stop: true), T0.AddSeconds(3)) is { Receipt: NoticeReceipt.Filtered, Reason: "no-timer" });
        var again = hub.Receive(Cli("p6", "job", NoticeLevel.Persistent), T0.AddSeconds(4));
        check("Hub: sent again as persistent after it was closed, it shows and stays again", again.Present && again.Notice == notice && notice.SourcePresent && Of(notice) == NoticeLevel.Persistent);
        var ordinary = hub.Receive(Cli("p7", "job"), T0.AddSeconds(5));
        check("Hub: sent again with the same words as an ordinary notice, it no longer stays and does not show again", !ordinary.Present && notice.Level == NoticeLevel.Auto
            && Of(notice) == NoticeLevel.Glow);
        var raised = hub.Receive(Cli("p8", "job", NoticeLevel.Persistent), T0.AddSeconds(6));
        check("Hub: the same words sent as persistent for the first time show again", raised.Present && Of(notice) == NoticeLevel.Persistent);

        // The whole way from brim-notify's words to the hub.
        var wire = new NoticeHub();
        NoticeOutcome Send(params string[] args)
        {
            var message = NotifyCommandLine.Parse(args).Message!;
            return NoticeProtocol.Validate(message) is null ? wire.Receive(NoticeIntake.FromPipe(message, T0), T0) : new(NoticeReceipt.Filtered, null, false, "invalid");
        }
        var sent = Send("Deploying", "-r", "deploy", "-l", "persistent", "--align", "left");
        check("Hub: brim-notify -l persistent --align left reaches the notice", sent is { Present: true, Notice: { Level: NoticeLevel.Persistent, Align: NoticeAlign.Left, SourcePresent: true } });
        var ended = Send("close", "deploy");
        check("Hub: brim-notify close KEY closes it", ended.Receipt == NoticeReceipt.Updated && ended.Notice == sent.Notice && !sent.Notice!.SourcePresent);
        var timer = Send("timer", "25m", "Tea", "-r", "tea");
        check("Hub: close and stop are one message, so close ends a timer as stop does", Send("close", "tea").Receipt == NoticeReceipt.Updated && timer.Notice!.Timing!.Stopped
            && timer.Notice.SourcePresent);

        // A countdown that comes due for an application with the Silent level is recorded, not rung.
        var timers = new NoticeHub(key => new NoticeAppRule { Key = key, Level = NoticeLevel.Silent });
        timers.Receive(NoticeIntake.FromPipe(NotifyCommandLine.Parse(["timer", "1m", "Tea"]).Message!, T0), T0);
        check("Hub: a countdown of an application set to Silent does not ring when it is due", timers.FireDue(T0.AddMinutes(1)).Count == 0 && timers.Notices.Single().Timing!.Stage == 1);
    }

    private static void Screens(Action<string, bool> check)
    {
        NoticeRouting.Candidate[] panels = [new("main", 1, true, true), new("second", 2, false, true), new("hidden", 3, false, false)];
        check("Screens: All shows on every panel that can show notices", NoticeRouting.Lanes(panels, 2, NoticeScreen.All).SequenceEqual(["main", "second"])
            && NoticeRouting.Lanes(panels, 3, NoticeScreen.All).SequenceEqual(["main", "second"]) && NoticeRouting.Lanes([], 1, NoticeScreen.All).Count == 0);
        check("Screens: the screen in use and the main panel's each name one panel, or none", NoticeRouting.Lanes(panels, 2, NoticeScreen.Foreground).SequenceEqual(["second"])
            && NoticeRouting.Lanes(panels, 3, NoticeScreen.Foreground).Count == 0 && NoticeRouting.Lanes(panels, 2, NoticeScreen.Main).SequenceEqual(["main"]));
        check("Screens: All is saved after the choices there were", (int)NoticeScreen.Foreground == 0 && (int)NoticeScreen.Main == 1 && (int)NoticeScreen.All == 2);
    }

    private static void Look(Action<string, bool> check)
    {
        IslandForm[] forms = [IslandForm.Capsule, IslandForm.Notch, IslandForm.Line];
        check("Effect: a plain notice keeps the island still; the indicator, which shows no words, lights up once", NoticeLevels.Effect(NoticeLevel.Plain, false, IslandForm.Capsule) == NoticeEffect.Still
            && NoticeLevels.Effect(NoticeLevel.Plain, false, IslandForm.Notch) == NoticeEffect.Still && NoticeLevels.Effect(NoticeLevel.Plain, false, IslandForm.Line) == NoticeEffect.Brighten);
        check("Effect: glow flashes the backlight; lively and persistent move the liquid as well", forms.All(form => NoticeLevels.Effect(NoticeLevel.Glow, false, form) == NoticeEffect.Glow
            && NoticeLevels.Effect(NoticeLevel.Lively, false, form) == NoticeEffect.Liquid && NoticeLevels.Effect(NoticeLevel.Persistent, false, form) == NoticeEffect.Liquid));
        check("Effect: a notice shown beyond the rate limit plays as a plain one", NoticeLevels.Effect(NoticeLevel.Lively, true, IslandForm.Capsule) == NoticeEffect.Still
            && NoticeLevels.Effect(NoticeLevel.Glow, true, IslandForm.Notch) == NoticeEffect.Still && NoticeLevels.Effect(NoticeLevel.Glow, true, IslandForm.Line) == NoticeEffect.Brighten);
        check("Effect: the backlight alone plays until its second flash has faded", NoticeLevels.EffectLength(NoticeEffect.Glow, IslandForm.Capsule) == IslandLiquid.GlowLength
            && IslandLiquid.GlowAt(IslandLiquid.GlowLength - .05) > 0 && IslandLiquid.GlowAt(IslandLiquid.GlowLength) == 0);
        check("Effect: the indicator breathes twice for glow and lights up once for plain", NoticeLevels.EffectLength(NoticeEffect.Glow, IslandForm.Line) == 2 * IslandLiquid.Breath
            && NoticeLevels.EffectLength(NoticeEffect.Brighten, IslandForm.Line) == IslandLiquid.Breath && Math.Abs(IslandLiquid.FlashAt(IslandLiquid.Breath / 2) - 1) < 1e-9
            && IslandLiquid.FlashAt(IslandLiquid.Breath * 1.5) == 0 && IslandLiquid.BreathAt(IslandLiquid.Breath * 1.5) > .99 && IslandLiquid.FlashAt(0) == 0);
        var still = new IslandLiquid(IslandForm.Capsule, 196, 32, liquid: false);
        var circles = new List<IslandLiquid.Circle>();
        still.Beads(.2, circles);
        var line = new IslandLiquid(IslandForm.Line, 56, 4, liquid: false);
        var necks = new List<IslandLiquid.Neck>();
        var drops = new List<IslandLiquid.Circle>();
        line.LineDrops(.3, drops, necks);
        check("Effect: without the liquid the outline keeps still and nothing leaves it", still.Outline.All(point => still.EdgeOut(point.S, .2) == 0) && circles.Count == 0
            && line.LineHalf(10, .3) == 2 && line.LineEnd(.3, 1) == 28 && drops.Count == 0 && necks.Count == 0);

        check("Align: the icon and the words sit at the left, in the middle or at the right of the room they have", NoticeAligns.Shift(100, 40, NoticeAlign.Left) == 0
            && NoticeAligns.Shift(100, 40, NoticeAlign.Center) == 30 && NoticeAligns.Shift(100, 40, NoticeAlign.Right) == 60 && NoticeAligns.Shift(101, 40, NoticeAlign.Center) == 30);
        check("Align: words that fill the room or are longer start at the left, where they scroll", NoticeAligns.Shift(100, 100, NoticeAlign.Right) == 0
            && NoticeAligns.Shift(100, 140, NoticeAlign.Center) == 0 && NoticeAligns.Shift(0, 10, NoticeAlign.Right) == 0);
    }

    private static void SettingsValues(string root, Action<string, bool> check)
    {
        var defaults = new DeckSettings();
        check("Settings: by default notices glow, sit in the middle and show on the screen in use", defaults.NoticeDefaultLevel == NoticeLevel.Glow && defaults.NoticeAlign == NoticeAlign.Center
            && defaults.NoticeScreen == NoticeScreen.Foreground);
        var odd = new DeckSettings
        {
            NoticeDefaultLevel = NoticeLevel.Persistent, NoticeAlign = (NoticeAlign)9, NoticeScreen = NoticeScreen.All,
            NoticeApps = [new() { Key = "a", Level = NoticeLevel.Persistent }, new() { Key = "b", Level = NoticeLevel.Silent }, new() { Key = "c", Level = (NoticeLevel)40 }, new() { Key = "d", Level = NoticeLevel.Lively }]
        };
        odd.Normalize();
        check("Settings: the default level is Plain, Glow or Lively; an application's may also be Silent, never Persistent", odd.NoticeDefaultLevel == NoticeLevel.Glow && odd.NoticeAlign == NoticeAlign.Center
            && odd.NoticeScreen == NoticeScreen.All && odd.NoticeApps.Select(rule => rule.Level).SequenceEqual([NoticeLevel.Auto, NoticeLevel.Silent, NoticeLevel.Auto, NoticeLevel.Lively]));
        foreach (var level in new[] { NoticeLevel.Auto, NoticeLevel.Silent })
        {
            var other = new DeckSettings { NoticeDefaultLevel = level };
            other.Normalize();
            check($"Settings: {level} is not a default level", other.NoticeDefaultLevel == NoticeLevel.Glow);
        }
        var copy = odd.Copy();
        copy.NoticeApps[1].Level = NoticeLevel.Plain; copy.NoticeAlign = NoticeAlign.Right;
        check("Settings: a copy carries the level and the alignment and shares no rule", copy.NoticeDefaultLevel == NoticeLevel.Glow && copy.NoticeScreen == NoticeScreen.All
            && odd.NoticeApps[1].Level == NoticeLevel.Silent && odd.NoticeAlign == NoticeAlign.Center);
        var store = new SettingsStore(Path.Combine(root, "notice-level-settings"), "1.2.0");
        store.Save(new DeckSettings
        {
            NoticeDefaultLevel = NoticeLevel.Lively, NoticeAlign = NoticeAlign.Right, NoticeScreen = NoticeScreen.All,
            NoticeApps = [new() { Key = "windows:X", Name = "X", Level = NoticeLevel.Plain }]
        });
        var back = store.Load();
        check("Settings: the level, the alignment, every screen and an application's level survive saving", back.NoticeDefaultLevel == NoticeLevel.Lively && back.NoticeAlign == NoticeAlign.Right
            && back.NoticeScreen == NoticeScreen.All && back.NoticeRule("windows:X")?.Level == NoticeLevel.Plain);
        File.WriteAllText(store.FilePath, "{\"NoticesEnabled\":true,\"NoticeScreen\":1,\"NoticeApps\":[{\"Key\":\"windows:X\",\"Muted\":true}]}");
        var old = store.Load();
        check("Settings: a file saved before they existed reads the defaults and keeps its screen choice", old.NoticeDefaultLevel == NoticeLevel.Glow && old.NoticeAlign == NoticeAlign.Center
            && old.NoticeScreen == NoticeScreen.Main && old.NoticeRule("windows:X") is { Muted: true, Level: NoticeLevel.Auto });
        check("Settings: the level and the alignment are kept for all profiles, as the other notice settings are", !Profiles.IsProfileKey(nameof(DeckSettings.NoticeDefaultLevel))
            && !Profiles.IsProfileKey(nameof(DeckSettings.NoticeAlign)) && !Profiles.IsProfileKey(nameof(DeckSettings.NoticeScreen)));
    }

    private static void Wire(Action<string, bool> check)
    {
        string Json(NoticeMessage message) => Encoding.UTF8.GetString(NoticeProtocol.Serialize(message));
        check("Wire: the level and the alignment travel as plain words and are left out when not given",
            Json(new NoticeMessage { Title = "t", Level = "persistent", Align = "right" }) is var json && json.Contains("\"level\":\"persistent\"") && json.Contains("\"align\":\"right\"")
            && !Json(new NoticeMessage { Title = "t" }).Contains("level") && !Json(new NoticeMessage { Title = "t" }).Contains("align"));
        var old = NoticeProtocol.Deserialize("{\"version\":1,\"type\":\"notify\",\"title\":\"t\"}"u8)!;
        check("Wire: a message without them is valid and leaves both to BrimDeck", old is { Level: null, Align: null } && NoticeProtocol.Validate(old) is null
            && NoticeIntake.FromPipe(old, T0) is { Level: NoticeLevel.Auto, Align: null });
        var later = new NoticeMessage { Title = "t", Level = "sparkle", Align = "justify" };
        check("Wire: values BrimDeck does not know are not refused; they read as not given", NoticeProtocol.Validate(later) is null
            && NoticeIntake.FromPipe(later, T0) is { Level: NoticeLevel.Auto, Align: null });
        var longLevel = new NoticeMessage { Title = "t", Level = new string('x', 500), Align = new string('y', 500) };
        check("Wire: an over-long level or alignment is cut like the other short fields", NoticeProtocol.Validate(longLevel) is null && longLevel.Level!.Length == NoticeTimes.MaxText
            && longLevel.Align!.Length == NoticeTimes.MaxText);
        check("Wire: every level and side is read, whatever its case", NoticeKeys.Level("silent") == NoticeLevel.Silent && NoticeKeys.Level("Plain") == NoticeLevel.Plain
            && NoticeKeys.Level("GLOW") == NoticeLevel.Glow && NoticeKeys.Level(" lively ") == NoticeLevel.Lively && NoticeKeys.Level("persistent") == NoticeLevel.Persistent
            && NoticeKeys.Level("auto") == NoticeLevel.Auto && NoticeKeys.Level(null) == NoticeLevel.Auto && NoticeKeys.Align("left") == NoticeAlign.Left
            && NoticeKeys.Align("Center") == NoticeAlign.Center && NoticeKeys.Align("RIGHT") == NoticeAlign.Right && NoticeKeys.Align("") is null && NoticeKeys.Align(null) is null);
        check("Wire: a notice through a WSL agent carries them too", NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", Title = "t", Level = "glow", Align = "center" }, "Ubuntu", false, T0)
            is { Level: NoticeLevel.Glow, Align: NoticeAlign.Center });
    }

    private static void CommandLine(Action<string, bool> check)
    {
        var send = NotifyCommandLine.Parse(["Deploying", "-r", "deploy", "-l", "Persistent", "--align", "RIGHT"]);
        check("brim-notify: -l and --align set the level and the alignment", send is { Kind: NotifyCommandKind.Send, Message: { Level: "persistent", Align: "right", ReplaceId: "deploy", Title: "Deploying" } }
            && NotifyCommandLine.Parse(["x", "--level=glow"]).Message?.Level == "glow" && NotifyCommandLine.Parse(["x"]).Message is { Level: null, Align: null });
        check("brim-notify: an unknown level or side is refused before anything is sent", NotifyCommandLine.Parse(["x", "-l", "loud"]).Kind == NotifyCommandKind.Error
            && NotifyCommandLine.Parse(["x", "--align", "middle"]).Kind == NotifyCommandKind.Error && NotifyCommandLine.Parse(["x", "-l"]).Kind == NotifyCommandKind.Error);
        var close = NotifyCommandLine.Parse(["close", "deploy", "-s", "ci"]);
        var stop = NotifyCommandLine.Parse(["stop", "deploy", "-s", "ci"]);
        check("brim-notify: close KEY sends what stop KEY sends", close is { Kind: NotifyCommandKind.Send, Message: { Stop: true, ReplaceId: "deploy", AppName: "ci", Title: null } }
            && NoticeProtocol.Validate(close.Message) is null && !NotifyCommandLine.PrintsKey(close.Message)
            && Encoding.UTF8.GetString(NoticeProtocol.Serialize(close.Message)) == Encoding.UTF8.GetString(NoticeProtocol.Serialize(stop.Message!)));
        check("brim-notify: close takes one key and a source only", NotifyCommandLine.Parse(["close"]).Kind == NotifyCommandKind.Error && NotifyCommandLine.Parse(["close", "a", "b"]).Kind == NotifyCommandKind.Error
            && NotifyCommandLine.Parse(["close", "a", "-l", "plain"]).Kind == NotifyCommandKind.Error && NotifyCommandLine.Parse(["stop", "a", "--align", "left"]).Kind == NotifyCommandKind.Error
            && NotifyCommandLine.Parse(["close", "a", "-t", "x"]) is { Kind: NotifyCommandKind.Error, Error: { } why } && why.StartsWith("close"));
        var run = NotifyCommandLine.Parse(["run", "-l", "persistent", "--align", "left", "--", "make"]);
        check("brim-notify: run shows its result at the level and alignment asked for", run.Kind == NotifyCommandKind.Run
            && NotifyCommandLine.RunResult(run.Message!, 0, TimeSpan.FromSeconds(1)) is { Level: "persistent", Align: "left" });
        check("brim-notify: a timer takes a level too", NotifyCommandLine.Parse(["timer", "25m", "Tea", "-l", "lively"]) is { Kind: NotifyCommandKind.Send, Message: { Level: "lively", Kind: NoticeTimes.Countdown } });
        check("brim-notify: the help names the new options and close", NotifyCommandLine.Usage.Contains("-l, --level LEVEL") && NotifyCommandLine.Usage.Contains("--align SIDE")
            && NotifyCommandLine.Usage.Contains("brim-notify close KEY") && NotifyCommandLine.Usage.Contains("persistent"));
    }
}
