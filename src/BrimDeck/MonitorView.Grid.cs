using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The monitor grid as the settings arrange it. A column is always a quarter of the page (ExpandedGrid.ColumnCount): a layout using
// fewer columns (ExpandedGrid.Trim) keeps that width and sits in the middle, rather than being stretched across. Readings use the
// same drawing blocks and Update path as the order-only view; only their containers and placement differ. Neither the arrangement
// nor a tile's share of it changes with the page's size: every row has one height, so a tile two rows high is twice a line of
// readings as the settings draw it, and a graph grows by giving its tile more rows there. Rows that do not fill the page sit in
// its middle; on a page too short for them the rows first close up to the least a line needs, and then the page scrolls.
internal sealed partial class MonitorView
{
    private readonly bool _gridMode;
    private ScrollViewer? _gridScroll;
    private Grid? _gridSurface;
    private readonly Dictionary<string, TextBlock> _gridMissingDrives = new(StringComparer.OrdinalIgnoreCase);
    // A CPU or GPU tile one row high has no room for its graph: the room after its figure carries the last minute's usage instead.
    private bool _cpuTrend, _gpuTrend;

    public MonitorView(IReadOnlyList<ExpandedGridTile> tiles, int columns, MonitorLayout layout, MonitorColors colors, MonitorMain main, ProcessSort sort,
        MonitorProcessView processView, Action<ProcessSort> sortChanged, Action<bool> hold, Action<GpuSample> gpuChanged, bool temperatures,
        PowerOptions power, CoreDisplay coreDisplay = CoreDisplay.Auto, bool free = false, int matrixColumns = 0,
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
        var surface = _gridSurface = new Grid { ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center };
        for (int column = 0; column < columns; column++)
            surface.ColumnDefinitions.Add(new ColumnDefinition());
        // A row's height: room for a gauge's figure, the tallest single line, with air around it; and the least, the figure's alone.
        double rowHeight = Math.Max(34, Math.Ceiling(layout.Figure * 1.4) + 6), leastHeight = Math.Max(26, Math.Ceiling(layout.Figure * 1.4) + 4);
        for (int row = 0; row < rowCount; row++)
            surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowHeight) });
        surface.Height = rowCount * rowHeight;
        surface.VerticalAlignment = VerticalAlignment.Center;

        foreach (var tile in tiles)
        {
            var reading = GridReading(tile, layout, colors, sort, processView, sortChanged, hold, gpuChanged, temperatures,
                power, coreDisplay, free, matrixColumns);
            // A reading sits in the middle of its tile's height, so readings side by side in a row line up however many lines each has.
            if (tile.Item is not (ExpandedItem.Cpu or ExpandedItem.Gpu or ExpandedItem.Processes))
                reading.VerticalAlignment = VerticalAlignment.Center;
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
        scroll.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0) CloseCard();
        };
        scroll.SizeChanged += (_, _) =>
        {
            double height = Math.Clamp(Math.Floor(scroll.ActualHeight / rowCount), leastHeight, rowHeight);
            foreach (var row in surface.RowDefinitions) row.Height = new GridLength(height);
            surface.Height = rowCount * height;
            surface.Width = Math.Max(0, scroll.ActualWidth * columns / ExpandedGrid.ColumnCount);
        };
        AutomationProperties.SetName(scroll, Loc.T("展开监控网格", "Expanded monitor grid"));
        Children.Add(scroll);
        SetColumnSpan(_overlay, columns);
        Children.Add(_overlay);
    }

    private FrameworkElement GridReading(ExpandedGridTile tile, MonitorLayout layout, MonitorColors colors, ProcessSort sort,
        MonitorProcessView processView, Action<ProcessSort> sortChanged, Action<bool> hold, Action<GpuSample> gpuChanged, bool temperatures,
        PowerOptions power, CoreDisplay coreDisplay, bool free, int matrixColumns)
    {
        switch (tile.Item)
        {
            case ExpandedItem.Cpu:
            {
                var content = GaugeTile(cpu: true, tile.Columns, tile.Rows, layout, colors, coreDisplay, matrixColumns, gpuChanged);
                return content;
            }
            case ExpandedItem.Gpu:
                return GaugeTile(cpu: false, tile.Columns, tile.Rows, layout, colors, coreDisplay, matrixColumns, gpuChanged);
            case ExpandedItem.Memory:
                // One row high: the figure and the bar on one line, level with the readings beside it; taller: the bar under the figure.
                return _memory = new MemoryBlock(colors, compact: tile.Rows < 2, layout.Body, layout.Caption, free)
                { VerticalAlignment = VerticalAlignment.Top };
            case ExpandedItem.Download:
                return _downloadCard = new NetworkBlock(colors, MetricsMode.Lines, layout.Body, layout.Caption, download: true, upload: false, steady: true)
                { VerticalAlignment = VerticalAlignment.Top };
            case ExpandedItem.Upload:
                return _uploadCard = new NetworkBlock(colors, MetricsMode.Lines, layout.Body, layout.Caption, download: false, upload: true, steady: true)
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

    // The height decides what a gauge shows. One row: the figure, with the last minute's usage after it where the tile is two columns
    // or wider (a single cell shows the figure alone). Two rows or more: the graph under the figure, at any width: the cores'
    // columns, and for the GPU its engines' columns, or one wide column in a tile one column wide (as the small GPU of the page
    // before the grid), where the engines' names would not fit.
    private FrameworkElement GaugeTile(bool cpu, int columns, int rows, MonitorLayout layout, MonitorColors colors, CoreDisplay coreDisplay,
        int matrixColumns, Action<GpuSample> gpuChanged)
    {
        bool graph = rows >= 2;
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (columns >= 2 && !graph) { if (cpu) _cpuTrend = true; else _gpuTrend = true; }
        // Without the graph the figure sits in the middle of the tile, level with the readings beside it.
        if (!graph) content.VerticalAlignment = VerticalAlignment.Center;
        if (graph)
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 16 });

        if (cpu)
        {
            var gauge = _cpuGauge = new FigureGauge("C", colors.Performance, layout.Figure, layout.Caption);
            gauge.VerticalAlignment = VerticalAlignment.Top;
            content.Children.Add(gauge);
            if (graph)
            {
                var cores = _cpuGraph = new ColumnGraph(double.PositiveInfinity, layout.Caption, labels: false,
                    name: Loc.T("CPU 各核心占用", "CPU usage by core"), display: coreDisplay, matrixColumns: matrixColumns)
                { Margin = new Thickness(0, 4, 0, 2) };
                Grid.SetRow(cores, 1);
                content.Children.Add(cores);
            }
        }
        else
        {
            var gauge = _gpuGauge = new FigureGauge("G", colors.Gpu, layout.Figure, layout.Caption);
            gauge.VerticalAlignment = VerticalAlignment.Top;
            gauge.Clicked += () => { if (_nextGpu is { } next) gpuChanged(next); };
            content.Children.Add(gauge);
            if (graph && columns < 2)
            {
                // The block is the page before the grid's button for making the GPU large; here it only shows the reading.
                var block = _gpuBlock = new BlockGauge(colors.Gpu, "", layout.Caption) { Margin = new Thickness(0, 4, 0, 2), Cursor = null, IsHitTestVisible = false };
                Grid.SetRow(block, 1);
                content.Children.Add(block);
            }
            else if (graph)
            {
                var engines = _gpuGraph = new ColumnGraph(EngineColumn, layout.Caption, labels: !layout.CompactMetrics, gap: 6,
                    name: Loc.T("GPU 各引擎占用", "GPU usage by engine"))
                { Margin = new Thickness(0, 4, 0, 2) };
                Grid.SetRow(engines, 1);
                content.Children.Add(engines);
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
