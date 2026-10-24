using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;
using BrimDeck.Protocol.DBus;

// Time-kind notices: brim-notify's timer, stopwatch and event, the times they carry, BrimDeck's own reminders, the live words,
// and sorting Windows' Clock, Outlook, calendar and Teams notifications into kinds. Fixed clocks and made-up time zones only.
internal static class NoticeTimeTests
{
    // 14:00 in UTC+8, a Saturday.
    private static readonly DateTimeOffset TimeNow = new(2026, 10, 24, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo China = TimeZoneInfo.CreateCustomTimeZone("Test/China", TimeSpan.FromHours(8), "Test China", "Test China");
    // UTC+1, with daylight saving (+2) from the last Sunday of March 02:00 to the last Sunday of October 03:00.
    private static readonly TimeZoneInfo Summer = TimeZoneInfo.CreateCustomTimeZone("Test/Summer", TimeSpan.FromHours(1), "Test Summer", "Test Standard", "Test Daylight",
    [
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday))
    ]);

    public static void Run(Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try
        {
            Durations(check);
            Times(check);
            Checks(check);
            CommandLine(check);
            Resolve(check);
            Words(check);
            Hub(check);
            Classify(check);
            Urgent(check);
        }
        finally { Loc.Use(language); }
    }

    private static void Durations(Action<string, bool> check)
    {
        check("Durations read units in order", NoticeTimes.ParseDuration("25m") == TimeSpan.FromMinutes(25) && NoticeTimes.ParseDuration("1h30m") == TimeSpan.FromMinutes(90)
            && NoticeTimes.ParseDuration("90s") == TimeSpan.FromSeconds(90) && NoticeTimes.ParseDuration("2d") == TimeSpan.FromDays(2) && NoticeTimes.ParseDuration("1H 5m") == TimeSpan.FromMinutes(65));
        check("Malformed durations are refused", new[] { "", " ", "m", "5", "5x", "30m1h", "1h1h", "1234567m", "-5m", "5m-", new string('1', 50) + "m" }.All(text => NoticeTimes.ParseDuration(text) is null));
    }

    private static void Times(Action<string, bool> check)
    {
        check("A clock time is its next occurrence today", NoticeTimes.ParseTime("15:30", TimeNow, China) == new DateTimeOffset(2026, 10, 24, 7, 30, 0, TimeSpan.Zero));
        check("A clock time already past is tomorrow's", NoticeTimes.ParseTime("13:00", TimeNow, China) == new DateTimeOffset(2026, 10, 25, 5, 0, 0, TimeSpan.Zero)
            && NoticeTimes.ParseTime("14:00", TimeNow, China) == new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.Zero));
        check("A time with an offset is absolute", NoticeTimes.ParseTime("2026-10-24T15:30:00+02:00", TimeNow, China) == new DateTimeOffset(2026, 10, 24, 13, 30, 0, TimeSpan.Zero)
            && NoticeTimes.ParseTime("2026-10-24T15:30:00Z", TimeNow, China) == new DateTimeOffset(2026, 10, 24, 15, 30, 0, TimeSpan.Zero));
        check("A date and time without an offset is read in Windows' zone", NoticeTimes.ParseTime("2026-10-26 09:00", TimeNow, China) == new DateTimeOffset(2026, 10, 26, 1, 0, 0, TimeSpan.Zero));
        check("Text that is not a time is refused", new[] { "soon", "25:00", "15:30+08:00", "", "2026-13-01 10:00" }.All(text => NoticeTimes.ParseTime(text, TimeNow, China) is null));
        check("A wall-clock time daylight saving skips moves to the end of the gap",
            NoticeTimes.FromLocal(new DateTime(2026, 3, 29, 2, 30, 0), Summer) == new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero));
        check("A wall-clock time daylight saving repeats is the earlier one",
            NoticeTimes.FromLocal(new DateTime(2026, 10, 25, 2, 30, 0), Summer) == new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero));
        // 22:00 summer time on the Saturday; tomorrow 15:30 is in winter time (+1).
        check("Tomorrow's clock time takes tomorrow's offset", NoticeTimes.ParseTime("15:30", new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero), Summer) == new DateTimeOffset(2026, 10, 25, 14, 30, 0, TimeSpan.Zero));
    }

    private static void Checks(Action<string, bool> check)
    {
        string? Check(NoticeMessage message) => NoticeTimes.Check(message, TimeNow, China);
        check("An unknown kind is refused", Check(new NoticeMessage { Kind = "reminder" }) == "bad-kind");
        check("Durations outside the limits are refused", Check(new NoticeMessage { Kind = "countdown", InMs = 0 }) == "bad-duration"
            && Check(new NoticeMessage { Kind = "countdown", InMs = (long)TimeSpan.FromDays(32).TotalMilliseconds }) == "bad-duration"
            && Check(new NoticeMessage { Kind = "event", InMs = 60000, LeadMs = -1 }) == "bad-duration"
            && Check(new NoticeMessage { Kind = "event", InMs = 60000, LeadMs = (long)TimeSpan.FromDays(8).TotalMilliseconds }) == "bad-duration");
        check("A countdown or event needs its time", Check(new NoticeMessage { Kind = "countdown" }) == "no-time" && Check(new NoticeMessage { Kind = "event" }) == "no-time");
        check("A time is given once", Check(new NoticeMessage { Kind = "countdown", At = "15:30", InMs = 1000 }) == "time-twice");
        check("Times far away or unreadable are refused", Check(new NoticeMessage { Kind = "event", At = "2028-01-01 10:00" }) == "bad-time"
            && Check(new NoticeMessage { Kind = "event", At = "2026-10-20 10:00" }) == "bad-time" && Check(new NoticeMessage { Kind = "event", At = "whenever" }) == "bad-time");
        check("A countdown to a past moment is refused", Check(new NoticeMessage { Kind = "countdown", At = "2026-10-24T05:00:00Z" }) == "bad-time");
        check("Times need a kind, and stop needs a key", Check(new NoticeMessage { At = "15:30" }) == "no-kind" && Check(new NoticeMessage { Kind = "stopwatch", Stop = true }) == "no-key"
            && Check(new NoticeMessage { Kind = "stopwatch", Stop = true, ReplaceId = "k" }) is null);
        check("A timer without text passes validation; a plain notice without text does not",
            NoticeProtocol.Validate(new NoticeMessage { Kind = "countdown", InMs = 60000 }) is null && NoticeProtocol.Validate(new NoticeMessage { Kind = "mail" }) == "empty");
        check("Over-long time fields are refused", NoticeProtocol.Validate(new NoticeMessage { Title = "x", Kind = "event", At = new string('1', 41) }) == "bad-time");
        var json = System.Text.Encoding.UTF8.GetString(NoticeProtocol.Serialize(new NoticeMessage { Title = "x", Kind = "countdown", InMs = 5000 }));
        var back = NoticeProtocol.Deserialize(NoticeProtocol.Serialize(new NoticeMessage { Kind = "event", At = "15:00", LeadMs = 600000, Until = "16:00", Stop = false }));
        check("Time fields travel in the frame", json.Contains("\"kind\":\"countdown\"", StringComparison.Ordinal) && json.Contains("\"inMs\":5000", StringComparison.Ordinal)
            && back is { Kind: "event", At: "15:00", LeadMs: 600000, Until: "16:00", Stop: false });
        var mail = FreedesktopNotifications.ToNotice(["thunderbird", 0u, "", "New mail", "From Ann", new List<object>(), new Dictionary<object, object> { ["category"] = new DBusVariant("s", "email.arrived") }, -1], 1);
        check("A Linux mail client's notice is a mail notice", mail.Kind == NoticeTimes.Mail && mail.Category == "message");
    }

    private static void CommandLine(Action<string, bool> check)
    {
        var timer = NotifyCommandLine.Parse(["timer", "25m", "番茄"]);
        check("timer takes a duration and a title", timer.Kind == NotifyCommandKind.Send && timer.Message is { Kind: "countdown", InMs: 1_500_000, Title: "番茄", AppName: "brim-notify" }
            && timer.Message.ReplaceId!.StartsWith("countdown-", StringComparison.Ordinal) && NotifyCommandLine.PrintsKey(timer.Message));
        var at = NotifyCommandLine.Parse(["timer", "--at", "15:30", "开会", "-r", "meet"]);
        check("timer takes --at and keeps a given key", at.Message is { Kind: "countdown", At: "15:30", InMs: null, Title: "开会", ReplaceId: "meet" });
        check("timer without a time, or with a bad one, is refused", NotifyCommandLine.Parse(["timer"]).Kind == NotifyCommandKind.Error
            && NotifyCommandLine.Parse(["timer", "0s"]).Kind == NotifyCommandKind.Error && NotifyCommandLine.Parse(["timer", "soon"]).Kind == NotifyCommandKind.Error
            && NotifyCommandLine.Parse(["timer", "--at", "15:00", "--in", "5m"]).Kind == NotifyCommandKind.Error);
        var watch = NotifyCommandLine.Parse(["stopwatch", "构建", "-r", "b1"]);
        check("stopwatch starts now under its key", watch.Message is { Kind: "stopwatch", Title: "构建", ReplaceId: "b1", InMs: null }
            && NotifyCommandLine.Parse(["stopwatch", "--in", "5m"]).Kind == NotifyCommandKind.Error);
        var meeting = NotifyCommandLine.Parse(["event", "周会", "--at", "15:00", "--remind", "10m", "--ends", "16:00"]);
        check("event takes its start, reminder and end", meeting.Message is { Kind: "event", At: "15:00", LeadMs: 600_000, Until: "16:00", Title: "周会" }
            && NotifyCommandLine.Parse(["event", "--at", "15:00"]).Kind == NotifyCommandKind.Error && NotifyCommandLine.Parse(["event", "周会"]).Kind == NotifyCommandKind.Error);
        var stop = NotifyCommandLine.Parse(["stop", "b1", "-s", "make"]);
        check("stop names one key", stop.Message is { Stop: true, ReplaceId: "b1", AppName: "make" } && !NotifyCommandLine.PrintsKey(stop.Message)
            && NotifyCommandLine.Parse(["stop"]).Kind == NotifyCommandKind.Error && NotifyCommandLine.Parse(["stop", "a", "b"]).Kind == NotifyCommandKind.Error
            && NotifyCommandLine.Parse(["stop", "a", "--at", "15:00"]).Kind == NotifyCommandKind.Error);
        check("Time options belong to timer and event", NotifyCommandLine.Parse(["x", "--at", "15:00"]).Kind == NotifyCommandKind.Error
            && NotifyCommandLine.Parse(["timer", "5m", "--ends", "16:00"]).Kind == NotifyCommandKind.Error && NotifyCommandLine.Parse(["timer", "5m", "--kind", "mail"]).Kind == NotifyCommandKind.Error);
        check("--kind labels a plain notice as alarm or mail", NotifyCommandLine.Parse(["新邮件", "--kind", "MAIL"]).Message?.Kind == "mail"
            && NotifyCommandLine.Parse(["x", "--kind", "event"]).Kind == NotifyCommandKind.Error && !NotifyCommandLine.PrintsKey(NotifyCommandLine.Parse(["x", "--kind", "alarm"]).Message!));
        check("The help lists the time commands", NotifyCommandLine.Usage.Contains("brim-notify timer", StringComparison.Ordinal) && NotifyCommandLine.Usage.Contains("brim-notify stop KEY", StringComparison.Ordinal));
    }

    private static void Resolve(Action<string, bool> check)
    {
        var countdown = NoticeTimeIntake.Resolve(new NoticeMessage { Kind = "countdown", InMs = 60_000 }, TimeNow, China);
        check("A countdown runs from receipt", countdown is { Kind: NoticeTimeKind.Countdown, Native: true, Stage: 0 } && countdown.StartsAt == TimeNow && countdown.EndsAt == TimeNow.AddMinutes(1));
        var meeting = NoticeTimeIntake.Resolve(new NoticeMessage { Kind = "event", At = "15:00", LeadMs = 600_000, Until = "16:00" }, TimeNow, China)!;
        check("An event keeps its start, end and reminder", meeting.StartsAt == TimeNow.AddHours(1) && meeting.EndsAt == TimeNow.AddHours(2) && meeting.RemindAt == TimeNow.AddMinutes(50) && meeting.Stage == 0);
        var soon = NoticeTimeIntake.Resolve(new NoticeMessage { Kind = "event", InMs = 300_000, LeadMs = 600_000 }, TimeNow, China)!;
        check("An event arriving after its reminder time counts as reminded", soon.Stage == 1);
        var started = NoticeTimeIntake.Resolve(new NoticeMessage { Kind = "event", At = "2026-10-24 13:30", Until = "2026-10-24 13:00" }, TimeNow, China)!;
        check("An event that already started has nothing left to remind, and an end before its start is dropped", started.Stage == 2 && started.EndsAt is null);
        check("A stop message and a plain notice have no timing", NoticeTimeIntake.Resolve(new NoticeMessage { Kind = "stopwatch", Stop = true, ReplaceId = "k" }, TimeNow, China) is null
            && NoticeTimeIntake.Resolve(new NoticeMessage { Title = "x" }, TimeNow, China) is null);
        var input = NoticeIntake.FromPipe(new NoticeMessage { Kind = "stopwatch", ReplaceId = "w" }, TimeNow, China);
        check("Intake resolves the timing of a pipe message", input.Timing is { Kind: NoticeTimeKind.Stopwatch, Native: true } && input.Timing.StartsAt == TimeNow && !input.StopTiming);
    }

    private static void Words(Action<string, bool> check)
    {
        check("Clock readings count whole seconds", NoticeClock.Clock(TimeSpan.FromSeconds(5.9)) == "00:05" && NoticeClock.Clock(TimeSpan.FromSeconds(3599)) == "59:59"
            && NoticeClock.Clock(TimeSpan.FromHours(1)) == "1:00:00" && NoticeClock.Clock(TimeSpan.FromHours(26)) == "1 天 2 小时" && NoticeClock.Clock(TimeSpan.FromSeconds(-3)) == "00:00");
        var countdown = new NoticeTiming { Kind = NoticeTimeKind.Countdown, StartsAt = TimeNow, EndsAt = TimeNow.AddMinutes(25), Native = true };
        check("A countdown says what is left while it runs", NoticeClock.Live(countdown, TimeNow.AddSeconds(1), China) == "还剩 24:59" && NoticeClock.IsLive(countdown, TimeNow)
            && Math.Abs(NoticeClock.Fraction(countdown, TimeNow.AddMinutes(5))!.Value - .2) < 1e-9);
        check("A countdown that ended says so", NoticeClock.Live(countdown, TimeNow.AddMinutes(25.5), China) == "时间到" && NoticeClock.Live(countdown, TimeNow.AddHours(1), China) == "已于 14:25 结束"
            && !NoticeClock.IsLive(countdown, TimeNow.AddMinutes(25)));
        var watch = new NoticeTiming { Kind = NoticeTimeKind.Stopwatch, StartsAt = TimeNow, Native = true };
        check("A stopwatch counts up until stopped", NoticeClock.Live(watch, TimeNow.AddSeconds(723), China) == "已计时 12:03" && NoticeClock.IsLive(watch, TimeNow.AddDays(1)));
        var meeting = new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = TimeNow.AddMinutes(15), EndsAt = TimeNow.AddMinutes(45), Native = false };
        check("An event counts down to its start in minutes", NoticeClock.Live(meeting, TimeNow, China) == "15 分钟后开始" && NoticeClock.Live(meeting, TimeNow.AddMinutes(14.5), China) == "即将开始"
            && NoticeClock.Live(meeting, TimeNow.AddMinutes(13.5), China) == "2 分钟后开始");
        check("An event in progress says when it ends", NoticeClock.Live(meeting, TimeNow.AddMinutes(20), China) == "进行中 · 14:45 结束" && NoticeClock.IsLive(meeting, TimeNow.AddMinutes(20))
            && !NoticeClock.IsLive(meeting, TimeNow.AddMinutes(50)));
        check("An event far ahead names its day", NoticeClock.Live(new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = TimeNow.AddDays(1) }, TimeNow, China) == "明天 14:00 开始");
        check("Mail and alarms have no live words", NoticeClock.Live(new NoticeTiming { Kind = NoticeTimeKind.Mail }, TimeNow, China) is null
            && NoticeClock.Live(new NoticeTiming { Kind = NoticeTimeKind.Alarm, EndsAt = TimeNow }, TimeNow, China) is null && !NoticeClock.IsLive(null, TimeNow));
        check("Alarms, ended countdowns and reminders are alerts; starting a timer and mail are not",
            NoticeClock.Alerting(new NoticeTiming { Kind = NoticeTimeKind.Alarm }) && !NoticeClock.Alerting(countdown) && NoticeClock.Alerting(meeting)
            && !NoticeClock.Alerting(new NoticeTiming { Kind = NoticeTimeKind.Event, Native = true }) && !NoticeClock.Alerting(new NoticeTiming { Kind = NoticeTimeKind.Mail }));
        check("BrimDeck offers cancel, then remind again, for its countdowns, and stop for stopwatches",
            NoticeClock.Actions(countdown, TimeNow).Single().Key == NoticeClock.CancelAction && NoticeClock.Actions(watch, TimeNow).Single().Key == NoticeClock.StopAction
            && NoticeClock.Actions(new NoticeTiming { Kind = NoticeTimeKind.Countdown, EndsAt = TimeNow, Native = true, Stage = 1 }, TimeNow.AddMinutes(1)).Single().Key == NoticeClock.SnoozeAction
            && NoticeClock.Actions(meeting, TimeNow).Count == 0);
        var twoStep = new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = TimeNow.AddMinutes(10), RemindAt = TimeNow.AddMinutes(5), Native = true };
        check("An event is due at its reminder, then at its start", NoticeClock.NextDue(twoStep) == TimeNow.AddMinutes(5) && NoticeClock.Advance(twoStep, TimeNow.AddMinutes(4)) is null
            && NoticeClock.Advance(twoStep, TimeNow.AddMinutes(5)) == TimeNow.AddMinutes(5) && twoStep.Stage == 1 && NoticeClock.NextDue(twoStep) == TimeNow.AddMinutes(10)
            && NoticeClock.Advance(twoStep, TimeNow.AddMinutes(10)) == TimeNow.AddMinutes(10) && twoStep.Stage == 2 && NoticeClock.NextDue(twoStep) is null);
        var slept = new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = TimeNow.AddMinutes(10), RemindAt = TimeNow.AddMinutes(5), Native = true };
        check("Sleeping through a reminder and the start counts from the start", NoticeClock.Advance(slept, TimeNow.AddMinutes(12)) == TimeNow.AddMinutes(10) && slept.Stage == 2);
    }

    private static NoticeInput Timed(NoticeMessage message, DateTimeOffset now, string source = "brim-notify")
    {
        message.AppName ??= source;
        message.EventId ??= Guid.NewGuid().ToString("N");
        return NoticeIntake.FromPipe(message, now, China);
    }

    private static void Hub(Action<string, bool> check)
    {
        var rules = new Dictionary<string, NoticeAppRule>();
        var hub = new NoticeHub(key => rules.GetValueOrDefault(key));
        var start = hub.Receive(Timed(new NoticeMessage { Kind = "countdown", InMs = 60_000, ReplaceId = "tea" }, TimeNow), TimeNow);
        var tea = start.Notice!;
        check("A timer without a title is named and shown as an ordinary notice", start is { Receipt: NoticeReceipt.Accepted, Present: true } && tea.Title == "计时器" && tea.Priority == NoticePriority.Normal);
        check("The hub knows when the timer is due", hub.NextDue() == TimeNow.AddMinutes(1) && hub.FireDue(TimeNow.AddSeconds(59)).Count == 0);
        hub.Receive(Timed(new NoticeMessage { Title = "other" }, TimeNow.AddSeconds(10)), TimeNow.AddSeconds(10));
        hub.MarkAllViewed();
        var fired = hub.FireDue(TimeNow.AddMinutes(1));
        check("A timer that ends is shown again, first, unseen and high priority", fired.Single() == tea && hub.Notices[0] == tea && !tea.Viewed && tea.Priority == NoticePriority.High
            && tea.Timing!.Stage == 1 && hub.NextDue() is null);
        check("It can be reminded again in five minutes", hub.Snooze(tea.Id, NoticeClock.Snooze, TimeNow.AddMinutes(2)) && hub.NextDue() == TimeNow.AddMinutes(7) && tea.Timing!.Stage == 0);
        var late = hub.FireDue(TimeNow.AddMinutes(30));
        check("A reminder missed by more than ten minutes is only recorded", late.Count == 0 && tea.Timing!.Stage == 1 && hub.NextDue() is null);

        rules["cli:quiet"] = new NoticeAppRule { Key = "cli:quiet", Muted = true };
        hub.Receive(Timed(new NoticeMessage { Kind = "countdown", InMs = 1000 }, TimeNow, "quiet"), TimeNow);
        check("A muted source's timer ends without showing", hub.FireDue(TimeNow.AddSeconds(1)).Count == 0);

        var watch = hub.Receive(Timed(new NoticeMessage { Kind = "stopwatch", ReplaceId = "build", Title = "构建" }, TimeNow), TimeNow).Notice!;
        var stopped = hub.Receive(Timed(new NoticeMessage { Kind = "stopwatch", Stop = true, ReplaceId = "build" }, TimeNow.AddSeconds(90)), TimeNow.AddSeconds(90));
        check("brim-notify stop ends the stopwatch with the same key and keeps its time", stopped is { Receipt: NoticeReceipt.Updated, Present: false } && watch.Timing is { Stopped: true }
            && watch.Timing.EndsAt == TimeNow.AddSeconds(90) && NoticeClock.Live(watch.Timing, TimeNow.AddHours(1), China) == "用时 01:30" && watch.Title == "构建");
        check("Stopping an unknown key, or another source's key, changes nothing", hub.Receive(Timed(new NoticeMessage { Kind = "stopwatch", Stop = true, ReplaceId = "nope" }, TimeNow), TimeNow).Reason == "no-timer"
            && hub.Receive(Timed(new NoticeMessage { Kind = "stopwatch", Stop = true, ReplaceId = "tea" }, TimeNow, "quiet"), TimeNow).Receipt == NoticeReceipt.Filtered);

        // An event next week outlives the retention until it has reminded.
        var keeper = new NoticeHub { Retention = TimeSpan.FromHours(24) };
        var week = keeper.Receive(Timed(new NoticeMessage { Kind = "event", Title = "复盘", InMs = (long)TimeSpan.FromDays(3).TotalMilliseconds }, TimeNow), TimeNow).Notice!;
        keeper.Prune(TimeNow.AddDays(2));
        check("A reminder still to come stays past the retention", keeper.Notices.Contains(week));
        keeper.FireDue(TimeNow.AddDays(3));
        keeper.Prune(TimeNow.AddDays(3));
        check("A reminder just given stays for the retention from when it rang", keeper.Notices.Contains(week) && week.ReceivedAt == TimeNow.AddDays(3));
        keeper.Prune(TimeNow.AddDays(3) + keeper.Retention + TimeSpan.FromSeconds(1));
        check("Once given, it ages out as usual", !keeper.Notices.Contains(week));

        var alarm = new NoticeHub().Receive(new NoticeInput { Origin = NoticeOrigin.Windows, AppKey = "windows:clock", AppName = "时钟", InstanceKey = "w1", Title = "闹钟", Timing = new NoticeTiming { Kind = NoticeTimeKind.Alarm, EndsAt = TimeNow } }, TimeNow);
        var lowAlarm = new NoticeHub().Receive(new NoticeInput { Origin = NoticeOrigin.Windows, AppKey = "windows:clock", AppName = "时钟", InstanceKey = "w2", Title = "闹钟", PriorityHint = NoticePriority.Low, Timing = new NoticeTiming { Kind = NoticeTimeKind.Alarm } }, TimeNow);
        check("An alarm is high priority unless lowered", alarm.Notice!.Priority == NoticePriority.High && lowAlarm.Notice!.Priority == NoticePriority.Low && !lowAlarm.Present);

        var copy = tea.Copy();
        copy.Timing!.Stage = 9;
        check("Copies of a notice do not share its timing", tea.Timing!.Stage == 1);

        var root = Path.Combine(Path.GetTempPath(), "BrimDeck-TimeTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new NoticeHistory(root, new NoticeHistory.PlainProtector());
            history.Save(hub.Notices, hub.KnownApps);
            history.Load(out var saved, out var apps);
            var restored = new NoticeHub();
            restored.Load(saved, apps, TimeNow.AddMinutes(31));
            var again = restored.Find(tea.Id)!;
            var other = restored.Notices.First(n => n.Timing is null);
            check("Saved notices keep their timing; BrimDeck's own timers stay present after a restart",
                again.Timing is { Kind: NoticeTimeKind.Countdown, Stage: 1, Native: true } && again.SourcePresent && !other.SourcePresent);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static NoticeInput Toast(string appId, string title, string body, string name = "App", DateTimeOffset? created = null) => new()
    {
        Origin = NoticeOrigin.Windows, AppKey = NoticeKeys.Windows(appId), AppId = appId, AppName = name, InstanceKey = "t:" + Guid.NewGuid().ToString("N"),
        Title = title, Body = body, CreatedAt = created ?? TimeNow
    };

    private static void Classify(Action<string, bool> check)
    {
        const string clock = "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App", outlook = "Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows",
            classic = "Microsoft.Office.OUTLOOK.EXE.15", teams = "MSTeams_8wekyb3d8bbwe!MSTeams", calendar = "Microsoft.M365Companions_8wekyb3d8bbwe!Calendar";
        NoticeTiming? Kind(NoticeInput input) => NoticeTimeSources.Classify(input, China).Timing;
        check("Windows Clock's notifications are alarms", Kind(Toast(clock, "闹钟", "7:00")) is { Kind: NoticeTimeKind.Alarm } alarm && alarm.EndsAt == TimeNow && NoticeClock.Alerting(alarm)
            && Kind(Toast(clock.ToLowerInvariant(), "计时器", "时间到")) is { Kind: NoticeTimeKind.Alarm });
        check("Outlook's new mail is mail", Kind(Toast(outlook, "张三", "周报\n请查收附件，3:00 前回复")) is { Kind: NoticeTimeKind.Mail }
            && Kind(Toast(classic, "Ann", "Lunch? We could meet 12:00-13:00")) is { Kind: NoticeTimeKind.Mail });
        check("An Outlook reminder in minutes is an event starting then", Kind(Toast(outlook, "周会", "15 分钟后\n会议室 A")) is { Kind: NoticeTimeKind.Event } inMinutes
            && inMinutes.StartsAt == TimeNow.AddMinutes(15) && Kind(Toast(classic, "Standup", "In 5 minutes")) is { Kind: NoticeTimeKind.Event } english && english.StartsAt == TimeNow.AddMinutes(5)
            && Kind(Toast(outlook, "Review", "Now\nRoom 4")) is { Kind: NoticeTimeKind.Event } now && now.StartsAt == TimeNow);
        var created = new DateTimeOffset(2026, 10, 24, 6, 50, 0, TimeSpan.Zero);
        check("A reminder's time range gives the start in Windows' zone", Kind(Toast(outlook, "周会", "下午3:00 - 下午3:30\n会议室 A", created: created))?.StartsAt == new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.Zero)
            && Kind(Toast(classic, "Sync", "3:00 PM - 3:30 PM", created: created))?.StartsAt == new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.Zero)
            && Kind(Toast(outlook, "Sync", "15:00–15:30", created: created))?.StartsAt == new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.Zero));
        var lateNight = new DateTimeOffset(2026, 10, 24, 15, 50, 0, TimeSpan.Zero); // 23:50 local
        check("A morning start seen late at night is tomorrow's", Kind(Toast(outlook, "晨会", "上午9:00 - 上午9:30", created: lateNight))?.StartsAt == new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero));
        check("Impossible clock readings give no start", Kind(Toast(outlook, "x", "下午13:00 - 下午14:00"))?.StartsAt is null);
        check("Teams chat stays as it is; a meeting becomes an event", NoticeTimeSources.Classify(Toast(teams, "李四", "晚上一起吃饭吗"), China).Timing is null
            && Kind(Toast(teams, "设计评审", "会议已开始")) is { Kind: NoticeTimeKind.Event } && Kind(Toast(teams, "Design review", "The meeting has started")) is { Kind: NoticeTimeKind.Event });
        check("Calendar companions' notifications are all events", Kind(Toast(calendar, "牙医", "明天")) is { Kind: NoticeTimeKind.Event, StartsAt: null });
        check("Other mail programs are known by name", Kind(Toast("E7CF176E110C211B", "Ann", "Hello", name: "Mozilla Thunderbird")) is { Kind: NoticeTimeKind.Mail }
            && NoticeTimeSources.Classify(Toast("Some.App", "Ann", "15 分钟后"), China).Timing is null);
        check("Microsoft To Do's reminders ring like alarms", Kind(Toast("Microsoft.Todos_8wekyb3d8bbwe!App", "交房租", "今天")) is { Kind: NoticeTimeKind.Alarm } todo && todo.EndsAt == TimeNow);
        var gmail = Toast("Chrome", "张三", "周报") with { AppKey = "windows:Chrome|mail.google.com" };
        var gmailReminder = Toast("Chrome", "周会", "15 分钟后") with { AppKey = "windows:Chrome|mail.google.com" };
        var googleCalendar = Toast("Chrome", "牙医", "下午3:00 - 下午4:00", created: created) with { AppKey = "windows:Chrome|calendar.google.com" };
        const string qqMailApp = "Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!https://mail.qq.com/";
        check("Web mail in the browser is mail, and its reminders are events", Kind(gmail) is { Kind: NoticeTimeKind.Mail }
            && Kind(gmailReminder) is { Kind: NoticeTimeKind.Event } reminder && reminder.StartsAt == TimeNow.AddMinutes(15) && Kind(Toast(qqMailApp, "李四", "合同")) is { Kind: NoticeTimeKind.Mail });
        check("A web calendar's notices are events", Kind(googleCalendar) is { Kind: NoticeTimeKind.Event } web && web.StartsAt == new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.Zero));
        check("Other sites stay as they are", NoticeTimeSources.Classify(Toast("Chrome", "x", "15 分钟后") with { AppKey = "windows:Chrome|github.com" }, China).Timing is null);
        var wsl = Toast(outlook, "x", "y") with { Origin = NoticeOrigin.Wsl };
        var labelled = Toast(outlook, "x", "15 分钟后") with { Timing = new NoticeTiming { Kind = NoticeTimeKind.Mail } };
        check("Only Windows notifications not sorted yet are sorted", NoticeTimeSources.Classify(wsl, China).Timing is null && NoticeTimeSources.Classify(labelled, China).Timing!.Kind == NoticeTimeKind.Mail);
        check("Every time source names its AUMIDs and kinds", NoticeTimeSources.All.All(s => s.AppIds.Count > 0 && s.Kinds.Count > 0 && (s.PackageFamily is not null || s.Program is not null))
            && NoticeTimeSources.ForApp(clock.ToUpperInvariant())?.Id == "clock");
    }

    private static void Urgent(Action<string, bool> check)
    {
        var coordinator = new AttentionCoordinator();
        AttentionItem Item(string lane, bool urgent = false) => new() { Lane = lane, Kind = AttentionKind.Notice, Key = lane, Group = lane, Duration = TimeSpan.FromSeconds(5), Urgent = urgent, Priority = urgent ? NoticePriority.High : NoticePriority.Normal };
        foreach (var lane in new[] { "a", "b", "c", "d" }) coordinator.Submit(Item(lane), TimeNow);
        coordinator.Submit(Item("alarm", urgent: true), TimeNow);
        check("An alarm is not held back by the rate limit that holds an ordinary notice", coordinator.Current("d") is null && coordinator.Queued("d").Count == 1 && coordinator.Current("alarm") is not null);
    }
}
