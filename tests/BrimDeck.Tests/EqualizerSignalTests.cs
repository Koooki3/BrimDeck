using BrimDeck.Core;

internal static class EqualizerSignalTests
{
    private const int Rate = 44100;

    public static void Run(Action<string, bool> check)
    {
        // Feed 10 ms blocks of stereo audio and analyse after each block, as the capture thread does.
        static List<(double Time, double Head)> Play(EqualizerSignal signal, Func<double, float> wave, double seconds, double start = 0)
        {
            var heads = new List<(double, double)>(); var block = new float[441 * 2];
            for (int n = 0; n < (int)(seconds * 100); n++)
            {
                double t0 = start + n / 100d;
                for (int i = 0; i < 441; i++) { float v = wave(t0 + i / (double)Rate); block[i * 2] = v; block[i * 2 + 1] = v; }
                signal.Push(block, 2, t0 + .01); signal.Advance(t0 + .01); heads.Add((t0 + .01, signal.Head));
            }
            return heads;
        }
        static float Sine(double t, double hz, double amplitude) => (float)(amplitude * Math.Sin(2 * Math.PI * hz * t));
        // A decaying tone started every period seconds, like a drum hit.
        static float Hits(double t, double hz, double period, double amplitude, double decay) { double since = t % period; return (float)(amplitude * Math.Exp(-since / decay) * Math.Sin(2 * Math.PI * hz * since)); }

        // A paused player can still send dither of one least significant bit.
        var random = new Random(7);
        var dither = Play(new EqualizerSignal(Rate), _ => (float)((random.Next(3) - 1) / 32768d), 4);
        check("Dither from a paused player keeps the bars at the bottom", dither.All(h => h.Head <= .06));

        var flowing = new EqualizerSignal(Rate);
        var played = Play(flowing, t => Sine(t, 220, .3) + Hits(t, 60, .5, .7, .08), 4);
        double now = played[^1].Time;
        var levels = new double[CompactMusicLayout.MaximumBars];
        flowing.Fill(levels, now);
        check("Every one of the most bars has a value while audio plays, the last one too", levels.All(level => level > 0));
        for (int n = 1; n <= 100; n++) flowing.Advance(now + n / 100d);
        check("The bars fall when the player stops sending audio", played.Max(h => h.Head) > .2 && flowing.Head < .05);
    }
}
