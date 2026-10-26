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
        // A query whose counters have no instances at all collects nothing (PDH_NO_DATA); the sampler's always has the processor's.
        pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
        var zones = new ThermalZones(pdh);
        check("thermal zones: unknown until asked", zones.State == SensorState.Unknown);
        check("thermal zones: the first ask only adds the counters", zones.Sample(pdh.Collect(), true, 0) is null);
        bool collected = pdh.Collect();
        var cpu = zones.Sample(collected, true, 1);
        Console.WriteLine(cpu is { } zone ? $"  CPU {zone.Celsius:0.0} °C from {zone.Zone}" : $"  CPU: no ACPI thermal zone gives a temperature on this machine ({zones.State})");
        check("thermal zones: the processor's temperature is plausible or none", cpu is null || Temperatures.Plausible(cpu.Value.Celsius) is not null);
        check("thermal zones: known once read", zones.State != SensorState.Unknown && (cpu is null || zones.State == SensorState.Found));
        check("thermal zones: none without a collection", zones.Sample(false, true, 2) is null);
        check("thermal zones: none once not wanted, and the counters leave the query", zones.Sample(pdh.Collect(), false, 3) is null && pdh.Read(@"\Thermal Zone Information(*)\Temperature").Count == 0);
    }

    private static void Adapters(PdhQuery pdh, Action<string, bool> check)
    {
        // The adapters by the key the counters name them with, as GpuProvider finds them.
        const string Memory = @"\GPU Adapter Memory(*)\Dedicated Usage";
        pdh.Add(Memory); pdh.Collect();
        var keys = pdh.Read(Memory).Select(item => item.Instance).Select(instance => instance.IndexOf("_phys", StringComparison.Ordinal) is var end and > 0 ? instance[..end] : instance)
            .Where(key => AdapterLuid.Parse(key) is not null).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        using var temperatures = new GpuTemperatures();
        var found = new List<string>();
        foreach (var key in keys)
        {
            var first = temperatures.Read(key, 0);
            var soon = temperatures.Read(key, 1);
            Console.WriteLine(first is { } celsius ? $"  GPU {key}: {celsius:0.0} °C" : $"  GPU {key}: no temperature");
            check($"graphics adapter {key}: plausible or none", first is null || Temperatures.Plausible(first.Value) is not null);
            check($"graphics adapter {key}: one without a temperature is not asked again at once", first is not null || soon is null);
            if (first is not null) found.Add(key);
        }
        if (keys.Count > 0)
        {
            // Whichever adapter is the busiest, the temperature comes from one with a sensor and stays with it.
            var readings = keys.Select(shown => temperatures.ReadFor(shown, keys, 2)).ToList();
            check("graphics adapters: an adapter without a sensor does not take the temperature away", found.Count == 0
                ? readings.All(reading => reading is null) && temperatures.State == SensorState.None
                : readings.All(reading => reading is { } r && found.Contains(r.Adapter)) && temperatures.State == SensorState.Found);
            var without = keys.Except(found).ToList();
            if (found.Count > 0 && without.Count > 0)
                check("graphics adapters: the adapter that last gave one keeps it", without.All(shown => temperatures.ReadFor(shown, keys, 3)?.Adapter == temperatures.ReadFor(without[0], keys, 3)?.Adapter));
        }
        check("graphics adapters: an unknown key reads nothing", temperatures.Read("luid_0x7FFFFFFF_0x7FFFFFFF", 0) is null && temperatures.Read("not an adapter", 0) is null);
        Console.WriteLine($"  {keys.Count} adapter(s) listed, {found.Count} with a temperature");
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
        // The checks wait for that reading itself, however long the drives take.
        var reader = new DiskTemperatures();
        check("drives: unknown until read", reader.State == SensorState.Unknown);
        check("drives: the first sample does not wait for the drives", reader.Sample(0).Count == 0);
        reader.Pending.Wait();
        var later = reader.Sample(1);
        check("drives: the background reading arrives", later.Count == drives.Count && reader.State == (drives.Count > 0 ? SensorState.Found : SensorState.None));
        reader.Rest();
        check("drives: the readings stay while the sampler pauses", reader.Sample(2).Count == drives.Count);
        reader.Pending.Wait();
        check("drives: read again on the next sample after a pause", reader.Sample(3).Count == drives.Count);
        var early = new DiskTemperatures();
        early.Start(); early.Start();
        early.Pending.Wait();
        check("drives: started early, one reading at a time", early.Sample(0).Count == drives.Count);
    }
}
