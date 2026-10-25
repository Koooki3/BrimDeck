using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// A monitor color scheme as the panel draws it on the panel theme in use: ftop's palettes in their light or dark variant by the
// theme, or the theme's own colors (its accent and its cyan, pink and lime), or the custom colors over the scheme they start
// from, each moved just far enough to read on the theme's surface. The page is built again when the theme changes
// (PanelTheme.Generation is part of its key). The warning and critical colors are the collapsed readings' own settings, or the
// panel theme's.
internal static class MonitorThemeColors
{
    public static MonitorColors For(DeckSettings settings) => For(settings.MonitorPalette, settings.MonitorCustomColors) with
    {
        Warning = Warning(settings), Critical = Critical(settings)
    };
    public static string Warning(DeckSettings settings) => Drawn(settings.CompactWarningColor ?? PanelTheme.WarningDefault);
    public static string Critical(DeckSettings settings) => Drawn(settings.CompactCriticalColor ?? PanelTheme.CriticalDefault);

    public static MonitorColors For(MonitorPalette palette, MonitorCustomColors custom)
    {
        if (palette == MonitorPalette.Custom)
            return custom.Over(For(custom.Base == MonitorPalette.Custom ? MonitorPalette.Theme : custom.Base, custom), Drawn);
        var theme = PanelTheme.Current;
        if (palette == MonitorPalette.Theme)
            return MonitorColors.FromTheme(Drawn(theme["accent"].ToString()),
                [.. new[] { ClockColor.Cyan, ClockColor.Pink, ClockColor.Lime, ClockColor.Amber }.Select(color => Drawn(theme.ClockPreset(color).ToString()))],
                Drawn(PanelTheme.WarningDefault), Drawn(PanelTheme.CriticalDefault));
        return MonitorColors.For(palette, theme.IsDark).Map(Drawn);
    }
    // Opaque "#RRGGBB" as drawn on the theme, which ColorMath needs for the core groups' shades and for telling colors apart.
    private static string Drawn(string color) => ThemeColor.Opaque(PanelTheme.Readable(ThemeColor.Opaque(color)));

    public static StripColors Strip(DeckSettings settings) => new(For(settings));
}

// The system monitor page, after ftop's panel: the main gauge large with its columns (the processor's cores, or the graphics
// adapter's engines), the other gauge small beside it (a click on either swaps them), memory and network below, and the
// busiest apps beside or under the readings. Every part draws itself; Update hands each its new reading.
internal sealed class MonitorView : Grid
{
    private readonly MonitorLayout _layout;
    private readonly MonitorColors _colors;
    private readonly MonitorMain _main;
    private readonly FigureGauge _cpuGauge, _gpuGauge;
    private readonly ColumnGraph _cpuGraph;
    private readonly ColumnGraph? _gpuGraph;
    private readonly BlockGauge? _gpuBlock;
    private readonly MemoryBlock _memory;
    private readonly NetworkBlock _network;
    private readonly ProcessListView? _list;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private ProcessCard? _card;
    private ProcessList? _processes;
    private int _cores = Environment.ProcessorCount;
    private bool _motion;

