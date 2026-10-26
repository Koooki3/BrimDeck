using BrimDeck.Core;

// The motion upgrade's collapse textures (CollapseFx): the theme key, the glitch's smooth bands, the foam's field and masks, the
// genie, the interruption and the flash budget.
internal static class CollapseFxTests
{
    internal static void Run(Action<string, bool> check)
    {
        static PanelPalette Palette(string id) => PanelThemes.Resolve(PanelThemes.BuiltIn(id)!);
        static PanelPalette Custom(string json) => PanelThemes.Build("custom", PanelThemes.Parse(json));
        Keys(check, Palette, Custom);
        Glitch(check);
        Foam(check);
        Genie(check);
        Playback(check);
        Budget(check);
        Effects(check, Palette, Custom);
        Covered(check, Palette);
        Lengths(check, Custom);
    }

    // A theme's own lengths: read in milliseconds, held to their ranges, 0 for the built-in ones.
    private static void Lengths(Action<string, bool> check, Func<string, PanelPalette> custom)
    {
        var slow = custom("""{ "base": "dark", "motion": { "collapse": "glitch", "collapseMs": 480, "particleMs": 1000, "sliceMs": 120, "scanMs": 300 } }""").Motion;
        check("Lengths: collapseMs, particleMs, sliceMs and scanMs are read and inherited",
            slow is { CollapseMs: 480, ParticleMs: 1000, SliceMs: 120, ScanMs: 300 } && ThemeMotion.Default is { CollapseMs: 0, ParticleMs: 0, SliceMs: 0, ScanMs: 0 });
        var wild = custom("""{ "base": "dark", "motion": { "collapseMs": 5000, "particleMs": 10, "sliceMs": 999, "scanMs": -4 } }""").Motion;
        check("Lengths: held to 200-630, 300-2400, 50-160 and 100-400 ms (0 or less keeps the built-in)",
            wild is { CollapseMs: 630, ParticleMs: 300, SliceMs: 160, ScanMs: 0 });
        check("Lengths: splashMs is read and held to 300-800 ms",
            custom("""{ "base": "dark", "motion": { "press": "splash", "splashMs": 620 } }""").Motion.SplashMs == 620
            && custom("""{ "base": "dark", "motion": { "splashMs": 2000 } }""").Motion.SplashMs == 800 && ThemeMotion.Default.SplashMs == 0);
        check("Lengths: a theme's collapseMs replaces the style's length, whatever its pace",
            CollapseFx.Length(CollapseStyle.Glitch, .8, 480) == .48 && CollapseFx.Length(CollapseStyle.Glitch, .8) == .40 && CollapseFx.Length(CollapseStyle.Fade, 1, 480) == 0);
        check("Lengths: the theme template writes them out", PanelThemes.FillTemplate("t", custom("""{ "base": "dark", "motion": { "collapseMs": 480, "scanMs": 300 } }""")) is var t
            && t.Contains("\"collapseMs\": 480") && t.Contains("\"scanMs\": 300"));
        // lock's slice and scan line take the theme's lengths; the scan line keeps the press alive until it has passed.
        var timing = MotionTiming.For(.8, 3) with { Glitch = 120, Scan = 300 };
        var press = new PressTrack();
        press.Press(0, 0, glitch: true);
        bool slice = press.At(.11, timing, 3).GlitchOn && !press.At(.13, timing, 3).GlitchOn;
        press.Release(.2, inside: true, scan: true, timing, 3);
        check("Lengths: lock's slice holds sliceMs and its scan line passes over scanMs",
            slice && press.At(.2 + .29, timing, 3).ScanAt >= 0 && press.At(.2 + .31, timing, 3).ScanAt < 0 && press.Alive(.2 + .29, timing));
    }

    // One thing at a time: the ambient loop stops while a maximized or full-screen window is in front on the island's monitor.
    private static void Covered(Action<string, bool> check, Func<string, PanelPalette> palette)
    {
        var motion = palette("cyberpunk-edgerunners").Motion;
        var shown = new MotionConditions(true, true, false, true, false, false, false, false, true);
        check("Covered: the ambient light runs on the desktop and stops under a maximized or full-screen window; the answers to the pointer stay",
            motion.Ambient.Any && MotionGate.Ambient(shown, motion) && !MotionGate.Ambient(shown with { Covered = true }, motion)
            && MotionGate.Interaction(shown with { Covered = true }, motion) && !shown.Covered);
    }

