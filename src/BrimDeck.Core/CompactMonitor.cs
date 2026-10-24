namespace BrimDeck.Core;

// The system monitor's choices beyond its colors. All are saved as numbers, so new members go at the end.
// MonitorMain: the gauge the page shows large; the other one shows small beside it, and a click swaps them.
public enum MonitorMain { Cpu, Gpu }
// MonitorProcessView: Auto shows the detailed rows (CPU, memory and the number of processes) where the list is wide enough.
public enum MonitorProcessView { Auto, Compact, Detailed }
// CompactMonitorMode: what the notch or capsule shows of the system, after ftop's menu bar strip and corner badges.
// Full is every reading as a strip; Brief is three ring gauges; Custom is the readings chosen in CompactMonitorItems.
// Since the table of readings (CompactReading) these are its presets: Full and Brief set which readings show and which rings
// carry their figure, and Custom is any other setting.
public enum CompactMonitorMode { Full, Brief, Custom }

[Flags]
public enum CompactMonitorItems
{
    None = 0, Columns = 1, Cpu = 2, Memory = 4, Gpu = 8, Download = 16, Upload = 32,
    All = Columns | Cpu | Memory | Gpu | Download | Upload
}

// One reading of the collapsed strip as set in the settings' table; its place there is its place in the strip, left to right.
// ShowValue: a dial (CPU, memory, GPU) shows its percentage after it, or, off, the dial alone with its letter inside, which is
// narrower; the core columns carry no figure and the rates are always text, so it means nothing for them.
// Color: "#RRGGBB" in place of the palette's color in the reading's normal state, or null to follow the palette.
// ExtraColors: the same for the reading's further channels in the normal state (CompactMonitor.Channels: the core columns'
// slowest kind of core), by position; null or missing follows the palette.
// WarningColor, CriticalColor: the same at the warning and critical levels (ReadingLevels); null follows the palette's
// warning and critical colors.
public sealed class CompactReading
{
    public CompactMonitorItems Item { get; set; }
    public bool Visible { get; set; } = true;
    public bool ShowValue { get; set; } = true;
    public string? Color { get; set; }
    public List<string?>? ExtraColors { get; set; }
    public string? WarningColor { get; set; }
    public string? CriticalColor { get; set; }
    // The reading's own color at a level (and channel, in the normal state), or null to follow the palette.
    public string? ColorAt(MemoryLevel level, int channel = 0) => level switch
    {
        MemoryLevel.Critical => CriticalColor, MemoryLevel.Elevated => WarningColor,
        _ => channel <= 0 ? Color : ExtraColors?.ElementAtOrDefault(channel - 1)
    };
    public void SetColor(MemoryLevel level, string? color, int channel = 0)
    {
        if (level == MemoryLevel.Critical) CriticalColor = color;
        else if (level == MemoryLevel.Elevated) WarningColor = color;
        else if (channel <= 0) Color = color;
        else
        {
            var extra = ExtraColors ?? [];
            while (extra.Count < channel) extra.Add(null);
            extra[channel - 1] = color;
            ExtraColors = extra.Any(own => own is not null) ? extra : null;
        }
    }
    // Every color back to the palette's.
    public void ResetColors() { Color = WarningColor = CriticalColor = null; ExtraColors = null; }
    public CompactReading Copy()
    {
        var copy = (CompactReading)MemberwiseClone();
        copy.ExtraColors = ExtraColors?.ToList();
        return copy;
    }
}

// What the collapsed row shows of the system and how it gives way when the row is short: whole readings leave one by one,
// the least telling first, so what stays is never cut through.
public static class CompactMonitor
{
    // Left to right by default; the settings' table can reorder them.
    public static readonly IReadOnlyList<CompactMonitorItems> Order =
        [CompactMonitorItems.Columns, CompactMonitorItems.Cpu, CompactMonitorItems.Memory, CompactMonitorItems.Gpu, CompactMonitorItems.Download, CompactMonitorItems.Upload];
    // When the row is too short, readings leave in this order.
    public static readonly IReadOnlyList<CompactMonitorItems> LeaveOrder =
        [CompactMonitorItems.Upload, CompactMonitorItems.Gpu, CompactMonitorItems.Download, CompactMonitorItems.Columns, CompactMonitorItems.Memory, CompactMonitorItems.Cpu];
    // The same order reversed: the order in which the collapsed row takes readings while they fit (CompactMusicLayout).
    public static readonly IReadOnlyList<CompactMonitorItems> Priority = [.. LeaveOrder.Reverse()];

