using System.IO;
using System.Windows.Threading;
using BrimDeck.Core;
using Windows.Devices.Enumeration;

namespace BrimDeck.Native;

// The batteries that only the devices' own protocols tell, over HID: Logitech receivers and cables (HID++), Razer mice, the
// 2.4 GHz mice and keyboards of ATK, VXE, VGN, MCHOSE, Endgame Gear, VAXEE, Inphic and Nordic receivers, SteelSeries, HyperX
// and MCHOSE headsets, PlayStation controllers, and any device that carries the standard HID battery usages. The protocols
// themselves are in Core (DeviceProtocols.cs); this sends them. Every request only reads: nothing is set on any device.
// One worker thread at below normal priority does the I/O, so a slow device never holds the panel: once a minute while the
// page or the collapsed reading shows (DeviceDemand.Watch), and not at all otherwise, when it lets every handle go and ends
// (the next start makes a new one). A device plugged in while the page shows is read within a moment (a watcher over the HID
// collections). On Bluetooth, only the collections a reader here speaks to are opened (Spoken).
internal sealed class HidBatteries(Dispatcher dispatcher, Action<DeviceSource, IReadOnlyList<DeviceBattery>> report) : IDeviceSource
{
    private static readonly DeviceSource[] Sources = [DeviceSource.Logitech, DeviceSource.Razer, DeviceSource.SteelSeries, DeviceSource.HyperX, DeviceSource.Sony, DeviceSource.Hid];
    private readonly AutoResetEvent _wake = new(false);
    // Guards _thread between Start and the worker ending itself.
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile DeviceDemand _demand;
    private volatile bool _disposed;
    private DeviceWatcher? _arrivals;
    private int _generation;
    // The worker's own: each collection's attributes by path, and what each Logitech device slot was found to be.
    private readonly Dictionary<string, HidInfo> _infos = [];
    private readonly LogitechReader _logitech = new();
    private readonly Dictionary<string, (DeviceBattery Device, int Missed)> _lastRead = [];
    // Set once a pass over the collections has finished since the start.
    private volatile bool _listed;
    public bool Listed => _listed;

