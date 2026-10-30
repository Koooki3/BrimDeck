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
// turns on its island), the notice window over its island, and the recent notices page.
public partial class MainWindow
{
    private static readonly TimeSpan TrackNoticeTime = TimeSpan.FromSeconds(4);
    private const int NoticePageStep = 40;
    private NoticeWindow? _noticeWindow;
    private bool _noticesSubscribed, _noticesRenderQueued, _keepPageOnOpen;
    private string? _expandedNotice;
    private int _noticeRows = NoticePageStep;
    // Notices not seen before the page opened; they keep their dot while it stays open.
    private HashSet<string> _freshNotices = [];
    // The rows the page lists, with the revision each was built from, and what else the page was built from (see UpdateNoticeRows).
    private StackPanel? _pageList;
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
            if (_noticesSubscribed && _noticeWindow is null && !_expanded && !_booting && !_bootWaiting && CanShowNotices && Settings.NoticesEnabled) (_noticeWindow = new NoticeWindow(this, _app)).Prepare();
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
                (_noticeWindow ??= new NoticeWindow(this, _app)).Present(item);
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
                (_noticeWindow ??= new NoticeWindow(this, _app)).Present(waiting);
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

    // While a notice covers the island, the island itself is not drawn (see NoticeWindow.Present and Finish). A notice that has
    // gone back into the island (reveal) leaves it without content; the island's own content then lands as it does when the
    // panel closes (SetExpanded's compact track), instead of appearing all at once.
    // ui-2: being covered is its own state, shown through HoverZone's Visibility; the boot and the power-off own its Opacity.
    // The island shows only when both allow it, and neither undoes the other.
    private bool _islandCovered;
    internal void SetIslandCovered(bool covered, bool reveal = false)
    {
        // A notice that ended before it had covered the island (within its first frame) leaves the island's content as it was.
        bool wasCovered = _islandCovered;
        _islandCovered = covered;
        HoverZone.Visibility = covered ? Visibility.Hidden : Visibility.Visible;
        // A boot under way lands the content itself.
        if (covered || !reveal || !wasCovered || _expanded || _poweringOff || BootHoldsPointer || Compact.Visibility != Visibility.Visible || Collapsed.Style == CompactStyle.Line) return;
        int duration = (int)(NoticeMotion.Length(Settings.AnimationDuration, Soft.Enabled) * NoticeMotion.RevealLength);
        if (duration <= 0) return;
        var land = new Track(0, 1);
        SetImmediate(Compact, OpacityProperty, 0d);
        SetImmediate(CompactScale, ScaleTransform.ScaleXProperty, .85); SetImmediate(CompactScale, ScaleTransform.ScaleYProperty, .85);
        Animate(Compact, OpacityProperty, 1, duration, land, Soft.Out);
        Animate(CompactScale, ScaleTransform.ScaleXProperty, 1, duration, land, Soft.Out);
        Animate(CompactScale, ScaleTransform.ScaleYProperty, 1, duration, land, Soft.Out);
    }

