using System.Diagnostics;
using System.Security.Principal;
using BrimDeck.Core;
using BrimDeck.Native;

// Reads this machine's temperature sensors the way the system monitor does, without a driver or administrator rights. A machine
// may lack any of them (no ACPI thermal zone, an adapter without a sensor, no solid-state drive), so the checks hold either way:
// whatever is read is plausible, nothing reads as zero, and a missing sensor stays missing. What was read is printed.
internal static class TemperatureChecks
{
    public static void Run(Action<string, bool> check)
    {
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Console.WriteLine($"Temperatures read {(elevated ? "with" : "without")} administrator rights.");
        using var pdh = new PdhQuery();
        Zones(pdh, check);
        Adapters(pdh, check);
        Drives(check, elevated);
    }

    private static void Zones(PdhQuery pdh, Action<string, bool> check)
    {
        var zones = new ThermalZones(pdh);
        check("thermal zones: the first ask only adds the counters", zones.Sample(pdh.Collect()) is null);
        bool collected = pdh.Collect();
        var cpu = zones.Sample(collected);
        Console.WriteLine(cpu is { } zone ? $"  CPU {zone.Celsius:0.0} °C from {zone.Zone}" : "  CPU: no ACPI thermal zone gives a temperature on this machine");
        check("thermal zones: the processor's temperature is plausible or none", cpu is null || Temperatures.Plausible(cpu.Value.Celsius) is not null);
        check("thermal zones: none without a collection", zones.Sample(false) is null);
    }

    private static void Adapters(PdhQuery pdh, Action<string, bool> check)
    {
        // The adapters by the key the counters name them with, as GpuProvider finds them.
        const string Memory = @"\GPU Adapter Memory(*)\Dedicated Usage";
        pdh.Add(Memory); pdh.Collect();
        var keys = pdh.Read(Memory).Select(item => item.Instance).Select(instance => instance.IndexOf("_phys", StringComparison.Ordinal) is var end and > 0 ? instance[..end] : instance)
            .Where(key => AdapterLuid.Parse(key) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using var temperatures = new GpuTemperatures();
        int read = 0;
        foreach (var key in keys)
        {
            var first = temperatures.Read(key);
            var second = temperatures.Read(key);
            Console.WriteLine(first is { } celsius ? $"  GPU {key}: {celsius:0.0} °C" : $"  GPU {key}: no temperature");
            check($"graphics adapter {key}: plausible or none", first is null || Temperatures.Plausible(first.Value) is not null);
            check($"graphics adapter {key}: an adapter without a sensor stays without one", first is not null || second is null);
            if (first is not null) read++;
        }
        check("graphics adapters: an unknown key reads nothing", temperatures.Read("luid_0x7FFFFFFF_0x7FFFFFFF") is null && temperatures.Read("not an adapter") is null);
        Console.WriteLine($"  {keys.Count} adapter(s) listed, {read} with a temperature");
    }

    private static void Drives(Action<string, bool> check, bool elevated)
    {
        var watch = Stopwatch.StartNew();
        var drives = DiskTemperatures.Read();
        Console.WriteLine($"  {drives.Count} solid-state drive(s) with a temperature, read in {watch.Elapsed.TotalMilliseconds:0} ms");
        foreach (var drive in drives) Console.WriteLine($"  Drive {drive.Name}: {drive.Celsius:0} °C (warning {drive.Warning?.ToString() ?? "-"}, critical {drive.Critical?.ToString() ?? "-"})");
        check("drives: every reading plausible and named", drives.All(drive => Temperatures.Plausible(drive.Celsius) is not null && drive.Name.Length > 0));
        check("drives: thresholds in order where given", drives.All(drive => drive.Warning is not { } w || drive.Critical is not { } c || w <= c));
        if (drives.Count > 0 && !elevated) check("drives: read without administrator rights", true);

        // The sampler's reader never waits for the drives: its first sample is empty, and the readings follow on the thread pool.
        var reader = new DiskTemperatures();
        check("drives: the first sample does not wait for the drives", reader.Sample(0).Count == 0);
        IReadOnlyList<DiskTemperature> later = [];
        for (int attempt = 0; attempt < 100 && later.Count < drives.Count; attempt++) { Thread.Sleep(50); later = reader.Sample(1); }
        check("drives: the background reading arrives", later.Count == drives.Count && reader.Found == drives.Count > 0);
        reader.Forget();
        check("drives: forgotten once the sampler pauses, read again on the next sample", reader.Sample(2).Count == 0);
    }
}
