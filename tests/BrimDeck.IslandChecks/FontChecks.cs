using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;
using Fonts = BrimDeck.Fonts;

// Changing the interface font while the app runs (W-10 stage 2), on the real App, panel, settings window and notice window,
// offscreen. The font is changed through App.UpdateSettings, as the settings window does, and then every window that exists is
// walked: no element may still carry the family of the font that was left (the settings window and the small windows are given
// theirs once, when they are made, so only the switch itself can change it), the new family must be what the texts are in, and
// every text that is laid out on one line must be as wide as its text is in the new family (a measure made in the old one is
// stale). Put the call to Fonts.Retarget in Fonts.Use back to nothing and the first of these fails.
internal static class FontChecks
{
    public static int Run(App app, MainWindow deck)
    {
        var failures = new List<string>();
        // No tray icon: the harness has no window handle to hang one on.
        app.Settings.ShowTrayIcon = false;
        void Check(string name, bool condition)
        {
            if (condition) Console.WriteLine("PASS: " + name); else { failures.Add(name); Console.Error.WriteLine("FAIL: " + name); }
        }

        // The window reads the price table and update state of a running app; this App has none, so it gets empty ones of its own.
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BrimDeck.FontChecks." + Environment.ProcessId);
        typeof(App).GetProperty(nameof(App.Prices))!.SetValue(app, new Pricing(folder));
        var settings = new SettingsWindow(app) { Left = -20000, Top = -20000, ShowActivated = false };
        settings.Show();
        var notice = new IslandNotice(deck, app);
        // A window made the way the small windows are: its text takes the panel family once, in the constructor.
        var small = new Window { Left = -20000, Top = -20000, Width = 240, Height = 80, ShowActivated = false, WindowStyle = WindowStyle.None, FontFamily = UI.PanelFont };
        small.Content = new TextBlock { Text = "Small window 小窗口", FontFamily = UI.PanelFont };
        small.Show();
        try
        {
            string[] installed = [.. Fonts.Installed()];
            Check("the installed families are listed, symbol fonts left out", installed.Length > 5 && !installed.Any(UiFonts.IsSymbolFamily));
            // A wide family, a narrow one and (always there) a plain one; the first two only if this machine has them.
            string wide = installed.FirstOrDefault(n => n == "Verdana") ?? "Segoe UI", narrow = installed.FirstOrDefault(n => n == "Arial Narrow") ?? "Calibri";
            string[] order = ["", wide, narrow, "", "Font That Is Not Installed 4711"];
            string before = "";
            foreach (var scale in new[] { 100, 150 })
            foreach (var family in order)
            {
                var next = app.Settings.Copy(); next.UiFont = family; next.PanelScale = scale;
                app.UpdateSettings(next);
                var left = StaleChains(before);
                foreach (var window in new Window[] { deck, settings, small, notice }) { window.UpdateLayout(); }
                string label = $"{(family.Length == 0 ? "default" : family)} at {scale}%";
                Check($"after the font becomes {label} the saved setting and the current family agree", app.Settings.UiFont == UiFonts.Normalize(family) && Fonts.Custom == app.Settings.UiFont);
                if (before != family)
                {
                    var stale = Walk(app).Where(node => node.GetValue(TextElement.FontFamilyProperty) is FontFamily f && left.Contains(f.Source ?? "")).Select(Describe).ToList();
                    Check($"after the font becomes {label} no element keeps the family of the one before ({stale.Count}: {string.Join(", ", stale.Take(4))})", stale.Count == 0);
                }
                var texts = Walk(app).OfType<TextBlock>().Where(t => t.Text.Length > 0 && t.IsVisible || t.Text.Length > 0 && ReferenceEquals(t, small.Content)).ToList();
                bool Ours(FontFamily f) => Enum.GetValues<UiFontRole>().Any(role => f.Source == Fonts.Of(role).Source);
                var inNew = texts.Count(t => Ours(t.FontFamily));
                Check($"{label}: the texts are drawn in the current families ({inNew} of {texts.Count} texts, the rest are icons and fixed faces)", inNew > texts.Count / 2);
                Check($"{label}: the small window's own text follows ({((TextBlock)small.Content).FontFamily.Source})", ((TextBlock)small.Content).FontFamily.Source == Fonts.Panel.Source && small.FontFamily.Source == Fonts.Panel.Source);
                // One-line texts are as wide as they are in the family now in use, nothing is cut.
                var cut = texts.Where(t => t.TextWrapping == TextWrapping.NoWrap && t.TextTrimming == TextTrimming.None && t.IsVisible && t.RenderSize.Width > 0 &&
                    Ours(t.FontFamily) && t.DesiredSize.Width + 1 < UI.TextWidth(t)).Select(Describe).ToList();
                Check($"{label}: no one-line text was measured in another family ({cut.Count}: {string.Join(", ", cut.Take(4))})", cut.Count == 0);
                before = family;
            }
            { var reset = app.Settings.Copy(); reset.UiFont = ""; app.UpdateSettings(reset); }
            // Back at the start: the families are the shipped ones again, as if no font had ever been chosen.
            Check("with no font chosen the families are the shipped chains", Fonts.Panel.Source == UiFonts.DefaultChain(UiFontRole.Panel) && Fonts.Text.Source == UiFonts.DefaultChain(UiFontRole.Text));
            Check("the icon and monospaced families never take the user's font", Fonts.Icon.Source == UiFonts.IconChain && Fonts.Mono.Source == UiFonts.MonoChain);
        }
        finally { small.Close(); notice.Close(); settings.Close(); }
        if (failures.Count > 0) { Console.Error.WriteLine($"{failures.Count} font check(s) failed."); return 1; }
        return 0;

        // The chains in use before the last change (the families each role had), which nothing may carry after it.
        static HashSet<string> StaleChains(string custom)
        {
            var now = Enum.GetValues<UiFontRole>().Select(role => UiFonts.Chain(role, Fonts.Custom)).ToHashSet();
            return Enum.GetValues<UiFontRole>().Select(role => UiFonts.Chain(role, custom)).Where(chain => !now.Contains(chain)).ToHashSet();
        }
        static string Describe(DependencyObject node) => node.GetType().Name + (node is TextBlock t ? "\"" + (t.Text.Length > 16 ? t.Text[..16] : t.Text) + "\"" : "");
    }

    // Every element of every window: the visual tree, the logical tree, and the menus and tips hung on elements.
    private static List<DependencyObject> Walk(App app)
    {
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<DependencyObject>();
        foreach (Window window in app.Windows) stack.Push(window);
        var all = new List<DependencyObject>();
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;
            all.Add(node);
            if (node is Visual) for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) stack.Push(VisualTreeHelper.GetChild(node, i));
            if (node is FrameworkElement or FrameworkContentElement) foreach (var child in LogicalTreeHelper.GetChildren(node)) if (child is DependencyObject d) stack.Push(d);
            if (node is FrameworkElement { ContextMenu: { } menu }) stack.Push(menu);
        }
        return all;
    }
}
