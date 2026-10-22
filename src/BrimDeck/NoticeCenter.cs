using System.IO;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;
using BrimDeck.Protocol;

namespace BrimDeck;

// Dynamic notices for the whole application: it runs the sources the settings switch on (the Windows notification list,
// brim-notify's pipe, the WSL agents), keeps the recent notices (NoticeHub, saved by NoticeHistory), and decides with the
// AttentionCoordinator which panel briefly shows what. Quota alerts and track changes go through the same coordinator, so
// the three never cover each other. Everything here runs on the UI thread; the sources hand over to it.
internal sealed class NoticeCenter : IDisposable
{
    public const string TestAppKey = "brimdeck:test";
    private readonly App _app;
    private readonly Dispatcher _dispatcher;
    private readonly PromptTimer _attentionTimer;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _maintenance = new() { Interval = TimeSpan.FromMinutes(10) };
    // While notices are held back, checks every 15 seconds whether presentation mode or the pause has ended (neither raises an event).
    private readonly DispatcherTimer _quietCheck = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly WindowsNoticeSource _windows = new();
    private readonly WslNoticeBridge _wsl = new();
    private readonly NoticeQuiet _quiet;
    private NoticePipeServer? _pipe;
    private NoticeHistory? _history;
    private bool _historyLoaded, _saving, _savePending, _enabled, _disposed;
    private DateTimeOffset? _attentionAt;
    // Messages each link (the pipe, each distribution) may deliver per window; more are refused until the window moves on.
    private const int IntakeLimit = 200;
    private static readonly TimeSpan IntakeWindow = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _intake = new(StringComparer.Ordinal);

    public NoticeHub Hub { get; }
    public AttentionCoordinator Attention { get; } = new();
    public NoticeIcons Icons { get; }
    public NoticeHistoryState HistoryState { get; private set; } = NoticeHistoryState.Empty;
    public NoticeSourceState WindowsState => _enabled && Settings.NoticeWindowsSource ? _windows.State : NoticeSourceState.Off;
    public string? PipeError => _pipe?.Error;
    public IReadOnlyDictionary<string, (WslAgentState State, string Detail)> WslStates => _wsl.States;
    public bool Enabled => _enabled;
    public bool Quiet { get; private set; }
    public bool FocusSupported => _quiet.FocusSupported;
    // Raised after the recent notices or a source's state changed; the panels' notices pages and the settings page follow it.
    public event Action? Changed;
    internal DeckSettings Settings => _app.Settings;

    public NoticeCenter(App app)
    {
        _app = app; _dispatcher = app.Dispatcher;
        Hub = new NoticeHub(key => _app.Settings.NoticeRule(key));
        Icons = new NoticeIcons(_dispatcher, _windows.LogoAsync, distro => _wsl.States.TryGetValue(distro, out var state) && state.State is WslAgentState.Server or WslAgentState.Monitor or WslAgentState.MonitorDenied);
        _quiet = new NoticeQuiet(_dispatcher);
        _quiet.Changed += UpdateQuiet;
        _attentionTimer = new PromptTimer(_dispatcher, () => { _attentionTimer!.Stop(); _attentionAt = null; Apply(Attention.Tick(DateTimeOffset.UtcNow)); });
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _ = SaveAsync(); };
        _maintenance.Tick += (_, _) => { Hub.Prune(DateTimeOffset.UtcNow); UpdateQuiet(); };
        _quietCheck.Tick += (_, _) => UpdateQuiet();
        Hub.Changed += () => Changed?.Invoke();
        Hub.Persist += QueueSave;
        Icons.Loaded += _ => Changed?.Invoke();
        _windows.Received += input => _dispatcher.BeginInvoke(() => Receive(input));
        _windows.Removed += key => _dispatcher.BeginInvoke(() => Hub.SourceRemoved(key));
        _windows.StateChanged += () => _dispatcher.BeginInvoke(() => Changed?.Invoke());
        _wsl.Received += (distro, message) => _dispatcher.BeginInvoke(() => FromAgent(distro, message));
        _wsl.StateChanged += () => _dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    // Starts and stops sources to match the settings. Called at start-up and after every settings change.
    public void ApplySettings()
    {
        if (_disposed) return;
        var settings = Settings;
        _enabled = settings.NoticesEnabled;
        Hub.Retention = TimeSpan.FromHours(settings.NoticeHistoryHours);
        Hub.MaxCount = NoticeHub.DefaultMaxCount;
        UseHistoryFolder(settings);
        if (!_enabled)
        {
            _windows.Stop(); _wsl.Apply([]);
            _pipe?.Dispose(); _pipe = null;
            _maintenance.Stop(); _quietCheck.Stop();
            // Whatever shows now ends; quota alerts and track changes keep working through the coordinator.
            if (Attention.Suppressed) Apply(Attention.SetSuppressed(false, DateTimeOffset.UtcNow, out _));
            foreach (var deck in _app.Decks.ToList())
                if (Attention.Current(deck.NoticeLane) is { IsNotice: true }) Apply(Attention.Dismiss(deck.NoticeLane, DateTimeOffset.UtcNow));
            Quiet = false;
            Changed?.Invoke();
            return;
        }
        LoadHistory();
        if (_pipe is null) { _pipe = new NoticePipeServer(HandlePipeAsync) { Failed = Log }; _pipe.Start(); }
        if (settings.NoticeWindowsSource) _windows.Start(); else _windows.Stop();
        _wsl.Apply(settings.NoticeWslDistros);
        _maintenance.Start();
        Hub.Prune(DateTimeOffset.UtcNow);
        UpdateQuiet();
        Changed?.Invoke();
    }

