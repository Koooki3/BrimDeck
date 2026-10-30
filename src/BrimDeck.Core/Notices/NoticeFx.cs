namespace BrimDeck.Core.Notices;

// How strongly the liquid motion (NoticeLiquid) plays for one notice, by what kind of notice it is, so that the motion carries
// the notice's weight and not only its arrival: an ordinary notice plays it as designed, a high priority one longer and with
// more droplets, an alarm, a call or an error stronger still and in the state's color, and what only informs (a summary of what
// arrived while BrimDeck kept quiet, a notice the user set to low priority) lightly, with a short glow and a few droplets.
// Whatever the source (the Windows notification list, brim-notify, WSL, a time notice), it is shown by the notice window as an
// AttentionItem of kind Notice or Summary, so one rule covers them all. (A quota alert and a track change are drawn by the island
// itself, not by the notice window, and do not play this motion.)
public enum NoticeFxTone { Theme, Warning, Danger }

// Intensity: the design's 1 to 10. Separation: how long droplets are thrown, in seconds. Glow: the glow's strength, 0 to 1.25.
public readonly record struct NoticeFxPlan(double Intensity, double Separation, double Glow, NoticeFxTone Tone)
{
    // The design's own defaults: intensity 5 and a separation of 0.6 s.
    public static NoticeFxPlan Standard => new(5, .6, 1, NoticeFxTone.Theme);
}

public static class NoticeFx
{
    // The motion keeps the panel's own pace: the animation duration set in the settings (360 ms by default) stretches or shortens
    // it as it does every move of the panel and its notices, within 0.75 to 1.5 times, so a slow setting does not leave the liquid
    // racing ahead of the surface it leaves and a fast one does not leave it dawdling. 1 at the default.
    public static double Pace(int animationDuration) => Math.Clamp(Math.Clamp(animationDuration, 100, 1000) / 360d, .75, 1.5);

    // The indicator's own motion (Curves.LineArrive, Curves.LineReturn) is the same for every notice but for its amplitude, which
    // follows the notice's weight as the liquid's intensity does: half to one and a half times the standard.
    public static double LineScale(NoticeFxPlan plan) => Math.Clamp(plan.Intensity / 5, .5, 1.5);

    // How far the line draws back before it grows into a card of cardWidth (at most 5 DIPs, and under a tenth of its length), as a
    // fraction of the whole move; none where the card is hardly wider than the line. The distances in DIPs are those before the panel's
    // scale (PanelScales), which the widths are drawn at, so they are multiplied by it.
    public static double LineWindUp(double lineWidth, double cardWidth, NoticeFxPlan plan, double scale = 1)
    {
        double s = Valid(scale);
        return cardWidth - lineWidth < 24 * s ? 0 : Math.Min(5 * s, .09 * lineWidth) * LineScale(plan) / (cardWidth - lineWidth);
    }

    // How far the surface carries on past the line's length (shorter, and thinner) as it returns from fromWidth (at most 7 DIPs, and
    // about an eighth of its length), as a fraction of the whole move.
    public static double LineCarry(double lineWidth, double fromWidth, NoticeFxPlan plan, double scale = 1)
    {
        double s = Valid(scale);
        return fromWidth - lineWidth < 24 * s ? 0 : Math.Min(7 * s, .12 * lineWidth) * LineScale(plan) / (fromWidth - lineWidth);
    }

    private static double Valid(double scale) => scale > 0 && double.IsFinite(scale) ? scale : 1;

    // category: the notice's own category (null for a summary); call: an incoming call.
    public static NoticeFxPlan For(AttentionKind kind, NoticePriority priority, bool urgent, bool call, NoticeCategory? category)
    {
        // What only informs plays lightly.
        if (kind == AttentionKind.Summary) return new(2, .4, .6, NoticeFxTone.Theme);
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
