using System.Text.Json;

namespace BrimDeck.Core.Notices;

// "Turn off other apps' notification banners": while BrimDeck shows Windows notifications on the island, Windows' own banner
// (the pop-up at the bottom right) would show each one a second time. Windows keeps that choice per application as the DWORD
// ShowBanner under HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\<AppUserModelId>, the value
// Settings › System › Notifications › <app> › "Show notification banners" writes: 0 is off; 1 or no value is on. Windows does
// not document it; it takes effect at once. The notification itself still goes to the notification center, where BrimDeck
// reads it.
//
// BrimDeck changes nothing else: only that value, only under HKCU. Before it writes, it records what was there (no key, no
// value, 0, 1, or a value of another kind) in its own ledger (BannerLedger), and restores exactly that when the switch goes
// off, an application is set to off or muted in BrimDeck, notices or the Windows source are switched off, Windows no longer
// lets BrimDeck read notifications, BrimDeck exits, or BrimDeck is uninstalled (BrimDeck.exe --restore-banners). The ledger is
// written to the disk before the registry, with each new record marked pending until its value is written, so a crash between
// the two never leaves a change BrimDeck does not know about, nor one it mistakes for the user's; the next start finishes the job.
public readonly record struct BannerReading(bool KeyExists, int? ShowBanner, int? Enabled, BannerValue? Other = null)
{
    // The user switched the application's notifications off in Windows: none reach the notification center, so BrimDeck could
    // not show them either. Such an application is never touched.
    public bool NotificationsOff => Enabled == 0;
    public bool BannerOff => ShowBanner == 0;
}

// A ShowBanner that is not a DWORD (written by hand or by some tool), kept as its registry kind ("String", "QWord", "Binary",
// ...) and its data in text (binary data in Base64, a multi-string's lines joined by '\n'), so that it can be put back as it was.
public sealed record BannerValue(string Kind, string Data);

// Windows' per-application notification settings (NoticeBannerRegistry in the app; a dictionary in the tests).
public interface IBannerRegistry
{
    BannerReading Read(string appId);
    // Sets ShowBanner (creating the application's key if needed); null deletes the value.
    void Write(string appId, int? showBanner);
    // Puts back a ShowBanner of another kind, as it was.
    void WriteValue(string appId, BannerValue value);
    // Deletes the application's key if it holds no value and no subkey (one BrimDeck created and Windows never used).
    void DeleteKeyIfEmpty(string appId);
}

// What BrimDeck changed for one application: the ShowBanner it found (Previous for a DWORD, PreviousValue for another kind, both
// null: none), whether it had to create the key, and whether the record is still pending: saved, but BrimDeck's 0 perhaps not
// written yet.
public sealed class BannerRecord
{
    public string AppId { get; set; } = "";
    public int? Previous { get; set; }
    public BannerValue? PreviousValue { get; set; }
    public bool CreatedKey { get; set; }
    public bool Pending { get; set; }

    // Whether the application's value is still the one found before BrimDeck changed it.
    internal bool Unchanged(BannerReading reading) => !reading.BannerOff && reading.ShowBanner == Previous && Equals(reading.Other, PreviousValue);
}

public enum BannerState
{
    // Not turned off by BrimDeck (the switch is off, or the application is left alone).
    None,
    // BrimDeck turned the banner off and will restore it.
    TurnedOff,
    // The banner was already off in Windows; BrimDeck changed nothing, so there is nothing to restore.
    AlreadyOff,
    // The application's notifications are off in Windows; BrimDeck does not touch it.
    NotificationsOff,
    // The banner was turned on again outside BrimDeck (in Windows Settings, or by the application); BrimDeck leaves it on
    // while the switch stays on.
    Released,
    // The registry or the ledger could not be written, or Windows did not keep the value; tried again with the next change.
    Failed
}

// BrimDeck's record of the banners it turned off, kept in its data folder apart from settings.json: the uninstaller reads it,
// and a settings file restored from a snapshot or another version can never lose it. Released lists the applications whose
// banner the user turned on again while the switch was on; BrimDeck leaves them alone until the switch goes off. Each save
// reaches the disk before it replaces the file, and keeps the one before as a backup (BackupSuffix), read when the file is not.
public sealed class BannerLedger
{
    public const string FileName = "notice-banners.json", BackupSuffix = ".bak";
    public const int MaxRecords = 2000;
    public List<BannerRecord> Records { get; set; } = [];
    public List<string> Released { get; set; } = [];

    public BannerRecord? Find(string appId) => Records.FirstOrDefault(record => string.Equals(record.AppId, appId, StringComparison.OrdinalIgnoreCase));
    public bool IsReleased(string appId) => Released.Contains(appId, StringComparer.OrdinalIgnoreCase);

