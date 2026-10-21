using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The system readings in the collapsed notch or capsule, after ftop's strip and corner sizes: the core columns small, then CPU,
// memory and GPU each as a ring gauge with its figure, then download and upload; or, brief, the three rings alone. It asks for
// the width of everything chosen (PreferredWidth) and shows what fits in the width it gets, dropping whole readings
// (CompactMonitor.Fit), so it never cuts through a figure. It draws itself once per reading and has no motion of its own: the
// collapsed row stays still between readings.
internal sealed class MonitorStrip : FrameworkElement
{
    private readonly CompactMonitorItems _items;
    private readonly bool _brief, _centered;
    private readonly MonitorColors _colors;
    private readonly double _scale, _font, _ring, _gap, _row;
    private MonitorSnapshot? _snapshot;

    public MonitorStrip(CompactMonitorMode mode, CompactMonitorItems custom, MonitorColors colors, double scale, bool centered)
    {
        _items = CompactMonitor.Items(mode, custom); _brief = mode == CompactMonitorMode.Brief; _colors = colors; _centered = centered;
        _scale = CompactMusicLayout.SafeScale(scale);
        _font = Math.Max(1, CompactMusicLayout.Half(11.5 * _scale));
        _ring = Math.Max(4, CompactMusicLayout.Whole((_brief ? 14 : 12) * _scale));
        _gap = CompactMusicLayout.Whole((_brief ? 7 : 10) * _scale);
        _row = Math.Max(1, CompactMusicLayout.Whole(24 * _scale));
        Height = _row;
        IsHitTestVisible = false;
    }

    // Fixed per item, from the widest figure each can show, so the row's layout never moves with the readings.
    private double ItemWidth(CompactMonitorItems item)
    {
        double text(string sample) => MonitorDraw.Text(sample, _font, UI.Primary, 1).WidthIncludingTrailingWhitespace;
        return item switch
        {
            CompactMonitorItems.Columns => _brief ? 0 : ColumnsWidth(Environment.ProcessorCount),
            CompactMonitorItems.Cpu or CompactMonitorItems.Memory or CompactMonitorItems.Gpu => _brief ? _ring : _ring + Math.Round(4 * _scale) + Math.Ceiling(text("100%")),
            CompactMonitorItems.Download or CompactMonitorItems.Upload => _brief ? 0 : Math.Round(9 * _scale) + Math.Round(3 * _scale) + Math.Ceiling(text("999.9M")),
            _ => 0
        };
    }
    private double ColumnsWidth(int cores) => Math.Min(Math.Round(56 * _scale), cores * Math.Max(1.5, 2 * _scale) + (cores - 1) * Math.Max(.5, _scale));

    // Brief shows only the rings; columns and rates are not part of it.
    private CompactMonitorItems Shown => _brief ? _items & (CompactMonitorItems.Cpu | CompactMonitorItems.Memory | CompactMonitorItems.Gpu) : _items;
    public double PreferredWidth => CompactMonitor.Width(Shown, ItemWidth, _gap);

