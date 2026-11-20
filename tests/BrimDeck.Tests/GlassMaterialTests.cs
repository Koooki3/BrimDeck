using BrimDeck.Core;
using System.Text.Json;

internal static class GlassMaterialTests
{
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    public static void Run(Action<string, bool> check)
    {
        check("Glass transparency: default keeps the current collapsed and expanded veil",
            Near(GlassMaterial.LuminosityOpacity(0, 0, 50), .30) && Near(GlassMaterial.LuminosityOpacity(0, 1, 50), .35));
        check("Glass transparency: the clear end keeps a veil and the ends are distinct",
            Near(GlassMaterial.LuminosityOpacity(0, 0, 100), .20) && Near(GlassMaterial.LuminosityOpacity(0, 0, 0), .55));
        check("Glass transparency: the readability veil takes priority even at full clarity, without rounding to a step",
            Enumerable.Range(0, 101).All(level => GlassMaterial.LuminosityOpacity(.87, .5, level) == .87));
        check("Glass transparency: the veil never thickens as the slider moves toward clear",
            Enumerable.Range(0, 100).All(level => GlassMaterial.LuminosityOpacity(.25, .35, level + 1)
                <= GlassMaterial.LuminosityOpacity(.25, .35, level)));
        check("Clearer glass relaxes the dark limit only above the default preference",
            GlassMaterial.DarkBackdropLimit(-1) == .26 && GlassMaterial.DarkBackdropLimit(50) == .26
            && Math.Abs(GlassMaterial.DarkBackdropLimit(75) - .29) < 1e-9
            && Math.Abs(GlassMaterial.DarkBackdropLimit(101) - .32) < 1e-9);
        check("Glass transparency: clearer glass softens the grain and retains the rim",
            GlassMaterial.GrainStrength(0) == 1.5 && GlassMaterial.GrainStrength(50) == 1
            && GlassMaterial.GrainStrength(100) == .5 && GlassMaterial.RimStrength(100, 0) > 1);
        check("Glass transparency: the rim softens through expansion and stays visible",
            GlassMaterial.RimStrength(50, 0) == 1 && GlassMaterial.RimStrength(50, 1) == .8
            && GlassMaterial.RimStrength(0, 1) > 0);
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
        check("Glass transparency: older settings use the current look", settings.GlassTransparency == 50);
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
