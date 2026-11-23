using BrimDeck.Core;
using System.Text.Json;

internal static class GlassMaterialTests
{
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    public static void Run(Action<string, bool> check)
    {
        check("Glass clarity: the default keeps the collapsed and expanded veil",
            Near(GlassMaterial.LuminosityOpacity(0, 0, 50), .30) && Near(GlassMaterial.LuminosityOpacity(0, 1, 50), .35));
        check("Glass clarity: the clear end keeps a thin veil and the ends are far apart",
            Near(GlassMaterial.LuminosityOpacity(0, 0, 100), .02) && Near(GlassMaterial.LuminosityOpacity(0, 0, 0), .82));
        check("Glass clarity: the text's readability is weighed by the clarity (whole at 0, half at 50, none at 100)",
            Near(GlassMaterial.LuminosityOpacity(.87, 0, 0), .87) && Near(GlassMaterial.LuminosityOpacity(.87, 0, 50), (.87 + .30) / 2)
            && Near(GlassMaterial.LuminosityOpacity(.87, 0, 100), .02));
        check("Glass clarity: forced readability takes the text's veil at every clarity",
            Enumerable.Range(0, 101).All(level => GlassMaterial.LuminosityOpacity(.87, .5, level, force: true) >= .87 - 1e-12));
        check("Glass clarity: the veil never thickens as the slider moves toward clear",
            Enumerable.Range(0, 100).All(level => GlassMaterial.LuminosityOpacity(.25, .35, level + 1)
                <= GlassMaterial.LuminosityOpacity(.25, .35, level) + 1e-12));
        check("Glass clarity: the text's own limits do not move with the slider",
            GlassRecipe.DarkMost == .26 && GlassRecipe.LightLeast == .82);
        // The rim against the veil the clarity really gives (GlassMaterial.LuminosityOpacity) for a backdrop that asks for nothing,
        // some or much of the text's veil, closed and open: 0/50/100 and every step between.
        foreach (double openness in new[] { 0d, .5, 1d })
            foreach (double required in new[] { 0d, .5, .87 })
            {
                double Rim(int clarity) => GlassMaterial.RimStrength(clarity, openness, GlassMaterial.LuminosityOpacity(required, openness, clarity));
                double[] s = [Rim(0), Rim(50), Rim(100)];
                double open = 1 - .2 * openness;
                check($"Glass clarity: the rim never brightens as the glass clears (0/50/100, openness {openness}, text asks {required}: {s[0]:0.000}/{s[1]:0.000}/{s[2]:0.000})",
                    s[1] <= s[0] + 1e-12 && s[2] <= s[1] + 1e-12 && Enumerable.Range(0, 100).All(c => Rim(c + 1) <= Rim(c) + 1e-12));
                check($"Glass clarity: clear glass keeps the rim's floor and no more (openness {openness}, text asks {required}: {s[2]:0.000})",
                    Math.Abs(s[2] - GlassMaterial.RimFloor * open) < 1e-9);
            }
        check("Glass clarity: the default glass keeps its rim, softened through the opening",
            GlassMaterial.RimStrength(50, 0, GlassMaterial.DefaultCollapsedVeil) == 1 && Near(GlassMaterial.RimStrength(50, 1, GlassMaterial.DefaultExpandedVeil), .8));
        check("Glass clarity: a thinner body dims the rim with it, down to the floor",
            GlassMaterial.RimStrength(50, 0, .15) < 1 && GlassMaterial.RimStrength(50, 0, .15) > GlassMaterial.RimStrength(50, 0, .05)
            && Near(GlassMaterial.RimStrength(50, 0, 0), GlassMaterial.RimFloor) && GlassMaterial.RimStrength(50, 0, double.NaN) == 1);
        check("A reading that needs more thickens the floor to it at once",
            GlassMaterial.Settle(.90, .40, .40) == .90 && GlassMaterial.Settle(.60, .90, .40) == .60);
        check("One calmer reading does not clear the glass; two in a row release it to the higher of the two",
            GlassMaterial.Settle(.30, .90, .90) == .90 && GlassMaterial.Settle(.30, .30, .90) == .30
            && GlassMaterial.Settle(.30, .50, .90) == .50);
        check("Releasing never thickens and a broken reading changes nothing",
            GlassMaterial.Settle(.30, .95, .60) == .60 && GlassMaterial.Settle(double.NaN, .3, .55) == .55
            && GlassMaterial.Settle(.4, double.NaN, .6) == .6);
        check("The material floor is continuous across the opening",
            Enumerable.Range(0, 100).Select(i => GlassMaterial.LuminosityOpacity(0, i / 99d, 50)).Distinct().Count() > 50);
        foreach (string id in new[] { PanelThemes.GlassLight, PanelThemes.GlassDark })
        {
            var theme = PanelThemes.BuiltIn(id)!;
            var palette = PanelThemes.Resolve(theme);
            check($"Liquid Glass: {id} declares the material and keeps its opaque fallback",
                string.IsNullOrEmpty(theme.File.Stage) && palette.Effects.Glass is { Held: true }
                && palette.Colors["surface"].A == 255);
            check($"Liquid Glass: arbitrary theme files cannot opt into {id}'s glass material",
                PanelThemes.Build(id, theme.File).Effects.Glass is null);
        }
        var settings = JsonSerializer.Deserialize<DeckSettings>("{}")!;
        check("Glass clarity: older settings keep the default 50", settings.GlassTransparency == 50);
        settings.GlassTransparency = 101;
        settings.Normalize();
        var copy = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(settings))!;
        check("Glass transparency: settings are clamped and survive a save", copy.GlassTransparency == 100);
        foreach (string id in new[] { PanelThemes.GlassLight, PanelThemes.GlassDark })
        {
            var legacy = new DeckSettings
            {
                PanelTheme = id, GlassTransparency = 67,
                ThemeChoices = new() { [id] = new ThemeChoices { ClockCustomColor = "#123456" } }
            };
            legacy.Normalize();
            check($"Legacy glass selection {id} migrates to the unified entry",
                legacy.PanelTheme == PanelThemes.GlassAuto && legacy.GlassTransparency == 67
                && legacy.ThemeChoices.ContainsKey(id)
                && legacy.ThemeChoices.TryGetValue(PanelThemes.GlassAuto, out var migrated) && migrated.ClockCustomColor == "#123456");
        }
    }
}
