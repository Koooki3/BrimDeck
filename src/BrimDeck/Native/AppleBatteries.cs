using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Threading;
using BrimDeck.Core;
using Windows.Devices.Bluetooth.Advertisement;

namespace BrimDeck.Native;

// AirPods and Beats: each bud's and the case's charge, from Apple's proximity pairing advertisement (AppleAdvert), which they
// send while out of the case or with its lid open. A passive scan (no requests sent) runs only while the page or the collapsed
// reading shows and an AirPods or Beats device is connected over Bluetooth; only a strong signal (the buds next to the PC)
// whose family (AirPods or Beats) matches the connected device's name counts. The advertisement carries no address to match
// (it rotates), so a neighbour's AirPods of the same family right next to the PC could be read instead; the signal threshold
// keeps that to arm's length. Its entry takes the
// connected device's key, replacing the single level Windows has for it.
internal sealed class AppleBatteries(Dispatcher dispatcher, Action<DeviceSource, IReadOnlyList<DeviceBattery>> report) : IDeviceSource
{
    private const short StrongSignal = -60;
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    private BluetoothLEAdvertisementWatcher? _watcher;
    private DeviceDemand _demand;
    private DeviceBattery? _target;
    private (IReadOnlyList<PartBattery> Parts, DateTimeOffset At)? _last;

    // It adds to what the Bluetooth source lists; it never lists a device of its own.
    public bool Listed => true;
    private DispatcherTimer? _stale;

    public void Start(DeviceDemand demand) { _demand = demand; Update(); }
    public void Stop() { _demand = DeviceDemand.None; _target = null; _last = null; Update(); report(DeviceSource.Apple, []); }
    public void Refresh() { }
    public void Dispose() => Stop();

    // Every change of the roster: whether an Apple device is connected, which the scan is for.
    // connected: what the Bluetooth source lists now (connected devices only), unmerged, so the AirPods' own entry never keeps
    // the scan going once they disconnect.
    public void Consider(IReadOnlyList<DeviceBattery> connected)
    {
        var target = connected.FirstOrDefault(device => device.Address is not null && IsApple(device.Name));
        if (target?.Key != _target?.Key) _last = null;
        _target = target is null ? null : target with { Source = DeviceSource.Bluetooth };
        Update();
    }
    private void Expire(object? sender, EventArgs e) { _stale?.Stop(); Publish(); }
    private static bool IsApple(string name) => name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) || name.Contains("Beats", StringComparison.OrdinalIgnoreCase);

    private void Update()
    {
        bool wanted = _demand == DeviceDemand.Watch && _target is not null;
        if (wanted && _watcher is null)
        {
            try
            {
                var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData { CompanyId = AppleAdvert.Company });
                watcher.SignalStrengthFilter.InRangeThresholdInDBm = StrongSignal;
                watcher.Received += Received;
                watcher.Start();
                _watcher = watcher;
            }
            catch { _watcher = null; }
        }
        else if (!wanted && _watcher is not null)
        {
            _watcher.Received -= Received;
            try { _watcher.Stop(); } catch { }
            _watcher = null;
        }
        Publish();
    }

    private void Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (args.RawSignalStrengthInDBm < StrongSignal) return;
        foreach (var data in args.Advertisement.ManufacturerData)
        {
            if (data.CompanyId != AppleAdvert.Company) continue;
            var bytes = data.Data.ToArray();
            if (AppleAdvert.Parts(bytes) is not { Count: > 0 } parts || AppleAdvert.Model(bytes) is not { } model) continue;
            dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                // The family must be the connected device's: AirPods for "Koooki's AirPods Pro", Beats for "Powerbeats Pro".
                string family = model.Contains("Beats", StringComparison.OrdinalIgnoreCase) || model.Contains("beats", StringComparison.Ordinal) ? "beats" : "AirPods";
                if (_target is not { } target || !target.Name.Contains(family, StringComparison.OrdinalIgnoreCase)) return;
                bool same = _last is { } last && last.Parts.SequenceEqual(parts);
                _last = (parts, DateTimeOffset.UtcNow);
                if (!same) Publish();
            });
        }
    }

    private void Publish()
    {
        // The buds go quiet in their closed case: the reading leaves once it is no longer fresh, without waiting for another.
        _stale?.Stop();
        if (_last is { } fresh && _demand == DeviceDemand.Watch && DateTimeOffset.UtcNow - fresh.At <= Fresh)
        {
            _stale ??= new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            _stale.Tick -= Expire; _stale.Tick += Expire;
            _stale.Interval = Fresh - (DateTimeOffset.UtcNow - fresh.At) + TimeSpan.FromMilliseconds(50);
            _stale.Start();
        }
        if (_target is not { } target || _last is not { } last || DateTimeOffset.UtcNow - last.At > Fresh || _demand != DeviceDemand.Watch) { report(DeviceSource.Apple, []); return; }
        var buds = last.Parts.Where(part => part.Part != BatteryPart.Case && part.Percent is not null).ToList();
        report(DeviceSource.Apple, [target with
        {
            Source = DeviceSource.Apple, Percent = buds.Count > 0 ? buds.Min(part => part.Percent) : target.Percent, Parts = last.Parts, Coarse = true,
            Charging = buds.Count > 0 && buds.All(part => part.Charging)
        }]);
    }
}
