namespace BrimDeck.Core;

// The system monitor page's readings. The model, the rule that a missing reading is never shown as zero, and the folding
// of an app's processes into one entry follow ftop by Nongfsq (https://github.com/Nongfsq/ftop, MIT License; see
// ThirdPartyNotices.txt), adapted to what Windows reports.

// A reading that is either known or unavailable for a stated reason. The page shows a dash for an unavailable reading,
// with the reason as its tooltip, and never a made-up zero.
public readonly struct Reading<T>
{
    private readonly T _value;
    private readonly string? _reason;
    public bool HasValue { get; }
    public string Reason => HasValue ? "" : _reason ?? "not read yet";
    private Reading(T value, bool known, string? reason) { _value = value; HasValue = known; _reason = reason; }
    public static Reading<T> Of(T value) => new(value, true, null);
    public static Reading<T> Unavailable(string reason) => new(default!, false, reason);
    public bool TryGet(out T value) { value = _value; return HasValue; }
    public T? OrDefault => HasValue ? _value : default;
    public Reading<TResult> Map<TResult>(Func<T, TResult> map) => HasValue ? Reading<TResult>.Of(map(_value)) : Reading<TResult>.Unavailable(Reason);
    public static implicit operator Reading<T>(T value) => Of(value);
}

// What Windows calls a core, from its efficiency class. A processor with one class has Unknown cores.
public enum CoreTier { Performance, Efficiency, Unknown }

// One logical processor. Group 0 holds the fastest kind of core, counting up to the slowest; Number is its position within
// its group, from 1. Usage is the 0..1 busy share of the last interval.
public sealed record CoreSample(int Id, int Group, CoreTier Tier, int Number, double Usage, Reading<double> FrequencyMhz, double? MaxFrequencyMhz)
{
    // 0..1 position of the current frequency within the core's range.
    public double? FrequencyFraction => FrequencyMhz.TryGet(out var mhz) && MaxFrequencyMhz is { } top && top > 0 ? Math.Clamp(mhz / top, 0, 1) : null;
}

public sealed record CpuSample(IReadOnlyList<CoreSample> Cores, Reading<double> FrequencyMhz)
{
    public double Usage => Cores.Count == 0 ? 0 : Cores.Average(core => core.Usage);
    public int GroupCount => Cores.Count == 0 ? 0 : Cores.Max(core => core.Group) + 1;
    // The cores of each group, fastest group first.
    public IReadOnlyList<IReadOnlyList<CoreSample>> Groups =>
        Enumerable.Range(0, GroupCount).Select(group => (IReadOnlyList<CoreSample>)Cores.Where(core => core.Group == group).ToList()).Where(list => list.Count > 0).ToList();
}

// Windows reports no memory pressure level as macOS does; the level is read from the share of physical memory in use and
// from the commit charge, the memory promised to processes, against its limit (physical memory plus the page file).
public enum MemoryLevel { Normal, Elevated, Critical }

public sealed record MemorySample(ulong Total, ulong Used, ulong CommitUsed, ulong CommitLimit)
{
    public double UsedFraction => Total > 0 ? Math.Clamp((double)Used / Total, 0, 1) : 0;
    public double CommitFraction => CommitLimit > 0 ? Math.Clamp((double)CommitUsed / CommitLimit, 0, 1) : 0;
    public MemoryLevel Level => UsedFraction >= .92 || CommitFraction >= .9 ? MemoryLevel.Critical
        : UsedFraction >= .8 || CommitFraction >= .8 ? MemoryLevel.Elevated : MemoryLevel.Normal;
}

public sealed record NetworkSample(double DownBytesPerSecond, double UpBytesPerSecond);

// The busiest graphics adapter, read as Task Manager reads it: its busiest engine type, the processes' shares summed.
public sealed record GpuSample(double Usage, Reading<ulong> DedicatedBytes);

// A process folded into an app's entry. CpuPercent is percent of one core, like ProcessSample's.
public sealed record ProcessMember(string Name, double CpuPercent, ulong MemoryBytes);

public enum ProcessSort { Cpu, Memory }

