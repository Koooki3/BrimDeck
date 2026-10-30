using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using Windows.Media;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace BrimDeck.Native;

// Each route has one authoritative source and, in log mode only, a control supplement.
internal sealed class MediaSessions : IDisposable
{
    // A session that continues a departed one of the same app (see MediaSessionHandover) keeps its route,
    // song and artwork until its own have been read.
    private sealed class SystemSource(Session session, SystemSource? heir = null)
    {
        public Session Session { get; } = session;
        public string App { get; } = session.SourceAppUserModelId;
        public string Id { get; } = heir?.Id ?? "system:" + Guid.NewGuid().ToString("N");
        public string ProcessName { get; } = MediaAppTarget.ProcessName(session.SourceAppUserModelId);
        public bool Dirty = true, MetadataDirty = true;
        public Task? PendingRead;
        public int Revision;
        public MediaTrack? Track = heir?.Track;
        public BitmapSource? Cover = heir?.Cover;
        public byte[]? CoverBytes = heir?.CoverBytes;
        // What the route shows, which differs from Track while MediaSwapGuard holds a change back.
        public readonly MediaSwapGuard Guard = new();
        public MediaTrack? Shown = heir?.Shown ?? heir?.Track;
        public BitmapSource? ShownCover = heir?.ShownCover ?? heir?.Cover;
        // When BrimDeck last moved this session's position through the window controls (MediaRouting.SettlingAfterSeek).
        public DateTimeOffset? SeekAt;
        // Where Apple Music's whole-second reports place the playing song's start (MediaRouting.FromWholeSeconds).
        public MediaRouting.WholeSecondStart? WholeSeconds;
    }
    private sealed class CoverCache
    {
        public BitmapSource? Cover;
        // The artwork on screen before a song change stays there while the new song's cover downloads.
        public BitmapSource? Previous;
        public string CoverKey = "";
        public byte[]? CoverBytes;
        public CancellationTokenSource? CoverCancellation;
    }
    private sealed record Route(string Id, string ProcessName, SystemSource? System, IMediaPlayerConnection? Connection = null, bool Supplement = false, bool Departed = false);

    private readonly Dispatcher _dispatcher;
    // Debounce and recheck decide when a track change reaches the panel, so they run on PromptTimer; see PromptTimer.
    private readonly PromptTimer _debounce, _recheck;
    private readonly DispatcherTimer _poll;
    private readonly MediaPlaybackStack _stack = new();
    private readonly AudioLevelMeter _meter = new();
    private readonly IMediaPlayerConnection[] _connections;
    private readonly AppleMusicControls? _appleMusic;
    private readonly HttpClient _coverHttp = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly Dictionary<Session, SystemSource> _sessions = new();
    private readonly Dictionary<string, CoverCache> _covers = new();
    // Downloaded artwork of recent songs, so switching back to one shows its cover at once.
    // Players that publish only a URL (QQ's pipe, Netease) would otherwise download it on every switch.
    private const int RecentCoverCount = 16;
    private readonly LinkedList<(string Url, byte[] Bytes)> _recentCovers = new();
    private readonly Dictionary<string, Route> _routes = new();
    private readonly MediaSessionHandover _handover = new();
    // Sessions on screen that disappeared within the handover hold, by route id.
    private readonly Dictionary<string, SystemSource> _departed = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _reading, _again, _disposed, _sessionListDirty = true, _visible, _systemVisible, _requestingManager, _announcedRestarting;
    private DateTimeOffset _recoveryAt;
    private string? _selected, _pinned, _pinnedSource, _announcedPin;
    public IReadOnlyList<MediaTrack> Tracks { get; private set; } = [];
    public MediaTrack? Current { get; private set; }
    public BitmapSource? Cover { get; private set; }
    public string? Error { get; private set; }
    public bool CanEnableFullControl => Current?.IsNeteaseLog == true && Netease?.ProcessId is not null;
    public bool RestartingNetease => Netease?.Restarting == true;
    public bool NeteaseInstalled => Netease?.Installed == true;
    public bool NeteaseRunning => Netease?.ProcessId is not null;
    public bool NeteaseFullControl => Netease is { ProcessId: not null, IsLogMode: false };
    private NeteaseSource? Netease => _connections.OfType<NeteaseSource>().FirstOrDefault();
    public bool NeteaseAutoFullControl { set { if (Netease is { } source) source.AutoFullControl = value; } }
    public async Task<bool> EnableFullControlAsync(bool quiet = false)
    {
        var source = Netease;
        if (source is null) return false;
        bool manual = _selected is not null;
        bool enabled = await source.EnableFullControlAsync(quiet);
        if (_disposed) return false;
        _selected = source.Identity;
        await RefreshAsync();
        if (!manual) _selected = null;
        if (!enabled && !quiet) Error = source.Error;
        Changed?.Invoke();
        return enabled;
    }
    // A pinned source stays on screen whatever other players do, until it is unpinned or closes.
    public string? Pinned => _pinned;
    public event Action? Changed;

