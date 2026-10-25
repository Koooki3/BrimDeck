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
    // By index, as Average and Max add up: both are read on every redraw of the collapsed readings, which allocate nothing.
    public double Usage
    {
        get
        {
            if (Cores.Count == 0) return 0;
            double sum = 0;
            for (int i = 0; i < Cores.Count; i++) sum += Cores[i].Usage;
            return sum / Cores.Count;
        }
    }
    public int GroupCount
    {
        get
        {
            if (Cores.Count == 0) return 0;
            int most = int.MinValue;
            for (int i = 0; i < Cores.Count; i++) most = Math.Max(most, Cores[i].Group);
            return most + 1;
        }
    }
    // The cores of each group, fastest group first.
    public IReadOnlyList<IReadOnlyList<CoreSample>> Groups =>
        Enumerable.Range(0, GroupCount).Select(group => (IReadOnlyList<CoreSample>)Cores.Where(core => core.Group == group).ToList()).Where(list => list.Count > 0).ToList();
}

// Windows reports no memory pressure level as macOS does; the level is read from the share of physical memory in use and
// from the commit charge, the memory promised to processes, against its limit (physical memory plus the page file).
public enum MemoryLevel { Normal, Elevated, Critical }

// Cached is the system file cache (standby and modified pages): filled, but given to any app that asks, so Task Manager counts
// it as available. The page draws it after the memory in use, lighter.
public sealed record MemorySample(ulong Total, ulong Used, ulong CommitUsed, ulong CommitLimit, ulong Cached = 0)
{
    // The levels' thresholds: the share of physical memory in use, or the commit charge against its limit.
    // Memory in use is read as every share is (ReadingLevels).
    public const double ElevatedUsed = ReadingLevels.ElevatedShare, ElevatedCommit = .8, CriticalUsed = ReadingLevels.CriticalShare, CriticalCommit = .9;
    public double CachedFraction => Total > 0 ? Math.Clamp((double)Math.Min(Cached, Total - Math.Min(Used, Total)) / Total, 0, 1) : 0;
    public double UsedFraction => Total > 0 ? Math.Clamp((double)Used / Total, 0, 1) : 0;
    public double CommitFraction => CommitLimit > 0 ? Math.Clamp((double)CommitUsed / CommitLimit, 0, 1) : 0;
    public MemoryLevel Level => UsedFraction >= CriticalUsed || CommitFraction >= CriticalCommit ? MemoryLevel.Critical
        : UsedFraction >= ElevatedUsed || CommitFraction >= ElevatedCommit ? MemoryLevel.Elevated : MemoryLevel.Normal;
}

public sealed record NetworkSample(double DownBytesPerSecond, double UpBytesPerSecond);

// The level of each reading of the collapsed strip, by one rule per kind of reading: a share (CPU, GPU, each core column,
// memory in use) against ElevatedShare and CriticalShare, memory also by its commit charge (MemorySample.Level), and a rate
// against fixed rates. A rate has no share: Windows reports an adapter's link speed, but that is the nominal speed of the
// link (a Wi-Fi rate, or 1 Gb/s on a line that delivers far less), so a share of it would hardly ever rise.
public static class ReadingLevels
{
    public const double ElevatedShare = .8, CriticalShare = .92;
    // Bytes per second in decimal units, as the rates are shown (MonitorFormat.Rate): 10 MB/s and 50 MB/s.
    public const double ElevatedRate = 10_000_000, CriticalRate = 50_000_000;

    public static MemoryLevel OfShare(double share) => share >= CriticalShare ? MemoryLevel.Critical : share >= ElevatedShare ? MemoryLevel.Elevated : MemoryLevel.Normal;
    public static MemoryLevel OfRate(double bytesPerSecond) =>
        bytesPerSecond >= CriticalRate ? MemoryLevel.Critical : bytesPerSecond >= ElevatedRate ? MemoryLevel.Elevated : MemoryLevel.Normal;

    // A reading's level as the strip shows it, Normal while it is unavailable. The core columns as a whole take the busiest
    // core's; the strip colors each column by its own core (OfShare).
    public static MemoryLevel Of(CompactMonitorItems item, MonitorSnapshot? snapshot) => item switch
    {
        CompactMonitorItems.Columns => snapshot?.Cpu.OrDefault is { Cores.Count: > 0 } cores ? OfShare(Busiest(cores.Cores)) : MemoryLevel.Normal,
        CompactMonitorItems.Cpu => snapshot?.Cpu.OrDefault is { } cpu ? OfShare(cpu.Usage) : MemoryLevel.Normal,
        CompactMonitorItems.Memory => snapshot?.Memory.OrDefault?.Level ?? MemoryLevel.Normal,
        CompactMonitorItems.Gpu => snapshot?.Gpu.OrDefault is { } gpu ? OfShare(gpu.Usage) : MemoryLevel.Normal,
        CompactMonitorItems.Download => snapshot?.Network.OrDefault is { } down ? OfRate(down.DownBytesPerSecond) : MemoryLevel.Normal,
        CompactMonitorItems.Upload => snapshot?.Network.OrDefault is { } up ? OfRate(up.UpBytesPerSecond) : MemoryLevel.Normal,
        _ => MemoryLevel.Normal
    };

