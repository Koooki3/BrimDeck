using System.Globalization;

namespace BrimDeck.Protocol;

// Times and durations in time-kind notices (brim-notify timer, stopwatch and event). A sender gives either a duration, which
// BrimDeck counts from when it receives the message (a WSL clock may drift from Windows' clock), or a time: an ISO 8601 time
// with its offset, or a wall-clock time without one ("15:30", "2026-10-24 15:30"), which BrimDeck reads in Windows' own
// time zone, so a distribution set to another zone means the same thing as the clock on the taskbar.
public static class NoticeTimes
{
    public const string Alarm = "alarm", Stopwatch = "stopwatch", Countdown = "countdown", Mail = "mail", Event = "event";
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal) { Alarm, Stopwatch, Countdown, Mail, Event };
    public const int MaxText = 40;
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1), MaxDuration = TimeSpan.FromDays(31), MaxLead = TimeSpan.FromDays(7);
    // How far from now a time may be: a little in the past (an event that just started), at most a year ahead.
    public static readonly TimeSpan MaxPast = TimeSpan.FromDays(1), MaxAhead = TimeSpan.FromDays(366);

    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy/MM/dd HH:mm:ss"
    ];

    // "90s", "25m", "1h30m", "2d", "1h 5m 10s"; at least one unit, each at most once, in the order d, h, m, s.
    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxText) return null;
        var span = TimeSpan.Zero;
        int order = 0, index = 0, units = 0;
        text = text.Trim().ToLowerInvariant();
        while (index < text.Length)
        {
            if (text[index] == ' ') { index++; continue; }
            int start = index;
            while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
            if (index == start || index - start > 6 || index >= text.Length) return null;
            long value = long.Parse(text.AsSpan(start, index - start), CultureInfo.InvariantCulture);
            int unit = text[index] switch { 'd' => 1, 'h' => 2, 'm' => 3, 's' => 4, _ => 0 };
            if (unit == 0 || unit <= order) return null;
            order = unit; index++; units++;
            span += unit switch { 1 => TimeSpan.FromDays(value), 2 => TimeSpan.FromHours(value), 3 => TimeSpan.FromMinutes(value), _ => TimeSpan.FromSeconds(value) };
        }
        return units > 0 ? span : null;
    }

    // The moment a time names, seen from now in the given zone. A clock time alone is its next occurrence (today, or tomorrow
    // when it has passed). A wall-clock time the zone skips (the hour lost when daylight saving starts) moves forward by the
    // gap; one it repeats (the hour gained when it ends) is the earlier of the two. Null when the text is not a time.
    public static DateTimeOffset? ParseTime(string? text, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxText) return null;
        text = text.Trim();
        if (HasOffset(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var absolute)) return absolute;
        if (TimeOnly.TryParseExact(text, ["H:mm", "H:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock))
        {
            var today = TimeZoneInfo.ConvertTime(now, zone).Date;
            var candidate = FromLocal(today + clock.ToTimeSpan(), zone);
            return candidate > now ? candidate : FromLocal(today.AddDays(1) + clock.ToTimeSpan(), zone);
        }
        if (DateTime.TryParseExact(text, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return FromLocal(local, zone);
        return null;
    }

    private static bool HasOffset(string text)
    {
        if (text.EndsWith('Z') || text.EndsWith('z')) return true;
        int t = text.IndexOfAny(['T', ' ']);
        if (t < 0) return false;
        var tail = text.AsSpan(t + 1);
        return tail.IndexOfAny('+', '-') > 0;
    }

    // A wall-clock time in the zone as a moment.
    public static DateTimeOffset FromLocal(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Skipped: the first wall-clock minute after the gap (gaps are whole minutes).
            var probe = local;
            for (int i = 0; i < 24 * 60 && zone.IsInvalidTime(probe); i++) probe = probe.AddMinutes(1);
            return new DateTimeOffset(probe, zone.GetUtcOffset(probe));
        }
        if (zone.IsAmbiguousTime(local)) return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max());
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    // Checks the time fields of a message. Null when they can be used, otherwise the reason (sent back as "rejected").
    public static string? Check(NoticeMessage message, DateTimeOffset now, TimeZoneInfo zone)
    {
        bool anyTime = message.At is not null || message.InMs is not null || message.Until is not null || message.LeadMs is not null;
        if (message.Kind is null)
        {
            if (anyTime || message.Stop == true) return "no-kind";
            return null;
        }
        if (!Kinds.Contains(message.Kind)) return "bad-kind";
        if (message.InMs is { } inMs && (inMs < MinDuration.TotalMilliseconds || inMs > MaxDuration.TotalMilliseconds)) return "bad-duration";
        if (message.LeadMs is { } lead && (lead < 0 || lead > MaxLead.TotalMilliseconds)) return "bad-duration";
        if (message.At is not null && message.InMs is not null) return "time-twice";
        if (message.At is { } at && Within(ParseTime(at, now, zone), now) is null) return "bad-time";
        if (message.Until is { } until && Within(ParseTime(until, now, zone), now) is null) return "bad-time";
        if (message.Stop == true) return string.IsNullOrWhiteSpace(message.ReplaceId) ? "no-key" : null;
        if (message.Kind is Countdown or Event && message.At is null && message.InMs is null) return "no-time";
        // A countdown to a moment already past would ring at once.
        if (message.Kind == Countdown && message.At is { } end && ParseTime(end, now, zone) <= now) return "bad-time";
        return null;
    }

    private static DateTimeOffset? Within(DateTimeOffset? time, DateTimeOffset now)
        => time is { } value && value >= now - MaxPast && value <= now + MaxAhead ? value : null;
}
