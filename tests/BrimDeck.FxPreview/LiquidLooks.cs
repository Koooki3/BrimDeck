using System.Globalization;
using BrimDeck.Core;

// Automatic liquid glass's decision between its looks over video-like backdrops, offscreen and without a GPU: each sequence is
// a minute of frames (96 x 24 texels, as a small mip of the backdrop under the island, 60 a second) drawn here, measured as
// the renderer measures it (a 1024-bin luma histogram: its mean for the round-11 decision, the mean of its middle for this
// one), and the share of texels changed since the frame before standing for the capture's comparison (DesktopSource.
// ChangedShare). Both decisions are run over every sequence, in both Windows app modes, and their turns counted; the white
// page also reports how soon it turns a dark-mode island light (legibility).
//   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --liquid-looks
internal static class LiquidLooks
{
    private const int Width = 96, Height = 24, Bins = 1024, FrameMs = 16, Seconds = 60;

    // A frame's texels (luma 0..1) at time t (ms), drawn by a sequence.
    private delegate void Draw(double[] texels, long t);

    public static int Run()
    {
        var sequences = new (string Name, Draw Draw)[]
        {
            ("flashing video (cuts between dark and bright scenes, white flashes)", FlashingVideo(7)),
            ("slowly brightening page (dark to white over 40 s)", BrighteningPage),
            ("mostly dark video with bright flashes and subtitles", DarkVideo(11)),
            ("white page opened over a dark desktop at 3 s", WhitePage),
            ("dark page scrolled into a white one (6 to 10 s), then still", ScrolledPage),
        };
        Console.WriteLine("sequence | app mode | turns before (round 11) | turns now | first turn now");
        bool ok = true;
        foreach (var (name, draw) in sequences)
            foreach (bool appDark in new[] { true, false })
            {
                var (before, now, first) = Measure(draw, appDark);
                Console.WriteLine(FormattableString.Invariant($"{name} | {(appDark ? "dark" : "light")} | {before} | {now} | {(first < 0 ? "-" : $"{first} ms")}"));
                if (name.StartsWith("white page", StringComparison.Ordinal) && appDark) ok &= first is > 3000 and <= 4000;
            }
        Console.WriteLine(ok ? "PASS: a white page turns a dark-mode island light within a second" : "FAIL: a white page did not turn a dark-mode island light within a second");
        return ok ? 0 : 1;
    }

    // The turns of the round-11 decision and of this one over a sequence, and when this one first turned (-1: never).
    private static (int Before, int Now, long First) Measure(Draw draw, bool appDark)
    {
        var texels = new double[Width * Height];
        var previous = new byte[texels.Length];
        var histogram = new int[Bins];
        OldAppearance? old = null;
        GlassAppearance? look = null;
        int before = 0, now = 0;
        long first = -1;
        for (long t = 0; t < Seconds * 1000; t += FrameMs)
        {
            draw(texels, t);
            Array.Clear(histogram);
            double sum = 0;
            int changed = 0;
            for (int i = 0; i < texels.Length; i++)
            {
                byte value = (byte)Math.Clamp(Math.Round(texels[i] * 255), 0, 255);
                if (value != previous[i]) changed++;
                previous[i] = value;
                double l = value / 255.0;
                sum += l;
                histogram[Math.Min(Bins - 1, (int)(l * Bins))]++;
            }
            double mean = sum / texels.Length, middle = GlassAppearance.RangeMean(histogram);
            if (old is null) { old = new OldAppearance(OldAppearance.FirstLook(mean)); old.Reset(old.Dark, t); }
            else if (old.Feed(mean, t)) before++;
            if (look is null) { bool dark = GlassAppearance.FirstLook(middle, appDark); look = new GlassAppearance(dark, appDark); look.Reset(dark, t); look.Feed(middle, t); continue; }
            if (t > 0) look.Changed(changed / (double)texels.Length, t);
            if (look.Feed(middle, t)) { now++; if (first < 0) first = t; }
        }
        return (before, now, first);
    }

    // ----- The sequences -----

    // A texture that moves every frame (a video's picture): a base brightness with a moving pattern and grain.
    private static void Picture(double[] texels, long t, double luma, Random grain, double amount = .06)
    {
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                texels[y * Width + x] = luma + amount * Math.Sin(x * .31 + y * .17 + t * .004) + (grain.NextDouble() - .5) * .03;
    }

