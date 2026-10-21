namespace BrimDeck.Core;

// The system monitor's choices beyond its colors. All are saved as numbers, so new members go at the end.
// MonitorMain: the gauge the page shows large; the other one shows small beside it, and a click swaps them.
public enum MonitorMain { Cpu, Gpu }
// MonitorProcessView: Auto shows the detailed rows (CPU, memory and the number of processes) where the list is wide enough.
public enum MonitorProcessView { Auto, Compact, Detailed }
// CompactMonitorMode: what the notch or capsule shows of the system, after ftop's menu bar strip and corner badges.
// Full is every reading as a strip; Brief is three ring gauges; Custom is the readings chosen in CompactMonitorItems.
public enum CompactMonitorMode { Full, Brief, Custom }

[Flags]
public enum CompactMonitorItems
{
    None = 0, Columns = 1, Cpu = 2, Memory = 4, Gpu = 8, Download = 16, Upload = 32,
    All = Columns | Cpu | Memory | Gpu | Download | Upload
}

// What the collapsed row shows of the system and how it gives way when the row is short: whole readings leave one by one,
// the least telling first, so what stays is never cut through.
public static class CompactMonitor
{
    // Left to right.
    public static readonly IReadOnlyList<CompactMonitorItems> Order =
        [CompactMonitorItems.Columns, CompactMonitorItems.Cpu, CompactMonitorItems.Memory, CompactMonitorItems.Gpu, CompactMonitorItems.Download, CompactMonitorItems.Upload];
    // When the row is too short, readings leave in this order.
    public static readonly IReadOnlyList<CompactMonitorItems> LeaveOrder =
        [CompactMonitorItems.Upload, CompactMonitorItems.Gpu, CompactMonitorItems.Download, CompactMonitorItems.Columns, CompactMonitorItems.Memory, CompactMonitorItems.Cpu];

    public static CompactMonitorItems Items(CompactMonitorMode mode, CompactMonitorItems custom) => mode switch
    {
        CompactMonitorMode.Full => CompactMonitorItems.All,
        CompactMonitorMode.Brief => CompactMonitorItems.Cpu | CompactMonitorItems.Memory | CompactMonitorItems.Gpu,
        _ => (custom & CompactMonitorItems.All) is var chosen && chosen != CompactMonitorItems.None ? chosen : CompactMonitorItems.Cpu
    };

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
