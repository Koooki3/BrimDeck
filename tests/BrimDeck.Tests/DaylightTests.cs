using BrimDeck.Core;

// Themes with a night look: the daylight mode (clock or Windows' dark and light mode), the dusk look and its hours, and how
// both survive validation, the template and the settings.
internal static class DaylightTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        var day0 = new DateTime(2026, 10, 24);
        PanelThemeFile Theme(string mode = "", string day = "06:30", string night = "18:30", int? dusk = null, bool withDusk = true) => new()
        {
            Base = "dark",
            Night = new PanelThemeFile { Colors = new() { ["surface"] = "#0B1622" } },
            Dusk = withDusk ? new PanelThemeFile { Colors = new() { ["surface"] = "#2A1A2E" } } : null,
            Daylight = new PanelThemeDaylight { Mode = mode, Day = day, Night = night, DuskMinutes = dusk }
        };
        DaylightLook At(PanelThemeFile file, int hour, int minute, bool appsLight = true, string? setting = null, int addDays = 0)
            => PanelThemes.LookAt(file, day0.AddDays(addDays).AddHours(hour).AddMinutes(minute), appsLight, setting);
        DateTime? Next(PanelThemeFile file, int hour, int minute, string? setting = null) => PanelThemes.NextDaylightChange(file, day0.AddHours(hour).AddMinutes(minute), setting);

        // Mode parsing and validation.
        check("Daylight modes parse: empty and clock go by the clock, system follows Windows, anything else is unknown",
            PanelThemes.ParseDaylightMode("") == DaylightMode.Clock && PanelThemes.ParseDaylightMode("clock") == DaylightMode.Clock
            && PanelThemes.ParseDaylightMode(" System ") == DaylightMode.System && PanelThemes.ParseDaylightMode("auto") is null);
        var folder = Path.Combine(root, "daylight");
        Directory.CreateDirectory(folder);
        const string night = "\"night\": { \"colors\": { \"surface\": \"#0B1622\" } }";
        foreach (var (name, json) in new[]
        {
            ("sys-mode", $$"""{ "id": "sys-mode", "daylight": { "mode": "system" }, {{night}} }"""),
            ("dusk", $$"""{ "id": "dusk", "daylight": { "mode": "clock", "day": "07:00", "night": "19:00", "duskMinutes": 90 }, {{night}}, "dusk": { "colors": { "surface": "#2A1A2E" } } }"""),
            ("bad-mode", $$"""{ "id": "bad-mode", "daylight": { "mode": "auto" }, {{night}} }"""),
            ("short-dusk", $$"""{ "id": "short-dusk", "daylight": { "duskMinutes": 10 }, {{night}}, "dusk": { "colors": {} } }"""),
            ("long-dusk", $$"""{ "id": "long-dusk", "daylight": { "duskMinutes": 181 }, {{night}}, "dusk": { "colors": {} } }"""),
            ("bad-dusk", $$"""{ "id": "bad-dusk", {{night}}, "dusk": { "colors": { "surface": "dusky" } } }"""),
            ("lonely-dusk", """{ "id": "lonely-dusk", "dusk": { "colors": { "surface": "#2A1A2E" } } }""")
        }) File.WriteAllText(Path.Combine(folder, name + ".json"), json);
        var loaded = PanelThemes.LoadFolder(folder).ToDictionary(theme => PanelThemes.FileName(theme.Path!));
        check("A system or clock mode with a dusk look is a usable file", loaded["sys-mode"].Error is null && loaded["dusk"].Error is null
            && loaded["dusk"].File is { Dusk: not null, Daylight: { Mode: "clock", DuskMinutes: 90 } });
        check("An unknown mode, a dusk outside 15 to 180 minutes, a bad dusk color or a dusk without a night look is a file error",
            new[] { "bad-mode", "short-dusk", "long-dusk", "bad-dusk", "lonely-dusk" }.All(name => loaded[name] is { Usable: false, Error.Length: > 0 }));

        // System mode: Windows' app mode decides, whatever the hour, and there is no dusk and no timer.
        var system = Theme(mode: "system");
        check("Following Windows shows the day look in light mode and the night look in dark mode, never dusk",
            At(system, 23, 0, appsLight: true) == DaylightLook.Day && At(system, 12, 0, appsLight: false) == DaylightLook.Night
            && At(system, 18, 0, appsLight: true) == DaylightLook.Day && Next(system, 12, 0) is null);
        var clock = Theme();
        check("The setting chooses the mode over the theme's: \"system\" follows Windows, hours go by the clock",
            At(clock, 12, 0, appsLight: false, setting: "system") == DaylightLook.Night && Next(clock, 12, 0, "system") is null
            && At(system, 12, 0, appsLight: false, setting: "07:00-19:00") == DaylightLook.Day && At(system, 20, 0, setting: "07:00-19:00") == DaylightLook.Night
            && PanelThemes.DaylightModeOf(system, "") == DaylightMode.System && PanelThemes.DaylightModeOf(clock, "") == DaylightMode.Clock);
        check("Windows' mode means nothing to a theme going by the clock", At(clock, 12, 0, appsLight: false) == DaylightLook.Day);

        // Dusk by the clock: the last hour of the day by default.
        check("Dusk takes the hour before the night", At(clock, 17, 29) == DaylightLook.Day && At(clock, 17, 30) == DaylightLook.Dusk
            && At(clock, 18, 29) == DaylightLook.Dusk && At(clock, 18, 30) == DaylightLook.Night && At(clock, 6, 29) == DaylightLook.Night && At(clock, 6, 30) == DaylightLook.Day);
        check("The next change includes the start of dusk", Next(clock, 12, 0) == day0.AddHours(17.5) && Next(clock, 17, 45) == day0.AddHours(18.5)
            && Next(clock, 19, 0) == day0.AddDays(1).AddHours(6.5) && Next(clock, 17, 30) == day0.AddHours(18.5));
        var noDusk = Theme(dusk: 90, withDusk: false);
        check("Without a dusk look there is no dusk, whatever duskMinutes says", At(noDusk, 17, 45) == DaylightLook.Day && Next(noDusk, 12, 0) == day0.AddHours(18.5));
        var shift = Theme(day: "20:00", night: "02:00", dusk: 180);
        check("A day that runs past midnight has its dusk before its night", At(shift, 22, 59) == DaylightLook.Day && At(shift, 23, 0) == DaylightLook.Dusk
            && At(shift, 1, 59, addDays: 1) == DaylightLook.Dusk && At(shift, 2, 0, addDays: 1) == DaylightLook.Night && At(shift, 19, 59) == DaylightLook.Night
            && Next(shift, 21, 0) == day0.AddHours(23));
        var late = Theme(day: "10:00", night: "00:30");
        check("A dusk can itself run past midnight", At(late, 23, 29) == DaylightLook.Day && At(late, 23, 45) == DaylightLook.Dusk && At(late, 0, 15) == DaylightLook.Dusk
            && At(late, 0, 30) == DaylightLook.Night && Next(late, 23, 0) == day0.AddHours(23.5) && Next(late, 23, 45) == day0.AddDays(1).AddMinutes(30));
        var brief = Theme(day: "18:00", night: "18:30", dusk: 60);
        check("A dusk longer than the day fills the day and no more", At(brief, 18, 0) == DaylightLook.Dusk && At(brief, 18, 29) == DaylightLook.Dusk
            && At(brief, 17, 59) == DaylightLook.Night && PanelThemes.DuskLength(brief) == TimeSpan.FromMinutes(30));
        check("The settings' hours move the dusk with the night", At(clock, 19, 45, setting: "07:00-20:00") == DaylightLook.Dusk
            && Next(clock, 12, 0, "07:00-20:00") == day0.AddHours(19));

        // The dusk look is laid over the day look, like the night look, and held to the same contrast rules.
        var dim = new PanelThemeFile
        {
            Base = "dark",
            Night = new PanelThemeFile { Colors = new() { ["surface"] = "#101418" } },
            Dusk = new PanelThemeFile { Colors = new() { ["surface"] = "#2A1A2E", ["textPrimary"] = "#5A4A5E" } }
        };
        var dusk = PanelThemes.Build("dim", dim, DaylightLook.Dusk);
        var dayLook = PanelThemes.Build("dim", dim);
        check("The dusk look is laid over the day look", dusk.Look == DaylightLook.Dusk && !dusk.IsNight && dusk.Surface == ThemeColor.Parse("#2A1A2E")
            && dusk["accent"] == PanelThemes.Build("dim", new PanelThemeFile { Base = "dark", Colors = new() { ["surface"] = "#2A1A2E" } })["accent"]
            && dayLook.Look == DaylightLook.Day && dayLook.Surface == PanelThemes.Build("dark", new PanelThemeFile { Base = "dark" }).Surface);
        check("The dusk look's colors are brought up to their contrast minimums",
            dusk["textPrimary"].MinimumContrast(dusk.SurfaceStops) >= PanelThemes.StrongTextContrast - .001 && dusk.Adjustments.Any(a => a.Key == "textPrimary"));
        check("A file without a dusk look resolves its day look for dusk",
            PanelThemes.Build("plain", Theme(withDusk: false), DaylightLook.Dusk) is { Look: DaylightLook.Day } plain && plain.Surface == ThemeColor.Black);

        // "New from current theme" keeps the mode, the dusk length and the dusk look.
        var source = Theme(mode: "system", dusk: 90);
        var copied = PanelThemes.Parse(PanelThemes.FillTemplate("copy", PanelThemes.Build("source", source), "dark", source));
        check("A theme made from one with a dusk look keeps its mode, dusk length and dusk look",
            copied.Daylight is { Mode: "system", Day: "06:30", Night: "18:30", DuskMinutes: 90 } && copied.Dusk?.Colors?["surface"] == "#2A1A2E"
            && PanelThemes.Build("copy", copied, DaylightLook.Dusk).Surface == PanelThemes.Build("source", source, DaylightLook.Dusk).Surface);
        var plainCopy = PanelThemes.Parse(PanelThemes.FillTemplate("copy", PanelThemes.Build("source", Theme(withDusk: false)), "dark", Theme(withDusk: false)));
        check("A theme with only a night look is copied without a mode, dusk length or dusk look",
            plainCopy.Daylight is { Mode: "", DuskMinutes: null } && plainCopy.Dusk is null && plainCopy.Night is not null);
        check("The template documents the daylight mode and the dusk look",
            PanelThemes.Template.Contains("mode: clock", StringComparison.Ordinal) && PanelThemes.Template.Contains("\"dusk\"", StringComparison.Ordinal)
            && PanelThemes.Template.Contains("duskMinutes", StringComparison.Ordinal));

        // The settings keep "system", hours, or nothing.
        check("The daylight setting keeps \"system\" and valid hours, and drops anything else",
            PanelThemes.NormalizeDaylightSetting("System") == "system" && PanelThemes.NormalizeDaylightSetting(" 7:00-19:30") == "07:00-19:30"
            && PanelThemes.NormalizeDaylightSetting("auto") == "" && PanelThemes.NormalizeDaylightSetting("07:00-07:00") == "" && PanelThemes.NormalizeDaylightSetting(null) == "");
        var settings = new DeckSettings { ThemeDaylight = "SYSTEM" };
        settings.Normalize();
        check("Saved settings keep the system daylight mode", settings.ThemeDaylight == "system");
    }
}
