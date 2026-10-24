using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;
using static BrimDeck.Tracks;

namespace BrimDeck;

// One panel's dynamic notices. The window lies over the panel's collapsed island and is shown only while a notice is: the
// notice grows out of the island's exact outline in the island's own theme material, shows the application's icon, title and
// summary, opens into its details on hover or click, and shrinks back into the island at the end, moving as the island does,
// on the panel's own clock and tracks (NoticeMotion: Soft.Bounce opening, Soft.Settle closing). Its window is sized once per
// notice to hold the largest form, so nothing resizes the window while it moves; only the drawn surface is hit-tested, the rest
// lets clicks through. It never takes focus unless the notices hotkey asks for the keyboard. Opened over, the panel takes the
// notice's outline and grows on from it (HandOver).
internal sealed partial class NoticeWindow : Window
{
    private const double HoverOpenMs = 250, GlowMargin = 32;
    private readonly MainWindow _deck;
    private readonly App _app;
    private readonly Canvas _stage = new() { Background = null };
    private readonly NoticeSurface _glow = new() { IsHitTestVisible = false };
    private readonly NoticeSurface _surface = new() { Cursor = Cursors.Hand, Focusable = false };
    private readonly Grid _content = new();
    private readonly Grid _toast = new() { Opacity = 0 }, _detail = new() { Opacity = 0, Visibility = Visibility.Collapsed };
    private readonly PromptTimer _hoverOpen, _leaveClose;
    private EscapeDismissal? _escape;
    private AttentionItem? _item;
    private NoticeLook _look = NoticeLook.Current();
    private Color _accent = Colors.White;
    private NoticeFrame _collapsed = null!, _toastFrame = null!, _detailFrame = null!;
    private Rect _stageRect;
    private bool _showingDetail, _keyboard, _arrivedUnderPointer, _leaving, _openFailed;
    private TextBlock? _announced;
    // The details' row of actions; the keyboard starts on its last button, the main action.
    private Panel? _actions;
    private int _generation;
    // The item the details were last built for; opening them for the same item only moves the surface.
    private AttentionItem? _detailFor;
    // What the details were built from apart from a task's progress (DetailKey), and the progress track's two columns, so that
    // progress alone changes in place: rebuilding the card under the pointer lost clicks, focus and the body's scroll.
    private string? _detailKey;
    // The window that had the keyboard before the notices hotkey (EnterKeyboard), to give it back.
    private IntPtr _previousForeground;
    private ColumnDefinition? _progressDone, _progressLeft;
    private FrameworkElement? _progressTrack;
    private bool _dressed;

