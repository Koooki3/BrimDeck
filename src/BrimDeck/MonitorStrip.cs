using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The system readings in the collapsed notch or capsule, after ftop's strip and corner sizes: the core columns small, CPU,
// memory and GPU each as a dial with its letter inside, with its figure after it or alone (narrower), download and upload, and,
// when chosen, the system drive's space with a drive and the temperature with a thermometer, in the order and the colors set in
// the settings' table (CompactReading). It asks for the width of everything chosen (PreferredWidth); when its region in the row
// is narrower, the row scrolls it through a window of whole readings (ReadingsCarousel, CompactSpacing.Window). Each reading
// keeps the width of the widest figure it can show (every digit as wide as the widest one), so the window and the readings in
// it never move with the figures, and two readings stand the row's gap apart (Gap). On its own it shows what fits the width it
// gets (CompactMonitor.Fit), so it never cuts through a figure. It draws itself once per reading; the collapsed row stays still
// between readings. A reading that joins once its first figures arrive (StripAdmission) fades into its place while the others
// slide aside; the panel keeps those transitions (StripPresence) across rebuilds of the row.
internal sealed class MonitorStrip : FrameworkElement
{
    private readonly StripPresence _presence;
    private readonly CompactMonitorItems _shown, _values, _chosen;
    // The shown readings left to right, as the settings' table orders them.
    private readonly CompactMonitorItems[] _order;
    private readonly bool _centered;
    private readonly StripColors _colors;
    private readonly double _scale, _font, _dial, _row, _inner, _arrow, _stroke;
    private double _gap;
    private MonitorSnapshot? _snapshot;
    // The lowest battery among the devices shown (MainWindow.LowestBattery), for the battery reading.
    private BatteryReading? _battery;
    // dpi is kept in the signature for the callers.
    // unavailable: readings left out (StripAdmission.Hidden): the temperature and the system drive until their first figures, and
    // for good on a machine without the sensor, so the row never shows a dash for them.
    public MonitorStrip(IReadOnlyList<CompactReading> readings, StripColors colors, double scale, bool centered, StripPresence presence, double dpi = 1,
        CompactMonitorItems unavailable = CompactMonitorItems.None)
    {
        _presence = presence; _colors = colors; _centered = centered;
        _chosen = CompactMonitor.Shown(readings);
        _shown = _chosen & ~unavailable; _values = CompactMonitor.Values(readings);
        _order = [.. readings.Select(reading => reading.Item).Concat(CompactMonitor.Order).Distinct().Where(item => (_shown & item) != 0)];
        _scale = CompactMusicLayout.SafeScale(scale);
        _font = Math.Max(1, CompactMusicLayout.Half(11.5 * _scale));
        // Each ring is a dial carrying its letter, with its figure after it or alone.
        _dial = Math.Max(4, CompactMusicLayout.Whole(16 * _scale));
        // The row's own gap (CompactMusicLayout.Gap), so the readings stand as far apart as every other part; the row may widen it.
        _gap = CompactMusicLayout.Gap(_scale);
        // The space between a reading's mark (dial or arrow) and its figure, from ink to ink.
        _inner = Math.Max(1, CompactMusicLayout.Whole(4 * _scale));
        _arrow = Math.Round(9 * _scale); _stroke = Math.Max(1, 1.4 * _scale);
        _row = Math.Max(1, CompactMusicLayout.Whole(24 * _scale));
        Height = _row;
        IsHitTestVisible = false;
        // A reading fading out of a narrow row stays within it, off the lyric or the rings beside it.
        ClipToBounds = true;
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
    // The drive and the thermometer are a little larger than the arrows, with their own space before the figure.
    private double GlyphSize => Math.Round(11 * _scale);

    // A reading's width with this figure; the widest it can be when figure is null. A dial without its figure is the dial alone.
    private double FigureWidth(CompactMonitorItems item, string? figure) => item switch
    {
        CompactMonitorItems.Columns => ColumnsWidth(Environment.ProcessorCount),
        CompactMonitorItems.Cpu or CompactMonitorItems.Memory or CompactMonitorItems.Gpu => (_values & item) != 0 ? _dial + _inner + FigureText(figure ?? "100%") : _dial,
        CompactMonitorItems.Download or CompactMonitorItems.Upload => Math.Ceiling(ArrowInk + _inner) + (figure is null ? WidestRates.Max(FigureText) : FigureText(figure)),
        CompactMonitorItems.Temperature => GlyphSize + Math.Round(2 * _scale) + FigureText(figure ?? "125°"),
        CompactMonitorItems.Storage => GlyphSize + Math.Round(3 * _scale) + FigureText(figure ?? "100%"),
        CompactMonitorItems.Battery => BatteryWidth + Math.Round(3 * _scale) + FigureText(figure ?? "100%"),
        _ => 0
    };
    // The widest each reading gets, so the row's choice of readings never changes with the figures.
    private double ItemWidth(CompactMonitorItems item) => FigureWidth(item, null);
    private double ColumnsWidth(int cores) => Math.Min(Math.Round(56 * _scale), cores * Math.Max(1.5, 2 * _scale) + (cores - 1) * Math.Max(.5, _scale));

    private static string GaugeFigure(double? share) => share is { } v ? MonitorFormat.Percent(v) + "%" : "—";
    private static string RateFigure(double? rate) => rate is { } r ? MonitorFormat.CompactRate(r) is var q ? q.Number + q.Unit : "" : "—";
    private string Figure(CompactMonitorItems item, MonitorSnapshot? s) => item switch
    {
        CompactMonitorItems.Cpu => GaugeFigure(s?.Cpu.OrDefault?.Usage),
        CompactMonitorItems.Memory => GaugeFigure(s?.Memory.OrDefault?.UsedFraction),
        CompactMonitorItems.Gpu => GaugeFigure(s?.Gpu.OrDefault is { NoReadings: false } shown ? shown.Usage : null),
        CompactMonitorItems.Download => RateFigure(s?.Network.OrDefault?.DownBytesPerSecond),
        CompactMonitorItems.Upload => RateFigure(s?.Network.OrDefault?.UpBytesPerSecond),
        CompactMonitorItems.Temperature => TemperatureFigure(Temperatures.Hottest(s)?.Celsius),
        CompactMonitorItems.Storage => GaugeFigure(SystemDrive(s)?.UsedFraction),
        CompactMonitorItems.Battery => Devices.Figure(_battery?.Percent),
        _ => ""
    };
    // Whole degrees with the degree sign, the unit left out for room: "62°". Made once for every degree a reading can have.
    private static readonly string[] Degrees = [.. Enumerable.Range(0, (int)Temperatures.Highest + 1).Select(degrees => Temperatures.Format(degrees) + "°")];
    private static string TemperatureFigure(double? celsius) => celsius is { } c ? Degrees[(int)Math.Clamp(Math.Round(c, MidpointRounding.AwayFromZero), 0, Temperatures.Highest)] : "—";

    private CompactMonitorItems Shown => _shown;
    // Rounded up to whole device pixels by the row (DevicePixels.Up), which must not drop a reading.
    public double PreferredWidth => CompactMonitor.Width(Shown, ItemWidth, _gap);
    // The gap between two readings: the row's, widened with the row's other gaps (CompactSpacing.Arrange).
    public double Gap
    {
        get => _gap;
        set { if (_gap == value || !double.IsFinite(value) || value < 0) return; _gap = value; InvalidateArrange(); InvalidateVisual(); }
    }
    // The width of each reading shown, left to right, for the window the row scrolls them through (CompactSpacing.Window).
    public List<double> ItemWidths() => [.. _order.Select(ItemWidth)];
    // Whether any reading is chosen and available: the row then shows the strip, and it needs readings
    // (MainWindow.UpdateMonitorDemand).
    public bool Showing(double now) => Shown != CompactMonitorItems.None;
    public bool Moving(double now) => _presence.Moving(now);

    // Whether the settings choose the battery reading, shown yet or not (StripAdmission); and whether any reading other than the
    // battery is chosen, which the system sampler reads.
    public bool ChoosesBattery => (_chosen & CompactMonitorItems.Battery) != 0;
    public bool NeedsSystem => (_chosen & ~CompactMonitorItems.Battery) != 0;
    // A new lowest battery, for the battery reading's figure.
    public void SetBattery(BatteryReading? battery)
    {
        if (_battery == battery) return;
        _battery = battery; InvalidateVisual();
    }

    public void Update(MonitorSnapshot? snapshot) { _snapshot = snapshot; InvalidateVisual(); }

    // The readings that fit the width the row gives; a change starts their transitions. A row built again in the middle of one
    // carries it on, as the old row no longer draws.
    protected override Size ArrangeOverride(Size finalSize)
    {
        var fitting = CompactMonitor.Fit(Shown, ItemWidth, _gap, finalSize.Width);
        if (_presence.Set(Shown, fitting, Motion.Enabled) | _presence.Moving(Eased.Now)) Motion.Start(this, Step);
        return finalSize;
    }
    private bool Step(double now) { InvalidateVisual(); return _presence.Moving(now); }

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), center = _row / 2, now = Eased.Now;
        // Halves go up, as FxShapes.Snap rounds: banker's rounding put alternate readings on either side of a half pixel.
        double Snap(double value) => Math.Round(value * ppd, MidpointRounding.AwayFromZero) / ppd;
        // Each reading takes its width and the gap after it in proportion to how present it is, so the row closes up smoothly.
        double total = 0, last = 0;
        foreach (var item in _order)
        {
            double p = _presence.At(item, now);
            if (p <= 0) continue;
            total += (ItemWidth(item) + _gap) * p; last = p;
        }
        total = Math.Max(0, total - _gap * last);
        if (total <= 0) return;
        // From the left: the row gives the strip its own width, or a carousel scrolls it from its first reading.
        double x = _centered ? (w - total) / 2 : 0;
        var s = _snapshot;
        foreach (var item in _order)
        {
            double presence = _presence.At(item, now), width = ItemWidth(item);
            if (presence <= 0) continue;
            double advance = presence;
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
            var level = item == CompactMonitorItems.Battery ? _battery?.Level ?? MemoryLevel.Normal : ReadingLevels.Of(item, s);
            string color = _colors.At(item, level);
            switch (item)
            {
                case CompactMonitorItems.Columns: Columns(drawing, left, ItemWidth(item), center, s?.Cpu.OrDefault); break;
                case CompactMonitorItems.Cpu: Gauge(drawing, left, center, s?.Cpu.OrDefault?.Usage, color, level, "C", (_values & item) != 0, ppd); break;
                case CompactMonitorItems.Memory:
                    var memory = s?.Memory.OrDefault;
                    Gauge(drawing, left, center, memory?.UsedFraction, memory is null ? UI.Tertiary : color, level, "M", (_values & item) != 0, ppd); break;
                case CompactMonitorItems.Gpu: Gauge(drawing, left, center, s?.Gpu.OrDefault is { NoReadings: false } gpu ? gpu.Usage : null, color, level, "G", (_values & item) != 0, ppd); break;
                case CompactMonitorItems.Download: Rate(drawing, left, center, MonitorGlyph.Down, s?.Network.OrDefault?.DownBytesPerSecond, color, level, ppd); break;
                case CompactMonitorItems.Upload: Rate(drawing, left, center, MonitorGlyph.Up, s?.Network.OrDefault?.UpBytesPerSecond, color, level, ppd); break;
                case CompactMonitorItems.Temperature: Temperature(drawing, left, center, Temperatures.Hottest(s)?.Celsius, color, level, ppd); break;
                case CompactMonitorItems.Storage: Drive(drawing, left, center, SystemDrive(s)?.UsedFraction, color, level, ppd); break;
                case CompactMonitorItems.Battery: Battery(drawing, left, center, _battery, color, level, ppd); break;
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
        // As thick as a quota ring and on the same track, so its whole outline shows even at a low share.
        MonitorDraw.Dial(drawing, new Point(x + _dial / 2, center), _dial, Math.Max(1.2, CompactMusicLayout.Half(2 * _scale)), Math.Max(1, 7 * _scale), share, color, letter, ppd);
        if (!figured) return;
        var text = MonitorDraw.Text(GaugeFigure(share), _font, FigureColor(level, color), ppd);
        MonitorDraw.Baseline(drawing, text, x + _dial + _inner, Math.Round(center + _font * .36));
    }

    // A figure keeps the text color while its reading is normal and takes the level's color at the warning and critical
    // levels, as the quota bars' numbers do.
    private static string FigureColor(MemoryLevel level, string color) => level == MemoryLevel.Normal ? UI.Primary : color;

    // An arrow and the rate in at most five characters: "84K", "1.2M", "99.9G". The arrow's ink starts at x.
    private void Rate(DrawingContext drawing, double x, double center, MonitorGlyph glyph, double? rate, string color, MemoryLevel level, double ppd)
    {
        double size = Math.Round(9 * _scale);
        MonitorDraw.Arrow(drawing, new Point(x, center - size / 2), size, Math.Max(1, 1.4 * _scale), glyph, color);
        var text = MonitorDraw.Text(RateFigure(rate), _font, FigureColor(level, color), ppd);
        MonitorDraw.Baseline(drawing, text, x + Math.Ceiling(ArrowInk + _inner), Math.Round(center + _font * .36));
    }

    // The drive holding Windows (Storage.System), whose space the collapsed row shows.
    private static StorageDrive? SystemDrive(MonitorSnapshot? snapshot) => snapshot is null ? null : Storage.System(snapshot.Storage, Storage.WindowsLetter);

    // A drive and the share of its space in use: "45%".
    private void Drive(DrawingContext drawing, double x, double center, double? share, string color, MemoryLevel level, double ppd)
    {
        double size = GlyphSize;
        MonitorDraw.Arrow(drawing, new Point(x, center - size / 2), size, Math.Max(1, 1.3 * _scale), MonitorGlyph.Drive, share is null ? UI.Tertiary : color);
        var text = MonitorDraw.Text(GaugeFigure(share), _font, FigureColor(level, color), ppd);
        MonitorDraw.Baseline(drawing, text, x + size + Math.Round(3 * _scale), Math.Round(center + _font * .36));
    }

    // A thermometer and the hotter of the processor's and the graphics adapter's temperatures (Temperatures.Hottest): "62°".
    private void Temperature(DrawingContext drawing, double x, double center, double? celsius, string color, MemoryLevel level, double ppd)
    {
        double size = GlyphSize;
        MonitorDraw.Arrow(drawing, new Point(x, center - size / 2), size, Math.Max(1, 1.3 * _scale), MonitorGlyph.Thermometer, celsius is null ? UI.Tertiary : color);
        var text = MonitorDraw.Text(TemperatureFigure(celsius), _font, FigureColor(level, color), ppd);
        MonitorDraw.Baseline(drawing, text, x + size + Math.Round(2 * _scale), Math.Round(center + _font * .36));
    }

    // A small battery on its side, as tall as the drive glyph beside it and outlined in the reading's color as the drive is, filled
    // to the lowest charge (none at 0%, as on the page); then the figure: "15%". On whole device pixels. Too small to carry a mark
    // over its fill, the battery takes a bolt in place of its tip while that device charges (its body that much shorter), and
    // an exclamation mark in its empty part when low, so color is not the only sign, as on the page.
    private double BatteryWidth => Math.Round(17 * _scale);
    private void Battery(DrawingContext drawing, double x, double center, BatteryReading? battery, string color, MemoryLevel level, double ppd)
    {
        double Snap(double value) => Math.Round(value * ppd) / ppd;
        bool charging = battery is { Charging: true };
        double width = BatteryWidth, height = Snap(9 * _scale), stroke = Math.Max(1, Math.Round(1.2 * _scale * ppd)) / ppd;
        double tip = charging ? Snap(height * 15 / 23) : Snap(Math.Max(1, 1.5 * _scale));
        var body = new Rect(Snap(x) + stroke / 2, Snap(center - height / 2) + stroke / 2, Snap(width - tip - 1) - stroke, height - stroke);
        string ink = battery is null ? UI.Tertiary : color;
        double radius = PanelTheme.Corner(1.5 * _scale), tipX = Snap(body.Right + stroke / 2 + 1 / ppd);
        drawing.DrawRoundedRectangle(null, ArcPen(ink, stroke), body, radius, radius);
        if (charging) DeviceGlyphs.Bolt(drawing, new Rect(tipX, Snap(center - height / 2), tip, height), ink);
        else drawing.DrawRectangle(UI.Brush(ink), null, new Rect(tipX, Snap(center - height * .2), tip, Snap(height * .4)));
        if (battery is { } b)
        {
            double inset = stroke + 1 / ppd, room = Math.Max(0, body.Width - 2 * inset + stroke), left = body.X - stroke / 2 + inset;
            double fill = b.Percent <= 0 ? 0 : Snap(Math.Max(Math.Min(room, 1.5 * _scale), room * Math.Clamp(b.Percent / 100.0, 0, 1)));
            if (fill > 0) drawing.DrawRectangle(UI.Brush(color), null, new Rect(left, body.Y - stroke / 2 + inset, fill, Math.Max(1, body.Height + stroke - 2 * inset)));
            if (!b.Charging && b.Level != MemoryLevel.Normal)
                DeviceGlyphs.Exclamation(drawing, new Rect(left + fill, body.Y + stroke / 2, Math.Max(0, room - fill), Math.Max(0, body.Height - stroke)), color, null, ppd);
        }
        var text = MonitorDraw.Text(Devices.Figure(battery?.Percent), _font, FigureColor(level, color), ppd);
        MonitorDraw.Baseline(drawing, text, x + width + Math.Round(3 * _scale), Math.Round(center + _font * .36));
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
