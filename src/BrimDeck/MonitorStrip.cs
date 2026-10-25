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
    // The shown readings left to right, as the settings' table orders them, each once.
    private readonly CompactMonitorItems[] _order;
    private readonly bool _centered;
    private readonly StripColors _colors;
    private readonly double _scale, _font, _dial, _gap, _row, _inner, _arrow, _stroke, _dpi;
    private MonitorSnapshot? _snapshot;
    // Made once, as the strip measures on every reading and steps on every frame of a transition: the delegates it hands to
    // CompactMonitor, StripPresence and Motion (a method group or a lambda passed each time would be allocated each time), each
    // reading's widest width, and the lists the readings are offered to the row in and placed by.
    private readonly Func<CompactMonitorItems, double> _itemWidth, _figureNow;
    private readonly Func<double, bool> _step;
    private readonly Dictionary<CompactMonitorItems, double> _widest = [];
    private readonly List<(double Width, double Presence)> _readings = [];
    private readonly List<CompactMonitorItems> _offered = [];
    // The readings as the row last placed them, with the widths it placed them at, where (from the strip's left) and how present
    // (less than their own while the row is short): taken in one arrange and drawn as they are, so the drawing never mixes a slot
    // of one moment with a reading of another. Unused while the strip places the readings itself (_rowPlaced false).
    private readonly List<(CompactMonitorItems Item, double Width, double X, double Share)> _frame = [];
    private bool _rowPlaced;

    // dpi: device pixels per DIP; a reading's width eases only for a change of at least one (StripPresence.Measure).
    public MonitorStrip(IReadOnlyList<CompactReading> readings, StripColors colors, double scale, bool centered, StripPresence presence, double dpi = 1)
    {
        _presence = presence; _colors = colors; _centered = centered; _step = Step;
        _dpi = double.IsFinite(dpi) && dpi > 0 ? dpi : 1;
        _shown = CompactMonitor.Shown(readings); _values = CompactMonitor.Values(readings);
        _order = [.. readings.Select(reading => reading.Item).Concat(CompactMonitor.Order).Distinct().Where(item => (_shown & item) != 0)];
        _itemWidth = ItemWidth; _figureNow = item => FigureWidth(item, Figure(item, _snapshot));
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
        // Before the first reading each reading takes its widest width; a width the panel already knows stays.
        _presence.Measure((_values, _scale), Shown, _itemWidth, false, false);
    }

    // The arrows' ink lies between 3.8 and 12.2 of their 16-unit box, widened by half the stroke on each side.
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
    // The widest each reading gets, so the row's choice of readings never changes with the figures. Worked out once per reading.
    private double ItemWidth(CompactMonitorItems item)
    {
        if (!_widest.TryGetValue(item, out var width)) _widest[item] = width = FigureWidth(item, null);
        return width;
    }
    // The width it takes now.
    private double LiveWidth(CompactMonitorItems item, double now) => _presence.Width(item, now) ?? ItemWidth(item);
    private double ColumnsWidth(int cores) => Math.Min(Math.Round(56 * _scale), cores * Math.Max(1.5, 2 * _scale) + (cores - 1) * Math.Max(.5, _scale));

    private CompactMonitorItems Shown => _shown;
    public double PreferredWidth => CompactMonitor.Width(Shown, _itemWidth, _gap);
    // What the collapsed row fits (CompactMusicLayout): each chosen reading's widest width in the order they stay, and the
    // space between two.
    public IReadOnlyList<double> Candidates => [.. CompactMonitor.Priority.Where(item => (Shown & item) != 0).Select(_itemWidth)];
    public double Gap => _gap;

    // The row's choice: the first count of the Candidates. From then on the strip shows these instead of what fits its own
    // width, and the row places them (Readings, Place), so nothing is cut at its edges.
    private CompactMonitorItems? _admitted;
    public void Admit(int count)
    {
        _admitted = CompactMonitor.First(Shown, count);
        ClipToBounds = false;
        if (_presence.Set(Shown, _admitted.Value)) Motion.Start(this, _step);
    }
    // The readings present now, left to right, each with its width and how present it is. The list is the strip's own, filled
    // again on every call; Place takes the row's placing of these very readings.
    public IReadOnlyList<(double Width, double Presence)> Readings(double now)
    {
        _readings.Clear(); _offered.Clear();
        foreach (var item in _order)
        {
            double p = _presence.At(item, now);
            if (p > 0) { _readings.Add((LiveWidth(item, now), p)); _offered.Add(item); }
        }
        return _readings;
    }
    // The row's placing of the last Readings, from the strip's left, and how present it placed each
    // (CompactSpacing.Placement.Presence); empty when none is present. The strip draws exactly these until the next placing.
    public void Place(IReadOnlyList<double> offsets, IReadOnlyList<double> presences)
    {
        int count = Math.Min(_offered.Count, Math.Min(offsets.Count, presences.Count));
        bool same = _rowPlaced && _frame.Count == count;
        for (int i = 0; same && i < count; i++)
            same = _frame[i] == (_offered[i], _readings[i].Width, offsets[i], presences[i]);
        _rowPlaced = true;
        if (same) return;
        _frame.Clear();
        for (int i = 0; i < count; i++) _frame.Add((_offered[i], _readings[i].Width, offsets[i], presences[i]));
        InvalidateVisual();
    }
    // The width the readings take on their own, each in proportion to how present it is.
    private double DrawnWidth(double now)
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
    // Whether the row admitted any reading or one is still fading out: otherwise nothing of the strip shows and it needs no
    // readings (MainWindow.UpdateMonitorDemand).
    public bool Showing(double now) => _admitted is not { } admitted || admitted != CompactMonitorItems.None || _presence.Any(now);
    public bool Moving(double now) => _presence.Moving(now);

    public void Update(MonitorSnapshot? snapshot)
    {
        _snapshot = snapshot; InvalidateVisual();
        // A figure of a new width eases its reading to it, and the row with it.
        if (snapshot is not null && _presence.Measure((_values, _scale), Shown, _figureNow, true, Motion.Enabled, _dpi)) { Motion.Start(this, _step); Relayout(); }
    }
    private void Relayout() => (VisualTreeHelper.GetParent(this) as UIElement)?.InvalidateArrange();

    // Every whole percent's figure, made once: the gauges show one on every reading.
    private static readonly string[] Percents = [.. Enumerable.Range(0, 101).Select(percent => MonitorFormat.Percent(percent / 100d) + "%")];
    private static string GaugeFigure(double? share) => share is { } v ? Percents[MonitorFormat.WholePercent(v)] : "—";
    private static string RateFigure(double? rate)
    {
        var q = rate is { } r ? MonitorFormat.CompactRate(r) : new MonitorFormat.Quantity("—", "");
        return q.Number + q.Unit;
    }
    private static string Figure(CompactMonitorItems item, MonitorSnapshot? s) => item switch
    {
        CompactMonitorItems.Cpu => GaugeFigure(s?.Cpu.OrDefault?.Usage),
        CompactMonitorItems.Memory => GaugeFigure(s?.Memory.OrDefault?.UsedFraction),
        CompactMonitorItems.Gpu => GaugeFigure(s?.Gpu.OrDefault?.Usage),
        CompactMonitorItems.Download => RateFigure(s?.Network.OrDefault?.DownBytesPerSecond),
        CompactMonitorItems.Upload => RateFigure(s?.Network.OrDefault?.UpBytesPerSecond),
        _ => ""
    };

    // The readings that fit the width the row gives (or that the row admitted); a change starts their transitions. A row built
    // again in the middle of one carries it on, as the old row no longer draws.
    protected override Size ArrangeOverride(Size finalSize)
    {
        var fitting = _admitted ?? CompactMonitor.Fit(Shown, _itemWidth, _gap, finalSize.Width);
        if (_presence.Set(Shown, fitting) | _presence.Moving(Eased.Now)) Motion.Start(this, _step);
        return finalSize;
    }
    // Placed by the row, the strip draws what the row placed: the row's arrange (Relayout) places the readings again and redraws
    // them when anything changed (Place).
    private bool Step(double now) { if (_rowPlaced) Relayout(); else InvalidateVisual(); return _presence.Moving(now); }

    protected override void OnRender(DrawingContext drawing)
    {
        double ppd = MonitorDraw.PixelsPerDip(this), center = _row / 2;
        var s = _snapshot;
        if (_rowPlaced)
        {
            foreach (var (item, width, x, share) in _frame) Reading(drawing, item, x, width, Math.Clamp(share, 0, 1), center, ppd, s);
            return;
        }
        // On its own, each reading takes its width and the gap after it in proportion to how present it is, so the strip closes up
        // smoothly.
        double now = Eased.Now, total = DrawnWidth(now);
        if (total <= 0) return;
        double left = _centered ? (ActualWidth - total) / 2 : ActualWidth - total;
        foreach (var item in _order)
        {
            double presence = _presence.At(item, now), width = LiveWidth(item, now);
            if (presence <= 0) continue;
            Reading(drawing, item, left, width, presence, center, ppd, s);
            left += (width + _gap) * presence;
        }
    }

    // One reading at x, width wide when fully present, drawn as present as given.
    private void Reading(DrawingContext drawing, CompactMonitorItems item, double x, double width, double presence, double center, double ppd, MonitorSnapshot? s)
    {
        if (presence <= 0) return;
        // Halves go up: banker's rounding put alternate readings on either side of a half pixel.
        double Snap(double value) => Math.Round(value * ppd, MidpointRounding.AwayFromZero) / ppd;
        // A reading on its way in or out is drawn smaller and fainter, within the room it has (CompactSpacing.Zoom), so it never
        // lies over the reading or the part beside it.
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
        // Each reading in the color of its level (ReadingLevels); the core columns take theirs core by core.
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
    }

    // The cores as thin columns, fastest kind first, in their colors; their tops show the load at a glance, and a busy core
    // takes the warning or critical color by its own load.
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
            drawing.DrawRectangle(UI.Brush(CoreColor(core.Group, groups, ReadingLevels.OfShare(core.Usage))), null, new Rect(cx, top + height - fill, column, fill));
        }
    }
    // A core's color is mixed from the strip's colors, which it keeps: each is mixed once, not for every core on every reading.
    private readonly Dictionary<(int Group, int Groups, MemoryLevel Level), string> _coreColors = [];
    private string CoreColor(int group, int groups, MemoryLevel level)
    {
        if (!_coreColors.TryGetValue((group, groups, level), out var color)) _coreColors[(group, groups, level)] = color = _colors.Core(group, groups, level);
        return color;
    }

    // A dial open at the bottom with the reading's letter inside, and the figure after it when it shows one: the gap tells it from
    // the full quota rings beside it, and a letter stays legible at this size where a glyph does not.
    private void Gauge(DrawingContext drawing, double x, double center, double? share, string color, MemoryLevel level, string letter, bool figured, double ppd)
    {
        // As thick as a quota ring and on the same track, so its whole outline shows even at a low share.
        MonitorDraw.Dial(drawing, new Point(x + _dial / 2, center), _dial, Math.Max(1.2, CompactMusicLayout.Half(2 * _scale)), Math.Max(1, 7 * _scale), share, color, letter, ppd);
        if (!figured) return;
        var text = MonitorDraw.Text(GaugeFigure(share), _font, FigureColor(level, color), ppd);
        MonitorDraw.Baseline(drawing, text, x + _dial + _inner, Math.Round(center + _font * .36));
    }

    // A figure keeps the text color while its reading is normal and takes the level's color at the warning and critical
    // levels, as the quota bars' numbers do.
    private static string FigureColor(MemoryLevel level, string color) => level == MemoryLevel.Normal ? UI.Primary : color;

    // An arrow and the rate in at most five characters: "84K", "1.2M", "99.9G". The figure stands the same space from the
    // arrow's ink as a dial's figure from its dial.
    private void Rate(DrawingContext drawing, double x, double center, MonitorGlyph glyph, double? rate, string color, MemoryLevel level, double ppd)
    {
        MonitorDraw.Arrow(drawing, new Point(x, center - _arrow / 2), _arrow, _stroke, glyph, color);
        var text = MonitorDraw.Text(RateFigure(rate), _font, FigureColor(level, color), ppd);
        MonitorDraw.Baseline(drawing, text, x + Math.Ceiling(ArrowInk + _inner), Math.Round(center + _font * .36));
    }
}

