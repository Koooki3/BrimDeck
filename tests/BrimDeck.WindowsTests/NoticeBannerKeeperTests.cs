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
        public int Writes, Reads, ReadTimeouts;
        public bool ReadOnDispatcher, WriteOnDispatcher;
        private readonly int _dispatcherThread = Environment.CurrentManagedThreadId;
        // While set and closed, reads wait for it: a pass that takes long (a slow registry, hundreds of apps).
        public ManualResetEventSlim? Gate;
        public readonly ManualResetEventSlim Entered = new();
        public BannerReading Read(string appId)
        {
            if (Environment.CurrentManagedThreadId == _dispatcherThread) ReadOnDispatcher = true;
            Entered.Set();
            if (Gate is { } gate && !gate.Wait(TimeSpan.FromSeconds(5)))
            {
                Interlocked.Increment(ref ReadTimeouts);
                gate.Set(); // A regressed synchronous pass fails once instead of waiting five seconds for each of 300 apps.
            }
            lock (Banners) { Reads++; return Banners.TryGetValue(appId, out var value) ? new(true, value, null) : new(false, null, null); }
        }
        public void Write(string appId, int? showBanner) { MarkWrite(); lock (Banners) { Writes++; Banners[appId] = showBanner; } }
        public void WriteValue(string appId, BannerValue value) => throw new IOException("not used");
        public void DeleteKeyIfEmpty(string appId) { MarkWrite(); lock (Banners) if (Banners.TryGetValue(appId, out var value) && value is null) Banners.Remove(appId); }
        private void MarkWrite() { if (Environment.CurrentManagedThreadId == _dispatcherThread) WriteOnDispatcher = true; }
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
        using var listEntered = new ManualResetEventSlim();
        using var releaseList = new ManualResetEventSlim();
        int listingThread = 0, dispatcherThread = Environment.CurrentManagedThreadId;
        var keeper = new NoticeBannerKeeper(() => settings, Dispatcher.CurrentDispatcher, ledger, () => true, registry,
            () =>
            {
                Interlocked.Increment(ref listed); listingThread = Environment.CurrentManagedThreadId; listEntered.Set();
                if (!releaseList.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Application listing was not released.");
                return [.. apps];
            }, ["BrimDeck.Desktop"], failures.Add, watch: false);
        int completedPasses = 0;
        keeper.Changed += () => { if (!keeper.Listing) completedPasses++; };

        try
        {
            keeper.Update();
            check("banner keeper: update returns while application listing is blocked on a worker",
                listEntered.Wait(TimeSpan.FromSeconds(5)) && keeper.Listing && listingThread != dispatcherThread && !releaseList.IsSet);
        }
        finally { releaseList.Set(); }
        Pump(() => !keeper.Listing && keeper.States.Count == 300);
        check("banner keeper: the listed apps are turned off on a worker, BrimDeck itself is not", listed == 1 && apps.Take(300).All(app => registry.Value(app) == 0)
            && registry.Value("BrimDeck.Desktop") is null && keeper.States.Values.All(state => state == BannerState.TurnedOff) && !registry.WriteOnDispatcher);
        int beforeUpdate = completedPasses;
        keeper.Update();
        Pump(() => completedPasses > beforeUpdate);
        check("banner keeper: later updates neither list again nor read the registry on the dispatcher",
            completedPasses > beforeUpdate && listed == 1 && !registry.ReadOnDispatcher && !registry.WriteOnDispatcher);

        // A pass still under way: another update only marks it to run again, it does not wait for the pass on the dispatcher.
        using (var gate = new ManualResetEventSlim(false))
        {
            registry.Gate = gate; registry.Entered.Reset();
            keeper.Update();
            bool entered = registry.Entered.Wait(TimeSpan.FromSeconds(5));
            keeper.Update();
            keeper.Arrived("Test.Late");
            bool returnedWhileBlocked = entered && !gate.IsSet && registry.ReadTimeouts == 0 && !registry.ReadOnDispatcher && !registry.WriteOnDispatcher;
            gate.Set(); registry.Gate = null;
            Pump(() => registry.Value("Test.Late") == 0);
            check("banner keeper: update and arrival return while a registry pass is blocked", returnedWhileBlocked && registry.Value("Test.Late") == 0);
        }

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
