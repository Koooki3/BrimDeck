using System.Globalization;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

internal static class NoticeGlyphTests
{
    internal static void Run(Action<string, bool> check)
    {
        // Every glyph has well-formed path data on the 20 unit grid, and every badge mark as well.
        var badPaths = NoticeGlyphs.All.Where(glyph => !OnGrid(NoticeGlyphs.PathData(glyph)) || !OnGrid(NoticeGlyphs.BadgeData(glyph, out _))).ToList();
        check("Notice glyph paths are well formed and stay on the 20 unit grid", badPaths.Count == 0);
        check("Notice glyphs all have a screen reader name", NoticeGlyphs.All.All(glyph => NoticeGlyphs.Name(glyph) is { Length: > 0 } name && name != glyph.ToString() || glyph == NoticeGlyph.Linux));

        // Kinds named by sources, the time kinds included, map to glyphs; unknown names have none.
        string[] kinds = ["message", "mail", "alarm", "timer", "stopwatch", "countdown", "calendar", "event", "meeting", "call", "task", "progress",
            "success", "done", "warning", "error", "failed", "info", "download", "upload", "update", "security", "shield", "system", "windows",
            "battery", "power", "network", "media", "terminal", "wsl", "phone", "browser", "assistant", "build", "ci", "game", "music", "video", "cloud", "code", "chat"];
        check("Notice kinds map to glyphs", kinds.All(kind => NoticeGlyphs.Kind(kind) is not null) && NoticeGlyphs.Kind("TIMER") == NoticeGlyph.Timer
            && NoticeGlyphs.Kind("event") == NoticeGlyph.Calendar && NoticeGlyphs.Kind("countdown") == NoticeGlyph.Countdown);
        check("Unknown notice kinds have no glyph", NoticeGlyphs.Kind("frobnicate") is null && NoticeGlyphs.Kind("") is null && NoticeGlyphs.Kind(null) is null);
        check("Every glyph is reachable by some kind name", NoticeGlyphs.All.All(glyph => NoticeGlyphs.Kinds.Values.Contains(glyph)));
        check("Notice categories map to state glyphs; a plain message has none",
            NoticeGlyphs.Status(NoticeCategory.Success) == NoticeGlyph.Success && NoticeGlyphs.Status(NoticeCategory.Error) == NoticeGlyph.Error
            && NoticeGlyphs.Status(NoticeCategory.Warning) == NoticeGlyph.Warning && NoticeGlyphs.Status(NoticeCategory.Info) == NoticeGlyph.Info
            && NoticeGlyphs.Status(NoticeCategory.Task) == NoticeGlyph.Progress && NoticeGlyphs.Status(NoticeCategory.Message) is null
            && Enum.GetValues<NoticeCategory>().All(category => category == NoticeCategory.Message || NoticeGlyphs.Status(category) is not null));
        check("A named kind wins over the category", NoticeGlyphs.Status(NoticeCategory.Message, "alarm") == NoticeGlyph.Alarm
            && NoticeGlyphs.Status(NoticeCategory.Error, "unknown-kind") == NoticeGlyph.Error);
        check("State glyphs take the state tones", NoticeGlyphs.Tone(NoticeGlyph.Error) == NoticeGlyphTone.Danger && NoticeGlyphs.Tone(NoticeGlyph.Success) == NoticeGlyphTone.Success
            && NoticeGlyphs.Tone(NoticeGlyph.Warning) == NoticeGlyphTone.Warning && NoticeGlyphs.Tone(NoticeGlyph.Info) == NoticeGlyphTone.Info);

        // Sources: only notices not from the Windows application itself get a source badge.
        check("Notice sources map to badges", NoticeGlyphs.Source(NoticeOrigin.Wsl, NoticeKeys.Wsl("Ubuntu", "notify-send")) == NoticeGlyph.Linux
            && NoticeGlyphs.Source(NoticeOrigin.Cli, NoticeKeys.Cli(null, "build")) == NoticeGlyph.Terminal
            && NoticeGlyphs.Source(NoticeOrigin.Windows, NoticeKeys.Windows("Microsoft.YourPhone_8wekyb3d8bbwe!App")) == NoticeGlyph.Phone
            && NoticeGlyphs.Source(NoticeOrigin.Windows, NoticeKeys.Windows("Chrome._crx_abcdefghijklmnop")) == NoticeGlyph.Browser
            && NoticeGlyphs.Source(NoticeOrigin.Windows, NoticeKeys.Windows("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App")) is null
            && NoticeGlyphs.Source(NoticeOrigin.Test, "test") is null);

        // Application types from keys and names; unknown applications keep their initial.
        (string Key, string Name, NoticeOrigin Origin, NoticeGlyph? Expected)[] apps =
        [
            (NoticeKeys.Windows("Tencent.WeChat"), "微信", NoticeOrigin.Windows, NoticeGlyph.Chat),
            (NoticeKeys.Windows("Tencent.QQ"), "QQ", NoticeOrigin.Windows, NoticeGlyph.Chat),
            (NoticeKeys.Windows("Tencent.QQMusic"), "QQ音乐", NoticeOrigin.Windows, NoticeGlyph.Music),
            (NoticeKeys.Windows("com.squirrel.slack.slack"), "Slack", NoticeOrigin.Windows, NoticeGlyph.Chat),
            (NoticeKeys.Windows("microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.mail"), "Mail", NoticeOrigin.Windows, NoticeGlyph.Mail),
            (NoticeKeys.Windows("Microsoft.Office.OUTLOOK.EXE.15"), "Outlook", NoticeOrigin.Windows, NoticeGlyph.Mail),
            (NoticeKeys.Windows("microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.calendar"), "Calendar", NoticeOrigin.Windows, NoticeGlyph.Calendar),
            (NoticeKeys.Windows("MSEdge"), "Microsoft Edge", NoticeOrigin.Windows, NoticeGlyph.Browser),
            (NoticeKeys.Windows("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"), "Terminal", NoticeOrigin.Windows, NoticeGlyph.Terminal),
            (NoticeKeys.Windows("Valve.Steam.Client"), "Steam", NoticeOrigin.Windows, NoticeGlyph.Game),
            (NoticeKeys.Windows("Spotify.exe"), "Spotify", NoticeOrigin.Windows, NoticeGlyph.Music),
            (NoticeKeys.Windows("Microsoft.SkyDrive.Desktop"), "OneDrive", NoticeOrigin.Windows, NoticeGlyph.Cloud),
            (NoticeKeys.Windows("Microsoft.VisualStudioCode"), "Visual Studio Code", NoticeOrigin.Windows, NoticeGlyph.Code),
            (NoticeKeys.Windows("Claude"), "Claude", NoticeOrigin.Windows, NoticeGlyph.Assistant),
            (NoticeKeys.Windows("GitHub.GitHubDesktop"), "GitHub Desktop", NoticeOrigin.Windows, NoticeGlyph.Build),
            (NoticeKeys.Windows("Windows.SystemToast.SecurityAndMaintenance"), "Security and Maintenance", NoticeOrigin.Windows, NoticeGlyph.Security),
            (NoticeKeys.Windows("Microsoft.YourPhone_8wekyb3d8bbwe!App"), "Phone Link", NoticeOrigin.Windows, NoticeGlyph.Phone),
            (NoticeKeys.Windows("VideoLAN.VLC"), "VLC media player", NoticeOrigin.Windows, NoticeGlyph.Video),
            (NoticeKeys.Cli(null, "deploy"), "deploy", NoticeOrigin.Cli, NoticeGlyph.Terminal),
            (NoticeKeys.Wsl("Ubuntu", "notify-send"), "notify-send", NoticeOrigin.Wsl, NoticeGlyph.Linux),
            (NoticeKeys.Wsl("Ubuntu", "Slack"), "Slack", NoticeOrigin.Wsl, NoticeGlyph.Chat),
            (NoticeKeys.Windows("Contoso.Widget"), "Contoso Widget", NoticeOrigin.Windows, null),
            (NoticeKeys.Windows("Example.Pipeline"), "Online Outline", NoticeOrigin.Windows, NoticeGlyph.Build),
            (NoticeKeys.Windows("Example.Viewer"), "Outline", NoticeOrigin.Windows, null),
        ];
        var wrong = apps.Where(app => NoticeGlyphs.App(app.Key, app.Name, app.Origin) != app.Expected).Select(app => app.Name).ToList();
        check("Application types are told from keys and names (" + string.Join(", ", wrong) + ")", wrong.Count == 0);

        // Colors on every built-in theme and on a custom theme with a light gradient and weak state colors.
        var custom = PanelThemes.Build("glyph-test", PanelThemes.Parse("""
            {"base":"light","colors":{"surface":"#F4F1EA","danger":"#FFB3B3","accent":"#C8E6FF","fill":"#0F000000"},
             "apps":{"warning":"#FFE680"},"effects":{"surfaceGradient":["#FFFFFF","#E9F2FF"]}}
            """));
        var palettes = PanelThemes.BuiltIns.Select(PanelThemes.Resolve).Append(custom).ToList();
        var weak = new List<string>();
        foreach (var palette in palettes)
        {
            var colors = NoticeGlyphColors.FromPalette(palette);
            foreach (var tone in Enum.GetValues<NoticeGlyphTone>())
            {
                var color = colors.Tone(tone);
                if (!color.IsOpaque || color.MinimumContrast(colors.Backdrops) < NoticeGlyphColors.Minimum - .01) weak.Add(palette.Id + ":" + tone);
                // A badge's mark is drawn in the surface color on the state color.
                if (ThemeColor.Contrast(colors.BadgeInk, color) < NoticeGlyphColors.Minimum - .01) weak.Add(palette.Id + ":ink-" + tone);
            }
            foreach (var preset in AppPresets.Palette.Append("#FFFFFF").Append("#000000").Append("#FFE000"))
                if (colors.OnPlate(ThemeColor.Parse(preset)).MinimumContrast(colors.Backdrops) < NoticeGlyphColors.Minimum - .01) weak.Add(palette.Id + ":app" + preset);
        }
        check("Notice glyph colors keep 3:1 on every theme's surface and plate (" + string.Join(", ", weak.Take(6)) + ")", weak.Count == 0 && palettes.Count >= 9);
        var lightTheme = NoticeGlyphColors.FromPalette(PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Light)!));
        var darkTheme = NoticeGlyphColors.FromPalette(PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!));
        check("Notice glyph colors follow the theme", lightTheme.Primary != darkTheme.Primary && lightTheme.Surface != darkTheme.Surface && !lightTheme.HighContrast);

        // High contrast: system colors only, and a scheme whose highlight does not stand out falls back to the text color.
        (string Window, string Text, string Control, string Highlight, string HighlightText)[] schemes =
        [
            ("#000000", "#FFFFFF", "#000000", "#1AEBFF", "#000000"), ("#FFFFFF", "#000000", "#FFFFFF", "#37006E", "#FFFFFF"),
            ("#202020", "#FFFFFF", "#202020", "#8EE3F0", "#263B50"), ("#FFFAEF", "#3D3D3D", "#FFFAEF", "#903909", "#FFF5E3"),
            ("#000000", "#FFFFFF", "#000000", "#101010", "#FFFFFF"),
        ];
        bool systemOnly = true, systemReads = true;
        foreach (var s in schemes)
        {
            var colors = NoticeGlyphColors.FromSystem(ThemeColor.Parse(s.Window), ThemeColor.Parse(s.Text), ThemeColor.Parse(s.Control), ThemeColor.Parse(s.Highlight), ThemeColor.Parse(s.HighlightText));
            var allowed = new[] { s.Window, s.Text, s.Control, s.Highlight, s.HighlightText }.Select(ThemeColor.Parse).ToHashSet();
            foreach (var tone in Enum.GetValues<NoticeGlyphTone>())
            {
                var color = colors.Tone(tone);
                systemOnly &= allowed.Contains(color) && allowed.Contains(colors.BadgeInk);
                systemReads &= color.MinimumContrast(colors.Backdrops) >= NoticeGlyphColors.Minimum && ThemeColor.Contrast(colors.BadgeInk, color) >= NoticeGlyphColors.Minimum;
            }
            systemOnly &= allowed.Contains(colors.OnPlate(ThemeColor.Parse("#FF8800"))) && colors.HighContrast;
        }
        check("High contrast notice glyphs use system colors only", systemOnly);
        check("High contrast notice glyphs keep 3:1, also when the highlight is too dark", systemReads);
    }

    // A small reader for the absolute path commands the glyphs use (M L H V C Q A Z): each command has whole sets of numbers,
    // and every point lies on the grid.
    private static bool OnGrid(string data)
    {
        var tokens = System.Text.RegularExpressions.Regex.Matches(data, @"[MLHVCQAZ]|-?\d+(\.\d+)?").Select(match => match.Value).ToList();
        if (tokens.Count == 0 || tokens[0] != "M") return false;
        if (System.Text.RegularExpressions.Regex.Replace(data, @"[MLHVCQAZ]|-?\d+(\.\d+)?|[\s,]", "").Length > 0) return false;
        int i = 0;
        bool Inside(double value) => value is >= 0 and <= NoticeGlyphs.Grid;
        while (i < tokens.Count)
        {
            string command = tokens[i++];
            int arity = command switch { "M" or "L" => 2, "H" or "V" => 1, "C" => 6, "Q" => 4, "A" => 7, "Z" => 0, _ => -1 };
            if (arity < 0) return false;
            var numbers = new List<double>();
            while (i < tokens.Count && !char.IsLetter(tokens[i][0])) numbers.Add(double.Parse(tokens[i++], CultureInfo.InvariantCulture));
            if (arity == 0 ? numbers.Count != 0 : numbers.Count == 0 || numbers.Count % arity != 0) return false;
            for (int n = 0; n < numbers.Count; n++)
            {
                int at = n % Math.Max(arity, 1);
                // An arc's radii, rotation and flags are not points.
                if (command == "A" && at < 5) continue;
                if (!Inside(numbers[n])) return false;
            }
        }
        return true;
    }
}