    // The new answers to the pointer: splash, rain, bubbles and the chromatic pulse, all chosen by a theme file only.
    private static void Effects(Action<string, bool> check, Func<string, PanelPalette> palette, Func<string, PanelPalette> custom)
    {
        check("Splash, rain, bubble and the pulse: only the cyberpunk demo (rain) and the island (splash and bubbles) opt in; no built-in theme takes the pulse",
            PanelThemes.BuiltIns.Where(theme => theme.Id is not (PanelThemes.Demo or PanelThemes.IslandHoliday))
                .All(theme => palette(theme.Id).Motion is { Aberration: 0 } m && m.Press != PressFx.Splash && m.Particles is not (ParticleFx.Rain or ParticleFx.Bubble))
            && palette(PanelThemes.Demo).Motion is { Particles: ParticleFx.Rain, Aberration: 0 } && palette(PanelThemes.IslandHoliday).Motion is { Press: PressFx.Splash, Particles: ParticleFx.Bubble, Aberration: 0 });
        var island = custom("""{ "base": "island-holiday", "motion": { "press": "splash", "particles": "bubble", "trail": 0.4 } }""");
        var night = custom("""{ "base": "cyberpunk-edgerunners", "motion": { "particles": "RAIN", "aberration": 1.25 } }""");
        check("They are read by name from motion.press, motion.particles and motion.aberration",
            island.Motion is { Press: PressFx.Splash, Particles: ParticleFx.Bubble } && night.Motion is { Particles: ParticleFx.Rain, Aberration: 1.25 }
            && custom("""{ "base": "dark", "motion": { "press": "ripple" } }""").Motion.Press == PressFx.Shade);
        static double Pulse(Func<string, PanelPalette> custom, string value) => custom($$"""{ "base": "dark", "motion": { "aberration": {{value}} } }""").Motion.Aberration;
        check("The pulse is 0 or 1 to 1.5 DIP whatever the file says",
            Pulse(custom, "0") == 0 && Pulse(custom, "-2") == 0 && Pulse(custom, "0.4") == 1 && Pulse(custom, "1.2") == 1.2 && Pulse(custom, "9") == 1.5
            && custom("""{ "base": "dark" }""").Motion.Aberration == 0);
        check("System has no pulse", night.Motion.ForSystem().Aberration == 0);
        check("The theme template writes the pulse out", PanelThemes.FillTemplate("t", night).Contains("\"aberration\": 1.25"));
        check("The pulse lasts at most 120 ms and peaks at its amount",
            CollapseFx.AberrationAt(.06, 1.5, 2) is > 2.99 and <= 3 && CollapseFx.AberrationAt(.12, 1.5, 2) == 0 && CollapseFx.AberrationAt(-.01, 1.5, 2) == 0
            && CollapseFx.AberrationAt(.05, 0, 2) == 0);
    }

