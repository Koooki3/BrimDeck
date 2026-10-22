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

namespace BrimDeck;

// One panel's dynamic notices. The window lies over the panel's collapsed island and is shown only while a notice is: the
// notice grows out of the island's exact outline and color, shows the application's icon, title and summary, opens into its
// details on hover or click, and shrinks back into the island at the end. Its window is sized once per notice to hold the
// largest form, so nothing resizes the window while it moves; only the drawn surface is hit-tested, the rest lets clicks
// through. It never takes focus unless the notices hotkey asks for the keyboard.
internal sealed class NoticeWindow : Window
{
    // Durations at the default animation length (360 ms); the user's length scales them.
    private const double EnterMs = 280, ExitMs = 180, DetailMs = 320, HoverOpenMs = 250, GlowMargin = 32;
    private readonly MainWindow _deck;
    private readonly App _app;
    private readonly Canvas _stage = new() { Background = null };
    private readonly NoticeSurface _glow = new() { IsHitTestVisible = false };
    private readonly NoticeSurface _surface = new() { Cursor = Cursors.Hand, Focusable = false };
    private readonly Grid _content = new();
    private readonly Grid _toast = new() { Opacity = 0 }, _detail = new() { Opacity = 0, Visibility = Visibility.Collapsed };
    private readonly TranslateTransform _contentShift = new();
    private readonly PromptTimer _hoverOpen, _leaveClose;
    private EscapeDismissal? _escape;
    private AttentionItem? _item;
    private NoticeLook _look = NoticeLook.For(NoticeTheme.Classic, true);
    private Color _accent = Colors.White;
    private NoticeFrame _collapsed = null!, _toastFrame = null!, _detailFrame = null!;
    private Rect _stageRect;
    private bool _showingDetail, _detailByHover, _keyboard, _arrivedUnderPointer, _leaving, _openFailed;
    private int _generation;

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
        _content.RenderTransform = _contentShift;
        // The surface's text sits on an opaque or nearly opaque fill, so ClearType stays on for it.
        RenderOptions.SetClearTypeHint(_content, ClearTypeHint.Enabled);
        AutomationProperties.SetName(_surface, Loc.T("灵动提示", "Dynamic notice"));
        AutomationProperties.SetLiveSetting(_toast, AutomationLiveSetting.Polite);
        _hoverOpen = new(Dispatcher, () => { _hoverOpen!.Stop(); if (_item is not null && _surface.IsMouseOver && !_showingDetail) OpenDetail(byHover: true); }) { Interval = TimeSpan.FromMilliseconds(HoverOpenMs) };
        _leaveClose = new(Dispatcher, () => { _leaveClose!.Stop(); if (!_surface.IsMouseOver && !_keyboard) LeaveDetail(); });
        _surface.MouseEnter += (_, _) => PointerEntered();
        _surface.MouseLeave += (_, _) => PointerLeft();
        _surface.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null) return; Clicked(); e.Handled = true; };
        _surface.MouseRightButtonUp += (_, e) => e.Handled = true;
        _toast.HorizontalAlignment = HorizontalAlignment.Center; _toast.VerticalAlignment = VerticalAlignment.Center;
        _detail.HorizontalAlignment = HorizontalAlignment.Center; _detail.VerticalAlignment = VerticalAlignment.Top;
        SourceInitialized += (_, _) => { WindowsHost.ConfigureOverlay(this); _escape = new EscapeDismissal(this, () => Dismiss(true)); };
        PreviewKeyDown += KeyPressed;
        Deactivated += (_, _) => { if (_keyboard) EndKeyboard(); };
        _app.Notices.Icons.Loaded += IconLoaded;
        Closed += (_, _) => { _escape?.Dispose(); _hoverOpen.Stop(); _leaveClose.Stop(); _app.Notices.Icons.Loaded -= IconLoaded; };
    }

    // True while a notice covers the island: the panel then leaves hovering to this window.
    public bool Covering => _item is not null || IsVisible && !_leaving;
    public AttentionItem? Item => _item;
    private DeckSettings Settings => _app.Settings;
    private NoticeCenter Center => _app.Notices;

    // ----- Showing, updating and ending -----

    public void Present(AttentionItem item)
    {
        bool continuing = IsVisible;
        _item = item; _leaving = false; _openFailed = false; _generation++;
        _look = NoticeLook.For(Settings.NoticeTheme, NoticeLook.IsDark(Settings.NoticeAppearance, Settings.Theme));
        _showingDetail = false; _keyboard = false;
        Layout();
        BuildToast();
        if (!continuing)
        {
            // Starts exactly as the island: its outline, its position and its color.
            SetFrame(_collapsed, 0);
            _surface.IslandColor = _deck.Collapsed.Style == CompactStyle.Line ? MainWindow.IndicatorColor : NoticeLook.Island;
            Set(_surface, NoticeSurface.IslandOpacityProperty, 1d);
            Set(_toast, OpacityProperty, 0d); Set(_detail, OpacityProperty, 0d); _detail.Visibility = Visibility.Collapsed; _toast.Visibility = Visibility.Visible;
            Set(_glow, OpacityProperty, 0d);
            Show();
            // The notice now stands exactly where the island is, in its outline and color; the island underneath is hidden so
            // that translucent themes do not show it through.
            _deck.SetIslandCovered(true);
            // Shown on a monitor with another scale, Windows has just given the window that scale; it is placed again in it.
            Place();
            NoticeNative.BringToTop(new WindowInteropHelper(this).Handle);
        }
        var cursor = CursorInStage();
        _arrivedUnderPointer = new Rect(_toastFrame.Rect.X - _stageRect.X, _toastFrame.Rect.Y - _stageRect.Y, _toastFrame.Rect.Width, _toastFrame.Rect.Height).Contains(cursor);
        double duration = Duration(EnterMs);
        SetFrame(_toastFrame, duration);
        Animate(_surface, NoticeSurface.IslandOpacityProperty, 0, duration, 0, .5);
        Animate(_detail, OpacityProperty, 0, duration * .5, 0, 1, () => { if (!_showingDetail) _detail.Visibility = Visibility.Collapsed; });
        _toast.Visibility = Visibility.Visible;
        Animate(_toast, OpacityProperty, 1, duration, continuing ? .2 : .35, 1);
        if (_look.Motion == NoticeMotion.Slide && duration > 0) { _contentShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(duration)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }); }
        GlowIn(duration);
        Announce();
    }

    // The shown notice changed in place (a task's progress, a burst from the same application).
    public void Update(AttentionItem item)
    {
        if (_item is null) return;
        _item = item;
        _toastFrame = Frame(NoticeForm.Toast, ToastSize());
        BuildToast();
        if (_showingDetail) ShowDetailFrame(Duration(DetailMs) * .6);
        else SetFrame(_toastFrame, Duration(EnterMs) * .6);
        Announce();
    }

    // The item ended. Unless another one follows at once (Present), the notice shrinks back into the island and the window hides.
    public void End(AttentionItem item)
    {
        if (!ReferenceEquals(_item, item) && _item?.Key != item.Key) return;
        _item = null; _leaving = true;
        _hoverOpen.Stop(); _leaveClose.Stop();
        if (_keyboard) EndKeyboard();
        _escape?.SetEnabled(false);
        int generation = ++_generation;
        // The island may have changed meanwhile (a window rule turned it into the indicator).
        _collapsed = NoticeGeometry.Collapsed(_deck.Collapsed);
        _surface.IslandColor = _deck.Collapsed.Style == CompactStyle.Line ? MainWindow.IndicatorColor : NoticeLook.Island;
        double duration = Duration(ExitMs);
        Animate(_toast, OpacityProperty, 0, duration, 0, .45);
        Animate(_detail, OpacityProperty, 0, duration, 0, .45);
        Animate(_glow, OpacityProperty, 0, duration, 0, .6);
        Animate(_surface, NoticeSurface.IslandOpacityProperty, 1, duration, .4, 1);
        SetFrame(_collapsed, duration, () => { if (generation == _generation && _item is null) Finish(); });
    }

    // The panel opens over the island: a notice still on its way out is removed at once, so the panel is never hidden.
    public void Abort()
    {
        if (_item is not null || !IsVisible) return;
        _generation++;
        Finish();
    }

    private void Finish()
    {
        _leaving = false; _showingDetail = false;
        // The notice ended in the island's outline and color: the island shows again in the same frame as the window hides.
        _deck.SetIslandCovered(false);
        Hide();
        // The surface keeps no stale animations, so the next notice starts from the island again.
        foreach (var property in new[] { Canvas.LeftProperty, Canvas.TopProperty, WidthProperty, HeightProperty })
        { _surface.BeginAnimation(property, null); _glow.BeginAnimation(property, null); }
        _toast.Children.Clear(); _detail.Children.Clear();
    }

    // The icon of the shown notice arrived after it was drawn: the content is drawn again in place, the frame stays.
    private void IconLoaded(string iconRef)
    {
        if (_leaving || Notice?.IconRef != iconRef) return;
        BuildToast();
        if (_showingDetail) BuildDetail();
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
        _detailFrame = Frame(NoticeForm.Detail, DetailSize());
    }

    // Keeps the surface where it is on screen while the stage's origin moves.
    private void ShiftStage(Rect from, Rect to)
    {
        double dx = from.X - to.X, dy = from.Y - to.Y;
        foreach (var element in new FrameworkElement[] { _surface, _glow })
        {
            double left = Canvas.GetLeft(element), top = Canvas.GetTop(element);
            Set(element, Canvas.LeftProperty, (double.IsNaN(left) ? 0 : left) + dx);
            Set(element, Canvas.TopProperty, (double.IsNaN(top) ? 0 : top) + dy);
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

    private void SetFrame(NoticeFrame frame, double duration, Action? completed = null)
    {
        double left = frame.Rect.X - _stageRect.X, top = frame.Rect.Y - _stageRect.Y;
        bool line = _deck.Collapsed.Style == CompactStyle.Line && ReferenceEquals(frame, _collapsed);
        foreach (var element in new NoticeSurface[] { _surface, _glow })
        {
            Animate(element, Canvas.LeftProperty, left, duration, 0, 1);
            Animate(element, Canvas.TopProperty, top, duration, 0, 1);
            Animate(element, WidthProperty, Math.Max(0, frame.Rect.Width), duration, 0, 1);
            Animate(element, HeightProperty, Math.Max(0, frame.Rect.Height), duration, 0, 1, ReferenceEquals(element, _surface) ? completed : null);
            Animate(element, NoticeSurface.TopRadiusProperty, frame.TopRadius, duration, 0, 1);
            Animate(element, NoticeSurface.BottomRadiusProperty, line ? frame.Rect.Height / 2 : frame.BottomRadius, duration, 0, 1);
            Animate(element, NoticeSurface.ShoulderProperty, frame.Shoulder, duration, 0, 1);
        }
    }

    // ----- Motion -----

    private double Duration(double baseMs)
    {
        if (!Settings.Animations || !SystemParameters.ClientAreaAnimation) return 0;
        return baseMs * Math.Clamp(Settings.AnimationDuration / 360d, .5, 2);
    }

    // A CSS-style ease-out curve; EaseIn mode makes WPF use it as written instead of mirroring it.
    private IEasingFunction Ease()
    {
        var curve = new BezierEase(.3, 0, .1, 1) { EasingMode = EasingMode.EaseIn };
        return _look.Motion == NoticeMotion.Steps ? new StepEase(curve, 6) { EasingMode = EasingMode.EaseIn } : curve;
    }

    private void Animate(IAnimatable target, DependencyProperty property, double value, double duration, double start, double end, Action? completed = null)
    {
        if (duration <= 0 || !double.IsFinite(value)) { Set(target, property, double.IsFinite(value) ? value : 0); completed?.Invoke(); return; }
        var animation = new DoubleAnimation
        {
            To = value, EasingFunction = Ease(),
            BeginTime = TimeSpan.FromMilliseconds(duration * start), Duration = TimeSpan.FromMilliseconds(Math.Max(1, duration * (end - start)))
        };
        if (completed is not null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Set(IAnimatable target, DependencyProperty property, object value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }

    private void GlowIn(double duration)
    {
        bool hard = _look.ShadowBlur <= 0;
        var color = _look.Accent == NoticeAccentStyle.Edge ? Blend(_accent) : _look.Shadow;
        // An opaque surface casts its shadow from a black copy of its shape behind it. A translucent one would show that copy
        // through itself, so its shadow comes from the outline alone, which for neon is the light of its edge.
        bool translucent = _look.Fill is not SolidColorBrush { Color.A: 255 };
        _glow.Look = translucent
            ? _look with { Fill = Brushes.Transparent, Sheen = null, Pattern = null, Rim = new SolidColorBrush(color), RimWidth = 1.5 }
            : _look with { Fill = UI.Brush("#FF000000"), Sheen = null, Pattern = null, RimWidth = 0 };
        _glow.Effect = _look.ShadowOpacity <= 0 ? null : new DropShadowEffect
        {
            Color = color, BlurRadius = hard ? 0 : _look.ShadowBlur, ShadowDepth = _look.ShadowDepth, Direction = 270,
            Opacity = _look.ShadowOpacity, RenderingBias = RenderingBias.Performance
        };
        if (_look.Motion == NoticeMotion.Pulse && duration > 0)
        {
            // Neon: one short flash of light as the notice arrives, then a steady low glow.
            var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(duration * 2.2) };
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(.4), new CubicEase { EasingMode = EasingMode.EaseOut }));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(.45, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseInOut }));
            _glow.BeginAnimation(OpacityProperty, pulse, HandoffBehavior.SnapshotAndReplace);
        }
        else Animate(_glow, OpacityProperty, 1, duration, .3, 1);
    }

    // The accent mixed toward the surface by the strength setting.
    private Color Blend(Color accent)
    {
        double strength = Settings.NoticeAccentStrength;
        return Color.FromArgb((byte)Math.Round(255 * Math.Clamp(.25 + .75 * strength, 0, 1)), accent.R, accent.G, accent.B);
    }

    // ----- Content -----

    private Notice? Notice => _item?.Payload as Notice;
    private NoticePrivacy Privacy(Notice notice) => Settings.NoticeRule(notice.AppKey)?.Privacy ?? Settings.NoticePrivacy;

    private (double Padding, double Plate) ToastMetrics(double height)
    {
        double plate = Math.Clamp(Math.Round(height - 20), 22, 30);
        double padding = _deck.Collapsed.Style == CompactStyle.Notch ? Math.Max(16, _deck.Collapsed.Shoulder + 14) : Math.Max(8, (height - plate) / 2);
        return (padding, plate);
    }

    private Size ToastSize()
    {
        var (title, summary) = ToastTexts();
        double height = summary.Length > 0 ? 52 : 44;
        if (_deck.Collapsed.Style == CompactStyle.Line) height -= 4;
        var (padding, plate) = ToastMetrics(height);
        double text = Math.Max(Measure(title, 12.5, FontWeights.SemiBold), summary.Length > 0 ? Measure(summary, 11.5, FontWeights.Normal) : 0);
        double trailing = TrailingWidth();
        return new Size(padding * 2 + plate + 10 + text + trailing, height);
    }

    private double TrailingWidth() => _item is { Count: > 1 } ? 30 : Notice?.Progress is not null ? 26 : 4;

    private (string Title, string Summary) ToastTexts()
    {
        if (_item is { Kind: AttentionKind.Summary, Payload: int count })
            return (NoticeText.Count(count), Loc.T("在 BrimDeck 安静期间收到", "Arrived while BrimDeck kept quiet"));
        if (Notice is not { } notice) return ("", "");
        var (title, summary) = NoticeText.Presented(notice, Privacy(notice));
        if (_item is { Count: > 1 } && summary.Length == 0) summary = notice.AppName;
        return (title, summary);
    }

    private static double Measure(string text, double size, FontWeight weight)
    {
        if (text.Length == 0) return 0;
        var formatted = new FormattedText(text, Loc.Culture, FlowDirection.LeftToRight, new Typeface(UI.PanelFont, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White, 1.25);
        return Math.Ceiling(formatted.WidthIncludingTrailingWhitespace);
    }

    private void BuildToast()
    {
        _toast.Children.Clear(); _toast.ColumnDefinitions.Clear();
        var size = new Size(_toastFrame.Rect.Width, _toastFrame.Rect.Height);
        var (padding, plate) = ToastMetrics(size.Height);
        _toast.Margin = new Thickness(padding, 0, padding, 0);
        _toast.Width = Math.Max(0, size.Width - padding * 2); _toast.Height = size.Height;
        _toast.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _toast.ColumnDefinitions.Add(new ColumnDefinition());
        _toast.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var notice = Notice;
        _accent = notice is null ? Color.FromRgb(0x9C, 0xB9, 0xFF) : Center.Icons.AccentFor(notice, Settings.NoticeRule(notice.AppKey));
        _surface.Look = _look;
        _surface.AccentRim = _look.Accent == NoticeAccentStyle.Edge ? new SolidColorBrush(Blend(_accent)) : null;
        var icon = Plate(notice, plate);
        icon.VerticalAlignment = VerticalAlignment.Center;
        _toast.Children.Add(icon);
        var (title, summary) = ToastTexts();
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        texts.Children.Add(Line(title, 12.5, _look.Primary, FontWeights.SemiBold));
        if (summary.Length > 0) texts.Children.Add(Line(summary, 11.5, _look.Secondary, FontWeights.Normal));
        Grid.SetColumn(texts, 1); _toast.Children.Add(texts);
        AutomationProperties.SetName(_toast, summary.Length > 0 ? title + ", " + summary : title);
        var trailing = Trailing(notice);
        if (trailing is not null) { Grid.SetColumn(trailing, 2); _toast.Children.Add(trailing); }
        if (_look.Accent == NoticeAccentStyle.Tag) _toast.Children.Add(AccentTag(size.Height));
    }

    // The count of notices this one stands for, or a task's progress ring.
    private FrameworkElement? Trailing(Notice? notice)
    {
        if (_item is { Count: > 1 } item)
        {
            var chip = new Border { MinWidth = 22, Height = 18, CornerRadius = new CornerRadius(_look.Stepped ? 2 : 9), Background = _look.Plate, Padding = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            chip.Child = new TextBlock { Text = item.Count > 99 ? "99+" : item.Count.ToString(Loc.Culture), FontFamily = UI.PanelFont, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(_look.Primary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(chip, NoticeText.Count(item.Count));
            return chip;
        }
        if (notice?.Progress is { } progress)
        {
            var ring = UI.Ring(progress * 100, "#" + Blend(_accent).ToString()[3..], 18, 2.2);
            ring.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(ring, Loc.T($"进度 {progress:P0}", $"Progress {progress:P0}"));
            return ring;
        }
        if (_look.Accent == NoticeAccentStyle.Dot && notice is not null)
            return new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Blend(_accent)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        return null;
    }

    // The paper theme's colored tag along the left edge.
    private FrameworkElement AccentTag(double height)
    {
        var tag = new Border { Width = 3, Height = Math.Max(0, height - 18), CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(Blend(_accent)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-9, 0, 0, 0), IsHitTestVisible = false };
        Grid.SetColumnSpan(tag, 3);
        return tag;
    }

    private TextBlock Line(string text, double size, string color, FontWeight weight) => new()
    {
        Text = text, FontFamily = UI.PanelFont, FontSize = size, FontWeight = weight, Foreground = UI.Brush(color),
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
    };

    // The application's icon on a small plate. The plate keeps white and transparent icons readable on any theme.
    private FrameworkElement Plate(Notice? notice, double size)
    {
        double radius = _look.Stepped ? 2 : size * .28;
        var plate = new Grid { Width = size, Height = size };
        var background = new Border { CornerRadius = new CornerRadius(radius), Background = _look.Plate };
        if (_look.Accent == NoticeAccentStyle.Mist) background.Background = new SolidColorBrush(Color.FromArgb((byte)(40 + 60 * Settings.NoticeAccentStrength), _accent.R, _accent.G, _accent.B));
        if (_look.Accent == NoticeAccentStyle.Frame) { background.BorderBrush = new SolidColorBrush(Blend(_accent)); background.BorderThickness = new Thickness(2); }
        plate.Children.Add(background);
        BitmapSource? image = _item?.Kind == AttentionKind.Summary ? null : Center.Icons.Get(notice?.IconRef, out _);
        if (image is not null)
        {
            var picture = new Image { Source = image, Width = size * .72, Height = size * .72, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(picture, _look.Stepped ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            plate.Children.Add(picture);
        }
        else plate.Children.Add(Fallback(notice, size));
        if (notice is not null && NoticeLook.CategoryColor(notice.Category) is { Length: > 0 } mark && notice.Category != NoticeCategory.Task)
        {
            double dot = Math.Round(size * .36);
            plate.Children.Add(new Ellipse { Width = dot, Height = dot, Fill = UI.Brush(mark), Stroke = _look.Fill, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -dot * .25, -dot * .25) });
        }
        AutomationProperties.SetName(plate, notice?.AppName ?? "BrimDeck");
        return plate;
    }

    // Without an icon: the application's first letter in its color, or a bell for a summary.
    private FrameworkElement Fallback(Notice? notice, double size)
    {
        if (notice is null)
            return new Viewbox { Width = size * .56, Height = size * .56, Child = new Path { Data = Geometry.Parse("M12,3 C8.7,3 6.5,5.6 6.5,8.8 V12.6 L4.8,15.6 H19.2 L17.5,12.6 V8.8 C17.5,5.6 15.3,3 12,3 Z M9.6,17.6 A2.4,2.4 0 0 0 14.4,17.6"), Stroke = UI.Brush(_look.Primary), StrokeThickness = 1.6, Width = 24, Height = 24 } };
        var letter = notice.AppName.Trim() is { Length: > 0 } name ? char.IsSurrogate(name[0]) && name.Length > 1 ? name[..2] : name[..1].ToUpper(Loc.Culture) : "?";
        return new TextBlock { Text = letter, FontFamily = UI.DisplayFont, FontSize = Math.Round(size * .5), FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Blend(_accent)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    // ----- Details -----

    private Size DetailSize()
    {
        if (_detail.Children.Count == 0) return new Size(400, NoticeGeometry.DetailMinHeight);
        // Measured at the width the card will have; the desired height includes the margins that pad the card.
        // A narrow monitor can make the card narrower than it asks for; the text is measured at the width it gets.
        double width = Frame(NoticeForm.Detail, new Size(400, NoticeGeometry.DetailMinHeight)).Rect.Width, padding = DetailPadding();
        _detail.Width = width - padding * 2; _detail.Height = double.NaN;
        _detail.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(width, _detail.DesiredSize.Height);
    }

    private double DetailPadding() => _deck.Collapsed.Style == CompactStyle.Notch ? Math.Max(18, _deck.Collapsed.Shoulder + 14) : 18;
    private double DetailTop() => 14;

    private void BuildDetail()
    {
        _detail.Children.Clear(); _detail.RowDefinitions.Clear();
        double padding = DetailPadding();
        _detail.Margin = new Thickness(padding, DetailTop(), padding, 16);
        var notice = Notice;
        var stack = new StackPanel();
        _detail.Children.Add(stack);
        // Header: icon, application, where it came from and when, and the close button.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Plate(notice, 32));
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
            if (notice.Body.Length > 0)
            {
                var body = Wrapped(notice.Body, 12.5, _look.Secondary, FontWeights.Normal, new Thickness(0));
                var scroll = new ScrollViewer { Content = body, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 4, 0, 0), Focusable = false };
                stack.Children.Add(scroll);
            }
            if (notice.Progress is { } progress)
            {
                var track = new Grid { Height = 4, Margin = new Thickness(0, 10, 0, 0) };
                track.Children.Add(new Border { Background = _look.Plate, CornerRadius = new CornerRadius(_look.Stepped ? 0 : 2) });
                track.Children.Add(new Border { Background = new SolidColorBrush(Blend(_accent)), CornerRadius = new CornerRadius(_look.Stepped ? 0 : 2), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(4, 364 * progress) });
                AutomationProperties.SetName(track, Loc.T($"进度 {progress:P0}", $"Progress {progress:P0}"));
                stack.Children.Add(track);
            }
            if (!notice.SourcePresent) stack.Children.Add(Wrapped(Loc.T("来源已关闭这条通知。", "The source has closed this notice."), 11, _look.Tertiary, FontWeights.Normal, new Thickness(0, 8, 0, 0)));
            if (_openFailed) stack.Children.Add(Wrapped(Loc.T("无法打开该应用；它可能不支持从通知启动。", "The application could not be opened; it may not support being started from a notice."), 11, "#FF8A80", FontWeights.Normal, new Thickness(0, 8, 0, 0)));
        }
        // Actions: only what can be done for this source.
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(TextButton(Loc.T("查看全部", "View all"), ViewAll, primary: false));
        if (notice is not null && (notice.Capabilities & NoticeCapabilities.Actions) != 0)
            foreach (var action in notice.Actions.Where(a => a.Key != "default").Take(3))
                actions.Children.Add(TextButton(action.Label, () => { if (Center.Invoke(notice, action.Key)) Dismiss(false); }, primary: false));
        if (notice is not null && Center.CanOpen(notice)) actions.Children.Add(TextButton(Loc.T("打开应用", "Open app"), () => _ = OpenAsync(notice), primary: true));
        stack.Children.Add(actions);
    }

    // Builds the details for the current item and moves the surface to their frame. The content keeps the final size while
    // the surface moves, so the text never rewraps during the morph; the surface's outline reveals it.
    private void ShowDetailFrame(double duration)
    {
        BuildDetail();
        _detailFrame = Frame(NoticeForm.Detail, DetailSize());
        double padding = DetailPadding();
        _detail.Width = Math.Max(0, _detailFrame.Rect.Width - padding * 2);
        _detail.Height = Math.Max(0, _detailFrame.Rect.Height - DetailTop() - 16);
        SetFrame(_detailFrame, duration);
    }

    private TextBlock Wrapped(string text, double size, string color, FontWeight weight, Thickness margin, int maxLines = 0)
    {
        var block = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = size, FontWeight = weight, Foreground = UI.Brush(color), TextWrapping = TextWrapping.Wrap, Margin = margin, LineHeight = Math.Round(size * 1.4) };
        if (maxLines > 0) { block.MaxHeight = block.LineHeight * maxLines; block.TextTrimming = TextTrimming.CharacterEllipsis; }
        return block;
    }

    private Button GlyphButton(string glyph, string name, Action click)
    {
        var button = TextButton("", click, primary: false);
        button.Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Foreground = UI.Brush(_look.Secondary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        button.Width = 32; button.Height = 32; button.Margin = new Thickness(0); button.Padding = new Thickness(0); button.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(button, name); button.ToolTip = name;
        return button;
    }

    // A flat button in the theme's colors; at least 32 DIPs high so it is easy to hit.
    private Button TextButton(string text, Action click, bool primary)
    {
        var fill = primary ? _look.PrimaryButton : _look.Button;
        var face = new Border { CornerRadius = new CornerRadius(_look.Stepped ? 2 : 8), Background = fill, Padding = new Thickness(12, 0, 12, 0), MinHeight = 32 };
        face.Child = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = 12, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, Foreground = UI.Brush(primary ? _look.PrimaryButtonText : _look.Primary), VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), MinHeight = 32, Cursor = Cursors.Hand, FocusVisualStyle = null };
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        button.Content = face;
        button.MouseEnter += (_, _) => face.Background = primary ? fill : _look.ButtonHover;
        button.MouseLeave += (_, _) => face.Background = fill;
        button.GotKeyboardFocus += (_, _) => { face.BorderBrush = UI.Brush(_look.Primary); face.BorderThickness = new Thickness(1.5); };
        button.LostKeyboardFocus += (_, _) => face.BorderThickness = new Thickness(0);
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, text);
        return button;
    }

    private void OpenDetail(bool byHover)
    {
        if (_item is null || _showingDetail) return;
        _showingDetail = true; _detailByHover = byHover;
        _hoverOpen.Stop();
        Center.Hold(_deck, true);
        if (Notice is { } notice) Center.Hub.MarkViewed(notice.Id);
        _detail.Visibility = Visibility.Visible;
        double duration = Duration(DetailMs);
        ShowDetailFrame(duration);
        Animate(_toast, OpacityProperty, 0, duration, 0, .3, () => { if (_showingDetail) _toast.Visibility = Visibility.Hidden; });
        Animate(_detail, OpacityProperty, 1, duration, .35, 1);
        _escape?.SetEnabled(!_keyboard);
    }

    // Back from the details to the arriving notice, which then runs out its remaining time.
    private void LeaveDetail()
    {
        if (!_showingDetail || _item is null) return;
        _showingDetail = false;
        _toast.Visibility = Visibility.Visible;
        _toastFrame = Frame(NoticeForm.Toast, ToastSize());
        BuildToast();
        double duration = Duration(DetailMs) * .8;
        SetFrame(_toastFrame, duration);
        Animate(_detail, OpacityProperty, 0, duration, 0, .3, () => { if (!_showingDetail) _detail.Visibility = Visibility.Collapsed; });
        Animate(_toast, OpacityProperty, 1, duration, .4, 1);
        _escape?.SetEnabled(false);
        if (!_surface.IsMouseOver) Center.Hold(_deck, false);
    }

    private async Task OpenAsync(Notice notice)
    {
        if (await Center.OpenAsync(notice)) { Dismiss(false); return; }
        _openFailed = true;
        if (_showingDetail && ReferenceEquals(Notice, notice)) ShowDetailFrame(Duration(DetailMs) * .5);
    }

    private void ViewAll()
    {
        var deck = _deck;
        Dismiss(false);
        deck.OpenNoticesPage();
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
        if (!_showingDetail && Settings.NoticeHoverDetails) _hoverOpen.Start();
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
        if (!_showingDetail) OpenDetail(byHover: false);
    }

    // The notices hotkey: details open and the keyboard moves through the buttons; Escape closes.
    public void EnterKeyboard()
    {
        if (_item is null) return;
        _keyboard = true;
        if (!_showingDetail) OpenDetail(byHover: false);
        Center.Hold(_deck, true);
        _escape?.SetEnabled(false);
        var handle = new WindowInteropHelper(this).Handle;
        NoticeNative.SetActivatable(handle, true);
        Activate();
        Dispatcher.BeginInvoke(() => FirstButton(_detail)?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EndKeyboard()
    {
        _keyboard = false;
        NoticeNative.SetActivatable(new WindowInteropHelper(this).Handle, false);
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
            if (child is Button button && button.IsVisible) return button;
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
    private void Announce()
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        (UIElementAutomationPeer.FromElement(_toast) ?? UIElementAutomationPeer.CreatePeerForElement(_toast))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // The theme or the island changed while a notice shows: it is laid out and drawn again in place.
    public void Refresh()
    {
        if (_item is null || _leaving) return;
        _look = NoticeLook.For(Settings.NoticeTheme, NoticeLook.IsDark(Settings.NoticeAppearance, Settings.Theme));
        Layout();
        BuildToast();
        if (_showingDetail) ShowDetailFrame(Duration(DetailMs) * .5);
        else SetFrame(_toastFrame, Duration(EnterMs) * .5);
    }
}

// Quantizes another easing into whole steps, for the pixel theme's stepwise motion.
internal sealed class StepEase(IEasingFunction inner, int steps) : EasingFunctionBase
{
    protected override Freezable CreateInstanceCore() => new StepEase(inner, steps);
    protected override double EaseInCore(double normalizedTime)
    {
        double value = inner.Ease(normalizedTime);
        return normalizedTime >= 1 ? 1 : Math.Floor(value * steps) / steps;
    }
}