    // The panel opens from a notice (NoticeWindow.HandOver): the island takes the notice's outline, place and material at once,
    // in the frame the notice goes, and SetExpanded's move to the open panel starts from there. The compact content stays out of
    // sight, as it is leaving anyway.
    private void StartFromNotice(NoticeFrame from)
    {
        // The notice lies within the panel's window (both are centred on the island and the window holds the widest page).
        double left = Math.Clamp(from.Rect.X - _windowOffset, 0, Math.Max(0, Width - from.Rect.Width));
        SetImmediate(HoverZone, MarginProperty, new Thickness(left, 0, 0, 0));
        SetImmediate(Island, MarginProperty, new Thickness(0, Math.Max(0, from.Rect.Y), 0, 0));
        SetImmediate(Island, WidthProperty, from.Rect.Width);
        SetImmediate(Island, HeightProperty, from.Rect.Height);
        SetImmediate(Island, Border.CornerRadiusProperty, new CornerRadius(from.TopRadius, from.TopRadius, from.BottomRadius, from.BottomRadius));
        SetImmediate(Island, IslandBorder.ShoulderRadiusProperty, from.Shoulder);
        SetImmediate(Island, IslandBorder.MaterialOpacityProperty, 1d);
        SetImmediate(_surface, SolidColorBrush.ColorProperty, PanelTheme.ColorOf("surface"));
        SetImmediate(Compact, OpacityProperty, 0d);
        ResetDotPose();
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

    // Opens the panel on the recent notices page, as the expand hotkey opens it: it stays open until the pointer has been in it.
    // byKey: the notices hotkey, rather than a click on "View all".
    internal void OpenNoticesPage(bool byKey = true)
    {
        if (!DeckPages.Shown(Settings, DeckPage.Notices)) return;
        if (_expanded) { SelectPage(DeckPage.Notices); return; }
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
        _noticeRows = NoticePageStep;
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
    private string NoticePageKey(NoticeCenter center) => string.Join('|', Status(center, center.Hub.Notices.Count), center.Paused, center.Epoch, _noticeRows,
        _expandedNotice, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_freshNotices), Loc.Language, center.WindowsState);

    // What the page's frame was built for: the settings (each change replaces them), its size and the theme.
    private (DeckSettings? Settings, int Theme, double Width, double Height, double UsageWidth) NoticeLayout()
        => (Settings, PanelTheme.Generation, ContentCanvas.Width, ContentCanvas.Height, _usageWidth);

    // RenderUsage on the recent notices page (N50-2). Closed, the page is not drawn at all: SetExpanded builds it as the panel
    // opens, with what arrived meanwhile. Open with the page already on screen for the same theme, a data refresh or the
    // minute's tick builds again only the rows that changed, which keeps the pointer's hover and the scroll, and a new size
    // only fits the page (FitNoticeLayout); the switch to the page, a new theme and a change of settings build it whole.
    private void RenderNoticesPage()
    {
        if (!_expanded && !_preview) return;
        if (NoticesContent.Visibility == Visibility.Visible && FitNoticeLayout() && UpdateNoticeRows()) { RefreshStatus(); return; }
        RenderNotices();
    }

    // Whether the page on screen was built for these settings and this theme, and fits it to a new size if that is all that
    // changed. Its rows stretch with the page, as the monitor page's content does, so a drag of the panel's edges only fits the
    // header and the margins again: building all the rows anew at every step of the drag made the page lag behind the pointer.
    private bool FitNoticeLayout()
    {
        var layout = NoticeLayout();
        if (_noticeLayout == layout) return true;
        bool sameSettings = ReferenceEquals(_noticeLayout.Settings, layout.Settings) || _noticeResizeSaved;
        if (!sameSettings || _noticeLayout.Theme != layout.Theme || _noticeLayout.UsageWidth != layout.UsageWidth) return false;
        // With the usage page shown the header follows that page's size, which this drag does not change.
        if (!Settings.UsagePage) ApplyHeader(UnfittedHeader(ContentCanvas.Height));
        NoticesContent.Margin = new Thickness(_header.Side, 0, _header.Side, 10);
        if (_pageEmpty is { } empty) empty.MaxWidth = EmptyWidth;
        _noticeLayout = layout;
        return true;
    }

    // While the page lists the same notices in the same order, only the rows of notices that changed are built again: a
    // task's progress no longer rebuilds the whole page on every step (perf-7), nor the minute's tick, which builds again only
    // the rows whose relative time it changed. False when the page needs building whole.
    private bool UpdateNoticeRows()
    {
        var center = _app.Notices;
        var notices = center.Hub.Notices;
        // While the rows are still being added (FillRows), the page lists the notices it set out to add, built or not.
        var listed = _fill?.Notices;
        int count = listed?.Count ?? _pageRows.Count;
        if (_pageKey != NoticePageKey(center) || Math.Min(notices.Count, _noticeRows) != count) return false;
        // The empty page holds no rows; the key above covers all it shows.
        if (_pageList is not { } list) return notices.Count == 0 && NoticesContent.Children.Count > 0;
        for (int i = 0; i < count; i++)
            if (!ReferenceEquals(notices[i], i < _pageRows.Count ? _pageRows[i].Notice : listed![i])) return false;
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < _pageRows.Count; i++)
        {
            var (notice, revision, key, old) = _pageRows[i];
            string current = RowKey(notice, now);
            if (notice.Revision == revision && current == key) continue;
            // A task's progress alone moves the row's track: replacing the row under the pointer lost clicks on its buttons.
            if (current == key && _rowProgress.TryGetValue(old, out var shown) && notice.Progress is { } progress)
            {
                double done = Math.Clamp(progress, 0, 1);
                shown.Done.Width = new GridLength(done, GridUnitType.Star); shown.Left.Width = new GridLength(1 - done, GridUnitType.Star);
                _pageRows[i] = (notice, notice.Revision, key, old);
                continue;
            }
            int index = list.Children.IndexOf(old);
            if (index < 0) return false;
            _liveRows.RemoveAll(live => ReferenceEquals(live.Notice, notice));
            // The old row's plates no longer wait for an icon (review 10: they piled up with every rebuilt row).
            foreach (var plates in _waitingPlates.Values) plates.RemoveAll(waiting => ReferenceEquals(waiting.Notice, notice));
            var row = Row(notice, now);
            // The row under the pointer keeps its hover fill (it is drawn on MouseEnter, which the new row has not had).
            if (old.IsMouseOver && _expandedNotice != notice.Id && row is Border face) face.Background = UI.Brush(UI.Fill);
            list.Children.RemoveAt(index); list.Children.Insert(index, row);
            _pageRows[i] = (notice, notice.Revision, current, row);
        }
        WatchPageLive();
        return true;
    }

