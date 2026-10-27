using BrimDeck.Core;

// The Endfield-inspired theme and the effects it is made of: cut corners, the signal bar and the hatching, the stage badge and its
// two looks that follow Windows.
internal static class EndfieldTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        var theme = PanelThemes.BuiltIn(PanelThemes.Endfield)!;
        var day = PanelThemes.Resolve(theme, DaylightLook.Day);
        var night = PanelThemes.Resolve(theme, DaylightLook.Night);
        check("Endfield is an extension theme named without a stage, which is a badge of its own",
            theme.Kind == PanelThemeKind.Extension && theme.File.Stage == "alpha" && !theme.File.Name.Contains("alpha", StringComparison.OrdinalIgnoreCase)
            && !theme.File.NameEn.Contains("alpha", StringComparison.OrdinalIgnoreCase) && theme.File.Description.Contains("非官方") && theme.File.DescriptionEn.Contains("unofficial"));
        check("Its day look is paper and its night look charcoal, both read as written (no adjustment)",
            !day.IsDark && night.IsDark && day.Adjustments.Count == 0 && night.Adjustments.Count == 0 && day.Surface == ThemeColor.Parse("#FAFAFA") && night.Surface == ThemeColor.Parse("#2B2B2B"));
        check("The looks follow Windows, not the clock",
            PanelThemes.DaylightModeOf(theme.File) == DaylightMode.System && PanelThemes.LookAt(theme.File, new DateTime(2026, 10, 27, 3, 0, 0), appsLight: true) == DaylightLook.Day
            && PanelThemes.LookAt(theme.File, new DateTime(2026, 10, 27, 12, 0, 0), appsLight: false) == DaylightLook.Night && PanelThemes.NextDaylightChange(theme.File, DateTime.Now) is null);
        check("Cut corners and the two marks are in both looks, in the look's signal and a faint hatching",
            day.Effects.Cut == 10 && night.Effects.Cut == 10 && day.Effects.Pixel == 0 && day.Effects.Glass is null && day.Effects.SurfaceGradient.Count == 0 && day.Effects.Scanlines == 0
            && day.Effects.Decor is { Tab: { } dayTab, Hatch: { } dayHatch, Horizon: null, Hud: null } && night.Effects.Decor is { Tab: { } nightTab, Hatch: { } nightHatch }
            && dayTab == ThemeColor.Parse("#FFFA00") && nightTab == night["accent"] && dayHatch.A < 80 && nightHatch.A < 80 && dayHatch != nightHatch);
        check("Signal yellow is a fill on paper (the selected segment, the indicator, ink on it); on graphite the signal is the HUD's yellow-green, a line and text",
            day["fillSelected"] == ThemeColor.Parse("#FFFA00") && day["indicator"] == day["fillSelected"] && day["textPrimary"].MinimumContrast([day["fillSelected"]]) >= 7
            && day["accent"] == day["textPrimary"] && day["onAccent"] == ThemeColor.Parse("#FFFA00") && night["accent"] == ThemeColor.Parse("#C6CA4C") && night["indicator"] == night["accent"] && night["accent"].MinimumContrast(night.SurfaceStops) >= 7
            && day["indicatorProgress"].MinimumContrast([day["indicator"]]) >= 7);
        check("The clock is the wide face in both looks, and offers five readable preset gradients each",
            day.Clock is { Style: "wide" } && night.Clock is { Style: "wide" } && day.ClockGradients.Count == 5 && night.ClockGradients.Count == 5
            && day.ClockGradients.SelectMany(g => g.Stops).All(stop => ThemeColor.Parse(stop).MinimumContrast(day.SurfaceStops) >= PanelThemes.TextContrast)
            && night.ClockGradients.SelectMany(g => g.Stops).All(stop => ThemeColor.Parse(stop).MinimumContrast(night.SurfaceStops) >= PanelThemes.TextContrast));
        check("It moves mechanically and costs nothing at rest: no ambient light, no particles, no steps, firm springs, brackets on focus",
            day.Motion is { Hover: HoverFx.Caret, Press: PressFx.Shade, Particles: ParticleFx.None, Steps: 0, Spring: SpringTier.Firm, Focus: FocusFx.Brackets } m && !m.Ambient.Any && night.Motion == day.Motion);

        // The cut effect: a range, an effect by name, ruled out by pixel art and by glass.
        PanelThemeEffectValues Cut(double? size, double? pixel = null, PanelThemeGlass? glass = null) => PanelThemes.Build("c", new PanelThemeFile { Effects = new() { Cut = size, Pixel = pixel, Glass = glass } }).Effects;
        check("The cut is 0 to 16 DIP, unset or not a number is none",
            Cut(8).Cut == 8 && Cut(99).Cut == 16 && Cut(-3).Cut == 0 && Cut(double.NaN).Cut == 0 && Cut(null).Cut == 0 && PanelThemes.EffectRange("cut") == (0, 16, 0));
        check("Pixel art wins over cut corners, and glass gives way to them",
            Cut(8, pixel: 2).Cut == 0 && Cut(8, pixel: 2).Pixel == 2
            && Cut(8, glass: new PanelThemeGlass { Tint = "#80FFFFFF" }) is { Cut: 8, Glass: null });
        var folder = Path.Combine(root, "endfield-files");
        Directory.CreateDirectory(folder);
        foreach (var (name, json) in new[]
        {
            ("both.json", """{ "formatVersion": 1, "id": "both", "effects": { "cut": 6, "glass": { "tint": "#80FFFFFF" } } }"""),
            ("cut-on-glass.json", """{ "formatVersion": 1, "id": "cut-on-glass", "base": "liquid-glass-dark", "effects": { "cut": 6 } }"""),
            ("glass-on-cut.json", """{ "formatVersion": 1, "id": "glass-on-cut", "base": "endfield-industrial", "effects": { "glass": { "tint": "#80FFFFFF" } } }"""),
            ("fine.json", """{ "formatVersion": 1, "id": "fine", "base": "endfield-industrial", "effects": { "cut": 4 } }"""),
            ("flat-on-glass.json", """{ "formatVersion": 1, "id": "flat-on-glass", "base": "liquid-glass-dark", "effects": { "cut": 0 } }"""),
            ("bad.json", """{ "formatVersion": 1, "id": "bad", "effects": { "decor": { "hatch": { "color": "red" } } } }"""),
        }) System.IO.File.WriteAllText(Path.Combine(folder, name), json);
        var files = PanelThemes.LoadFolder(folder);
        PanelThemeInfo Loaded(string name) => files.Single(theme => Path.GetFileName(theme.Path) == name);
        check("A file asking for glass and cut corners is refused, from the file or its base; cut 0 on glass is fine, as with pixel art",
            new[] { "both.json", "cut-on-glass.json", "glass-on-cut.json" }.All(name => Loaded(name).Error is not null) && Loaded("both.json").Error!.Contains("cut")
            && Loaded("fine.json").Error is null && Loaded("flat-on-glass.json").Error is null);
        check("A theme built on Endfield keeps its cuts and marks, drops or changes them one by one",
            PanelThemes.Build("kid", new PanelThemeFile { Base = PanelThemes.Endfield }).Effects is { Cut: 10, Decor: { Tab: not null, Hatch: not null } }
            && PanelThemes.Build("kid", new PanelThemeFile { Base = PanelThemes.Endfield, Effects = new() { Cut = 0 } }).Effects.Cut == 0
            && PanelThemes.Build("kid", new PanelThemeFile { Base = PanelThemes.Endfield, Effects = new() { Decor = new() { Tab = new() { Color = "#00000000" } } } }).Effects.Decor is { Tab: null, Hatch: not null }
            && PanelThemes.Build("kid", new PanelThemeFile { Base = PanelThemes.Endfield, Effects = new() { Decor = new() { Hatch = new() { Color = "#FF0000FF" } } } }).Effects.Decor is { Tab: not null, Hatch: { } hatch } && hatch == ThemeColor.Parse("#FF0000FF"));
        check("The marks need a color of their own", PanelThemes.Build("none", new PanelThemeFile { Effects = new() { Decor = new() { Tab = new() { Color = null }, Hatch = new() } } }).Effects.Decor is null);
        check("A bad mark color makes the file unusable", Loaded("bad.json").Error is not null);

        // The template and copies.
        check("The template lists cut, off", PanelThemes.Parse(PanelThemes.Template).Effects is { Cut: 0 } && PanelThemes.Build("t", PanelThemes.Parse(PanelThemes.Template)).Effects.Cut == 0);
        var copy = PanelThemes.Build("copy", PanelThemes.Parse(PanelThemes.FillTemplate("copy", day, PanelThemes.Endfield)));
        check("A theme made from Endfield writes out its cut and both marks",
            copy.Effects is { Cut: 10, Decor: { Tab: not null, Hatch: not null } } && copy.Effects.Decor!.Tab == day.Effects.Decor!.Tab && copy.Effects.Decor.Hatch == day.Effects.Decor.Hatch && copy.Adjustments.Count == 0);
    }
}
