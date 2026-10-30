using System.Text.RegularExpressions;
using BrimDeck.Core;

// The shared font entry (UiFonts here, Fonts in the app) and the setting behind it: with no font chosen every role is the chain the
// app always used; a chosen font goes in front of it; the setting reads back, defaults for old files and survives hand edits; and no
// source names a font outside the entry.
internal static class FontTests
{
    // The strings that were written at every use before the entry existed (UI.cs, App.xaml, ToolbarResources.xaml, MainWindow.xaml,
    // MusicResources.xaml, OffPeakBar.cs). A change here is a change of how the app looks with no font chosen.
    private static readonly (UiFontRole Role, string Chain)[] Original =
    [
        (UiFontRole.Text, "Microsoft YaHei"),
        (UiFontRole.Panel, "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
        (UiFontRole.Display, "Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI"),
        (UiFontRole.Menu, "Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI"),
        (UiFontRole.Label, "Segoe UI")
    ];

    public static void Run(string root, Action<string, bool> check)
    {
        // Defaults.
        check("with no font chosen every role is the chain the app always used",
            Original.All(o => UiFonts.Chain(o.Role, "") == o.Chain && UiFonts.Chain(o.Role, null) == o.Chain && UiFonts.DefaultChain(o.Role) == o.Chain)
            && Enum.GetValues<UiFontRole>().Length == Original.Length);
        check("a name that is only blanks is no font", Original.All(o => UiFonts.Chain(o.Role, "   \t ") == o.Chain));

        // A chosen font.
        check("a chosen font goes in front of each role's own chain",
            Original.All(o => UiFonts.Chain(o.Role, "LXGW WenKai") == "LXGW WenKai, " + o.Chain));
        check("the chosen font is trimmed and its blanks made one", UiFonts.Chain(UiFontRole.Panel, "  Source   Han  Sans ").StartsWith("Source Han Sans, Segoe UI Variable Text"));
        check("a font the chain already names is not repeated, whatever its case",
            UiFonts.Chain(UiFontRole.Panel, "segoe ui") == "segoe ui, Segoe UI Variable Text, Microsoft YaHei UI"
            && UiFonts.Chain(UiFontRole.Label, "Segoe UI") == "Segoe UI");
        check("the Chinese fallback survives in every text role of a custom font",
            new[] { UiFontRole.Panel, UiFontRole.Display, UiFontRole.Menu, UiFontRole.Text }.All(role => UiFonts.Chain(role, "Inter").Contains("YaHei")));
        check("the icon and code chains are fixed and never take a font",
            UiFonts.IconChain == "Segoe Fluent Icons, Segoe MDL2 Assets" && UiFonts.IconMdl2Chain == "Segoe MDL2 Assets" && UiFonts.MonoChain == "Cascadia Mono, Consolas");

        // Normalization keeps a hand-edited file from passing extra families, a path or a URI into the chain.
        string[] bad = ["Inter, Arial", "a;b", "\"Inter\"", "'Inter'", "pack://application:,,,/x#Inter", "./Fonts/#Inter", "C:\\Fonts\\Inter", "a/b", "Inter\u0001",
            "<b>", "x|y", "wild*", "what?", new string('A', UiFonts.MaxNameLength + 1)];
        check("a name that could not be one family is no font", bad.All(name => UiFonts.Normalize(name) == "" && UiFonts.Chain(UiFontRole.Panel, name) == Original[1].Chain));
        check("ordinary family names, Chinese ones and digits among them, are kept",
            UiFonts.Normalize("Segoe UI Variable") == "Segoe UI Variable" && UiFonts.Normalize("微软雅黑") == "微软雅黑" && UiFonts.Normalize("Noto Sans SC Medium") == "Noto Sans SC Medium"
            && UiFonts.Normalize("Source Han Sans CN-Bold") == "Source Han Sans CN-Bold" && UiFonts.Normalize(new string('A', UiFonts.MaxNameLength)).Length == UiFonts.MaxNameLength);

        // The setting.
        check("the font setting starts empty", new DeckSettings().UiFont == "");
        var messy = new DeckSettings { UiFont = "  Inter   Display " }; messy.Normalize();
        var hostile = new DeckSettings { UiFont = "Inter, Evil" }; hostile.Normalize();
        var missing = new DeckSettings { UiFont = null! }; missing.Normalize();
        check("the font setting is normalized with the others", messy.UiFont == "Inter Display" && hostile.UiFont == "" && missing.UiFont == "");
        check("a copy of the settings keeps the font", new DeckSettings { UiFont = "Inter" }.Copy().UiFont == "Inter");

        var store = new SettingsStore(Path.Combine(root, "font-setting"), "0.1.0");
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","Width":900}""");
        check("a file saved before the setting reads as no font", store.Load().UiFont == "" && store.Load().Width == 900);
        var chosen = store.Load(); chosen.UiFont = "Inter"; store.Save(chosen);
        check("a chosen font is saved and read back", File.ReadAllText(store.FilePath).Contains("\"UiFont\": \"Inter\"") && new SettingsStore(store.DirectoryPath, "0.1.0").Load().UiFont == "Inter");
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","UiFont":"Inter, Arial"}""");
        check("a hand-edited list of families in the file reads as no font", store.Load().UiFont == "");
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","UiFont":null}""");
        check("a null font in the file reads as no font", store.Load().UiFont == "");
        var reset = new DeckSettings { UiFont = "Inter" }; reset.UiFont = new DeckSettings().UiFont;
        check("restoring the default is the empty name", reset.UiFont == "" && UiFonts.Chain(UiFontRole.Text, reset.UiFont) == Original[0].Chain);

        // The sources. Every font the app draws text in is named in Fonts.cs and UiFonts.cs and nowhere else; App.xaml carries their
        // defaults under the keys Fonts publishes again.
        var offenders = Offences("a.cs", "var f = new FontFamily(\"Segoe UI\");");
        check("the scan finds a font named in code", offenders.Count == 1
            && Offences("b.cs", "block.FontFamily = new(\"Consolas\");").Count == 1
            && Offences("c.cs", "x.FontFamily = \"Arial\";").Count == 1
            && Offences("d.cs", "new Typeface(\"Segoe UI\")").Count >= 1
            && Offences("e.cs", "var n = \"Microsoft YaHei UI\";").Count == 1
            && Offences("f.xaml", "<TextBlock FontFamily=\"Segoe UI\"/>").Count == 1
            && Offences("g.xaml", "<Setter Property=\"FontFamily\" Value=\"Consolas\"/>").Count == 1);
        check("the scan lets through what goes by the entry",
            Offences("a.cs", "block.FontFamily = UI.PanelFont; var t = new Typeface(Fonts.Panel, a, b, c); // Segoe UI in a comment").Count == 0
            && Offences("b.cs", "var o = Bundled(\"Orbitron\");").Count == 0
            && Offences("c.xaml", "<TextBlock FontFamily=\"{DynamicResource FontPanel}\"/><Setter Property=\"FontFamily\" Value=\"{StaticResource FontIcon}\"/>").Count == 0
            && Offences("d.xaml", "<TextBlock FontFamily=\"{TemplateBinding FontFamily}\"/><!-- FontFamily=\"Segoe UI\" -->").Count == 0);

        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "BrimDeck.sln"))) source = source.Parent;
        check("the source tree is found for the font scan", source is not null);
        if (source is null) return;
        var app = Path.Combine(source.FullName, "src", "BrimDeck");
        var files = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(file => (file.EndsWith(".cs") || file.EndsWith(".xaml")) && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && Path.GetFileName(file) != "Fonts.cs").ToList();
        check("the app's sources are found for the font scan", files.Count > 100 && files.Any(file => file.EndsWith("App.xaml")));
        var found = files.SelectMany(file => Offences(file, File.ReadAllText(file))).ToList();
        check("no source names a font outside Fonts.cs: " + string.Join("; ", found.Take(5).Select(f => Path.GetFileName(f.File) + " " + f.Text)), found.Count == 0);

        var xaml = File.ReadAllText(Path.Combine(app, "App.xaml"));
        string Declared(string key) => Regex.Match(xaml, $@"<FontFamily x:Key=""{key}"">([^<]*)</FontFamily>") is { Success: true } m ? m.Groups[1].Value : "<missing>";
        var keys = new Dictionary<UiFontRole, string> { [UiFontRole.Text] = "AppTextFont", [UiFontRole.Panel] = "FontPanel", [UiFontRole.Display] = "FontDisplay", [UiFontRole.Menu] = "FontMenu", [UiFontRole.Label] = "FontLabel" };
        check("App.xaml declares each role's default chain under its resource key", Original.All(o => Declared(keys[o.Role]) == o.Chain));
        check("App.xaml declares the fixed icon and code families", Declared("FontIcon") == UiFonts.IconChain && Declared("FontIconMdl2") == UiFonts.IconMdl2Chain && Declared("FontMono") == UiFonts.MonoChain);
        var fonts = File.ReadAllText(Path.Combine(app, "Fonts.cs"));
        check("Fonts publishes each role under the key its XAML uses", keys.All(pair => fonts.Contains($"UiFontRole.{pair.Key} => \"{pair.Value}\"") || pair.Key == UiFontRole.Panel && fonts.Contains($"_ => \"{pair.Value}\"")));
        check("Fonts takes its chains from UiFonts",
            fonts.Contains("UiFonts.Chain(role, _custom)") && fonts.Contains("UiFonts.IconChain") && fonts.Contains("UiFonts.IconMdl2Chain") && fonts.Contains("UiFonts.MonoChain"));
        var ui = File.ReadAllText(Path.Combine(app, "UI.cs"));
        check("UI's three families are Fonts' and read at each use, so a new font reaches them",
            ui.Contains("TextFont => Fonts.Text;") && ui.Contains("PanelFont => Fonts.Panel;") && ui.Contains("DisplayFont => Fonts.Display;"));
    }

    private sealed record Offence(string File, string Text);

    // The ways a source can name a font itself. Comments, the entry's own Bundled(...) names (the clock styles' shipped faces) and
    // XAML that refers to a resource or a template are not.
    private static readonly Regex[] CodeRules =
    [
        new(@"new\s+FontFamily\s*\("),
        new(@"FontFamily\s*=\s*new\b"),
        new(@"FontFamily\s*=\s*"""),
        new(@"new\s+Typeface\s*\(\s*"""),
        new(@"""[^""]*\b(Segoe|Microsoft YaHei|Consolas|Cascadia|Arial|Calibri|Helvetica|Times New Roman|Courier|Tahoma|Verdana|SimSun|SimHei|Bahnschrift)\b[^""]*""")
    ];
    private static readonly Regex XamlFontValue = new(@"FontFamily=""([^""]*)""|Property=""FontFamily""\s+Value=""([^""]*)""");
    private static readonly Regex XamlAllowed = new(@"^\{(Dynamic|Static)Resource (Font\w+|AppTextFont)\}$|^\{TemplateBinding FontFamily\}$|^\{Binding\b[^}]*\}$");

    private static List<Offence> Offences(string file, string text)
    {
        var found = new List<Offence>();
        if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            var bare = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);
            // App.xaml's own declarations of the default chains are the one place XAML spells a family (as element content, not as a property value).
            foreach (Match m in XamlFontValue.Matches(bare))
            {
                var value = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!XamlAllowed.IsMatch(value)) found.Add(new Offence(file, m.Value));
            }
            return found;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = Regex.Replace(raw, @"(^|\s)//.*$", "$1");
            line = Regex.Replace(line, @"Bundled\(""[^""]*""\)", "Bundled()");
            foreach (var rule in CodeRules)
                if (rule.Match(line) is { Success: true } hit) { found.Add(new Offence(file, hit.Value)); break; }
        }
        return found;
    }
}
