using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The system readings in the collapsed notch or capsule, after ftop's strip and corner sizes: the core columns small, then CPU,
// memory and GPU each as a ring gauge with its figure, then download and upload; or, brief, the three rings alone. It offers
// each reading's widest width (Candidates) and the collapsed row admits the readings that fit whole (Admit, CompactMusicLayout);
// on its own it shows what fits the width it gets (CompactMonitor.Fit). Either way it never cuts through a figure.
// Each reading takes the room its figure needs, with every digit as wide as the widest one, so figures of one length take one
// width; a wider figure widens it at once and a narrower one gives the room back only after a while (HeldWidth), easing either
// way, so a busy rate does not push its neighbours about every second. In the row, the readings stand the row's own gap apart
// (CompactSpacing places them as parts of the row, Place). It draws itself once per reading; the collapsed row stays still
// between readings. When the row narrows or widens (a drag on its end, the lyric beside it changing), a reading that no longer
// fits fades and shrinks out of its place while the others slide together, and one that fits again comes back the same way.
// The panel keeps those transitions and the readings' widths (StripPresence) across rebuilds of the row, which happen on every
// change of its width.
internal sealed class MonitorStrip : FrameworkElement
{
    private readonly StripPresence _presence;
    private readonly CompactMonitorItems _items;
    private readonly bool _brief, _centered;
    private readonly MonitorColors _colors;
    private readonly double _scale, _font, _ring, _gap, _row, _inner, _arrow, _stroke;
    private MonitorSnapshot? _snapshot;
    // Where the row placed each reading that shows, from the strip's left, in Order; null while the strip places them itself.
    private double[]? _placed;
    // How present the row placed each of them, which a reading is drawn at (less than its own while the row is short).
    private double[]? _shares;

    public MonitorStrip(CompactMonitorMode mode, CompactMonitorItems custom, MonitorColors colors, double scale, bool centered, StripPresence presence)
    {
        _presence = presence;
        _items = CompactMonitor.Items(mode, custom); _brief = mode == CompactMonitorMode.Brief; _colors = colors; _centered = centered;
        _scale = CompactMusicLayout.SafeScale(scale);
        _font = Math.Max(1, CompactMusicLayout.Half(11.5 * _scale));
        _ring = Math.Max(4, CompactMusicLayout.Whole((_brief ? 14 : 12) * _scale));
        // The row's own gap (CompactMusicLayout.Gap), so the readings stand as far apart as every other part.
        _gap = CompactMusicLayout.Whole(8 * _scale);
        // The space between a reading's mark (ring or arrow) and its figure, from ink to ink.
        _inner = Math.Max(1, CompactMusicLayout.Whole(4 * _scale));
        _arrow = Math.Round(9 * _scale); _stroke = Math.Max(1, 1.4 * _scale);
        _row = Math.Max(1, CompactMusicLayout.Whole(24 * _scale));
        Height = _row;
        IsHitTestVisible = false;
        // A reading fading out of a narrow row stays within it, off the lyric or the rings beside it.
        ClipToBounds = true;
        _presence.Measure((_brief, _scale), Shown, item => FigureWidth(item, Figure(item, null)), false, false);
    }

    // The arrows' ink lies between 3.8 and 12.2 of their 16-unit box, widened by half the stroke on each side.
    private double ArrowInkLeft => 3.8 / 16 * _arrow - _stroke / 2;
    private double ArrowInk => 8.4 / 16 * _arrow + _stroke;

    // Every digit takes the advance of the widest, so figures of one length take one width.
    private double? _digit;
    private double Digit => _digit ??= Enumerable.Range(0, 10).Max(d => MonitorDraw.Text(d.ToString(System.Globalization.CultureInfo.InvariantCulture), _font, UI.Primary, 1).WidthIncludingTrailingWhitespace);
    private double FigureText(string figure)
    {
        int digits = figure.Count(char.IsAsciiDigit);
        string rest = string.Concat(figure.Where(c => !char.IsAsciiDigit(c)));
        return Math.Ceiling(digits * Digit + (rest.Length > 0 ? MonitorDraw.Text(rest, _font, UI.Primary, 1).WidthIncludingTrailingWhitespace : 0));
    }
    private static readonly string[] WidestRates = ["999K", "99.9M", "999M", "99.9G", "999G"];