    // Null when neither the file nor its backup can be read: then nothing is turned off or restored, so records BrimDeck cannot
    // see are never written over. No file is an empty ledger.
    public static BannerLedger? Load(string path)
    {
        if (!File.Exists(path) && !File.Exists(path + BackupSuffix)) return new BannerLedger();
        return Read(path) ?? Read(path + BackupSuffix);
    }

    private static BannerLedger? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var ledger = JsonSerializer.Deserialize<BannerLedger>(File.ReadAllText(path));
            if (ledger is null) return null;
            ledger.Records = (ledger.Records ?? []).Where(record => record is not null && NoticeBanners.Usable(record.AppId)
                    && (record.PreviousValue is null ? record.Previous is not 0 :record.Previous is null && record.PreviousValue.Kind.Length > 0 && record.PreviousValue.Data is not null))
                .DistinctBy(record => record.AppId, StringComparer.OrdinalIgnoreCase).Take(MaxRecords).ToList();
            ledger.Released = (ledger.Released ?? []).Where(NoticeBanners.Usable).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRecords).ToList();
            return ledger;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (Records.Count == 0 && Released.Count == 0) { File.Delete(path); File.Delete(path + BackupSuffix); return true; }
            var temporary = path + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true });
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + BackupSuffix, ignoreMetadataErrors: true);
            else File.Move(temporary, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

public static class NoticeBanners
{
    public const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    // An application id that can name a key under SettingsKey: a registry path of non-empty parts (Windows keeps a desktop
    // program without a registered id as "{known folder}\folder\app.exe", nested keys), none of them "." or "..", without
    // control characters. Tray balloons (named by a number that changes), file paths ("C:\...") and links, which the Start
    // menu lists but Windows never names a notification by, are left out; a colon or slash may only follow the "!" of a
    // packaged id, as in an Edge web app's ("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!https://web.whatsapp.com/").
    public static bool Usable(string? appId)
    {
        if (string.IsNullOrEmpty(appId) || appId.Length > 256 || appId.Any(char.IsControl)) return false;
        int bang = appId.IndexOf('!');
        if (appId.IndexOfAny([':', '/']) is var odd and >= 0 && (bang < 0 || odd < bang)) return false;
        if (appId.StartsWith(WindowsNoticeApps.TrayPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return appId.Split('\\').All(part => part.Length > 0 && part.Trim().Length > 0 && part is not "." and not "..");
    }

    // The applications whose banners BrimDeck should turn off now: every known one (candidates: the Start menu's applications,
    // those Windows already keeps notification settings for, and those notices arrived from) while the switch, notices and the
    // Windows source are on and Windows lets BrimDeck read notifications. Not BrimDeck itself (own), not one the user set to off
    // or muted in BrimDeck (its notices only show in Windows), and not one whose banner the user turned on again (Released).
    public static HashSet<string> Wanted(DeckSettings settings, bool windowsReadable, IEnumerable<string> candidates, IEnumerable<string> own, BannerLedger ledger)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!settings.NoticesEnabled || !settings.NoticeWindowsSource || !settings.NoticeHideBanners || !windowsReadable) return wanted;
        var skip = new HashSet<string>(own, StringComparer.OrdinalIgnoreCase);
        foreach (var appId in candidates)
        {
            if (!Usable(appId) || skip.Contains(appId) || ledger.IsReleased(appId)) continue;
            if (settings.NoticeRule(NoticeKeys.Windows(appId)) is { } rule && (!rule.Enabled || rule.Muted)) continue;
            wanted.Add(appId);
        }
        return wanted;
    }

    // Brings Windows in line with what is wanted: restores what is no longer wanted, then records and turns off what is. The
    // ledger is saved (save says whether that worked) after the restorations and before any new value is written, and once more
    // when the new values are in. Returns each touched application's state. switchOn is the user's switch itself, whether or not
    // it applies right now: while it is on, an application whose banner was turned on again outside BrimDeck joins
    // ledger.Released (also when it is found while restoring, as on quitting), so the two never fight; switched off, Released is
    // cleared.
    public static Dictionary<string, BannerState> Reconcile(IReadOnlySet<string> wanted, bool switchOn, BannerLedger ledger, IBannerRegistry registry, Func<bool> save)
    {
        var states = new Dictionary<string, BannerState>(StringComparer.OrdinalIgnoreCase);
        bool changed = !switchOn && ledger.Released.Count > 0;
        if (!switchOn) ledger.Released.Clear();
        void Release(string appId) { if (switchOn && !ledger.IsReleased(appId)) ledger.Released.Add(appId); }
        // Restorations first. Each is safe to repeat: a value already restored no longer reads as BrimDeck's 0, so a crash before
        // the ledger is saved only leaves a record the next run drops.
        foreach (var record in ledger.Records.ToList())
        {
            try
            {
                var reading = registry.Read(record.AppId);
                bool keep = wanted.Contains(record.AppId) && !reading.NotificationsOff;
                if (record.Pending)
                {
                    // Saved, and then perhaps stopped before BrimDeck's 0 was written: a value still as it was found is no change
                    // of the user's. It is written now if the application is still wanted, else the record just goes.
                    if (reading.BannerOff || record.Unchanged(reading))
                    {
                        record.Pending = false; changed = true;
                        if (keep && !reading.BannerOff && !TurnOff(record, registry)) { ledger.Records.Remove(record); states[record.AppId] = BannerState.Failed; continue; }
                        if (keep) { states[record.AppId] = BannerState.TurnedOff; continue; }
                        if (reading.BannerOff) Restore(record, registry);
                        else if (record.CreatedKey && record.Previous is null && record.PreviousValue is null) registry.DeleteKeyIfEmpty(record.AppId);
                        ledger.Records.Remove(record);
                        states[record.AppId] = reading.NotificationsOff ? BannerState.NotificationsOff : BannerState.None;
                        continue;
                    }
                }
                if (keep)
                {
                    if (reading.BannerOff) { states[record.AppId] = BannerState.TurnedOff; continue; }
                    // Turned on again outside BrimDeck: that is the user's (or the application's) choice now.
                    ledger.Records.Remove(record); changed = true;
                    Release(record.AppId);
                    states[record.AppId] = BannerState.Released;
                    continue;
                }
                // Restored only where BrimDeck's 0 is still there; a value changed since is left as it is.
                if (reading.BannerOff) Restore(record, registry);
                ledger.Records.Remove(record); changed = true;
                // Turned on again outside BrimDeck before this restoration (quitting gives everything back at once): with the
                // switch still on, that app stays the user's at the next start too.
                if (!reading.BannerOff && !reading.NotificationsOff) Release(record.AppId);
                states[record.AppId] = reading.NotificationsOff ? BannerState.NotificationsOff : BannerState.None;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { states[record.AppId] = BannerState.Failed; }
        }
        // Then the new ones: all recorded as pending and saved before Windows is changed.
        var fresh = new List<BannerRecord>();
        foreach (var appId in wanted)
        {
            if (states.ContainsKey(appId) || ledger.Find(appId) is not null || !Usable(appId)) continue;
            try
            {
                var reading = registry.Read(appId);
                if (reading.NotificationsOff) { states[appId] = BannerState.NotificationsOff; continue; }
                if (reading.BannerOff) { states[appId] = BannerState.AlreadyOff; continue; }
                if (ledger.Records.Count >= BannerLedger.MaxRecords) { states[appId] = BannerState.Failed; continue; }
                var record = new BannerRecord { AppId = appId, Previous = reading.ShowBanner, PreviousValue = reading.Other, CreatedKey = !reading.KeyExists, Pending = true };
                ledger.Records.Add(record); fresh.Add(record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { states[appId] = BannerState.Failed; }
        }
        if (!changed && fresh.Count == 0) return states;
        if (!save())
        {
            // Nothing new is written without its record on disk.
            foreach (var record in fresh) { ledger.Records.Remove(record); states[record.AppId] = BannerState.Failed; }
            return states;
        }
        if (fresh.Count == 0) return states;
        foreach (var record in fresh)
        {
            if (TurnOff(record, registry)) { record.Pending = false; states[record.AppId] = BannerState.TurnedOff; continue; }
            ledger.Records.Remove(record);
            states[record.AppId] = BannerState.Failed;
        }
        // The pending marks go (a failed save leaves them, which the next run settles as above).
        save();
        return states;
    }

    // Writes BrimDeck's 0 and reads it back: a value Windows (or a policy) does not keep would leave the banner on while BrimDeck
    // reports it off. False when it did not stay.
    private static bool TurnOff(BannerRecord record, IBannerRegistry registry)
    {
        try
        {
            registry.Write(record.AppId, 0);
            return registry.Read(record.AppId).BannerOff;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    // Puts back what was there before BrimDeck's 0: the value, a value of another kind, or none (and the key, if BrimDeck made
    // it and Windows has not used it since).
    private static void Restore(BannerRecord record, IBannerRegistry registry)
    {
        if (record.PreviousValue is { } value) registry.WriteValue(record.AppId, value);
        else if (record.Previous != 0) registry.Write(record.AppId, record.Previous);
        if (record.CreatedKey && record.Previous is null && record.PreviousValue is null) registry.DeleteKeyIfEmpty(record.AppId);
    }
}
