using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The system readings in the collapsed notch or capsule, after ftop's strip and corner sizes: the core columns small, CPU,
// memory and GPU each as a dial with its letter inside, with its figure after it or alone (narrower), download and upload, and,
// when chosen, the drives' space with a drive glyph, in the order and the colors set in the settings' table (CompactReading);
// the rings alone sit closer together. CPU, GPU and the drives show their share in use, their temperature, or both one above the
// other in two smaller lines (CompactFigures); the drives show each chosen letter after one glyph, each after its letter when
// there are several (CompactSources). It asks for the width of everything chosen (PreferredWidth); when its region is narrower,
// the row draws it at that width and scrolls it through a window of whole readings (ReadingsCarousel, CompactSpacing.Window).
// Each reading keeps the width of the widest figures it can show, so the window and the readings in it never move with the
// figures; the readings keep their own close spacing (Gap), which the row does not widen. It draws itself once per reading; the
// collapsed row stays still between readings. Given a narrower width directly, it shows what fits, dropping whole readings
// (CompactMonitor.Fit) so it never cuts through a figure: a reading that no longer fits fades and shrinks out of its place while
// the others slide together. The panel keeps those transitions (StripPresence) across rebuilds of the row, which happen on every
// change of its width. The battery reading (the lowest charge among the wireless devices) takes its figure from the panel (SetBattery).
internal sealed class MonitorStrip : FrameworkElement
{
    private readonly StripPresence _presence;
    private readonly CompactMonitorItems _shown, _values, _chosen;
    private readonly IReadOnlyList<CompactMonitorItems> _order;
    private readonly bool _centered;
    private readonly StripColors _colors;
    private readonly double _scale, _font, _small, _dial, _gap, _row, _dpi;
    private readonly Func<CompactMonitorItems, double> _width;
    private MonitorSnapshot? _snapshot;
    // The look of the power and charge readings on the theme in use, and the threshold under which the charge is low.
    private readonly PowerStyle _powerStyle;
    private readonly int _low;
    // The lowest battery among the devices shown (MainWindow.LowestBattery), for the battery reading.
    private BatteryReading? _battery;
    // Made once, as the strip measures and draws on every reading: the readings in order, each once, the delegates it hands to
    // CompactMonitor (a method group or a lambda passed each time would be allocated each time), and the list they are
    // arranged into.
    private readonly CompactMonitorItems[] _orderOnce;
    private readonly Func<CompactMonitorItems, double> _presenceNow, _ringFigure;
    private readonly List<(CompactMonitorItems Item, double X, double Width, double Presence)> _arranged = [];
    private double _now;
    // The rings showing their share in use and their temperature after them; the drive letters showing, the tag before each
    // (empty when one shows), and whether each shows its space and its temperature.
    private readonly CompactMonitorItems _usage, _heat;
    private readonly string[] _letters, _tags;
    private readonly bool[] _letterUsage, _letterHeat;

