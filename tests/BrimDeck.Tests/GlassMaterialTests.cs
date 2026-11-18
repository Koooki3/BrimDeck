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
        double flash = GlassMaterial.FollowBackdrop(.40, .90, .40);
        double steady = GlassMaterial.FollowBackdrop(flash, .90, .90);
        double firstCalm = GlassMaterial.FollowBackdrop(steady, .30, .90);
        check("Glass veil responds gently to one frame and reaches the confirmed floor on the next",
            Math.Abs(flash - .425) < 1e-9 && steady == .90);
        check("An unusually high first reading cannot lift the veil above the next reading",
            GlassMaterial.FollowBackdrop(flash, .60, .90) == .60
            && GlassMaterial.FollowBackdrop(flash, .90, .50) == .50);
        check("Glass veil holds through an alternating frame and releases on consecutive calmer frames",
            firstCalm == steady && Math.Abs(GlassMaterial.FollowBackdrop(firstCalm, .30, .30) - .875) < 1e-9);
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
        foreach (var id in new[] { PanelThemes.GlassLight, PanelThemes.GlassDark })
        {
            var previous = new ThemeChoices();
            var legacy = new DeckSettings { PanelTheme = id, GlassTransparency = 67, ThemeChoices = new() { [id] = previous } };
            legacy.Normalize();
            legacy.ThemeChoices.TryGetValue(PanelThemes.GlassAuto, out var migrated);
            check($"Legacy glass selection {id} becomes unified and preserves its preferences",
                legacy.PanelTheme == PanelThemes.GlassAuto && legacy.GlassTransparency == 67
                && legacy.ThemeChoices.ContainsKey(id) && migrated is not null
                && !ReferenceEquals(previous, migrated));
            legacy.Normalize();
            check($"Unified glass settings migration is idempotent for {id}",
                legacy.ThemeChoices.TryGetValue(PanelThemes.GlassAuto, out var repeated) && ReferenceEquals(repeated, migrated));
        }
    }
}
