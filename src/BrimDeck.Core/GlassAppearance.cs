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
//   are compared with it. A new scene is taken at once instead: two measures in a row that agree with each other (within
//   Agree) for at least JumpSpan and lie far from the average (beyond Jump, a factor of about three in brightness) move the average
//   onto them, so a switch from a dark app to a white page turns the glass within a quarter of a second, while a flash still only
//   nudges it.
//   The decision (review v2 U06) runs Stable -> Candidate -> InFlight -> SettledHold. Candidate: the smoothed measure is past
//   the threshold, and since the earlier of that and the first raw measure past it the backdrop has stayed there for
//   CandidateSpan (250 ms, the upper end of the 180-250 ms range: a page's 200 ms white flash never turns the look; the price is
//   that a real new scene turns no sooner than 250 ms). A moved average alone (one bright measure after a grey backdrop) is
//   not enough. InFlight: from the decision until the palette has arrived (Arrived, or MaxFlight at the latest); a candidate
//   for the opposite look reverses it meanwhile without waiting for any hold. SettledHold: from the arrival the look holds for
//   at least Dwell; a turn that starts within Restless of the hold ending doubles the hold, up to MaxHold (a backdrop that
//   keeps flipping), and a turn after a quieter while starts from Dwell again.
//   Video: while most of the backdrop changes from capture to capture (more than VideoShare of its pixels, answers no further
//   apart than VideoGap) for longer than VideoAfter, the backdrop is a video (or a scroll, a fade) and the look holds: its
//   cuts and flashes never turn it. Only a backdrop that stays past the threshold for VideoEscape turns it meanwhile (a video
//   that has become bright for good). The video state ends VideoGap after the last large change.
//   Held: while the open panel is shown (Apple: large surfaces do not flip) or the island is pressed, nothing turns, so a turn
//   never runs over the panel's or the press's own motion; the measure goes on, and once released the look may turn after
//   Settle.
// Times are milliseconds on any steady clock.
public sealed class GlassAppearance(bool dark, bool appDark, long videoGap = GlassAppearance.VideoGap)
{
    public const double DarkModeToLight = .72, DarkModeToDark = .55, LightModeToDark = .28, LightModeToLight = .45;
    public const double LowQuantile = .2, HighQuantile = .8;
    public const double Tau = 200;
    public const double Jump = 1.1, Agree = .25;
    // The new scene's measures must have agreed for this long (not only two readings in a row: in a burst they are 80 ms apart),
    // so a page's white flash between two dark pages does not turn the glass.
    public const long JumpSpan = 250;
    public const long Dwell = 1750, MaxHold = 8000, Restless = 4000, Settle = 250;
    // How long the backdrop must stay past the threshold before the look turns (see above), and the longest a turn counts as
    // under way when its arrival is never told (a panel hidden mid-way): the palette's turn takes about 365 ms.
    public const long CandidateSpan = 250, MaxFlight = 1000;
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

    // The same middle quantiles from raw luma samples already sorted in ascending order.
    public static double SortedRangeMean(ReadOnlySpan<double> sorted)
    {
        if (sorted.IsEmpty) return double.NaN;
        double from = sorted.Length * LowQuantile, to = sorted.Length * HighQuantile;
        double sum = 0;
        for (int i = (int)from; i < to; i++)
            sum += sorted[i] * (Math.Min(i + 1, to) - Math.Max(i, from));
        return sum / (to - from);
    }

    public bool Dark { get; private set; } = dark;
    public bool AppDark { get; private set; } = appDark;
    // The thresholds the smoothed measure is compared with now.
    public double ToLight => AppDark ? DarkModeToLight : LightModeToLight;
    public double ToDark => AppDark ? DarkModeToDark : LightModeToDark;
    // The smoothed measure (NaN before the first).
    public double Smoothed => double.IsNaN(_log) ? double.NaN : Math.Exp(_log);
    // How long the look holds after it last settled now.
    public long HoldFor { get; private set; } = Dwell;
    // Whether the backdrop is a video now (see above).
    public bool Video => _movingSince >= 0 && _movedAt - _movingSince >= VideoAfter;
    // Where the decision stands at the last measure, or at now (see above).
    public GlassTurnState State => StateAt(_fedAt);
    public GlassTurnState StateAt(long now)
    {
        if (InFlightAt(now)) return GlassTurnState.InFlight;
        if (now - _settledAt < HoldFor) return GlassTurnState.SettledHold;
        return _candidateSince >= 0 ? GlassTurnState.Candidate : GlassTurnState.Stable;
    }

    private double _log = double.NaN, _lastLog = double.NaN, _runFrom = double.NaN;
    private long _fedAt, _agreeSince;
    // When the look last settled (the hold runs from it), when it last began a turn (restlessness counts from it), before when
    // nothing turns (released), since when the smoothed and the raw measure are past the threshold (-1: not), and the earlier
    // of the two (the candidate's start).
    private long _settledAt = long.MinValue / 2, _turnedAt = long.MinValue / 2, _calmUntil = long.MinValue / 2;
    private long _smoothPastSince = -1, _rawPastSince = -1, _candidateSince = -1, _flightFrom = long.MinValue / 2;
    private bool _held, _inFlight, _immediate;
    // Since when large changes have come no further apart than VideoGap (-1: none), and the last one.
    private long _movingSince = -1, _movedAt = long.MinValue / 2;

    // A turn under way counts from its first decision, a reversal included, so reversals cannot keep one going.
    private bool InFlightAt(long now) => _inFlight && now - _flightFrom < MaxFlight;