    // Asks Windows for access to its notifications (from the settings page) and reads the list once access is granted.
    public async Task<Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus> RequestWindowsAccessAsync()
    {
        var status = await WindowsNoticeSource.RequestAccessAsync();
        _windows.Refresh(baseline: true);
        Changed?.Invoke();
        return status;
    }

    // ----- Intake -----

    public NoticeOutcome Receive(NoticeInput input)
    {
        var now = DateTimeOffset.UtcNow;
        var outcome = Hub.Receive(input, now);
        if (outcome.Notice is not { } notice) return outcome;
        if (outcome.Present) Present(notice, refresh: outcome.Receipt == NoticeReceipt.Updated);
        else if (outcome.Receipt == NoticeReceipt.Updated)
        {
            // A task's progress moves the notice that shows; nothing new appears.
            foreach (var deck in _app.Decks)
                if (Attention.Current(deck.NoticeLane) is { Kind: AttentionKind.Notice, Payload: Notice shown } && shown.Id == notice.Id)
                    Apply(Attention.Submit(Item(deck.NoticeLane, notice, refresh: true), now));
        }
        return outcome;
    }

    private AttentionItem Item(string lane, Notice notice, bool refresh = false) => new()
    {
        Lane = lane, Kind = AttentionKind.Notice, Key = notice.Id, Group = notice.AppKey, Priority = notice.Priority, Payload = notice, Refresh = refresh,
        Duration = TimeSpan.FromSeconds(notice.Priority == NoticePriority.High ? Settings.NoticeHighDuration : Settings.NoticeDuration)
    };

    private void Present(Notice notice, bool refresh)
    {
        var now = DateTimeOffset.UtcNow;
        UpdateQuiet();
        if (Attention.Suppressed) { Attention.Submit(Item("", notice), now); return; }
        if (Lane() is not { } lane) return;
        Apply(Attention.Submit(Item(lane, notice, refresh), now));
    }

    // The panel that shows an arriving notice (NoticeRouting), or null when that panel cannot show one now.
    private string? Lane()
    {
        long? foreground = NoticeNative.ForegroundMonitor() is var monitor && monitor != IntPtr.Zero ? monitor.ToInt64() : null;
        var panels = _app.Decks.Select(deck => new NoticeRouting.Candidate(deck.NoticeLane, deck.NoticeMonitor, deck == _app.Deck, deck.CanShowNotices)).ToList();
        return NoticeRouting.Choose(panels, foreground, Settings.NoticeScreen);
    }

    private async Task<NoticeMessage> HandlePipeAsync(NoticeMessage message)
    {
        if (NoticeProtocol.Validate(message) is { } problem) return NoticeProtocol.Reply(NoticeResults.Rejected, problem, message.EventId);
        return await _dispatcher.InvokeAsync(() =>
        {
            if (!_enabled) return NoticeProtocol.Reply(NoticeResults.Rejected, "notices-off", message.EventId);
            if (message.Type == NoticeTypes.Status) return NoticeProtocol.Reply(NoticeResults.Accepted, StatusLine(), message.EventId);
            if (message.Type != NoticeTypes.Notify) return NoticeProtocol.Reply(NoticeResults.Rejected, "unknown-type", message.EventId);
            if (!Admit("pipe")) return NoticeProtocol.Reply(NoticeResults.Rejected, "rate-limited", message.EventId);
            return Reply(Receive(NoticeIntake.FromPipe(message)), message.EventId);
        });
    }