    // unavailable: readings left out (StripAdmission.Hidden): the drives until their first figures, and for good on a machine
    // without them, so the row never shows a dash for them. admission: which temperatures and letters have given their first
    // figures (StripAdmission.Shows); without it every chosen one shows.
    public MonitorStrip(IReadOnlyList<CompactReading> readings, StripColors colors, double scale, bool centered, StripPresence presence, double dpi,
        CompactMonitorItems unavailable = CompactMonitorItems.None, StripAdmission? admission = null, int low = Devices.DefaultLowPercent)
    {
        _presence = presence; _dpi = dpi; _colors = colors; _centered = centered; _low = low; _powerStyle = PowerStyle.For(colors.Palette);
        _order = [.. readings.Select(reading => reading.Item)];
        _orderOnce = [.. _order.Distinct()];
        _presenceNow = item => _presence.At(item, _now); _ringFigure = RingFigure;
        bool shows(CompactMonitorItems item, string part) => admission?.Shows(item, part) ?? true;
        _usage = CompactMonitorItems.Memory;
        foreach (var reading in readings)
        {
            if (reading.Item is not (CompactMonitorItems.Cpu or CompactMonitorItems.Gpu)) continue;
            if (CompactFigures.Shows(reading, CompactFigures.Usage)) _usage |= reading.Item;
            if (CompactFigures.Shows(reading, CompactFigures.Temperature) && shows(reading.Item, StripAdmission.Heat)) _heat |= reading.Item;
        }
        var drives = readings.FirstOrDefault(reading => reading.Item == CompactMonitorItems.Storage) ?? new CompactReading { Item = CompactMonitorItems.Storage };
        bool driveUsage = CompactFigures.Shows(drives, CompactFigures.Usage), driveHeat = CompactFigures.Shows(drives, CompactFigures.Temperature);
        _letters = [.. admission?.Letters(drives) ?? CompactSources.Of(drives)];
        _tags = [.. _letters.Select(letter => _letters.Length > 1 ? CompactSources.Tag(letter) : "")];
        _letterUsage = [.. _letters.Select(letter => driveUsage && shows(CompactMonitorItems.Storage, letter))];
        _letterHeat = [.. _letters.Select(letter => driveHeat && shows(CompactMonitorItems.Storage, letter + StripAdmission.Heat))];
        if (_letters.Length == 0) unavailable |= CompactMonitorItems.Storage;
        _chosen = CompactMonitor.Shown(readings);
        _shown = _chosen & ~unavailable;
        // A ring set to show only its temperature, on a machine that has none, shows its dial alone.
        _values = CompactMonitor.Values(readings) & (_usage | _heat);
        _scale = CompactMusicLayout.SafeScale(scale);
        _font = Math.Max(1, CompactMusicLayout.Half(11.5 * _scale));
        // Two figures stack in smaller type, so both fit the row's height.
        _small = Math.Max(1, CompactMusicLayout.Half(8.5 * _scale));
        // Each ring is a dial carrying its letter, with its figure after it or alone.
        _dial = Math.Max(4, CompactMusicLayout.Whole(16 * _scale));
        // Readings with figures sit closer than the dials alone: a figure narrower than the widest it can show already leaves room after it.
        _gap = CompactMusicLayout.Whole((CompactMonitor.RingsOnly(_shown, _values) ? 7 : 6) * _scale);
        _row = Math.Max(1, CompactMusicLayout.Whole(24 * _scale));
        Height = _row;
        IsHitTestVisible = false;
        // A reading fading out of a narrow row stays within it, off the lyric or the rings beside it.
        ClipToBounds = true;
        // Fixed per item, from the widest figure each can show, so the row's layout never moves with the readings. A rate has
        // at most five characters (MonitorFormat.CompactRate): "99.9M" or "99.9G" at the widest.
        double text(string sample) => MonitorDraw.Text(sample, _font, UI.Primary, 1).WidthIncludingTrailingWhitespace;
        _width = CompactMonitor.Widths(_values, _dial, _ringFigure, _dial, ColumnsWidth(Environment.ProcessorCount),
            Math.Round(9 * _scale) + Math.Round(3 * _scale) + Math.Ceiling(Math.Max(text("99.9M"), text("99.9G"))), DrivesWidth(), DeviceWidth + Math.Round(3 * _scale) + FigureWidth(WidestPercent),
            Math.Round(9 * _scale) + Math.Round(3 * _scale) + FigureWidth("≈112W"), BatteryWidth + Math.Round(3 * _scale) + FigureWidth(WidestPercent));
    }

    private const string WidestDegrees = "125°", WidestPercent = "100%";
    // The figures after a ring or a drive letter: the share alone, the temperature alone, or both stacked in the smaller type,
    // at the widest each can show.
    private double Column(bool usage, bool heat)
    {
        if (usage && heat) return Math.Max(SmallWidth(WidestPercent), SmallWidth(WidestDegrees));
        return usage ? FigureWidth(WidestPercent) : heat ? FigureWidth(WidestDegrees) : 0;
    }
    private double RingFigure(CompactMonitorItems item) => Math.Round(4 * _scale) + Column((_usage & item) != 0, (_heat & item) != 0);

    // The drives: a glyph, then each letter, its tag (if any) and its figures.
    private double Glyph => Math.Round(11 * _scale);
    private double TagGap => Math.Round(2 * _scale);
    private double EntryGap => Math.Round(5 * _scale);
    private double DrivesWidth()
    {
        double width = Glyph + Math.Round(3 * _scale);
        for (int i = 0; i < _letters.Length; i++)
            width += (i > 0 ? EntryGap : 0) + (_tags[i].Length > 0 ? FigureWidth(_tags[i]) + TagGap : 0) + Column(_letterUsage[i], _letterHeat[i]);
        return width;
    }