// How present each reading of the collapsed row is, 0 to 1, easing as readings come and go, and how wide each is as its figure
// changes. Owned by the panel, so that it outlives the row, which is built again whenever its width changes.
internal sealed class StripPresence
{
    private readonly Dictionary<CompactMonitorItems, Eased> _items = [];
    private readonly Dictionary<CompactMonitorItems, ReadingWidth> _widths = [];
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

    // Each chosen reading's width for its figure now (width), held a while when it narrows (ReadingWidth). A reading measured for
    // the first time takes its width at once, and so does one that does not show or moves less than a device pixel (dpi), so only
    // a visible change starts frames; placeholders (figures false, before the first reading) never change a width the panel already
    // knows. Returns whether any width moves. Asked on every reading: it allocates nothing once the readings are known.
    public bool Measure((CompactMonitorItems Values, double Scale) sizes, CompactMonitorItems chosen, Func<CompactMonitorItems, double> width, bool figures, bool motion, double dpi = 1)
    {
        // Widths measured for other sizes (a new height, or rings that show or hide their figures) are measured again.
        if (sizes != _sizes) { _widths.Clear(); _sizes = sizes; }
        double now = Eased.Now; bool moving = false;
        for (int i = 0; i < CompactMonitor.Order.Count; i++)
        {
            var item = CompactMonitor.Order[i];
            if ((chosen & item) == 0) continue;
            _widths.TryGetValue(item, out var entry);
            if (entry.Known && !figures) continue;
            moving |= entry.Update(width(item), now, Shows(item, now), motion, dpi);
            _widths[item] = entry;
        }
        return moving;
    }

    public double At(CompactMonitorItems item, double now) => _items.TryGetValue(item, out var eased) ? Math.Clamp(eased.At(now), 0, 1) : 0;
    public double? Width(CompactMonitorItems item, double now) => _widths.TryGetValue(item, out var entry) ? entry.At(now) : null;
    // Whether a reading shows or is on its way in or out.
    private bool Shows(CompactMonitorItems item, double now) => _items.TryGetValue(item, out var eased) && (eased.Target > 0 || eased.At(now) > 0);
    // Whether any reading still shows, even fading out.
    public bool Any(double now)
    {
        foreach (var eased in _items.Values) if (eased.At(now) > 0 || eased.Target > 0) return true;
        return false;
    }
    public bool Moving(double now)
    {
        // Asked on every frame of a transition; the dictionaries' own enumerators allocate nothing.
        foreach (var eased in _items.Values) if (eased.Moving(now)) return true;
        foreach (var entry in _widths.Values) if (entry.Moving(now)) return true;
        return false;
    }
}
