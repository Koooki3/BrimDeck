using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

// The keeper of "turn off other apps' notification banners" on a dispatcher, as the app runs it: the list read once, a new
// application handled when its notice arrives, a banner turned on again left alone, everything given back on exit, and a
// ledger that cannot be read leaving Windows as it is. The registry is a dictionary here; the real one is in
// NoticeBannerRegistryTests.
internal static class NoticeBannerKeeperTests
{
    private sealed class Registry : IBannerRegistry
    {
        public readonly Dictionary<string, int?> Banners = new(StringComparer.OrdinalIgnoreCase);
        public int Writes, Reads;
        public BannerReading Read(string appId) { lock (Banners) { Reads++; return Banners.TryGetValue(appId, out var value) ? new(true, value, null) : new(false, null, null); } }
        public void Write(string appId, int? showBanner) { lock (Banners) { Writes++; Banners[appId] = showBanner; } }
        public void WriteValue(string appId, BannerValue value) => throw new IOException("not used");
        public void DeleteKeyIfEmpty(string appId) { lock (Banners) if (Banners.TryGetValue(appId, out var value) && value is null) Banners.Remove(appId); }
        public int? Value(string appId) { lock (Banners) return Banners.TryGetValue(appId, out var value) ? value : null; }
    }

    public static void Run(string root, Action<string, bool> check)
    {
        Directory.CreateDirectory(root);
        string ledger = Path.Combine(root, BannerLedger.FileName);
        var settings = new DeckSettings { NoticesEnabled = true, NoticeWindowsSource = true, NoticeHideBanners = true };
        var registry = new Registry();
        int listed = 0;
        var apps = Enumerable.Range(0, 300).Select(i => "Test.App" + i).Append("BrimDeck.Desktop").ToList();
        var failures = new List<Exception>();
        var keeper = new NoticeBannerKeeper(() => settings, Dispatcher.CurrentDispatcher, ledger, () => true, registry,
            () => { Interlocked.Increment(ref listed); return [.. apps]; }, ["BrimDeck.Desktop"], failures.Add, watch: false);

        var clock = Stopwatch.StartNew();
        keeper.Update();
        long first = clock.ElapsedMilliseconds;
        check("banner keeper: the first update only starts the list, leaving the dispatcher at once", keeper.Listing && first < 50);
        Pump(() => !keeper.Listing && keeper.States.Count == 300);
        check("banner keeper: the listed apps are turned off on a worker, BrimDeck itself is not", listed == 1 && apps.Take(300).All(app => registry.Value(app) == 0)
            && registry.Value("BrimDeck.Desktop") is null && keeper.States.Values.All(state => state == BannerState.TurnedOff));
        clock.Restart();
        keeper.Update();
        long again = clock.ElapsedMilliseconds;
        Pump(() => Settled(keeper));
        check("banner keeper: later updates neither list again nor hold the dispatcher", listed == 1 && again < 50);

        int writes = registry.Writes;
        keeper.Arrived("Test.App5");
        keeper.Arrived("Test.New");
        Pump(() => registry.Value("Test.New") == 0);
        check("banner keeper: an app first seen in a notice is turned off, a known one is not touched again", registry.Writes == writes + 1);

        registry.Write("Test.App7", 1);
        keeper.Update();
        Pump(() => keeper.Released == 1);
        keeper.Update();
        Pump(() => Settled(keeper));
        check("banner keeper: a banner turned on again is left alone", registry.Value("Test.App7") == 1 && keeper.States.GetValueOrDefault("Test.App7") != BannerState.TurnedOff);

        keeper.ReleaseAll();
        check("banner keeper: quitting gives every banner back and remembers the one turned on again",
            apps.Take(300).Where(app => app != "Test.App7").All(app => registry.Value(app) is null) && registry.Value("Test.New") is null
            && BannerLedger.Load(ledger) is { Records.Count: 0 } kept && kept.IsReleased("Test.App7"));

        // A ledger that cannot be read: nothing is changed, and the keeper says so.
        File.WriteAllText(ledger, "{ damaged");
        File.WriteAllText(ledger + BannerLedger.BackupSuffix, "damaged too");
        var untouched = new Registry();
        var blind = new NoticeBannerKeeper(() => settings, Dispatcher.CurrentDispatcher, ledger, () => true, untouched, () => [.. apps], [], failures.Add, watch: false);
        blind.Update();
        Pump(() => !blind.Listing && blind.LedgerUnreadable);
        check("banner keeper: an unreadable ledger changes nothing in Windows", blind.LedgerUnreadable && untouched.Writes == 0);
        check("banner keeper: no pass failed", failures.Count == 0);

        // Watching Windows' notification settings: a change there (here a made-up application's key) asks for a pass by itself.
        File.Delete(ledger); File.Delete(ledger + BannerLedger.BackupSuffix);
        var watched = new Registry();
        var watcher = new NoticeBannerKeeper(() => settings, Dispatcher.CurrentDispatcher, ledger, () => true, watched, () => ["Test.Watched"], [], failures.Add);
        string probe = NoticeBanners.SettingsKey + "\\BrimDeck.Tests.Watch." + Guid.NewGuid().ToString("N");
        try
        {
            watcher.Update();
            Pump(() => !watcher.Listing && watched.Value("Test.Watched") == 0);
            int reads;
            lock (watched.Banners) reads = watched.Reads;
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(probe)) key.SetValue("Probe", 1);
            Pump(() => { lock (watched.Banners) return watched.Reads > reads; });
            bool noticed;
            lock (watched.Banners) noticed = watched.Reads > reads;
            check("banner keeper: a change in Windows' notification settings is noticed by itself", noticed);
        }
        finally
        {
            watcher.ReleaseAll(); watcher.Dispose();
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(probe, throwOnMissingSubKey: false);
        }
    }

    private static bool Settled(NoticeBannerKeeper keeper) => !keeper.Listing;

    // Runs the dispatcher until the condition holds (or a few seconds pass), so that the keeper's passes come back to it.
    private static void Pump(Func<bool> done)
    {
        var limit = Stopwatch.StartNew();
        while (!done() && limit.Elapsed < TimeSpan.FromSeconds(10))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        // Let a pass that was asked for meanwhile finish too.
        for (int i = 0; i < 20; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }
}
