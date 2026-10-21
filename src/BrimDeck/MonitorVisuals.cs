using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The system monitor page's content: the processor headline, one column per logical processor with the GPU's column
// beside them, memory, network and GPU figures, and the busiest apps. The page's look follows ftop by Nongfsq
// (https://github.com/Nongfsq/ftop, MIT License; see ThirdPartyNotices.txt): columns that ease to each reading with a
// frequency tick, colors by palette, a list that rests, and a dash with its reason for a reading the system does not give.
internal sealed class MonitorView : Grid
{
    private readonly MonitorLayout _layout;
    private readonly MonitorColors _colors;
    private readonly ProcessSort _sort;
    private readonly TextBlock _cpuFigure, _cpuUnit, _cpuFrequency;
    private readonly CoreColumns _columns;
    private readonly TextBlock _memory, _network, _gpu;
    private readonly Grid? _memoryBar;
    private readonly Border? _memoryFill;
    private readonly List<ProcessRow> _rows = [];
    private readonly TextBlock? _listMessage;

    public MonitorView(MonitorLayout layout, MonitorColors colors, ProcessSort sort, Action<ProcessSort> sortChanged, Action<bool> hold)
    {
        _layout = layout; _colors = colors; _sort = sort;
        var readings = new Grid();
        readings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        readings.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 14 });
        readings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // The headline: "CPU 37 % 3.21 GHz", the figure set larger than its caption and unit.
        var headline = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        var caption = UI.Centered("CPU", layout.Caption, UI.Secondary, FontWeights.SemiBold); caption.VerticalAlignment = VerticalAlignment.Bottom;
        caption.Margin = new Thickness(0, 0, 8, Math.Round(layout.Figure * .14));
        _cpuFigure = UI.Centered("—", layout.Figure, UI.Primary, FontWeights.SemiBold, UI.DisplayFont); _cpuFigure.VerticalAlignment = VerticalAlignment.Bottom;
        _cpuUnit = UI.Centered("%", layout.Body, UI.Secondary); _cpuUnit.VerticalAlignment = VerticalAlignment.Bottom; _cpuUnit.Margin = new Thickness(2, 0, 0, Math.Round(layout.Figure * .14));
        _cpuFrequency = UI.Centered("", layout.Caption, UI.Tertiary); _cpuFrequency.VerticalAlignment = VerticalAlignment.Bottom; _cpuFrequency.Margin = new Thickness(10, 0, 0, Math.Round(layout.Figure * .14));
        headline.Children.Add(caption); headline.Children.Add(_cpuFigure); headline.Children.Add(_cpuUnit); headline.Children.Add(_cpuFrequency);
        readings.Children.Add(headline);

        _columns = new CoreColumns(colors) { Margin = new Thickness(0, layout.CompactMetrics ? 6 : 8, 0, layout.CompactMetrics ? 6 : 10) };
        SetRow(_columns, 1); readings.Children.Add(_columns);

        if (layout.CompactMetrics)
        {
            // Memory, network and GPU share one line under small columns.
            var line = new StackPanel { Orientation = Orientation.Horizontal, ClipToBounds = true };
            _memory = Figure(layout.Caption); _network = Figure(layout.Caption); _gpu = Figure(layout.Caption);
            foreach (var block in new[] { _memory, _network, _gpu }) { block.Margin = new Thickness(0, 0, 14, 0); line.Children.Add(block); }
            SetRow(line, 2); readings.Children.Add(line);
        }
        else
        {
            var metrics = new Grid();
            double captionWidth = Math.Ceiling(layout.Caption * (Loc.IsEnglish ? 5.2 : 2.8));
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(captionWidth) });
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            double rowHeight = Math.Ceiling(layout.Body * 1.75);
            string[] captions = [Loc.T("内存", "Memory"), Loc.T("网络", "Network"), "GPU"];
            var values = new TextBlock[3];
            for (int i = 0; i < 3; i++)
            {
                metrics.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowHeight) });
                var label = UI.Centered(captions[i], layout.Caption, UI.Tertiary); SetRow(label, i); metrics.Children.Add(label);
                values[i] = Figure(layout.Body); SetRow(values[i], i); SetColumn(values[i], 1); metrics.Children.Add(values[i]);
            }
            (_memory, _network, _gpu) = (values[0], values[1], values[2]);
            // Memory in use as a bar from after the widest figure to the column's edge, colored by how tight memory is.
            // Its two star columns split the track at the share in use, so it needs no measuring.
            _memoryBar = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            _memoryBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
            _memoryBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var track = new Border { CornerRadius = new CornerRadius(2), Background = UI.Brush(UI.Track) };
            SetColumnSpan(track, 2); _memoryBar.Children.Add(track);
            _memoryFill = new Border { CornerRadius = new CornerRadius(2) };
            _memoryBar.Children.Add(_memoryFill);
            SetColumn(_memoryBar, 2); metrics.Children.Add(_memoryBar);
            SetRow(metrics, 2); readings.Children.Add(metrics);
        }

        if (!layout.Processes) { Children.Add(readings); return; }
        var list = new Grid { Background = Brushes.Transparent };
        list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(MonitorLayout.ListHead) });
        for (int i = 0; i < layout.Rows; i++) list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.RowHeight) });
        var head = new DockPanel { LastChildFill = false, VerticalAlignment = VerticalAlignment.Top };
        var title = UI.Centered(Loc.T("进程", "Processes"), layout.Caption, UI.Secondary, FontWeights.SemiBold);
        head.Children.Add(title);
        string memoryName = Loc.T("内存", "Memory");
        var sorter = MainWindow.Segmented([new("CPU", Loc.T("按 CPU 排序", "Sort by CPU"), sort == ProcessSort.Cpu, () => sortChanged(ProcessSort.Cpu)),
            new(memoryName, Loc.T("按内存排序", "Sort by memory"), sort == ProcessSort.Memory, () => sortChanged(ProcessSort.Memory))], layout.Caption, 18, 8, centerText: true);
        DockPanel.SetDock(sorter, Dock.Right); head.Children.Add(sorter);
        list.Children.Add(head);
        for (int i = 0; i < layout.Rows; i++)
        {
            var row = new ProcessRow(layout, sort); SetRow(row, i + 1); list.Children.Add(row); _rows.Add(row);
        }
        _listMessage = UI.Centered("", layout.Caption, UI.Tertiary); _listMessage.HorizontalAlignment = HorizontalAlignment.Center;
        SetRow(_listMessage, 1); SetRowSpan(_listMessage, Math.Max(1, layout.Rows)); list.Children.Add(_listMessage);
        // While the pointer is over the list, rows already listed keep their places.
        list.MouseEnter += (_, _) => hold(true); list.MouseLeave += (_, _) => hold(false);

        if (layout.SideList)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.ListWidth) });
            Children.Add(readings); SetColumn(list, 2); Children.Add(list);
        }
        else
        {
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Children.Add(readings); SetRow(list, 2); Children.Add(list);
        }
    }

    private static TextBlock Figure(double size)
    {
        var block = UI.Centered("—", size, UI.Primary); block.TextTrimming = TextTrimming.CharacterEllipsis; return block;
    }

    public void Update(MonitorSnapshot snapshot, bool motion)
    {
        int cores = Math.Max(1, snapshot.LogicalProcessors);
        if (snapshot.Cpu.TryGet(out var cpu))
        {
            _cpuFigure.Text = MonitorFormat.Percent(cpu.Usage); _cpuUnit.Visibility = Visibility.Visible;
            _cpuFrequency.Text = cpu.FrequencyMhz.TryGet(out var mhz) ? MonitorFormat.Gigahertz(mhz) + " GHz" : "";
            _cpuFigure.ToolTip = null;
        }
        else { _cpuFigure.Text = "—"; _cpuUnit.Visibility = Visibility.Collapsed; _cpuFrequency.Text = ""; _cpuFigure.ToolTip = Unavailable(snapshot.Cpu.Reason); }
        _columns.Set(cpu?.Cores ?? [], cpu?.GroupCount ?? 1, snapshot.Gpu.TryGet(out var gpu) ? gpu : null, motion);

        if (snapshot.Memory.TryGet(out var memory))
        {
            string commit = Loc.T("已提交 ", "Committed ") + MonitorFormat.Gigabytes(memory.CommitUsed) + " / " + MonitorFormat.Gigabytes(memory.CommitLimit) + " GB";
            if (_layout.CompactMetrics) Show(_memory, (Loc.T("内存 ", "Mem ") + MonitorFormat.Percent(memory.UsedFraction), "%"));
            else Show(_memory, (MonitorFormat.Gigabytes(memory.Used) + " / " + MonitorFormat.GigabytesCompact(memory.Total), "GB"));
            _memory.ToolTip = Loc.T("物理内存 ", "Physical memory ") + MonitorFormat.Percent(memory.UsedFraction) + "% · " + commit;
            if (_memoryFill is not null && _memoryBar is not null)
            {
                SetMemoryShare(memory.UsedFraction);
                _memoryFill.Background = UI.Brush(_colors.Memory(memory.Level));
                _memoryBar.ToolTip = _memory.ToolTip;
            }
        }
        else { Dash(_memory, snapshot.Memory.Reason); SetMemoryShare(0); }

        if (snapshot.Network.TryGet(out var network))
        {
            var (down, up) = (MonitorFormat.Rate(network.DownBytesPerSecond), MonitorFormat.Rate(network.UpBytesPerSecond));
            Show(_network, ("↓ " + down.Number, down.Unit), ("↑ " + up.Number, up.Unit));
            _network.ToolTip = Loc.T("下载 ", "Download ") + down.Text + " · " + Loc.T("上传 ", "Upload ") + up.Text;
        }
        else Dash(_network, snapshot.Network.Reason);

        if (gpu is not null)
        {
            var usage = ((_layout.CompactMetrics ? "GPU " : "") + MonitorFormat.Percent(gpu.Usage), "%");
            if (!_layout.CompactMetrics && gpu.DedicatedBytes.TryGet(out var dedicated)) Show(_gpu, usage, (Loc.T("显存 ", "VRAM ") + MonitorFormat.Gigabytes(dedicated), "GB"));
            else Show(_gpu, usage);
            _gpu.ToolTip = "GPU " + MonitorFormat.Percent(gpu.Usage) + "%" + (gpu.DedicatedBytes.TryGet(out var bytes) ? Loc.T(" · 专用显存 ", " · dedicated memory ") + MonitorFormat.Gigabytes(bytes) + " GB" : "");
        }
        else Dash(_gpu, snapshot.Gpu.Reason, _layout.CompactMetrics ? "GPU —" : "—");

        if (_rows.Count == 0) return;
        if (snapshot.Processes.TryGet(out var processes))
        {
            var ranked = processes.Ranked(_sort);
            for (int i = 0; i < _rows.Count; i++) _rows[i].Show(i < ranked.Count ? ranked[i] : null, cores, _colors);
            _listMessage!.Text = ranked.Count == 0 ? Loc.T("没有进程", "No processes") : "";
        }
        else
        {
            foreach (var row in _rows) row.Show(null, cores, _colors);
            _listMessage!.Text = Loc.T("正在读取进程…", "Reading processes…");
        }
    }

    // In whole percent, so a reading that moves by a fraction does not lay the panel out again.
    private void SetMemoryShare(double share)
    {
        if (_memoryBar is null) return;
        double used = Math.Round(Math.Clamp(share, 0, 1) * 100);
        if (_memoryBar.ColumnDefinitions[0].Width.Value == used) return;
        _memoryBar.ColumnDefinitions[0].Width = new GridLength(used, GridUnitType.Star);
        _memoryBar.ColumnDefinitions[1].Width = new GridLength(100 - used, GridUnitType.Star);
    }

    private static string Unavailable(string reason) => Loc.T("暂无读数：", "No reading: ") + reason;
    private static void Dash(TextBlock block, string reason, string text = "—")
    {
        if (!Equals(block.Tag, text)) { block.Inlines.Clear(); block.Text = text; block.Tag = text; }
        block.ToolTip = Unavailable(reason);
    }
    // Numbers in the block's own size and color, each unit smaller and lighter, as ftop sets them. The text is rebuilt only
    // when it changes: new runs lay out and redraw the whole layered panel even when they read the same.
    private static void Show(TextBlock block, params (string Number, string Unit)[] parts)
    {
        string key = string.Join("\n", parts.Select(part => part.Number + "\t" + part.Unit));
        if (Equals(block.Tag, key)) return;
        block.Tag = key; block.Inlines.Clear();
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) block.Inlines.Add(new Run("   "));
            block.Inlines.Add(new Run(parts[i].Number));
            block.Inlines.Add(new Run((parts[i].Unit == "%" ? "" : " ") + parts[i].Unit) { FontSize = Math.Max(8, block.FontSize - 1.5), Foreground = UI.Brush(UI.Secondary) });
        }
    }
}