    public MonitorView(MonitorLayout layout, MonitorColors colors, MonitorMain main, ProcessSort sort, MonitorProcessView processView,
        Action<MonitorMain> mainChanged, Action<ProcessSort> sortChanged, Action<bool> hold)
    {
        _layout = layout; _colors = colors; _main = main;
        bool cpuMain = main == MonitorMain.Cpu;
        string swapToGpu = Loc.T("切换为以 GPU 为主", "Show the GPU large"), swapToCpu = Loc.T("切换为以 CPU 为主", "Show the CPU large");
        var readings = new Grid();
        readings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        readings.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 16 });
        readings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        readings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // The gauge shown small takes a fixed width at the right: a wide block for the GPU, thin columns for the processor.
        double mini = cpuMain ? Math.Round(Math.Clamp(layout.Figure * 3.4, 54, 84)) : Math.Round(Math.Clamp(layout.Figure * 5, 80, 120));
        double headMini = Math.Max(mini, Math.Round(layout.Figure * 4.6));
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(headMini) });
        _cpuGauge = new FigureGauge("C", colors.Performance, cpuMain ? layout.Figure : layout.Body + 1, layout.Caption, right: !cpuMain, button: cpuMain ? null : swapToCpu);
        _gpuGauge = new FigureGauge("G", colors.Gpu, cpuMain ? layout.Body + 1 : layout.Figure, layout.Caption, right: cpuMain, button: cpuMain ? swapToGpu : null);
        var big = cpuMain ? _cpuGauge : _gpuGauge; var small = cpuMain ? _gpuGauge : _cpuGauge;
        small.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(small, 1); head.Children.Add(big); head.Children.Add(small);
        small.Clicked += () => mainChanged(cpuMain ? MonitorMain.Gpu : MonitorMain.Cpu);
        readings.Children.Add(head);

        var graphs = new Grid { Margin = new Thickness(0, layout.CompactMetrics ? 4 : 6, 0, layout.CompactMetrics ? 6 : 10) };
        graphs.ColumnDefinitions.Add(new ColumnDefinition());
        graphs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        graphs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(mini) });
        if (cpuMain)
        {
            // The cores carry no figures: the fill is the load, the tick the clock, and pointing at a core gives both.
            _cpuGraph = new ColumnGraph(26, layout.Caption, labels: false);
            _gpuBlock = new BlockGauge(colors.Gpu, swapToGpu);
            _gpuBlock.Clicked += () => mainChanged(MonitorMain.Gpu);
            SetColumn(_gpuBlock, 2); graphs.Children.Add(_cpuGraph); graphs.Children.Add(_gpuBlock);
        }
        else
        {
            _gpuGraph = new ColumnGraph(44, layout.Caption, !layout.CompactMetrics, gap: 6);
            _cpuGraph = new ColumnGraph(6, layout.Caption, false, gap: 1.5, button: swapToCpu);
            _cpuGraph.Clicked += () => mainChanged(MonitorMain.Cpu);
            SetColumn(_cpuGraph, 2); graphs.Children.Add(_gpuGraph); graphs.Children.Add(_cpuGraph);
        }
        SetRow(graphs, 1); readings.Children.Add(graphs);

        _memory = new MemoryBlock(colors, layout.Metrics != MetricsMode.Blocks, layout.Body, layout.Caption);
        _network = new NetworkBlock(colors, layout.Metrics, layout.Body, layout.Caption);
        if (layout.Metrics == MetricsMode.Line)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); line.ColumnDefinitions.Add(new ColumnDefinition());
            SetColumn(_network, 2); line.Children.Add(_memory); line.Children.Add(_network);
            SetRow(line, 2); readings.Children.Add(line);
        }
        else
        {
            _network.Margin = new Thickness(0, layout.Metrics == MetricsMode.Blocks ? 10 : 6, 0, 0);
            SetRow(_memory, 2); SetRow(_network, 3); readings.Children.Add(_memory); readings.Children.Add(_network);
        }

        if (layout.List == ListPlacement.None) Children.Add(readings);
        else
        {
            _list = new ProcessListView(layout, sort, processView, sortChanged, hold, Open);
            if (layout.List == ListPlacement.Side)
            {
                // The list keeps its share of the width within its bounds; the readings keep at least 260 DIP.
                ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 260 });
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MonitorLayout.ListShare / (1 - MonitorLayout.ListShare), GridUnitType.Star),
                    MinWidth = MonitorLayout.ListMinimum, MaxWidth = MonitorLayout.ListMaximum });
                SetColumn(_list, 2);
            }
            else
            {
                RowDefinitions.Add(new RowDefinition { Height = new GridLength(MonitorLayout.StackedReadings) });
                RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
                RowDefinitions.Add(new RowDefinition());
                SetRow(_list, 2);
            }
            Children.Add(readings); Children.Add(_list);
        }
        SetColumnSpan(_overlay, 3); SetRowSpan(_overlay, 3);
        Children.Add(_overlay);
        // A click anywhere outside the card and the list closes it; the click still reaches what it was meant for. On the list the
        // row decides (Open): its own row closes the card, another row opens its own.
        PreviewMouseLeftButtonDown += (_, e) => { if (_card is not null && !_card.IsMouseOver && _list?.IsMouseOver != true) CloseCard(animate: true); };
    }

    public bool CardOpen => _card is not null;

    public void Update(MonitorSnapshot snapshot, bool motion)
    {
        _motion = motion;
        _cores = Math.Max(1, snapshot.LogicalProcessors);
        bool cpuMain = _main == MonitorMain.Cpu;
        if (snapshot.Cpu.TryGet(out var cpu))
        {
            string frequency = cpu.FrequencyMhz.TryGet(out var mhz) ? MonitorFormat.Gigahertz(mhz) + " GHz" : "";
            _cpuGauge.Set(cpu.Usage, MonitorFormat.Percent(cpu.Usage), "%", "",
                "CPU " + MonitorFormat.Percent(cpu.Usage) + "%" + (frequency.Length > 0 ? " · " + frequency : "") + Loc.T($" · {cpu.Cores.Count} 个逻辑处理器", $" · {cpu.Cores.Count} logical processors"), motion);
            int groups = Math.Max(1, cpu.GroupCount);
            _cpuGraph.Set(cpu.Cores.Select(core => new GraphColumn(core.Usage, cpuMain ? core.FrequencyFraction : null, _colors.Core(core.Group, groups), core.Group, Describe(core))).ToList(), motion);
        }
        else { _cpuGauge.Set(null, "—", "", "", Loc.T("暂无读数：", "No reading: ") + snapshot.Cpu.Reason, motion); _cpuGraph.Set([], motion); }

        if (snapshot.Gpu.TryGet(out var gpu))
        {
            string memory = gpu.DedicatedBytes.TryGet(out var used)
                ? Loc.T("显存 ", "VRAM ") + MonitorFormat.Gigabytes(used) + (gpu.DedicatedTotal is { } total ? " / " + MonitorFormat.GigabytesCompact(total) : "") + " GB" : "";
            string tip = (gpu.Name ?? "GPU") + " · " + MonitorFormat.Percent(gpu.Usage) + "%" + (memory.Length > 0 ? " · " + memory : "")
                + (gpu.SharedBytes.TryGet(out var shared) ? Loc.T(" · 共享 ", " · shared ") + MonitorFormat.Gigabytes(shared) + " GB" : "");
            // The adapter's name and its memory are in the tooltip and in the block's line, not spelled out beside the figure.
            _gpuGauge.Set(gpu.Usage, MonitorFormat.Percent(gpu.Usage), "%", "", tip, motion);
            _gpuBlock?.Set(gpu.Usage, gpu.DedicatedFraction, tip, motion);
            _gpuGraph?.Set(gpu.Engines.Select(engine => new GraphColumn(engine.Usage, null, _colors.Gpu, 0,
                GpuEngines.Label(engine.Kind) + " · " + MonitorFormat.Percent(engine.Usage) + "%", GpuEngines.Short(engine.Kind))).ToList(), motion);
        }
        else
        {
            string reason = Loc.T("暂无读数：", "No reading: ") + snapshot.Gpu.Reason;
            _gpuGauge.Set(null, "—", "", "", reason, motion);
            _gpuBlock?.Set(0, null, reason, motion);
            _gpuGraph?.Set([], motion);
        }

        // The main gauge carries the last minute's usage in the room after its figure.
        (cpuMain ? _cpuGauge : _gpuGauge).Trend = cpuMain ? snapshot.CpuHistory : snapshot.GpuHistory;
        _memory.Set(snapshot.Memory, motion);
        _network.Set(snapshot.Network, snapshot.DownHistory, snapshot.UpHistory);
        _list?.Set(snapshot.Processes, _cores, motion);
        _processes = snapshot.Processes.OrDefault;
        // An open card follows its app's readings; it closes when the app has left the list.
        if (_card is not null)
        {
            var sample = _processes?.Top.Concat(_processes.ByMemory).FirstOrDefault(s => s.Id == _card.Id);
            if (sample is null) CloseCard(animate: true); else _card.Show(sample, _cores, _layout.Body, _layout.Caption);
        }
    }

    private static string Describe(CoreSample core)
    {
        string kind = core.Tier switch
        {
            CoreTier.Performance => Loc.T("性能核", "Performance core"), CoreTier.Efficiency => Loc.T("能效核", "Efficiency core"), _ => Loc.T("逻辑处理器", "Logical processor")
        };
        string frequency = core.FrequencyMhz.TryGet(out var mhz) ? " · " + MonitorFormat.Gigahertz(mhz) + " GHz" : "";
        return $"{kind} {core.Number} · {MonitorFormat.Percent(core.Usage)}%{frequency}";
    }

    // Opens an app's card beside its row: to the left of a list at the right, otherwise just right of the row's start.
    private void Open(ProcessSample sample, Rect row)
    {
        if (_list is null) return;
        if (_card?.Id == sample.Id) { CloseCard(animate: true); return; }
        CloseCard(animate: true);
        var card = new ProcessCard(_layout.Body, _layout.Caption);
        card.Show(sample, _cores, _layout.Body, _layout.Caption);
        card.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseCard(animate: true); };
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var origin = _list.TranslatePoint(new Point(row.X, row.Y), this);
        double width = card.DesiredSize.Width, height = card.DesiredSize.Height;
        double x = _layout.List == ListPlacement.Side ? origin.X - width - 12 : Math.Min(origin.X + 36, ActualWidth - width);
        double y = Math.Clamp(origin.Y + row.Height / 2 - height / 2, 0, Math.Max(0, ActualHeight - height));
        Canvas.SetLeft(card, Math.Max(0, x)); Canvas.SetTop(card, y);
        _overlay.Children.Add(card); _overlay.IsHitTestVisible = true;
        _card = card;
        card.Appear(_motion);
    }

    // Asked by the user (or when the app leaves the list) the card fades away; when the page goes, it simply goes.
    public void CloseCard(bool animate = false)
    {
        if (_card is not { } card) return;
        _overlay.IsHitTestVisible = false; _card = null;
        if (animate && _motion) card.Leave(() => _overlay.Children.Remove(card));
        else _overlay.Children.Remove(card);
    }

    public void SetSort(ProcessSort sort) => _list?.SetSort(sort, _motion);
}
