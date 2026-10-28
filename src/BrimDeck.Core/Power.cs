using System.Globalization;

namespace BrimDeck.Core;

// The whole machine's power draw for the monitor page and the collapsed row. The probes (SystemSampler) hand over what the machine
// reports in PowerInputs; PowerResolver chooses what to show, in this order, and says where each figure comes from:
//   1. SystemMeter: a meter of the whole platform that Windows exposes through the Energy Meter Interface or the ACPI power meter
//      (the "Energy Meter" and "Power Meter" performance counters). Measured. Few machines have one (some tablets and servers).
//   2. Battery: the power the battery supplies while the machine runs on it (Windows.Devices.Power.Battery's charge rate), which is
//      all that the machine draws, display and fans included. Measured, but only on battery.
//   3. Components: the processor package (RAPL, through the Energy Meter counters), memory where it has a meter of its own, and each
//      graphics adapter that reports its board power (NVIDIA through the driver's nvidia-smi). A sum of what can be read: the display, drives, fans,
//      other chips and the power supply's losses are not in it, so it is labelled an estimate and never shown as a measurement.
//   4. None: the reading says why there is none, and the page shows a dash with that reason.
public enum PowerSource { SystemMeter, Battery, Components }

public enum PowerOrigin { Measured, Estimated }

// One component's share of the sum: Name is what the page calls it ("CPU"), Source where the figure was read ("Energy Meter").
public sealed record PowerPart(string Key, string Name, double Watts, string Source);

public sealed record PowerSample(double Watts, PowerOrigin Origin, PowerSource Source, IReadOnlyList<PowerPart> Parts)
{
    // Components that exist but gave no reading right now, with the reason (a graphics adapter asleep).
    public IReadOnlyList<string> Missing { get; init; } = [];
    public bool Estimated => Origin == PowerOrigin.Estimated;
}

// One graphics adapter's board power as its driver reports it; Awake: the adapter is doing work, so reading it does not wake it.
public sealed record GpuPowerInput(string Name, Reading<double> Watts);

// What the probes read in one tick, each part a Reading in watts (unavailable with the reason where the machine has no such meter).
public sealed record PowerInputs
{
    public Reading<double> SystemMeter { get; init; } = Reading<double>.Unavailable("");
    // The power the battery supplies; unavailable while it is not discharging.
    public Reading<double> BatteryDischarge { get; init; } = Reading<double>.Unavailable("");
    // The processor package (graphics cores inside it included) and, where it has a meter of its own, memory.
    public Reading<double> Cpu { get; init; } = Reading<double>.Unavailable("");
    public Reading<double> Memory { get; init; } = Reading<double>.Unavailable("");
    public IReadOnlyList<GpuPowerInput> Gpus { get; init; } = [];
    // Said when nothing at all could be read, in the interface language.
    public string NoSourceReason { get; init; } = "";
}

public static class PowerResolver
{
    // A figure under this is a meter at rest or a battery at its threshold, not a draw worth showing as the machine's.
    public const double MeterFloor = 1, BatteryFloor = .5;
    // No machine this app runs on draws more; a reading above is a broken counter.
    public const double Ceiling = 2000;

    // Where a graphics adapter's power is read: the graphics driver's own tool.
    public static string GpuSource => Loc.T("NVIDIA 驱动", "NVIDIA driver");

    // A helper process that has ended (a driver update, a reset, ended from outside) is started again while its adapter still works, but
    // not before this many seconds after it was started, so one that dies at once is not started every second.
    public const double RestartSeconds = 30;
    public static bool RestartDue(bool ended, double now, double startedAt) => ended && now - startedAt >= RestartSeconds;

    public static bool Plausible(double watts) => double.IsFinite(watts) && watts >= 0 && watts <= Ceiling;
    private static double? Value(Reading<double> reading) => reading.TryGet(out var watts) && Plausible(watts) ? watts : null;

    public static Reading<PowerSample> Resolve(PowerInputs inputs)
    {
        var parts = new List<PowerPart>();
        var missing = new List<string>();
        if (Value(inputs.Cpu) is { } cpu) parts.Add(new PowerPart("cpu", Loc.T("CPU 封装", "CPU package"), cpu, "RAPL"));
        if (Value(inputs.Memory) is { } memory && memory > 0) parts.Add(new PowerPart("memory", Loc.T("内存", "Memory"), memory, "RAPL"));
        foreach (var gpu in inputs.Gpus)
        {
            // One adapter is "GPU"; several are told apart by their names.
            string name = inputs.Gpus.Count > 1 && gpu.Name.Length > 0 ? gpu.Name : "GPU";
            if (Value(gpu.Watts) is { } watts) parts.Add(new PowerPart("gpu", name, watts, GpuSource));
            else if (!gpu.Watts.HasValue && gpu.Watts.Reason.Length > 0) missing.Add(name + Loc.T("（", " (") + gpu.Watts.Reason + Loc.T("）", ")"));
        }
        PowerSample Make(double watts, PowerOrigin origin, PowerSource source) => new(watts, origin, source, parts) { Missing = missing };

        if (Value(inputs.SystemMeter) is { } meter && meter >= MeterFloor) return Make(meter, PowerOrigin.Measured, PowerSource.SystemMeter);
        if (Value(inputs.BatteryDischarge) is { } battery && battery >= BatteryFloor) return Make(battery, PowerOrigin.Measured, PowerSource.Battery);
        if (parts.Count > 0) return Make(parts.Sum(part => part.Watts), PowerOrigin.Estimated, PowerSource.Components);
        string reason = inputs.NoSourceReason.Length > 0 ? inputs.NoSourceReason
            : Loc.T("无可读的功率来源（功率计、电池放电、CPU / GPU）", "No readable power source (meter, battery discharge, CPU / GPU)");
        return Reading<PowerSample>.Unavailable(reason);
    }

