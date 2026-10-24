using System.Globalization;
using System.Text.RegularExpressions;
using BrimDeck.Protocol;

namespace BrimDeck.Core.Notices;

// What a time-kind notice is about. None for every other notice. The glyph set keys its symbols on these names.
public enum NoticeTimeKind { None, Alarm, Stopwatch, Countdown, Mail, Event }

// The times of a time-kind notice, all absolute (UTC-based) moments; the words shown ("12 min left") are worked out from them
// when shown, never kept. Native: BrimDeck itself keeps the time and reminds (sent with brim-notify); otherwise the notice
// came from an application's own reminder and is only shown.
public sealed class NoticeTiming
{
    public NoticeTimeKind Kind { get; set; }
    // A stopwatch's or countdown's start; an event's start.
    public DateTimeOffset? StartsAt { get; set; }
    // A countdown's end (when it reminds); an event's end; when a stopwatch was stopped; when an alarm rang.
    public DateTimeOffset? EndsAt { get; set; }
    // When BrimDeck reminds of an event before its start (StartsAt without a lead).
    public DateTimeOffset? RemindAt { get; set; }
    public bool Native { get; set; }
    // Stopped or cancelled by the user or the sender.
    public bool Stopped { get; set; }
    // What BrimDeck has already reminded of: a countdown 1 once it ended; an event 1 once reminded, 2 once started.
    public int Stage { get; set; }
    public NoticeTiming Copy() => (NoticeTiming)MemberwiseClone();
}

// The rules of time-kind notices: when one is live (its words change by the second), when BrimDeck must act next, and what
// the notice says about its time.
public static class NoticeClock
{
    // A reminder BrimDeck could not give on time (asleep, not running) is still given within this; later it is only recorded.
    public static readonly TimeSpan MissedLimit = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Snooze = TimeSpan.FromMinutes(5);
    // An event without an end counts as starting for this long.
    private static readonly TimeSpan StartWindow = TimeSpan.FromMinutes(1);

    public static bool IsLive(NoticeTiming? timing, DateTimeOffset now) => timing is { Stopped: false } t && t.Kind switch
    {
        NoticeTimeKind.Stopwatch => t.StartsAt is not null && t.EndsAt is null,
        NoticeTimeKind.Countdown => t.EndsAt is { } end && now < end,
        NoticeTimeKind.Event => t.StartsAt is { } start && now < (t.EndsAt ?? start + StartWindow),
        _ => false
    };

    // The next moment BrimDeck reminds for a notice it keeps time for, or null.
    public static DateTimeOffset? NextDue(NoticeTiming? timing)
    {
        if (timing is not { Native: true, Stopped: false } t) return null;
        return t.Kind switch
        {
            NoticeTimeKind.Countdown when t.Stage == 0 => t.EndsAt,
            NoticeTimeKind.Event when t.Stage == 0 && t.RemindAt is { } remind && t.StartsAt is { } start && remind < start => remind,
            NoticeTimeKind.Event when t.Stage < 2 => t.StartsAt,
            _ => null
        };
    }

    // Moves the notice past what is due. Returns the moment of the latest step it passed (an event whose reminder and start
    // both passed while BrimDeck slept counts from its start), or null when nothing was due.
    public static DateTimeOffset? Advance(NoticeTiming timing, DateTimeOffset now)
    {
        if (NextDue(timing) is not { } due || due > now) return null;
        if (timing.Kind == NoticeTimeKind.Countdown) { timing.Stage = 1; return due; }
        if (timing.StartsAt is { } start && now >= start) { timing.Stage = 2; return start; }
        timing.Stage = 1;
        return due;
    }

    // An alarm ringing, a countdown that ended, an event's reminder: these are high priority and are not held back by the rate
    // limit. Starting a timer and new mail are ordinary notices.
    public static bool Alerting(NoticeTiming? timing) => timing is { Stopped: false } t && t.Kind switch
    {
        NoticeTimeKind.Alarm => true,
        NoticeTimeKind.Countdown => t.Stage >= 1,
        NoticeTimeKind.Event => !t.Native || t.Stage >= 1,
        _ => false
    };

