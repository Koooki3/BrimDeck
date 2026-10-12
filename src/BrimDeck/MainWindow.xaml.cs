using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

public partial class MainWindow : Window
{
    private readonly App _app;
    // Hover timing runs on PromptTimer rather than DispatcherTimer; see PromptTimer for why.
    private readonly PromptTimer _hover, _leave, _pointerTrack, _pageSwitchHold;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(60) };
    // Fires just after each minute boundary, when the clock text and the reset countdowns change.
    private readonly DispatcherTimer _clock = new();
    private readonly DispatcherTimer _alertTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    // Compact hover follows the cursor position rather than window mouse messages, which reach
    // this per-pixel transparent window unreliably over the nearly invisible strip around the surface.
    private Size _compactHover;
    private bool _cursorInCompact, _trackPointer;
    // Checks run every 40 ms near the surface. Farther away the interval grows with the distance, assuming the cursor
    // approaches at no more than 10 physical pixels per millisecond, so it still arrives within one short interval.
    private const double TrackNear = 40, TrackFar = 160, ApproachSpeed = 10;
    private readonly WindowsHost _host = new();
    // Animated in place so the indicator style can fade its surface in and out instead of switching brushes.
    private readonly SolidColorBrush _surface = new(Colors.Black);
    private const double CapsuleGap = 5;
    // The indicator is the surface itself shrunk to a short grey bar; a wider invisible strip above it keeps hovering easy.
    private const double IndicatorWidth = 56, IndicatorHeight = 4, IndicatorGap = 4, IndicatorHoverWidth = 72, IndicatorHoverHeight = 10;
    internal static readonly Color IndicatorColor = Color.FromRgb(0x8E, 0x8E, 0x93);
    private VirtualDesktopPresence? _virtualDesktops;
    private EscapeDismissal? _escapeDismissal;
    private bool _expanded, _refreshing, _preview, _refreshRequested, _pointerInside;
    private Rect? _popupReturnArea;
    private int _days = 1;
    private bool _details;
    private int _applicationPage;
    private readonly Dictionary<Guid, int> _quotaPages = new();
    private DateTimeOffset? _updatedAt;
    private ScreenContext _context;
    // Threshold levels seen per quota; a rise after the first reading briefly widens the compact island.
    private readonly Dictionary<string, int> _levels = new();
    private bool _levelsSeeded;
    private CompactAlertInfo? _alert;
    private DeckSettings Settings => _app.Settings;
    public bool IsExpanded => _expanded;
    public bool IsSuppressed => !_preview && Settings.Behavior(_context) == WindowBehavior.Hide;
    public IReadOnlyList<ProviderSnapshot> Snapshots { get; private set; } = [];
    public string? LastRefreshError { get; private set; }

    private sealed record CompactAlertInfo(AppEntry Entry, string Name, string Label, double Used, DateTimeOffset? ResetAt);

    public MainWindow(App app)
    {
        _app = app;
        _hover = new(Dispatcher, () => { _hover!.Stop(); if (!IsSuppressed && _pointerInside) SetExpanded(true); });
        _leave = new(Dispatcher, () => CheckPointerExit(WindowsHost.Cursor()));
        _pointerTrack = new(Dispatcher, TrackPointer) { Interval = TimeSpan.FromMilliseconds(TrackNear) };
        _pageSwitchHold = new(Dispatcher, () =>
        {
            _pageSwitchHold!.Stop();
            // Enter/leave events keep the current pointer state up to date during the hold.
            // Only now resume the normal exit delay for the newly sized page.
            if (_expanded && !_preview) PointerChanged(_pointerInside);
        }) { Interval = TimeSpan.FromSeconds(1) };
        InitializeComponent();
        InitializeMusic();
        Island.Background = _surface;
        _refresh.Tick += async (_, _) => await RefreshAutomaticallyAsync();
        _clock.Tick += (_, _) => { UpdateClock(); ScheduleClock(); if (_expanded) RenderUsage(); _app.CheckUpdatesIfDue(); };
        _alertTimer.Tick += (_, _) => { _alertTimer.Stop(); _alert = null; RenderCompact(); if (!_expanded) SetExpanded(false); };
        _host.ContextChanged += context => { _context = context; if (!_preview && _detailPopup is null && _popupReturnArea is null && MediaSourceButton.ContextMenu?.IsOpen != true) _expanded = false; ApplySettings(); };
        SourceInitialized += (_, _) => { WindowsHost.ConfigureOverlay(this); _escapeDismissal = new EscapeDismissal(this, () => SetExpanded(false)); };
        Loaded += async (_, _) =>
        {
            _virtualDesktops ??= new VirtualDesktopPresence(this);
            _context = WindowsHost.DetectContext(); ApplySettings();
            _trackPointer = true; UpdatePointerTracking();
            ScheduleClock(); _clock.Start(); StartMusic(); await RefreshAutomaticallyAsync();
        };
        Closing += (_, e) => { if (!_app.Exiting) { e.Cancel = true; SetExpanded(false); } };
        Closed += (_, _) => { _escapeDismissal?.Dispose(); _virtualDesktops?.Dispose(); _host.Dispose(); _hover.Stop(); _leave.Stop(); _pointerTrack.Stop(); _pageSwitchHold.Stop(); _clock.Stop(); _refresh.Stop(); _alertTimer.Stop(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; SetExpanded(false); } };
        // The collapsed island opens on hover, so a right click there does nothing.
        MouseRightButtonUp += (_, e) => { if (!_expanded || !Settings.RightClickSettings) return; _app.OpenSettings(); e.Handled = true; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SetExpanded(false); e.Handled = true; } };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        // A scale change moves the window to a rectangle Windows suggests, and a taskbar change (auto-hide, size) alters the
        // work area without a display change; both centre the island again.
        // WPF also raises DpiChanged, with equal old and new values, after the window is moved or resized; reacting to
        // those would move the window again and loop.
        DpiChanged += (_, e) => { if (e.OldDpi.DpiScaleX != e.NewDpi.DpiScaleX) DisplayChanged(null, EventArgs.Empty); };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool _) =>
        {
            const int SettingChange = 0x001A, SetWorkArea = 0x002F;
            if (message == SettingChange && wParam == SetWorkArea) DisplayChanged(null, EventArgs.Empty);
            return IntPtr.Zero;
        });
        Closed += (_, _) => DisposeMusic();
    }

    private void DisplayChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(ApplySettings);
    private void ScheduleClock() => _clock.Interval = ClockDelay(DateTime.Now);
    internal static TimeSpan ClockDelay(DateTime now) => TimeSpan.FromMilliseconds(60_000 - now.Second * 1000 - now.Millisecond + 50);
    // Hidden or previewing, the surface cannot be hovered, so the checks stop instead of returning early 25 times a second.
    private void UpdatePointerTracking()
    {
        bool run = _trackPointer && !IsSuppressed && !_preview;
        if (run && !_pointerTrack.IsEnabled) { _pointerTrack.Interval = TimeSpan.FromMilliseconds(TrackNear); _pointerTrack.Start(); }
        else if (!run && _pointerTrack.IsEnabled) _pointerTrack.Stop();
    }
    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockLabel.Text = now.ToString("HH:mm");
        if (_compactClock is not null) _compactClock.Time = now;
    }
    public void ApplySettings() => ApplySettings(false);    private void ApplySettings(bool animatePage)
    {
        ApplyLanguage();
        if (!Settings.QuotaAlerts && _alert is not null) ClearAlert();
        ApplySyncSchedule();
        ApplyMusicSettings();
        var (bounds, work, scale) = WindowsHost.PrimaryScreen();
        double usageWidth = Math.Max(240, Math.Min(Math.Max(Settings.Width, Settings.MinimumWidth), work.Width / scale - 24));
        double musicWidth = Math.Max(240, Math.Min(Settings.MusicWidth, work.Width / scale - 24));
        _pageWidth = IsMusicPage ? musicWidth : usageWidth;
        // Keep the layered HWND large enough for either enabled page. Resizing it in separate
        // width/height/position steps presents intermediate surfaces; only Island should animate.
        bool bothPages = Settings.UsagePage && Settings.MusicPage;
        Width = bothPages ? Math.Max(usageWidth, musicWidth) : _pageWidth;
        double hostHeight = bothPages ? Math.Max(Settings.Height, Settings.MusicHeight) : IsMusicPage ? Settings.MusicHeight : Settings.Height;
        Height = Math.Max(140, Math.Min(hostHeight + 16, work.Height / scale - 16));
        // Sizes above are DIPs as they will be on the primary monitor. WPF turns Left and Top into pixels with the DPI of
        // the monitor the window is on now, which differs while it arrives from another monitor or the primary's scale
        // has just changed; the DPI change that follows applies the settings again with matching values.
        double current = PresentationSource.FromVisual(this) is null ? scale : VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = (bounds.Left + bounds.Width / 2) / current - Width / 2;
        Top = bounds.Top / current;
        UpdateClock();
        RenderCompact();
        if (IsSuppressed)
        {
            _hover.Stop(); _leave.Stop(); _pointerInside = false; SetExpanded(false, true); Hide();
        }
        else
        {
            if (!IsVisible) Show();
            SetExpanded(_expanded, !animatePage);
        }
        UpdatePointerTracking();
        RenderUsage();
    }
    // The toolbar's fixed buttons are declared in XAML; their names follow the interface language.
    private void ApplyLanguage()
    {
        AIUsageButton.ToolTip = Loc.T("AI 用量", "AI usage"); System.Windows.Automation.AutomationProperties.SetName(AIUsageButton, (string)AIUsageButton.ToolTip);
        MusicPageButton.ToolTip = Loc.T("音乐", "Music"); System.Windows.Automation.AutomationProperties.SetName(MusicPageButton, (string)MusicPageButton.ToolTip);
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton, Loc.T("设置", "Settings"));
    }
    public void Preview(bool enabled, bool expanded = true)
    { _preview = enabled; if (enabled) _pageSwitchHold.Stop(); ApplySettings(); SetExpanded(enabled && expanded, true); }
    internal void ResumeAfterPreview()
    {
        if (!_preview) return;
        _preview = false; _pageSwitchHold.Stop();
        ApplySettings();
        PointerChanged(_pointerInside);
    }
    public void SetSnapshots(IReadOnlyList<ProviderSnapshot> snapshots)
    {
        Snapshots = snapshots; _updatedAt = DateTimeOffset.Now;
        DetectThresholds();
        RenderCompact(); RenderUsage();
    }

    // The compact island shows one ring per enabled application: the highest used share among its quotas.
    internal void RenderCompact()
    {
        CompactRings.Children.Clear(); CompactAlert.Children.Clear();
        CompactRings.HorizontalAlignment = HorizontalAlignment.Center;
        if (_quotaCarousel is not null) { _quotaElapsed = _quotaCarousel.Elapsed; _quotaCarousel.SetRunning(false, false); }
        _compactEqualizer = null; _compactMarquee = null; _quotaCarousel = null; _compactClock = null;
        if (_alert is { } alert)
        {
            CompactAlert.Visibility = Visibility.Visible; CompactRings.Visibility = Visibility.Collapsed;
            var ring = UI.Ring(alert.Used, alert.Entry.Theme, 14, 2.2); ring.Margin = new Thickness(0, 0, 8, 0); ring.VerticalAlignment = VerticalAlignment.Center;
            CompactAlert.Children.Add(ring);
            var name = UI.Fixed(alert.Name, 11.5, 16, UI.Primary, FontWeights.SemiBold); name.MaxWidth = 76; name.ToolTip = alert.Name;
            CompactAlert.Children.Add(name);
            var text = UI.Fixed(Loc.T($"{alert.Label}已用 {alert.Used:0}%", $"{alert.Label}{alert.Used:0}% used"), 11.5, 16, UI.Secondary); text.Margin = new Thickness(8, 0, 0, 0); CompactAlert.Children.Add(text);
            var reset = UI.ShortReset(alert.ResetAt);
            if (reset.Length > 0) { var sub = UI.Fixed(Loc.T(reset + "重置", reset.StartsWith("in ", StringComparison.Ordinal) ? "resets " + reset : reset), 11.5, 16, UI.Tertiary); sub.Margin = new Thickness(8, 0, 0, 0); CompactAlert.Children.Add(sub); }
            return;
        }
        CompactAlert.Visibility = Visibility.Collapsed; CompactRings.Visibility = Visibility.Visible;
        RenderCompactMusic();
    }

    private void DetectThresholds()
    {
        CompactAlertInfo? risen = null;
        foreach (var entry in Settings.EnabledApps)
        {
            var snapshot = DashboardUsage.Quota(entry, Snapshots);
            if (snapshot.IsStale || !snapshot.LiveQuota) continue;
            foreach (var quota in snapshot.Metrics)
            {
                if (quota.UsedPercent is not { } used) continue;
                var key = $"{entry.InstanceId}:{entry.ConfigurationKey}:{quota.Id}";
                int level = AppPresets.Level(used);
                bool rose = _levelsSeeded && _levels.TryGetValue(key, out var previous) && level > previous;
                _levels[key] = level;
                if (rose && level > 0 && (risen is null || used > risen.Used))
                    risen = new CompactAlertInfo(entry, entry.Name, MetricLabel(quota) + " ", Math.Clamp(used, 0, 100), quota.ResetAt);
            }
        }
        _levelsSeeded = true;
        // Levels are still tracked while notices are off, so switching them back on does not replay an earlier rise.
        if (risen is not null && Settings.QuotaAlerts) ShowAlert(risen);
    }

    internal void ShowAlert(AppEntry entry, string label, double used, DateTimeOffset? resetAt)
        => ShowAlert(new CompactAlertInfo(entry, entry.Name, label + " ", used, resetAt));
    internal void ClearAlert() { _alertTimer.Stop(); _alert = null; RenderCompact(); }
    private void ShowAlert(CompactAlertInfo alert)
    {
        _alert = alert; _alertTimer.Stop(); _alertTimer.Start();
        RenderCompact();
        if (!_expanded && !IsSuppressed) SetExpanded(false);
    }

    private CompactStyle EffectiveStyle => !_preview && Settings.Behavior(_context) == WindowBehavior.Line ? CompactStyle.Line : Settings.Style;
    private double CompactWidth(CompactStyle style) => style switch
    {
        CompactStyle.Line => IndicatorWidth,
        CompactStyle.Capsule => _alert is null ? 196 : 300,
        _ => _alert is null ? 238 : 324
    };
    // One animated track: where it starts and ends, as fractions of the configured duration.
    private readonly record struct Track(double Start, double End);
    public void SetExpanded(bool expanded, bool instant = false)
    {
        if (expanded && IsSuppressed) return;
        if (expanded && !_expanded && !_preview) ApplyDefaultMusicPage();
        _expanded = expanded;
        _escapeDismissal?.SetEnabled(expanded && !_preview);
        if (!expanded) { CloseSourceMenu(); _popupReturnArea = null; _leave.Stop(); _pageSwitchHold.Stop(); _detailPopup?.Close(); }
        var style = EffectiveStyle;
        bool line = style == CompactStyle.Line, indicator = !expanded && line;
        double width = expanded ? _pageWidth : CompactWidth(style);
        double height = expanded ? Math.Min(IsMusicPage ? Settings.MusicHeight : Settings.Height, Height - 12) : line ? IndicatorHeight : 32;
        // Every compact style opens into the same panel hanging from the screen edge.
        var radius = expanded ? new CornerRadius(0, 0, 24, 24) : style switch
        {
            CompactStyle.Notch => new CornerRadius(0, 0, 12, 12),
            CompactStyle.Capsule => new CornerRadius(18),
            _ => new CornerRadius(IndicatorHeight / 2)
        };
        double gap = !expanded && style == CompactStyle.Capsule ? CapsuleGap : indicator ? IndicatorGap : 0;
        _compactHover = line ? new Size(IndicatorHoverWidth, IndicatorHoverHeight)
            : new Size(CompactWidth(style), (style == CompactStyle.Capsule ? CapsuleGap : 0) + 32);
        // Compact content keeps its final width while the surface moves, so nothing drifts with the shrinking edges.
        double inset = style == CompactStyle.Notch ? 30 : 16;
        Compact.Width = Math.Max(0, CompactWidth(style) - inset * 2);
        Island.BorderThickness = new Thickness(0);
        ExpandedContent.Height = Math.Min(IsMusicPage ? Settings.MusicHeight : Settings.Height, Height - 12);
        // Lay out in device-independent pixels. Column typography adapts to its available space.
        ContentCanvas.Width = _pageWidth;
        ContentCanvas.Height = ExpandedContent.Height;
        int duration = !instant && Settings.Animations && SystemParameters.ClientAreaAnimation ? Settings.AnimationDuration : 0;
        ExpandedContent.IsHitTestVisible = expanded;
        if (expanded) ExpandedContent.Visibility = Visibility.Visible;
        Compact.Visibility = line ? Visibility.Hidden : Visibility.Visible;
        HoverExtension.Width = indicator ? IndicatorHoverWidth : double.NaN;
        // Collapsing runs in three stages: the content leaves first, the surface settles with a slow start, the compact content lands last.
        // Expanding must feel immediate: the surface grows from the first frame on a fast-out curve, the compact content vanishes
        // quickly, and the content fades in once the surface is about two thirds open. The fractions follow the approved mock-up.
        Track content = expanded ? new(.25, .9) : new(0, .30);
        Track surface = expanded ? new(0, .8) : new(.12, line ? .92 : 1);
        Track compact = expanded ? new(0, .15) : new(.62, 1);
        Track shadow = expanded ? new(0, .3) : new(.5, 1);
        Track rim = expanded ? new(.2, .5) : new(.5, .8);
        Track tint = expanded ? new(0, .12) : new(.68, 1);
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var easeInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };
        IEasingFunction settle = expanded ? easeOut : new BezierEase(.45, 0, .12, 1);
        Animate(ExpandedContent, OpacityProperty, expanded ? 1 : 0, duration, content, easeOut, () => { if (!_expanded) ExpandedContent.Visibility = Visibility.Hidden; });
        Animate(ContentScale, ScaleTransform.ScaleXProperty, expanded ? 1 : .96, duration, content, expanded ? easeOut : easeInOut);
        Animate(ContentScale, ScaleTransform.ScaleYProperty, expanded ? 1 : .96, duration, content, expanded ? easeOut : easeInOut);
        Animate(Island, WidthProperty, width, duration, surface, settle);
        Animate(Island, HeightProperty, height, duration, surface, settle);
        Animate(Island, IslandBorder.ShoulderRadiusProperty, !expanded && style == CompactStyle.Notch ? 16 : 0, duration, surface, settle);
        // Compact notch and capsule hover over their full bounding box from the screen edge; the open panel uses its own outline.
        Animate(HoverExtension, HeightProperty, indicator ? IndicatorHoverHeight : expanded ? 0 : gap + height, duration, surface, settle);
        if (duration == 0)
        {
            SetImmediate(Island, MarginProperty, new Thickness(0, gap, 0, 0));
            SetImmediate(Island, Border.CornerRadiusProperty, radius);
        }
        else
        {
            Island.BeginAnimation(MarginProperty, Timed(new ThicknessAnimation { To = new Thickness(0, gap, 0, 0), EasingFunction = settle }, duration, surface), HandoffBehavior.SnapshotAndReplace);
            Island.BeginAnimation(Border.CornerRadiusProperty, Timed(new CornerRadiusAnimation { To = radius, EasingFunction = settle }, duration, surface), HandoffBehavior.SnapshotAndReplace);
        }
        Animate(Compact, OpacityProperty, expanded ? 0 : 1, duration, compact, easeOut);
        Animate(CompactScale, ScaleTransform.ScaleXProperty, expanded ? .85 : 1, duration, compact, expanded ? easeInOut : easeOut);
        Animate(CompactScale, ScaleTransform.ScaleYProperty, expanded ? .85 : 1, duration, compact, expanded ? easeInOut : easeOut);
        Animate(IslandShadow, OpacityProperty, !expanded && !line ? 1 : 0, duration, shadow, easeOut);
        // The surface stays opaque throughout; it loses its rim and turns grey only once it has the indicator's shape.
        Animate(Island, IslandBorder.MaterialOpacityProperty, indicator ? 0 : 1, duration, rim, easeOut);
        if (duration == 0) SetImmediate(_surface, SolidColorBrush.ColorProperty, indicator ? IndicatorColor : Colors.Black);
        else _surface.BeginAnimation(SolidColorBrush.ColorProperty, Timed(new ColorAnimation { To = indicator ? IndicatorColor : Colors.Black, EasingFunction = easeInOut }, duration, tint), HandoffBehavior.SnapshotAndReplace);
        // The panel is rebuilt when data or settings change, never here: a rebuild on the first frame reads as a stall.
        if (expanded) { RefreshStatus(); UpdateMusicPosition(); }
        else RenderSyncButton();
        UpdateMusicTimer();
    }
    private static T Timed<T>(T animation, int duration, Track track) where T : AnimationTimeline
    {
        animation.BeginTime = TimeSpan.FromMilliseconds(duration * track.Start);
        animation.Duration = TimeSpan.FromMilliseconds(duration * (track.End - track.Start));
        return animation;
    }
    private static void Animate(IAnimatable target, DependencyProperty property, double value, int duration, Track track, IEasingFunction easing, Action? completed = null)
    {
        if (duration == 0) { SetImmediate(target, property, value); completed?.Invoke(); return; }
        var animation = Timed(new DoubleAnimation { To = value, EasingFunction = easing }, duration, track);
        if (completed is not null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private static void SetImmediate(IAnimatable target, DependencyProperty property, object value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }
    // While compact, TrackPointer alone decides; an extra enter or leave message would restart the open delay.
    private void Island_MouseEnter(object sender, MouseEventArgs e)
    { if (_expanded) PointerChanged(true); }
    private void Island_MouseLeave(object sender, MouseEventArgs e)
    { if (_expanded) PointerChanged(false); }
    internal void TrackPointer()
    {
        if (_preview || IsSuppressed || !IsVisible || PresentationSource.FromVisual(Stage) is null) return;
        var cursor = WindowsHost.Cursor();
        double left = (Stage.ActualWidth - _compactHover.Width) / 2;
        var area = new Rect(Stage.PointToScreen(new Point(left, 0)), Stage.PointToScreen(new Point(left + _compactHover.Width, _compactHover.Height)));
        bool inCompact = area.Contains(cursor);
        double dx = Math.Max(0, Math.Max(area.Left - cursor.X, cursor.X - area.Right)), dy = Math.Max(0, Math.Max(area.Top - cursor.Y, cursor.Y - area.Bottom));
        // Whole 20 ms steps, so small movements far away do not reschedule the timer on every check.
        double wait = _expanded ? TrackNear : Math.Clamp(Math.Floor(Math.Sqrt(dx * dx + dy * dy) / ApproachSpeed / 20) * 20, TrackNear, TrackFar);
        _pointerTrack.Interval = TimeSpan.FromMilliseconds(wait);
        if (_expanded)
        {
            // Kept current so that collapsing with the cursor still on the surface does not count as a new entry.
            _cursorInCompact = inCompact;
            // A jump straight out of the panel can leave without any mouse message reaching the window.
            if (_pointerInside && !HoverZone.IsMouseOver && !ScreenBounds(HoverZone).Contains(cursor)) PointerChanged(false);
            return;
        }
        if (inCompact != _cursorInCompact) { _cursorInCompact = inCompact; PointerChanged(inCompact); }
        else if (!inCompact && _pointerInside) PointerChanged(false);
    }
    internal void PointerChanged(bool inside)
    {
        _pointerInside = inside; UpdateQuotaCarousel();
        if (inside)
        {
            _popupReturnArea = null;
            _leave.Stop(); if (_expanded || IsSuppressed) return;
            _hover.Stop(); _hover.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Settings.OpenDelay)); _hover.Start();
        }
        else
        {
            _hover.Stop(); if (!_expanded || _preview || _pageSwitchHold.IsEnabled || _detailPopup is not null || MediaSourceButton.ContextMenu?.IsOpen == true) return;
            _leave.Stop(); _leave.Interval = TimeSpan.FromMilliseconds(Math.Max(_popupReturnArea is null ? 1 : 50, Settings.CloseDelay)); _leave.Start();
        }
    }
    internal void CheckPointerExit(Point screenPoint)
    {
        if (_pointerInside || _preview || _pageSwitchHold.IsEnabled || _detailPopup is not null || MediaSourceButton.ContextMenu?.IsOpen == true) { _leave.Stop(); return; }
        // Closing a calendar with either button must leave a path back to the results.
        // Keep checking until the cursor leaves that area; entering the island restores normal hover behavior.
        if (_popupReturnArea is { } area && area.Contains(screenPoint)) return;
        _leave.Stop(); SetExpanded(false);
    }
    private void Settings_Click(object sender, RoutedEventArgs e) { if (_app.Updates.NoticeVersion is not null) _app.OpenUpdateSettings(); else _app.OpenSettings(); }
    private async Task<IReadOnlyList<ProviderSnapshot>> LoadSnapshotsAsync(DateTime? historyStart)
    {
        var settings = Settings.Copy();
        var prices = _app.Prices.RefreshAsync(cancellation: _app.Lifetime.Token);
        var usage = Task.Run(() => _app.Usage.RefreshAsync(settings, _app.Lifetime.Token, historyStart));
        await Task.WhenAll(prices, usage);
        return await usage;
    }
    public async Task RefreshAsync(bool queueIfBusy = true)
    {
        // Timer requests reuse the current read. Changed settings or a wider
        // date range still require one follow-up, and automatic requests must not clear it.
        if (_refreshing) { if (queueIfBusy) _refreshRequested = true; return; }
        _refreshing = true; LastRefreshError = null;
        RenderSyncButton();
        try
        {
            var result = await LoadSnapshotsAsync(_historyStart);
            SetSnapshots(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastRefreshError = ex.GetType().Name + "\n" + ex.StackTrace; }
        finally
        {
            _refreshing = false; RestartSyncCycle(); RenderUsage();
            if (_refreshRequested)
            {
                _refreshRequested = false; _ = RefreshAsync();
            }
        }
    }

}