    // A measure at now (the same measure fed again while the backdrop holds still: the average still moves toward it). Held:
    // the open panel shown or the island pressed. True when the look changed (Dark tells which): a turn begins, or a turn
    // under way reverses.
    public bool Feed(double luma, long now, bool held = false)
    {
        if (!double.IsFinite(luma)) return false;
        // Prior's waiver holds for the next measure only.
        bool immediate = _immediate;
        _immediate = false;
        double log = Math.Log(Math.Max(luma, Floor));
        if (double.IsNaN(_log)) _log = log;
        else
        {
            // The distance is from the average where the run of agreeing measures began: meanwhile the average already follows it.
            bool agrees = !double.IsNaN(_lastLog) && Math.Abs(log - _lastLog) < Agree;
            if (!agrees) { _agreeSince = now; _runFrom = _log; }
            if (agrees && Math.Abs(log - _runFrom) > Jump && now - _agreeSince >= JumpSpan) _log = log;
            else _log += (1 - Math.Exp(-Math.Max(0, now - _fedAt) / Tau)) * (log - _log);
        }
        if (double.IsNaN(_lastLog)) { _agreeSince = now; _runFrom = _log; }
        _lastLog = log;
        _fedAt = now;
        if (_inFlight && !InFlightAt(now)) Settled(_flightFrom + MaxFlight);
        if (_movingSince >= 0 && now - _movedAt > videoGap) _movingSince = -1;
        if (_held && !held) _calmUntil = now + Settle;
        _held = held;
        double smoothed = Math.Exp(_log);
        bool wants = Dark ? !(smoothed > ToLight) : smoothed < ToDark;
        bool rawPast = Dark ? luma > ToLight : luma < ToDark;
        _rawPastSince = !rawPast ? -1 : _rawPastSince < 0 ? now : _rawPastSince;
        if (wants == Dark) { _smoothPastSince = _candidateSince = -1; return false; }
        if (_smoothPastSince < 0) _smoothPastSince = now;
        _candidateSince = _rawPastSince >= 0 ? Math.Min(_rawPastSince, _smoothPastSince) : _smoothPastSince;
        if (held || now < _calmUntil) return false;
        bool reversal = InFlightAt(now);
        if (!reversal && now - _settledAt < HoldFor) return false;
        if (Video && now - _smoothPastSince < VideoEscape) return false;
        if (!immediate && now - _candidateSince < CandidateSpan) return false;
        // A reversal is a restless backdrop by definition: it doubles the hold that follows.
        HoldFor = reversal || now - _turnedAt < HoldFor + Restless ? Math.Min(HoldFor * 2, MaxHold) : Dwell;
        if (!reversal) _flightFrom = now;
        Dark = wants; _turnedAt = now; _inFlight = true;
        _smoothPastSince = _rawPastSince = _candidateSince = -1;
        return true;
    }

    // How far past the threshold the area an opening panel is about to cover must be for the look to turn with the opening.
    public const double OpenMargin = .1;

    // The panel begins to open over this measure (the area it is about to cover, read before the panel's lock holds): clearly
    // past the threshold (by OpenMargin), the look turns with the opening at once - no candidate's span, no settled hold, the
    // panel is a new scene of its own - and the average starts from it; otherwise nothing changes, and the lock keeps the look.
    // Not over a video. True when the look turned (a turn under way reverses the same way).
    public bool Open(double covered, long now)
    {
        if (!double.IsFinite(covered) || Video) return false;
        if (!(Dark ? covered > ToLight + OpenMargin : covered < ToDark - OpenMargin)) return false;
        bool reversal = InFlightAt(now);
        HoldFor = reversal || now - _turnedAt < HoldFor + Restless ? Math.Min(HoldFor * 2, MaxHold) : Dwell;
        if (!reversal) _flightFrom = now;
        _log = Math.Log(Math.Max(covered, Floor));
        _lastLog = double.NaN;
        Dark = !Dark; _turnedAt = now; _inFlight = true;
        _smoothPastSince = _rawPastSince = _candidateSince = -1;
        return true;
    }

    // The palette has arrived at the look decided (its text swapped): the settled hold begins.
    public void Arrived(long now)
    {
        if (_inFlight) Settled(now);
    }

    private void Settled(long at)
    {
        _inFlight = false;
        _settledAt = at;
    }

    // The readings stopped being trustworthy (none fresh for a while, or late, or for another place): a candidate does not
    // carry over the gap, and a new scene's measures must agree again from the next one on.
    public void Invalidate()
    {
        _smoothPastSince = _rawPastSince = _candidateSince = -1;
        _lastLog = double.NaN;
    }

    // How much of the backdrop changed since the capture before (0..1, the share of its pixels), when a capture compared it.
    public void Changed(double share, long now)
    {
        if (!(share > VideoShare)) return;
        if (_movingSince < 0 || now - _movedAt > videoGap) _movingSince = now;
        _movedAt = now;
    }

    // Set from outside (a theme turned by someone else): settled there now, which counts for the hold without lengthening it.
    public void Reset(bool dark, long now)
    {
        Dark = dark; Settled(now);
        _smoothPastSince = _rawPastSince = _candidateSince = -1;
        _immediate = false;
    }

    // The Windows app mode changed: the thresholds lean the other way, and the look may turn at once (no hold).
    public void Prior(bool appDark)
    {
        if (appDark == AppDark) return;
        // The measure is not new, only the thresholds moved: no hold, and no candidate's span for the next measure.
        AppDark = appDark; Settled(long.MinValue / 2);
        _smoothPastSince = _rawPastSince = _candidateSince = -1;
        _immediate = true;
    }
}

public enum GlassTurnState { Stable, Candidate, InFlight, SettledHold }