    private double ColumnsWidth(int cores) => Math.Min(Math.Round(56 * _scale), cores * Math.Max(1.5, 2 * _scale) + (cores - 1) * Math.Max(.5, _scale));

    private CompactMonitorItems Shown => _shown;
    // Rounded up to whole device pixels: the row rounds the region it gives down to them, which must not drop a reading.
    public double PreferredWidth => DevicePixels.Up(CompactMonitor.Width(Shown, _width, _gap), _dpi);
    // The gap between two readings.
    public double Gap => _gap;
    // While the readings scroll (ReadingsCarousel), the download and the upload after it are drawn as a pair, as close as the
    // readings are, as the scrolling strip drew its figures at their own width before; standing still, each keeps the room of
    // its widest figure.
    public bool Paired { get; set; }
    // The width of each reading shown, left to right, for the window the row scrolls them through (CompactSpacing.Window). The
    // upload right after the download is one item with it, with the gap between them: the carousel never stops between them,
    // and draws them as a pair (Paired).
    public List<double> ItemWidths()
    {
        var widths = new List<double>();
        var previous = CompactMonitorItems.None;
        foreach (var item in _orderOnce)
        {
            if ((Shown & item) == 0 || !CompactMonitor.Order.Contains(item)) continue;
            double width = Math.Max(0, _width(item));
            if (item == CompactMonitorItems.Upload && previous == CompactMonitorItems.Download) widths[^1] += _gap + width;
            else widths.Add(width);
            previous = item;
        }
        return widths;
    }

    public void Update(MonitorSnapshot? snapshot)
    {
        _snapshot = snapshot;
        InvalidateVisual();
    }
    // Whether any reading is chosen and available: the row then shows the strip, and it needs readings
    // (MainWindow.UpdateMonitorDemand).
    public bool Showing(double now) => Shown != CompactMonitorItems.None;
    public bool Moving(double now) => _presence.Moving(now);