    // The reading of an adapter among the driver's lines (name, watts), the driver's order kept: the nth of the lines that name this adapter,
    // so two identical cards are two readings and not one counted twice; with one line and one adapter, that line.
    public static Reading<double> PickGpu(IReadOnlyList<(string Name, double? Watts)> lines, string adapter, int ordinal)
    {
        var found = lines.Where(line => line.Name.Equals(adapter, StringComparison.OrdinalIgnoreCase) || adapter.Contains(line.Name, StringComparison.OrdinalIgnoreCase)
            || line.Name.Contains(adapter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0 && lines.Count == 1) found = [.. lines];
        if (found.Count == 0) return Reading<double>.Unavailable(Loc.T("未列出", "not listed"));
        // An adapter beyond the lines (one the driver does not list twice) has none of its own.
        if (ordinal >= found.Count && found.Count > 1) return Reading<double>.Unavailable(Loc.T("驱动没有列出这块显卡", "the driver does not list this adapter"));
        var line = found[Math.Min(ordinal, found.Count - 1)];
        return line.Watts is { } watts && Plausible(watts) ? watts : Reading<double>.Unavailable(Loc.T("无功率数据", "no power data"));
    }

    // Where a figure comes from, in the words the page shows beside it.
    public static string SourceName(PowerSample sample) => sample.Source switch
    {
        PowerSource.SystemMeter => Loc.T("整机能量计", "System meter"),
        PowerSource.Battery => Loc.T("电池放电", "Battery discharge"),
        _ => Loc.T("部件合计", "Sum of parts")
    };
    // "实测" or "估算": a figure that is not a measurement is always marked.
    public static string OriginName(PowerSample sample) => sample.Origin == PowerOrigin.Measured ? Loc.T("实测", "Measured") : Loc.T("估算", "Estimate");

    // The counters behind the first source and the components: instance names of the Energy Meter and Power Meter counters
    // (milliwatts) turned into watts. RAPL domains are named "RAPL_Package<N>_<domain>": PKG is the package (cores, graphics and the
    // uncore; one per socket), DRAM the memory behind it; PP0 and PP1 are parts of PKG and not added to it. Any other channel is a
    // meter of the platform (a battery rail, a board meter) and counts toward the system meter; "_Total" is skipped so channels are
    // not counted twice.
    public static (Reading<double> System, Reading<double> Cpu, Reading<double> Memory) FromCounters(
        IReadOnlyList<(string Instance, double MilliWatts)> energy, IReadOnlyList<(string Instance, double MilliWatts)> meter)
    {
        double? package = null, dram = null, system = null;
        bool rapl = false;
        foreach (var (instance, milliwatts) in energy)
        {
            if (!double.IsFinite(milliwatts) || milliwatts < 0) continue;
            if (instance.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
            // PSYS is RAPL's platform domain: the whole platform, a system meter.
            if (instance.EndsWith("_PSYS", StringComparison.OrdinalIgnoreCase)) system = (system ?? 0) + milliwatts / 1000;
            else if (instance.StartsWith("RAPL_", StringComparison.OrdinalIgnoreCase))
            {
                rapl = true;
                if (instance.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase)) package = (package ?? 0) + milliwatts / 1000;
                else if (instance.EndsWith("_DRAM", StringComparison.OrdinalIgnoreCase)) dram = (dram ?? 0) + milliwatts / 1000;
            }
            else system = (system ?? 0) + milliwatts / 1000;
        }
        // The ACPI power meter is the platform's own: it stands for the system meter, not on top of the channels counted above.
        double? meterSum = null;
        foreach (var (instance, milliwatts) in meter)
            if (double.IsFinite(milliwatts) && milliwatts >= 0 && !instance.Equals("_Total", StringComparison.OrdinalIgnoreCase)) meterSum = (meterSum ?? 0) + milliwatts / 1000;
        if (meterSum is { } acpi) system = acpi;
        string none = Loc.T("无此类功率计", "no such meter");
        return (system is { } s ? s : Reading<double>.Unavailable(none), package is { } p ? p : Reading<double>.Unavailable(rapl ? Loc.T("无封装功率", "no package power") : none),
            dram is { } d ? d : Reading<double>.Unavailable(none));
    }
}

// The peak and the average of the readings since the page was opened (the sampler pauses a few seconds after it closes and starts
// again from nothing), over every reading whatever its source.
public sealed class PowerStats
{
    private double _sum;
    public int Count { get; private set; }
    public double Peak { get; private set; }
    public double Average => Count == 0 ? 0 : _sum / Count;
    public void Add(double watts)
    {
        if (!PowerResolver.Plausible(watts)) return;
        _sum += watts; Count++; Peak = Math.Max(Peak, watts);
    }
    public void Reset() { _sum = 0; Count = 0; Peak = 0; }
}

public static class PowerFormat
{
    // Watts with one decimal under 100 and whole from there: "38.2", "112".
    public static string Watts(double watts)
    {
        double value = double.IsFinite(watts) ? Math.Max(0, watts) : 0;
        return value < 99.95 ? value.ToString("0.0", CultureInfo.InvariantCulture) : ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
    }
    // For the collapsed row, at most three characters and the unit: "8.4", "38", "112".
    public static string Compact(double watts)
    {
        double value = double.IsFinite(watts) ? Math.Max(0, watts) : 0;
        return value < 9.95 ? value.ToString("0.0", CultureInfo.InvariantCulture) : ((int)Math.Round(Math.Min(value, 999))).ToString(CultureInfo.InvariantCulture);
    }
}
