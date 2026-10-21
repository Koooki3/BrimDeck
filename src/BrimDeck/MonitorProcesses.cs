using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BrimDeck.Core;

namespace BrimDeck;

// The busiest apps, drawn as one element: a header with the sort switch, then rows in one or two columns. After ftop, a row
// that changes place by a step or two slides there; one that goes further, or comes new, fades in where it lands. The rows
// keep their order while the pointer is over the list (hold), a pressed row gives a little, and a click opens its card.
// Compact rows show the sorted figure; detailed rows show CPU, memory and how many processes the app has.
internal sealed class ProcessListView : FrameworkElement
{
    private const double ColumnGap = 18;
    private readonly MonitorLayout _layout;
    private readonly ProcessSort _sort;
    private readonly MonitorProcessView _view;
    private readonly Action<ProcessSort> _sortChanged;
    private readonly Action<bool> _hold;
    private readonly Action<ProcessSample, Rect> _open;
    private IReadOnlyList<ProcessSample> _rows = [];
    private int _cores = 1;
    private string _message = "";
    // Per app: its place as a slot number (eased, so a short move slides) and its opacity (a new or far-moved row fades in).
    private readonly Dictionary<string, (Eased Slot, Eased Alpha)> _motion = [];
    private string? _hover, _pressed;
    private int _hoverSort = -1;
    private Rect[] _sortRects = [];

    public ProcessListView(MonitorLayout layout, ProcessSort sort, MonitorProcessView view, Action<ProcessSort> sortChanged, Action<bool> hold, Action<ProcessSample, Rect> open)
    {
        _layout = layout; _sort = sort; _view = view; _sortChanged = sortChanged; _hold = hold; _open = open;
        AutomationProperties.SetName(this, Loc.T("进程列表", "Process list"));
        ClipToBounds = true;
    }

    private (int Rows, int Columns) Grid => MonitorLayout.ListGrid(ActualWidth, ActualHeight, _layout.RowHeight);
    private double ColumnWidth(int columns) => columns <= 1 ? ActualWidth : (ActualWidth - ColumnGap * (columns - 1)) / columns;

