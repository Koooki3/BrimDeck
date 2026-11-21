using System.IO.Compression;
using System.Text;
using BrimDeck.Core;

internal static class ConfigBackupTests
{
    private static readonly Version Running = new(9, 0, 0);

    private static BackupContent Content(DeckSettings settings, string version = "9.0.0", IReadOnlyDictionary<string, byte[]>? themes = null,
        IReadOnlyList<ModelPrice>? prices = null, IReadOnlyDictionary<Guid, string>? keys = null)
        => new(new BackupManifest { Version = version, Exported = new DateTimeOffset(2026, 11, 17, 21, 30, 0, TimeSpan.FromHours(8)), Computer = "STUDY-PC" },
            ConfigBackup.SettingsText(settings, version), themes ?? new Dictionary<string, byte[]>(), prices, keys);

    private static byte[] Pack(BackupContent content)
    {
        using var stream = new MemoryStream();
        ConfigBackup.Write(stream, content);
        return stream.ToArray();
    }

    private static BackupPackage Unpack(byte[] bytes) { using var stream = new MemoryStream(bytes); return ConfigBackup.Read(stream, Running); }

    private static BackupProblem? Problem(byte[] bytes)
    {
        try { Unpack(bytes); return null; }
        catch (BackupException ex) { return ex.Problem; }
    }

