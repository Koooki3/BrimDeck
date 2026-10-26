namespace BrimDeck.Core;

// Which look automatic liquid glass ("liquid-glass") takes from what lies behind the island: dark glass with light text over
// dark content, light glass with dark text over light content, as Apple's glass adapts; the text keeps its contrast either way.
//   The measure: the mean of the middle of the backdrop's brightness (sRGB luma 0..1 between its LowQuantile and HighQuantile,
//   RangeMean), so a bright logo or subtitle over a dark video, or a dark sidebar beside a white page, does not pull it.
//   The Windows app mode is the prior (AppDark): in dark mode the glass turns light only above DarkModeToLight and back below
//   DarkModeToDark; in light mode it turns dark only below LightModeToDark and back above LightModeToLight. The first decision
//   leans the same way (FirstLook). A dark-mode user keeps dark glass over most backdrops, and gets light glass over a white
//   page, where the light text could no longer read (Windows' vendor guidance: vendor-benchmark §2.3).
//   Smoothing: an exponential moving average of the log of the measure with time constant Tau, the step weighted by the time
//   since the last measure (1 - e^(-Δt/Tau)), so it is the same at any frame rate; the thresholds above (a Schmitt trigger)
//   are compared with it. After a change the look holds for at least Dwell; a change that comes within Restless of the hold
//   ending doubles the hold, up to MaxHold (a backdrop that keeps flipping), and a change after a quieter while starts from
//   Dwell again.
//   Video: while most of the backdrop changes from capture to capture (more than VideoShare of its pixels, answers no further
//   apart than VideoGap) for longer than VideoAfter, the backdrop is a video (or a scroll, a fade) and the look holds: its
//   cuts and flashes never turn it. Only a backdrop that stays past the threshold for VideoEscape turns it meanwhile (a video
//   that has become bright for good). The video state ends VideoGap after the last large change.
//   Held: while the open panel is shown (Apple: large surfaces do not flip) or the island is pressed, nothing turns, so a turn
//   never runs over the panel's or the press's own motion; the measure goes on, and once released the look may turn after
//   Settle.
// Times are milliseconds on any steady clock.
public sealed class GlassAppearance(bool dark, bool appDark)
{
    public const double DarkModeToLight = .72, DarkModeToDark = .55, LightModeToDark = .28, LightModeToLight = .45;
    public const double LowQuantile = .2, HighQuantile = .8;
    public const double Tau = 400;
    public const long Dwell = 1750, MaxHold = 8000, Restless = 4000, Settle = 250;
    public const double VideoShare = .5;
    public const long VideoAfter = 1000, VideoGap = 400, VideoEscape = 5000;
    // The darkest measure the log is taken of (a black backdrop would otherwise weigh without bound).
    private const double Floor = .02;

    // The look a first measure, with nothing shown before it, decides, leaning toward the Windows app mode.
    public static bool FirstLook(double luma, bool appDark) => appDark ? !(luma > DarkModeToLight) : luma < LightModeToDark;

    // The mean of the measures between the low and high quantiles of a histogram whose bins split 0..1 evenly (a bin's
    // measures taken at its middle, a bin cut by a quantile counted in part). NaN for an empty histogram.
    public static double RangeMean(ReadOnlySpan<int> histogram, double low = LowQuantile, double high = HighQuantile)
    {
        long total = 0;
        foreach (int count in histogram) total += count;
        if (total == 0 || histogram.Length == 0) return double.NaN;
        double from = total * Math.Clamp(low, 0, 1), to = total * Math.Clamp(high, 0, 1);
        if (to <= from) to = Math.Min(total, from + 1);
        double seen = 0, sum = 0, weight = 0;
        for (int i = 0; i < histogram.Length && seen < to; i++)
        {
            double next = seen + histogram[i];
            double taken = Math.Min(next, to) - Math.Max(seen, from);
            if (taken > 0) { sum += taken * (i + .5) / histogram.Length; weight += taken; }
            seen = next;
        }
        return weight > 0 ? sum / weight : double.NaN;
    }

    public bool Dark { get; private set; } = dark;
    public bool AppDark { get; private set; } = appDark;
    // The thresholds the smoothed measure is compared with now.
    public double ToLight => AppDark ? DarkModeToLight : LightModeToLight;
    public double ToDark => AppDark ? DarkModeToDark : LightModeToDark;
    // The smoothed measure (NaN before the first).
    public double Smoothed => double.IsNaN(_log) ? double.NaN : Math.Exp(_log);
    // How long the look holds after its last change now.
    public long HoldFor { get; private set; } = Dwell;
    // Whether the backdrop is a video now (see above).
    public bool Video => _movingSince >= 0 && _movedAt - _movingSince >= VideoAfter;

    private double _log = double.NaN;
    private long _fedAt;
    // The last change of either kind (the hold runs from it), the last one this decided (restlessness counts from it), since
    // when the smoothed measure has been past the threshold (-1: it is not), and before when nothing turns (released).
    private long _changedAt = long.MinValue / 2, _turnedAt = long.MinValue / 2, _pastSince = -1, _calmUntil = long.MinValue / 2;
    private bool _held;
    // Since when large changes have come no further apart than VideoGap (-1: none), and the last one.
    private long _movingSince = -1, _movedAt = long.MinValue / 2;

    // A measure at now (the same measure fed again while the backdrop holds still: the average still moves toward it). Held:
    // the open panel shown or the island pressed. True when the look changed (Dark tells which).
    public bool Feed(double luma, long now, bool held = false)
    {
        if (!double.IsFinite(luma)) return false;
        double log = Math.Log(Math.Max(luma, Floor));
        if (double.IsNaN(_log)) _log = log;
        else _log += (1 - Math.Exp(-Math.Max(0, now - _fedAt) / Tau)) * (log - _log);
        _fedAt = now;
        if (_movingSince >= 0 && now - _movedAt > VideoGap) _movingSince = -1;
        if (_held && !held) _calmUntil = now + Settle;
        _held = held;
        double smoothed = Math.Exp(_log);
        bool wants = Dark ? !(smoothed > ToLight) : smoothed < ToDark;
        if (wants == Dark) { _pastSince = -1; return false; }
        if (_pastSince < 0) _pastSince = now;
        if (held || now < _calmUntil || now - _changedAt < HoldFor) return false;
        if (Video && now - _pastSince < VideoEscape) return false;
        HoldFor = now - _turnedAt < HoldFor + Restless ? Math.Min(HoldFor * 2, MaxHold) : Dwell;
        Dark = wants; _changedAt = _turnedAt = now; _pastSince = -1;
        return true;
    }

    // How much of the backdrop changed since the capture before (0..1, the share of its pixels), when a capture compared it.
    public void Changed(double share, long now)
    {
        if (!(share > VideoShare)) return;
        if (_movingSince < 0 || now - _movedAt > VideoGap) _movingSince = now;
        _movedAt = now;
    }

    // Set from outside (a theme turned by someone else): counts as a change for the hold, without lengthening it.
    public void Reset(bool dark, long now)
    {
        Dark = dark; _changedAt = now; _pastSince = -1;
    }

    // The Windows app mode changed: the thresholds lean the other way, and the look may turn at once (no hold).
    public void Prior(bool appDark)
    {
        if (appDark == AppDark) return;
        AppDark = appDark; _changedAt = long.MinValue / 2; _pastSince = -1;
    }
}
