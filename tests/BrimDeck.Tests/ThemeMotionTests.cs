using BrimDeck.Core;

// Theme motion: the built-in themes' characters, the "motion" section's inheritance and limits, flash safety, the ambient
// light's frames, and when anything may move at all.
internal static class ThemeMotionTests
{
    internal static void Run(Action<string, bool> check)
    {
        static PanelPalette Palette(string id) => PanelThemes.Resolve(PanelThemes.BuiltIn(id)!);
        static PanelPalette Custom(string json) => PanelThemes.Build("custom", PanelThemes.Parse(json));

        // Every built-in theme but the default dark and light ones and the GitHub pair (which keep the controls' own looks: the hover
        // mark is off unless a theme turns it on) has its own answers to the pointer; ambient light is for the cyberpunk demo, the glass themes' drifting glint and the island's swell.
        string[] builtIns = ["dark", "light", PanelThemes.GlassLight, PanelThemes.GlassDark, "one-dark-pro", "github-dark", "github-light", "dracula", "night-owl",
            PanelThemes.IslandHoliday, PanelThemes.IslandHoliday2, PanelThemes.PixelBlocks, PanelThemes.Demo, PanelThemes.Endfield];
        string[] ambient = [PanelThemes.Demo, PanelThemes.GlassLight, PanelThemes.GlassDark, PanelThemes.IslandHoliday, PanelThemes.IslandHoliday2];
        check("Every built-in theme is listed for motion", PanelThemes.BuiltIns.Select(theme => theme.Id).Order().SequenceEqual(builtIns.Order()));
        check("Every built-in theme answers the pointer except the default dark and light and the GitHub pair, which draw nothing over the controls",
            builtIns.All(id => Palette(id).Motion.AnyInteraction == id is not ("dark" or "light" or "github-dark" or "github-light")));
        check("Only the cyberpunk, glass and island themes have ambient light", builtIns.All(id => Palette(id).Motion.Ambient.Any == ambient.Contains(id)));
        check("Glass: only a slow drifting glint, no breathing, sweep or flicker; a lens on hover, no particles",
            new[] { PanelThemes.GlassLight, PanelThemes.GlassDark }.All(id => Palette(id).Motion is { Hover: HoverFx.None, Particles: ParticleFx.None, Steps: 0 } m
                && m.Ambient is { Drift: >= 20, Breathe: 0, Sweep: 0, Flicker: 0 })
            && new[] { PanelThemes.GlassLight, PanelThemes.GlassDark }.All(id => Palette(id).Motion.Press == PressFx.Jelly));
        var firstDay = Palette(PanelThemes.IslandHoliday).Motion;
        var firstNight = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.IslandHoliday)!, true).Motion;
        check("Island holiday: a swell and waves; sun glints by day, glowing plankton by night",
            firstDay is { Hover: HoverFx.None, Press: PressFx.Sparkle, Particles: ParticleFx.Star, ParticleStrength: > 0, Steps: 0 } && firstDay.Ambient is { Breathe: > 0, Drift: > 0, Sweep: > 0 }
            && firstNight is { Hover: HoverFx.None, Press: PressFx.Ember, Particles: ParticleFx.Firefly, ParticleStrength: > 0 } && firstNight.Ambient.Breathe > firstDay.Ambient.Breathe);
        var islandDay = Palette(PanelThemes.IslandHoliday2).Motion;
        var islandNight = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.IslandHoliday2)!, true).Motion;
        check("Island holiday 2: a swell and waves; a splash of water, bubbles by day and glowing plankton by night",
            islandDay is { Hover: HoverFx.None, Press: PressFx.Splash, Particles: ParticleFx.Bubble, ParticleStrength: > 0, Steps: 0, Spring: SpringTier.Default } && islandDay.Ambient is { Breathe: > 0, Drift: > 0, Sweep: > 0 }
            && islandNight is { Hover: HoverFx.None, Press: PressFx.Splash, Particles: ParticleFx.Firefly, ParticleStrength: > 0 } && islandNight.Ambient.Breathe > islandDay.Ambient.Breathe
            && islandNight.Pace == islandDay.Pace);
        check("Pixel blocks: slots and bevels in four hard steps, square particles, no ambient light",
            Palette(PanelThemes.PixelBlocks).Motion is { Hover: HoverFx.None, Press: PressFx.Bevel, Particles: ParticleFx.Pixel, ParticleStrength: > 0, Steps: 4 } pixel && !pixel.Ambient.Any);
        var cyber = Palette(PanelThemes.Demo).Motion;
        check("Cyberpunk: breathing, drifting, sweeping, a rare flicker; hard HUD answers in steps, and rain",
            cyber.Ambient is { Breathe: 6.5, BreatheDepth: .22, Drift: 11, Sweep: 16, SweepOpacity: .07, Flicker: 41, FlickerDepth: .1 }
            && cyber is { Hover: HoverFx.None, Press: PressFx.Lock, Focus: FocusFx.Brackets, Color: "accent", Secondary: "shadow", Highlight: "clock", Particles: ParticleFx.Rain, Steps: 3, Pace: < 1, Spring: SpringTier.Firm });
        check("GitHub themes have no motion section: no hover, press or focus marks",
            new[] { "github-dark", "github-light" }.All(id => Palette(id).Motion is { Hover: HoverFx.None, Press: PressFx.None, Focus: FocusFx.None, AnyInteraction: false, Ambient.Any: false }));
        check("Dark and light keep BrimDeck's default look: no hover, press or focus marks",
            new[] { "dark", "light" }.All(id => Palette(id).Motion is { Hover: HoverFx.None, Press: PressFx.None, Focus: FocusFx.None, Particles: ParticleFx.None, Ambient.Any: false })
            && ThemeMotion.Default is { AnyInteraction: false });
        check("One Dark Pro is the editor: a caret, a selection",
            Palette("one-dark-pro").Motion is { Hover: HoverFx.None, Press: PressFx.Select, Focus: FocusFx.Caret, Particles: ParticleFx.None });
        check("Dracula sparkles in purple and pink; Night Owl glows warm with fireflies",
            Palette("dracula").Motion is { Hover: HoverFx.None, Press: PressFx.Sparkle, Particles: ParticleFx.Star, ParticleStrength: > 0, Secondary: "liked" }
            && Palette("night-owl").Motion is { Hover: HoverFx.None, Press: PressFx.Ember, Particles: ParticleFx.Firefly, ParticleStrength: > 0 });
        // No built-in theme marks hovered controls (as upstream); each keeps its own press, particles, focus and pace. The glass pair is
        // one material in two tones, and shares its answers; dark, light and the GitHub pair share having none.
        check("No built-in theme marks a hovered control", builtIns.All(id => Palette(id).Motion.Hover == HoverFx.None));
        check("Every built-in theme has a language of its own", builtIns.Select(id => Palette(id).Motion is var m ? (m.Press, m.Particles, m.Focus, m.Pace) : default).Distinct().Count() == builtIns.Length - 4);
        check("Only the mechanical and pixel themes move in steps", builtIns.All(id => (Palette(id).Motion.Steps > 0) == (id is PanelThemes.Demo or PanelThemes.PixelBlocks)));
        check("Particle styles inherit and unknown ones keep the base's",
            Custom("""{ "base": "dracula", "motion": { "particles": "firefly" } }""").Motion.Particles == ParticleFx.Firefly
            && Custom("""{ "base": "dracula", "motion": { "particles": "confetti" } }""").Motion.Particles == ParticleFx.Star);
        check("System on the default themes draws nothing either",
            new[] { "dark", "light" }.All(id => Palette(id).Motion.ForSystem() is { AnyInteraction: false, Color: ThemeMotion.SystemAccent } system && !system.Ambient.Any));
        var system = Palette("dracula").Motion.ForSystem();
        check("System follows the Windows accent, quietly", system is { Color: ThemeMotion.SystemAccent, Highlight: ThemeMotion.SystemAccent, Hover: HoverFx.None, Press: PressFx.Shade, Particles: ParticleFx.None, Steps: 0, Focus: FocusFx.Ring } && !system.Ambient.Any
            && system.HoverStrength == Palette("dracula").Motion.HoverStrength && system.Pace == Palette("dracula").Motion.Pace);
        check("System keeps a theme's lift and sink, in the Windows accent",
            (Palette("dracula").Motion with { Hover = HoverFx.Lift, Press = PressFx.Sink, Secondary = "shadow" }).ForSystem() is { Hover: HoverFx.Lift, Press: PressFx.Sink, Secondary: "shadow", Color: ThemeMotion.SystemAccent });
        check("System never takes a theme's steps or particles", Palette(PanelThemes.Demo).Motion.ForSystem() is { Steps: 0, Particles: ParticleFx.None, Hover: HoverFx.None, Press: PressFx.Shade });

        // Names of earlier versions still read sensibly; numbers are not names.
        var old = Custom("""{ "motion": { "hover": "neon", "press": "ring", "focus": "neon" } }""").Motion;
        check("Earlier style names read as the closest new style",
            old is { Hover: HoverFx.Brackets, Press: PressFx.Lock, Focus: FocusFx.Brackets }
            && Custom("""{ "motion": { "hover": "spot", "press": "ripple" } }""").Motion is { Hover: HoverFx.Reveal, Press: PressFx.Shade }
            && Custom("""{ "motion": { "hover": "edge", "press": "pulse" } }""").Motion is { Hover: HoverFx.Underline, Press: PressFx.Shade });
        check("Style names ignore case and spaces; numbers keep the base's",
            Custom("""{ "motion": { "hover": " Caret ", "press": "LOCK" } }""").Motion is { Hover: HoverFx.Caret, Press: PressFx.Lock }
            && Custom("""{ "motion": { "hover": "3", "press": "-1", "focus": "2" } }""").Motion is { Hover: HoverFx.None, Press: PressFx.None, Focus: FocusFx.None });

        // Inheritance, field by field along the base chain.
        var neonEdge = Custom("""{ "base": "cyberpunk-edgerunners", "motion": { "hover": "underline", "ambient": { "sweep": 0 } } }""").Motion;
        check("A custom theme inherits motion from its base and changes only what it names",
            neonEdge.Hover == HoverFx.Underline && neonEdge.Press == PressFx.Lock && neonEdge.Steps == 3 && neonEdge.Highlight == "clock"
            && neonEdge.Ambient.Breathe == 6.5 && neonEdge.Ambient.Sweep == 0 && neonEdge.Ambient.Drift == 11);
        check("Steps and the highlight inherit and can be changed",
            Custom("""{ "base": "cyberpunk-edgerunners", "motion": { "steps": 0, "highlight": "#FF0000" } }""").Motion is { Steps: 0, Highlight: "#FF0000", Hover: HoverFx.None }
            && Custom("""{ "base": "dracula", "motion": { "steps": 5 } }""").Motion is { Steps: 5, Hover: HoverFx.None });
        var calm = Custom("""{ "base": "dracula", "motion": { "ambient": { "breathe": 8, "breatheDepth": 0.1 } } }""").Motion;
        check("A custom theme can add ambient light to any base", calm.Ambient is { Breathe: 8, BreatheDepth: .1, Drift: 0 } && calm.Ambient.Any && calm.Hover == HoverFx.None && calm.Secondary == "liked");
        check("A theme without a motion section has its base's", Custom("""{ "base": "github-light" }""").Motion == Palette("github-light").Motion);

        // Limits: nothing a file says can make the light flash or the answers loud.
        var wild = Custom("""
            { "motion": { "ambient": { "breathe": 0.1, "breatheDepth": 5, "drift": 0.01, "sweep": 0.5, "sweepOpacity": 3, "flicker": 1, "flickerDepth": 9 },
              "hover": "bogus", "hoverStrength": 9, "press": "7", "pressStrength": -1, "trail": 4, "focus": "BRACKETS", "pace": 99, "steps": 40 } }
            """).Motion;
        check("Ambient values are clamped to their ranges", wild.Ambient == new AmbientMotion(MotionSafety.BreatheMin, MotionSafety.BreatheDepthMax, MotionSafety.DriftMin,
            MotionSafety.SweepMin, MotionSafety.SweepOpacityMax, MotionSafety.FlickerMin, MotionSafety.FlickerDepthMax));
        check("Interaction values are clamped and unknown styles keep the base's",
            wild is { Hover: HoverFx.None, HoverStrength: MotionSafety.HoverMax, Press: PressFx.None, PressStrength: 0, ParticleStrength: MotionSafety.TrailMax, Focus: FocusFx.Brackets, Pace: MotionSafety.PaceMax, Steps: MotionSafety.StepsMax });
        check("One step is no steps; fractions round; nonsense keeps the base's",
            Custom("""{ "base": "cyberpunk-edgerunners", "motion": { "steps": 1 } }""").Motion.Steps == 0
            && Custom("""{ "motion": { "steps": 2.6 } }""").Motion.Steps == 3
            && Custom("""{ "motion": { "steps": -5 } }""").Motion.Steps == 0);
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
        check("The light starts at rest, so starting it never jumps", rest is { Rim: 1, Drift: 0, Sweep: -1, SweepOpacity: 0 });
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

        // How strongly a mark draws (upstream review, PR #287): the lift's light top edge fades with the strength as its shadow
        // does. Here the presence it takes is the lift itself, which a sink press flattens (InteractionFx.Draw).
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
        double[] strengths = [.. Enumerable.Range(0, 301).Select(i => i / 1000.0)];
        var highlight = strengths.Select(strength => HoverMarkOpacity.LiftHighlight(strength, 1)).ToArray();
        check("The lift's top highlight fades with the strength: nothing at zero, faint at 0.01, its full 0.9 only at the strongest",
            highlight[0] == 0 && HoverMarkOpacity.LiftHighlight(.01, 1) <= .05 && Near(HoverMarkOpacity.LiftHighlight(MotionSafety.HoverMax, 1), .9)
            && highlight.Zip(highlight.Skip(1)).All(pair => pair.Second >= pair.First && pair.Second - pair.First <= .01));
        check("The highlight follows the mark's presence, and a negative strength draws nothing",
            Near(HoverMarkOpacity.LiftHighlight(.2, .5), HoverMarkOpacity.LiftHighlight(.2, 1) / 2) && HoverMarkOpacity.LiftHighlight(.2, 0) == 0 && HoverMarkOpacity.LiftHighlight(-1, 1) == 0);

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
            PanelThemes.Parse(PanelThemes.FillTemplate("copy", Palette("dracula"))) is { Clock: { Color: "default", Style: "minimal" }, Motion.Color: "accent" });
        // A custom theme's copy and its draft saved as new keep its day motion (upstream review, PR #287): this build writes the
        // whole motion section out every time (FillMotion), so the copy does not fall back to the dark base's.
        var daySource = PanelThemes.Parse("""{ "id": "own", "motion": { "hover": "lift", "hoverStrength": 0.2, "pace": 1.3, "color": "#336699", "secondary": "rim" } }""");
        var dayStart = PanelThemes.Build("own", daySource);
        var dayCopy = PanelThemes.Build("copy", PanelThemes.Parse(PanelThemes.FillTemplate("copy", dayStart, null, daySource)));
        check("New from a custom theme keeps the day look's motion", dayCopy.Motion == dayStart.Motion && dayCopy.Motion.Hover == HoverFx.Lift);
        var copyFolder = Directory.CreateTempSubdirectory("brimdeck-motion-copy").FullName;
        try
        {
            var ownInfo = new PanelThemeInfo("own", PanelThemeKind.Custom, daySource, Path.Combine(copyFolder, "own.json"), null);
            var draft = new PanelThemeDraft("own"); draft.SetColor(daySource, "accent", ThemeColor.Parse("#FF8800"));
            var saved = PanelThemes.Parse(File.ReadAllText(PanelThemes.SaveDraftAsNew(copyFolder, ownInfo, draft)));
            check("Saving a custom theme's draft as a new theme keeps the day look's motion", PanelThemes.Build("saved", saved).Motion == dayStart.Motion);
        }
        finally { Directory.Delete(copyFolder, true); }

        // A bad motion value is a warning on the settings page, never a file error (upstream review, PR #287): that field falls
        // back (to the base's, or to the day look's for the night and dusk looks), and everything else in the file, the other
        // looks' motion included, is used. Every field of this build's richer section is read on its own, the ambient ones too.
        string Problem(string json)
        {
            var folder = Directory.CreateTempSubdirectory("brimdeck-motion").FullName;
            try { File.WriteAllText(Path.Combine(folder, "t.json"), json); return PanelThemes.LoadFolder(folder).Single().Error ?? ""; }
            finally { Directory.Delete(folder, true); }
        }
        static IReadOnlyList<string> Warnings(string json) => PanelThemes.Warnings(PanelThemes.Parse(json));
        var githubDarkMotion = Palette("github-dark").Motion;
        check("An unknown motion.hover name is a warning, not a file error, in the day and the night look",
            Problem("""{ "id": "t", "motion": { "hover": "confetti" } }""") == "" && Warnings("""{ "id": "t", "motion": { "hover": "confetti" } }""").Single().Contains("confetti")
            && Problem("""{ "id": "t", "night": { "motion": { "hover": "2" } } }""") == "" && Warnings("""{ "id": "t", "night": { "motion": { "hover": "2" } } }""").Single().Contains('2'));
        check("Unknown press, focus, particles and spring names are warnings too, one each",
            Warnings("""{ "motion": { "press": "boom", "focus": "halo", "particles": "confetti", "spring": "wobbly" } }""") is { Count: 4 } styleWarnings
            && new[] { "boom", "halo", "confetti", "wobbly" }.All(name => styleWarnings.Any(w => w.Contains(name))));
        check("Valid, earlier or blank style names are no problem and no warning", new[] { "reveal", "LIFT", "none", "spot", "", "  " }
            .All(name => Problem("{ \"id\": \"t\", \"motion\": { \"hover\": \"" + name + "\" } }") == "" && Warnings("{ \"motion\": { \"hover\": \"" + name + "\" } }").Count == 0)
            && Warnings("""{ "motion": { "press": "ripple", "focus": "neon", "particles": "bubble", "spring": "Jelly" } }""").Count == 0);
        check("A motion value of the wrong type is a warning, and that field keeps its base's value",
            Problem("""{ "id": "t", "motion": { "hover": "lift", "hoverStrength": "strong" } }""") == ""
            && Custom("""{ "base": "github-dark", "motion": { "hover": "lift", "hoverStrength": "strong" } }""").Motion is { Hover: HoverFx.Lift } typed && typed.HoverStrength == githubDarkMotion.HoverStrength
            && Warnings("""{ "motion": { "hover": "lift", "hoverStrength": "strong" } }""").Single().Contains("hoverStrength"));
        const string AllWrong = """{ "base": "github-dark", "motion": { "hover": 2, "hoverStrength": "0.2", "pace": "fast", "color": 5, "secondary": true, "press": [], "pressStrength": "x", "trail": {}, "particles": 1, "focus": false, "steps": "4", "highlight": 3, "spring": 0, "particleMs": "1", "sliceMs": "1", "scanMs": "1", "splashMs": "1" } }""";
        check("Every motion field of the wrong type is a warning of its own and keeps the base's value, a number written as text too",
            Custom(AllWrong).Motion == githubDarkMotion && Warnings(AllWrong).Count == 17 && Problem(AllWrong.Replace("{ \"base\"", "{ \"id\": \"t\", \"base\"")) == "");
        const string AmbientWrong = """{ "base": "cyberpunk-edgerunners", "motion": { "ambient": { "breathe": "slow", "sweep": 0 } } }""";
        check("A wrong ambient value is a warning; the ambient values next to it still apply",
            Custom(AmbientWrong).Motion.Ambient is { Breathe: 6.5, Sweep: 0, Drift: 11 } && Warnings(AmbientWrong).Single().Contains("ambient.breathe")
            && Custom("""{ "base": "cyberpunk-edgerunners", "motion": { "ambient": 3, "hover": "underline" } }""").Motion is { Hover: HoverFx.Underline, Ambient.Breathe: 6.5 }
            && Warnings("""{ "motion": { "ambient": 3 } }""").Count == 1);
        check("A motion section that is not an object is a warning and no section",
            Custom("""{ "base": "github-dark", "motion": "fast" }""").Motion == githubDarkMotion && Warnings("""{ "motion": [1] }""").Count == 1 && Problem("""{ "id": "t", "motion": 3 }""") == "");
        const string NightWrong = """{ "id": "t", "motion": { "hover": "lift", "hoverStrength": 0.2 }, "night": { "motion": { "hover": "confetti", "hoverStrength": "strong", "pace": 1.4 } } }""";
        var nightWrong = PanelThemes.Parse(NightWrong);
        check("Only the night look's motion wrong: the day look's motion loads, and the night keeps the day's for the bad fields",
            Problem(NightWrong) == "" && PanelThemes.Build("t", nightWrong).Motion is { Hover: HoverFx.Lift, HoverStrength: .2, Pace: 1 }
            && PanelThemes.Build("t", nightWrong, night: true).Motion is { Hover: HoverFx.Lift, HoverStrength: .2, Pace: 1.4 }
            && PanelThemes.Warnings(nightWrong) is { Count: 2 } nightWarnings && nightWarnings.All(w => w.StartsWith(Loc.T("夜间外观：", "Night look: "), StringComparison.Ordinal)));
        check("A wrong value in the dusk look is a warning marked as the dusk look's",
            PanelThemes.Warnings(PanelThemes.Parse("""{ "id": "t", "dusk": { "motion": { "pace": "slow" } } }""")).Single().StartsWith(Loc.T("黄昏外观：", "Dusk look: "), StringComparison.Ordinal));
        var roundTrip = PanelThemes.Parse("""{ "motion": { "ambient": { "breathe": 8, "drift": "x" }, "hover": "lift", "hoverStrength": "x", "press": "sink", "steps": 3, "spring": "firm", "splashMs": 400, "highlight": "#123456" } }""");
        var again = PanelThemes.Parse(System.Text.Json.JsonSerializer.Serialize(new { motion = roundTrip.Motion }));
        check("The motion section is written back with every usable value and without the ignored ones",
            again.Motion is { Hover: "lift", HoverStrength: null, Press: "sink", Steps: 3, Spring: "firm", SplashMs: 400, Highlight: "#123456", Ambient: { Breathe: 8, Drift: null } } && again.Motion.Problems.Count == 0);
    }
}
