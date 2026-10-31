namespace BrimDeck.Core;

// Splits a player's audio into frequency bands for the level bars: the bars, left to right, are low to high pitch, so a kick drum
// lifts the left bars, a cymbal the right ones, and a single tone only the bars around its pitch.
//
// One analysis serves any number of bars from 1 to MaxBars (the collapsed and the expanded equalizer are set to different
// lengths). A window of WindowSize samples goes through one FFT; its power is gathered into Bands narrow bands spaced evenly on a
// log axis between LowHz and the top of the audible range; and the bars are made from those narrow bands, as many bars as the
// panel draws, each an equal share of the log axis (BarRange). The narrow bands of the last RangeSeconds are kept, so the bars can
// be made again for another count without starting over: a bar's history, and so its scale, is rebuilt from them (Rebuild).
//
// Each bar is scaled against its own last RangeSeconds (so the treble is not always smaller than the bass, and a wide bar does not
// sit higher than a narrow one just for holding more bins) and against the loudest bar of the moment (so a steady tone keeps
// standing instead of being scaled down to nothing), with the sharpness of a recent rise added on top.
public sealed class EqualizerSignal
{
    // 4096 samples are 93 ms at 44.1 kHz and 10.8 Hz to a bin: the lowest bars of a long equalizer (20 bars: 40 to 54 Hz, 54 to
    // 72 Hz) lie in a bin or two. 2048 samples put several of them into the same bin.
    public const int WindowSize = 4096;
    public const int Bands = 60;
    public const int MaxBars = Bands;
    public const int DefaultBars = 5;
    public const double LowHz = 40, HighHz = 16000;
    private const double RangeSeconds = 3, RiseSmoothing = .08, Attack = .03, Release = .2, IdleSeconds = .15, StaleSeconds = .3;
    // A full-scale sine puts about 60 dB in its bin with this window. A bar whose share of the narrow bands averages less than
    // this (a paused player's dither, a band with nothing in it) is silent and does not move, whatever it was scaled against.
    private const double SilenceDb = -11;
    // Samples all below this (one least significant bit of 16-bit audio) are digital silence or dither: no analysis is made.
    private const double QuietPeak = 4e-5;
    // How far below the loudest bar a bar still counts for the absolute part of its level, in dB.
    private const double AbsoluteRange = 60;
    private const int HistoryCapacity = 200;

