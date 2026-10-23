using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The system readings in the collapsed notch or capsule, after ftop's strip and corner sizes: the core columns small, CPU,
// memory and GPU each as a ring gauge with its figure, or alone with its glyph inside (narrower), and download and upload, in
// the order and the colors set in the settings' table (CompactReading); the rings alone sit closer together. It asks for
// the width of everything chosen (PreferredWidth) and shows what fits in the width it gets, dropping whole readings
// (CompactMonitor.Fit), so it never cuts through a figure. It draws itself once per reading; the collapsed row stays still
// between readings. When the row narrows or widens (a drag on its end, the lyric beside it changing), a reading that no longer
// fits fades and shrinks out of its place while the others slide together, and one that fits again comes back the same way.
// The panel keeps those transitions (StripPresence) across rebuilds of the row, which happen on every change of its width.
internal sealed class MonitorStrip : FrameworkElement
{
    private readonly StripPresence _presence;
    private readonly CompactMonitorItems _shown, _values;
    private readonly IReadOnlyList<CompactMonitorItems> _order;
    private readonly bool _centered;
    private readonly StripColors _colors;
    private readonly double _scale, _font, _ring, _bare, _gap, _row, _dpi;
    private readonly Func<CompactMonitorItems, double> _width;
    private MonitorSnapshot? _snapshot;

    public MonitorStrip(IReadOnlyList<CompactReading> readings, StripColors colors, double scale, bool centered, StripPresence presence, double dpi)
    {
        _presence = presence; _dpi = dpi; _colors = colors; _centered = centered;
        _order = [.. readings.Select(reading => reading.Item)];
        _shown = CompactMonitor.Shown(readings); _values = CompactMonitor.Values(readings);
        _scale = CompactMusicLayout.SafeScale(scale);
        _font = Math.Max(1, CompactMusicLayout.Half(11.5 * _scale));
        // A ring with its figure is smaller than one alone, which carries its glyph.
        _ring = Math.Max(4, CompactMusicLayout.Whole(12 * _scale));
        _bare = Math.Max(4, CompactMusicLayout.Whole(14 * _scale));
        _gap = CompactMusicLayout.Whole((CompactMonitor.RingsOnly(_shown, _values) ? 7 : 10) * _scale);
        _row = Math.Max(1, CompactMusicLayout.Whole(24 * _scale));
        Height = _row;
        IsHitTestVisible = false;
        // A reading fading out of a narrow row stays within it, off the lyric or the rings beside it.
        ClipToBounds = true;
        // Fixed per item, from the widest figure each can show, so the row's layout never moves with the readings. A rate has
        // at most five characters (MonitorFormat.CompactRate): "99.9M" or "99.9G" at the widest.
        double text(string sample) => MonitorDraw.Text(sample, _font, UI.Primary, 1).WidthIncludingTrailingWhitespace;
        _width = CompactMonitor.Widths(_values, _ring, Math.Round(4 * _scale) + Math.Ceiling(text("100%")), _bare, ColumnsWidth(Environment.ProcessorCount),
            Math.Round(9 * _scale) + Math.Round(3 * _scale) + Math.Ceiling(Math.Max(text("99.9M"), text("99.9G"))));
    }

    private double ItemWidth(CompactMonitorItems item) => _width(item);
    private double ColumnsWidth(int cores) => Math.Min(Math.Round(56 * _scale), cores * Math.Max(1.5, 2 * _scale) + (cores - 1) * Math.Max(.5, _scale));

    private CompactMonitorItems Shown => _shown;
    // Rounded up to whole device pixels: the row rounds the region it gives down to them, which must not drop a reading.
    public double PreferredWidth => DevicePixels.Up(CompactMonitor.Width(Shown, ItemWidth, _gap), _dpi);
    // The width of the readings that fit in the given width, measured as ArrangeOverride will measure the region it gets.
    public double FittedWidth(double available)
    {
        double room = DevicePixels.Down(available, _dpi);
        return Math.Min(room, DevicePixels.Up(CompactMonitor.Width(CompactMonitor.Fit(Shown, ItemWidth, _gap, room), ItemWidth, _gap), _dpi));
    }

    public void Update(MonitorSnapshot? snapshot) { _snapshot = snapshot; InvalidateVisual(); }

