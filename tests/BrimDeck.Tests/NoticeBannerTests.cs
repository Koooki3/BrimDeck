using BrimDeck.Core;
using BrimDeck.Core.Notices;

// "Turn off other apps' notification banners" (NoticeBanners): which applications are wanted, what is recorded before Windows
// is changed, that exactly what was there comes back, and that a banner the user turns on again is left alone. The registry is
// a dictionary here; the real one is checked in the Windows tests, and Windows' own reading of the value by hand.
internal static class NoticeBannerTests
{
    private sealed class FakeRegistry : IBannerRegistry
    {
        // Keys that exist, and their DWORD values by name; a ShowBanner of another kind in Others.
        public readonly Dictionary<string, Dictionary<string, int>> Keys = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, BannerValue> Others = new(StringComparer.OrdinalIgnoreCase);
        public bool FailWrites, Ignore;
        public int Writes;

        public BannerReading Read(string appId) => Keys.TryGetValue(appId, out var values)
            ? new(true, values.TryGetValue("ShowBanner", out var banner) ? banner : null, values.TryGetValue("Enabled", out var enabled) ? enabled : null,
                Others.TryGetValue(appId, out var other) ? other : null)
            : new(false, null, null);

        public void Write(string appId, int? showBanner)
        {
            if (FailWrites) throw new UnauthorizedAccessException();
            Writes++;
            // A Windows that no longer keeps the value (a policy, a later version): the write goes nowhere.
            if (Ignore) return;
            Others.Remove(appId);
            if (showBanner is { } value) { if (!Keys.TryGetValue(appId, out var values)) Keys[appId] = values = []; values["ShowBanner"] = value; }
            else if (Keys.TryGetValue(appId, out var values)) values.Remove("ShowBanner");
        }

        public void WriteValue(string appId, BannerValue value)
        {
            if (FailWrites) throw new UnauthorizedAccessException();
            Writes++;
            if (!Keys.TryGetValue(appId, out var values)) Keys[appId] = values = [];
            values.Remove("ShowBanner"); Others[appId] = value;
        }

        public void DeleteKeyIfEmpty(string appId) { if (Keys.TryGetValue(appId, out var values) && values.Count == 0 && !Others.ContainsKey(appId)) Keys.Remove(appId); }
    }

    internal static void Run(string root, Action<string, bool> check)
    {
        Ids(check);
        Wanted(check);
        Reconcile(check);
        Released(check);
        Pending(check);
        OtherKinds(check);
        Ledger(root, check);
        SettingsRoundTrip(root, check);
    }

