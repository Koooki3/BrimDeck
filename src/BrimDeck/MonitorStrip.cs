using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The system readings in the collapsed notch or capsule, after ftop's strip and corner sizes: the core columns small, then CPU,
// memory and GPU each as a ring gauge with its figure, then download and upload; or, brief, the three rings alone. It asks for
// the width of everything chosen (PreferredWidth) and shows what fits in the width it gets, dropping whole readings
// (CompactMonitor.Fit), so it never cuts through a figure. It draws itself once per reading; the collapsed row stays still
// between readings. When the row narrows or widens (a drag on its end, the lyric beside it changing), a reading that no longer
// fits fades and shrinks out of its place while the others slide together, and one that fits again comes back the same way.
// The panel keeps those transitions (StripPresence) across rebuilds of the row, which happen on every change of its width.
internal sealed class MonitorStrip : FrameworkElement
{
    private readonly StripPresence _presence;
    private readonly CompactMonitorItems _items;
    private readonly bool _brief, _centered;
    private readonly MonitorColors _colors;
    private readonly double _scale, _font, _ring, _gap, _row;
    private MonitorSnapshot? _snapshot;

    public MonitorStrip(CompactMonitorMode mode, CompactMonitorItems custom, MonitorColors colors, double scale, bool centered, StripPresence presence)
    {
        _presence = presence;
        _items = CompactMonitor.Items(mode, custom); _brief = mode == CompactMonitorMode.Brief; _colors = colors; _centered = centered;
        _scale = CompactMusicLayout.SafeScale(scale);
        _font = Math.Max(1, CompactMusicLayout.Half(11.5 * _scale));
        _ring = Math.Max(4, CompactMusicLayout.Whole((_brief ? 14 : 12) * _scale));
        _gap = CompactMusicLayout.Whole((_brief ? 7 : 10) * _scale);
        _row = Math.Max(1, CompactMusicLayout.Whole(24 * _scale));
        Height = _row;
        IsHitTestVisible = false;
        // A reading fading out of a narrow row stays within it, off the lyric or the rings beside it.
        ClipToBounds = true;
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
        double total = 0, last = 0;
        foreach (var item in CompactMonitor.Order)
        {
            if ((Shown & item) == 0) continue;
            double p = _presence.At(item, now);
            if (p <= 0) continue;
            total += (ItemWidth(item) + _gap) * p; last = p;
        }
        total -= _gap * last;
        if (total <= 0) return;
        double x = _centered ? (w - total) / 2 : w - total;
        var s = _snapshot;
        foreach (var item in CompactMonitor.Order)
        {
            if ((Shown & item) == 0) continue;
            double presence = _presence.At(item, now), width = ItemWidth(item);
            if (presence <= 0) continue;
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
                case CompactMonitorItems.Cpu: Gauge(drawing, left, center, s?.Cpu.OrDefault?.Usage, _colors.Performance, MonitorGlyph.Cpu, ppd); break;
                case CompactMonitorItems.Memory:
                    var memory = s?.Memory.OrDefault;
                    Gauge(drawing, left, center, memory?.UsedFraction, memory is null ? UI.Tertiary : _colors.Memory(memory.Level), MonitorGlyph.Memory, ppd); break;
                case CompactMonitorItems.Gpu: Gauge(drawing, left, center, s?.Gpu.OrDefault?.Usage, _colors.Gpu, MonitorGlyph.Gpu, ppd); break;
                case CompactMonitorItems.Download: Rate(drawing, left, center, MonitorGlyph.Down, s?.Network.OrDefault?.DownBytesPerSecond, _colors.Performance, ppd); break;
                case CompactMonitorItems.Upload: Rate(drawing, left, center, MonitorGlyph.Up, s?.Network.OrDefault?.UpBytesPerSecond, _colors.Efficiency, ppd); break;
            }
            if (partial) { drawing.Pop(); drawing.Pop(); drawing.Pop(); }
            x += (width + _gap) * presence;
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
