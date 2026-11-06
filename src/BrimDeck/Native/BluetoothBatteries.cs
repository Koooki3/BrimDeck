using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Threading;
using BrimDeck.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace BrimDeck.Native;

// Paired Bluetooth devices and the battery Windows itself reads for them, as Settings › Bluetooth shows it: the device node's
// DEVPKEY_Bluetooth_Battery ({104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2), which Windows keeps for hands-free headsets (the
// AT+IPHONEACCEV/+BIEV level on the HFP node) and for Bluetooth LE devices with a battery service (on the BTHLE node). Two
// watchers keep everything current without polling: the paired devices (association endpoints, for the name, the kind and
// whether each is connected) and the Bluetooth device nodes (for the level). Windows leaves the last level on a node after the
// device disconnects, so only connected devices are listed. A connected LE device whose node has no level is read through
// its GATT battery service (0x180F, level 0x2A19) instead, once when it connects and once a minute while the page shows: read
// only, nothing is written to the device.
internal sealed class BluetoothBatteries(Dispatcher dispatcher, Action<DeviceSource, IReadOnlyList<DeviceBattery>> report) : IDeviceSource
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string Connected = "System.Devices.Aep.IsConnected", Address = "System.Devices.Aep.DeviceAddress", Category = "System.Devices.Aep.Category",
        Appearance = "System.Devices.Aep.Bluetooth.Le.Appearance";
    private static readonly string[] EndpointProperties = [Connected, Address, Category, Appearance];
    // Categories that are not peripherals with a battery of their own worth listing.
    private static readonly string[] Ignored = ["Communication.Phone", "Computers", "Computer", "Display", "Network"];

    private sealed record Endpoint(string Id, string Name, string? Category, int? Appearance, bool Connected, bool LowEnergy);
    private readonly Dictionary<string, Endpoint> _endpoints = [];
    // Device node id → (address, level).
    private readonly Dictionary<string, (string Address, int? Level)> _nodes = [];
    private readonly Dictionary<string, GattBattery> _gatt = [];
    private readonly List<DeviceWatcher> _watchers = [];
    private DispatcherTimer? _poll;
    private DeviceDemand _demand;
    private bool _nodesListed;
    private int _generation;

    // Every watcher that started has listed what there is.
    public bool Listed => _listed >= _watchers.Count;
    private int _listed;

    public void Start(DeviceDemand demand)
    {
        bool running = _watchers.Count > 0;
        _demand = demand;
        if (!running)
        {
            // The watchers' first listing, and each GATT reader's first read, count as a reading for Refresh.
            _readAt = Environment.TickCount64;
            int generation = ++_generation;
            Watch(BluetoothDevice.GetDeviceSelectorFromPairingState(true), EndpointProperties, DeviceInformationKind.AssociationEndpoint, generation, lowEnergy: false);
            Watch(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), EndpointProperties, DeviceInformationKind.AssociationEndpoint, generation, lowEnergy: true);
            Watch("System.Devices.DeviceInstanceId:~<\"BTH\"", [BatteryKey], DeviceInformationKind.Device, generation, lowEnergy: null);
        }
        // The page and the collapsed reading poll the GATT levels.
        if (demand == DeviceDemand.Watch)
        {
            _poll ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = DevicePolling.Interval };
            _poll.Tick -= PollTick; _poll.Tick += PollTick; _poll.Start();
        }
        else _poll?.Stop();
        Publish();
    }

    public void Stop()
    {
        _generation++;
        foreach (var watcher in _watchers)
        {
            try { if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop(); } catch { }
        }
        _watchers.Clear();
        _poll?.Stop();
        foreach (var gatt in _gatt.Values) gatt.Dispose();
        _gatt.Clear(); _endpoints.Clear(); _nodes.Clear(); _nodesListed = false; _listed = 0;
        report(DeviceSource.Bluetooth, []); report(DeviceSource.Gatt, []);
    }

    // The page opened: read again, unless the last reading is under a minute old (each GATT read wakes the device over the
    // air). The minute's poll then counts from this reading.
    public void Refresh()
    {
        if (_demand != DeviceDemand.Watch || Environment.TickCount64 - _readAt < (long)DevicePolling.Interval.TotalMilliseconds) return;
        ReadAll();
        if (_poll is { IsEnabled: true }) { _poll.Stop(); _poll.Start(); }
    }
    private long _readAt;

    // The GATT levels, and Windows' own levels for the connected devices: whether the node watcher reports a level that changes
    // is not documented, so they are read again too (a property read, about half a millisecond each).
    private void ReadAll()
    {
        if (_demand != DeviceDemand.Watch) return;
        _readAt = Environment.TickCount64;
        foreach (var gatt in _gatt.Values) gatt.Read();
        var connected = _endpoints.Values.Where(endpoint => endpoint.Connected).Select(endpoint => Devices.AddressIn(endpoint.Id)).ToHashSet();
        int generation = _generation;
        foreach (var (id, node) in _nodes.Where(node => node.Value.Level is not null && connected.Contains(node.Value.Address)).ToList()) Reread(id, generation);
    }
    private async void Reread(string id, int generation)
    {
        Dictionary<string, object?>? properties = null;
        try { properties = Copy((await DeviceInformation.CreateFromIdAsync(id, [BatteryKey], DeviceInformationKind.Device)).Properties); }
        catch { }
        await dispatcher.InvokeAsync(() => { if (properties is not null && generation == _generation && _nodes.ContainsKey(id)) Node(id, properties, update: true); });
    }
    private void PollTick(object? sender, EventArgs e) => ReadAll();

    private void Watch(string aqs, string[] properties, DeviceInformationKind kind, int generation, bool? lowEnergy)
    {
        DeviceWatcher watcher;
        try { watcher = DeviceInformation.CreateWatcher(aqs, properties, kind); }
        catch { return; }
        void Post(Action action) => dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (generation == _generation) action(); });
        bool node = lowEnergy is null;
        watcher.Added += (_, info) => { var id = info.Id; var name = info.Name; var props = Copy(info.Properties); Post(() => { if (node) Node(id, props); else Add(id, name, props, lowEnergy == true); }); };
        watcher.Updated += (_, update) => { var id = update.Id; var props = Copy(update.Properties); Post(() => { if (node) Node(id, props, update: true); else Update(id, props); }); };
        watcher.Removed += (_, update) => { var id = update.Id; Post(() => { if (node) _nodes.Remove(id); else _endpoints.Remove(id); Publish(); }); };
        watcher.EnumerationCompleted += (_, _) => Post(() => { if (node) _nodesListed = true; _listed++; Publish(); });
        try { watcher.Start(); _watchers.Add(watcher); } catch { }
    }
    private static Dictionary<string, object?> Copy(IReadOnlyDictionary<string, object> properties) => properties.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);

    private void Add(string id, string name, Dictionary<string, object?> properties, bool lowEnergy)
    {
        _endpoints[id] = new Endpoint(id, name, Text(properties, Category), properties.TryGetValue(Appearance, out var a) && a is ushort raw ? raw : null,
            properties.TryGetValue(Connected, out var c) && c is true, lowEnergy);
        Publish();
    }
    private void Update(string id, Dictionary<string, object?> properties)
    {
        if (!_endpoints.TryGetValue(id, out var known)) return;
        if (properties.TryGetValue(Connected, out var c)) known = known with { Connected = c is true };
        if (properties.TryGetValue(Category, out _)) known = known with { Category = Text(properties, Category) };
        _endpoints[id] = known;
        Publish();
    }
    private void Node(string id, Dictionary<string, object?> properties, bool update = false)
    {
        if (Devices.AddressIn(id) is not { } address) return;
        bool has = properties.TryGetValue(BatteryKey, out var value);
        int? level = has && value is byte b ? b : has && value is int i ? i : null;
        // An update that does not carry the level keeps the one known.
        if (update && !has && _nodes.TryGetValue(id, out var known)) level = known.Level;
        if (level is null && !_nodes.ContainsKey(id) && !update) { _nodes[id] = (address, null); return; }
        if (_nodes.TryGetValue(id, out var was) && was == (address, level)) return;
        _nodes[id] = (address, level);
        Publish();
    }
    private static string? Text(Dictionary<string, object?> properties, string key) => properties.TryGetValue(key, out var value) ? value switch
    {
        string text => text, string[] list => string.Join(",", list), _ => null
    } : null;

    private void Publish()
    {
        var plain = new List<DeviceBattery>();
        var gatt = new List<DeviceBattery>();
        var wanted = new HashSet<string>();
        foreach (var endpoint in _endpoints.Values)
        {
            if (!endpoint.Connected || endpoint.Category is { } category && Ignored.Any(ignored => category.Contains(ignored, StringComparison.OrdinalIgnoreCase))) continue;
            string? address = Devices.AddressIn(endpoint.Id);
            if (address is null) continue;
            int? level = _nodes.Values.Where(node => node.Address == address && node.Level is not null).Select(node => node.Level).FirstOrDefault();
            string? hardware = _nodes.Where(node => node.Value.Address == address).Select(node => Devices.HardwareIn(node.Key)).FirstOrDefault(found => found is not null);
            var kind = Devices.Kind(endpoint.Category, endpoint.Appearance, endpoint.Name);
            // A connected LE device without Windows' level is read through its battery service, once the nodes are listed.
            if (level is null && endpoint.LowEnergy && _nodesListed && _demand == DeviceDemand.Watch)
            {
                wanted.Add(address);
                if (!_gatt.TryGetValue(address, out var reader))
                {
                    reader = new GattBattery(ulong.Parse(address, System.Globalization.NumberStyles.HexNumber), dispatcher, () => Publish());
                    _gatt[address] = reader; reader.Read();
                }
                if (reader.Level is { } read) { gatt.Add(new DeviceBattery("bt:" + address, endpoint.Name, kind, DeviceSource.Gatt, Devices.Clamp(read)) { Address = address, Hardware = hardware }); continue; }
                if (!reader.Done) continue;
            }
            // A device that reports nothing shows as not available, unless it is not a kind worth listing.
            if (level is null && kind == DeviceKind.Other) continue;
            plain.Add(new DeviceBattery("bt:" + address, endpoint.Name, kind, DeviceSource.Bluetooth, level is { } l ? Devices.Clamp(l) : null) { Address = address, Hardware = hardware });
        }
        foreach (var gone in _gatt.Keys.Where(address => !wanted.Contains(address)).ToList()) { _gatt[gone].Dispose(); _gatt.Remove(gone); }
        report(DeviceSource.Bluetooth, plain);
        report(DeviceSource.Gatt, gatt);
    }

    public void Dispose() => Stop();

    // One LE device's battery service, read on demand. Each read opens the device, reads the level and lets the device go again,
    // so BrimDeck never holds a connection open and the device sleeps as it would without BrimDeck. Done once the first read has
    // finished, with or without a level.
    private sealed class GattBattery(ulong address, Dispatcher dispatcher, Action changed) : IDisposable
    {
        private bool _reading, _disposed;
        public int? Level { get; private set; }
        public bool Done { get; private set; }

        public async void Read()
        {
            if (_reading || _disposed) return;
            _reading = true;
            int? level = null;
            BluetoothLEDevice? device = null;
            GattDeviceServicesResult? services = null;
            try
            {
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                if (device is not null && !_disposed)
                {
                    services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached);
                    if (services.Status == GattCommunicationStatus.Success && services.Services.Count > 0)
                    {
                        var characteristics = await services.Services[0].GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached);
                        if (characteristics.Status == GattCommunicationStatus.Success && characteristics.Characteristics.Count > 0 && !_disposed)
                        {
                            var result = await characteristics.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
                            if (result.Status == GattCommunicationStatus.Success && result.Value.Length > 0) level = result.Value.ToArray()[0];
                        }
                    }
                }
            }
            catch { }
            finally
            {
                try { if (services is not null) foreach (var service in services.Services) service.Dispose(); device?.Dispose(); } catch { }
            }
            await dispatcher.InvokeAsync(() =>
            {
                _reading = false;
                if (_disposed) return;
                bool moved = level is not null && level != Level || !Done;
                if (level is not null) Level = level;
                Done = true;
                if (moved) changed();
            });
        }

        public void Dispose() => _disposed = true;
    }
}

// How often the sources without events of their own are read while the page or the collapsed reading shows. A battery moves a
// point in minutes; a minute keeps the page current without waking the devices more than their own software does.
internal static class DevicePolling
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
}