    private static NoticeMessage Reply(NoticeOutcome outcome, string? eventId) => NoticeProtocol.Reply(outcome.Receipt switch
    {
        NoticeReceipt.Accepted => NoticeResults.Accepted, NoticeReceipt.Updated => NoticeResults.Updated,
        NoticeReceipt.Duplicate => NoticeResults.Duplicate, _ => NoticeResults.Filtered
    }, outcome.Reason, eventId);

    private string StatusLine() => $"Windows: {WindowsState}; WSL: {string.Join(", ", WslStates.Select(pair => pair.Key + " " + pair.Value.State))}" + (Quiet ? "; held back" : "");

    private void FromAgent(string distro, NoticeMessage message)
    {
        if (!_enabled || NoticeProtocol.Validate(message) is not null) return;
        bool server = _wsl.States.TryGetValue(distro, out var state) && state.State == WslAgentState.Server;
        switch (message.Type)
        {
            case NoticeTypes.Notify:
            {
                if (!Admit("wsl:" + distro))
                {
                    if (message.Origin == "cli" && message.EventId is { } refused) _wsl.Send(distro, NoticeProtocol.Reply(NoticeResults.Rejected, "rate-limited", refused));
                    break;
                }
                var outcome = Receive(NoticeIntake.FromAgent(message, distro, server));
                // brim-notify inside the distribution waits for this reply through the agent.
                if (message.Origin == "cli" && message.EventId is { } id) _wsl.Send(distro, Reply(outcome, id));
                break;
            }
            case NoticeTypes.Close when message.NativeId is { } native:
                Hub.SourceRemoved($"wsl:{distro}:{message.Session ?? ""}:{native}");
                break;
        }
    }