    // Started, the worker reads at once unless its last full pass is under a minute old.
    public void Start(DeviceDemand demand)
    {
        var was = _demand;
        lock (_gate)
        {
            _demand = demand;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "BrimDeck HID batteries" };
                _thread.Start();
            }
        }
        if (demand == DeviceDemand.Watch) WatchArrivals(); else StopArrivals();
        // Any change wakes the worker, which reads only what its own intervals allow (a minute between full passes).
        if (demand != was) Wake();
    }

    public void Stop()
    {
        _demand = DeviceDemand.None;
        // Started again before the worker has noticed, it lists afresh rather than from where it was.
        _listed = false; Interlocked.Exchange(ref _lastScan, Never); _restart = true;
        StopArrivals();
        Wake();
        foreach (var source in Sources) report(source, []);
    }

    // The page opened: read now, unless the last full pass is under a minute old (no HID++ request comes faster than that).
    public void Refresh() { if (_demand == DeviceDemand.Watch && Stale) Wake(); }

    public void Dispose()
    {
        _disposed = true; _demand = DeviceDemand.None;
        StopArrivals();
        Wake();
    }

    // The worker's event lives as long as the process: set from the UI thread and the watcher's threads, it is never disposed
    // under them.
    private void Wake() => _wake.Set();
    // When the last full pass ended (Environment.TickCount64); Never before the first.
    private const long Never = -1;
    private long _lastScan = Never;
    private volatile bool _arrived;
    // The worker's own: whether it has made a full pass since it started (_listed waits for the UI thread to take the result).
    private bool _scanned;
    private volatile bool _restart;
    private long SinceScan => Interlocked.Read(ref _lastScan) is var at && at == Never ? long.MaxValue : Environment.TickCount64 - at;
    private bool Stale => SinceScan >= (long)DevicePolling.Interval.TotalMilliseconds;

    // HID collections that come or go while the page shows wake the worker; the first listing (every collection there is) does not.
    private void WatchArrivals()
    {
        if (_arrivals is not null) return;
        int generation = Interlocked.Increment(ref _generation);
        try
        {
            var watcher = DeviceInformation.CreateWatcher("System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\" AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True",
                [], DeviceInformationKind.DeviceInterface);
            bool listed = false;
            // Set and read on the watcher's threads.
            watcher.EnumerationCompleted += (_, _) => Volatile.Write(ref listed, true);
            watcher.Added += (_, _) => { if (Volatile.Read(ref listed) && generation == Volatile.Read(ref _generation)) { _arrived = true; Wake(); } };
            watcher.Removed += (_, _) => { if (Volatile.Read(ref listed) && generation == Volatile.Read(ref _generation)) { _arrived = true; Wake(); } };
            // A watcher that does not handle updates stops reporting arrivals after its first listing.
            watcher.Updated += (_, _) => { };
            watcher.Start();
            _arrivals = watcher;
        }
        catch { }
    }
    private void StopArrivals()
    {
        Interlocked.Increment(ref _generation);
        try { if (_arrivals?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _arrivals.Stop(); } catch { }
        _arrivals = null;
    }

    private void Run()
    {
        while (!_disposed)
        {
            try { if (!Pass()) return; }
            catch { _wake.WaitOne(DevicePolling.Interval); }
        }
    }

    // One round of the worker: a pass if one is due, then the wait for the next. False once nothing is wanted: the thread ends.
    private bool Pass()
    {
        var demand = _demand;
        if (_restart) { _restart = false; _scanned = false; }
        if (demand != DeviceDemand.None)
        {
            // A burst of arrivals (a receiver brings several collections) is read once.
            Thread.Sleep(300);
            var interval = DevicePolling.Interval;
            bool full = SinceScan >= (long)interval.TotalMilliseconds || !_scanned;
            // Between full passes, a collection that arrived is read alone; the devices already known are not asked again.
            bool arrivals = _arrived; _arrived = false;
            if (full || arrivals)
            {
                Dictionary<DeviceSource, List<DeviceBattery>>? found = null;
                try { found = Scan(onlyNew: !full); }
                catch { }
                // A full pass counts as done even when it failed, so a failing one is tried again at the next interval, not at once.
                if (full) { Interlocked.Exchange(ref _lastScan, Environment.TickCount64); _scanned = true; }
                // A pass that failed as a whole reports nothing: the last readings stay.
                if (found is not null && _demand != DeviceDemand.None && !_disposed)
                    dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        if (_demand == DeviceDemand.None || _disposed) return;
                        foreach (var source in Sources) report(source, found.TryGetValue(source, out var list) ? list : []);
                        _listed = true;
                    });
            }
        }
        else
        {
            _infos.Clear(); _logitech.Clear(); _lastRead.Clear(); _previous.Clear(); _slow.Clear(); _listed = false; _scanned = false; Interlocked.Exchange(ref _lastScan, Never);
            // Nothing is wanted: the thread ends rather than wait, and the next Start makes a new one. Started again meanwhile,
            // it goes round at once.
            lock (_gate)
            {
                if (_demand != DeviceDemand.None) return true;
                _thread = null;
                return false;
            }
        }
        _wake.WaitOne(TimeSpan.FromMilliseconds(Math.Clamp(DevicePolling.Interval.TotalMilliseconds - Math.Min(SinceScan, int.MaxValue), 1000, DevicePolling.Interval.TotalMilliseconds)));
        return true;
    }

    // ----- One pass over the collections -----

    // The last full pass's readings, which a pass over new collections alone adds to.
    private Dictionary<DeviceSource, List<DeviceBattery>> _previous = [];
    // Collections whose feature reports timed out, left alone until then (Environment.TickCount64).
    private readonly Dictionary<string, long> _slow = [];

    private Dictionary<DeviceSource, List<DeviceBattery>> Scan(bool onlyNew)
    {
        var found = onlyNew ? _previous.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()) : new Dictionary<DeviceSource, List<DeviceBattery>>();
        void Add(DeviceBattery device)
        {
            if (!found.TryGetValue(device.Source, out var list)) found[device.Source] = list = [];
            if (list.All(known => known.Key != device.Key)) list.Add(device);
        }
        var paths = Hid.Paths();
        foreach (var gone in _infos.Keys.Except(paths).ToList()) _infos.Remove(gone);
        var infos = new List<HidInfo>();
        foreach (var path in paths)
        {
            bool known = _infos.TryGetValue(path, out var info);
            if (!known)
            {
                // A Bluetooth collection no reader here speaks to is not opened at all (its level comes from Windows). One that
                // is has its name and serial read as on USB: the serial keeps two pads of one model apart (a classic Bluetooth
                // path carries no address).
                bool bluetooth = Hid.IsBluetooth(path);
                if (bluetooth && !Spoken(Hid.Ids(path))) continue;
                // A collection that could not be opened (its driver still starting) is tried again next time.
                info = Hid.Query(path, batteries: !bluetooth);
                if (info is not null) _infos[path] = info;
            }
            if (info is not null && (!onlyNew || !known)) infos.Add(info);
        }
        // Between full passes the last readings stand, without the devices whose collections have gone (a receiver pulled out).
        if (onlyNew)
        {
            foreach (var list in found.Values) list.RemoveAll(device => !Present(device.Key));
            if (infos.Count == 0) return _previous = found;
        }
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Try(Func<IEnumerable<DeviceBattery>> read)
        {
            try { foreach (var device in read()) Add(device); }
            catch { }
        }

        Try(() => _logitech.Read(infos.Where(info => info.Vendor == Hidpp.Vendor && info.UsagePage == 0xFF00).ToList(), claimed));
        foreach (var group in infos.Where(info => info.Vendor == Razer.Vendor).GroupBy(info => info.Product))
            if (Razer.Find(group.Key) is { } model) Try(() => ReadRazer(model, [.. group], claimed));
        foreach (var info in infos)
        {
            if (claimed.Contains(info.Path)) continue;
            if (Headsets.Find(info.Vendor, info.Product) is { } headset) { if (Matches(info, Headsets.Channel(headset))) Try(() => ReadHeadset(headset, info, claimed)); continue; }
            if (info.Vendor == PlayStation.Vendor && (PlayStation.IsDualSense(info.Product) || PlayStation.IsDualShock(info.Product)) && info.UsagePage == 0x01 && info.Usage == 0x05)
            { Try(() => ReadPlayStation(info, claimed)); continue; }
            if (Dongle(info) is { } dongle) Try(() => dongle(info, claimed));
        }
        // The standard battery usages, on whatever no vendor protocol read. Bluetooth collections are left out (their level comes
        // from Windows, and a feature report over the air can wait on a sleeping device), and so is one that timed out lately.
        // So are power devices (a UPS on the Power Device or Battery System page, 0x84 and 0x85), which are not wireless
        // devices and often count their charge in capacity units rather than percent.
        long now = Environment.TickCount64;
        foreach (var info in infos.Where(info => info.FeatureBatteries.Count > 0 && !claimed.Contains(info.Path) && !info.Bluetooth && info.UsagePage is not (0x84 or 0x85)
            && (!_slow.TryGetValue(info.Path, out var until) || now >= until)))
        {
            try { foreach (var device in ReadUsage(info)) Add(device); }
            catch (TimeoutException) { _slow[info.Path] = now + 10 * 60_000; }
            catch { }
        }
        if (onlyNew) return _previous = found;

        // A device that missed one reading (asleep for a moment, busy with its own software) keeps its last one until the next
        // pass; missing twice, it is gone.
        foreach (var list in found.Values) foreach (var device in list) _lastRead[device.Key] = (device, 0);
        foreach (var (key, (device, missed)) in _lastRead.ToList())
        {
            if (found.Values.SelectMany(list => list).Any(seen => seen.Key == key)) continue;
            if (missed >= 1 || !Present(key)) { _lastRead.Remove(key); continue; }
            _lastRead[key] = (device, missed + 1);
            Add(device);
        }
        _previous = found;
        return found;
    }
    // Whether a collection still present belongs to the device a key names (its vendor and product are in the key; a Logitech
    // device's key names its unit, so any Logitech collection will do).
    private static bool KeyOwner(HidInfo info, string key) => key.Contains($"{info.Vendor:X4}:{info.Product:X4}", StringComparison.Ordinal);
    // A Logitech device's key names its unit, not its receiver: it is present while the receiver it was read through is.
    private bool Present(string key) => key.StartsWith("logitech:", StringComparison.Ordinal)
        ? _logitech.Receiver(key) is { } group && _infos.Keys.Any(path => GroupKey(path) == group)
        : _infos.Values.Any(info => KeyOwner(info, key));

    // Whether a reader here speaks to a vendor and product over Bluetooth (from the path, before it is opened): PlayStation
    // controllers, the headsets and Razer's models. Logitech's Bluetooth collections carry HID++ on a page the receiver reader
    // does not use, and the 2.4 GHz brands are read on their dongles only, so neither is opened over the air.
    internal static bool Spoken((int Vendor, int Product) ids) => ids.Vendor == PlayStation.Vendor
        || ids.Vendor == Razer.Vendor && Razer.Find(ids.Product) is not null || Headsets.Find(ids.Vendor, ids.Product) is not null;

    // Headsets.Channel: an interface, a usage page, or (VendorPages) any vendor-defined page.
    private static bool Matches(HidInfo info, (int Interface, int UsagePage) channel)
        => (channel.Interface < 0 || info.Interface == channel.Interface)
        && (channel.UsagePage == Headsets.VendorPages ? info.UsagePage >= 0xFF00 : channel.UsagePage < 0 || info.UsagePage == channel.UsagePage);

    // A stable key for a device on a collection: its vendor and product, and its serial where it has one, else its Bluetooth
    // address where the path carries one (LE). Not the path, which names the USB port: a receiver moved to another port is the same
    // device, still switched off. The price: two receivers of one model without serials are listed as one device.
    private static string Key(string prefix, HidInfo info)
        => $"{prefix}:{info.Vendor:X4}:{info.Product:X4}" + (info.Serial.Length > 0 ? ":" + info.Serial : AddressOf(info) is { } address ? ":" + address : "");
    internal static string GroupKey(string path)
    {
        var p = System.Text.RegularExpressions.Regex.Replace(path.ToLowerInvariant(), "&col[0-9a-f]+", "");
        p = System.Text.RegularExpressions.Regex.Replace(p, "&mi_[0-9a-f]+", "");
        return System.Text.RegularExpressions.Regex.Replace(p, @"&[0-9a-f]{4}#\{", "#{");
    }
    private static string NameOf(HidInfo info, string fallback) => info.ProductName.Length > 0 ? info.ProductName : fallback;
    private static string? AddressOf(HidInfo info) => info.Bluetooth ? Devices.AddressIn(info.Path) : null;

    // ----- Razer (razer-battery-report's flow: the battery, then charging where the model reports it) -----

    private IEnumerable<DeviceBattery> ReadRazer(Razer.Model model, List<HidInfo> collections, HashSet<string> claimed)
    {
        // On the model's interface: the mouse collection first, then the vendor one; the first that answers.
        var candidates = collections.Where(info => info.Interface < 0 || info.Interface == model.Interface)
            .OrderBy(info => info.UsagePage == 0x01 && info.Usage == 0x02 ? 0 : info.UsagePage >= 0xFF00 ? 1 : 2).ToList();
        foreach (var info in candidates)
        {
            HidDevice device;
            try { device = new HidDevice(info, readWrite: false); }
            catch { continue; }
            using (device)
            {
                var battery = RazerQuery(device, model, Razer.Battery);
                if (battery is null) continue;
                bool charging = model.Charging && RazerQuery(device, model, Razer.ChargingState) is { } state && state != 0;
                foreach (var each in collections) claimed.Add(each.Path);
                yield return new DeviceBattery(Key("razer", info), model.Name, DeviceKind.Mouse, DeviceSource.Razer, Razer.Percent(battery.Value))
                { Charging = charging, Hardware = Devices.Hardware(info.Vendor, info.Product) };
                yield break;
            }
        }
    }
    private static byte? RazerQuery(HidDevice device, Razer.Model model, (byte Class, byte Id, byte Size) command)
    {
        var request = new byte[Razer.ReportLength + 1];
        Razer.Request(model.Transaction, command).CopyTo(request, 1);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0) Thread.Sleep(10);
            if (!device.SetFeature(request)) return null;
            Thread.Sleep(Math.Max(1, model.WaitMicroseconds / 1000));
            var reply = device.GetFeature(0, Math.Max(device.FeatureLength, Razer.ReportLength + 1));
            if (reply is null || reply.Length < Razer.ReportLength + 1) return null;
            if (Razer.Reply(reply[1..(Razer.ReportLength + 1)], command) is not { } answer) continue;
            if (answer.Status == Razer.Success) return answer.Value;
            if (answer.Status is not (Razer.Busy or Razer.NoResponse)) return null;
        }
        return null;
    }

    // ----- Headsets on their own dongles -----

    private static IEnumerable<DeviceBattery> ReadHeadset(Headsets.Kind kind, HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info) { StripZero = true };
        // A dongle whose headset is off is not read: its answer to the level could be a stale one.
        if (Headsets.ConnectionRequest(kind) is { } ask
            && Hid.Transact<object>([device], () => device.Write(ask), r => Headsets.Connected(kind, r) is { } connected ? connected : null, 600) is false) return [];
        var reading = Hid.Transact<object>([device], () => device.Write(Headsets.Request(kind)), r => Headsets.Battery(kind, r) is { } read ? read : null, 600);
        if (reading is not BatteryRead battery) return [];
        bool charging = battery.Charging;
        if (Headsets.ChargingRequest(kind) is { } second)
            charging |= Hid.Transact<object>([device], () => device.Write(second), r => Headsets.Charging(kind, r) is { } state ? state : null, 600) is true;
        claimed.Add(info.Path);
        return [new DeviceBattery(Key("headset", info), NameOf(info, Headsets.Name(kind)), DeviceKind.Headphones, Headsets.Source(kind), battery.Percent)
            { Charging = charging, Coarse = battery.Coarse, Hardware = Devices.Hardware(info.Vendor, info.Product) }];
    }

    // ----- PlayStation controllers: a few input reports, read as they come -----

    private static IEnumerable<DeviceBattery> ReadPlayStation(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        claimed.Add(info.Path);
        long deadline = Environment.TickCount64 + 300;
        while (Environment.TickCount64 < deadline)
        {
            if (device.Read((int)Math.Max(1, deadline - Environment.TickCount64)) is not { } report) break;
            if (PlayStation.Battery(info.Product, report, info.Bluetooth) is not { } battery) continue;
            string name = PlayStation.IsDualSense(info.Product) ? "DualSense Wireless Controller" : "DUALSHOCK 4 Wireless Controller";
            return [new DeviceBattery(Key("ps", info), NameOf(info, name), DeviceKind.Controller, DeviceSource.Sony, battery.Percent)
                { Charging = battery.Charging, Hardware = Devices.Hardware(info.Vendor, info.Product), Address = AddressOf(info) }];
        }
        // A controller on Bluetooth in its short reports: connected, but without a reading until a game asks for full reports.
        return [new DeviceBattery(Key("ps", info), NameOf(info, "PlayStation Controller"), DeviceKind.Controller, DeviceSource.Sony, null)
            { Hardware = Devices.Hardware(info.Vendor, info.Product), Address = AddressOf(info) }];
    }

    // ----- The 2.4 GHz brands -----

    // On their dongles only: a request is never sent to a Bluetooth collection that happens to share a vendor and page.
    private static Func<HidInfo, HashSet<string>, IEnumerable<DeviceBattery>>? Dongle(HidInfo info) => info.Bluetooth ? null : (info.Vendor, info.UsagePage) switch
    {
        (Dongles.AtkVendor, 0xFF05) => BitMouse,
        (Dongles.CompxVendor or Dongles.AtkVendor, 0xFF02 or 0xFF04) => Compx,
        (Dongles.VgnVendor, 0xFF1C) when info.Product == 0x5088 => Vgn,
        (Dongles.EndgameVendor, 0xFF01) => Endgame,
        (Dongles.VaxeeVendor, 0xFF05) => Vaxee,
        (Dongles.NordicVendor, 0xFFA0) => Nordic,
        (Dongles.InphicVendor, _) when info.Product is 0x5B2E or 0x5B2F && info.FeatureLength >= 33 => Inphic,
        // The 2.4 GHz receiver's product is not fixed: only its vendor collection.
        (Dongles.InphicWireless, >= 0xFF00) when info.FeatureLength >= 33 => Inphic,
        // MCHOSE's mouse protocol only on its mice: Realtek's 0x4762 dongle, and MCHOSE devices that call themselves a mouse (its
        // keyboards, headsets and the MagDock speak other protocols).
        (0x0BDA, 0xFF01 or 0xFF0B) when info.Product == 0x4762 => Mchose,
        (Dongles.MchoseVendor, 0xFF01 or 0xFF0B) when Dongles.IsMchoseMouse(info.Product, info.ProductName) => Mchose,
        _ => null
    };

    private static DeviceBattery Mouse(string prefix, HidInfo info, string fallback, BatteryRead battery, DeviceKind kind = DeviceKind.Mouse)
        => new(Key(prefix, info), NameOf(info, fallback), Devices.Named(info.ProductName) is { } named and not DeviceKind.Other ? named : kind, DeviceSource.Hid, battery.Percent)
        { Charging = battery.Charging, Coarse = battery.Coarse, Hardware = Devices.Hardware(info.Vendor, info.Product) };

    private static IEnumerable<DeviceBattery> BitMouse(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        // The mouse behind a receiver first; a mouse on its cable answers for itself.
        foreach (byte target in new byte[] { 1, 0 })
        {
            var read = Hid.Transact<object>([device], () => device.Write(Dongles.BitMouseRequest(0x07, target)), r => Dongles.BitMouseBattery(r) is { } b ? b : null, 500);
            if (read is not BatteryRead battery) continue;
            bool charging = Hid.Transact<object>([device], () => device.Write(Dongles.BitMouseRequest(0x0F, target)), r => Dongles.BitMouseCharging(r) is { } c ? c : null, 500) is true;
            claimed.Add(info.Path);
            return [Mouse("atk", info, "ATK / VXE", battery with { Charging = charging })];
        }
        return [];
    }

    private static IEnumerable<DeviceBattery> Compx(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        var request = Dongles.CompxRequest(0x04);
        // Most send it as an output report; the A9 Ultra as a feature report.
        var read = Hid.Transact<object>([device], () => { if (!device.SetOutput(request)) device.Write(request); }, r => Dongles.CompxBattery(r) is { } b ? b : null, 500);
        if (read is null && device.SetFeature(request) && device.GetFeature(0x08, Math.Max(17, device.FeatureLength)) is { } feature) read = Dongles.CompxBattery(feature);
        if (read is not BatteryRead battery) return [];
        claimed.Add(info.Path);
        return [Mouse("compx", info, "VXE", battery)];
    }

    private static IEnumerable<DeviceBattery> Vgn(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        var read = Hid.Transact<object>([device], () => device.Write(Dongles.VgnRequest()), r => Dongles.VgnBattery(r) is { } b ? b : null, 500);
        if (read is not BatteryRead battery) return [];
        claimed.Add(info.Path);
        return [Mouse("vgn", info, "VGN", battery, DeviceKind.Keyboard)];
    }

    // The feature-report brands: set the request, wait, get the reply.
    private static IEnumerable<DeviceBattery> Feature(HidInfo info, HashSet<string> claimed, string prefix, string fallback, byte[] request, int waitMs, Func<byte[], BatteryRead?> parse)
    {
        using var device = new HidDevice(info);
        if (!device.SetFeature(request)) return [];
        Thread.Sleep(waitMs);
        if (device.GetFeature(request[0], device.FeatureLength) is not { } reply || parse(reply) is not { } battery) return [];
        claimed.Add(info.Path);
        return [Mouse(prefix, info, fallback, battery)];
    }
    private static IEnumerable<DeviceBattery> Endgame(HidInfo info, HashSet<string> claimed) => Feature(info, claimed, "endgame", "Endgame Gear", Dongles.EndgameRequest(info.FeatureLength), 350, Dongles.EndgameBattery);
    private static IEnumerable<DeviceBattery> Vaxee(HidInfo info, HashSet<string> claimed) => Feature(info, claimed, "vaxee", "VAXEE", Dongles.VaxeeRequest(info.FeatureLength), 120, Dongles.VaxeeBattery);
    private static IEnumerable<DeviceBattery> Nordic(HidInfo info, HashSet<string> claimed) => Feature(info, claimed, "nordic", "2.4G", Dongles.NordicRequest(info.FeatureLength), 90, Dongles.NordicBattery);

    // Inphic IN9: the handshake, then the battery, with one sequence number (InphicBatteryTray's order).
    private static IEnumerable<DeviceBattery> Inphic(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        device.SetFeature(Dongles.InphicRequest(0xF0, 1, Dongles.InphicHandshake));
        Thread.Sleep(10);
        if (!device.SetFeature(Dongles.InphicRequest(0x20, 1))) return [];
        Thread.Sleep(10);
        if (device.GetFeature(0x0C, 33) is not { } reply || Dongles.InphicBattery(reply) is not { } battery) return [];
        claimed.Add(info.Path);
        return [Mouse("inphic", info, "Inphic", battery)];
    }

    private static IEnumerable<DeviceBattery> Mchose(HidInfo info, HashSet<string> claimed)
    {
        using var device = new HidDevice(info);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) Thread.Sleep(30);
            var read = Hid.Transact<object>([device], () => device.Write(Dongles.MchoseRequest()), r => Dongles.MchoseBattery(r) is { } b ? b : null, 400);
            if (read is not BatteryRead battery) continue;
            claimed.Add(info.Path);
            return [Mouse("mchose", info, "MCHOSE", battery)];
        }
        return [];
    }

    // ----- The standard battery usages -----

    private static IEnumerable<DeviceBattery> ReadUsage(HidInfo info)
    {
        using var device = new HidDevice(info, readWrite: false);
        foreach (var battery in info.FeatureBatteries)
        {
            if (Hid.FeatureValue(device, battery, info.FeatureLength) is not { } value || HidBattery.Percent(value, battery.Min, battery.Max) is not { } percent) continue;
            var kind = (info.UsagePage, info.Usage) switch
            {
                (0x01, 0x06) => DeviceKind.Keyboard, (0x01, 0x02) => DeviceKind.Mouse, (0x01, 0x04 or 0x05) => DeviceKind.Controller, (0x0D, _) => DeviceKind.Pen, _ => DeviceKind.Other
            };
            return [new DeviceBattery(Key("hid", info), NameOf(info, Devices.KindName(kind)), Devices.Named(info.ProductName) ?? kind, DeviceSource.Hid, percent)
                { Hardware = Devices.Hardware(info.Vendor, info.Product), Address = AddressOf(info) }];
        }
        return [];
    }

    // ----- Logitech HID++ (device-battery-viewer's LogitechHidpp, ported) -----

    // A receiver's or a cabled device's HID++ channel: the long collection sends and receives, the short one only receives (HID++
    // 1.0 errors come short). Device slots it has found are kept with their feature indices, name, kind and key, so a later
    // reading costs one request.
    private sealed class LogitechReader
    {
        private const int TimeoutMs = 400, PingTimeoutMs = 1500;
        // Legacy: a HID++ 1.0 device, read through its registers.
        private sealed class Slot { public string Key = "", Name = ""; public bool Legacy; public DeviceKind Kind = DeviceKind.Other; public readonly Dictionary<int, byte> Features = []; public byte[]? Capabilities; public DeviceBattery? Last; public long LastAt; }
        // A device that sleeps between readings (a mouse left alone) keeps its last reading this long.
        private const long Asleep = 10 * 60_000;
        private static DeviceBattery? Remembered(Slot? slot) => slot?.Last is { } last && Environment.TickCount64 - slot.LastAt < Asleep ? last : null;
        private readonly Dictionary<string, Slot> _known = [];
        public void Clear() => _known.Clear();
        // The receiver (its collections' group) a device was last read through, by the device's key.
        public string? Receiver(string key) => _known.FirstOrDefault(pair => pair.Value.Key == key).Key is { } slot ? slot[..slot.LastIndexOf('|')] : null;

        public IEnumerable<DeviceBattery> Read(List<HidInfo> collections, HashSet<string> claimed)
        {
            var found = new List<DeviceBattery>();
            var shorts = collections.Where(info => info.Usage == 0x0001).GroupBy(info => GroupKey(info.Path)).ToDictionary(g => g.Key, g => g.First());
            foreach (var longInfo in collections.Where(info => info.Usage == 0x0002))
            {
                string group = GroupKey(longInfo.Path);
                shorts.TryGetValue(group, out var shortInfo);
                Channel channel;
                try { channel = new Channel(longInfo, shortInfo); }
                catch { continue; }
                using (channel)
                {
                    claimed.Add(longInfo.Path); if (shortInfo is not null) claimed.Add(shortInfo.Path);
                    foreach (var index in Hidpp.Indices(Hidpp.IsReceiver(longInfo.Product, longInfo.ProductName)))
                    {
                        DeviceBattery? device;
                        // A failed write or read (the receiver pulled out half way) costs this slot its reading, not the others'.
                        try { device = ReadOne(channel, group, index, longInfo); }
                        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { device = Remembered(_known.GetValueOrDefault(group + "|" + index)); }
                        if (device is not null && found.All(known => known.Key != device.Key)) found.Add(device);
                    }
                }
            }
            return found;
        }

        private DeviceBattery? ReadOne(Channel channel, string group, byte index, HidInfo info)
        {
            string slotKey = group + "|" + index;
            _known.TryGetValue(slotKey, out var slot);
            try
            {
                // Root's protocol version is the ping; a sleeping or empty slot does not answer.
                var version = channel.Request(index, 0x00, 1, [0, 0, 0x5A], PingTimeoutMs);
                if (version[0] < 2) return null;
            }
            catch (HidppError error) when (error.Code == Hidpp.InvalidSubId)
            {
                // HID++ 1.0: the battery registers. Its slot is kept as a 2.0 device's is, so it counts as present and keeps
                // its last reading through a missed one.
                if (Registers(channel, index, info) is not { } read) return Remembered(slot is { Legacy: true } ? slot : null);
                _known[slotKey] = new Slot { Key = read.Key, Name = read.Name, Legacy = true, Last = read, LastAt = Environment.TickCount64 };
                return read;
            }
            catch (Exception error) when (error is TimeoutException or HidppError) { return Remembered(slot); }
            // A slot a HID++ 1.0 device held is found afresh.
            if (slot is { Legacy: true }) slot = null;
            var device = new Device(channel, index, slot ?? new Slot());
            try
            {
                if (slot is null)
                {
                    var identity = device.Call(Hidpp.DeviceInfo, 0);
                    string unit = identity is { Length: >= 5 } ? Convert.ToHexString(identity, 1, 4) : "";
                    device.Slot.Key = "logitech:" + (unit.Length > 0 && unit != "00000000" ? unit : $"{info.Product:X4}:{index}");
                    string name = device.ReadName();
                    device.Slot.Name = name.Length > 0 ? name : info.ProductName.Length > 0 && index == 0xFF ? info.ProductName : "Logitech #" + index;
                    device.Slot.Kind = device.ReadKind();
                    _known[slotKey] = slot = device.Slot;
                }
                // A device without a battery feature (a keyboard on a cable) is not listed.
                if (device.ReadBattery() is not { } battery) return null;
                slot.Last = new DeviceBattery(slot.Key, slot.Name, slot.Kind != DeviceKind.Other ? slot.Kind : Devices.Named(slot.Name) ?? DeviceKind.Other, DeviceSource.Logitech, battery.Percent)
                { Charging = battery.Charging, Coarse = battery.Coarse };
                slot.LastAt = Environment.TickCount64;
                return slot.Last;
            }
            catch (Exception error) when (error is TimeoutException or HidppError)
            {
                // The device fell asleep half way; its feature indices may have changed, so they are asked again next time.
                slot?.Features.Clear();
                return Remembered(slot);
            }
        }

        // HID++ 1.0 devices: register 0x0D (mileage) or 0x07 (a level 1 to 7).
        private static DeviceBattery? Registers(Channel channel, byte index, HidInfo info)
        {
            BatteryRead? read = null;
            try { read = Hidpp.Mileage(channel.Register(index, 0x0D)); }
            catch (Exception error) when (error is TimeoutException or HidppError) { }
            if (read is null)
                try { read = Hidpp.Level(channel.Register(index, 0x07)); }
                catch (Exception error) when (error is TimeoutException or HidppError) { }
            if (read is not { } battery) return null;
            string name = index == 0xFF && info.ProductName.Length > 0 ? info.ProductName : "Logitech #" + index;
            return new DeviceBattery($"logitech:{info.Product:X4}:{index}", name, Devices.Named(name) ?? DeviceKind.Other, DeviceSource.Logitech, battery.Percent)
            { Charging = battery.Charging, Coarse = battery.Coarse };
        }

        private sealed class HidppError(int code) : Exception($"HID++ error 0x{code:X2}") { public int Code { get; } = code; }

        private sealed class Channel : IDisposable
        {
            private readonly HidDevice _long;
            private readonly List<HidDevice> _all = [];
            public Channel(HidInfo longInfo, HidInfo? shortInfo)
            {
                _long = new HidDevice(longInfo); _all.Add(_long);
                if (shortInfo is not null) try { _all.Add(new HidDevice(shortInfo)); } catch { }
            }
            public void Dispose() { foreach (var device in _all) device.Dispose(); }

            // A request; the reply's parameters, or TimeoutException, or HidppError.
            public byte[] Request(byte index, byte feature, int function, byte[] parameters, int timeoutMs = TimeoutMs)
            {
                var message = Hidpp.Request(index, feature, function, parameters);
                var result = Hid.Transact<object>(_all, () => _long.Write(message), report =>
                {
                    var (reply, error) = Hidpp.Match(report, index, feature, function);
                    return reply is not null ? reply : error is { } code ? new HidppError(code) : null;
                }, timeoutMs);
                return result switch { byte[] reply => reply, HidppError error => throw error, _ => throw new TimeoutException() };
            }

            // A HID++ 1.0 register read, sent short.
            public byte[] Register(byte index, byte register)
            {
                // HID++ 1.0 registers are read with short reports, which only the short collection takes.
                if (_all.Count < 2) throw new HidppError(0);
                var shortDevice = _all[1];
                var result = Hid.Transact<object>(_all, () => shortDevice.Write(Hidpp.RegisterRequest(index, register)), report =>
                {
                    if (report.Length < 6 || report[1] != index) return null;
                    if (report[2] == 0x81 && report[3] == register) return report[4..];
                    if (report[2] == 0x8F && report[3] == 0x81 && report[4] == register) return new HidppError(report[5]);
                    return null;
                }, TimeoutMs);
                return result switch { byte[] reply => reply, HidppError error => throw error, _ => throw new TimeoutException() };
            }
        }

        private sealed class Device(Channel channel, byte index, Slot slot)
        {
            public readonly Slot Slot = slot;

            // A feature's function; null when the device lacks the feature.
            public byte[]? Call(int feature, int function, params byte[] parameters)
            {
                if (!Slot.Features.TryGetValue(feature, out var featureIndex))
                    Slot.Features[feature] = featureIndex = channel.Request(index, 0x00, 0, Hidpp.FeatureParameters(feature))[0];
                return featureIndex == 0 ? null : channel.Request(index, featureIndex, function, parameters);
            }

            public string ReadName()
            {
                var count = Call(Hidpp.DeviceName, 0);
                if (count is null) return "";
                var name = new List<byte>();
                while (name.Count < count[0])
                {
                    var chunk = Call(Hidpp.DeviceName, 1, (byte)name.Count);
                    var part = (chunk ?? []).Take(count[0] - name.Count).TakeWhile(b => b != 0).ToArray();
                    if (part.Length == 0) break;
                    name.AddRange(part);
                }
                return System.Text.Encoding.UTF8.GetString([.. name]).Trim();
            }

            public DeviceKind ReadKind() => Call(Hidpp.DeviceName, 2) is { } reply ? Hidpp.Kind(reply[0]) : DeviceKind.Other;

            // 0x1004, then 0x1000, then 0x1001, then the headsets' 0x1F20.
            public BatteryRead? ReadBattery()
            {
                // 0x1004 capabilities do not change: asked once per device.
                if (Call(Hidpp.UnifiedBattery, 1) is { } status) return Hidpp.Unified(status, Slot.Capabilities ??= Call(Hidpp.UnifiedBattery, 0));
                if (Call(Hidpp.BatteryStatus, 0) is { } level) return Hidpp.Status(level);
                if (Call(Hidpp.BatteryVoltage, 0) is { } voltage) return Hidpp.Voltage(voltage);
                // A headset that is off answers without a valid measurement: no reading.
                if (Call(Hidpp.AdcMeasurement, 0) is { } adc) return Hidpp.Adc(adc);
                return null;
            }
        }
    }
}
