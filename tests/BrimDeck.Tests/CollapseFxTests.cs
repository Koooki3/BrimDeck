using BrimDeck.Core;

// The motion upgrade's collapse pictures (CollapseFx): the theme key, the ported glitch, foam and genie, the interruption and
// the flash budget.
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
        check("Lengths: held to 200-630, 300-1600, 50-160 and 100-400 ms (0 or less keeps the built-in)",
            wild is { CollapseMs: 630, ParticleMs: 300, SliceMs: 160, ScanMs: 0 });
        check("Lengths: a theme's collapseMs replaces the style's length, whatever its pace",
            CollapseFx.Length(CollapseStyle.Glitch, .8, 480) == .48 && CollapseFx.Length(CollapseStyle.Glitch, .8) == .24 && CollapseFx.Length(CollapseStyle.Fade, 1, 480) == 0);
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
        // Red pulled right and blue left, nothing at the centre: a white bar's ends turn red on the right, blue on the left.
        var row = new uint[21]; for (int x = 5; x <= 15; x++) row[x] = 0xFFFFFFFF;
        CollapseFx.Aberrate(row, new uint[21], 3);
        check("Aberration: red moves right and blue left at the edges, the centre stays",
            row[10] == 0xFFFFFFFF && (row[16] & 0xFF0000) != 0 && (row[16] & 0xFF) == 0 && (row[4] & 0xFF) != 0 && (row[4] & 0xFF0000) == 0 && (row[16] & 0xFF00) == 0);
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
        check("Lengths: glitch 240-300 ms, foam 300-360 ms, genie about 450 ms, whatever the pace",
            Math.Abs(CollapseFx.Length(CollapseStyle.Glitch, 1) - .27) < 1e-9 && Math.Abs(CollapseFx.Length(CollapseStyle.Foam, 1) - .33) < 1e-9
            && Math.Abs(CollapseFx.Length(CollapseStyle.Genie, 1) - .45) < 1e-9
            && new[] { .6, 1.6, double.NaN }.All(p => CollapseFx.Length(CollapseStyle.Glitch, p) is >= .24 and <= .30
                && CollapseFx.Length(CollapseStyle.Foam, p) is >= .30 and <= .36 && CollapseFx.Length(CollapseStyle.Genie, p) is >= .42 and <= .48)
            && CollapseFx.Length(CollapseStyle.Fade, 1) == 0);
    }

    // A test picture: opaque, a different color per row and column, with a transparent margin of two pixels.
    private static uint[] Picture(int w, int h)
    {
        var pixels = new uint[w * h];
        for (int y = 2; y < h - 2; y++)
            for (int x = 2; x < w - 2; x++) pixels[y * w + x] = 0xFF000000u | (uint)(x * 3 & 0xFF) << 16 | (uint)(y * 5 & 0xFF) << 8 | 0x40;
        return pixels;
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
        check("Glitch: alpha (1 - ss(0,1,p))(1 - ss(.55,1,p)) after smoothstep, whole at the start and gone at the end",
            CollapseFx.GlitchAlpha(0) == 1 && CollapseFx.GlitchAlpha(1) == 0 && Math.Abs(CollapseFx.GlitchAlpha(.5) - .5) < 1e-9 && CollapseFx.GlitchAlpha(.75) < .2);

        int w = 300, h = 120;
        var glitch = new GlitchCollapse(Picture(w, h), w, h, CollapseFx.GlitchBands(60));
        int limit = (int)Math.Ceiling((GlitchCollapse.Tear + GlitchCollapse.Jitter) / 2 * w);
        bool within = true; var patterns = new HashSet<string>();
        for (double p = 0; p <= 1.0001; p += .01)
        {
            var shifts = Enumerable.Range(0, h).Select(y => glitch.Shift(y, p)).ToArray();
            within &= shifts.All(s => Math.Abs(s) <= limit) && limit < glitch.Margin;
            patterns.Add(string.Join(",", shifts));
        }
        check($"Glitch: rows tear at most ±5% and jitter ±1.25% of the width ({limit} px of {w}), inside the canvas margin", within);
        // Three hard steps of the seed over the collapse (and the end frame's own seed): never a smooth slide.
        check($"Glitch: the tears jump in three steps, not smoothly ({patterns.Count} patterns)", patterns.Count == 3);
        // Each step holds a third of the picture: at the cyberpunk theme's 480 ms, 160 ms each, and the tears jump twice,
        // under three changes in any second.
        int jumps = 0; int last = GlitchCollapse.Seed(0);
        for (double t = 0; t <= .48; t += .001) { int seed = GlitchCollapse.Seed(t / .48); if (seed != last) { jumps++; last = seed; } }
        check("Glitch: each step holds a third of the length and the tears jump twice (under 3 a second)",
            jumps == 2 && GlitchCollapse.Seed(.33) == 0 && GlitchCollapse.Seed(.34) == 1 && GlitchCollapse.Seed(.67) == 2 && GlitchCollapse.Seed(1) == 2);
        check("Glitch: it breaks up and fades only in the last third",
            GlitchCollapse.BreakupAt(.66) == 0 && GlitchCollapse.BreakupAt(1) == 1 && Math.Abs(GlitchCollapse.BreakupAt(5 / 6.0) - .5) < 1e-9);
        int active = Enumerable.Range(0, 200).Count(line => CollapseFx.GlitchHash(line, 0) >= GlitchCollapse.Threshold);
        check($"Glitch: about 45% of the bands tear ({active / 2.0:0}%)", active is > 70 and < 110);

        var canvas = new uint[glitch.CanvasWidth * h];
        int Drawn(double p) { glitch.Render(p, canvas); return canvas.Count(c => c >> 24 != 0); }
        int whole = Picture(w, h).Count(c => c >> 24 != 0);
        glitch.Render(0, canvas);
        bool rowsIntact = true;
        for (int y = 0; y < h; y++)
        {
            var row = canvas.AsSpan(y * glitch.CanvasWidth, glitch.CanvasWidth).ToArray().Where(c => c != 0).ToArray();
            var source = Picture(w, h).AsSpan(y * w, w).ToArray().Where(c => c != 0).ToArray();
            rowsIntact &= row.SequenceEqual(source);
        }
        check("Glitch: the first frame is the whole picture, each row only moved sideways", Drawn(0) == whole && rowsIntact);
        check("Glitch: through the first two steps the whole picture shows, torn but unbroken", Drawn(.5) == whole && Drawn(.6) == whole);
        double share = Drawn(5 / 6.0) / (double)whole, cut = 1 - CollapseFx.SmoothStep(0, 1, .5) * GlitchCollapse.Breakup;
        check($"Glitch: in the last third it breaks up pixel by pixel (1 - 0.92p kept: {share:P0} against {cut:P0})", Math.Abs(share - cut) < .06);
        check("Glitch: nothing is left at the end", Drawn(1) == 0 && Drawn(.995) == 0);
    }

    private static void Foam(Action<string, bool> check)
    {
        check("Foam: the hash is windows-animations' pseudo_hash", FoamCollapse.Hash(3, 7) == ((3u * 73856093u) ^ (7u * 19349663u)) % 10000 / 10000.0);
        int w = 240, h = 96;
        var source = Picture(w, h);
        var foam = new FoamCollapse(source, w, h, 2);
        bool starts = true;
        for (int y = 0; y < h; y += 3) for (int x = 0; x < w; x += 3) starts &= foam.StartOf(x, y) is >= 0 and <= .9f * .875f + 1e-4f;
        check("Foam: every pixel starts within 0.9 of three octaves of value noise, gone 0.15 later", starts && foam.Edge == 2);
        var canvas = new uint[w * h];
        foam.Render(0, canvas);
        check("Foam: the first frame is the picture itself", canvas.SequenceEqual(source));
        int last = int.MaxValue; bool shrinking = true, foamed = false, foamNearGone = true;
        uint white = 0xE6E6E6E6;
        for (double p = .05; p <= 1.0001; p += .05)
        {
            foam.Render(p, canvas);
            int visible = canvas.Count(c => c >> 24 != 0);
            shrinking &= visible <= last; last = visible;
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    if (canvas[y * w + x] != white) continue;
                    foamed = true;
                    // A foam pixel's block touches a block that has dissolved.
                    bool near = false;
                    for (int dy = 1 - 2 * foam.Edge; dy < 2 * foam.Edge && !near; dy++)
                        for (int dx = 1 - 2 * foam.Edge; dx < 2 * foam.Edge && !near; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            near = xx >= 0 && yy >= 0 && xx < w && yy < h && (p - foam.StartOf(xx, yy)) / FoamCollapse.LifeSpan >= 1;
                        }
                    foamNearGone &= near;
                }
        }
        foam.Render(1, canvas);
        check("Foam: it dissolves steadily, leaving a line of foam along the dissolving edge and nothing at the end",
            shrinking && foamed && foamNearGone && canvas.All(c => c == 0));
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
        var shared = new FlashBudget();
        check("Budget: a theme without a picture spends nothing", CollapseFx.Choose(CollapseStyle.Fade, on, shared, 0) == CollapseStyle.Fade && shared.Available(0) && shared.TryTake(0) && shared.TryTake(0) && !shared.Available(0));
        shared.Clear();
        check("Budget: with Windows' animations off the default fade runs and spends nothing",
            CollapseFx.Choose(CollapseStyle.Glitch, off, shared, 0) == CollapseStyle.Fade && !CollapseFx.Possible(CollapseStyle.Glitch, off, shared, 0) && shared.TryTake(0) && shared.TryTake(0));
        shared.Clear();
        var picks = new[] { 0, .2, .4, .9, 1.25 }.Select(t => CollapseFx.Choose(CollapseStyle.Glitch, on, shared, t)).ToArray();
        check($"Budget: each collapse is a flash; a third within one second falls back to the default fade ({string.Join(",", picks)})",
            picks.SequenceEqual([CollapseStyle.Glitch, CollapseStyle.Glitch, CollapseStyle.Fade, CollapseStyle.Fade, CollapseStyle.Glitch]));
        // A window's presses spend the same budget: two glitch slices leave no room for a picture in that second, and a
        // picture's flash leaves a press only one.
        shared.Clear();
        var presses = new FlashBudget(shared);
        bool pressed = presses.TryTake(0) && presses.TryTake(.1);
        check("Budget: presses and pictures share one budget (two flashes a second in all)",
            pressed && CollapseFx.Choose(CollapseStyle.Foam, on, shared, .3) == CollapseStyle.Fade && !CollapseFx.Possible(CollapseStyle.Foam, on, shared, .3)
            && CollapseFx.Choose(CollapseStyle.Foam, on, shared, 1.05) == CollapseStyle.Foam && presses.TryTake(1.15) && !presses.TryTake(1.2));
        presses.Clear();
        check("Budget: clearing a window's budget leaves the shared one spent", !shared.Available(1.2) && !presses.Available(1.2));
        check("Budget: the app has one shared budget", ReferenceEquals(FlashBudget.Shared, FlashBudget.Shared));
    }
}