    public MediaSessions(Dispatcher dispatcher, bool connectPlayers = true)
    {
        _dispatcher = dispatcher;
        _connections = connectPlayers ? [new NeteaseSource(), new QqMediaConnection()] : [];
        _debounce = new PromptTimer(dispatcher, async () => { _debounce!.Stop(); await RefreshAsync(); }) { Interval = TimeSpan.FromMilliseconds(100) };
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            async (_, _) => await RefreshAsync(), dispatcher);
        // A briefly stopped source is held in place; no event arrives when that hold ends.
        _recheck = new PromptTimer(dispatcher, () => { _recheck!.Stop(); Schedule(); }) { Interval = TimeSpan.FromSeconds(1) };
        _debounce.Stop(); _poll.Stop(); _recheck.Stop();
        foreach (var connection in _connections) connection.Changed += Schedule;
        if (connectPlayers) { _appleMusic = new AppleMusicControls(); _appleMusic.Changed += AppleMusicChanged; }
    }
    // Apple Music's shuffle, repeat and seeking through the controls of its window (AppleMusicControls), enabled from the
    // music page's "Enable full control" entry after its explanation. It lasts while BrimDeck runs and is not saved, so the
    // entry and its explanation come back after a restart, unless the setting AppleMusicFullControl enables it at start.
    public bool AppleMusicFullControl { get; private set; }
    public void EnableAppleMusicFullControl()
    {
        if (_appleMusic is null) return;
        AppleMusicFullControl = true;
        // The window's controls are read at once instead of at the next session event.
        _appleMusic.Refresh(now: true); AppleMusicChanged();
    }
    private void AppleMusicChanged() => _dispatcher.BeginInvoke(() =>
    {
        foreach (var source in _sessions.Values.Where(s => MediaRouting.IsAppleMusic(s.App))) source.Dirty = true;
        Schedule();
    });
    public async Task StartAsync()
    {
        foreach (var connection in _connections) await connection.StartAsync();
        _ = RequestManagerAsync();
        if (_disposed) return;
        await RefreshAsync(); _poll.Start();
    }
    private async Task RequestManagerAsync()
    {
        if (_disposed || _manager is not null || _requestingManager) return;
        _requestingManager = true;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return;
            _manager = manager;
            manager.SessionsChanged += SessionsChanged;
            manager.CurrentSessionChanged += CurrentChanged;
            _sessionListDirty = true; Error = null; Schedule();
        }
        catch (Exception ex) { Error = Loc.T("无法读取媒体会话：", "Media sessions could not be read: ") + ex.Message; }
        finally { _requestingManager = false; }
    }
    private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => _dispatcher.BeginInvoke(() => { _sessionListDirty = true; Schedule(); });
    private void CurrentChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Schedule();
    private void MetadataChanged(Session sender, MediaPropertiesChangedEventArgs args) => Dirty(sender, true);
    private void PlaybackChanged(Session sender, PlaybackInfoChangedEventArgs args) => Dirty(sender, false);
    private void TimelineChanged(Session sender, TimelinePropertiesChangedEventArgs args) => Dirty(sender, false);
    private void Dirty(Session session, bool metadata) => _dispatcher.BeginInvoke(() =>
    {
        if (_sessions.TryGetValue(session, out var source))
        {
            source.Dirty = true; source.MetadataDirty |= metadata;
            if (metadata)
            {
                source.Revision++;
                _ = RetryMetadataAsync(source, source.Revision);
            }
        }
        Schedule();
    });
    private void Schedule()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        // A burst of events is read once, 100 ms after its first event. Restarting the wait on every event
        // would postpone the read for as long as a player keeps reporting progress.
        _dispatcher.BeginInvoke(() => { if (!_disposed && !_debounce.IsEnabled) _debounce.Start(); });
    }
    public void Select(string? id)
    {
        // Choosing another source while one is pinned moves the pin instead of dropping it.
        if (_pinned is not null && id is not null) { Pin(id); return; }
        _selected = id; Schedule();
    }
    public void Pin(string? id)
    {
        if (id is not null && Tracks.FirstOrDefault(t => t.Id == id) is { } track)
        { _pinned = id; _pinnedSource = track.Source; _selected = null; }
        else
        {
            // Unpinning keeps the source on screen as an ordinary selection until another player starts.
            if (_pinned is not null) _selected = _pinned;
            _pinned = _pinnedSource = null;
        }
        Schedule();
    }
    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) => a is { } x && b is { } y ? (x < y ? x : y) : a ?? b;
    private void ScheduleRecheck(DateTimeOffset? at)
    {
        _recheck.Stop();
        if (at is not { } due) return;
        _recheck.Interval = TimeSpan.FromMilliseconds(Math.Max(50, (due - DateTimeOffset.UtcNow).TotalMilliseconds + 50));
        _recheck.Start();
    }
    // system: bars are shown that read the whole system's sound while no song plays.
    public void SetPresentationActive(bool active, bool system = false)
    {
        if (_visible == active && _systemVisible == system) return;
        bool appeared = active && !_visible;
        _visible = active; _systemVisible = system; UpdateMeter();
        if (appeared) Schedule();
    }
    private void Subscribe(Session session)
    { session.MediaPropertiesChanged += MetadataChanged; session.PlaybackInfoChanged += PlaybackChanged; session.TimelinePropertiesChanged += TimelineChanged; }
    private void Unsubscribe(Session session)
    { session.MediaPropertiesChanged -= MetadataChanged; session.PlaybackInfoChanged -= PlaybackChanged; session.TimelinePropertiesChanged -= TimelineChanged; }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        if (_reading) { _again = true; return; }
        _reading = true;
        try
        {
            do
            {
                _again = false;
                // Recovery is deliberately slow. Normal discovery and GSMTC updates are event-driven.
                if (DateTimeOffset.UtcNow - _recoveryAt > TimeSpan.FromSeconds(30))
                {
                    _recoveryAt = DateTimeOffset.UtcNow; _sessionListDirty = true;
                    if (_manager is null) _ = RequestManagerAsync();
                    foreach (var source in _sessions.Values) source.Dirty = source.MetadataDirty = true;
                }
                if (_sessionListDirty)
                {
                    _sessionListDirty = false;
                    var live = _manager?.GetSessions().ToArray() ?? [];
                    var synced = DateTimeOffset.UtcNow;
                    foreach (var removed in _sessions.Keys.Where(s => !live.Contains(s)).ToArray())
                    {
                        var departed = _sessions[removed];
                        Unsubscribe(removed); _sessions.Remove(removed);
                        if (departed.Track is not null && Current?.Id == departed.Id)
                        { _handover.Depart(departed.Id, departed.App, synced); _departed[departed.Id] = departed; }
                    }
                    foreach (var session in live.Where(s => !_sessions.ContainsKey(s)))
                    {
                        var heir = _handover.Claim(session.SourceAppUserModelId, synced) is { } continued && _departed.Remove(continued, out var previous) ? previous : null;
                        var source = new SystemSource(session, heir);
                        _sessions.Add(session, source); Subscribe(session); _ = RetryMetadataAsync(source, source.Revision);
                    }
                }
                var heldAt = DateTimeOffset.UtcNow;
                foreach (var id in _departed.Keys.Where(id => !_handover.Holds(id, heldAt)).ToArray()) _departed.Remove(id);
                // Provider reads complete independently and schedule their own refresh. A slow
                // Windows source must not delay native player events.
                foreach (var source in _sessions.Values.Where(s => s.Dirty).ToArray()) _ = ReadSystemAsync(source);
                if (_disposed) return;
                var routes = BuildRoutes();
                var tracks = new List<MediaTrack>();
                var covers = new Dictionary<string, BitmapSource?>();
                DateTimeOffset? swapDue = null;
                foreach (var route in routes)
                {
                    MediaTrack? track;
                    BitmapSource? artwork;
                    if (route.Connection?.Snapshot is { } native)
                    {
                        track = FromDesktop(native) with { Id = route.Id, Source = SourceName(route.ProcessName) };
                        var log = track;
                        // NetEase's SMTC session, when switched on, reports a skip before its log does; it leads and the log fills in.
                        var leading = route.Supplement && route.System?.Track is { } smtc && !string.IsNullOrWhiteSpace(smtc.Title) ? smtc : null;
                        if (leading is not null) track = MediaRouting.PreferSystem(log, leading);
                        else
                        {
                            // If the log format is unavailable, the system source owns metadata.
                            if (native.IsNeteaseLog && native.Title.Length == 0 && route.System?.Track is { } fallback)
                                track = fallback with { Id = route.Id, IsNeteaseLog = true, CanSeek = false, CanShuffle = false, CanRepeat = false };
                            if (route.Supplement) track = MediaRouting.Merge(track, route.System?.Track);
                        }
                        if (!_covers.TryGetValue(route.Id, out var cache)) _covers[route.Id] = cache = new();
                        // Not SongKey: it includes the duration, which QQ's pipe reports about a second after the song.
                        string key = (native.SongId ?? $"{track.Title}\n{track.Artist}\n{track.Album}") + "|" + native.CoverUrl;
                        bool bytesChanged = native.Cover is { } bytes && (cache.CoverBytes is null || !bytes.AsSpan().SequenceEqual(cache.CoverBytes));
                        if (cache.CoverKey != key || bytesChanged)
                        {
                            cache.CoverCancellation?.Cancel(); cache.CoverKey = key; cache.CoverBytes = native.Cover;
                            var shown = cache.Cover ?? cache.Previous;
                            cache.Cover = DecodeCover(native.Cover ?? RecentCover(native.CoverUrl));
                            cache.Previous = null;
                            if (cache.Cover is null && native.CoverUrl is not null) { cache.Previous = shown; _ = ReadCoverAsync(cache, key, native.CoverUrl); }
                        }
                        // The log's cover is the full-size album picture, so it is used once the log has reached the session's song.
                        // Until then the session's thumbnail stands in; neither borrows another player's artwork.
                        if (leading is not null)
                            artwork = MediaRouting.LogMatchesSession(log, leading) && cache.Cover is { } logCover ? logCover : route.System!.Cover ?? cache.Cover ?? cache.Previous;
                        else artwork = native.IsNeteaseLog && native.Title.Length == 0 ? route.System?.Cover : cache.Cover ?? cache.Previous;
                    }
                    else
                    {
                        var source = route.System;
                        track = source?.Track is { } system ? system with { Id = route.Id } : null; artwork = source?.Cover;
                        if (route.Departed)
                        {
                            // A departed session no longer plays; it only keeps its place until a successor arrives or the hold ends.
                            if (source?.Shown is { } shown) { track = shown with { Id = route.Id }; artwork = source.ShownCover; }
                            if (track is { State: MediaState.Playing }) track = track with { State = MediaState.Paused };
                        }
                        else if (source is not null && track is not null)
                        {
                            // Another tab's song standing in for a playing one during its track change is not shown; see MediaSwapGuard.
                            if (source.Guard.Holds(source.Shown, track, heldAt)) { track = source.Shown! with { Id = route.Id }; artwork = source.ShownCover; }
                            else { source.Shown = track; source.ShownCover = artwork; }
                            swapDue = Earliest(swapDue, source.Guard.RecheckAt);
                        }
                    }
                    if (track is null) continue;
                    tracks.Add(track); covers[track.Id] = artwork;
                }
                if (_disposed) return;
                _routes.Clear(); foreach (var route in routes) _routes[route.Id] = route;
                foreach (var key in _covers.Keys.Where(k => routes.All(r => r.Id != k)).ToArray())
                { _covers[key].CoverCancellation?.Cancel(); _covers.Remove(key); }
                if (_selected is not null && tracks.All(t => t.Id != _selected)) _selected = null;
                _pinned = MediaPlaybackStack.FollowPin(_pinned, _pinnedSource, tracks, Tracks);
                if (_pinned is null) _pinnedSource = null;
                var systemCurrent = _manager?.GetCurrentSession();
                var preferred = routes.FirstOrDefault(r => r.System?.Session == systemCurrent)?.Id;
                var now = DateTimeOffset.UtcNow;
                var decision = _stack.Update(tracks, now, preferred, Current?.Id);
                // Starting playback in another player is a newer choice than a manual selection.
                if (decision.Started is { } started && started != _selected) _selected = null;
                ScheduleRecheck(Earliest(Earliest(decision.RecheckAt, _handover.NextExpiry), swapDue));
                var current = tracks.FirstOrDefault(t => t.Id == _pinned) ?? tracks.FirstOrDefault(t => t.Id == _selected)
                    ?? tracks.FirstOrDefault(t => t.Id == decision.Current);
                // QQ Music reports a stop of 50 to 100 ms between songs. A stopped player leaves the compact view,
                // so a brief stop is shown as a pause; the recheck shows a lasting stop once the stack's grace ends.
                if (current is { State: MediaState.Stopped } && _stack.BrieflyStopped(current.Id, now)) current = current with { State = MediaState.Paused };
                var cover = current is null ? null : covers.GetValueOrDefault(current.Id);
                string? connectionError = routes.FirstOrDefault(r => r.Id == current?.Id)?.Connection?.Error;
                // The end of a restart changes nothing else once the player is back, yet the entries it disabled must return.
                bool restarting = RestartingNetease;
                bool changed = Error != connectionError || !Tracks.SequenceEqual(tracks) || Current != current || Cover != cover || _announcedPin != _pinned || _announcedRestarting != restarting;
                _announcedPin = _pinned; _announcedRestarting = restarting;
                Tracks = tracks; Current = current; Cover = cover;
                Error = connectionError;
                UpdateMeter();
                if (changed) Changed?.Invoke();
            } while (_again && !_disposed);
        }
        catch (Exception ex) { Error = Loc.T("媒体信息暂时不可用：", "Media information is temporarily unavailable: ") + ex.Message; Changed?.Invoke(); }
        finally { _reading = false; }
    }

    private async Task RetryMetadataAsync(SystemSource source, int revision)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(attempt == 0 ? 500 : 1000);
            if (_disposed || source.Revision != revision || !_sessions.ContainsValue(source)) return;
            source.Dirty = source.MetadataDirty = true; Schedule();
        }
    }
    private static bool AppMatches(SystemSource source, string executable) =>
        string.Equals(Path.GetFileName(source.Session.SourceAppUserModelId), executable, StringComparison.OrdinalIgnoreCase);
    private List<Route> BuildRoutes()
    {
        var routes = new List<Route>();
        var consumed = new HashSet<SystemSource>();
        var current = _connections.Length > 0 ? _manager?.GetCurrentSession() : null;
        foreach (var connection in _connections)
        {
            if (connection.ProcessId is not { } pid || connection.Snapshot is not { } snapshot) continue;
            bool netease = connection is NeteaseSource;
            string name = netease ? "cloudmusic" : "QQMusic";
            var systems = _sessions.Values.Where(s => AppMatches(s, name + ".exe")).ToArray();
            var system = systems.FirstOrDefault(s => s.Session == current) ?? systems.FirstOrDefault();
            // A disconnected QQ pipe falls back as a whole to GSMTC. Retain its last
            // song without capabilities only when there is no system replacement.
            if (connection is QqMediaConnection { Ready: false } && system is not null) continue;
            foreach (var entry in systems) consumed.Add(entry);
            string identity = connection is NeteaseSource ns ? ns.Identity : ((QqMediaConnection)connection).Identity;
            routes.Add(new(identity, name, system, connection, netease && snapshot.IsNeteaseLog));
        }
        foreach (var source in _sessions.Values.Where(s => !consumed.Contains(s)))
            routes.Add(new(source.Id, source.ProcessName, source));
        foreach (var source in _departed.Values) routes.Add(new(source.Id, source.ProcessName, source, Departed: true));
        return routes;
    }

    private async Task ReadCoverAsync(CoverCache cache, string key, string url)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        cache.CoverCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
            // Netease originals reach 3000 px and more than the size limit below. Its image CDN returns
            // the size DecodeCover keeps anyway when asked with its own resize parameter.
            if (uri.Query.Length == 0 && uri.Host.EndsWith(".music.126.net", StringComparison.OrdinalIgnoreCase))
                uri = new Uri(uri.AbsoluteUri + $"?param={CoverPixels}y{CoverPixels}");
            using var response = await _coverHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 8_388_608) return;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var bytes = new byte[16_384];
            int length;
            while ((length = await stream.ReadAsync(bytes, token)) > 0)
            {
                if (buffer.Length + length > 8_388_608) return;
                buffer.Write(bytes, 0, length);
            }
            var downloaded = buffer.ToArray();
            if (_disposed) return;
            RememberCover(url, downloaded);
            if (cache.CoverKey != key) return;
            var decoded = await Task.Run(() => DecodeCover(downloaded));
            if (_disposed || cache.CoverKey != key) return;
            cache.Cover = decoded; cache.Previous = null; Schedule();
        }
        catch { /* A cover request cannot block playback state or controls. */ }
        finally
        {
            if (ReferenceEquals(cache.CoverCancellation, cancellation)) cache.CoverCancellation = null;
            // A failed download stops holding the previous song's artwork; the placeholder replaces it.
            if (!_disposed && cache.CoverKey == key && cache.Previous is not null) { cache.Previous = null; Schedule(); }
        }
    }
    private byte[]? RecentCover(string? url)
    {
        if (url is null) return null;
        for (var node = _recentCovers.First; node is not null; node = node.Next)
            if (node.Value.Url == url) { _recentCovers.Remove(node); _recentCovers.AddFirst(node); return node.Value.Bytes; }
        return null;
    }
    private void RememberCover(string url, byte[] bytes)
    {
        if (RecentCover(url) is not null) return;
        _recentCovers.AddFirst((url, bytes));
        while (_recentCovers.Count > RecentCoverCount) _recentCovers.RemoveLast();
    }

    private async Task ReadSystemAsync(SystemSource source)
    {
        // One hung provider must not stall audio discovery or create more in-flight reads each tick.
        if (source.PendingRead is { IsCompleted: false }) return;
        source.Dirty = false;
        bool metadata = source.MetadataDirty || source.Track is null;
        source.MetadataDirty = false;
        var pending = ReadSystemCoreAsync(source, metadata, source.Revision);
        source.PendingRead = pending;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (TimeoutException) { Error = Loc.T("Windows 媒体读取超时。", "Reading Windows media timed out."); }
    }

    private async Task ReadSystemCoreAsync(SystemSource source, bool metadata, int revision)
    {
        try
        {
            var track = source.Track ?? new MediaTrack { PositionKnown = false };
            var cover = source.Cover;
            var coverBytes = source.CoverBytes;
            if (metadata)
            {
                var properties = await source.Session.TryGetMediaPropertiesAsync();
                var (artist, album) = MediaRouting.SessionArtistAlbum(source.App, properties.Artist ?? "", properties.AlbumTitle ?? "");
                var updated = track with { Title = properties.Title ?? "", Artist = artist, Album = album };
                if (!MediaRouting.SameSong(track, updated)) { cover = null; coverBytes = null; }
                track = updated;
                if (properties.Thumbnail is { } thumbnail)
                {
                    try
                    {
                        using var stream = await thumbnail.OpenReadAsync();
                        if (stream.Size is > 0 and < 16_777_216)
                        {
                            using var reader = new Windows.Storage.Streams.DataReader(stream);
                            await reader.LoadAsync((uint)stream.Size);
                            var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
                            // Unchanged artwork keeps its image, so periodic rereads do not rebuild the page.
                            // Decoding a large cover took tens of milliseconds on the UI thread; the frozen image is made on the pool.
                            if (coverBytes is null || !bytes.AsSpan().SequenceEqual(coverBytes)) { cover = await Task.Run(() => DecodeCover(bytes)); coverBytes = bytes; }
                        }
                    }
                    catch { /* Artwork failure must not suppress controls. */ }
                }
            }
            var playback = source.Session.GetPlaybackInfo(); var controls = playback.Controls;
            var timeline = source.Session.GetTimelineProperties();
            var state = playback.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaState.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaState.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaState.Stopped,
                _ => MediaState.Unknown
            };
            bool known = timeline.EndTime > timeline.StartTime && timeline.LastUpdatedTime.Year > 1601 &&
                timeline.Position >= timeline.StartTime && timeline.Position <= timeline.EndTime;
            if (_disposed || revision != source.Revision) return;
            source.Cover = cover; source.CoverBytes = coverBytes;
            var read = track with
            {
                Id = source.Id, State = state, Source = SourceName(source.Session.SourceAppUserModelId),
                PositionKnown = known, Start = timeline.StartTime, End = timeline.EndTime,
                ReportedPosition = timeline.Position, PositionAt = timeline.LastUpdatedTime,
                MinSeek = timeline.MinSeekTime, MaxSeek = timeline.MaxSeekTime, Rate = playback.PlaybackRate ?? 1,
                CanToggle = controls.IsPlayPauseToggleEnabled || (state == MediaState.Playing ? controls.IsPauseEnabled : controls.IsPlayEnabled),
                CanPrevious = controls.IsPreviousEnabled, CanNext = controls.IsNextEnabled,
                CanSeek = known && controls.IsPlaybackPositionEnabled,
                CanShuffle = controls.IsShuffleEnabled, CanRepeat = controls.IsRepeatEnabled, Shuffle = playback.IsShuffleActive == true,
                Repeat = playback.AutoRepeatMode switch { MediaPlaybackAutoRepeatMode.Track => MediaRepeat.Track, MediaPlaybackAutoRepeatMode.List => MediaRepeat.List, _ => MediaRepeat.None }
            };
            if (MediaRouting.IsAppleMusic(source.App))
            {
                var now = DateTimeOffset.UtcNow;
                if (source.Track is { } settling && MediaRouting.SettlingAfterSeek(settling, read, source.SeekAt, now))
                    read = read with { State = MediaState.Playing, Start = settling.Start, ReportedPosition = settling.ReportedPosition, PositionAt = settling.PositionAt };
                else (read, source.WholeSeconds) = MediaRouting.FromWholeSeconds(source.WholeSeconds, read);
                // The session offers no shuffle, repeat or seeking; with full control the window's controls stand in while
                // they are found.
                if (AppleMusicFullControl) _appleMusic?.Refresh();
                if (AppleMusicFullControl && _appleMusic?.Latest is { } window)
                    read = read with { CanShuffle = true, Shuffle = window.Shuffle, CanRepeat = true, Repeat = window.Repeat,
                        CanSeek = read.HasTimeline && window.CanSeek, SeparateModes = true };
            }
            source.Track = read;
        }
        catch (Exception ex)
        {
            Error = Loc.T("Windows 媒体读取失败：", "Reading Windows media failed: ") + ex.Message;
            if (source.Track is { } previous) source.Track = previous with { State = MediaState.Unknown, PositionKnown = false,
                CanToggle = false, CanPrevious = false, CanNext = false, CanSeek = false, CanRepeat = false, CanShuffle = false };
        }
        finally { Schedule(); }
    }
    private static MediaTrack FromDesktop(DesktopMediaSnapshot snapshot) => new()
    {
        Title = snapshot.Title, Artist = snapshot.Artist, Album = snapshot.Album, State = snapshot.State ?? MediaState.Unknown,
        PositionKnown = snapshot.Position is { } position && snapshot.Duration is { } duration && duration > TimeSpan.Zero && position >= TimeSpan.Zero && position <= duration,
        TimelinePending = snapshot.TimelinePending, Rate = snapshot.Rate, IsNeteaseLog = snapshot.IsNeteaseLog, PreviousRestricted = snapshot.PreviousRestricted,
        End = snapshot.Duration ?? TimeSpan.Zero, ReportedPosition = snapshot.Position ?? TimeSpan.Zero, PositionAt = snapshot.PositionAt ?? DateTimeOffset.UtcNow,
        SongId = snapshot.SongId ?? "", EmbeddedLyrics = snapshot.SyncedLyrics ?? "", CurrentLyric = snapshot.CurrentLyric, CurrentLyricStart = snapshot.CurrentLyricStart,
        CanToggle = snapshot.CanToggle, CanPrevious = snapshot.CanPrevious, CanNext = snapshot.CanNext, CanSeek = snapshot.CanSeek,
        CanShuffle = snapshot.CanShuffle, CanRepeat = snapshot.CanRepeat, Shuffle = snapshot.Shuffle == true, Repeat = snapshot.Repeat ?? MediaRepeat.None,
        CanLike = snapshot.CanLike, Liked = snapshot.CanLike && snapshot.Liked
    };
    private const int CoverPixels = 512;
    internal static BitmapSource? DecodeCover(byte[]? bytes)
    {
        if (bytes is null) return null;
        try
        {
            try { return Decode(BitmapCreateOptions.None); }
            // An empty EXIF block in some QQ covers makes WIC's color-context read fail.
            // Decode the pixels without that metadata, while keeping normal color handling first.
            catch (IOException) { return Decode(BitmapCreateOptions.IgnoreColorProfile); }
        }
        catch { return null; }

        BitmapSource Decode(BitmapCreateOptions options)
        {
            using var memory = new MemoryStream(bytes);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = options;
            image.DecodePixelWidth = CoverPixels; image.StreamSource = memory; image.EndInit(); image.Freeze(); return image;
        }
    }

    public async Task<bool> CommandAsync(string id, MediaCommand command)
    {
        if (!_routes.TryGetValue(id, out var route)) return false;
        var current = Tracks.FirstOrDefault(t => t.Id == id);
        if (current is null || command == MediaCommand.Previous && !current.CanPrevious || command == MediaCommand.Next && !current.CanNext ||
            command == MediaCommand.PlayPause && !current.CanToggle || command == MediaCommand.Shuffle && !current.CanShuffle || command == MediaCommand.Repeat && !current.CanRepeat ||
            command == MediaCommand.Like && !current.CanLike) return false;
        var state = current.State;
        try
        {
            if (command is MediaCommand.Shuffle or MediaCommand.Repeat && current.SeparateModes && _appleMusic is { } apple)
                return await (command == MediaCommand.Shuffle ? apple.ToggleShuffleAsync() : apple.CycleRepeatAsync()).WaitAsync(TimeSpan.FromSeconds(3));
            return await MediaRouting.DispatchAsync(async () => route.Connection is { } connection
                ? DispatchResult(await connection.CommandAsync(command, state))
                : MediaDispatchResult.Unavailable,
                () => (route.Connection is null || route.Supplement && command is MediaCommand.PlayPause or MediaCommand.Previous or MediaCommand.Next)
                    ? SystemCommandAsync(route.System?.Session, command).WaitAsync(TimeSpan.FromSeconds(2)) : Task.FromResult(false));
        }
        catch { return false; }
        finally { Invalidate(route); }
    }
    public async Task<bool> SetPlaybackModeAsync(string id, MusicPlaybackMode mode)
    {
        if (!_routes.TryGetValue(id, out var route) || Tracks.FirstOrDefault(t => t.Id == id) is not { } current ||
            !MusicPlaybackModes.Supported(current).Contains(mode)) return false;
        try
        {
            if (route.Connection is { } connection)
                return await connection.SetPlaybackModeAsync(mode) == DesktopCommandResult.Sent;
            if (route.System?.Session is not { } session) return false;
            var info = session.GetPlaybackInfo();
            bool shuffle = mode == MusicPlaybackMode.Shuffle;
            if ((info.IsShuffleActive == true) != shuffle &&
                !await session.TryChangeShuffleActiveAsync(shuffle).AsTask().WaitAsync(TimeSpan.FromSeconds(2))) return false;
            if (shuffle || !current.CanRepeat) return true;
            var repeat = mode switch { MusicPlaybackMode.List => MediaPlaybackAutoRepeatMode.List, MusicPlaybackMode.Track => MediaPlaybackAutoRepeatMode.Track, _ => MediaPlaybackAutoRepeatMode.None };
            return info.AutoRepeatMode == repeat || await session.TryChangeAutoRepeatModeAsync(repeat).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { return false; }
        finally { Invalidate(route); }
    }
    private static async Task<bool> SystemCommandAsync(Session? session, MediaCommand command)
    {
        if (session is null) return false;
        var info = session.GetPlaybackInfo(); var controls = info.Controls;
        return command switch
        {
            // The system media session has no command for liking a song.
            MediaCommand.Like => false,
            MediaCommand.Previous => controls.IsPreviousEnabled && await session.TrySkipPreviousAsync(),
            MediaCommand.Next => controls.IsNextEnabled && await session.TrySkipNextAsync(),
            MediaCommand.Shuffle => controls.IsShuffleEnabled && await session.TryChangeShuffleActiveAsync(info.IsShuffleActive != true),
            MediaCommand.Repeat => controls.IsRepeatEnabled && await session.TryChangeAutoRepeatModeAsync(info.AutoRepeatMode switch
            { MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List, MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track, _ => MediaPlaybackAutoRepeatMode.None }),
            _ => controls.IsPlayPauseToggleEnabled ? await session.TryTogglePlayPauseAsync()
                : info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    ? controls.IsPauseEnabled && await session.TryPauseAsync() : controls.IsPlayEnabled && await session.TryPlayAsync()
        };
    }
    public async Task<bool> SeekAsync(MediaTrack track, double fraction)
    {
        if (!track.HasTimeline || !track.CanSeek || !_routes.TryGetValue(track.Id, out var route)) return false;
        // A drag can finish after the source has changed songs. Never apply its old
        // duration or fraction to the next song, including changes not rendered yet.
        var current = Tracks.FirstOrDefault(t => t.Id == track.Id);
        if (current is null || !MediaRouting.SameSong(current, track) || !SameKnownId(track.SongId, current.SongId)) return false;
        if (route.Connection?.Snapshot is { } snapshot &&
            (!MediaRouting.SameSong(track, FromDesktop(snapshot)) || !SameKnownId(track.SongId, snapshot.SongId))) return false;
        if (route.Connection is null && route.System is { } apple && MediaRouting.IsAppleMusic(apple.App))
        {
            // The pause Apple Music reports after the seek is not shown; once the time is over the session is read again.
            apple.SeekAt = DateTimeOffset.UtcNow;
            _ = Task.Delay(MediaRouting.SeekSettle + TimeSpan.FromMilliseconds(50)).ContinueWith(_ => _dispatcher.BeginInvoke(() =>
            { if (!_disposed) { apple.Dirty = true; Schedule(); } }), TaskScheduler.Default);
        }
        try
        {
            return await MediaRouting.DispatchAsync(async () => route.Connection is { } connection
                ? DispatchResult(await connection.SeekAsync(TimeSpan.FromTicks(current.SeekTicks(fraction))))
                : MediaDispatchResult.Unavailable,
                async () => route.Connection is null && route.System?.Track is { CanSeek: true } system && MediaRouting.SameSong(track, system) &&
                    (MediaRouting.IsAppleMusic(route.System.App) && _appleMusic is { } apple
                        ? await apple.SeekAsync(TimeSpan.FromTicks(system.SeekTicks(fraction)) - system.Start).WaitAsync(TimeSpan.FromSeconds(3))
                        : await route.System.Session.TryChangePlaybackPositionAsync(system.SeekTicks(fraction)).AsTask().WaitAsync(TimeSpan.FromSeconds(2))));
        }
        catch { return false; }
        finally { Invalidate(route); }
    }
    private static bool SameKnownId(string? first, string? second) =>
        string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second) || string.Equals(first, second, StringComparison.Ordinal);
    private void Invalidate(Route route)
    {
        if (route.System is { } system) system.Dirty = true;
        Schedule();
    }
    private static MediaDispatchResult DispatchResult(DesktopCommandResult result) => result switch
    { DesktopCommandResult.Unavailable => MediaDispatchResult.Unavailable, DesktopCommandResult.Sent => MediaDispatchResult.Sent, _ => MediaDispatchResult.Uncertain };
    public float Peak() => _meter.Peak;
    // Flowing bar values from the player's captured audio; false when only the peak meter is available.
    public bool FlowLevels(Span<double> levels) => _meter.TryFill(levels);
    // The playing player's sound while its bars are seen; otherwise the whole system's, while bars shown at all times ask for it.
    private void UpdateMeter()
    {
        bool player = _visible && Current?.State == MediaState.Playing;
        _meter.SetSource(Current is not null && _routes.TryGetValue(Current.Id, out var route) ? route.ProcessName : null, player, !player && _systemVisible);
    }

    // The application playing the current song, for bringing its window forward.
    public MediaAppOwner? CurrentOwner => Current is not null && _routes.TryGetValue(Current.Id, out var route)
        ? new(route.Connection?.ProcessId, route.ProcessName, route.System?.App ?? "") : null;
    internal static string SourceName(string id)
    {
        string lower = id.ToLowerInvariant();
        if (lower.Contains("cloudmusic") || lower.Contains("netease")) return "网易云音乐";
        if (lower.Contains("qqmusic")) return "QQ 音乐";
        if (lower.Contains("msedge")) return "Microsoft Edge";
        if (lower.Contains("chrome")) return "Google Chrome";
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("itunes") || lower.Contains("applemusic")) return "Apple Music";
        if (lower.Contains("qiyi")) return "爱奇艺";
        return Path.GetFileNameWithoutExtension(id.Split('!')[0]);
    }
    public void Dispose()
    {
        _disposed = true; _debounce.Stop(); _poll.Stop(); _recheck.Stop();
        if (_manager is not null) { _manager.SessionsChanged -= SessionsChanged; _manager.CurrentSessionChanged -= CurrentChanged; }
        foreach (var session in _sessions.Keys) Unsubscribe(session);
        foreach (var cache in _covers.Values) cache.CoverCancellation?.Cancel();
        _sessions.Clear(); _covers.Clear(); _routes.Clear(); _departed.Clear();
        foreach (var connection in _connections) { connection.Changed -= Schedule; connection.Dispose(); }
        _coverHttp.Dispose();
        _meter.Dispose();
    }
}
