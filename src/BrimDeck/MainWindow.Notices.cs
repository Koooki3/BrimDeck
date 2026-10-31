using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;
using static BrimDeck.Tracks;
using Track = BrimDeck.Core.Notices.NoticeMotion.Track;

namespace BrimDeck;

// The panel's part of dynamic notices: its lane in the attention coordinator (notices, quota alerts and track changes take
// turns on its island), the notice shown on its island (IslandNotice), and the notices page.
public partial class MainWindow
{
    private static readonly TimeSpan TrackNoticeTime = TimeSpan.FromSeconds(4);
    private IslandNotice? _noticeWindow;
    private bool _noticesSubscribed, _noticesRenderQueued, _keepPageOnOpen;
    // Notices not seen before the page opened; they keep their dot while it stays open.
    private HashSet<string> _freshNotices = [];
    // The rows the page lists, with the revision each was built from, and what else the page was built from (see UpdateNoticeRows).
    private readonly List<(Notice Notice, int Revision, string Key, FrameworkElement Row)> _pageRows = [];
    private string _pageKey = "";
    // The settings, size and theme the page was last built for (NoticeLayout).
    private (DeckSettings? Settings, int Theme, double Width, double Height, double UsageWidth) _noticeLayout;
    // The empty page's text block, whose width follows the page's (FitNoticeLayout).
    private FrameworkElement? _pageEmpty;
    // A drag of the page's edges is being saved (FinishResize): the settings that come back are a copy of those the page was
    // fitted to during the drag, so it is not built again for them.
    private bool _noticeResizeSaved;

    // The coordinator's name for this panel: the main panel's stays the same when its monitor changes.
    internal string NoticeLane => _lead is null ? "main" : "display:" + (Display?.Id ?? "");
    internal long NoticeMonitor => MonitorHandle.ToInt64();
    // ui-2: nothing starts while the panel powers off; it draws back into the dot in sight (PowerOff ends a shown notice).
    internal bool CanShowNotices => IsVisible && !IsSuppressed && !_preview && !_poweringOff;
    private bool NoticeCovering => _noticeWindow?.Covering == true;

    private void SubscribeNotices()
    {
        if (_noticesSubscribed || _app.Notices is null) return;
        _noticesSubscribed = true;
        _app.Notices.Changed += NoticesChanged;
        _app.Notices.Icons.Loaded += NoticeIconLoaded;
        PrepareNoticeWindow();
    }

