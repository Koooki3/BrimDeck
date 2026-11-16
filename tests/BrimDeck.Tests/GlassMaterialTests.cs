using BrimDeck.Core;
using System.Text.Json;

internal static class GlassMaterialTests
{
    public static void Run(Action<string, bool> check)
    {
        check("Glass transparency: default keeps the current collapsed and expanded veil",
            GlassMaterial.LuminosityOpacity(0, 0, 50) == .30 && GlassMaterial.LuminosityOpacity(0, 1, 50) == .35);
        check("Glass transparency: the clear end keeps a veil and the ends are distinct",
            GlassMaterial.LuminosityOpacity(0, 0, 100) == .20 && GlassMaterial.LuminosityOpacity(0, 0, 0) == .55);
        check("Glass transparency: the readability veil takes priority even at full clarity",
            Enumerable.Range(0, 101).All(level => GlassMaterial.LuminosityOpacity(.87, .5, level) == .90));
        check("Glass transparency: the veil never thickens as the slider moves toward clear",
            Enumerable.Range(0, 100).All(level => GlassMaterial.LuminosityOpacity(.25, .35, level + 1)
                <= GlassMaterial.LuminosityOpacity(.25, .35, level)));
        check("Glass transparency: clearer glass softens the grain and retains the rim",
            GlassMaterial.GrainStrength(0) == 1.5 && GlassMaterial.GrainStrength(50) == 1
            && GlassMaterial.GrainStrength(100) == .5 && GlassMaterial.RimStrength(100, 0) > 1);
        check("Glass transparency: the rim softens through expansion and stays visible",
            GlassMaterial.RimStrength(50, 0) == 1 && GlassMaterial.RimStrength(50, 1) == .8
            && GlassMaterial.RimStrength(0, 1) > 0);
        check("Liquid glass advertises Beta separately from the stable theme name",
            new[] { PanelThemes.GlassLight, PanelThemes.GlassDark }.All(id =>
                PanelThemes.BuiltIn(id) is { File.Stage: "Beta" } theme
                && !theme.File.Name.EndsWith("alpha", StringComparison.OrdinalIgnoreCase)
                && !theme.File.NameEn.EndsWith("alpha", StringComparison.OrdinalIgnoreCase)));
        var settings = JsonSerializer.Deserialize<DeckSettings>("{}")!;
        check("Glass transparency: older settings use the current look", settings.GlassTransparency == 50);
        settings.GlassTransparency = 101;
        settings.Normalize();
        var copy = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(settings))!;
        check("Glass transparency: settings are clamped and survive a save", copy.GlassTransparency == 100);
    }
}
