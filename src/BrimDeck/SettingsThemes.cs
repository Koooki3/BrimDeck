using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// The "面板主题" page: the basic themes, the editor themes ported as built-in extensions, and custom themes (the built-in demo and
// the files in the themes folder). Choosing a card applies it at once and holds the panel open as a preview. While the page is
// shown, saving a file in the themes folder reloads it, so a theme can be edited with the panel beside it.
public sealed partial class SettingsWindow
{
    internal const int ThemePage = 8;
    private FileSystemWatcher? _themeWatcher;
    private DispatcherTimer? _themeReload;
    // The themes folder as last read (file names, sizes and times), so a watcher event that changed nothing reloads nothing.
    private string _themeStamp = "";
    // A stamp no folder has, so the next watcher tick reloads.
    private const string UnknownStamp = "?";
    // Set when the folder needs no new read for this rebuild: the theme changed without the page, or the page just read it.
    // Saved files are covered by the watcher, and the Reload button reads it again.
    private bool _themesFresh;
    // The "当前主题" section, replaced alone when a card is chosen.
    private StackPanel? _themeDetails;
    // Each card's miniature, kept while its palette stays the same; a file theme gets a new palette each time it is read.
    private readonly Dictionary<(string Card, string Language), (PanelPalette Palette, FrameworkElement Preview)> _themePreviews = [];

    private void ThemesPage()
    {
        WatchThemeFolder(true);
        // The stamp is taken just before the read, so a write during the read still differs from it.
        if (!_themesFresh) { _themeStamp = ThemeFolderStamp(); _app.ApplyPanelTheme(reload: true); }
        _themesFresh = false;
        var chosen = PanelThemes.NormalizeId(S.PanelTheme);
        var users = PanelTheme.UserThemes;
        Note(Loc.T("选择主题时，会清除已为各应用选择的主题、预警与告警颜色，改用该主题的配色；时间的样式与颜色也会换成该主题推荐的。之后仍可在“配额与统计”和“收起外观”中单独修改。",
            "Choosing a theme clears the theme, warning and critical colors chosen for each app, which then take the theme's, and switches the clock's style and color to the theme's. Both can be changed again in \"Quotas and usage\" and \"Collapsed view\"."),
            12, TextSecondary, new Thickness(0, -6, 0, 24));
        SectionTitle(Loc.T("基础", "Basics"));
        ThemeCards([(PanelThemes.SystemId, Loc.T("跟随系统", "System"), PanelTheme.Find(PanelThemes.SystemId)),
            .. PanelThemes.BuiltIns.Where(t => t.Kind == PanelThemeKind.Basic).Select(Card)], chosen);
        SectionTitle(Loc.T("主题扩展", "Theme extensions"));
        Note(Loc.T("移植自开源的 VS Code 主题，配色按各自的 MIT 许可证使用。Monokai Pro 为商业授权主题，未收录。",
            "Ported from open-source VS Code themes; their colors are used under each theme's MIT License. Monokai Pro is commercially licensed and not included."),
            11.5, TextTertiary, new Thickness(0, -2, 0, 10));
        ThemeCards([.. PanelThemes.BuiltIns.Where(t => t.Kind == PanelThemeKind.Extension).Select(Card)], chosen);
        SectionTitle(Loc.T("自定义", "Custom"));
        ThemeCards([.. PanelThemes.BuiltIns.Where(t => t.Kind == PanelThemeKind.Custom).Concat(users.Where(t => t.Usable)).Select(Card)], chosen);
        _themeDetails = Collect(() => ThemeDetails(chosen, users)); _content.Children.Add(_themeDetails);
        // Miniatures of cards no longer on the page (a theme file renamed or removed) are let go.
        var cards = new[] { PanelThemes.SystemId }.Concat(PanelThemes.BuiltIns.Select(t => t.Id)).Concat(users.Where(t => t.Usable).Select(t => t.Id)).ToHashSet();
        foreach (var key in _themePreviews.Keys.Where(key => !cards.Contains(key.Card) || key.Language != Loc.Language).ToList()) _themePreviews.Remove(key);
        CustomThemeTools(users);
        static (string Id, string Name, PanelThemeInfo Theme) Card(PanelThemeInfo theme) => (theme.Id, theme.DisplayName, theme);
    }