    // A reading's width with this figure; the widest it can be when figure is null.
    private double FigureWidth(CompactMonitorItems item, string? figure) => item switch
    {
        CompactMonitorItems.Columns => _brief ? 0 : ColumnsWidth(Environment.ProcessorCount),
        CompactMonitorItems.Cpu or CompactMonitorItems.Memory or CompactMonitorItems.Gpu => _brief ? _ring : _ring + _inner + FigureText(figure ?? "100%"),
        CompactMonitorItems.Download or CompactMonitorItems.Upload => _brief ? 0 : Math.Ceiling(ArrowInk + _inner) + (figure is null ? WidestRates.Max(FigureText) : FigureText(figure)),
        _ => 0
    };
    // The widest each reading gets, so the row's choice of readings never changes with the figures.
    private double ItemWidth(CompactMonitorItems item) => FigureWidth(item, null);
    // The width it takes now.
    private double LiveWidth(CompactMonitorItems item, double now) => _presence.Width(item, now) ?? ItemWidth(item);
    private double ColumnsWidth(int cores) => Math.Min(Math.Round(56 * _scale), cores * Math.Max(1.5, 2 * _scale) + (cores - 1) * Math.Max(.5, _scale));

    private static string Figure(CompactMonitorItems item, MonitorSnapshot? s)
    {
        string Percent(double? share) => share is { } v ? MonitorFormat.Percent(v) + "%" : "—";
        string Rate(double? rate) => rate is { } r ? MonitorFormat.CompactRate(r) is var q ? q.Number + q.Unit : "" : "—";
        return item switch
        {
            CompactMonitorItems.Cpu => Percent(s?.Cpu.OrDefault?.Usage),
            CompactMonitorItems.Memory => Percent(s?.Memory.OrDefault?.UsedFraction),
            CompactMonitorItems.Gpu => Percent(s?.Gpu.OrDefault?.Usage),
            CompactMonitorItems.Download => Rate(s?.Network.OrDefault?.DownBytesPerSecond),
            CompactMonitorItems.Upload => Rate(s?.Network.OrDefault?.UpBytesPerSecond),
            _ => ""
        };
    }

    // Brief shows only the rings; columns and rates are not part of it.
    private CompactMonitorItems Shown => _brief ? _items & (CompactMonitorItems.Cpu | CompactMonitorItems.Memory | CompactMonitorItems.Gpu) : _items;
    public double PreferredWidth => CompactMonitor.Width(Shown, ItemWidth, _gap);
    // What the collapsed row fits (CompactMusicLayout): each chosen reading's widest width in the order they stay, and the
    // space between two.
    public IReadOnlyList<double> Candidates => [.. CompactMonitor.Priority.Where(item => (Shown & item) != 0).Select(ItemWidth)];
    public double Gap => _gap;

    // The row's choice: the first count of the Candidates. From then on the strip shows these instead of what fits its own
    // width, and the row places them (Readings, Place), so nothing is cut at its edges.
    private CompactMonitorItems? _admitted;
    public void Admit(int count)
    {
        _admitted = CompactMonitor.First(Shown, count);
        ClipToBounds = false;
        if (_presence.Set(Shown, _admitted.Value)) Motion.Start(this, Step);
    }
    // The readings present now, left to right, each with its width and how present it is.
    public IReadOnlyList<(double Width, double Presence)> Readings(double now)
    {
        var list = new List<(double, double)>();
        foreach (var item in CompactMonitor.Order)
        {
            if ((Shown & item) == 0) continue;
            double p = _presence.At(item, now);
            if (p > 0) list.Add((LiveWidth(item, now), p));
        }
        return list;
    }
    // The row's placing of Readings, from the strip's left, and how present it placed each (CompactSpacing.Placement.Presence).
    public void Place(double[] offsets, double[] presences)
    {
        if (_placed is not null && _placed.AsSpan().SequenceEqual(offsets) && _shares is not null && _shares.AsSpan().SequenceEqual(presences)) return;
        _placed = offsets; _shares = presences; InvalidateVisual();
    }
    // The width the readings take on their own, each in proportion to how present it is.
    public double DrawnWidth(double now)
    {
        double total = 0, last = 0;
        foreach (var item in CompactMonitor.Order)
        {
            if ((Shown & item) == 0) continue;
            double p = _presence.At(item, now);
            if (p <= 0) continue;
            total += (LiveWidth(item, now) + _gap) * p; last = p;
        }
        return Math.Max(0, total - _gap * last);
    }
    public double Presence(double now) => CompactMonitor.Order.Where(item => (Shown & item) != 0).Select(item => _presence.At(item, now)).DefaultIfEmpty(0).Max();
    public bool Moving(double now) => _presence.Moving(now);

