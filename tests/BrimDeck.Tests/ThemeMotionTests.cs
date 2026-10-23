using BrimDeck.Core;

// Theme motion: the built-in themes' characters, the "motion" section's inheritance and limits, flash safety, the ambient
// light's frames, and when anything may move at all.
internal static class ThemeMotionTests
{
    internal static void Run(Action<string, bool> check)
    {
        static PanelPalette Palette(string id) => PanelThemes.Resolve(PanelThemes.BuiltIn(id)!);
        static PanelPalette Custom(string json) => PanelThemes.Build("custom", PanelThemes.Parse(json));

        // Every built-in theme has its own answers to the pointer; only the cyberpunk demo has ambient light.
        string[] builtIns = ["dark", "light", "one-dark-pro", "github-dark", "github-light", "dracula", "night-owl", PanelThemes.Demo];
        check("Every built-in theme is listed for motion", PanelThemes.BuiltIns.Select(theme => theme.Id).Order().SequenceEqual(builtIns.Order()));
        check("Every built-in theme answers the pointer", builtIns.All(id => Palette(id).Motion.AnyInteraction));
        check("Only the cyberpunk theme has ambient light", builtIns.All(id => Palette(id).Motion.Ambient.Any == (id == PanelThemes.Demo)));
        var cyber = Palette(PanelThemes.Demo).Motion;
        check("Cyberpunk: breathing, drifting, sweeping, a rare flicker, neon pointer answers",
            cyber.Ambient is { Breathe: 6.5, BreatheDepth: .22, Drift: 11, Sweep: 16, SweepOpacity: .07, Flicker: 41, FlickerDepth: .1 }
            && cyber is { Hover: HoverFx.Neon, Press: PressFx.Ring, Focus: FocusFx.Neon, Color: "accent", Secondary: "shadow" } && cyber.Trail > 0);
        check("GitHub themes are restrained and quick", Palette("github-dark").Motion is { Hover: HoverFx.Edge, Particles: ParticleFx.Pixel, Pace: < 1 } && Palette("github-light").Motion is { Hover: HoverFx.Edge, Particles: ParticleFx.Pixel, Pace: < 1 });
        check("Every built-in theme leaves its own particles",
            new Dictionary<string, ParticleFx> { ["dark"] = ParticleFx.Dust, ["light"] = ParticleFx.Dust, ["one-dark-pro"] = ParticleFx.Spark, ["dracula"] = ParticleFx.Star,
                ["night-owl"] = ParticleFx.Firefly, [PanelThemes.Demo] = ParticleFx.Neon }.All(pair => Palette(pair.Key).Motion is { Trail: > 0 } motion && motion.Particles == pair.Value));
        check("Particle styles inherit and unknown ones keep the base's",
            Custom("""{ "base": "dracula", "motion": { "particles": "firefly" } }""").Motion.Particles == ParticleFx.Firefly
            && Custom("""{ "base": "dracula", "motion": { "particles": "confetti" } }""").Motion.Particles == ParticleFx.Star);
        check("Light lifts, dark lights, the editor themes ripple",
            Palette("light").Motion.Hover == HoverFx.Lift && Palette("dark").Motion.Hover == HoverFx.Spot
            && new[] { "one-dark-pro", "dracula", "night-owl" }.All(id => Palette(id).Motion is { Hover: HoverFx.Spot, Press: PressFx.Ripple, Trail: > 0 }));
        check("Dracula's second color is its pink", Palette("dracula").Motion.Secondary == "liked");
        var system = Palette("dark").Motion.ForSystem();
        check("System follows the Windows accent, quietly", system is { Color: ThemeMotion.SystemAccent, Hover: HoverFx.Spot, Particles: ParticleFx.Dust, Focus: FocusFx.Ring } && !system.Ambient.Any
            && system.HoverStrength <= Palette("dark").Motion.HoverStrength && system.Pace == Palette("dark").Motion.Pace);

        // Inheritance, field by field along the base chain.
        var neonEdge = Custom("""{ "base": "cyberpunk-edgerunners", "motion": { "hover": "edge", "ambient": { "sweep": 0 } } }""").Motion;
        check("A custom theme inherits motion from its base and changes only what it names",
            neonEdge.Hover == HoverFx.Edge && neonEdge.Press == PressFx.Ring && neonEdge.Ambient.Breathe == 6.5 && neonEdge.Ambient.Sweep == 0 && neonEdge.Ambient.Drift == 11);
        var calm = Custom("""{ "base": "dracula", "motion": { "ambient": { "breathe": 8, "breatheDepth": 0.1 } } }""").Motion;
        check("A custom theme can add ambient light to any base", calm.Ambient is { Breathe: 8, BreatheDepth: .1, Drift: 0 } && calm.Ambient.Any && calm.Hover == HoverFx.Spot && calm.Secondary == "liked");
        check("A theme without a motion section has its base's", Custom("""{ "base": "github-light" }""").Motion == Palette("github-light").Motion);

        // Limits: nothing a file says can make the light flash or the answers loud.
        var wild = Custom("""
            { "motion": { "ambient": { "breathe": 0.1, "breatheDepth": 5, "drift": 0.01, "sweep": 0.5, "sweepOpacity": 3, "flicker": 1, "flickerDepth": 9 },
              "hover": "bogus", "hoverStrength": 9, "press": "7", "pressStrength": -1, "trail": 4, "focus": "NEON", "pace": 99 } }
            """).Motion;
        check("Ambient values are clamped to their ranges", wild.Ambient == new AmbientMotion(MotionSafety.BreatheMin, MotionSafety.BreatheDepthMax, MotionSafety.DriftMin,
            MotionSafety.SweepMin, MotionSafety.SweepOpacityMax, MotionSafety.FlickerMin, MotionSafety.FlickerDepthMax));
        check("Interaction values are clamped and unknown styles keep the base's",
            wild is { Hover: HoverFx.Spot, HoverStrength: MotionSafety.HoverMax, Press: PressFx.Pulse, PressStrength: 0, Trail: MotionSafety.TrailMax, Focus: FocusFx.Neon, Pace: MotionSafety.PaceMax });
        check("Negative periods turn effects off", !Custom("""{ "base": "cyberpunk-edgerunners", "motion": { "ambient": { "breathe": -3, "drift": -1, "sweep": 0, "flicker": -9 } } }""").Motion.Ambient.Any);
        check("Not-a-number values keep the base's", Custom("""{ "base": "cyberpunk-edgerunners", "motion": { "ambient": { "breathe": 1e400 } } }""").Motion.Ambient.Breathe == 6.5);

        // Flash safety (WCAG 2.3: at most three flashes in any second); the built-in light changes far slower.
        check("The harshest allowed ambient light stays under three changes a second", MotionSafety.FastestHz(wild.Ambient) <= MotionSafety.MaxFlashHz && MotionSafety.PeakFlashes(wild.Ambient, 90) <= 3);
        check("The cyberpunk light changes less than once a second", MotionSafety.FastestHz(cyber.Ambient) < 1 && MotionSafety.PeakFlashes(cyber.Ambient, 120) <= 1);
        check("Breathing alone is a slow swell", MotionSafety.FastestHz(cyber.Ambient with { Flicker = 0, Sweep = 0 }) < .2);
        var random = new Random(7);
        bool allSafe = true;
        for (int i = 0; i < 200 && allSafe; i++)
        {
            string N() => (random.NextDouble() * 400 - 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var any = Custom($$"""{ "motion": { "ambient": { "breathe": {{N()}}, "breatheDepth": {{N()}}, "drift": {{N()}}, "sweep": {{N()}}, "sweepOpacity": {{N()}}, "flicker": {{N()}}, "flickerDepth": {{N()}} } } }""").Motion.Ambient;
            allSafe = MotionSafety.FastestHz(any) <= MotionSafety.MaxFlashHz && any.BreatheDepth <= MotionSafety.BreatheDepthMax && any.FlickerDepth <= MotionSafety.FlickerDepthMax && any.SweepOpacity <= MotionSafety.SweepOpacityMax;
        }
        check("Random theme files never pass the flash limits", allSafe);

        // Frames.
        var rest = cyber.Ambient.At(0);
        check("The light starts at rest, so starting it never jumps", rest is { Rim: 1, Glow: 1, Drift: 0, Sweep: -1, SweepOpacity: 0 });
        double lowest = 1, highestSweep = 0, biggestStep = 0; bool sweepSeen = false, flickerSeen = false;
        var previous = rest;
        for (int i = 1; i <= 120 * 60; i++)
        {
            var frame = cyber.Ambient.At(i / 60.0);
            lowest = Math.Min(lowest, frame.Rim); highestSweep = Math.Max(highestSweep, frame.SweepOpacity);
            sweepSeen |= frame.Sweep >= 0;
            double expectedRim = 1 - cyber.Ambient.BreatheDepth * (.5 - .5 * Math.Cos(2 * Math.PI * i / 60.0 / cyber.Ambient.Breathe));
            flickerSeen |= frame.Rim < expectedRim - .01;
            biggestStep = Math.Max(biggestStep, Math.Abs(frame.Rim - previous.Rim));
            previous = frame;
        }
        check("The rim dims by at most its breath and one flicker", lowest >= 1 - (cyber.Ambient.BreatheDepth + cyber.Ambient.FlickerDepth) - 1e-9 && lowest < .8);
        check("A scan line passes within two minutes, never brighter than its opacity", sweepSeen && highestSweep <= cyber.Ambient.SweepOpacity + 1e-9 && highestSweep > cyber.Ambient.SweepOpacity * .9);
        check("The neon sags now and then", flickerSeen);
        check("Brightness moves smoothly: under 1.5 percent per frame at 60 fps", biggestStep < .015);
        bool driftSmooth = true;
        for (int i = 1; i <= 60 * 60 && driftSmooth; i++)
        {
            double a = cyber.Ambient.At((i - 1) / 60.0).Drift, b = cyber.Ambient.At(i / 60.0).Drift;
            driftSmooth = Math.Abs(b - a) < .01 || Math.Abs(Math.Abs(b - a) - 2) < .01;
        }
        check("Colors drift smoothly; the loop's wrap is seamless (a reflected brush repeats every two widths)", driftSmooth);
        cyber.Ambient.At(1.5);
        long before = GC.GetAllocatedBytesForCurrentThread();
        double sum = 0;
        for (int i = 0; i < 20_000; i++) sum += cyber.Ambient.At(i * .033).Rim;
        check("An ambient frame allocates nothing", GC.GetAllocatedBytesForCurrentThread() == before && sum > 0);

        // When anything may move.
        var on = new MotionConditions(Animations: true, SystemAnimations: true, HighContrast: false, Visible: true, Suppressed: false,
            PoweringOff: false, Locked: false, BatterySaver: false, HasMaterial: true);
        check("Ambient light runs on a visible island with animations on", MotionGate.Ambient(on, cyber) && MotionGate.Interaction(on, cyber));
        check("No ambient light for a theme without it", !MotionGate.Ambient(on, Palette("dracula").Motion));
        var stops = new (string Name, MotionConditions Conditions)[]
        {
            ("animations off in BrimDeck", on with { Animations = false }), ("Windows animation effects off", on with { SystemAnimations = false }),
            ("high contrast", on with { HighContrast = true }), ("the island hidden", on with { Visible = false }), ("hidden by a rule or by hand", on with { Suppressed = true }),
            ("powering off", on with { PoweringOff = true }), ("a locked session", on with { Locked = true }), ("battery saver", on with { BatterySaver = true }),
            ("the indicator line", on with { HasMaterial = false })
        };
        foreach (var (name, conditions) in stops) check($"No ambient light with {name}", !MotionGate.Ambient(conditions, cyber));
        check("No pointer answers with animations off, Windows' effects off or high contrast",
            !MotionGate.Interaction(on with { Animations = false }, cyber) && !MotionGate.Interaction(on with { SystemAnimations = false }, cyber) && !MotionGate.Interaction(on with { HighContrast = true }, cyber));
        check("Pointer answers do not need the light's conditions", MotionGate.Interaction(on with { Locked = true, BatterySaver = true, HasMaterial = false }, cyber));
        var silent = Custom("""{ "motion": { "hover": "none", "press": "none", "trail": 0, "focus": "none" } }""").Motion;
        check("A theme can turn every answer off", !silent.AnyInteraction && !MotionGate.Interaction(on, silent));

        // Durations follow the animation length chosen in settings and the theme's pace.
        var normal = MotionTiming.From(360, 1);
        check("At the default length a hover arrives in about the monitor's hover time and a ripple lasts half a second",
            Math.Abs(normal.HoverIn - Eased.Hover * 1000) <= 10 && normal.Press is >= 450 and <= 550 && normal.HoverOut > normal.HoverIn);
        var slow = MotionTiming.From(1000, 1.6); var fast = MotionTiming.From(100, .6);
        check("Durations scale with the length and pace, within limits", slow.Press == 1400 && slow.Press > normal.Press && fast.Press == 240 && fast.HoverIn == 80
            && MotionTiming.From(720, 1).HoverIn > normal.HoverIn && MotionTiming.From(360, double.NaN) == normal);

        // Colors.
        var cyberPalette = Palette(PanelThemes.Demo);
        check("Motion colors name theme colors, colors or the Windows accent",
            ThemeMotion.ResolveColor(cyberPalette, "shadow") == cyberPalette.Readable(cyberPalette["shadow"], PanelThemes.GraphicContrast)
            && ThemeMotion.ResolveColor(cyberPalette, "bogus") == ThemeMotion.ResolveColor(cyberPalette, "accent")
            && ThemeMotion.ResolveColor(cyberPalette, "system") == ThemeMotion.ResolveColor(cyberPalette, "accent")
            && ThemeMotion.ResolveColor(cyberPalette, "system", ThemeColor.Parse("#0078D4")) == cyberPalette.Readable(ThemeColor.Parse("#0078D4"), PanelThemes.GraphicContrast));
        var dim = ThemeMotion.ResolveColor(cyberPalette, "#1A1020");
        check("A motion color that would vanish on the surface is made readable", dim.MinimumContrast(cyberPalette.SurfaceStops) >= PanelThemes.GraphicContrast - .05);

        // The template lists the motion section, and a theme made from another starts with the same motion.
        check("The template has a motion section with the dark theme's values", PanelThemes.Build("t", PanelThemes.Parse(PanelThemes.Template)).Motion == Palette("dark").Motion);
        check("A theme made from another starts out with the same motion",
            builtIns.All(id => PanelThemes.Build("copy", PanelThemes.Parse(PanelThemes.FillTemplate("copy", Palette(id), id))).Motion == Palette(id).Motion
                && PanelThemes.Build("copy", PanelThemes.Parse(PanelThemes.FillTemplate("copy", Palette(id)))).Motion == Palette(id).Motion));
        check("Filling the template leaves the clock's color and style to the clock",
            PanelThemes.Parse(PanelThemes.FillTemplate("copy", Palette("dracula"))) is { Clock: { Color: "gradient", Style: "neon" }, Motion.Color: "accent" });
    }
}
