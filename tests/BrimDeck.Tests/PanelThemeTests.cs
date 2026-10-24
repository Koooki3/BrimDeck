using System.Text.RegularExpressions;
using BrimDeck.Core;

internal static class PanelThemeTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        PanelPalette BuiltIn(string id) => PanelThemes.Resolve(PanelThemes.BuiltIn(id)!);
        PanelThemeInfo ByFile(IReadOnlyList<PanelThemeInfo> themes, string name) => themes.Single(theme => theme.Path!.EndsWith(name, StringComparison.Ordinal));
        // A folder of theme files under the test root, written fresh.
        string Folder(string name, params (string File, string Json)[] files)
        {
            var folder = Path.Combine(root, name);
            foreach (var (file, json) in files)
            {
                var path = Path.Combine(folder, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
            }
            Directory.CreateDirectory(folder);
            return folder;
        }

        // ThemeColor: parsing and WCAG contrast.
        check("Theme colors parse both forms and print the short form when opaque",
            ThemeColor.Parse("#9EEBEBF5") == new ThemeColor(0x9E, 0xEB, 0xEB, 0xF5) && ThemeColor.Parse("1d1d1f").ToString() == "#1D1D1F"
            && ThemeColor.Parse("#80FF0000").ToString() == "#80FF0000" && !ThemeColor.TryParse("#12345", out _) && !ThemeColor.TryParse("red", out _));
        check("Colors do not allow white space inside", !ThemeColor.TryParse("#  FFFF", out _) && ThemeColor.TryParse(" #00FFFF ", out _));
        check("Contrast follows WCAG", Math.Abs(ThemeColor.Contrast(ThemeColor.White, ThemeColor.Black) - 21) < .01
            && Math.Abs(ThemeColor.Contrast(ThemeColor.Parse("#777777"), ThemeColor.White) - 4.48) < .01);
        check("A translucent color is measured over its background",
            Math.Abs(ThemeColor.Contrast(ThemeColor.Parse("#80FFFFFF"), ThemeColor.Black) - ThemeColor.Contrast(ThemeColor.Parse("#808080"), ThemeColor.Black)) < .05);
        var pastel = ThemeColor.Parse("#E5C890");
        check("A readable color is kept exactly", pastel.EnsureContrast(ThemeColor.Black, 3) == pastel);
        var onWhite = pastel.EnsureContrast(ThemeColor.White, 3);
        var (h1, _, _) = ThemeColor.ToHsl(pastel); var (h2, _, _) = ThemeColor.ToHsl(onWhite);
        check("An unreadable color is darkened on a light surface just enough, keeping its hue",
            ThemeColor.Contrast(onWhite, ThemeColor.White) >= 3 && ThemeColor.Contrast(onWhite, ThemeColor.White) < 3.3 && Math.Abs(h1 - h2) < 3);
        check("An unreadable color is lightened on a dark surface", ThemeColor.Contrast(ThemeColor.Parse("#3A538D").EnsureContrast(ThemeColor.Black, 3), ThemeColor.Black) >= 3);
        var gradient = new[] { ThemeColor.Parse("#0A0613"), ThemeColor.Parse("#1C0B30") };
        check("Contrast holds against every stop of a gradient", ThemeColor.Parse("#5A4A80").EnsureContrast(gradient, 4.5).MinimumContrast(gradient) >= 4.5);

        // Built-in themes.
        check("Built-in themes are listed in order and Monokai Pro is not among them",
            PanelThemes.BuiltIns.Select(theme => theme.Id).SequenceEqual(["dark", "light", "one-dark-pro", "github-dark", "github-light", "dracula", "night-owl", "cyberpunk-edgerunners"]));
        check("A built-in theme's id is its file name", PanelThemes.BuiltIns.All(theme => PanelThemes.NormalizeId(theme.File.Id) == theme.Id));
        check("Every built-in theme names an open license", PanelThemes.BuiltIns.All(theme => theme.File.License is "MIT" or "Apache-2.0"));
        bool allValid = true; var adjusted = new List<string>();
        foreach (var theme in PanelThemes.BuiltIns)
        {
            var palette = PanelThemes.Resolve(theme);
            allValid &= palette.Colors.Count == PanelThemes.Tokens.Count;
            var popup = palette["popup"].Over(palette.Surface);
            foreach (var token in PanelThemes.Tokens.Where(token => token.Against is PanelThemeBackdrop.Surface or PanelThemeBackdrop.SurfaceAndPopup))
                allValid &= palette[token.Key].MinimumContrast(token.Against == PanelThemeBackdrop.SurfaceAndPopup ? [.. palette.SurfaceStops, popup] : palette.SurfaceStops) >= token.Minimum - .001;
            allValid &= palette["onAccent"].MinimumContrast([palette["accent"].Over(popup)]) >= PanelThemes.GraphicContrast - .001;
            allValid &= palette["indicatorProgress"].MinimumContrast([palette["indicator"].Over(ThemeColor.Black)]) >= PanelThemes.IndicatorContrast - .001;
            allValid &= palette["textPrimary"].MinimumContrast([palette["fillSelected"].Over(palette.Surface), palette["pressed"].Over(palette.Surface),
                palette["input"].Over(popup), palette["rangeFill"].Over(popup), palette["tooltip"], palette["bubble"]]) >= PanelThemes.TextContrast - .001;
            allValid &= palette["textSecondary"].MinimumContrast([palette["fill"].Over(palette.Surface), palette["hover"].Over(palette.Surface)]) >= PanelThemes.TextContrast - .001;
            foreach (var color in palette.Apps.Values) allValid &= color.IsOpaque && color.MinimumContrast(palette.SurfaceStops) >= PanelThemes.GraphicContrast - .001;
            // The built-in themes are designed to pass as written; an adjustment would mean a mistake in the file.
            adjusted.AddRange(palette.Adjustments.Select(a => $"{theme.Id}.{a.Key} {a.Before}->{a.After} {a.Ratio:0.00}/{a.Minimum}"));
        }
        check("Every built-in theme meets every contrast rule", allValid);
        check("Built-in themes pass without any adjustment " + string.Join("; ", adjusted), adjusted.Count == 0);
        var dark = BuiltIn("dark");
        check("The dark theme is the original look", dark.Surface == ThemeColor.Black && dark["textPrimary"].ToString() == "#F5F5F7"
            && dark["textSecondary"].ToString() == "#9EEBEBF5" && dark.AppDefault(ProviderId.Claude)?.ToString() == AppPresets.ThemeColor(ProviderId.Claude) && !dark.HasOverlay && dark.IsDark);
        check("The light theme is light and has its own app colors", !BuiltIn("light").IsDark && BuiltIn("light").AppDefault(ProviderId.DeepSeek)?.ToString() == "#9B51C9");
        // Each theme keeps what the user changed under it.
        {
            var kept = new DeckSettings();
            var column = kept.Apps[0];
            column.ThemeColor = "#123456"; column.WarningColor = "#654321"; kept.ClockStyle = ClockStyle.Serif; kept.ClockColor = ClockColor.Cyan;
            PanelThemes.SwitchTheme(kept, "dark", BuiltIn("dark"), "dracula", BuiltIn("dracula"));
            check("A theme chosen for the first time starts from its own colors", column.ThemeColor == "" && column.WarningColor == "" && kept.PanelTheme == "dracula");
            column.ThemeColor = "#ABCDEF"; kept.ClockStyle = ClockStyle.Neon;
            PanelThemes.SwitchTheme(kept, "dracula", BuiltIn("dracula"), "dark", BuiltIn("dark"));
            check("Switching back brings the colors and clock changed under that theme back", column.ThemeColor == "#123456" && column.WarningColor == "#654321"
                && kept.ClockStyle == ClockStyle.Serif && kept.ClockColor == ClockColor.Cyan);
            kept.Apps.Add(new AppEntry { ThemeColor = "#111111" });
            PanelThemes.SwitchTheme(kept, "dark", BuiltIn("dark"), "Dracula", BuiltIn("dracula"));
            check("Each theme's own changes come back with it", column.ThemeColor == "#ABCDEF" && kept.ClockStyle == ClockStyle.Neon);
            check("An column added later takes the theme's own color under a theme that saved none for it", kept.Apps[^1].ThemeColor == "");
            var copy = kept.Copy(); copy.ThemeChoices["dark"].Apps.Clear();
            check("A copy of the settings has its own saved choices", kept.ThemeChoices["dark"].Apps.Count > 0);
            PanelThemes.ForgetTheme(kept, "DARK");
            check("A deleted theme takes its saved choices with it", !kept.ThemeChoices.ContainsKey("dark"));
        }
        // Restoring the application list keeps the colors the other themes saved for the same quota sources.
        {
            var reset = new DeckSettings();
            reset.Apps[0].ThemeColor = "#123456";
            PanelThemes.SwitchTheme(reset, "dark", BuiltIn("dark"), "dracula", BuiltIn("dracula"));
            reset.Apps[0].ThemeColor = "#ABCDEF";
            var previous = reset.Apps; reset.Apps = new DeckSettings().Apps;
            PanelThemes.CarryAppChoices(reset, previous);
            check("The restored applications are new instances", reset.Apps[0].InstanceId != previous[0].InstanceId && reset.Apps[0].ThemeColor == "");
            PanelThemes.SwitchTheme(reset, "dracula", BuiltIn("dracula"), "dark", BuiltIn("dark"));
            check("Another theme's saved colors come back after the application list is restored", reset.Apps[0].ThemeColor == "#123456");
            check("Saved colors of applications no longer listed are dropped",
                reset.ThemeChoices["dracula"].Apps.Keys.All(key => reset.Apps.Any(app => app.InstanceId.ToString("N") == key)));
        }
        // Renaming a theme file changes only its own name.
        {
            const string original = """
                { // my theme
                  "id": "mine", "name": "我的主题", "nameEn": "My theme",
                  "clock": { "gradients": [ { "name": "Neon", "stops": [] } ] },
                  /* "name": "not this" */ "description": "a \"name\" inside" }
                """;
            var renamed = PanelThemes.Renamed(original, "夜航");
            check("Renaming sets the theme's names in both languages", renamed.Contains(""" "name": "夜航" """.Trim()) && renamed.Contains(""" "nameEn": "夜航" """.Trim()) && !renamed.Contains("My theme"));
            check("Renaming leaves nested names, comments, strings and the id alone",
                renamed.Contains(""" "name": "Neon" """.Trim()) && renamed.Contains(""" /* "name": "not this" */ """.Trim()) && renamed.Contains(""" a \"name\" inside """.Trim())
                && renamed.Contains(""" "id": "mine" """.Trim()) && renamed.StartsWith("{ // my theme"));
            check("A theme file without a name gets one", PanelThemes.Renamed(""" { "id": "x" } """.Trim(), "Named").Contains(""" "name": "Named" """.Trim()));
            var file = Path.Combine(root, "rename-theme.json"); File.WriteAllText(file, original);
            PanelThemes.RenameFile(file, "  Blue  ");
            check("Renaming a file trims the name and writes it back", File.ReadAllText(file).Contains(""" "name": "Blue" """.Trim()));
        }
        var demo = BuiltIn(PanelThemes.Demo);
        check("The demo theme is listed with the extensions, leaving the custom section to the user", PanelThemes.BuiltIn(PanelThemes.Demo)!.Kind == PanelThemeKind.Extension);
        check("The demo theme carries its effects", demo.HasOverlay && demo.Effects.RimGradient.Count == 3 && demo.Effects.ShadowDepth == 0 && demo.Effects.Scanlines > 0);
        check("System follows the Windows app theme", PanelThemes.Find("system", true, []).Id == "dark" && PanelThemes.Find("SYSTEM", false, []).Id == "light");
        check("An unknown theme shows as dark", PanelThemes.Find("gone", false, []).Id == "dark");

        // Theme files and ids.
        var folder = Folder("themes",
            ("sparse.json", """
                { // comments and trailing commas are fine
                  "id": "Sparse Theme", "name": "稀疏", "base": "dracula",
                  "colors": { "textTertiary": "#30303A", },
                  "apps": { "claude": "#202020" },
                }
                """),
            ("neon/theme.json", """{ "name": "Neon", "base": "light", "effects": { "scanlines": 5, "rimWidth": -2 } }"""),
            ("broken.json", "{ not json"),
            ("clash.json", """{ "id": "dracula" }"""),
            ("badcolor.json", """{ "id": "bad", "colors": { "textPrimary": "white" } }"""),
            ("badkey.json", """{ "id": "badkey", "colors": { "background": "#FFFFFF" } }"""),
            ("badbase.json", """{ "id": "badbase", "base": "nothing" }"""));
        var user = PanelThemes.LoadFolder(folder);
        check("Theme files and theme folders are found", user.Count == 7);
        var sparse = user.Single(theme => theme.Id == "sparse-theme");
        var sparsePalette = PanelThemes.Resolve(sparse);
        check("A theme inherits its base", sparsePalette.Surface.ToString() == "#21222C" && sparsePalette["accent"].ToString() == "#BD93F9");
        check("Unreadable colors in a file are adjusted and reported",
            sparsePalette["textTertiary"].MinimumContrast([.. sparsePalette.SurfaceStops, sparsePalette["popup"].Over(sparsePalette.Surface)]) >= 3 && sparsePalette.Adjustments.Any(a => a.Key == "textTertiary")
            && sparsePalette.AppDefault(ProviderId.Claude)!.Value.MinimumContrast(sparsePalette.SurfaceStops) >= 3 && sparsePalette.Adjustments.Any(a => a.Key == "claude"));
        var neon = PanelThemes.Resolve(user.Single(theme => theme.Id == "neon"));
        check("A folder theme takes its folder name and effects are clamped", neon.Effects.Scanlines == .12 && neon.Effects.RimWidth == 0 && !neon.IsDark);
        check("Broken files are listed with the reason and cannot be chosen",
            user.Count(theme => !theme.Usable) == 5 && user.Where(theme => !theme.Usable).All(theme => theme.Error!.Length > 0 && theme.Id.StartsWith("file:", StringComparison.Ordinal)));
        check("A file cannot take a built-in theme's id", ByFile(user, "clash.json") is { Usable: false } clash
            && clash.Id.StartsWith("file:", StringComparison.Ordinal) && PanelThemes.Find("dracula", true, user).BuiltIn);
        check("Unusable entries have distinct ids", user.Select(theme => theme.Id).Distinct().Count() == user.Count);
        check("A user theme is found by its id", PanelThemes.Find("sparse-theme", true, user) == sparse);
        check("A user palette is resolved once per reading", ReferenceEquals(PanelThemes.Resolve(sparse), sparsePalette)
            && !ReferenceEquals(PanelThemes.Resolve(PanelThemes.LoadFolder(folder).Single(theme => theme.Id == "sparse-theme")), sparsePalette));
        var claimed = PanelThemes.LoadFolder(Folder("claims",
            ("a-copy.json", """{ "id": "shared", "colors": { "surface": "nope" } }"""),
            ("b.json", """{ "id": "shared", "colors": { "surface": "#102030" } }"""),
            ("c.json", """{ "id": "shared" }""")));
        check("Only a usable file takes an id; a later duplicate is refused", claimed.Single(theme => theme.Id == "shared").Path!.EndsWith("b.json", StringComparison.Ordinal)
            && claimed.Count(theme => !theme.Usable) == 2);
        // A copy made in Explorer sorts before its original.
        var copied = PanelThemes.LoadFolder(Folder("copies",
            ("my-theme.json", """{ "id": "my-theme", "colors": { "surface": "#101010" } }"""),
            ("my-theme - Copy.json", """{ "id": "my-theme", "colors": { "surface": "#202020" } }""")));
        check("The file named after its id keeps the id over a copy", copied.Single(theme => theme.Usable).Path!.EndsWith("my-theme.json", StringComparison.Ordinal)
            && copied.Single(theme => !theme.Usable).Error!.Length > 0);
        var halfSaved = PanelThemes.LoadFolder(Folder("halfsaved",
            ("my-theme.json", """{ "id": "my-theme", "colors": { "surface": """),
            ("my-theme - Copy.json", """{ "id": "my-theme", "colors": { "surface": "#202020" } }""")));
        check("A file named after its id keeps the id while it cannot be read, so a copy does not take its place", halfSaved.All(theme => !theme.Usable));
        check("An id in Chinese is refused with a reason that names it",
            PanelThemes.LoadFolder(Folder("foreign", ("cn.json", """{ "id": "赛博朋克", "name": "赛博朋克" }"""))).Single() is { Usable: false } foreign && foreign.Error!.Contains("赛博朋克"));
        var nullThemes = PanelThemes.LoadFolder(Folder("nulls",
            ("id.json", """{ "id": null }"""),
            ("name.json", """{ "id": "n", "name": null }"""),
            ("color.json", """{ "id": "c", "colors": { "surface": null } }"""),
            ("clock.json", """{ "id": "k", "clock": { "style": null } }"""),
            ("stop.json", """{ "id": "g", "effects": { "surfaceGradient": [null, "#000000"] } }""")));
        check("null values make a file unusable instead of throwing", nullThemes.Count == 5 && nullThemes.All(theme => !theme.Usable && theme.Error!.Length > 0)
            && nullThemes.All(theme => theme.DisplayName.Length > 0));
        var badClock = PanelThemes.LoadFolder(Folder("badclocks",
            ("style.json", """{ "id": "style", "clock": { "style": "sundial" } }"""),
            ("presets.json", """{ "id": "presets", "clock": { "gradients": [ { "name": "x", "stops": ["#000000"] } ] } }""")));
        check("An unknown clock style or a preset gradient without three colors makes a file unusable", badClock.All(theme => theme.Error is { Length: > 0 }));

        // Building and contrast.
        var mixed = PanelThemes.Build("mixed", new PanelThemeFile { Colors = new() { ["surface"] = "#000000", ["popup"] = "#FFFFFF", ["textPrimary"] = "#F5F5F7" } });
        List<ThemeColor> both = [ThemeColor.Black, ThemeColor.White];
        check("Text on light and dark backgrounds takes the best worst case", mixed["textPrimary"].MinimumContrast(both) > ThemeColor.Parse("#F5F5F7").MinimumContrast(both)
            && mixed["textPrimary"].MinimumContrast(both) > 4 && mixed.Adjustments.Single(a => a.Key == "textPrimary") is { Reached: false } unmet && unmet.Describe().Length > 0);
        var fills = PanelThemes.Build("fills", new PanelThemeFile { Colors = new() { ["fillSelected"] = "#FFFFFF", ["pressed"] = "#FFFFFF" } });
        check("A light fill never turns text black on a black surface", fills["textPrimary"].MinimumContrast([ThemeColor.Black]) >= 4.5);
        var ownSwatches = PanelThemes.Build("own", new PanelThemeFile { Base = "light", Swatches = ["#FFFF66", "#80000000"], Apps = new() { ["claude"] = "#80C15F3C" } });
        check("A theme's own picker colors and app colors are opaque and readable", ownSwatches.HasOwnSwatches
            && ownSwatches.Swatches.All(color => color.IsOpaque && color.MinimumContrast(ownSwatches.SurfaceStops) >= PanelThemes.GraphicContrast - .001)
            && ownSwatches.AppDefault(ProviderId.Claude)!.Value.IsOpaque);

        // Template and new theme files.
        var templates = Folder("templates");
        var created = PanelThemes.CreateFromTemplate(templates);
        var second = PanelThemes.CreateFromTemplate(templates);
        var made = PanelThemes.LoadFolder(templates);
        check("The template creates a usable theme without overwriting another",
            Path.GetFileName(created) == "my-theme.json" && Path.GetFileName(second) == "my-theme-2.json"
            && made.Any(theme => theme.Id == "my-theme" && theme.Usable) && made.Any(theme => theme.Id == "my-theme-2" && theme.Usable));
        check("New themes are numbered in their names and start without a description",
            PanelThemes.Parse(File.ReadAllText(second)) is { Name: "我的主题 2", NameEn: "My theme 2", Description: "", DescriptionEn: "" }
            && PanelThemes.Parse(File.ReadAllText(created)) is { Name: "我的主题", NameEn: "My theme" });
        File.Move(created, Path.Combine(templates, "renamed.json"));
        var third = PanelThemes.CreateFromTemplate(templates);
        check("A new theme never reuses an id a renamed file still carries", PanelThemes.LoadFolder(templates).Single(theme => theme.Path == third) is { Usable: true } fresh && fresh.Id != "my-theme");
        var template = PanelThemes.Parse(PanelThemes.Template);
        check("The template lists every token and app", PanelThemes.Tokens.All(token => template.Colors!.ContainsKey(token.Key)) && PanelThemes.AppKeys.All(key => template.Apps!.ContainsKey(key)));
        check("The template resolves without adjustments", PanelThemes.Build("t", template).Adjustments.Count == 0);
        // A theme started from another one reproduces it exactly, so editing begins from what was on screen.
        bool reproduced = true;
        foreach (var theme in PanelThemes.BuiltIns)
        {
            var original = PanelThemes.Resolve(theme);
            var copy = PanelThemes.Build("copy", PanelThemes.Parse(PanelThemes.FillTemplate("copy", original, theme.Id)));
            reproduced &= original.Colors.All(pair => copy[pair.Key] == pair.Value) && copy.Adjustments.Count == 0
                && original.Effects.SurfaceGradient.SequenceEqual(copy.Effects.SurfaceGradient) && original.Effects.RimGradient.SequenceEqual(copy.Effects.RimGradient)
                && original.Effects with { SurfaceGradient = [], RimGradient = [] } == copy.Effects with { SurfaceGradient = [], RimGradient = [] }
                && Enum.GetValues<ProviderId>().All(id => original.AppDefault(id) == copy.AppDefault(id))
                && original.Clock is { } clock && copy.Clock is { } copyClock && clock.Style == copyClock.Style && clock.Color == copyClock.Color
                && ClockColors.Swatches.All(color => original.ClockPreset(color) == copy.ClockPreset(color))
                && original.ClockGradients.Select(g => g.Name + g.NameEn + string.Join(",", g.Stops)).SequenceEqual(copy.ClockGradients.Select(g => g.Name + g.NameEn + string.Join(",", g.Stops)))
                && original.HasOwnSwatches == copy.HasOwnSwatches && original.Swatches.SequenceEqual(copy.Swatches);
        }
        check("A theme made from another starts out identical: colors, effects, apps, clock and swatches", reproduced);
        var nightOwl = BuiltIn("night-owl");
        // No built-in theme suggests a gradient clock, so a theme of its own carries one.
        var glow = PanelThemes.Build("glow", new PanelThemeFile { Base = "night-owl", Clock = new PanelThemeClock { Style = "neon", Color = "gradient", Gradient = ["#C792EA", "#82AAFF", "#7FDBCA"] } });
        check("A theme made from another keeps its clock gradient, and the template's placeholder otherwise",
            PanelThemes.Build("copy", PanelThemes.Parse(PanelThemes.FillTemplate("copy", glow, "night-owl"))).Clock is { Style: "neon", Color: "gradient" } copiedClock
            && copiedClock.Gradient!.SequenceEqual(glow.Clock!.Gradient!) && PanelThemes.Parse(PanelThemes.FillTemplate("copy", nightOwl)).Clock!.Custom == "#B9A8FF");

        // Clock, swatches and what choosing a theme changes.
        bool clockColors = true, pickers = true;
        foreach (var theme in PanelThemes.BuiltIns)
        {
            var palette = PanelThemes.Resolve(theme);
            clockColors &= palette.Clock is { Style.Length: > 0, Color.Length: > 0 } && palette.ClockGradients.Count == 5
                && palette.ClockGradients.All(g => g.Stops.Count == 3 && g.Name.Length > 0 && g.NameEn.Length > 0)
                && ClockColors.Swatches.Select(palette.ClockPreset).Concat(palette.ClockGradients.SelectMany(g => g.Stops).Select(ThemeColor.Parse))
                    .All(color => color.MinimumContrast(palette.SurfaceStops) >= PanelThemes.TextContrast);
            pickers &= palette.Swatches.Count == 12 && palette.Swatches.Distinct().Count() == 12 && palette.Swatches.All(color => color.MinimumContrast(palette.SurfaceStops) >= PanelThemes.GraphicContrast - .001);
        }
        check("Every theme suggests a clock and has its own swatches and five preset gradients, readable as written", clockColors);
        check("Every theme offers twelve readable picker colors", pickers);
        check("The dark theme keeps the original picker colors and clock presets",
            dark.Swatches.Select(color => color.ToString()).SequenceEqual(AppPresets.Palette) && dark.ClockPreset(ClockColor.Amber).ToString() == ClockColors.Preset(ClockColor.Amber)
            && dark.ClockGradients.Select(g => g.Stops).Zip(ClockColors.Gradients).All(pair => pair.First.SequenceEqual(pair.Second.Stops)));
        var glowClock = new DeckSettings();
        PanelThemes.ApplyClock(glow, glowClock);
        check("A theme's clock is applied to the settings", glowClock.ClockStyle == ClockStyle.Neon && glowClock.ClockColor == ClockColor.Gradient
            && glowClock.ClockGradient.SequenceEqual(["#C792EA", "#82AAFF", "#7FDBCA"]));
        check("The built-in themes suggest a clock in one color, not a gradient",
            PanelThemes.BuiltIns.All(theme => PanelThemes.Resolve(theme).Clock?.Color != "gradient"));
        var demoClock = new DeckSettings { ClockGradient = ["#111111", "#222222", "#333333"] };
        PanelThemes.ApplyClock(demo, demoClock);
        check("The demo uses the seven-segment clock in the theme's color and leaves the gradient alone", demoClock.ClockStyle == ClockStyle.Segment
            && demoClock.ClockColor == ClockColor.White && demoClock.ClockGradient.SequenceEqual(["#111111", "#222222", "#333333"]));
        var placeholder = new DeckSettings { ClockCustomColor = "#123456", ClockGradient = ["#111111", "#222222", "#333333"] };
        PanelThemes.ApplyClock(PanelThemes.Build("p", template), placeholder);
        check("The clock's custom color and gradient count only when the clock uses them", placeholder.ClockCustomColor == "#123456"
            && placeholder.ClockGradient.SequenceEqual(["#111111", "#222222", "#333333"]) && placeholder.ClockColor == ClockColor.White);
        check("A theme without its own clock inherits its base's", sparsePalette.Clock?.Style == "minimal");
        var partial = PanelThemes.Build("partial", new PanelThemeFile { Base = "one-dark-pro", Clock = new PanelThemeClock { Style = "segment" } });
        check("A clock that names only a style keeps its base's color", partial.Clock is { Style: "segment", Color: "custom", Custom: "#61AFEF" });
        var wide = new DeckSettings();
        PanelThemes.ApplyClock(PanelThemes.Build("w", new PanelThemeFile { Clock = new PanelThemeClock { Color = "custom", Custom = "#FF61AFEF" } }), wide);
        check("An 8-digit clock color is applied as its opaque color", wide.ClockColor == ClockColor.Custom && wide.ClockCustomColor == "#61AFEF");
        var matched = new DeckSettings(); matched.Apps[0].ThemeColor = "#123456"; matched.Apps[0].WarningColor = "#654321"; matched.Apps[1].CriticalColor = "#ABCDEF";
        PanelThemes.ApplyMatching(BuiltIn("cyberpunk-edgerunners"), matched);
        check("Choosing a theme makes the app colors follow it and switches the clock", matched.Apps.All(app => app.ThemeColor == "" && app.WarningColor == "" && app.CriticalColor == "")
            && matched.ClockStyle == ClockStyle.Segment);
        var dracula = BuiltIn("dracula"); var githubDark = BuiltIn("github-dark");
        var carried = new DeckSettings { ClockGradient = [.. dracula.ClockGradients[2].Stops] };
        PanelThemes.ApplyMatching(githubDark, carried, dracula);
        var own = new DeckSettings { ClockGradient = ["#123456", "#234567", "#345678"] };
        PanelThemes.ApplyMatching(githubDark, own, dracula);
        check("A preset gradient becomes the new theme's preset in the same place; a made one stays",
            carried.ClockGradient.SequenceEqual(githubDark.ClockGradients[2].Stops) && own.ClockGradient.SequenceEqual(["#123456", "#234567", "#345678"]));
        var lowercase = new DeckSettings { ClockGradient = [.. dracula.ClockGradients[0].Stops.Select(stop => stop.ToLowerInvariant())] };
        var translucent = PanelThemes.Build("translucent", new PanelThemeFile { Clock = new PanelThemeClock { Gradients = [new() { Name = "a", Stops = ["#FF111111", " #222222", "#333333"] }] } });
        var fromTranslucent = new DeckSettings { ClockGradient = ["#111111", "#222222", "#333333"] };
        check("A preset gradient is recognized however its colors are written", PanelThemes.CarryGradient(dracula, githubDark, lowercase)
            && lowercase.ClockGradient.SequenceEqual(githubDark.ClockGradients[0].Stops) && PanelThemes.CarryGradient(translucent, githubDark, fromTranslucent));

        // Settings.
        var settings = new DeckSettings { PanelTheme = "  GitHub-Dark " }; settings.Normalize();
        var empty = new DeckSettings { PanelTheme = "" }; empty.Normalize();
        check("The panel theme setting is normalized", settings.PanelTheme == "github-dark" && empty.PanelTheme == "dark" && new DeckSettings().PanelTheme == "dark");

        // XAML: App.xaml carries the dark theme's colors as the first values of the brushes PanelTheme replaces.
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "BrimDeck.sln"))) source = source.Parent;
        check("The source tree is found for the XAML checks", source is not null);
        if (source is null) return;
        var app = Path.Combine(source.FullName, "src", "BrimDeck");
        var xaml = File.ReadAllText(Path.Combine(app, "App.xaml"));
        var brushes = Regex.Matches(xaml, @"<SolidColorBrush x:Key=""Panel(\w+)"" Color=""(#\w+)""/>");
        string Token(string name) => name switch
        {
            "Primary" or "Secondary" or "Tertiary" => "text" + name, "ToolTip" => "tooltip", "ToolTipBorder" => "tooltipBorder",
            _ => char.ToLowerInvariant(name[0]) + name[1..]
        };
        check("The XAML fallback brushes are the dark theme's colors", brushes.Count >= 15
            && brushes.All(match => dark.Colors.TryGetValue(Token(match.Groups[1].Value), out var color)
                && string.Equals(color.ToString(), ThemeColor.Parse(match.Groups[2].Value).ToString(), StringComparison.OrdinalIgnoreCase)));
        // A brush outside the merged dictionary would win over the one PanelTheme publishes and never change.
        int mergedEnd = xaml.IndexOf("</ResourceDictionary.MergedDictionaries>", StringComparison.Ordinal);
        check("The panel brushes sit in the merged dictionary PanelTheme replaces", mergedEnd > 0 && brushes.All(match => match.Index < mergedEnd));
        var published = File.ReadAllText(Path.Combine(app, "PanelTheme.cs"));
        check("Every XAML panel brush is published by PanelTheme", brushes.All(match => Regex.IsMatch(published,
            $@"\(\s*""Panel{match.Groups[1].Value}""\s*,\s*""{Token(match.Groups[1].Value)}""\s*\)")));
        check("No XAML refers to a panel brush with StaticResource, which would keep the first theme",
            Directory.EnumerateFiles(app, "*.xaml").All(file => !Regex.IsMatch(File.ReadAllText(file), @"StaticResource Panel(?!KeyboardFocus|WideKeyboardFocus|OutsetKeyboardFocus|IconButton|SourceButton|GlyphButton|SettingsButton|TextButton)\w+")));
    }
}
