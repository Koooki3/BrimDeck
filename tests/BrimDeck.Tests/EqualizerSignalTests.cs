using BrimDeck.Core;

internal static class EqualizerSignalTests
{
    private const int Rate = 44100;

    public static void Run(Action<string, bool> check)
    {
        // Feed 10 ms blocks of stereo audio and analyse after each block, as the capture thread does; the loudest bar is read after each.
        static List<(double Time, double Loudest)> Play(EqualizerSignal signal, Func<double, float> wave, double seconds, double start = 0, int bars = 5)
        {
            var peaks = new List<(double, double)>(); var block = new float[441 * 2]; var levels = new double[bars];
            signal.Fill(levels, start);
            for (int n = 0; n < (int)(seconds * 100); n++)
            {
                double t0 = start + n / 100d;
                for (int i = 0; i < 441; i++) { float v = wave(t0 + i / (double)Rate); block[i * 2] = v; block[i * 2 + 1] = v; }
                signal.Push(block, 2, t0 + .01); signal.Advance(t0 + .01); signal.Fill(levels, t0 + .01); peaks.Add((t0 + .01, levels.Max()));
            }
            return peaks;
        }
        static float Sine(double t, double hz, double amplitude) => (float)(amplitude * Math.Sin(2 * Math.PI * hz * t));
        // A decaying tone started every period seconds, like a drum hit.
        static float Hits(double t, double hz, double period, double amplitude, double decay) { double since = t % period; return (float)(amplitude * Math.Exp(-since / decay) * Math.Sin(2 * Math.PI * hz * since)); }

        // A paused player can still send dither of one least significant bit.
        var random = new Random(7);
        var dither = Play(new EqualizerSignal(Rate), _ => (float)((random.Next(3) - 1) / 32768d), 4);
        check("Dither from a paused player keeps the bars at the bottom", dither.All(h => h.Loudest <= .06));

        var flowing = new EqualizerSignal(Rate);
        var played = Play(flowing, t => Sine(t, 220, .3) + Hits(t, 60, .5, .7, .08), 4, 0, CompactMusicLayout.MaximumBars);
        double now = played[^1].Time;
        var levels = new double[CompactMusicLayout.MaximumBars];
        flowing.Fill(levels, now);
        check("Bars of the most bars sound at least where the music has something, and others rest", levels.Max() > .2 && levels.All(level => level >= 0 && level <= 1));
        var rest = new double[CompactMusicLayout.MaximumBars];
        for (int n = 1; n <= 100; n++) flowing.Advance(now + n / 100d);
        flowing.Fill(rest, now + 1);
        check("The bars fall when the player stops sending audio", played.Max(h => h.Loudest) > .2 && rest.Max() < .05);
    }
}
