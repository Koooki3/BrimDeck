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
    private double _pageWidth = 760, _usageWidth = 760, _widestPage = 760;
    private MediaSessions? _media => _app.Media;
    private MediaTrack? _mediaTrack;
    private IReadOnlyList<MediaTrack> _mediaTracks = [];
    private string? _pinnedMedia;
    private BitmapSource? _mediaCover;
    private Brush _musicAccent = MusicVisuals.Accent(null);
    // The color the cover gave, if it gave one; _musicAccent is the theme's equalizer color otherwise.
    private Color? _coverAccent;
    private Lyrics _lyrics = Lyrics.Empty;
    private CancellationTokenSource? _lyricsRequest;
    // Completed when Apple Music writes a lyric file while the current Apple Music song has no lyrics of Apple's.
    private TaskCompletionSource? _appleLyricsWritten;
    private string _lyricsKey = "", _musicRenderKey = "", _compactLyric = "";
    // Drives the equalizer, which must keep moving while the cursor moves; see PromptTimer.
    private PromptTimer _musicTick = null!;
    private DateTimeOffset _lastMusicTick = DateTimeOffset.UtcNow, _positionTick, _trackNoticeUntil;
    private readonly TrackNoticeGate _trackNotice = new();
    private bool _musicStarted, _seeking, _updatingSeek, _commandBusy;
    private MediaTrack? _seekTrack;
    private readonly MediaSeekPreview _seekPreview = new();
    // Keeps the progress bar's room while the player moves to its next song; the timer lays the page out again when it ends.
    private readonly MusicTimelineHold _timelineHold = new();
    private DispatcherTimer? _timelineHoldEnd;
    private Slider? _musicSeek;
    private readonly ScaleTransform _musicCoverScale = new(1, 1);
    internal MusicLayout? CurrentMusicLayout { get; private set; }
    private TextBlock? _musicElapsed, _musicDuration, _lyricPrevious, _lyricNext, _musicMessage;
    // The current line scrolls when it is wider than the column; the lines around it are cut off with an ellipsis.
    private MusicMarquee? _lyricCurrent;
    private Grid? _musicInfo;
    private Canvas? _musicControls;
    private Button? _musicFullControl;
    private TextBlock? _musicSmtcHint;
    private readonly Dictionary<MediaCommand, (MusicControlButton Button, string Icon)> _musicControlButtons = new();
    // The transport button the pointer last clicked, and when (Environment.TickCount64): only its own new glyph grows into
    // place; one changed by a media key, the keyboard or the player switches at once (motion-review F10).
    private (MediaCommand Id, long At)? _musicPointerClick;
    private const int MusicClickAnswer = 2000;
    private MusicEqualizer? _musicEqualizer, _compactEqualizer;
    private readonly double[] _flowLevels = new double[5];
    private MusicMarquee? _compactMarquee;
    // The collapsed row's bar heights and the place of its scrolling line. The row is built again on every change of its width
    // (a resize drag does that every frame), so like the readings' StripPresence they belong to the panel and outlive each row.
    private readonly EqualizerBars _compactBars = new();
    // The collapsed title scrolls its laps once per line (MarqueeScroll.Limited); a row built again for the same line keeps it
    // resting, until the line changes or the panel closes (ReplayCompactTitle).
    private readonly MarqueeScroll _compactScroll = new() { Limited = true };
    private ClockFace? _compactClock;
    private QuotaCarousel? _quotaCarousel;
    private TimeSpan _quotaElapsed;
    // The system readings' carousel, when they do not all fit, and where its cycle stood when the row was last built.
    private ReadingsCarousel? _stripCarousel;
    private TimeSpan _stripElapsed;
    // The page on screen: the chosen one while it is shown, otherwise the one DeckPages falls back to.
    internal DeckPage CurrentPage => DeckPages.Resolve(Settings, _page);
    internal bool IsMusicPage => CurrentPage == DeckPage.Music;
    internal bool IsMonitorPage => CurrentPage == DeckPage.Monitor;
    internal bool IsUsagePage => CurrentPage == DeckPage.Usage;
    internal CompactMusicLayout? CurrentCompactLayout { get; private set; }

    private void InitializeMusic()
    {
        _page = Settings.LastPage;
        _sourceMenuWatch = new(Dispatcher, TickSourceMenu) { Interval = TimeSpan.FromMilliseconds(40) };
        _musicTick = new(Dispatcher, TickMusic) { Interval = TimeSpan.FromMilliseconds(33) };
        IsVisibleChanged += (_, _) => UpdateMusicTimer();
        _app.MediaChanged += MediaChanged;
        _app.Lyrics.AppleLyricsWritten += AppleLyricsWritten;
        DisplayPower.Changed += DisplayPowerChanged;
    }
    private void StartMusic()
    {
        _musicStarted = true;
        ApplyMusicSettings();
        // A panel opened while music plays takes the current song without announcing it as a new one.
        if (_media is { } media) { _pinnedMedia = media.Pinned; ApplyMediaUpdate(media.Current, media.Tracks, media.Cover, notice: false); }
    }
    private void ApplyMusicSettings()
    {
        if (!Settings.MusicPage) CloseSourceMenu();
        _page = CurrentPage;
        if (!Settings.UsagePage || !IsUsagePage) _details = false;
        if (_musicStarted) _app.StartMedia();
        if (!Settings.MediaWanted) { _mediaTrack = null; _mediaCover = null; _mediaTracks = []; _seekPreview.Observe(null, DateTimeOffset.UtcNow); }
        UpdateLyrics();
        _musicRenderKey = "";
    }
    private void ApplyDefaultMusicPage()
    {
        // Opened for a page of its own (the recent notices from a notice), the panel keeps that page.
        if (_keepPageOnOpen) return;
        var wanted = DeckPages.Opening(Settings.DefaultPage) ?? _page;
        if (wanted != _page && DeckPages.Shown(Settings, wanted)) SelectPage(wanted, remember: false);
    }
    // The page switch moves as one: the page left behind leaves as a picture toward the side it is left for, while the next
    // page is built at once and comes in from the other side and the panel takes its size (PageHandover).
    internal void SelectPage(DeckPage page, bool remember = true)
    {
        if (!DeckPages.Shown(Settings, page)) return;
        bool changed = CurrentPage != page;
        // What the pointer lit belongs to the page that leaves.
        if (changed) { CloseSourceMenu(); _fx?.Reset(); PageToolTips.CloseWithin(PageContent(CurrentPage)); }
        if (changed && _expanded && !_preview)
        {
            // Start before resizing: the shrinking surface can immediately fire MouseLeave.
            _hover.Stop(); _leave.Stop(); _pageSwitchHold.Stop(); _pageSwitchHold.Start();
        }
        if (remember) { Settings.LastPage = page; _app.QueueSave(); }
        bool animate = changed && _expanded && !_preview && Soft.Enabled;
        var before = CurrentPage;
        var from = PageContent(before);
        var picture = animate ? PageHandover.Capture(from) : null;
        var place = PageHandover.Place(from);
        // The recent notices count as seen before the page is built (ShowPage builds it): marked after, the new fresh set and
        // the viewed marks changed what the page shows, and the whole page was built a second time at background priority in
        // the middle of the switch, which stalled it. Marked now, the change arrives while another page still shows.
        if (page == DeckPage.Notices && changed && _expanded) NoticesPageShown();
        ShowPage(page, animatePage: changed && _expanded && !_preview);
        bool back = Settings.PageOrder.IndexOf(page) < Settings.PageOrder.IndexOf(before);
        _handover.Send(picture, place, PageContent(page), back ? Handover.Back : Handover.Forward);
        // Chosen again while it shows (the notices hotkey on an open panel): what arrived since counts as seen too.
        if (page == DeckPage.Notices && !changed && _expanded) NoticesPageShown();
    }
    private FrameworkElement PageContent(DeckPage page) => page switch { DeckPage.Music => MusicContent, DeckPage.Monitor => MonitorContent, DeckPage.Devices => DevicesContent, DeckPage.Notices => NoticesContent, _ => UsageContent };
    private void ShowPage(DeckPage page, bool animatePage)
    {
        // A switch the hand-over animates lets the page still leaving from the last one finish (PageHandover.Begin).
        _detailPopup?.Close(); _details = false; _page = page; _musicRenderKey = ""; if (!animatePage) _handover.Drop();
        ApplySettings(animatePage);
    }
    // Places the chosen page's fill under its button. While the open panel shows it, it glides there as the monitor page's sort pill
    // does; otherwise it is set in place. A switch whose buttons are not laid out yet places it once they are.
    private void PlacePageCapsule()
    {
        var button = CurrentPage switch { DeckPage.Music => MusicPageButton, DeckPage.Monitor => MonitorPageButton, DeckPage.Devices => DevicesPageButton, DeckPage.Notices => NoticesPageButton, _ => AIUsageButton };
        if (button.Visibility != Visibility.Visible || !button.IsMeasureValid || button.ActualWidth <= 0 || PresentationSource.FromVisual(button) is null)
        {
            if (!_capsulePending) { _capsulePending = true; Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { _capsulePending = false; if (button.ActualWidth > 0) PlacePageCapsule(); }); }
            return;
        }
        double x = button.TranslatePoint(new Point(), (UIElement)PageCapsule.Parent).X, width = button.ActualWidth, height = HitArea.Look(button);
        bool glide = _expanded && !_preview && PageSwitch.IsVisible && Soft.Enabled && PageCapsule.Width > 0 && Math.Abs(PageCapsuleShift.X - x) > .5;
        PageCapsule.Height = height;
        // motion-review F8: the width is set at once and the change is drawn by stretching the capsule from its left edge, so
        // the glide lays nothing out again.
        // A stretch still under way goes on from the width it draws now, not from its laid-out width.
        double was = PageCapsule.Width * PageCapsuleStretch.ScaleX;
        PageCapsule.Width = width;
        PageCapsuleStretch.BeginAnimation(ScaleTransform.ScaleXProperty, null); PageCapsuleStretch.ScaleX = 1;
        if (glide && double.IsFinite(was) && was > 0 && width > 0 && Math.Abs(was - width) > .5)
            PageCapsuleStretch.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(was / width, 1, TimeSpan.FromMilliseconds(Soft.Choose))
            { EasingFunction = Soft.Follow, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        double from = PageCapsuleShift.X;
        PageCapsuleShift.BeginAnimation(TranslateTransform.XProperty, null);
        PageCapsuleShift.X = x;
        if (glide) PageCapsuleShift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(from, x, TimeSpan.FromMilliseconds(Soft.Choose))
        { EasingFunction = Soft.Follow, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
    }
    private bool _capsulePending;
    // The buttons stand in the order chosen in the settings; the first has no gap before it.
    private void OrderPageButtons()
    {
        if (AIUsageButton.Parent is not Panel buttons) return;
        var order = Settings.PageOrder.Select(PageButton).ToList();
        if (!buttons.Children.Cast<UIElement>().SequenceEqual(order))
        {
            buttons.Children.Clear();
            foreach (var button in order) buttons.Children.Add(button);
        }
        for (int i = 0; i < order.Count; i++) order[i].Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0);
    }
    private Button PageButton(DeckPage page) => page switch { DeckPage.Music => MusicPageButton, DeckPage.Monitor => MonitorPageButton, DeckPage.Devices => DevicesPageButton, DeckPage.Notices => NoticesPageButton, _ => AIUsageButton };
    // The wheel walks the shown pages (down moves along the switch, up moves back) or steps the system volume. A wheel that
    // a scrolling part of a page uses (the lyrics, the model details) never arrives, as those mark it handled on the way up;
    // an open menu or the detail popup keeps the wheel to itself as well.
    private void PanelWheel(object sender, MouseWheelEventArgs e)
    {
        if (Settings.MouseWheel == MouseWheelAction.None || !_expanded || _preview || _detailPopup is not null || MediaSourceButton.ContextMenu?.IsOpen == true) return;
        if (Settings.MouseWheel == MouseWheelAction.Volume) { WheelVolume(e.Delta); e.Handled = true; return; }
        var shown = DeckPages.Visible(Settings);
        if (shown.Count < 2) return;
        int at = shown.ToList().IndexOf(CurrentPage);
        if (at < 0) return;
        int next = e.Delta > 0 ? at - 1 : at + 1;
        if (next < 0 || next >= shown.Count) return;
        SelectPage(shown[next]);
        e.Handled = true;
    }
    // A precision touchpad sends parts of a notch; they add up until a whole notch (120) presses the volume key once.
    private int _wheelVolumeRest;
    private void WheelVolume(int delta)
    {
        if (Math.Sign(delta) != Math.Sign(_wheelVolumeRest)) _wheelVolumeRest = 0;
        _wheelVolumeRest += delta;
        int notches = _wheelVolumeRest / Mouse.MouseWheelDeltaForOneLine;
        if (notches == 0) return;
        _wheelVolumeRest -= notches * Mouse.MouseWheelDeltaForOneLine;
        Native.VolumeKeys.Press(notches > 0, Math.Abs(notches));
    }

    private void MusicPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Music);
    private void MonitorPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Monitor);
    private void DevicesPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Devices);
    // Where the usage page's application tabs start: after the page switch, which holds one 28 DIP button per shown page.
    private double ApplicationPagesLeft(double side) { int pages = DeckPages.Visible(Settings).Count; return side + (pages <= 1 ? 36 : 72 + 29 * (pages - 2)); }
    // The header shared by both pages (see PanelHeader). Its row holds the page's top margin, so the clock, the switch and the
    // right group are centred on one line, and the switch and the gear line up with the page's content edges. Toolbar's own
    // height replaces its binding to the row: the music page takes the usage page's controls (PanelHeader.WithControlsOf), so
    // its band can be shorter than its row, with the rest below the controls; the row may also be one DIP taller, and the
    // controls are centred in the even part. Toolbar lies outside ContentCanvas's rows, so a band taller than the row is not
    // clipped. Only layout values change, and setting an unchanged value does nothing.
    private PanelHeader _header = PanelHeader.For(36, 20);
    private void ApplyHeader(PanelHeader header)
    {
        _header = header;
        ToolbarRow.Height = new GridLength(header.Row);
        Toolbar.Height = header.Height;
        PageSwitch.Margin = new Thickness(header.Side, 0, 0, 0);
        // The buttons take the band's height to the pointer, up to HitArea.Target, while they look as tall as before.
        foreach (var button in new[] { AIUsageButton, MonitorPageButton, MusicPageButton, DevicesPageButton, NoticesPageButton })
        {
            HitArea.Fit(button, header.ButtonHeight, header.Height);
            // A local corner for these two buttons only; PanelIconButton keeps its 5 DIP corner elsewhere.
            button.ApplyTemplate();
            if (button.Template?.FindName("Chrome", button) is Border chrome) chrome.CornerRadius = new CornerRadius(PanelTheme.Corner(PanelHeader.ButtonRadius));
        }
        PageCapsule.CornerRadius = new CornerRadius(PanelTheme.Corner(PanelHeader.ButtonRadius));
        HitArea.Fit(SettingsButton, header.SettingsHeight, header.Height);
        HitArea.Fit(MediaSourceButton, 24, header.Height);
        HitArea.Fit(SyncButton, 28, header.Height);
        if (SettingsButton.Parent is FrameworkElement right) right.Margin = new Thickness(0, 0, header.RightMargin, 0);
        ApplicationPages.Margin = new Thickness(ApplicationPagesLeft(header.Side), 0, 0, 0);
    }
    private void RenderPageSwitch()
    {
        SetValue(SuppressMusicToolTipsProperty, IsMusicPage);
        foreach (var control in new FrameworkElement[] { AIUsageButton, MonitorPageButton, MusicPageButton, DevicesPageButton, NoticesPageButton, MediaSourceButton, ClockLabel })
            ToolTipService.SetIsEnabled(control, !IsMusicPage);
        PageSwitch.Visibility = _details && IsUsagePage ? Visibility.Collapsed : Visibility.Visible;
        OrderPageButtons();
        foreach (var (button, page) in new[] { (AIUsageButton, DeckPage.Usage), (MusicPageButton, DeckPage.Music), (MonitorPageButton, DeckPage.Monitor), (DevicesPageButton, DeckPage.Devices), (NoticesPageButton, DeckPage.Notices) })
        {
            bool current = CurrentPage == page;
            button.Visibility = DeckPages.Shown(Settings, page) ? Visibility.Visible : Visibility.Collapsed;
            button.Foreground = UI.Brush(current ? UI.Primary : UI.Secondary);
        }
        PlacePageCapsule();
        UpdateNoticeBadge();
        ApplicationPages.Margin = new Thickness(ApplicationPagesLeft(_header.Side), 0, 0, 0);
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
    }
    private void MediaChanged()
    {
        if (_media is null) return;
        _pinnedMedia = _media.Pinned;
        ApplyMediaUpdate(_media.Current, _media.Tracks, _media.Cover);
    }
    private void ApplyMediaUpdate(MediaTrack? track, IReadOnlyList<MediaTrack> tracks, BitmapSource? cover, bool notice = true)
    {
        bool changedSong = _mediaTrack?.Id != track?.Id || _mediaTrack is not null && track is not null && !SameMediaSong(_mediaTrack, track);
        bool lyricsChanged = changedSong || _mediaTrack?.SongKey != track?.SongKey || _mediaTrack?.SongId != track?.SongId || _mediaTrack?.EmbeddedLyrics != track?.EmbeddedLyrics;
        var old = _mediaTrack;
        _mediaTrack = track; _mediaTracks = tracks;
        var now = DateTimeOffset.UtcNow;
        _seekPreview.Observe(track, now);
        _timelineHold.Observe(track, now); ScheduleTimelineHoldEnd(now);
        bool coverChanged = _mediaCover != cover;
        if (coverChanged) { _mediaCover = cover; _musicAccent = MusicVisuals.Accent(_mediaCover, out _coverAccent); }
        // A new song scrolls its title again, even one of the same name (the scroll's key is its text).
        if (changedSong) ReplayCompactTitle();
        if (changedSong)
        {
            _seeking = false; _seekTrack = null;
            // A notice still on screen switches to the new title instead of giving way to the summary until playback starts.
            if (_mediaTrack is not null && Settings.MusicTrackNotice && notice) _trackNotice.Arm(now); else _trackNotice.Disarm();
        }
        // The coordinator lets the title show only when nothing more important does (NoticeCenter.TryTrack).
        if (_trackNotice.Observe(_mediaTrack, now) && _app.Notices.TryTrack(this, TrackNoticeTime)) _trackNoticeUntil = now + TrackNoticeTime;
        if (lyricsChanged) UpdateLyrics();
        if (changedSong || lyricsChanged || coverChanged || old?.State != _mediaTrack?.State || old?.HasTimeline != _mediaTrack?.HasTimeline) RenderCompact();
        if (IsMusicPage) RenderMusic();
        // Native lyric events must update compact text even while playback is paused.
        UpdateMusicPosition();
        UpdateMusicTimer();
    }
    private void ScheduleTimelineHoldEnd(DateTimeOffset now)
    {
        _timelineHoldEnd?.Stop();
        if (_timelineHold.Until is not { } until || until <= now) return;
        if (_timelineHoldEnd is null)
        {
            var timer = _timelineHoldEnd = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            timer.Tick += (_, _) => { timer.Stop(); if (IsMusicPage) RenderMusic(); };
        }
        // A little after the end, so the page laid out then no longer holds.
        _timelineHoldEnd.Interval = until - now + TimeSpan.FromMilliseconds(50); _timelineHoldEnd.Start();
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
            // Apple Music writes a song's lyrics to its cache as the song starts, which can be after the song changes here, or later
            // still: they are looked for again 3 and 15 seconds later and whenever Apple Music writes a lyric file, keeping any
            // lyrics already shown.
            for (int attempt = 0; ; attempt++)
            {
                var lyrics = await _app.Lyrics.FindAsync(requested, true, request.Token);
                if (request.IsCancellationRequested || _lyricsKey != key) return;
                if (lyrics.Lines.Count > 0 || lyrics.Instrumental || attempt == 0)
                {
                    _lyrics = lyrics; _musicRenderKey = "";
                    if (IsMusicPage) RenderMusic(); RenderCompact();
                }
                if (!requested.IsAppleMusic || lyrics.Source == LyricsService.AppleSource) break;
                var written = (_appleLyricsWritten = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                await (attempt < 2 ? Task.WhenAny(written, Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 3 : 15), request.Token)) : written.WaitAsync(request.Token));
                request.Token.ThrowIfCancellationRequested();
                // The file is read once Apple Music has finished writing it.
                if (written.IsCompleted) await Task.Delay(TimeSpan.FromSeconds(1), request.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Lyrics are optional; provider failures keep the no-lyrics layout. */ }
    }
    private void AppleLyricsWritten() => Dispatcher.BeginInvoke(() => _appleLyricsWritten?.TrySetResult());
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
    // The heart of a liked song, in the theme's color for it (NetEase Cloud Music's red in the dark theme).
    private static string LikedColor => PanelTheme.Hex("liked");
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
            var row = new Grid(); RenderOptions.SetClearTypeHint(row, PanelTheme.ClearType);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var source = new TextBlock { Text = Loc.Label(track.Source), FontSize = TextSize.Body, Foreground = UI.Brush(UI.Primary), MaxWidth = 100,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, Space.Tight, 0) };
            var title = new MusicMarquee { Text = track.Title, FontSize = TextSize.Body, Foreground = UI.Brush(PanelTheme.Hex("menuDetail")),
                CenterWhenFits = false, IsHitTestVisible = false, Height = 28, Margin = new Thickness(0, 0, Space.S12, 0) };
            var status = new TextBlock { Text = state, FontSize = TextSize.Caption, Foreground = UI.Brush(UI.Tertiary), TextWrapping = TextWrapping.NoWrap };
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
            var label = new TextBlock { Text = FullControlEntryLabel, FontSize = TextSize.Body, Foreground = UI.Brush(UI.Primary), TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var entry = new MenuItem { Style = (Style)FindResource("MediaSourceMenuItem"), Header = label, Tag = "action", IsEnabled = _media?.RestartingNetease != true };
            AutomationProperties.SetName(entry, FullControlEntryLabel);
            entry.Click += (_, _) => { CloseSourceMenu(); _ = StartNeteaseFullControl(); }; menu.Items.Add(entry);
        }
        WatchSourceMenu(menu);
        MediaSourceButton.ContextMenu = menu; menu.PlacementTarget = MediaSourceButton; menu.Placement = PlacementMode.Bottom;
        // 6 DIP under the button as it looks, not under its taller hit area (HitArea). A pop-up is not part of the panel's visual
        // tree, so it takes the panel's scale and the text size itself (PanelTheme.Text on its Opened).
        UI.Scale(menu, _scale * Zoom); menu.VerticalOffset = (6 - HitArea.GetInset(MediaSourceButton).Bottom) * _scale * Zoom; ResizeSourceMenu(menu); menu.IsOpen = true;
    }
    // Guidance shown in place of the transport when Netease runs without SMTC. Both sentences stay at every size:
    // what is required and where to switch it on. Only the type size and the entry's label adapt to the row.
    internal static string SmtcHintWhy => Loc.T("开启网易云的 SMTC 后，才能在这里控制播放", "Turn on SMTC in NetEase to control playback here");
    // The English hint names the menu items as NetEase Cloud Music's Chinese interface shows them.
    internal static string SmtcHintHow => Loc.T("位置：网易云音乐 › 设置 › 系统 › 开启 SMTC", "Where: NetEase Cloud Music › 设置 › 系统 › 开启 SMTC");
    // The song and the form (PageHandover) the media page was last built with: a new song swaps in, a new form (the lyric lines
    // the room holds, the title merged with the artist, the message or the entry shown) morphs in, as when a drag crosses a size step.
    private string _musicSong = "", _musicForm = "";
    private bool _musicHadLyrics;
    private long _songArrived;
    private BitmapSource? _musicCoverShown;
    // The titles of a new song rise into place. A rebuild during the change continues it from where it is.
    private void RiseIn(FrameworkElement titles)
    {
        if (!Soft.Enabled) return;
        double done = Math.Clamp((Environment.TickCount64 - _songArrived) / (double)Soft.Move, 0, 1), start = Curves.Follow(done);
        int left = (int)Math.Max(1, Soft.Move * (1 - done));
        var shift = new TranslateTransform();
        titles.RenderTransform = shift;
        var rise = Soft.Fade(8 * (1 - start), 0, left, Soft.Follow); rise.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
        var fade = Soft.Fade(start, 1, left, Soft.Follow); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
        shift.BeginAnimation(TranslateTransform.YProperty, rise);
        titles.BeginAnimation(OpacityProperty, fade);
    }
    private void RenderMusic()
    {
        // Only a page that was on screen hands over; one that arrives by the page switch or with the panel opening comes in whole.
        bool shown = MusicContent.IsVisible && _expanded && !_preview;
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed; DevicesContent.Visibility = Visibility.Collapsed; NoticesContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double h = ContentCanvas.Height, w = ContentCanvas.Width;
        var track = _mediaTrack;
        if (_seeking && _seekTrack is { } seeking && track is not null && SameMediaSong(seeking, track))
        { UpdateMusicPosition(); return; }
        bool playerLyrics = track?.IsQqMusic == true && _lyrics.Lines.Count == 0 && !_lyrics.Instrumental;
        bool hasPlayerLyric = playerLyrics && Settings.LyricsEnabled && !string.IsNullOrWhiteSpace(track?.CurrentLyric) && track?.State != MediaState.Stopped;
        bool hasLyrics = playerLyrics ? hasPlayerLyric : (_lyrics.Lines.Count > 0 || _lyrics.Instrumental) && Settings.LyricsEnabled;
        bool logMode = track?.IsNeteaseLog == true;
        bool appleEntry = track is { IsAppleMusic: true } && _media?.AppleMusicFullControl == false;
        bool showSmtcHint = logMode && track is { CanToggle: false, CanPrevious: false, CanNext: false }
            && _media?.RestartingNetease != true && _media?.Error is null;
        string message = _media?.RestartingNetease == true ? Loc.T("正在重启网易云…", "Restarting NetEase Cloud Music…") : _media?.Error ?? "";
        var modes = track is null ? [] : MusicPlaybackModes.Supported(track);
        // While the timeline is held (MusicTimelineHold) the bar stays in its place, empty and without times.
        bool timeline = track?.HasTimeline == true || track is not null && _timelineHold.Holds(DateTimeOffset.UtcNow);
        var layout = MusicLayout.Calculate(w, h, hasLyrics, timeline, message.Length > 0, logMode || appleEntry, transport: !showSmtcHint,
            lyricsExpected: Settings.LyricsEnabled);
        CurrentMusicLayout = layout;
        var c = layout.Metrics;
        // With the usage page on, the controls are the usage page's, as it would lay them out now; see PanelHeader.WithControlsOf.
        ApplyHeader(Settings.UsagePage ? PanelHeader.Music(c).WithControlsOf(UsageHeader(UsageLayoutWidth, UsagePageHeight)) : PanelHeader.Music(c));
        bool animate = Soft.Enabled;
        string key = $"{track?.Id}|{track?.SongKey}|{logMode}|{appleEntry}|{_media?.RestartingNetease}|{track?.State}|{track?.CanToggle}|{track?.CanPrevious}|{track?.CanNext}|{track?.CanSeek}|{track?.CanRepeat}|{track?.CanShuffle}|{track?.Shuffle}|{track?.Repeat}|{track?.CanLike}|{track?.Liked}|{track?.HasTimeline}|{timeline}|{playerLyrics}|{hasPlayerLyric}|{w}|{h}|{_lyrics.GetHashCode()}|{_mediaCover?.GetHashCode()}|{_media?.Error}|{Settings.LyricsEnabled}|{animate}";
        if (_musicRenderKey == key) { UpdateMusicPosition(); return; }
        _musicRenderKey = key;
        string song = track is null ? "" : $"{track.Id}|{track.SongKey}|{track.Title}|{track.Artist}";
        // Lyrics that arrive after the song, or come and go with the player's line, only fade in their own lines (lyricsArrive):
        // the form counts the lyric lines the room holds, not whether lyrics are there.
        string form = $"{track is null}|{layout.LyricRoom}|{layout.MergeTitle}|{layout.ShowFullControl}|{layout.ShowMessage}|{Settings.LyricsEnabled}|{showSmtcHint}|{timeline}";
        bool lyricsArrive = shown && hasLyrics && !_musicHadLyrics && Soft.Enabled;
        _musicHadLyrics = hasLyrics;
        // A new song changes the titles and perhaps the cover; the rest of the page is the same and stays still. The titles
        // rise into place (RiseIn) and a new cover fades in. A player announces a song in a few updates (title, artist, and a
        // timeline that may go and come back); the first starts the change and the rest carry it on instead of starting it
        // again, and a form that changes during it is taken at once rather than morphed twice.
        bool newCover = shown && !ReferenceEquals(_mediaCover, _musicCoverShown);
        _musicCoverShown = _mediaCover;
        if (shown && song != _musicSong && Environment.TickCount64 - _songArrived >= Soft.Move) _songArrived = Environment.TickCount64;
        long sinceSong = Environment.TickCount64 - _songArrived;
        bool songChanging = sinceSong < Soft.Move;
        // A slow player may bring the timeline back a little after the move: the form is not morphed for twice its length.
        var motion = shown && form != _musicForm && sinceSong >= 2 * Soft.Move ? Handover.Morph : Handover.None;
        _musicSong = song; _musicForm = form;
        // Transport buttons that stay glide to their new places. While the panel is dragged they follow it frame by frame like
        // the rest of the page, and glide only when its form changes.
        bool glide = shown && animate && (!Resizing || motion != Handover.None);
        // Keep the transport layer attached: replacing a hovered button restarts
        // hit testing and its fade-in on every metadata, artwork or lyric update.
        _handover.Hold(MusicContent, keep: _musicControls);
        _musicSeek = null; _musicElapsed = null; _musicDuration = null; _musicEqualizer = null; _lyricCurrent = null; _lyricPrevious = null; _lyricNext = null; _musicMessage = null; _musicInfo = null;
        MusicContent.Margin = new Thickness(c.Side, 0, c.Side, c.Bottom);
        if (track is null)
        {
            MusicContent.Children.Clear(); _musicControls = null; _musicFullControl = null; _musicSmtcHint = null; _musicControlButtons.Clear();
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = Math.Max(200, w - 60) };
            var icon = MusicVisuals.Icon("music", 26); icon.Margin = new Thickness(0, 0, 0, Space.S8); empty.Children.Add(icon);
            // The whole block is centred: the title, the hint and the button (V-02).
            var heading = UI.Fixed(Loc.T("没有正在播放的媒体", "Nothing is playing"), TextSize.Strong, 22, UI.Secondary); heading.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(heading);
            // Why the page is empty, and where to go next: the players BrimDeck follows are chosen in the settings.
            var hint = UI.Line(_media?.Error ?? Loc.T("打开网易云音乐、QQ 音乐、Spotify 等播放器并开始播放后，会在这里显示。", "Start playing in NetEase Cloud Music, QQ Music, Spotify or another player and it shows here."),
                TextSize.Body, UI.Tertiary);
            hint.TextWrapping = TextWrapping.Wrap; hint.TextAlignment = TextAlignment.Center; hint.HorizontalAlignment = HorizontalAlignment.Center;
            // A hint that wraps is cut into lines of even length, so its last line is never a stray word or two (V-02).
            double room = Math.Min(360, Math.Max(200, w - 60)), whole = UI.TextWidth(hint);
            if (whole > room) hint.MaxWidth = Math.Ceiling(whole / Math.Ceiling(whole / room)) + 14;
            empty.Children.Add(hint);
            var players = EmptyAction(Loc.T("选择播放器", "Choose players"), 26); players.HorizontalAlignment = HorizontalAlignment.Center; players.Margin = new Thickness(0, Space.S8, 0, 0);
            players.Click += (_, _) => _app.OpenSettings(SettingsWindow.PlayersPage);
            empty.Children.Add(players);
            MusicContent.Children.Add(empty); _handover.Release(empty, motion); return;
        }
        var body = new Grid();
        var upper = new Grid { Height = layout.Cover, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, layout.CoverY, 0, 0) };
        upper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Cover) });
        upper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c.Gap) });
        upper.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(upper);
        var art = MusicVisuals.Cover(_mediaCover, layout.Cover, layout.CoverRadius, outlined: true);
        art.RenderTransformOrigin = new Point(.5, .5); art.RenderTransform = _musicCoverScale;
        Soft.Glide(_musicCoverScale, ScaleTransform.ScaleXProperty, track.State == MediaState.Paused ? .94 : 1, animate, Soft.Move);
        Soft.Glide(_musicCoverScale, ScaleTransform.ScaleYProperty, track.State == MediaState.Paused ? .94 : 1, animate, Soft.Move);
        AutomationProperties.SetName(art, Loc.T("专辑封面", "Album cover")); upper.Children.Add(art);
        AttachCoverFocus(art);
        if (newCover && animate) { var fade = Soft.Fade(0, 1, Soft.Move, Soft.Follow); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop; art.BeginAnimation(OpacityProperty, fade); }
        var right = new Grid(); Grid.SetColumn(right, 2); upper.Children.Add(right);
        bool synchronized = playerLyrics || _lyrics.CanSynchronize(track);
        int lyricLines = playerLyrics ? Math.Min(1, layout.LyricLines) : layout.LyricLines;
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.HeaderHeight) });
        right.RowDefinitions.Add(new RowDefinition());
        double equalizerWidth = Math.Max(27, Math.Round(c.Equalizer * 1.7));
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(equalizerWidth + 16) });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var title = UI.Fixed(track.Title, c.Title, c.TitleLine, UI.Primary, FontWeights.SemiBold);
        AutomationProperties.SetName(title, Loc.T("歌名 ", "Title ") + track.Title); titles.Children.Add(title);
        if (layout.MergeTitle)
        {
            if (track.Artist.Length > 0) title.Inlines.Add(new Run(" · " + track.Artist) { Foreground = UI.Brush(UI.Secondary), FontSize = c.Artist, FontWeight = FontWeights.Normal });
            ShowCutText(title, track.Artist.Length > 0 ? track.Title + " · " + track.Artist : track.Title);
        }
        else
        {
            string artist = string.Join(" · ", new[] { track.Artist, track.Album }.Where(s => s.Length > 0));
            var line = UI.Fixed(artist, c.Artist, c.ArtistLine, UI.Secondary); titles.Children.Add(line);
            ShowCutText(title, track.Title); ShowCutText(line, artist);
        }
        header.Children.Add(titles);
        if (shown && songChanging) RiseIn(titles);
        _musicEqualizer = new MusicEqualizer { Width = equalizerWidth, Height = c.Equalizer, Color = _musicAccent, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, (c.TitleLine - c.Equalizer) / 2, 0, 0) };
        Grid.SetColumn(_musicEqualizer, 1); header.Children.Add(_musicEqualizer); right.Children.Add(header);

        var middle = new Grid(); Grid.SetRow(middle, 1); right.Children.Add(middle); _musicInfo = middle;
        if (hasLyrics && lyricLines > 0 && synchronized)
        {
            var lyrics = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            // All lines share one size; only the brightness marks the current one.
            if (lyricLines == 3) { _lyricPrevious = UI.Fixed("", c.Lyric, c.LyricLine, UI.Tertiary); lyrics.Children.Add(_lyricPrevious); }
            _lyricCurrent = new MusicMarquee { FontSize = c.Lyric, Height = c.LyricLine, CenterWhenFits = false, Backdrop = PanelTheme.MarqueeBackdrop, InkFade = PanelTheme.Current.HasOverlay };
            lyrics.Children.Add(_lyricCurrent); UpdateLyricScrolling();
            if (lyricLines >= 2) { _lyricNext = UI.Fixed("", c.Lyric, c.LyricLine, UI.Tertiary); lyrics.Children.Add(_lyricNext); }
            lyrics.Visibility = layout.ShowMessage ? Visibility.Collapsed : Visibility.Visible; middle.Children.Add(lyrics);
        }
        else if (hasLyrics && lyricLines > 0)
        {
            var text = UI.Line(_lyrics.Instrumental && _lyrics.Lines.Count == 0 ? "♪" : string.Join("\n", _lyrics.Lines.Select(line => line.Text)), c.Lyric, UI.Secondary);
            text.TextWrapping = TextWrapping.Wrap; text.LineHeight = c.LyricLine; text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            var lyrics = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalAlignment = VerticalAlignment.Center, Height = lyricLines * c.LyricLine, CanContentScroll = false, Visibility = layout.ShowMessage ? Visibility.Collapsed : Visibility.Visible };
            // The viewport clip would turn the lyrics grayscale; they lie over the island's opaque surface.
            RenderOptions.SetClearTypeHint(text, PanelTheme.ClearType);
            AutomationProperties.SetName(lyrics, Loc.T("歌词", "Lyrics")); middle.Children.Add(lyrics);
        }
        if (lyricsArrive) { var fade = Soft.Fade(0, 1, Soft.Move, Soft.Follow); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop; middle.BeginAnimation(OpacityProperty, fade); }
        if (layout.MiddleHeight >= c.MiddleGap + c.LyricLine)
        {
            // One line; a longer message (a failed full control connection) is read whole in its tooltip, like a cut title.
            _musicMessage = UI.Fixed(message, c.Lyric, c.LyricLine, UI.Tertiary); ShowCutText(_musicMessage, message);
            _musicMessage.Visibility = layout.ShowMessage ? Visibility.Visible : Visibility.Collapsed; middle.Children.Add(_musicMessage);
        }
        if (timeline)
        {
            var times = new Grid { Height = c.TimeLine, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, layout.TimesY, 0, 0), IsHitTestVisible = false };
            _musicElapsed = UI.Fixed("", c.Time, c.TimeLine, UI.Secondary); times.Children.Add(_musicElapsed);
            _musicDuration = UI.Fixed("", c.Time, c.TimeLine, UI.Secondary); _musicDuration.HorizontalAlignment = HorizontalAlignment.Right; times.Children.Add(_musicDuration);
            foreach (var time in new[] { _musicElapsed, _musicDuration }) Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            body.Children.Add(times);
            _musicSeek = new MusicSeekSlider { Style = (Style)FindResource("MusicSeek"), RestHeight = c.Bar, HoverHeight = c.BarHover, BarHeight = c.BarHover,
                Accent = ((SolidColorBrush)_musicAccent).Color, DurationSeconds = track.Duration.TotalSeconds, Height = c.SeekRow, AnimationsEnabled = animate, IsEnabled = track.CanSeek && track.HasTimeline,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, layout.ProgressY, 0, 0) };
            AutomationProperties.SetName(_musicSeek, Loc.T("播放进度", "Playback position"));
            // The bar takes the pointer HitArea.Minimum tall: above it up to the cover's gap, below it over the times, short of
            // the controls.
            double seekGrow = Math.Max(0, HitArea.Minimum - c.SeekRow);
            double seekAbove = Math.Max(0, c.SectionGap - (c.SeekRow - c.Bar) / 2 - 1), seekBelow = Math.Max(0, layout.ControlsY - layout.ProgressY - c.SeekRow - 1);
            double seekTop = Math.Min(Math.Ceiling(seekGrow / 2), seekAbove);
            HitArea.Grow(_musicSeek, seekTop, Math.Min(seekGrow - seekTop, seekBelow), c.SeekRow);
            _musicSeek.Margin = new Thickness(0, layout.ProgressY + _musicSeek.Margin.Top, 0, _musicSeek.Margin.Bottom);
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
            body.Children.Add(_musicSeek);
        }
        if (_musicControls is null)
        {
            _musicControls = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            MusicContent.Children.Add(_musicControls);
        }
        var controls = _musicControls;
        controls.Width = layout.BodyWidth; controls.Height = c.Play;
        controls.Margin = new Thickness(0, layout.ControlsY, 0, 0);
        var visibleControls = new HashSet<MediaCommand>();
        void Add(MediaCommand id, string name, string glyph, double size, double x, bool enabled, Func<Task> command, bool mode = false, bool single = false, string? color = null)
        {
            visibleControls.Add(id);
            if (!_musicControlButtons.TryGetValue(id, out var slot))
            {
                var created = new MusicControlButton { Style = (Style)FindResource("MusicControl") };
                created.Click += async (_, _) =>
                {
                    if (InputManager.Current.MostRecentInputDevice is MouseDevice or StylusDevice) _musicPointerClick = (id, Environment.TickCount64);
                    if (created.Tag is Func<Task> execute) await execute();
                };
                slot = (created, ""); controls.Children.Add(created);
            }
            var button = slot.Button;
            string iconKey = $"{glyph}|{size}|{mode}|{single}|{color}|{PanelTheme.Generation}";
            if (slot.Icon != iconKey)
            {
                UIElement icon = MusicVisuals.Icon(glyph, size, UI.Brush(color ?? (mode ? UI.Secondary : UI.Primary)));
                if (single)
                {
                    var container = new Grid { Width = size, Height = size }; container.Children.Add(icon);
                    var singleLabel = UI.CenteredSymbol("1", c.Mode <= 14 ? 7 : c.Mode >= 22 ? 10 : 9, UI.Secondary);
                    singleLabel.HorizontalAlignment = HorizontalAlignment.Center;
                    container.Children.Add(singleLabel); icon = container;
                }
                // A glyph changed by a click on it (play and pause, the playback mode, the heart) grows into place from nearly its
                // size instead of switching (motion-review F10).
                bool clicked = _musicPointerClick is { } click && click.Id == id && Environment.TickCount64 - click.At < MusicClickAnswer;
                if (clicked) _musicPointerClick = null;
                if (glide && clicked && button.IsVisible && slot.Icon.Length > 0 && icon is FrameworkElement grown)
                {
                    var scale = new ScaleTransform(.9, .9);
                    grown.RenderTransformOrigin = new Point(.5, .5); grown.RenderTransform = scale;
                    var grow = Soft.Fade(.9, 1, 200); grow.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
                    var fade = Soft.Fade(0, 1, 200); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
                    scale.ScaleX = scale.ScaleY = 1;
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow); scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                    grown.BeginAnimation(OpacityProperty, fade);
                }
                button.Content = icon;
            }
            _musicControlButtons[id] = (button, iconKey);
            button.Width = size + 16; button.Height = size + 16;
            button.IsEnabled = enabled; button.AnimationsEnabled = animate; button.Tag = command;
            bool moves = glide && button.IsVisible;
            PageHandover.Glide(button, Canvas.LeftProperty, x - 8, moves);
            PageHandover.Glide(button, Canvas.TopProperty, (c.Play - size) / 2 - 8, moves);
            AutomationProperties.SetName(button, name);
        }
        if (track is { SeparateModes: true })
        {
            // Two switches instead of the mode button: shuffle in its place left of previous, repeat in the heart's place
            // right of next. A switch that is on is drawn in the primary color, one that is off in the tertiary.
            string On(bool on) => on ? UI.Primary : UI.Tertiary;
            Add(MediaCommand.Shuffle, Loc.T("随机播放 · ", "Shuffle · ") + (track.Shuffle ? Loc.T("开", "on") : Loc.T("关", "off")), "shuffle",
                c.Mode, layout.ModeX, track.CanShuffle, () => RunMediaCommand(track, MediaCommand.Shuffle), true, color: On(track.Shuffle));
            Add(MediaCommand.Repeat, Loc.T("重复 · ", "Repeat · ") + (track.Repeat switch
                { MediaRepeat.List => Loc.T("全部", "all"), MediaRepeat.Track => Loc.T("单曲", "one"), _ => Loc.T("关", "off") }), "repeat",
                c.Mode, layout.TripletX + layout.TripletWidth + (layout.TripletX - layout.ModeX - c.Mode), track.CanRepeat,
                () => RunMediaCommand(track, MediaCommand.Repeat), true, track.Repeat == MediaRepeat.Track, On(track.Repeat != MediaRepeat.None));
        }
        else if (modes.Count > 0)
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
            var sizes = Enumerable.Range(0, 4).Select(step => c.Time - step * .5).Where(size => size >= 9.5);
            bool fitted = false;
            foreach (var label in layout.ShowFullControl ? MusicLayout.FullControlLabels : [""])
            {
                double reserve = label.Length > 0 ? EntryWidth(label) : 0;
                foreach (double size in sizes)
                {
                    double width = layout.BodyWidth - (reserve > 0 ? reserve + MusicLayout.FullControlGap : 0);
                    hint.FontSize = size; hint.LineHeight = Math.Max(12, Math.Round(size * 1.25)); hint.Width = width;
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
            Add(MediaCommand.Previous, Loc.T("上一首", "Previous"), "previous", c.Skip, gx, track.CanPrevious, () => RunMediaCommand(track, MediaCommand.Previous)); gx += c.Skip + layout.ControlGap;
            Add(MediaCommand.PlayPause, track.State == MediaState.Playing ? Loc.T("暂停", "Pause") : Loc.T("播放", "Play"), track.State == MediaState.Playing ? "pause" : "play", c.Play, gx, track.CanToggle, () => RunMediaCommand(track, MediaCommand.PlayPause)); gx += c.Play + layout.ControlGap;
            Add(MediaCommand.Next, Loc.T("下一首", "Next"), "next", c.Skip, gx, track.CanNext, () => RunMediaCommand(track, MediaCommand.Next));
            // The heart mirrors the mode button: right of next, as far from it as the mode button is from previous. Only
            // full control offers it, and the full-control entry, which shares this side, appears only without full control.
            if (track.CanLike)
                Add(MediaCommand.Like, track.Liked ? Loc.T("取消喜欢", "Unlike") : Loc.T("喜欢", "Like"), track.Liked ? "liked" : "like", c.Mode,
                    layout.TripletX + layout.TripletWidth + (layout.TripletX - layout.ModeX - c.Mode), true, () => RunMediaCommand(track, MediaCommand.Like), mode: true, color: track.Liked ? LikedColor : null);
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
                _musicFullControl = new Button { Style = (Style)FindResource("PanelTextButton"), FontFamily = UI.TextFont, Padding = new Thickness(Space.Tight, Space.Hair, Space.Tight, Space.Hair),
                    Height = 26, HorizontalContentAlignment = HorizontalAlignment.Right };
                AutomationProperties.SetName(_musicFullControl, MusicLayout.FullControlLabels[0]);
                _musicFullControl.Click += async (_, _) => { if (_mediaTrack?.IsAppleMusic == true) EnableAppleMusicControl(); else await EnableNeteaseControl(); };
                controls.Children.Add(_musicFullControl);
            }
            var enable = _musicFullControl; enable.IsEnabled = _media?.RestartingNetease != true;
            // The app-wide TextBlock style wraps; pixel snapping must never push the last character to a second line.
            enable.Width = entryWidth;
            enable.Content = new TextBlock { Text = entryLabel, FontFamily = UI.TextFont, FontSize = c.Time, TextWrapping = TextWrapping.NoWrap };
            Canvas.SetLeft(enable, layout.BodyWidth - entryWidth); Canvas.SetTop(enable, (c.Play - 26) / 2);
        }
        if (_musicFullControl is not null) _musicFullControl.Visibility = entryLabel is not null ? Visibility.Visible : Visibility.Collapsed;
        MusicContent.Children.Insert(0, body); _handover.Release(body, motion); UpdateMusicPosition();
    }
    // The page suppresses tooltips. A line cut off by its ellipsis (a title, an artist, a long message) still shows its whole
    // text on hover. A later text goes in ToolTip alone: whether it is cut is checked when the tooltip would open.
    private static void ShowCutText(TextBlock block, string text)
    {
        block.SetValue(SuppressMusicToolTipsProperty, false);
        block.ToolTip = text.Length > 0 ? text : null;
        block.ToolTipOpening += (_, e) => { if (!UI.IsCut(block)) e.Handled = true; };
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
    private void EnableAppleMusicControl()
    {
        if (_media is not { AppleMusicFullControl: false } media) return;
        var dialog = new AppleMusicControlWindow(Settings.Theme) { Owner = this };
        if (dialog.ShowDialog() != true || _media != media) return;
        media.EnableAppleMusicFullControl();
        _musicRenderKey = ""; RenderMusic();
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
        _musicMessage.Text = error; _musicMessage.ToolTip = error; _musicMessage.Visibility = Visibility.Visible;
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
        // Hidden, the bar keeps its last length: setting it four times a second laid the island out again for nothing. It takes
        // its length on the first update after it shows again.
        if (MusicIndicator.Visibility != Visibility.Visible) return;
        // The progress runs along the indicator's own length, whatever length is set.
        double length = double.IsFinite(Collapsed.Surface.Width) ? Math.Max(0, Collapsed.Surface.Width) : 0;
        double progress = track.HasTimeline && track.Duration > TimeSpan.Zero ? Math.Clamp(position.TotalSeconds / track.Duration.TotalSeconds, 0, 1) : 0;
        MusicIndicator.Width = length * progress;
    }
    private (string Previous, string Current, string Next) CurrentLyrics(MediaTrack track)
    {
        if (!NeedsLyrics || track.State == MediaState.Stopped) return ("", "", "");
        if (track.IsQqMusic && _lyrics.Lines.Count == 0 && !_lyrics.Instrumental) return ("", track.CurrentLyric ?? "", "");
        return _lyrics.At(track, DateTimeOffset.UtcNow);
    }
    // The color taken from the playing song's cover; null without one, or when the cover gave no color.
    internal Color? CoverColor => _mediaCover is not null && _mediaTrack is { State: not MediaState.Stopped } ? _coverAccent : null;
    private void RenderCompactMusic()
    {
        // The collapsed shape gives the row's width in DIPs and the height scale; every size below follows from them.
        var shape = Collapsed; _compactRendered = shape;
        double scale = CompactMusicLayout.SafeScale(shape.Scale);
        double contentWidth = CompactContentWidth(shape);
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
        // A quota ring with nothing read yet would be an empty grey circle that says nothing: it is left out until its quota has
        // a reading (QuotaRings).
        var apps = Settings.ShowsSummary(EffectiveStyle) && !notice ? [.. Settings.EnabledApps.Where(HasQuotaReading)] : new List<AppEntry>();
        // The clock keeps its own width at the right; shown alone, it is centred.
        var clock = Settings.ShowsClock(EffectiveStyle) ? new ClockFace(Settings.ClockStyle, Settings.Clock24Hour, ClockFace.Palette(Settings, CoverColor), DateTime.Now, scale) : null;
        // The system readings offer each chosen reading, in the order of the settings' table and in the scheme's colors; the row
        // admits those that fit whole (CompactMusicLayout).
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // A device switched on or off in the settings changes whether the battery reading shows, with no change of the devices to
        // say so: its admission is taken again as the row is built.
        if (StripChoosesBattery) _stripPresence.Arrive(UpdateStripAdmission(_app.Monitor?.Latest).Arriving);
        var strip = Settings.ShowsMonitor(EffectiveStyle) ? new MonitorStrip(Settings.CompactReadings, MonitorThemeColors.Strip(Settings), scale,
            !media && apps.Count == 0 && text.Length == 0 && clock is null, _stripPresence, dpi, _stripUnavailable = _stripAdmission.Hidden, _stripAdmission, Settings.DeviceLowPercent) : null;
        _stripLayout = _stripAdmission.Layout;
        if (StripChoosesBattery) strip?.SetBattery(StripBattery());
        double rowWidth = SnapDown(contentWidth, dpi);
        // Readings that do not fit scroll through a window of whole readings, as the rings do (CompactSpacing.Window).
        var layout = CompactMusicLayout.Calculate(rowWidth, scale, media, apps.Count, text.Length > 0, clock?.ReservedWidth ?? 0,
            strip is null ? 0 : DevicePixels.Up(strip.PreferredWidth, dpi));
        CurrentCompactLayout = layout;
        // An auto-width surface takes the width of what the row shows; the new shape builds the row again. The track notice
        // and the lyric's changing length are left out, so the width changes only when the content itself does.
        int steadyRings = Settings.ShowsSummary(EffectiveStyle) ? Settings.EnabledApps.Count(HasQuotaReading) : 0;
        bool steadyText = media && Settings.MusicText != CompactMusicText.None;
        double wanted = CompactMusicLayout.NaturalWidth(scale, media, steadyRings, steadyText, clock?.ReservedWidth ?? 0, strip is null ? 0 : DevicePixels.Up(strip.PreferredWidth, dpi), dpi);
        // The surface is never narrower than what the row must show (CompactMusicLayout.RequiredWidth); like the width it asks
        // for, this leaves out the track notice, so the surface does not move for it.
        var readingWidths = strip?.ItemWidths() ?? [];
        double required = CompactMusicLayout.RequiredWidth(scale, media, steadyText, clock?.ReservedWidth ?? 0,
            steadyRings > 0 ? Math.Max(1, CompactMusicLayout.Whole(12 * scale)) : 0, CompactSpacing.Minimum(readingWidths, strip?.Gap ?? 0));
        if (double.IsNaN(_compactWanted) || Math.Abs(wanted - _compactWanted) > .5 || Math.Abs(required - _compactRequired) > .5)
        { _compactWanted = wanted; _compactRequired = required; Dispatcher.BeginInvoke(DispatcherPriority.Background, FitCompactToContent); }
        double row = layout.RowHeight, unit = CompactMusicLayout.Gap(scale);
        var grid = new Grid { Width = rowWidth, Height = row };
        // Each part shows whole or not at all. The title wants the share the layout gives it, which follows the row's width and
        // never the text: the row is not built again for a new lyric line, so a width taken from one line would stay with the
        // lines after it, and the parts would move whenever it was. The rings and the readings share what the other parts and
        // the gaps between all of them leave (CompactSpacing.Share): each shows at least its narrowest window and scrolls through
        // the rest, and they widen by turns. Room neither can use goes back to the title, then to the gaps between the parts,
        // before the row is centred (CompactSpacing.Arrange).
        bool hasCover = media && layout.CoverWidth >= layout.CoverSize - .001, hasEqualizer = media && layout.EqualizerWidth >= layout.EqualizerSize - .001;
        bool hasClock = clock is not null && layout.ClockWidth >= clock.ReservedWidth - .001, hasText = layout.TextWidth > 0 && text.Length > 0;
        double titleWanted = hasText ? layout.TextWidth : 0;
        double shortest = CompactMusicLayout.Whole(CompactMusicLayout.MinimumText * scale);
        double titleShare = hasText ? Math.Min(titleWanted, Math.Max(shortest, CompactMusicLayout.Whole(CompactSpacing.TitleMax * scale))) : 0;
        double titleFloor = hasText ? Math.Min(shortest, titleWanted) : 0;
        var ringWidths = apps.Select(_ => layout.RingSize).ToList();
        bool hasRings = ringWidths.Count > 0, hasReadings = readingWidths.Count > 0;
        int parts = (hasCover ? 1 : 0) + (hasEqualizer ? 1 : 0) + (hasClock ? 1 : 0) + (hasText ? 1 : 0) + (hasRings ? 1 : 0) + (hasReadings ? 1 : 0);
        double others = (hasCover ? layout.CoverSize : 0) + (hasEqualizer ? layout.EqualizerSize : 0) + (hasClock ? clock!.ReservedWidth : 0) + Math.Max(0, parts - 1) * unit;
        double pool = rowWidth - others - titleShare;
        // Too short for both narrowest windows, the title gives way down to its shortest.
        double needed = CompactSpacing.Minimum(ringWidths, layout.RingGap) + CompactSpacing.Minimum(readingWidths, strip?.Gap ?? 0);
        if (pool < needed) pool += Math.Min(needed - pool, Math.Max(0, titleShare - titleFloor));
        var (rings, readings) = CompactSpacing.Share(ringWidths, layout.RingGap, readingWidths, strip?.Gap ?? 0, pool);
        // While the surface is dragged the windows take the room as it comes, an item cut at the edge, and ease back to whole items
        // when it settles (MainWindow.CompactFlow.cs): the row follows the pointer as 1.2.1's did, with no jump per item.
        double flow = CompactFlow();
        double ringCut = 0, readingCut = 0;
        if (flow <= 0) { _restRings = rings.Window; _restReadings = readings.Window; _restHeld = true; }
        else
        {
            var live = CompactSpacing.ShareFlowing(ringWidths, layout.RingGap, readingWidths, strip?.Gap ?? 0, pool);
            // While the flow rises the windows start from those of the rest; while it falls, from the whole items of the width it stopped at.
            bool rising = FlowRising;
            if (!rising) _restHeld = false;
            else if (!_restHeld) { _restRings = rings.Window; _restReadings = readings.Window; _restHeld = true; }
            (int Count, double Window, int Gaps, double Travel) Blend((int Count, double Window, int Gaps, double Travel) whole, (int Count, double Window, int Gaps, double Travel) now, double rest)
                => (now.Count, RowFlow.Window(rising && rest >= 0 ? rest : whole.Window, now.Window, flow), 0, 0);
            rings = Blend(rings, live.First, _restRings); readings = Blend(readings, live.Second, _restReadings);
            ringCut = CompactSpacing.CutFade(ringWidths, layout.RingGap, rings.Window, Math.Min(layout.RingEdge, unit));
            readingCut = CompactSpacing.CutFade(readingWidths, strip?.Gap ?? 0, readings.Window, Math.Min(Snap(layout.TextEdge, dpi), unit));
        }
        var shown = new List<(object Part, CompactSpacing.Part Size)>();
        if (hasCover) shown.Add(("cover", new(layout.CoverSize)));
        foreach (var part in Settings.CompactOrder(EffectiveStyle))
        {
            if (part == CompactPart.Rings && rings.Window > 0) shown.Add((part, new(rings.Window)));
            else if (part == CompactPart.Music && hasText) shown.Add((part, new(0)));
            else if (part == CompactPart.Monitor && readings.Window > 0) shown.Add((part, new(readings.Window)));
        }
        if (hasEqualizer) shown.Add(("equalizer", new(layout.EqualizerSize)));
        if (hasClock) shown.Add(("clock", new(clock!.ReservedWidth)));
        int title = shown.FindIndex(entry => entry.Part is CompactPart.Music);
        // One gap between every two parts; what is left widens them evenly and then centres the row.
        var place = CompactSpacing.Arrange([.. shown.Select(entry => entry.Size)], title, titleWanted, shortest, grid.Width, unit, scale, dpi);
        bool animate = Soft.Enabled;
        for (int i = 0; i < shown.Count; i++)
        {
            double left = place.Left[i], room = place.Room[i];
            FrameworkElement element;
            switch (shown[i].Part)
            {
                case "cover":
                    var cover = MusicVisuals.Cover(_mediaCover, layout.CoverSize, layout.CoverRadius); AutomationProperties.SetName(cover, _mediaTrack!.Caption);
                    element = CompactRegion(cover, room, layout.CoverSize, layout.CoverSize, row, RegionAnchor.Left, dpi); break;
                case "equalizer":
                    // The bars' levels carry over from the row before (EqualizerBars), so rebuilding the row while resizing never resets them.
                    _compactEqualizer = new MusicEqualizer { Bars = 4, Levels = _compactBars, Width = layout.EqualizerSize, Height = layout.EqualizerHeight, BarWidth = layout.EqualizerBar,
                        Color = Settings.MusicCoverColor ? _musicAccent : UI.Brush(PanelTheme.Hex("equalizer")) };
                    element = CompactRegion(_compactEqualizer, room, layout.EqualizerSize, layout.EqualizerHeight, row, RegionAnchor.Left, dpi); break;
                case "clock":
                    element = CompactRegion(clock!, room, clock!.ReservedWidth, clock.Height, row, RegionAnchor.Left, dpi); _compactClock = clock; break;
                case CompactPart.Rings:
                    element = _quotaCarousel = new QuotaCarousel(QuotaRings(apps), layout.RingSize, layout.RingThickness, layout.RingGap, rings.Count, Math.Min(layout.RingEdge, place.Gap), scale, _quotaElapsed, animate,
                        flow > 0 ? rings.Window : null, ringCut); break;
                case CompactPart.Music:
                    // The title's scroll position carries over from the row before (MarqueeScroll).
                    _compactMarquee = new MusicMarquee { Text = text, Scroll = _compactScroll, Width = room, Height = layout.TextHeight, FontSize = layout.TextFont, EdgeWidth = layout.TextEdge,
                        ScrollAllowed = layout.TextScrolls, Backdrop = PanelTheme.MarqueeBackdrop, InkFade = PanelTheme.Current.HasOverlay };
                    _compactMarquee.Gliding += _ => UpdateMusicTimer();
                    element = _compactMarquee; break;
                default:
                    // The readings that fit stand still; more scroll through the window. Either way they need sampling.
                    strip!.Margin = new Thickness(0); _compactStrip = strip;
                    // The fades lie over the gaps beside the window, no wider than those.
                    // A window narrower than the readings clips them from their start; the strip alone, narrower than what it draws, would
                    // show the middle of them (centred).
                    if (readings.Travel > 0 || readingCut > 0 || flow > 0 && strip.PreferredWidth > readings.Window + .5) element = _stripCarousel = new ReadingsCarousel(strip, readings.Window, readings.Travel, Math.Min(Snap(layout.TextEdge, dpi), place.Gap), _stripElapsed, animate, readingCut);
                    else { strip.Width = room; element = strip; }
                    break;
            }
            // The carousels' fades lie outside their window (a negative margin); the margin puts the window at its place.
            element.Margin = new Thickness(element.Margin.Left + left, 0, element.Margin.Right, 0);
            element.HorizontalAlignment = HorizontalAlignment.Left; element.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(element);
        }
        CompactRings.Children.Add(grid); UpdateMusicTimer();
        // The readings were built with the row; they start or stop the sampler and take the newest reading.
        _stripShowing = false; UpdateMonitorDemand();
    }
    // Whether an application's quota has anything read, which its collapsed ring needs.
    private bool HasQuotaReading(AppEntry entry) => DashboardUsage.Quota(entry, Snapshots).Metrics.Count > 0;
    // One ring per application: the highest used share among its quotas, with every quota in the tooltip, and the application's
    // letter inside so the rings are told apart by more than their color.
    private (double Used, string Color, string Tip, string Letter)[] QuotaRings(IReadOnlyList<AppEntry> apps) => apps.Select(entry =>
    {
        var snapshot = DashboardUsage.Quota(entry, Snapshots);
        var percentages = snapshot.Metrics.Where(m => m.UsedPercent is not null).Select(m => Math.Clamp(m.UsedPercent!.Value, 0, 100)).ToArray();
        double used = percentages.Length > 0 ? percentages.Max() : snapshot.Metrics.Count > 0 ? 100 : 0;
        string tip = entry.Name + " · " + (percentages.Length > 0 ? Loc.T($"已用 {used:0.#}%", $"{used:0.#}% used")
            : snapshot.Metrics.Count > 0 ? string.Join(Loc.T("；", "; "), snapshot.Metrics.Select(m => m.ValueText.Length > 0 ? m.ValueText + " " + m.ValueSuffix : Loc.Label(m.Label))) : snapshot.StatusLabel);
        return (used, PanelTheme.QuotaTheme(entry), tip, QuotaCarousel.Letter(entry.Name));
    }).ToArray();
    private void UpdateMusicTimer()
    {
        bool musicVisible = MusicPresentationVisible;
        _app.PresentMedia(this, musicVisible && !CompactMusicRests);
        bool progressVisible = !_expanded && EffectiveStyle == CompactStyle.Line && Settings.MusicIndicatorProgress && _mediaTrack is { HasTimeline: true, State: not MediaState.Stopped };
        bool animations = MusicMotion;
        // A paused song rests at the start of its line; an endless scroll would keep the window composing for hours. So does a
        // display that is off: the line rests until it is back.
        _compactMarquee?.SetScrolling(animations && MusicAudioActive && IsVisible && !IsSuppressed && !_expanded && _alert is null && EffectiveStyle != CompactStyle.Line && !CompactMusicRests);
        UpdateLyricScrolling();
        UpdateQuotaCarousel();
        bool needed = IsVisible && !IsSuppressed && !ScreenOff && (MusicAudioActive && (musicVisible || progressVisible) || _trackNoticeUntil > DateTimeOffset.UtcNow);
        // Frame-rate ticks only drive visible equalizer bars; the line indicator's progress moves a pixel every few seconds.
        bool bars = animations && MusicAudioActive && musicVisible && !CompactMusicRests;
        // Without bars the tick only moves the open page's position and ends a track notice; collapsed, nothing else it drives
        // shows more often than once a second (the indicator's progress moves a pixel every few seconds).
        _musicTick.Interval = TimeSpan.FromMilliseconds(bars ? BarsMs : _expanded || _trackNoticeUntil > DateTimeOffset.UtcNow ? 250 : 1000);
        // One analysis of the captured audio per move of the bars, the fastest any panel shows them at.
        _media?.SetBarsInterval(_app.Decks.Any(deck => deck.IsExpanded) ? 33 : CompactBarsMs);
        // While the collapsed title glides, WPF draws a frame every refresh for it; the bars move on those frames (MusicFrame), so
        // the timer's own frames, which fell between them, are not drawn as well. In its rests, and when it does not scroll, the
        // timer runs as before.
        if (needed && bars && TitleGliding)
        {
            _musicTick.Stop();
            if (!_barsOnFrames) { _barsOnFrames = true; Motion.Start(_barsClock, MusicFrame); }
        }
        else
        {
            _barsOnFrames = false;
            if (needed) { if (!_musicTick.IsEnabled) { _lastMusicTick = DateTimeOffset.UtcNow; _musicTick.Start(); } }
            else _musicTick.Stop();
        }
        if (_expanded || EffectiveStyle != CompactStyle.Line || !Settings.MusicIndicatorProgress || _mediaTrack is not { HasTimeline: true, State: not MediaState.Stopped }) MusicIndicator.Visibility = Visibility.Collapsed;
    }
    // The panel closed: a collapsed title that has rested scrolls its laps again, as the row comes back into view. (Reaching
    // the island opens the panel, so the collapsed title is not seen while the pointer is on it.)
    private void ReplayCompactTitle() { if (_compactMarquee is not null) _compactMarquee.Replay(); else _compactScroll.Replay(); }
    // How often the bars move: 30 times a second on the music page; 15 on the collapsed row, where each move redraws the whole
    // layered island and the bars are a few device pixels tall (measured: the row's bars and title cost about 50 ms of CPU a
    // second while music played, the largest part of the idle panel; 20 a second still cost about 20).
    private const int CompactBarsMs = 66;
    private int BarsMs => _expanded ? 33 : CompactBarsMs;
    // Over a full-screen window (a video, a game in a borderless window) the collapsed row's music rests: the bars stand at rest,
    // the title does not scroll and the audio is not read. What is watched there is the window, not the island; the open panel
    // moves as ever.
    private bool CompactMusicRests => !_expanded && _context is ScreenContext.Borderless or ScreenContext.Exclusive;
    private bool TitleGliding => !_expanded && _alert is null && _compactEqualizer is not null && _compactMarquee?.IsGliding == true;
    // The bars on the display's frames (Motion), keyed by an element of their own so a row built again never adds a second step.
    // _musicFrameAt is the frame time (Motion.FrameTime) the bars last moved at.
    private readonly UIElement _barsClock = new();
    private bool _barsOnFrames;
    private double _musicFrameAt = double.NegativeInfinity;
    // A display frame while the collapsed title glides: the bars move once at least a timer's interval has passed (a few
    // milliseconds early, so a 144 Hz frame that comes just short of it does not wait for the next). Once the title lets go of
    // the clock, or the row is built again, the timer takes over again.
    private bool MusicFrame(double now)
    {
        if (!_barsOnFrames) return false;
        if (!TitleGliding) { _barsOnFrames = false; UpdateMusicTimer(); return _barsOnFrames; }
        double time = Motion.FrameTime;
        if (time - _musicFrameAt >= BarsMs / 1000d - .004 || time < _musicFrameAt) { _musicFrameAt = time; TickMusic(); }
        return _barsOnFrames;
    }
    private void UpdateLyricScrolling() => _lyricCurrent?.SetScrolling(MusicMotion && MusicAudioActive && IsVisible && !IsSuppressed && _expanded && IsMusicPage);
    // The carousel runs its own timer and WPF animation; it only needs to know when it may move.
    private void UpdateQuotaCarousel()
    {
        bool running = IsVisible && !IsSuppressed && !ScreenOff && !_expanded && _alert is null;
        _quotaCarousel?.SetRunning(running, _pointerInside);
        _stripCarousel?.SetRunning(running, _pointerInside);
    }
    // What moves with the music (the marquees and the bars) moves only while motion is allowed and the display is on: with the
    // screen off and the session unlocked, nothing of it is seen.
    private static bool MusicMotion => Soft.Enabled && !ScreenOff;
    // The display is off by either watch: the motion environment's (ambient light, notices) or DisplayPower's (readings, music).
    private static bool ScreenOff => MotionEnvironment.DisplayOff || DisplayPower.Off;
    private bool MusicPresentationVisible => IsVisible && !IsSuppressed && !ScreenOff && _mediaTrack is { State: not MediaState.Stopped }
        && (_expanded ? IsMusicPage : Settings.ShowsMusic(EffectiveStyle) && _alert is null);
    // The display went off or came back on (DisplayPower): what runs only to be seen stops or starts again.
    // A track notice that ran out while the display was off (the music tick that ends it was stopped) is cleared first, so the
    // display comes back to the row as it is now.
    private void DisplayPowerChanged()
    {
        if (!DisplayPower.Off && _trackNoticeUntil != default && DateTimeOffset.UtcNow >= _trackNoticeUntil) { _trackNoticeUntil = default; RenderCompact(); }
        UpdateMusicTimer(); UpdateMonitorDemand();
    }
    private bool MusicAudioActive => _mediaTrack is { State: MediaState.Playing };
    private void TickMusic()
    {
        var now = DateTimeOffset.UtcNow; double seconds = Math.Min(.5, (now - _lastMusicTick).TotalSeconds); _lastMusicTick = now;
        // motion-review F18: the bars rest whenever the music's motion does (animations off, the display off).
        bool playing = MusicMotion && MusicAudioActive && !CompactMusicRests;
        bool musicVisible = MusicPresentationVisible;
        _app.PresentMedia(this, musicVisible && !CompactMusicRests);
        float peak = musicVisible ? _media?.Peak() ?? 0 : 0;
        // Captured audio drives the flowing bars; the session peak is the fallback.
        bool flowing = musicVisible && _media?.FlowLevels(_flowLevels) == true;
        if (_expanded && IsMusicPage) { if (flowing) _musicEqualizer?.Show(_flowLevels, playing); else _musicEqualizer?.Tick(peak, playing, seconds); }
        if (!_expanded && _alert is null) { if (flowing) _compactEqualizer?.Show(_flowLevels, playing); else _compactEqualizer?.Tick(peak, playing, seconds); }
        if (_trackNoticeUntil != default && now >= _trackNoticeUntil) { _trackNoticeUntil = default; RenderCompact(); }
        if ((now - _positionTick).TotalMilliseconds >= 250) { _positionTick = now; UpdateMusicPosition(); }
    }
    private void DisposeMusic() { CloseSourceMenu(); _musicTick.Stop(); _barsOnFrames = false; _lyricsRequest?.Cancel(); _app.MediaChanged -= MediaChanged; _app.Lyrics.AppleLyricsWritten -= AppleLyricsWritten; DisplayPower.Changed -= DisplayPowerChanged; _app.PresentMedia(this, false); }
}