    private void UpdateNoticeBadge()
    {
        if (_app.Notices is null) return;
        int count = _app.Notices.Hub.UnviewedCount;
        NoticesBadge.Visibility = count > 0 && Settings.NoticesEnabled ? Visibility.Visible : Visibility.Collapsed;
        NoticesBadge.Background = UI.Brush(PanelTheme.Hex("danger"));
        AutomationProperties.SetName(NoticesPageButton, Loc.T("通知历史", "Notice history") + (count > 0 ? " · " + NoticeText.Count(count) : ""));
    }

    // ----- The recent notices page -----

    // fresh: the page comes into view (the panel opens on it, or another page gives way to it). It starts at the top, with its
    // first rows only; the others follow a few at a time (FillRows).
    private void RenderNotices(bool fresh = false)
    {
        fresh |= NoticesContent.Visibility != Visibility.Visible;
        StopFill();
        SubscribeNotices();
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed; NoticesContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        ApplyHeader(Settings.UsagePage ? UsageHeader(_usageWidth, UsagePageHeight) : UnfittedHeader(ContentCanvas.Height));
        double offset = fresh ? 0 : NoticesContent.Children.OfType<DockPanel>().FirstOrDefault()?.Children.OfType<ScrollViewer>().FirstOrDefault()?.VerticalOffset ?? 0;
        // Worked from the keyboard, the page keeps the focus on the same notice when it is built again (an Enter, a new notice).
        string? refocus = NoticesContent.IsKeyboardFocusWithin ? _focusedNotice : null;
        NoticesContent.Children.Clear();
        _waitingPlates.Clear();
        _liveRows.Clear();
        _pageRows.Clear(); _pageList = null; _pageEmpty = null; _stretch.Forget();
        NoticesContent.Margin = new Thickness(_header.Side, 0, _header.Side, 10);
        var center = _app.Notices;
        var notices = center.Hub.Notices;
        var page = new DockPanel();
        NoticesContent.Children.Add(page);
        var head = new DockPanel { Margin = new Thickness(0, 2, 0, 6), LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top); page.Children.Add(head);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(tools, Dock.Right); head.Children.Add(tools);
        tools.Children.Add(PageButton(center.Paused ? Loc.T("恢复提醒", "Resume") : Loc.T("暂停 1 小时", "Pause 1 h"),
            () => { center.Pause(center.Paused ? null : TimeSpan.FromHours(1)); RenderNotices(); }));
        if (notices.Count > 0) tools.Children.Add(PageButton(Loc.T("清空", "Clear"), () => { center.Clear(); _expandedNotice = null; RenderNotices(); }));
        head.Children.Add(UI.Fixed(Status(center, notices.Count), 11.5, 22, UI.Tertiary));
        _pageKey = NoticePageKey(center); _noticeLayout = NoticeLayout();
        if (notices.Count == 0) { page.Children.Add(_pageEmpty = Empty(center)); WatchPageLive(); return; }
        var list = new StackPanel();
        // Up and Down move between the rows and their buttons; Tab goes through them in order.
        KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Contained);
        var now = DateTimeOffset.UtcNow;
        var listed = notices.Take(_noticeRows).ToList();
        // A page built again in place that keeps its scroll position or the keyboard's notice is built whole, as the place needs
        // the rows above it.
        int first = offset > 0 || refocus is not null ? listed.Count : Math.Min(listed.Count, FirstRows);
        for (int i = 0; i < first; i++) AddRow(list, listed[i], now);
        _pageList = list;
        if (refocus is not null && _pageRows.FirstOrDefault(r => r.Notice.Id == refocus).Row is { } focused)
            Dispatcher.BeginInvoke(() => focused.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        bool earlier = notices.Count > _noticeRows;
        if (first < listed.Count) StartFill(listed, first, earlier);
        else if (earlier) AddEarlierButton(list);
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
        page.Children.Add(scroll);
        if (offset > 0) { scroll.UpdateLayout(); scroll.ScrollToVerticalOffset(offset); }
        WatchPageLive();
    }