    // For screen readers (the row's readings are drawn, not controls): every reading shown, named for what it is. The two batteries are
    // told apart in words: the devices' lowest (with the device), and this PC's own.
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, System.Windows.Automation.Peers.AutomationControlType.Text, Spoken);
    private string Spoken()
    {
        var parts = new List<string>();
        var s = _snapshot;
        foreach (var item in _orderOnce)
        {
            if ((Shown & item) == 0 || !CompactMonitor.Order.Contains(item)) continue;
            switch (item)
            {
                case CompactMonitorItems.Cpu when s?.Cpu.OrDefault is { } cpu: parts.Add("CPU " + MonitorFormat.Percent(cpu.Usage) + "%"); break;
                case CompactMonitorItems.Memory when s?.Memory.OrDefault is { } memory: parts.Add(Loc.T("内存 ", "Memory ") + MonitorFormat.Percent(memory.UsedFraction) + "%"); break;
                case CompactMonitorItems.Gpu when s?.Gpu.OrDefault is { NoReadings: false } gpu: parts.Add("GPU " + MonitorFormat.Percent(gpu.Usage) + "%"); break;
                case CompactMonitorItems.Download when s?.Network.OrDefault is { } down: parts.Add(Loc.T("下载 ", "Download ") + MonitorFormat.Rate(down.DownBytesPerSecond).Text); break;
                case CompactMonitorItems.Upload when s?.Network.OrDefault is { } up: parts.Add(Loc.T("上传 ", "Upload ") + MonitorFormat.Rate(up.UpBytesPerSecond).Text); break;
                case CompactMonitorItems.Battery when _battery is { } device:
                    parts.Add(Loc.T("设备最低电量（外设）", "Lowest device battery (peripherals)") + " " + device.Name + " " + device.Percent + "%"
                        + (device.Charging ? Loc.T("，充电", ", charging") : "") + LevelWords(device.Level)); break;
                case CompactMonitorItems.Power when s?.Power.OrDefault is { } power:
                    parts.Add(Loc.T("整机功耗 ", "System power ") + (power.Estimated ? "≈" : "") + PowerText.Watts(power.Watts) + Loc.T("，", ", ") + (power.Estimated ? Loc.T("估算", "estimated") : Loc.T("实测", "measured"))); break;
                case CompactMonitorItems.Charge when s?.Battery.OrDefault is { } own:
                    parts.Add(Loc.T("本机电池 ", "This PC's battery ") + PowerText.Percent(own.Percent) + Loc.T("，", ", ") + PowerText.State(own) + LevelWords(BatteryModel.Level(own, _low))); break;
            }
        }
        return string.Join(Loc.T("；", "; "), parts);
    }
    private static string LevelWords(MemoryLevel level) => level switch
    { MemoryLevel.Critical => Loc.T("，低电量", ", low"), MemoryLevel.Elevated => Loc.T("，电量偏低", ", getting low"), _ => "" };
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
    private double FigureWidth(string figure) => Math.Ceiling(MonitorDraw.Text(figure, _font, UI.Primary, 1).WidthIncludingTrailingWhitespace);
    private double SmallWidth(string figure) => Math.Ceiling(MonitorDraw.Text(figure, _small, UI.Primary, 1).WidthIncludingTrailingWhitespace);

    // Every whole percent's figure, made once: the gauges show one on every reading.
    private static readonly string[] Percents = [.. Enumerable.Range(0, 101).Select(percent => MonitorFormat.Percent(percent / 100d) + "%")];
    private static string GaugeFigure(double? share) => share is { } v ? Percents[MonitorFormat.WholePercent(v)] : "—";
    // A rate's arrow and figure as drawn (Rate), at this figure's own width.
    private double RateWidth(double? rate) => Math.Round(9 * _scale) + Math.Round(3 * _scale) + FigureWidth(RateFigure(rate));
    private static string RateFigure(double? rate)
    {
        var q = rate is { } r ? MonitorFormat.CompactRate(r) : new MonitorFormat.Quantity("—", "");
        return q.Number + q.Unit;
    }
    // Whole degrees with the degree sign, the unit left out for room: "62°". Made once for every degree a reading can have.
    private static readonly string[] Degrees = [.. Enumerable.Range(0, (int)Temperatures.Highest + 1).Select(degrees => Temperatures.Format(degrees) + "°")];
    private static string TemperatureFigure(double? celsius) => celsius is { } c ? Degrees[(int)Math.Clamp(Math.Round(c, MidpointRounding.AwayFromZero), 0, Temperatures.Highest)] : "—";

    // The readings that fit the width the row gives; a change starts their transitions. A row built again in the middle of one
    // carries it on, as the old row no longer draws.
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_presence.Set(Shown, CompactMonitor.Fit(Shown, _width, _gap, finalSize.Width), Motion.Enabled) | _presence.Moving(Eased.Now)) Motion.Start(this, Step);
        return finalSize;
    }
    private bool Step(double now) { InvalidateVisual(); return _presence.Moving(now); }

    protected override void OnRender(DrawingContext drawing)
    {
        double w = ActualWidth, ppd = MonitorDraw.PixelsPerDip(this), center = _row / 2, now = Eased.Now;
        // Each reading takes its width and the gap after it in proportion to how present it is, so the row closes up smoothly.
        _now = now;
        double total = CompactMonitor.Arrange(_orderOnce, Shown, _width, _gap, _presenceNow, _arranged);
        if (total <= 0) return;
        double start = _centered ? (w - total) / 2 : w - total;
        var s = _snapshot;
        // Paired, the upload right after the download follows the download's figure, not the room kept for its widest: the
        // pair stays as close as the readings are, and the room its figures do not use lies after the upload.
        double pull = 0;
        foreach (var (item, at, width, presence) in _arranged)
        {
            double x = start + at;
            if (item == CompactMonitorItems.Upload && presence >= .999) x -= pull;
            pull = Paired && item == CompactMonitorItems.Download && presence >= .999
                ? Math.Max(0, width - RateWidth(s?.Network.OrDefault?.DownBytesPerSecond)) : 0;
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
            double left = Math.Round(x);
            // Each reading in the color of its level (ReadingLevels); the core columns take theirs core by core.
            var level = item == CompactMonitorItems.Battery ? _battery?.Level ?? MemoryLevel.Normal
                : item == CompactMonitorItems.Charge ? BatteryModel.Level(s?.Battery.OrDefault, _low) : ReadingLevels.Of(item, s);
            string color = _colors.At(item, level);
            switch (item)
            {
                case CompactMonitorItems.Columns: Columns(drawing, left, width, center, s?.Cpu.OrDefault); break;
                case CompactMonitorItems.Cpu: Gauge(drawing, left, center, item, s?.Cpu.OrDefault?.Usage, color, level, "C", s, ppd); break;
                case CompactMonitorItems.Memory:
                    var memory = s?.Memory.OrDefault;
                    Gauge(drawing, left, center, item, memory?.UsedFraction, memory is null ? UI.Tertiary : color, level, "M", s, ppd); break;
                case CompactMonitorItems.Gpu: Gauge(drawing, left, center, item, s?.Gpu.OrDefault is { NoReadings: false } gpu ? gpu.Usage : null, color, level, "G", s, ppd); break;
                case CompactMonitorItems.Download: Rate(drawing, left, center, MonitorGlyph.Down, s?.Network.OrDefault?.DownBytesPerSecond, color, level, ppd); break;
                case CompactMonitorItems.Upload: Rate(drawing, left, center, MonitorGlyph.Up, s?.Network.OrDefault?.UpBytesPerSecond, color, level, ppd); break;
                case CompactMonitorItems.Storage: Drives(drawing, left, center, s, ppd); break;
                case CompactMonitorItems.Battery: Battery(drawing, left, center, _battery, color, level, ppd); break;
                case CompactMonitorItems.Power: Power(drawing, left, center, s?.Power.OrDefault, ppd); break;
                case CompactMonitorItems.Charge: Charge(drawing, left, center, s?.Battery.OrDefault, level, ppd); break;
            }
            if (partial) { drawing.Pop(); drawing.Pop(); drawing.Pop(); }
        }
    }

    // The cores as thin columns, fastest kind first, in their colors; their tops show the load at a glance, and a busy core
    // takes the warning or critical color by its own load.
    private void Columns(DrawingContext drawing, double x, double width, double center, CpuSample? cpu)
    {
        double height = Math.Round(14 * _scale), top = Math.Round(center - height / 2);
        if (cpu is null || cpu.Cores.Count == 0) { drawing.DrawRoundedRectangle(UI.Brush(UI.Fill), null, new Rect(x, top, width, height), 2, 2); return; }
        int cores = cpu.Cores.Count, groups = Math.Max(1, cpu.GroupCount);
        double gap = Math.Max(.5, _scale);
        // With more cores than columns fit (CompactSpacing.CoreColumns), neighbours share a column, which shows the busiest of them.
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
    // A core's color is mixed from the strip's colors, which it keeps: each is mixed once, not for every core on every reading.
    private readonly Dictionary<(int Group, int Groups, MemoryLevel Level), string> _coreColors = [];
    private string CoreColor(int group, int groups, MemoryLevel level)
    {
        if (!_coreColors.TryGetValue((group, groups, level), out var color)) _coreColors[(group, groups, level)] = color = _colors.Core(group, groups, level);
        return color;
    }

    // A dial open at the bottom with the reading's letter inside, and its figures after it when it shows them: the gap tells it
    // from the full quota rings beside it, and a letter stays legible at this size where a glyph does not.
    private void Gauge(DrawingContext drawing, double x, double center, CompactMonitorItems item, double? share, string color, MemoryLevel level, string letter, MonitorSnapshot? s, double ppd)
    {
        // As thick as a quota ring and on the same track, so its whole outline shows even at a low share.
        MonitorDraw.Dial(drawing, new Point(x + _dial / 2, center), _dial, Math.Max(1.2, CompactMusicLayout.Half(2 * _scale)), Math.Max(1, 7 * _scale), share, color, letter, ppd);
        if ((_values & item) == 0) return;
        var heat = (_heat & item) != 0 ? CompactFigures.Heat(item, s) : null;
        Figures(drawing, x + _dial + Math.Round(4 * _scale), center, item, (_usage & item) != 0 ? (share, level) : null, (_heat & item) != 0, heat, ppd);
    }

    // The share in use and the temperature at x: one in the row's type, or both stacked in the smaller type, the share above.
    // Each keeps the text color while normal and takes its own level's color at the warning and critical levels.
    private void Figures(DrawingContext drawing, double x, double center, CompactMonitorItems item, (double? Share, MemoryLevel Level)? usage, bool heat,
        (double Celsius, MemoryLevel Level)? degrees, double ppd)
    {
        var heatLevel = degrees?.Level ?? MemoryLevel.Normal;
        string? shareText = usage is { } u ? GaugeFigure(u.Share) : null, heatText = heat ? TemperatureFigure(degrees?.Celsius) : null;
        string ShareColor() => FigureColor(usage!.Value.Level, _colors.At(item, usage.Value.Level));
        string HeatColor() => FigureColor(heatLevel, _colors.At(item, heatLevel));
        if (shareText is not null && heatText is not null)
        {
            // The baselines sit far enough apart for a gap of about four DIPs between the lines' digits, within the row's height.
            MonitorDraw.Baseline(drawing, MonitorDraw.Text(shareText, _small, ShareColor(), ppd), x, Math.Round(center - 2.5 * _scale));
            MonitorDraw.Baseline(drawing, MonitorDraw.Text(heatText, _small, HeatColor(), ppd), x, Math.Round(center + 8 * _scale));
            return;
        }
        double baseline = Math.Round(center + _font * .36);
        if (shareText is not null) MonitorDraw.Baseline(drawing, MonitorDraw.Text(shareText, _font, ShareColor(), ppd), x, baseline);
        else if (heatText is not null) MonitorDraw.Baseline(drawing, MonitorDraw.Text(heatText, _font, HeatColor(), ppd), x, baseline);
    }

    // A figure keeps the text color while its reading is normal and takes the level's color at the warning and critical
    // levels, as the quota bars' numbers do.
    private static string FigureColor(MemoryLevel level, string color) => level == MemoryLevel.Normal ? UI.Primary : color;

    // An arrow and the rate in at most four characters and a letter: "84K", "1.2M".
    private void Rate(DrawingContext drawing, double x, double center, MonitorGlyph glyph, double? rate, string color, MemoryLevel level, double ppd)
    {
        double size = Math.Round(9 * _scale);
        MonitorDraw.Arrow(drawing, new Point(x, center - size / 2), size, Math.Max(1, 1.4 * _scale), glyph, color);
        var text = MonitorDraw.Text(RateFigure(rate), _font, FigureColor(level, color), ppd);
        MonitorDraw.Baseline(drawing, text, x + size + Math.Round(3 * _scale), Math.Round(center + _font * .36));
    }

    // A drive glyph in the color of the highest level among the figures shown, then each letter's tag, in the tertiary text
    // color, and its figures: "C 45% D 60%", or each share above its drive's temperature. A figure missing for the moment shows
    // a dash.
    private void Drives(DrawingContext drawing, double x, double center, MonitorSnapshot? s, double ppd)
    {
        const CompactMonitorItems item = CompactMonitorItems.Storage;
        var level = MemoryLevel.Normal;
        bool any = false;
        for (int i = 0; i < _letters.Length; i++)
        {
            if (_letterUsage[i] && CompactSources.Share(_letters[i], s) is { } share) { any = true; if (share.Level > level) level = share.Level; }
            if (_letterHeat[i] && CompactSources.Heat(_letters[i], s) is { } heat) { any = true; if (heat.Level > level) level = heat.Level; }
        }
        double size = Glyph, baseline = Math.Round(center + _font * .36);
        MonitorDraw.Arrow(drawing, new Point(x, center - size / 2), size, Math.Max(1, 1.3 * _scale), MonitorGlyph.Drive, any ? _colors.At(item, level) : UI.Tertiary);
        double at = x + size + Math.Round(3 * _scale);
        for (int i = 0; i < _letters.Length; i++)
        {
            if (i > 0) at += EntryGap;
            if (_tags[i].Length > 0)
            {
                MonitorDraw.Baseline(drawing, MonitorDraw.Text(_tags[i], _font, UI.Tertiary, ppd), at, baseline);
                at += FigureWidth(_tags[i]) + TagGap;
            }
            var share = _letterUsage[i] ? CompactSources.Share(_letters[i], s) : null;
            var heat = _letterHeat[i] ? CompactSources.Heat(_letters[i], s) : null;
            Figures(drawing, at, center, item, _letterUsage[i] ? (share?.Share, share?.Level ?? MemoryLevel.Normal) : null, _letterHeat[i], heat, ppd);
            // Each letter keeps the room of the widest figures it can show.
            at += Column(_letterUsage[i], _letterHeat[i]);
        }
    }

    // The lowest battery among the wireless devices (not this PC's own, which is the Charge reading and draws a battery): the icon of
    // that device, a mouse, a keyboard, headphones, in the text color (the warning or critical color only at those levels, as every
    // reading's level colors), a bolt after it while it charges, then the figure, drawn as the other readings' figures are (Figure):
    // "83%". The device's name and the kind of reading are in the row's automation name (Spoken).
    private double DeviceWidth => Math.Round(14 * _scale) + Math.Round(5 * _scale);
    private void Battery(DrawingContext drawing, double x, double center, BatteryReading? battery, string color, MemoryLevel level, double ppd)
    {
        double size = Math.Round(14 * _scale), scale = size / DeviceIcons.Grid;
        string ink = battery is null ? UI.Tertiary : level == MemoryLevel.Normal ? UI.Secondary : color;
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, Math.Round(center - size / 2)));
        drawing.DrawGeometry(null, DeviceGlyphs.Stroke(ink, 2.1), DeviceGlyphs.Icon(battery?.Kind ?? DeviceKind.Other));
        drawing.Pop();
        if (battery is { Charging: true }) DeviceGlyphs.Bolt(drawing, new Rect(x + size + Math.Round(.5 * _scale), Math.Round(center - size * .4), Math.Round(4 * _scale), Math.Round(size * .8)), ink);
        Figure(drawing, x + DeviceWidth + Math.Round(3 * _scale), center, battery is { } device ? device.Percent / 100.0 : null, "", FigureColor(level, color), ppd);
    }
    // The figure of a reading that is a share: "83%", one size, weight and baseline for every reading that has one (the dials' figures,
    // the drives', the battery's).
    private void Figure(DrawingContext drawing, double x, double center, double? share, string unused, string color, double ppd)
        => MonitorDraw.Baseline(drawing, MonitorDraw.Text(GaugeFigure(share), _font, color, ppd), x, Math.Round(center + _font * .36));

    // The whole machine's power: a bolt in the theme's curve color, then the draw in watts, "38W", with "≈" before an estimate; a dash
    // while there is no reading. The room is kept for the widest figure ("≈112W").
    private void Power(DrawingContext drawing, double x, double center, PowerSample? power, double ppd)
    {
        double size = Math.Round(11 * _scale), slot = Math.Round(9 * _scale);
        PowerIcons.Draw(drawing, PowerIcon.Bolt, new Rect(x, Math.Round(center - size / 2), slot, size), power is null ? UI.Tertiary : _powerStyle.Draw, _powerStyle, ppd);
        string figure = power is { } p ? (p.Estimated ? "≈" : "") + PowerFormat.Compact(p.Watts) + "W" : "—";
        MonitorDraw.Baseline(drawing, MonitorDraw.Text(figure, _font, UI.Primary, ppd), x + slot + Math.Round(3 * _scale), Math.Round(center + _font * .36));
    }

    private double BatteryWidth => Math.Round(17 * _scale);
    // The machine's own battery in the shape of the theme's look (PowerGlyphs.Battery), filled to the charge, with the bolt in its tip
    // while it charges, the leaf while Battery saver is on and an exclamation mark when low; then the figure: "96%".
    private void Charge(DrawingContext drawing, double x, double center, BatterySample? battery, MemoryLevel level, double ppd)
    {
        double height = Math.Round(9 * _scale * ppd) / ppd;
        var visual = battery is null ? new BatteryVisual(0, BatteryState.Discharging, false, MemoryLevel.Normal) : BatteryVisual.Of(battery, _low);
        PowerGlyphs.Battery(drawing, new Rect(x, Math.Round((center - height / 2) * ppd) / ppd, BatteryWidth, height), visual, _powerStyle, ppd,
            battery is null ? 0 : battery.Percent / 100, compact: true, unknown: battery is null);
        string color = battery is null ? UI.Tertiary : _powerStyle.Fill(level, battery.State);
        Figure(drawing, x + BatteryWidth + Math.Round(3 * _scale), center, battery is null ? null : battery.Percent / 100, "", FigureColor(level, color), ppd);
    }
}
