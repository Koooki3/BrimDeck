namespace BrimDeck.Core;

// How present each reading of the collapsed row is, 0 to 1, easing as readings come and go. Owned by the panel, so that it
// outlives the row, which is built again whenever its width changes.
public sealed class StripPresence
{
    private readonly Dictionary<CompactMonitorItems, Eased> _items = [];
    private CompactMonitorItems _chosen = CompactMonitorItems.None, _arriving = CompactMonitorItems.None;

    // Readings that join the row because their first figures arrived (StripAdmission), not because the settings chose them: the
    // next Set brings them in from nothing with the row's own entrance, while the others slide aside.
    public void Arrive(CompactMonitorItems items) => _arriving |= items;

    // Returns whether anything moves. A new choice of readings (the settings) shows at once; motion: whether the row eases at all
    // (the app's animations).
    public bool Set(CompactMonitorItems chosen, CompactMonitorItems fitting, bool motion = true)
    {
        var arriving = _arriving & chosen;
        bool settled = motion && (chosen & ~arriving) == (_chosen & ~arriving) && _items.Count > 0, moving = false;
        _arriving = CompactMonitorItems.None;
        _chosen = chosen;
        foreach (var item in CompactMonitor.Order)
        {
            if ((chosen & item) == 0) { _items.Remove(item); continue; }
            bool fits = (fitting & item) != 0, arrives = (arriving & item) != 0 && !_items.ContainsKey(item);
            var eased = _items.TryGetValue(item, out var known) ? known : new Eased(fits && !(arrives && motion) ? 1 : 0, Eased.Seconds * .8);
            moving |= eased.Set(fits ? 1 : 0, settled || arrives && motion);
            _items[item] = eased;
        }
        return moving;
    }

    public double At(CompactMonitorItems item, double now) => _items.TryGetValue(item, out var eased) ? Math.Clamp(eased.At(now), 0, 1) : 0;
    public bool Moving(double now)
    {
        // Asked on every frame of a transition; the dictionary's own enumerator allocates nothing.
        foreach (var eased in _items.Values) if (eased.Moving(now)) return true;
        return false;
    }
}

// Which of the collapsed row's later readings (the temperature, the system drive) it leaves out, and when they join. Neither joins
// before its first figure arrives, so the row never shows a dash for a sensor still being read, nor builds itself again when the
// sensor turns out to be missing: a machine without one never shows the reading at all. Once a reading has shown it stays
// (a figure missing for a moment shows a dash) until its sensor is known to be missing.
public sealed class StripAdmission
{
    public const CompactMonitorItems Later = CompactMonitorItems.Temperature | CompactMonitorItems.Storage;
    private CompactMonitorItems _seen = CompactMonitorItems.None;
    // The readings left out of the row as it was last built; all of them until the first figures.
    public CompactMonitorItems Hidden { get; private set; } = Later;

    // Whether the system must still be read for the row although it shows none of these readings yet: a chosen reading (chosen,
    // CompactMonitor.Shown) left out only because its first figure has not come. Without this a row showing only the temperature
    // or the system drive would have no width, so no readings would be asked for, so the first figure would never come. False
    // once each such reading's sensor is known to be missing, so a machine without them stops reading for them.
    public bool Awaits(CompactMonitorItems chosen, SensorState cpu, SensorState gpu, SensorState storage)
    {
        var waiting = chosen & Hidden & Later;
        if (!Temperatures.Readable(cpu, gpu)) waiting &= ~CompactMonitorItems.Temperature;
        if (storage == SensorState.None) waiting &= ~CompactMonitorItems.Storage;
        return waiting != CompactMonitorItems.None;
    }

    // The readings to leave out for this snapshot and the sensors' states, and those that join now (Hidden before, not now).
    public (CompactMonitorItems Hidden, CompactMonitorItems Arriving) Update(MonitorSnapshot? latest, SensorState cpu, SensorState gpu, SensorState storage)
    {
        if (Temperatures.Hottest(latest) is not null) _seen |= CompactMonitorItems.Temperature;
        if (latest is not null && Storage.System(latest.Storage, Storage.WindowsLetter) is not null) _seen |= CompactMonitorItems.Storage;
        var hidden = Later & ~_seen;
        if (!Temperatures.Readable(cpu, gpu)) hidden |= CompactMonitorItems.Temperature;
        if (storage == SensorState.None) hidden |= CompactMonitorItems.Storage;
        var arriving = Hidden & ~hidden;
        Hidden = hidden;
        return (hidden, arriving);
    }
}
