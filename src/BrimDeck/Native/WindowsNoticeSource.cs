using System.Runtime.InteropServices;
using BrimDeck.Core.Notices;
using Microsoft.Win32;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace BrimDeck.Native;

public enum NoticeSourceState
{
    Off,
    // Windows has not been asked yet; the user grants access from the settings.
    NeedsAccess,
    // Access is denied (or was revoked) in Windows' privacy settings.
    Denied,
    // Receiving the notification named by each log entry, with periodic full-list reconciliation.
    EventLog,
    // Receiving by reading the notification list on a timer (the log cannot be used).
    Polling,
    Failed
}

// The notifications Windows keeps for the current user (UserNotificationListener). Without package identity Windows does not
// raise its change event for BrimDeck. A push notification log entry names the notification and its application, so the
// loop reads that one notification and hands it over at once; the whole list is read in the background for baselines,
// removals, the safety reading and when the log cannot be used (WindowsNoticeLoop decides which, this class does the Windows
// side). A step that fails waits longer after each failure in a row. The first reading after starting or being granted access
// is a baseline: notices already there are not shown again. The first after waking hands over what arrived meanwhile as
// Missed (WindowsNoticeList). Events are raised on a worker thread.
internal sealed class WindowsNoticeSource : IDisposable
{
    public const string OwnAppId = "BrimDeck.Desktop";
    // A reading Windows does not answer (seen after waking) is given up, and the listener taken again (life-L5).
    private static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(15);
    // The same for one notification a log entry named: a lookup takes milliseconds, so one that has not returned by then has
    // hung like a reading that does not return.
    private static readonly TimeSpan LookupLimit = TimeSpan.FromSeconds(5);
    private readonly WindowsNoticeLoop _loop = new(DateTimeOffset.UtcNow);
    private readonly object _gate;
    private readonly Dictionary<string, Windows.ApplicationModel.AppInfo> _apps = new(StringComparer.OrdinalIgnoreCase);
    // The name and icon a desktop program registered for its notifications, read once per application id.
    private readonly Dictionary<string, (string? Name, string? Icon)> _registered = new(StringComparer.OrdinalIgnoreCase);

    // One Start's loop and what only it touches: the Windows side of a WindowsNoticeLoop run. Stop cancels it and the loop lets
    // go of its listener as it ends, so a loop still finishing a reading never shares its listener or reports a state after
    // Stop, even when Start follows at once.
    private sealed class Run(WindowsNoticeSource source) : IWindowsNoticeHost
    {
        public readonly CancellationTokenSource Stop = new();
        public readonly WindowsNoticeRun Core = new();
        public UserNotificationListener? Listener;
        // The log subscription, taken with the listener and let go with it.
        public WindowsNoticeLog? Log;
        // Wakes this loop only: a loop still ending cannot take the wake meant for the next one.
        public readonly SemaphoreSlim Signal = new(0, 1);
        // The last failure, so that one repeating every step goes to notices.log once, and how many steps in a row failed.
        public string? Failure;
        public int Failures;

        public DateTimeOffset Now => DateTimeOffset.UtcNow;
        public DateTimeOffset LastInteraction => new(Interlocked.Read(ref s_lastInteraction), TimeSpan.Zero);
        public WindowsNoticeAccess Access() => AccessStatus() switch
        {
            UserNotificationListenerAccessStatus.Allowed => WindowsNoticeAccess.Allowed,
            UserNotificationListenerAccessStatus.Unspecified => WindowsNoticeAccess.NeedsAccess,
            _ => WindowsNoticeAccess.Denied
        };
        public void OpenListener() => Listener = UserNotificationListener.Current;
        public void CloseListener() => Listener = null;
        public WindowsNoticeNotificationRead ReadOne(uint id) => source.LookUp(this, id);

