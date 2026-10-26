using System.Diagnostics;
using System.Runtime.InteropServices;
using BrimDeck.Core;

namespace BrimDeck.Native;

// The temperatures Windows reports to a normal user without a driver: nothing here needs administrator rights or loads anything
// beyond the system's own libraries. Hardware monitors that read the processor's own sensors do it through a kernel driver
// (WinRing0 and the like), which BrimDeck does not install. Each reader belongs to the sampling thread, as the providers do; the
// sensor states are read from the UI thread.

// The processor from the ACPI thermal zones, through the performance counters the sampler already collects. The zones are the
// firmware's: on some machines one is the processor package, on others the board near it, some report a fixed value (SteadyZones
// leaves those out) and some machines have none (then the page shows no processor temperature). The counters are in the query
// only while temperatures are wanted.
internal sealed class ThermalZones(PdhQuery? pdh)
{
    private const string PrecisePath = @"\Thermal Zone Information(*)\High Precision Temperature";
    private const string WholePath = @"\Thermal Zone Information(*)\Temperature";
    private readonly SteadyZones _steady = new();
    private bool _added, _available;
    private volatile SensorState _state = pdh is null ? SensorState.None : SensorState.Unknown;
    public SensorState State => _state;

    // Called on every tick: wanted adds the counters (read from the next collection on), not wanted takes them out again.
    public (double Celsius, string Zone)? Sample(bool collected, bool wanted, double now)
    {
        if (pdh is null) return null;
        if (!wanted)
        {
            if (_added) { pdh.Remove(PrecisePath); pdh.Remove(WholePath); _added = false; }
            return null;
        }
        if (!_added)
        {
            _added = true; _available = pdh.Add(PrecisePath) | pdh.Add(WholePath);
            if (!_available) _state = SensorState.None;
            return null;
        }
        if (!_available || !collected) return null;
        var zones = Temperatures.Zones(pdh.Read(PrecisePath), pdh.Read(WholePath));
        if (zones.Count > 0) _state = SensorState.Found; else if (_state == SensorState.Unknown) _state = SensorState.None;
        return _steady.Choose(zones, now);
    }
}

// Graphics adapters as Task Manager reads their temperature: the adapter's performance data from the graphics kernel (WDDM 2.4
// and later). Each adapter is opened by its LUID and kept open. An adapter whose driver says it does not report the data (a
// software renderer) is not asked again; one that gives no temperature for now, or fails otherwise, is asked again after a
// pause that grows from 5 seconds to 5 minutes.
internal sealed class GpuTemperatures : IDisposable
{
    // D3DKMT_ADAPTER_PERFDATA on 64-bit Windows: PhysicalAdapterIndex, then 8-byte figures; Temperature (tenths of a degree
    // Celsius) at 56, 64 bytes in all.
    private const int PerfDataSize = 64, TemperatureOffset = 56, AdapterPerfData = 62;
    private const uint NotSupported = 0xC00000BB, InvalidParameter = 0xC000000D;
    private const double FirstPause = 5, LongestPause = 300;
    private sealed class Adapter { public uint Handle; public bool Never; public double RetryAt = double.NegativeInfinity, Pause; }
    private readonly Dictionary<string, Adapter> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly IntPtr _data = Marshal.AllocHGlobal(PerfDataSize);
    private string? _last;
    private bool _disposed;
    private volatile SensorState _state;
    public SensorState State => _state;

    // The temperature for the adapter the page shows: its own where it has one. Otherwise the adapter that last gave one, and
    // then any other, so an adapter without a sensor (most integrated adapters) never takes the temperature away while the
    // busiest adapter changes. Adapter is the key of the one read. adapters: every adapter's key, in a fixed order.
    public (double Celsius, string Adapter)? ReadFor(string shown, IReadOnlyList<string> adapters, double now)
    {
        if (Read(shown, now) is { } own) return Found(own, shown);
        if (_last is { } last && last != shown && adapters.Contains(last, StringComparer.OrdinalIgnoreCase) && Read(last, now) is { } kept) return Found(kept, last);
        foreach (var adapter in adapters)
            if (adapter != shown && adapter != _last && Read(adapter, now) is { } other) return Found(other, adapter);
        if (_state == SensorState.Unknown && adapters.Count > 0) _state = SensorState.None;
        return null;
    }
    private (double, string) Found(double celsius, string adapter) { _last = adapter; _state = SensorState.Found; return (celsius, adapter); }

