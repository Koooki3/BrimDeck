using BrimDeck.Core;

// W-21: the notices page takes every color from the panel theme. This group holds what the theme side of that has to keep: the keys
// the page reads exist in every built-in theme (and its night look), the colors that stand on the page's fills read there, and a
// theme that leaves a key out or sets it too faint is made readable instead of showing the old constants.
internal static class NoticeThemeFitTests
{
    // Every color the notices page reads from the theme.
    internal static readonly string[] PageKeys =
    [
        "surface", "textPrimary", "textSecondary", "textTertiary", "accent", "onAccent", "danger", "success", "caution",
        "hairline", "fill", "fillSelected", "hover", "pressed"
    ];

    internal static void Run(Action<string, bool> check)
    {
        var looks = PanelThemes.BuiltIns.SelectMany(theme => theme.File.Night is null
            ? new[] { (Theme: theme, Night: false) } : [(theme, false), (theme, true)]).ToList();

        // Every built-in theme writes the page's keys itself, in its night look as well: a key left out would come from the dark
        // theme, whose colors say nothing about this theme's surface.
        var missing = new List<string>();
        foreach (var (theme, night) in looks)
        {
            var layer = night ? theme.File.Night! : theme.File;
            var written = (layer.Colors ?? []).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "success", "caution" }.Concat(night ? [] : PageKeys))
                if (!written.Contains(key)) missing.Add($"{theme.Id}{(night ? " (night)" : "")}.{key}");
        }
        check("Every built-in theme writes the keys the notices page reads, in both looks" + (missing.Count > 0 ? ": " + string.Join(", ", missing) : ""), missing.Count == 0);
        check("Every resolved built-in palette has the page's keys", looks.All(look => PageKeys.All(key => PanelThemes.Resolve(look.Theme, look.Night).Colors.ContainsKey(key))));
        check("The theme template lists success and caution", PanelThemes.Parse(PanelThemes.Template).Colors is { } template && template.ContainsKey("success") && template.ContainsKey("caution"));
        check("Success and caution are tokens held to the body-text minimum on the surface and pop-ups",
            PanelThemes.Tokens.Where(token => token.Key is "success" or "caution").All(token => token.Minimum == PanelThemes.TextContrast && token.Against == PanelThemeBackdrop.SurfaceAndPopup)
            && PanelThemes.Tokens.Count(token => token.Key is "success" or "caution") == 2);

        // What the page draws on its fills, in every built-in theme and look, measured as ThemeCoverageTests measures the panel.
        var failures = new List<string>();
        foreach (var (theme, night) in looks)
        {
            var palette = PanelThemes.Resolve(theme, night);
            string name = theme.Id + (night ? " (night)" : "");
            var popup = palette["popup"].Over(palette.Surface);
            foreach (var (what, color, minimum, backgrounds) in new (string, ThemeColor, double, IReadOnlyList<ThemeColor>)[]
            {
                ("success", palette["success"], PanelThemes.TextContrast, [.. palette.SurfaceStops, popup]),
                ("caution", palette["caution"], PanelThemes.TextContrast, [.. palette.SurfaceStops, popup]),
                ("tertiary on fill", palette["textTertiary"], PanelThemes.TextContrast, [palette["fill"].Over(palette.Surface)]),
                ("secondary on fill", palette["textSecondary"], PanelThemes.TextContrast, [palette["fill"].Over(palette.Surface)]),
                ("secondary on hover", palette["textSecondary"], PanelThemes.TextContrast, [palette["hover"].Over(palette.Surface)]),
                ("secondary on the selected row", palette["textSecondary"], PanelThemes.TextContrast, [palette["fillSelected"].Over(palette.Surface)]),
                ("secondary on pressed", palette["textSecondary"], PanelThemes.TextContrast, [palette["pressed"].Over(palette.Surface)]),
                ("primary on the selected row", palette["textPrimary"], PanelThemes.TextContrast, [palette["fillSelected"].Over(palette.Surface)]),
                ("primary on pressed", palette["textPrimary"], PanelThemes.TextContrast, [palette["pressed"].Over(palette.Surface)]),
            })
            {
                double ratio = color.MinimumContrast(backgrounds);
                if (ratio < minimum - .005) failures.Add($"{name}: {what} {ratio:0.00} < {minimum}");
            }
            // The built-in themes are written to pass as they are.
            if (palette.Adjustments.Any(a => a.Key is "success" or "caution" or "textTertiary" or "textSecondary")) failures.Add($"{name}: adjusted " + string.Join(", ", palette.Adjustments.Select(a => a.Key)));
        }
        check("Every built-in theme's page colors read on the fills they stand on, as written" + (failures.Count > 0 ? ": " + string.Join("; ", failures) : ""), failures.Count == 0);

        // A tertiary text that misses 4.5:1 on the fill is moved, just far enough (the page's buttons and strip sit on the fill).
        var faint = new PanelThemeFile { Colors = new() { ["surface"] = "#000000", ["fill"] = "#40FFFFFF", ["textTertiary"] = "#8A8A8A" } };
        var moved = PanelThemes.Build("faint", faint);
        var faintFill = moved["fill"].Over(moved.Surface);
        check("A tertiary text too faint for the fill is brought to 4.5:1 on it, and the change is listed",
            ThemeColor.Contrast(ThemeColor.Parse("#8A8A8A"), faintFill) < PanelThemes.TextContrast && moved.Adjustments.Any(a => a.Key == "textTertiary")
            && ThemeColor.Contrast(moved["textTertiary"].Over(faintFill), faintFill) >= PanelThemes.TextContrast - .005);
        // Secondary text on a pressed row, likewise.
        var heavy = new PanelThemeFile { Colors = new() { ["surface"] = "#000000", ["pressed"] = "#40FFFFFF", ["textSecondary"] = "#9E9E9E" } };
        var heavyBuilt = PanelThemes.Build("heavy", heavy);
        check("A secondary text too faint for the pressed fill is brought to 4.5:1 on it",
            heavyBuilt.Adjustments.Any(a => a.Key == "textSecondary") && ThemeColor.Contrast(heavyBuilt["textSecondary"], heavyBuilt["pressed"].Over(heavyBuilt.Surface)) >= PanelThemes.TextContrast - .005);

        // A custom theme that sets neither state color (it starts from the dark theme's) on a light surface still gets readable ones.
        var light = PanelThemes.Build("custom-light", new PanelThemeFile { Colors = new() { ["surface"] = "#FFFFFF", ["popup"] = "#FFFFFF", ["textPrimary"] = "#101010" } });
        check("A custom theme without the state colors gets readable ones on its own surface",
            light["success"].MinimumContrast(light.SurfaceStops) >= PanelThemes.TextContrast - .005 && light["caution"].MinimumContrast(light.SurfaceStops) >= PanelThemes.TextContrast - .005
            && light.Adjustments.Any(a => a.Key == "success") && light.Adjustments.Any(a => a.Key == "caution"));
        var darkCustom = PanelThemes.Build("custom-dark", new PanelThemeFile { Colors = new() { ["surface"] = "#101014" } });
        check("A custom theme on a dark surface keeps the dark theme's state colors",
            darkCustom["success"] == PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!)["success"] && darkCustom["caution"] == PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!)["caution"]);

        Sources(check);

        // High contrast sets both from the system's colors.
        var contrast = PanelThemes.HighContrast(ThemeColor.Parse("#000000"), ThemeColor.Parse("#FFFFFF"), ThemeColor.Parse("#3FF23F"), ThemeColor.Parse("#1AEBFF"), ThemeColor.Parse("#000000"));
        check("High contrast draws the page's states in the window text", contrast["success"] == ThemeColor.Parse("#FFFFFF") && contrast["caution"] == ThemeColor.Parse("#FFFFFF"));
    }

    // The page's code takes its colors from the theme: no color literals in the sources that draw the notices page, its letter plate,
    // its badges and its page in the settings, the states come from their own tokens, and the foot's fade is a mask.
    private static void Sources(Action<string, bool> check)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BrimDeck.sln"))) dir = dir.Parent;
        if (dir is null) { check("The source tree is found for the notices page's sources", false); return; }
        string Source(string name) => File.ReadAllText(Path.Combine(dir.FullName, "src", "BrimDeck", name));
        var files = new[] { "MainWindow.Notices.cs", "NoticeLetter.cs", "NoticeBadges.cs", "SettingsNotices.cs", "NoticePanels.cs" };
        // A color written out: "#RGB", "#RRGGBB", "#AARRGGBB", Color.FromRgb, a named Colors or Brushes member (Transparent aside).
        var literal = new System.Text.RegularExpressions.Regex(@"""#[0-9A-Fa-f]{3,8}""|\bColor\.FromRgb\(|\bColors\.(White|Black|Red|Green|Blue|Gray|Grey|Yellow|Orange|Purple|Pink|Cyan|Magenta|Brown|Silver|Gold)\b|\bBrushes\.(White|Black|Red|Green|Blue|Gray|Grey|Yellow|Orange|Purple|Pink|Cyan|Magenta|Brown|Silver|Gold)\b");
        var found = new List<string>();
        foreach (var file in files)
        {
            var text = Source(file);
            foreach (System.Text.RegularExpressions.Match match in literal.Matches(text))
                found.Add($"{file}: {match.Value} (line {text[..match.Index].Count(c => c == (char)10) + 1})");
        }
        check("The notices page's sources have no color literals" + (found.Count > 0 ? ": " + string.Join(", ", found) : ""), found.Count == 0);

        var page = Source("MainWindow.Notices.cs");
        check("The page's status colors are the theme's success, caution and danger, and its unread dot the accent",
            page.Contains("PanelTheme.Hex(\"success\")") && page.Contains("PanelTheme.Hex(\"caution\")") && page.Contains("PanelTheme.Hex(\"danger\")") && page.Contains("UnreadColor => UI.Accent"));
        check("A row's states take the selected, hover and pressed fills, each its own",
            page.Contains("selected ? \"fillSelected\"") && page.Contains("\"hover\"") && page.Contains("\"pressed\"") && !page.Contains("selected ? UI.Fill :"));
        check("Each of the page's four kinds of button goes through Feel, so each state has its own fill",
            System.Text.RegularExpressions.Regex.Matches(page, @"\bFeel\(button, ").Count == 4 && !page.Contains("face.Background = UI.Brush(UI.FillSelected); };"));
        check("The foot of the list and of a long text fade by a mask, not by a color laid over them",
            page.Contains("NoticeFade.Foot(scroll, FadeHeight)") && page.Contains("NoticeFade.Foot(scroll, WordsFadeHeight)") && !page.Contains("LinearGradientBrush"));
        var settings = Source("SettingsNotices.cs");
        check("The settings page's error text is the settings palette's, not a literal", settings.Contains("_palette.Negative") && !settings.Contains("F0A6AA") && !settings.Contains("B12E3A"));
        var letter = Source("NoticeLetter.cs");
        check("The letter on a plate is made readable on the plate", letter.Contains("EnsureContrast(plateOver"));
    }
}
