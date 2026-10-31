// The equalizer's analysis as upstream/dev (5fb96075) had it before the Spectrum way was added, kept unchanged but for its name
// as the reference the Flow way is compared with, frame by frame (EqualizerModeTests). Do not edit.


// Turns a player's audio into the single value that flows across the level bars.
// The value combines how loud each of five frequency bands is relative to its own
// last three seconds with how sharply any band just rose, so a piano note counts as
// much as a kick drum. The bars show it at increasing delays, left to right.
internal sealed class LegacyFlowSignal
{
    public const int WindowSize = 2048;
    public const double BarDelay = .075;
    private const double RangeSeconds = 3, RiseSmoothing = .08, Attack = .03, Release = .2, HistorySeconds = 2;
    // A full-scale sine concentrates about 54 dB in its band with this window; anything 60 dB
    // below that is treated as silence, so a paused player's dither never gets scaled up.
    private const double SilenceDb = -6;
    // The history reaches back past the last of 20 bars (19 BarDelay), so every bar has a value.
    private static readonly double[] Edges = [50, 150, 500, 1500, 4500, 14000];
    private static readonly double[] Hann = Enumerable.Range(0, WindowSize).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / (WindowSize - 1))).ToArray();
    // The FFT's twiddle factors, e^(-2πik/N) for k below N/2, taken once instead of carried by a recurrence in every butterfly.
    private static readonly double[] TwiddleCos = Enumerable.Range(0, WindowSize / 2).Select(k => Math.Cos(-2 * Math.PI * k / WindowSize)).ToArray();
    private static readonly double[] TwiddleSin = Enumerable.Range(0, WindowSize / 2).Select(k => Math.Sin(-2 * Math.PI * k / WindowSize)).ToArray();
    private readonly int _rate;
    private readonly float[] _samples = new float[WindowSize];
    private readonly double[] _real = new double[WindowSize], _imaginary = new double[WindowSize];
    private readonly Queue<(double Time, double Db)>[] _ranges = Enumerable.Range(0, Edges.Length - 1).Select(_ => new Queue<(double, double)>()).ToArray();
    private readonly double[] _smoothed = new double[Edges.Length - 1], _rises = new double[Edges.Length - 1];
    private readonly List<(double Time, double Value)> _history = [];
    // Each band's last RangeSeconds of levels in order, kept as levels come and go, for its percentiles: sorting them all again
    // on every analysis was most of its cost.
    private readonly List<double>[] _ordered = Enumerable.Range(0, Edges.Length - 1).Select(_ => new List<double>(128)).ToArray();
    private readonly object _gate = new();
    private int _write;
    private double _head, _last = double.NaN, _lastInput = double.NaN;

    public LegacyFlowSignal(int sampleRate)
    {
        _rate = sampleRate;
        Array.Fill(_smoothed, double.NaN);
    }

    public double Head { get { lock (_gate) return _head; } }

    // Interleaved samples scaled to -1..1; channels are averaged.
    public void Push(ReadOnlySpan<float> interleaved, int channels, double now)
    {
        for (int i = 0; i + channels <= interleaved.Length; i += channels)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[i + c];
            _samples[_write] = sum / channels; _write = (_write + 1) % WindowSize;
        }
        if (interleaved.Length > 0) _lastInput = now;
    }

    // Analyse the most recent window and advance the flowing value to time now (seconds).
    public void Advance(double now)
    {
        double dt = double.IsNaN(_last) ? 1d / 60 : Math.Clamp(now - _last, .001, .1);
        _last = now;
        // Without input for a while, the player has gone quiet: let the bars fall.
        double target = !double.IsNaN(_lastInput) && now - _lastInput < .15 ? Target(now, dt) : 0;
        lock (_gate)
        {
            _head += (target - _head) * (1 - Math.Exp(-dt / (target > _head ? Attack : Release)));
            _history.Add((now, _head));
            int stale = 0;
            while (stale < _history.Count && now - _history[stale].Time > HistorySeconds) stale++;
            if (stale > 0) _history.RemoveRange(0, stale);
        }
    }

    // The flowing value at an earlier analysis time, interpolated between analyses.
    public double Sample(double at)
    {
        lock (_gate)
        {
            if (_history.Count == 0 || at < _history[0].Time) return 0;
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i].Time > at) continue;
                if (i + 1 == _history.Count) return _history[i].Value;
                var (t0, v0) = _history[i]; var (t1, v1) = _history[i + 1];
                return t1 == t0 ? v0 : v0 + (v1 - v0) * (at - t0) / (t1 - t0);
            }
            return 0;
        }
    }

    public void Fill(Span<double> levels, double now)
    {
        for (int i = 0; i < levels.Length; i++) levels[i] = Sample(now - i * BarDelay);
    }

    public void Reset()
    {
        Array.Clear(_samples); _write = 0; _last = _lastInput = double.NaN;
        foreach (var range in _ranges) range.Clear();
        foreach (var ordered in _ordered) ordered.Clear();
        Array.Fill(_smoothed, double.NaN); Array.Clear(_rises);
        lock (_gate) { _head = 0; _history.Clear(); }
    }

    private double Target(double now, double dt)
    {
        for (int i = 0; i < WindowSize; i++) { _real[i] = _samples[(_write + i) % WindowSize] * Hann[i]; _imaginary[i] = 0; }
        Fft(_real, _imaginary);
        double level = 0;
        for (int b = 0; b < Edges.Length - 1; b++)
        {
            int k0 = (int)(Edges[b] * WindowSize / _rate), k1 = Math.Min(WindowSize / 2, Math.Max(k0 + 1, (int)(Edges[b + 1] * WindowSize / _rate)));
            double power = 0;
            for (int k = k0; k < k1; k++) power += _real[k] * _real[k] + _imaginary[k] * _imaginary[k];
            double db = Math.Max(SilenceDb, 10 * Math.Log10(1e-12 + power));
            var range = _ranges[b]; var ordered = _ordered[b];
            range.Enqueue((now, db)); Insert(ordered, db);
            while (now - range.Peek().Time > RangeSeconds) Remove(ordered, range.Dequeue().Db);
            double floor = Percentile(ordered, .25), ceiling = Math.Max(Percentile(ordered, .98), floor + 15);
            level += Math.Pow(Math.Clamp((db - floor) / (ceiling - floor), 0, 1), 1.5) / (Edges.Length - 1);
            if (double.IsNaN(_smoothed[b])) _smoothed[b] = db;
            _rises[b] = Math.Clamp((db - _smoothed[b]) / 6, 0, 1);
            _smoothed[b] += (db - _smoothed[b]) * (1 - Math.Exp(-dt / RiseSmoothing));
        }
        // The two clearest onsets, so one noisy band cannot drive the bars alone.
        double first = 0, second = 0;
        foreach (var rise in _rises) { if (rise > first) { second = first; first = rise; } else if (rise > second) second = rise; }
        return Math.Clamp(.05 + .45 * level + .45 * (first + second) / 2, 0, 1);
    }

    private static double Percentile(List<double> sorted, double p) => sorted[(int)Math.Clamp(p * (sorted.Count - 1), 0, sorted.Count - 1)];
    private static void Insert(List<double> sorted, double value)
    {
        int at = sorted.BinarySearch(value);
        sorted.Insert(at < 0 ? ~at : at, value);
    }
    private static void Remove(List<double> sorted, double value)
    {
        int at = sorted.BinarySearch(value);
        if (at >= 0) sorted.RemoveAt(at);
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            // The stage's twiddle for j is the window's for j * (n / length).
            int stride = n / length;
            for (int i = 0; i < n; i += length)
            {
                for (int j = 0; j < length / 2; j++)
                {
                    int a = i + j, b = a + length / 2;
                    double cr = TwiddleCos[j * stride], ci = TwiddleSin[j * stride];
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                }
            }
        }
    }
}
