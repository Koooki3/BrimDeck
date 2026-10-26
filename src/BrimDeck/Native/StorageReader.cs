using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BrimDeck.Core;

namespace BrimDeck.Native;

// The internal drives for the system monitor (Storage): every fixed volume with a letter, its size and free space from the file
// system, the physical drive it lies on from the volume's disk extents, and what Windows says of that drive (model, bus,
// removable media). Optionally each solid-state drive's own temperature through the storage stack's temperature property. Each
// volume and drive is opened without read or write access, which a normal user may do; nothing here needs administrator rights.
// Asking a drive for its temperature sends it a command (a few to over a hundred milliseconds, and it leaves its deepest power
// state), and listing the volumes can wait on a slow drive, so everything is read on the thread pool: the volumes every 10
// seconds, the temperatures every 30, never on a tick. Hard disks are not asked for a temperature, as asking could spin one up.
// The readings and states are kept while the sampler pauses, so a page opened again has its drives at once.
internal sealed class StorageReader
{
    private const double Every = 10, TemperatureEvery = 30;
    private const int MaximumDrives = 64;
    private volatile IReadOnlyList<StorageDrive> _plain = [], _heated = [];
    private volatile SensorState _drives, _temperatures;
    private volatile int _count = -1;
    private double _readAt = double.NegativeInfinity;
    // Touched only by the reading under way.
    private double _temperaturesAt = double.NegativeInfinity;
    private Dictionary<int, DiskTemperature> _known = [];
    private int _running;
    private volatile Task _reading = Task.CompletedTask;

    // Whether any internal drive with a lettered volume was found, and how many; whether any drive gave a temperature.
    public SensorState Drives => _drives;
    public int Count => _count;
    public SensorState Temperatures => _temperatures;
    // The reading under way, or a finished task.
    public Task Pending => _reading;

    // Starts a reading on the thread pool unless one is under way; temperatures: ask the drives for theirs too when they are due.
    public void Start(bool temperatures)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _reading = Task.Run(() =>
        {
            try { Read(temperatures, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine(ex); }
            finally { Volatile.Write(ref _running, 0); }
        });
    }

    // The latest drives, with their temperatures when asked for; a new reading starts when the last is old, and its result shows
    // from the next tick.
    public IReadOnlyList<StorageDrive> Sample(double now, bool temperatures)
    {
        // Temperatures asked for after the drives were read without them are read at once rather than at the next reading.
        if (now - _readAt >= Every || temperatures && _temperatures == SensorState.Unknown && _drives != SensorState.Unknown)
        {
            _readAt = now; Start(temperatures);
        }
        return temperatures ? _heated : _plain;
    }

    // The sampler paused: the next sample reads again at once. The last readings stay until then.
    public void Rest() => _readAt = double.NegativeInfinity;

    private void Read(bool temperatures, double now)
    {
        var volumes = Volumes();
        var numbers = volumes.Select(item => item.Disk).OfType<int>().Distinct().ToList();
        var disks = new Dictionary<int, StorageDisk>();
        var solid = new HashSet<int>();
        var query = Marshal.AllocHGlobal(QuerySize);
        var output = Marshal.AllocHGlobal(OutputSize);
        try
        {
            foreach (int number in numbers)
            {
                var device = Open($@"\\.\PhysicalDrive{number}");
                if (device == InvalidHandle) continue;
                try
                {
                    if (Describe(device, query, output) is not { } disk) continue;
                    disks[number] = disk;
                    if (Core.Storage.Internal(disk) && SolidState(device, query, output)) solid.Add(number);
                }
                finally { CloseHandle(device); }
            }
            if (temperatures && (now - _temperaturesAt >= TemperatureEvery || _temperatures == SensorState.Unknown))
            {
                _temperaturesAt = now;
                var known = new Dictionary<int, DiskTemperature>();
                foreach (int number in solid)
                {
                    var device = Open($@"\\.\PhysicalDrive{number}");
                    if (device == InvalidHandle) continue;
                    try { if (Temperature(device, query, output, disks[number].Model) is { } heat) known[number] = heat with { Number = number }; }
                    finally { CloseHandle(device); }
                }
                _known = known;
                if (known.Count > 0) _temperatures = SensorState.Found; else if (_temperatures == SensorState.Unknown) _temperatures = SensorState.None;
            }
        }
        finally { Marshal.FreeHGlobal(query); Marshal.FreeHGlobal(output); }
        // A volume whose drive could not be described is left out with it.
        var described = volumes.Where(item => item.Disk is not { } number || disks.ContainsKey(number)).ToList();
        var plain = Core.Storage.Group(described, disks);
        _plain = plain;
        _heated = Core.Storage.Group(described, disks, _known);
        _count = plain.Count;
        if (plain.Count > 0) _drives = SensorState.Found; else if (_drives == SensorState.Unknown) _drives = SensorState.None;
    }