    public void Set(Reading<ProcessList> reading, int cores, bool motion)
    {
        _cores = Math.Max(1, cores);
        var (rows, columns) = Grid;
        int capacity = Math.Max(rows * columns, MonitorLayout.MaximumRows * MonitorLayout.MaximumColumns);
        if (!reading.TryGet(out var list)) { _rows = []; _message = Loc.T("正在读取进程…", "Reading processes…"); _motion.Clear(); InvalidateVisual(); return; }
        var ranked = list.Ranked(_sort).Take(capacity).ToList();
        _message = ranked.Count == 0 ? Loc.T("没有进程", "No processes") : "";
        bool moving = false;
        var seen = new HashSet<string>();
        for (int slot = 0; slot < ranked.Count; slot++)
        {
            var id = ranked[slot].Id; seen.Add(id);
            if (_motion.TryGetValue(id, out var state))
            {
                double from = state.Slot.Target;
                // A short move within a column slides; a longer one, or one into another column, turns over in place.
                bool slide = rows > 0 && Math.Abs(slot - from) <= 2 && (int)from / rows == slot / rows;
                if (slide) moving |= state.Slot.Set(slot, motion);
                else if (Math.Abs(slot - from) > 1e-6) { state.Slot = new Eased(slot); state.Alpha = new Eased(0); moving |= state.Alpha.Set(1, motion); }
                _motion[id] = state;
            }
            else
            {
                var fresh = (Slot: new Eased(slot), Alpha: new Eased(_rows.Count == 0 ? 1 : 0));
                moving |= fresh.Alpha.Set(1, motion);
                _motion[id] = fresh;
            }
        }
        foreach (var gone in _motion.Keys.Where(id => !seen.Contains(id)).ToList()) _motion.Remove(gone);
        _rows = ranked;
        if (moving) Motion.Start(this, Step); else InvalidateVisual();
    }
    private bool Step(double now) { InvalidateVisual(); return _motion.Values.Any(m => m.Slot.Moving(now) || m.Alpha.Moving(now)); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    private bool Detailed(double columnWidth) => _view switch { MonitorProcessView.Detailed => columnWidth >= 200, MonitorProcessView.Compact => false, _ => columnWidth >= 300 };

    // A row's box from its slot.
    private Rect RowRect(double slot, int rows, int columns)
    {
        double width = ColumnWidth(columns);
        int column = rows > 0 ? Math.Clamp((int)Math.Floor(slot / rows + 1e-9), 0, columns - 1) : 0;
        double row = slot - column * rows;
        return new Rect(column * (width + ColumnGap), MonitorLayout.ListHead + row * _layout.RowHeight, width, _layout.RowHeight);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, h = ActualHeight, ppd = MonitorDraw.PixelsPerDip(this), now = Eased.Now;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        Header(drawing, w, ppd);
        var (rows, columns) = Grid;
        if (_message.Length > 0 || rows == 0)
        {
            var text = MonitorDraw.Text(_message, _layout.Caption, UI.Tertiary, ppd);
            if (_message.Length > 0) drawing.DrawText(text, new Point((w - text.Width) / 2, MonitorLayout.ListHead + 6));
            return;
        }
        double columnWidth = ColumnWidth(columns);
        bool detailed = Detailed(columnWidth);
        for (int i = 0; i < _rows.Count && i < rows * columns; i++)
        {
            var sample = _rows[i];
            var (slot, alpha) = _motion.TryGetValue(sample.Id, out var m) ? (m.Slot.At(now), m.Alpha.At(now)) : (i, 1d);
            var r = RowRect(slot, rows, columns);
            if (r.Bottom > h + .5) continue;
            if (alpha < .999) drawing.PushOpacity(Math.Clamp(alpha, 0, 1));
            bool pressed = _pressed == sample.Id;
            if (pressed) drawing.PushTransform(new ScaleTransform(.97, .97, r.X + r.Width / 2, r.Y + r.Height / 2));
            Row(drawing, r, sample, detailed, ppd);
            if (pressed) drawing.Pop();
            if (alpha < .999) drawing.Pop();
        }
    }

    private void Header(DrawingContext drawing, double w, double ppd)
    {
        double line = MonitorLayout.ListHead - 6, baseline = Math.Round(line / 2 + _layout.Caption * .38);
        MonitorDraw.Baseline(drawing, MonitorDraw.Text(Loc.T("进程", "Processes"), _layout.Caption, UI.Secondary, ppd, strong: true), 0, baseline);
        // Two pills at the right: CPU and memory, the chosen one filled.
        string[] names = ["CPU", Loc.T("内存", "Memory")];
        var texts = names.Select(name => MonitorDraw.Text(name, _layout.Caption, UI.Secondary, ppd)).ToArray();
        double pad = 8, x = w;
        _sortRects = new Rect[2];
        for (int i = 1; i >= 0; i--)
        {
            double width = texts[i].WidthIncludingTrailingWhitespace + pad * 2;
            x -= width;
            _sortRects[i] = new Rect(x, 1, width, line - 2);
            x -= 2;
        }
        var strip = new Rect(_sortRects[0].X - 1, 0, _sortRects[1].Right - _sortRects[0].X + 2, line);
        drawing.DrawRoundedRectangle(UI.Brush(UI.Fill), null, strip, line / 2, line / 2);
        for (int i = 0; i < 2; i++)
        {
            bool chosen = (int)_sort == i, hover = _hoverSort == i && !chosen;
            if (chosen || hover) drawing.DrawRoundedRectangle(UI.Brush(chosen ? UI.FillSelected : PanelTheme.Hex("hover")), null, _sortRects[i], (line - 2) / 2, (line - 2) / 2);
            var text = MonitorDraw.Text(names[i], _layout.Caption, chosen ? UI.Primary : UI.Secondary, ppd, strong: chosen);
            MonitorDraw.Baseline(drawing, text, _sortRects[i].X + pad, baseline);
        }
    }

    private void Row(DrawingContext drawing, Rect r, ProcessSample sample, bool detailed, double ppd)
    {
        if (_hover == sample.Id) drawing.DrawRoundedRectangle(UI.Brush(PanelTheme.Hex("rowHover")), null, new Rect(r.X - 4, r.Y + 1, r.Width + 8, r.Height - 2), 6, 6);
        double icon = Math.Min(16, r.Height - 5), baseline = Math.Round(r.Y + r.Height / 2 + _layout.Body * .36);
        var iconRect = new Rect(r.X, Math.Round(r.Y + (r.Height - icon) / 2), icon, icon);
        if (ProcessIcons.For(sample.Path) is { } image) drawing.DrawImage(image, iconRect);
        else
        {
            // Without an icon (a process this user cannot open) its first letter stands in, in a faint disc.
            drawing.DrawEllipse(UI.Brush(UI.Fill), null, new Point(iconRect.X + icon / 2, iconRect.Y + icon / 2), icon / 2, icon / 2);
            var letter = MonitorDraw.Text(sample.Name.Length > 0 ? char.ToUpperInvariant(sample.Name[0]).ToString() : "?", Math.Max(8, icon * .55), UI.Secondary, ppd, strong: true);
            drawing.DrawText(letter, new Point(iconRect.X + (icon - letter.Width) / 2, iconRect.Y + (icon - letter.Height) / 2));
        }
        string cpu = MonitorFormat.ProcessShare(sample.CpuPercent, _cores) + "%", memory = MonitorFormat.ProcessMemory(sample.MemoryBytes);
        double right = r.Right;
        void Figure(string text, bool primary, double slot)
        {
            var f = MonitorDraw.Text(text, _layout.Body, primary ? UI.Primary : UI.Tertiary, ppd);
            MonitorDraw.Baseline(drawing, f, right - f.WidthIncludingTrailingWhitespace, baseline);
            right -= slot;
        }
        if (detailed)
        {
            if (sample.Members.Count > 1)
            {
                // How many processes the app has, as a small chip.
                var count = MonitorDraw.Text("×" + sample.Members.Count, _layout.Caption, UI.Secondary, ppd);
                var chip = new Rect(right - count.Width - 8, Math.Round(r.Y + (r.Height - _layout.Caption * 1.5) / 2), count.Width + 8, Math.Round(_layout.Caption * 1.5));
                drawing.DrawRoundedRectangle(UI.Brush(UI.Fill), null, chip, chip.Height / 2, chip.Height / 2);
                drawing.DrawText(count, new Point(chip.X + 4, chip.Y + (chip.Height - count.Height) / 2));
            }
            right -= Math.Ceiling(_layout.Caption * 3.6);
            Figure(memory, _sort == ProcessSort.Memory, Math.Ceiling(_layout.Body * 4.3));
            Figure(cpu, _sort == ProcessSort.Cpu, Math.Ceiling(_layout.Body * 4));
        }
        else Figure(_sort == ProcessSort.Memory ? memory : cpu, true, Math.Ceiling(_layout.Body * 4));
        // The name takes what the figures leave; each figure's slot is wider than its text.
        double nameLeft = iconRect.Right + 7;
        MonitorDraw.Baseline(drawing, MonitorDraw.Trimmed(sample.Name, _layout.Body, UI.Primary, Math.Max(10, right - nameLeft - 6), ppd), nameLeft, baseline);
    }

    private ProcessSample? At(Point point, out Rect box)
    {
        box = Rect.Empty;
        var (rows, columns) = Grid;
        for (int i = 0; i < _rows.Count && i < rows * columns; i++)
        {
            var r = RowRect(i, rows, columns);
            if (r.Contains(point)) { box = r; return _rows[i]; }
        }
        return null;
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); _hold(true); }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e); _hold(false);
        if (_hover is null && _pressed is null && _hoverSort < 0) return;
        _hover = _pressed = null; _hoverSort = -1; Cursor = null; InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        int sort = Array.FindIndex(_sortRects, r => r.Contains(point));
        string? hover = sort < 0 ? At(point, out _)?.Id : null;
        Cursor = sort >= 0 || hover is not null ? Cursors.Hand : null;
        if (hover == _hover && sort == _hoverSort) return;
        _hover = hover; _hoverSort = sort; InvalidateVisual();
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        if (Array.FindIndex(_sortRects, r => r.Contains(point)) is var sort and >= 0) { e.Handled = true; if (sort != (int)_sort) _sortChanged((ProcessSort)sort); return; }
        if (At(point, out _) is { } sample) { _pressed = sample.Id; InvalidateVisual(); e.Handled = true; }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressed is null) return;
        var sample = At(e.GetPosition(this), out var box);
        bool same = sample?.Id == _pressed;
        _pressed = null; InvalidateVisual(); e.Handled = true;
        if (same && sample is not null) _open(sample, box);
    }
}

