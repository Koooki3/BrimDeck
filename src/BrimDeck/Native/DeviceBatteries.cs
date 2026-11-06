using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Whether the devices' batteries are wanted. Watch: a panel shows the devices page or the collapsed battery reading, so every
// source reads, the ones without events at most once a minute (DevicePolling). None: every source is stopped and lets its
// devices go.
internal enum DeviceDemand { None, Watch }

// One way of reading batteries. Started and stopped on the UI thread; it reports its whole current list through the callback
// it was made with, on the UI thread, whenever that list changes.
internal interface IDeviceSource : IDisposable
{
    void Start(DeviceDemand demand);
    void Stop();
    // Read again now (the page opened), as far as the source's own interval allows.
    void Refresh();
    // Whether it has listed what is connected since it started (an empty list then means none).
    bool Listed { get; }
}

// The batteries of the connected wireless devices, from every source, merged (DeviceRoster). It runs only as far as the demand
// asks (DeviceDemand), and lingers a little after the page closes, so reopening it finds the readings current.
internal sealed class DeviceBatteries : IDisposable
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(10);
    private readonly Dispatcher _dispatcher;
    private readonly DeviceRoster _roster = new();
    private readonly List<IDeviceSource> _sources = [];
    private readonly DispatcherTimer _linger;
    private readonly AppleBatteries _apple;
    private DeviceDemand _demand = DeviceDemand.None, _running = DeviceDemand.None;
    private bool _disposed;

    public DeviceBatteries(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _linger = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Linger };
        _linger.Tick += (_, _) => { _linger.Stop(); Run(_demand); };
        _apple = new AppleBatteries(dispatcher, Report);
        _sources.Add(new BluetoothBatteries(dispatcher, Report));
        _sources.Add(new HidBatteries(dispatcher, Report));
        _sources.Add(new GamingBatteries(dispatcher, Report));
        _sources.Add(_apple);
    }

    // A source's whole list, on the UI thread. The AirPods scan follows what is connected (AppleBatteries.Consider), on every
    // Bluetooth list whether or not the merged one changed: AirPods that disconnect leave it unchanged while the Apple source
    // still holds their entry.
    private void Report(DeviceSource source, IReadOnlyList<DeviceBattery> devices)
    {
        if (_disposed || _running == DeviceDemand.None) return;
        bool changed = _roster.Set(source, devices);
        if (source == DeviceSource.Bluetooth) _apple.Consider(_roster.Of(DeviceSource.Bluetooth));
        if (changed) Changed?.Invoke();
    }

    // Every device read now, hidden ones included; empty while nothing runs.
    public IReadOnlyList<DeviceBattery> All => _roster.All;
    // Whether the sources have run long enough to have listed what is connected (the page says "no devices" only then).
    public bool Settled { get; private set; }
    public DeviceDemand Demand => _running;
    // Raised on the UI thread when the list or any reading changes.
    public event Action? Changed;

    public void SetDemand(DeviceDemand demand)
    {
        if (_disposed) return;
        _demand = demand;
        // Asked for less, the sources keep running a while (Linger); asked for more, they start at once.
        if (demand < _running) { if (!_linger.IsEnabled) _linger.Start(); return; }
        _linger.Stop();
        Run(demand);
    }

    public void Refresh() { if (_running == DeviceDemand.Watch) foreach (var source in _sources) source.Refresh(); }

    private DispatcherTimer? _settle;
    private void Run(DeviceDemand demand)
    {
        if (demand == _running) return;
        var was = _running;
        _running = demand;
        if (demand == DeviceDemand.None)
        {
            foreach (var source in _sources) source.Stop();
            _roster.Clear(); Settled = false; _settle?.Stop();
            Changed?.Invoke();
            return;
        }
        foreach (var source in _sources) source.Start(demand);
        if (was == DeviceDemand.None)
        {
            // Enumerating takes a moment; until every source has listed what is connected (or ten seconds have passed, should one
            // hang), an empty list means "not read yet", not "no devices".
            _settleStarted = DateTime.UtcNow;
            _settle ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            _settle.Tick -= Settle; _settle.Tick += Settle; _settle.Start();
        }
    }
    private DateTime _settleStarted;
    private void Settle(object? sender, EventArgs e)
    {
        if (_running == DeviceDemand.None || Settled) { _settle?.Stop(); return; }
        if (!_sources.All(source => source.Listed) && DateTime.UtcNow - _settleStarted < TimeSpan.FromSeconds(10)) return;
        _settle?.Stop();
        Settled = true; Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _linger.Stop(); _settle?.Stop();
        foreach (var source in _sources) { try { source.Dispose(); } catch { } }
    }
}