    // The readings that fit the width the row gives; a change starts their transitions. A row built again in the middle of one
    // carries it on, as the old row no longer draws.
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_presence.Set(Shown, CompactMonitor.Fit(Shown, ItemWidth, _gap, finalSize.Width)) | _presence.Moving(Eased.Now)) Motion.Start(this, Step);
        return finalSize;
    }
    private bool Step(double now) { InvalidateVisual(); return _presence.Moving(now); }

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), center = _row / 2, now = Eased.Now;
        // Each reading takes its width and the gap after it in proportion to how present it is, so the row closes up smoothly.
        var (items, total) = CompactMonitor.Arrange(_order, Shown, ItemWidth, _gap, item => _presence.At(item, now));
        if (total <= 0) return;
        double start = _centered ? (w - total) / 2 : w - total;
        var s = _snapshot;
        foreach (var (item, at, width, presence) in items)
        {
            double x = start + at;
            // A reading on its way in or out is drawn smaller and fainter, centred in the room it has.
            bool partial = presence < .999;
            double offset = (width * presence - width) / 2;
            if (partial)
            {
                double zoom = .82 + .18 * presence;
                drawing.PushOpacity(presence * presence);
                drawing.PushTransform(new ScaleTransform(zoom, zoom, x + width * presence / 2, center));
                drawing.PushTransform(new TranslateTransform(offset, 0));
            }
            double left = Math.Round(x);
            switch (item)
            {
                case CompactMonitorItems.Columns: Columns(drawing, left, width, center, s?.Cpu.OrDefault); break;
                case CompactMonitorItems.Cpu: Gauge(drawing, left, center, s?.Cpu.OrDefault?.Usage, _colors.Normal(item), MonitorGlyph.Cpu, (_values & item) != 0, ppd); break;
                case CompactMonitorItems.Memory:
                    var memory = s?.Memory.OrDefault;
                    Gauge(drawing, left, center, memory?.UsedFraction, memory is null ? UI.Tertiary : _colors.Memory(memory.Level), MonitorGlyph.Memory, (_values & item) != 0, ppd); break;
                case CompactMonitorItems.Gpu: Gauge(drawing, left, center, s?.Gpu.OrDefault?.Usage, _colors.Normal(item), MonitorGlyph.Gpu, (_values & item) != 0, ppd); break;
                case CompactMonitorItems.Download: Rate(drawing, left, center, MonitorGlyph.Down, s?.Network.OrDefault?.DownBytesPerSecond, _colors.Normal(item), ppd); break;
                case CompactMonitorItems.Upload: Rate(drawing, left, center, MonitorGlyph.Up, s?.Network.OrDefault?.UpBytesPerSecond, _colors.Normal(item), ppd); break;
            }
            if (partial) { drawing.Pop(); drawing.Pop(); drawing.Pop(); }
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

    // A ring whose arc is the share, and the figure after it; a ring alone carries its glyph inside, as ftop's corner badges do,
    // so it is not taken for the quota rings beside it.
    private void Gauge(DrawingContext drawing, double x, double center, double? share, string color, MonitorGlyph glyph, bool figured, double ppd)
    {
        double size = figured ? _ring : _bare, ring = Math.Max(1.2, size * .16), radius = size / 2 - ring / 2;
        var c = new Point(x + size / 2, center);
        var track = new Pen(UI.Brush(UI.Track), ring); track.Freeze();
        drawing.DrawEllipse(null, track, c, radius, radius);
        if (share is { } value) MonitorDraw.Arc(drawing, c, radius, ring, value, color);
        if (!figured)
        {
            double glyphSize = size * .5, scale = glyphSize / 16;
            var pen = new Pen(UI.Brush(UI.Secondary), Math.Max(.9, _scale) / scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, c.X - glyphSize / 2, c.Y - glyphSize / 2));
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

// How present each reading of the collapsed row is, 0 to 1, easing as readings come and go. Owned by the panel, so that it
// outlives the row, which is built again whenever its width changes.
internal sealed class StripPresence
{
    private readonly Dictionary<CompactMonitorItems, Eased> _items = [];
    private CompactMonitorItems _chosen = CompactMonitorItems.None;

    // Returns whether anything moves. A new choice of readings (the settings) shows at once.
    public bool Set(CompactMonitorItems chosen, CompactMonitorItems fitting)
    {
        bool motion = Motion.Enabled && chosen == _chosen && _items.Count > 0, moving = false;
        _chosen = chosen;
        foreach (var item in CompactMonitor.Order)
        {
            if ((chosen & item) == 0) { _items.Remove(item); continue; }
            var eased = _items.TryGetValue(item, out var known) ? known : new Eased((fitting & item) != 0 ? 1 : 0, Eased.Seconds * .8);
            moving |= eased.Set((fitting & item) != 0 ? 1 : 0, motion);
            _items[item] = eased;
        }
        return moving;
    }

    public double At(CompactMonitorItems item, double now) => _items.TryGetValue(item, out var eased) ? Math.Clamp(eased.At(now), 0, 1) : 0;
    public bool Moving(double now) => _items.Values.Any(eased => eased.Moving(now));
}
