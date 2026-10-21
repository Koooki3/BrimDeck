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

// The "面板主题" page: the basic themes, the built-in extensions, and the user's own themes (the files in the themes folder),
// which can be opened or deleted here. Each card shows the collapsed capsule in its theme, as the collapsed view's style cards
// do: a fixed miniature that does not follow later changes to the panel. Choosing a card applies it at once and holds the panel
// open as a preview; what the user changed under the theme being left is kept for it (ThemeChoices). While the page is shown,
// saving a file in the themes folder reloads it, so a theme can be edited with the panel beside it.
public sealed partial class SettingsWindow
{
    internal const int ThemePage = 8, MonitorSettingsPage = 9;
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
        Note(Loc.T("每个主题会分别记住你在它下面修改的应用颜色、预警与告警颜色和时间样式，切换回来时恢复；第一次选择的主题使用它自己的配色。",
            "Each theme remembers the app, warning and critical colors and the clock style you change while using it, and brings them back when you return to it. A theme chosen for the first time uses its own colors."),
            12, TextSecondary, new Thickness(0, -6, 0, 24));
        ThemeSection(Loc.T("基础", "Basics"));
        ThemeCards([(PanelThemes.SystemId, Loc.T("跟随系统", "System"), PanelTheme.Find(PanelThemes.SystemId)),
            .. PanelThemes.BuiltIns.Where(t => t.Kind == PanelThemeKind.Basic).Select(Card)], chosen);
        ThemeSection(Loc.T("主题扩展", "Theme extensions"));
        ThemeCards([.. PanelThemes.BuiltIns.Where(t => t.Kind == PanelThemeKind.Extension).Select(Card)], chosen);
        ThemeSection(Loc.T("自定义", "Custom"));
        var own = users.Where(t => t.Usable).ToList();
        if (own.Count == 0)
            Note(Loc.T("还没有自定义主题。可以在下方“以当前主题新建”，或把主题文件放进主题文件夹。", "No custom themes yet. Use \"New from current theme\" below, or put a theme file in the themes folder."),
                12, TextTertiary, new Thickness(0, 0, 0, 18));
        else ThemeCards([.. own.Select(Card)], chosen);
        _themeDetails = Collect(() => ThemeDetails(chosen, users)); _content.Children.Add(_themeDetails);
        // Miniatures of cards no longer on the page (a theme file renamed or removed) are let go.
        var cards = new[] { PanelThemes.SystemId }.Concat(PanelThemes.BuiltIns.Select(t => t.Id)).Concat(users.Where(t => t.Usable).Select(t => t.Id)).ToHashSet();
        foreach (var key in _themePreviews.Keys.Where(key => !cards.Contains(key.Card) || key.Language != Loc.Language).ToList()) _themePreviews.Remove(key);
        CustomThemeTools(users);
        static (string Id, string Name, PanelThemeInfo Theme) Card(PanelThemeInfo theme) => (theme.Id, theme.DisplayName, theme);
    }

    // A section title with a divider under it, as the other pages' groups start.
    private void ThemeSection(string title)
    {
        SectionTitle(title);
        Target.Children.Add(new Border { Height = 1, Background = UI.Brush(Divider), Margin = new Thickness(0, 0, 0, 12) });
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

    // A theme and what goes with it: what the user changed under the theme being left is kept for it, and the theme chosen brings
    // back what was changed under it, or else its own app colors and suggested clock (PanelThemes.SwitchTheme). Restore defaults
    // on this page goes through here too.
    private static void ChooseTheme(DeckSettings settings, string id)
        => PanelThemes.SwitchTheme(settings, settings.PanelTheme, PanelTheme.Current, id, PanelThemes.Resolve(PanelTheme.Find(id)));

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
        // The collapsed capsule with its quota rings and clock, as on the collapsed view's style cards, in this theme's colors.
        var preview = CompactStylePreview.Create(CompactStyle.Capsule, palette.IsDark, clock: true, palette, focus: true);
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
        string description = chosen == PanelThemes.SystemId ? Loc.T("跟随 Windows 的应用主题，在深色与浅色之间切换。", "Follows the Windows app theme, switching between dark and light.") : theme.DisplayDescription;
        if (description.Length > 0) rows.Add(Row(Loc.T("说明", "About"), Wrapped(description, false)));
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
        // A user's own theme file says who made it and where it comes from; the built-in themes' notices are in ThirdPartyNotices.txt.
        if (!theme.BuiltIn)
        {
            if (theme.File.Author.Length > 0) rows.Add(Row(Loc.T("作者", "Author"), TextLine(theme.File.Author, 12, TextSecondary)));
            if (theme.File.Homepage.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) rows.Add(Row(Loc.T("来源", "Source"), Link(Loc.T("主页 ↗", "Homepage ↗"), theme.File.Homepage)));
        }
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
                // The new theme copies the current one, so it starts with what was changed under the current one.
                if (id is not null) Change(s => { var kept = ThemeChoices.From(s); ChooseTheme(s, id); kept.ApplyTo(s); });
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
        // The user's own themes, each with its file and a delete that asks once more before the file goes to the Recycle Bin.
        foreach (var own in users.Where(t => t.Usable && t.Path is not null))
            rows.Add(Row(own.DisplayName, OwnThemeActions(own)));
        foreach (var broken in users.Where(t => !t.Usable))
            rows.Add(Row(broken.Path is { } file ? PanelThemes.DisplayFileName(file) : broken.Id, Wrapped(Loc.T("无法使用：", "Cannot be used: ") + broken.Error, true)));
        Group([.. rows]);
    }

    private FrameworkElement OwnThemeActions(PanelThemeInfo theme)
    {
        var path = theme.Path!;
        var edit = ActionButton(Loc.T("编辑", "Edit"), () => OpenThemeFile(path)); edit.ToolTip = path;
        Button remove = null!;
        bool armed = false;
        var disarm = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(4) };
        void Arm(bool on) { armed = on; remove.Content = on ? Loc.T("确认删除", "Confirm delete") : Loc.T("删除", "Delete"); if (on) disarm.Start(); else disarm.Stop(); }
        disarm.Tick += (_, _) => Arm(false);
        remove = ActionButton(Loc.T("删除", "Delete"), () =>
        {
            if (!armed) { Arm(true); return; }
            Arm(false);
            DeleteOwnTheme(theme);
        });
        AutomationProperties.SetName(remove, Loc.T("删除主题 ", "Delete theme ") + theme.DisplayName);
        foreach (var button in new[] { edit, remove }) { button.FontSize = 11; button.Margin = new Thickness(6, 0, 0, 0); }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; buttons.Children.Add(edit); buttons.Children.Add(remove);
        return buttons;
    }

    // A theme in its own folder (theme.json) takes the folder with it; a single file goes alone. The theme in use gives way to
    // the dark theme first, and the theme's saved choices go with it.
    private void DeleteOwnTheme(PanelThemeInfo theme)
    {
        var path = theme.Path!;
        var target = string.Equals(Path.GetFileName(path), "theme.json", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path)! : path;
        if (!Native.RecycleBin.Send(target)) { SaveStatus(Loc.T("无法删除主题文件：", "Could not delete the theme file: ") + target, true); return; }
        string id = theme.Id;
        Change(s =>
        {
            if (PanelThemes.NormalizeId(s.PanelTheme) == id) ChooseTheme(s, PanelThemes.Dark);
            PanelThemes.ForgetTheme(s, id);
        });
        _themesFresh = false; ShowPage(ThemePage);
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
        if (!IsLoaded || (onlyThemePage ? _page != ThemePage : _page is not (1 or 3 or ThemePage or MonitorSettingsPage))) return;
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
