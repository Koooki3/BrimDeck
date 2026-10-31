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
            !day.IsDark && night.IsDark && day.Adjustments.Count == 0 && night.Adjustments.Count == 0 && day.Surface == ThemeColor.Parse("#FAFAFA") && night.Surface == ThemeColor.Parse("#121212"));
        // All three text levels are held to AAA (7:1), not only the primary: on the surface and on the pop-up, in both looks.
        check("Every text level reads at 7:1 on the surface and the pop-up in both looks, and the deep look is deep",
            new[] { day, night }.All(look => new[] { "textPrimary", "textSecondary", "textTertiary" }.All(key => look[key].MinimumContrast([.. look.SurfaceStops, look["popup"].Over(look.Surface)]) >= 7))
            && night.Surface.Luminance < .01 && night["popup"].Over(night.Surface).Luminance < .014 && night["accent"].MinimumContrast(night.SurfaceStops) >= 9);
        check("The signal is spent sparingly: the clock and the borders are neutral in the deep look, and the frame is a faint hairline",
            night["clock"].MinimumContrast(night.SurfaceStops) >= 12 && night["clock"].R == night["clock"].B && night["bubbleBorder"].R == night["bubbleBorder"].B
            && night["rim"].R == night["rim"].B && night["popupBorder"].R == night["popupBorder"].B && night.Effects.Decor is { Frame: { A: < 80 } } && day.Effects.Decor is { Frame: { A: < 80 } });
        check("The looks follow Windows, not the clock",
            PanelThemes.DaylightModeOf(theme.File) == DaylightMode.System && PanelThemes.LookAt(theme.File, new DateTime(2026, 10, 27, 3, 0, 0), appsLight: true) == DaylightLook.Day
            && PanelThemes.LookAt(theme.File, new DateTime(2026, 10, 27, 12, 0, 0), appsLight: false) == DaylightLook.Night && PanelThemes.NextDaylightChange(theme.File, DateTime.Now) is null);
        check("Cut corners and the marks are in both looks: on paper a faint hatching, on graphite a hazard strip, the same yellow bar",
            day.Effects.Cut == 10 && night.Effects.Cut == 10 && day.Effects.Pixel == 0 && day.Effects.Glass is null && day.Effects.SurfaceGradient.Count == 0 && day.Effects.Scanlines == 0
            && day.Effects.Decor is { Tab: { } dayTab, Hatch: { } dayHatch, Frame: not null, Diamond: null, Cross: null, Horizon: null, Hud: null } && night.Effects.Decor is { Tab: { } nightTab, Hatch: { } nightHatch, Diamond: null, Cross: null }
            && dayTab == ThemeColor.Parse("#FFD400") && nightTab == dayTab && dayHatch.A < 80 && nightHatch.A > 150 && dayHatch != nightHatch);
        check("Signal yellow is a fill on paper (the selected segment, the indicator, ink on it); on graphite the signal is the HUD's yellow-green, a line and text",
            day["fillSelected"] == ThemeColor.Parse("#FFD400") && day["indicator"] == day["fillSelected"] && night["indicator"] == day["indicator"] && day["textPrimary"].MinimumContrast([day["fillSelected"]]) >= 7
            && day["accent"] == day["textPrimary"] && day["onAccent"] == ThemeColor.Parse("#FFD400") && night["accent"] == ThemeColor.Parse("#C6CA4C") && night["accent"].MinimumContrast(night.SurfaceStops) >= 7
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
        check("A theme made from Endfield writes out its cut and its three marks",
            copy.Effects is { Cut: 10, Decor: { Tab: not null, Hatch: not null, Frame: not null } } && copy.Effects.Decor!.Tab == day.Effects.Decor!.Tab && copy.Effects.Decor.Hatch == day.Effects.Decor.Hatch && copy.Effects.Decor.Frame == day.Effects.Decor.Frame && copy.Adjustments.Count == 0);

        // The liquid motion's notch follows the cut outline: slanted shoulders and cut bottom corners, with unit normals, and the round
        // outline is unchanged without a cut.
        static (bool OnCut, bool Unit, bool RoundArc) Outline(double cut)
        {
            double w = 376, h = 52, shoulder = 16, radius = 20, right = w - shoulder, leg = 10;
            var liquid = new BrimDeck.Core.Notices.IslandLiquid(BrimDeck.Core.Notices.IslandForm.Notch, w, h, corner: radius, shoulder: shoulder, cut: cut);
            bool onCut = true, unit = true, arc = false;
            foreach (var p in liquid.Outline)
            {
                unit &= Math.Abs(Math.Sqrt(p.Nx * p.Nx + p.Ny * p.Ny) - 1) < 1e-6;
                // The bottom right corner's points lie on the cut line x + y = right + h - leg, or, round, off it.
                if (p.X > right - leg + 1e-6 && p.Y > h - leg + 1e-6 && p.X < right + 1e-6)
                {
                    double off = Math.Abs(p.X + p.Y - (right + h - leg));
                    onCut &= off < 1e-6; arc |= off > .3;
                }
            }
            return (onCut, unit, arc);
        }
        var cutOutline = Outline(10); var roundOutline = Outline(0);
        check("The liquid motion's notch runs along the cut outline: its bottom corners on the 45-degree line, normals of unit length",
            cutOutline.OnCut && cutOutline.Unit && !cutOutline.RoundArc);
        check("Without a cut the notch keeps its round corners", roundOutline.Unit && roundOutline.RoundArc);
    }
}
