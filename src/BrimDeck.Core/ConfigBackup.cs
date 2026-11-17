using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrimDeck.Core;

// A settings backup (.brimbackup): a ZIP file that carries the settings with every profile and rule, and, as chosen when it is
// made, the custom themes, the model prices added by hand and the API keys entered by hand. Sign-ins (WorkBuddy, the Xiaomi
// account), the clipboard sync's connections, the clipboard history, the file transfer and every cache stay on the PC they
// belong to. Nothing in it is encrypted: the API keys, when chosen, are plain text in keys.json.
//
// manifest.json   format, version and time of export, the PC's name, and whether it is a backup made before an import
// settings.json   the settings as settings.json holds them, without the earlier versions' snapshots
// themes/…        the custom theme files, by their path in the themes folder ("name.json" or "folder/theme.json")
// prices.json     the model prices added by hand
// keys.json       { source row id: API key }, when chosen
// secrets.dat     only in the backup made before an import: the key file as it was, encrypted for the Windows account
public static class ConfigBackup
{
    public const string Extension = ".brimbackup";
    public const int Format = 1;
    private const string ManifestEntry = "manifest.json", SettingsEntry = "settings.json", PricesEntry = "prices.json", KeysEntry = "keys.json",
        ProtectedEntry = "secrets.dat", ThemesFolder = "themes/";
    // What a package may hold, so that a damaged or hostile file cannot fill the memory.
    private const long MaximumEntryBytes = 16L << 20, MaximumTotalBytes = 64L << 20;
    public const int MaximumThemeBytes = 256 * 1024;
    private const int MaximumEntries = 2000;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // The settings as a package holds them: settings.json's own form, with neither the snapshots of earlier versions nor the
    // members only another build knows. A store on no folder writes nothing and keeps nothing.
    public static string SettingsText(DeckSettings settings, string? version = null)
        => new SettingsStore(Path.GetTempPath(), version).Serialize(settings.Copy());

