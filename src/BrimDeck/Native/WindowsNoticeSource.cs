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
    private UserNotificationListener? _listener;
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private uint _newest;
    private bool _baseline = true, _events, _dirty, _quiet;
    // A full reading has seen a notice arrive, so _newest is the newest id Windows has given out and the probe can rely on it.
    private bool _confirmed;
    private DateTimeOffset _lastFull = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public NoticeSourceState State { get; private set; } = NoticeSourceState.Off;
    public string Detail { get; private set; } = "";
    public event Action<NoticeInput>? Received;
    public event Action<string>? Removed;
    public event Action? StateChanged;

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
        _run = new CancellationTokenSource();
        lock (_gate) { _baseline = true; _known.Clear(); _newest = 0; _confirmed = false; }
        SystemEvents.PowerModeChanged += PowerModeChanged;
        var token = _run.Token;
        _loop = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        if (_run is not { } run) return;
        _run = null;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        run.Cancel();
        if (_listener is { } listener && _events) try { listener.NotificationChanged -= Changed; } catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        _listener = null; _events = false;
        SetState(NoticeSourceState.Off, "");
    }

    // Locked or paused: arriving notices are not shown, so the list is read rarely; it is read in full when that ends.
    public void SetQuiet(bool quiet)
    {
        lock (_gate) { if (_quiet == quiet) return; _quiet = quiet; if (!quiet) _dirty = true; }
        Wake();
    }

    // Reads the list again soon, for example after access was granted.
    public void Refresh(bool baseline = false)
    {
        lock (_gate) { _dirty = true; if (baseline) _baseline = true; }
        Wake();
    }

    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

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

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await StepAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                SetState(NoticeSourceState.Failed, ex.Message);
            }
            try { await _wake.WaitAsync(ProbeInterval, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task StepAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        bool full, quiet;
        uint newest;
        bool confirmed;
        lock (_gate) { full = _dirty || _baseline; quiet = _quiet; newest = _newest; confirmed = _confirmed; }
        var interval = quiet ? QuietInterval : _events ? EventSafetyInterval : !confirmed ? UnknownInterval : FullInterval;
        if (!full && now - _lastFull >= interval) full = true;
        // The next id is taken by the next notice for this user, so finding it means something arrived. Without access the
        // lookup finds nothing, and the next full reading reports the change of access.
        if (!full && _listener is not null && !_events && !quiet && newest > 0)
        {
            try { if (_listener.GetNotification(newest + 1) is not null) full = true; }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { full = true; }
        }
        if (!full && _listener is not null) return;
        // A revoked permission makes the list read as empty; that must not look like every notice being removed.
        var access = AccessStatus();
        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            lock (_gate) { _baseline = true; _known.Clear(); _newest = 0; _confirmed = false; }
            if (_listener is { } old && _events) { try { old.NotificationChanged -= Changed; } catch (Exception ex) when (ex is COMException or InvalidOperationException) { } _events = false; }
            _listener = null;
            SetState(access == UserNotificationListenerAccessStatus.Unspecified ? NoticeSourceState.NeedsAccess : NoticeSourceState.Denied, "");
            return;
        }
        if (_listener is null)
        {
            _listener = UserNotificationListener.Current;
            // Only a process with package identity gets the change event; without it subscribing fails (0x80070490).
            try { _listener.NotificationChanged += Changed; _events = true; }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException) { _events = false; }
        }
        SetState(_events ? NoticeSourceState.Events : NoticeSourceState.Polling, "");
        lock (_gate) _dirty = false;
        _lastFull = now;
        var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(token);
        Sync(list);
    }

    private void Sync(IReadOnlyList<UserNotification> list)
    {
        var added = new List<NoticeInput>();
        var removed = new List<string>();
        lock (_gate)
        {
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
            var logo = app.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            using var stream = await logo.OpenReadAsync();
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
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("shell:AppsFolder\\" + appId);
            using var shell = System.Diagnostics.Process.Start(start);
            return shell is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    // Letters, digits and the punctuation application ids use (package family names, "!App", "{GUID}\\folder\\app.exe");
    // no commas, slashes, colons, quotes or leading dashes, which the shell would read as switches or paths.
    internal static bool IsAppId(string appId)
        => appId.Length is > 0 and <= 512 && appId[0] is not ('-' or '/' or '\\' or '.')
           && appId.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '!' or '{' or '}' or '-' or '\\' or ' ' or '+' or '(' or ')' or '~');

    private void SetState(NoticeSourceState state, string detail)
    {
        if (State == state && Detail == detail) return;
        State = state; Detail = detail;
        StateChanged?.Invoke();
    }

    public void Dispose() { Stop(); _wake.Dispose(); }
}