    private void AddRow(StackPanel list, Notice notice, DateTimeOffset now)
    {
        var row = Row(notice, now);
        list.Children.Add(row);
        _pageRows.Add((notice, notice.Revision, RowKey(notice, now), row));
    }

    private void AddEarlierButton(StackPanel list)
    {
        var more = PageButton(Loc.T("显示更早的通知", "Show earlier notices"), () => { _noticeRows += NoticePageStep; RenderNotices(); });
        more.HorizontalAlignment = HorizontalAlignment.Center; more.Margin = new Thickness(0, 6, 0, 2);
        list.Children.Add(more);
    }

    // The page's rows after its first ones, RowsPerFrame of them each frame until all are there. Built and laid out at once, the
    // forty rows a page lists took over 100 ms on the first frame of the opening (each new row lays its texts out the first
    // time), a stall the panel showed at the very start of its move; a few rows a frame stay within one.
    private const int FirstRows = 3, RowsPerFrame = 2;
    private (List<Notice> Notices, int Next, bool Earlier)? _fill;
    private TimeSpan _fillFrame;
    private bool _filling;

    private void StartFill(List<Notice> listed, int next, bool earlier)
    {
        _fill = (listed, next, earlier);
        if (_filling) return;
        _filling = true; _fillFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += FillRows;
    }

    private void StopFill()
    {
        _fill = null;
        if (!_filling) return;
        _filling = false;
        CompositionTarget.Rendering -= FillRows;
    }

    private void FillRows(object? sender, EventArgs e)
    {
        // WPF may raise Rendering more than once for one frame.
        if (e is RenderingEventArgs args) { if (args.RenderingTime == _fillFrame) return; _fillFrame = args.RenderingTime; }
        // The page closed or was left: what is missing is built when it shows again (the rows no longer match the list).
        if (_fill is not { } fill || _pageList is not { } list || !_expanded || CurrentPage != DeckPage.Notices) { StopFill(); return; }
        var now = DateTimeOffset.UtcNow;
        int end = Math.Min(fill.Notices.Count, fill.Next + RowsPerFrame);
        for (int i = fill.Next; i < end; i++) AddRow(list, fill.Notices[i], now);
        if (end < fill.Notices.Count) { _fill = fill with { Next = end }; return; }
        StopFill();
        if (fill.Earlier) AddEarlierButton(list);
        WatchPageLive();
    }

    private static string Status(NoticeCenter center, int count)
    {
        string text = count == 0 ? Loc.T("通知历史", "Notice history") : Loc.T($"通知历史 · {count} 条", $"Notice history · {count}");
        if (center.Paused && center.Settings.NoticesPausedUntil is { } until) text += Loc.T($" · 暂停至 {until.ToLocalTime():HH:mm}", $" · paused until {until.ToLocalTime():HH:mm}");
        else if (center.Quiet) text += Loc.T(" · 安静中", " · quiet");
        return text;
    }

    private double EmptyWidth => Math.Max(200, ContentCanvas.Width - 60);

