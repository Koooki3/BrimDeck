using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The system monitor's own settings page ("系统监控 › 显示与读数"), as AI usage and music have theirs: how the expanded page looks
// and what the collapsed notch or capsule shows. The page's size stays with the other pages' sizes under "展开外观", and the
// switches that show the readings in the notch or the capsule stay under "收起外观 › 显示内容", next to the quota rings.
public sealed partial class SettingsWindow
{
    // The color scheme comes first on its own, as it colors both the collapsed readings and the expanded page; then what the
    // collapsed row shows and how the expanded page looks, under the music page's "收起时" and "展开时".
    private void MonitorSettings()
    {
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
            choice.Checked += (_, _) => { Resize(s => s.MonitorPalette = palette, DeckPage.Monitor); _refreshReadings?.Invoke(); };
            schemes.Children.Add(choice);
        }
        // The options stay available while the page is off, so they can be set before it is shown.
        SectionTitle(Loc.T("通用", "General"));
        Group(Row(Loc.T("配色", "Colors"), SegmentBorder(schemes)));
        CompactMonitorOptions();
        SectionTitle(Loc.T("展开时", "When expanded"));
        // The choices that show themselves on the monitor page keep its preview open, so switching between them does not collapse it first.
        Group(KeepPreview(MusicChoice(Loc.T("主视图", "Large gauge"), S.MonitorMain, [(MonitorMain.Cpu, "CPU"), (MonitorMain.Gpu, "GPU")], v => Resize(s => s.MonitorMain = v, DeckPage.Monitor))),
            MusicChoice(Loc.T("刷新间隔", "Update interval"), S.MonitorInterval, [(1, Loc.T("1 秒", "1 s")), (2, Loc.T("2 秒", "2 s"))], v => Change(s => s.MonitorInterval = v)),
            KeepPreview(MusicChoice(Loc.T("进程行", "Process rows"), S.MonitorProcessView,
                [(MonitorProcessView.Auto, Loc.T("自动", "Auto")), (MonitorProcessView.Compact, Loc.T("简略", "Compact")), (MonitorProcessView.Detailed, Loc.T("详细", "Detailed"))],
                v => Resize(s => s.MonitorProcessView = v, DeckPage.Monitor))),
            KeepPreview(Toggle(Loc.T("显示进程列表", "Show the process list"), S.MonitorProcesses, v => Resize(s => s.MonitorProcesses = v, DeckPage.Monitor),
                help:
                Loc.T("按 CPU 或内存列出占用最高的应用，同一程序的多个进程合并为一行。面板足够大时显示。",
                    "Lists the apps using the most CPU or memory, with the processes of one program folded into one row. Shown when the panel is large enough."))));
    }

    // What the collapsed notch or capsule shows when its "系统监控" switch is on: a table of the readings, laid out as the app list
    // is (drag handle, color circle, switches), with Full and Brief under it as quick presets; and whether each surface widens to its content.
    private Action? _refreshReadings;
    private const double ReadingRowHeight = 53;
    private void CompactMonitorOptions()
    {
        bool syncing = false;
        var refresh = new List<Action>();
        // Shows what the settings hold in place, without saving anything: the preset the table matches, the switches, the colors.
        void Refresh() { syncing = true; try { foreach (var update in refresh) update(); } finally { syncing = false; } }
        _refreshReadings = Refresh;
        void Edit(Action<DeckSettings> edit) { if (syncing) return; Change(edit); Refresh(); }
        // Clears the table's colors only, so every reading follows the color scheme again; which readings show, their figures
        // and their order stay (the presets below set those). Always available, as the size's reset is.
        var reset = ActionButton(Loc.T("恢复默认", "Restore defaults"), () => Edit(s => CompactMonitor.ResetColors(s.CompactReadings))); reset.FontSize = 11;
        reset.ToolTip = Loc.T("将各读数的主题、预警和告警颜色恢复为当前配色的颜色，不改变显示项、数值和顺序。",
            "Returns every reading's theme, warning and critical colors to the current color scheme's. Which readings show, their figures and their order stay.");
        AutomationProperties.SetName(reset, Loc.T("恢复收起时读数的默认颜色", "Restore the collapsed readings' default colors"));
        SectionTitle(Loc.T("收起时", "When collapsed"), reset);

        var rows = new StackPanel();
        foreach (var reading in S.CompactReadings) rows.Children.Add(ReadingRow(reading.Item, rows, refresh, Edit));
        UpdateDividers(rows);
        var headings = ReadingColumns(); headings.Height = 34;
        // The color columns and their notes as the app list has them: every reading has a color per level.
        AddHeadings(headings, new[]
        {
            (Loc.T("监控项", "Item"), 1, ""),
            (Loc.T("主题", "Theme"), 2, Loc.T("正常状态下的颜色。核心柱左侧为性能核，右侧为能效核。",
                "The color in the normal state. For the core columns, the left one is for performance cores and the right one for efficiency cores.")),
            (Loc.T("预警", "Warning"), 3, LevelHelp(MemoryLevel.Elevated)),
            (Loc.T("告警", "Critical"), 4, LevelHelp(MemoryLevel.Critical)),
            (Loc.T("显示数值", "Value"), 5, ""),
            (Loc.T("显示", "Show"), 6, "")
        });
        // The names in the rows start at the left of their column, so their heading does too rather than floating in its middle.
        var nameHeading = headings.Children.OfType<FrameworkElement>().First(heading => Grid.GetColumn(heading) == 1);
        nameHeading.HorizontalAlignment = HorizontalAlignment.Left; nameHeading.Margin = new Thickness(6, 0, 6, 0);
        var table = new StackPanel();
        table.Children.Add(headings);
        table.Children.Add(new Border { Child = rows, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0) });
        // Quick presets under the table, as the quick sizes: one sets which readings show and which rings carry their figure, never
        // the order or the colors. The one the table matches exactly is lit, else none; the table itself is the custom choice.
        // Each segment keeps the width of a quick size's.
        var presets = new UniformGrid { Columns = 2, Width = 196 };
        foreach (var (mode, label, help) in new[]
        {
            (CompactMonitorMode.Full, Loc.T("完整", "Full"), Loc.T("显示全部读数，CPU、内存和 GPU 带百分比。不改变顺序和颜色。", "Shows every reading, with the percentages of CPU, memory and GPU. The order and the colors stay.")),
            (CompactMonitorMode.Brief, Loc.T("简洁", "Brief"), Loc.T("只显示 CPU、内存和 GPU 的圆环，不带数值。不改变顺序和颜色。", "Shows only the CPU, memory and GPU rings, without figures. The order and the colors stay."))
        })
        {
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = label, GroupName = "CompactMonitorPreset", ToolTip = help };
            AutomationProperties.SetName(choice, Loc.T("快捷预设 ", "Preset ") + label); AutomationProperties.SetHelpText(choice, help);
            choice.Checked += (_, _) => { if (!syncing) Edit(s => CompactMonitor.Apply(s.CompactReadings, mode)); };
            refresh.Add(() => choice.IsChecked = CompactMonitor.Preset(S.CompactReadings) == mode);
            presets.Children.Add(choice);
        }
        var quick = QuickRow(Loc.T("快捷预设", "Presets"), presets);
        Refresh();
        Group(table, quick);
    }

    private static Grid ReadingColumns()
    {
        var grid = new Grid { Background = Brushes.Transparent };
        var swatch = SwatchColumn;
        // The theme column holds a swatch per channel (CompactMonitor.Channels), 4 apart as the clock's gradient stops are.
        int channels = CompactMonitor.Order.Max(CompactMonitor.Channels);
        var theme = new GridLength(Math.Max(swatch.Value, channels * 32 + (channels - 1) * 4 + 8));
        foreach (var width in new[] { new GridLength(40), new GridLength(1, GridUnitType.Star), theme, swatch, swatch,
            new GridLength(Loc.IsEnglish ? 76 : 84), new GridLength(64) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        return grid;
    }

    // The note of a level's color column, from the thresholds the levels are read with (ReadingLevels, MemorySample.Level).
    private static string LevelHelp(MemoryLevel level)
    {
        bool critical = level == MemoryLevel.Critical;
        double share = critical ? ReadingLevels.CriticalShare : ReadingLevels.ElevatedShare,
            commit = critical ? MemorySample.CriticalCommit : MemorySample.ElevatedCommit,
            rate = (critical ? ReadingLevels.CriticalRate : ReadingLevels.ElevatedRate) / 1_000_000;
        // The memory commit charge is always named: by its own threshold where it differs from the shares', else as part of memory.
        bool apart = commit != share;
        return Loc.T($"占用达到 {share * 100:0}%{(apart ? $"（内存提交量为 {commit * 100:0}%）" : "（内存包括内存提交量）")}或网速达到 {rate:0} MB/s 时的颜色。",
            $"The color when usage reaches {share * 100:0}%{(apart ? $" ({commit * 100:0}% for the memory commit charge)" : " (for memory, including the commit charge)")} or the network rate reaches {rate:0} MB/s.");
    }

    // A color column's level: the reading's normal color, then the warning and critical ones of a reading with levels.
    private static int SwatchColumnOf(MemoryLevel level) => level switch { MemoryLevel.Elevated => 3, MemoryLevel.Critical => 4, _ => 2 };

    private static string ReadingName(CompactMonitorItems item) => item switch
    {
        CompactMonitorItems.Columns => Loc.T("核心柱", "Core columns"), CompactMonitorItems.Memory => Loc.T("内存", "Memory"), CompactMonitorItems.Gpu => "GPU",
        CompactMonitorItems.Download => Loc.T("下载", "Download"), CompactMonitorItems.Upload => Loc.T("上传", "Upload"), _ => "CPU"
    };
    private static CompactReading Reading(DeckSettings settings, CompactMonitorItems item) => settings.CompactReadings.First(reading => reading.Item == item);

    // One row of the readings' table; its switches follow the settings when a preset changes them (refresh).
    private Border ReadingRow(CompactMonitorItems item, StackPanel rows, List<Action> refresh, Action<Action<DeckSettings>> edit)
    {
        string name = ReadingName(item);
        var row = ReadingColumns();
        var handle = new Border { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        var label = TextLine(name, 13, TextPrimary); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(label, 1); row.Children.Add(label);
        // Every reading has a color per level; in the normal state one per channel (the core columns: one per kind of core).
        foreach (var level in new[] { MemoryLevel.Normal, MemoryLevel.Elevated, MemoryLevel.Critical })
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(cell, SwatchColumnOf(level)); row.Children.Add(cell);
            int channels = level == MemoryLevel.Normal ? CompactMonitor.Channels(item) : 1;
            for (int channel = 0; channel < channels; channel++)
            {
                int at = channel;
                Button? swatch = null;
                void Swatch()
                {
                    var next = ReadingSwatch(item, level, at, Swatch);
                    // A panel will not take a new child at an index still holding one, so the old swatch leaves first.
                    int index = swatch is null ? -1 : cell.Children.IndexOf(swatch);
                    if (index < 0) cell.Children.Add(next); else { cell.Children.RemoveAt(index); cell.Children.Insert(index, next); }
                    swatch = next;
                }
                Swatch();
                refresh.Add(Swatch);
            }
        }

        // The figure switch is for the rings only; the core columns (no figure) and the rates (always text) show it off and dimmed.
        bool choice = CompactMonitor.HasValueChoice(item);
        var value = new CheckBox { Style = (Style)FindResource("SettingsToggle"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = choice, IsChecked = item is CompactMonitorItems.Download or CompactMonitorItems.Upload };
        AutomationProperties.SetName(value, Loc.T("显示数值 ", "Show figure ") + name);
        if (!choice)
        {
            value.ToolTip = item == CompactMonitorItems.Columns ? Loc.T("核心柱没有数值。", "The core columns have no figure.") : Loc.T("下载和上传始终显示速率。", "Download and upload always show their rate.");
            ToolTipService.SetShowOnDisabled(value, true);
        }
        else
        {
            value.Checked += (_, _) => edit(s => Reading(s, item).ShowValue = true); value.Unchecked += (_, _) => edit(s => Reading(s, item).ShowValue = false);
            refresh.Add(() => value.IsChecked = Reading(S, item).ShowValue);
        }
        Grid.SetColumn(value, 5); row.Children.Add(value);

        var shown = new CheckBox { Style = (Style)FindResource("SettingsToggle"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(shown, Loc.T("显示 ", "Show ") + name);
        shown.Checked += (_, _) => edit(s => Reading(s, item).Visible = true); shown.Unchecked += (_, _) => edit(s => Reading(s, item).Visible = false);
        refresh.Add(() => shown.IsChecked = Reading(S, item).Visible);
        Grid.SetColumn(shown, 6); row.Children.Add(shown);

        // Dropped in a new place, the strip takes the new order at once.
        AttachDrag(handle, () => rows, ReadingRowHeight, list =>
        {
            var order = list.Children.OfType<Border>().Where(b => b.Tag is CompactMonitorItems).Select(b => (CompactMonitorItems)b.Tag).ToArray();
            Change(s => s.CompactReadings = order.Select(id => Reading(s, id)).ToList());
        });
        return new Border { Child = row, Height = ReadingRowHeight, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0), Tag = item, RenderTransform = new TranslateTransform() };
    }

    // The name of a reading's color channel where it has more than one (CompactMonitor.Channels).
    private static string? ChannelName(CompactMonitorItems item, int channel) => CompactMonitor.Channels(item) < 2 ? null
        : item == CompactMonitorItems.Columns ? channel == 0 ? Loc.T("性能核", "Performance cores") : Loc.T("能效核", "Efficiency cores") : null;

    // The reading's color circle at a level and channel: its own color, or the color scheme's as the strip draws it. The
    // picker's "默认" follows the scheme again.
    private Button ReadingSwatch(CompactMonitorItems item, MemoryLevel level, int channel, Action refreshed)
    {
        var colors = MonitorThemeColors.Strip(S.MonitorPalette, S.CompactReadings);
        string? own = Reading(S, item).ColorAt(level, channel);
        string color = own ?? colors.DefaultAt(item, level, channel), name = ReadingName(item);
        string label = level switch { MemoryLevel.Elevated => Loc.T("预警", "warning"), MemoryLevel.Critical => Loc.T("告警", "critical"), _ => Loc.T("主题", "theme") };
        var button = new Button { Style = (Style)FindResource("SettingsColorButton"), Foreground = UI.Brush(colors.At(item, level, channel)) };
        // Two swatches in one cell tell themselves apart by a note, as the clock's gradient stops do.
        if (ChannelName(item, channel) is { } part)
        {
            name += " " + part; button.ToolTip = part;
            if (channel > 0) button.Margin = new Thickness(4, 0, 0, 0);
        }
        AutomationProperties.SetName(button, Loc.T($"{name} {label}颜色", $"{name} {label} color"));
        AutomationProperties.SetHelpText(button, color + (own is null ? Loc.T("（跟随配色）", " (follows Colors)") : ""));
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        popup.Child = ColorPicker(color, own is null, value =>
        {
            popup.IsOpen = false;
            Change(s => Reading(s, item).SetColor(level, value.Length > 0 ? value : null, channel));
            refreshed();
        });
        TogglePopup(button, popup);
        return button;
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