    private static void Keys(Action<string, bool> check, Func<string, PanelPalette> palette, Func<string, PanelPalette> custom)
    {
        check("Collapse: the default fade stays the default; only the cyberpunk demo (glitch) and the island (foam) opt in",
            ThemeMotion.Default.Collapse == CollapseStyle.Fade && PanelThemes.BuiltIns.All(theme => palette(theme.Id).Motion.Collapse
                == (theme.Id == PanelThemes.Demo ? CollapseStyle.Glitch : theme.Id == PanelThemes.IslandHoliday ? CollapseStyle.Foam : CollapseStyle.Fade)));
        check("motion.collapse is read by name, inherits, and an unknown name keeps the base's",
            custom("""{ "base": "dark", "motion": { "collapse": "glitch" } }""").Motion.Collapse == CollapseStyle.Glitch
            && custom("""{ "base": "dark", "motion": { "collapse": "Genie" } }""").Motion.Collapse == CollapseStyle.Genie
            && custom("""{ "base": "dark", "motion": { "collapse": "thanos" } }""").Motion.Collapse == CollapseStyle.Fade
            && custom("""{ "base": "dark", "motion": { "collapse": "3" } }""").Motion.Collapse == CollapseStyle.Fade);
        check("System never plays a collapse picture", custom("""{ "base": "dark", "motion": { "collapse": "foam" } }""").Motion.ForSystem().Collapse == CollapseStyle.Fade);
        check("The theme template writes the collapse out", PanelThemes.FillTemplate("t", custom("""{ "base": "dark", "motion": { "collapse": "foam" } }""")).Contains("\"collapse\": \"foam\""));
        check("Lengths: the closing takes glitch 400-450 ms, foam 460-520 ms, genie 420-480 ms, whatever the pace",
            Math.Abs(CollapseFx.Length(CollapseStyle.Glitch, 1) - .42) < 1e-9 && Math.Abs(CollapseFx.Length(CollapseStyle.Foam, 1) - .48) < 1e-9
            && Math.Abs(CollapseFx.Length(CollapseStyle.Genie, 1) - .45) < 1e-9
            && new[] { .6, 1.6, double.NaN }.All(p => CollapseFx.Length(CollapseStyle.Glitch, p) is >= .40 and <= .45
                && CollapseFx.Length(CollapseStyle.Foam, p) is >= .46 and <= .52 && CollapseFx.Length(CollapseStyle.Genie, p) is >= .42 and <= .48)
            && CollapseFx.Length(CollapseStyle.Fade, 1) == 0);
        check("The texture plays over the start of the closing while the outline morphs on its own track",
            CollapseFx.Track(CollapseStyle.Glitch) == (0, .75) && CollapseFx.Track(CollapseStyle.Foam) == (0, .75) && CollapseFx.Track(CollapseStyle.Genie) == (0, .85));
    }

    private static void Glitch(Action<string, bool> check)
    {
        // The original's hash, written out again from windows-animations' RenderGlitch.
        static double Reference(int x, int y)
        {
            uint h = unchecked((uint)(x * 374761393) ^ (uint)(y * 668265263));
            h = unchecked((h ^ (h >> 13)) * 1274126177u);
            return (h & 0xFFFFu) / 65535.0;
        }
        bool same = true;
        for (int x = -50; x < 50; x += 7) for (int y = -30; y < 300; y += 11) same &= CollapseFx.GlitchHash(x, y) == Reference(x, y);
        check("Glitch: the hash is the original's to the bit", same);
        check("Glitch: 24 to 32 bands, one per four DIP", CollapseFx.GlitchBands(32) == 24 && CollapseFx.GlitchBands(112) == 28 && CollapseFx.GlitchBands(400) == 32);
        int active = Enumerable.Range(0, 200).Count(band => CollapseFx.GlitchHash(band, 0) >= GlitchTexture.Threshold);
        check($"Glitch: about 45% of the bands tear ({active / 2.0:0}%)", active is > 70 and < 110);

        // Smooth: from one 60 Hz frame of a 315 ms texture to the next no band moves more than a small share of its range, and
        // it starts and ends in place.
        const double frame = 1 / (60 * .315);
        double limit = (GlitchTexture.Tear + GlitchTexture.Jitter) / 2, worst = 0; bool within = true;
        for (int band = 0; band < 32; band++)
            for (double p = 0; p < 1; p += frame)
            {
                double a = GlitchTexture.Offset(band, p), b = GlitchTexture.Offset(band, Math.Min(1, p + frame));
                within &= Math.Abs(a) <= limit + 1e-9;
                worst = Math.Max(worst, Math.Abs(b - a));
            }
        check($"Glitch: bands slide smoothly within ±{limit:P2} of the width, never jumping (largest step a frame {worst:P2})",
            within && worst < limit * .4 && Enumerable.Range(0, 32).All(band => GlitchTexture.Offset(band, 0) == 0 && Math.Abs(GlitchTexture.Offset(band, 1)) < 1e-12));
        check("Glitch: bands drop out one by one between 40 and 100 percent of the way, all there at first and gone at the end",
            Enumerable.Range(0, 32).All(band => GlitchTexture.Alpha(band, .39) == 1 && GlitchTexture.Alpha(band, 1) == 0)
            && Enumerable.Range(0, 32).Select(band => GlitchTexture.Alpha(band, .7)).Distinct().Count() > 8);
        check("Glitch: the jolt doubles the tears for a tenth of the way, from 30 percent, and only when it plays",
            GlitchTexture.JoltOn(.35) && !GlitchTexture.JoltOn(.25) && !GlitchTexture.JoltOn(.45)
            && Enumerable.Range(0, 32).All(band => Math.Abs(GlitchTexture.Offset(band, .35, jolt: true)) <= 2 * limit + 1e-9)
            && Enumerable.Range(0, 32).Any(band => GlitchTexture.Offset(band, .35, jolt: true) != GlitchTexture.Offset(band, .35)));
    }

