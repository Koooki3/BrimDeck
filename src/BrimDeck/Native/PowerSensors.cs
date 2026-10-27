using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BrimDeck.Core;
using Windows.Devices.Enumeration;
using Windows.Devices.Power;

namespace BrimDeck.Native;

// What Windows reports about power to a normal user: nothing here needs administrator rights or installs a driver. Processor power
// comes from the Energy Meter counters (Windows exposes the processor's RAPL registers through its own Energy Meter Interface
// driver); a graphics adapter's board power from NVIDIA's management library, which the graphics driver ships in the system folder
// (an adapter of another make reports none yet); the battery from Windows.Devices.Power; the power mode from the power API. Each
// reader belongs to the sampling thread, as the providers in SystemSampler do; the sensor states are read from the UI thread.

// The whole machine's power (PowerResolver chooses between the sources, PowerInputs hands them over). The counters are in the query
// only while power is wanted. A graphics adapter is asked only while it is doing work (Awake seconds after its last use): asking
// an idle hybrid-graphics laptop's discrete adapter would wake it from sleep and cost the battery more than the figure is worth.
internal sealed class PowerProvider(PdhQuery? pdh) : IDisposable
{
    private const string EnergyPath = @"\Energy Meter(*)\Power", MeterPath = @"\Power Meter(*)\Power";
    private const double Awake = 8;
    // Ticks without any source before the machine is taken for one without (a battery machine keeps trying: unplugged it has one).
    private const int GiveUpAfter = 5;
    private bool _added, _available;
    private int _empty;
    private volatile SensorState _state = pdh is null ? SensorState.None : SensorState.Unknown;
    private readonly Dictionary<string, double> _busyAt = new(StringComparer.OrdinalIgnoreCase);
    private NvmlPower? _nvml;
    public SensorState State => _state;

    // gpus: the adapters read this tick (usage tells whether one is awake); discharge: the battery's supply (BatteryModel.Discharge);
    // hasBattery: the machine runs on a battery, so a missing source may only be for the moment.
    public Reading<PowerSample> Sample(bool collected, bool wanted, double now, IReadOnlyList<GpuSample> gpus, Reading<double> discharge, bool hasBattery)
    {
        if (!wanted)
        {
            if (_added && pdh is not null) { pdh.Remove(EnergyPath); pdh.Remove(MeterPath); _added = false; }
            _nvml?.Dispose(); _nvml = null; _busyAt.Clear();
            return Reading<PowerSample>.Unavailable(Why.Waiting);
        }
        if (pdh is not null && !_added) { _added = true; _available = pdh.Add(EnergyPath) | pdh.Add(MeterPath); }
        Reading<double> system = Reading<double>.Unavailable(""), cpu = system, memory = system;
        if (_available && collected && pdh is not null)
            (system, cpu, memory) = PowerResolver.FromCounters(pdh.Read(EnergyPath), pdh.Read(MeterPath));
        var inputs = new PowerInputs { SystemMeter = system, BatteryDischarge = discharge, Cpu = cpu, Memory = memory, Gpus = ReadGpus(gpus, now) };
        var power = PowerResolver.Resolve(inputs);
        if (power.HasValue) { _empty = 0; _state = SensorState.Found; }
        else if (collected && ++_empty >= GiveUpAfter && !hasBattery) _state = SensorState.None;
        return power;
    }

    private List<GpuPowerInput> ReadGpus(IReadOnlyList<GpuSample> gpus, double now)
    {
        var read = new List<GpuPowerInput>();
        foreach (var gpu in gpus)
        {
            string name = gpu.Name ?? "GPU";
            // Only NVIDIA's driver is asked, and only an adapter that is working.
            if (!name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;
            if (gpu.Usage > 0) _busyAt[name] = now;
            if (!_busyAt.TryGetValue(name, out var last) || now - last > Awake)
            {
                read.Add(new GpuPowerInput(name, Reading<double>.Unavailable(Loc.T("空闲，未读取以免唤醒休眠的显卡", "idle; not read so a sleeping adapter stays asleep"))));
                continue;
            }
            _nvml ??= new NvmlPower();
            read.Add(new GpuPowerInput(name, _nvml.Read(name)));
        }
        return read;
    }

    public void Dispose() { _nvml?.Dispose(); _nvml = null; }
}

// NVIDIA's management library (nvml.dll), loaded from the system folder or NVIDIA's own program folder (both writable by
// administrators only), initialised when first asked and closed when power is no longer wanted. Board power is read in milliwatts.
internal sealed class NvmlPower : IDisposable
{
    private delegate int Init();
    private delegate int Shutdown();
    private delegate int Count(out uint count);
    private delegate int Handle(uint index, out IntPtr device);
    private delegate int Name(IntPtr device, byte[] name, uint length);
    private delegate int Power(IntPtr device, out uint milliwatts);
    private IntPtr _library;
    private Shutdown? _shutdown;
    private Power? _power;
    private readonly List<(IntPtr Device, string Name)> _devices = [];
    private string? _failure;

    public NvmlPower()
    {
        try
        {
            foreach (var path in new[] { Path.Combine(Environment.SystemDirectory, "nvml.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll") })
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out _library)) break;
            if (_library == IntPtr.Zero) { _failure = Loc.T("驱动没有提供 NVIDIA 管理库", "the driver provides no NVIDIA management library"); return; }
            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
            if (Export<Init>("nvmlInit_v2")() != 0) { _failure = Loc.T("NVIDIA 管理库初始化失败", "the NVIDIA management library did not start"); return; }
            _shutdown = Export<Shutdown>("nvmlShutdown");
            _power = Export<Power>("nvmlDeviceGetPowerUsage");
            var count = Export<Count>("nvmlDeviceGetCount_v2"); var handle = Export<Handle>("nvmlDeviceGetHandleByIndex_v2"); var name = Export<Name>("nvmlDeviceGetName");
            if (count(out uint devices) != 0) return;
            for (uint i = 0; i < devices; i++)
            {
                if (handle(i, out var device) != 0) continue;
                var text = new byte[96];
                _devices.Add((device, name(device, text, (uint)text.Length) == 0 ? Encoding.ASCII.GetString(text).TrimEnd('\0') : ""));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine(ex); _failure = Loc.T("NVIDIA 管理库不可用", "the NVIDIA management library is unavailable");
        }
    }