// One row of the process list: the app's icon, its name, and its CPU and memory, the sorted figure brighter.
internal sealed class ProcessRow : Grid
{
    private readonly Image _icon;
    private readonly Border _initial;
    private readonly TextBlock _letter, _name, _cpu, _memory;
    private string? _path;

    public ProcessRow(MonitorLayout layout, ProcessSort sort)
    {
        Background = Brushes.Transparent;
        double icon = Math.Min(16, layout.RowHeight - 4);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(icon + 7) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Ceiling(layout.Body * 3.4)) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Ceiling(layout.Body * 4)) });
        _icon = new Image { Width = icon, Height = icon, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        // Without an icon (a process this user cannot open) its first letter stands in, in a faint disc.
        _letter = UI.Centered("", Math.Max(8, icon * .55), UI.Secondary, FontWeights.SemiBold); _letter.HorizontalAlignment = HorizontalAlignment.Center;
        _initial = new Border { Width = icon, Height = icon, CornerRadius = new CornerRadius(icon / 2), Background = UI.Brush(UI.Fill), Child = _letter,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        _name = UI.Centered("", layout.Body, UI.Primary); _name.TextTrimming = TextTrimming.CharacterEllipsis; _name.Margin = new Thickness(0, 0, 8, 0);
        _cpu = UI.Centered("", layout.Body, sort == ProcessSort.Cpu ? UI.Primary : UI.Tertiary); _cpu.HorizontalAlignment = HorizontalAlignment.Right;
        _memory = UI.Centered("", layout.Body, sort == ProcessSort.Memory ? UI.Primary : UI.Tertiary); _memory.HorizontalAlignment = HorizontalAlignment.Right;
        Children.Add(_initial); Children.Add(_icon);
        SetColumn(_name, 1); Children.Add(_name);
        SetColumn(_cpu, 2); Children.Add(_cpu);
        SetColumn(_memory, 3); Children.Add(_memory);
    }

    public void Show(ProcessSample? sample, int cores, MonitorColors colors)
    {
        if (sample is null) { Visibility = Visibility.Hidden; return; }
        Visibility = Visibility.Visible;
        _name.Text = sample.Name;
        _cpu.Text = MonitorFormat.ProcessShare(sample.CpuPercent, cores) + "%";
        _memory.Text = MonitorFormat.ProcessMemory(sample.MemoryBytes);
        if (_path != sample.Path)
        {
            _path = sample.Path;
            _icon.Source = ProcessIcons.For(sample.Path);
            _icon.Visibility = _icon.Source is null ? Visibility.Collapsed : Visibility.Visible;
            _initial.Visibility = _icon.Source is null ? Visibility.Visible : Visibility.Collapsed;
        }
        _letter.Text = sample.Name.Length > 0 ? char.ToUpperInvariant(sample.Name[0]).ToString() : "";
        ToolTip = sample.Members.Count > 1
            ? sample.Name + Loc.T($" · {sample.Members.Count} 个进程", $" · {sample.Members.Count} processes")
            : sample.Name + " · PID " + sample.Pid;
    }
}

