using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The system monitor's own settings page ("系统监控 › 显示与读数"), as AI usage and music have theirs: how the expanded page looks
// and what the collapsed notch or capsule shows. The page's size stays with the other pages' sizes under "展开外观", and the
// switches that show the readings in the notch or the capsule stay under "收起外观 › 显示内容", next to the quota rings.
public sealed partial class SettingsWindow
{
    private void MonitorSettings()
    {
        SectionTitle(Loc.T("展开面板", "Expanded panel"));
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
            MusicChoice(Loc.T("主视图", "Large gauge"), S.MonitorMain, [(MonitorMain.Cpu, "CPU"), (MonitorMain.Gpu, "GPU")], v => Resize(s => s.MonitorMain = v, DeckPage.Monitor)),
            MusicChoice(Loc.T("刷新间隔", "Update interval"), S.MonitorInterval, [(1, Loc.T("1 秒", "1 s")), (2, Loc.T("2 秒", "2 s"))], v => Change(s => s.MonitorInterval = v)),
            MusicChoice(Loc.T("进程行", "Process rows"), S.MonitorProcessView,
                [(MonitorProcessView.Auto, Loc.T("自动", "Auto")), (MonitorProcessView.Compact, Loc.T("简略", "Compact")), (MonitorProcessView.Detailed, Loc.T("详细", "Detailed"))],
                v => Resize(s => s.MonitorProcessView = v, DeckPage.Monitor)),
            KeepPreview(Toggle(Loc.T("显示进程列表", "Show the process list"), S.MonitorProcesses, v => Resize(s => s.MonitorProcesses = v, DeckPage.Monitor),
                help:
                Loc.T("按 CPU 或内存列出占用最高的应用，同一程序的多个进程合并为一行。面板足够大时显示。",
                    "Lists the apps using the most CPU or memory, with the processes of one program folded into one row. Shown when the panel is large enough."))));
        CompactMonitorOptions();
    }

    // What the collapsed notch or capsule shows when its "系统监控" switch is on: full, brief or chosen readings, the chosen ones
    // as switches shown while Custom is picked; and whether each surface widens to its content.
    private void CompactMonitorOptions()
    {
        SectionTitle(Loc.T("收起时的读数", "Collapsed readings"));
        var items = new UniformGrid { Columns = 2, Margin = new Thickness(20, 0, 0, 4), Visibility = S.CompactMonitor == CompactMonitorMode.Custom ? Visibility.Visible : Visibility.Collapsed };
        var content = MusicChoice(Loc.T("显示内容", "Content"), S.CompactMonitor,
            [(CompactMonitorMode.Full, Loc.T("完整", "Full")), (CompactMonitorMode.Brief, Loc.T("简洁", "Brief")), (CompactMonitorMode.Custom, Loc.T("自选", "Custom"))],
            mode => { Change(s => s.CompactMonitor = mode); items.Visibility = mode == CompactMonitorMode.Custom ? Visibility.Visible : Visibility.Collapsed; });
        (CompactMonitorItems Item, string Name)[] choices =
        [
            (CompactMonitorItems.Columns, Loc.T("核心柱", "Core columns")), (CompactMonitorItems.Cpu, "CPU"), (CompactMonitorItems.Memory, Loc.T("内存", "Memory")),
            (CompactMonitorItems.Gpu, "GPU"), (CompactMonitorItems.Download, Loc.T("下载", "Download")), (CompactMonitorItems.Upload, Loc.T("上传", "Upload"))
        ];
        foreach (var (item, name) in choices)
        {
            var toggle = Toggle(name, S.CompactMonitorItems.HasFlag(item), on => Change(s => s.CompactMonitorItems = on ? s.CompactMonitorItems | item : s.CompactMonitorItems & ~item));
            if (toggle is Border { Child: CheckBox { Content: TextBlock text } }) text.FontWeight = FontWeights.Normal;
            items.Children.Add(toggle);
        }
        var chosen = new StackPanel(); chosen.Children.Add(content); chosen.Children.Add(items);
        string widen = Loc.T("内容放不下时自动加宽。拖动其左右两端或调整宽度滑块即手动设定宽度（会关闭此项），双击两端恢复。",
            "Widens the surface when its content does not fit. Dragging either end or moving the width slider sets the width by hand (and turns this off); a double click on an end turns it back on.");
        Group(chosen,
            Toggle(Loc.T("刘海按内容自动加宽", "Notch widens to the content"), S.NotchAutoWidth, on => Change(s => s.NotchAutoWidth = on), help: widen),
            Toggle(Loc.T("胶囊按内容自动加宽", "Capsule widens to the content"), S.CapsuleAutoWidth, on => Change(s => s.CapsuleAutoWidth = on), help: widen));
        Note(Loc.T("读数显示在打开了“系统监控”的刘海或胶囊中（收起外观 › 显示内容）。放不下的读数按上传、GPU、下载、核心柱、内存的顺序隐藏，CPU 保留到最后。",
            "The readings show in a notch or capsule whose \"System monitor\" switch is on (Collapsed view › Content). Readings that do not fit give way in turn: upload, GPU, download, the core columns, memory; CPU stays longest."),
            11.5, TextTertiary, new Thickness(0, -16, 0, 20));
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