    public static void Run(string root, Action<string, bool> check)
    {
        var settings = new DeckSettings { Width = 812, Display = DisplayPlacement.Chosen };
        settings.Normalize();
        var first = settings.CurrentProfile;
        settings.Profiles.Add(Profiles.Duplicate(settings, first, "Work"));
        var second = settings.Profiles[1].Id;
        settings.DisplayMonitors = [new ChosenDisplay(@"\\?\DISPLAY#DEL40B6#OLD-A", "DELL U2720Q") { Profile = second }, new ChosenDisplay(@"\\?\DISPLAY#BOE#OLD-B", "Built-in display")];
        settings.Display = DisplayPlacement.Chosen;
        settings.ProfileRules = [new ProfileRule { Name = "Code", Path = @"C:\Users\old\AppData\Local\Programs\Code\Code.exe", Profile = second,
            Displays = [new ProfileRuleDisplay { Id = @"\\?\DISPLAY#DEL40B6#OLD-A", Name = "DELL U2720Q", Hidden = true }] }];
        var keyed = new AppEntry { QuotaSource = ProviderId.DeepSeek, SecretRevision = Guid.NewGuid() };
        var unkeyed = new AppEntry { QuotaSource = ProviderId.OpenRouter, SecretRevision = Guid.NewGuid() };
        settings.Apps = [new AppEntry { QuotaSource = ProviderId.Claude }, keyed, unkeyed];

        var text = ConfigBackup.SettingsText(settings, "9.0.0");
        check("a backup's settings leave out the snapshots of earlier versions", !text.Contains("Snapshots") && text.Contains("\"AppVersion\": \"9.0.0\""));

        var theme = Encoding.UTF8.GetBytes("""{"Id":"dusk","Name":"Dusk"}""");
        var price = new ModelPrice("acme/model-1", new TokenPrices(0.000001m, 0.000002m, null, null, null), []);
        var bytes = Pack(Content(settings, themes: new Dictionary<string, byte[]> { ["dusk.json"] = theme, ["night/theme.json"] = theme },
            prices: [price], keys: new Dictionary<Guid, string> { [keyed.InstanceId] = "sk-test-123" }));
        var package = Unpack(bytes);
        check("a backup reads back with its settings, profiles, rules and displays",
            package.Settings.Width == 812 && package.Settings.Profiles.Count == 2 && package.Settings.ProfileRules.Single().Displays.Single().Hidden
            && package.Settings.DisplayMonitors.Count == 2 && package.Settings.Entry(keyed.InstanceId)?.QuotaSource == ProviderId.DeepSeek);
        check("a backup reads back with its themes, prices and keys", package.Themes.Count == 2 && package.Themes["night/theme.json"].SequenceEqual(theme)
            && package.Prices.Single().Model == "acme/model-1" && package.Keys[keyed.InstanceId] == "sk-test-123" && package.Manifest.Computer == "STUDY-PC");
        check("a backup without keys has no key file", Unpack(Pack(Content(settings))).Keys.Count == 0
            && !Encoding.UTF8.GetString(Pack(Content(settings))).Contains("keys.json"));

        check("a file that is not a ZIP is not a backup", Problem(Encoding.UTF8.GetBytes("not a backup")) == BackupProblem.NotBackup);
        using (var empty = new MemoryStream())
        {
            using (var zip = new ZipArchive(empty, ZipArchiveMode.Create, true)) zip.CreateEntry("readme.txt");
            check("a ZIP without a manifest is not a backup", Problem(empty.ToArray()) == BackupProblem.NotBackup);
        }
        var damaged = Pack(Content(settings, keys: new Dictionary<Guid, string> { [keyed.InstanceId] = "sk-test-123" }));
        // A byte in the middle of the compressed settings.
        int middle = Encoding.ASCII.GetString(damaged).IndexOf("settings.json", StringComparison.Ordinal) + 200;
        damaged[middle] ^= 0x5A;
        check("a damaged backup is refused", Problem(damaged) == BackupProblem.NotBackup);
        check("a backup from a later version asks for an update first", Problem(Pack(Content(settings, "9.1.0"))) == BackupProblem.Newer);
        try { Unpack(Pack(Content(settings, "10.0.0"))); check("the later version is named", false); }
        catch (BackupException ex) { check("the later version is named", ex.Version == "10.0.0"); }

        using (var old = new MemoryStream())
        {
            using (var zip = new ZipArchive(old, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write("""{"Format":1,"Version":"0.0.9"}""");
                using (var writer = new StreamWriter(zip.CreateEntry("settings.json").Open())) writer.Write("""{"AppVersion":"0.0.9","CompactSummary":false,"Snapshots":{"0.0.8":{}}}""");
            }
            var migrated = Unpack(old.ToArray());
            check("a backup from an earlier version goes through the settings migrations", !migrated.Settings.NotchSummary && !migrated.Settings.CapsuleSummary);
        }

        check("theme paths keep to the themes folder", ConfigBackup.ThemePath("dusk.json") == "dusk.json" && ConfigBackup.ThemePath("night/theme.json") == "night/theme.json"
            && ConfigBackup.ThemePath("../evil.json") is null && ConfigBackup.ThemePath("night/../../evil.json") is null && ConfigBackup.ThemePath("a/b/theme.json") is null
            && ConfigBackup.ThemePath("night/other.json") is null && ConfigBackup.ThemePath("notes.txt") is null && ConfigBackup.ThemePath("C:/x.json") is null);
        using (var odd = new MemoryStream())
        {
            ConfigBackup.Write(odd, Content(settings));
            using (var zip = new ZipArchive(odd, ZipArchiveMode.Update, true))
            {
                zip.CreateEntry("themes/../escape.json");
                using (var big = zip.CreateEntry("themes/huge.json").Open()) big.Write(new byte[ConfigBackup.MaximumThemeBytes + 1]);
                using (var fine = zip.CreateEntry("themes/fine.json").Open()) fine.Write(theme);
            }
            check("only theme files of a usable path and size are read", Unpack(odd.ToArray()).Themes.Keys.SequenceEqual(["fine.json"]));
        }

        var themes = Path.Combine(root, "backup-themes");
        Directory.CreateDirectory(themes);
        File.WriteAllText(Path.Combine(themes, "dusk.json"), "old");
        File.WriteAllText(Path.Combine(themes, "mine.json"), "mine");
        ConfigBackup.WriteThemes(themes, package.Themes);
        check("imported themes replace those of the same name and leave the others", File.ReadAllBytes(Path.Combine(themes, "dusk.json")).SequenceEqual(theme)
            && File.ReadAllText(Path.Combine(themes, "mine.json")) == "mine" && File.Exists(Path.Combine(themes, "night", "theme.json"))
            && ConfigBackup.ReadThemes(themes).Count == 3);

        // This PC: the same DELL under another device path, a display of another model, and not the built-in one.
        var here = new List<BackupDisplay> { new(@"\\?\DISPLAY#GSM#NEW-1", "LG ULTRAFINE"), new(@"\\?\DISPLAY#DEL40B6#NEW-2", "DELL U2720Q") };
        var missing = ConfigBackup.DisplaysToMatch(package.Settings, here.Select(d => d.Id));
        check("the displays of a backup that this PC lacks are listed once each", missing.Select(d => d.Name).SequenceEqual(["DELL U2720Q", "Built-in display"]));
        var named = package.Settings.DisplayMonitors.Select(d => d.Id);
        var guesses = ConfigBackup.GuessDisplays(missing, here, named);
        check("a display of the same model is matched first, and none for a model this PC lacks", guesses.SequenceEqual([@"\\?\DISPLAY#DEL40B6#NEW-2", null]));
        check("a PC with the same displays has none to match", ConfigBackup.DisplaysToMatch(package.Settings, [@"\\?\DISPLAY#DEL40B6#OLD-A", @"\\?\DISPLAY#BOE#OLD-B"]).Count == 0);
        var primaryOnly = package.Settings.Copy(); primaryOnly.Display = DisplayPlacement.Primary; primaryOnly.ProfileRules = [];
        check("chosen displays matter only when the panels show on chosen displays", ConfigBackup.DisplaysToMatch(primaryOnly, []).Count == 0);

        var map = new Dictionary<string, BackupDisplay?> { [@"\\?\DISPLAY#DEL40B6#OLD-A"] = here[1], [@"\\?\DISPLAY#BOE#OLD-B"] = null };
        // This PC: the key of the OpenRouter row is kept (the same row, read from the same source); none of DeepSeek's.
        var current = new DeckSettings { Apps = [unkeyed.Copy()] };
        var prepared = ConfigBackup.Prepare(Unpack(Pack(Content(settings))), current, map, row => row.InstanceId == unkeyed.InstanceId);
        var monitor = prepared.DisplayMonitors.First(d => d.Profile == second);
        check("an imported display moves to the display chosen for it, with its profile", monitor.Id == here[1].Id && monitor.Name == "DELL U2720Q"
            && prepared.ProfileRules.Single().Displays.Single().Id == here[1].Id);
        check("a display given no counterpart keeps its settings", prepared.DisplayMonitors.Any(d => d.Id == @"\\?\DISPLAY#BOE#OLD-B"));
        check("without its key a row no longer counts as having one", prepared.Entry(keyed.InstanceId)!.SecretRevision == Guid.Empty);
        check("a row whose key this PC keeps still has it", prepared.Entry(unkeyed.InstanceId)!.SecretRevision == unkeyed.SecretRevision);
        check("the rows to enter a key for again are listed", ConfigBackup.KeysToEnter(Unpack(Pack(Content(settings))), prepared).Select(a => a.InstanceId).SequenceEqual([keyed.InstanceId]));
        var withKeys = ConfigBackup.Prepare(package, new DeckSettings(), map, _ => false);
        check("a row whose key comes with the file has it", withKeys.Entry(keyed.InstanceId)!.SecretRevision != Guid.Empty && ConfigBackup.KeysToEnter(package, withKeys).Count == 1);
        var held = settings.Copy(); held.ReturnProfile = first; held = Profiles.Switch(held, second); held.ReturnProfile = first;
        var returned = ConfigBackup.Prepare(Unpack(Pack(Content(held))), new DeckSettings(), new Dictionary<string, BackupDisplay?>(), _ => false);
        check("a profile a rule switched to while exporting goes back to the one it replaced", returned.CurrentProfile == first && returned.ReturnProfile is null);
        var twice = new Dictionary<string, BackupDisplay?> { [@"\\?\DISPLAY#DEL40B6#OLD-A"] = here[1], [@"\\?\DISPLAY#BOE#OLD-B"] = here[1] };
        check("two displays matched to the same one keep the first's settings", ConfigBackup.Prepare(package, new DeckSettings(), twice, _ => false)
            .DisplayMonitors.Single().Profile == second);

        check("an export's file name says whether it holds keys", ConfigBackup.FileName(new DateTime(2026, 11, 17), false).EndsWith("2026-11-17.brimbackup")
            && ConfigBackup.FileName(new DateTime(2026, 11, 17), true) != ConfigBackup.FileName(new DateTime(2026, 11, 17), false));
        var backups = Path.Combine(root, "backup-prune");
        Directory.CreateDirectory(backups);
        for (int i = 0; i < 7; i++) File.WriteAllText(Path.Combine(backups, ConfigBackup.BackupName(new DateTime(2026, 11, 1 + i, 9, 0, 0))), "");
        File.WriteAllText(Path.Combine(backups, "mine.brimbackup"), "");
        ConfigBackup.PruneBackups(backups);
        var left = Directory.GetFiles(backups).Select(Path.GetFileName).Order().ToList();
        check("the newest five backups made before imports are kept, and other files stay", left.Count == 6 && left.Contains("mine.brimbackup")
            && left.Contains(ConfigBackup.BackupName(new DateTime(2026, 11, 7, 9, 0, 0))) && !left.Contains(ConfigBackup.BackupName(new DateTime(2026, 11, 2, 9, 0, 0))));

        var pricing = new Pricing(Path.Combine(root, "backup-prices"), new HttpClient(new NoNetwork()));
        int merged = pricing.MergeManual([price, new ModelPrice("", new TokenPrices(-1, 0, null, null, null), [])]);
        check("imported prices are added, and an invalid one is left out", merged == 1 && pricing.IsManual("acme/model-1") && pricing.ManualModels.Count == 1
            && File.Exists(Path.Combine(root, "backup-prices", "manual-model-prices.json")));
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("offline");
    }
}