    private static void Ids(Action<string, bool> check)
    {
        check("Banners: packaged and registered ids are usable", NoticeBanners.Usable("Microsoft.VisualStudioCode") && NoticeBanners.Usable("Claude_pzs8sxrjxfjjc!Claude"));
        check("Banners: a desktop program's known-folder id is kept as nested keys", NoticeBanners.Usable(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe"));
        check("Banners: Start menu file paths and links are not notification ids", !NoticeBanners.Usable(@"C:\Git\cmd\git-gui.exe") && !NoticeBanners.Usable("https://example.com/")
            && !NoticeBanners.Usable(@"\\server\share\tool.exe") && !NoticeBanners.Usable("a/b!c"));
        check("Banners: an Edge web app's id, with its site after the \"!\", is usable", NoticeBanners.Usable("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!https://web.whatsapp.com/"));
        check("Banners: tray balloons are never touched", !NoticeBanners.Usable("NotifyIconGeneratedAumid_123"));
        check("Banners: ids that could leave the key or are empty are refused", !NoticeBanners.Usable(@"..\..\Run") && !NoticeBanners.Usable(@"a\\b")
            && !NoticeBanners.Usable(@"\a") && !NoticeBanners.Usable("a\nb") && !NoticeBanners.Usable("") && !NoticeBanners.Usable(new string('a', 257)));
    }

    private static DeckSettings Settings(params NoticeAppRule[] rules) => new() { NoticesEnabled = true, NoticeWindowsSource = true, NoticeHideBanners = true, NoticeApps = [.. rules] };

    private static void Wanted(Action<string, bool> check)
    {
        var settings = Settings(new NoticeAppRule { Key = "windows:Muted", Muted = true }, new NoticeAppRule { Key = "windows:Off", Enabled = false },
            new NoticeAppRule { Key = "windows:Ruled" }, new NoticeAppRule { Key = "windows:Chrome|x.com", Muted = true });
        string[] candidates = ["A", "Muted", "Off", "Ruled", "Chrome", "BrimDeck.Desktop", @"C:\tool.exe", "Released"];
        var ledger = new BannerLedger { Released = ["released"] };
        HashSet<string> Wanted(bool readable = true) => NoticeBanners.Wanted(settings, readable, candidates, ["BrimDeck.Desktop"], ledger);
        check("Banners: every other app is wanted, a browser with one muted site included", Wanted().SetEquals(["A", "Ruled", "Chrome"]));
        check("Banners: BrimDeck itself, apps off or muted in BrimDeck, paths and released apps are not", !Wanted().Overlaps(["BrimDeck.Desktop", "Muted", "Off", @"C:\tool.exe", "Released"]));
        check("Banners: application ids match without regard to case", Wanted().Contains("ruled") && Wanted().Contains("CHROME"));
        settings.NoticeHideBanners = false;
        check("Banners: nothing is wanted with the switch off", Wanted().Count == 0);
        settings.NoticeHideBanners = true; settings.NoticesEnabled = false;
        check("Banners: nothing is wanted with notices off", Wanted().Count == 0);
        settings.NoticesEnabled = true; settings.NoticeWindowsSource = false;
        check("Banners: nothing is wanted with the Windows source off", Wanted().Count == 0);
        settings.NoticeWindowsSource = true;
        check("Banners: nothing is wanted while Windows refuses access", Wanted(readable: false).Count == 0);
        check("Banners: the switch is off by default", !new DeckSettings().NoticeHideBanners);
    }

    private static Dictionary<string, BannerState> Apply(HashSet<string> wanted, BannerLedger ledger, FakeRegistry registry, Func<bool>? save = null, bool? holding = null)
        => NoticeBanners.Reconcile(wanted, holding ?? wanted.Count > 0, ledger, registry, save ?? (() => true));

    private static void Reconcile(Action<string, bool> check)
    {
        // Three previous states: no key at all, a key without the value, a key with ShowBanner = 1.
        var registry = new FakeRegistry();
        registry.Keys["Absent"] = new() { ["LastNotificationAddedTime"] = 1 };
        registry.Keys["One"] = new() { ["ShowBanner"] = 1 };
        var ledger = new BannerLedger();
        var all = new HashSet<string>(["NoKey", "Absent", "One"], StringComparer.OrdinalIgnoreCase);
        var states = Apply(all, ledger, registry);
        check("Banners: turning off sets ShowBanner to 0", states.Values.All(state => state == BannerState.TurnedOff) && all.All(id => registry.Read(id).ShowBanner == 0));
        check("Banners: the previous state is recorded exactly", ledger.Find("NoKey") is { Previous: null, CreatedKey: true }
            && ledger.Find("Absent") is { Previous: null, CreatedKey: false } && ledger.Find("One") is { Previous: 1, CreatedKey: false });
        int writes = registry.Writes, saves = 0;
        Apply(all, ledger, registry, () => { saves++; return true; });
        check("Banners: reconciling again changes and saves nothing", registry.Writes == writes && saves == 0 && ledger.Records.Count == 3);
        states = Apply([], ledger, registry);
        check("Banners: restoring brings back no key, no value and 1", !registry.Keys.ContainsKey("NoKey")
            && registry.Keys["Absent"].Count == 1 && !registry.Keys["Absent"].ContainsKey("ShowBanner") && registry.Keys["One"]["ShowBanner"] == 1
            && ledger.Records.Count == 0 && states.Values.All(state => state == BannerState.None));

        // An app set to off or muted in BrimDeck while the switch is on gets its banner back; the others stay off.
        var some = new FakeRegistry(); var someLedger = new BannerLedger();
        Apply(["Keep", "Mute"], someLedger, some);
        Apply(["Keep"], someLedger, some);
        check("Banners: an app no longer wanted is restored, the rest stay off", !some.Keys.ContainsKey("Mute") && some.Keys["Keep"]["ShowBanner"] == 0 && someLedger.Records.Count == 1);

        // A key BrimDeck created that Windows has written into since stays.
        var used = new FakeRegistry(); var usedLedger = new BannerLedger();
        Apply(["New"], usedLedger, used);
        used.Keys["New"]["LastNotificationAddedTime"] = 5;
        Apply([], usedLedger, used);
        check("Banners: a created key Windows has used since is kept", used.Keys.TryGetValue("New", out var kept) && kept.Count == 1 && !kept.ContainsKey("ShowBanner"));

        // An app whose notifications are off in Windows is never touched, and one turned off later gets its banner setting back.
        var disabled = new FakeRegistry(); disabled.Keys["Off"] = new() { ["Enabled"] = 0 };
        var disabledLedger = new BannerLedger();
        states = Apply(["Off"], disabledLedger, disabled);
        check("Banners: an app with notifications off in Windows is left alone", states["Off"] == BannerState.NotificationsOff && disabled.Writes == 0
            && disabledLedger.Records.Count == 0 && !disabled.Keys["Off"].ContainsKey("ShowBanner"));
        var later = new FakeRegistry(); var laterLedger = new BannerLedger();
        Apply(["App"], laterLedger, later);
        later.Keys["App"]["Enabled"] = 0;
        states = Apply(["App"], laterLedger, later);
        check("Banners: switching the app's notifications off in Windows restores the banner setting", states["App"] == BannerState.NotificationsOff
            && laterLedger.Records.Count == 0 && !later.Keys["App"].ContainsKey("ShowBanner"));

        // A banner already off in Windows is not recorded, so restoring never turns it on.
        var already = new FakeRegistry(); already.Keys["Quiet"] = new() { ["ShowBanner"] = 0 };
        var alreadyLedger = new BannerLedger();
        states = Apply(["Quiet"], alreadyLedger, already);
        Apply([], alreadyLedger, already);
        check("Banners: a banner the user had turned off stays off", states["Quiet"] == BannerState.AlreadyOff && alreadyLedger.Records.Count == 0
            && already.Keys["Quiet"]["ShowBanner"] == 0 && already.Writes == 0);

        // Restoring leaves a value changed since as it is.
        var other = new FakeRegistry(); var otherLedger = new BannerLedger();
        other.Keys["Mine"] = new() { ["ShowBanner"] = 1 };
        Apply(["Mine"], otherLedger, other);
        other.Keys["Mine"].Remove("ShowBanner");
        Apply([], otherLedger, other);
        check("Banners: restoring leaves a value changed since as it is", !other.Keys["Mine"].ContainsKey("ShowBanner") && otherLedger.Records.Count == 0);

        // The ledger is written before Windows is changed; failures leave both consistent.
        var noSave = new FakeRegistry(); var noSaveLedger = new BannerLedger();
        states = Apply(["App"], noSaveLedger, noSave, () => false);
        check("Banners: nothing changes in Windows when the ledger cannot be written", states["App"] == BannerState.Failed && noSave.Writes == 0
            && noSaveLedger.Records.Count == 0 && !noSave.Keys.ContainsKey("App"));
        var order = new FakeRegistry(); var orderLedger = new BannerLedger();
        bool? recordedFirst = null;
        Apply(["A", "B"], orderLedger, order, () => { recordedFirst ??= order.Writes == 0 && orderLedger.Records.Count == 2; return true; });
        check("Banners: every record is saved before any ShowBanner is written", recordedFirst == true && order.Keys["A"]["ShowBanner"] == 0 && order.Keys["B"]["ShowBanner"] == 0);
        int batch = 0;
        Apply(new HashSet<string>(Enumerable.Range(0, 200).Select(i => "App" + i)), new BannerLedger(), new FakeRegistry(), () => { batch++; return true; });
        check("Banners: many apps are recorded with one save, and their pending marks cleared with one more", batch == 2);
        var denied = new FakeRegistry { FailWrites = true }; var deniedLedger = new BannerLedger();
        states = Apply(["App"], deniedLedger, denied);
        check("Banners: a refused registry write leaves no record behind", states["App"] == BannerState.Failed && deniedLedger.Records.Count == 0);
        var ignored = new FakeRegistry { Ignore = true }; var ignoredLedger = new BannerLedger();
        states = Apply(["App"], ignoredLedger, ignored);
        check("Banners: a value Windows does not keep is reported as failed, not as off", states["App"] == BannerState.Failed && ignoredLedger.Records.Count == 0);
        var stuck = new FakeRegistry(); var stuckLedger = new BannerLedger();
        Apply(["App"], stuckLedger, stuck);
        stuck.FailWrites = true;
        states = Apply([], stuckLedger, stuck);
        check("Banners: a restore that fails keeps its record to try again", states["App"] == BannerState.Failed && stuckLedger.Records.Count == 1);
        stuck.FailWrites = false;
        Apply([], stuckLedger, stuck);
        check("Banners: the next attempt restores it", stuckLedger.Records.Count == 0 && !stuck.Keys.ContainsKey("App"));
    }

    // Turned on again outside BrimDeck while the switch is on: released, kept that way, and forgotten once the switch goes off.
    private static void Released(Action<string, bool> check)
    {
        var registry = new FakeRegistry(); var ledger = new BannerLedger();
        var settings = Settings();
        HashSet<string> Wanted() => NoticeBanners.Wanted(settings, true, ["Rewrites", "Other"], [], ledger);
        Apply(Wanted(), ledger, registry, holding: true);
        registry.Keys["Rewrites"]["ShowBanner"] = 1;
        var states = Apply(Wanted(), ledger, registry, holding: true);
        check("Banners: a banner turned on again in Windows is released", states["Rewrites"] == BannerState.Released && ledger.IsReleased("Rewrites")
            && ledger.Find("Rewrites") is null && registry.Keys["Rewrites"]["ShowBanner"] == 1);
        int writes = registry.Writes;
        Apply(Wanted(), ledger, registry, holding: true);
        check("Banners: a released app is not turned off again while the switch stays on", registry.Writes == writes && registry.Keys["Rewrites"]["ShowBanner"] == 1
            && !Wanted().Contains("Rewrites") && registry.Keys["Other"]["ShowBanner"] == 0);
        // Quitting gives the others back but remembers the choice; switching off forgets it.
        Apply([], ledger, registry, holding: true);
        check("Banners: quitting restores the rest and keeps the released list", ledger.Records.Count == 0 && ledger.IsReleased("Rewrites") && !registry.Keys.ContainsKey("Other"));
        settings.NoticeHideBanners = false;
        Apply(Wanted(), ledger, registry, holding: false);
        check("Banners: switching off forgets released apps", ledger.Released.Count == 0 && registry.Keys["Rewrites"]["ShowBanner"] == 1);

        // Turned on again in Windows and then BrimDeck quit before it looked again: still the user's at the next start.
        var quit = new FakeRegistry(); var quitLedger = new BannerLedger();
        settings.NoticeHideBanners = true;
        HashSet<string> Again() => NoticeBanners.Wanted(settings, true, ["Mine", "Other"], [], quitLedger);
        Apply(Again(), quitLedger, quit, holding: true);
        quit.Keys["Mine"]["ShowBanner"] = 1;
        Apply([], quitLedger, quit, holding: true);
        check("Banners: a banner turned on again before quitting is remembered", quitLedger.IsReleased("Mine") && !quitLedger.IsReleased("Other")
            && quit.Keys["Mine"]["ShowBanner"] == 1 && !quit.Keys.ContainsKey("Other"));
        Apply(Again(), quitLedger, quit, holding: true);
        check("Banners: at the next start it is left alone and the others are turned off again", quit.Keys["Mine"]["ShowBanner"] == 1 && quit.Keys["Other"]["ShowBanner"] == 0);
        // Switched off, a restoration that finds the user's own value remembers nothing.
        var off = new FakeRegistry(); var offLedger = new BannerLedger();
        Apply(["App"], offLedger, off, holding: true);
        off.Keys["App"]["ShowBanner"] = 1;
        Apply([], offLedger, off, holding: false);
        check("Banners: switching off after the user's change keeps it and remembers nothing", offLedger.Released.Count == 0 && off.Keys["App"]["ShowBanner"] == 1);
    }

    // A record saved as pending, and BrimDeck stopped before its 0 was written: the next run writes it (or, no longer wanted,
    // drops the record), and never takes the unchanged value for the user's.
    private static void Pending(Action<string, bool> check)
    {
        var registry = new FakeRegistry(); registry.Keys["App"] = new() { ["ShowBanner"] = 1 }; registry.Keys["Gone"] = new() { ["ShowBanner"] = 1 };
        var ledger = new BannerLedger { Records = [new() { AppId = "App", Previous = 1, Pending = true }, new() { AppId = "Gone", Previous = 1, Pending = true }] };
        var states = Apply(["App"], ledger, registry, holding: true);
        check("Banners: a pending record whose 0 was never written is finished at the next start", states["App"] == BannerState.TurnedOff
            && registry.Keys["App"]["ShowBanner"] == 0 && ledger.Find("App") is { Pending: false } && !ledger.IsReleased("App"));
        check("Banners: a pending record no longer wanted just goes, leaving the value as it was", ledger.Find("Gone") is null && registry.Keys["Gone"]["ShowBanner"] == 1
            && !ledger.IsReleased("Gone"));
        var done = new FakeRegistry(); done.Keys["App"] = new() { ["ShowBanner"] = 0 };
        var doneLedger = new BannerLedger { Records = [new() { AppId = "App", Previous = null, CreatedKey = false, Pending = true }] };
        Apply(["App"], doneLedger, done, holding: true);
        check("Banners: a pending record whose 0 was written is simply kept", doneLedger.Find("App") is { Pending: false } && done.Keys["App"]["ShowBanner"] == 0);
        var user = new FakeRegistry(); user.Keys["App"] = new() { ["ShowBanner"] = 1 };
        var userLedger = new BannerLedger { Records = [new() { AppId = "App", Previous = null, Pending = true }] };
        Apply(["App"], userLedger, user, holding: true);
        check("Banners: a pending record whose value changed otherwise is the user's", userLedger.IsReleased("App") && user.Keys["App"]["ShowBanner"] == 1);
        // Saved twice: once with every new record pending before anything is written, once when they are in.
        var order = new FakeRegistry(); var orderLedger = new BannerLedger(); var pendingAtSave = new List<bool>();
        Apply(["A"], orderLedger, order, () => { pendingAtSave.Add(orderLedger.Find("A")?.Pending == true); return true; }, holding: true);
        check("Banners: new records are saved pending, then saved again once written", pendingAtSave.SequenceEqual([true, false]));
        // The switch, not whether it applies now, decides about a banner turned on again (Windows source off meanwhile).
        var paused = new FakeRegistry(); var pausedLedger = new BannerLedger();
        Apply(["App"], pausedLedger, paused, holding: true);
        paused.Keys["App"]["ShowBanner"] = 1;
        NoticeBanners.Reconcile(new HashSet<string>(), true, pausedLedger, paused, () => true);
        check("Banners: with the switch on but not applying, a banner turned on again is still remembered", pausedLedger.IsReleased("App"));
        NoticeBanners.Reconcile(new HashSet<string>(), true, pausedLedger, paused, () => true);
        check("Banners: and the list is kept until the switch goes off", pausedLedger.IsReleased("App"));
    }

    // A ShowBanner of another kind than a DWORD is recorded and put back as it was.
    private static void OtherKinds(Action<string, bool> check)
    {
        var registry = new FakeRegistry(); var text = new BannerValue("String", "1");
        registry.Keys["App"] = []; registry.Others["App"] = text;
        var ledger = new BannerLedger();
        Apply(["App"], ledger, registry, holding: true);
        check("Banners: a value of another kind is recorded with its kind", ledger.Find("App") is { Previous: null, PreviousValue: { Kind: "String", Data: "1" } }
            && registry.Keys["App"]["ShowBanner"] == 0 && !registry.Others.ContainsKey("App"));
        Apply([], ledger, registry, holding: false);
        check("Banners: and put back as it was", registry.Others.TryGetValue("App", out var back) && back == text && !registry.Keys["App"].ContainsKey("ShowBanner")
            && ledger.Records.Count == 0);
        // A DWORD other than 0 or 1 (another tool's) is recorded, survives a reload of the ledger and comes back as it was.
        var odd = new FakeRegistry(); odd.Keys["Odd"] = new() { ["ShowBanner"] = 2 };
        var path = Path.Combine(Path.GetTempPath(), "brimdeck-banners-odd-" + Guid.NewGuid().ToString("N") + ".json");
        var oddLedger = new BannerLedger();
        NoticeBanners.Reconcile(new HashSet<string> { "Odd" }, true, oddLedger, odd, () => oddLedger.Save(path));
        var reloaded = BannerLedger.Load(path);
        check("Banners: a DWORD of 2 is recorded and read back", odd.Keys["Odd"]["ShowBanner"] == 0 && reloaded?.Find("Odd") is { Previous: 2 });
        if (reloaded is not null) NoticeBanners.Reconcile(new HashSet<string>(), false, reloaded, odd, () => reloaded.Save(path));
        check("Banners: and comes back as 2", odd.Keys["Odd"]["ShowBanner"] == 2 && !File.Exists(path));
        File.Delete(path + BannerLedger.BackupSuffix);
    }

    private static void Ledger(string root, Action<string, bool> check)
    {
        string folder = Path.Combine(root, "banners");
        string path = Path.Combine(folder, BannerLedger.FileName);
        check("Banners: no ledger file reads as empty", BannerLedger.Load(path) is { Records.Count: 0, Released.Count: 0 });
        var ledger = new BannerLedger { Records = [new() { AppId = "A", Previous = null, CreatedKey = true }, new() { AppId = "B", Previous = 1 }], Released = ["C"] };
        check("Banners: the ledger saves", ledger.Save(path) && File.Exists(path));
        var read = BannerLedger.Load(path);
        check("Banners: the ledger reads back exactly", read is not null && read.Find("a") is { Previous: null, CreatedKey: true } && read.Find("B") is { Previous: 1, CreatedKey: false }
            && read.IsReleased("c"));
        ledger.Records.Clear();
        check("Banners: a ledger with only released apps is kept", ledger.Save(path) && File.Exists(path));
        ledger.Released.Clear();
        check("Banners: an empty ledger leaves no file", ledger.Save(path) && !File.Exists(path));
        File.WriteAllText(path, "{ not json");
        check("Banners: an unreadable ledger is not taken as empty", BannerLedger.Load(path) is null);
        File.WriteAllText(path, """{"Records":[{"AppId":"..\\Run","Previous":1},{"AppId":"Zero","Previous":0},{"AppId":"Good","Previous":1},{"AppId":"good","Previous":null}],"Released":["C:\\x.exe","Kept"]}""");
        check("Banners: records that could not have been written are dropped", BannerLedger.Load(path) is { } cleaned && cleaned.Records.Count == 1 && cleaned.Records[0].AppId == "Good"
            && cleaned.Released.SequenceEqual(["Kept"]));
        File.WriteAllText(path, """{"Records":[{"AppId":"Old","Previous":null,"CreatedKey":true}]}""");
        check("Banners: a ledger written before the released list reads with none", BannerLedger.Load(path) is { Records.Count: 1, Released.Count: 0 });
        File.Delete(path);
        // Each save keeps the one before as a backup, read when the file itself is damaged.
        var first = new BannerLedger { Records = [new() { AppId = "First", Previous = 1 }] };
        var second = new BannerLedger { Records = [new() { AppId = "First", Previous = 1 }, new() { AppId = "Second", Previous = null }] };
        check("Banners: saving twice keeps the earlier ledger as the backup", first.Save(path) && second.Save(path) && File.Exists(path + BannerLedger.BackupSuffix)
            && BannerLedger.Load(path) is { Records.Count: 2 });
        File.WriteAllText(path, "{ cut off");
        check("Banners: a damaged ledger falls back to its backup", BannerLedger.Load(path) is { } fallback && fallback.Find("First") is not null && fallback.Find("Second") is null);
        File.WriteAllText(path + BannerLedger.BackupSuffix, "also damaged");
        check("Banners: with both damaged nothing is assumed", BannerLedger.Load(path) is null);
        File.WriteAllText(path + BannerLedger.BackupSuffix, """{"Records":[{"AppId":"First","Previous":1}]}""");
        File.Delete(path);
        check("Banners: a backup alone is still read", BannerLedger.Load(path) is { } alone && alone.Find("First") is not null);
        new BannerLedger().Save(path);
        check("Banners: an empty ledger removes its backup too", !File.Exists(path) && !File.Exists(path + BannerLedger.BackupSuffix));
    }

    private static void SettingsRoundTrip(string root, Action<string, bool> check)
    {
        var store = new SettingsStore(Path.Combine(root, "banner-settings"));
        store.Save(new DeckSettings { NoticesEnabled = true, NoticeHideBanners = true });
        var loaded = store.Load();
        check("Banners: the switch survives saving and loading", loaded.NoticeHideBanners && loaded.Copy().NoticeHideBanners);
        // A settings file saved before the switch existed reads with it off.
        File.WriteAllText(store.FilePath, $$"""{"AppVersion":"{{ApplicationVersion.Parse(ApplicationVersion.Current).ToString(3)}}","NoticesEnabled":true}""");
        check("Banners: an older settings file reads with the switch off", !store.Load().NoticeHideBanners);
    }
}