    // The adapter by its name (DXGI and NVML name it alike); with one device and one NVIDIA adapter, that device.
    public Reading<double> Read(string adapter)
    {
        if (_failure is not null) return Reading<double>.Unavailable(_failure);
        var found = _devices.Where(device => device.Name.Length > 0 && (adapter.Contains(device.Name, StringComparison.OrdinalIgnoreCase) || device.Name.Contains(adapter, StringComparison.OrdinalIgnoreCase))).ToList();
        if (found.Count == 0 && _devices.Count == 1) found = _devices;
        if (found.Count == 0 || _power is null) return Reading<double>.Unavailable(Loc.T("NVIDIA 管理库没有这块显卡", "the NVIDIA library does not list this adapter"));
        try
        {
            if (_power(found[0].Device, out uint milliwatts) == 0) return milliwatts / 1000.0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine(ex); }
        return Reading<double>.Unavailable(Loc.T("驱动没有报告功率", "the driver reports no power"));
    }

    public void Dispose()
    {
        try { _shutdown?.Invoke(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine(ex); }
        if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
        _devices.Clear(); _power = null; _shutdown = null;
    }
}

// The machine's own batteries: each one's report, the mains connection, Battery saver and the power mode (BatteryInputs). The
// batteries are listed now and then and read on every tick; a machine without one is known after the first listing (SensorState.None)
// and asked again only every minute (a docking battery can appear).
internal sealed class BatteryProvider
{
    private const int Relist = 60;
    private readonly List<Windows.Devices.Power.Battery> _batteries = [];
    private int _sinceList = Relist;
    private volatile SensorState _state = SensorState.Unknown;
    public SensorState State => _state;
    public bool Present => _state == SensorState.Found;

    // Whether the system says it has a battery, asked of the system directly (no reading of the batteries): the page needs it to
    // know how many lines to keep before the sampler has run. 128 is "no system battery", 255 "unknown".
    public static bool SystemHasBattery => GetSystemPowerStatus(out var status) && status.BatteryFlag is not (128 or 255);

    public (BatteryInputs Inputs, Reading<BatterySample> Sample) Sample()
    {
        if (_sinceList++ >= Relist) List();
        var status = SystemStatus();
        var cells = new List<BatteryCell>(_batteries.Count);
        foreach (var battery in _batteries)
        {
            try
            {
                var report = battery.GetReport();
                if (report.Status == Windows.System.Power.BatteryStatus.NotPresent) continue;
                cells.Add(new BatteryCell(battery.DeviceId ?? "", report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours,
                    report.DesignCapacityInMilliwattHours, report.ChargeRateInMilliwatts, report.Status == Windows.System.Power.BatteryStatus.Charging,
                    report.Status == Windows.System.Power.BatteryStatus.Discharging));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
        }
        // The system's own view where the batteries give no report (a driver that lists none): it knows a battery is there and its share.
        bool systemBattery = status.BatteryFlag is not (128 or 255);
        var inputs = new BatteryInputs(cells, status.AcOnline, status.Saver, BatteryModel.ModeOf(Overlay()), status.Percent, status.Remaining);
        if (cells.Count == 0 && systemBattery && status.Percent is not null)
            inputs = inputs with { Cells = [new BatteryCell("system", null, null, null, null, status.AcOnline == false, false)] };
        var sample = BatteryModel.Build(inputs);
        _state = sample.HasValue ? SensorState.Found : cells.Count == 0 && !systemBattery ? SensorState.None : _state == SensorState.Found ? SensorState.Found : SensorState.Unknown;
        return (inputs, sample);
    }

    private void List()
    {
        _sinceList = 0;
        try
        {
            var found = DeviceInformation.FindAllAsync(Windows.Devices.Power.Battery.GetDeviceSelector()).AsTask().GetAwaiter().GetResult();
            var batteries = new List<Windows.Devices.Power.Battery>();
            foreach (var info in found)
                if (Windows.Devices.Power.Battery.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult() is { } battery) batteries.Add(battery);
            _batteries.Clear(); _batteries.AddRange(batteries);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or TypeLoadException or DllNotFoundException)
        {
            Debug.WriteLine(ex);
        }
    }

    // GetSystemPowerStatus: the mains connection, the share Windows shows, the time left it estimates, Battery saver.
    private static (bool? AcOnline, byte BatteryFlag, double? Percent, TimeSpan? Remaining, bool Saver) SystemStatus()
    {
        if (!GetSystemPowerStatus(out var status)) return (null, 255, null, null, false);
        return (status.ACLineStatus switch { 0 => false, 1 => true, _ => null }, status.BatteryFlag, status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null,
            status.BatteryLifeTime != uint.MaxValue ? TimeSpan.FromSeconds(status.BatteryLifeTime) : null, (status.SystemStatusFlag & 1) != 0);
    }

    // The effective power mode; the zero GUID (Balanced) when the call is not there.
    private static Guid Overlay()
    {
        try { return PowerGetEffectiveOverlayScheme(out var scheme) == 0 ? scheme : Guid.Empty; }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return Guid.Empty; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
}