    // The adapter by the counters' key ("luid_0x…_0x…"), or null without a temperature for now.
    public double? Read(string key, double now)
    {
        if (_disposed) return null;
        if (!_adapters.TryGetValue(key, out var adapter)) _adapters[key] = adapter = new Adapter();
        if (adapter.Never || now < adapter.RetryAt) return null;
        if (adapter.Handle == 0 && (adapter.Handle = Open(key)) == 0) return Wait(adapter, now);
        for (int i = 0; i < PerfDataSize; i += 8) Marshal.WriteInt64(_data, i, 0);
        var query = new QueryAdapterInfo { Adapter = adapter.Handle, Type = AdapterPerfData, Data = _data, Size = PerfDataSize };
        uint status;
        try { status = (uint)D3DKMTQueryAdapterInfo(ref query); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { Debug.WriteLine(ex); adapter.Never = true; return null; }
        if (status == 0)
        {
            if (Temperatures.FromDeciCelsius((uint)Marshal.ReadInt32(_data, TemperatureOffset)) is { } celsius) { adapter.Pause = 0; return celsius; }
            return Wait(adapter, now);
        }
        // The driver does not report the data at all: this adapter is left alone from now on. Any other failure (the adapter
        // went away, the driver was busy) opens it again after the pause.
        Close(adapter.Handle); adapter.Handle = 0;
        if (status is NotSupported or InvalidParameter) { adapter.Never = true; return null; }
        return Wait(adapter, now);
    }
    private static double? Wait(Adapter adapter, double now)
    {
        adapter.Pause = Math.Clamp(adapter.Pause * 2, FirstPause, LongestPause);
        adapter.RetryAt = now + adapter.Pause;
        return null;
    }

    private static uint Open(string adapter)
    {
        if (AdapterLuid.Parse(adapter) is not { } luid) return 0;
        var open = new OpenAdapterFromLuid { Low = luid.Low, High = luid.High };
        try { return D3DKMTOpenAdapterFromLuid(ref open) == 0 ? open.Adapter : 0; }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { Debug.WriteLine(ex); return 0; }
    }

    private static void Close(uint handle)
    {
        if (handle == 0) return;
        var close = new CloseAdapter { Adapter = handle };
        try { D3DKMTCloseAdapter(ref close); } catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var adapter in _adapters.Values) Close(adapter.Handle);
        _adapters.Clear();
        Marshal.FreeHGlobal(_data);
    }

    [StructLayout(LayoutKind.Sequential)] private struct OpenAdapterFromLuid { public uint Low; public int High; public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)] private struct CloseAdapter { public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryAdapterInfo { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }
    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid open);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter close);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);
}

// Solid-state drives as they report their own temperature (an NVMe drive's composite temperature, or a SATA drive's), through the
// storage stack's temperature property. The drive is opened without read or write access, which a normal user may do. Asking
// sends the drive a command and takes from a few to over a hundred milliseconds, and wakes a drive from its deepest power
// state, so the drives are read on the thread pool now and then, never on a tick. Hard disks are left out, as asking could spin
// one up, and so are removable and USB drives. The readings and the state are kept while the sampler pauses, so a page opened
// again has its drives' line at once.
internal sealed class DiskTemperatures
{
    private const double Every = 30;
    private const int MaximumDrives = 16;
    private volatile IReadOnlyList<DiskTemperature> _latest = [];
    private volatile SensorState _state;
    private double _readAt = double.NegativeInfinity;
    private int _running;
    private volatile Task _reading = Task.CompletedTask;
    public SensorState State => _state;
    // The reading under way, or a finished task.
    public Task Pending => _reading;

