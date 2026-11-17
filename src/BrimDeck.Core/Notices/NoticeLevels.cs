namespace BrimDeck.Core.Notices;

// What the island plays for a notice: nothing (it only shows the words), the indicator's one brightening, the backlight
// alone (the indicator: its breathing bloom), or the backlight with the liquid.
public enum NoticeEffect { Still, Brighten, Glow, Liquid }

// How strongly a notice shows on the island (NoticeLevel), from the user's rule, what the notice is and what its sender asked.
public static class NoticeLevels
{
    // In order: the level the user chose for the application; a call, a ringing alarm, a countdown that ended or an event's
    // reminder stays until handled (unless the notice is low priority); the level the sender asked for; high priority, errors,
    // warnings, tasks and results are lively; low priority and plain information are plain; everything else takes fallback
    // (the setting, one of Plain, Glow and Lively). Persistent is only for a notice still at its source whose time was not
    // stopped: with nothing left to handle it shows once, as a lively one does.
    public static NoticeLevel Resolve(Notice notice, NoticeAppRule? rule, NoticeLevel fallback, DateTimeOffset now)
    {
        var level = Decide(notice, rule, fallback, now);
        return level == NoticeLevel.Persistent && (!notice.SourcePresent || notice.Timing is { Stopped: true }) ? NoticeLevel.Lively : level;
    }

    private static NoticeLevel Decide(Notice notice, NoticeAppRule? rule, NoticeLevel fallback, DateTimeOffset now)
    {
        if (rule?.Level is NoticeLevel.Silent or NoticeLevel.Plain or NoticeLevel.Glow or NoticeLevel.Lively) return rule.Level;
        if (notice.Priority != NoticePriority.Low && (notice.Call || NoticeClock.Alerting(notice.Timing, now))) return NoticeLevel.Persistent;
        if (notice.Level != NoticeLevel.Auto) return notice.Level;
        if (notice.Priority == NoticePriority.High
            || notice.Category is NoticeCategory.Error or NoticeCategory.Warning or NoticeCategory.Task or NoticeCategory.Success) return NoticeLevel.Lively;
        if (notice.Priority == NoticePriority.Low || notice.Category == NoticeCategory.Info) return NoticeLevel.Plain;
        return Default(fallback);
    }

    // The setting's value as it may be used: Plain, Glow or Lively.
    public static NoticeLevel Default(NoticeLevel level) => level is NoticeLevel.Plain or NoticeLevel.Glow or NoticeLevel.Lively ? level : NoticeLevel.Glow;

    // What the island plays. subdued: the notice shows beyond the rate limit, so it shows as a plain one. The indicator has no
    // room for words: a plain notice lights its line once, without the bloom.
    public static NoticeEffect Effect(NoticeLevel level, bool subdued, IslandForm form) => (subdued ? NoticeLevel.Plain : level) switch
    {
        NoticeLevel.Lively or NoticeLevel.Persistent => NoticeEffect.Liquid,
        NoticeLevel.Glow => NoticeEffect.Glow,
        NoticeLevel.Plain when form == IslandForm.Line => NoticeEffect.Brighten,
        _ => NoticeEffect.Still
    };

    // How long the effect plays, in seconds, without the liquid (whose model knows its own length).
    public static double EffectLength(NoticeEffect effect, IslandForm form) => effect switch
    {
        NoticeEffect.Brighten => IslandLiquid.Breath,
        NoticeEffect.Glow => form == IslandForm.Line ? 2 * IslandLiquid.Breath : IslandLiquid.GlowLength,
        _ => 0
    };
}

// Where a notice's icon and words sit in the island (NoticeAlign).
public static class NoticeAligns
{
    // How far the group moves right from the left of the room it has, in whole DIPs: none of what is left over, half of it or
    // all. A group that fills the room or is longer stays at the left, where its words start scrolling.
    public static double Shift(double room, double content, NoticeAlign align)
        => content >= room ? 0 : Math.Floor((room - content) * (align switch { NoticeAlign.Left => 0, NoticeAlign.Center => .5, _ => 1 }));
}
