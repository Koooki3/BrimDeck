using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// Keeps Windows' own banners in line with the "turn off other apps' notification banners" switch (NoticeBanners). The
// applications it knows of are read once per run when the switch first applies (the Start menu and Windows' notification
// settings, on a worker thread) and grow by every application a notice arrives from, so one installed since is caught at the
// next start or at its first notice. Everything else runs on the dispatcher: Update after each settings or source change,
// Arrived for each Windows notice, ReleaseAll on exit, which gives the banners back while BrimDeck is not running.
internal sealed class NoticeBannerKeeper(Func<DeckSettings> settings, Dispatcher dispatcher, string dataFolder, Func<bool> windowsReadable)
{
    private readonly NoticeBannerRegistry _registry = new();
    private readonly string _ledgerPath = NoticeBannerRegistry.LedgerPath(dataFolder);
    private readonly HashSet<string> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private bool _listing, _listed, _released;

    // Each application's state after the last reconciliation, by application id.
    public IReadOnlyDictionary<string, BannerState> States { get; private set; } = new Dictionary<string, BannerState>();
    // The ledger file exists but could not be read: nothing was changed.
    public bool LedgerUnreadable { get; private set; }
    // The switch applies (it is on, and so are notices and the Windows source, and Windows lets BrimDeck read notifications).
    public bool Holding { get; private set; }
    // The application list is still being read.
    public bool Listing => _listing;
    public int Released { get; private set; }
    public event Action? Changed;

    private DeckSettings Settings => settings();

    public void Update()
    {
        if (_released) return;
        var s = Settings;
        Holding = s.NoticeHideBanners && s.NoticesEnabled && s.NoticeWindowsSource && windowsReadable();
        if (Holding && !_listed)
        {
            if (!_listing) List();
            return;
        }
        Reconcile();
    }

    // A Windows notice arrived from this application: one not known yet (installed since the list was read) is handled now.
    public void Arrived(string appId)
    {
        if (_released || !Holding || !_listed || !NoticeBanners.Usable(appId) || !_candidates.Add(appId)) return;
        Reconcile();
    }

    // On exit: every banner BrimDeck turned off goes back to Windows. Released stays, so a banner the user turned on again is
    // still left alone at the next start.
    public void ReleaseAll()
    {
        _released = true; Holding = false;
        if (BannerLedger.Load(_ledgerPath) is not { } ledger || ledger.Records.Count == 0) return;
        NoticeBanners.Reconcile(new HashSet<string>(), Settings.NoticeHideBanners, ledger, _registry, () => ledger.Save(_ledgerPath));
    }

    private void List()
    {
        _listing = true;
        Changed?.Invoke();
        Task.Run(() =>
        {
            var found = NoticeBannerRegistry.StartMenu();
            found.AddRange(NoticeBannerRegistry.Configured());
            return found;
        }).ContinueWith(task => dispatcher.BeginInvoke(() =>
        {
            _listing = false; _listed = true;
            if (task.IsCompletedSuccessfully) _candidates.UnionWith(task.Result);
            Update();
        }), TaskScheduler.Default);
    }

    private void Reconcile()
    {
        if (BannerLedger.Load(_ledgerPath) is not { } ledger)
        {
            LedgerUnreadable = true; States = new Dictionary<string, BannerState>();
            Changed?.Invoke();
            return;
        }
        LedgerUnreadable = false;
        var wanted = NoticeBanners.Wanted(Settings, windowsReadable(), _candidates, [WindowsNoticeSource.OwnAppId], ledger);
        if (wanted.Count == 0 && ledger.Records.Count == 0 && (Holding || ledger.Released.Count == 0))
        {
            States = new Dictionary<string, BannerState>(); Released = ledger.Released.Count;
            Changed?.Invoke();
            return;
        }
        States = NoticeBanners.Reconcile(wanted, Holding, ledger, _registry, () => ledger.Save(_ledgerPath));
        Released = ledger.Released.Count;
        Changed?.Invoke();
    }
}