        public bool Probe(IEnumerable<uint> ids)
        {
            if (Listener is not { } probe) return false;
            try
            {
                foreach (var id in ids)
                    if (probe.GetNotification(id) is not null) return true;
                return false;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { return true; }
        }

        public Task<WindowsNoticeReadSnapshot> ReadAll(IReadOnlySet<uint> known, CancellationToken token)
        {
            var listener = Listener ?? throw new InvalidOperationException("The notification listener was let go.");
            return Task.Run(() => source.ReadSnapshotAsync(listener, known, token), token);
        }

        public bool LogEnabled() => WindowsNoticeLog.Enabled();
        public bool LogOpen => Log is not null;

        public bool OpenLog()
        {
            CloseLog();
            Log = WindowsNoticeLog.Start(entry => source.LogArrived(this, entry), () => source.LogBroke(this));
            return Log is not null;
        }

        public void CloseLog()
        {
            Log?.Dispose();
            Log = null;
        }

        public void Wake() => WindowsNoticeSource.Wake(this);
        public void Report(WindowsNoticeLoopState state) => source.SetState(state switch
        {
            WindowsNoticeLoopState.EventLog => NoticeSourceState.EventLog,
            WindowsNoticeLoopState.Polling => NoticeSourceState.Polling,
            WindowsNoticeLoopState.NeedsAccess => NoticeSourceState.NeedsAccess,
            _ => NoticeSourceState.Denied
        }, "", this);
        public void Publish(WindowsNoticeList.Changes changes) => source.Publish(changes);
        public void Synced(int covers) => source.Synced?.Invoke(covers);
    }
    private Run? _run;
    // The pointer on an island counts for every source (Interacted), hence static.
    private static long s_lastInteraction;
    // win2 P2: readings Windows has not finished, even ones given up on, with when each started. A new one waits for them, so
    // readings that never return cannot pile up (UserNotificationListener.Current is one object for every run); after one has
    // hung for WindowsNoticePace.StuckAfter another may start, at most WindowsNoticePace.MostOutstanding at a time (L4).
    private readonly List<(Task Task, DateTimeOffset Started)> _reading = [];
    // The same for lookups of one notification, counted apart: a slow full reading must not hold them up.
    private readonly List<(Task Task, DateTimeOffset Started)> _lookups = [];

    public WindowsNoticeSource() => _gate = _loop.Gate;

    public NoticeSourceState State { get; private set; } = NoticeSourceState.Off;
    public string Detail { get; private set; } = "";
    public event Action<NoticeInput>? Received;
    // Arrived while the computer slept (core-M5): to be recorded in the recent notices without being shown as it arrives.
    public event Action<NoticeInput>? Missed;
    public event Action<string>? Removed;
    public event Action? StateChanged;
    // A full reading has been compared with the list and its notices handed over (Received and Removed come first). It
    // carries the number of the last Refresh the reading covers.
    public event Action<int>? Synced;

    public static UserNotificationListenerAccessStatus AccessStatus()
    {
        try { return UserNotificationListener.Current.GetAccessStatus(); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException) { return UserNotificationListenerAccessStatus.Denied; }
    }

    // Asks Windows for access; must run on the UI thread. Windows shows its prompt only the first time; later it answers
    // with the user's earlier choice, which the user changes in Settings > Privacy > Notifications.
    public static async Task<UserNotificationListenerAccessStatus> RequestAccessAsync()
    {
        try { return await UserNotificationListener.Current.RequestAccessAsync(); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException) { return UserNotificationListenerAccessStatus.Denied; }
    }

    public void Start()
    {
        if (_run is not null) return;
        var run = new Run(this);
        lock (_gate) { _run = run; _loop.Begin(run.Core, DateTimeOffset.UtcNow); }
        SystemEvents.PowerModeChanged += PowerModeChanged;
        _ = Task.Run(() => LoopAsync(run));
    }

    public void Stop()
    {
        if (_run is not { } run) return;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        // Cancelled under the lock, so the loop's last state report either comes before Off or not at all.
        lock (_gate) { _run = null; _loop.End(run.Core); run.Stop.Cancel(); }
        SetState(NoticeSourceState.Off, "");
    }

    // Locked or paused: arriving notices are not shown, so the list is read rarely; it is read in full when that ends.
    public void SetQuiet(bool quiet)
    {
        if (_loop.SetQuiet(quiet, DateTimeOffset.UtcNow)) Wake();
    }

    // The pointer came onto an island (MainWindow): someone is at the screen, so the brisk reading starts over (C-L8).
    public static void Interacted() => Interlocked.Exchange(ref s_lastInteraction, DateTimeOffset.UtcNow.UtcTicks);

    // Reads the list again soon, for example after access was granted. Returns the request's number: a Synced with this number
    // or more comes from a reading that started after it.
    public int Refresh(bool baseline = false) => Refresh(baseline ? WindowsNoticeBaseline.Start : WindowsNoticeBaseline.None);

    private int Refresh(WindowsNoticeBaseline baseline)
    {
        int number = _loop.Refresh(baseline, DateTimeOffset.UtcNow);
        Wake();
        return number;
    }