    // Alerting as of now: an event BrimDeck is told of only well after it started (entered afterwards) is recorded as an
    // ordinary notice, not rung as high priority (core-L1). A reminder BrimDeck gives late keeps MissedLimit, as FireDue does.
    public static bool Alerting(NoticeTiming? timing, DateTimeOffset now) => Alerting(timing)
        && !(timing is { Kind: NoticeTimeKind.Event, Native: true, Stage: 2, StartsAt: { } start } && now - start > MissedLimit);

    // How far a countdown has run, 0 to 1, for its ring.
    public static double? Fraction(NoticeTiming? timing, DateTimeOffset now)
    {
        if (timing is not { Kind: NoticeTimeKind.Countdown, StartsAt: { } start, EndsAt: { } end } || end <= start) return null;
        return Math.Clamp((now - start) / (end - start), 0, 1);
    }

    // The words about the notice's time: "还剩 24:59", "已计时 12:03", "14 分钟后开始". Null for kinds without any.
    public static string? Live(NoticeTiming? timing, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (timing is null) return null;
        switch (timing.Kind)
        {
            case NoticeTimeKind.Countdown when timing.EndsAt is { } end:
                if (timing.Stopped) return Loc.T("已取消", "Cancelled");
                if (now < end) return Loc.T("还剩 ", "") + Clock(end - now) + Loc.T("", " left");
                return now - end < TimeSpan.FromMinutes(1) ? Loc.T("时间到", "Time's up") : Loc.T($"已于 {At(end, now, zone)} 结束", $"Ended at {At(end, now, zone)}");
            case NoticeTimeKind.Stopwatch when timing.StartsAt is { } start:
                if (timing.EndsAt is { } stopped) return Loc.T("用时 ", "Took ") + Clock(stopped - start);
                if (timing.Stopped) return Loc.T("已停止", "Stopped");
                return Loc.T("已计时 ", "Running ") + Clock(now - start);
            case NoticeTimeKind.Event when timing.StartsAt is { } begins:
                if (timing.Stopped) return Loc.T("已取消", "Cancelled");
                var until = begins - now;
                if (until >= TimeSpan.FromHours(1)) return Loc.T($"{At(begins, now, zone)} 开始", $"Starts {At(begins, now, zone)}");
                if (until >= TimeSpan.FromMinutes(1))
                {
                    int minutes = (int)Math.Ceiling(until.TotalMinutes);
                    return Loc.T($"{minutes} 分钟后开始", $"Starts in {minutes} min");
                }
                if (until > TimeSpan.Zero) return Loc.T("即将开始", "Starting now");
                if (timing.EndsAt is { } ends && now < ends) return Loc.T($"进行中 · {At(ends, now, zone)} 结束", $"In progress · ends {At(ends, now, zone)}");
                if (now - begins < StartWindow) return Loc.T("已开始", "Started");
                return Loc.T($"已于 {At(begins, now, zone)} 开始", $"Started at {At(begins, now, zone)}");
            default: return null;
        }
    }

    // "04:05", "24:59", "1:02:03"; days as "2 天 3 小时".
    public static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        // Whole seconds, rounded up while counting down would show 0:00 a second early; truncation is used for both ways.
        long seconds = (long)span.TotalSeconds;
        if (seconds >= 86400) return Loc.T($"{seconds / 86400} 天 {seconds % 86400 / 3600} 小时", $"{seconds / 86400} d {seconds % 86400 / 3600} h");
        if (seconds >= 3600) return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}");
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}:{seconds % 60:00}");
    }

    // A moment as the clock on the taskbar shows it: "15:30" today, "明天 15:30", otherwise with its date.
    public static string At(DateTimeOffset time, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(time, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        string clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today) return clock;
        if (local.Date == today.AddDays(1)) return Loc.T("明天 ", "tomorrow ") + clock;
        if (local.Date == today.AddDays(-1)) return Loc.T("昨天 ", "yesterday ") + clock;
        return local.ToString("MM-dd ", CultureInfo.InvariantCulture) + clock;
    }

    // The name a time-kind notice without its own title gets.
    public static string Name(NoticeTimeKind kind) => kind switch
    {
        NoticeTimeKind.Alarm => Loc.T("闹钟", "Alarm"), NoticeTimeKind.Stopwatch => Loc.T("秒表", "Stopwatch"), NoticeTimeKind.Countdown => Loc.T("计时器", "Timer"),
        NoticeTimeKind.Mail => Loc.T("邮件", "Mail"), NoticeTimeKind.Event => Loc.T("日程", "Event"), _ => ""
    };

    // What BrimDeck itself can do with a notice it keeps time for. Keys start with "brimdeck:" so they never mix with a WSL
    // application's own actions.
    public const string CancelAction = "brimdeck:cancel", StopAction = "brimdeck:stop", SnoozeAction = "brimdeck:snooze";
    public static IReadOnlyList<NoticeAction> Actions(NoticeTiming? timing, DateTimeOffset now)
    {
        if (timing is not { Native: true, Stopped: false } t) return [];
        return t.Kind switch
        {
            NoticeTimeKind.Stopwatch when t.EndsAt is null => [new(StopAction, Loc.T("停止", "Stop"))],
            NoticeTimeKind.Countdown when t.Stage == 0 => [new(CancelAction, Loc.T("取消", "Cancel"))],
            NoticeTimeKind.Countdown when t.EndsAt is { } end && now - end < TimeSpan.FromHours(1) => [new(SnoozeAction, Loc.T("5 分钟后再提醒", "Remind in 5 min"))],
            NoticeTimeKind.Event when t.Stage < 2 => [new(CancelAction, Loc.T("取消", "Cancel"))],
            _ => []
        };
    }
}

