using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The system readings in the collapsed notch or capsule, after ftop's strip and corner sizes: the core columns small, CPU,
// memory and GPU each as a dial with its letter inside, with its figure after it or alone (narrower), and download and upload, in
// the order and the colors set in the settings' table (CompactReading). It offers each reading's widest width (Candidates) and
// the collapsed row admits the readings that fit whole (Admit, CompactMusicLayout); on its own it shows what fits the width it
// gets (CompactMonitor.Fit). Either way it never cuts through a figure.
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
    private readonly CompactMonitorItems _shown, _values;
    // The shown readings left to right, as the settings' table orders them.
    private readonly CompactMonitorItems[] _order;
    private readonly bool _centered;
    private readonly StripColors _colors;
    private readonly double _scale, _font, _dial, _gap, _row, _inner, _arrow, _stroke;
    private MonitorSnapshot? _snapshot;
    // Where the row placed each reading that shows, from the strip's left, in _order; null while the strip places them itself.
    private double[]? _placed;
    // How present the row placed each of them, which a reading is drawn at (less than its own while the row is short).
    private double[]? _shares;

    // dpi is kept in the signature for the callers; the row rounds the readings' places itself (CompactRow).
    public MonitorStrip(IReadOnlyList<CompactReading> readings, StripColors colors, double scale, bool centered, StripPresence presence, double dpi = 1)
    {
        _presence = presence; _colors = colors; _centered = centered;
        _shown = CompactMonitor.Shown(readings); _values = CompactMonitor.Values(readings);
        _order = [.. readings.Select(reading => reading.Item).Concat(CompactMonitor.Order).Distinct().Where(item => (_shown & item) != 0)];
        _scale = CompactMusicLayout.SafeScale(scale);
        _font = Math.Max(1, CompactMusicLayout.Half(11.5 * _scale));
        // Each ring is a dial carrying its letter, with its figure after it or alone.
        _dial = Math.Max(4, CompactMusicLayout.Whole(16 * _scale));
        // The row's own gap (CompactMusicLayout.Gap), so the readings stand as far apart as every other part.
        _gap = CompactMusicLayout.Whole(8 * _scale);
        // The space between a reading's mark (dial or arrow) and its figure, from ink to ink.
        _inner = Math.Max(1, CompactMusicLayout.Whole(4 * _scale));
        _arrow = Math.Round(9 * _scale); _stroke = Math.Max(1, 1.4 * _scale);
        _row = Math.Max(1, CompactMusicLayout.Whole(24 * _scale));
        Height = _row;
        IsHitTestVisible = false;
        // A reading fading out of a narrow row stays within it, off the lyric or the rings beside it.
        ClipToBounds = true;
        _presence.Measure((_values, _scale), Shown, item => FigureWidth(item, Figure(item, null)), false, false);
    }

    // The arrows' ink lies between 3.8 and 12.2 of their 16-unit box, widened by half the stroke on each side.
    private double ArrowInkLeft => 3.8 / 16 * _arrow - _stroke / 2;
    private double ArrowInk => 8.4 / 16 * _arrow + _stroke;

    // Every digit takes the advance of the widest, so figures of one length take one width.
    private double? _digit;
    private double Digit => _digit ??= Enumerable.Range(0, 10).Max(d => MonitorDraw.Text(d.ToString(System.Globalization.CultureInfo.InvariantCulture), _font, UI.Primary, 1).WidthIncludingTrailingWhitespace);
    // A figure's width is its digits' and the rest's ("%", ".", a unit letter, "—"); the few rests there are are measured once.
    // Asked for every reading on every sample, so it allocates nothing once they are known.
    private readonly Dictionary<string, double> _restWidths = [];
    private double FigureText(string figure)
    {
        int digits = 0, length = 0;
        Span<char> rest = figure.Length <= 32 ? stackalloc char[figure.Length] : new char[figure.Length];
        foreach (char c in figure) if (char.IsAsciiDigit(c)) digits++; else rest[length++] = c;
        double restWidth = 0;
        if (length > 0 && !_restWidths.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(rest[..length], out restWidth))
        {
            var text = new string(rest[..length]);
            _restWidths[text] = restWidth = MonitorDraw.Text(text, _font, UI.Primary, 1).WidthIncludingTrailingWhitespace;
        }
        return Math.Ceiling(digits * Digit + restWidth);
    }
    // A rate has at most five characters (MonitorFormat.CompactRate): "99.9M" or "99.9G" at the widest.
    private static readonly string[] WidestRates = ["999K", "99.9M", "999M", "99.9G", "999G"];

    // A reading's width with this figure; the widest it can be when figure is null. A dial without its figure is the dial alone.
    private double FigureWidth(CompactMonitorItems item, string? figure) => item switch
    {
        CompactMonitorItems.Columns => ColumnsWidth(Environment.ProcessorCount),
        CompactMonitorItems.Cpu or CompactMonitorItems.Memory or CompactMonitorItems.Gpu => (_values & item) != 0 ? _dial + _inner + FigureText(figure ?? "100%") : _dial,
        CompactMonitorItems.Download or CompactMonitorItems.Upload => Math.Ceiling(ArrowInk + _inner) + (figure is null ? WidestRates.Max(FigureText) : FigureText(figure)),
        _ => 0
    };
    // The widest each reading gets, so the row's choice of readings never changes with the figures.
    private double ItemWidth(CompactMonitorItems item) => FigureWidth(item, null);
    // The width it takes now.
    private double LiveWidth(CompactMonitorItems item, double now) => _presence.Width(item, now) ?? ItemWidth(item);
    private double ColumnsWidth(int cores) => Math.Min(Math.Round(56 * _scale), cores * Math.Max(1.5, 2 * _scale) + (cores - 1) * Math.Max(.5, _scale));

    private static string GaugeFigure(double? share) => share is { } v ? MonitorFormat.Percent(v) + "%" : "—";
    private static string RateFigure(double? rate) => rate is { } r ? MonitorFormat.CompactRate(r) is var q ? q.Number + q.Unit : "" : "—";
    private static string Figure(CompactMonitorItems item, MonitorSnapshot? s) => item switch
    {
        CompactMonitorItems.Cpu => GaugeFigure(s?.Cpu.OrDefault?.Usage),
        CompactMonitorItems.Memory => GaugeFigure(s?.Memory.OrDefault?.UsedFraction),
        CompactMonitorItems.Gpu => GaugeFigure(s?.Gpu.OrDefault?.Usage),
        CompactMonitorItems.Download => RateFigure(s?.Network.OrDefault?.DownBytesPerSecond),
        CompactMonitorItems.Upload => RateFigure(s?.Network.OrDefault?.UpBytesPerSecond),
        _ => ""
    };

    private CompactMonitorItems Shown => _shown;
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
        var list = new List<(double, double)>(_order.Length);
        foreach (var item in _order)
        {
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
        foreach (var item in _order)
        {
            double p = _presence.At(item, now);
            if (p <= 0) continue;
            total += (LiveWidth(item, now) + _gap) * p; last = p;
        }
        return Math.Max(0, total - _gap * last);
    }
    public double Presence(double now)
    {
        double most = 0;
        foreach (var item in _order) most = Math.Max(most, _presence.At(item, now));
        return most;
    }
    // Whether the row admitted any reading or one is still fading out: otherwise nothing of the strip shows and it needs no
    // readings (MainWindow.UpdateMonitorDemand).
    public bool Showing(double now) => _admitted is not { } admitted || admitted != CompactMonitorItems.None || _presence.Any(now);
    public bool Moving(double now) => _presence.Moving(now);

    public void Update(MonitorSnapshot? snapshot)
    {
        _snapshot = snapshot; InvalidateVisual();
        // A figure of a new width eases its reading to it, and the row with it.
        if (snapshot is not null && _presence.Measure((_values, _scale), Shown, item => FigureWidth(item, Figure(item, snapshot)), true, Motion.Enabled)) { Motion.Start(this, Step); Relayout(); }
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
        // Halves go up, as FxShapes.Snap rounds: banker's rounding put alternate readings on either side of a half pixel.
        double Snap(double value) => Math.Round(value * ppd, MidpointRounding.AwayFromZero) / ppd;
        // Each reading takes its width and the gap after it in proportion to how present it is, so the row closes up smoothly.
        double total = DrawnWidth(now);
        if (total <= 0) return;
        double x = _centered ? (w - total) / 2 : w - total;
        var s = _snapshot;
        int index = 0;
        foreach (var item in _order)
        {
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
            // Each reading in the color of its level (ReadingLevels); the core columns take theirs column by column.
            var level = ReadingLevels.Of(item, s);
            string color = _colors.At(item, level);
            switch (item)
            {
                case CompactMonitorItems.Columns: Columns(drawing, left, ItemWidth(item), center, s?.Cpu.OrDefault); break;
                case CompactMonitorItems.Cpu: Gauge(drawing, left, center, s?.Cpu.OrDefault?.Usage, color, level, "C", (_values & item) != 0, ppd); break;
                case CompactMonitorItems.Memory:
                    var memory = s?.Memory.OrDefault;
                    Gauge(drawing, left, center, memory?.UsedFraction, memory is null ? UI.Tertiary : color, level, "M", (_values & item) != 0, ppd); break;
                case CompactMonitorItems.Gpu: Gauge(drawing, left, center, s?.Gpu.OrDefault?.Usage, color, level, "G", (_values & item) != 0, ppd); break;
                case CompactMonitorItems.Download: Rate(drawing, left, center, MonitorGlyph.Down, s?.Network.OrDefault?.DownBytesPerSecond, color, level, ppd); break;
                case CompactMonitorItems.Upload: Rate(drawing, left, center, MonitorGlyph.Up, s?.Network.OrDefault?.UpBytesPerSecond, color, level, ppd); break;
            }
            if (partial) { drawing.Pop(); drawing.Pop(); drawing.Pop(); }
            x += (width + _gap) * advance;
        }
    }

    // The cores as thin columns, fastest kind first, each in the color of its kind of core; their tops show the load at a glance,
    // and a busy core takes the warning or critical color by its own load. With more cores than columns fit
    // (CompactSpacing.CoreColumns), neighbours share a column, which shows the busiest of them: its load, its kind's color and its
    // level, so a column never looks calmer than the busiest core in it.
    private void Columns(DrawingContext drawing, double x, double width, double center, CpuSample? cpu)
    {
        double height = Math.Round(14 * _scale), top = Math.Round(center - height / 2);
        if (cpu is null || cpu.Cores.Count == 0) { drawing.DrawRoundedRectangle(UI.Brush(UI.Fill), null, new Rect(x, top, width, height), 2, 2); return; }
        int cores = cpu.Cores.Count, groups = Math.Max(1, cpu.GroupCount);
        double gap = Math.Max(.5, _scale);
        int count = CompactSpacing.CoreColumns(cores, width, gap);
        double column = Math.Max(1, (width - (count - 1) * gap) / count);
        for (int i = 0; i < count; i++)
        {
            var (first, end) = CompactSpacing.CoreRange(i, count, cores);
            var core = cpu.Cores[first];
            for (int j = first + 1; j < end; j++) if (cpu.Cores[j].Usage > core.Usage) core = cpu.Cores[j];
            double cx = x + i * (column + gap), fill = Math.Max(1.5, core.Usage * height);
            drawing.DrawRectangle(UI.Brush(UI.Fill), null, new Rect(cx, top, column, height));
            drawing.DrawRectangle(UI.Brush(CoreColor(core.Group, groups, ReadingLevels.OfShare(core.Usage))), null, new Rect(cx, top + height - fill, column, fill));
        }
    }
    // The colors of the core columns, worked out once per kind of core and level (StripColors.Core mixes them).
    private readonly Dictionary<(int, int, MemoryLevel), string> _cores = [];
    private string CoreColor(int group, int groups, MemoryLevel level)
    {
        if (!_cores.TryGetValue((group, groups, level), out var color)) _cores[(group, groups, level)] = color = _colors.Core(group, groups, level);
        return color;
    }

    // A dial open at the bottom with the reading's letter inside, and the figure after it when it shows one: the gap tells it from
    // the full quota rings beside it, and a letter stays legible at this size where a glyph does not.
    private void Gauge(DrawingContext drawing, double x, double center, double? share, string color, MemoryLevel level, string letter, bool figured, double ppd)
    {
        Dial(drawing, x, center, share, color, letter, ppd);
        if (!figured) return;
        var text = MonitorDraw.Text(GaugeFigure(share), _font, FigureColor(level, color), ppd);
        MonitorDraw.Baseline(drawing, text, x + _dial + _inner, Math.Round(center + _font * .36));
    }

    // Three quarters of a circle, from lower left over the top to lower right, filled clockwise by the share. Its centre sits a
    // little low, so the open circle looks centred in the row; the letter is centred on its own outline, not on the font's box.
    private const double DialStart = Math.PI * 1.25, DialSweep = Math.PI * 1.5;
    private void Dial(DrawingContext drawing, double x, double center, double? share, string color, string letter, double ppd)
    {
        // As thick as a quota ring and on the same track, so its whole outline shows even at a low share.
        double size = _dial, ring = Math.Max(1.2, CompactMusicLayout.Half(2 * _scale)), radius = size / 2 - ring / 2;
        var c = new Point(x + size / 2, center + .5 * _scale);
        DialArc(drawing, c, radius, 1, ArcPen(PanelTheme.Hex("ringTrack"), ring));
        if (share is { } value && value > .002) DialArc(drawing, c, radius, Math.Min(1, value), ArcPen(color, ring));
        // Filled as its outline rather than drawn as text: WPF puts a glyph run's baseline on whole device pixels and hints the
        // glyph to them, which at a fractional scale left the letter up to a pixel above the ring's centre (0.7 px at 150 %). The
        // outline is rasterised exactly where it lies, as the ring is, so the two share their centre at any scale. Unhinted, it
        // comes out lighter than the text did (about two thirds of its ink at 150 %); a stroke around it gives the weight back
        // evenly on every side, so the centre stays. The text renderer darkens a letter by a fraction of a pixel that grows much
        // more slowly than the letter, so the stroke follows the square root of its size in device pixels (about half a pixel at
        // 150 %, and close to the text's weight up to 300 %); below 8 px it shrinks with the letter, which keeps the
        // counters of a 100 % letter open. The outline and its stroke are made once per letter and scale.
        if (Letter(letter, ppd) is not var (glyph, weight)) return;
        var bounds = glyph.Bounds;
        drawing.PushTransform(new TranslateTransform(c.X - bounds.X - bounds.Width / 2, c.Y - bounds.Y - bounds.Height / 2));
        drawing.DrawGeometry(UI.Brush(UI.Primary), weight, glyph);
        drawing.Pop();
    }
    private readonly Dictionary<string, (Geometry Glyph, Pen Weight)> _letters = [];
    private double _lettersPpd;
    private (Geometry Glyph, Pen Weight)? Letter(string letter, double ppd)
    {
        if (ppd != _lettersPpd) { _letters.Clear(); _lettersPpd = ppd; }
        if (_letters.TryGetValue(letter, out var known)) return known;
        var glyph = MonitorDraw.Text(letter, Math.Max(1, 7 * _scale), UI.Primary, ppd, strong: true).BuildGeometry(new Point());
        if (glyph.Bounds.IsEmpty) return null;
        glyph.Freeze();
        double pixels = Math.Max(1, 7 * _scale) * ppd;
        var weight = Frozen(new Pen(UI.Brush(UI.Primary), Math.Min(.06 * pixels, .17 * Math.Sqrt(pixels)) / ppd) { LineJoin = PenLineJoin.Round });
        return _letters[letter] = (glyph, weight);
    }

    private static void DialArc(DrawingContext drawing, Point c, double radius, double fraction, Pen pen)
    {
        Point at(double angle) => new(c.X + radius * Math.Sin(angle), c.Y - radius * Math.Cos(angle));
        double sweep = DialSweep * fraction;
        var figure = new PathFigure { StartPoint = at(DialStart), IsClosed = false };
        figure.Segments.Add(new ArcSegment(at(DialStart + sweep), new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]); geometry.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
    }

    // A figure keeps the text color while its reading is normal and takes the level's color at the warning and critical
    // levels, as the quota bars' numbers do.
    private static string FigureColor(MemoryLevel level, string color) => level == MemoryLevel.Normal ? UI.Primary : color;

    // An arrow and the rate in at most five characters: "84K", "1.2M", "99.9G". The arrow's ink starts at x.
    private void Rate(DrawingContext drawing, double x, double center, MonitorGlyph glyph, double? rate, string color, MemoryLevel level, double ppd)
    {
        double size = _arrow, scale = size / 16;
        if (!_arrows.TryGetValue(color, out var pen))
            _arrows[color] = pen = Frozen(new Pen(UI.Brush(color), _stroke / scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, x - ArrowInkLeft, center - size / 2));
        drawing.DrawGeometry(null, pen, MonitorDraw.Glyph(glyph));
        drawing.Pop();
        var text = MonitorDraw.Text(RateFigure(rate), _font, FigureColor(level, color), ppd);
        MonitorDraw.Baseline(drawing, text, x + Math.Ceiling(ArrowInk + _inner), Math.Round(center + _font * .36));
    }

    // The pens depend only on the strip's size and colors, which a strip keeps; they are made on its first drawing, one per color
    // (a reading changes color with its level).
    private readonly Dictionary<string, Pen> _arrows = [], _arcs = [];
    private Pen ArcPen(string color, double thickness)
    {
        if (!_arcs.TryGetValue(color, out var pen)) _arcs[color] = pen = Frozen(new Pen(UI.Brush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        return pen;
    }
    private static Pen Frozen(Pen pen) { pen.Freeze(); return pen; }
}

// How present each reading of the collapsed row is, 0 to 1, easing as readings come and go, and how wide each is as its figure
// changes. Owned by the panel, so that it outlives the row, which is built again whenever its width changes.
internal sealed class StripPresence
{
    private readonly Dictionary<CompactMonitorItems, Eased> _items = [];
    private readonly Dictionary<CompactMonitorItems, (HeldWidth Held, Eased Width)> _widths = [];
    private CompactMonitorItems _chosen = CompactMonitorItems.None;
    private (CompactMonitorItems, double) _sizes;

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
    public bool Measure((CompactMonitorItems Values, double Scale) sizes, CompactMonitorItems chosen, Func<CompactMonitorItems, double> width, bool figures, bool motion)
    {
        // Widths measured for other sizes (a new height, or rings that show or hide their figures) are measured again.
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
