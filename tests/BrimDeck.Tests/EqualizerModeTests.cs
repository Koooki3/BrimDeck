using System.Reflection;
using System.Text.Json;
using BrimDeck.Core;

// The equalizer's two ways of showing the sound (EqualizerMode): Flow must be what it always was and cost what it always cost,
// Spectrum is the choice that has to be asked for, and the way can be changed while the sound plays.
internal static class EqualizerModeTests
{
    private const int Rate = 44100;

    private static float Sine(double t, double hz, double amplitude) => (float)(amplitude * Math.Sin(2 * Math.PI * hz * t));
    // A decaying tone started every period seconds, like a drum hit, swelling over a few ms as a struck drum does.
    private static float Hit(double t, double hz, double period, double amplitude, double decay) { double since = t % period; return (float)(amplitude * (1 - Math.Exp(-since / .004)) * Math.Exp(-since / decay) * Math.Sin(2 * Math.PI * hz * since)); }
    // A floor of noise that depends only on the time, so every signal hears the same.
    private static double Floor(double t) { double x = Math.Sin(t * 12345.6789) * 43758.5453; return (x - Math.Floor(x) - .5) * .1; }
    // Notes, drums and noise: something in most of the bars.
    private static float Music(double t) => (float)(Sine(t, 220, .25) + Sine(t, 1800, .12 * (1 + Math.Sin(t * 5)) / 2) + Hit(t, 60, .5, .5, .08) + Hit(t + .25, 6500, .5, .1, .03) + Floor(t));

    // Feeds 10 ms blocks of stereo audio to push and calls frame after every third block (30 analyses a second, as the capture
    // does); returns the time the run ended.
    private static double Drive(Func<double, float> wave, double start, double seconds, Action<float[], double> push, Action<double> frame)
    {
        var block = new float[441 * 2]; int blocks = (int)Math.Round(seconds * 100);
        for (int n = 0; n < blocks; n++)
        {
            double t0 = start + n / 100d;
            for (int i = 0; i < 441; i++) { float v = wave(t0 + i / (double)Rate); block[i * 2] = v; block[i * 2 + 1] = v; }
            push(block, t0 + .01);
            if (n % 3 == 2) frame(t0 + .01);
        }
        return start + blocks / 100d;
    }

    private static object? Private(EqualizerSignal signal, string name) =>
        typeof(EqualizerSignal).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(signal) ?? throw new InvalidOperationException("no field " + name);
    private static bool Unmade(EqualizerSignal signal, string name) => typeof(EqualizerSignal).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(signal) is null;