// The time of a brim-notify message, resolved when BrimDeck receives it (see NoticeTimes in the protocol).
public static class NoticeTimeIntake
{
    public static NoticeTimeKind Kind(string? kind) => kind switch
    {
        NoticeTimes.Alarm => NoticeTimeKind.Alarm, NoticeTimes.Stopwatch => NoticeTimeKind.Stopwatch, NoticeTimes.Countdown => NoticeTimeKind.Countdown,
        NoticeTimes.Mail => NoticeTimeKind.Mail, NoticeTimes.Event => NoticeTimeKind.Event, _ => NoticeTimeKind.None
    };

    // The timing for a validated message, or null for a notice without one. A stop message has no timing of its own.
    public static NoticeTiming? Resolve(NoticeMessage message, DateTimeOffset now, TimeZoneInfo zone)
    {
        var kind = Kind(message.Kind);
        if (kind == NoticeTimeKind.None || message.Stop == true) return null;
        var at = NoticeTimes.ParseTime(message.At, now, zone) ?? (message.InMs is { } ms ? now + TimeSpan.FromMilliseconds(ms) : null);
        switch (kind)
        {
            case NoticeTimeKind.Stopwatch: return new NoticeTiming { Kind = kind, StartsAt = now, Native = true };
            case NoticeTimeKind.Countdown when at is { } end: return new NoticeTiming { Kind = kind, StartsAt = now, EndsAt = end, Native = true };
            case NoticeTimeKind.Event when at is { } start:
            {
                // The end is read from the start ("--at 23:00 --ends 01:00" ends after midnight), not from now.
                var ends = NoticeTimes.ParseTime(message.Until, start, zone);
                var remind = start - TimeSpan.FromMilliseconds(Math.Max(0, message.LeadMs ?? 0));
                // Arriving after its reminder time, the notice itself is the reminder; after its start, there is nothing left to remind.
                int stage = start <= now ? 2 : remind <= now ? 1 : 0;
                return new NoticeTiming { Kind = kind, StartsAt = start, EndsAt = ends > start ? ends : null, RemindAt = remind, Native = true, Stage = stage };
            }
            case NoticeTimeKind.Alarm: return new NoticeTiming { Kind = kind, EndsAt = now };
            case NoticeTimeKind.Mail: return new NoticeTiming { Kind = kind };
            default: return null;
        }
    }
}

// Applications on Windows whose notifications are time-kind: Windows' Clock, Outlook (new and classic), the calendar
// companions and Teams. Used to sort their notifications into kinds and to list what this PC has in the settings.
public enum NoticeTimeRole
{
    // Every notification is an alarm, a timer that finished or the end of a focus session.
    Clock,
    // Every notification is an event's reminder.
    Calendar,
    // Mail, unless the notification reads as a reminder of an event.
    MailAndCalendar,
    // Only notifications that read as a meeting's reminder or start; the rest (chat) stay as they are.
    Meetings,
    // Every notification is the reminder of a task the user set (Microsoft To Do): it rings as an alarm does.
    Reminders
}