    // The busiest core's share, by index: asked on every redraw of the collapsed readings, which allocate nothing for it.
    private static double Busiest(IReadOnlyList<CoreSample> cores)
    {
        // As Enumerable.Max: a share that is not a number is passed over.
        double most = double.NaN;
        for (int i = 0; i < cores.Count; i++) if (double.IsNaN(most) || cores[i].Usage > most) most = cores[i].Usage;
        return most;
    }
}

// One kind of engine of a graphics adapter (3D, Copy, VideoDecode, ...) with the shares of all processes summed, 0..1.
public sealed record GpuEngine(string Kind, double Usage);

// The busiest graphics adapter, read as Task Manager reads it: its busiest engine type, the processes' shares summed. Engines are
// its kinds of engine in a fixed order (GpuEngines.Order); Name and DedicatedTotal come from the adapter's description and are
// null where the system does not give them.
public sealed record GpuSample(double Usage, Reading<ulong> DedicatedBytes)
{
    public IReadOnlyList<GpuEngine> Engines { get; init; } = [];
    public Reading<ulong> SharedBytes { get; init; } = Reading<ulong>.Unavailable("");
    public string? Name { get; init; }
    public ulong? DedicatedTotal { get; init; }
    public double? DedicatedFraction => DedicatedBytes.TryGet(out var used) && DedicatedTotal is { } total && total > 0 ? Math.Clamp((double)used / total, 0, 1) : null;
}

public static class GpuEngines
{
    // The engines worth a column of their own, in the order Task Manager lists them; other kinds follow by name.
    private static readonly string[] Known = ["3D", "Compute", "Copy", "VideoDecode", "VideoEncode", "VideoProcessing"];
    // "Compute_0" and "Compute_1" are one kind; the suffix numbers engines of a kind.
    public static string Kind(string engineType)
    {
        int cut = engineType.LastIndexOf('_');
        return cut > 0 && int.TryParse(engineType.AsSpan(cut + 1), out _) ? engineType[..cut] : engineType;
    }
    public static IReadOnlyList<GpuEngine> Order(IEnumerable<GpuEngine> engines) => engines
        .GroupBy(engine => Kind(engine.Kind), StringComparer.OrdinalIgnoreCase).Select(group => new GpuEngine(group.Key, Math.Clamp(group.Max(e => e.Usage), 0, 1)))
        .OrderBy(engine => Array.FindIndex(Known, known => known.Equals(engine.Kind, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : Known.Length)
        .ThenBy(engine => engine.Kind, StringComparer.OrdinalIgnoreCase).ToList();
    public static string Label(string kind) => kind switch
    {
        "3D" => "3D", "Compute" => Loc.T("计算", "Compute"), "Copy" => Loc.T("复制", "Copy"), "VideoDecode" => Loc.T("视频解码", "Video decode"),
        "VideoEncode" => Loc.T("视频编码", "Video encode"), "VideoProcessing" => Loc.T("视频处理", "Video processing"), _ => kind
    };
    // Two or three letters under a narrow column.
    public static string Short(string kind) => kind switch
    { "3D" => "3D", "Compute" => "CMP", "Copy" => "CPY", "VideoDecode" => "DEC", "VideoEncode" => "ENC", "VideoProcessing" => "VP", _ => kind.Length <= 3 ? kind : kind[..3].ToUpperInvariant() };
}

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

// DownHistory and UpHistory are the last network readings, oldest first, bytes per second; empty until there are two.
// CpuHistory and GpuHistory are the last processor and graphics usage readings in the same way, 0 to 1.
public sealed record MonitorSnapshot(DateTimeOffset Time, int LogicalProcessors, Reading<CpuSample> Cpu, Reading<MemorySample> Memory,
    Reading<NetworkSample> Network, Reading<GpuSample> Gpu, Reading<ProcessList> Processes)
{
    public IReadOnlyList<double> DownHistory { get; init; } = [];
    public IReadOnlyList<double> UpHistory { get; init; } = [];
    public IReadOnlyList<double> CpuHistory { get; init; } = [];
    public IReadOnlyList<double> GpuHistory { get; init; } = [];
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
