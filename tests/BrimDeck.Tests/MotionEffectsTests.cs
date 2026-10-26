using BrimDeck.Core;

// The motion upgrade's answers to the pointer (splash, rain, bubbles), a theme's own lengths for them, the shared flash budget
// and the ambient light's pause under a maximized or full-screen window.
internal static class MotionEffectsTests
{
    internal static void Run(Action<string, bool> check)
    {
        static PanelPalette Palette(string id) => PanelThemes.Resolve(PanelThemes.BuiltIn(id)!);
        static PanelPalette Custom(string json) => PanelThemes.Build("custom", PanelThemes.Parse(json));
        Effects(check, Palette, Custom);
        Lengths(check, Custom);
        Budget(check);
        Covered(check, Palette);
    }

    private static void Effects(Action<string, bool> check, Func<string, PanelPalette> palette, Func<string, PanelPalette> custom)
    {
        check("Splash, rain and bubble: only the cyberpunk demo (rain) and the island (splash and bubbles) opt in",
            PanelThemes.BuiltIns.Where(theme => theme.Id is not (PanelThemes.Demo or PanelThemes.IslandHoliday))
                .All(theme => palette(theme.Id).Motion is { } m && m.Press != PressFx.Splash && m.Particles is not (ParticleFx.Rain or ParticleFx.Bubble))
            && palette(PanelThemes.Demo).Motion is { Particles: ParticleFx.Rain } && palette(PanelThemes.IslandHoliday).Motion is { Press: PressFx.Splash, Particles: ParticleFx.Bubble });
        var island = custom("""{ "base": "island-holiday", "motion": { "press": "splash", "particles": "bubble", "trail": 0.4 } }""");
        var night = custom("""{ "base": "cyberpunk-edgerunners", "motion": { "particles": "RAIN" } }""");
        check("They are read by name from motion.press and motion.particles; ripple stays shade",
            island.Motion is { Press: PressFx.Splash, Particles: ParticleFx.Bubble } && night.Motion is { Particles: ParticleFx.Rain }
            && custom("""{ "base": "dark", "motion": { "press": "ripple" } }""").Motion.Press == PressFx.Shade);
        check("The collapse pictures and the chromatic pulse are gone: their keys are not read and the template has none",
            PanelThemes.FillTemplate("t", custom("""{ "base": "dark", "motion": { "collapse": "glitch", "aberration": 1.2 } }""")) is var t
            && !t.Contains("\"collapse\"") && !t.Contains("\"aberration\"") && !t.Contains("\"collapseMs\""));
    }

    // A theme's own lengths: read in milliseconds, held to their ranges, 0 for the built-in ones.
    private static void Lengths(Action<string, bool> check, Func<string, PanelPalette> custom)
    {
        var slow = custom("""{ "base": "dark", "motion": { "particleMs": 1000, "sliceMs": 120, "scanMs": 300, "splashMs": 620 } }""").Motion;
        check("Lengths: particleMs, sliceMs, scanMs and splashMs are read and inherited",
            slow is { ParticleMs: 1000, SliceMs: 120, ScanMs: 300, SplashMs: 620 } && ThemeMotion.Default is { ParticleMs: 0, SliceMs: 0, ScanMs: 0, SplashMs: 0 });
        var wild = custom("""{ "base": "dark", "motion": { "particleMs": 10, "sliceMs": 999, "scanMs": -4, "splashMs": 2000 } }""").Motion;
        check("Lengths: held to 300-2400, 50-160, 100-400 and 300-800 ms (0 or less keeps the built-in)",
            wild is { ParticleMs: 300, SliceMs: 160, ScanMs: 0, SplashMs: 800 });
        check("Lengths: the theme template writes them out", PanelThemes.FillTemplate("t", custom("""{ "base": "dark", "motion": { "particleMs": 800, "scanMs": 300 } }""")) is var t
            && t.Contains("\"particleMs\": 800") && t.Contains("\"scanMs\": 300"));
        // lock's slice and scan line take the theme's lengths; the scan line keeps the press alive until it has passed.
        var timing = MotionTiming.For(.8, 3) with { Glitch = 120, Scan = 300 };
        var press = new PressTrack();
        press.Press(0, 0, glitch: true);
        bool slice = press.At(.11, timing, 3).GlitchOn && !press.At(.13, timing, 3).GlitchOn;
        press.Release(.2, inside: true, scan: true, timing, 3);
        check("Lengths: lock's slice holds sliceMs and its scan line passes over scanMs",
            slice && press.At(.2 + .29, timing, 3).ScanAt >= 0 && press.At(.2 + .31, timing, 3).ScanAt < 0 && press.Alive(.2 + .29, timing));
    }

    // Every window's flashes share one budget: two a second in all.
    private static void Budget(Action<string, bool> check)
    {
        var shared = new FlashBudget();
        var presses = new FlashBudget(shared);
        var other = new FlashBudget(shared);
        bool two = presses.TryTake(0) && other.TryTake(.1);
        check("Budget: two windows' flashes share one budget (two a second in all)", two && !presses.TryTake(.3) && !other.TryTake(.4) && presses.TryTake(1.05));
        presses.Clear();
        check("Budget: clearing a window's budget leaves the shared one spent", !shared.Available(1.08) && !presses.Available(1.08));
        check("Budget: the app has one shared budget", ReferenceEquals(FlashBudget.Shared, FlashBudget.Shared));
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
}
