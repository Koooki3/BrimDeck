using BrimDeck.Core;

// The first load of the collapsed row's temperature and system drive: before their first figures the row leaves them out and does
// not change; when the figures arrive the row is built once more and they come in with its entrance; a machine without the
// sensor never shows them, and nothing is built again for it.
static class StripAdmissionTests
{
    private const CompactMonitorItems Temperature = CompactMonitorItems.Temperature, Storage_ = CompactMonitorItems.Storage, Later = StripAdmission.Later;
    private const SensorState Unknown = SensorState.Unknown, Found = SensorState.Found, None = SensorState.None;

    public static void Run(Action<string, bool> check)
    {
        FirstLoad(check);
        Missing(check);
        OnlyLater(check);
        Presence(check);
    }

    private static MonitorSnapshot Snapshot(double? gpuHeat, bool drive) => MonitorSnapshot.Empty(4, "test") with
    {
        Gpu = new GpuSample(.1, Reading<ulong>.Unavailable("")) { Temperature = gpuHeat },
        Storage = drive ? [new StorageDrive(0, "SSD", [new StorageVolume(Storage.WindowsLetter ?? "C:", "", 100UL << 30, 40UL << 30)])] : []
    };

    // Counts the times the row would be built again (the hidden set changing) and what arrives each time.
    private static (int Rebuilds, List<CompactMonitorItems> Arrivals, CompactMonitorItems Hidden) Run(IEnumerable<(MonitorSnapshot? Snapshot, SensorState Cpu, SensorState Gpu, SensorState Storage)> ticks)
    {
        var admission = new StripAdmission();
        var built = admission.Hidden;
        int rebuilds = 0; var arrivals = new List<CompactMonitorItems>();
        foreach (var (snapshot, cpu, gpu, storage) in ticks)
        {
            var (hidden, arriving) = admission.Update(snapshot, cpu, gpu, storage);
            if (hidden == built) continue;
            rebuilds++; arrivals.Add(arriving); built = hidden;
        }
        return (rebuilds, arrivals, built);
    }

    private static void FirstLoad(Action<string, bool> check)
    {
        check("Strip admission: both later readings are left out before any figure", new StripAdmission().Hidden == Later);
        // The sampler starting: no snapshot, then snapshots before the sensors answer, then the first temperature, then the drive.
        var ticks = new List<(MonitorSnapshot?, SensorState, SensorState, SensorState)>
        {
            (null, Unknown, Unknown, Unknown), (Snapshot(null, false), Unknown, Unknown, Unknown), (Snapshot(null, false), None, Unknown, Unknown),
            (Snapshot(48, false), None, Found, Unknown), (Snapshot(49, false), None, Found, Unknown), (Snapshot(49, true), None, Found, Found), (Snapshot(50, true), None, Found, Found)
        };
        var (rebuilds, arrivals, hidden) = Run(ticks);
        check("Strip admission: the row stays as it is until a result arrives, then each reading joins once", rebuilds == 2 && arrivals.SequenceEqual([Temperature, Storage_]) && hidden == CompactMonitorItems.None);
        var together = Run([(null, Unknown, Unknown, Unknown), (Snapshot(48, true), None, Found, Found), (Snapshot(48, true), None, Found, Found)]);
        check("Strip admission: both arriving together build the row once", together.Rebuilds == 1 && together.Arrivals.Single() == Later);
        var flicker = Run([(Snapshot(48, true), None, Found, Found), (Snapshot(null, true), None, Found, Found), (Snapshot(null, false), None, Found, Found), (Snapshot(50, true), None, Found, Found)]);
        check("Strip admission: a figure missing for a moment does not take a reading out again", flicker.Rebuilds == 1 && flicker.Hidden == CompactMonitorItems.None);
    }

    private static void Missing(Action<string, bool> check)
    {
        var none = Run([(null, Unknown, Unknown, Unknown), (Snapshot(null, false), None, Unknown, None), (Snapshot(null, false), None, None, None), (Snapshot(null, false), None, None, None)]);
        check("Strip admission: without sensors the readings never show, and the row is never built again for them", none.Rebuilds == 0 && none.Hidden == Later);
        var drivesOnly = Run([(null, Unknown, Unknown, Unknown), (Snapshot(null, true), None, None, Found), (Snapshot(null, true), None, None, Found)]);
        check("Strip admission: only the drive joins on a machine without temperatures", drivesOnly.Rebuilds == 1 && drivesOnly.Arrivals.Single() == Storage_ && drivesOnly.Hidden == Temperature);
        var lost = Run([(Snapshot(48, true), None, Found, Found), (Snapshot(null, true), None, None, Found)]);
        check("Strip admission: a reading leaves only once its sensor is known to be missing", lost.Rebuilds == 2 && lost.Hidden == Temperature && lost.Arrivals[1] == CompactMonitorItems.None);
    }

