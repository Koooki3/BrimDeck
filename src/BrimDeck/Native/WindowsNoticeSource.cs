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
    // Receiving through Windows' change events (BrimDeck runs with package identity).
    Events,
    // Receiving by reading the notification list on a timer (no package identity).
    Polling,
    Failed
}

// The notifications Windows keeps for the current user (UserNotificationListener). Without package identity Windows does not
// raise its change event for BrimDeck, so the list is read on a timer: every ProbeInterval only the id after the newest one
// is looked up (some ms of CPU, a fraction of reading the whole list), and the whole list is read every FullInterval and
// whenever the probe finds a new notice. Tiles and badges take ids from the same count, so after a notice is seen to have
// skipped that id the next few are looked up for a while (WindowsNoticePace.ProbeIds). The probe needs the newest id Windows has given out: notifications removed before
// BrimDeck started can leave that ahead of the newest one still listed, so until a full reading has seen a notice arrive, the
// list is read every 3 s instead, and less often (WindowsNoticeList.UnknownWait) only once nothing has happened for a few
// minutes (WindowsNoticePace). A step that fails waits longer after each failure in a row. Access is checked before every
// full reading, the only one that can conclude that a notice was removed. The first reading after starting or being granted
// access is a baseline: notices already there are not shown again. The first after waking hands over what arrived meanwhile
// as Missed (WindowsNoticeList). Events are raised on a worker thread.
internal sealed class WindowsNoticeSource : IDisposable
{
    public const string OwnAppId = "BrimDeck.Desktop";
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2.5), FullInterval = TimeSpan.FromSeconds(15),
        EventSafetyInterval = TimeSpan.FromSeconds(60), QuietInterval = TimeSpan.FromSeconds(30);
    // A reading Windows does not answer (seen after waking) is given up, and the listener taken again (life-L5).
    private static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(15);
    private readonly object _gate = new();
    private readonly WindowsNoticeList _list = new(FullInterval);
    private readonly Dictionary<string, Windows.ApplicationModel.AppInfo> _apps = new(StringComparer.OrdinalIgnoreCase);
    // The name and icon a desktop program registered for its notifications, read once per application id.
    private readonly Dictionary<string, (string? Name, string? Icon)> _registered = new(StringComparer.OrdinalIgnoreCase);
    // One Start's loop and what only it touches. Stop cancels it and the loop lets go of its listener as it ends, so a loop
    // still finishing a reading never shares its listener or reports a state after Stop, even when Start follows at once.
    private sealed class Run
    {
        public readonly CancellationTokenSource Stop = new();
        public UserNotificationListener? Listener;
        public bool Events;
        public DateTimeOffset LastFull = DateTimeOffset.MinValue;
        // Until when the probe looks a few ids ahead (WindowsNoticePace.ProbeIds).
        public DateTimeOffset WideUntil = DateTimeOffset.MinValue;
        // Wakes this loop only: a loop still ending cannot take the wake meant for the next one.
        public readonly SemaphoreSlim Wake = new(0, 1);
        // The last failure, so that one repeating every step goes to notices.log once, and how many steps in a row failed.
        public string? Failure;
        public int Failures;
    }
    private Run? _run;
    private bool _dirty, _quiet;
    // When something last happened that makes a notice likely soon (WindowsNoticePace.Unconfirmed). The pointer on an island
    // counts for every source (Interacted), hence static.
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private static long s_lastInteraction;
    // win2 P2: readings Windows has not finished, even ones given up on, with when each started. A new one waits for them, so
    // readings that never return cannot pile up (UserNotificationListener.Current is one object for every run); after one has
    // hung for WindowsNoticePace.StuckAfter another may start, at most WindowsNoticePace.MostOutstanding at a time (L4).
    private readonly List<(Task Task, DateTimeOffset Started)> _reading = [];

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
    private int _refreshes;

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
        var run = new Run();
        lock (_gate) { _run = run; _list.Reset(); _lastActivity = DateTimeOffset.UtcNow; }
        SystemEvents.PowerModeChanged += PowerModeChanged;
        _ = Task.Run(() => LoopAsync(run));
    }

    public void Stop()
    {
        if (_run is not { } run) return;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        // Cancelled under the lock, so the loop's last state report either comes before Off or not at all.
        lock (_gate) { _run = null; run.Stop.Cancel(); }
        SetState(NoticeSourceState.Off, "");
    }

    // Locked or paused: arriving notices are not shown, so the list is read rarely; it is read in full when that ends.
    public void SetQuiet(bool quiet)
    {
        lock (_gate) { if (_quiet == quiet) return; _quiet = quiet; if (!quiet) { _dirty = true; _lastActivity = DateTimeOffset.UtcNow; } }
        Wake();
    }

    // The pointer came onto an island (MainWindow): someone is at the screen, so the brisk reading starts over (C-L8).
    public static void Interacted() => Interlocked.Exchange(ref s_lastInteraction, DateTimeOffset.UtcNow.UtcTicks);

    // Reads the list again soon, for example after access was granted. Returns the request's number: a Synced with this number
    // or more comes from a reading that started after it.
    public int Refresh(bool baseline = false) => Refresh(baseline ? WindowsNoticeBaseline.Start : WindowsNoticeBaseline.None);

    private int Refresh(WindowsNoticeBaseline baseline)
    {
        int number;
        lock (_gate) { _dirty = true; _list.ExpectBaseline(baseline); number = ++_refreshes; _lastActivity = DateTimeOffset.UtcNow; }
        Wake();
        return number;
    }

    private void Wake()
    {
        Run? run;
        lock (_gate) run = _run;
        try { run?.Wake.Release(); } catch (SemaphoreFullException) { }
    }

    private void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Notices that arrived during sleep are handed over as missed instead of being shown at once.
        if (e.Mode == PowerModes.Resume) Refresh(WindowsNoticeBaseline.Resume);
    }

    private void Changed(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        lock (_gate) _dirty = true;
        Wake();
    }

    private async Task LoopAsync(Run run)
    {
        var token = run.Stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try { await StepAsync(run); run.Failure = null; run.Failures = 0; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                // Any failure, a reading that timed out included, is reported and the next step starts over with a new
                // listener and a full reading, instead of the loop ending while the settings still say it receives (life-L5).
                catch (Exception ex)
                {
                    lock (_gate) _dirty = true;
                    Detach(run);
                    if (ex is not (COMException or InvalidOperationException or UnauthorizedAccessException or TimeoutException) && run.Failure != ex.Message)
                        NoticeCenter.Log(ex);
                    run.Failure = ex.Message; run.Failures++;
                    SetState(NoticeSourceState.Failed, ex.Message, run);
                }
                // A Refresh (access granted, waking, unlocking) still tries again at once.
                try { await run.Wake.WaitAsync(run.Failures > 0 ? WindowsNoticePace.Retry(run.Failures) : ProbeInterval, token); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally { Detach(run); run.Stop.Dispose(); }
    }

    private void Detach(Run run)
    {
        if (run.Listener is { } listener && run.Events)
            try { listener.NotificationChanged -= Changed; } catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        run.Listener = null; run.Events = false;
    }

    private async Task StepAsync(Run run)
    {
        var token = run.Stop.Token;
        var now = DateTimeOffset.UtcNow;
        bool full, quiet;
        uint newest;
        TimeSpan unknownWait;
        bool confirmed;
        DateTimeOffset active;
        lock (_gate)
        {
            full = _dirty || _list.Baseline != WindowsNoticeBaseline.None; quiet = _quiet;
            newest = _list.Newest; confirmed = _list.Confirmed; unknownWait = _list.UnknownWait;
            var touched = new DateTimeOffset(Interlocked.Read(ref s_lastInteraction), TimeSpan.Zero);
            active = touched > _lastActivity ? touched : _lastActivity;
        }
        var interval = quiet ? QuietInterval : run.Events ? EventSafetyInterval : !confirmed ? WindowsNoticePace.Unconfirmed(now, active, unknownWait) : FullInterval;
        if (!full && now - run.LastFull >= interval) full = true;
        // The next id is taken by the next notice for this user, so finding it means something arrived. Without access the
        // lookup finds nothing, and the next full reading reports the change of access.
        if (!full && run.Listener is { } probe && !run.Events && !quiet && newest > 0)
        {
            try
            {
                foreach (var id in WindowsNoticePace.ProbeIds(newest, now, run.WideUntil))
                    if (probe.GetNotification(id) is not null) { full = true; break; }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { full = true; }
        }
        if (!full && run.Listener is not null) return;
        // A revoked permission makes the list read as empty; that must not look like every notice being removed.
        var access = AccessStatus();
        token.ThrowIfCancellationRequested();
        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            lock (_gate) _list.Reset();
            Detach(run);
            SetState(access == UserNotificationListenerAccessStatus.Unspecified ? NoticeSourceState.NeedsAccess : NoticeSourceState.Denied, "", run);
            return;
        }
        if (run.Listener is null)
        {
            run.Listener = UserNotificationListener.Current;
            // Only a process with package identity gets the change event; without it subscribing fails (0x80070490).
            try { run.Listener.NotificationChanged += Changed; run.Events = true; }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException) { run.Events = false; }
        }
        SetState(run.Events ? NoticeSourceState.Events : NoticeSourceState.Polling, "", run);
        int covers;
        lock (_gate) { _dirty = false; covers = _refreshes; }
        run.LastFull = now;
        Sync(await ReadAsync(run.Listener, token), run, covers);
    }

    // The whole list, given up after ReadLimit. win2 P2: the WinRT call itself is cancelled then (before, only the wait for it
    // ended and the call stayed outstanding), and while one that ignored its cancellation is still out, no other is started.
    private async Task<IReadOnlyList<UserNotification>> ReadAsync(UserNotificationListener listener, CancellationToken token)
    {
        Task<IReadOnlyList<UserNotification>> read;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_gate)
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

    private void Sync(IReadOnlyList<UserNotification> list, Run run, int covers)
    {
        var listed = new List<uint>(list.Count);
        var byId = new Dictionary<uint, UserNotification>(list.Count);
        foreach (var notification in list)
        {
            uint id;
            try { id = notification.Id; } catch (COMException) { continue; }
            listed.Add(id); byId.TryAdd(id, notification);
        }
        // New notifications are read outside the lock (review 8): reading one takes several WinRT calls and the registry, and the
        // UI thread takes the lock for every notice it shows (SetQuiet). A baseline reading after starting reads them all.
        List<uint> fresh;
        lock (_gate) fresh = _list.Unknown(listed);
        var read = new Dictionary<uint, NoticeInput?>(fresh.Count);
        foreach (var id in fresh) read[id] = Read(byId[id], id);
        WindowsNoticeList.Changes changes;
        lock (_gate)
        {
            // Stopped (and perhaps started again) while reading: this reading belongs to no run any more.
            if (_run != run) return;
            changes = _list.Compare(listed, id => read.TryGetValue(id, out var input) ? input : Read(byId[id], id));
            if (changes.Arrived.Count + changes.Missed.Count > 0) _lastActivity = DateTimeOffset.UtcNow;
            if (_list.SkippedProbe) run.WideUntil = DateTimeOffset.UtcNow + WindowsNoticePace.WideFor;
        }
        foreach (var input in changes.Arrived) Received?.Invoke(input);
        foreach (var input in changes.Missed) Missed?.Invoke(input);
        foreach (var key in changes.Removed) Removed?.Invoke(key);
        Synced?.Invoke(covers);
    }

    private NoticeInput? Read(UserNotification notification, uint id)
    {
        try
        {
            var app = notification.AppInfo;
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
                IconRef = registered.Icon ?? "aumid:" + appId, CreatedAt = created, Category = who.Category, Call = who.Call, Capabilities = NoticeCapabilities.OpenApp
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or NullReferenceException) { return null; }
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

    // The application's logo as Windows provides it for its notifications, as PNG (or other image) bytes.
    public async Task<byte[]?> LogoAsync(string appId)
    {
        Windows.ApplicationModel.AppInfo? app;
        lock (_apps) _apps.TryGetValue(appId, out app);
        if (app is null) return null;
        try
        {
            // Desktop applications may have no display information or no logo at all.
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

    // The runs' semaphores are left alone: a late change event may still touch one.
    public void Dispose() => Stop();
}
