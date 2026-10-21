using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The system monitor page's settings, on the expanded panel's settings page under its own heading.
public sealed partial class SettingsWindow
{
    private void MonitorOptions()
    {
        SectionTitle(Loc.T("系统监控", "System monitor"));
        // Each scheme is shown by the colors it has on the panel with the panel theme in use.
        var schemes = KeepPreview(new UniformGrid { Columns = 4, Width = 388 });
        foreach (var palette in Enum.GetValues<MonitorPalette>())
        {
            var colors = MonitorThemeColors.For(palette);
            var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var color in new[] { colors.Performance, colors.Efficiency, colors.Gpu })
                content.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = UI.Brush(color), Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = MonitorColors.Name(palette), Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = content, GroupName = "MonitorPalette", IsChecked = S.MonitorPalette == palette };
            AutomationProperties.SetName(choice, Loc.T("配色 ", "Colors ") + MonitorColors.Name(palette));
            // Choosing a scheme shows it on the open monitor page, as a size change does.
            choice.Checked += (_, _) => Resize(s => s.MonitorPalette = palette, DeckPage.Monitor);
            schemes.Children.Add(choice);
        }
        // The options stay available while the page is off, so they can be set before it is shown.
        Group(Row(Loc.T("配色", "Colors"), SegmentBorder(schemes)),
            MusicChoice(Loc.T("刷新间隔", "Update interval"), S.MonitorInterval, [(1, Loc.T("1 秒", "1 s")), (2, Loc.T("2 秒", "2 s"))], v => Change(s => s.MonitorInterval = v)),
            KeepPreview(Toggle(Loc.T("显示进程列表", "Show the process list"), S.MonitorProcesses, v => Resize(s => s.MonitorProcesses = v, DeckPage.Monitor),
                help:
                Loc.T("按 CPU 或内存列出占用最高的应用，同一程序的多个进程合并为一行。面板足够大时显示。",
                    "Lists the apps using the most CPU or memory, with the processes of one program folded into one row. Shown when the panel is large enough."))));
    }

    private void MonitorSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.MonitorWidth, 440, 1200, value => Resize(s => s.MonitorWidth = value, DeckPage.Monitor)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.MonitorHeight, 140, 400, value => Resize(s => s.MonitorHeight = value, DeckPage.Monitor)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        var sizes = KeepPreview(new UniformGrid { Columns = 3, Width = 292 });
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var (w, h) = MonitorSizes.For(size);
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = PanelSizes.Name(size), GroupName = "MonitorSize", IsChecked = (S.MonitorWidth, S.MonitorHeight) == (w, h) };
            AutomationProperties.SetName(choice, Loc.T("系统监控快捷尺寸 ", "System monitor quick size ") + PanelSizes.Name(size));
            choice.Checked += (_, _) => { Resize(s => { s.MonitorWidth = w; s.MonitorHeight = h; }, DeckPage.Monitor); ShowPage(2); };
            sizes.Children.Add(choice);
        }
        QuickSizeRow(sizes);
    }
}