    public static void Run(string root, Action<string, bool> check)
    {
        check("Equalizer mode: Flow is the default and 0, Spectrum is 1, so a saved number keeps its meaning", (int)EqualizerMode.Flow == 0 && (int)EqualizerMode.Spectrum == 1 && new DeckSettings().EqualizerMode == EqualizerMode.Flow);

        // Settings: saved as a number, a file without it (an older one) or with an unknown number is Flow.
        var store = new SettingsStore(Path.Combine(root, "equalizer-mode"), "0.1.0");
        var loaded = store.Load();
        bool first = loaded.EqualizerMode == EqualizerMode.Flow;
        loaded.EqualizerMode = EqualizerMode.Spectrum; store.Save(loaded);
        using (var doc = JsonDocument.Parse(File.ReadAllText(store.FilePath)))
            first &= doc.RootElement.GetProperty("EqualizerMode").ValueKind == JsonValueKind.Number && doc.RootElement.GetProperty("EqualizerMode").GetInt32() == 1;
        first &= new SettingsStore(store.DirectoryPath, "0.1.0").Load().EqualizerMode == EqualizerMode.Spectrum;
        loaded.EqualizerMode = EqualizerMode.Flow; store.Save(loaded);
        first &= new SettingsStore(store.DirectoryPath, "0.1.0").Load().EqualizerMode == EqualizerMode.Flow;
        check("Equalizer mode: a first start is Flow, and Spectrum and Flow are saved as numbers and read back", first);
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","Width":900,"EqualizerBars":7}""");
        var older = new SettingsStore(store.DirectoryPath, "0.1.0").Load();
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","EqualizerMode":1,"MusicEqualizerBars":9}""");
        var chosen = new SettingsStore(store.DirectoryPath, "0.1.0").Load();
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","EqualizerMode":7}""");
        var unknown = new SettingsStore(store.DirectoryPath, "0.1.0").Load();
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.0.9","Width":900}""");
        var oldVersion = new SettingsStore(store.DirectoryPath, "0.1.0").Load();
        check("Equalizer mode: a file without it, and one with a number that is not a mode, read as Flow; a saved Spectrum is kept",
            older.EqualizerMode == EqualizerMode.Flow && older.EqualizerBars == 7 && unknown.EqualizerMode == EqualizerMode.Flow && oldVersion.EqualizerMode == EqualizerMode.Flow
            && chosen.EqualizerMode == EqualizerMode.Spectrum && chosen.MusicEqualizerBars == 9 && new DeckSettings { EqualizerMode = (EqualizerMode)(-3) }.EqualizerMode == EqualizerMode.Flow);

        // Flow is the analysis upstream had before the Spectrum: the same value after every analysis and the same bars at any moment,
        // to the last bit, whatever the sound and whatever the sample rate the analysis is told about.
        (string Name, (Func<double, float> Wave, double Seconds)[] Parts)[] scenes =
        [
            ("music", [(Music, 5)]),
            ("drums, a tone, silence, dither, music again", [(t => Hit(t, 60, .5, .8, .08) + Sine(t, 220, .3), 3), (_ => 0f, 2), (t => (float)(((int)(Math.Abs(Math.Sin(t * 977)) * 3) - 1) / 32768d), 2), (Music, 2)]),
            ("noise", [(t => (float)(Floor(t * 7.3) * 6), 3)]),
            ("a steady tone, then a sweep", [(t => Sine(t, 1000, .5), 2), (t => Sine(t, 100 * Math.Exp(t), .5), 4)])
        ];
        int frames = 0, differing = 0, unequalLevels = 0;
        foreach (int rate in new[] { 44100, 48000 })
        {
            foreach (var scene in scenes)
            {
                var current = new EqualizerSignal(rate); var legacy = new LegacyFlowSignal(rate);
                var now20 = new double[20]; var then20 = new double[20]; var mid20 = new double[20]; var midLegacy = new double[20];
                double clock = 0;
                foreach (var part in scene.Parts)
                {
                    clock = Drive(part.Wave, clock, part.Seconds, (block, t) => { current.Push(block, 2, t); legacy.Push(block, 2, t); }, t =>
                    {
                        current.Advance(t); legacy.Advance(t); frames++;
                        if (current.Head != legacy.Head) differing++;
                        // The bars at the analysis, and between it and the next, for the most bars and for fewer.
                        current.Fill(now20, t); legacy.Fill(then20, t);
                        current.Fill(mid20, t + .017, EqualizerMode.Flow); legacy.Fill(midLegacy, t + .017);
                        if (!now20.AsSpan().SequenceEqual(then20) || !mid20.AsSpan().SequenceEqual(midLegacy)) unequalLevels++;
                        foreach (int count in new[] { 3, 5, 12 })
                        {
                            var few = new double[count]; current.Fill(few, t + .005);
                            legacy.Fill(then20, t + .005);
                            if (!few.AsSpan().SequenceEqual(then20.AsSpan(0, count))) unequalLevels++;
                        }
                    });
                }
            }
        }
        check($"Equalizer mode: Flow gives, over {frames} analyses of 4 kinds of sound at 2 sample rates, exactly the values and bars of the analysis before the Spectrum existed", frames > 1500 && differing == 0 && unequalLevels == 0);

        // The default of Fill is Flow too.
        var byDefault = new EqualizerSignal(Rate); var explicitFlow = new EqualizerSignal(Rate);
        var a = new double[5]; var b = new double[5]; bool same = true;
        Drive(Music, 0, 2, (block, t) => { byDefault.Push(block, 2, t); explicitFlow.Push(block, 2, t); }, t =>
        {
            byDefault.Advance(t); explicitFlow.Advance(t); byDefault.Fill(a, t); explicitFlow.Fill(b, t, EqualizerMode.Flow); same &= a.AsSpan().SequenceEqual(b) && a.Max() > 0;
        });
        check("Equalizer mode: asking for the bars without naming a way is asking for Flow", same);

        // Flow does none of the Spectrum's work and takes none of its memory; the Spectrum does none of Flow's.
        var onlyFlow = new EqualizerSignal(Rate); var flowBars = new double[5];
        double flowEnd = Drive(Music, 0, 3, (block, t) => onlyFlow.Push(block, 2, t), t => { onlyFlow.Advance(t); onlyFlow.Fill(flowBars, t); });
        check("Equalizer mode: playing as Flow makes no narrow bands or histories, no 4096-point FFT, and never starts the Spectrum",
            flowBars.Max() > .1 && Unmade(onlyFlow, "_weights") && (int)Private(onlyFlow, "_frameCount")! == 0 && ((double[])Private(onlyFlow, "_real")!).Length == 2048
            && (int)Private(onlyFlow, "_bars")! == 0 && (int)Private(onlyFlow, "_publishedBars")! == 0 && (int)Private(onlyFlow, "_running")! == (int)EqualizerMode.Flow);
        var onlySpectrum = new EqualizerSignal(Rate); var spectrumBars = new double[5];
        onlySpectrum.Fill(spectrumBars, 0, EqualizerMode.Spectrum);
        double spectrumEnd = Drive(Music, 0, 3, (block, t) => onlySpectrum.Push(block, 2, t), t => { onlySpectrum.Advance(t); onlySpectrum.Fill(spectrumBars, t, EqualizerMode.Spectrum); });
        check("Equalizer mode: playing as Spectrum from the start never makes the flow's value or its history",
            spectrumBars.Max() > .1 && onlySpectrum.Head == 0 && onlySpectrum.Sample(spectrumEnd) == 0 && ((System.Collections.ICollection)Private(onlySpectrum, "_history")!).Count == 0
            && ((Queue<(double, double)>[])Private(onlySpectrum, "_flowRanges")!).All(q => q.Count == 0) && (int)Private(onlySpectrum, "_frameCount")! > 0 && (int)Private(onlySpectrum, "_running")! == (int)EqualizerMode.Spectrum);

        // The way changes while the sound plays, with the bars asked for at the same moment as before. The first look at the new way
        // is the bars as they stood; the analyses after it carry on from them and do not go to rest or jump.
        foreach (int bars in new[] { 3, 5, 20 })
        {
            var signal = new EqualizerSignal(Rate); var levels = new double[bars];
            var mode = EqualizerMode.Flow; double clock = 0, frameTime = 0, previousMean = 0, biggestStep = 0, biggestAtSwitch = 0, lowest = 1;
            bool looksAlike = true, carriesOn = true; int analyses = 0;
            void Phase(double seconds, bool counted) => clock = Drive(Music, clock, seconds, (block, t) => signal.Push(block, 2, t), t =>
            {
                signal.Advance(t); signal.Fill(levels, t, mode); analyses++; frameTime = t;
                double mean = levels.Average();
                if (counted) { biggestStep = Math.Max(biggestStep, Math.Abs(mean - previousMean)); lowest = Math.Min(lowest, levels.Max()); }
                previousMean = mean;
            });
            Phase(2.5, false);
            var shown = (double[])levels.Clone();
            for (int round = 0; round < 8; round++)
            {
                var next = mode == EqualizerMode.Flow ? EqualizerMode.Spectrum : EqualizerMode.Flow;
                // Before the new way has made an analysis, asking for it shows the bars of the way that was shown.
                var look = new double[bars]; signal.Fill(look, frameTime, next);
                looksAlike &= look.AsSpan().SequenceEqual(shown) || look.Zip(shown).All(pair => Math.Abs(pair.First - pair.Second) < 1e-9);
                mode = next;
                double beforeSwitch = previousMean;
                Phase(.1, true);
                biggestAtSwitch = Math.Max(biggestAtSwitch, Math.Abs(levels.Average() - beforeSwitch));
                Phase(.7, true);
                shown = (double[])levels.Clone();
                carriesOn &= levels.Max() > .1;
            }
            check($"Equalizer mode: switching between Flow and Spectrum 8 times while music plays, with {bars} bars: the first look is the bars as they stood and no bar level jumps or rests",
                looksAlike && carriesOn && analyses > 200 && lowest > .05 && biggestStep < .25 && biggestAtSwitch < .35);
        }
    }
}