    // The first count of the chosen items in Priority order.
    public static CompactMonitorItems First(CompactMonitorItems items, int count)
    {
        var taken = CompactMonitorItems.None;
        foreach (var item in Priority)
        {
            if (count <= 0) break;
            if ((items & item) == 0) continue;
            taken |= item; count--;
        }
        return taken;
    }

    // The readings with a figure that can be hidden.
    public const CompactMonitorItems Rings = CompactMonitorItems.Cpu | CompactMonitorItems.Memory | CompactMonitorItems.Gpu;
    public static bool HasValueChoice(CompactMonitorItems item) => item is CompactMonitorItems.Cpu or CompactMonitorItems.Memory or CompactMonitorItems.Gpu;
    // The colors a reading has in its normal state: the core columns one for the fastest and one for the slowest kind of core,
    // the others one. Every reading has besides a warning and a critical color (ReadingLevels).
    public static int Channels(CompactMonitorItems item) => PaletteColors.TryGetValue(item, out var colors) ? colors.Length : 1;
    // Which of the color scheme's three main colors (MonitorColors.Main: 0 performance, 1 efficiency, 2 GPU) each channel of a
    // reading takes by default, so the three stand for CPU, memory and GPU; the core columns and the rates take the first two.
    public static readonly IReadOnlyDictionary<CompactMonitorItems, int[]> PaletteColors = new Dictionary<CompactMonitorItems, int[]>
    {
        [CompactMonitorItems.Columns] = [0, 1], [CompactMonitorItems.Cpu] = [0], [CompactMonitorItems.Memory] = [1],
        [CompactMonitorItems.Gpu] = [2], [CompactMonitorItems.Download] = [0], [CompactMonitorItems.Upload] = [1]
    };
    // Every color of the table back to the palette's; which readings show, their figures and their order stay.
    public static void ResetColors(IEnumerable<CompactReading> readings) { foreach (var reading in readings) reading.ResetColors(); }

    // What an old mode showed (settings saved before the table; see Readings).
    public static CompactMonitorItems Items(CompactMonitorMode mode, CompactMonitorItems custom) => mode switch
    {
        CompactMonitorMode.Full => CompactMonitorItems.All,
        CompactMonitorMode.Brief => CompactMonitorItems.Cpu | CompactMonitorItems.Memory | CompactMonitorItems.Gpu,
        _ => (custom & CompactMonitorItems.All) is var chosen && chosen != CompactMonitorItems.None ? chosen : CompactMonitorItems.Cpu
    };

    // The table for settings saved before it: Full shows every reading with its figure, Brief the three rings alone, Custom the
    // chosen readings with their figures. All in the default order, following the palette.
    public static List<CompactReading> Readings(CompactMonitorMode mode, CompactMonitorItems custom)
    {
        var shown = Items(mode, custom);
        return [.. Order.Select(item => new CompactReading { Item = item, Visible = (shown & item) != 0, ShowValue = mode != CompactMonitorMode.Brief })];
    }

    // Every reading once, in the saved order: unknown and repeated readings are dropped, missing ones join hidden at the end,
    // a color that is not "#RRGGBB" follows the palette, and colors for channels a reading does not have are dropped.
    public static List<CompactReading> Normalize(IEnumerable<CompactReading?>? readings)
    {
        static string? Valid(string? color) => AppPresets.NormalizeColor(color) is { Length: > 0 } valid ? valid : null;
        static List<string?>? Extra(CompactReading reading)
        {
            var extra = Enumerable.Range(0, Channels(reading.Item) - 1).Select(index => Valid(reading.ExtraColors?.ElementAtOrDefault(index))).ToList();
            return extra.Any(color => color is not null) ? extra : null;
        }
        var list = (readings ?? []).OfType<CompactReading>().Where(reading => Order.Contains(reading.Item)).DistinctBy(reading => reading.Item)
            .Select(reading => new CompactReading { Item = reading.Item, Visible = reading.Visible, ShowValue = reading.ShowValue, Color = Valid(reading.Color),
                ExtraColors = Extra(reading), WarningColor = Valid(reading.WarningColor), CriticalColor = Valid(reading.CriticalColor) }).ToList();
        list.AddRange(Order.Where(item => list.All(reading => reading.Item != item)).Select(item => new CompactReading { Item = item, Visible = false }));
        return list;
    }