// The icons of executables, read once per path on the sampler's thread (Prepare) and taken from the cache on the UI thread
// (For), which never waits for the shell. A failed read is remembered as no icon.
internal static class ProcessIcons
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string? path) => !string.IsNullOrEmpty(path) && Cache.TryGetValue(path, out var icon) ? icon : null;

    public static void Prepare(string? path)
    {
        if (string.IsNullOrEmpty(path) || Cache.ContainsKey(path)) return;
        ImageSource? icon = null;
        var info = new ShellFileInfo();
        // The 32 px icon is drawn at 16 DIP, so it stays sharp on scaled displays.
        if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100) != IntPtr.Zero && info.Icon != IntPtr.Zero)
        {
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze(); icon = source;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException) { }
            finally { DestroyIcon(info.Icon); }
        }
        // Programs come and go over days; the cache is bounded rather than tracked.
        if (Cache.Count >= 512) Cache.Clear();
        Cache[path] = icon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}

// One column per logical processor, fastest kind of core first, with a gap between kinds and the GPU's column after them.
// The fill is usage; the tick is the core's current frequency within the highest it has reached. Each column eases to a new
// reading over most of the interval, so the row moves on smoothly instead of jumping once a reading.
internal sealed class CoreColumns : FrameworkElement
{
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(CoreColumns),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    private const double CoreGap = 3, GroupGap = 7, GpuGap = 12, MaxColumn = 26, Rest = 3, MoveMilliseconds = 240;
    private const string TickColor = "#B3F5F5F7", TrackColor = "#14FFFFFF";
    private readonly MonitorColors _colors;
    private double[] _from = [], _to = [], _tickFrom = [], _tickTo = [];
    private IReadOnlyList<CoreSample> _cores = [];
    private GpuSample? _gpu;
    private int _groups = 1;
    private readonly ToolTip _tip = new();
    private int _tipIndex = -1;

