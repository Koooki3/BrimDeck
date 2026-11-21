using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The explicit two-column monitor grid. Readings use the same drawing blocks and Update path as the order-only view; only their
// containers and placement differ. A logical row matches the settings editor, and the whole surface scrolls vertically.
internal sealed partial class MonitorView
{
    private const double GridRowHeight = 48;
    private readonly bool _gridMode;
    private ScrollViewer? _gridScroll;
    private Grid? _gridSurface;
    private readonly Dictionary<string, TextBlock> _gridMissingDrives = new(StringComparer.OrdinalIgnoreCase);

    public MonitorView(IReadOnlyList<ExpandedGridTile> tiles, bool twoColumns, MonitorLayout layout, MonitorColors colors, MonitorMain main, ProcessSort sort,
        MonitorProcessView processView, Action<ProcessSort> sortChanged, Action<bool> hold, Action<GpuSample> gpuChanged, bool temperatures,
        bool compactMemoryBar, PowerOptions power, CoreDisplay coreDisplay = CoreDisplay.Auto, bool free = false, int matrixColumns = 0,
        FrameworkElement? emptyMessage = null)
    {
        _gridMode = true;
        _layout = layout;
        _colors = colors;
        _main = main;
        _temperatures = temperatures;
        ClipToBounds = true;

        if (tiles.Count == 0)
        {
            if (emptyMessage is not null) Children.Add(emptyMessage);
            return;
        }

        int rowCount = Math.Max(1, tiles.Max(tile => tile.Row + tile.Rows));
        var surface = _gridSurface = new Grid { ClipToBounds = true };
        int columns = twoColumns ? ExpandedGrid.ColumnCount : 1;
        for (int column = 0; column < columns; column++)
            surface.ColumnDefinitions.Add(new ColumnDefinition());
        for (int row = 0; row < rowCount; row++)
            surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(GridRowHeight) });

        foreach (var tile in tiles)
        {
            var reading = GridReading(tile, layout, colors, sort, processView, sortChanged, hold, gpuChanged, temperatures,
                compactMemoryBar, power, coreDisplay, free, matrixColumns);
            var frame = new Border
            {
                Child = reading,
                BorderThickness = new Thickness(0),
                ClipToBounds = true,
                Margin = new Thickness(2),
                Padding = new Thickness(5, 0, 5, 0)
            };
            Grid.SetRow(frame, tile.Row);
            Grid.SetColumn(frame, tile.Column);
            Grid.SetRowSpan(frame, tile.Rows);
            Grid.SetColumnSpan(frame, tile.Columns);
            surface.Children.Add(frame);
        }

        var scroll = _gridScroll = new ScrollViewer
        {
            Content = surface,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = true,
            ClipToBounds = true
        };
        scroll.AddHandler(MouseWheelEvent, new MouseWheelEventHandler((_, e) =>
        {
            if (scroll.ScrollableHeight < 1) e.Handled = false;
        }), true);
        AutomationProperties.SetName(scroll, Loc.T("展开监控网格", "Expanded monitor grid"));
        Children.Add(scroll);
        SetColumnSpan(_overlay, columns);
        Children.Add(_overlay);
    }

    private FrameworkElement GridReading(ExpandedGridTile tile, MonitorLayout layout, MonitorColors colors, ProcessSort sort,
        MonitorProcessView processView, Action<ProcessSort> sortChanged, Action<bool> hold, Action<GpuSample> gpuChanged, bool temperatures,
        bool compactMemoryBar, PowerOptions power, CoreDisplay coreDisplay, bool free, int matrixColumns)
    {
        switch (tile.Item)
        {
            case ExpandedItem.Cpu:
            {
                var content = GaugeTile(cpu: true, tile.Rows, layout, colors, coreDisplay, matrixColumns, gpuChanged);
                return content;
            }
            case ExpandedItem.Gpu:
                return GaugeTile(cpu: false, tile.Rows, layout, colors, coreDisplay, matrixColumns, gpuChanged);
            case ExpandedItem.Memory:
                return _memory = new MemoryBlock(colors, compact: false, layout.Body, layout.Caption, free, compactMemoryBar)
                { VerticalAlignment = VerticalAlignment.Top };
            case ExpandedItem.Download:
                return _downloadCard = new NetworkBlock(colors, MetricsMode.Lines, layout.Body, layout.Caption, download: true, upload: false)
                { VerticalAlignment = VerticalAlignment.Top };
            case ExpandedItem.Upload:
                return _uploadCard = new NetworkBlock(colors, MetricsMode.Lines, layout.Body, layout.Caption, download: false, upload: true)
                { VerticalAlignment = VerticalAlignment.Top };
            case ExpandedItem.Frames:
                return _frames = new FramesBlock(colors[MonitorRole.Frames], layout.Body, layout.Caption, layout.StorageRow)
                { VerticalAlignment = VerticalAlignment.Top };
            case ExpandedItem.Storage when tile.Letter is { } letter:
            {
                var drive = new StorageBlock(colors, layout.Body, layout.Caption, layout.StorageRow, 1, 1, temperatures, free, alignCardBaseline: true)
                { VerticalAlignment = VerticalAlignment.Top };
                _driveCards.Add((letter, drive));
                var host = new Grid();
                host.Children.Add(drive);
                var missing = new TextBlock
                {
                    Text = Loc.T($"{letter} 暂无读数", $"{letter} · No drive reading"),
                    FontSize = layout.Caption,
                    Foreground = UI.Brush(Look.Tertiary),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
                _gridMissingDrives[letter] = missing;
                host.Children.Add(missing);
                return host;
            }
            case ExpandedItem.Power:
                return _power = new PowerBlock(colors, power, layout.Body, layout.Caption, layout.StorageRow)
                { VerticalAlignment = VerticalAlignment.Top };
            case ExpandedItem.Processes:
                _list = new ProcessListView(layout, sort, processView, sortChanged, hold, Open);
                return _list!;
            default:
                return new TextBlock { Text = Loc.T("暂无读数", "No reading"), Foreground = UI.Brush(Look.Tertiary),
                    FontSize = layout.Caption, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        }
    }

    private FrameworkElement GaugeTile(bool cpu, int rows, MonitorLayout layout, MonitorColors colors, CoreDisplay coreDisplay,
        int matrixColumns, Action<GpuSample> gpuChanged)
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (rows >= 2)
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 16 });

        if (cpu)
        {
            var gauge = _cpuGauge = new FigureGauge("C", colors.Performance, layout.Figure, layout.Caption);
            gauge.VerticalAlignment = VerticalAlignment.Top;
            content.Children.Add(gauge);
            if (rows >= 2)
            {
                var graph = _cpuGraph = new ColumnGraph(double.PositiveInfinity, layout.Caption, labels: false,
                    name: Loc.T("CPU 各核心占用", "CPU usage by core"), display: coreDisplay, matrixColumns: matrixColumns)
                { Margin = new Thickness(0, 4, 0, 2) };
                Grid.SetRow(graph, 1);
                content.Children.Add(graph);
            }
        }
        else
        {
            var gauge = _gpuGauge = new FigureGauge("G", colors.Gpu, layout.Figure, layout.Caption);
            gauge.VerticalAlignment = VerticalAlignment.Top;
            gauge.Clicked += () => { if (_nextGpu is { } next) gpuChanged(next); };
            content.Children.Add(gauge);
            if (rows >= 2)
            {
                var graph = _gpuGraph = new ColumnGraph(EngineColumn, layout.Caption, labels: !layout.CompactMetrics, gap: 6,
                    name: Loc.T("GPU 各引擎占用", "GPU usage by engine"))
                { Margin = new Thickness(0, 4, 0, 2) };
                Grid.SetRow(graph, 1);
                content.Children.Add(graph);
            }
        }
        return content;
    }

    public void KeepGridScrollFrom(MonitorView previous)
    {
        var scroll = _gridScroll;
        if (scroll is null || previous._gridScroll is not { } old || old.VerticalOffset <= 0) return;
        double offset = old.VerticalOffset;
        RoutedEventHandler? restore = null;
        restore = (_, _) =>
        {
            scroll.Loaded -= restore;
            scroll.ScrollToVerticalOffset(Math.Min(offset, scroll.ScrollableHeight));
        };
        scroll.Loaded += restore;
    }

    private void SetGridDrivePresence(string letter, bool present)
    {
        if (_gridMissingDrives.TryGetValue(letter, out var placeholder))
            placeholder.Visibility = present ? Visibility.Collapsed : Visibility.Visible;
    }
}