    public static CompactMonitorItems Visible(IEnumerable<CompactReading> readings) =>
        readings.Where(reading => reading.Visible).Aggregate(CompactMonitorItems.None, (items, reading) => items | reading.Item) & CompactMonitorItems.All;
    // The rings set to show their figure, shown or not.
    public static CompactMonitorItems Values(IEnumerable<CompactReading> readings) =>
        readings.Where(reading => reading.ShowValue).Aggregate(CompactMonitorItems.None, (items, reading) => items | reading.Item) & Rings;
    // What the strip shows: with every reading switched off, CPU, as the old Custom choice did.
    public static CompactMonitorItems Shown(IEnumerable<CompactReading> readings) => Visible(readings) is var shown && shown != CompactMonitorItems.None ? shown : CompactMonitorItems.Cpu;

    // The preset the table matches exactly: Full is every reading with the rings' figures, Brief the three rings alone; else
    // none, and the table itself is the custom choice (saved as Custom in the old key).
    public static CompactMonitorMode? Preset(IEnumerable<CompactReading> readings)
    {
        var all = readings.ToList();
        var (shown, values) = (Visible(all), Values(all));
        return shown == CompactMonitorItems.All && values == Rings ? CompactMonitorMode.Full
            : shown == Rings && values == CompactMonitorItems.None ? CompactMonitorMode.Brief : null;
    }

    // Sets which readings show and which rings carry their figure as the preset has them; the order and the colors stay.
    // Custom (no preset) changes nothing.
    public static void Apply(IEnumerable<CompactReading> readings, CompactMonitorMode preset)
    {
        if (preset == CompactMonitorMode.Custom) return;
        foreach (var reading in readings)
        {
            bool ring = HasValueChoice(reading.Item);
            reading.Visible = preset == CompactMonitorMode.Full || ring;
            if (ring) reading.ShowValue = preset == CompactMonitorMode.Full;
        }
    }

    // The width of each reading: a ring with its figure (the ring and the room after it) or alone, the core columns, a rate.
    public static Func<CompactMonitorItems, double> Widths(CompactMonitorItems values, double ring, double figure, double bareRing, double columns, double rate) => item => item switch
    {
        CompactMonitorItems.Columns => columns,
        CompactMonitorItems.Cpu or CompactMonitorItems.Memory or CompactMonitorItems.Gpu => (values & item) != 0 ? ring + figure : bareRing,
        CompactMonitorItems.Download or CompactMonitorItems.Upload => rate,
        _ => 0
    };
    // Rings alone, as Brief showed them, sit closer together.
    public static bool RingsOnly(CompactMonitorItems shown, CompactMonitorItems values) => shown != CompactMonitorItems.None && (shown & ~Rings) == 0 && (shown & values) == 0;

    // The shown readings left to right in the order given, each with its left edge from the start of the strip. Each takes its
    // width and the gap after it in proportion to how present it is (1 when settled), so the row closes up smoothly as one
    // leaves; Total is the width they take together.
    public static (List<(CompactMonitorItems Item, double X, double Width, double Presence)> Items, double Total) Arrange(IEnumerable<CompactMonitorItems> order,
        CompactMonitorItems shown, Func<CompactMonitorItems, double> width, double gap, Func<CompactMonitorItems, double> presence)
    {
        var items = new List<(CompactMonitorItems, double, double, double)>();
        double x = 0, last = 0;
        foreach (var item in order.Distinct())
        {
            if ((shown & item) == 0 || !Order.Contains(item)) continue;
            double p = Math.Clamp(presence(item), 0, 1), w = Math.Max(0, width(item));
            if (p <= 0) continue;
            items.Add((item, x, w, p));
            x += (w + gap) * p; last = p;
        }
        return (items, Math.Max(0, x - gap * last));
    }

    // The width of the items side by side with this gap between them.
    public static double Width(CompactMonitorItems items, Func<CompactMonitorItems, double> width, double gap)
    {
        double total = 0; int count = 0;
        foreach (var item in Order)
        {
            if ((items & item) == 0) continue;
            total += Math.Max(0, width(item)); count++;
        }
        return count == 0 ? 0 : total + (count - 1) * Math.Max(0, gap);
    }

