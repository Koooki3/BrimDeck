using BrimDeck.Core;

// Theme motion (the hover mark): the "motion" section's inheritance and limits, which built-in themes have one, when anything may
// draw at all, the hover durations and the mark's timeline on an injected clock.
internal static class ThemeMotionTests
{
    internal static void Run(Action<string, bool> check)
    {
        static PanelPalette Palette(string id) => PanelThemes.Resolve(PanelThemes.BuiltIn(id)!);
        static PanelPalette Custom(string json) => PanelThemes.Build("custom", PanelThemes.Parse(json));
        static bool Near(double a, double b, double e = 1e-9) => Math.Abs(a - b) < e;

        // A theme of its own that turns the mark on (no built-in theme does): what the tests below inherit from. The night look is
        // laid over the day look, so a night look's motion inherits field by field from the day's as a theme does from its base.
        const string Day = "\"motion\": { \"hover\": \"underline\", \"hoverStrength\": 0.24, \"pace\": 0.75, \"color\": \"accent\", \"secondary\": \"#8B949E\" }";
        var own = Custom("{ " + Day + " }").Motion;
        static string WithNight(string motion) => "{ \"id\": \"t\", " + Day + ", \"night\": { \"motion\": " + motion + " } }";
        static ThemeMotion Night(string motion) => PanelThemes.Build("t", PanelThemes.Parse(WithNight(motion)), night: true).Motion;

        // ----- Built-in themes -----
        check("No built-in theme draws a hover mark, in any look: the mark is off unless a theme turns it on",
            PanelThemes.BuiltIns.All(theme => Enum.GetValues<DaylightLook>().All(look => PanelThemes.Resolve(theme, look).Motion == ThemeMotion.Default))
            && !ThemeMotion.Default.AnyInteraction);
        check("A theme that turns the mark on gets what it wrote", own is { Hover: HoverFx.Underline, HoverStrength: .24, Pace: .75, Color: "accent", Secondary: "#8B949E", AnyInteraction: true });
        check("A mark turned on over a GitHub theme resolves to readable colors on its surface", new[] { "github-dark", "github-light" }.All(id =>
        {
            var palette = Custom("{ \"base\": \"" + id + "\", " + Day + " }");
            return new[] { palette.Motion.Color, palette.Motion.Secondary }.All(value => ThemeMotion.ResolveColor(palette, value).MinimumContrast([palette.Surface]) >= PanelThemes.GraphicContrast);
        }));

        // ----- The section: inheritance, limits, bad input -----
        check("A theme without a motion section has the default, whatever its colors", Custom("""{ "base": "dark", "colors": { "accent": "#FF0000" } }""").Motion == ThemeMotion.Default);
        var child = Night("""{ "hover": "reveal" }""");
        check("A field a look leaves out comes from the look beneath it", child is { Hover: HoverFx.Reveal, Pace: .75, Secondary: "#8B949E" } && child.HoverStrength == own.HoverStrength
            && Custom("""{ "base": "github-dark", "motion": { "hover": "reveal" } }""").Motion is { Hover: HoverFx.Reveal } fromBase && fromBase.Pace == ThemeMotion.Default.Pace);
        check("Names are case-insensitive", Custom("""{ "motion": { "hover": "LIFT", "hoverStrength": 0.1 } }""").Motion.Hover == HoverFx.Lift);
        check("An unknown name keeps the base's", Night("""{ "hover": "confetti" }""").Hover == HoverFx.Underline);
        check("A number is not a style, whatever the enum would make of it",
            Night("""{ "hover": "2" }""").Hover == HoverFx.Underline && Custom("""{ "motion": { "hover": "7" } }""").Motion.Hover == HoverFx.None);
        check("An empty or blank name keeps the base's", Night("""{ "hover": "  " }""").Hover == HoverFx.Underline);
        check("A style of a later version (a name this one does not know) draws nothing rather than guessing", Custom("""{ "motion": { "hover": "brackets", "hoverStrength": 0.2 } }""").Motion.Hover == HoverFx.None);
        check("Strength is held to the limit, and to zero from below",
            Custom("""{ "motion": { "hover": "reveal", "hoverStrength": 5 } }""").Motion.HoverStrength == MotionSafety.HoverMax
            && Custom("""{ "motion": { "hover": "reveal", "hoverStrength": -1 } }""").Motion.HoverStrength == 0);
        check("Strength zero draws nothing", !Custom("""{ "motion": { "hover": "reveal", "hoverStrength": 0 } }""").Motion.AnyInteraction);
        check("Pace is held to its range", Custom("""{ "motion": { "pace": 9 } }""").Motion.Pace == MotionSafety.PaceMax && Custom("""{ "motion": { "pace": 0.01 } }""").Motion.Pace == MotionSafety.PaceMin);
        check("Not a number keeps what was there", MotionSafety.Amount(double.NaN, .3, .12) == .12 && MotionSafety.Amount(double.PositiveInfinity, .3, .12) == .12
            && MotionSafety.Pace(double.NaN, .75) == .75 && MotionSafety.Pace(null, .75) == .75);
        check("Colors are trimmed, and an empty one keeps the base's",
            Night("""{ "color": "  #112233 ", "secondary": "" }""") is { Color: "#112233", Secondary: "#8B949E" }
            && Night("""{ "color": "   " }""").Color == "accent");
        check("Unknown members of the section are ignored (later versions add press and focus)",
            Custom("""{ "motion": { "hover": "reveal", "press": "sink", "focus": "ring", "particles": "dust", "trail": 0.3 } }""").Motion.Hover == HoverFx.Reveal);
        check("A section that is null is no section", Night("null") == own && Custom("""{ "base": "github-dark", "motion": null }""").Motion == ThemeMotion.Default);
        check("The night look can change the motion; a day look without one is untouched",
            PanelThemes.Build("n", PanelThemes.Parse("""{ "base": "github-dark", "night": { "motion": { "hover": "reveal" } } }"""), night: true).Motion.Hover == HoverFx.Reveal
            && PanelThemes.Build("n", PanelThemes.Parse("""{ "base": "github-dark", "night": { "motion": { "hover": "reveal" } } }""")).Motion.Hover == HoverFx.None);

        var nightSource = PanelThemes.Parse("""{ "base": "github-dark", "night": { "motion": { "hover": "reveal", "hoverStrength": 0.2 } } }""");
        var nightCopy = PanelThemes.Parse(PanelThemes.FillTemplate("copy", Palette("github-dark"), "github-dark", nightSource));
        check("Copying a theme keeps the night look's motion", nightCopy.Night?.Motion is { Hover: "reveal", HoverStrength: .2 }
            && PanelThemes.Build("c", nightCopy, night: true).Motion.Hover == HoverFx.Reveal);
        // A custom theme's copy is based on dark, so its day motion must be written out, not inherited.
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
        check("A theme without motion is copied without a motion section", PanelThemes.Parse(PanelThemes.FillTemplate("plain", Palette("dark"), "dark")).Motion is null);
        string Problem(string json)
        {
            var folder = Directory.CreateTempSubdirectory("brimdeck-motion").FullName;
            try { File.WriteAllText(Path.Combine(folder, "t.json"), json); return PanelThemes.LoadFolder(folder).Single().Error ?? ""; }
            finally { Directory.Delete(folder, true); }
        }
        // A bad motion value is a warning on the settings page, never a file error: that field falls back (to the base's, or to the
        // day look's for the night look), and everything else in the file, the other look's motion included, is used.
        static IReadOnlyList<string> Warnings(string json) => PanelThemes.Warnings(PanelThemes.Parse(json));
        check("An unknown motion.hover name is a warning, not a file error, in the day and the night look",
            Problem("""{ "id": "t", "motion": { "hover": "confetti" } }""") == "" && Warnings("""{ "id": "t", "motion": { "hover": "confetti" } }""").Single().Contains("confetti")
            && Problem("""{ "id": "t", "night": { "motion": { "hover": "2" } } }""") == "" && Warnings("""{ "id": "t", "night": { "motion": { "hover": "2" } } }""").Single().Contains('2'));
        check("Valid or blank motion.hover names are no problem and no warning", new[] { "reveal", "LIFT", "none", "", "  " }
            .All(name => Problem("{ \"id\": \"t\", \"motion\": { \"hover\": \"" + name + "\" } }") == "" && Warnings("{ \"motion\": { \"hover\": \"" + name + "\" } }").Count == 0));
        check("A motion value of the wrong type is a warning, and that field keeps its base's value",
            Problem("""{ "id": "t", "motion": { "hover": "lift", "hoverStrength": "strong" } }""") == ""
            && Custom("""{ "base": "github-dark", "motion": { "hover": "lift", "hoverStrength": "strong" } }""").Motion is { Hover: HoverFx.Lift } typed && typed.HoverStrength == ThemeMotion.Default.HoverStrength
            && Night("""{ "hover": "lift", "hoverStrength": "strong" }""") is { Hover: HoverFx.Lift } typedNight && typedNight.HoverStrength == own.HoverStrength
            && Warnings("""{ "motion": { "hover": "lift", "hoverStrength": "strong" } }""").Single().Contains("hoverStrength"));
        const string AllWrong = """{ "hover": 2, "hoverStrength": "0.2", "pace": "fast", "color": 5, "secondary": true }""";
        check("Every motion field of the wrong type is a warning of its own and keeps the base's value, a number written as text too",
            Night(AllWrong) == own && Warnings(WithNight(AllWrong)).Count == 5 && Problem(WithNight(AllWrong)) == "");
        check("A motion section that is not an object is a warning and no section",
            Night("\"fast\"") == own && Warnings("""{ "motion": [1] }""").Count == 1 && Problem("""{ "id": "t", "motion": 3 }""") == "");
        const string NightWrong = """{ "id": "t", "motion": { "hover": "lift", "hoverStrength": 0.2 }, "night": { "motion": { "hover": "confetti", "hoverStrength": "strong", "pace": 1.4 } } }""";
        var nightWrong = PanelThemes.Parse(NightWrong);
        check("Only the night look's motion wrong: the day look's motion loads, and the night keeps the day's for the bad fields",
            Problem(NightWrong) == "" && PanelThemes.Build("t", nightWrong).Motion is { Hover: HoverFx.Lift, HoverStrength: .2, Pace: 1 }
            && PanelThemes.Build("t", nightWrong, night: true).Motion is { Hover: HoverFx.Lift, HoverStrength: .2, Pace: 1.4 }
            && PanelThemes.Warnings(nightWrong) is { Count: 2 } nightWarnings && nightWarnings.All(w => w.StartsWith(Loc.T("夜间外观：", "Night look: "), StringComparison.Ordinal)));

        // ----- Colors -----
        var dark = Palette("dark");
        check("A color key resolves to the palette's color; an unknown name falls back to the accent; a hex color is used",
            ThemeMotion.ResolveColor(dark, "accent") == ThemeMotion.ResolveColor(dark, "ACCENT") && ThemeMotion.ResolveColor(dark, "no-such-key") == ThemeMotion.ResolveColor(dark, "accent")
            && ThemeMotion.ResolveColor(dark, "#FFFFFF").R == 255);
        check("A dim color is moved just far enough to read on the surface", ThemeMotion.ResolveColor(dark, "#101010").MinimumContrast([dark.Surface]) >= PanelThemes.GraphicContrast);

        // ----- When anything may draw -----
        var reveal = ThemeMotion.Default with { Hover = HoverFx.Reveal };
        check("The mark draws only when animations are allowed and the theme has one",
            MotionGate.Interaction(true, reveal) && !MotionGate.Interaction(false, reveal) && !MotionGate.Interaction(true, ThemeMotion.Default)
            && !MotionGate.Interaction(true, reveal with { HoverStrength = 0 }));
        check("Allowed is the app-wide rule: BrimDeck's setting, Windows' effects, no high contrast",
            MotionConditions.Allowed(true, true, false) && !MotionConditions.Allowed(false, true, false) && !MotionConditions.Allowed(true, false, false) && !MotionConditions.Allowed(true, true, true));

        // ----- How strongly a mark draws -----
        double[] strengths = [.. Enumerable.Range(0, 301).Select(i => i / 1000.0)];
        var highlight = strengths.Select(strength => HoverMarkOpacity.LiftHighlight(strength, 1)).ToArray();
        check("The lift's top highlight fades with the strength: nothing at zero, faint at 0.01, its full 0.9 only at the strongest",
            highlight[0] == 0 && HoverMarkOpacity.LiftHighlight(.01, 1) <= .05 && Near(HoverMarkOpacity.LiftHighlight(MotionSafety.HoverMax, 1), .9)
            && highlight.Zip(highlight.Skip(1)).All(pair => pair.Second >= pair.First && pair.Second - pair.First <= .01));
        check("The highlight follows the mark's presence, and a negative strength draws nothing",
            Near(HoverMarkOpacity.LiftHighlight(.2, .5), HoverMarkOpacity.LiftHighlight(.2, 1) / 2) && HoverMarkOpacity.LiftHighlight(.2, 0) == 0 && HoverMarkOpacity.LiftHighlight(-1, 1) == 0);

        // ----- Timing -----
        var normal = MotionTiming.For(1);
        check("At pace 1 a mark arrives in the monitor's hover time and leaves in seven tenths of it",
            normal.HoverIn == (int)Math.Round(Eased.Hover * 1000) && normal.HoverOut == (int)Math.Round(normal.HoverIn * Eased.HoverOut / Eased.Hover) && normal.HoverOut < normal.HoverIn);
        var quick = MotionTiming.For(.75);
        check("A quicker pace is quicker, and a pace outside the range is held", quick.HoverIn < normal.HoverIn && MotionTiming.For(100) == MotionTiming.For(MotionSafety.PaceMax)
            && MotionTiming.For(0) == MotionTiming.For(MotionSafety.PaceMin) && MotionTiming.For(double.NaN) == normal);
        check("The longest mark is under half a second", MotionTiming.For(MotionSafety.PaceMax) is { HoverIn: <= 450, HoverOut: <= 315, Glide: <= 600 });

        // ----- The mark over time, on an injected clock -----
        var line = new HoverTimeline();
        check("A mark never shown shows nothing and is not moving", line.Presence(5, normal) == 0 && !line.Moving(5, normal));
        line.Arrive(10, 0);
        double inMs = normal.HoverIn / 1000.0, last = 0; bool rising = true;
        for (int i = 0; i <= 100; i++) { double v = line.Presence(10 + inMs * i / 100, normal); rising &= v >= last; last = v; }
        check("It arrives from nothing to whole in the hover time, only ever rising", line.Presence(10, normal) == 0 && Near(line.Presence(10 + inMs, normal), 1) && rising);
        check("It is moving while it arrives and still once it has", line.Moving(10 + inMs / 2, normal) && !line.Moving(10 + inMs + .001, normal) && line.Presence(20, normal) == 1);
        double outMs = normal.HoverOut / 1000.0;
        line.Leave(20, line.Presence(20, normal));
        check("It leaves from whole to nothing in the leaving time and is gone exactly then",
            line.Leaving && line.Presence(20, normal) == 1 && line.Presence(20 + outMs / 2, normal) is > 0 and < 1 && line.Presence(20 + outMs, normal) == 0 && !line.Moving(20 + outMs, normal) && line.Moving(20 + outMs / 2, normal));
        var half = new HoverTimeline(); half.Arrive(0, 0); double mid = half.Presence(inMs / 2, normal);
        half.Leave(inMs / 2, mid);
        check("Leaving part way starts from where it was, never from whole", Near(half.Presence(inMs / 2, normal), mid) && half.Presence(inMs / 2 + outMs, normal) == 0);
        var back = new HoverTimeline(); back.Leave(0, .6); double fading = back.Presence(outMs / 3, normal);
        back.Arrive(outMs / 3, fading);
        check("Coming back to a fading mark goes on from where it is: no jump down, then up to whole",
            Near(back.Presence(outMs / 3, normal), fading) && !back.Leaving && Near(back.Presence(outMs / 3 + inMs, normal), 1) && back.Presence(outMs / 3 + inMs / 2, normal) > fading);
        var clamped = new HoverTimeline(); clamped.Arrive(0, 7); clamped.Leave(0, -3);
        check("A start outside 0 to 1 is held to it", clamped.ShownFrom == 1 && clamped.LeftFrom == 0);
        check("A time before the mark arrived shows nothing more than its start", new HoverTimeline().Presence(-5, normal) == 0 && MotionCurve.Linear(-1, .1) == 0 && MotionCurve.Linear(1, 0) == 1);
    }
}
