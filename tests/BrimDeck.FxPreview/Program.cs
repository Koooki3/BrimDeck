using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BrimDeck;
using BrimDeck.Core;
using Path = System.Windows.Shapes.Path;

// Checks the answers to the pointer (InteractionFx) on real WPF controls styled as on the panel, and, given a folder, renders
// for every built-in theme frame strips of a panel snippet over the hover and press timelines: <theme>-hover.png,
// <theme>-press.png and <theme>-cases.png (the panel opening under a hovered control, a theme switch mid-press, keyboard focus,
// a disabled control, animations off). The snippet is hosted in an HwndSource that is never shown; BrimDeck is not started and
// no settings or theme files are read. Not part of BrimDeck.sln; run it by hand:
//   dotnet run -c Release --project tests/BrimDeck.FxPreview -- <output folder> [--dpi 1.5] [--themes id,id,...]
// The checks run at 100, 125, 150 and 175 percent display scaling (VisualTreeHelper.SetRootDpi on the snippet, so layout,
// GetDpi and the layer's pixel snapping all see the scale). The strips are drawn at --dpi (100 percent when not given): at 100
// percent at three times the size, otherwise pixel for pixel at that scale, each device pixel shown as a 2 x 2 block.
internal static class Program
{
    private const double Scale = 3;
    private static readonly double[] Scales = [1, 1.25, 1.5, 1.75];
    private static int _failures;
    private static double _now;
    private static double _dpi = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        // The real panel's pages over liquid glass (LiquidPages): --liquid-pages <backgrounds> <output folder>. It needs BrimDeck's own
        // App (never run) instead of a plain Application, so it starts before one is made.
        // [--scale <percent>] (or BRIMDECK_PAGES_SCALE) draws them at another display scale than 150 percent.
        // The real panel's pages in every built-in theme and look (LiquidPages.RunThemes): --theme-pages <output folder> [--scale 150]
        // [--text-scale 125] [--themes ids]. --text-scale stands in for Windows' text size (TextScale).
        // What each theme costs to draw (LiquidPages.RunCost): --theme-cost [--scale 150] [--rounds 120] [--themes id,id,...].
        if (args.Length > 0 && args[0] == "--theme-cost")
        {
            int at = Array.IndexOf(args, "--themes"), rounds = Array.IndexOf(args, "--rounds");
            return LiquidPages.RunCost(LiquidPages.ScaleFrom(args) ?? 1.5, at > 0 && at + 1 < args.Length ? args[at + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet() : null,
                rounds > 0 && rounds + 1 < args.Length ? int.Parse(args[rounds + 1], CultureInfo.InvariantCulture) : 120);
        }
        // The monitor page with the machine's power and battery, and the collapsed row's readings, in every theme (PowerPages): --power-pages <folder> [--scale 150] [--themes ids] [--sizes]
        if (args.Length > 0 && args[0] == "--smi-crash") return LiquidPages.RunSmiCrash();
        if (args.Length > 0 && args[0] == "--power-colors") return LiquidPages.RunPowerColors();
        if (args.Length > 0 && args[0] == "--power-memory") return LiquidPages.RunPowerMemory();
        if (args.Length > 0 && args[0] == "--power-cost") return LiquidPages.RunPowerCost(args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 60);
        if (args.Length > 1 && args[0] == "--power-pages")
        {
            if (LiquidPages.ScaleFrom(args) is not { } powerScale) { Console.WriteLine("FAIL: --scale wants a display scale such as 100, 125, 150"); return 1; }
            int named = Array.IndexOf(args, "--themes");
            return LiquidPages.RunPower(args[1], powerScale, named > 0 && named + 1 < args.Length ? args[named + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet() : null,
                args.Contains("--sizes"), args.Contains("--en"));
        }
        if (args.Length > 1 && args[0] == "--theme-pages")
        {
            if (LiquidPages.ScaleFrom(args) is not { } themeScale) { Console.WriteLine("FAIL: --scale wants a display scale such as 100, 125, 150"); return 1; }
            int text = Array.IndexOf(args, "--text-scale");
            double textScale = 1;
            if (text > 0 && (text + 1 >= args.Length || !double.TryParse(args[text + 1], System.Globalization.CultureInfo.InvariantCulture, out textScale) || textScale < 100 || textScale > 225))
            { Console.WriteLine("FAIL: --text-scale wants Windows' text size, 100 to 225"); return 1; }
            int listed = Array.IndexOf(args, "--themes");
            return LiquidPages.RunThemes(args[1], themeScale, listed > 0 && listed + 1 < args.Length ? args[listed + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet() : null,
                text > 0 ? TextScale.Clamp(textScale) : 1);
        }
        // The collapsed row giving way to a quota alert and back, frame by frame in every theme and collapsed form (AlertReturn):
        // --alert-return [<output folder>] [--themes ids].
        if (args.Length > 0 && args[0] == "--alert-return")
        {
            int themes = Array.IndexOf(args, "--themes");
            return AlertReturn.Run(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null,
                themes > 0 && themes + 1 < args.Length ? args[themes + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet() : null);
        }
        if (args.Length > 2 && args[0] == "--liquid-pages")
        {
            if (LiquidPages.ScaleFrom(args) is not { } pagesScale) { Console.WriteLine("FAIL: --scale (or BRIMDECK_PAGES_SCALE) wants a display scale such as 100, 125, 150 or 175"); return 1; }
            // --focus: the keyboard focus pass instead of the page sheets (LiquidPages.Focus.cs).
            return args.Contains("--focus") ? LiquidPages.RunFocus(args[1], args[2], pagesScale) : LiquidPages.Run(args[1], args[2], pagesScale);
        }
        // The glass's answers to the pointer and its accessibility (GlassAccess): hover and press, the content row, transparency
        // effects off and animations off, on the real panel offscreen: --glass-access.
        if (args.Length > 0 && args[0] == "--glass-access") return GlassAccess.Run();
        // The acrylic body's edge as composition draws it, on an off-screen window: --edge-probe <folder>.
        if (args.Length > 1 && args[0] == "--edge-probe") return EdgeProbe.Run(args[1]);
        // What the island's shadow costs while the panel opens and closes over liquid glass (ShadowCost): --shadow-cost [cycles].
        if (args.Length > 0 && args[0] == "--shadow-cost") return ShadowCost.Run(args.Length > 1 && int.TryParse(args[1], out int cycles) ? cycles : 6);
        // What liquid glass costs against the blur, collapsed with music, open and animating (PerfCost): --perf [seconds] [rounds] [--hidden].
        if (args.Length > 0 && args[0] == "--perf") return PerfCost.Run(args);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var key in new[] { "PanelKeyboardFocus", "PanelWideKeyboardFocus", "PanelOutsetKeyboardFocus" }) app.Resources[key] = new Style();
        app.Resources["AppTextFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        foreach (var dictionary in new[] { "ToolbarResources.xaml", "MusicResources.xaml", "ModelDetailsResources.xaml" })
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/BrimDeck;component/" + dictionary, UriKind.Relative)));
        // The theme's brushes and corners (PanelRadius*), as App.xaml and PanelTheme give them to the panel's XAML.
        PanelTheme.Show(PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!));

        // The date range picker's strips (DatePreview): --date <output folder>.
        if (args.Length > 1 && args[0] == "--date") return DatePreview.Run(args[1]) == 0 ? 0 : 1;
        // The liquid glass sheets (LiquidPreview): --liquid <output folder>.
        if (args.Length > 1 && args[0] == "--liquid") return LiquidPreview.Run(args[1]);
        // The same over top-of-screen backgrounds, for the comparison with the references: --liquid-bg <backgrounds> <output folder>.
        if (args.Length > 2 && args[0] == "--liquid-bg") return LiquidPreview.RunBackgrounds(args[1], args[2]);
        // The port proof (the glass as liquid-glass-web draws it): --liquid-port <backgrounds> <output folder> [--island].
        if (args.Length > 2 && args[0] == "--liquid-port") return LiquidPreview.RunPort(args[1], args[2], !args.Contains("--island"));
        // Every state of the glass (shapes, opening frames, pressed, hovered, the pages' sizes): --liquid-states <backgrounds> <output folder>.
        if (args.Length > 2 && args[0] == "--liquid-states") return LiquidPreview.RunStates(args[1], args[2]);
        // The liquid glass shaders compiled ahead into src/BrimDeck/LiquidGlass (Liquid.vs.cso, Liquid.ps.cso, Liquid.hlsl.sha256):
        // --compile-liquid <the LiquidGlass folder>. Run it after every change to Liquid.hlsl; WindowsTests checks it was.
        if (args.Length > 1 && args[0] == "--compile-liquid")
        {
            var source = File.ReadAllBytes(System.IO.Path.Combine(args[1], "Liquid.hlsl"));
            var (vertex, pixel, zoned) = BrimDeck.LiquidGlass.LiquidRenderer.Compiled(source);
            File.WriteAllBytes(System.IO.Path.Combine(args[1], "Liquid.vs.cso"), vertex);
            File.WriteAllBytes(System.IO.Path.Combine(args[1], "Liquid.ps.cso"), pixel);
            File.WriteAllBytes(System.IO.Path.Combine(args[1], "Liquid.zoned.cso"), zoned);
            File.WriteAllText(System.IO.Path.Combine(args[1], "Liquid.hlsl.sha256"), BrimDeck.LiquidGlass.LiquidRenderer.SourceHash(source) + "\n");
            Console.WriteLine($"Compiled Liquid.hlsl: vertex {vertex.Length} bytes, pixel {pixel.Length} bytes, zoned {zoned.Length} bytes");
            // The frost's pyramid and stats (Frost.hlsl), alike.
            var frost = File.ReadAllBytes(System.IO.Path.Combine(args[1], "Frost.hlsl"));
            var (down, up, band, stats) = BrimDeck.LiquidGlass.LiquidRenderer.CompiledFrost(frost);
            File.WriteAllBytes(System.IO.Path.Combine(args[1], "Frost.down.cso"), down);
            File.WriteAllBytes(System.IO.Path.Combine(args[1], "Frost.up.cso"), up);
            File.WriteAllBytes(System.IO.Path.Combine(args[1], "Frost.band.cso"), band);
            File.WriteAllBytes(System.IO.Path.Combine(args[1], "Frost.stats.cso"), stats);
            File.WriteAllText(System.IO.Path.Combine(args[1], "Frost.hlsl.sha256"), BrimDeck.LiquidGlass.LiquidRenderer.SourceHash(frost) + "\n");
            Console.WriteLine($"Compiled Frost.hlsl: down {down.Length} bytes, up {up.Length} bytes, band {band.Length} bytes, stats {stats.Length} bytes");
            return 0;
        }
        // Only the GPU timing of a liquid glass frame: --liquid-time.
        if (args.Length > 0 && args[0] == "--liquid-time") return LiquidPreview.TimeOnly();
        // The frost's temporal stability over a moving page (FrostCheck): --frost-check <output folder>.
        if (args.Length > 1 && args[0] == "--frost-check") return FrostCheck.Run(args[1]);
        // The frost's levels' deviations (LiquidRenderer.LevelSigma): --frost-calibrate.
        if (args.Length > 0 && args[0] == "--frost-calibrate") return FrostCheck.Calibrate();
        // The backdrop's stats per tile, as images and tables: --frost-stats <backgrounds> <output folder>.
        if (args.Length > 2 && args[0] == "--frost-stats") return FrostCheck.Stats(args[1], args[2]);
        // Automatic liquid glass turning light and dark over a split background: --liquid-auto <output folder>.
        if (args.Length > 1 && args[0] == "--liquid-auto") return LiquidPreview.RunAuto(args[1]);
        // Automatic liquid glass's decision over video-like backdrops, in both Windows app modes, against the round-11 decision:
        // --liquid-looks.
        if (args.Length > 0 && args[0] == "--liquid-looks") return LiquidLooks.Run();
        // The devices page in every theme (DevicesSheet): --devices <output folder>; the devices on this PC, read only: --devices-probe.
        if (args.Length > 1 && args[0] == "--devices") return DevicesSheet.Run(args[1]);
        // A device's low battery notice in every theme (DeviceNoticeSheet): --device-notice <output folder>.
        if (args.Length > 1 && args[0] == "--device-notice") return DeviceNoticeSheet.Run(args[1]);
        if (args.Length > 0 && args[0] == "--devices-cost") return DevicesSheet.Cost(args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 60);
        if (args.Length > 0 && args[0] == "--devices-probe") return DevicesSheet.Probe(args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 12);
        // The settings page's theme cards (ThemeCards): --cards <output folder> [--themes id,id,...] [--scale 1.5].
        if (args.Length > 1 && args[0] == "--cards")
        {
            int at = Array.IndexOf(args, "--themes"), zoom = Array.IndexOf(args, "--scale");
            return ThemeCards.Run(args[1], at > 0 && at + 1 < args.Length ? args[at + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet() : null,
                zoom > 0 && zoom + 1 < args.Length ? double.Parse(args[zoom + 1], CultureInfo.InvariantCulture) : 2);
        }
        // The theme design sheets (ThemeSheet): --sheet <output folder> [--themes id,id,...] [--dpi 1.5].
        if (args.Length > 1 && args[0] == "--sheet")
        {
            int at = Array.IndexOf(args, "--themes"), scale = Array.IndexOf(args, "--dpi");
            return ThemeSheet.Run(args[1], at > 0 && at + 1 < args.Length ? args[at + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet() : null,
                scale > 0 && scale + 1 < args.Length ? double.Parse(args[scale + 1], CultureInfo.InvariantCulture) : 1);
        }
        string? folder = null; double stripDpi = 1; HashSet<string>? only = null; bool upgrade = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--upgrade") upgrade = true;
            else if (args[i] == "--step" && i + 1 < args.Length) _step = double.Parse(args[++i], CultureInfo.InvariantCulture) / 1000;
            else if (args[i] == "--dpi" && i + 1 < args.Length) stripDpi = double.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--themes" && i + 1 < args.Length) only = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            else folder = args[i];
        }

        foreach (var dpi in Scales)
        {
            _dpi = dpi;
            Console.WriteLine($"--- {dpi * 100:0}% ---");
            Checks();
            PixelChecks();
            StairChecks();
            CutChecks();
        }
        // A bundled face that fails to load falls back to another font without an error, so each must be the file's own face.
        Check("The clock's bundled fonts load from BrimDeck's resources", new[]
        {
            ("Orbitron", FontWeights.SemiBold, "Orbitron SemiBold"), ("Unbounded", FontWeights.SemiBold, "Unbounded SemiBold"),
            ("Bebas Neue", FontWeights.Normal, "Bebas Neue"), ("Tektur", FontWeights.Medium, "Tektur Medium"), ("Fredoka", FontWeights.SemiBold, "Fredoka SemiBold")
        }.All(font => new Typeface(ClockFace.Bundled(font.Item1), FontStyles.Normal, font.Item2, FontStretches.Normal).TryGetGlyphTypeface(out var face)
            && face.Win32FamilyNames.Values.Contains(font.Item3)));
        _dpi = stripDpi;
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
            // --upgrade: only the motion upgrade's press effects, on theme files that opt in (no built-in theme does).
            if (upgrade)
            {
                foreach (var (id, json) in UpgradeThemes.Where(t => only is null || only.Contains(t.Id)))
                    UpgradeStrip(json.Length == 0 ? PanelThemes.Resolve(PanelThemes.BuiltIn(id)!)
                        : json == "night" ? PanelThemes.Resolve(PanelThemes.BuiltIn(id[..^"-night".Length])!, night: true)
                        : PanelThemes.Build(id, PanelThemes.Parse(json)), id, folder);
                Console.WriteLine($"Wrote upgrade strips at {stripDpi * 100:0}% to " + System.IO.Path.GetFullPath(folder));
                return _failures == 0 ? 0 : 1;
            }
            foreach (var theme in PanelThemes.BuiltIns.Where(t => only is null || only.Contains(t.Id)))
            {
                Strips(PanelThemes.Resolve(theme), theme.Id, folder);
                // A theme with a night look is drawn in it too, when asked for by name (the default run keeps the files it always made).
                if (only is not null && theme.File.Night is not null) Strips(PanelThemes.Resolve(theme, true), theme.Id + "-night", folder);
            }
            if (only is null || only.Contains("system-dark")) Strips(PanelThemes.Resolve(PanelThemes.BuiltIn("dark")!), "system-dark", folder, system: true);
            if (only is null || only.Contains("system-light")) Strips(PanelThemes.Resolve(PanelThemes.BuiltIn("light")!), "system-light", folder, system: true);
            Console.WriteLine($"Wrote previews at {stripDpi * 100:0}% to " + System.IO.Path.GetFullPath(folder));
        }
        Console.WriteLine(_failures == 0 ? "PASS: all interaction checks" : $"{_failures} interaction checks failed");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + name);
    }

    // ----- The snippet -----

    private sealed class Scene
    {
        public required HwndSource Source;
        public required Border Island;
        public required Grid Content;
        public required StackPanel Expanded;
        public required FxSurface Fx;
        public required Button Page1, Page2, Page3, Sync, Settings, Row1, Row2, CardButton, Prev, Play, Next, Notice, Disabled;
        public required Border Card, NoticeFace;
        public required ScaleTransform Opening;
        public required PanelPalette Palette;
    }

    private static Scene Build(PanelPalette palette, bool system = false)
    {
        var island = new Border { Width = 400, Height = 196, CornerRadius = new CornerRadius(0, 0, 16, 16), Background = new SolidColorBrush(C(palette.Surface)), ClipToBounds = true };
        if (palette.Effects.SurfaceGradient.Count >= 2)
            island.Background = new LinearGradientBrush(C(palette.Effects.SurfaceGradient[0]), C(palette.Effects.SurfaceGradient[^1]), 90);
        foreach (var (resource, token) in Tokens) island.Resources[resource] = new SolidColorBrush(C(palette[token]));
        var content = new Grid();
        var opening = new ScaleTransform();
        var stage = new Grid();
        stage.Children.Add(content);
        island.Child = stage;
        // The opening scale lies on the page content inside the layer's scope, as ExpandedContent does in the panel.
        var stack = new StackPanel { Margin = new Thickness(10, 6, 10, 0), RenderTransform = opening, RenderTransformOrigin = new Point(.5, 0) };
        content.Children.Add(stack);

        // The toolbar: the page switch with its sliding capsule, the sync glyph and the settings button.
        var toolbar = new Grid { Height = 30 };
        var pageSwitch = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        pageSwitch.SetResourceReference(Border.BackgroundProperty, "PanelSwitchFill");
        var pages = new Grid();
        var capsule = new Border { Width = 28, Height = 24, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left };
        capsule.SetResourceReference(Border.BackgroundProperty, "PanelFillSelected");
        pages.Children.Add(capsule);
        var pageRow = new StackPanel { Orientation = Orientation.Horizontal };
        Button Page(string data) { var b = Styled("PanelIconButton"); b.Width = 28; b.Height = 24; b.Margin = new Thickness(0, 0, 1, 0); b.Content = Icon(data, b); pageRow.Children.Add(b); return b; }
        var page1 = Page("M 4,21 L 4,16 M 9,21 L 9,13 M 14,21 L 14,15 M 19,21 L 19,10 M 3,12 L 9,6 L 14,10 L 21,3 M 16,3 L 21,3 L 21,8");
        var page2 = Page("M 2,12.5 H 6.5 L 9.5,5 L 14.5,19 L 17.5,12.5 H 22");
        var page3 = Page("M 9,18 V 5.5 L 20,3.3 V 16 M 9,18 A 3,3 0 1 1 3,18 A 3,3 0 1 1 9,18 M 20,16 A 3,3 0 1 1 14,16 A 3,3 0 1 1 20,16");
        pages.Children.Add(pageRow);
        pageSwitch.Child = pages;
        toolbar.Children.Add(pageSwitch);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var sync = Styled("PanelGlyphButton");
        sync.Content = new Grid { Width = 24, Height = 24, Children = { new Viewbox { Width = 18, Height = 18, Child = Icon("M 3,11 A 9,9 0 0 1 18.4,5.6 L 21,8 M 21,3 L 21,8 L 16,8 M 21,13 A 9,9 0 0 1 5.6,18.4 L 3,16 M 3,21 L 3,16 L 8,16", sync, viewbox: false) } } };
        sync.Margin = new Thickness(0, 0, 4, 0);
        var settings = Styled("PanelSettingsButton");
        settings.Content = ""; settings.FontFamily = new FontFamily("Segoe MDL2 Assets"); settings.FontSize = 16;
        right.Children.Add(sync); right.Children.Add(settings);
        toolbar.Children.Add(right);
        stack.Children.Add(toolbar);

        // Two list rows (the model details' link rows).
        Button Row(string name, string value)
        {
            var b = Styled("DetailLinkButton"); b.Height = 26; b.Width = 220; b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 2, 0, 0);
            var grid = new Grid { Margin = new Thickness(8, 0, 8, 0) };
            grid.Children.Add(new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            grid.Children.Add(new TextBlock { Text = value, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            b.Content = grid; stack.Children.Add(b); return b;
        }
        var row1 = Row("Claude Code", "42%");
        var row2 = Row("Codex", "18%");

        // An opaque card with a text button: the answers must show above it.
        var card = new Border { CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 6, 6, 6), Background = new SolidColorBrush(C(palette["popup"])) };
        var cardGrid = new Grid();
        cardGrid.Children.Add(new TextBlock { Text = "本周用量 12.4k", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(C(palette["textPrimary"])) });
        var cardButton = Styled("PanelTextButton"); cardButton.Content = "全部"; cardButton.FontSize = 11;
        cardGrid.Children.Add(cardButton);
        card.Child = cardGrid;
        stack.Children.Add(card);

        // Media buttons, a notice's text button, and a disabled button.
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        MusicControlButton Media(double size, string data)
        {
            var b = new MusicControlButton { Style = (Style)Application.Current.FindResource("MusicControl"), Width = size, Height = size, Margin = new Thickness(2, 0, 2, 0) };
            b.Content = new Viewbox { Width = size * .5, Height = size * .5, Child = new Canvas { Width = 24, Height = 24, Children = { new Path { Data = Geometry.Parse(data), Fill = new SolidColorBrush(C(palette["textPrimary"])) } } } };
            bottom.Children.Add(b); return b;
        }
        var prev = Media(30, "M 6,5 H 8 V 19 H 6 Z M 19,5 L 9,12 L 19,19 Z");
        var play = Media(36, "M 8,5 L 19,12 L 8,19 Z");
        var next = Media(30, "M 16,5 H 18 V 19 H 16 Z M 5,5 L 15,12 L 5,19 Z");
        var face = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 0, 12, 0), MinHeight = 28, Background = new SolidColorBrush(C(palette["fill"])), Margin = new Thickness(0) };
        face.Child = new TextBlock { Text = "查看详情", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(C(palette["textPrimary"])) };
        var notice = new Button { Content = face, Margin = new Thickness(18, 0, 0, 0), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }, VerticalAlignment = VerticalAlignment.Center };
        bottom.Children.Add(notice);
        var disabled = Styled("PanelTextButton"); disabled.Content = "不可用"; disabled.IsEnabled = false; disabled.Margin = new Thickness(10, 0, 0, 0);
        bottom.Children.Add(disabled);
        stack.Children.Add(bottom);

