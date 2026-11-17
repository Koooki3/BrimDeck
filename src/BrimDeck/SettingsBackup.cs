using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The backup section of the displays and profiles page: exporting the settings to a file, importing one (ConfigBackup,
// App.Backup.cs) and opening the data folder. Each window is built from a view of plain data, so that every state of it can be
// drawn without the file or the PC it describes.
public sealed partial class SettingsWindow
{
    private static string BackupFilter => Loc.T("BrimDeck 配置 (*.brimbackup)|*.brimbackup", "BrimDeck settings (*.brimbackup)|*.brimbackup");

    private void BackupSettings()
    {
        SectionTitle(Loc.T("备份", "Backup"));
        Button Action(string text, Action action) { var button = ActionButton(text, action); button.Style = (Style)FindResource("SettingsOutlineButton"); return button; }
        Group(Row(Loc.T("导出配置", "Export settings"), Action(Loc.T("导出…", "Export…"), ExportBackup)),
            Row(Loc.T("导入配置", "Import settings"), Action(Loc.T("导入…", "Import…"), ImportBackup)),
            Row(Loc.T("数据文件夹", "Data folder"), Action(Loc.T("打开", "Open"), () => OpenFolder(_app.Store.DirectoryPath))));
    }

    private void OpenFolder(string folder, string? select = null)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string arguments = select is null ? "\"" + folder + "\"" : "/select,\"" + select + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { SaveStatus(Loc.T("无法打开文件夹。", "The folder could not be opened."), true); }
    }

    // ---- Export ----

    internal sealed record BackupExportParts(StackPanel Root, CheckBox? Themes, CheckBox? Prices, CheckBox? Keys, TextBlock Status, Button Export);

    // The export window: the settings always, a switch for each other part this PC has (themes, prices, keys), and what stays out.
    private BackupExportParts BackupExportView(int themes, int prices, int keys, bool customScripts)
    {
        var root = new StackPanel();
        var intro = BackupText(Loc.T("设置会全部导出，包括所有配置、自动切换规则和每台显示器使用的配置。",
            "All the settings are exported, with every profile, the automatic switch rules and the profile each display uses."), TextPrimary);
        root.Children.Add(intro);
        var rows = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        CheckBox? Part(string title, string count, bool on)
        {
            var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = on };
            AutomationProperties.SetName(toggle, title);
            var subtitle = TextLine(count, 11.5, TextTertiary);
            if (rows.Children.Count > 0) rows.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
            rows.Children.Add(Row(title, toggle, subtitle));
            return toggle;
        }
        var themeSwitch = themes > 0 ? Part(Loc.T("自定义主题", "Custom themes"), Loc.T($"{themes} 个", themes == 1 ? "1 theme" : $"{themes} themes"), true) : null;
        var priceSwitch = prices > 0 ? Part(Loc.T("手动添加的模型单价", "Model prices added by hand"), Loc.T($"{prices} 个模型", prices == 1 ? "1 model" : $"{prices} models"), true) : null;
        var keySwitch = keys > 0 ? Part(Loc.T("API 密钥", "API keys"), Loc.T($"{keys} 个来源", keys == 1 ? "1 source" : $"{keys} sources"), false) : null;
        if (rows.Children.Count > 0) root.Children.Add(rows);
        if (keySwitch is not null)
        {
            var warning = BackupText(Loc.T("密钥会以明文写入文件。不要把这个文件发给别人，也不要放在别人能看到的地方。",
                "The keys are written to the file as plain text. Do not send the file to anyone or keep it where others can see it."), "#F0A6AA", 12);
            warning.SetResourceReference(TextBlock.ForegroundProperty, "SettingsError");
            warning.Margin = new Thickness(0, 2, 0, 4);
            warning.Visibility = Visibility.Collapsed;
            keySwitch.Checked += (_, _) => warning.Visibility = Visibility.Visible;
            keySwitch.Unchecked += (_, _) => warning.Visibility = Visibility.Collapsed;
            root.Children.Add(warning);
        }
        var left = BackupText(Loc.T("登录状态、剪贴板同步的连接、剪贴板记录和文件中转不会导出。",
            "Sign-ins, the clipboard sync's connections, the clipboard history and the file transfer are not exported."), TextTertiary, 11.5);
        left.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(left);
        if (customScripts)
        {
            var scripts = BackupText(Loc.T("自定义来源的脚本会原样导出，写在脚本里的密钥也会一起导出。",
                "The scripts of custom sources are exported as they are, with any key written in them."), TextTertiary, 11.5);
            scripts.Margin = new Thickness(0, 4, 0, 0);
            root.Children.Add(scripts);
        }
        var status = BackupStatus();
        root.Children.Add(status);
        var export = BackupButton(Loc.T("导出", "Export"));
        root.Children.Add(export);
        return new(root, themeSwitch, priceSwitch, keySwitch, status, export);
    }

    // After the file is written: where it is, and a button that shows it in its folder.
    private StackPanel BackupExportedView(string path, Action? show = null)
    {
        var root = new StackPanel();
        root.Children.Add(BackupText(Loc.T("已导出到：", "Exported to:"), TextPrimary));
        var file = BackupText(path, TextSecondary); file.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(file);
        var open = BackupButton(Loc.T("在文件夹中显示", "Show in folder"), primary: false);
        if (show is not null) open.Click += (_, _) => show();
        root.Children.Add(open);
        return root;
    }

    private void ExportBackup()
    {
        int themes = ConfigBackup.ReadThemes(PanelTheme.Folder).Count, prices = _app.Prices.ManualModels.Count, keys = _app.ReadKeys().Count;
        bool customScripts = S.Apps.Any(app => app.QuotaSource == ProviderId.Custom && app.Script.Trim().Length > 0);
        var shell = SyncDialog(Loc.T("导出配置", "Export settings"));
        var view = BackupExportView(themes, prices, keys, customScripts);
        shell.Body.Children.Add(view.Root);
        view.Export.Click += (_, _) =>
        {
            bool withKeys = view.Keys?.IsChecked == true;
            var dialog = new SaveFileDialog { Title = Loc.T("导出配置", "Export settings"), Filter = BackupFilter, DefaultExt = ConfigBackup.Extension, AddExtension = true,
                OverwritePrompt = true, FileName = ConfigBackup.FileName(DateTime.Today, withKeys) };
            if (dialog.ShowDialog(shell.Window) != true) return;
            try { App.WritePackage(dialog.FileName, _app.ExportContent(view.Themes?.IsChecked == true, view.Prices?.IsChecked == true, withKeys)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetBackupStatus(view.Status, Loc.T("无法写入这个位置，请换一个文件夹再试。", "The file could not be written there. Try another folder."));
                return;
            }
            string path = dialog.FileName;
            shell.Body.Children.Clear();
            shell.Body.Children.Add(BackupExportedView(path, () => OpenFolder(Path.GetDirectoryName(path)!, path)));
        };
        shell.Window.ShowDialog();
    }

    // ---- Import ----

    internal sealed record BackupImportParts(StackPanel Root, IReadOnlyList<ComboBox> Displays, TextBlock Status, Button Import);

    // The import window: where the file comes from and what it holds, a choice of display here for each display of the file this PC
    // does not have, and what the import replaces. here: this PC's displays with their labels; guesses: the one each starts at.
    private BackupImportParts BackupImportView(string fileName, BackupPackage package, IReadOnlyList<BackupDisplay> missing,
        IReadOnlyList<BackupDisplay> here, IReadOnlyList<string?> guesses)
    {
        var root = new StackPanel();
        var name = TextLine(fileName, 13, TextPrimary, FontWeights.SemiBold); name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = fileName;
        root.Children.Add(name);
        var manifest = package.Manifest;
        string time = manifest.Exported.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string origin = manifest.BeforeImport ? Loc.T($"导入前自动备份 · {time}", $"Saved automatically before an import · {time}")
            : manifest.Computer.Length > 0 ? Loc.T($"{manifest.Computer} 导出 · {time} · BrimDeck {manifest.Version}", $"Exported on {manifest.Computer} · {time} · BrimDeck {manifest.Version}")
            : Loc.T($"导出于 {time} · BrimDeck {manifest.Version}", $"Exported {time} · BrimDeck {manifest.Version}");
        var from = TextLine(origin, 11.5, TextTertiary); from.Margin = new Thickness(0, 3, 0, 0); from.TextTrimming = TextTrimming.CharacterEllipsis;
        root.Children.Add(from);

        // What it holds, one line each.
        var settings = package.Settings;
        int profiles = Math.Max(1, settings.Profiles.Count), rules = settings.ProfileRules.Count, sources = settings.Apps.Count;
        var lines = new List<(string, string)>
        {
            (Loc.T("设置", "Settings"), Loc.T($"{profiles} 套配置 · {rules} 条自动切换规则 · {sources} 个用量来源",
                $"{Plural(profiles, "profile")} · {Plural(rules, "switch rule")} · {Plural(sources, "source")}"))
        };
        if (package.Themes.Count > 0) lines.Add((Loc.T("自定义主题", "Custom themes"), Loc.T($"{package.Themes.Count} 个", Plural(package.Themes.Count, "theme"))));
        if (package.Prices.Count > 0) lines.Add((Loc.T("模型单价", "Model prices"), Loc.T($"{package.Prices.Count} 个模型", Plural(package.Prices.Count, "model"))));
        lines.Add((Loc.T("API 密钥", "API keys"), package.Keys.Count > 0 ? Loc.T($"{package.Keys.Count} 个来源", Plural(package.Keys.Count, "source")) : Loc.T("不包含", "None")));
        var table = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < lines.Count; i++)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = TextLine(lines[i].Item1, 12.5, TextSecondary); label.Margin = new Thickness(0, i == 0 ? 0 : 6, 16, 0);
            var value = BackupText(lines[i].Item2, TextPrimary); value.Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0);
            Grid.SetRow(label, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
            table.Children.Add(label); table.Children.Add(value);
        }
        root.Children.Add(table);

        var combos = new List<ComboBox>();
        if (missing.Count > 0)
        {
            var heading = TextLine(Loc.T("显示器", "Displays"), 13, TextPrimary, FontWeights.SemiBold); heading.Margin = new Thickness(0, 20, 0, 0);
            root.Children.Add(heading);
            var note = BackupText(missing.Count == 1
                ? Loc.T("文件里的这台显示器在这台电脑上没有找到。选择这台电脑上对应的显示器，它的设置会转到所选的显示器上：",
                    "This display from the file was not found on this PC. Choose the display here that takes its place; its settings move to it:")
                : Loc.T("文件里的这些显示器在这台电脑上没有找到。为每台选择这台电脑上对应的显示器，它们的设置会转到所选的显示器上：",
                    "These displays from the file were not found on this PC. Choose the display here that takes the place of each; their settings move to it:"), TextSecondary);
            note.Margin = new Thickness(0, 4, 0, 4);
            root.Children.Add(note);
            var options = here.Select(d => d.Name).Append(Loc.T("不对应", "None")).ToList();
            for (int i = 0; i < missing.Count; i++)
            {
                var old = missing[i];
                string label = old.Name.Length > 0 ? old.Name : Loc.T("显示器", "Display");
                int start = guesses.ElementAtOrDefault(i) is { } guess && here.ToList().FindIndex(d => d.Id == guess) is >= 0 and var at ? at : options.Count - 1;
                var combo = new ComboBox { Width = 270, Style = (Style)FindResource("SettingsCombo"), ItemsSource = options, SelectedIndex = start };
                AutomationProperties.SetName(combo, label); PopupMotion.Attach(combo);
                combos.Add(combo);
                var row = DisplayLine(Row(label, combo)); row.MinHeight = 40;
                if (row.Children[0] is StackPanel { Children: [TextBlock text] }) { text.TextTrimming = TextTrimming.CharacterEllipsis; text.ToolTip = label; }
                root.Children.Add(row);
            }
        }

        var replace = BackupText(package.Themes.Count > 0 || package.Prices.Count > 0
            ? Loc.T("导入后，这台电脑的设置、配置和自动切换规则会被替换；主题和单价会加入这台电脑，同名的会被替换。",
                "The settings, profiles and automatic switch rules on this PC are replaced. The themes and prices are added to those here, replacing any of the same name.")
            : Loc.T("导入后，这台电脑的设置、配置和自动切换规则会被替换。", "The settings, profiles and automatic switch rules on this PC are replaced."), TextTertiary, 11.5);
        replace.Margin = new Thickness(0, 18, 0, 0);
        root.Children.Add(replace);
        var undo = BackupText(Loc.T("导入前会自动备份当前配置，需要时可以导入这份备份来恢复。", "The current settings are backed up first; import that backup to go back."), TextTertiary, 11.5);
        undo.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(undo);
        var status = BackupStatus();
        root.Children.Add(status);
        var import = BackupButton(Loc.T("导入", "Import"));
        root.Children.Add(import);
        return new(root, combos, status, import);
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // A file that cannot be imported: why, and a button to choose another.
    private (StackPanel Root, Button Choose) BackupProblemView(string fileName, string message)
    {
        var root = new StackPanel();
        var name = TextLine(fileName, 13, TextPrimary, FontWeights.SemiBold); name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = fileName;
        root.Children.Add(name);
        var text = BackupText(message, "#F0A6AA"); text.SetResourceReference(TextBlock.ForegroundProperty, "SettingsError"); text.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(text);
        var choose = BackupButton(Loc.T("选择其他文件", "Choose another file"), primary: false);
        root.Children.Add(choose);
        return (root, choose);
    }

    private static string BackupProblemText(Exception problem) => problem switch
    {
        BackupException { Problem: BackupProblem.Newer } newer => Loc.T(
            $"这个文件由 BrimDeck {newer.Version} 导出，比这台电脑上的 {ApplicationVersion.Parse(ApplicationVersion.Current).ToString(3)} 新。请先把 BrimDeck 更新到 {newer.Version} 或更高版本，再导入。",
            $"This file was exported by BrimDeck {newer.Version}, newer than {ApplicationVersion.Parse(ApplicationVersion.Current).ToString(3)} on this PC. Update BrimDeck to {newer.Version} or later, then import it."),
        BackupException => Loc.T("这个文件不是 BrimDeck 的配置文件，或者已经损坏。", "This is not a BrimDeck settings file, or it is damaged."),
        _ => Loc.T("无法读取这个文件。请确认它没有被其他程序占用，再试一次。", "The file could not be read. Make sure no other program is using it, then try again.")
    };

    // After an import: the backup of what it replaced, and what is left to do here.
    private StackPanel BackupDoneView(string backupPath, IReadOnlyList<ImportNote> notes, Action? showBackup = null)
    {
        var root = new StackPanel();
        root.Children.Add(BackupText(notes.Count == 0 ? Loc.T("配置已导入，不需要再做其他设置。", "The settings were imported. Nothing else needs to be set.")
            : Loc.T("配置已导入。还有几项需要在这台电脑上处理：", "The settings were imported. A few things still need doing on this PC:"), TextPrimary));
        foreach (var note in notes)
        {
            var text = BackupText(note.Text, TextPrimary); text.Margin = new Thickness(0, 14, 0, 0);
            root.Children.Add(text);
            if (note.Names.Count == 0) continue;
            var names = BackupText(string.Join(Loc.T("、", ", "), note.Names), TextSecondary); names.Margin = new Thickness(0, 3, 0, 0);
            root.Children.Add(names);
        }
        var backup = BackupText(Loc.T("导入前的配置已备份，需要恢复时导入这个文件：", "The settings before the import were backed up. To go back, import this file:"), TextTertiary, 11.5);
        backup.Margin = new Thickness(0, 18, 0, 0);
        root.Children.Add(backup);
        var file = BackupText(Path.GetFileName(backupPath), TextSecondary, 11.5); file.Margin = new Thickness(0, 2, 0, 0);
        root.Children.Add(file);
        var open = BackupButton(Loc.T("打开备份文件夹", "Open the backup folder"), primary: false);
        if (showBackup is not null) open.Click += (_, _) => showBackup();
        root.Children.Add(open);
        return root;
    }

    private void ImportBackup()
    {
        string? Pick(Window owner)
        {
            var dialog = new OpenFileDialog { Title = Loc.T("导入配置", "Import settings"), Filter = BackupFilter, CheckFileExists = true };
            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }
        if (Pick(this) is not { } first) return;
        var shell = SyncDialog(Loc.T("导入配置", "Import settings"), 460);
        void Show(string title, FrameworkElement view)
        {
            var body = new StackPanel(); body.Children.Add(view);
            shell.Window.Title = title;
            shell.Window.Content = SyncDialogFrame(title, body, shell.Window.Close);
        }
        void Open(string path)
        {
            BackupPackage package;
            try { package = App.OpenPackage(path); }
            catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException)
            {
                var (problem, choose) = BackupProblemView(Path.GetFileName(path), BackupProblemText(ex));
                choose.Click += (_, _) => { if (Pick(shell.Window) is { } next) Open(next); };
                Show(Loc.T("导入配置", "Import settings"), problem);
                return;
            }
            var displays = Native.Displays.Current();
            var missing = ConfigBackup.DisplaysToMatch(package.Settings, displays.Select(d => d.Id));
            var named = package.Settings.DisplayMonitors.Select(d => d.Id).Concat(package.Settings.ProfileRules.SelectMany(r => r.Displays).Select(d => d.Id));
            var guesses = ConfigBackup.GuessDisplays(missing, [.. displays.Select(d => new BackupDisplay(d.Id, d.Name))], named);
            var shortened = displays.Select(d => d with { Name = ShortName(d.Name) }).ToList();
            var here = shortened.Select(d => new BackupDisplay(d.Id, Native.Displays.Label(d, shortened) + (d.Primary ? Loc.T(" · 主显示器", " · primary") : ""))).ToList();
            var view = BackupImportView(Path.GetFileName(path), package, missing, here, guesses);
            view.Import.Click += (_, _) =>
            {
                var map = new Dictionary<string, BackupDisplay?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < missing.Count; i++)
                    map[missing[i].Id] = view.Displays[i].SelectedIndex is >= 0 and var at && at < displays.Count ? new BackupDisplay(displays[at].Id, displays[at].Name) : null;
                App.ImportOutcome outcome;
                try { outcome = _app.Import(package, map); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SetBackupStatus(view.Status, Loc.T("无法在数据文件夹中备份当前配置，没有导入。请检查数据文件夹的权限。",
                        "The current settings could not be backed up in the data folder, so nothing was imported. Check the data folder's permissions."));
                    return;
                }
                RebuildTheme();
                Show(Loc.T("导入完成", "Import complete"), BackupDoneView(outcome.BackupPath, outcome.Notes,
                    () => OpenFolder(Path.GetDirectoryName(outcome.BackupPath)!, outcome.BackupPath)));
            };
            Show(Loc.T("导入配置", "Import settings"), view.Root);
        }
        Open(first);
        shell.Window.ShowDialog();
    }

    // ---- Parts ----

    // A line for what went wrong, shown only while there is something to say.
    private TextBlock BackupStatus()
    {
        var status = BackupText("", TextSecondary); status.Margin = new Thickness(0, 12, 0, 0); status.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        return status;
    }
    private static void SetBackupStatus(TextBlock status, string text)
    {
        status.Text = text; status.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        status.SetResourceReference(TextBlock.ForegroundProperty, "SettingsError");
    }

    // A display's name cut short enough that its resolution still shows in the list.
    private static string ShortName(string name) => name.Length <= 24 ? name : name[..23].TrimEnd() + "…";

    private TextBlock BackupText(string text, string color, double size = 12.5)
    {
        var block = TextLine(text, size, color); block.TextWrapping = TextWrapping.Wrap; block.LineHeight = size + 6.5;
        return block;
    }

    private Button BackupButton(string text, bool primary = true)
    {
        var button = ActionButton(text, () => { }, primary);
        if (!primary) button.Style = (Style)FindResource("SettingsOutlineButton");
        button.HorizontalAlignment = HorizontalAlignment.Right; button.MinWidth = 96; button.Padding = new Thickness(16, 6, 16, 6);
        button.Margin = new Thickness(0, 20, 0, 0);
        return button;
    }
}
