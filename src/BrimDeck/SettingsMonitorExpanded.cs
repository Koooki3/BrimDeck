using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The readings the expanded monitor page shows ("展开时"), a table laid out as the collapsed row's is: drag handle (Alt+Up/Down in a
// row moves it from the keyboard), the reading's colors from the scheme, its name, and a switch. The order is the page's order top to
// bottom. Quick presets under it. The process list has a switch of its own above, not a row: its place is chosen by the page.
public sealed partial class SettingsWindow
{
    private const double ExpandedRowHeight = 44;

    private static string ExpandedName(ExpandedItem item) => item switch
    {
        ExpandedItem.Gpu => "GPU", ExpandedItem.Memory => Loc.T("内存", "Memory"), ExpandedItem.Download => Loc.T("下载", "Download"), ExpandedItem.Upload => Loc.T("上传", "Upload"),
        ExpandedItem.Frames => Loc.T("帧率", "Frame rate"), ExpandedItem.Storage => Loc.T("硬盘", "Drives"), ExpandedItem.Power => Loc.T("功耗与电池", "Power and battery"),
        ExpandedItem.Processes => Loc.T("进程列表", "Process list"), _ => "CPU"
    };

    private static string ExpandedHelp(ExpandedItem item) => item switch
    {
        ExpandedItem.Cpu => Loc.T("CPU 总占用和各核心的柱。", "The CPU's total usage and its columns, one per core."),
        ExpandedItem.Gpu => Loc.T("GPU 占用和各引擎的柱。两项都显示时，上方“主视图”决定哪一个大。", "The GPU's usage and its columns, one per engine. With both shown, Large gauge above says which is large."),
        ExpandedItem.Memory => Loc.T("内存占用与提交量。", "Memory in use and the commit charge."),
        ExpandedItem.Download => Loc.T("下载速率；与上传同占一行。", "The download rate; it shares a line with the upload."),
        ExpandedItem.Upload => Loc.T("上传速率；与下载同占一行。", "The upload rate; it shares a line with the download."),
        ExpandedItem.Frames => Loc.T("前台程序的帧率、程序名称和所在显示器的刷新率。只在面板展开到系统监控页时读取。首次开启时需要授权。",
            "The frame rate of the program in front, with its name and its display's refresh rate. Read only while the panel is open on the system monitor page. Needs permission the first time."),
        ExpandedItem.Storage => Loc.T("不含 U 盘、移动硬盘、网络盘和光驱。", "USB, external, network and optical drives are not included."),
        ExpandedItem.Power => Loc.T("整机功耗和本机电池。只在面板展开到系统监控页时读取。", "System power and this PC's battery. Read only while the panel is open on the system monitor page."),
        _ => ""
    };

    // The scheme's colors of an item, one dot each: the CPU's fastest and slowest cores, the others one.
    private static MonitorRole[] ExpandedRoles(ExpandedItem item) => item switch
    {
        ExpandedItem.Cpu => [MonitorRole.Cpu, MonitorRole.Efficiency], ExpandedItem.Gpu => [MonitorRole.Gpu], ExpandedItem.Memory => [MonitorRole.Memory],
        ExpandedItem.Download => [MonitorRole.Download], ExpandedItem.Upload => [MonitorRole.Upload], ExpandedItem.Frames => [MonitorRole.Frames],
        ExpandedItem.Storage => [MonitorRole.Storage], ExpandedItem.Power => [MonitorRole.Power, MonitorRole.Battery], _ => []
    };

    private static ExpandedReading ExpandedOf(DeckSettings settings, ExpandedItem item) => settings.ExpandedReadings.First(reading => reading.Item == item);

    // Changes the table and shows the page, as the switches above do. The list is the settings' own or, before one is set, the one
    // the old switches give.
    private void EditExpanded(Action<List<ExpandedReading>> edit)
        => Resize(s => { var list = ExpandedMonitor.Normalize(s.ExpandedReadings); edit(list); s.ExpandedReadings = list; }, DeckPage.Monitor);

    private Action? _refreshExpanded;