        var fx = new FxSurface(island, content, () => true)
        {
            Preview = new()
            {
                Clock = () => _now, Palette = () => palette, SystemTheme = system,
                Conditions = () => new MotionConditions(true, true, false, true, false, false, false, false, true)
            }
        };
        content.Children.Add(fx);

        // As in BrimDeck's windows, layout is rounded to device pixels; the scale is the one under test, not the screen's.
        island.UseLayoutRounding = true;
        var source = new HwndSource(new HwndSourceParameters("BrimDeck.FxPreview") { Width = 400, Height = 196, WindowStyle = unchecked((int)0x80000000) }) { RootVisual = island, SizeToContent = SizeToContent.Manual };
        VisualTreeHelper.SetRootDpi(island, new DpiScale(_dpi, _dpi));
        island.InvalidateMeasure();
        island.Measure(new Size(400, 196)); island.Arrange(new Rect(0, 0, 400, 196)); island.UpdateLayout();
        return new Scene
        {
            Source = source, Island = island, Content = content, Expanded = stack, Fx = fx, Page1 = page1, Page2 = page2, Page3 = page3, Sync = sync, Settings = settings,
            Row1 = row1, Row2 = row2, CardButton = cardButton, Card = card, Prev = prev, Play = play, Next = next, Notice = notice, NoticeFace = face, Disabled = disabled,
            Opening = opening, Palette = palette
        };
    }

    private static readonly (string Resource, string Token)[] Tokens =
    [
        ("PanelSurface", "surface"), ("PanelPrimary", "textPrimary"), ("PanelSecondary", "textSecondary"), ("PanelTertiary", "textTertiary"),
        ("PanelAccent", "accent"), ("PanelTrack", "track"), ("PanelFill", "fill"), ("PanelFillSelected", "fillSelected"),
        ("PanelHover", "hover"), ("PanelPressed", "pressed"), ("PanelSwitchFill", "switchFill"), ("PanelPopup", "popup")
    ];

    private static Button Styled(string style) => new() { Style = (Style)Application.Current.FindResource(style) };

    private static FrameworkElement Icon(string data, Button owner, bool viewbox = true)
    {
        var path = new Path { Data = Geometry.Parse(data), StrokeThickness = 1.65, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = owner });
        var canvas = new Canvas { Width = 24, Height = 24, Children = { path } };
        return viewbox ? new Viewbox { Width = 16, Height = 16, Child = canvas } : canvas;
    }

    private static Color C(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    // The control's own feedback, as Feedback and MusicControlButton show it for the pointer (the preview has no pointer).
    private static void Feedback(Scene scene, Button button, bool over, bool pressed)
    {
        button.ApplyTemplate();
        if (button.Template?.FindName("HoverLayer", button) is UIElement hover) hover.Opacity = over || pressed ? 1 : 0;
        if (button.Template?.FindName("PressLayer", button) is UIElement press) press.Opacity = pressed ? 1 : 0;
        if (button.Template?.FindName("Glyph", button) is UIElement glyph) glyph.Opacity = pressed ? .6 : over ? 1 : .78;
        if (button.Template?.FindName("Hover", button) is Ellipse ellipse) ellipse.Opacity = pressed ? .16 : over ? .1 : 0;
        if (button.Template?.FindName("ControlScale", button) is ScaleTransform scale) scale.ScaleX = scale.ScaleY = pressed ? .94 : 1;
        if (ReferenceEquals(button, scene.Notice)) scene.NoticeFace.Background = new SolidColorBrush(C(scene.Palette[pressed ? "pressed" : over ? "hover" : "fill"]));
        scene.Island.UpdateLayout();
    }

    // ----- Checks -----

    private static void Checks()
    {
        var scene = Build(PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Demo)!));
        var fx = scene.Fx;
        _now = 100;
        fx.Frame(_now);
        Check($"The snippet and its layer see {_dpi * 100:0}% scaling", VisualTreeHelper.GetDpi(fx).DpiScaleX == _dpi && VisualTreeHelper.GetDpi(scene.Page2).DpiScaleY == _dpi);
        Rect Shape(FrameworkElement control) => FxMark.For(control) is { } mark ? Placed(mark) : Rect.Empty;
        Rect Placed(FxMark mark) { mark.Update(scene.Content, VisualTreeHelper.GetDpi(fx)); return mark.Box; }
        Rect Box(FrameworkElement control) => control.TransformToVisual(scene.Content).TransformBounds(new Rect(control.RenderSize));

        var sync = Shape(scene.Sync); var syncBox = Box(scene.Sync);
        Check($"The transparent sync button is marked on its glyph ({sync.Width:0.#} x {sync.Height:0.#}), not its {syncBox.Width:0} x {syncBox.Height:0} box",
            sync.Width < syncBox.Width - 6 && sync.Height <= syncBox.Height && sync.Width is > 14 and < 26 && Math.Abs(sync.X + sync.Width / 2 - (syncBox.X + syncBox.Width / 2)) < 1.5);
        var page = Shape(scene.Page2); var pageBox = Box(scene.Page2);
        Check("A page button is marked on its hover layer, which fills it", Math.Abs(page.Width - pageBox.Width) < 1 && Math.Abs(page.Height - pageBox.Height) < 1 && FxMark.For(scene.Page2)!.Source == FxShapeSource.HoverLayer);
        var play = FxMark.For(scene.Play)!; Placed(play);
        Check($"A round media button is marked on its circle ({play.Box.Width:0.##} x {play.Box.Height:0.##})",play.Ellipse && Math.Abs(play.Box.Width - play.Box.Height) < 1 && play.Box.Width <= 36 + 1 / _dpi);
        var notice = FxMark.For(scene.Notice)!; Placed(notice);
        Check("A notice button is marked on its rounded face", notice.Source == FxShapeSource.Face && notice.LocalRadius == 8);
        var row = Shape(scene.Row1);
        Check("A list row is marked on its row", Math.Abs(row.Width - Box(scene.Row1).Width) < 1 && row.Height <= 27);

        // The mark follows the control as the panel scales open, every frame, with no pointer move.
        fx.HoverOn(scene.Page2); _now += 1; fx.Frame(_now);
        var before = FxMarkOf(fx, scene.Page2);
        scene.Opening.ScaleX = scene.Opening.ScaleY = .8; scene.Island.UpdateLayout();
        _now += .016; fx.Frame(_now);
        var during = FxMarkOf(fx, scene.Page2);
        Check("A hovered mark follows the content as the panel scales open", Math.Abs(during.Width - before.Width * .8) < 1.5 && Math.Abs(during.X - Box(scene.Page2).X) < 1);
        scene.Opening.ScaleX = scene.Opening.ScaleY = 1; scene.Island.UpdateLayout();

        // A disabled control gets nothing; animations off and high contrast draw nothing.
        fx.Reset(); fx.HoverOn(scene.Disabled); _now += 1; fx.Frame(_now);
        Check("A disabled control gets no mark", fx.DrawnBounds.IsEmpty);
        fx.HoverOn(scene.Page1); _now += 1; fx.Frame(_now);
        Check("An enabled control gets a mark", !fx.DrawnBounds.IsEmpty);
        fx.Preview!.Conditions = () => new MotionConditions(false, true, false, true, false, false, false, false, true);
        _now += .016; fx.Frame(_now);
        Check("Animations off: nothing is drawn and no frame callback stays", fx.DrawnBounds.IsEmpty && !fx.Hooked);
        fx.Preview!.Conditions = () => new MotionConditions(true, true, true, true, false, false, false, false, true);
        fx.HoverOn(scene.Page1); fx.PressOn(scene.Page1); _now += .016; fx.Frame(_now);
        Check("High contrast: nothing is drawn", fx.DrawnBounds.IsEmpty && !fx.Hooked);
        fx.Preview!.Conditions = () => new MotionConditions(true, true, false, true, false, false, false, false, true);

        // Released outside, a press backs out without its flourish.
        fx.HoverOn(scene.Page1); fx.PressOn(scene.Page1); _now += .2; fx.Frame(_now);
        Check("A press is drawn while held", !fx.DrawnBounds.IsEmpty);
        fx.Reset();
        Check("After Reset nothing is drawn and nothing is hooked", fx.DrawnBounds.IsEmpty && !fx.Hooked);
        fx.HoverOn(scene.Page1); fx.PressOn(scene.Page1);
        fx.Detach();
        Check("After Detach nothing is drawn and nothing is hooked", fx.DrawnBounds.IsEmpty && !fx.Hooked);
        scene.Source.Dispose();
    }

    private static Rect FxMarkOf(FxSurface fx, FrameworkElement control) => fx.HoverBox;

    // ----- Device pixels -----

    // Every theme, every kind of control, frame by frame through hover, a glide, a press held and completed, leaving and
    // keyboard focus: what the layer draws must lie on device pixels at the scale under test. Lines are whole device pixels
    // wide with their edges (and square or flat ends) on pixel boundaries; fills and edges of a mark lie on its snapped box;
    // the mark hugs the control's drawn shape within a device pixel. Only the round, soft things (glow dots, particles' halos
    // and diagonal star rays) are free of the grid.
    // A pixel theme's stepped outline (IslandBorder.BuildStairs) for the island's shapes at this scale: every corner on a whole
    // device pixel, every edge level or upright, nothing outside the smooth outline's bounds, corners that really step in,
    // and left and right mirror images.
    private static void StairChecks()
    {
        double step = Math.Max(1, Math.Round(2 * _dpi, MidpointRounding.AwayFromZero)) / _dpi;
        var shapes = new (string Name, double W, double H, CornerRadius Corners, double Shoulder, double Inset)[]
        {
            ("capsule", 230, 32, new CornerRadius(16), 0, 0), ("notch", 260, 32, new CornerRadius(0, 0, 12, 12), 16, 0), ("panel", 580, 300, new CornerRadius(0, 0, 18, 18), 0, 0),
            ("notice", 320, 104, new CornerRadius(22), 0, 0), ("shadow copy", 230, 32, new CornerRadius(16), 0, 1), ("notch copy", 260, 32, new CornerRadius(0, 0, 12, 12), 16, 1)
        };
        foreach (var (name, w, h, corners, shoulder, inset) in shapes)
        {
            var points = IslandBorder.BuildStairs(w, h, shoulder, corners, inset, step);
            // Whole device pixels from the edge the steps are counted from (the inset copy's edges lie an inset inside).
            bool Whole(double v) => Math.Abs(v * _dpi - Math.Round(v * _dpi)) < .01;
            bool OnGrid(double v) => Whole(v) || Whole(v - inset) || Whole(w - inset - v) || Whole(h - inset - v);
            bool axis = points.Select((p, i) => (p, q: points[(i + 1) % points.Count])).All(e => Math.Abs(e.p.X - e.q.X) < 1e-9 || Math.Abs(e.p.Y - e.q.Y) < 1e-9);
            bool inside = points.All(p => p.X >= inset - 1e-6 && p.X <= w - inset + 1e-6 && p.Y >= -1e-6 && p.Y <= h - (shoulder > 0 ? inset : inset) + 1e-6);
            double leftTop = points.Where(p => p.X <= inset + 1e-6).Select(p => p.Y).DefaultIfEmpty(0).Min();
            bool steps = corners.TopLeft > 0 ? leftTop > 1 : corners.BottomLeft > 0 && points.Where(p => p.X <= inset + 1e-6 && shoulder <= 0).Select(p => p.Y).DefaultIfEmpty(0).Max() < h - inset - 1;
            var xs = points.Select(p => Math.Round(p.X, 4)).Distinct().Order().ToList();
            bool mirror = xs.Zip(xs.AsEnumerable().Reverse()).All(pair => Math.Abs(pair.First - inset + pair.Second - (w - inset)) < 1e-4);
            Check($"Stepped {name} at {_dpi * 100:0}%: on device pixels, axis-aligned, within bounds, stepped corners, mirrored",
                points.Count >= 8 && points.All(p => OnGrid(p.X) && OnGrid(p.Y)) && axis && inside && steps && mirror);
        }
    }

    // The cut outline (IslandBorder.BuildCut): every edge is horizontal, vertical or at 45 degrees, no leg is longer than the cut or its
    // corner's radius, the outline fills the same bounds as the curved one, an inset copy lies inside it, and a pixel theme's steps win.
    private static void CutChecks()
    {
        const double cut = 10;
        var shapes = new (string Name, double W, double H, CornerRadius Corners, double Shoulder)[]
        {
            ("lowered panel", 230, 70, new CornerRadius(20), 0), ("notch", 260, 32, new CornerRadius(0, 0, 12, 12), 16), ("panel", 580, 300, new CornerRadius(0, 0, 18, 18), 0),
            ("notice", 320, 104, new CornerRadius(22), 0), ("small", 40, 30, new CornerRadius(4), 0)
        };
        // A capsule keeps its round ends: the pill, at any size, is the curved outline's shape, and there is no cut in it.
        foreach (var (w, h) in new[] { (230d, 32d), (180d, 24d), (300d, 64d) })
        {
            var pill = IslandBorder.BuildCut(w, h, 0, new CornerRadius(h / 2), 0, cut);
            var round = IslandBorder.BuildOutline(w, h, 0, new CornerRadius(h / 2), 0);
            Check($"A {w}x{h} capsule is still a pill with cut corners on: the curved outline to a tenth of a DIP²",
                Math.Abs(pill.GetArea() - round.GetArea()) < Math.Max(2, .002 * round.GetArea()) && pill.Bounds == round.Bounds);
        }
        // Opening into a panel the shape changes continuously: between frames a quarter DIP apart only the quarter DIP of height shows.
        double worst = 0; Geometry? before = null;
        for (double h = 32; h <= 140; h += .25)
        {
            double radius = Math.Min(24, 16 + (h - 32) / 4);
            var now = IslandBorder.BuildCut(230, h, 0, new CornerRadius(radius), 0, cut);
            if (before is not null) worst = Math.Max(worst, Geometry.Combine(before, now, GeometryCombineMode.Xor, null).GetArea());
            before = now;
        }
        Check($"A capsule opening into a panel changes shape continuously (worst frame {worst:0.0} DIP², a quarter DIP of a 230 wide island is 57.5)", worst < 230 * .25 + 4);
        foreach (var (name, w, h, corners, shoulder) in shapes)
        {
            var outline = IslandBorder.BuildCut(w, h, shoulder, corners, 0, cut);
            var curved = IslandBorder.BuildOutline(w, h, shoulder, corners, 0);
            var points = outline.GetFlattenedPathGeometry().Figures.SelectMany(f => new[] { f.StartPoint }.Concat(f.Segments.OfType<PolyLineSegment>().SelectMany(s => s.Points))).ToList();
            bool angles = points.Select((p, i) => (p, q: points[(i + 1) % points.Count])).All(e =>
            {
                double dx = Math.Abs(e.p.X - e.q.X), dy = Math.Abs(e.p.Y - e.q.Y);
                // A leg is at most the cut, and a corner's cut at most its radius (a shoulder's slant is longer, from the screen's edge).
                return dx < 1e-6 || dy < 1e-6 || Math.Abs(dx - dy) < 1e-6 && dx <= cut + 1e-6 || shoulder > 0 && dy <= shoulder + 1e-6;
            });
            Check($"Cut {name}: straight, horizontal, vertical or 45 degrees, legs within the cut, the curved outline's bounds",
                points.Count >= 4 && angles && Math.Abs(outline.Bounds.Width - curved.Bounds.Width) < .01 && Math.Abs(outline.Bounds.Height - curved.Bounds.Height) < .01
                && outline.Bounds.Left >= -1e-6 && outline.Bounds.Top >= -1e-6);
            // A corner the size of its radius is cut by no more than that radius.
            if (name == "small") Check("A corner is never cut by more than its own radius", points.All(p => p.X >= -1e-6) && outline.Bounds.Width > 0
                && points.Any(p => Math.Abs(p.X) < 1e-6 && p.Y > 3.9 && p.Y < 4.1 || Math.Abs(p.Y) < 1e-6 && p.X > 3.9 && p.X < 4.1));
            var inset = IslandBorder.BuildCut(w, h, shoulder, corners, 1, cut);
            Check($"Cut {name}: the shadow's inset copy lies inside the outline", inset.Bounds.Left >= outline.Bounds.Left + .99 && inset.Bounds.Right <= outline.Bounds.Right - .99 && inset.Bounds.Bottom <= outline.Bounds.Bottom - .99
                && Geometry.Combine(inset, outline, GeometryCombineMode.Exclude, null).GetArea() < .01);
        }
        var stepped = IslandBorder.Shape(230, 32, 0, new CornerRadius(16), 0, 2, 1, 1, cut).Outline;
        Check("A pixel theme's steps win over cut corners", stepped.Bounds == IslandBorder.Shape(230, 32, 0, new CornerRadius(16), 0, 2, 1, 1).Outline.Bounds);
        Check("Cut zero keeps the curves", IslandBorder.Shape(230, 32, 0, new CornerRadius(16), 0, 0, 1, 1, 0).Outline.ToString() == IslandBorder.BuildOutline(230, 32, 0, new CornerRadius(16), 0).ToString());
    }

    private static void PixelChecks()
    {
        var problems = new List<string>();
        int frames = 0, items = 0, problemCount = 0;
        double worstHug = 0, worstEdge = 0;
        bool glitch = false, scan = false;
        foreach (var theme in PanelThemes.BuiltIns)
        {
            var palette = PanelThemes.Resolve(theme);
            bool bubbles = palette.Motion.Particles == ParticleFx.Bubble;
            var scene = Build(palette);
            var fx = scene.Fx;
            var origin = scene.Content.TransformToAncestor(scene.Island).Transform(new Point());
            bool released = false;
            string phase = "";
            Rect pressedBox = Rect.Empty;

            void Sample(double seconds)
            {
                for (double t = 0; t < seconds; t += 1 / 60.0)
                {
                    _now += 1 / 60.0;
                    fx.Frame(_now);
                    frames++;
                    if (VisualTreeHelper.GetChildrenCount(fx) == 0 || VisualTreeHelper.GetDrawing((Visual)VisualTreeHelper.GetChild(fx, 0)) is not { } drawing) continue;
                    Walk(drawing, Matrix.Identity, theme.Id + " " + phase);
                }
            }

            void Walk(Drawing drawing, Matrix matrix, string where)
            {
                switch (drawing)
                {
                    case DrawingGroup group:
                        if (group.Transform is { } transform) matrix = transform.Value * matrix;
                        foreach (var child in group.Children) Walk(child, matrix, where);
                        break;
                    case GeometryDrawing g:
                        items++;
                        Inspect(g, matrix, where);
                        break;
                }
            }

            void Inspect(GeometryDrawing g, Matrix matrix, string where)
            {
                var pen = g.Pen;
                double t = pen?.Thickness ?? 0;
                if (g.Brush is VisualBrush) glitch = true;
                switch (g.Geometry)
                {
                    case EllipseGeometry e:
                        // Glow dots and particles are soft and round on purpose.
                        if (pen is null && (g.Brush is RadialGradientBrush || e.RadiusX < 3)) return;
                        // So are bubbles, a thin round outline (2 to 4 DIP) swaying as it rises after a completed press: only their pen
                        // is checked. A theme may let them live long (particleMs), so they can still be rising in the phase after the
                        // release; only a theme with bubbles has them, and no control mark is a circle that small.
                        if (pen is not null && g.Brush is null && e.RadiusX <= 4 && e.RadiusX == e.RadiusY && matrix.M11 == 1 && matrix.M22 == 1
                            && bubbles)
                        {
                            Width(pen, where + " bubble");
                            return;
                        }
                        Width(pen, where + " ellipse");
                        Edges(where + " ellipse", t / 2, e.Center.X - e.RadiusX, e.Center.X + e.RadiusX, e.Center.Y - e.RadiusY, e.Center.Y + e.RadiusY);
                        break;
                    case RectangleGeometry r:
                        Width(pen, where + " rect");
                        Edges(where + (g.Brush is VisualBrush ? " glitch slice" : " rect"), t / 2, r.Rect.Left, r.Rect.Right, r.Rect.Top, r.Rect.Bottom);
                        break;
                    case LineGeometry l when pen is not null:
                    {
                        bool horizontal = Math.Abs(l.StartPoint.Y - l.EndPoint.Y) < 1e-9, vertical = Math.Abs(l.StartPoint.X - l.EndPoint.X) < 1e-9;
                        if (!horizontal && !vertical) return;
                        Width(pen, where + " line");
                        double cap = pen.StartLineCap == PenLineCap.Square ? t / 2 : 0;
                        if (pen.StartLineCap == PenLineCap.Round) cap = double.NaN;
                        double x1 = Math.Min(l.StartPoint.X, l.EndPoint.X), x2 = Math.Max(l.StartPoint.X, l.EndPoint.X);
                        double y1 = Math.Min(l.StartPoint.Y, l.EndPoint.Y), y2 = Math.Max(l.StartPoint.Y, l.EndPoint.Y);
                        if (horizontal)
                        {
                            Edge(where + " line", y1 - t / 2, vertical: true); Edge(where + " line", y1 + t / 2, vertical: true);
                            if (!double.IsNaN(cap)) { Edge(where + " line end", x1 - cap); Edge(where + " line end", x2 + cap); }
                            if (released && !pressedBox.IsEmpty && Math.Abs(x2 - x1 - pressedBox.Width) < .01) scan = true;
                        }
                        else
                        {
                            Edge(where + " line", x1 - t / 2); Edge(where + " line", x1 + t / 2);
                            if (!double.IsNaN(cap)) { Edge(where + " line end", y1 - cap, vertical: true); Edge(where + " line end", y2 + cap, vertical: true); }
                        }
                        break;
                    }
                }

                void Edges(string what, double half, double left, double right, double top, double bottom)
                {
                    Edge(what, left - half); Edge(what, right + half);
                    Edge(what, top - half, vertical: true); Edge(what, bottom + half, vertical: true);
                }

                void Edge(string what, double at, bool vertical = false)
                {
                    var point = matrix.Transform(vertical ? new Point(0, at) : new Point(at, 0));
                    double device = ((vertical ? point.Y : point.X) + (vertical ? origin.Y : origin.X)) * _dpi;
                    double off = Math.Abs(device - Math.Round(device));
                    worstEdge = Math.Max(worstEdge, off);
                    if (off > .02) Problem($"{what}: edge at {device:0.###} px");
                }
            }

            void Width(Pen? pen, string what)
            {
                if (pen is null) return;
                double device = pen.Thickness * _dpi;
                if (Math.Abs(device - Math.Round(device)) > .02 || device < .98) Problem($"{what}: stroke {device:0.###} px wide");
            }

            // One line per kind of problem (theme, phase, shape), with how often it came up and one example.
            void Problem(string text)
            {
                problemCount++;
                int cut = text.IndexOf(':');
                string kind = cut > 0 ? text[..cut] : text;
                problems.Add(kind + "|" + text);
            }

            foreach (var target in new[] { scene.Page2, scene.Sync, scene.Row1, scene.Play, scene.CardButton, scene.Notice })
            {
                fx.Reset(); released = false; pressedBox = Rect.Empty;
                _now += 10;
                phase = "hover " + target.GetType().Name;
                Feedback(scene, target, true, false); fx.HoverOn(target); Sample(.6);
                // The mark hugs the control's drawn shape, and its box is on device pixels.
                if (FxMark.For(target) is { } mark && !fx.HoverBox.IsEmpty)
                {
                    var exact = mark.Anchor.TransformToVisual(scene.Content).TransformBounds(mark.Local);
                    var box = fx.HoverBox;
                    foreach (var (a, b) in new[] { (box.Left, exact.Left), (box.Right, exact.Right), (box.Top, exact.Top), (box.Bottom, exact.Bottom) })
                        worstHug = Math.Max(worstHug, Math.Abs(a - b) * _dpi);
                    foreach (var (v, x) in new[] { (box.Left + origin.X, true), (box.Right + origin.X, true), (box.Top + origin.Y, false), (box.Bottom + origin.Y, false) })
                        if (Math.Abs(v * _dpi - Math.Round(v * _dpi)) > .02) Problem($"{theme.Id} mark box edge at {v * _dpi:0.###} px");
                }
                phase = "press " + target.GetType().Name;
                Feedback(scene, target, true, true); fx.PressOn(target); pressedBox = fx.HoverBox; Sample(.4);
                phase = "release " + target.GetType().Name;
                Feedback(scene, target, true, false); fx.LiftOff(true); released = true; Sample(1.2); released = false;
                phase = "leave " + target.GetType().Name;
                fx.HoverOn(null); Sample(.6);
                Feedback(scene, target, false, false);
                phase = "focus " + target.GetType().Name;
                fx.FocusOn(target); Sample(1.2);
            }
            fx.Reset(); _now += 10;
            phase = "glide";
            fx.HoverOn(scene.Page1); Sample(.5); fx.HoverOn(scene.Page2); Sample(.5); fx.HoverOn(scene.Page3); Sample(.5);
            scene.Source.Dispose();
        }
        Check($"Marks hug the drawn shape within a device pixel (worst {worstHug:0.##} px)", worstHug <= 1.0001);
        Check($"Everything drawn lies on device pixels with whole-pixel strokes ({frames} frames, {items} shapes, worst edge {worstEdge:0.###} px, {problemCount} off)", problemCount == 0);
        foreach (var kind in problems.GroupBy(p => p[..p.IndexOf('|')]).Take(40))
            Console.WriteLine($"      {kind.Count(),5} x {kind.First()[(kind.Key.Length + 1)..]}");
        Check("The cyberpunk glitch slice and scan line were drawn (and checked)", glitch && scan);
    }

    // ----- Strips -----

    private sealed record Shot(string Label, BitmapSource Image);

    private static void Strips(PanelPalette palette, string id, string folder, bool system = false)
    {
        var scene = Build(palette, system);
        var fx = scene.Fx;
        var motion = system ? palette.Motion.ForSystem() : palette.Motion;
        var timing = MotionTiming.For(motion.Pace, motion.Steps);
        double hi = timing.HoverIn / 1000.0, ho = timing.HoverOut / 1000.0, gl = timing.Glide / 1000.0, pin = timing.PressIn / 1000.0, pout = timing.PressOut / 1000.0, flourish = timing.Press / 1000.0;

        // Hover: a page button, gliding to its neighbor, then leaving; the glyph button; a list row; a media button; the card's button.
        var hover = new List<List<Shot>>();
        hover.Add(Run(scene, Toolbar, [
            (0, "进入 0", s => Hover(s, s.Page1)), (hi / 3, "1/3", null), (hi * 2 / 3, "2/3", null), (hi, "到位", null),
            (hi + .2, "滑到相邻", s => Hover(s, s.Page2)), (hi + .2 + gl / 3, "1/3", null), (hi + .2 + gl, "到位", null),
            (hi + .5, "离开", s => Hover(s, null)), (hi + .5 + ho / 3, "1/3", null), (hi + .5 + ho * .8, "4/5", null)]));
        foreach (var (target, area) in new (Func<Scene, Button>, Func<Scene, Rect>)[] { (s => s.Sync, SyncArea), (s => s.Row1, Rows), (s => s.Play, Media), (s => s.CardButton, CardArea), (s => s.Notice, NoticeArea) })
            hover.Add(Run(scene, area, [(0, "进入", s => Hover(s, target(s))), (hi / 3, "1/3", null), (hi, "到位", null), (hi + .4, "停留", null), (hi + .5, "离开", s => Hover(s, null)), (hi + .5 + ho / 2, "1/2", null)]));
        Save(hover, palette, id + " · 悬停", System.IO.Path.Combine(folder, id + "-hover.png"));

        // Press: down, held, released on the control; on the card's button; a list row; released outside; rapid clicks; a media button.
        var press = new List<List<Shot>>();
        List<(double, string, Action<Scene>?)> PressSteps(Func<Scene, Button> target) => [
            (0, "悬停", s => Hover(s, target(s))), (hi + .1, "按下 +0", s => Down(s, target(s))), (hi + .1 + pin / 2, "+" + Ms(pin / 2), null), (hi + .1 + pin, "+" + Ms(pin), null),
            (hi + .4, "按住", null), (hi + .5, "松开 +0", s => Up(s, target(s), true)), (hi + .5 + pout / 3, "+" + Ms(pout / 3), null), (hi + .5 + pout * 2 / 3, "+" + Ms(pout * 2 / 3), null),
            (hi + .5 + pout, "+" + Ms(pout), null), (hi + .5 + flourish * .7, "+" + Ms(flourish * .7), null)];
        press.Add(Run(scene, Toolbar, PressSteps(s => s.Page2)));
        press.Add(Run(scene, CardArea, PressSteps(s => s.CardButton)));
        press.Add(Run(scene, Rows, PressSteps(s => s.Row1)));
        press.Add(Run(scene, Media, PressSteps(s => s.Play)));
        press.Add(Run(scene, Toolbar, [
            (0, "悬停", s => Hover(s, s.Page3)), (hi + .1, "按下", s => Down(s, s.Page3)), (hi + .3, "按住", null), (hi + .32, "拖出", s => { Hover(s, null); s.Fx.ArmPress(false); }),
            (hi + .32 + pout / 2, "+" + Ms(pout / 2), null), (hi + .32 + pout, "已退回", null), (hi + .6, "外部松开", s => Up(s, s.Page3, false)), (hi + .6 + pout / 2, "+" + Ms(pout / 2), null), (hi + .6 + pout, "+" + Ms(pout), null)]));
        var rapid = new List<(double, string, Action<Scene>?)> { (0, "悬停", s => Hover(s, s.Page1)) };
        for (int i = 0; i < 4; i++)
        {
            double at = hi + .1 + i * .12;
            rapid.Add((at, $"第{i + 1}下", s => Down(s, s.Page1)));
            rapid.Add((at + .05, "松开", s => Up(s, s.Page1, true)));
        }
        rapid.Add((hi + .1 + 4 * .12 + .15, "结束", null));
        press.Add(Run(scene, Toolbar, rapid));
        Save(press, palette, id + " · 按下与松开", System.IO.Path.Combine(folder, id + "-press.png"));

        // Cases: the panel opening under a hovered control, a theme switch mid-press, keyboard focus, a disabled control.
        var cases = new List<List<Shot>>();
        cases.Add(Run(scene, Toolbar, [
            (0, "展开 0.6", s => { s.Opening.ScaleX = s.Opening.ScaleY = .6; s.Expanded.Opacity = .2; s.Island.UpdateLayout(); Hover(s, s.Page1); }),
            (.05, "0.8", s => { s.Opening.ScaleX = s.Opening.ScaleY = .8; s.Expanded.Opacity = .6; s.Island.UpdateLayout(); }),
            (.1, "0.95", s => { s.Opening.ScaleX = s.Opening.ScaleY = .95; s.Expanded.Opacity = .9; s.Island.UpdateLayout(); }),
            (.15, "1.0", s => { s.Opening.ScaleX = s.Opening.ScaleY = 1; s.Expanded.Opacity = 1; s.Island.UpdateLayout(); }), (.4, "停留", null)]));
        cases.Add(Run(scene, Toolbar, [
            (0, "按下", s => { Hover(s, s.Page2); Down(s, s.Page2); }), (.1, "按住", null),
            (.12, "切换主题", s => { s.Fx.Preview!.Palette = () => PanelThemes.Resolve(PanelThemes.BuiltIn(id.StartsWith("dracula") ? "dark" : "dracula")!); s.Fx.Restyle(); }), (.15, "之后", null),
            (.2, "恢复", s => { s.Fx.Preview!.Palette = () => s.Palette; s.Fx.Restyle(); })]));
        cases.Add(Run(scene, CardArea, [(0, "键盘焦点", s => s.Fx.FocusOn(s.CardButton)), (timing.Focus / 4000.0, "1/4", null), (timing.Focus / 2000.0, "1/2", null), (timing.Focus / 1000.0 * .8, "4/5", null)]));
        cases.Add(Run(scene, NoticeArea, [(0, "禁用控件悬停", s => Hover(s, s.Disabled)), (hi, "无标记", null), (hi + .1, "按下", s => Down(s, s.Disabled)), (hi + .2, "无反应", null)]));
        Save(cases, palette, id + " · 其他情形", System.IO.Path.Combine(folder, id + "-cases.png"));
        scene.Source.Dispose();
    }

    // Theme files that try the motion upgrade's press effects on the themes they were made for.
    private static readonly (string Id, string Json)[] UpgradeThemes =
    [
        ("upgrade-splash-bubble", """{ "base": "island-holiday", "motion": { "press": "splash", "particles": "bubble", "trail": 0.4 } }"""),
        ("upgrade-rain", """{ "base": "cyberpunk-edgerunners", "motion": { "particles": "rain", "trail": 0.4, "highlight": "#B0D0F0" } }"""),
        // The built-in theme itself, as it ships.
        ("cyberpunk-edgerunners", ""),
        ("island-holiday-2", ""),
        ("island-holiday-2-night", "night"),
    ];
    // --step: frames this many seconds apart from the press to the end of what it leaves (0: the strip's own beats).
    private static double _step;

    // A press held and let go on a page button and a media button, sampled long enough for the second ring and the particles.
    private static void UpgradeStrip(PanelPalette palette, string id, string folder)
    {
        var scene = Build(palette);
        var timing = MotionTiming.For(palette.Motion.Pace, palette.Motion.Steps);
        double hi = timing.HoverIn / 1000.0, down = hi + .1, up = down + .45;
        List<(double, string, Action<Scene>?)> Steps(Func<Scene, Button> target) => [
            (0, "悬停", s => Hover(s, target(s))), (down, "按下 +0", s => Down(s, target(s))), (down + .1, "+100ms", null), (down + .2, "+200ms", null), (down + .36, "+360ms", null),
            (up, "松开 +0", s => Up(s, target(s), true)), (up + .06, "+60ms", null), (up + .12, "+120ms", null), (up + .2, "+200ms", null), (up + .36, "+360ms", null),
            (up + .5, "+500ms", null), (up + .7, "+700ms", null), (up + .95, "+950ms", null), (up + 1.25, "+1250ms", null)];
        if (_step > 0)
        {
            // Down, held a quarter second, let go, and every frame until the particles have gone.
            double release = down + .25;
            List<(double, string, Action<Scene>?)> Every(Func<Scene, Button> target)
            {
                var list = new List<(double, string, Action<Scene>?)> { (0, "悬停", s => Hover(s, target(s))), (down, "按下", s => Down(s, target(s))) };
                for (double t = down + _step; t < release - 1e-6; t += _step) list.Add((t, $"+{(t - down) * 1000:0}", null));
                list.Add((release, "松开", s => Up(s, target(s), true)));
                for (double t = release + _step; t <= release + 2.1; t += _step) list.Add((t, $"松+{(t - release) * 1000:0}", null));
                return list;
            }
            Save([Run(scene, Media, Every(s => s.Play))], palette, id + $" · 每 {_step * 1000:0} ms 一帧", System.IO.Path.Combine(folder, id + "-press-every.png"));
            scene.Source.Dispose();
            return;
        }
        var press = new List<List<Shot>> { Run(scene, Toolbar, Steps(s => s.Page2)), Run(scene, Media, Steps(s => s.Play)), Run(scene, Rows, Steps(s => s.Row1)) };
        Save(press, palette, id + " · 按下与松开", System.IO.Path.Combine(folder, id + "-press.png"));
        scene.Source.Dispose();
    }

    private static string Ms(double seconds) => ((int)Math.Round(seconds * 1000)).ToString(CultureInfo.InvariantCulture) + "ms";

    private static Button? _hovered, _pressedButton;

    private static void Hover(Scene scene, Button? target)
    {
        if (_hovered is not null && !ReferenceEquals(_hovered, _pressedButton)) Feedback(scene, _hovered, false, false);
        _hovered = target;
        if (target is not null) Feedback(scene, target, true, ReferenceEquals(target, _pressedButton));
        scene.Fx.HoverOn(target);
    }

    private static void Down(Scene scene, Button target)
    {
        _pressedButton = target;
        Feedback(scene, target, true, true);
        if (target.IsEnabled) scene.Fx.PressOn(target);
    }

    private static void Up(Scene scene, Button target, bool inside)
    {
        _pressedButton = null;
        Feedback(scene, target, inside && ReferenceEquals(_hovered, target), false);
        scene.Fx.LiftOff(inside);
    }

    // One row of frames: the steps run at their times (seconds from the row's start) and a frame is taken at each.
    private static List<Shot> Run(Scene scene, Func<Scene, Rect> area, List<(double At, string Label, Action<Scene>? Step)> steps)
    {
        scene.Fx.Reset(); _hovered = null; _pressedButton = null;
        foreach (var button in new[] { scene.Page1, scene.Page2, scene.Page3, scene.Sync, scene.Settings, scene.Row1, scene.Row2, scene.CardButton, scene.Prev, scene.Play, scene.Next, scene.Notice })
            Feedback(scene, button, false, false);
        double start = _now += 10;
        var shots = new List<Shot>();
        foreach (var (at, label, step) in steps)
        {
            // Frames in between, as the frame callback would run them.
            while (_now + 1 / 60.0 < start + at) { _now += 1 / 60.0; scene.Fx.Frame(_now); }
            _now = start + at;
            step?.Invoke(scene);
            scene.Fx.Frame(_now);
            shots.Add(new Shot(label, Render(scene, area(scene))));
        }
        scene.Fx.Reset();
        return shots;
    }

    private static Rect Toolbar(Scene s) => Around(s, s.Page2, 130, 44);
    private static Rect SyncArea(Scene s) => Around(s, s.Sync, 110, 44);
    private static Rect Rows(Scene s) => Around(s, s.Row1, 240, 64);
    private static Rect CardArea(Scene s) => Around(s, s.CardButton, 140, 50);
    private static Rect Media(Scene s) => Around(s, s.Play, 130, 54);
    private static Rect NoticeArea(Scene s) => Around(s, s.Notice, 150, 54);

    // A fixed window around a control, inside the snippet.
    private static Rect Around(Scene s, FrameworkElement element, double width, double height)
    {
        var box = element.TransformToVisual(s.Island).TransformBounds(new Rect(element.RenderSize));
        double x = Math.Clamp(Math.Round(box.X + box.Width / 2 - width / 2), 0, 400 - width), y = Math.Clamp(Math.Round(box.Y + box.Height / 2 - height / 2), 0, 196 - height);
        return new Rect(x, y, width, height);
    }

    private static BitmapSource Render(Scene scene, Rect area)
    {
        // At 100 percent three times the size; at another scale pixel for pixel, as the screen would show it.
        double scale = _dpi == 1 ? Scale : _dpi;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(400 * scale), (int)Math.Ceiling(196 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(scene.Island);
        int x = (int)Math.Round(area.X * scale), y = (int)Math.Round(area.Y * scale);
        var crop = new CroppedBitmap(bitmap, new Int32Rect(x, y, Math.Min(bitmap.PixelWidth - x, (int)Math.Round(area.Width * scale)), Math.Min(bitmap.PixelHeight - y, (int)Math.Round(area.Height * scale))));
        crop.Freeze();
        return crop;
    }

    private static void Save(List<List<Shot>> scenarios, PanelPalette palette, string title, string file)
    {
        // Five frames to a line, so a strip stays readable; a scenario's lines follow one another.
        var rows = new List<List<Shot>>();
        foreach (var scenario in scenarios)
            for (int i = 0; i < scenario.Count; i += 5) rows.Add(scenario.Skip(i).Take(5).ToList());
        const double gap = 8, label = 18, top = 30;
        // Pixel-exact frames are magnified with every device pixel a sharp 2 x 2 block, so a blurred edge shows.
        double zoom = _dpi == 1 ? 1 : 2;
        double cellWidth = rows.SelectMany(r => r).Max(s => s.Image.PixelWidth) * zoom;
        int columns = rows.Max(r => r.Count);
        double width = gap + columns * (cellWidth + gap), height = top + rows.Sum(r => r.Max(s => s.Image.PixelHeight) * zoom + label + gap) + gap;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        var ink = new SolidColorBrush(palette.IsDark ? Color.FromRgb(0xE8, 0xE8, 0xEE) : Color.FromRgb(0x20, 0x20, 0x28));
        var typeface = new Typeface("Microsoft YaHei UI");
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(palette.IsDark ? Color.FromRgb(0x30, 0x30, 0x36) : Color.FromRgb(0xD8, 0xD8, 0xDE)), null, new Rect(0, 0, width, height));
            dc.DrawText(new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 16, ink, 1), new Point(gap, 6));
            double y = top;
            foreach (var row in rows)
            {
                double x = gap, rowHeight = row.Max(s => s.Image.PixelHeight) * zoom;
                foreach (var shot in row)
                {
                    dc.DrawImage(shot.Image, new Rect(x, y, shot.Image.PixelWidth * zoom, shot.Image.PixelHeight * zoom));
                    dc.DrawText(new FormattedText(shot.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, ink, 1), new Point(x + 2, y + rowHeight + 1));
                    x += cellWidth + gap;
                }
                y += rowHeight + label + gap;
            }
        }
        var output = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        output.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