    private static readonly double[] Hann = Enumerable.Range(0, WindowSize).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / (WindowSize - 1))).ToArray();
    // The FFT's twiddle factors, e^(-2πik/N) for k below N/2, taken once instead of carried by a recurrence in every butterfly.
    private static readonly double[] TwiddleCos = Enumerable.Range(0, WindowSize / 2).Select(k => Math.Cos(-2 * Math.PI * k / WindowSize)).ToArray();
    private static readonly double[] TwiddleSin = Enumerable.Range(0, WindowSize / 2).Select(k => Math.Sin(-2 * Math.PI * k / WindowSize)).ToArray();

    private readonly int _rate;
    private readonly float[] _samples = new float[WindowSize];
    private readonly double[] _real = new double[WindowSize], _imaginary = new double[WindowSize];
    // Which bins make each narrow band, and how much of each bin's cell lies in it.
    private readonly int[] _first = new int[Bands], _start = new int[Bands + 1];
    private readonly float[] _weights;
    private readonly float[] _power = new float[Bands];
    // The narrow bands' power of the last RangeSeconds, newest last, with each analysis's time and the time since the one before.
    private readonly float[][] _frames = new float[HistoryCapacity][];
    private readonly double[] _frameTime = new double[HistoryCapacity], _frameStep = new double[HistoryCapacity];
    private int _frameOldest, _frameCount;
    // The bars as made for _bars of them: each bar's last RangeSeconds of levels in order and in time, kept as levels come and go
    // for its percentiles (sorting them all again on every analysis was most of the cost), and the levels the bars stand at.
    private readonly List<double>?[] _ordered = new List<double>?[MaxBars];
    private readonly Queue<(double Time, double Db)>?[] _ranges = new Queue<(double, double)>?[MaxBars];
    private readonly double[] _db = new double[MaxBars], _smoothed = new double[MaxBars], _state = new double[MaxBars];
    private readonly double[] _published = new double[MaxBars];
    private readonly object _gate = new();
    private int _write, _bars, _publishedBars, _requested;
    private double _last = double.NaN, _lastInput = double.NaN, _publishedAt = double.NaN;

    public EqualizerSignal(int sampleRate)
    {
        _rate = Math.Max(8000, sampleRate);
        double top = TopHz(_rate), delta = _rate / (double)WindowSize;
        var weights = new List<float>();
        for (int band = 0; band < Bands; band++)
        {
            // In bin units; bin k stands for the frequencies from k - .5 to k + .5.
            double low = Edge(top, band, Bands) / delta, high = Edge(top, band + 1, Bands) / delta;
            int first = Math.Max(1, (int)Math.Floor(low + .5)), last = Math.Min(WindowSize / 2 - 1, (int)Math.Floor(high + .5));
            _first[band] = first; _start[band] = weights.Count;
            for (int k = first; k <= last; k++) weights.Add((float)Math.Max(0, Math.Min(high, k + .5) - Math.Max(low, k - .5)));
        }
        _start[Bands] = weights.Count; _weights = [.. weights];
        Array.Fill(_smoothed, double.NaN);
    }

    // The highest frequency the bars show: 16 kHz, or less when the sample rate leaves no room above.
    public static double TopHz(int sampleRate) => Math.Min(HighHz, Math.Max(sampleRate, 8000) * .45);

    private static double Edge(double top, int index, int count) => LowHz * Math.Pow(top / LowHz, index / (double)count);

    // The frequencies bar index of bars covers, in Hz: the bars split LowHz to TopHz into equal shares of the log axis, so each
    // bar starts where the one before ends, whatever the count.
    public static (double Low, double High) BarRange(int bars, int index, int sampleRate)
    {
        bars = Math.Clamp(bars, 1, MaxBars); index = Math.Clamp(index, 0, bars - 1);
        double top = TopHz(sampleRate);
        return (Edge(top, index, bars), Edge(top, index + 1, bars));
    }

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

    // Analyse the most recent window and move the bars to time now (seconds), for as many bars as Fill last asked for.
    public void Advance(double now)
    {
        double dt = double.IsNaN(_last) ? 1d / 60 : Math.Clamp(now - _last, .001, .1);
        _last = now;
        int requested = Volatile.Read(ref _requested), bars = requested <= 0 ? DefaultBars : Math.Min(requested, MaxBars);
        if (bars != _bars) Rebuild(bars);
        // Without input for a while the player has gone quiet, and nothing but silence needs no analysis at all: let the bars fall.
        bool heard = !double.IsNaN(_lastInput) && now - _lastInput < IdleSeconds && Analyse(now, dt);
        if (!heard) Fall(dt);
        lock (_gate)
        {
            Array.Copy(_state, _published, _bars); _publishedBars = _bars; _publishedAt = now;
        }
    }

    // The bars' levels, 0 to 1 and lowest pitch first, for levels.Length bars (up to MaxBars; any others are left at 0). The
    // analysis then goes on making that many bars. A count that has just changed is answered from the bars as they stood
    // until the next analysis, so no frame shows them at rest. Levels from an analysis older than a moment are not shown.
    public void Fill(Span<double> levels, double now)
    {
        int count = Math.Min(levels.Length, MaxBars);
        levels.Clear();
        if (count == 0) return;
        Volatile.Write(ref _requested, count);
        lock (_gate)
        {
            if (_publishedBars == 0 || double.IsNaN(_publishedAt) || now - _publishedAt > StaleSeconds) return;
            if (_publishedBars == count) _published.AsSpan(0, count).CopyTo(levels);
            else Resample(_published.AsSpan(0, _publishedBars), levels[..count]);
        }
    }

    public void Reset()
    {
        Array.Clear(_samples); _write = 0; _last = _lastInput = double.NaN;
        _frameOldest = _frameCount = 0;
        _bars = 0;
        Array.Fill(_smoothed, double.NaN); Array.Clear(_state);
        lock (_gate) { _publishedBars = 0; _publishedAt = double.NaN; Array.Clear(_published); }
    }

    // The levels of from stretched or squeezed along the pitch axis to fill to.
    public static void Resample(ReadOnlySpan<double> from, Span<double> to)
    {
        if (from.Length == 0) { to.Clear(); return; }
        for (int i = 0; i < to.Length; i++)
        {
            double at = Math.Clamp((i + .5) * from.Length / to.Length - .5, 0, from.Length - 1);
            int low = (int)at, high = Math.Min(from.Length - 1, low + 1);
            to[i] = from[low] + (from[high] - from[low]) * (at - low);
        }
    }

    // Makes the bars for a new count the way they would be had they been that many all along: from the narrow bands kept.
    private void Rebuild(int bars)
    {
        _bars = bars;
        for (int i = 0; i < bars; i++)
        {
            (_ordered[i] ??= new List<double>(128)).Clear(); (_ranges[i] ??= new Queue<(double, double)>()).Clear();
            _smoothed[i] = double.NaN; _state[i] = 0;
        }
        for (int n = 0; n < _frameCount; n++)
        {
            int slot = (_frameOldest + n) % HistoryCapacity;
            Step(_frames[slot], _frameTime[slot], _frameStep[slot]);
        }
    }

    // False when the window holds nothing to analyse.
    private bool Analyse(double now, double dt)
    {
        int tail = WindowSize - _write;
        bool loud = false;
        for (int i = 0; i < WindowSize; i++) if (Math.Abs(_samples[i]) >= QuietPeak) { loud = true; break; }
        if (!loud) return false;
        // The oldest sample first, the window applied as it is copied.
        for (int i = 0; i < tail; i++) { _real[i] = _samples[_write + i] * Hann[i]; _imaginary[i] = 0; }
        for (int i = tail; i < WindowSize; i++) { _real[i] = _samples[i - tail] * Hann[i]; _imaginary[i] = 0; }
        Fft(_real, _imaginary);
        for (int band = 0; band < Bands; band++)
        {
            double power = 0;
            int weight = _start[band], bin = _first[band];
            for (int end = _start[band + 1]; weight < end; weight++, bin++) power += _weights[weight] * (_real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin]);
            _power[band] = (float)power;
        }
        // Frames of the last RangeSeconds are kept for making the bars again.
        while (_frameCount > 0 && (now - _frameTime[_frameOldest] > RangeSeconds || _frameCount == HistoryCapacity)) { _frameOldest = (_frameOldest + 1) % HistoryCapacity; _frameCount--; }
        int slot = (_frameOldest + _frameCount) % HistoryCapacity;
        _frames[slot] ??= new float[Bands];
        _power.CopyTo(_frames[slot], 0); _frameTime[slot] = now; _frameStep[slot] = dt; _frameCount++;
        Step(_frames[slot], now, dt);
        return true;
    }

    // One analysis's narrow bands (power by band) moved into the bars.
    private void Step(float[] power, double now, double dt)
    {
        int bars = _bars;
        double share = Bands / (double)bars, loudest = double.MinValue;
        for (int i = 0; i < bars; i++)
        {
            // The bar takes the part of each narrow band it overlaps, and its mean power per narrow band is what is measured, so a
            // bar of three narrow bands and one of twenty are held to the same silence.
            double start = i * share, end = start + share, sum = 0;
            for (int band = (int)start; band < Bands && band < end; band++) sum += power[band] * (Math.Min(end, band + 1) - Math.Max(start, band));
            _db[i] = Math.Max(SilenceDb, 10 * Math.Log10(1e-12 + sum / share));
            loudest = Math.Max(loudest, _db[i]);
        }
        for (int i = 0; i < bars; i++)
        {
            double db = _db[i];
            var range = _ranges[i]!; var ordered = _ordered[i]!;
            range.Enqueue((now, db)); Insert(ordered, db);
            while (now - range.Peek().Time > RangeSeconds) Remove(ordered, range.Dequeue().Db);
            double floor = Percentile(ordered, .25), ceiling = Math.Max(Percentile(ordered, .98), floor + 15);
            double relative = Math.Pow(Math.Clamp((db - floor) / (ceiling - floor), 0, 1), 1.5);
            double absolute = Math.Clamp((db - (loudest - AbsoluteRange)) / AbsoluteRange, 0, 1);
            double level = Math.Max(relative, .9 * absolute * absolute);
            if (double.IsNaN(_smoothed[i])) _smoothed[i] = db;
            double rise = Math.Clamp((db - _smoothed[i]) / 6, 0, 1);
            _smoothed[i] += (db - _smoothed[i]) * (1 - Math.Exp(-dt / RiseSmoothing));
            double target = db <= SilenceDb ? 0 : Math.Clamp(.05 + .6 * level + .35 * rise, 0, 1);
            _state[i] += (target - _state[i]) * (1 - Math.Exp(-dt / (target > _state[i] ? Attack : Release)));
            if (_state[i] < .002 && target == 0) _state[i] = 0;
        }
    }

    private void Fall(double dt)
    {
        double keep = Math.Exp(-dt / Release);
        for (int i = 0; i < _bars; i++) { _state[i] = _state[i] < .002 ? 0 : _state[i] * keep; _smoothed[i] = double.NaN; }
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