public sealed class CornerRadiusAnimation : AnimationTimeline
{
    public CornerRadius To { get; set; }
    public IEasingFunction? EasingFunction { get; set; }
    public override Type TargetPropertyType => typeof(CornerRadius);
    protected override Freezable CreateInstanceCore() => new CornerRadiusAnimation { To = To, EasingFunction = EasingFunction };
    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var from = (CornerRadius)defaultOriginValue;
        var p = animationClock.CurrentProgress ?? 0;
        p = EasingFunction?.Ease(p) ?? p;
        double L(double a, double b) => a + (b - a) * p;
        return new CornerRadius(L(from.TopLeft, To.TopLeft), L(from.TopRight, To.TopRight), L(from.BottomRight, To.BottomRight), L(from.BottomLeft, To.BottomLeft));
    }
}

// Cubic Bézier easing with CSS semantics, so the surface follows the same curve as the approved mock-up.
public sealed class BezierEase : EasingFunctionBase
{
    private readonly double _x1, _y1, _x2, _y2;
    public BezierEase(double x1, double y1, double x2, double y2) { _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2; }
    protected override Freezable CreateInstanceCore() => new BezierEase(_x1, _y1, _x2, _y2);
    protected override double EaseInCore(double normalizedTime)
    {
        double x = Math.Clamp(normalizedTime, 0, 1), t = x;
        // Newton steps on the x polynomial; x(t) is monotonic while the control points stay inside [0, 1].
        for (int i = 0; i < 8; i++)
        {
            double slope = 3 * A(_x1, _x2) * t * t + 2 * B(_x1, _x2) * t + 3 * _x1;
            if (slope < 1e-6) break;
            t -= (Sample(t, _x1, _x2) - x) / slope;
        }
        return Sample(Math.Clamp(t, 0, 1), _y1, _y2);
    }
    private static double A(double a1, double a2) => 1 - 3 * a2 + 3 * a1;
    private static double B(double a1, double a2) => 3 * a2 - 6 * a1;
    private static double Sample(double t, double a1, double a2) => ((A(a1, a2) * t + B(a1, a2)) * t + 3 * a1) * t;
}