    public static void Write(Stream stream, BackupContent content)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        void Add(string name, byte[] bytes)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var target = entry.Open();
            target.Write(bytes);
        }
        void Text(string name, string text) => Add(name, Encoding.UTF8.GetBytes(text));
        Text(ManifestEntry, JsonSerializer.Serialize(content.Manifest, Indented));
        Text(SettingsEntry, content.Settings);
        foreach (var (path, bytes) in content.Themes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) Add(ThemesFolder + path, bytes);
        if (content.Prices is { Count: > 0 } prices) Text(PricesEntry, JsonSerializer.Serialize(prices, Indented));
        if (content.Keys is { Count: > 0 } keys) Text(KeysEntry, JsonSerializer.Serialize(keys.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value), Indented));
        if (content.ProtectedSecrets is { Length: > 0 } secrets) Add(ProtectedEntry, secrets);
    }

    // Reads a package and brings its settings up to the running version (SettingsMigrations). A file that is not a package, or a
    // damaged one, throws BackupException(NotBackup); one from a later version throws BackupException(Newer).
    public static BackupPackage Read(Stream stream, Version running)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
            if (zip.Entries.Count > MaximumEntries) throw Damaged();
            long total = 0;
            byte[] Bytes(ZipArchiveEntry entry, long limit)
            {
                if (entry.Length > limit || (total += entry.Length) > MaximumTotalBytes) throw Damaged();
                using var source = entry.Open();
                using var copy = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    // Length is what the file claims; the data itself is held to the same limit.
                    if (copy.Length + read > limit) throw Damaged();
                    copy.Write(buffer, 0, read);
                }
                return copy.ToArray();
            }
            string? Text(string name) => zip.GetEntry(name) is { } entry ? Encoding.UTF8.GetString(Bytes(entry, MaximumEntryBytes)) : null;

            var manifest = JsonSerializer.Deserialize<BackupManifest>(Text(ManifestEntry) ?? throw Damaged()) ?? throw Damaged();
            if (manifest.Format < 1) throw Damaged();
            var made = ApplicationVersion.Parse(manifest.Version);
            if (manifest.Format > Format || made > running) throw new BackupException(BackupProblem.Newer, manifest.Version);
            var root = SettingsMigrations.Parse(Text(SettingsEntry) ?? throw Damaged());
            root.Remove("Snapshots");
            if (SettingsMigrations.VersionOf(root) > running) throw new BackupException(BackupProblem.Newer, SettingsMigrations.VersionOf(root).ToString(3));
            var settings = SettingsMigrations.Read(root, running, out _);

            var themes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(ThemesFolder, StringComparison.OrdinalIgnoreCase) || name.EndsWith('/')) continue;
                // Only what the themes folder itself would hold; anything else in the folder is left out.
                if (ThemePath(name[ThemesFolder.Length..]) is { } path && entry.Length <= MaximumThemeBytes) themes[path] = Bytes(entry, MaximumThemeBytes);
            }
            var prices = Text(PricesEntry) is { } priceText ? JsonSerializer.Deserialize<List<ModelPrice>>(priceText) ?? [] : [];
            var keys = new Dictionary<Guid, string>();
            if (Text(KeysEntry) is { } keyText)
                foreach (var (id, key) in JsonSerializer.Deserialize<Dictionary<string, string>>(keyText) ?? [])
                    if (Guid.TryParse(id, out var guid) && !string.IsNullOrEmpty(key)) keys[guid] = key;
            var secrets = zip.GetEntry(ProtectedEntry) is { } protectedEntry ? Bytes(protectedEntry, MaximumEntryBytes) : null;
            return new BackupPackage(manifest, settings, themes, prices, keys, secrets);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or NotSupportedException or ArgumentException or FormatException)
        { throw Damaged(); }
    }

    private static BackupException Damaged() => new(BackupProblem.NotBackup, null);

    // A theme file's path inside the themes folder, as LoadFolder reads it: "name.json", or "folder/theme.json". Anything else,
    // including a path that would leave the folder, is null.
    public static string? ThemePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        static bool Plain(string part) => part.Length is > 0 and <= 120 && part != "." && part != ".." && part.Trim() == part && !part.EndsWith('.')
            && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && part.IndexOfAny(['/', '\\', ':']) < 0;
        if (parts.Length == 1 && Plain(parts[0]) && parts[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return parts[0];
        if (parts.Length == 2 && Plain(parts[0]) && parts[1].Equals("theme.json", StringComparison.OrdinalIgnoreCase)) return parts[0] + "/theme.json";
        return null;
    }

    // The theme files in the user's themes folder, by their path in it.
    public static Dictionary<string, byte[]> ReadThemes(string folder)
    {
        var themes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(folder)) return themes;
            var files = Directory.EnumerateFiles(folder, "*.json").Select(Path.GetFileName)
                .Concat(Directory.EnumerateDirectories(folder).Where(dir => File.Exists(Path.Combine(dir, "theme.json"))).Select(dir => Path.GetFileName(dir) + "/theme.json"));
            foreach (var file in files)
            {
                if (file is null || ThemePath(file) is not { } path) continue;
                var full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
                if (new FileInfo(full).Length <= MaximumThemeBytes) themes[path] = File.ReadAllBytes(full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return themes;
    }

    // Writes theme files into the themes folder: one with the same path is replaced, the others in the folder stay.
    public static void WriteThemes(string folder, IReadOnlyDictionary<string, byte[]> themes)
    {
        foreach (var (path, bytes) in themes)
        {
            if (ThemePath(path) is not { } safe) continue;
            var full = Path.Combine(folder, safe.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temporary = full + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, full, true);
        }
    }

    // The source rows whose API key a package can carry: those that saved one (AppEntry.SecretRevision).
    public static IEnumerable<AppEntry> KeyedRows(DeckSettings settings) => settings.Apps.Where(app => app.SecretRevision != Guid.Empty);

    // The displays an imported file names that this PC does not have: the chosen displays (when the panels show on chosen ones) and
    // those in the rules, once each.
    public static IReadOnlyList<BackupDisplay> DisplaysToMatch(DeckSettings imported, IEnumerable<string> here)
    {
        var known = new HashSet<string>(here, StringComparer.OrdinalIgnoreCase);
        var named = (imported.Display == DisplayPlacement.Chosen ? imported.DisplayMonitors.Select(d => new BackupDisplay(d.Id, d.Name)) : [])
            .Concat(imported.ProfileRules.SelectMany(rule => rule.Displays).Select(d => new BackupDisplay(d.Id, d.Name)));
        return named.Where(d => d.Id.Length > 0 && !known.Contains(d.Id)).DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The display of this PC to start each one at: one of the same model that neither the file names nor an earlier one took; none
    // when there is no such display.
    public static IReadOnlyList<string?> GuessDisplays(IReadOnlyList<BackupDisplay> missing, IReadOnlyList<BackupDisplay> here, IEnumerable<string> namedInFile)
    {
        var taken = new HashSet<string>(namedInFile, StringComparer.OrdinalIgnoreCase);
        var guesses = new List<string?>();
        foreach (var old in missing)
        {
            var match = here.FirstOrDefault(d => !taken.Contains(d.Id) && old.Name.Length > 0 && string.Equals(d.Name, old.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) taken.Add(match.Id);
            guesses.Add(match?.Id);
        }
        return guesses;
    }

    // The settings an import puts in place of the current ones.
    // map: for a display of the file that this PC does not have, the display here that takes its place (null: none).
    // keptKey: whether this PC keeps the key of a source row that the file has too (the same row, from the same source).
    public static DeckSettings Prepare(BackupPackage package, DeckSettings current, IReadOnlyDictionary<string, BackupDisplay?> map, Func<AppEntry, bool> keptKey)
    {
        var settings = package.Settings.Copy();
        // A rule that held while the file was made has nothing to hold on here yet; its profile goes back as at a start.
        if (settings.ReturnProfile is { } back && Profiles.Find(settings, back) is not null) settings = Profiles.Switch(settings, back);
        settings.ReturnProfile = null;
        BackupDisplay? Target(string id) => map.TryGetValue(id, out var target) ? target : null;
        settings.DisplayMonitors = [.. settings.DisplayMonitors.Select(d => Target(d.Id) is { } to ? d with { Id = to.Id, Name = to.Name } : d)
            .DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase)];
        foreach (var rule in settings.ProfileRules)
        {
            foreach (var display in rule.Displays)
                if (Target(display.Id) is { } to) { display.Id = to.Id; display.Name = to.Name; }
            rule.Displays = [.. rule.Displays.DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase)];
        }
        var keys = package.Keys;
        foreach (var app in settings.Apps)
        {
            if (keys.ContainsKey(app.InstanceId)) { if (app.SecretRevision == Guid.Empty) app.SecretRevision = Guid.NewGuid(); continue; }
            var here = current.Entry(app.InstanceId);
            app.SecretRevision = here is not null && here.QuotaSource == app.QuotaSource && here.SecretRevision != Guid.Empty && keptKey(here) ? here.SecretRevision : Guid.Empty;
        }
        settings.Normalize();
        return settings;
    }

    // The rows of an imported file that had an API key, and have none after the import.
    public static IReadOnlyList<AppEntry> KeysToEnter(BackupPackage package, DeckSettings prepared)
        => [.. package.Settings.Apps.Where(app => app.SecretRevision != Guid.Empty && prepared.Entry(app.InstanceId)?.SecretRevision == Guid.Empty)];

    // What is left to do after an import, in the order the import window lists it; a list with no names says nothing.
    public static List<ImportNote> Notes(IReadOnlyList<string> unmatchedDisplays, IReadOnlyList<string> absentDevices, IReadOnlyList<string> signIns,
        IReadOnlyList<string> keys, IReadOnlyList<string> sync)
    {
        var notes = new List<ImportNote>();
        if (unmatchedDisplays.Count > 0)
            notes.Add(new(Loc.T("这些显示器没有对应，它们的设置会保留，在“显示器与配置”中可以删除：",
                "These displays were given no counterpart. Their settings are kept and can be removed in Displays and profiles:"), unmatchedDisplays));
        if (absentDevices.Count > 0)
            notes.Add(new(Loc.T("这些设备现在没有连接，连接后按导入的名称和颜色显示：",
                "These devices are not connected now. Once connected, they show with the imported names and colors:"), absentDevices));
        if (signIns.Count > 0) notes.Add(new(Loc.T("这些来源需要在“配额与统计”中重新登录：", "Sign in again in Quotas and usage for:"), signIns));
        if (keys.Count > 0) notes.Add(new(Loc.T("这些来源需要在“配额与统计”中重新填写 API 密钥：", "Enter the API key again in Quotas and usage for:"), keys));
        if (sync.Count > 0) notes.Add(new(Loc.T("剪贴板同步需要在“剪贴板同步”中重新连接：", "Connect the clipboard sync again in Clipboard sync:"), sync));
        return notes;
    }
    public static ImportNote ThemesNotWritten => new(Loc.T("主题文件无法写入主题文件夹，主题没有导入。", "The theme files could not be written to the themes folder; the themes were not imported."), []);
    public static ImportNote PricesNotWritten => new(Loc.T("手动添加的模型单价无法写入，单价没有导入。", "The model prices added by hand could not be written; the prices were not imported."), []);

    // The file name an export is given: "BrimDeck-配置-2026-11-17.brimbackup", "…-含密钥" when it carries keys.
    public static string FileName(DateTime day, bool keys) =>
        "BrimDeck-" + Loc.T("配置", "settings") + "-" + day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
        + (keys ? Loc.T("-含密钥", "-with-keys") : "") + Extension;

    // The backups made before imports, the newest five kept.
    public const int KeptBackups = 5;
    public static string BackupFolder(string dataFolder) => Path.Combine(dataFolder, "backups");
    public static string BackupName(DateTime time) => "before-import-" + time.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + Extension;
    public static void PruneBackups(string folder)
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(folder, "before-import-*" + Extension).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(KeptBackups))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class BackupManifest
{
    public int Format { get; set; } = ConfigBackup.Format;
    public string Version { get; set; } = "";
    public DateTimeOffset Exported { get; set; }
    public string Computer { get; set; } = "";
    // Made by BrimDeck itself just before an import, to undo it.
    public bool BeforeImport { get; set; }
}

public sealed record BackupContent(BackupManifest Manifest, string Settings, IReadOnlyDictionary<string, byte[]> Themes,
    IReadOnlyList<ModelPrice>? Prices, IReadOnlyDictionary<Guid, string>? Keys, byte[]? ProtectedSecrets = null);

public sealed record BackupPackage(BackupManifest Manifest, DeckSettings Settings, IReadOnlyDictionary<string, byte[]> Themes,
    IReadOnlyList<ModelPrice> Prices, IReadOnlyDictionary<Guid, string> Keys, byte[]? ProtectedSecrets)
{
    // The keys it carries, once the encrypted key file of a backup made before an import has been opened (ConfigBackup.Read leaves
    // it as it is).
    public BackupPackage WithKeys(IReadOnlyDictionary<Guid, string> keys) => this with { Keys = keys };
}

// A display as a file names it: its device path and its name at the time.
public sealed record BackupDisplay(string Id, string Name);

// Something left to do after an import: what it is, and the names it concerns.
public sealed record ImportNote(string Text, IReadOnlyList<string> Names);

public enum BackupProblem { NotBackup, Newer }
public sealed class BackupException(BackupProblem problem, string? version) : Exception(problem.ToString())
{
    public BackupProblem Problem { get; } = problem;
    // The version that made the file, for Newer.
    public string? Version { get; } = version;
}