    public void Update(MonitorSnapshot? snapshot)
    {
        _snapshot = snapshot; InvalidateVisual();
        // A figure of a new width eases its reading to it, and the row with it.
        if (snapshot is not null && _presence.Measure((_brief, _scale), Shown, item => FigureWidth(item, Figure(item, snapshot)), true, Motion.Enabled)) { Motion.Start(this, Step); Relayout(); }
    }
    private void Relayout() => (VisualTreeHelper.GetParent(this) as UIElement)?.InvalidateArrange();

    // The readings that fit the width the row gives (or that the row admitted); a change starts their transitions. A row built
    // again in the middle of one carries it on, as the old row no longer draws.
    protected override Size ArrangeOverride(Size finalSize)
    {
        var fitting = _admitted ?? CompactMonitor.Fit(Shown, ItemWidth, _gap, finalSize.Width);
        if (_presence.Set(Shown, fitting) | _presence.Moving(Eased.Now)) Motion.Start(this, Step);
        return finalSize;
    }
    private bool Step(double now) { InvalidateVisual(); if (_placed is not null) Relayout(); return _presence.Moving(now); }

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), center = _row / 2, now = Eased.Now;
        double Snap(double value) => Math.Round(value * ppd) / ppd;
        // Each reading takes its width and the gap after it in proportion to how present it is, so the row closes up smoothly.
        double total = DrawnWidth(now);
        if (total <= 0) return;
        double x = _centered ? (w - total) / 2 : w - total;
        var s = _snapshot;
        int index = 0;
        foreach (var item in CompactMonitor.Order)
        {
            if ((Shown & item) == 0) continue;
            double presence = _presence.At(item, now), width = LiveWidth(item, now);
            if (presence <= 0) continue;
            double advance = presence;
            if (_placed is { } placed && index < placed.Length)
            {
                x = placed[index];
                if (_shares is { } shares && index < shares.Length) presence = Math.Clamp(shares[index], 0, 1);
            }
            index++;
            if (presence <= 0) continue;
            // A reading on its way in or out is drawn smaller and fainter, within the room it has (CompactSpacing.Zoom), so it
            // never lies over the reading or the part beside it.
            bool partial = presence < .999;
            double offset = (width * presence - width) / 2;
            if (partial)
            {
                double zoom = CompactSpacing.Zoom(presence);
                drawing.PushOpacity(presence * presence);
                drawing.PushTransform(new ScaleTransform(zoom, zoom, x + width * presence / 2, center));
                drawing.PushTransform(new TranslateTransform(offset, 0));
            }
            double left = Snap(x);
            switch (item)
            {
                case CompactMonitorItems.Columns: Columns(drawing, left, ItemWidth(item), center, s?.Cpu.OrDefault); break;
                case CompactMonitorItems.Cpu: Gauge(drawing, left, center, s?.Cpu.OrDefault?.Usage, _colors.Performance, MonitorGlyph.Cpu, ppd); break;
                case CompactMonitorItems.Memory:
                    var memory = s?.Memory.OrDefault;
                    Gauge(drawing, left, center, memory?.UsedFraction, memory is null ? UI.Tertiary : _colors.Memory(memory.Level), MonitorGlyph.Memory, ppd); break;
                case CompactMonitorItems.Gpu: Gauge(drawing, left, center, s?.Gpu.OrDefault?.Usage, _colors.Gpu, MonitorGlyph.Gpu, ppd); break;
                case CompactMonitorItems.Download: Rate(drawing, left, center, MonitorGlyph.Down, s?.Network.OrDefault?.DownBytesPerSecond, _colors.Performance, ppd); break;
                case CompactMonitorItems.Upload: Rate(drawing, left, center, MonitorGlyph.Up, s?.Network.OrDefault?.UpBytesPerSecond, _colors.Efficiency, ppd); break;
            }
            if (partial) { drawing.Pop(); drawing.Pop(); drawing.Pop(); }
            x += (width + _gap) * advance;
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
        MonitorDraw.Baseline(drawing, text, x + _ring + _inner, Math.Round(center + _font * .36));
    }

    // An arrow and the rate in at most four characters and a letter: "84K", "1.2M". The arrow's ink starts at x.
    private void Rate(DrawingContext drawing, double x, double center, MonitorGlyph glyph, double? rate, string color, double ppd)
    {
        double size = _arrow, scale = size / 16;
        var pen = new Pen(UI.Brush(color), _stroke / scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, x - ArrowInkLeft, center - size / 2));
        drawing.DrawGeometry(null, pen, MonitorDraw.Glyph(glyph));
        drawing.Pop();
        var q = rate is { } r ? MonitorFormat.CompactRate(r) : new MonitorFormat.Quantity("—", "");
        var text = MonitorDraw.Text(q.Number + q.Unit, _font, UI.Primary, ppd);
        MonitorDraw.Baseline(drawing, text, x + Math.Ceiling(ArrowInk + _inner), Math.Round(center + _font * .36));
    }
}