    private static void Foam(Action<string, bool> check)
    {
        check("Foam: the hash is windows-animations' pseudo_hash", FoamField.Hash(3, 7) == ((3u * 73856093u) ^ (7u * 19349663u)) % 10000 / 10000.0);
        var panel = new FoamField(760, 300);
        check($"Foam: a panel's field has at most about 16 000 blocks ({panel.Columns * panel.Rows}, {panel.Block} DIP each)", panel.Columns * panel.Rows <= 16500 && panel.Block >= 3);
        var field = new FoamField(340, 92);
        int n = field.Columns * field.Rows;
        float first = float.MaxValue, last = 0;
        for (int y = 0; y < field.Rows; y++) for (int x = 0; x < field.Columns; x++) { first = Math.Min(first, field.StartOf(x, y)); last = Math.Max(last, field.StartOf(x, y)); }
        check("Foam: the field is spread over the whole move (IS-01: no corner lingers)", first == 0 && Math.Abs(last - 1) < 1e-6);
        var shown = new byte[n]; var foam = new byte[n];
        field.Mask(0, shown, foam);
        bool whole = shown.All(a => a == 255) && foam.All(f => f == 0);
        field.Mask(FoamField.Levels - 1, shown, foam);
        bool gone = shown.All(a => a == 0) && foam.All(f => f == 0);
        check("Foam: the first level shows everything and no foam, the last nothing", whole && gone);
        // Steady: about the same share goes at every step, and what shows is whole or gone, not a wide half-faded band (IS-02).
        int before = n; bool steady = true, crisp = true, foamed = false;
        for (int level = 1; level < FoamField.Levels; level++)
        {
            field.Mask(level, shown, foam);
            int left = shown.Count(a => a > 127), half = shown.Count(a => a is > 12 and < 243);
            steady &= left <= before && before - left <= n * .12;
            crisp &= half <= n * .12;
            foamed |= foam.Any(f => f > 200);
            before = left;
        }
        check("Foam: it dissolves steadily level by level, with a soft edge and a line of foam, never a wide half-faded band", steady && crisp && foamed);
        check("Foam: a frame picks its level by progress", FoamField.LevelAt(0) == 0 && FoamField.LevelAt(1) == FoamField.Levels - 1 && FoamField.LevelAt(.5) is > 15 and < 25);
    }