public sealed record NoticeTimeSource(string Id, string Name, string NameEn, NoticeTimeRole Role, string? PackageFamily, IReadOnlyList<string> AppIds, string? Program = null)
{
    public string DisplayName => Loc.T(Name, NameEn);
    public IReadOnlyList<NoticeTimeKind> Kinds => Role switch
    {
        NoticeTimeRole.Clock or NoticeTimeRole.Reminders => [NoticeTimeKind.Alarm],
        NoticeTimeRole.Calendar or NoticeTimeRole.Meetings => [NoticeTimeKind.Event],
        _ => [NoticeTimeKind.Mail, NoticeTimeKind.Event]
    };
}

public static partial class NoticeTimeSources
{
    public static readonly IReadOnlyList<NoticeTimeSource> All =
    [
        new("clock", "Windows 时钟", "Windows Clock", NoticeTimeRole.Clock, "Microsoft.WindowsAlarms_8wekyb3d8bbwe", ["Microsoft.WindowsAlarms_8wekyb3d8bbwe!App"]),
        new("outlook", "Outlook（新）", "Outlook (new)", NoticeTimeRole.MailAndCalendar, "Microsoft.OutlookForWindows_8wekyb3d8bbwe", ["Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows"]),
        new("outlook-classic", "Outlook（经典）", "Outlook (classic)", NoticeTimeRole.MailAndCalendar, null, ["Microsoft.Office.OUTLOOK.EXE.15", "Microsoft.Office.OUTLOOK.EXE.16"], "OUTLOOK.EXE"),
        new("m365-calendar", "Microsoft 365 日历", "Microsoft 365 Calendar", NoticeTimeRole.Calendar, "Microsoft.M365Companions_8wekyb3d8bbwe", ["Microsoft.M365Companions_8wekyb3d8bbwe!Calendar"]),
        new("todo", "Microsoft To Do", "Microsoft To Do", NoticeTimeRole.Reminders, "Microsoft.Todos_8wekyb3d8bbwe", ["Microsoft.Todos_8wekyb3d8bbwe!App"]),
        new("teams", "Microsoft Teams", "Microsoft Teams", NoticeTimeRole.Meetings, "MSTeams_8wekyb3d8bbwe", ["MSTeams_8wekyb3d8bbwe!MSTeams"]),
        new("windows-mail", "邮件和日历", "Mail and Calendar", NoticeTimeRole.MailAndCalendar, "microsoft.windowscommunicationsapps_8wekyb3d8bbwe",
            ["microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.mail", "microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.calendar"])
    ];

    // Other mail programs, known by the name Windows gives their notifications.
    private static readonly string[] MailNames = ["Thunderbird", "Foxmail", "网易邮箱大师", "MailMaster", "eM Client", "Mailbird", "Spark", "Betterbird"];

    public static NoticeTimeSource? ForApp(string appId) => All.FirstOrDefault(source => source.AppIds.Contains(appId, StringComparer.OrdinalIgnoreCase));