    // Scenes cut every .4 to 3 s, each dark (.05 to .25) or bright (.7 to .95) at random, with a white flash of 2 to 4 frames
    // about every 3 s.
    private static Draw FlashingVideo(int seed)
    {
        var random = new Random(seed);
        var scenes = new List<(long End, double Luma)>();
        for (long end = 0; end < Seconds * 1000;) { end += random.Next(400, 3000); scenes.Add((end, random.NextDouble() < .5 ? .05 + .2 * random.NextDouble() : .7 + .25 * random.NextDouble())); }
        var flashes = new List<(long From, long To)>();
        for (long at = 0; at < Seconds * 1000;) { at += random.Next(1500, 4500); flashes.Add((at, at + FrameMs * random.Next(2, 5))); }
        var grain = new Random(seed + 1);
        return (texels, t) =>
        {
            double luma = scenes.First(scene => scene.End > t).Luma;
            if (flashes.Any(flash => t >= flash.From && t < flash.To)) luma = .97;
            Picture(texels, t, luma, grain);
        };
    }

    // A page (text on its background: 15 % of the texels dark ink) whose background brightens from .08 to .95 over 40 s, then
    // holds: every texel changes now and then as it brightens.
    private static void BrighteningPage(double[] texels, long t)
    {
        double background = .08 + .87 * Math.Clamp(t / 40_000.0, 0, 1);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                texels[y * Width + x] = (x * 7 + y * 13) % 20 < 3 ? .05 : background;
    }

    // A mostly dark video (.08 to .2, drifting) with flashes of 3 to 20 frames near white every 1.5 to 5 s, and a bright
    // subtitle line (12 % of the texels) shown half the time.
    private static Draw DarkVideo(int seed)
    {
        var random = new Random(seed);
        var flashes = new List<(long From, long To)>();
        for (long at = 0; at < Seconds * 1000;) { at += random.Next(1500, 5000); flashes.Add((at, at + FrameMs * random.Next(3, 21))); }
        var grain = new Random(seed + 1);
        return (texels, t) =>
        {
            double luma = .14 + .06 * Math.Sin(t / 2300.0);
            if (flashes.Any(flash => t >= flash.From && t < flash.To)) luma = .9;
            Picture(texels, t, luma, grain);
            if (t / 4000 % 2 == 1)
                for (int y = Height - 4; y < Height - 1; y++)
                    for (int x = 10; x < Width - 10; x++) texels[y * Width + x] = .95;
        };
    }

    // A dark desktop (.1, still) and, from 3 s, a white page (.96, 10 % dark text) over it, still.
    private static void WhitePage(double[] texels, long t)
    {
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                texels[y * Width + x] = t < 3000 ? .1 : (x * 7 + y * 3) % 10 == 0 ? .1 : .96;
    }

    // A dark page scrolled (every texel moving, 4 s) into a white one from 6 s, then still.
    private static void ScrolledPage(double[] texels, long t)
    {
        double offset = t < 6000 ? 0 : t < 10_000 ? (t - 6000) / 4000.0 * 400 : 400;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                double row = y + offset;
                bool ink = ((int)row * 5 + x * 3) % 17 < 2;
                double page = row < 200 ? .12 : .95;
                texels[y * Width + x] = ink ? (page > .5 ? .1 : .8) : page;
            }
    }

    // The round-11 decision (GlassAppearance before this round), kept here for the comparison: hysteresis .40 / .60 on the mean
    // brightness, a confirmation of 150 ms, a hold of 1 s doubling to 8 s when restless, a big jump turning on two measures.
    private sealed class OldAppearance(bool dark)
    {
        private const double DarkBelow = .40, LightAbove = .60, BigJump = .45;
        private const long Confirm = 150, Hold = 1000, MaxHold = 8000, Restless = 4000;
        private const int JumpMeasures = 2;
        public static bool FirstLook(double luma) => luma < (DarkBelow + LightAbove) / 2;
        public bool Dark { get; private set; } = dark;
        private long _holdFor = Hold, _changedAt = long.MinValue / 2, _turnedAt = long.MinValue / 2, _pendingSince = -1;
        private double _fed = double.NaN;
        private bool _jumped;
        private int _fresh;

        public bool Feed(double luma, long now)
        {
            bool jump = double.IsFinite(_fed) && Math.Abs(luma - _fed) > BigJump;
            _fed = luma;
            bool wants = Dark ? luma <= LightAbove : luma < DarkBelow;
            if (wants == Dark) { _pendingSince = -1; _jumped = false; _fresh = 0; return false; }
            if (_pendingSince < 0) { _pendingSince = now; _jumped = jump; _fresh = 1; }
            else { _jumped |= jump; _fresh++; }
            bool confirmed = now - _pendingSince >= Confirm || _jumped && _fresh >= JumpMeasures;
            if (!confirmed || now - _changedAt < _holdFor) return false;
            _holdFor = now - _turnedAt < _holdFor + Restless ? Math.Min(_holdFor * 2, MaxHold) : Hold;
            Dark = wants; _changedAt = _turnedAt = now; _pendingSince = -1; _jumped = false; _fresh = 0;
            return true;
        }

        public void Reset(bool dark, long now) { Dark = dark; _changedAt = now; _pendingSince = -1; _jumped = false; _fresh = 0; }
    }
}
