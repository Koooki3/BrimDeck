using BrimDeck.Core;

// Changing the number of bars (the collapsed and the expanded equalizer differ) makes the bars again from the narrow bands kept:
// in silence it must not bring back the level of the last sound.
internal static class EqualizerRebuildTests
{
    private const int Rate = 44100;

    // 10 ms blocks of stereo audio, analysed every third block as the capture does; the bars are read for 'bars' bars after each.
    private static double Feed(EqualizerSignal signal, Func<double, float> wave, double seconds, double start, int bars, double[] levels)
    {
        var block = new float[441 * 2]; double now = start;
        signal.Fill(levels.AsSpan(0, bars), start, EqualizerMode.Spectrum);
        for (int n = 0; n < (int)Math.Round(seconds * 100); n++)
        {
            double t0 = start + n / 100d;
            for (int i = 0; i < 441; i++) { float v = wave(t0 + i / (double)Rate); block[i * 2] = v; block[i * 2 + 1] = v; }
            signal.Push(block, 2, t0 + .01);
            if (n % 3 != 2) continue;
            now = t0 + .01; signal.Advance(now); signal.Fill(levels.AsSpan(0, bars), now, EqualizerMode.Spectrum);
        }
        return now;
    }

    public static void Run(Action<string, bool> check)
    {
        // A tone, then silence: the bars have fallen to rest when the count changes.
        var tone = new EqualizerSignal(Rate); var five = new double[5];
        double toneEnd = Feed(tone, t => (float)(.5 * Math.Sin(2 * Math.PI * 440 * t)), 1.5, 0, 5, five);
        double peak = five.Max();
        double silentEnd = Feed(tone, _ => 0f, 1, toneEnd, 5, five);
        double rested = five.Max();
        var twelve = new double[12];
        tone.Fill(twelve, silentEnd, EqualizerMode.Spectrum);
        tone.Advance(silentEnd + .033); tone.Fill(twelve, silentEnd + .033, EqualizerMode.Spectrum);
        check("Equalizer rebuild: the tone lifted the bars and a second of silence let them rest first", peak > .3 && rested < .01);
        check("Equalizer rebuild: a bar count changed in silence does not bring back the level of the last sound", twelve.Max() < .01);

        // A count changed while the sound goes on still makes the bars from the history, so they keep standing.
        var live = new EqualizerSignal(Rate); var liveFive = new double[5];
        double liveEnd = Feed(live, t => (float)(.5 * Math.Sin(2 * Math.PI * 440 * t)), 1.5, 0, 5, liveFive);
        var liveTwelve = new double[12];
        double afterEnd = Feed(live, t => (float)(.5 * Math.Sin(2 * Math.PI * 440 * t)), .3, liveEnd, 12, liveTwelve);
        check("Equalizer rebuild: a bar count changed while the sound goes on keeps the bars standing", liveTwelve.Max() > .3 && afterEnd > liveEnd);
    }
}
