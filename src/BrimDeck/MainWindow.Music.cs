using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;
using MediaState = BrimDeck.Core.MediaState;

namespace BrimDeck;

public partial class MainWindow
{
    private static readonly DependencyProperty SuppressMusicToolTipsProperty = DependencyProperty.RegisterAttached(
        "SuppressMusicToolTips", typeof(bool), typeof(MainWindow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    static MainWindow()
    {
        // ToolTipOpening is a direct event. A class handler covers nested template elements as well as their controls.
        static void Suppress(object sender, ToolTipEventArgs e)
        {
            if (sender is DependencyObject element && (bool)element.GetValue(SuppressMusicToolTipsProperty)) e.Handled = true;
        }
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(Suppress), true);
        EventManager.RegisterClassHandler(typeof(FrameworkContentElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(Suppress), true);
    }
    private DeckPage _page;
    private double _pageWidth = 760;
    private MediaSessions? _media;
    private MediaTrack? _mediaTrack;
    private IReadOnlyList<MediaTrack> _mediaTracks = [];
    private string? _pinnedMedia;
    private BitmapSource? _mediaCover;
    private Brush _musicAccent = UI.Brush(UI.Secondary);
    private readonly LyricsService _lyricsService = new();
    private Lyrics _lyrics = Lyrics.Empty;
    private CancellationTokenSource? _lyricsRequest;
    private string _lyricsKey = "", _musicRenderKey = "", _compactLyric = "";
    // Drives the equalizer, which must keep moving while the cursor moves; see PromptTimer.
    private PromptTimer _musicTick = null!;
    private DateTimeOffset _lastMusicTick = DateTimeOffset.UtcNow, _positionTick, _trackNoticeUntil, _trackNoticeArmedUntil;
    // QQ Music reports the next song while it is stopped and starts playing it up to 1.5 s later (measured
    // through its pipe). A song that starts within this window still shows its title; one started by hand later does not.
    private static readonly TimeSpan TrackNoticeStartWindow = TimeSpan.FromSeconds(5);
    private bool _musicStarted, _seeking, _updatingSeek, _commandBusy;
    private MediaTrack? _seekTrack;
    private readonly MediaSeekPreview _seekPreview = new();
    private Slider? _musicSeek;
    private readonly ScaleTransform _musicCoverScale = new(1, 1);
    internal MusicLayout? CurrentMusicLayout { get; private set; }
    private TextBlock? _musicElapsed, _musicDuration, _lyricPrevious, _lyricCurrent, _lyricNext, _musicMessage;
    private Grid? _musicInfo;
    private Canvas? _musicControls;
    private Button? _musicFullControl;
    private TextBlock? _musicSmtcHint;
    private readonly Dictionary<MediaCommand, (MusicControlButton Button, string Icon)> _musicControlButtons = new();
    private MusicEqualizer? _musicEqualizer, _compactEqualizer;
    private readonly double[] _flowLevels = new double[5];
    private MusicMarquee? _compactMarquee;
    private ClockFace? _compactClock;
    private QuotaCarousel? _quotaCarousel;
    private TimeSpan _quotaElapsed;
    internal bool IsMusicPage => _page == DeckPage.Music && Settings.MusicPage;
    internal CompactMusicLayout? CurrentCompactLayout { get; private set; }

    private void InitializeMusic()
    {
        _page = Settings.LastPage;
        _sourceMenuWatch = new(Dispatcher, TickSourceMenu) { Interval = TimeSpan.FromMilliseconds(40) };
        _musicTick = new(Dispatcher, TickMusic) { Interval = TimeSpan.FromMilliseconds(33) };
        IsVisibleChanged += (_, _) => UpdateMusicTimer();
    }
    private void StartMusic()
    {
        _musicStarted = true;
        ApplyMusicSettings();
    }
    private void ApplyMusicSettings()
    {
        if (!Settings.MusicPage) CloseSourceMenu();
        if (!Settings.MusicPage && _page == DeckPage.Music) _page = DeckPage.Usage;
        if (!Settings.UsagePage && Settings.MusicPage) _page = DeckPage.Music;
        if (!Settings.UsagePage || IsMusicPage) _details = false;
        if (_musicStarted && Settings.MediaWanted && _media is null)
        { _media = new MediaSessions(Dispatcher); _media.Changed += MediaChanged; _ = _media.StartAsync(); }
        if (!Settings.MediaWanted && _media is not null)
        { _media.Changed -= MediaChanged; _media.Dispose(); _media = null; _mediaTrack = null; _mediaCover = null; _mediaTracks = []; }
        if (!Settings.MediaWanted) _seekPreview.Observe(null, DateTimeOffset.UtcNow);
        UpdateLyrics();
        _musicRenderKey = "";
    }
    private void ApplyDefaultMusicPage()
    {
        var wanted = Settings.DefaultPage switch { DefaultDeckPage.Music => DeckPage.Music, DefaultDeckPage.Usage => DeckPage.Usage, _ => _page };
        if (wanted != _page && (wanted == DeckPage.Music ? Settings.MusicPage : Settings.UsagePage)) SelectPage(wanted, remember: false);
    }
    internal void SelectPage(DeckPage page, bool remember = true)
    {
        if (page == DeckPage.Music && !Settings.MusicPage || page == DeckPage.Usage && !Settings.UsagePage) return;
        bool changed = _page != page;
        if (changed) CloseSourceMenu();
        if (changed && _expanded && !_preview)
        {
            // Start before resizing: the shrinking surface can immediately fire MouseLeave.
            _hover.Stop(); _leave.Stop(); _pageSwitchHold.Stop(); _pageSwitchHold.Start();
        }
        _detailPopup?.Close(); _details = false; _page = page; _musicRenderKey = "";
        if (remember) { Settings.LastPage = page; _app.QueueSave(); }
        ApplySettings(animatePage: changed && _expanded && !_preview);
    }
    private void MusicPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Music);
    private void RenderPageSwitch()
    {
        SetValue(SuppressMusicToolTipsProperty, IsMusicPage);
        foreach (var control in new FrameworkElement[] { AIUsageButton, MusicPageButton, MediaSourceButton, SettingsButton, ClockLabel })
            ToolTipService.SetIsEnabled(control, !IsMusicPage);
        PageSwitch.Visibility = _details && !IsMusicPage ? Visibility.Collapsed : Visibility.Visible;
        AIUsageButton.Visibility = Settings.UsagePage ? Visibility.Visible : Visibility.Collapsed;
        MusicPageButton.Visibility = Settings.MusicPage ? Visibility.Visible : Visibility.Collapsed;
        AIUsageButton.Background = UI.Brush(!IsMusicPage ? UI.FillSelected : "Transparent");
        MusicPageButton.Background = UI.Brush(IsMusicPage ? UI.FillSelected : "Transparent");
        AIUsageButton.Foreground = UI.Brush(!IsMusicPage ? UI.Primary : UI.Secondary);
        MusicPageButton.Foreground = UI.Brush(IsMusicPage ? UI.Primary : UI.Secondary);
        ApplicationPages.Margin = new Thickness(Settings.MusicPage ? 82 : 46, 0, 0, 0);
        // With the entry switched on the source menu has one more row. Without a source that row stands alone in the button.
        bool entry = FullControlEntryShown, alone = entry && _mediaTrack is null;
        MediaSourceButton.Visibility = IsMusicPage && (_mediaTrack is not null || entry) ? Visibility.Visible : Visibility.Collapsed;
        MediaSourceButton.IsEnabled = !alone || _media?.RestartingNetease != true;
        // An open menu keeps its rows; only the entry's availability follows the restart.
        if (MediaSourceButton.ContextMenu is { IsOpen: true } open)
            foreach (var item in open.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "action"))) item.IsEnabled = _media?.RestartingNetease != true;
        AutomationProperties.SetName(MediaSourceButton, alone ? FullControlButtonLabel : Loc.T("切换播放来源", "Switch playback source"));
        MediaSourceLabel.Text = _mediaTrack is { } playing ? Loc.Label(playing.Source) : alone ? FullControlButtonLabel : "";
        MediaSourcePin.Visibility = _pinnedMedia is not null && _pinnedMedia == _mediaTrack?.Id ? Visibility.Visible : Visibility.Collapsed;
        MediaSourceChevron.Visibility = _mediaTrack is not null && (_mediaTracks.Count > 1 || entry) ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Style = (Style)FindResource(IsMusicPage ? (object)"PanelMusicSettingsButton" : typeof(Button));
        SettingsButton.Width = IsMusicPage ? 28 : 32; SettingsButton.Height = IsMusicPage ? 24 : 32;
        SettingsButton.FontSize = IsMusicPage ? 16 : 18;
    }
    private void MediaChanged()
    {
        if (_media is null) return;
        _pinnedMedia = _media.Pinned;
        ApplyMediaUpdate(_media.Current, _media.Tracks, _media.Cover);
    }
    private void ApplyMediaUpdate(MediaTrack? track, IReadOnlyList<MediaTrack> tracks, BitmapSource? cover)
    {
        bool changedSong = _mediaTrack?.Id != track?.Id || _mediaTrack is not null && track is not null && !SameMediaSong(_mediaTrack, track);
        bool lyricsChanged = changedSong || _mediaTrack?.SongKey != track?.SongKey || _mediaTrack?.SongId != track?.SongId || _mediaTrack?.EmbeddedLyrics != track?.EmbeddedLyrics;
        var old = _mediaTrack;
        _mediaTrack = track; _mediaTracks = tracks;
        _seekPreview.Observe(track, DateTimeOffset.UtcNow);
        bool coverChanged = _mediaCover != cover;
        if (coverChanged) { _mediaCover = cover; _musicAccent = MusicVisuals.Accent(_mediaCover); }
        var now = DateTimeOffset.UtcNow;
        if (changedSong)
        {
            _seeking = false; _seekTrack = null;
            // A notice still on screen switches to the new title instead of giving way to the summary until playback starts.
            _trackNoticeArmedUntil = _mediaTrack is not null && Settings.MusicTrackNotice ? now + TrackNoticeStartWindow : default;
        }
        if (_trackNoticeArmedUntil != default && _mediaTrack is { State: MediaState.Playing })
        {
            if (now <= _trackNoticeArmedUntil) _trackNoticeUntil = now.AddSeconds(4);
            _trackNoticeArmedUntil = default;
        }
        if (lyricsChanged) UpdateLyrics();
        if (changedSong || lyricsChanged || coverChanged || old?.State != _mediaTrack?.State || old?.HasTimeline != _mediaTrack?.HasTimeline) RenderCompact();
        if (IsMusicPage) RenderMusic();
        // Native lyric events must update compact text even while playback is paused.
        UpdateMusicPosition();
        UpdateMusicTimer();
    }
    // The expanded page and the compact text choose lyrics independently; either one is enough to look them up.
    private bool CompactLyrics => Settings.ShowsMusic(Settings.Style) && Settings.MusicText == CompactMusicText.Lyrics;
    private bool NeedsLyrics => Settings.MusicPage && Settings.LyricsEnabled || CompactLyrics;
    private string LyricsKey(MediaTrack? track) => NeedsLyrics && track is not null
        ? string.Join("\n", track.Id, track.SongKey, track.SongId, track.EmbeddedLyrics) : "";
    private async void UpdateLyrics()
    {
        string key = LyricsKey(_mediaTrack);
        if (_lyricsKey == key) return;
        _lyricsKey = key; _lyricsRequest?.Cancel(); _lyricsRequest?.Dispose(); _lyricsRequest = null;
        _lyrics = Lyrics.Empty; _compactLyric = ""; _musicRenderKey = "";
        if (key.Length == 0) return;
        var requested = _mediaTrack!;
        if (!requested.IsQqMusic && !string.IsNullOrWhiteSpace(requested.EmbeddedLyrics))
        {
            _lyrics = Lyrics.Parse(requested.EmbeddedLyrics, null, requested.Duration.TotalSeconds, requested.Source);
            if (IsMusicPage) RenderMusic(); RenderCompact(); return;
        }
        var request = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token); _lyricsRequest = request;
        try
        {
            var lyrics = await _lyricsService.FindAsync(requested, true, request.Token);
            if (request.IsCancellationRequested || _lyricsKey != key) return;
            _lyrics = lyrics; _musicRenderKey = "";
            if (IsMusicPage) RenderMusic(); RenderCompact();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Lyrics are optional; provider failures keep the no-lyrics layout. */ }
    }
    private void ToggleSourcePin(ContextMenu menu, MenuItem item, string id)
    {
        bool pin = !Equals(item.Tag, "pinned");
        _pinnedMedia = pin ? id : null;
        foreach (var other in menu.Items.OfType<MenuItem>().Where(other => !Equals(other.Tag, "action")))
        {
            other.Tag = pin && other == item ? "pinned" : null;
            if (pin) other.IsChecked = other == item;
        }
        _media?.Pin(_pinnedMedia);
        RenderPageSwitch();
    }
    internal static string FullControlEntryLabel => Loc.T("网易云音乐 · 完整控制", "NetEase Cloud Music · Full control");
    // Alone in the toolbar the entry has at most 150 DIP; the full English name would lose "Full control" to the ellipsis.
    internal static string FullControlButtonLabel => Loc.T("网易云音乐 · 完整控制", "NetEase full control");
    private bool FullControlEntryShown => Settings.NeteaseFullControlEntry && _media?.NeteaseInstalled == true;
    // Started from the source menu or the lone button. Only the disabled entry shows that it is under way;
    // neither progress nor a failure is reported, and a later click is ignored until it ends.
    private async Task StartNeteaseFullControl()
    {
        if (_media is not { RestartingNetease: false } media) return;
        var action = !media.NeteaseRunning ? NeteaseControlAction.Start : media.NeteaseFullControl ? NeteaseControlAction.Reconnect : NeteaseControlAction.Restart;
        var dialog = new NeteaseControlWindow(Settings.Theme, action) { Owner = this };
        if (dialog.ShowDialog() != true || _media != media || media.RestartingNetease) return;
        var pending = media.EnableFullControlAsync(quiet: true);
        _musicRenderKey = ""; RenderPageSwitch(); if (IsMusicPage) RenderMusic();
        await pending;
        if (_media != media) return;
        _musicRenderKey = ""; RenderPageSwitch(); if (IsMusicPage) RenderMusic();
    }
    private void MediaSource_Click(object sender, RoutedEventArgs e)
    {
        if (MediaSourceButton.ContextMenu?.IsOpen == true) { CloseSourceMenu(); return; }
        if (_mediaTrack is null) { if (FullControlEntryShown) _ = StartNeteaseFullControl(); return; }
        var menu = new ContextMenu { Style = (Style)FindResource("MediaSourceMenu") };
        menu.SetValue(SuppressMusicToolTipsProperty, true);
        foreach (var track in _mediaTracks)
        {
            var state = track.State switch { MediaState.Playing => Loc.T("播放中", "Playing"), MediaState.Paused => Loc.T("已暂停", "Paused"),
                MediaState.Stopped => Loc.T("已停止", "Stopped"), _ => Loc.T("状态未知", "Unknown state") };
            var label = $"{Loc.Label(track.Source)} · {track.Title} · {state}";
            // The menu's viewport clip would turn the row grayscale; the menu's background beneath it is opaque.
            var row = new Grid(); RenderOptions.SetClearTypeHint(row, ClearTypeHint.Enabled);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var source = new TextBlock { Text = Loc.Label(track.Source), FontSize = 12.5, Foreground = UI.Brush(UI.Primary), MaxWidth = 100,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 6, 0) };
            var title = new MusicMarquee { Text = track.Title, FontSize = 12, Foreground = UI.Brush("#80EBEBF5"),
                CenterWhenFits = false, IsHitTestVisible = false, Height = 28, Margin = new Thickness(0, 0, 12, 0) };
            var status = new TextBlock { Text = state, FontSize = 11, Foreground = UI.Brush(UI.Tertiary), TextWrapping = TextWrapping.NoWrap };
            foreach (var text in new FrameworkElement[] { source, title, status }) text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 1); Grid.SetColumn(status, 2); row.Children.Add(source); row.Children.Add(title); row.Children.Add(status);
            var item = new MenuItem { Style = (Style)FindResource("MediaSourceMenuItem"), Header = row, IsCheckable = true, IsChecked = track.Id == _mediaTrack?.Id };
            AutomationProperties.SetName(item, label);
            item.Tag = track.Id == _pinnedMedia ? "pinned" : _mediaTracks.Count < 2 ? "single" : null;
            // The pin button marks its click handled, so MenuItem neither selects the row nor closes the menu.
            item.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) => { e.Handled = true; ToggleSourcePin(menu, item, track.Id); }));
            item.Click += (_, _) => _media?.Select(track.Id); menu.Items.Add(item);
        }
        if (FullControlEntryShown)
        {
            menu.Items.Add(new Separator { Style = (Style)FindResource("MediaSourceMenuSeparator") });
            var label = new TextBlock { Text = FullControlEntryLabel, FontSize = 12.5, Foreground = UI.Brush(UI.Primary), TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var entry = new MenuItem { Style = (Style)FindResource("MediaSourceMenuItem"), Header = label, Tag = "action", IsEnabled = _media?.RestartingNetease != true };
            AutomationProperties.SetName(entry, FullControlEntryLabel);
            entry.Click += (_, _) => { CloseSourceMenu(); _ = StartNeteaseFullControl(); }; menu.Items.Add(entry);
        }
        WatchSourceMenu(menu);
        MediaSourceButton.ContextMenu = menu; menu.PlacementTarget = MediaSourceButton; menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = 6; ResizeSourceMenu(menu); menu.IsOpen = true;
    }
    // Guidance shown in place of the transport when Netease runs without SMTC. Both sentences stay at every size:
    // what is required and where to switch it on. Only the type size and the entry's label adapt to the row.
    internal static string SmtcHintWhy => Loc.T("开启网易云的 SMTC 后，才能在这里控制播放", "Turn on SMTC in NetEase to control playback here");
    // The English hint names the menu items as NetEase Cloud Music's Chinese interface shows them.
    internal static string SmtcHintHow => Loc.T("位置：网易云音乐 › 设置 › 系统 › 开启 SMTC", "Where: NetEase Cloud Music › 设置 › 系统 › 开启 SMTC");
    private void RenderMusic()
    {
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double h = ContentCanvas.Height, w = ContentCanvas.Width;
        var track = _mediaTrack;
        if (_seeking && _seekTrack is { } seeking && track is not null && SameMediaSong(seeking, track))
        { UpdateMusicPosition(); return; }
        bool playerLyrics = track?.IsQqMusic == true && _lyrics.Lines.Count == 0 && !_lyrics.Instrumental;
        bool hasPlayerLyric = playerLyrics && Settings.LyricsEnabled && !string.IsNullOrWhiteSpace(track?.CurrentLyric) && track?.State != MediaState.Stopped;
        bool hasLyrics = playerLyrics ? hasPlayerLyric : (_lyrics.Lines.Count > 0 || _lyrics.Instrumental) && Settings.LyricsEnabled;
        bool logMode = track?.IsNeteaseLog == true;
        bool showSmtcHint = logMode && track is { CanToggle: false, CanPrevious: false, CanNext: false }
            && _media?.RestartingNetease != true && _media?.Error is null;
        string message = _media?.RestartingNetease == true ? Loc.T("正在重启网易云…", "Restarting NetEase Cloud Music…") : _media?.Error ?? "";
        var modes = track is null ? [] : MusicPlaybackModes.Supported(track);
        var layout = MusicLayout.Calculate(w, h, hasLyrics, track?.HasTimeline == true, modes.Count > 0, message.Length > 0, logMode, transport: !showSmtcHint);
        CurrentMusicLayout = layout;
        var c = layout.Tier;
        ToolbarRow.Height = new GridLength(c.Toolbar);
        bool animate = Settings.Animations && SystemParameters.ClientAreaAnimation;
        string key = $"{track?.Id}|{track?.SongKey}|{logMode}|{_media?.RestartingNetease}|{track?.State}|{track?.CanToggle}|{track?.CanPrevious}|{track?.CanNext}|{track?.CanSeek}|{track?.CanRepeat}|{track?.CanShuffle}|{track?.Shuffle}|{track?.Repeat}|{track?.HasTimeline}|{playerLyrics}|{hasPlayerLyric}|{w}|{h}|{_lyrics.GetHashCode()}|{_mediaCover?.GetHashCode()}|{_media?.Error}|{Settings.LyricsEnabled}|{animate}";
        if (_musicRenderKey == key) { UpdateMusicPosition(); return; }
        _musicRenderKey = key;
        // Keep the transport layer attached: replacing a hovered button restarts
        // hit testing and its fade-in on every metadata, artwork or lyric update.
        foreach (var child in MusicContent.Children.Cast<UIElement>().Where(child => child != _musicControls).ToArray())
            MusicContent.Children.Remove(child);
        _musicSeek = null; _musicElapsed = null; _musicDuration = null; _musicEqualizer = null; _lyricCurrent = null; _lyricPrevious = null; _lyricNext = null; _musicMessage = null; _musicInfo = null;
        MusicContent.Margin = new Thickness(c.Side, c.Top, c.Side, c.Bottom);
        if (track is null)
        {
            MusicContent.Children.Clear(); _musicControls = null; _musicFullControl = null; _musicSmtcHint = null; _musicControlButtons.Clear();
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = Math.Max(200, w - 60) };
            var icon = MusicVisuals.Icon("music", 26); icon.Margin = new Thickness(0, 0, 0, 8); empty.Children.Add(icon);
            empty.Children.Add(UI.Fixed(Loc.T("没有正在播放的媒体", "Nothing is playing"), 14, 22, UI.Secondary));
            if (_media?.Error is { } error)
            {
                var hint = UI.Line(error, 11.5, UI.Tertiary); hint.TextWrapping = TextWrapping.Wrap; hint.TextAlignment = TextAlignment.Center; empty.Children.Add(hint);
            }
            MusicContent.Children.Add(empty); return;
        }
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Cover) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c.Gap) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var art = MusicVisuals.Cover(_mediaCover, layout.Cover, c.Radius, outlined: true);
        art.VerticalAlignment = VerticalAlignment.Top; art.Margin = new Thickness(0, layout.CoverY - layout.BodyY, 0, 0);
        art.RenderTransformOrigin = new Point(.5, .5); art.RenderTransform = _musicCoverScale;
        MusicControlButton.Animate(_musicCoverScale, ScaleTransform.ScaleXProperty, track.State == MediaState.Paused ? .94 : 1, animate, 250);
        MusicControlButton.Animate(_musicCoverScale, ScaleTransform.ScaleYProperty, track.State == MediaState.Paused ? .94 : 1, animate, 250);
        AutomationProperties.SetName(art, Loc.T("专辑封面", "Album cover")); body.Children.Add(art);
        var right = new Grid(); Grid.SetColumn(right, 2); body.Children.Add(right);
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.HeaderHeight) });
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(c.Lift + c.Play +
            (layout.ProgressHeight > 0 ? c.ProgressGap + layout.ProgressHeight : 0)) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(43) });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var title = UI.Fixed(track.Title, c.Title, c.TitleLine, UI.Primary, FontWeights.SemiBold);
        AutomationProperties.SetName(title, Loc.T("歌名 ", "Title ") + track.Title); titles.Children.Add(title);
        if (layout.MergeTitle)
        {
            if (track.Artist.Length > 0) title.Inlines.Add(new Run(" · " + track.Artist) { Foreground = UI.Brush(UI.Secondary), FontSize = c.Artist, FontWeight = FontWeights.Normal });
        }
        else titles.Children.Add(UI.Fixed(string.Join(" · ", new[] { track.Artist, track.Album }.Where(s => s.Length > 0)), c.Artist, c.ArtistLine, UI.Secondary));
        header.Children.Add(titles);
        _musicEqualizer = new MusicEqualizer { Width = 27, Height = c.Equalizer, Color = _musicAccent, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, (c.TitleLine - c.Equalizer) / 2, 0, 0) };
        Grid.SetColumn(_musicEqualizer, 1); header.Children.Add(_musicEqualizer); right.Children.Add(header);

        var middle = new Grid { Margin = new Thickness(0, 0, 0, c.MiddleGap) }; Grid.SetRow(middle, 1); right.Children.Add(middle); _musicInfo = middle;
        bool synchronized = playerLyrics || _lyrics.CanSynchronize(track);
        int lyricLines = playerLyrics ? Math.Min(1, layout.LyricLines) : layout.LyricLines;
        if (hasLyrics && lyricLines > 0 && synchronized)
        {
            var lyrics = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (lyricLines == 3) { _lyricPrevious = UI.Fixed("", c.LyricSide, c.LyricLine, UI.Tertiary); lyrics.Children.Add(_lyricPrevious); }
            _lyricCurrent = UI.Fixed("", c.Lyric, c.LyricLine, UI.Primary); lyrics.Children.Add(_lyricCurrent);
            if (lyricLines == 3) { _lyricNext = UI.Fixed("", c.LyricSide, c.LyricLine, UI.Tertiary); lyrics.Children.Add(_lyricNext); }
            lyrics.Visibility = layout.ShowMessage ? Visibility.Collapsed : Visibility.Visible; middle.Children.Add(lyrics);
        }
        else if (hasLyrics && lyricLines > 0)
        {
            var text = UI.Line(_lyrics.Instrumental && _lyrics.Lines.Count == 0 ? "♪" : string.Join("\n", _lyrics.Lines.Select(line => line.Text)), c.Lyric, UI.Secondary);
            text.TextWrapping = TextWrapping.Wrap; text.LineHeight = c.LyricLine; text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            var lyrics = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalAlignment = VerticalAlignment.Center, Height = lyricLines * c.LyricLine, CanContentScroll = false, Visibility = layout.ShowMessage ? Visibility.Collapsed : Visibility.Visible };
            // The viewport clip would turn the lyrics grayscale; they lie over the island's opaque black surface.
            RenderOptions.SetClearTypeHint(text, ClearTypeHint.Enabled);
            AutomationProperties.SetName(lyrics, Loc.T("歌词", "Lyrics")); middle.Children.Add(lyrics);
        }
        if (c != MusicTier.Compact && layout.MiddleHeight >= c.LyricLine)
        {
            _musicMessage = UI.Fixed(message, 11.5, c.LyricLine, UI.Tertiary);
            _musicMessage.Visibility = layout.ShowMessage ? Visibility.Visible : Visibility.Collapsed; middle.Children.Add(_musicMessage);
        }
        var footer = new Grid(); Grid.SetRow(footer, 2); right.Children.Add(footer);
        if (track.HasTimeline)
        {
            var progress = new Grid { Height = c.ProgressRow, VerticalAlignment = VerticalAlignment.Top };
            progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            progress.ColumnDefinitions.Add(new ColumnDefinition());
            progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            _musicElapsed = UI.Fixed("", c.Time, c.ProgressRow, UI.Secondary); progress.Children.Add(_musicElapsed);
            _musicDuration = UI.Fixed("", c.Time, c.ProgressRow, UI.Secondary); _musicDuration.TextAlignment = TextAlignment.Right; Grid.SetColumn(_musicDuration, 2); progress.Children.Add(_musicDuration);
            _musicSeek = new MusicSeekSlider { Style = (Style)FindResource("MusicSeek"), RestHeight = c.Bar, HoverHeight = c.BarHover, BarHeight = c.Bar,
                Accent = ((SolidColorBrush)_musicAccent).Color, DurationSeconds = track.Duration.TotalSeconds, Height = c.ProgressRow, AnimationsEnabled = animate, IsEnabled = track.CanSeek };
            AutomationProperties.SetName(_musicSeek, Loc.T("播放进度", "Playback position")); Grid.SetColumn(_musicSeek, 1); progress.Children.Add(_musicSeek);
            // Slider handles move-to-point before instance handlers; observe that handled event too.
            _musicSeek.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((sender, _) =>
            {
                var seek = (Slider)sender; BeginSeek(seek);
                var sliderTrack = seek.Template.FindName("PART_Track", seek) as System.Windows.Controls.Primitives.Track;
                // Track clicks do not capture in WPF. Keep the release even if it is outside the bar.
                if (_seeking && Mouse.LeftButton == MouseButtonState.Pressed && sliderTrack?.Thumb.IsMouseOver != true) seek.CaptureMouse();
            }), true);
            _musicSeek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((sender, _) => BeginSeek((Slider)sender)), true);
            _musicSeek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(async (_, e) =>
            {
                if (e.Canceled) { _seeking = false; _seekTrack = null; if (_musicSeek is MusicSeekSlider slider) slider.Seeking = false; UpdateMusicPosition(); }
                else await CommitSeek();
            }), true);
            _musicSeek.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, _) => await CommitSeek()), true);
            _musicSeek.LostMouseCapture += async (_, _) => { if (_seeking && _musicSeek?.IsMouseCaptureWithin == false) await CommitSeek(); };
            _musicSeek.ValueChanged += (_, _) => { if (!_updatingSeek && _seeking) UpdateSeekLabels(_musicSeek.Value); };
            _musicSeek.PreviewKeyUp += async (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageDown or Key.PageUp) { _seekTrack = _mediaTrack; _seeking = true; await CommitSeek(); } };
            footer.Children.Add(progress);
        }
        if (_musicControls is null)
        {
            _musicControls = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            MusicContent.Children.Add(_musicControls);
        }
        var controls = _musicControls;
        controls.Width = layout.ColumnWidth; controls.Height = c.Play;
        controls.Margin = new Thickness(layout.ColumnX - c.Side, layout.ControlsY - layout.BodyY, 0, 0);
        var visibleControls = new HashSet<MediaCommand>();
        void Add(MediaCommand id, string name, string glyph, double size, double x, bool enabled, Func<Task> command, bool mode = false, bool single = false)
        {
            visibleControls.Add(id);
            if (!_musicControlButtons.TryGetValue(id, out var slot))
            {
                var created = new MusicControlButton { Style = (Style)FindResource("MusicControl") };
                created.Click += async (_, _) => { if (created.Tag is Func<Task> execute) await execute(); };
                slot = (created, ""); controls.Children.Add(created);
            }
            var button = slot.Button;
            string iconKey = $"{glyph}|{size}|{mode}|{single}";
            if (slot.Icon != iconKey)
            {
                UIElement icon = MusicVisuals.Icon(glyph, size, UI.Brush(mode ? UI.Secondary : UI.Primary));
                if (single)
                {
                    var container = new Grid { Width = size, Height = size }; container.Children.Add(icon);
                    var singleLabel = UI.CenteredSymbol("1", c.Mode <= 14 ? 7 : c.Mode >= 22 ? 10 : 9, UI.Secondary);
                    singleLabel.HorizontalAlignment = HorizontalAlignment.Center;
                    container.Children.Add(singleLabel); icon = container;
                }
                button.Content = icon;
            }
            _musicControlButtons[id] = (button, iconKey);
            button.Width = size + 16; button.Height = size + 16;
            button.IsEnabled = enabled; button.AnimationsEnabled = animate; button.Tag = command;
            Canvas.SetLeft(button, x - layout.ColumnX - 8); Canvas.SetTop(button, (c.Play - size) / 2 - 8);
            AutomationProperties.SetName(button, name);
        }
        if (modes.Count > 0)
        {
            var mode = MusicPlaybackModes.Current(track);
            Add(MediaCommand.Repeat, Loc.T("播放模式 · ", "Playback mode · ") + MusicPlaybackModes.Label(mode), mode == MusicPlaybackMode.Shuffle ? "shuffle" : mode == MusicPlaybackMode.Order ? "sequential" : "repeat",
                c.Mode, layout.ModeX, true, () => CyclePlaybackMode(track), true, mode == MusicPlaybackMode.Track);
        }
        // The full-control entry never moves the transport. It shortens its label step by step to the room it is given.
        double EntryWidth(string label)
        {
            var probe = new TextBlock { Text = label, FontFamily = UI.TextFont, FontSize = c.Time };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(probe.DesiredSize.Width) + MusicLayout.FullControlChrome;
        }
        string? entryLabel = null; double entryWidth = 0;
        if (showSmtcHint)
        {
            // Without SMTC the transport cannot do anything, so its row carries the explanation instead of three dead glyphs.
            // The wording is fixed; the row is fitted by trying the full entry label first, then smaller type, then the short label.
            if (_musicSmtcHint is null)
            {
                _musicSmtcHint = new TextBlock { FontFamily = UI.TextFont, TextWrapping = TextWrapping.Wrap, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
                AutomationProperties.SetName(_musicSmtcHint, Loc.T("SMTC 提示", "SMTC hint")); controls.Children.Add(_musicSmtcHint);
            }
            var hint = _musicSmtcHint; hint.Visibility = Visibility.Visible; hint.Inlines.Clear();
            hint.Inlines.Add(new Run(SmtcHintWhy) { Foreground = UI.Brush(UI.Tertiary) });
            hint.Inlines.Add(new LineBreak());
            hint.Inlines.Add(new Run(SmtcHintHow) { Foreground = UI.Brush(UI.Secondary) });
            double[] sizes = c == MusicTier.Compact ? [10.5, 10, 9.5] : [11.5, 11, 10.5, 10];
            bool fitted = false;
            foreach (var label in layout.ShowFullControl ? MusicLayout.FullControlLabels : [""])
            {
                double reserve = label.Length > 0 ? EntryWidth(label) : 0;
                foreach (double size in sizes)
                {
                    double width = layout.ColumnWidth - (reserve > 0 ? reserve + MusicLayout.FullControlGap : 0);
                    hint.FontSize = size; hint.LineHeight = size >= 11 ? 14 : size >= 10.5 ? 13 : 12; hint.Width = width;
                    hint.Measure(new Size(width, double.PositiveInfinity));
                    if (hint.DesiredSize.Height <= c.Play + 2) { fitted = true; break; }
                }
                if (label.Length > 0) { entryLabel = label; entryWidth = reserve; }
                if (fitted) break;
            }
            Canvas.SetLeft(hint, 0); Canvas.SetTop(hint, Math.Floor((c.Play - hint.DesiredSize.Height) / 2));
        }
        else
        {
            if (_musicSmtcHint is not null) _musicSmtcHint.Visibility = Visibility.Collapsed;
            double gx = layout.TripletX;
            Add(MediaCommand.Previous, Loc.T("上一首", "Previous"), "previous", c.Skip, gx, track.CanPrevious, () => RunMediaCommand(track, MediaCommand.Previous)); gx += c.Skip + c.ControlGap;
            Add(MediaCommand.PlayPause, track.State == MediaState.Playing ? Loc.T("暂停", "Pause") : Loc.T("播放", "Play"), track.State == MediaState.Playing ? "pause" : "play", c.Play, gx, track.CanToggle, () => RunMediaCommand(track, MediaCommand.PlayPause)); gx += c.Play + c.ControlGap;
            Add(MediaCommand.Next, Loc.T("下一首", "Next"), "next", c.Skip, gx, track.CanNext, () => RunMediaCommand(track, MediaCommand.Next));
            if (layout.ShowFullControl)
                foreach (var label in MusicLayout.FullControlLabels)
                    if ((entryWidth = EntryWidth(label)) <= layout.FullControlRoom) { entryLabel = label; break; }
        }
        foreach (var (id, slot) in _musicControlButtons)
            slot.Button.Visibility = visibleControls.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
        if (entryLabel is not null)
        {
            if (_musicFullControl is null)
            {
                _musicFullControl = new Button { Style = (Style)FindResource("PanelTextButton"), FontFamily = UI.TextFont, Padding = new Thickness(6, 2, 6, 2),
                    Height = 26, HorizontalContentAlignment = HorizontalAlignment.Right };
                AutomationProperties.SetName(_musicFullControl, MusicLayout.FullControlLabels[0]);
                _musicFullControl.Click += async (_, _) => await EnableNeteaseControl(); controls.Children.Add(_musicFullControl);
            }
            var enable = _musicFullControl; enable.IsEnabled = _media?.RestartingNetease != true;
            // The app-wide TextBlock style wraps; pixel snapping must never push the last character to a second line.
            enable.Width = entryWidth;
            enable.Content = new TextBlock { Text = entryLabel, FontFamily = UI.TextFont, FontSize = c.Time, TextWrapping = TextWrapping.NoWrap };
            Canvas.SetLeft(enable, layout.ColumnWidth - entryWidth); Canvas.SetTop(enable, (c.Play - 26) / 2);
        }
        if (_musicFullControl is not null) _musicFullControl.Visibility = entryLabel is not null ? Visibility.Visible : Visibility.Collapsed;
        MusicContent.Children.Insert(0, body); UpdateMusicPosition();
    }
    private async Task CyclePlaybackMode(MediaTrack track)
    {
        if (_commandBusy || _mediaTrack?.Id != track.Id || _media is null) return;
        _commandBusy = true;
        try
        {
            var next = MusicPlaybackModes.Next(_mediaTrack);
            if (!await _media.SetPlaybackModeAsync(track.Id, next)) ShowMusicError(Loc.T("播放器未接受此操作，请在原应用中重试。", "The player did not accept this action. Try again in the player."));
            await _media.RefreshAsync();
        }
        finally { _commandBusy = false; }
    }
    private async Task EnableNeteaseControl()
    {
        if (_media?.CanEnableFullControl != true || _media.RestartingNetease) return;
        var dialog = new NeteaseControlWindow(Settings.Theme) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var media = _media;
        var pending = media.EnableFullControlAsync();
        _musicRenderKey = ""; RenderMusic();
        bool succeeded = await pending;
        if (_media != media) return;
        await media.RefreshAsync(); _musicRenderKey = ""; RenderMusic();
        if (!succeeded) ShowMusicError(media.Error ?? Loc.T("未能启用完整控制，请重试。", "Full control could not be enabled. Try again."));
    }

    private async Task RunMediaCommand(MediaTrack track, MediaCommand command)
    {
        if (_commandBusy || _mediaTrack?.Id != track.Id) return;
        _commandBusy = true;
        try { if (_media is not null && !await _media.CommandAsync(track.Id, command)) ShowMusicError(Loc.T("播放器未接受此操作，请在原应用中重试。", "The player did not accept this action. Try again in the player.")); }
        finally { _commandBusy = false; }
    }
    private void BeginSeek(Slider seek)
    {
        if (seek != _musicSeek || _mediaTrack is not { CanSeek: true, HasTimeline: true }) return;
        if (!_seeking) { _seeking = true; _seekTrack = _mediaTrack; }
        if (seek is MusicSeekSlider musicSeek) musicSeek.Seeking = true;
        UpdateSeekLabels(seek.Value);
    }
    private async Task CommitSeek()
    {
        if (!_seeking || _musicSeek is null) return;
        var track = _seekTrack; double value = _musicSeek.Value; _seeking = false; _seekTrack = null;
        if (_musicSeek is MusicSeekSlider musicSeek) musicSeek.Seeking = false;
        if (_musicSeek.IsMouseCaptured) _musicSeek.ReleaseMouseCapture();
        if (track is null || track.Id != _mediaTrack?.Id || !SameMediaSong(track, _mediaTrack)) return;
        if (_media is not null)
        {
            var request = _seekPreview.Begin(track, value, DateTimeOffset.UtcNow);
            UpdateMusicPosition();
            if (!await _media.SeekAsync(track, value) && _seekPreview.Cancel(request))
            {
                UpdateMusicPosition();
                ShowMusicError(Loc.T("播放器未接受进度调整。", "The player did not accept the new position."));
            }
        }
        RenderMusic();
    }
    private static bool SameMediaSong(MediaTrack first, MediaTrack second) => MediaRouting.SameSong(first, second) &&
        (string.IsNullOrEmpty(first.SongId) || string.IsNullOrEmpty(second.SongId) || first.SongId == second.SongId);
    private void ShowMusicError(string error)
    {
        if (_musicMessage is null) return;
        _musicMessage.Text = error; _musicMessage.Visibility = Visibility.Visible;
        if (_musicInfo is not null) foreach (UIElement child in _musicInfo.Children) child.Visibility = ReferenceEquals(child, _musicMessage) ? Visibility.Visible : Visibility.Collapsed;
    }
    private static string MusicTime(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    private void UpdateSeekLabels(double fraction)
    {
        if (_mediaTrack is not { HasTimeline: true } track || _musicElapsed is null || _musicDuration is null) return;
        var position = TimeSpan.FromSeconds(track.Duration.TotalSeconds * fraction);
        _musicElapsed.Text = MusicTime(position);
        _musicDuration.Text = MusicTime(track.Duration);
        AutomationProperties.SetName(_musicElapsed, Loc.T("已播放 ", "Elapsed ") + _musicElapsed.Text);
        AutomationProperties.SetName(_musicDuration, Loc.T("总时长 ", "Duration ") + _musicDuration.Text);
    }
    private void UpdateMusicPosition()
    {
        if (_mediaTrack is not { } track || track.State == MediaState.Stopped) { MusicIndicator.Visibility = Visibility.Collapsed; return; }
        var position = _seekPreview.Position(track, DateTimeOffset.UtcNow);
        // The expanded page's controls stay in the tree while collapsed; they are brought up to date when the panel opens.
        bool page = _expanded && IsMusicPage;
        if (page && !_seeking && _musicSeek is not null) { _updatingSeek = true; _musicSeek.Value = track.Duration > TimeSpan.Zero ? position.TotalSeconds / track.Duration.TotalSeconds : 0; _updatingSeek = false; UpdateSeekLabels(_musicSeek.Value); }
        var line = CurrentLyrics(track);
        if (page && _lyricCurrent is not null) _lyricCurrent.Text = line.Current;
        if (page && _lyricPrevious is not null) _lyricPrevious.Text = line.Previous;
        if (page && _lyricNext is not null) _lyricNext.Text = line.Next;
        if (CompactLyrics && _compactLyric != line.Current)
        {
            _compactLyric = line.Current;
            if (_trackNoticeUntil <= DateTimeOffset.UtcNow)
            {
                if (_compactMarquee is not null) _compactMarquee.Text = line.Current.Length > 0 ? line.Current : track.Caption;
                else RenderCompact();
            }
        }
        MusicIndicator.Visibility = !_expanded && EffectiveStyle == CompactStyle.Line && Settings.MusicIndicatorProgress && track.State != MediaState.Stopped && track.HasTimeline ? Visibility.Visible : Visibility.Collapsed;
        MusicIndicator.Width = CompactWidth(CompactStyle.Line) * (track.HasTimeline ? position.TotalSeconds / track.Duration.TotalSeconds : 0);
    }
    private (string Previous, string Current, string Next) CurrentLyrics(MediaTrack track)
    {
        if (!NeedsLyrics || track.State == MediaState.Stopped) return ("", "", "");
        if (track.IsQqMusic && _lyrics.Lines.Count == 0 && !_lyrics.Instrumental) return ("", track.CurrentLyric ?? "", "");
        return _lyrics.At(track, DateTimeOffset.UtcNow);
    }
    private const double CompactClockGap = 8;
    // The color taken from the playing song's cover; the fallback brush used without a cover is not opaque.
    internal Color? CoverColor => _mediaCover is not null && _mediaTrack is { State: not MediaState.Stopped } && _musicAccent is SolidColorBrush { Color.A: 255 } accent ? accent.Color : null;
    private void RenderCompactMusic()
    {
        double contentWidth = Math.Max(0, CompactWidth(EffectiveStyle) - (EffectiveStyle == CompactStyle.Notch ? 60 : 32));
        bool media = Settings.ShowsMusic(EffectiveStyle) && _mediaTrack is { State: not MediaState.Stopped };
        bool notice = media && _trackNoticeUntil > DateTimeOffset.UtcNow;
        string text = "";
        if (media)
        {
            if (notice || Settings.MusicText == CompactMusicText.Title) text = _mediaTrack!.Caption;
            else if (Settings.MusicText == CompactMusicText.Lyrics)
            {
                text = CurrentLyrics(_mediaTrack!).Current;
                _compactLyric = text;
                if (text.Length == 0) text = _mediaTrack!.Caption;
            }
        }
        var apps = Settings.ShowsSummary(EffectiveStyle) && !notice ? Settings.EnabledApps : [];
        // The clock keeps its own width at the right; the rest goes to the cover, rings, text and equalizer as before.
        // Shown alone, it is centred.
        var clock = Settings.ShowsClock(EffectiveStyle) ? new ClockFace(Settings.ClockStyle, Settings.Clock24Hour, ClockFace.Palette(Settings, CoverColor), DateTime.Now) : null;
        bool clockAlone = clock is not null && !media && apps.Count == 0;
        double clockRoom = clock is not null && !clockAlone ? clock.ReservedWidth + CompactClockGap : 0;
        var layout = CompactMusicLayout.Calculate(Math.Max(0, contentWidth - clockRoom), media, apps.Count, text.Length > 0); CurrentCompactLayout = layout;
        var grid = new Grid { Width = contentWidth, Height = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(media ? 28 : 0) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(media ? 26 : 0) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(clockRoom) });
        if (clock is not null)
        {
            clock.VerticalAlignment = VerticalAlignment.Center;
            clock.HorizontalAlignment = clockAlone ? HorizontalAlignment.Center : HorizontalAlignment.Right;
            if (clockAlone) Grid.SetColumnSpan(clock, 4); else Grid.SetColumn(clock, 3);
            grid.Children.Add(clock); _compactClock = clock;
        }
        if (media)
        {
            var cover = MusicVisuals.Cover(_mediaCover, 20, 4); cover.HorizontalAlignment = HorizontalAlignment.Left; cover.VerticalAlignment = VerticalAlignment.Center; AutomationProperties.SetName(cover, _mediaTrack!.Caption); grid.Children.Add(cover);
            _compactEqualizer = new MusicEqualizer { Bars = 4, Width = 18, Height = 14, Color = Settings.MusicCoverColor ? _musicAccent : UI.Brush(UI.Secondary), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(_compactEqualizer, 2); grid.Children.Add(_compactEqualizer);
        }
        var middle = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(middle, 1); grid.Children.Add(middle);
        if (layout.VisibleRings > 0)
        {
            var items = apps.Select(entry =>
            {
                var snapshot = DashboardUsage.Quota(entry, Snapshots);
                var percentages = snapshot.Metrics.Where(m => m.UsedPercent is not null).Select(m => Math.Clamp(m.UsedPercent!.Value, 0, 100)).ToArray();
                double used = percentages.Length > 0 ? percentages.Max() : snapshot.Metrics.Count > 0 ? 100 : 0;
                string tip = entry.Name + " · " + (percentages.Length > 0 ? Loc.T($"已用 {used:0.#}%", $"{used:0.#}% used")
                    : snapshot.Metrics.Count > 0 ? string.Join(Loc.T("；", "; "), snapshot.Metrics.Select(m => m.ValueText.Length > 0 ? m.ValueText + " " + m.ValueSuffix : Loc.Label(m.Label))) : snapshot.StatusLabel);
                return (used, entry.Theme, tip);
            }).ToArray();
            _quotaCarousel = new QuotaCarousel(items, layout, _quotaElapsed, Settings.Animations && SystemParameters.ClientAreaAnimation) { VerticalAlignment = VerticalAlignment.Center }; middle.Children.Add(_quotaCarousel);
        }
        if (layout.TextWidth > 0)
        { _compactMarquee = new MusicMarquee { Text = text, Width = layout.TextWidth, Height = 22, Backdrop = Colors.Black, Margin = new Thickness(layout.VisibleRings > 0 ? 8 : 0, 0, 0, 0) }; middle.Children.Add(_compactMarquee); }
        CompactRings.Children.Add(grid); UpdateMusicTimer();
    }
    private void UpdateMusicTimer()
    {
        bool musicVisible = MusicPresentationVisible;
        _media?.SetPresentationActive(musicVisible);
        bool progressVisible = !_expanded && EffectiveStyle == CompactStyle.Line && Settings.MusicIndicatorProgress && _mediaTrack is { HasTimeline: true, State: not MediaState.Stopped };
        bool animations = Settings.Animations && SystemParameters.ClientAreaAnimation;
        // A paused song rests at the start of its line; an endless scroll would keep the window composing for hours.
        _compactMarquee?.SetScrolling(animations && MusicAudioActive && IsVisible && !IsSuppressed && !_expanded && _alert is null && EffectiveStyle != CompactStyle.Line);
        UpdateQuotaCarousel();
        bool needed = IsVisible && !IsSuppressed && (MusicAudioActive && (musicVisible || progressVisible) || _trackNoticeUntil > DateTimeOffset.UtcNow);
        // Frame-rate ticks only drive visible equalizer bars; the line indicator's progress moves a pixel every few seconds.
        _musicTick.Interval = TimeSpan.FromMilliseconds(animations && MusicAudioActive && musicVisible ? 33 : 250);
        if (needed) { if (!_musicTick.IsEnabled) { _lastMusicTick = DateTimeOffset.UtcNow; _musicTick.Start(); } }
        else _musicTick.Stop();
        if (_expanded || EffectiveStyle != CompactStyle.Line || !Settings.MusicIndicatorProgress || _mediaTrack is not { HasTimeline: true, State: not MediaState.Stopped }) MusicIndicator.Visibility = Visibility.Collapsed;
    }
    // The carousel runs its own timer and WPF animation; it only needs to know when it may move.
    private void UpdateQuotaCarousel()
        => _quotaCarousel?.SetRunning(IsVisible && !IsSuppressed && !_expanded && _alert is null, _pointerInside);
    private bool MusicPresentationVisible => IsVisible && !IsSuppressed && _mediaTrack is { State: not MediaState.Stopped }
        && (_expanded ? IsMusicPage : Settings.ShowsMusic(EffectiveStyle) && _alert is null);
    private bool MusicAudioActive => _mediaTrack is { State: MediaState.Playing };
    private void TickMusic()
    {
        var now = DateTimeOffset.UtcNow; double seconds = Math.Min(.5, (now - _lastMusicTick).TotalSeconds); _lastMusicTick = now;
        bool animations = Settings.Animations && SystemParameters.ClientAreaAnimation;
        bool playing = animations && MusicAudioActive;
        bool musicVisible = MusicPresentationVisible;
        _media?.SetPresentationActive(musicVisible);
        float peak = musicVisible ? _media?.Peak() ?? 0 : 0;
        // Captured audio drives the flowing bars; the session peak is the fallback.
        bool flowing = musicVisible && _media?.FlowLevels(_flowLevels) == true;
        if (_expanded && IsMusicPage) { if (flowing) _musicEqualizer?.Show(_flowLevels, playing); else _musicEqualizer?.Tick(peak, playing, seconds); }
        if (!_expanded && _alert is null) { if (flowing) _compactEqualizer?.Show(_flowLevels, playing); else _compactEqualizer?.Tick(peak, playing, seconds); }
        if (_trackNoticeUntil != default && now >= _trackNoticeUntil) { _trackNoticeUntil = default; RenderCompact(); }
        if ((now - _positionTick).TotalMilliseconds >= 250) { _positionTick = now; UpdateMusicPosition(); }
    }
    private void DisposeMusic() { CloseSourceMenu(); _musicTick.Stop(); _lyricsRequest?.Cancel(); _media?.Dispose(); _lyricsService.Dispose(); }
}