    private Grid ExpandedColumns()
    {
        var grid = new Grid { Background = Brushes.Transparent };
        foreach (var width in new[] { new GridLength(40), new GridLength(ReadingDot * 2 + 3 + 6), new GridLength(1, GridUnitType.Star), new GridLength(64) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        return grid;
    }

    // The table with its presets, for the "展开时" group.
    private (FrameworkElement Table, FrameworkElement Presets) ExpandedOptions()
    {
        bool syncing = false;
        var refresh = new List<Action>();
        void Refresh() { syncing = true; try { foreach (var update in refresh) update(); } finally { syncing = false; } }
        _refreshExpanded = Refresh;
        void Edit(Action<List<ExpandedReading>> edit) { if (syncing) return; EditExpanded(edit); Refresh(); }
        var rows = new StackPanel();
        foreach (var reading in ExpandedMonitor.Normalize(S.ExpandedReadings).Where(reading => reading.Item != ExpandedItem.Processes))
            rows.Children.Add(ExpandedRow(reading.Item, rows, refresh, Edit));
        UpdateDividers(rows);
        var headings = ExpandedColumns(); headings.Height = 34;
        AddHeadings(headings, new[] { (Loc.T("监控项", "Item"), 2, ""), (Loc.T("显示", "Show"), 3, "") });
        var nameHeading = headings.Children.OfType<FrameworkElement>().First(heading => Grid.GetColumn(heading) == 2);
        nameHeading.HorizontalAlignment = HorizontalAlignment.Left; nameHeading.Margin = new Thickness(6, 0, 6, 0);
        var table = new StackPanel();
        table.Children.Add(headings);
        table.Children.Add(new Border { Child = rows, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0) });
        var empty = TextLine(Loc.T("一个都不选时，展开页显示提示，并且展开页不请求任何数据（进程列表开关打开时除外）。", "With nothing chosen the expanded page says so and asks for no data (unless the process list switch is on)."), 12, TextTertiary);
        empty.Margin = new Thickness(0, 6, 0, 6); empty.TextWrapping = TextWrapping.Wrap;
        table.Children.Add(empty);

        var presets = new UniformGrid { Columns = 4, Width = Loc.IsEnglish ? 480 : 420 };
        foreach (var (preset, label, help) in new[]
        {
            (ExpandedPreset.All, Loc.T("全部", "All"), Loc.T("显示全部监控项；帧率需要授权，保持原状。不改变顺序和进程列表。", "Shows every reading; the frame rate needs permission and stays as it is. The order and the process list stay.")),
            (ExpandedPreset.Brief, Loc.T("简洁", "Brief"), Loc.T("只显示 CPU、内存、下载和上传。", "Shows only the CPU, memory, download and upload.")),
            (ExpandedPreset.CpuMemory, Loc.T("CPU 与内存", "CPU and memory"), Loc.T("只显示 CPU 和内存。", "Shows only the CPU and memory.")),
            (ExpandedPreset.None, Loc.T("全部不选", "None"), Loc.T("清空表格中的监控项，不影响进程列表。要显示空状态并停止展开页的采集，还需关闭“显示进程列表”开关。", "Clears the table items only; the process list is not affected. To get the empty state and stop the expanded page collecting, also turn off the process list switch."))
        })
        {
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = label, GroupName = "ExpandedPreset" };
            AutomationProperties.SetName(choice, Loc.T("展开时快捷预设 ", "Expanded preset ") + label); AutomationProperties.SetHelpText(choice, help); choice.ToolTip = help;
            choice.Checked += (_, _) => { if (!syncing) Edit(list => ExpandedMonitor.Apply(list, preset)); };
            refresh.Add(() => choice.IsChecked = ExpandedMonitor.Preset(S.ExpandedReadings) == preset);
            presets.Children.Add(choice);
        }
        Refresh();
        return (KeepPreview(table), KeepPreview(QuickRow(Loc.T("快捷预设", "Presets"), presets)));
    }

    private Border ExpandedRow(ExpandedItem item, StackPanel rows, List<Action> refresh, Action<Action<List<ExpandedReading>>> edit)
    {
        string name = ExpandedName(item);
        var row = ExpandedColumns();
        var handle = new GripHandle { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        var label = TextLine(name, 13, TextPrimary); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(6, 0, 6, 0);
        label.ToolTip = ExpandedHelp(item);
        Grid.SetColumn(label, 2); row.Children.Add(label);
        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
            ToolTip = Loc.T("颜色在上方“各项颜色”中设置。", "Set under Reading colors above.") };
        var roles = ExpandedRoles(item);
        var fills = roles.Select((_, channel) => new Ellipse { Width = ReadingDot, Height = ReadingDot, Margin = new Thickness(channel == 0 ? 0 : 3, 0, 0, 0) }).ToArray();
        foreach (var fill in fills) dots.Children.Add(fill);
        refresh.Add(() =>
        {
            var colors = MonitorThemeColors.For(S);
            for (int channel = 0; channel < fills.Length; channel++) fills[channel].Fill = UI.Brush(RoleColor(colors, roles[channel]));
        });
        Grid.SetColumn(dots, 1); row.Children.Add(dots);

        var shown = new CheckBox { Style = (Style)FindResource("SettingsToggle"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(shown, Loc.T("展开时显示 ", "Show when expanded: ") + name); AutomationProperties.SetHelpText(shown, ExpandedHelp(item));
        shown.Checked += (_, _) =>
        {
            bool was = ExpandedOf(S, item).Visible;
            edit(list => list.First(reading => reading.Item == item).Visible = true);
            // Only a frame rate the user turns on asks for permission.
            if (item == ExpandedItem.Frames && !was) AskFrameAccess();
        };
        shown.Unchecked += (_, _) => { edit(list => list.First(reading => reading.Item == item).Visible = false); if (item == ExpandedItem.Frames) FrameAccessChanged?.Invoke(); };
        refresh.Add(() => shown.IsChecked = ExpandedOf(S, item).Visible);
        Grid.SetColumn(shown, 3); row.Children.Add(shown);

        AttachDrag(handle, name, () => rows, ExpandedRowHeight, list =>
        {
            var order = list.Children.OfType<Border>().Where(b => b.Tag is ExpandedItem).Select(b => (ExpandedItem)b.Tag).ToArray();
            Change(s =>
            {
                var current = ExpandedMonitor.Normalize(s.ExpandedReadings);
                s.ExpandedReadings = [.. order.Select(id => current.First(reading => reading.Item == id)), .. current.Where(reading => !order.Contains(reading.Item))];
            });
        });
        return new Border { Child = row, Height = ExpandedRowHeight, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0), Tag = item, RenderTransform = new TranslateTransform() };
    }
}