// A row of the process list: one process, or an app with its other processes folded in.
// CpuPercent is percent of one core as it is measured (250 is two and a half cores); the page shows it as a share of the
// whole machine (MonitorFormat.ProcessShare), so an app never passes 100 %. MemoryBytes is the private working set, the
// figure Task Manager shows. AppKey identifies the executable an entry folds by; null keeps the process on its own.
public sealed record ProcessSample(int Pid, string Name, double CpuPercent, ulong MemoryBytes, string? AppKey = null, string? Path = null)
{
    public IReadOnlyList<ProcessMember> Members { get; init; } = [];
    // Stable while the entry stays in the list: the app, or the process.
    public string Id => AppKey ?? "pid " + Pid;

    // The list with every app's processes folded into one entry that carries their sums and the app's name, ranked by sort.
    // Only what is in the list is summed.
    public static IReadOnlyList<ProcessSample> Folded(IEnumerable<ProcessSample> list, ProcessSort sort)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<ProcessSample>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in list)
        {
            if (!groups.TryGetValue(sample.Id, out var group)) { groups[sample.Id] = group = []; order.Add(sample.Id); }
            group.Add(sample);
        }
        int Heavier(ProcessSample first, ProcessSample second) => sort == ProcessSort.Cpu && first.CpuPercent != second.CpuPercent
            ? second.CpuPercent.CompareTo(first.CpuPercent) : second.MemoryBytes.CompareTo(first.MemoryBytes);
        var entries = order.Select(key =>
        {
            var group = groups[key];
            group.Sort(Heavier);
            var head = group[0];
            if (head.AppKey is null || group.Count == 1) return head;
            return head with
            {
                CpuPercent = group.Sum(sample => sample.CpuPercent), MemoryBytes = (ulong)group.Sum(sample => (decimal)sample.MemoryBytes),
                Members = group.Select(sample => new ProcessMember(sample.Name, sample.CpuPercent, sample.MemoryBytes)).ToList()
            };
        }).ToList();
        entries.Sort(Heavier);
        return entries;
    }
}

// Top is ranked by processor use, ByMemory by memory, both folded.
public sealed record ProcessList(IReadOnlyList<ProcessSample> Top, IReadOnlyList<ProcessSample> ByMemory)
{
    public IReadOnlyList<ProcessSample> Ranked(ProcessSort sort) => sort == ProcessSort.Memory ? ByMemory : Top;
}

public sealed record MonitorSnapshot(DateTimeOffset Time, int LogicalProcessors, Reading<CpuSample> Cpu, Reading<MemorySample> Memory,
    Reading<NetworkSample> Network, Reading<GpuSample> Gpu, Reading<ProcessList> Processes)
{
    public static MonitorSnapshot Empty(int logicalProcessors, string reason) => new(DateTimeOffset.Now, logicalProcessors,
        Reading<CpuSample>.Unavailable(reason), Reading<MemorySample>.Unavailable(reason), Reading<NetworkSample>.Unavailable(reason),
        Reading<GpuSample>.Unavailable(reason), Reading<ProcessList>.Unavailable(reason));
}

// Deltas between two cumulative counters. Each returns null when no time passed or a counter went backwards, as after
// sleep or when an interface went away, so a reading is skipped rather than shown wrong.
public static class MonitorMetrics
{
    // Busy share of a processor between two readings of its idle, kernel and user times. Windows counts idle time inside
    // kernel time.
    public static double? Usage((long Idle, long Kernel, long User) previous, (long Idle, long Kernel, long User) current)
    {
        long idle = current.Idle - previous.Idle, total = current.Kernel - previous.Kernel + current.User - previous.User;
        if (idle < 0 || total <= 0 || idle > total * 1.05) return null;
        return Math.Clamp(1 - (double)idle / total, 0, 1);
    }
    // Bytes per second between two cumulative counters.
    public static double? Rate(ulong previous, ulong current, double seconds)
        => seconds > 0 && current >= previous ? (current - previous) / seconds : null;
    // Percent of one core used between two readings of a process's processor time, in 100 ns units.
    public static double? CpuPercent(long previous, long current, double seconds)
        => seconds > 0 && current >= previous ? (current - previous) / (seconds * 10_000_000) * 100 : null;
}
