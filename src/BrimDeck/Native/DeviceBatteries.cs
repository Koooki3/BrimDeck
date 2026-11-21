using System.Runtime.InteropServices;
using System.Windows.Threading;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck.Native;

// Whether the devices' batteries are wanted. Watch: a panel shows the devices page or the collapsed battery reading, so every
// source reads, the ones without events as often as DevicePolling has it. None: every source is stopped and lets its devices
// go.
internal enum DeviceDemand { None, Watch }

// One way of reading batteries. Started and stopped on the UI thread; it reports its whole current list through the callback
// it was made with, on the UI thread, whenever that list changes.
internal interface IDeviceSource : IDisposable
{
    void Start(DeviceDemand demand);
    void Stop();
    // Read again now (the page opened), as far as the source's own interval allows.
    void Refresh();
    // Read every device at once, whatever the intervals: the computer woke, or the user came back after a while.
    void ReadNow();
    // Whether it has listed what is connected since it started (an empty list then means none).
    bool Listed { get; }
}

// The batteries of the connected wireless devices, from every source, merged (DeviceRoster). It runs only as far as the demand
// asks (DeviceDemand), and lingers after the page closes (the memory mode's ReadingsLinger), so reopening it finds the readings current.
internal sealed class DeviceBatteries : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DeviceRoster _roster = new();
    private readonly List<IDeviceSource> _sources = [];
    private readonly DispatcherTimer _linger;
    private readonly AppleBatteries _apple;
    private readonly EncoDeckBatteries _encoDeck;
    private DeviceDemand _demand = DeviceDemand.None, _running = DeviceDemand.None;
    private bool _disposed;

    public DeviceBatteries(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _idle = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _idle.Tick += (_, _) => Idle();
        SystemEvents.PowerModeChanged += PowerModeChanged;
        _linger = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = MemoryPlan.For(MemoryMode.Balanced).ReadingsLinger };
        _linger.Tick += (_, _) => { _linger.Stop(); Run(_demand); };
        _apple = new AppleBatteries(dispatcher, Report);
        _encoDeck = new EncoDeckBatteries(dispatcher, Report);
        // Phone Link first: it is the primary reading for a phone, and the Bluetooth source stands in only without it.
        _sources.Add(new PhoneLinkBatteries(dispatcher, Report));
        _sources.Add(new BluetoothBatteries(dispatcher, Report));
        _sources.Add(new HidBatteries(dispatcher, Report));
        _sources.Add(new GamingBatteries(dispatcher, Report));
        _sources.Add(_apple);
        _sources.Add(_encoDeck);
    }

    // A source's whole list, on the UI thread. The AirPods scan follows what is connected (AppleBatteries.Consider), on every
    // Bluetooth list whether or not the merged one changed: AirPods that disconnect leave it unchanged while the Apple source
    // still holds their entry. A device that connects is also when EncoDeck is looked for (EncoDeckBatteries.Consider).
    private void Report(DeviceSource source, IReadOnlyList<DeviceBattery> devices)
    {
        if (_disposed || _running == DeviceDemand.None) return;
        bool changed = _roster.Set(source, devices);
        if (source == DeviceSource.Bluetooth) { _apple.Consider(_roster.Of(DeviceSource.Bluetooth)); _encoDeck.Consider(_roster.Of(DeviceSource.Bluetooth)); }
        if (changed) { Show(); Changed?.Invoke(); }
    }

    // Every device read now, hidden ones included; empty while nothing runs.
    public IReadOnlyList<DeviceBattery> All => _roster.All;
    // What the panels show: All, and until the sources have listed what is connected (Settled), the readings of the last run for
    // the devices without one yet, faded (DeviceRoster.WithEarlier). Kept in memory only, from the last time the sources stopped.
    public IReadOnlyList<DeviceBattery> Showing { get; private set; } = [];
    private IReadOnlyList<DeviceBattery> _earlier = [];
    private void Show() => Showing = Settled || _running == DeviceDemand.None ? _roster.All : DeviceRoster.WithEarlier(_roster.All, _earlier);
    // Whether the sources have run long enough to have listed what is connected (the page says "no devices" only then).
    public bool Settled { get; private set; }
    public DeviceDemand Demand => _running;
    // Raised on the UI thread when the list or any reading changes.
    public event Action? Changed;

    // How long the sources keep running after they are no longer asked for (MemoryPlan.ReadingsLinger).
    public void UsePlan(MemoryPlan plan) => _linger.Interval = plan.ReadingsLinger;

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

    // ----- Reading at once: the computer woke, the user came back -----

    // What the open-source battery tools on Windows do (quiet-battery, HaloBattery, ChargeDock, Solaar on resume): the devices are
    // read again when the computer wakes, a couple of seconds on (they reconnect first), and when input comes after 20 seconds
    // without any (a mouse picked up again wakes from its sleep), at most once in five seconds. Only the time of the last input
    // is looked at (GetLastInputInfo), never what it was. A device whose search for its read ran out is tried once more then
    // (DeviceReadBook.Again): each waking, and on input within the book's own limits.
    private static readonly TimeSpan Away = TimeSpan.FromSeconds(20), Again = TimeSpan.FromSeconds(5);
    private readonly DispatcherTimer _idle;
    private bool _away;
    private long _readNowAt;

    // input: the user came back (Idle), which gives a search that ran out another try within the book's limits.
    private void ReadNow(bool input = false)
    {
        if (_running != DeviceDemand.Watch || Environment.TickCount64 - _readNowAt < (long)Again.TotalMilliseconds) return;
        _readNowAt = Environment.TickCount64;
        if (input) DeviceReadStore.Book.Again(_readNowAt, woke: false);
        foreach (var source in _sources) source.ReadNow();
    }

    private void Idle()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return;
        uint idle = unchecked((uint)Environment.TickCount - info.dwTime);
        if (idle >= Away.TotalMilliseconds) { _away = true; return; }
        if (_away) { _away = false; ReadNow(input: true); }
    }

    private void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var later = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(2) };
            later.Tick += (_, _) => { later.Stop(); DeviceReadStore.Book.Again(Environment.TickCount64, woke: true); _readNowAt = 0; ReadNow(); };
            later.Start();
        });
    }

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    private DispatcherTimer? _settle;
    private void Run(DeviceDemand demand)
    {
        if (demand == _running) return;
        var was = _running;
        _running = demand;
        if (demand == DeviceDemand.Watch) { _away = false; _idle.Start(); } else _idle.Stop();
        if (demand == DeviceDemand.None)
        {
            foreach (var source in _sources) source.Stop();
            _earlier = [.. Showing.Where(device => device.Lowest is not null)];
            _roster.Clear(); Settled = false; _settle?.Stop();
            Show();
            Changed?.Invoke();
            return;
        }
        // The last run's readings show at once (Showing); the change is told after this call, as the demand is set while a panel
        // builds its collapsed row.
        if (was == DeviceDemand.None)
        {
            Show();
            if (Showing.Count > 0) _dispatcher.BeginInvoke(() => { if (!_disposed) Changed?.Invoke(); });
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
        Settled = true; _earlier = []; Show(); Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _linger.Stop(); _settle?.Stop(); _idle.Stop();
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        foreach (var source in _sources) { try { source.Dispose(); } catch { } }
    }
}

// How often the sources without events of their own are read while the page or the collapsed reading shows, as the open-source
// battery tools on Windows have it. Ask: the brands that only answer when asked (vigil-watt's quick round, G-Helper's 20 seconds).
// Logitech: a fallback, its devices sending their own events in between (OpenLogi's 30 seconds). Cached: Windows' own Bluetooth
// levels, read from its cache without reaching the device (BlueGauge). Listen: the PlayStation controllers, which send their
// state all the time and are only listened to. Gatt: a Bluetooth LE level whose device sends no notices (the Battery Service
// specification asks not to poll it often). Slow: the receivers whose answer takes seconds (Corsair's, read on a thread of their
// own). The receivers that send their battery unasked have no interval: they are listened to all the time (PushEvents).
internal static class DevicePolling
{
    public static readonly TimeSpan Ask = TimeSpan.FromSeconds(15), Logitech = TimeSpan.FromSeconds(30), Cached = TimeSpan.FromSeconds(5),
        Listen = TimeSpan.FromSeconds(5), Gatt = TimeSpan.FromSeconds(60), Slow = TimeSpan.FromSeconds(60);
}