    public NoticeWindow(MainWindow deck, App app)
    {
        _deck = deck; _app = app;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false; Title = "BrimDeck notices"; UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Content = _stage;
        _stage.Children.Add(_glow); _stage.Children.Add(_surface);
        _surface.Child = _content;
        _content.Children.Add(_toast); _content.Children.Add(_detail);
        // The text sits on the theme's opaque surface, as the island's does, so ClearType stays on for it.
        RenderOptions.SetClearTypeHint(_content, ClearTypeHint.Enabled);
        AutomationProperties.SetName(_surface, Loc.T("灵动提示", "Dynamic notice"));
        _toast.HorizontalAlignment = HorizontalAlignment.Center; _toast.VerticalAlignment = VerticalAlignment.Center;
        _detail.HorizontalAlignment = HorizontalAlignment.Center; _detail.VerticalAlignment = VerticalAlignment.Top;
        _hoverOpen = new(Dispatcher, () => { _hoverOpen!.Stop(); if (_item is not null && _surface.IsMouseOver && !_showingDetail) OpenDetail(); }) { Interval = TimeSpan.FromMilliseconds(HoverOpenMs) };
        _leaveClose = new(Dispatcher, () => { _leaveClose!.Stop(); if (!_surface.IsMouseOver && !_keyboard) LeaveDetail(); });
        _surface.MouseEnter += (_, _) => PointerEntered();
        _surface.MouseLeave += (_, _) => PointerLeft();
        _surface.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null) return; Clicked(); e.Handled = true; };
        _surface.MouseRightButtonUp += (_, e) => e.Handled = true;
        SourceInitialized += (_, _) => { WindowsHost.ConfigureOverlay(this); _escape = new EscapeDismissal(this, () => Dismiss(true)); };
        PreviewKeyDown += KeyPressed;
        Deactivated += (_, _) => { if (_keyboard) EndKeyboard(); };
        _app.Notices.Icons.Loaded += IconLoaded;
        Closed += (_, _) => { _escape?.Dispose(); _hoverOpen.Stop(); _leaveClose.Stop(); _app.Notices.Icons.Loaded -= IconLoaded; _item = null; WatchLive(); };
        // ui-3: moved to a monitor with another scale while it shows, the material is made again for that scale.
        DpiChanged += (_, _) => { if (_item is not null && !_leaving) Dress(); };
    }

    // True while a notice covers the island: the panel then leaves hovering to this window.
    public bool Covering => _item is not null || IsVisible && !_leaving;
    public AttentionItem? Item => _item;
    private DeckSettings Settings => _app.Settings;
    private NoticeCenter Center => _app.Notices;
    private bool Indicator => _deck.Collapsed.Style == CompactStyle.Line;

    // ----- Showing, updating and ending -----

    public void Present(AttentionItem item)
    {
        bool continuing = IsVisible;
        _item = item; _leaving = false; _openFailed = false;
        int generation = ++_generation;
        _showingDetail = false; _keyboard = false; _detailFor = null;
        // Everything the move needs is built before it starts: the window's size and place, the theme and the toast.
        Layout();
        Dress();
        BuildToast();
        if (!continuing)
        {
            // Starts exactly as the island: its outline, its place and its material. The indicator has no material of its own;
            // the notice starts in its flat color and takes the theme's surface as it grows.
            SetFrame(_collapsed, 0, NoticeMotion.Surface);
            _surface.IslandColor = PanelTheme.ColorOf("indicator");
            SetImmediate(_surface, NoticeSurface.IslandOpacityProperty, Indicator ? 1d : 0d);
            SetImmediate(_toast, OpacityProperty, 0d); SetImmediate(_detail, OpacityProperty, 0d); _detail.Visibility = Visibility.Collapsed; _toast.Visibility = Visibility.Visible;
            SetImmediate(_glow, OpacityProperty, 0d);
            Show();
            // Shown on a monitor with another scale, Windows has just given the window that scale; it is placed again in it,
            // and its material (the scan lines are drawn per device pixel) made for it if the scale it was made for was not it
            // (ui-3; an unchanged look is kept, so this costs nothing then).
            Place();
            Dress();
            NoticeNative.BringToTop(new WindowInteropHelper(this).Handle);
        }
        var cursor = CursorInStage();
        _arrivedUnderPointer = new Rect(_toastFrame.Rect.X - _stageRect.X, _toastFrame.Rect.Y - _stageRect.Y, _toastFrame.Rect.Width, _toastFrame.Rect.Height).Contains(cursor);
        _toast.Visibility = Visibility.Visible;
        Announce();
        // A window just shown draws its first frame late (Windows sets up the layered window, WPF lays out the toast). Started
        // now, the growth would spend its first frames behind that and seem to jump; it starts once that frame is drawn. The
        // notice stands in the island's place until then, drawn over the island itself, so neither ever goes missing.
        if (continuing || Duration() <= 0) Arrive(continuing);
        else Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (generation == _generation && _item is not null) Arrive(false); });
        WatchLive();
    }

    // The notice grows out of the island (or out of the notice before it) on the opening spring, as the panel opens.
    private void Arrive(bool continuing)
    {
        // The notice now stands exactly where the island is; the island underneath is hidden while it does.
        if (!continuing) _deck.SetIslandCovered(true);
        double duration = Duration();
        SetFrame(_toastFrame, duration, NoticeMotion.Surface, Soft.Bounce);
        Animate(_surface, NoticeSurface.IslandOpacityProperty, 0, duration, NoticeMotion.ArriveTint, Soft.Out);
        Animate(_detail, OpacityProperty, 0, duration, NoticeMotion.ReturnContent, Soft.InOut, () => { if (!_showingDetail && _detailFor is null) _detail.Visibility = Visibility.Collapsed; });
        Animate(_toast, OpacityProperty, 1, duration, continuing ? NoticeMotion.FollowContent : NoticeMotion.ArriveContent, Soft.Out);
        GlowIn(duration);
    }

    // The shown notice changed in place (a task's progress, a burst from the same application).
    public void Update(AttentionItem item)
    {
        if (_item is null) return;
        _item = item;
        var wanted = ToastSize();
        // While it shows, the notice only grows: text that changes length does not make it pulse in and out.
        var frame = Frame(NoticeForm.Toast, new Size(Math.Max(wanted.Width, _toastFrame.Rect.Width), Math.Max(wanted.Height, _toastFrame.Rect.Height)));
        // A burst updates the text many times a second; the surface only moves when its size changes, so an unchanged frame
        // does not start the morph again (it would redraw the window at full frame rate for the whole burst).
        bool moved = frame != _toastFrame;
        _toastFrame = frame;
        if (!_showingDetail) _detailFor = null;
        // perf-7: a task's progress or a burst's count changes only those parts of the toast, in place; the toast is built
        // again only when what it is made of changes. Screen readers hear it again only when its words change.
        bool spoken = moved || !RefreshToast();
        if (spoken) BuildToast();
        // A change in place glides as the monitor's selections do. Progress alone changes the open details in place.
        if (_showingDetail && !UpdateDetailInPlace()) ShowDetailFrame(InPlace, Soft.Follow);
        else if (moved) SetFrame(_toastFrame, InPlace, NoticeMotion.Surface, Soft.Follow);
        if (spoken || _toastWordsChanged) Announce();
        WatchLive();
    }

    // The item ended. Unless another one follows at once (Present), the notice shrinks back into the island and the window hides.
    public void End(AttentionItem item)
    {
        if (!ReferenceEquals(_item, item) && _item?.Key != item.Key) return;
        _item = null; _leaving = true;
        WatchLive();
        _hoverOpen.Stop(); _leaveClose.Stop();
        if (_keyboard) EndKeyboard();
        _escape?.SetEnabled(false);
        int generation = ++_generation;
        // The island may have changed meanwhile (a window rule turned it into the indicator).
        _collapsed = NoticeGeometry.Collapsed(_deck.Collapsed);
        _surface.IslandColor = PanelTheme.ColorOf("indicator");
        // As the panel closes: the content leaves first, the surface settles into the island with a slow start, and the
        // indicator's color comes back only once the surface is nearly the line. The shadow or glow ends as the island's own
        // (none for the line), so nothing about it changes when the island takes over.
        double duration = Duration();
        Animate(_toast, OpacityProperty, 0, duration, NoticeMotion.ReturnContent, Soft.InOut);
        Animate(_detail, OpacityProperty, 0, duration, NoticeMotion.ReturnContent, Soft.InOut);
        Animate(_glow, OpacityProperty, Indicator ? 0 : 1, duration, NoticeMotion.ReturnGlow, Soft.InOut);
        if (Indicator) Animate(_surface, NoticeSurface.IslandOpacityProperty, 1, duration, NoticeMotion.ReturnTint, Soft.InOut);
        SetFrame(_collapsed, duration, NoticeMotion.ReturnSurface, Soft.Settle, () => { if (generation == _generation && _item is null) Finish(reveal: true); });
    }

    // The panel opens over the island: a notice still on its way out is removed at once, so the panel is never hidden.
    public void Abort()
    {
        if (_item is not null || !IsVisible) return;
        _generation++;
        Finish();
    }

    // The panel opens over the island while a notice shows or leaves (the notices hotkey, "View all"): the notice gives the
    // panel its outline as it is on screen now, and goes in the same frame, so the panel grows on from the notice instead of
    // the notice vanishing and the panel starting again from the island. What the notice showed ends with it; the coordinator
    // ends its item as the panel opens (AttentionCoordinator.SetBusy), which then finds nothing left to do here.
    public NoticeFrame? HandOver()
    {
        if (!IsVisible) return null;
        double left = Canvas.GetLeft(_surface), top = Canvas.GetTop(_surface), width = _surface.Width, height = _surface.Height;
        NoticeFrame? frame = double.IsFinite(left) && double.IsFinite(top) && width > 0 && height > 0
            ? new NoticeFrame(new DipRect(_stageRect.X + left, _stageRect.Y + top, width, height), _surface.TopRadius, _surface.BottomRadius, _surface.Shoulder)
            : null;
        _item = null;
        _hoverOpen.Stop(); _leaveClose.Stop();
        if (_keyboard) EndKeyboard();
        _escape?.SetEnabled(false);
        _generation++;
        Finish();
        return frame;
    }

    // ui-2: the panel is hidden, or BrimDeck exits and the panel powers off: a notice that still shows or leaves goes at once,
    // so it never plays on over a hidden panel and the island it covers draws back into the dot in sight.
    public void Vanish() { if (IsVisible) HandOver(); }

    private void Finish(bool reveal = false)
    {
        _leaving = false; _showingDetail = false; _detailFor = null;
        // The notice ended in the island's outline and material: the island shows again in the same frame as the window hides,
        // and its own content lands as it does when the panel closes.
        _deck.SetIslandCovered(false, reveal);
        Hide();
        // The surface keeps no stale animations, so the next notice starts from the island again.
        foreach (var property in new[] { Canvas.LeftProperty, Canvas.TopProperty, WidthProperty, HeightProperty })
        { _surface.BeginAnimation(property, null); _glow.BeginAnimation(property, null); }
        _toast.Children.Clear(); _detail.Children.Clear(); _toastShape = null;
    }

    // The icon of the shown notice arrived after it was drawn: the content is drawn again in place, the frame stays.
    private void IconLoaded(string iconRef)
    {
        if (_leaving || Notice?.IconRef != iconRef) return;
        BuildToast();
        if (_showingDetail) BuildDetail();
        else _detailFor = null;
    }

    // The theme or the island changed while a notice shows: it is laid out and drawn again in place.
    public void Refresh()
    {
        if (_item is null || _leaving) return;
        Layout();
        Dress();
        BuildToast();
        if (_showingDetail) ShowDetailFrame(InPlace, Soft.Follow);
        else { _detailFor = null; SetFrame(_toastFrame, InPlace, NoticeMotion.Surface, Soft.Follow); }
    }

    // ----- Geometry -----

    private NoticeFrame Frame(NoticeForm form, Size wanted) => NoticeGeometry.Frame(_deck.Collapsed, _deck.MonitorBoundsDip, _deck.WorkDip, form, wanted.Width, wanted.Height);

    // Sizes the window once for this notice: it holds the island, the toast and the largest details card, with room for the glow.
    private void Layout()
    {
        _collapsed = NoticeGeometry.Collapsed(_deck.Collapsed);
        _toastFrame = Frame(NoticeForm.Toast, ToastSize());
        _detailFrame = Frame(NoticeForm.Detail, new Size(NoticeGeometry.DetailMaxWidth, NoticeGeometry.DetailMaxHeight));
        var union = Rect.Union(Rect.Union(ToRect(_collapsed.Rect), ToRect(_toastFrame.Rect)), ToRect(_detailFrame.Rect));
        union.Inflate(GlowMargin, GlowMargin);
        var monitor = _deck.MonitorBoundsDip;
        union.Intersect(new Rect(0, 0, monitor.Width, monitor.Height));
        if (union.IsEmpty) union = new Rect(0, 0, 1, 1);
        if (IsVisible && _stageRect.Contains(union)) union = _stageRect;
        else if (IsVisible)
        {
            // Moving a visible window would make the surface jump; it grows to include both instead.
            union.Union(_stageRect);
            ShiftStage(_stageRect, union);
        }
        _stageRect = union;
        Place();
    }

    // Keeps the surface where it is on screen while the stage's origin moves.
    private void ShiftStage(Rect from, Rect to)
    {
        double dx = from.X - to.X, dy = from.Y - to.Y;
        foreach (var element in new FrameworkElement[] { _surface, _glow })
        {
            double left = Canvas.GetLeft(element), top = Canvas.GetTop(element);
            SetImmediate(element, Canvas.LeftProperty, (double.IsNaN(left) ? 0 : left) + dx);
            SetImmediate(element, Canvas.TopProperty, (double.IsNaN(top) ? 0 : top) + dy);
        }
    }

    private static Rect ToRect(DipRect rect) => new(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));

    private void Place()
    {
        var (bounds, _, scale) = WindowsHost.MonitorScreen(_deck.HotkeyMonitor) ?? WindowsHost.PrimaryScreen();
        Width = _stageRect.Width; Height = _stageRect.Height;
        int x = (int)Math.Round(bounds.Left + _stageRect.X * scale), y = (int)Math.Round(bounds.Top + _stageRect.Y * scale);
        if (PresentationSource.FromVisual(this) is not null && VisualTreeHelper.GetDpi(this).DpiScaleX is var current && Math.Abs(current - scale) > .001)
            WindowsHost.MoveTo(this, x, y);
        double dpi = PresentationSource.FromVisual(this) is null ? scale : VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = x / dpi; Top = y / dpi;
    }

    // The cursor relative to the stage, in DIPs.
    private Point CursorInStage()
    {
        var (bounds, _, scale) = WindowsHost.MonitorScreen(_deck.HotkeyMonitor) ?? WindowsHost.PrimaryScreen();
        var cursor = WindowsHost.Cursor();
        return new Point((cursor.X - bounds.Left) / scale - _stageRect.X, (cursor.Y - bounds.Top) / scale - _stageRect.Y);
    }

    // Moves the surface and the copy that casts its shadow to a frame: place, size, corners and shoulders on one clock and one
    // curve, so the outline never changes shape on the way other than as the curve says.
    private void SetFrame(NoticeFrame frame, double duration, NoticeMotion.Track track, IEasingFunction? easing = null, Action? completed = null)
    {
        easing ??= Soft.Out;
        double left = frame.Rect.X - _stageRect.X, top = frame.Rect.Y - _stageRect.Y;
        bool line = Indicator && ReferenceEquals(frame, _collapsed);
        foreach (var element in new NoticeSurface[] { _surface, _glow })
        {
            Animate(element, Canvas.LeftProperty, left, duration, track, easing);
            Animate(element, Canvas.TopProperty, top, duration, track, easing);
            Animate(element, WidthProperty, Math.Max(0, frame.Rect.Width), duration, track, easing);
            Animate(element, HeightProperty, Math.Max(0, frame.Rect.Height), duration, track, easing, ReferenceEquals(element, _surface) ? completed : null);
            Animate(element, NoticeSurface.TopRadiusProperty, frame.TopRadius, duration, track, easing);
            Animate(element, NoticeSurface.BottomRadiusProperty, line ? frame.Rect.Height / 2 : frame.BottomRadius, duration, track, easing);
            Animate(element, NoticeSurface.ShoulderProperty, frame.Shoulder, duration, track, easing);
        }
    }

    // ----- Motion -----

    // A move takes as long as the panel's opening or closing (NoticeMotion.Length); a change in place as long as the monitor's
    // glide between choices (Soft.Choose). Without animations (BrimDeck's setting or Windows' own), nothing moves.
    private double Duration() => NoticeMotion.Length(Settings.AnimationDuration, Soft.Enabled);
    private static double InPlace => Soft.Enabled ? Soft.Choose : 0;

    // The theme as it is now, for the surface and the shadow or glow behind it. Before the window is first shown it has no scale
    // of its own yet; the monitor it will show on gives it (ui-3).
    private void Dress()
    {
        double dpi = PresentationSource.FromVisual(this) is null ? (WindowsHost.MonitorScreen(_deck.HotkeyMonitor) ?? WindowsHost.PrimaryScreen()).Scale
            : VisualTreeHelper.GetDpi(this).DpiScaleX;
        var look = NoticeLook.Current(dpi);
        // The look is cached per theme and scale: an unchanged one keeps its brushes and shadow, so a notice that arrives does
        // not build them again as its move starts.
        if (_dressed && ReferenceEquals(look, _look)) return;
        _dressed = true;
        _look = look;
        _surface.Look = _look;
        // An opaque surface casts its shadow from a copy of its shape behind it. A translucent one (a theme file may make the
        // surface see-through) would show that copy, so its shadow comes from its outline alone.
        bool translucent = _look.Surface.A < 255;
        _glow.FillOverride = translucent ? Brushes.Transparent : null;
        _glow.MaterialHidden = !translucent;
        _glow.Look = _look;
        bool hard = _look.ShadowBlur <= 0;
        _glow.Effect = _look.ShadowOpacity <= 0 || _look.HighContrast ? null : new DropShadowEffect
        {
            Color = _look.Shadow, BlurRadius = hard ? 0 : _look.ShadowBlur, ShadowDepth = _look.ShadowDepth, Direction = 270,
            Opacity = _look.ShadowOpacity, RenderingBias = RenderingBias.Performance
        };
    }

    private void GlowIn(double duration)
    {
        if (_look.Glow && duration > 0)
        {
            // A theme that glows lights up once as the notice arrives, then keeps its usual glow.
            var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(duration * 2.4) };
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(.35), Soft.Out));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(.7, KeyTime.FromPercent(1), Soft.InOut));
            _glow.BeginAnimation(OpacityProperty, pulse, HandoffBehavior.SnapshotAndReplace);
        }
        else Animate(_glow, OpacityProperty, 1, duration, NoticeMotion.ArriveGlow, Soft.Out);
    }

    // ----- Content -----

    private Notice? Notice => _item?.Payload as Notice;
    private NoticePrivacy Privacy(Notice notice) => Settings.NoticeRule(notice.AppKey)?.Privacy ?? Settings.NoticePrivacy;
    private string AccentHex => "#" + _accent.ToString()[3..];

    private (double Padding, double Plate) ToastMetrics(double height)
    {
        double plate = Math.Clamp(Math.Round(height - 18), 24, 32);
        // The badges overhang the plate's lower corners a little; on a round-ended toast the plate keeps far enough from the end
        // that they stay inside its curve.
        double padding = _deck.Collapsed.Style == CompactStyle.Notch ? Math.Max(16, _deck.Collapsed.Shoulder + 14) : Math.Max(16, (height - plate) / 2 + 6);
        return (padding, plate);
    }

    private Size ToastSize()
    {
        var (title, summary, _) = ToastTexts();
        double height = summary.Length > 0 ? 52 : 44;
        if (Indicator) height -= 4;
        var (padding, plate) = ToastMetrics(height);
        double text = Math.Max(Measure(title, 12.5, FontWeights.SemiBold), summary.Length > 0 ? Measure(summary, 11.5, FontWeights.Normal) : 0);
        // In 16 DIP steps, so that titles of nearly the same length give the same width. The text column has 10 DIP before it
        // and 6 after it (BuildToast).
        return new Size(Math.Ceiling((padding * 2 + plate + 16 + text + TrailingWidth()) / 16) * 16, height);
    }

    // The count chip is as wide as its number with its padding (at least 22); the progress ring and the dot take fixed room.
    private double TrailingWidth() => _item is { Count: > 1 } item ? Math.Max(22, Measure(item.Count > 99 ? "99+" : item.Count.ToString(Loc.Culture), 11, FontWeights.SemiBold) + 12)
        : Notice?.Progress is not null || HasCountdownRing(Notice) ? 26 : 12;

    // Live says the summary is the live words about the notice's time (NoticeWindow.Time.cs), worked out once here: read twice,
    // a second could pass between the two and the countdown would stop (ui-7).
    private (string Title, string Summary, bool Live) ToastTexts()
    {
        if (_item is { Kind: AttentionKind.Summary, Payload: int count })
            return (NoticeText.Count(count), Loc.T("在 BrimDeck 安静期间收到", "Arrived while BrimDeck kept quiet"), false);
        if (Notice is not { } notice) return ("", "", false);
        var (title, summary) = NoticeText.Presented(notice, Privacy(notice));
        var live = LiveWords(notice);
        summary = live ?? summary;
        if (_item is { Count: > 1 } && summary.Length == 0) summary = notice.AppName;
        return (title, summary, live is not null);
    }

    // Measured at the window's own scale: the window draws text in display mode, whose glyph widths round to device pixels.
    private double Measure(string text, double size, FontWeight weight)
    {
        if (text.Length == 0) return 0;
        var formatted = new FormattedText(text, Loc.Culture, FlowDirection.LeftToRight, new Typeface(UI.PanelFont, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Ceiling(formatted.WidthIncludingTrailingWhitespace);
    }

    // What the toast was built from (ToastShape), and the parts an update in place changes.
    private object? _toastShape;
    private TextBlock? _summaryLine, _countText;
    private Grid? _progressRing;
    private bool _toastWordsChanged;

    // Everything the toast's build depends on besides its words, the count it shows and how far a task has come.
    private object ToastShape(Notice? notice, bool summary) => (_item?.Kind, notice?.IconRef, notice?.AppKey, notice?.AppName, notice?.Origin, notice?.Category,
        notice?.TimeKind, _accent, TrailingKind(notice), summary, (_toastFrame.Rect.Width, _toastFrame.Rect.Height, _look));

    private int TrailingKind(Notice? notice) => _item is { Count: > 1 } ? 1 : HasCountdownRing(notice) ? 2 : notice?.Progress is not null ? 3 : notice is not null ? 4 : 0;

    private Color AccentFor(Notice? notice) => _look.AppColor(notice is null ? PanelTheme.ColorOf("accent") : Center.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey)));

    // Changes the shown toast's words, count and progress in place; false when it has to be built again (BuildToast).
    private bool RefreshToast()
    {
        var notice = Notice;
        var accent = AccentFor(notice);
        var (title, summary, live) = ToastTexts();
        var previous = _accent;
        _accent = accent;
        if (_announced is null || _toastShape is null || !_toastShape.Equals(ToastShape(notice, summary.Length > 0))) { _accent = previous; return false; }
        string name = SpokenName(notice, title, summary);
        _toastWordsChanged = AutomationProperties.GetName(_announced) != name;
        _announced.Text = title; Directed(_announced);
        AutomationProperties.SetName(_announced, name);
        if (_summaryLine is not null) { _summaryLine.Text = summary; Directed(_summaryLine); }
        _liveLine = live ? _summaryLine : null;
        if (_countText is not null && _item is { } item)
        {
            _countText.Text = item.Count > 99 ? "99+" : item.Count.ToString(Loc.Culture);
            if (_countText.Parent is FrameworkElement chip) AutomationProperties.SetName(chip, NoticeText.Count(item.Count));
        }
        if (_progressRing is not null && notice?.Progress is { } progress)
        {
            RedrawRing(_progressRing, progress);
            AutomationProperties.SetName(_progressRing, Loc.T($"进度 {progress:P0}", $"Progress {progress:P0}"));
        }
        if (_liveRing is Grid ring && NoticeClock.Fraction(notice?.Timing, DateTimeOffset.UtcNow) is { } fraction) RedrawRing(ring, fraction);
        return true;
    }

    // A ring's arc drawn anew inside the shown ring, so the toast around it is not laid out again.
    private void RedrawRing(Grid shown, double fraction)
    {
        var fresh = UI.Ring(fraction * 100, AccentHex, shown.Width, 2.2);
        shown.Children.Clear();
        while (fresh.Children.Count > 0) { var part = fresh.Children[0]; fresh.Children.RemoveAt(0); shown.Children.Add(part); }
    }

    private static string SpokenName(Notice? notice, string title, string summary) =>
        (notice?.AppName is { Length: > 0 } app ? app + ": " : "") + (summary.Length > 0 ? title + ", " + summary : title);

    private void BuildToast()
    {
        _toast.Children.Clear(); _toast.ColumnDefinitions.Clear();
        _countText = null; _progressRing = null;
        var size = new Size(_toastFrame.Rect.Width, _toastFrame.Rect.Height);
        var (padding, plate) = ToastMetrics(size.Height);
        _toast.Margin = new Thickness(padding, 0, padding, 0);
        _toast.Width = Math.Max(0, size.Width - padding * 2); _toast.Height = size.Height;
        _toast.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _toast.ColumnDefinitions.Add(new ColumnDefinition());
        _toast.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var notice = Notice;
        _accent = AccentFor(notice);
        var icon = Plate(notice, plate);
        icon.VerticalAlignment = VerticalAlignment.Center;
        _toast.Children.Add(icon);
        var (title, summary, live) = ToastTexts();
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        // The title carries the whole notice for screen readers and is the live region they announce.
        _announced = Line(title, 12.5, _look.Primary, FontWeights.SemiBold);
        AutomationProperties.SetName(_announced, SpokenName(notice, title, summary));
        AutomationProperties.SetLiveSetting(_announced, AutomationLiveSetting.Polite);
        texts.Children.Add(_announced);
        _summaryLine = summary.Length > 0 ? Line(summary, 11.5, _look.Secondary, FontWeights.Normal) : null;
        if (_summaryLine is not null) texts.Children.Add(_summaryLine);
        // The tick changes the summary's text only while it shows the live words.
        _liveLine = live ? _summaryLine : null;
        Grid.SetColumn(texts, 1); _toast.Children.Add(texts);
        var trailing = Trailing(notice);
        if (trailing is not null) { Grid.SetColumn(trailing, 2); _toast.Children.Add(trailing); }
        _toastShape = ToastShape(notice, summary.Length > 0);
    }

    // The count of notices this one stands for, a task's progress ring, or a dot in the application's color.
    private FrameworkElement? Trailing(Notice? notice)
    {
        if (_item is { Count: > 1 } item)
        {
            var chip = new Border { MinWidth = 22, Height = 18, CornerRadius = new CornerRadius(9), Background = UI.Brush(_look.Fill), Padding = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            chip.Child = _countText = new TextBlock { Text = item.Count > 99 ? "99+" : item.Count.ToString(Loc.Culture), FontFamily = UI.PanelFont, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(_look.Primary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(chip, NoticeText.Count(item.Count));
            return chip;
        }
        if (CountdownRing(notice) is { } countdown) return countdown;
        if (notice?.Progress is { } progress)
        {
            var ring = UI.Ring(progress * 100, AccentHex, 18, 2.2);
            ring.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(ring, Loc.T($"进度 {progress:P0}", $"Progress {progress:P0}"));
            _progressRing = ring;
            return ring;
        }
        if (notice is null) return null;
        return new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(AccentHex), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), IsHitTestVisible = false };
    }

    private static TextBlock Line(string text, double size, string color, FontWeight weight) => Directed(new()
    {
        Text = text, FontFamily = UI.PanelFont, FontSize = size, FontWeight = weight, Foreground = UI.Brush(color),
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
    });

    // A text in a right-to-left language reads and aligns from the right (NoticeText.RightToLeft).
    internal static TextBlock Directed(TextBlock block)
    {
        block.FlowDirection = NoticeText.RightToLeft(block.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        return block;
    }

    // The application's icon on a small plate in the theme's fill, which keeps white and transparent icons readable.
    private FrameworkElement Plate(Notice? notice, double size)
    {
        var plate = new Grid { Width = size, Height = size };
        plate.Children.Add(new Border { CornerRadius = new CornerRadius(size * .28), Background = UI.Brush(_look.Fill) });
        BitmapSource? image = _item?.Kind == AttentionKind.Summary ? null : Center.Icons.Get(notice?.IconRef, out _);
        if (image is not null)
        {
            var picture = new Image { Source = image, Width = size * .84, Height = size * .84, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            plate.Children.Add(picture);
        }
        else plate.Children.Add(Fallback(notice, size));
        // The state and source badges on the plate's corners (NoticeBadges).
        if (notice is not null) NoticeBadges.Decorate(plate, notice, size, _look.Glow, image is null ? NoticeGlyphs.App(notice.AppKey, notice.AppName, notice.Origin) : null);
        AutomationProperties.SetName(plate, notice?.AppName ?? "BrimDeck");
        return plate;
    }

    // Without an icon: a glyph for the kind of application when it can be told, else its first letter in its color; a bell
    // for a summary.
    private FrameworkElement Fallback(Notice? notice, double size)
    {
        if (notice is null)
        {
            var bell = NoticeBadges.Glyph(NoticeGlyph.Bell, Math.Round(size * .6), PanelTheme.ToColor(NoticeBadges.Colors.Primary));
            bell.HorizontalAlignment = HorizontalAlignment.Center; bell.VerticalAlignment = VerticalAlignment.Center;
            return bell;
        }
        if (NoticeBadges.AppGlyph(notice, size, _accent) is { } glyph) return glyph;
        var letter = notice.AppName.Trim() is { Length: > 0 } name ? char.IsSurrogate(name[0]) && name.Length > 1 ? name[..2] : name[..1].ToUpper(Loc.Culture) : "?";
        return new TextBlock { Text = letter, FontFamily = UI.DisplayFont, FontSize = Math.Round(size * .5), FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(AccentHex), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    // ----- Details -----

    private Size DetailSize()
    {
        if (_detail.Children.Count == 0) return new Size(400, NoticeGeometry.DetailMinHeight);
        // Measured at the width the card will have (a narrow monitor can make it narrower than it asks for); the desired height
        // includes the margins that pad the card.
        double width = Frame(NoticeForm.Detail, new Size(400, NoticeGeometry.DetailMinHeight)).Rect.Width, padding = DetailPadding();
        _detail.Width = width - padding * 2; _detail.Height = double.NaN;
        _detail.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(width, _detail.DesiredSize.Height);
    }

    private double DetailPadding() => _deck.Collapsed.Style == CompactStyle.Notch ? Math.Max(18, _deck.Collapsed.Shoulder + 14) : 18;
    private const double DetailTop = 14, DetailBottom = 16;

    // Everything the details show except a task's progress and the live time words (which follow their own tick).
    private string DetailKey()
    {
        var notice = Notice;
        var key = new System.Text.StringBuilder();
        key.Append(_item?.Key).Append('\u0001').Append(_item?.Kind).Append('\u0001').Append(_item?.Payload is int count ? count : -1).Append('\u0001').Append(_openFailed).Append('\u0001').Append(Loc.Language);
        if (notice is not null)
        {
            key.Append('\u0001').Append(notice.Id).Append('\u0001').Append(notice.AppName).Append('\u0001').Append(notice.Title).Append('\u0001').Append(notice.Body)
                .Append('\u0001').Append(notice.Category).Append('\u0001').Append(notice.Origin).Append('\u0001').Append(notice.Distro).Append('\u0001').Append(notice.IconRef)
                .Append('\u0001').Append(NoticeText.Relative(notice.ReceivedAt, DateTimeOffset.UtcNow)).Append('\u0001').Append(notice.SourcePresent).Append('\u0001').Append(notice.Capabilities)
                .Append('\u0001').Append(notice.Progress is not null).Append('\u0001').Append(notice.Call).Append('\u0001').Append(notice.TimeKind)
                .Append('\u0001').Append(NoticeClock.IsLive(notice.Timing, DateTimeOffset.UtcNow)).Append('\u0001').Append(Center.CanOpen(notice));
            foreach (var action in notice.Actions) key.Append('\u0001').Append(action.Key).Append('\u0002').Append(action.Label);
            foreach (var action in Center.TimeActions(notice)) key.Append('\u0001').Append(action.Key);
        }
        return key.ToString();
    }

    // The open details for the same item, changed only in a task's progress: the track follows, nothing is built again and
    // the frame does not move. False when the details must be built again.
    private bool UpdateDetailInPlace()
    {
        if (_detail.Children.Count == 0 || _detailKey is null || _detailKey != DetailKey()) return false;
        if (Notice?.Progress is { } progress && _progressDone is not null && _progressLeft is not null && _progressTrack is not null)
        {
            double done = Math.Clamp(progress, 0, 1);
            _progressDone.Width = new GridLength(done, GridUnitType.Star);
            _progressLeft.Width = new GridLength(1 - done, GridUnitType.Star);
            AutomationProperties.SetName(_progressTrack, Loc.T($"进度 {progress:P0}", $"Progress {progress:P0}"));
        }
        _detailFor = _item;
        return true;
    }

    private void BuildDetail()
    {
        _detail.Children.Clear();
        _progressDone = _progressLeft = null; _progressTrack = null;
        _detailKey = DetailKey();
        double padding = DetailPadding();
        _detail.Margin = new Thickness(padding, DetailTop, padding, DetailBottom);
        var notice = Notice;
        var stack = new StackPanel();
        _detail.Children.Add(stack);
        // Header: icon, application, where it came from and when, and the close button.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Plate(notice, 40));
        var who = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(Line(notice?.AppName ?? "BrimDeck", 12.5, _look.Primary, FontWeights.SemiBold));
        string meta = notice is null ? Loc.T("最近通知", "Recent notices")
            : NoticeText.OriginName(notice.Origin, notice.Distro) + " · " + NoticeText.Relative(notice.ReceivedAt, DateTimeOffset.UtcNow) + (notice.Category is not NoticeCategory.Message ? " · " + NoticeText.CategoryName(notice.Category) : "");
        who.Children.Add(Line(meta, 11, _look.Tertiary, FontWeights.Normal));
        Grid.SetColumn(who, 1); header.Children.Add(who);
        var close = GlyphButton("", Loc.T("关闭提示", "Close notice"), () => Dismiss(true));
        Grid.SetColumn(close, 2); header.Children.Add(close);
        stack.Children.Add(header);
        if (_item is { Kind: AttentionKind.Summary, Payload: int count })
        {
            stack.Children.Add(Wrapped(NoticeText.Count(count), 14, _look.Primary, FontWeights.SemiBold, new Thickness(0, 12, 0, 0)));
            stack.Children.Add(Wrapped(Loc.T("BrimDeck 暂停或安静期间收到的通知都在最近通知中。", "Notices that arrived while BrimDeck was paused or quiet are in the recent notices."), 12.5, _look.Secondary, FontWeights.Normal, new Thickness(0, 4, 0, 0)));
        }
        else if (notice is not null)
        {
            if (notice.Title.Length > 0) stack.Children.Add(Wrapped(notice.Title, 14, _look.Primary, FontWeights.SemiBold, new Thickness(0, 12, 0, 0), maxLines: 3));
            if (LiveDetailLine(notice) is { } live) stack.Children.Add(live);
            if (notice.Body.Length > 0)
            {
                var body = Wrapped(notice.Body, 12.5, _look.Secondary, FontWeights.Normal, new Thickness(0));
                stack.Children.Add(new ScrollViewer { Content = body, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 4, 0, 0),
                    // Reachable with Tab in keyboard mode, so a long body scrolls with the arrows and page keys (review 7b).
                    Focusable = true, IsTabStop = true });
            }
            if (notice.Progress is { } progress)
            {
                // The fill is a share of the track itself; the card's width, padding included, overshot it.
                double done = Math.Clamp(progress, 0, 1);
                var track = new Grid { Height = 4, Margin = new Thickness(0, 10, 0, 0) };
                _progressDone = new ColumnDefinition { Width = new GridLength(done, GridUnitType.Star), MinWidth = 4 };
                _progressLeft = new ColumnDefinition { Width = new GridLength(1 - done, GridUnitType.Star) };
                track.ColumnDefinitions.Add(_progressDone); track.ColumnDefinitions.Add(_progressLeft);
                _progressTrack = track;
                var rail = new Border { Background = UI.Brush(PanelTheme.Hex("track")), CornerRadius = new CornerRadius(2) };
                Grid.SetColumnSpan(rail, 2);
                track.Children.Add(rail);
                track.Children.Add(new Border { Background = UI.Brush(AccentHex), CornerRadius = new CornerRadius(2) });
                AutomationProperties.SetName(track, Loc.T($"进度 {progress:P0}", $"Progress {progress:P0}"));
                stack.Children.Add(track);
            }
            if (!notice.SourcePresent) stack.Children.Add(Wrapped(Loc.T("来源已关闭这条通知。", "The source has closed this notice."), 11, _look.Tertiary, FontWeights.Normal, new Thickness(0, 8, 0, 0)));
            if (_openFailed) stack.Children.Add(Wrapped(Loc.T("无法打开该应用；它可能不支持从通知启动。", "The application could not be opened; it may not support being started from a notice."), 11, _look.Danger, FontWeights.Normal, new Thickness(0, 8, 0, 0)));
        }
        // Actions: only what can be done for this source.
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(TextButton(Loc.T("查看全部", "View all"), ViewAll, primary: false));
        if (notice is not null && (notice.Capabilities & NoticeCapabilities.Actions) != 0)
            foreach (var action in notice.Actions.Where(a => a.Key != "default").Take(3))
                actions.Children.Add(TextButton(action.Label, () => { if (Center.Invoke(notice, action.Key)) Dismiss(false); }, primary: false));
        if (notice is not null) AddTimeActions(actions, notice);
        if (notice is not null && Center.CanOpen(notice)) actions.Children.Add(TextButton(Loc.T("打开应用", "Open app"), () => _ = OpenAsync(notice), primary: true));
        stack.Children.Add(actions);
        _actions = actions;
    }

    // Builds the details for the current item and moves the surface to their frame. The content keeps the final size while
    // the surface moves, so the text never rewraps during the morph; the surface's outline reveals it.
    private void ShowDetailFrame(double duration, IEasingFunction easing)
    {
        PrepareDetail(rebuild: true);
        SetFrame(_detailFrame, duration, NoticeMotion.Surface, easing);
    }

    // Builds and measures the details for the shown item, unless they are built for it already, and lays them out hidden, so
    // that opening them only moves the surface (the monitor's rule: build before the move, never at its start). Returns
    // whether it built them now.
    private bool PrepareDetail(bool rebuild = false)
    {
        if (!rebuild && _detail.Children.Count > 0 && ReferenceEquals(_detailFor, _item)) return false;
        // Laid out but not drawn: a collapsed element measures to nothing, which would leave the card at its least height.
        if (_detail.Visibility == Visibility.Collapsed) _detail.Visibility = Visibility.Hidden;
        BuildDetail();
        _detailFrame = Frame(NoticeForm.Detail, DetailSize());
        double padding = DetailPadding();
        _detail.Width = Math.Max(0, _detailFrame.Rect.Width - padding * 2);
        _detail.Height = Math.Max(0, _detailFrame.Rect.Height - DetailTop - DetailBottom);
        _detailFor = _item;
        return true;
    }

    private static TextBlock Wrapped(string text, double size, string color, FontWeight weight, Thickness margin, int maxLines = 0)
    {
        var block = Directed(new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = size, FontWeight = weight, Foreground = UI.Brush(color), TextWrapping = TextWrapping.Wrap, Margin = margin, LineHeight = Math.Round(size * 1.4) });
        if (maxLines > 0) { block.MaxHeight = block.LineHeight * maxLines; block.TextTrimming = TextTrimming.CharacterEllipsis; }
        return block;
    }

    private void AnnounceNow(object? sender, EventArgs e)
    {
        _announceLater?.Stop();
        if (_item is not null && !_leaving) Announce();
    }

    private Button GlyphButton(string glyph, string name, Action click)
    {
        var button = TextButton("", click, primary: false, flat: true);
        ((Border)button.Content).Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Foreground = UI.Brush(_look.Secondary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ((Border)button.Content).Padding = new Thickness(0);
        button.Width = 32; button.Height = 32; button.Margin = new Thickness(0); button.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(button, name); button.ToolTip = name;
        return button;
    }

    // A button in the theme's fill, hover and pressed colors, or its accent for the main action; at least 32 DIPs high.
    private Button TextButton(string text, Action click, bool primary, bool flat = false)
    {
        string rest = primary ? _look.Accent : flat ? "Transparent" : _look.Fill;
        var face = new Border { CornerRadius = new CornerRadius(8), Background = UI.Brush(rest), Padding = new Thickness(12, 0, 12, 0), MinHeight = 32, BorderBrush = UI.Brush(primary ? _look.OnAccent : _look.Accent) };
        face.Child = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = 12, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, Foreground = UI.Brush(primary ? _look.OnAccent : _look.Primary), VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = face, Margin = new Thickness(8, 0, 0, 0), MinHeight = 32, Cursor = Cursors.Hand, FocusVisualStyle = null,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        // High contrast lights hover and press in the highlight color, so the label takes the highlight's text color (review 5);
        // the glyph button's label is set after this, hence looked up when lit.
        Brush? unlit = null;
        void Lit(bool lit)
        {
            if (primary || !_look.HighContrast || face.Child is not TextBlock label) return;
            if (lit) { unlit ??= label.Foreground; label.Foreground = UI.Brush(_look.OnAccent); }
            else if (unlit is not null) label.Foreground = unlit;
        }
        button.MouseEnter += (_, _) => { if (!primary) { face.Background = UI.Brush(_look.Hover); Lit(true); } else face.Opacity = .88; };
        button.MouseLeave += (_, _) => { face.Background = UI.Brush(rest); face.Opacity = 1; Lit(false); };
        button.PreviewMouseLeftButtonDown += (_, _) => { if (!primary) { face.Background = UI.Brush(_look.Pressed); Lit(true); } };
        // Only for focus moved by the keyboard: a button takes focus when clicked too, and should not then wear a ring.
        button.GotKeyboardFocus += (_, _) => { if (InputManager.Current.MostRecentInputDevice is KeyboardDevice) face.BorderThickness = new Thickness(1.5); };
        button.LostKeyboardFocus += (_, _) => face.BorderThickness = new Thickness(0);
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, text);
        return button;
    }

    private void OpenDetail()
    {
        if (_item is null || _showingDetail) return;
        _showingDetail = true;
        _hoverOpen.Stop();
        Center.Hold(_deck, true);
        if (Notice is { } notice) Center.Hub.MarkViewed(notice.Id);
        _escape?.SetEnabled(!_keyboard);
        // Usually built while the pointer rested on the notice (PointerEntered). Built only now (a click, the hotkey), the card
        // opens once its first layout is done, rather than losing the first frames of its move to it.
        bool built = PrepareDetail();
        _detail.Visibility = Visibility.Visible;
        int generation = _generation;
        if (!built || Duration() <= 0) GrowIntoDetail();
        else Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (generation == _generation && _showingDetail && _item is not null) GrowIntoDetail(); });
    }

    // The notice opens into its details as the panel opens: on the opening spring, its text gone before the details arrive.
    private void GrowIntoDetail()
    {
        double duration = Duration();
        SetFrame(_detailFrame, duration, NoticeMotion.Surface, Soft.Bounce);
        Animate(_toast, OpacityProperty, 0, duration, NoticeMotion.OpenOut, Soft.InOut, () => { if (_showingDetail) _toast.Visibility = Visibility.Hidden; });
        Animate(_detail, OpacityProperty, 1, duration, NoticeMotion.OpenIn, Soft.Out);
    }

    // Back from the details to the arriving notice, which then runs out its remaining time. It closes as the panel closes: the
    // details leave first, the card settles, the notice's text lands last. The details stay built (hidden) for the next opening.
    private void LeaveDetail()
    {
        if (!_showingDetail || _item is null) return;
        _showingDetail = false;
        _toast.Visibility = Visibility.Visible;
        var toast = Frame(NoticeForm.Toast, ToastSize());
        if (toast != _toastFrame) { _toastFrame = toast; BuildToast(); }
        double duration = Duration();
        SetFrame(_toastFrame, duration, NoticeMotion.ReturnSurface, Soft.Settle);
        Animate(_detail, OpacityProperty, 0, duration, NoticeMotion.ReturnContent, Soft.InOut, () => { if (!_showingDetail && _detail.Children.Count > 0) _detail.Visibility = Visibility.Hidden; });
        Animate(_toast, OpacityProperty, 1, duration, NoticeMotion.Landing, Soft.Out);
        _escape?.SetEnabled(false);
        if (!_surface.IsMouseOver) Center.Hold(_deck, false);
    }

    private async Task OpenAsync(Notice notice)
    {
        if (await Center.OpenAsync(notice)) { Dismiss(false); return; }
        _openFailed = true;
        if (_showingDetail && ReferenceEquals(Notice, notice)) ShowDetailFrame(InPlace, Soft.Follow);
        else _detailFor = null;
    }

    private void ViewAll()
    {
        var deck = _deck;
        Dismiss(false);
        deck.OpenNoticesPage(byKey: false);
    }

    private void Dismiss(bool byUser)
    {
        if (_item is null) return;
        Center.Dismiss(_deck, reportToSource: byUser);
    }

    // ----- Pointer and keyboard -----

    private void PointerEntered()
    {
        _leaveClose.Stop();
        if (_item is null || _leaving) return;
        // A notice that arrives under a resting pointer does not open by itself: the pointer has to leave and come back, or click.
        if (_arrivedUnderPointer) return;
        Center.Hold(_deck, true);
        _escape?.SetEnabled(true);
        if (!_showingDetail && Settings.NoticeHoverDetails)
        {
            _hoverOpen.Start();
            // The details are built and laid out while the pointer rests, after anything more urgent, so the card only has to
            // move when it opens.
            var item = _item;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { if (ReferenceEquals(_item, item) && !_showingDetail && !_leaving && IsVisible) PrepareDetail(); });
        }
    }

    private void PointerLeft()
    {
        _hoverOpen.Stop();
        _arrivedUnderPointer = false;
        if (_item is null) return;
        if (_showingDetail && !_keyboard)
        {
            _leaveClose.Interval = TimeSpan.FromMilliseconds(Math.Max(300, Settings.CloseDelay));
            _leaveClose.Start();
            return;
        }
        if (!_keyboard) { _escape?.SetEnabled(false); Center.Hold(_deck, false); }
    }

    private void Clicked()
    {
        if (_item is null || _leaving) return;
        _arrivedUnderPointer = false;
        if (_item.Kind == AttentionKind.Summary) { ViewAll(); return; }
        if (!_showingDetail) OpenDetail();
    }

    // The notices hotkey: details open and the keyboard moves through the buttons; Escape closes.
    public void EnterKeyboard()
    {
        if (_item is null) return;
        _keyboard = true;
        if (!_showingDetail) OpenDetail();
        Center.Hold(_deck, true);
        _escape?.SetEnabled(false);
        var own = new WindowInteropHelper(this).Handle;
        if (NoticeNative.Foreground() is var previous && previous != own) _previousForeground = previous;
        NoticeNative.SetActivatable(own, true);
        Activate();
        Dispatcher.BeginInvoke(() => (_actions?.Children.OfType<Button>().LastOrDefault() ?? FirstButton(_detail))?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EndKeyboard()
    {
        _keyboard = false;
        var own = new WindowInteropHelper(this).Handle;
        NoticeNative.SetActivatable(own, false);
        // At once, not after the notice's way out: keys typed meanwhile go to where the user was (review 7a).
        NoticeNative.ReturnForeground(own, _previousForeground);
        _previousForeground = IntPtr.Zero;
        if (_item is not null && !_surface.IsMouseOver) { LeaveDetail(); Center.Hold(_deck, false); }
    }

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Dismiss(true); e.Handled = true; }
    }

    private static Button? FirstButton(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { IsVisible: true } button) return button;
            if (FirstButton(child) is { } found) return found;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    // Screen readers hear the new notice once, politely, without the window taking focus.
    // A burst would queue dozens of announcements that go on long after the notice left (review 9): at most one a second,
    // and the latest words once the burst pauses.
    private static readonly TimeSpan AnnounceSpacing = TimeSpan.FromSeconds(1);
    private DateTime _announcedAt;
    private System.Windows.Threading.DispatcherTimer? _announceLater;

    private void Announce()
    {
        if (_announced is null || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        var wait = _announcedAt + AnnounceSpacing - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            _announceLater ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher);
            _announceLater.Tick -= AnnounceNow; _announceLater.Tick += AnnounceNow;
            if (!_announceLater.IsEnabled) { _announceLater.Interval = wait; _announceLater.Start(); }
            return;
        }
        _announcedAt = DateTime.UtcNow;
        (UIElementAutomationPeer.FromElement(_announced) ?? UIElementAutomationPeer.CreatePeerForElement(_announced))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