    public void Update(MonitorSnapshot? snapshot) { _snapshot = snapshot; InvalidateVisual(); }

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), center = _row / 2;
        var shown = CompactMonitor.Fit(Shown, ItemWidth, _gap, w);
        if (shown == CompactMonitorItems.None) return;
        double total = CompactMonitor.Width(shown, ItemWidth, _gap);
        double x = _centered ? Math.Round((w - total) / 2) : Math.Round(w - total);
        var s = _snapshot;
        foreach (var item in CompactMonitor.Order)
        {
            if ((shown & item) == 0) continue;
            double width = ItemWidth(item);
            switch (item)
            {
                case CompactMonitorItems.Columns: Columns(drawing, x, width, center, s?.Cpu.OrDefault); break;
                case CompactMonitorItems.Cpu: Gauge(drawing, x, center, s?.Cpu.OrDefault?.Usage, _colors.Performance, MonitorGlyph.Cpu, ppd); break;
                case CompactMonitorItems.Memory:
                    var memory = s?.Memory.OrDefault;
                    Gauge(drawing, x, center, memory?.UsedFraction, memory is null ? UI.Tertiary : _colors.Memory(memory.Level), MonitorGlyph.Memory, ppd); break;
                case CompactMonitorItems.Gpu: Gauge(drawing, x, center, s?.Gpu.OrDefault?.Usage, _colors.Gpu, MonitorGlyph.Gpu, ppd); break;
                case CompactMonitorItems.Download: Rate(drawing, x, center, MonitorGlyph.Down, s?.Network.OrDefault?.DownBytesPerSecond, _colors.Performance, ppd); break;
                case CompactMonitorItems.Upload: Rate(drawing, x, center, MonitorGlyph.Up, s?.Network.OrDefault?.UpBytesPerSecond, _colors.Efficiency, ppd); break;
            }
            x += width + _gap;
        }
    }

    // The cores as thin columns, fastest kind first, in their colors; their tops show the load at a glance.
    private void Columns(DrawingContext drawing, double x, double width, double center, CpuSample? cpu)
    {
        double height = Math.Round(14 * _scale), top = Math.Round(center - height / 2);
        if (cpu is null || cpu.Cores.Count == 0) { drawing.DrawRoundedRectangle(UI.Brush(UI.Fill), null, new Rect(x, top, width, height), 2, 2); return; }
        int count = cpu.Cores.Count, groups = Math.Max(1, cpu.GroupCount);
        double gap = Math.Max(.5, _scale), column = Math.Max(1, (width - (count - 1) * gap) / count);
        for (int i = 0; i < count; i++)
        {
            var core = cpu.Cores[i];
            double cx = x + i * (column + gap), fill = Math.Max(1.5, core.Usage * height);
            drawing.DrawRectangle(UI.Brush(UI.Fill), null, new Rect(cx, top, column, height));
            drawing.DrawRectangle(UI.Brush(_colors.Core(core.Group, groups)), null, new Rect(cx, top + height - fill, column, fill));
        }
    }

    // A ring whose arc is the share, and, unless brief, the figure after it. Brief rings carry their glyph inside, as ftop's
    // corner badges do, so they are not taken for the quota rings beside them.
    private void Gauge(DrawingContext drawing, double x, double center, double? share, string color, MonitorGlyph glyph, double ppd)
    {
        double ring = Math.Max(1.2, _ring * .16), radius = _ring / 2 - ring / 2;
        var c = new Point(x + _ring / 2, center);
        var track = new Pen(UI.Brush(UI.Track), ring); track.Freeze();
        drawing.DrawEllipse(null, track, c, radius, radius);
        if (share is { } value) MonitorDraw.Arc(drawing, c, radius, ring, value, color);
        if (_brief)
        {
            double size = _ring * .5, scale = size / 16;
            var pen = new Pen(UI.Brush(UI.Secondary), Math.Max(.9, _scale) / scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, c.X - size / 2, c.Y - size / 2));
            drawing.DrawGeometry(null, pen, MonitorDraw.Glyph(glyph));
            drawing.Pop();
            return;
        }
        string figure = share is { } v ? MonitorFormat.Percent(v) + "%" : "—";
        var text = MonitorDraw.Text(figure, _font, UI.Primary, ppd);
        MonitorDraw.Baseline(drawing, text, x + _ring + Math.Round(4 * _scale), Math.Round(center + _font * .36));
    }

    // An arrow and the rate in at most four characters and a letter: "84K", "1.2M".
    private void Rate(DrawingContext drawing, double x, double center, MonitorGlyph glyph, double? rate, string color, double ppd)
    {
        double size = Math.Round(9 * _scale), scale = size / 16;
        var pen = new Pen(UI.Brush(color), Math.Max(1, 1.4 * _scale) / scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, center - size / 2));
        drawing.DrawGeometry(null, pen, MonitorDraw.Glyph(glyph));
        drawing.Pop();
        var q = rate is { } r ? MonitorFormat.CompactRate(r) : new MonitorFormat.Quantity("—", "");
        var text = MonitorDraw.Text(q.Number + q.Unit, _font, UI.Primary, ppd);
        MonitorDraw.Baseline(drawing, text, x + size + Math.Round(3 * _scale), Math.Round(center + _font * .36));
    }
}