    private void Wake()
    {
        Run? run;
        lock (_gate) run = _run;
        if (run is not null) Wake(run);
    }

    private static void Wake(Run run)
    {
        try { run.Signal.Release(); } catch (SemaphoreFullException) { }
    }

    private void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Notices that arrived during sleep are handed over as missed instead of being shown at once.
        if (e.Mode == PowerModes.Resume) Refresh(WindowsNoticeBaseline.Resume);
    }

    // An entry in the push notification log, on the log's thread: its id is queued, and the first of a batch wakes the loop a
    // moment later, so the batch is taken at one turn (WindowsNoticeLoop.Arrive). A run already stopped is not woken.
    private void LogArrived(Run run, WindowsNoticeLogEvent entry)
    {
        if (!WindowsNoticeLoop.Arrive(run.Core, entry)) return;
        _ = Task.Delay(WindowsNoticeTriggers.Coalesce).ContinueWith(_ =>
        {
            if (_loop.Fired(run.Core)) Wake(run);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    // The log stopped delivering: the loop lets go of the subscription and takes it again (WindowsNoticeLoop.Step), reading in
    // full meanwhile.
    private void LogBroke(Run run)
    {
        if (_loop.LogBroke(run.Core)) Wake(run);
    }

    private async Task LoopAsync(Run run)
    {
        var token = run.Stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // A full reading applied: the step that read it succeeded, so the wait after failures starts over.
                    if (_loop.Step(run.Core, run, token)) { run.Failure = null; run.Failures = 0; }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                // Any failure, a reading or lookup that timed out included, is reported and the next step starts over with a
                // new listener and a full reading, instead of the loop ending while the settings still say it receives (life-L5).
                catch (Exception ex)
                {
                    _loop.Failed(run.Core, run);
                    if (ex is not (COMException or InvalidOperationException or UnauthorizedAccessException or TimeoutException) && run.Failure != ex.Message)
                        NoticeCenter.Log(ex);
                    run.Failure = ex.Message; run.Failures++;
                    SetState(NoticeSourceState.Failed, ex.Message, run);
                }
                // A Refresh (access granted, waking, unlocking) still tries again at once.
                try { await run.Signal.WaitAsync(run.Failures > 0 ? WindowsNoticePace.Retry(run.Failures) : WindowsNoticeLoop.ProbeInterval, token); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally { WindowsNoticeLoop.Detach(run.Core, run); run.Stop.Dispose(); }
    }

    // The notification a log entry named, looked up on a worker so that a lookup Windows does not answer (seen after waking)
    // is given up after LookupLimit and the loop starts over (life-L5). While one given up on is still out no other starts:
    // InvalidOperationException makes the loop fall back to a full reading for that entry instead.
    private WindowsNoticeNotificationRead LookUp(Run run, uint id)
    {
        var listener = run.Listener ?? throw new InvalidOperationException("The notification listener was let go.");
        Task<WindowsNoticeNotificationRead> lookup;
        lock (_lookups)
        {
            var now = DateTimeOffset.UtcNow;
            _lookups.RemoveAll(r => r.Task.IsCompleted || now - r.Started >= WindowsNoticePace.ForgetAfter);
            if (!WindowsNoticePace.MayRead(_lookups.Select(r => r.Started), now)) throw new InvalidOperationException("An earlier notification lookup has not finished.");
            lookup = Task.Run(() => LookUpNow(listener, id));
            _lookups.Add((lookup, now));
        }
        // A lookup given up on may still fail later; that is seen here rather than as an unobserved task exception.
        _ = lookup.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try
        {
            if (!lookup.Wait(LookupLimit, run.Stop.Token)) throw new TimeoutException("Windows did not return the notification in time.");
        }
        catch (AggregateException) { }
        return lookup.GetAwaiter().GetResult();
    }

    private WindowsNoticeNotificationRead LookUpNow(UserNotificationListener listener, uint id)
    {
        var notification = listener.GetNotification(id);
        if (notification is null) return WindowsNoticeNotificationRead.Missing;
        if (notification.Id != id) throw new InvalidOperationException("The event id did not match the returned notification.");
        var input = Read(notification, id);
        // The application Windows will not name (AppInfo is E_NOTIMPL for some senders) leaves no id to check against the log
        // entry: the loop then reads the whole list, which leaves this one out (Read).
        var app = Safe(() => notification.AppInfo);
        var appUserModelId = input?.AppId;
        if (app is not null && string.IsNullOrWhiteSpace(appUserModelId)) appUserModelId = Safe(() => app.AppUserModelId);
        if (app is not null && string.IsNullOrWhiteSpace(appUserModelId)) appUserModelId = Safe(() => app.Id);
        return new WindowsNoticeNotificationRead(true, appUserModelId, input);
    }

    // The whole list, given up after ReadLimit. win2 P2: the WinRT call itself is cancelled then (before, only the wait for it
    // ended and the call stayed outstanding), and while one that ignored its cancellation is still out, no other is started.
    private async Task<IReadOnlyList<UserNotification>> ReadAsync(UserNotificationListener listener, CancellationToken token)
    {
        Task<IReadOnlyList<UserNotification>> read;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_reading)
        {
            var now = DateTimeOffset.UtcNow;
            _reading.RemoveAll(r => r.Task.IsCompleted || now - r.Started >= WindowsNoticePace.ForgetAfter);
            if (!WindowsNoticePace.MayRead(_reading.Select(r => r.Started), now)) throw new TimeoutException("An earlier reading of the notification list has not finished.");
            limit.CancelAfter(ReadLimit);
            read = listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(limit.Token);
            _reading.Add((read, now));
        }
        // A reading given up on may still fail later; that is seen here rather than as an unobserved task exception.
        _ = read.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try { return await read.WaitAsync(ReadLimit + TimeSpan.FromSeconds(1), token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Windows did not return the notification list in time."); }
    }

    // The list with the notices of the ids not seen yet read, on a worker: reading one takes several WinRT calls and the
    // registry (review 8), and a baseline reading after starting reads them all.
    private async Task<WindowsNoticeReadSnapshot> ReadSnapshotAsync(
        UserNotificationListener listener, IReadOnlySet<uint> known, CancellationToken token)
    {
        var notifications = await ReadAsync(listener, token);
        // Each notification on its own: one Windows cannot describe must not fail the reading of all the others.
        return WindowsNoticeSnapshot.Build(notifications, notification => notification.Id, Read, known, token);
    }

    private void Publish(WindowsNoticeList.Changes changes)
    {
        foreach (var input in changes.Arrived) Received?.Invoke(input);
        foreach (var input in changes.Missed) Missed?.Invoke(input);
        foreach (var key in changes.Removed) Removed?.Invoke(key);
    }

    // Null for a notification that is BrimDeck's own, whose application cannot be told, or that cannot be read. Windows answers
    // AppInfo with E_NOTIMPL (NotImplementedException) for some senders, such as a toast sent by an unpackaged program under an
    // unusual application id. Such a notice is left out like one with no application id: without the application its own
    // toasts cannot be told apart (BrimDeck would show its own toast again), its banner cannot be turned off, it cannot be
    // muted per application or opened, so a notice with a made-up application would behave unlike every other. What matters
    // is that it is skipped alone: the rest of the list is read as usual (WindowsNoticeSnapshot).
    private NoticeInput? Read(UserNotification notification, uint id)
    {
        try
        {
            var app = Safe(() => notification.AppInfo);
            if (app is null) return null;
            string appId = Safe(() => app.AppUserModelId) ?? Safe(() => app.Id) ?? "";
            if (appId.Length == 0 || string.Equals(appId, OwnAppId, StringComparison.OrdinalIgnoreCase)) return null;
            lock (_apps) _apps[appId] = app;
            var registered = Registered(appId);
            var visual = notification.Notification?.Visual;
            var binding = visual?.GetBinding(KnownNotificationBindings.ToastGeneric) ?? visual?.Bindings.FirstOrDefault();
            var texts = binding?.GetTextElements().Select(text => text.Text?.Trim() ?? "").Where(text => text.Length > 0).ToList() ?? [];
            var who = WindowsNoticeApps.Identify(new(appId, Safe(() => app.DisplayInfo.DisplayName), registered.Name, texts), BrimDeck.Protocol.NoticeProtocol.MaxField);
            var created = Safe(() => (DateTimeOffset?)notification.CreationTime) ?? DateTimeOffset.UtcNow;
            return new NoticeInput
            {
                Origin = NoticeOrigin.Windows, AppKey = who.AppKey, AppName = who.AppName, AppId = appId,
                InstanceKey = $"windows:{appId}:{id}:{created.UtcTicks}",
                Title = BrimDeck.Protocol.NoticeProtocol.Clip(who.Title, BrimDeck.Protocol.NoticeProtocol.MaxTitle)!,
                Body = BrimDeck.Protocol.NoticeProtocol.Clip(who.Body, BrimDeck.Protocol.NoticeProtocol.MaxBody)!,
                IconRef = registered.Icon ?? "aumid:" + appId, Picture = WindowsToastPicture.Find(id, appId), CreatedAt = created, Category = who.Category, Call = who.Call, Capabilities = NoticeCapabilities.OpenApp
            };
        }
        catch (Exception ex) when (WindowsNoticeSnapshot.Unreadable(ex)) { return null; }
    }

    // A desktop program that sends notifications without a Start menu shortcut registers its name and icon under
    // HKCU\Software\Classes\AppUserModelId (as ToastNotificationManagerCompat does); Windows' display information may have
    // neither for it. Only an icon on a local drive is taken (NoticeIntake.IconRef); NoticeIcons checks it again when reading.
    private (string? Name, string? Icon) Registered(string appId)
    {
        lock (_registered) if (_registered.TryGetValue(appId, out var known)) return known;
        (string? Name, string? Icon) found = (null, null);
        // The id becomes part of a registry path: one that could climb out of the key is not looked up.
        if (appId.Length <= 256 && !appId.Contains("..", StringComparison.Ordinal) && !appId.Contains('/') && !appId.Any(char.IsControl))
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + appId);
                if (key is not null)
                    found = (key.GetValue("DisplayName") as string, NoticeIntake.IconRef(key.GetValue("IconUri") as string, NoticeOrigin.Windows, null));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException or ArgumentException) { }
        }
        lock (_registered) { if (_registered.Count >= 256) _registered.Clear(); _registered[appId] = found; }
        return found;
    }

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotImplementedException) { return default; }
    }