    private static void Genie(Action<string, bool> check)
    {
        int n = GenieCollapse.Tiles + 1;
        var points = new (double X, double Y)[n * n];
        // A notice hanging from a notch: the island above, at its top.
        var window = new GenieRect(100, 0, 320, 90);
        var island = new GenieRect(190, 0, 140, 32);
        var start = GenieCollapse.Mesh(0, window, island, points);
        bool grid = true;
        for (int gy = 0; gy < n; gy++) for (int gx = 0; gx < n; gx++)
            grid &= Math.Abs(points[gy * n + gx].X - (window.X + window.Width * gx / GenieCollapse.Tiles)) < 1e-6 && Math.Abs(points[gy * n + gx].Y - (window.Y + window.Height * gy / GenieCollapse.Tiles)) < 1e-6;
        check("Genie: at the start the 12 x 12 mesh is the snapshot itself", grid && Math.Abs(start.Width - 320) < 1e-6 && Math.Abs(start.Height - 90) < 1e-6);
        GenieCollapse.Mesh(.999, window, island, points);
        check("Genie: at the end it has gone into the island, across the island's width at its edge",
            points.All(p => Math.Abs(p.Y - island.Bottom) < 1 && p.X >= island.X - 1 && p.X <= island.Right + 1));
        check("Genie: once flat it draws nothing", GenieCollapse.Mesh(1, window, island, points).Width == 0);
        // Mirrored for an island below the snapshot, as the original's bottom taskbar.
        var below = new GenieRect(190, 200, 140, 32);
        GenieCollapse.Mesh(.999, window, below, points);
        check("Genie: an island below draws it down into the island's top edge", points.All(p => Math.Abs(p.Y - below.Y) < 1 && p.X >= below.X - 1 && p.X <= below.Right + 1));
        // The lamp curve itself, on the original's own numbers: a window above a 22 px dock.
        var w = new GenieRect(0, 0, 800, 600); var dock = new GenieRect(400, 1000, 22, 33);
        var mid = GenieCollapse.LampVertex(.5, 1, .3, w, dock);
        check("Genie: the lamp curve's neck reaches the dock by the end of its first stage (p = 0.3)", Math.Abs(mid.Y - 1000) < 1e-6);
        // The mesh follows smoothstep: a quarter of the way in time is under a fifth of the way along the curve.
        var at = GenieCollapse.Mesh(.25, window, island, points);
        check("Genie: progress eases in by smoothstep", at.Height > 90 * .5);
    }

    private static void Playback(Action<string, bool> check)
    {
        var playback = new CollapsePlayback();
        playback.Start(CollapseStyle.Genie, 10, .45);
        bool playing = playback.Active && Math.Abs(playback.Progress(10.225) - .5) < 1e-9 && !playback.Done(10.4);
        // A new notice: stopped at once, nothing more drawn, whatever the time.
        playback.Stop();
        check("Playback: a new notice stops the picture at once; nothing of it plays after that",
            playing && !playback.Active && playback.Progress(10.3) == -1 && playback.Done(10.3) && playback.Style == CollapseStyle.Fade);
        playback.Start(CollapseStyle.Fade, 0, 1);
        check("Playback: the default fade is no picture", !playback.Active);
        playback.Start(CollapseStyle.Glitch, 0, .27);
        check("Playback: a picture ends after its length", playback.Done(.27) && !playback.Done(.2));
    }

    private static void Budget(Action<string, bool> check)
    {
        var on = new MotionConditions(true, true, false, true, false, false, false, false, true);
        var off = on with { SystemAnimations = false };
        check("Budget: a texture plays while motion is allowed; with Windows' animations off it does not",
            CollapseFx.Plays(CollapseStyle.Foam, on) && !CollapseFx.Plays(CollapseStyle.Foam, off) && !CollapseFx.Plays(CollapseStyle.Fade, on));
        var shared = new FlashBudget();
        var jolts = new[] { 0, .2, .4, 1.25 }.Select(t => CollapseFx.Jolt(CollapseStyle.Glitch, shared, t)).ToArray();
        check($"Budget: only the glitch's jolt is a flash; a third within a second plays without it ({string.Join(",", jolts)})",
            jolts.SequenceEqual([true, true, false, true]) && !CollapseFx.Jolt(CollapseStyle.Foam, new FlashBudget(), 0) && !CollapseFx.Jolt(CollapseStyle.Genie, new FlashBudget(), 0));
        // A window's presses spend the same budget: two glitch slices leave no jolt in that second.
        shared.Clear();
        var presses = new FlashBudget(shared);
        bool pressed = presses.TryTake(0) && presses.TryTake(.1);
        check("Budget: presses and the jolt share one budget (two flashes a second in all)",
            pressed && !CollapseFx.Jolt(CollapseStyle.Glitch, shared, .3) && CollapseFx.Jolt(CollapseStyle.Glitch, shared, 1.05) && presses.TryTake(1.15) && !presses.TryTake(1.2));
        presses.Clear();
        check("Budget: clearing a window's budget leaves the shared one spent", !shared.Available(1.2) && !presses.Available(1.2));
        check("Budget: the app has one shared budget", ReferenceEquals(FlashBudget.Shared, FlashBudget.Shared));
    }
}
