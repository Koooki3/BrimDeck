using System.Globalization;

namespace BrimDeck.Core;

// Which look of a theme shows (PanelThemeFile.Night, PanelThemeFile.Dusk).
public enum DaylightLook { Day, Dusk, Night }
// What chooses it: the local clock (the default), or Windows' app mode.
public enum DaylightMode { Clock, System }

// Themes with a night look: which look is due, and when the next change comes. By the clock the day runs from Daylight.Day to
// Daylight.Night, and a dusk look, when the theme has one, takes the last DuskMinutes of the day. Following Windows the night
// look shows while apps use dark mode and the day look in light mode, with no dusk. The settings can move the hours or choose
// either mode (DeckSettings.ThemeDaylight).
public static partial class PanelThemes
{
    public static readonly TimeSpan DefaultDay = new(6, 30, 0), DefaultNight = new(18, 30, 0);
    public const int DefaultDuskMinutes = 60, MinDuskMinutes = 15, MaxDuskMinutes = 180;
    // The daylight mode names, in a theme file's "daylight.mode" and, for "system", in DeckSettings.ThemeDaylight.
    public const string ClockDaylight = "clock", SystemDaylight = "system";

    // "HH:mm" (or "H:mm") within a day, or null.
    public static TimeSpan? ParseTimeOfDay(string? text)
        => TimeSpan.TryParseExact((text ?? "").Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) ? time : null;
    public static string FormatTimeOfDay(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    // A theme file's mode: empty or "clock" is the clock, "system" follows Windows; anything else is null (a file error).
    public static DaylightMode? ParseDaylightMode(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "" or ClockDaylight => DaylightMode.Clock,
        SystemDaylight => DaylightMode.System,
        _ => null
    };

    // The setting as it is kept: "system", valid "HH:mm-HH:mm" of two different times, or "" for the theme's own choice.
    public static string NormalizeDaylightSetting(string? setting)
    {
        var text = (setting ?? "").Trim();
        if (string.Equals(text, SystemDaylight, StringComparison.OrdinalIgnoreCase)) return SystemDaylight;
        return SettingHours(text) is { } hours ? FormatDaylight(hours.Day, hours.Night) : "";
    }
    private static (TimeSpan Day, TimeSpan Night)? SettingHours(string? setting)
        => (setting ?? "").Split('-', 2) is [var day, var night] && ParseTimeOfDay(day) is { } d && ParseTimeOfDay(night) is { } n && d != n ? (d, n) : null;

    // The mode in use: the settings' "system" or hours when set, else the theme's.
    public static DaylightMode DaylightModeOf(PanelThemeFile file, string? setting = null)
    {
        if (string.Equals((setting ?? "").Trim(), SystemDaylight, StringComparison.OrdinalIgnoreCase)) return DaylightMode.System;
        if (SettingHours(setting) is not null) return DaylightMode.Clock;
        return ParseDaylightMode(file.Daylight?.Mode) ?? DaylightMode.Clock;
    }

    // The day's start and end: the settings' "HH:mm-HH:mm" when it is valid, else the theme's, else 06:30 to 18:30.
    // A day of no length would never end, so it falls back too.
    public static (TimeSpan Day, TimeSpan Night) DaylightHours(PanelThemeFile file, string? setting = null)
    {
        if (SettingHours(setting) is { } hours) return hours;
        var own = (Day: ParseTimeOfDay(file.Daylight?.Day) ?? DefaultDay, Night: ParseTimeOfDay(file.Daylight?.Night) ?? DefaultNight);
        return own.Day != own.Night ? own : (DefaultDay, DefaultNight);
    }

    // How long the dusk look shows before the night: none without a dusk look, and never longer than the day itself.
    public static TimeSpan DuskLength(PanelThemeFile file, string? setting = null)
    {
        if (file.Dusk is null || file.Night is null) return TimeSpan.Zero;
        var (day, night) = DaylightHours(file, setting);
        var dayLength = night > day ? night - day : night - day + TimeSpan.FromDays(1);
        var dusk = TimeSpan.FromMinutes(Math.Clamp(file.Daylight?.DuskMinutes ?? DefaultDuskMinutes, MinDuskMinutes, MaxDuskMinutes));
        return dusk < dayLength ? dusk : dayLength;
    }

    // The look due at this local time. appsLight is whether Windows apps use light mode, read only in system mode; it is passed
    // in so the choice can be made without the registry. A day may run past midnight (a night shift's "20:00-08:00"), and so
    // may its dusk.
    public static DaylightLook LookAt(PanelThemeFile file, DateTime local, bool appsLight, string? setting = null)
    {
        if (file.Night is null) return DaylightLook.Day;
        if (DaylightModeOf(file, setting) == DaylightMode.System) return appsLight ? DaylightLook.Day : DaylightLook.Night;
        var (day, night) = DaylightHours(file, setting);
        var now = local.TimeOfDay;
        if (!Within(day, night, now)) return DaylightLook.Night;
        var dusk = DuskLength(file, setting);
        return dusk > TimeSpan.Zero && Within(Wrap(night - dusk), night, now) ? DaylightLook.Dusk : DaylightLook.Day;
    }
    // Whether a time of day falls in [from, to), the span running past midnight when to comes first.
    private static bool Within(TimeSpan from, TimeSpan to, TimeSpan time) => from < to ? time >= from && time < to : time >= from || time < to;
    private static TimeSpan Wrap(TimeSpan time) => time < TimeSpan.Zero ? time + TimeSpan.FromDays(1) : time;

    // The next local time the look changes by the clock, after `local`: day, dusk or night beginning. Null for a theme without a
    // night look, and in system mode, where Windows says when (SystemEvents.UserPreferenceChanged).
    public static DateTime? NextDaylightChange(PanelThemeFile file, DateTime local, string? setting = null)
    {
        if (file.Night is null || DaylightModeOf(file, setting) == DaylightMode.System) return null;
        var (day, night) = DaylightHours(file, setting);
        DateTime Next(TimeSpan at) { var t = local.Date + at; return t > local ? t : t.AddDays(1); }
        var next = Next(day) < Next(night) ? Next(day) : Next(night);
        var dusk = DuskLength(file, setting);
        if (dusk > TimeSpan.Zero && Next(Wrap(night - dusk)) < next) next = Next(Wrap(night - dusk));
        return next;
    }

    // "06:30-18:30" for the settings.
    public static string FormatDaylight(TimeSpan day, TimeSpan night) => FormatTimeOfDay(day) + "-" + FormatTimeOfDay(night);
}