    // The application's logo as Windows provides it for its notifications, as PNG (or other image) bytes. Desktop applications
    // often give none; their icon then comes from the Apps folder, as the Start menu shows it (ShellAppIcon).
    public async Task<byte[]?> LogoAsync(string appId) => await NotificationLogoAsync(appId) ?? await ShellAppIcon.PngAsync(appId);

    private async Task<byte[]?> NotificationLogoAsync(string appId)
    {
        Windows.ApplicationModel.AppInfo? app;
        lock (_apps) _apps.TryGetValue(appId, out app);
        if (app is null) return null;
        try
        {
            if (app.DisplayInfo?.GetLogo(new Windows.Foundation.Size(128, 128)) is not { } logo) return null;
            using var stream = await logo.OpenReadAsync();
            if (stream is null) return null;
            if (stream.Size == 0 || stream.Size > 2_000_000) return null;
            var bytes = new byte[stream.Size];
            using var reader = new Windows.Storage.Streams.DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.IO.IOException) { return null; }
    }

    // Starts or brings forward the application a notice came from. True when Windows accepted the request.
    public async Task<bool> LaunchAsync(string appId)
    {
        Windows.ApplicationModel.AppInfo? app;
        lock (_apps) _apps.TryGetValue(appId, out app);
        string? family = app is null ? null : Safe(() => app.PackageFamilyName);
        if (!string.IsNullOrEmpty(family))
        {
            try
            {
                var manager = new Windows.Management.Deployment.PackageManager();
                foreach (var package in manager.FindPackagesForUser("", family))
                    foreach (var entry in await package.GetAppListEntriesAsync())
                        if (string.Equals(entry.AppUserModelId, appId, StringComparison.OrdinalIgnoreCase)) return await entry.LaunchAsync();
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        }
        // Desktop applications register their id with a Start menu shortcut; the shell starts them by it. The id is passed as
        // one argument and must look like an application id, so it cannot carry switches or a path for the shell to open.
        if (!WindowsNoticeList.IsAppId(appId)) return false;
        try
        {
            // By its full path: a bare name is also looked for in BrimDeck's folder and the current directory.
            var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var start = new System.Diagnostics.ProcessStartInfo(explorer) { UseShellExecute = false };
            start.ArgumentList.Add("shell:AppsFolder\\" + appId);
            using var shell = System.Diagnostics.Process.Start(start);
            return shell is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    // A loop reports only while its run is current: after Stop the state stays Off.
    private void SetState(NoticeSourceState state, string detail, Run? run = null)
    {
        lock (_gate)
        {
            if (run is not null && _run != run) return;
            if (State == state && Detail == detail) return;
            State = state; Detail = detail;
        }
        StateChanged?.Invoke();
    }

    // The runs' semaphores are left alone: a late log entry may still touch one.
    public void Dispose() => Stop();
}
