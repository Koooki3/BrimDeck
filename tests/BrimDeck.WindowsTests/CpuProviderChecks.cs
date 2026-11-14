using BrimDeck.Core;
using BrimDeck.Native;

// The processor's readings before and after the performance counters join (CpuProvider.Attach): the kernel's times give the first
// figures alone, and the handover to the counters keeps giving them.
internal static class CpuProviderChecks
{
    public static void Run(Action<string, bool> check)
    {
        using var pdh = new PdhQuery();
        var cpu = new CpuProvider(pdh);
        check("cpu: nothing is waited for at construction, the counters are not in the query yet", !cpu.Attached);
        var first = cpu.Sample(false);
        Thread.Sleep(300);
        var second = cpu.Sample(false);
        check("cpu: the kernel's times give no figure at first and a figure after a window, without the counters",
            !first.HasValue && second.TryGet(out var kernel) && kernel.Cores.Count > 0 && kernel.Cores.All(core => core.Usage is >= 0 and <= 1) && !cpu.Attached);
        check("cpu: before the counters join the frequency is waiting, not missing", second.TryGet(out kernel!) && kernel.Cores.All(core => !core.FrequencyMhz.HasValue)
            && kernel.Cores.All(core => core.FrequencyMhz.Reason == Why.Waiting));

        cpu.Attach();
        Thread.Sleep(300);
        bool collected = pdh.Collect();
        var counters = cpu.Sample(collected);
        check("cpu: after the counters join the figures go on, from the counters", cpu.Attached && counters.TryGet(out var read) && read.Cores.Count > 0
            && read.Cores.All(core => core.Usage is >= 0 and <= 1));
        Thread.Sleep(300);
        var again = cpu.Sample(pdh.Collect());
        check("cpu: and stay there on the next reading", again.HasValue && again.TryGet(out read!) && read.Cores.Count == kernel.Cores.Count);
    }
}