// How present each reading of the collapsed row is, 0 to 1, easing as readings come and go, and how wide each is as its figure
// changes. Owned by the panel, so that it outlives the row, which is built again whenever its width changes.
internal sealed class StripPresence
{
    private readonly Dictionary<CompactMonitorItems, Eased> _items = [];
    private readonly Dictionary<CompactMonitorItems, (HeldWidth Held, Eased Width)> _widths = [];
    private CompactMonitorItems _chosen = CompactMonitorItems.None;
    private (bool, double) _sizes;

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

    // Each chosen reading's width for its figure now (width), held a while when it narrows (HeldWidth). A reading measured for
    // the first time takes its width at once; placeholders (figures false, before the first reading) never change a width the
    // panel already knows. Returns whether any width moves.
    public bool Measure((bool Brief, double Scale) sizes, CompactMonitorItems chosen, Func<CompactMonitorItems, double> width, bool figures, bool motion)
    {
        // Widths measured for other sizes (a new height, brief or full) are measured again.
        if (sizes != _sizes) { _widths.Clear(); _sizes = sizes; }
        double now = Eased.Now; bool moving = false;
        foreach (var item in CompactMonitor.Order)
        {
            if ((chosen & item) == 0) continue;
            bool known = _widths.TryGetValue(item, out var entry);
            if (known && !figures) continue;
            double target = entry.Held.Update(width(item), now);
            if (!known) entry.Width = new Eased(target, Eased.Seconds);
            else moving |= entry.Width.Set(target, motion, now);
            _widths[item] = entry;
        }
        return moving;
    }

    public double At(CompactMonitorItems item, double now) => _items.TryGetValue(item, out var eased) ? Math.Clamp(eased.At(now), 0, 1) : 0;
    public double? Width(CompactMonitorItems item, double now) => _widths.TryGetValue(item, out var entry) ? Math.Max(0, entry.Width.At(now)) : null;
    // Whether any reading still shows, even fading out.
    public bool Any(double now) => _items.Values.Any(eased => eased.At(now) > 0 || eased.Target > 0);
    public bool Moving(double now) => _items.Values.Any(eased => eased.Moving(now)) || _widths.Values.Any(entry => entry.Width.Moving(now));
}
