namespace BrimDeck.Core;

// The level bars' heights, 0 to 1 and leftmost first: from the session's peak over time (Tick), or as computed from captured
// audio (Show). They are kept apart from the element that draws them, so the panel can hand them to the collapsed row, which is
// built again on every change of its width: the new row shows the bars where they were instead of starting again from rest.
public sealed class EqualizerBars
{
    public const int Count = 5;
    private readonly (double Time, double Peak)[] _history = new (double, double)[64];
    private readonly double[] _levels = new double[Count];
    private int _next, _count;
    private double _time;

    public float Peak { get; private set; }
    public bool Playing { get; private set; }
    public ReadOnlySpan<double> Levels => _levels;

    public void Tick(float peak, bool playing, double seconds = 1d / 30)
    {
        Peak = float.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0; Playing = playing;
        if (!playing)
        {
            Array.Clear(_levels); _next = _count = 0; _time = 0;
            return;
        }
        seconds = double.IsFinite(seconds) ? Math.Clamp(seconds, .001, .5) : 1d / 30;
        _time += seconds;
        _history[_next] = (_time, Peak); _next = (_next + 1) % _history.Length;
        _count = Math.Min(_count + 1, _history.Length);
        double low = Peak, high = Peak;
        for (int i = 0; i < _count; i++)
        {
            var sample = Previous(i);
            if (_time - sample.Time > 1.6) break;
            low = Math.Min(low, sample.Peak); high = Math.Max(high, sample.Peak);
        }
        // Expand the recent musical dynamics instead of compressing absolute
        // volume with a square root. A minimum range prevents noise amplification.
        double range = Math.Max(.0025, Math.Max(high * .25, high - low));
        for (int i = 0; i < _levels.Length; i++)
        {
            double sample = SampleAt(_time - i * .075);
            double contrast = Math.Clamp((sample - low) / range, 0, 1);
            double target = Math.Clamp(sample / .0015, 0, 1) * (.1 + .9 * Math.Pow(contrast, .8));
            double response = 1 - Math.Exp(-seconds / (target > _levels[i] ? .035 : .085));
            _levels[i] += (target - _levels[i]) * response;
        }
    }

    // Bar values already computed from captured audio, leftmost first.
    public void Show(ReadOnlySpan<double> levels, bool playing)
    {
        Playing = playing;
        for (int i = 0; i < _levels.Length; i++) _levels[i] = playing && i < levels.Length ? Math.Clamp(levels[i], 0, 1) : 0;
    }

    private (double Time, double Peak) Previous(int offset) => _history[(_next - 1 - offset + _history.Length) % _history.Length];
    private double SampleAt(double time)
    {
        var newer = Previous(0);
        for (int i = 0; i < _count; i++)
        {
            var older = Previous(i);
            if (older.Time <= time)
                return newer.Time == older.Time ? older.Peak : older.Peak + (newer.Peak - older.Peak) * (time - older.Time) / (newer.Time - older.Time);
            newer = older;
        }
        return 0;
    }
}
