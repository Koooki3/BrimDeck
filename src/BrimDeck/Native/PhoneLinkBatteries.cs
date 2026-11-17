using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck.Native;

// The phone's battery from Phone Link, the app behind "Link to Windows": its Start card carries the phone's name, the
// charge and whether it is charging. Read only, from the app package's own local state; nothing is ever written there.
// When the card has no percentage (the phone is away, the app is closed) nothing is reported, and the Bluetooth source's
// entry for the phone stands in: the roster prefers Phone Link only while it has a reading (DeviceRoster.Merge).
internal sealed class PhoneLinkBatteries(Dispatcher dispatcher, Action<DeviceSource, IReadOnlyList<DeviceBattery>> report) : IDeviceSource
{
    private const string PackagePrefix = "Microsoft.YourPhone_";
    private static readonly string[] Relative =
    [
        "LocalState", "StartMenu", "StartMenuCompanion.json"
    ];
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background, dispatcher) { Interval = DevicePolling.Ask };
    private DeviceDemand _demand;
    private string? _path;
    private bool _listed;
    // The write time of the card the last reading came from, and whether that reading was reported. Phone Link rewrites the
    // file only when the phone's state changes, so a tick that finds the same write time does no reading at all.
    private DateTime? _stamp;
    private bool _reported;
    // Whether Phone Link itself is running, asked only for a card older than PhoneLinkCard.Unverified and remembered for a
    // while: one process lookup costs milliseconds, and the answer cannot change faster than this.
    private static readonly TimeSpan AliveEvery = TimeSpan.FromMinutes(5);
    private DateTime _aliveAt;
    private bool _alive;
    // When the packages folder was last listed for the card; a scan that found none is not repeated on every tick.
    private static readonly TimeSpan ScanEvery = TimeSpan.FromMinutes(5);
    private DateTime _scannedAt;

    public void Start(DeviceDemand demand)
    {
        _demand = demand;
        _poll.Stop();
        if (demand != DeviceDemand.Watch) return;
        _poll.Tick -= Tick; _poll.Tick += Tick;
        _poll.Start();
        Read();
    }

    public void Stop()
    {
        _demand = DeviceDemand.None;
        _poll.Stop();
        _listed = false;
        _stamp = null;
        _reported = false;
        _scannedAt = default;
        report(DeviceSource.PhoneLink, []);
    }

    public void Refresh() => Read(force: true);
    public void ReadNow() => Read(force: true);

    // Whether the card has been looked at since the source started; without one there is no phone to report.
    public bool Listed => _listed;

    private void Tick(object? sender, EventArgs e) => Read();

    private void Read(bool force = false)
    {
        if (_demand != DeviceDemand.Watch) return;
        _listed = true;
        string? path = CardPath(force);
        if (path is null) { Clear(null); return; }
        var file = new FileInfo(path);
        if (!file.Exists) { Clear(null); return; }
        // A card this old describes a phone that is no longer there; the Bluetooth entry, if any, stands in. Between
        // Unverified and MaximumAge an idle phone keeps its reading only while Phone Link itself is running, so a card
        // frozen by the app closing does not outlive it by hours.
        var stamp = file.LastWriteTimeUtc;
        var age = DateTime.UtcNow - stamp;
        if (PhoneLinkCard.Expired(age) || (PhoneLinkCard.NeedsLiveness(age) && !Alive())) { Clear(null); return; }
        // The same card as the last reading: Phone Link writes on every change, so there is nothing new to parse.
        if (PhoneLinkCard.IsCurrent(stamp, _stamp, force)) return;
        string json;
        // Another app's file, replaced as it is read: a read that fails says nothing about the phone, so the reading on
        // hand stands and the next tick reads the card again. It stands no longer than a reading may, though: the card was
        // rewritten, so a reading kept from a card past MaximumAge describes a phone that is no longer there.
        try { json = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The reading kept is the one on hand, so its own age decides how long it stands: past Unverified it needs Phone
            // Link running, past MaximumAge it goes whatever runs.
            if (_stamp is { } held)
            {
                var kept = DateTime.UtcNow - held;
                if (PhoneLinkCard.Expired(kept) || (PhoneLinkCard.NeedsLiveness(kept) && !Alive())) Clear(null);
            }
            return;
        }
        if (PhoneLinkCard.Parse(json) is not { } phone) { Clear(stamp); return; }
        _stamp = stamp;
        _reported = true;
        report(DeviceSource.PhoneLink,
            [new DeviceBattery(Key(phone.Name), phone.Name, DeviceKind.Phone, DeviceSource.PhoneLink, Devices.Clamp(phone.Percent)) { Charging = phone.Charging }]);
    }

    // Drops the reading once: the phone is away, the card is stale, or it has nothing to say. A card that was read keeps its
    // write time, so the same card is not parsed again until Phone Link rewrites it; an unreadable one is retried next tick.
    private void Clear(DateTime? stamp)
    {
        _stamp = stamp;
        if (!_reported) return;
        _reported = false;
        report(DeviceSource.PhoneLink, []);
    }

    // Phone Link's own process (the YourPhone package's PhoneExperienceHost). Asked only for a card old enough that its write
    // time no longer proves anything, and the answer is kept for AliveEvery: a lookup costs milliseconds.
    private bool Alive()
    {
        var now = DateTime.UtcNow;
        if (now - _aliveAt < AliveEvery) return _alive;
        _aliveAt = now;
        try
        {
            var processes = Process.GetProcessesByName("PhoneExperienceHost");
            _alive = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
        }
        // A lookup that fails says nothing about the phone; the card's own age still bounds the reading.
        catch (Exception ex) when (ex is not OutOfMemoryException) { _alive = false; }
        return _alive;
    }

    // The newest card of the Phone Link package, wherever Windows put this version of it. Looking for it lists the
    // packages folder on the timer's thread, so a look that finds nothing waits ScanEvery before the next: an app that is
    // not installed will not appear within seconds. A forced read looks at once.
    private string? CardPath(bool force)
    {
        if (_path is { } cached && File.Exists(cached)) return cached;
        var now = DateTime.UtcNow;
        if (!force && now - _scannedAt < ScanEvery) return null;
        _scannedAt = now;
        string? newest = null;
        DateTime newestAt = DateTime.MinValue;
        try
        {
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            foreach (var directory in Directory.EnumerateDirectories(packages, PackagePrefix + "*"))
            {
                string candidate = Path.Combine(directory, Relative[0], Relative[1], Relative[2]);
                if (!File.Exists(candidate)) continue;
                var at = File.GetLastWriteTimeUtc(candidate);
                if (at > newestAt) { newestAt = at; newest = candidate; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return _path = newest;
    }

    // The device's key, steady across readings: its name's letters and digits, lower case ("phonelink:xiaomi13ultra").
    private static string Key(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        return "phonelink:" + (builder.Length > 0 ? builder.ToString() : "phone");
    }

    public void Dispose() => _poll.Stop();
}