// An app's card, opened from its row: its icon and name, where it runs from, its CPU and memory, how many processes it has and
// the busiest of them. It grows in from just smaller than itself, follows the readings while open, and closes on a click.
internal sealed class ProcessCard : Border
{
    private readonly TextBlock _name, _where, _cpu, _memory, _count;
    private readonly StackPanel _members;
    private readonly Image _icon;
    public string Id { get; private set; } = "";

    public ProcessCard(double body, double caption)
    {
        Width = 248; Padding = new Thickness(14, 12, 14, 12); CornerRadius = new CornerRadius(12);
        Background = UI.Brush(PanelTheme.Hex("popup")); BorderBrush = UI.Brush(PanelTheme.Hex("popupBorder")); BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand; AutomationProperties.SetName(this, Loc.T("进程详情", "Process details"));
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        _icon = new Image { Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        head.Children.Add(_icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _name = UI.Line("", body + 1, UI.Primary, FontWeights.SemiBold); _name.FontFamily = UI.PanelFont;
        _where = UI.Line("", caption, UI.Tertiary); _where.FontFamily = UI.PanelFont;
        titles.Children.Add(_name); titles.Children.Add(_where); head.Children.Add(titles);
        stack.Children.Add(head);
        var figures = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
        (_cpu, _memory, _count) = (Cell(figures, "CPU", body, caption), Cell(figures, Loc.T("内存", "Memory"), body, caption), Cell(figures, Loc.T("进程", "Processes"), body, caption));
        stack.Children.Add(figures);
        _members = new StackPanel();
        stack.Children.Add(_members);
        Child = stack;
        RenderTransformOrigin = new Point(.5, .5);
        RenderTransform = new ScaleTransform(1, 1);
    }

    private static TextBlock Cell(Panel parent, string caption, double body, double small)
    {
        var cell = new StackPanel();
        var label = UI.Line(caption, small, UI.Tertiary); label.FontFamily = UI.PanelFont;
        var value = UI.Centered("", body + 2, UI.Primary, FontWeights.SemiBold, UI.DisplayFont);
        cell.Children.Add(label); cell.Children.Add(value); parent.Children.Add(cell);
        return value;
    }

    public void Show(ProcessSample sample, int cores, double body, double caption)
    {
        Id = sample.Id;
        _icon.Source = ProcessIcons.For(sample.Path); _icon.Visibility = _icon.Source is null ? Visibility.Collapsed : Visibility.Visible;
        _name.Text = sample.Name;
        _where.Text = sample.Path ?? (sample.AppKey ?? "") + " · PID " + sample.Pid;
        _where.ToolTip = sample.Path;
        _cpu.Text = MonitorFormat.ProcessShare(sample.CpuPercent, cores) + "%";
        _memory.Text = MonitorFormat.ProcessMemory(sample.MemoryBytes);
        _count.Text = Math.Max(1, sample.Members.Count).ToString();
        _members.Children.Clear();
        // The busiest of the app's processes, up to five.
        foreach (var member in sample.Members.Take(5))
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var mem = UI.Line(MonitorFormat.ProcessMemory(member.MemoryBytes), caption, UI.Tertiary); mem.FontFamily = UI.PanelFont; mem.Width = 44; mem.TextAlignment = TextAlignment.Right;
            var cpu = UI.Line(MonitorFormat.ProcessShare(member.CpuPercent, cores) + "%", caption, UI.Secondary); cpu.FontFamily = UI.PanelFont; cpu.Width = 44; cpu.TextAlignment = TextAlignment.Right;
            DockPanel.SetDock(mem, Dock.Right); DockPanel.SetDock(cpu, Dock.Right);
            row.Children.Add(mem); row.Children.Add(cpu);
            var name = UI.Line(member.Name, caption, UI.Secondary); name.FontFamily = UI.PanelFont; row.Children.Add(name);
            _members.Children.Add(row);
        }
    }

    public void Appear(bool motion)
    {
        if (!motion) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
        var scale = (ScaleTransform)RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.92, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.92, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
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