    // The items that fit in the space, dropping whole readings in LeaveOrder; None when not even one fits.
    public static CompactMonitorItems Fit(CompactMonitorItems items, Func<CompactMonitorItems, double> width, double gap, double available)
    {
        if (!double.IsFinite(available) || available <= 0) return CompactMonitorItems.None;
        var shown = items & CompactMonitorItems.All;
        foreach (var item in LeaveOrder)
        {
            if (Width(shown, width, gap) <= available + 1e-9) return shown;
            shown &= ~item;
        }
        return Width(shown, width, gap) <= available + 1e-9 ? shown : CompactMonitorItems.None;
    }
}

// The colors of the collapsed readings at each level (ReadingLevels): a reading's own color where one is set, else the
// palette's. The normal state has a color per channel (CompactMonitor.Channels: the core columns one for the fastest and one
// for the slowest kind of core); the warning and critical states one each, the palette's warning and critical colors unless
// the reading sets its own.
public sealed class StripColors
{
    private readonly Dictionary<CompactMonitorItems, CompactReading> _own;
    public MonitorColors Palette { get; }

    // The readings' own colors, each as drawn (the app moves them to read on the panel theme; by default as given).
    public StripColors(MonitorColors palette, IEnumerable<CompactReading>? readings = null, Func<string, string>? drawn = null)
    {
        Palette = palette;
        string? Draw(string? color) => color is { Length: > 0 } ? drawn is null ? color : drawn(color) : null;
        _own = (readings ?? []).DistinctBy(reading => reading.Item).ToDictionary(reading => reading.Item, reading => new CompactReading
        {
            Item = reading.Item, Color = Draw(reading.Color), ExtraColors = reading.ExtraColors?.Select(Draw).ToList(),
            WarningColor = Draw(reading.WarningColor), CriticalColor = Draw(reading.CriticalColor)
        });
    }

    // The palette's color of each reading's channel in its normal state.
    public string Default(CompactMonitorItems item, int channel = 0) =>
        Palette.Main(CompactMonitor.PaletteColors.TryGetValue(item, out var colors) ? colors[Math.Clamp(channel, 0, colors.Length - 1)] : 0);
    // The palette's color of a reading at a level: its channel's normal one, or the palette's warning or critical color.
    public string DefaultAt(CompactMonitorItems item, MemoryLevel level, int channel = 0) => level == MemoryLevel.Normal ? Default(item, channel) : Palette.Memory(level);

    private string? Own(CompactMonitorItems item, MemoryLevel level, int channel) => _own.TryGetValue(item, out var reading) ? reading.ColorAt(level, channel) : null;
    public string Normal(CompactMonitorItems item, int channel = 0) => Own(item, MemoryLevel.Normal, channel) ?? Default(item, channel);
    // A reading's color at a level: its own color for that level (and channel, in the normal state) where set, else the palette's.
    public string At(CompactMonitorItems item, MemoryLevel level, int channel = 0) => Own(item, level, channel) ?? DefaultAt(item, level, channel);
    public string Memory(MemoryLevel level) => At(CompactMonitorItems.Memory, level);

    // A core column's color. In the normal state the kinds of core run from the first color, for the fastest kind, to the
    // second in even steps; with only the first set, part of the way to the palette's slowest-core color. At the warning and
    // critical levels a core takes the reading's color for that level.
    public string Core(int group, int groups, MemoryLevel level = MemoryLevel.Normal)
    {
        if (level != MemoryLevel.Normal) return At(CompactMonitorItems.Columns, level);
        double step = groups > 1 ? Math.Clamp(group, 0, groups - 1) / (double)(groups - 1) : 0;
        string? fast = Own(CompactMonitorItems.Columns, MemoryLevel.Normal, 0), slow = Own(CompactMonitorItems.Columns, MemoryLevel.Normal, 1);
        if (slow is not null) return ColorMath.Mix(Normal(CompactMonitorItems.Columns), slow, step);
        return fast is not null ? ColorMath.Mix(fast, Palette.Efficiency, .45 * step) : Palette.Core(group, groups);
    }
}

// Widths on whole device pixels, for a DIP width and the device pixels per DIP (1.5 at 150 %). The collapsed row rounds its
// regions down (Down); the readings ask for their width rounded up (Up), so that rounding never takes a reading away.
public static class DevicePixels
{
    public static double Down(double value, double dpi) => double.IsFinite(value) && value > 0 && dpi > 0 ? Math.Floor(value * dpi + .001) / dpi : 0;
    public static double Up(double value, double dpi) => !double.IsFinite(value) || value <= 0 ? 0 : double.IsFinite(dpi) && dpi > 0 ? Math.Ceiling(value * dpi - .001) / dpi : value;
}