    private bool Admit(string link)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_intake.TryGetValue(link, out var times)) _intake[link] = times = new Queue<DateTimeOffset>();
        while (times.Count > 0 && now - times.Peek() >= IntakeWindow) times.Dequeue();
        if (times.Count >= IntakeLimit) return false;
        times.Enqueue(now);
        return true;
    }

    // ----- Presentation -----

    private void Apply(IReadOnlyList<AttentionChange> changes)
    {
        foreach (var change in changes)
        {
            var deck = _app.Decks.FirstOrDefault(d => d.NoticeLane == change.Lane);
            if (deck is null) continue;
            if (change.Ended is { } ended) deck.AttentionEnded(ended);
            if (change.Started is { } started)
            {
                deck.AttentionStarted(started);
                if (started.Payload is Notice notice) Hub.MarkViewed(notice.Id);
            }
            if (change.Updated is { } updated) deck.AttentionUpdated(updated);
        }
        Schedule();
    }

    private void Schedule()
    {
        var now = DateTimeOffset.UtcNow;
        var next = Attention.NextDeadline(now);
        if (next == _attentionAt && _attentionTimer.IsEnabled) return;
        _attentionTimer.Stop(); _attentionAt = next;
        if (next is not { } at) return;
        var wait = at - now;
        _attentionTimer.Interval = wait > TimeSpan.FromMilliseconds(1) ? wait : TimeSpan.FromMilliseconds(1);
        _attentionTimer.Start();
    }

    // A quota alert from a panel. It shows when nothing more important does; while the panel is open it waits.
    public void SubmitQuota(MainWindow deck, object alert, string key)
        => Apply(Attention.Submit(new AttentionItem { Lane = deck.NoticeLane, Kind = AttentionKind.Quota, Key = key, Payload = alert, Duration = TimeSpan.FromSeconds(5) }, DateTimeOffset.UtcNow));
    // A track change shows only if nothing else does right now. True when it shows.
    public bool TryTrack(MainWindow deck, TimeSpan duration)
    {
        var changes = Attention.Submit(new AttentionItem { Lane = deck.NoticeLane, Kind = AttentionKind.Track, Key = "track", Duration = duration }, DateTimeOffset.UtcNow);
        Apply(changes);
        return Attention.Current(deck.NoticeLane) is { Kind: AttentionKind.Track };
    }
    public void Withdraw(string key) => Apply(Attention.Withdraw(key, DateTimeOffset.UtcNow));

    // The pointer is over the shown item, its details are open, or the keyboard works it: its time stands still.
    public void Hold(MainWindow deck, bool held) => Apply(Attention.Hold(deck.NoticeLane, held, DateTimeOffset.UtcNow));
    public void DeckExpanded(MainWindow deck, bool expanded) => Apply(Attention.SetBusy(deck.NoticeLane, expanded, DateTimeOffset.UtcNow));
    public void DeckRetired(MainWindow deck) { Attention.RemoveLane(deck.NoticeLane); Schedule(); }
    // A panel stopped being able to show anything (hidden by the user or a full-screen rule): what it shows ends.
    public void DeckHidden(MainWindow deck)
    {
        if (Attention.Current(deck.NoticeLane) is not null) Apply(Attention.Dismiss(deck.NoticeLane, DateTimeOffset.UtcNow));
    }

    // Closes what the panel shows (close button, Escape). Only BrimDeck's own presentation closes; Windows' notification
    // center keeps the notice. A WSL notice whose agent is the notification service is reported as dismissed.
    public void Dismiss(MainWindow deck, bool reportToSource)
    {
        var current = Attention.Current(deck.NoticeLane);
        Apply(Attention.Dismiss(deck.NoticeLane, DateTimeOffset.UtcNow));
        if (reportToSource && current?.Payload is Notice notice) ReportDismissed(notice);
    }

    private void ReportDismissed(Notice notice)
    {
        if ((notice.Capabilities & NoticeCapabilities.ReportDismissal) == 0 || notice.Distro is not { } distro) return;
        if (NativeId(notice) is { } id) _wsl.Send(distro, new NoticeMessage { Type = NoticeTypes.Dismissed, NativeId = id });
    }

    private static string? NativeId(Notice notice) => notice.InstanceKey.Split(':') is [.., var last] && uint.TryParse(last, out _) ? last : null;

    public bool CanOpen(Notice notice) => notice.Origin == NoticeOrigin.Windows && (notice.Capabilities & NoticeCapabilities.OpenApp) != 0
        || notice.Actions.Any(action => action.Key == "default") && (notice.Capabilities & NoticeCapabilities.Actions) != 0;

    // Opens the application the notice came from. False when that did not work; the caller keeps the details open and says so.
    public async Task<bool> OpenAsync(Notice notice)
    {
        Hub.MarkViewed(notice.Id);
        if (notice.Origin == NoticeOrigin.Windows) return notice.AppId.Length > 0 && await _windows.LaunchAsync(notice.AppId);
        if (notice.Actions.Any(action => action.Key == "default")) return Invoke(notice, "default");
        return false;
    }

    // Passes a chosen action back to the WSL application (only offered while the agent is its notification service).
    public bool Invoke(Notice notice, string key)
    {
        if ((notice.Capabilities & NoticeCapabilities.Actions) == 0 || notice.Distro is not { } distro || NativeId(notice) is not { } id) return false;
        _wsl.Send(distro, new NoticeMessage { Type = NoticeTypes.Action, NativeId = id, ActionKey = key });
        Hub.SourceRemoved(notice.InstanceKey);
        Withdraw(notice.Id);
        return true;
    }

    public void Delete(Notice notice) { ReportDismissed(notice); Withdraw(notice.Id); Hub.Delete(notice.Id); }
    // Clears BrimDeck's recent notices; Windows' notification center is left as it is.
    public void Clear()
    {
        foreach (var notice in Hub.Notices.ToList()) ReportDismissed(notice);
        foreach (var deck in _app.Decks.ToList())
            if (Attention.Current(deck.NoticeLane) is { IsNotice: true }) Apply(Attention.Dismiss(deck.NoticeLane, DateTimeOffset.UtcNow));
        Hub.Clear();
    }

    // A notice of BrimDeck's own, to try the look and the placement. It is never sent to Windows, so it cannot come back
    // through the notification list.
    public void SendTest()
    {
        Receive(new NoticeInput
        {
            Origin = NoticeOrigin.Test, AppKey = TestAppKey, AppName = "BrimDeck", InstanceKey = "test:" + Guid.NewGuid().ToString("N"),
            Title = Loc.T("灵动提示测试", "Dynamic notice test"),
            Body = Loc.T("通知会以当前主题在这里出现。悬停查看详情，点击“查看全部”打开最近通知。", "Notices appear here in the current theme. Hover for details; \"View all\" opens the recent notices."),
            Category = NoticeCategory.Info
        });
    }

    // ----- Quiet and pause -----

    public bool Paused => Settings.NoticesPausedUntil is { } until && until > DateTimeOffset.UtcNow;

    public void Pause(TimeSpan? duration)
    {
        _app.Settings.NoticesPausedUntil = duration is { } span ? DateTimeOffset.UtcNow + span : null;
        _app.QueueSave();
        UpdateQuiet();
        Changed?.Invoke();
    }

    // Recomputes whether notices are held back. Ending a quiet spell shows one summary of what arrived meanwhile.
    public void UpdateQuiet()
    {
        if (!_enabled) return;
        if (Settings.NoticesPausedUntil is { } until && until <= DateTimeOffset.UtcNow) { _app.Settings.NoticesPausedUntil = null; _app.QueueSave(); }
        bool quiet = _quiet.Locked || Paused || Settings.NoticeRespectFocus && (_quiet.FocusActive || NoticeQuiet.PresentationOrQuietTime());
        _windows.SetQuiet(_quiet.Locked || Paused);
        if (quiet) { if (!_quietCheck.IsEnabled) _quietCheck.Start(); } else _quietCheck.Stop();
        if (quiet == Quiet) return;
        Quiet = quiet;
        var now = DateTimeOffset.UtcNow;
        Apply(Attention.SetSuppressed(quiet, now, out int arrived));
        if (!quiet && arrived > 0 && Lane() is { } lane)
            Apply(Attention.Submit(new AttentionItem { Lane = lane, Kind = AttentionKind.Summary, Key = "summary:" + now.Ticks, Payload = arrived, Duration = TimeSpan.FromSeconds(Settings.NoticeDuration) }, now));
        Changed?.Invoke();
    }

    // ----- Diagnostics -----

    // Failures go to notices.log in the data folder: the exception's type, code and stack, never a message, a title or a body.
    // The log keeps about the last 256 KB.
    public static void Log(Exception error)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "notices.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {ApplicationVersion.Current} {error.GetType().FullName} 0x{error.HResult:X8}\n{error.StackTrace}\n\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ----- History -----

    private string HistoryFolder(DeckSettings settings) => settings.NoticeHistoryFolder.Length > 0 ? settings.NoticeHistoryFolder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "notices");

    private void UseHistoryFolder(DeckSettings settings)
    {
        string folder = HistoryFolder(settings);
        Icons.Folder = folder;
        if (_history?.DirectoryPath == folder)
        {
            if (!settings.NoticeHistory) _history.Delete();
            return;
        }
        // A new folder takes the notices from now on; the file in the old one is removed so no copy stays behind.
        _history?.Delete();
        _history = new NoticeHistory(folder, new NoticeProtection());
        if (_historyLoaded && settings.NoticeHistory) QueueSave();
    }

    private void LoadHistory()
    {
        if (_historyLoaded || _history is null) return;
        _historyLoaded = true;
        if (!Settings.NoticeHistory) { _history.Delete(); return; }
        HistoryState = _history.Load(out var notices, out var apps);
        if (HistoryState == NoticeHistoryState.Loaded) Hub.Load(notices, apps, DateTimeOffset.UtcNow);
    }

    // Replaces an unreadable file with the current (empty or new) list.
    public void ResetHistory()
    {
        _history?.Delete();
        HistoryState = NoticeHistoryState.Empty;
        QueueSave();
        Changed?.Invoke();
    }

    private void QueueSave()
    {
        if (!Settings.NoticeHistory || _history is null || HistoryState == NoticeHistoryState.Unreadable) return;
        if (!_saveTimer.IsEnabled) _saveTimer.Start();
    }

    // The list is copied here, on the UI thread; encrypting and writing happen in the background, one save at a time.
    private async Task SaveAsync()
    {
        if (_history is not { } history || !Settings.NoticeHistory) return;
        if (_saving) { _savePending = true; return; }
        _saving = true;
        var notices = Hub.Notices.Select(n => n.Copy()).ToList();
        var apps = Hub.KnownApps.ToList();
        try { await Task.Run(() => history.Save(notices, apps)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
        finally { _saving = false; }
        if (_savePending) { _savePending = false; QueueSave(); }
    }

    // On exit: what is not saved yet is written now.
    public void Flush()
    {
        if (!_saveTimer.IsEnabled && !_savePending) return;
        _saveTimer.Stop(); _savePending = false;
        if (_history is not { } history || !Settings.NoticeHistory || HistoryState == NoticeHistoryState.Unreadable) return;
        try { history.Save(Hub.Notices, Hub.KnownApps); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Flush();
        _attentionTimer.Stop(); _saveTimer.Stop(); _maintenance.Stop(); _quietCheck.Stop();
        _windows.Dispose(); _wsl.Dispose(); _pipe?.Dispose(); _quiet.Dispose();
    }
}
