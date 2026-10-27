namespace BrimDeck.Core.Notices;

// How strongly the liquid motion (NoticeLiquid) plays for one notice, by what kind of notice it is, so that the motion carries
// the notice's weight and not only its arrival: an ordinary notice plays it as designed, a high priority one longer and with
// more droplets, an alarm, a call or an error stronger still and in the state's color, and what only informs (a summary of what
// arrived while BrimDeck kept quiet, a track change, a notice the user set to low priority) lightly, with a short glow and a
// few droplets. Whatever the source (the Windows notification list, brim-notify, WSL, a quota alert, a device's low battery, a
// time notice), it arrives as an AttentionItem, so one rule covers them all.
public enum NoticeFxTone { Theme, Warning, Danger }

// Intensity: the design's 1 to 10. Separation: how long droplets are thrown, in seconds. Glow: the glow's strength, 0 to 1.25.
public readonly record struct NoticeFxPlan(double Intensity, double Separation, double Glow, NoticeFxTone Tone)
{
    // The design's own defaults: intensity 5 and a separation of 0.6 s.
    public static NoticeFxPlan Standard => new(5, .6, 1, NoticeFxTone.Theme);
}

public static class NoticeFx
{
    // category: the notice's own category (null for a summary, a quota alert or a track change); call: an incoming call.
    public static NoticeFxPlan For(AttentionKind kind, NoticePriority priority, bool urgent, bool call, NoticeCategory? category)
    {
        // What only informs plays lightly.
        if (kind is AttentionKind.Summary or AttentionKind.Track) return new(2, .4, .6, NoticeFxTone.Theme);
        // A quota alert is a warning by its nature.
        if (kind == AttentionKind.Quota) return new(5, .6, 1, NoticeFxTone.Warning);
        var tone = category switch { NoticeCategory.Error => NoticeFxTone.Danger, NoticeCategory.Warning => NoticeFxTone.Warning, _ => NoticeFxTone.Theme };
        // An alarm, a call, a countdown that ended, an event's reminder.
        if (urgent || call) return new(8, 1, 1.25, tone == NoticeFxTone.Theme ? NoticeFxTone.Warning : tone);
        return priority switch
        {
            NoticePriority.High => new(7, .9, 1.15, tone),
            NoticePriority.Low => new(3, .45, .7, tone),
            _ => tone == NoticeFxTone.Theme ? NoticeFxPlan.Standard : NoticeFxPlan.Standard with { Tone = tone, Glow = 1.1 }
        };
    }
}
