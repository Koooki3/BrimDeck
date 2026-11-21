using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// Exporting and importing the settings (ConfigBackup). An import replaces the settings, adds the file's themes and prices to
// those here, writes its API keys, and first saves what it replaces in a backup of its own, which imports back the same way.
public partial class App
{
    // The API keys entered by hand that an export can carry: the rows that saved one and whose key this account can read.
    internal IReadOnlyDictionary<Guid, string> ReadKeys()
    {
        var keys = new Dictionary<Guid, string>();
        foreach (var app in ConfigBackup.KeyedRows(Settings))
            if (ReadKey(app.InstanceId) is { Length: > 0 } key) keys[app.InstanceId] = key;
        return keys;
    }

    private string? ReadKey(Guid id)
    {
        try { return Secrets.Read(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException) { return null; }
    }

    internal BackupContent ExportContent(bool themes, bool prices, bool keys, bool beforeImport = false)
    {
        FinishWriting();
        Profiles.Keep(Settings);
        var manifest = new BackupManifest { Version = ApplicationVersion.Current, Exported = DateTimeOffset.Now, Computer = Environment.MachineName, BeforeImport = beforeImport };
        byte[]? secrets = null;
        // The backup made before an import keeps the key file as it is, still encrypted for this Windows account.
        if (beforeImport)
        {
            var file = Path.Combine(Store.DirectoryPath, "secrets.dat");
            try { if (File.Exists(file)) secrets = File.ReadAllBytes(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new BackupContent(manifest, ConfigBackup.SettingsText(Settings),
            themes ? ConfigBackup.ReadThemes(PanelTheme.Folder) : new Dictionary<string, byte[]>(),
            prices ? Prices.ManualModels : null, keys ? ReadKeys() : null, secrets);
    }

    internal static void WritePackage(string path, BackupContent content)
    {
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary)) ConfigBackup.Write(stream, content);
        File.Move(temporary, path, true);
    }

    // Reads a package. The keys of a backup made before an import are opened with this Windows account; those it cannot open stay out.
    internal static BackupPackage OpenPackage(string path)
    {
        BackupPackage package;
        using (var stream = File.OpenRead(path)) package = ConfigBackup.Read(stream, ApplicationVersion.Parse(ApplicationVersion.Current));
        if (package.ProtectedSecrets is not { Length: > 0 } file || package.Keys.Count > 0) return package;
        var keys = new Dictionary<Guid, string>();
        try
        {
            foreach (var (id, encrypted) in JsonSerializer.Deserialize<Dictionary<Guid, string>>(file) ?? [])
            {
                if (package.Settings.Entry(id) is null) continue;
                try
                {
                    var plain = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
                    try { keys[id] = Encoding.UTF8.GetString(plain); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException) { }
            }
        }
        catch (JsonException) { }
        return package.WithKeys(keys);
    }

    internal sealed record ImportOutcome(string BackupPath, IReadOnlyList<ImportNote> Notes);

    // Replaces the settings with the package's. map: the display here that takes the place of each display of the file that this
    // PC does not have (ConfigBackup.DisplaysToMatch). Throws IOException or UnauthorizedAccessException when the backup of what
    // is replaced cannot be written, before anything changes.
    internal ImportOutcome Import(BackupPackage package, IReadOnlyDictionary<string, BackupDisplay?> map)
    {
        var folder = ConfigBackup.BackupFolder(Store.DirectoryPath);
        Directory.CreateDirectory(folder);
        var backup = Path.Combine(folder, ConfigBackup.BackupName(DateTime.Now));
        WritePackage(backup, ExportContent(themes: true, prices: true, keys: false, beforeImport: true));
        ConfigBackup.PruneBackups(folder);

        var prepared = ConfigBackup.Prepare(package, Settings, map, here => ReadKey(here.InstanceId) is { Length: > 0 });
        var notes = new List<ImportNote>();
        if (package.Themes.Count > 0)
        {
            try { ConfigBackup.WriteThemes(PanelTheme.Folder, package.Themes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add(ConfigBackup.ThemesNotWritten); }
        }
        if (package.Prices.Count > 0 && Prices.MergeManual(package.Prices) < 0)
            notes.Add(ConfigBackup.PricesNotWritten);
        // Starting with Windows is the registry's to say (StartupRegistration); the file's choice is made there too.
        try { if (prepared.LaunchAtStartup != StartupRegistration.IsEnabled()) StartupRegistration.SetEnabled(prepared.LaunchAtStartup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        try { prepared.LaunchAtStartup = StartupRegistration.IsEnabled(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }

        UpdateSettings(prepared);
        foreach (var (id, key) in package.Keys)
            if (Settings.Entry(id) is not null)
            {
                try { Secrets.Write(id, key); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException) { }
            }
        if (package.Themes.Count > 0) ApplyPanelTheme(reload: true);
        FlushSettings();
        notes.AddRange(ImportNotes(package, prepared, map));
        return new(backup, notes);
    }

    // What is left to do here after an import: displays given no counterpart, devices not connected, sign-ins, keys and the clipboard sync.
    private List<ImportNote> ImportNotes(BackupPackage package, DeckSettings prepared, IReadOnlyDictionary<string, BackupDisplay?> map)
    {
        var here = Native.Displays.Current().Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmatched = ConfigBackup.DisplaysToMatch(package.Settings, here).Where(d => !map.TryGetValue(d.Id, out var to) || to is null)
            .Select(d => d.Name.Length > 0 ? d.Name : Loc.T("显示器", "Display")).ToList();
        // Only while the devices are being read is it known which ones are connected.
        var absent = new List<string>();
        if (Devices is { Settled: true, Demand: DeviceDemand.Watch } devices)
        {
            var connected = devices.All.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
            absent = prepared.DeviceChoices.Where(c => !connected.Contains(c.Key)).Select(c => string.IsNullOrWhiteSpace(c.Label) ? c.Name : c.Label!).Where(n => n.Length > 0).Distinct().ToList();
        }
        var signIns = new List<string>();
        foreach (var edition in new[] { WorkBuddyUsage.China, WorkBuddyUsage.International })
            if (prepared.Apps.Any(a => a.Enabled && a.QuotaSource == edition.Id) && ReadKey(edition.SessionKey) is not { Length: > 0 })
                signIns.Add(AppPresets.Name(edition.Id));
        if (prepared.Apps.Any(a => a.Enabled && a.QuotaSource == ProviderId.MiMo) && !MiMo.SignedIn) signIns.Add(AppPresets.Name(ProviderId.MiMo));
        var keys = ConfigBackup.KeysToEnter(package, prepared).Select(a => prepared.Entry(a.InstanceId)?.Name ?? a.Name).Distinct().ToList();
        var sync = new List<string>();
        if (prepared.WeTypeClipboardSync && !ClipboardSync.Connected(false)) sync.Add(Loc.T("微信输入法", "WeType"));
        if (prepared.DoubaoClipboardSync && !ClipboardSync.Connected(true)) sync.Add(Loc.T("豆包输入法", "Doubao IME"));
        if (prepared.LanClipboardSync && ClipboardSync.Accounts.Lan is null) sync.Add(Loc.T("局域网", "Local network"));
        return ConfigBackup.Notes(unmatched, absent, signIns, keys, sync);
    }
}
