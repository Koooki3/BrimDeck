namespace BrimDeck.Core;

// The readings the expanded system monitor page can show, one table of them (ExpandedReading) in the order the page stacks them.
// Saved as numbers, so new members go at the end. Cpu and Gpu are the gauges with their columns (the one chosen in MonitorMain is
// shown large when both are shown, a lone one is shown large), Memory, Download and Upload the readings under them (the two rates share one
// block of one line, side by side or alone; with memory they are shown side by side, one to a line or as blocks as room allows), Frames, Storage and Power the lines under those (Power is the whole machine's
// power draw with the machine's own battery beside it), Processes the list of the busiest apps. The temperatures are not an
// item: they are a detail of Cpu, Gpu and Storage (DeckSettings.MonitorTemperatures).
public enum ExpandedItem { Cpu, Gpu, Memory, Download, Frames, Storage, Power, Processes, Upload }

// One item of the table as set in the settings: whether the page shows it. Its place in the table is its place on the page.
public sealed class ExpandedReading
{
    public ExpandedItem Item { get; set; }
    public bool Visible { get; set; } = true;
    public ExpandedReading Copy() => (ExpandedReading)MemberwiseClone();
}

// What the sampler must read for the page as laid out; nothing else is read for it.
[Flags]
public enum ExpandedDemand
{
    None = 0, Cpu = 1, Gpu = 2, Memory = 4, Network = 8, Frames = 16, Storage = 32, Power = 64, Processes = 128,
    // The processor's, the graphics adapter's and the drives' temperatures; only asked for with a reading that carries one.
    Temperatures = 256
}

// The quick sets of the settings page: every reading the page can show (the frame rate, which asks for permission, stays as it is);
// CPU, memory and the network; CPU and memory; nothing. They set the table's items, not the process list, which has its own switch.
public enum ExpandedPreset { All, Brief, CpuMemory, None }

public static class ExpandedMonitor
{
    // The default order, top to bottom: the page as it was before the table.
    public static readonly IReadOnlyList<ExpandedItem> Order =
        [ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Frames, ExpandedItem.Storage, ExpandedItem.Power, ExpandedItem.Processes];

    // The table for settings saved before it: the four switches the page had (the process list, the drives, power with the battery,
    // the frame rate) say which of those show; CPU, GPU, memory and network always showed. In the default order.
    public static List<ExpandedReading> Defaults(bool processes = true, bool storage = true, bool power = true, bool frames = false)
        => [.. Order.Select(item => new ExpandedReading
        {
            Item = item,
            Visible = item switch { ExpandedItem.Processes => processes, ExpandedItem.Storage => storage, ExpandedItem.Power => power, ExpandedItem.Frames => frames, _ => true }
        })];

    // Every item once, in the saved order: unknown and repeated items are dropped, missing ones join at the end as the default
    // table has them. Unlike the collapsed row there is no reading that must stay: a table with nothing shown is kept as it is
    // (the page then says there is nothing to show).
    public static List<ExpandedReading> Normalize(IEnumerable<ExpandedReading?>? readings)
    {
        var list = (readings ?? []).OfType<ExpandedReading>().Where(reading => Enum.IsDefined(reading.Item)).DistinctBy(reading => reading.Item)
            .Select(reading => new ExpandedReading { Item = reading.Item, Visible = reading.Visible }).ToList();
        // Upload came after the table was made (the network was one item, Download's number): a table without it has it follow Download,
        // shown as Download is, so the network shows as it did.
        if (list.All(reading => reading.Item != ExpandedItem.Upload) && list.FindIndex(reading => reading.Item == ExpandedItem.Download) is >= 0 and var at)
            list.Insert(at + 1, new ExpandedReading { Item = ExpandedItem.Upload, Visible = list[at].Visible });
        list.AddRange(Defaults().Where(item => list.All(reading => reading.Item != item.Item)));
        return list;
    }

    // The shown items in the table's order.
    public static List<ExpandedItem> Shown(IEnumerable<ExpandedReading> readings) => [.. readings.Where(reading => reading.Visible).Select(reading => reading.Item).Distinct()];

    // The four switches older versions read, as the table has them: (processes, storage, power, frames).
    public static (bool Processes, bool Storage, bool Power, bool Frames) Switches(IEnumerable<ExpandedReading> readings)
    {
        var shown = Shown(readings);
        return (shown.Contains(ExpandedItem.Processes), shown.Contains(ExpandedItem.Storage), shown.Contains(ExpandedItem.Power), shown.Contains(ExpandedItem.Frames));
    }

    // What to read for a selection, before the page is laid out (an upper bound: ExpandedPlan.Demand is what the layout kept).
    public static ExpandedDemand Demand(IEnumerable<ExpandedItem> shown, bool temperatures)
    {
        var demand = ExpandedDemand.None;
        foreach (var item in shown.Distinct())
            demand |= item switch
            {
                ExpandedItem.Cpu => ExpandedDemand.Cpu, ExpandedItem.Gpu => ExpandedDemand.Gpu, ExpandedItem.Memory => ExpandedDemand.Memory,
                ExpandedItem.Download or ExpandedItem.Upload => ExpandedDemand.Network, ExpandedItem.Frames => ExpandedDemand.Frames, ExpandedItem.Storage => ExpandedDemand.Storage,
                ExpandedItem.Power => ExpandedDemand.Power, ExpandedItem.Processes => ExpandedDemand.Processes, _ => ExpandedDemand.None
            };
        return temperatures && (demand & (ExpandedDemand.Cpu | ExpandedDemand.Gpu | ExpandedDemand.Storage)) != 0 ? demand | ExpandedDemand.Temperatures : demand;
    }

    // The items the table lists in the settings (the process list has a switch of its own).
    public static readonly IReadOnlyList<ExpandedItem> Listed = [.. Order.Where(item => item != ExpandedItem.Processes)];

    private static bool Wanted(ExpandedPreset preset, ExpandedItem item) => preset switch
    {
        ExpandedPreset.All => item != ExpandedItem.Frames,
        ExpandedPreset.Brief => item is ExpandedItem.Cpu or ExpandedItem.Memory or ExpandedItem.Download or ExpandedItem.Upload,
        ExpandedPreset.CpuMemory => item is ExpandedItem.Cpu or ExpandedItem.Memory,
        _ => false
    };

    // Sets which listed items show as the preset has them; the order, the process list and (for All) the frame rate stay.
    public static void Apply(IEnumerable<ExpandedReading> readings, ExpandedPreset preset)
    {
        foreach (var reading in readings)
        {
            if (reading.Item == ExpandedItem.Processes || preset == ExpandedPreset.All && reading.Item == ExpandedItem.Frames) continue;
            reading.Visible = Wanted(preset, reading.Item);
        }
    }

    // The preset the table matches exactly, else none (the table is then the user's own choice).
    public static ExpandedPreset? Preset(IEnumerable<ExpandedReading> readings)
    {
        var list = readings.Where(reading => reading.Item != ExpandedItem.Processes).ToList();
        foreach (var preset in Enum.GetValues<ExpandedPreset>())
            if (list.All(reading => preset == ExpandedPreset.All && reading.Item == ExpandedItem.Frames || reading.Visible == Wanted(preset, reading.Item))) return preset;
        return null;
    }
}
