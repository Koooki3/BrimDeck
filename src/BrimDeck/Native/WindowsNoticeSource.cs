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
// is looked up (about 2.5 ms of CPU, a fifth of reading the whole list), and the whole list is read every FullInterval and
// whenever the probe finds a new notice. The probe needs the newest id Windows has given out: notifications removed before
// BrimDeck started can leave that ahead of the newest one still listed, so until a full reading has seen a notice arrive, the
// list is read every UnknownInterval instead. Access is checked before every full reading, the only one that can conclude that a
// notice was removed. The first reading after starting, waking or being granted access is a baseline: notices already there
// are not shown again.
// Events are raised on a worker thread.
internal sealed class WindowsNoticeSource : IDisposable
{
    public const string OwnAppId = "BrimDeck.Desktop";
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2.5), FullInterval = TimeSpan.FromSeconds(15),
        UnknownInterval = TimeSpan.FromSeconds(3), EventSafetyInterval = TimeSpan.FromSeconds(60), QuietInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Dictionary<uint, string> _known = [];
    private readonly Dictionary<string, Windows.ApplicationModel.AppInfo> _apps = new(StringComparer.OrdinalIgnoreCase);
    // One Start's loop and what only it touches. Stop cancels it and the loop lets go of its listener as it ends, so a loop
    // still finishing a reading never shares its listener or reports a state after Stop, even when Start follows at once.
    private sealed class Run
    {
        public readonly CancellationTokenSource Stop = new();
        public UserNotificationListener? Listener;
        public bool Events;
        public DateTimeOffset LastFull = DateTimeOffset.MinValue;
    }
    private Run? _run;
    private uint _newest;
    private bool _baseline = true, _dirty, _quiet;
    // A full reading has seen a notice arrive, so _newest is the newest id Windows has given out and the probe can rely on it.
    private bool _confirmed;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public NoticeSourceState State { get; private set; } = NoticeSourceState.Off;
    public string Detail { get; private set; } = "";
    public event Action<NoticeInput>? Received;
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
        lock (_gate) { _run = run; _baseline = true; _known.Clear(); _newest = 0; _confirmed = false; }
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
        lock (_gate) { if (_quiet == quiet) return; _quiet = quiet; if (!quiet) _dirty = true; }
        Wake();
    }

    // Reads the list again soon, for example after access was granted. Returns the request's number: a Synced with this number
    // or more comes from a reading that started after it.
    public int Refresh(bool baseline = false)
    {
        int number;
        lock (_gate) { _dirty = true; if (baseline) _baseline = true; number = ++_refreshes; }
        Wake();
        return number;
    }

    private void Wake() { try { _wake.Release(); } catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException) { } }

    private void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Notices that arrived during sleep are taken as a new baseline instead of being shown at once.
        if (e.Mode == PowerModes.Resume) Refresh(baseline: true);
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
                try { await StepAsync(run); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
                {
                    SetState(NoticeSourceState.Failed, ex.Message, run);
                }
                try { await _wake.WaitAsync(ProbeInterval, token); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            }
        }
        finally { Detach(run); }
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
        bool confirmed;
        lock (_gate) { full = _dirty || _baseline; quiet = _quiet; newest = _newest; confirmed = _confirmed; }
        var interval = quiet ? QuietInterval : run.Events ? EventSafetyInterval : !confirmed ? UnknownInterval : FullInterval;
        if (!full && now - run.LastFull >= interval) full = true;
        // The next id is taken by the next notice for this user, so finding it means something arrived. Without access the
        // lookup finds nothing, and the next full reading reports the change of access.
        if (!full && run.Listener is { } probe && !run.Events && !quiet && newest > 0)
        {
            try { if (probe.GetNotification(newest + 1) is not null) full = true; }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { full = true; }
        }
        if (!full && run.Listener is not null) return;
        // A revoked permission makes the list read as empty; that must not look like every notice being removed.
        var access = AccessStatus();
        token.ThrowIfCancellationRequested();
        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            lock (_gate) { _baseline = true; _known.Clear(); _newest = 0; _confirmed = false; }
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
        var list = await run.Listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(token);
        Sync(list, run, covers);
    }

    private void Sync(IReadOnlyList<UserNotification> list, Run run, int covers)
    {
        var added = new List<NoticeInput>();
        var removed = new List<string>();
        lock (_gate)
        {
            // Stopped (and perhaps started again) while reading: this reading belongs to no run any more.
            if (_run != run) return;
            var present = new HashSet<uint>();
            foreach (var notification in list)
            {
                uint id;
                try { id = notification.Id; } catch (COMException) { continue; }
                present.Add(id);
                if (id > _newest) _newest = id;
                if (_known.ContainsKey(id)) continue;
                // One unreadable notification is skipped without affecting the others.
                var input = Read(notification, id);
                _known[id] = input?.InstanceKey ?? "";
                if (!_baseline) _confirmed = true;
                if (input is not null && !_baseline) added.Add(input);
            }
            foreach (var gone in _known.Keys.Where(id => !present.Contains(id)).ToList())
            {
                if (_known[gone].Length > 0) removed.Add(_known[gone]);
                _known.Remove(gone);
            }
            _baseline = false;
        }
        // Oldest first, so that the newest ends up on top of the recent notices.
        foreach (var input in added.OrderBy(i => i.CreatedAt)) Received?.Invoke(input);
        foreach (var key in removed) Removed?.Invoke(key);
        Synced?.Invoke(covers);
    }

    private NoticeInput? Read(UserNotification notification, uint id)
    {
        try
        {
            var app = notification.AppInfo;
            string appId = Safe(() => app.AppUserModelId) ?? Safe(() => app.Id) ?? "";
            if (appId.Length == 0 || string.Equals(appId, OwnAppId, StringComparison.OrdinalIgnoreCase)) return null;
            string name = Safe(() => app.DisplayInfo.DisplayName) is { Length: > 0 } display ? display : appId;
            lock (_apps) _apps[appId] = app;
            var visual = notification.Notification?.Visual;
            var binding = visual?.GetBinding(KnownNotificationBindings.ToastGeneric) ?? visual?.Bindings.FirstOrDefault();
            var texts = binding?.GetTextElements().Select(text => text.Text?.Trim() ?? "").Where(text => text.Length > 0).ToList() ?? [];
            name = BrimDeck.Protocol.NoticeProtocol.Clip(name, BrimDeck.Protocol.NoticeProtocol.MaxField)!;
            var created = Safe(() => (DateTimeOffset?)notification.CreationTime) ?? DateTimeOffset.UtcNow;
            return new NoticeInput
            {
                Origin = NoticeOrigin.Windows, AppKey = NoticeKeys.Windows(appId), AppName = name, AppId = appId,
                InstanceKey = $"windows:{appId}:{id}:{created.UtcTicks}",
                Title = BrimDeck.Protocol.NoticeProtocol.Clip(texts.FirstOrDefault() ?? "", BrimDeck.Protocol.NoticeProtocol.MaxTitle)!,
                Body = BrimDeck.Protocol.NoticeProtocol.Clip(string.Join("\n", texts.Skip(1)), BrimDeck.Protocol.NoticeProtocol.MaxBody)!,
                IconRef = "aumid:" + appId, CreatedAt = created, Capabilities = NoticeCapabilities.OpenApp
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or NullReferenceException) { return null; }
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
            if (app.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64)) is not { } logo) return null;
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
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException) { }
        }
        // Desktop applications register their id with a Start menu shortcut; the shell starts them by it. The id is passed as
        // one argument and must look like an application id, so it cannot carry switches or a path for the shell to open.
        if (!IsAppId(appId)) return false;
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

    // Letters, digits and the punctuation application ids use (package family names, "!App", "{GUID}\\folder\\app.exe");
    // no commas, slashes, colons, quotes, ".." or leading dashes, which the shell would read as switches or paths.
    internal static bool IsAppId(string appId)
        => appId.Length is > 0 and <= 512 && appId[0] is not ('-' or '/' or '\\' or '.') && !appId.Contains("..", StringComparison.Ordinal)
           && appId.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '!' or '{' or '}' or '-' or '\\' or ' ' or '+' or '(' or ')' or '~');

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

    // The semaphore is left alone: a loop still ending, or a late change event, may touch it.
    public void Dispose() => Stop();
}
