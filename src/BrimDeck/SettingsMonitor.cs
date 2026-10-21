using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
            MusicChoice(Loc.T("主视图", "Large gauge"), S.MonitorMain, [(MonitorMain.Cpu, "CPU"), (MonitorMain.Gpu, "GPU")], v => Resize(s => s.MonitorMain = v, DeckPage.Monitor)),
            MusicChoice(Loc.T("刷新间隔", "Update interval"), S.MonitorInterval, [(1, Loc.T("1 秒", "1 s")), (2, Loc.T("2 秒", "2 s"))], v => Change(s => s.MonitorInterval = v)),
            MusicChoice(Loc.T("进程行", "Process rows"), S.MonitorProcessView,
                [(MonitorProcessView.Auto, Loc.T("自动", "Auto")), (MonitorProcessView.Compact, Loc.T("简略", "Compact")), (MonitorProcessView.Detailed, Loc.T("详细", "Detailed"))],
                v => Resize(s => s.MonitorProcessView = v, DeckPage.Monitor)),
            KeepPreview(Toggle(Loc.T("显示进程列表", "Show the process list"), S.MonitorProcesses, v => Resize(s => s.MonitorProcesses = v, DeckPage.Monitor),
                help:
                Loc.T("按 CPU 或内存列出占用最高的应用，同一程序的多个进程合并为一行。面板足够大时显示。",
                    "Lists the apps using the most CPU or memory, with the processes of one program folded into one row. Shown when the panel is large enough."))));
    }

    // The readings in the collapsed notch or capsule: a switch with its options under it, opened by its arrow as the clock's.
    private bool _compactMonitorOpen;
    private FrameworkElement CompactMonitorRow(CompactStyle style)
    {
        bool notch = style == CompactStyle.Notch;
        string title = Loc.T("系统监控", "System monitor");
        var options = new Border { Background = UI.Brush(Inset), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 4), Margin = new Thickness(0, 0, 0, 12), Child = CompactMonitorOptions(style) };
        void Enable(bool on) { options.IsEnabled = on; options.Opacity = on ? 1 : .45; }
        Enable(S.ShowsMonitor(style));
        Button arrow = null!;
        void Show(bool open) { options.Visibility = open ? Visibility.Visible : Visibility.Collapsed; PointArrow(arrow, open); }
        void Flip() { _compactMonitorOpen = !_compactMonitorOpen; Show(_compactMonitorOpen); }
        arrow = ChevronButton(Loc.T("系统监控设置", "System monitor settings"), Flip);
        arrow.Margin = new Thickness(2, 0, 0, 0);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(SettingLabel(title, () => Loc.T("在收起的刘海或胶囊中显示 CPU、内存、GPU 与网速。显示时每隔一个刷新间隔读取一次系统数据，不读取进程。",
            "Shows CPU, memory, GPU and network speed in the collapsed notch or capsule. While shown, the system is read once per update interval, without the processes.")));
        heading.Children.Add(arrow);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = S.ShowsMonitor(style), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title);
        void Turn(bool on) { Change(s => { if (notch) s.NotchMonitor = on; else s.CapsuleMonitor = on; }); Enable(on); }
        toggle.Checked += (_, _) => Turn(true); toggle.Unchecked += (_, _) => Turn(false);
        var header = new Grid { MinHeight = 44, Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(heading); Grid.SetColumn(toggle, 1); header.Children.Add(toggle);
        header.MouseLeftButtonDown += (_, e) => { Flip(); e.Handled = true; };
        Show(_compactMonitorOpen);
        var row = new StackPanel(); row.Children.Add(header); row.Children.Add(options); return row;
    }

    // Full, brief or chosen readings; the chosen ones as switches, shown while Custom is picked.
    private FrameworkElement CompactMonitorOptions(CompactStyle style)
    {
        var panel = new StackPanel();
        var items = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0), Visibility = S.CompactMonitor == CompactMonitorMode.Custom ? Visibility.Visible : Visibility.Collapsed };
        panel.Children.Add(MusicChoice(Loc.T("显示内容", "Content"), S.CompactMonitor,
            [(CompactMonitorMode.Full, Loc.T("完整", "Full")), (CompactMonitorMode.Brief, Loc.T("简洁", "Brief")), (CompactMonitorMode.Custom, Loc.T("自选", "Custom"))],
            mode => { Change(s => s.CompactMonitor = mode); items.Visibility = mode == CompactMonitorMode.Custom ? Visibility.Visible : Visibility.Collapsed; }));
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
        panel.Children.Add(items);
        bool notch = style == CompactStyle.Notch;
        var fit = Toggle(Loc.T("按内容自动加宽", "Widen to the content"), S.CompactAutoWidth(style), on => Change(s => s.SetCompactAutoWidth(style, on)),
            help: Loc.T("内容放不下时自动加宽刘海或胶囊。拖动其左右两端或调整宽度滑块即手动设定宽度（会关闭此项），双击两端恢复。",
                "Widens the notch or capsule when its content does not fit. Dragging either end or moving the width slider sets the width by hand (and turns this off); a double click on an end turns it back on."));
        panel.Children.Add(fit);
        var hint = TextLine(Loc.T("放不下的读数按上传、GPU、下载、核心柱、内存的顺序隐藏，CPU 保留到最后。",
            "Readings that do not fit give way in turn: upload, GPU, download, the core columns, memory; CPU stays longest."), 11, TextTertiary);
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 6, 0, 10); panel.Children.Add(hint);
        return panel;
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