    // Starts reading the drives on the thread pool unless a reading is under way. Called from the sampler and, to know early
    // whether the page needs a line for them, from the UI thread when the drives are first wanted.
    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _reading = Task.Run(() =>
        {
            try
            {
                var drives = Read();
                _latest = drives;
                if (drives.Count > 0) _state = SensorState.Found; else if (_state == SensorState.Unknown) _state = SensorState.None;
            }
            finally { Volatile.Write(ref _running, 0); }
        });
    }

    // The latest readings; a new reading is started when the last is old, and its result shows from the next tick.
    public IReadOnlyList<DiskTemperature> Sample(double now)
    {
        if (now - _readAt >= Every) { _readAt = now; Start(); }
        return _latest;
    }

    // The sampler paused: the next sample reads the drives again at once. The last readings stay until then.
    public void Rest() => _readAt = double.NegativeInfinity;

    public static List<DiskTemperature> Read()
    {
        var drives = new List<DiskTemperature>();
        var query = Marshal.AllocHGlobal(QuerySize);
        var output = Marshal.AllocHGlobal(OutputSize);
        try
        {
            for (int number = 0; number < MaximumDrives; number++)
            {
                var device = CreateFileW($@"\\.\PhysicalDrive{number}", 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (device == InvalidHandle) continue;
                try { if (Drive(device, query, output) is { } drive) drives.Add(drive); }
                finally { CloseHandle(device); }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine(ex); }
        finally { Marshal.FreeHGlobal(query); Marshal.FreeHGlobal(output); }
        return drives;
    }

    private static DiskTemperature? Drive(IntPtr device, IntPtr query, IntPtr output)
    {
        // STORAGE_DEVICE_DESCRIPTOR: RemovableMedia at 10, the offset of ProductId at 16, BusType at 28.
        if (!Property(device, query, output, DeviceProperty, out int length) || length < 32) return null;
        if (Marshal.ReadByte(output, 10) != 0 || Marshal.ReadInt32(output, 28) is BusUsb or BusSd or BusMmc) return null;
        int productAt = Marshal.ReadInt32(output, 16);
        string name = productAt > 0 && productAt < length ? Marshal.PtrToStringAnsi(output + productAt)?.Trim() ?? "" : "";
        // DEVICE_SEEK_PENALTY_DESCRIPTOR: IncursSeekPenalty at 8, true for a spinning disk. A drive that does not say is taken
        // for a disk too.
        if (!Property(device, query, output, SeekPenaltyProperty, out length) || length < 9 || Marshal.ReadByte(output, 8) != 0) return null;
        // STORAGE_TEMPERATURE_DATA_DESCRIPTOR: CriticalTemperature at 8, WarningTemperature at 10, InfoCount at 12, then the
        // STORAGE_TEMPERATURE_INFO entries from 24, 16 bytes each, Temperature at 2. The first is the drive's own.
        if (!Property(device, query, output, TemperatureProperty, out length) || length < 40 || Marshal.ReadInt16(output, 12) < 1) return null;
        return Temperatures.Disk(name.Length > 0 ? name : "SSD", Marshal.ReadInt16(output, 26), Marshal.ReadInt16(output, 10), Marshal.ReadInt16(output, 8));
    }

    // STORAGE_PROPERTY_QUERY: PropertyId, QueryType (0, the standard query), one byte of parameters.
    private static bool Property(IntPtr device, IntPtr query, IntPtr output, int property, out int length)
    {
        Marshal.WriteInt32(query, 0, property); Marshal.WriteInt32(query, 4, 0); Marshal.WriteInt32(query, 8, 0);
        return DeviceIoControl(device, QueryProperty, query, QuerySize, output, OutputSize, out length, IntPtr.Zero);
    }

    private const int QuerySize = 12, OutputSize = 1024;
    private const int DeviceProperty = 0, SeekPenaltyProperty = 7, TemperatureProperty = 52;
    private const int BusUsb = 7, BusSd = 12, BusMmc = 13;
    private const uint QueryProperty = 0x2D1400, ShareReadWrite = 3, OpenExisting = 3;
    private static readonly IntPtr InvalidHandle = new(-1);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