    // The first notice builds the notice window (its window, its theme's materials, its fonts), which took the UI thread tens of
    // milliseconds in the middle of the notice's own arrival (live check 10-28: 70 ms in the first notice, 8 to 26 ms in later
    // ones). It is built a few seconds after the panel has started, while nothing moves, with no window shown.
    private void PrepareNoticeWindow()
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle, Dispatcher) { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_noticesSubscribed && _noticeWindow is null && !_expanded && !_booting && !_bootWaiting && CanShowNotices && Settings.NoticesEnabled) (_noticeWindow = new IslandNotice(this, _app)).Prepare();
        };
        timer.Start();
    }

    private void CloseNotices()
    {
        if (_noticesSubscribed) { _app.Notices.Changed -= NoticesChanged; _app.Notices.Icons.Loaded -= NoticeIconLoaded; }
        _noticesSubscribed = false;
        if (_app.Notices is not null) { _liveRows.Clear(); WatchPageLive(); }
        _noticeWindow?.Close(); _noticeWindow = null;
        _bootNotice?.Stop(); _noticeAfterBoot = null;
        _app.Notices?.DeckRetired(this);
    }

    // ----- The coordinator's steps for this panel -----

    internal void AttentionStarted(AttentionItem item)
    {
        switch (item.Kind)
        {
            case AttentionKind.Notice or AttentionKind.Summary:
                if (!CanShowNotices) { _app.Notices.Dismiss(this, reportToSource: false); return; }
                // ui-2: one that arrives while the panel boots (the summary of what came in while BrimDeck was quiet) waits for
                // the boot to play; covering the island then would hide the boot, and the island would show in the wrong shape.
                // Likewise while the panel is still closing: the notice would take the island's final shape in one frame and the
                // half-closed panel would vanish under it (N50-4).
                if (HoldsNotices) { ShowAfterBoot(item); return; }
                (_noticeWindow ??= new IslandNotice(this, _app)).Present(item);
                break;
            case AttentionKind.Quota when item.Payload is CompactAlertInfo alert:
                PresentAlert(alert); break;
            case AttentionKind.Track:
                _trackNoticeUntil = DateTimeOffset.UtcNow + item.Duration;
                RenderCompact(); UpdateMusicTimer();
                break;
        }
    }

    internal void AttentionEnded(AttentionItem item)
    {
        switch (item.Kind)
        {
            case AttentionKind.Notice or AttentionKind.Summary:
                if (_noticeAfterBoot?.Key == item.Key) _noticeAfterBoot = null;
                _noticeWindow?.End(item);
                // Ended because the panel opened: no exit motion over the opening panel.
                if (_expanded) _noticeWindow?.Abort();
                break;
            case AttentionKind.Quota when item.Payload is CompactAlertInfo alert: EndAlert(alert); break;
            case AttentionKind.Track:
                if (_trackNoticeUntil == default) break;
                _trackNoticeUntil = default; RenderCompact(); UpdateMusicTimer();
                break;
        }
    }

    internal void AttentionUpdated(AttentionItem item)
    {
        if (item.IsNotice && _noticeAfterBoot?.Key == item.Key) _noticeAfterBoot = item;
        else if (item.IsNotice) _noticeWindow?.Update(item);
        // The same quota rose again while its alert shows: the alert shows the new level.
        else if (item.Kind == AttentionKind.Quota && item.Payload is CompactAlertInfo alert && _alert is not null) { _alert = alert; RenderCompact(); }
    }

    private AttentionItem? _noticeAfterBoot;
    private System.Windows.Threading.DispatcherTimer? _bootNotice;
    // win2 P4: when the held notice started waiting. A boot that never ends (its clock replaced, frames stopped on a locked
    // session) holds it no longer than BootNoticeLimit; the notice then shows over whatever the island is.
    private long _bootNoticeSince;
    private const int BootNoticeLimit = 10_000;
    // When the panel's closing ends (SetExpanded); 0 when it is not closing.
    private long _collapseEnds;
    // A notice waits for the boot to play and for the panel to finish closing.
    private bool HoldsNotices => BootHoldsPointer || Environment.TickCount64 < _collapseEnds;

    // Shows the item once the boot has played and the panel has closed; ended or updated meanwhile (AttentionEnded,
    // AttentionUpdated), the latest wins.
    private void ShowAfterBoot(AttentionItem item)
    {
        _noticeAfterBoot = item;
        if (_bootNotice is null)
        {
            _bootNotice = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, Dispatcher);
            _bootNotice.Tick += (_, _) =>
            {
                long now = Environment.TickCount64;
                if (HoldsNotices && _noticeAfterBoot is not null && now - _bootNoticeSince < BootNoticeLimit)
                {
                    // Once the boot has started (or the closing) its end is known: the next look is then, not every 100 ms.
                    _bootNotice.Interval = BootNoticeWait(now);
                    return;
                }
                _bootNotice.Stop();
                if (_noticeAfterBoot is not { } waiting) return;
                _noticeAfterBoot = null;
                if (!CanShowNotices || _expanded) { _app.Notices.Dismiss(this, reportToSource: false); return; }
                (_noticeWindow ??= new IslandNotice(this, _app)).Present(waiting);
            };
        }
        if (!_bootNotice.IsEnabled) _bootNoticeSince = Environment.TickCount64;
        _bootNotice.Interval = BootNoticeWait(Environment.TickCount64);
        _bootNotice.Start();
    }

    // Until the boot's or the closing's end, or a short look while the boot still waits for an idle dispatcher (Boot), never
    // past the limit.
    private TimeSpan BootNoticeWait(long now)
    {
        long ends = Math.Max(_bootEnds, _collapseEnds);
        long wait = _bootWaiting || ends <= now ? 100 : ends - now + 1;
        return TimeSpan.FromMilliseconds(Math.Clamp(wait, 1, Math.Max(1, _bootNoticeSince + BootNoticeLimit - now)));
    }

    // While a notice covers the island, the island itself is not drawn (see IslandNotice.Present and Finish). A notice that has
    // gone back into the island (reveal) leaves it without content; the island's own content then lands as it does when the
    // panel closes (SetExpanded's compact track), instead of appearing all at once.
    // ui-2: being covered is its own state, shown through HoverZone's Visibility; the boot and the power-off own its Opacity.
    // The island shows only when both allow it, and neither undoes the other.
    private bool _islandCovered;
    internal void SetIslandCovered(bool covered, bool reveal = false)
    {
        _islandCovered = covered;
        HoverZone.Visibility = covered ? Visibility.Hidden : Visibility.Visible;
        if (covered || !_compactFaded) return;
        // The island's own content faded out for the notice (FadeIslandContent) comes back: by its opacity alone once the notice ran
        // out, at once when the panel opens over it (its opening moves the content itself).
        _compactFaded = false;
        bool fade = reveal && Soft.Enabled && !_expanded && !_poweringOff && !BootHoldsPointer;
        if (fade) Animate(Compact, OpacityProperty, 1, IslandNotice.FadeIn, new Track(0, 1), Soft.Out);
        else SetImmediate(Compact, OpacityProperty, 1d);
    }

    // The island's own content fades out before a notice takes the island, so the notice's words come in after it rather than over
    // it; done follows once it has gone (at once when nothing fades: the indicator has no content, an open panel, no animations).
    private bool _compactFaded;
    internal void FadeIslandContent(Action done)
    {
        if (_expanded || _poweringOff || BootHoldsPointer || !Soft.Enabled || Compact.Visibility != Visibility.Visible || Collapsed.Style == CompactStyle.Line) { done(); return; }
        _compactFaded = true;
        Animate(Compact, OpacityProperty, 0, IslandNotice.FadeOut, new Track(0, 1), Soft.InOut, done);
    }

    // The island moved or changed style, or the theme changed: a shown notice follows.
    private void NoticesLayoutChanged() => _noticeWindow?.Refresh();

    // The notices hotkey with a notice on this panel: its details take the keyboard.
    internal bool NoticeKeyboard()
    {
        if (_noticeWindow?.Item is null) return false;
        _noticeWindow.EnterKeyboard();
        return true;
    }

    // Opens the panel on the notices page, as the expand hotkey opens it: it stays open until the pointer has been in it.
    // byKey: the notices hotkey, rather than the pointer resting on a notice on the island. focus: the notice to put in front.
    internal void OpenNoticesPage(bool byKey = true, string? focus = null)
    {
        if (!DeckPages.Shown(Settings, DeckPage.Notices)) return;
        if (focus is not null) _noticeFocus = focus;
        if (_expanded) { SelectPage(DeckPage.Notices); if (focus is not null) RenderNotices(); return; }
        SelectPage(DeckPage.Notices, remember: false);
        // Opening marks the list seen before it draws the page (SetExpanded): marked here afterwards, the page was built a second
        // time in the middle of the opening.
        _keepPageOnOpen = true;
        try { OpenFromHotkey(byKey); } finally { _keepPageOnOpen = false; }
    }

    private void NoticesPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Notices);

    // The page is on screen: everything listed counts as seen; what was new keeps its dot until the page closes.
    private void NoticesPageShown()
    {
        _freshNotices = _app.Notices.Hub.Notices.Where(n => !n.Viewed).Select(n => n.Id).ToHashSet();
        _noticeEarlier = false;
        _app.Notices.Hub.MarkAllViewed();
    }

    private void NoticesChanged()
    {
        UpdateNoticeBadge();
        if (!_expanded || CurrentPage != DeckPage.Notices || _noticesRenderQueued) return;
        // Several changes in a row (a burst, icons arriving) are drawn once.
        _noticesRenderQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            // Marked viewed while the render is still queued: the change that raises is not drawn a second time (perf-8).
            bool showing = _expanded && CurrentPage == DeckPage.Notices;
            if (showing) _app.Notices.Hub.MarkAllViewed();
            _noticesRenderQueued = false;
            if (showing && !UpdateNoticeRows()) RenderNotices();
        });
    }

    // What the page shows besides its rows; a change of it builds the whole page again.
    private string NoticePageKey(NoticeCenter center) => string.Join('|', center.Paused, center.Quiet, center.Epoch, _noticeEarlier, _noticeUnread, FocusedNotice(center)?.Id,
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_freshNotices), Loc.Language, center.WindowsState, DateTime.Today);

    // What the page's frame was built for: the settings (each change replaces them), its size and the theme.
    private (DeckSettings? Settings, int Theme, double Width, double Height, double UsageWidth) NoticeLayout()
        => (Settings, PanelTheme.Generation, ContentCanvas.Width, ContentCanvas.Height, _usageWidth);

    // RenderUsage on the notices page (N50-2). Closed, the page is not drawn at all: SetExpanded builds it as the panel opens,
    // with what arrived meanwhile. Open with the page already on screen for the same theme, a data refresh or the minute's tick
    // builds it again only when what it shows changed, and a new size only fits it (FitNoticeLayout).
    private void RenderNoticesPage()
    {
        if (!_expanded && !_preview) return;
        if (NoticesContent.Visibility == Visibility.Visible && FitNoticeLayout() && UpdateNoticeRows()) { RefreshStatus(); return; }
        RenderNotices();
    }

    // Whether the page on screen was built for these settings and this theme, and fits it to a new size if that is all that
    // changed: its two columns share the width by proportion, so a drag of the panel's edges only fits the header and the margins.
    private bool FitNoticeLayout()
    {
        var layout = NoticeLayout();
        if (_noticeLayout == layout) return true;
        bool sameSettings = ReferenceEquals(_noticeLayout.Settings, layout.Settings) || _noticeResizeSaved;
        if (!sameSettings || _noticeLayout.Theme != layout.Theme || _noticeLayout.UsageWidth != layout.UsageWidth) return false;
        if (!Settings.UsagePage) ApplyHeader(UnfittedHeader(ContentCanvas.Height));
        NoticesContent.Margin = PageMargin;
        if (_pageEmpty is { } empty) empty.MaxWidth = EmptyWidth;
        _noticeLayout = layout;
        return true;
    }

    // Whether the page on screen still shows what the notices are: the same notices in the same order, none changed and no
    // relative time moved on. False when the page needs building again.
    private bool UpdateNoticeRows()
    {
        var center = _app.Notices;
        if (_pageKey != NoticePageKey(center)) return false;
        var listed = Listed(center);
        if (listed.Count != _pageRows.Count) return false;
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < listed.Count; i++)
        {
            var (notice, revision, key, _) = _pageRows[i];
            if (!ReferenceEquals(listed[i], notice) || notice.Revision != revision || RowKey(notice, now) != key) return false;
        }
        return _focusKey == FocusKey(FocusedNotice(center), now);
    }

    private void UpdateNoticeBadge()
    {
        if (_app.Notices is null) return;
        int count = _app.Notices.Hub.UnviewedCount;
        NoticesBadge.Visibility = count > 0 && Settings.NoticesEnabled ? Visibility.Visible : Visibility.Collapsed;
        NoticesBadge.Background = UI.Brush(PanelTheme.Hex("danger"));
        AutomationProperties.SetName(NoticesPageButton, Loc.T("通知", "Notices") + (count > 0 ? " · " + NoticeText.Count(count) : ""));
    }

    // ----- The notices page -----
    //
    // Two columns, as the music page: on the left the focus, one notice at a time with its application's icon as the cover, its
    // title, its words and what can be done with it; on the right today's notices, scrolling within the panel. The focus is an
    // incoming call, else the notice the user picked (in the list, or by resting the pointer on it on the island), else the newest.
    // Without any notice, one empty state takes both columns.

    // Status colors of the design: an incoming call (and a success), a warning, an error (danger); unread dots. All from the theme,
    // which keeps each readable on its surface (PanelThemes.Tokens).
    private static string CallColor => PanelTheme.Hex("success");
    private static string WaitColor => PanelTheme.Hex("caution");
    private static string UnreadColor => UI.Accent;
    // A call stays in front for this long after it arrived.
    private static readonly TimeSpan CallFocus = TimeSpan.FromMinutes(2);
    // The notice put in front (by a click in the list, or from the island); the list shows only unread ones; earlier days are listed.
    private string? _noticeFocus;
    private bool _noticeUnread, _noticeEarlier;
    private string _focusKey = "";

    private Thickness PageMargin => new(_header.Side, 0, Math.Max(0, _header.Side - 6), 14);

    private static bool Today(Notice notice) => notice.ReceivedAt.ToLocalTime().Date == DateTime.Today;

    // The notices the right column lists: today's (or all, once earlier ones were asked for), all or only the unread ones.
    private List<Notice> Listed(NoticeCenter center)
    {
        IEnumerable<Notice> notices = center.Hub.Notices;
        if (!_noticeEarlier) notices = notices.Where(Today);
        if (_noticeUnread) notices = notices.Where(n => _freshNotices.Contains(n.Id));
        return notices.ToList();
    }

    private Notice? FocusedNotice(NoticeCenter center)
    {
        var notices = center.Hub.Notices;
        if (notices.Count == 0) return null;
        var now = DateTimeOffset.UtcNow;
        if (notices.FirstOrDefault(n => n.Call && n.SourcePresent && now - n.ReceivedAt < CallFocus) is { } call) return call;
        if (_noticeFocus is not null && notices.FirstOrDefault(n => n.Id == _noticeFocus) is { } picked) return picked;
        return notices[0];
    }

    // fresh: the page comes into view (the panel opens on it, or another page gives way to it).
    private void RenderNotices(bool fresh = false)
    {
        fresh |= NoticesContent.Visibility != Visibility.Visible;
        SubscribeNotices();
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed; NoticesContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        ApplyHeader(Settings.UsagePage ? UsageHeader(_usageWidth, UsagePageHeight) : UnfittedHeader(ContentCanvas.Height));
        double offset = fresh ? 0 : _pageScroll?.VerticalOffset ?? 0;
        // Worked from the keyboard, the page keeps the focus on the same notice when it is built again.
        string? refocus = NoticesContent.IsKeyboardFocusWithin ? _focusedNotice : null;
        NoticesContent.Children.Clear(); NoticesContent.ColumnDefinitions.Clear();
        _waitingPlates.Clear();
        _liveRows.Clear();
        _pageRows.Clear(); _pageEmpty = null; _pageScroll = null;
        NoticesContent.Margin = PageMargin;
        var center = _app.Notices;
        var now = DateTimeOffset.UtcNow;
        _pageKey = NoticePageKey(center); _noticeLayout = NoticeLayout();
        var focus = FocusedNotice(center);
        _focusKey = FocusKey(focus, now);
        if (focus is null) { NoticesContent.Children.Add(_pageEmpty = Empty(center)); WatchPageLive(); return; }
        // The columns share the width by proportion, but the list never gets less than ListMinimum: at the narrowest page (440) the
        // proportion alone left it about 170 DIP, too little for a title, a time and a header.
        NoticesContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(452, GridUnitType.Star), MinWidth = FocusMinimum });
        NoticesContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(308, GridUnitType.Star), MinWidth = ListMinimum });
        var left = FocusColumn(focus, center, now);
        NoticesContent.Children.Add(left);
        var right = ListColumn(center, focus, now);
        Grid.SetColumn(right, 1);
        NoticesContent.Children.Add(right);
        if (offset > 0 && _pageScroll is { } scroll) { scroll.UpdateLayout(); scroll.ScrollToVerticalOffset(offset); }
        if (refocus is not null && _pageRows.FirstOrDefault(r => r.Notice.Id == refocus).Row is { } focused)
            Dispatcher.BeginInvoke(() => focused.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        WatchPageLive();
    }

    private ScrollViewer? _pageScroll;
    // The height of the fade at the foot of the list, and of the one under a long text in the focus.
    private const double FadeHeight = 36, WordsFadeHeight = 18;
    // The least width of the focus and of the list (DIP).
    private const double FocusMinimum = 170, ListMinimum = 228;

    // What the focus column shows apart from what RowKey covers: its relative time and its live words.
    private string FocusKey(Notice? notice, DateTimeOffset now) => notice is null ? "" : string.Join('\u0001', notice.Id, notice.Revision, NoticeText.Relative(notice.ReceivedAt, now),
        notice.Progress, NoticeClock.IsLive(notice.Timing, now), _app.Notices.CanOpen(notice));

    // ----- The focus column -----

    private FrameworkElement FocusColumn(Notice notice, NoticeCenter center, DateTimeOffset now)
    {
        var accent = PanelTheme.Readable(center.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey)));
        var column = new Grid { Margin = new Thickness(0, 4, 20, 0), ClipToBounds = false };
        // A faint glow in the application's color behind the column, as the music page takes its color from the cover.
        // As wide as the column (and 60 beyond it to the left), not a fixed 420: a glow wider than its column is cut at the column's
        // edge where it is still visible.
        var glow = new Border
        {
            Height = 300, Margin = new Thickness(-60, -64, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
            Background = new RadialGradientBrush(Color.FromArgb(NoticeLines.GlowAlpha, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B))
        };
        column.Children.Add(glow);
        var stack = new DockPanel { LastChildFill = true };
        column.Children.Add(stack);

        // The status line: what kind of notice is in front, and how many of the same conversation it stands for.
        var group = center.Hub.Notices.Where(n => n.AppKey == notice.AppKey && n.Title.Length > 0 && n.Title == notice.Title).ToList();
        var meta = new DockPanel { Height = 20 };
        DockPanel.SetDock(meta, Dock.Top); stack.Children.Add(meta);
        var (status, color) = StatusOf(notice, center);
        if (color is not null)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(dot);
        }
        meta.Children.Add(UI.Fixed(status, 12, 20, color ?? UI.Secondary, FontWeights.SemiBold));
        string aside = group.Count > 1 ? Loc.T($"{group.Count} 条合并", $"{group.Count} merged") : NoticeText.OriginName(notice.Origin, notice.Distro);
        var asideText = UI.Fixed(aside, 11.5, 20, UI.Tertiary); asideText.HorizontalAlignment = HorizontalAlignment.Right;
        meta.Children.Add(asideText);

        // The hero: the icon as the cover, the application and when, the title, and where it came from.
        var hero = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        DockPanel.SetDock(hero, Dock.Top); stack.Children.Add(hero);
        var plate = NoticePlate(notice, 56);
        plate.VerticalAlignment = VerticalAlignment.Center;
        hero.Children.Add(plate);
        var who = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(who, 1); hero.Children.Add(who);
        who.Children.Add(UI.Fixed(notice.AppName + " · " + NoticeText.Relative(notice.ReceivedAt, now), 12, 17, UI.Secondary));
        var title = IslandNotice.Directed(UI.Fixed(notice.Title.Length > 0 ? notice.Title : notice.AppName, 19, 25, UI.Primary, FontWeights.SemiBold));
        title.TextTrimming = TextTrimming.CharacterEllipsis; title.Margin = new Thickness(0, 2, 0, 0);
        who.Children.Add(title);
        string context = notice.SourcePresent || notice.Origin == NoticeOrigin.Test ? NoticeText.CategoryName(notice.Category) : Loc.T("来源已关闭这条通知", "Closed at the source");
        if (context.Length > 0) { var ctx = UI.Fixed(context, 12, 17, UI.Tertiary); ctx.TextTrimming = TextTrimming.CharacterEllipsis; ctx.Margin = new Thickness(0, 2, 0, 0); who.Children.Add(ctx); }

        // The buttons, at the foot of the column.
        var buttons = FocusButtons(notice, center);
        DockPanel.SetDock(buttons, Dock.Bottom); stack.Children.Add(buttons);

        // The words, scrolling within the column when they are long; the other messages of the same conversation under them.
        var words = new StackPanel { Margin = new Thickness(0, 0, 0, WordsFadeHeight) };
        if (NoticeTimeLine(notice, now) is { } timeLine) { timeLine.FontSize = 13; timeLine.FontWeight = FontWeights.SemiBold; timeLine.Margin = new Thickness(0, 0, 0, 4); words.Children.Add(timeLine); }
        var privacy = Settings.NoticeRule(notice.AppKey)?.Privacy ?? Settings.NoticePrivacy;
        if (notice.Body.Length > 0 && privacy == NoticePrivacy.Full)
        {
            var body = IslandNotice.Directed(new TextBlock { Text = notice.Body, FontFamily = UI.PanelFont, FontSize = group.Count > 1 ? 14 : 13, LineHeight = group.Count > 1 ? 22 : 21, Foreground = UI.Brush(UI.Primary), TextWrapping = TextWrapping.Wrap, Opacity = .9 });
            words.Children.Add(body);
        }
        if (notice.Progress is { } progress) words.Children.Add(ProgressTrack(progress, accent));
        if (group.Count > 1)
        {
            var others = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            others.Children.Add(new Border { Height = 1, Background = UI.Brush(UI.Hairline), Margin = new Thickness(0, 0, 0, 6) });
            foreach (var other in group.Where(n => !ReferenceEquals(n, notice)).Take(20))
            {
                var line = new DockPanel { Height = 22 };
                var when = UI.Fixed(NoticeText.Relative(other.ReceivedAt, now), 11, 22, UI.Tertiary); DockPanel.SetDock(when, Dock.Right); when.Margin = new Thickness(8, 0, 0, 0);
                line.Children.Add(when);
                var text = UI.Fixed(privacy == NoticePrivacy.Full ? NoticeText.Summary(other.Body) : other.AppName, 12, 22, UI.Secondary); text.TextTrimming = TextTrimming.CharacterEllipsis;
                line.Children.Add(IslandNotice.Directed(text));
                others.Children.Add(line);
            }
            words.Children.Add(others);
        }
        var scroll = new ScrollViewer { Content = words, Margin = new Thickness(0, 14, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
        // A long text fades at the column's foot rather than being cut mid line.
        NoticeFade.Foot(scroll, WordsFadeHeight);
        stack.Children.Add(scroll);
        return column;
    }

    // The status line's words and its dot's color (null for no dot).
    private static (string Text, string? Color) StatusOf(Notice notice, NoticeCenter center)
    {
        if (notice.Call) return (Loc.T("来电", "Incoming call"), CallColor);
        if (notice.Category == NoticeCategory.Error) return (NoticeText.CategoryName(notice.Category), PanelTheme.Hex("danger"));
        if (notice.Category == NoticeCategory.Warning) return (NoticeText.CategoryName(notice.Category), WaitColor);
        bool latest = center.Hub.Notices.Count > 0 && ReferenceEquals(center.Hub.Notices[0], notice);
        return (latest ? Loc.T("最新", "Latest") : Loc.T("通知", "Notice"), null);
    }

    private static FrameworkElement ProgressTrack(double progress, Color accent)
    {
        double done = Math.Clamp(progress, 0, 1);
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var label = UI.Fixed(Loc.T($"{done:P0}", $"{done:P0}"), 11, 16, UI.Tertiary); DockPanel.SetDock(label, Dock.Right); label.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(label);
        var track = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(done, GridUnitType.Star), MinWidth = 3 });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - done, GridUnitType.Star) });
        var rail = new Border { Background = UI.Brush(UI.Track), CornerRadius = new CornerRadius(1.5) }; Grid.SetColumnSpan(rail, 2);
        track.Children.Add(rail);
        track.Children.Add(new Border { Background = new SolidColorBrush(accent), CornerRadius = new CornerRadius(1.5) });
        row.Children.Add(track);
        AutomationProperties.SetName(row, Loc.T($"进度 {done:P0}", $"Progress {done:P0}"));
        return row;
    }

    // Delete, the notice's own and BrimDeck's actions, and opening the application as the main one on the right.
    private FrameworkElement FocusButtons(Notice notice, NoticeCenter center)
    {
        // Delete and the notice's own actions, then the main button, which stays at the right; they wrap to a second line where
        // they do not fit one (at the narrowest page, with two actions), instead of being cut off (NoticeButtons).
        var row = new NoticeButtons { Margin = new Thickness(0, 12, 0, 0) };
        var flow = row.Children;
        var delete = PillButton("", Loc.T("删除这条记录", "Delete this notice"), () => { center.Delete(notice); if (_noticeFocus == notice.Id) _noticeFocus = null; RenderNotices(); });
        ((Border)delete.Content).Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = UI.Brush(UI.Primary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ((Border)delete.Content).Padding = new Thickness(0); delete.Width = 32; delete.ToolTip = Loc.T("删除这条记录", "Delete this notice");
        flow.Add(delete);
        if ((notice.Capabilities & NoticeCapabilities.Actions) != 0)
            foreach (var action in notice.Actions.Where(a => a.Key != "default").Take(2))
                flow.Add(PillButton(action.Label, action.Label, () => { center.Invoke(notice, action.Key); RenderNotices(); }));
        foreach (var action in center.TimeActions(notice).Take(2))
            flow.Add(PillButton(action.Label, action.Label, () => { center.TimeAction(notice, action.Key); RenderNotices(); }));
        if (center.CanOpen(notice))
        {
            string open = notice.Call ? Loc.T($"打开 {notice.AppName} 接听", $"Answer in {notice.AppName}") : Loc.T("打开应用", "Open app");
            var main = PillButton(open, open, () => _ = OpenFromPage(notice), primary: true);
            row.MainLast = true;
            flow.Add(main);
        }
        return row;
    }

    // A button of the page in its states: paint(over, down) is called as the pointer enters, leaves, presses and releases, so that
    // each state takes its own color from the theme (hover, pressed, selected) instead of one fill for all of them.
    private static void Feel(Button button, Action<bool, bool> paint)
    {
        bool over = false, down = false;
        button.MouseEnter += (_, _) => paint(over = true, down);
        button.MouseLeave += (_, _) => paint(over = false, down = false);
        button.PreviewMouseLeftButtonDown += (_, _) => paint(over, down = true);
        button.PreviewMouseLeftButtonUp += (_, _) => paint(over, down = false);
    }

    // A capsule button of the design: 32 high, the main one in the text color with the surface's color on it.
    private static Button PillButton(string text, string name, Action click, bool primary = false)
    {
        string rest = primary ? UI.Primary : UI.Fill;
        var face = new Border { CornerRadius = new CornerRadius(PanelTheme.Corner(16)), Background = UI.Brush(rest), Padding = new Thickness(16, 0, 16, 0), Height = 32 };
        var label = UI.InkCentered(text, 12.5, 32, UI.Primary, FontWeights.SemiBold);
        if (primary) label.Foreground = new SolidColorBrush(PanelTheme.ColorOf("surface"));
        face.Child = label;
        var button = new Button { Content = face, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0), FocusVisualStyle = (Style)Application.Current.FindResource("PanelPillKeyboardFocus"), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        if (primary) button.Margin = new Thickness(0);
        // Under the pointer the main button dims a little, the others take the selected fill; pressed, the main one dims more and
        // the others take the pressed fill.
        Feel(button, (over, down) =>
        {
            if (primary) face.Opacity = down ? .76 : over ? .88 : 1;
            else face.Background = UI.Brush(down ? PanelTheme.Hex("pressed") : over ? UI.FillSelected : rest);
        });
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, name);
        return button;
    }

    // ----- The list column -----

    private FrameworkElement ListColumn(NoticeCenter center, Notice focus, DateTimeOffset now)
    {
        var column = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var border = new Border { BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(14, 0, 0, 0), Child = column };
        // The title, its count and the tools on one line; when the column is too narrow for that the count goes under them (NoticeHead).
        var head = new NoticeHead();
        DockPanel.SetDock(head, Dock.Top); column.Children.Add(head);
        head.Children.Add(UI.Fixed(Loc.T("通知", "Notices"), 15, 20, UI.Primary, FontWeights.SemiBold));
        int today = center.Hub.Notices.Count(Today);
        string count = Loc.T($"今天 {today} 条", $"{today} today");
        if (center.Paused && center.Settings.NoticesPausedUntil is { } until) count += Loc.T($" · 暂停至 {until.ToLocalTime():HH:mm}", $" · paused until {until.ToLocalTime():HH:mm}");
        else if (center.Quiet) count += Loc.T(" · 安静中", " · quiet");
        var countText = UI.Centered(count, 11.5, UI.Tertiary);
        countText.TextWrapping = TextWrapping.Wrap; countText.LineHeight = NoticeLines.CountLine; countText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        head.Children.Add(countText);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var pause = IconToggle(center.Paused ? "" : "", center.Paused ? Loc.T("恢复提醒", "Resume") : Loc.T("暂停提醒 1 小时", "Pause for 1 h"),
            () => { center.Pause(center.Paused ? null : TimeSpan.FromHours(1)); RenderNotices(); });
        tools.Children.Add(pause);
        var switcher = new StackPanel { Orientation = Orientation.Horizontal };
        var frame = new Border { CornerRadius = new CornerRadius(6), Background = UI.Brush(UI.Fill), Padding = new Thickness(1), Child = switcher, Margin = new Thickness(6, 0, 0, 0) };
        switcher.Children.Add(Segment(Loc.T("全部", "All"), !_noticeUnread, () => { _noticeUnread = false; RenderNotices(); }));
        switcher.Children.Add(Segment(Loc.T("未读", "Unread"), _noticeUnread, () => { _noticeUnread = true; RenderNotices(); }));
        tools.Children.Add(frame);
        head.Children.Add(tools);

        var list = new StackPanel { Margin = new Thickness(-8, 8, 0, FadeHeight) };
        KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Contained);
        var listed = Listed(center);
        foreach (var notice in listed)
        {
            var row = Row(notice, now, ReferenceEquals(notice, focus));
            list.Children.Add(row);
            _pageRows.Add((notice, notice.Revision, RowKey(notice, now), row));
        }
        if (listed.Count == 0)
        {
            var none = UI.Fixed(_noticeUnread ? Loc.T("没有未读的通知", "No unread notices") : Loc.T("今天还没有通知", "No notices today"), 12, 18, UI.Tertiary);
            none.Margin = new Thickness(8, 6, 0, 0); list.Children.Add(none);
        }
        if (!_noticeEarlier && center.Hub.Notices.Any(n => !Today(n)))
        {
            var more = PageButton(Loc.T("查看更早的通知", "Show earlier notices"), () => { _noticeEarlier = true; RenderNotices(); });
            more.HorizontalAlignment = HorizontalAlignment.Center; more.Margin = new Thickness(0, 6, 0, 12);
            list.Children.Add(more);
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
        _pageScroll = scroll;
        // The list fades out at its foot into the panel, by a mask that is the same on every material (NoticeFade); the free
        // space under the last row is as high as the fade.
        NoticeFade.Foot(scroll, FadeHeight);
        column.Children.Add(scroll);
        return border;
    }

    private Button Segment(string text, bool on, Action click)
    {
        var face = new Border { CornerRadius = new CornerRadius(5), Height = 18, Padding = new Thickness(9, 0, 9, 0), Background = UI.Brush(on ? UI.FillSelected : "Transparent") };
        face.Child = UI.InkCentered(text, 11, 18, on ? UI.Primary : UI.Secondary);
        var button = new Button { Content = face, Cursor = Cursors.Hand, FocusVisualStyle = (Style)Application.Current.FindResource("PanelKeyboardFocus"), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        button.Click += (_, _) => { if (!on) click(); };
        // The chosen segment keeps its fill; the other takes the hover and pressed ones.
        Feel(button, (over, down) => face.Background = UI.Brush(down ? PanelTheme.Hex("pressed") : on ? UI.FillSelected : over ? PanelTheme.Hex("hover") : "Transparent"));
        AutomationProperties.SetName(button, text);
        return button;
    }

    private static Button IconToggle(string glyph, string name, Action click)
    {
        var face = new Border { CornerRadius = new CornerRadius(6), Width = 24, Height = 20, Background = UI.Brush("Transparent") };
        face.Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = UI.Brush(UI.Secondary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = face, Cursor = Cursors.Hand, ToolTip = name, FocusVisualStyle = (Style)Application.Current.FindResource("PanelKeyboardFocus"), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        Feel(button, (over, down) => face.Background = UI.Brush(down ? PanelTheme.Hex("pressed") : over ? PanelTheme.Hex("hover") : "Transparent"));
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, name);
        return button;
    }

    private double EmptyWidth => Math.Max(200, ContentCanvas.Width - 60);

    // No notices at all: what the page is for, whether notices are being received, and the way to the settings.
    private FrameworkElement Empty(NoticeCenter center)
    {
        var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = EmptyWidth, Margin = new Thickness(0, 0, 0, 14) };
        var bell = new Grid { Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Center };
        bell.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = UI.Brush(UI.Fill) });
        var glyph = NoticeBadges.Glyph(NoticeGlyph.Bell, 20, PanelTheme.ColorOf("textSecondary"));
        glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Center;
        bell.Children.Add(glyph);
        empty.Children.Add(bell);
        var title = UI.Fixed(Loc.T("今天还没有通知", "No notices today"), 19, 26, UI.Primary, FontWeights.SemiBold); title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 16, 0, 0);
        empty.Children.Add(title);
        string where = Collapsed.Style switch { CompactStyle.Notch => Loc.T("刘海", "the notch"), CompactStyle.Capsule => Loc.T("胶囊", "the capsule"), _ => Loc.T("指示条", "the indicator") };
        var hint = UI.Line(Loc.T($"新通知到达时{where}会闪动，鼠标移上去即可在这里查看", $"When a notice arrives, {where} flashes; rest the pointer on it to see it here"), 12.5, UI.Tertiary);
        hint.TextWrapping = TextWrapping.Wrap; hint.TextAlignment = TextAlignment.Center; hint.Margin = new Thickness(0, 5, 0, 0);
        empty.Children.Add(hint);
        var (state, color) = center.WindowsState switch
        {
            NoticeSourceState.NeedsAccess => (Loc.T("尚未允许读取 Windows 通知", "Access to Windows notifications is not allowed yet"), PanelTheme.Hex("danger")),
            NoticeSourceState.Denied => (Loc.T("Windows 已拒绝通知访问，可在系统隐私设置中开启", "Windows denies access to notifications; it can be allowed in Windows' privacy settings"), PanelTheme.Hex("danger")),
            NoticeSourceState.Off => (Loc.T("未接收 Windows 应用通知", "Not receiving Windows notifications"), UI.Tertiary),
            _ => (Loc.T("正在接收 Windows 应用通知", "Receiving Windows notifications"), CallColor)
        };
        var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        status.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        status.Children.Add(UI.Fixed(state, 11.5, 16, UI.Tertiary));
        empty.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(PillButton(Loc.T("通知设置", "Notice settings"), Loc.T("通知设置", "Notice settings"), () => _app.OpenSettings()));
        empty.Children.Add(buttons);
        return empty;
    }

    // A row of the list: the icon with a dot while unread, the title and when, and a line of preview.
    private FrameworkElement Row(Notice notice, DateTimeOffset now, bool selected)
    {
        var row = new Border { CornerRadius = new CornerRadius(PanelTheme.Corner(10)), Padding = new Thickness(8, 7, 8, 7), Background = UI.Brush(selected ? UI.FillSelected : "Transparent"), Cursor = Cursors.Hand };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        row.Child = grid;
        var icon = new Grid { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        icon.Children.Add(NoticePlate(notice, 24));
        // The unread dot has a ring in the color of what is behind it, which is the row's own fill (it changes with the row's state).
        Ellipse? dot = null;
        if (_freshNotices.Contains(notice.Id))
            icon.Children.Add(dot = new Ellipse { Width = 9, Height = 9, Fill = UI.Brush(notice.Call ? CallColor : UnreadColor), StrokeThickness = 2,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, -3, 0), IsHitTestVisible = false });
        grid.Children.Add(icon);
        // The title, the time and a line of preview; the time moves under a title it would leave too short (NoticeRowTexts).
        var texts = new NoticeRowTexts { Opacity = notice.SourcePresent || notice.Origin == NoticeOrigin.Test ? 1 : .78 };
        Grid.SetColumn(texts, 1); grid.Children.Add(texts);
        var title = IslandNotice.Directed(UI.Fixed(notice.Title.Length > 0 ? notice.Title : notice.AppName, 12.5, NoticeLines.RowTitleLine, UI.Primary, FontWeights.SemiBold));
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(title);
        var when = UI.Fixed(NoticeText.Relative(notice.ReceivedAt, now), 11, NoticeLines.RowTitleLine, UI.Tertiary);
        texts.Children.Add(when);
        var privacy = Settings.NoticeRule(notice.AppKey)?.Privacy ?? Settings.NoticePrivacy;
        string preview = privacy == NoticePrivacy.Full && notice.Body.Length > 0 ? NoticeText.Summary(notice.Body) : notice.AppName;
        var second = new TextBlock { FontFamily = UI.PanelFont, FontSize = 12, LineHeight = NoticeLines.RowPreviewLine, Foreground = UI.Brush(UI.Secondary), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        var (status, color) = notice.Call ? (Loc.T("来电", "Call"), CallColor) : notice.Category == NoticeCategory.Error ? (NoticeText.CategoryName(notice.Category), PanelTheme.Hex("danger"))
            : notice.Category == NoticeCategory.Warning ? (NoticeText.CategoryName(notice.Category), WaitColor) : ("", "");
        if (status.Length > 0) second.Inlines.Add(new System.Windows.Documents.Run(status + " · ") { Foreground = UI.Brush(color) });
        if (NoticeClock.Live(notice.Timing, now, TimeZoneInfo.Local) is { } live) second.Inlines.Add(new System.Windows.Documents.Run(live + " · ") { Foreground = UI.Brush(UI.Accent) });
        second.Inlines.Add(new System.Windows.Documents.Run(preview));
        IslandNotice.Directed(second);
        texts.Children.Add(second);
        // The row's states: the chosen one has the selected fill, one under the pointer the hover fill, one being pressed the pressed
        // fill. Its time is drawn in the secondary text on any of them (the tertiary text is not held to 4.5:1 on those fills).
        bool over = false, down = false;
        void Paint()
        {
            string? fill = down ? "pressed" : selected ? "fillSelected" : over ? "hover" : null;
            row.Background = UI.Brush(fill is null ? "Transparent" : PanelTheme.Hex(fill));
            when.Foreground = UI.Brush(fill is null ? UI.Tertiary : UI.Secondary);
            dot?.Stroke = UI.Brush(RowGround(fill));
        }
        Paint();
        row.MouseEnter += (_, _) => { over = true; Paint(); };
        row.MouseLeave += (_, _) => { over = false; down = false; Paint(); };
        row.PreviewMouseLeftButtonDown += (_, _) => { down = true; Paint(); };
        row.MouseLeftButtonUp += (_, e) => { _noticeFocus = notice.Id; RenderNotices(); e.Handled = true; };
        // From the keyboard, once a hotkey gave the panel the keyboard (MainWindow.Hotkeys.cs): Enter or Space puts the notice in front.
        row.Focusable = true;
        row.FocusVisualStyle = (Style)Application.Current.FindResource("PanelRowKeyboardFocus");
        row.GotKeyboardFocus += (_, e) => { if (e.NewFocus == row) _focusedNotice = notice.Id; };
        row.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space) || e.OriginalSource != row) return;
            _noticeFocus = notice.Id; RenderNotices(); e.Handled = true;
        };
        AutomationProperties.SetName(row, notice.AppName + ", " + notice.Title + ", " + NoticeText.Relative(notice.ReceivedAt, now));
        return row;
    }

    // What a row's fill looks like over the panel, as an opaque color: the color behind the unread dot's ring.
    private static string RowGround(string? fill)
    {
        var surface = PanelTheme.Current.Surface;
        return (fill is null ? surface : PanelTheme.Current[fill].Over(surface)).ToString();
    }

    // Everything a row shows.
    private string RowKey(Notice notice, DateTimeOffset now)
        => string.Join('\u0001', notice.Title, notice.Body, notice.AppName, notice.IconRef, notice.Picture, notice.SourcePresent, notice.Category, notice.Call,
            NoticeClock.Live(notice.Timing, now, TimeZoneInfo.Local), NoticeText.Relative(notice.ReceivedAt, now), _freshNotices.Contains(notice.Id));

    // The notice whose row last had the keyboard focus, to keep it there when the page is built again.
    private string? _focusedNotice;

    private async Task OpenFromPage(Notice notice)
    {
        if (await _app.Notices.OpenAsync(notice)) { SetExpanded(false); return; }
        MessageHint(Loc.T("无法打开该应用；它可能不支持从通知启动。", "The application could not be opened; it may not support being started from a notice."));
    }

    private void MessageHint(string text)
    {
        if (NoticesContent.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<DockPanel>().FirstOrDefault() is not { } column) return;
        var hint = UI.Line(text, 11, PanelTheme.Hex("danger")); hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(hint, Dock.Bottom);
        column.Children.Insert(0, hint);
    }

    // Plates on the notices page still waiting for their icon. An icon that arrives fills only its own plates: rebuilding the
    // page for it would ask again for icons the cache let go of, whose arrival would rebuild it again.
    private readonly Dictionary<string, List<(Grid Plate, Notice Notice)>> _waitingPlates = new(StringComparer.Ordinal);

    private void NoticeIconLoaded(string iconRef)
    {
        if (!_waitingPlates.Remove(iconRef, out var plates)) return;
        foreach (var (plate, notice) in plates) FillPlate(plate, notice);
    }

    // The application's icon, or its letter until the icon arrives.
    private FrameworkElement NoticePlate(Notice notice, double size)
    {
        var plate = new Grid { Width = size, Height = size };
        if (!FillPlate(plate, notice))
            foreach (var reference in new[] { notice.IconRef, NoticeLetter.ShowsPicture(notice, Settings) ? notice.Picture : null })
            {
                if (reference is null) continue;
                if (!_waitingPlates.TryGetValue(reference, out var plates)) _waitingPlates[reference] = plates = [];
                plates.Add((plate, notice));
            }
        return plate;
    }

    // True when the plate shows the icon; false when it shows the initial (the icon may still arrive). The focus's cover (56) holds
    // the icon on a faint plate, as the music page holds its cover; the list's rows show the icon itself.
    private bool FillPlate(Grid plate, Notice notice)
    {
        double size = plate.Width;
        bool cover = size >= 48;
        plate.Children.Clear();
        // A contact's avatar, round, as Windows shows it; on the focus's cover the application's icon sits on its corner.
        if (NoticeLetter.ShowsPicture(notice, Settings) && _app.Notices.Icons.Get(notice.Picture, out _) is { } avatar)
        {
            plate.Children.Add(NoticeLetter.Round(avatar, size));
            if (cover && _app.Notices.Icons.Get(notice.IconRef, out _) is { } logo)
            {
                var small = new Image { Source = logo, Stretch = Stretch.Uniform, Width = 20, Height = 20 };
                // A 128 pixel icon drawn at 20 DIPs is jagged and blurred unless WPF scales it down with its best filter.
                RenderOptions.SetBitmapScalingMode(small, BitmapScalingMode.HighQuality);
                var badge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(PanelTheme.ColorOf("surface")), Padding = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -4, -4), Child = small };
                plate.Children.Add(badge);
            }
            return true;
        }
        var image = _app.Notices.Icons.Get(notice.IconRef, out _);
        if (image is null)
        {
            plate.Children.Add(NoticeLetter.Plate(notice.AppName, size, PanelTheme.Readable(_app.Notices.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey)))));
            return false;
        }
        if (cover) plate.Children.Add(new Border { CornerRadius = new CornerRadius(15), Background = UI.Brush(UI.Fill), BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(1) });
        var picture = new Image { Source = image, Width = cover ? 40 : size, Height = cover ? 40 : size, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        plate.Children.Add(picture);
        return true;
    }

    // A small flat button of the panel's kind.
    private static Button PageButton(string text, Action click)
    {
        var face = new Border { CornerRadius = new CornerRadius(6), Background = UI.Brush(UI.Fill), Padding = new Thickness(9, 0, 9, 0), Height = 22, Margin = new Thickness(4, 0, 0, 0) };
        face.Child = UI.InkCentered(text, 11.5, 22, UI.Secondary);
        var button = new Button { Content = face, Cursor = Cursors.Hand, FocusVisualStyle = (Style)Application.Current.FindResource("PanelKeyboardFocus"), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        Feel(button, (over, down) => face.Background = UI.Brush(down ? PanelTheme.Hex("pressed") : over ? UI.FillSelected : UI.Fill));
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, text);
        return button;
    }
}