    // Every fixed volume with a letter that is ready, with the drive it lies on: null when its extents span drives or cannot be read.
    public static List<(StorageVolume Volume, int? Disk)> Volumes()
    {
        var volumes = new List<(StorageVolume, int?)>();
        var extents = Marshal.AllocHGlobal(ExtentsSize);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    string letter = drive.Name.TrimEnd('\\');
                    string label = "";
                    try { label = drive.VolumeLabel; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    var volume = new StorageVolume(letter, label, (ulong)Math.Max(0, drive.TotalSize), (ulong)Math.Max(0, drive.TotalFreeSpace));
                    volumes.Add((volume, Disk(letter, extents)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
            }
        }
        finally { Marshal.FreeHGlobal(extents); }
        return volumes;
    }

    // VOLUME_DISK_EXTENTS: NumberOfDiskExtents, padding, then DISK_EXTENT entries of 24 bytes from 8 (DiskNumber at 0). A volume on
    // one drive has its extents all on it.
    private static int? Disk(string letter, IntPtr extents)
    {
        var volume = Open($@"\\.\{letter}");
        if (volume == InvalidHandle) return null;
        try
        {
            if (!DeviceIoControl(volume, VolumeDiskExtents, IntPtr.Zero, 0, extents, ExtentsSize, out int length, IntPtr.Zero) || length < 32) return null;
            int count = Marshal.ReadInt32(extents);
            if (count < 1 || 8 + count * 24 > length) return null;
            int first = Marshal.ReadInt32(extents, 8);
            for (int i = 1; i < count; i++) if (Marshal.ReadInt32(extents, 8 + i * 24) != first) return null;
            return first is >= 0 and < MaximumDrives ? first : null;
        }
        finally { CloseHandle(volume); }
    }

    // STORAGE_DEVICE_DESCRIPTOR: RemovableMedia at 10, the offset of ProductId at 16, BusType at 28.
    private static StorageDisk? Describe(IntPtr device, IntPtr query, IntPtr output)
    {
        if (!Property(device, query, output, DeviceProperty, out int length) || length < 32) return null;
        int productAt = Marshal.ReadInt32(output, 16);
        string model = productAt > 0 && productAt < length ? Marshal.PtrToStringAnsi(output + productAt)?.Trim() ?? "" : "";
        return new StorageDisk(model, Marshal.ReadInt32(output, 28), Marshal.ReadByte(output, 10) != 0);
    }

    // DEVICE_SEEK_PENALTY_DESCRIPTOR: IncursSeekPenalty at 8, true for a spinning disk. A drive that does not say is taken for a disk.
    private static bool SolidState(IntPtr device, IntPtr query, IntPtr output) =>
        Property(device, query, output, SeekPenaltyProperty, out int length) && length >= 9 && Marshal.ReadByte(output, 8) == 0;

    // STORAGE_TEMPERATURE_DATA_DESCRIPTOR: CriticalTemperature at 8, WarningTemperature at 10, InfoCount at 12, then the
    // STORAGE_TEMPERATURE_INFO entries from 24, 16 bytes each, Temperature at 2. The first is the drive's own.
    private static DiskTemperature? Temperature(IntPtr device, IntPtr query, IntPtr output, string model)
    {
        if (!Property(device, query, output, TemperatureProperty, out int length) || length < 40 || Marshal.ReadInt16(output, 12) < 1) return null;
        return Core.Temperatures.Disk(model.Length > 0 ? model : "SSD", Marshal.ReadInt16(output, 26), Marshal.ReadInt16(output, 10), Marshal.ReadInt16(output, 8));
    }

    // STORAGE_PROPERTY_QUERY: PropertyId, QueryType (0, the standard query), one byte of parameters.
    private static bool Property(IntPtr device, IntPtr query, IntPtr output, int property, out int length)
    {
        Marshal.WriteInt32(query, 0, property); Marshal.WriteInt32(query, 4, 0); Marshal.WriteInt32(query, 8, 0);
        return DeviceIoControl(device, QueryProperty, query, QuerySize, output, OutputSize, out length, IntPtr.Zero);
    }

    private static IntPtr Open(string path) => CreateFileW(path, 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    private const int QuerySize = 12, OutputSize = 1024, ExtentsSize = 8 + 24 * 32;
    private const int DeviceProperty = 0, SeekPenaltyProperty = 7, TemperatureProperty = 52;
    private const uint QueryProperty = 0x2D1400, VolumeDiskExtents = 0x560000, ShareReadWrite = 3, OpenExisting = 3;
    private static readonly IntPtr InvalidHandle = new(-1);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