    // A Windows notification sorted into its kind. Only notifications of the applications above (and other mail programs by
    // name) are touched; a notification already sorted keeps its kind. The text is looked at here only, never kept or logged.
    public static NoticeInput Classify(NoticeInput input, TimeZoneInfo zone)
    {
        if (input.Origin != NoticeOrigin.Windows || input.Timing is not null) return input;
        var created = input.CreatedAt ?? DateTimeOffset.UtcNow;
        var source = ForApp(input.AppId);
        // A web mail or calendar in the browser (a site's notice or an installed web app) is sorted like the programs.
        if (source is null && WindowsNoticeApps.SiteOf(input.AppKey, input.AppId) is { } site)
        {
            string said = input.Title + "\n" + input.Body;
            if (WindowsNoticeApps.IsCalendarSite(site) || WindowsNoticeApps.IsMailSite(site) && ReadsAsReminder(said))
                return input with { Timing = new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = Start(said, created, zone) } };
            return WindowsNoticeApps.IsMailSite(site) ? input with { Timing = new NoticeTiming { Kind = NoticeTimeKind.Mail } } : input;
        }
        if (source is null)
            return MailNames.Any(name => input.AppName.Contains(name, StringComparison.OrdinalIgnoreCase)) ? input with { Timing = new NoticeTiming { Kind = NoticeTimeKind.Mail } } : input;
        string text = input.Title + "\n" + input.Body;
        // The calendar's own notifications are all reminders; others only when they read as one.
        bool calendarApp = source.Role == NoticeTimeRole.Calendar || input.AppId.EndsWith("windowslive.calendar", StringComparison.OrdinalIgnoreCase);
        switch (source.Role)
        {
            case NoticeTimeRole.Clock or NoticeTimeRole.Reminders:
                return input with { Timing = new NoticeTiming { Kind = NoticeTimeKind.Alarm, EndsAt = created } };
            case NoticeTimeRole.Meetings when !ReadsAsMeeting(text):
                return input;
            case NoticeTimeRole.MailAndCalendar when !calendarApp && !ReadsAsReminder(text):
                return input with { Timing = new NoticeTiming { Kind = NoticeTimeKind.Mail } };
            default:
                return input with { Timing = new NoticeTiming { Kind = NoticeTimeKind.Event, StartsAt = Start(text, created, zone) } };
        }
    }

    // A reminder's own lines: "15 分钟后", "In 15 minutes", "现在", "Now", or a line that begins with a time range
    // ("下午3:00 - 下午3:30", "3:00 PM - 3:30 PM"). Mail rarely has a line of only these, so new mail is not taken for one.
    public static bool ReadsAsReminder(string text)
        => text.Split('\n').Any(line => RelativeLine().IsMatch(line.Trim()) || RangeStart().IsMatch(line.Trim()));

    // A meeting's notification in Teams: a reminder's lines, or that a meeting started.
    public static bool ReadsAsMeeting(string text) => ReadsAsReminder(text) || MeetingWords().IsMatch(text);

    // When the event starts, as well as the notification tells: "in 15 minutes" from when it was sent, "now" when it was
    // sent, or the start of a time range on the day it was sent (the next day when that is long past). Null when it does not say.
    public static DateTimeOffset? Start(string text, DateTimeOffset created, TimeZoneInfo zone)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var relative = RelativeLine().Match(line);
            if (relative.Success)
            {
                if (relative.Groups["n"].Success && int.TryParse(relative.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                    return created + (relative.Groups["hours"].Success ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n));
                return created;
            }
        }
        foreach (var raw in text.Split('\n'))
        {
            var range = RangeStart().Match(raw.Trim());
            if (!range.Success) continue;
            int hour = int.Parse(range.Groups["h"].Value, CultureInfo.InvariantCulture), minute = int.Parse(range.Groups["m"].Value, CultureInfo.InvariantCulture);
            // "p.m." reads as "PM".
            string half = (range.Groups["pre"].Value + range.Groups["post"].Value).Replace(".", "", StringComparison.Ordinal).ToUpperInvariant();
            bool pm = half.Contains("PM", StringComparison.Ordinal) || half.Contains("下午", StringComparison.Ordinal) || half.Contains("晚上", StringComparison.Ordinal);
            bool am = half.Contains("AM", StringComparison.Ordinal) || half.Contains("上午", StringComparison.Ordinal) || half.Contains("早上", StringComparison.Ordinal);
            if (pm || am) { if (hour > 12) return null; hour = hour % 12 + (pm ? 12 : 0); }
            if (hour > 23 || minute > 59) return null;
            var day = TimeZoneInfo.ConvertTime(created, zone).Date;
            var start = NoticeTimes.FromLocal(day + new TimeSpan(hour, minute, 0), zone);
            // A reminder comes before its event: a start more than 12 hours before the notification is tomorrow's.
            return start < created - TimeSpan.FromHours(12) ? NoticeTimes.FromLocal(day.AddDays(1) + new TimeSpan(hour, minute, 0), zone) : start;
        }
        return null;
    }

    [GeneratedRegex(@"^(?:(?<n>\d{1,3})\s*(?:(?<hours>小时)|分钟)后(?:开始)?|in\s+(?<n>\d{1,3})\s+(?:(?<hours>hours?)|min(?:ute)?s?)|now|starting now|starts now|现在|即将开始|正在进行|已开始)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeLine();

    [GeneratedRegex(@"^(?<pre>上午|下午|早上|晚上)?\s*(?<h>\d{1,2}):(?<m>\d{2})\s*(?<post>[AaPp]\.?[Mm]\.?)?\s*[-–—~至到]\s*(?:上午|下午|早上|晚上)?\s*\d{1,2}:\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex RangeStart();

    [GeneratedRegex(@"会议已开始|会议即将开始|会议开始了|meeting (?:has )?started|meeting is starting|meeting starts|started a meeting|发起了会议", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MeetingWords();
}
