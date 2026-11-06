using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BrimDeck.Core;

namespace BrimDeck;

// "展开外观 › 剪贴板与文件中转": the page's size, and whether and for how long the clipboard is recorded. The history stays on this
// computer, encrypted for the current Windows user; entries in a category and phrases are kept whatever is chosen here.
public partial class SettingsWindow
{
    private void ShelfSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.ClipboardWidth, 440, 1200, value => Resize(s => s.ClipboardWidth = value, DeckPage.Clipboard)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.ClipboardHeight, 140, 400, value => Resize(s => s.ClipboardHeight = value, DeckPage.Clipboard)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        var sizes = KeepPreview(new UniformGrid { Columns = 3, Width = 292 });
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var (w, h) = ShelfSizes.For(size);
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = PanelSizes.Name(size), GroupName = "ShelfSize", IsChecked = (S.ClipboardWidth, S.ClipboardHeight) == (w, h) };
            AutomationProperties.SetName(choice, Loc.T("剪贴板与文件中转快捷尺寸 ", "Clipboard and file transfer quick size ") + PanelSizes.Name(size));
            choice.Checked += (_, _) => { Resize(s => { s.ClipboardWidth = w; s.ClipboardHeight = h; }, DeckPage.Clipboard); ShowPage(2); };
            sizes.Children.Add(choice);
        }
        QuickSizeRow(sizes);

        Target.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider), Margin = new Thickness(0, 20, 0, 0) });
        Target.Children.Add(Toggle(Loc.T("记录剪贴板", "Record the clipboard"), S.ClipboardRecord, value => Change(s => s.ClipboardRecord = value),
            help: Loc.T("记录复制的文字和图片，保存在本机并加密存放。密码管理器标记为不记录的内容不会保存。",
                "Keeps the text and pictures you copy, encrypted on this computer. What password managers mark as private is never kept.")));
        var retention = new ComboBox { Width = 150, Style = (Style)FindResource("SettingsCombo") };
        var choices = new[] { (ClipRetention.Week, Loc.T("7 天", "7 days")), (ClipRetention.Month, Loc.T("30 天", "30 days")),
            (ClipRetention.Recent500, Loc.T("最近 500 条", "Latest 500")), (ClipRetention.UntilExit, Loc.T("退出时清除", "Until exit")) };
        retention.ItemsSource = choices.Select(choice => choice.Item2).ToList();
        retention.SelectedIndex = Math.Max(0, Array.FindIndex(choices, choice => choice.Item1 == S.ClipboardRetention));
        retention.IsEnabled = S.ClipboardRecord;
        AutomationProperties.SetName(retention, Loc.T("保留历史记录", "Keep history"));
        PopupMotion.Attach(retention);
        retention.SelectionChanged += (_, _) => { if (retention.SelectedIndex >= 0) Change(s => s.ClipboardRetention = choices[retention.SelectedIndex].Item1); };
        Target.Children.Add(Row(Loc.T("保留历史记录", "Keep history"), retention, help: () => Loc.T("放进分类的内容和常用语一直保留，只在删除时移除。",
            "Entries in a category and phrases are kept until you delete them.")));
        var excluded = new TextBox { Style = (Style)FindResource("SettingsField"), Width = 220, Text = string.Join(", ", S.ClipboardExcluded), IsEnabled = S.ClipboardRecord };
        AutomationProperties.SetName(excluded, Loc.T("不记录的应用", "Apps not recorded"));
        excluded.LostKeyboardFocus += (_, _) =>
        {
            var names = excluded.Text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries).Select(name => name.Trim()).ToList();
            if (!names.SequenceEqual(S.ClipboardExcluded)) Change(s => s.ClipboardExcluded = names);
        };
        Target.Children.Add(Row(Loc.T("不记录的应用", "Apps not recorded"), excluded, help: () => Loc.T("填写进程名，用逗号分隔，例如 KeePass, 1Password。",
            "Process names separated by commas, for example KeePass, 1Password.")));
    }
}