    private void Note(string text, double size, string color, Thickness margin)
    {
        var note = TextLine(text, size, color);
        note.TextWrapping = TextWrapping.Wrap; note.Margin = margin; _content.Children.Add(note);
    }

    // Three cards to a row, each a miniature of the panel in that theme.
    private void ThemeCards(IReadOnlyList<(string Id, string Name, PanelThemeInfo Theme)> themes, string chosen)
    {
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(-6, 0, -6, 18) };
        foreach (var (id, name, theme) in themes)
        {
            var card = KeepPreview(new RadioButton { Style = (Style)FindResource("SettingsChoice"), Content = ThemePreview(id, theme),
                Margin = new Thickness(6, 3, 6, 6), Tag = name, GroupName = "PanelTheme", IsChecked = id == chosen });
            AutomationProperties.SetName(card, Loc.T("面板主题 ", "Panel theme ") + name);
            AutomationProperties.SetAutomationId(card, "panel-theme-" + id);
            var description = id == PanelThemes.SystemId ? Loc.T("跟随 Windows 的应用主题，在深色与浅色之间切换。", "Follows the Windows app theme, switching between dark and light.") : theme.DisplayDescription;
            if (description.Length > 0) card.ToolTip = description;
            card.Checked += (_, _) =>
            {
                if (PanelThemes.NormalizeId(S.PanelTheme) == id) return;
                Change(s => ChooseTheme(s, id));
                _preview = true; _previewPage = S.UsagePage ? DeckPage.Usage : DeckPage.Music; _app.Deck.Preview(true);
                // The cards stay as they are, the radio group moving the check; only the details change. A choice that did not
                // take (the settings could not be updated) rebuilds the page to show what is saved.
                if (PanelThemes.NormalizeId(S.PanelTheme) != id || _themeDetails is null || !_content.Children.Contains(_themeDetails)) { _themesFresh = true; ShowPage(ThemePage); return; }
                int at = _content.Children.IndexOf(_themeDetails);
                _themeDetails = Collect(() => ThemeDetails(id, PanelTheme.UserThemes));
                // A panel's slot cannot take a new child while the old one is still in it, so the old one leaves first.
                _content.Children.RemoveAt(at); _content.Children.Insert(at, _themeDetails);
            };
            grid.Children.Add(card);
        }
        _content.Children.Add(grid);
    }

    // A theme and what goes with it: the app colors follow it and the clock takes its suggestion (PanelThemes.ApplyMatching).
    // Restore defaults on this page goes through here too.
    private static void ChooseTheme(DeckSettings settings, string id)
    {
        var previous = PanelTheme.Current;
        settings.PanelTheme = id;
        PanelThemes.ApplyMatching(PanelThemes.Resolve(PanelTheme.Find(id)), settings, previous);
    }

    private FrameworkElement ThemePreview(string card, PanelThemeInfo theme)
    {
        var palette = PanelThemes.Resolve(theme);
        var key = (card, Loc.Language);
        if (_themePreviews.TryGetValue(key, out var kept) && ReferenceEquals(kept.Palette, palette))
        {
            // The miniature may still belong to a card of the page this one replaces.
            if (kept.Preview.Parent is ContentControl previous) previous.Content = null;
            return kept.Preview;
        }
        var preview = PanelThemePreview.Create(palette);
        _themePreviews[key] = (palette, preview);
        return preview;
    }

    private void ThemeDetails(string chosen, IReadOnlyList<PanelThemeInfo> users)
    {
        bool kept = PanelTheme.KeptLastGood;
        var theme = kept ? PanelTheme.ShownTheme : PanelTheme.Find(chosen);
        bool missing = !kept && chosen != PanelThemes.SystemId && !PanelThemes.IsBuiltIn(chosen) && users.All(t => !t.Usable || t.Id != chosen);
        SectionTitle(Loc.T("当前主题", "Current theme"));
        var rows = new List<FrameworkElement>();
        string title = chosen == PanelThemes.SystemId ? Loc.T("跟随系统（当前：", "System (now: ") + theme.DisplayName + Loc.T("）", ")") : theme.DisplayName;
        rows.Add(Row(Loc.T("名称", "Name"), TextLine(title, 12, TextSecondary)));
        // A file that carries the chosen id (or is named after it) but cannot be used is there, with its error listed below.
        bool broken = missing && users.Any(t => !t.Usable && (PanelThemes.NormalizeId(t.File.Id) == chosen || t.Path is { } file && PanelThemes.NormalizeId(PanelThemes.FileName(file)) == chosen));
        if (missing)
            rows.Add(Row(Loc.T("提示", "Note"), Wrapped(broken
                ? Loc.T("主题文件有错误（见下方），暂时显示为深色；修正并保存后即可恢复。", "The theme file has an error (see below) and the dark theme is shown meanwhile. Fix and save the file to restore it.")
                : Loc.T($"找不到主题“{chosen}”，暂时显示为深色。放回主题文件并重新加载后即可恢复。",
                    $"The theme \"{chosen}\" was not found and the dark theme is shown meanwhile. Put the theme file back and reload to restore it."), true)));
        if (kept)
            rows.Add(Row(Loc.T("提示", "Note"), Wrapped(Loc.T("主题文件现在有错误（见下方），面板暂时保留它上次正确的版本；修正并保存后即恢复同步。",
                "The theme file has an error now (see below). The panel keeps its last good version until the file is fixed and saved."), true)));
        if (theme.File.Author.Length > 0) rows.Add(Row(Loc.T("作者", "Author"), TextLine(theme.File.Author, 12, TextSecondary)));
        if (theme.File.License.Length > 0) rows.Add(Row(Loc.T("许可证", "License"), TextLine(theme.File.License, 12, TextSecondary)));
        if (theme.File.Homepage.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) rows.Add(Row(Loc.T("来源", "Source"), Link(Loc.T("主页 ↗", "Homepage ↗"), theme.File.Homepage)));
        if (theme.Path is { } path)
        {
            var open = ActionButton(Path.GetFileName(path) + " ↗", () => OpenThemeFile(path)); open.Style = (Style)FindResource("SettingsLink"); open.ToolTip = path;
            rows.Add(Row(Loc.T("文件", "File"), open));
        }
        var adjustments = PanelThemes.Resolve(theme).Adjustments;
        if (adjustments.Count > 0)
            rows.Add(Row(Loc.T("可读性调整", "Readability"), Wrapped(string.Join("\n", adjustments.Select(adjustment => adjustment.Describe())), false),
                help: () => Loc.T("为保证文字与信息清晰可读，低于对比度下限的颜色会沿原色相自动调亮或调暗到刚好达标，不影响主题文件本身。",
                    "To keep text and figures readable, a color below its contrast minimum is lightened or darkened along its own hue just enough to pass. The theme file is not changed.")));
        Group([.. rows]);
    }

    private TextBlock Wrapped(string text, bool error)
    {
        var block = TextLine(text, 11.5, error ? (_palette.Dark ? "#F0A6AA" : "#B12E3A") : TextSecondary);
        block.TextWrapping = TextWrapping.Wrap; block.TextTrimming = TextTrimming.None; block.MaxWidth = 460; block.TextAlignment = TextAlignment.Right;
        return block;
    }

    private void CustomThemeTools(IReadOnlyList<PanelThemeInfo> users)
    {
        SectionTitle(Loc.T("制作自定义主题", "Make a custom theme"));
        var create = ActionButton(Loc.T("以当前主题新建", "New from current theme"), () =>
        {
            try
            {
                // The theme on screen, which is the last good version while the chosen file has an error.
                var current = PanelTheme.ShownTheme;
                var path = PanelThemes.CreateFromTemplate(PanelTheme.Folder, PanelTheme.Current, current.BuiltIn ? current.Id : null);
                PanelTheme.Reload();
                // The folder as just read, including this new file, so the watcher's report of the write reloads nothing.
                _themeStamp = ThemeFolderStamp();
                var id = PanelTheme.UserThemes.FirstOrDefault(t => t.Usable && string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))?.Id;
                if (id is not null) Change(s => s.PanelTheme = id);
                OpenThemeFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SaveStatus(Loc.T("无法创建主题文件：", "Could not create the theme file: ") + ex.Message, true); }
            _themesFresh = true; ShowPage(ThemePage);
        });
        var folder = ActionButton(Loc.T("打开主题文件夹", "Open themes folder"), () =>
        {
            try { Directory.CreateDirectory(PanelTheme.Folder); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + PanelTheme.Folder + "\"") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { SaveStatus(ex.Message, true); }
        });
        var reload = ActionButton(Loc.T("重新加载", "Reload"), () => ShowPage(ThemePage));
        foreach (var button in new[] { create, folder, reload }) { button.FontSize = 11; button.Margin = new Thickness(6, 0, 0, 0); }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; buttons.Children.Add(create); buttons.Children.Add(folder); buttons.Children.Add(reload);
        var rows = new List<FrameworkElement>
        {
            Row(Loc.T("主题文件", "Theme files"), buttons, help: () => Loc.T(
                "“以当前主题新建”会把当前主题的全部颜色写入一个带注释的模板文件并打开它；改好后保存，本页会自动重新加载。主题文件放在主题文件夹中（*.json，或子文件夹中的 theme.json），格式与制作流程见项目文档 docs/zh-CN/themes.md。",
                "\"New from current theme\" writes every color of the current theme into a commented template file and opens it; save your changes and this page reloads them. Theme files live in the themes folder (*.json, or theme.json in a subfolder); the format and the design steps are in docs/en-US/themes.md.")),
            Row(Loc.T("制作指南", "Guide"), Link(Loc.T("主题制作指南 ↗", "Theme guide ↗"), Updates.GitHubUpdates.RepositoryUrl + Loc.T("/blob/main/docs/zh-CN/themes.md", "/blob/main/docs/en-US/themes.md")))
        };
        foreach (var broken in users.Where(t => !t.Usable))
            rows.Add(Row(broken.Path is { } file ? PanelThemes.DisplayFileName(file) : broken.Id, Wrapped(Loc.T("无法使用：", "Cannot be used: ") + broken.Error, true)));
        Group([.. rows]);
    }

    private void OpenThemeFile(string path)
    {
        if (!File.Exists(path)) { SaveStatus(Loc.T("主题文件不存在：", "The theme file does not exist: ") + path, true); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No program is set for .json files.
            try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            catch (Win32Exception error) { SaveStatus(error.Message, true); }
        }
    }

    // The panel theme changed without this page, or Windows switched between light and dark (the "System" card shows it).
    // The collapsed view (style cards, clock tiles) and the quotas page (default color swatches) show theme colors too.
    internal void PanelThemeChanged(bool onlyThemePage = false)
    {
        if (!IsLoaded || (onlyThemePage ? _page != ThemePage : _page is not (1 or 3 or ThemePage))) return;
        _themesFresh = _page == ThemePage; ShowPage(_page);
    }

    // Names, sizes and times of the files LoadFolder reads: a file's content cannot change without one of them changing.
    private static string ThemeFolderStamp()
    {
        try
        {
            var folder = new DirectoryInfo(PanelTheme.Folder);
            if (!folder.Exists) return "";
            return string.Join("|", folder.EnumerateFiles("*.json").Concat(folder.EnumerateDirectories().Select(dir => new FileInfo(Path.Combine(dir.FullName, "theme.json"))).Where(file => file.Exists))
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).Select(file => $"{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Guid.NewGuid().ToString(); }
    }

    // Saving a theme file while the page is shown reloads the themes; editors write a file in several steps, so changes are
    // gathered for a moment first.
    private void WatchThemeFolder(bool on)
    {
        if (!on) { _themeWatcher?.Dispose(); _themeWatcher = null; _themeReload?.Stop(); return; }
        if (_themeWatcher is not null) return;
        try
        {
            Directory.CreateDirectory(PanelTheme.Folder);
            if (_themeReload is null)
            {
                _themeReload = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
                _themeReload.Tick += (_, _) =>
                {
                    _themeReload.Stop();
                    if (IsLoaded && _page == ThemePage && ThemeFolderStamp() != _themeStamp) ShowPage(ThemePage);
                };
            }
            void Restart() { _themeReload.Stop(); _themeReload.Start(); }
            var watcher = new FileSystemWatcher(PanelTheme.Folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Changed(object? _, FileSystemEventArgs e)
            {
                if (!e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && e.ChangeType != WatcherChangeTypes.Deleted && e.ChangeType != WatcherChangeTypes.Renamed) return;
                Dispatcher.BeginInvoke(Restart);
            }
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed;
            // A burst of changes can overflow the watcher's buffer, and a removed folder stops it; the rebuild that follows makes a
            // new one and reads whatever was missed.
            watcher.Error += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_themeWatcher, watcher)) return;
                _themeWatcher = null; watcher.Dispose(); _themeStamp = UnknownStamp;
                Restart();
            });
            watcher.EnableRaisingEvents = true;
            _themeWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { _themeWatcher = null; }
    }
}