    private FrameworkElement Empty(NoticeCenter center)
    {
        var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = EmptyWidth };
        var bell = new Viewbox { Width = 24, Height = 24, Margin = new Thickness(0, 0, 0, 8), Child = new Path { Data = Geometry.Parse("M12,3 C8.7,3 6.5,5.6 6.5,8.8 V12.6 L4.8,15.6 H19.2 L17.5,12.6 V8.8 C17.5,5.6 15.3,3 12,3 Z M9.6,17.6 A2.4,2.4 0 0 0 14.4,17.6"), Stroke = UI.Brush(UI.Secondary), StrokeThickness = 1.5, Width = 24, Height = 24 } };
        empty.Children.Add(bell);
        empty.Children.Add(UI.Fixed(Loc.T("暂无通知", "No notices"), 14, 22, UI.Secondary));
        string hint = center.WindowsState switch
        {
            NoticeSourceState.NeedsAccess => Loc.T("在设置的“灵动提示”中允许读取 Windows 通知。", "Allow access to Windows notifications under Live notices in the settings."),
            NoticeSourceState.Denied => Loc.T("Windows 已拒绝通知访问，可在系统隐私设置中开启。", "Windows denies access to notifications; it can be allowed in Windows' privacy settings."),
            _ => Loc.T("应用的新通知会出现在这里，保留 " + Settings.NoticeHistoryHours + " 小时。", "New notices from your apps appear here and are kept for " + Settings.NoticeHistoryHours + " h.")
        };
        var line = UI.Line(hint, 11.5, UI.Tertiary); line.TextWrapping = TextWrapping.Wrap; line.TextAlignment = TextAlignment.Center; line.Margin = new Thickness(0, 4, 0, 0);
        empty.Children.Add(line);
        return empty;
    }

    private FrameworkElement Row(Notice notice, DateTimeOffset now)
    {
        var center = _app.Notices;
        bool open = _expandedNotice == notice.Id;
        var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(0, 0, 0, 2), Background = UI.Brush(open ? UI.Fill : "Transparent"), Cursor = Cursors.Hand };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Child = grid;
        var plate = NoticePlate(notice, 34);
        plate.VerticalAlignment = VerticalAlignment.Top; plate.Margin = new Thickness(0, 1, 10, 0);
        grid.Children.Add(plate);
        var texts = new StackPanel { Opacity = notice.SourcePresent || notice.Origin == NoticeOrigin.Test ? 1 : .78 };
        var textColumn = new SteppedWidth { Child = texts, Stepping = _stretch.Active };
        Grid.SetColumn(textColumn, 1); grid.Children.Add(textColumn);
        texts.Children.Add(NoticeWindow.Directed(UI.Fixed(notice.Title.Length > 0 ? notice.Title : notice.AppName, 12.5, 18, UI.Primary, FontWeights.SemiBold)));
        if (NoticeTimeLine(notice, now) is { } timeLine) texts.Children.Add(timeLine);
        if (notice.Body.Length > 0)
        {
            var body = NoticeWindow.Directed(UI.Line(open ? notice.Body : NoticeText.Summary(notice.Body), 11.5, UI.Secondary));
            body.FontFamily = UI.PanelFont;
            if (open) { body.TextWrapping = TextWrapping.Wrap; body.TextTrimming = TextTrimming.None; }
            texts.Children.Add(body);
        }
        string meta = notice.AppName + " · " + NoticeText.OriginName(notice.Origin, notice.Distro) + (notice.SourcePresent || notice.Origin == NoticeOrigin.Test ? "" : Loc.T(" · 来源已关闭", " · closed at source"));
        texts.Children.Add(UI.Fixed(meta, 10.5, 16, UI.Tertiary));
        if (notice.Progress is { } progress)
        {
            // The fill is a share of the track, which is as wide as the row's texts: a fixed 200 DIP left a finished task
            // two thirds full.
            double done = Math.Clamp(progress, 0, 1);
            var track = new Grid { Height = 3, Margin = new Thickness(0, 4, 0, 0), MinWidth = 3 };
            var doneColumn = new ColumnDefinition { Width = new GridLength(done, GridUnitType.Star), MinWidth = 3 };
            var leftColumn = new ColumnDefinition { Width = new GridLength(1 - done, GridUnitType.Star) };
            track.ColumnDefinitions.Add(doneColumn); track.ColumnDefinitions.Add(leftColumn);
            _rowProgress.AddOrUpdate(row, new RowProgress(doneColumn, leftColumn));
            var rail = new Border { Background = UI.Brush(UI.Track), CornerRadius = new CornerRadius(1.5) };
            Grid.SetColumnSpan(rail, 2);
            track.Children.Add(rail);
            track.Children.Add(new Border { Background = UI.Brush(UI.Accent), CornerRadius = new CornerRadius(1.5) });
            texts.Children.Add(track);
        }
        if (open)
        {
            var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (center.CanOpen(notice)) actions.Children.Add(PageButton(Loc.T("打开应用", "Open app"), () => _ = OpenFromPage(notice)));
            if ((notice.Capabilities & NoticeCapabilities.Actions) != 0)
                foreach (var action in notice.Actions.Where(a => a.Key != "default").Take(3))
                    actions.Children.Add(PageButton(action.Label, () => { center.Invoke(notice, action.Key); RenderNotices(); }));
            foreach (var action in center.TimeActions(notice))
                actions.Children.Add(PageButton(action.Label, () => { center.TimeAction(notice, action.Key); RenderNotices(); }));
            actions.Children.Add(PageButton(Loc.T("删除", "Delete"), () => { center.Delete(notice); _expandedNotice = null; RenderNotices(); }));
            texts.Children.Add(actions);
        }
        var side = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 1, 0, 0) };
        Grid.SetColumn(side, 2); grid.Children.Add(side);
        side.Children.Add(UI.Fixed(NoticeText.Relative(notice.ReceivedAt, now), 10.5, 16, UI.Tertiary));
        if (_freshNotices.Contains(notice.Id))
            side.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(PanelTheme.Hex("danger")), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 2, 0) });
        row.MouseEnter += (_, _) => { if (!open) row.Background = UI.Brush(UI.Fill); };
        row.MouseLeave += (_, _) => { if (!open) row.Background = UI.Brush("Transparent"); };
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindButton(source)) return;
            _expandedNotice = open ? null : notice.Id; RenderNotices(); e.Handled = true;
        };
        // From the keyboard, once a hotkey gave the panel the keyboard (MainWindow.Hotkeys.cs): Enter or Space opens or closes
        // the row, whose buttons are then the next stops. The focus mark is the panel's static one (motion-review C7).
        row.Focusable = true;
        row.FocusVisualStyle = (Style)Application.Current.FindResource("PanelKeyboardFocus");
        row.GotKeyboardFocus += (_, e) => { if (e.NewFocus == row) _focusedNotice = notice.Id; };
        row.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space) || e.OriginalSource != row) return;
            _expandedNotice = open ? null : notice.Id; RenderNotices(); e.Handled = true;
        };
        AutomationProperties.SetName(row, notice.AppName + ", " + notice.Title + ", " + NoticeText.Relative(notice.ReceivedAt, now));
        return row;
    }

    // The rows while the panel's edges are dragged on this page (NoticeRowStretch).
    private readonly NoticeRowStretch _stretch = new();

    private void FollowRows() { if (_pageList is { } list) _stretch.Follow(list, RowTexts()); }

    private void ReleaseRows() => _stretch.Release(RowTexts());

    private IEnumerable<SteppedWidth> RowTexts()
        => _pageRows.Select(row => row.Row).OfType<Border>().Select(row => row.Child).OfType<Grid>().SelectMany(grid => grid.Children.OfType<SteppedWidth>());

    // The rows that show a task's progress, with their track's columns (UpdateNoticeRows).
    private sealed record RowProgress(ColumnDefinition Done, ColumnDefinition Left);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, RowProgress> _rowProgress = [];

    // Everything a row shows apart from a task's progress.
    private string RowKey(Notice notice, DateTimeOffset now)
    {
        var center = _app.Notices;
        return string.Join('\u0001', notice.Title, notice.Body, notice.AppName, notice.Origin, notice.Distro, notice.IconRef, notice.SourcePresent, notice.Category,
            notice.TimeKind, NoticeClock.IsLive(notice.Timing, now), NoticeText.Relative(notice.ReceivedAt, now), notice.Capabilities, center.CanOpen(notice), _expandedNotice == notice.Id,
            _freshNotices.Contains(notice.Id), string.Join('\u0002', notice.Actions.Select(a => a.Key + "=" + a.Label)), string.Join('\u0002', center.TimeActions(notice).Select(a => a.Key)));
    }

    // The notice whose row last had the keyboard focus, to keep it there when the page is built again.
    private string? _focusedNotice;

    private static bool FindButton(DependencyObject node)
    {
        for (var current = node; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is Button) return true;
        return false;
    }

    private async Task OpenFromPage(Notice notice)
    {
        if (await _app.Notices.OpenAsync(notice)) { SetExpanded(false); return; }
        MessageHint(Loc.T("无法打开该应用；它可能不支持从通知启动。", "The application could not be opened; it may not support being started from a notice."));
    }

    private void MessageHint(string text)
    {
        if (NoticesContent.Children.OfType<DockPanel>().FirstOrDefault() is not { } page) return;
        var hint = UI.Line(text, 11, PanelTheme.Hex("danger")); hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 0, 0, 4);
        DockPanel.SetDock(hint, Dock.Top);
        page.Children.Insert(1, hint);
    }

    // Plates on the notices page still waiting for their icon. An icon that arrives fills only its own plates: rebuilding the
    // page for it would ask again for icons the cache let go of, whose arrival would rebuild it again.
    private readonly Dictionary<string, List<(Grid Plate, Notice Notice)>> _waitingPlates = new(StringComparer.Ordinal);

    private void NoticeIconLoaded(string iconRef)
    {
        if (!_waitingPlates.Remove(iconRef, out var plates)) return;
        foreach (var (plate, notice) in plates) FillPlate(plate, notice);
    }

    // The icon as in the arriving notice, on the panel's own plate.
    private FrameworkElement NoticePlate(Notice notice, double size)
    {
        var plate = new Grid { Width = size, Height = size };
        if (!FillPlate(plate, notice) && notice.IconRef is { } iconRef)
        {
            if (!_waitingPlates.TryGetValue(iconRef, out var plates)) _waitingPlates[iconRef] = plates = [];
            plates.Add((plate, notice));
        }
        return plate;
    }

    // True when the plate shows the icon; false when it shows the initial (the icon may still arrive).
    private bool FillPlate(Grid plate, Notice notice)
    {
        double size = plate.Width;
        plate.Children.Clear();
        plate.Children.Add(new Border { CornerRadius = new CornerRadius(size * .28), Background = UI.Brush(UI.Fill) });
        var image = _app.Notices.Icons.Get(notice.IconRef, out _);
        if (image is not null) plate.Children.Add(new Image { Source = image, Width = size * .84, Height = size * .84, Stretch = Stretch.Uniform });
        else if (NoticeBadges.AppGlyph(notice, size, _app.Notices.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey))) is { } glyph) plate.Children.Add(glyph);
        else
        {
            var color = PanelTheme.Readable(_app.Notices.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey)));
            var name = notice.AppName.Trim();
            plate.Children.Add(new TextBlock { Text = name.Length == 0 ? "?" : char.IsSurrogate(name[0]) && name.Length > 1 ? name[..2] : name[..1].ToUpper(Loc.Culture), FontFamily = UI.DisplayFont, FontSize = Math.Round(size * .48), FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }
        NoticeBadges.Decorate(plate, notice, size, NoticeLook.Current().Glow, image is null ? NoticeGlyphs.App(notice.AppKey, notice.AppName, notice.Origin) : null);
        return image is not null;
    }

    // A small flat button of the panel's kind.
    private static Button PageButton(string text, Action click)
    {
        var face = new Border { CornerRadius = new CornerRadius(6), Background = UI.Brush(UI.Fill), Padding = new Thickness(9, 0, 9, 0), Height = 22, Margin = new Thickness(4, 0, 0, 0) };
        face.Child = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = 11.5, Foreground = UI.Brush(UI.Secondary), VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = face, Cursor = Cursors.Hand, FocusVisualStyle = (Style)Application.Current.FindResource("PanelKeyboardFocus"), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        button.MouseEnter += (_, _) => face.Background = UI.Brush(UI.FillSelected);
        button.MouseLeave += (_, _) => face.Background = UI.Brush(UI.Fill);
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, text);
        return button;
    }
}