    // After a restart with only the temperature, or only the system drive, chosen for the row: before their first figures the row
    // shows nothing and has no width, yet the system must be read for it (Awaits), or the first figure would never come. Once it
    // has come the reading joins; on a machine without the sensor the row stops asking.
    private static void OnlyLater(Action<string, bool> check)
    {
        foreach (var (chosen, name) in new[] { (Temperature, "temperature"), (Storage_, "system drive"), (Later, "both") })
        {
            var admission = new StripAdmission();
            var shownNow = chosen & ~admission.Hidden;
            check($"Strip admission ({name} only, after a restart): the row shows nothing yet", shownNow == CompactMonitorItems.None);
            check($"Strip admission ({name} only): the system is still read for it", admission.Awaits(chosen, Unknown, Unknown, Unknown)
                && admission.Awaits(chosen, None, Unknown, Unknown));
            var (hidden, arriving) = admission.Update(Snapshot(48, true), None, Found, Found);
            check($"Strip admission ({name} only): the first figure arrives and the reading joins", (hidden & chosen) == CompactMonitorItems.None && (arriving & chosen) == chosen
                && !admission.Awaits(chosen, None, Found, Found));
        }
        var bare = new StripAdmission();
        bare.Update(Snapshot(null, false), None, None, None);
        check("Strip admission: without the sensors the row stops asking for readings", !bare.Awaits(Later, None, None, None) && !bare.Awaits(Temperature, None, None, Found));
        check("Strip admission: other readings do not wait", !new StripAdmission().Awaits(CompactMonitorItems.Cpu | CompactMonitorItems.Memory, Unknown, Unknown, Unknown));
    }

    // The row's presence: a reading that arrives comes in from nothing and eases to its place; one the settings choose shows at once;
    // with the animations off it is in place at once.
    private static void Presence(Action<string, bool> check)
    {
        var shown = CompactMonitorItems.Cpu | CompactMonitorItems.Memory;
        var presence = new StripPresence();
        presence.Set(shown, shown, motion: true);
        double now = Eased.Now;
        check("Strip presence: readings in place before anything arrives", presence.At(CompactMonitorItems.Cpu, now) == 1 && !presence.Moving(now) && presence.At(Temperature, now) == 0);
        presence.Arrive(Temperature);
        bool moving = presence.Set(shown | Temperature, shown | Temperature, motion: true);
        now = Eased.Now;
        check("Strip presence: an arriving reading eases in from nothing, the others stay", moving && presence.At(Temperature, now) < .5 && presence.Moving(now)
            && presence.At(CompactMonitorItems.Cpu, now) == 1);
        check("Strip presence: it reaches its place", presence.At(Temperature, now + Eased.Seconds * 2) == 1);
        presence.Set(shown | Temperature, shown | Temperature, motion: true);
        check("Strip presence: a later arrange does not start it again", presence.At(Temperature, Eased.Now + Eased.Seconds * 2) == 1);
        var chosen = new StripPresence();
        chosen.Set(shown, shown, motion: true);
        chosen.Set(shown | Storage_, shown | Storage_, motion: true);
        check("Strip presence: a reading the settings choose shows at once", chosen.At(Storage_, Eased.Now) == 1 && !chosen.Moving(Eased.Now));
        var still = new StripPresence();
        still.Set(shown, shown, motion: false);
        still.Arrive(Temperature);
        still.Set(shown | Temperature, shown | Temperature, motion: false);
        check("Strip presence: with animations off an arrival is in place at once", still.At(Temperature, Eased.Now) == 1 && !still.Moving(Eased.Now));
        var crowded = new StripPresence();
        crowded.Set(shown, shown, motion: true);
        crowded.Arrive(Temperature);
        crowded.Set(shown | Temperature, shown, motion: true);
        check("Strip presence: an arrival with no room stays out", crowded.At(Temperature, Eased.Now + Eased.Seconds * 2) == 0);
    }
}
