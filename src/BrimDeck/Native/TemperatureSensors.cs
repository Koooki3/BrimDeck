using System.Diagnostics;
using System.Runtime.InteropServices;
using BrimDeck.Core;

namespace BrimDeck.Native;

// The temperatures Windows reports to a normal user without a driver: nothing here needs administrator rights or loads anything
// beyond the system's own libraries. Hardware monitors that read the processor's own sensors do it through a kernel driver
// (WinRing0 and the like), which BrimDeck does not install. Each reader belongs to the sampling thread, as the providers do.

// The processor from the ACPI thermal zones, through the performance counters the sampler already collects. The zones are the
// firmware's: on some machines one is the processor package, on others the board near it, and some machines have none (then
// the page shows no processor temperature). The counters join the query the first time a temperature is asked for.
internal sealed class ThermalZones(PdhQuery? pdh)
{
    private const string PrecisePath = @"\Thermal Zone Information(*)\High Precision Temperature";
    private const string WholePath = @"\Thermal Zone Information(*)\Temperature";
    private bool _added, _available;

    public (double Celsius, string Zone)? Sample(bool collected)
    {
        if (pdh is null) return null;
        // Added now, read from the next collection on.
        if (!_added) { _added = true; _available = pdh.Add(PrecisePath) | pdh.Add(WholePath); return null; }
        return _available && collected ? Temperatures.Cpu(pdh.Read(PrecisePath), pdh.Read(WholePath)) : null;
    }
}

// Graphics adapters as Task Manager reads their temperature: the adapter's performance data from the graphics kernel (WDDM 2.4
// and later). Each adapter is opened once by its LUID and kept open; one that gives no temperature (a software renderer, most
// integrated adapters) is not asked again. The sampler asks only for the adapter the page shows, the busiest, so a dedicated
// adapter idling in a hybrid laptop is left alone.
internal sealed class GpuTemperatures : IDisposable
{
    // D3DKMT_ADAPTER_PERFDATA on 64-bit Windows: PhysicalAdapterIndex, then 8-byte figures; Temperature (tenths of a degree
    // Celsius) at 56, 64 bytes in all.
    private const int PerfDataSize = 64, TemperatureOffset = 56, AdapterPerfData = 62;
    private readonly Dictionary<string, uint> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly IntPtr _data = Marshal.AllocHGlobal(PerfDataSize);
    private bool _disposed;

    // The adapter by the counters' key ("luid_0x…_0x…"), or null without a temperature.
    public double? Read(string adapter)
    {
        if (_disposed) return null;
        if (!_adapters.TryGetValue(adapter, out uint handle)) _adapters[adapter] = handle = Open(adapter);
        if (handle == 0) return null;
        for (int i = 0; i < PerfDataSize; i += 8) Marshal.WriteInt64(_data, i, 0);
        var query = new QueryAdapterInfo { Adapter = handle, Type = AdapterPerfData, Data = _data, Size = PerfDataSize };
        try
        {
            if (D3DKMTQueryAdapterInfo(ref query) == 0) return Temperatures.FromDeciCelsius((uint)Marshal.ReadInt32(_data, TemperatureOffset));
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { Debug.WriteLine(ex); }
        // The driver does not report it (or the adapter went away): this adapter is left alone from now on.
        Close(handle); _adapters[adapter] = 0;
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
        foreach (var handle in _adapters.Values) Close(handle);
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
// one up, and so are removable and USB drives.
internal sealed class DiskTemperatures
{
    private const double Every = 30;
    private const int MaximumDrives = 16;
    private volatile IReadOnlyList<DiskTemperature> _latest = [];
    private double _readAt = double.NegativeInfinity;
    private Task? _reading;
    // Counts the pauses, so a reading still under way when the sampler paused does not bring its figures back.
    private volatile int _generation;
    // Whether any drive has reported a temperature since BrimDeck started; the page keeps a row for the drives from then on.
    public volatile bool Found;

    // The latest readings; a new reading is started when the last is old, and its result shows from the next tick.
    public IReadOnlyList<DiskTemperature> Sample(double now)
    {
        if (now - _readAt >= Every && _reading?.IsCompleted != false)
        {
            _readAt = now;
            int generation = _generation;
            _reading = Task.Run(() =>
            {
                var drives = Read();
                if (drives.Count > 0) Found = true;
                if (generation == _generation) _latest = drives;
            });
        }
        return _latest;
    }

    // Once the sampler pauses the readings grow old: the next start reads the drives again at once.
    public void Forget()
    {
        if (_readAt == double.NegativeInfinity && _latest.Count == 0) return;
        _generation++; _latest = []; _readAt = double.NegativeInfinity;
    }

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