    public CoreColumns(MonitorColors colors)
    {
        _colors = colors;
        ToolTip = _tip;
        ToolTipService.SetPlacement(this, System.Windows.Controls.Primitives.PlacementMode.Mouse);
        // Hundreds of logical processors leave columns of the minimum width, which may run past the area; they are cut there.
        SnapsToDevicePixels = true; ClipToBounds = true;
    }

    public void Set(IReadOnlyList<CoreSample> cores, int groups, GpuSample? gpu, bool motion)
    {
        int count = cores.Count + (gpu is null ? 0 : 1);
        var to = new double[count]; var tickTo = new double[count];
        for (int i = 0; i < cores.Count; i++) { to[i] = cores[i].Usage; tickTo[i] = cores[i].FrequencyFraction ?? double.NaN; }
        if (gpu is not null) { to[^1] = gpu.Usage; tickTo[^1] = double.NaN; }
        bool sameShape = count == _to.Length && (gpu is null) == (_gpu is null);
        _cores = cores; _gpu = gpu; _groups = Math.Max(1, groups);
        if (sameShape && motion)
        {
            // Start from where the columns are now, so a reading that lands mid-move continues it.
            double p = Progress;
            _from = _from.Select((value, i) => value + (_to[i] - value) * p).ToArray();
            _tickFrom = _tickFrom.Select((value, i) => double.IsNaN(value) || double.IsNaN(_tickTo[i]) ? _tickTo[i] : value + (_tickTo[i] - value) * p).ToArray();
            _to = to; _tickTo = tickTo;
            // ftop eases over the whole interval; here every frame redraws the whole layered panel in software, so the move
            // is short and at half the display rate, and the panel rests between readings.
            var move = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(MoveMilliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Timeline.SetDesiredFrameRate(move, 30);
            BeginAnimation(ProgressProperty, move, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            BeginAnimation(ProgressProperty, null);
            _from = to; _to = to; _tickFrom = tickTo; _tickTo = tickTo; Progress = 1;
            InvalidateVisual();
        }
        if (_tipIndex >= 0 && _tip.IsOpen) _tip.Content = Describe(_tipIndex);
    }

    // The x of each column and the shared column width.
    private (double[] X, double Width) Geometry(double width)
    {
        int count = _to.Length;
        if (count == 0) return ([], 0);
        double gaps = 0;
        for (int i = 1; i < count; i++) gaps += Gap(i);
        double column = Math.Clamp((width - gaps) / count, 1, MaxColumn);
        var x = new double[count];
        for (int i = 1; i < count; i++) x[i] = x[i - 1] + column + Gap(i);
        return (x, column);
    }
    private double Gap(int index)
    {
        if (_gpu is not null && index == _to.Length - 1) return GpuGap;
        return index < _cores.Count && _cores[index].Group != _cores[index - 1].Group ? GroupGap : _to.Length > 48 ? 1.5 : CoreGap;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double height = ActualHeight;
        var (xs, column) = Geometry(ActualWidth);
        if (xs.Length == 0 || height <= 0) return;
        double p = Progress, radius = Math.Min(column / 2, 4);
        var track = UI.Brush(TrackColor);
        var tick = new Pen(UI.Brush(TickColor), Math.Max(1, Math.Min(1.5, column / 6)));
        tick.Freeze();
        for (int i = 0; i < xs.Length; i++)
        {
            double value = Math.Clamp(_from[i] + (_to[i] - _from[i]) * p, 0, 1);
            drawing.DrawRoundedRectangle(track, null, new Rect(xs[i], 0, column, height), radius, radius);
            double fill = Math.Max(Rest, value * height);
            string color = i < _cores.Count ? _colors.Core(_cores[i].Group, _groups) : _colors.Gpu;
            drawing.DrawRoundedRectangle(UI.Brush(color), null, new Rect(xs[i], height - fill, column, fill), radius, radius);
            double frequency = double.IsNaN(_tickFrom[i]) || double.IsNaN(_tickTo[i]) ? _tickTo[i] : _tickFrom[i] + (_tickTo[i] - _tickFrom[i]) * p;
            if (column >= 6 && !double.IsNaN(frequency))
            {
                double y = Math.Round((1 - Math.Clamp(frequency, 0, 1)) * (height - 2)) + 1, inset = column * .2;
                drawing.DrawLine(tick, new Point(xs[i] + inset, y), new Point(xs[i] + column - inset, y));
            }
        }
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (xs, column) = Geometry(ActualWidth);
        double x = e.GetPosition(this).X;
        int index = Array.FindLastIndex(xs, start => x >= start - CoreGap / 2);
        if (index >= 0 && x > xs[index] + column + GpuGap / 2) index = -1;
        if (index == _tipIndex) return;
        _tipIndex = index;
        _tip.Content = index < 0 ? null : Describe(index);
        ToolTip = index < 0 ? null : _tip;
    }

    private string Describe(int index)
    {
        if (index >= _cores.Count)
            return _gpu is null ? "" : "GPU · " + MonitorFormat.Percent(_gpu.Usage) + "%" +
                (_gpu.DedicatedBytes.TryGet(out var bytes) ? Loc.T(" · 专用显存 ", " · dedicated memory ") + MonitorFormat.Gigabytes(bytes) + " GB" : "");
        var core = _cores[index];
        string kind = core.Tier switch
        {
            CoreTier.Performance => Loc.T("性能核", "Performance core"), CoreTier.Efficiency => Loc.T("能效核", "Efficiency core"), _ => Loc.T("逻辑处理器", "Logical processor")
        };
        string frequency = core.FrequencyMhz.TryGet(out var mhz) ? " · " + MonitorFormat.Gigahertz(mhz) + " GHz" : "";
        return $"{kind} {core.Number} · {MonitorFormat.Percent(core.Usage)}%{frequency}";
    }
}
