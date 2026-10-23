using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The theme's answers to the pointer on BrimDeck's own windows (ThemeMotion): a light under the pointer on the control it hovers,
// which glides to the next control as the monitor's sort pill glides; a pulse, ripple or ring where it presses; a faint light that
// trails it over the open panel; and a bloom where keyboard focus lands. They are drawn on a layer above the content that never
// takes input, so no control is restyled and the system cursor is never touched. Durations come from the animation length in
// settings and the theme's pace; with animations off (BrimDeck's or Windows'), or in high contrast, nothing is drawn.
internal static class InteractionFx
{
    private static readonly List<FxSurface> Surfaces = [];
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached("Attached", typeof(bool), typeof(InteractionFx));

    // Once at startup: notices get their layer when they first load; panels attach their own (Island).
    public static void Install(App app)
    {
        MotionEnvironment.Install(app.Dispatcher);
        MotionEnvironment.Changed += ResetAll;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));
    }

    public static void Uninstall()
    {
        MotionEnvironment.Changed -= ResetAll;
        MotionEnvironment.Uninstall();
        foreach (var surface in Surfaces.ToList()) surface.Detach();
    }

    // Everything shown ends at once: a theme, setting or system change.
    public static void ResetAll() { foreach (var surface in Surfaces) surface.Reset(); }

    // The panel's layer lies inside the island, so the island's outline clips it like the content.
    public static FxSurface Island(MainWindow window, IslandBorder island, Panel content, Func<bool> interactive)
    {
        var surface = new FxSurface(island, content, interactive, trail: true, FxScope.Panel);
        content.Children.Add(surface);
        Track(window, surface);
        return surface;
    }

    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(e.OriginalSource, window) || (bool)window.GetValue(AttachedProperty)) return;
        // The settings window keeps its own hover and focus styles: a second layer over them reads as a stray outline.
        var scope = window switch { NoticeWindow => FxScope.Panel, _ => (FxScope?)null };
        if (scope is null) return;
        // Above the content in the window's own adorner layer, over the content presenter, which stays when the content is replaced.
        var decorator = FindDecorator(window, 4);
        if (decorator?.Child is not UIElement adorned || decorator.AdornerLayer is not { } layer) return;
        window.SetValue(AttachedProperty, true);
        var surface = new FxSurface(window, adorned, () => true, trail: false, scope.Value);
        layer.Add(new FxAdorner(adorned, surface));
        Track(window, surface);
    }

    private static void Track(Window window, FxSurface surface)
    {
        Surfaces.Add(surface);
        window.Closed += (_, _) => surface.Detach();
        surface.Detached += () => Surfaces.Remove(surface);
    }

    private static AdornerDecorator? FindDecorator(DependencyObject element, int depth)
    {
        if (element is AdornerDecorator decorator) return decorator;
        if (depth == 0) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindDecorator(VisualTreeHelper.GetChild(element, i), depth - 1) is { } found) return found;
        return null;
    }
}

// Panel: the island, its pop-ups and notices, in the panel theme's colors. Settings: the settings window, in its own accent with the
// panel theme's styles.
internal enum FxScope { Panel, Settings }

internal sealed class FxAdorner : Adorner
{
    private readonly FxSurface _surface;
    public FxAdorner(UIElement adorned, FxSurface surface) : base(adorned)
    {
        _surface = surface; IsHitTestVisible = false;
        AddVisualChild(surface);
    }
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _surface;
    protected override Size MeasureOverride(Size constraint) { _surface.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize; }
    protected override Size ArrangeOverride(Size finalSize) { _surface.Arrange(new Rect(finalSize)); return finalSize; }
}

internal sealed class FxSurface : Canvas
{
    private const int RippleLimit = 4;
    private const double TrailRadius = 84;
    private readonly UIElement _input;
    private readonly UIElement _scope;
    private readonly Func<bool> _interactive;
    private readonly bool _trailAllowed;
    private readonly FxScope _kind;

    // Hover: a group that fades, with the lift's shadow ring outside the control and, clipped to the control, the light and edge.
    private readonly Canvas _hover = new() { Opacity = 0 }, _hoverInside = new();
    private readonly RectangleGeometry _hoverRect = new();
    private readonly Path _liftOuter = new() { StrokeThickness = 7 }, _liftInner = new() { StrokeThickness = 3 }, _edge = new() { StrokeThickness = 2 };
    private readonly Ellipse _spot = new();
    private readonly TranslateTransform _spotShift = new();
    private readonly Canvas _ripples = new();
    private readonly Ellipse _trail = new() { Width = TrailRadius * 2, Height = TrailRadius * 2, Opacity = 0 };
    private readonly TranslateTransform _trailShift = new();
    private readonly Canvas _focus = new() { Opacity = 0 };
    private readonly RectangleGeometry _focusRect = new();
    private readonly Path _focusOuter = new() { StrokeThickness = 6 }, _focusInner = new() { StrokeThickness = 1.5 };

    private ThemeMotion _motion = ThemeMotion.Default;
    private int _generation = -1;
    private Color _dressedAccent;
    private bool _dressedSystem;
    private Brush? _pulseFill, _rippleFill, _ringFill, _ringStroke;
    private FrameworkElement? _target;
    private DependencyObject? _lastSource;
    private FrameworkElement? _lastTarget;
    private bool _hasRect, _hoverShown, _trailShown, _trailHooked, _detached;
    private Spring _trailX, _trailY;
    private double _lastFrame;

    public event Action? Detached;

    public FxSurface(UIElement input, UIElement scope, Func<bool> interactive, bool trail, FxScope kind)
    {
        _input = input; _scope = scope; _interactive = interactive; _trailAllowed = trail; _kind = kind;
        IsHitTestVisible = false; Focusable = false; ClipToBounds = false;
        _liftOuter.Data = _liftInner.Data = _edge.Data = _hoverRect;
        _hoverInside.Clip = _hoverRect;
        _spot.RenderTransform = _spotShift;
        _hoverInside.Children.Add(_spot); _hoverInside.Children.Add(_edge);
        _hover.Children.Add(_liftOuter); _hover.Children.Add(_liftInner); _hover.Children.Add(_hoverInside);
        _trail.RenderTransform = _trailShift;
        _focusOuter.Data = _focusInner.Data = _focusRect;
        _focus.Children.Add(_focusOuter); _focus.Children.Add(_focusInner);
        Children.Add(_trail); Children.Add(_hover); Children.Add(_ripples); Children.Add(_focus);
        input.PreviewMouseMove += Moved;
        input.PreviewMouseLeftButtonDown += Pressed;
        input.MouseLeave += Left;
        input.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Focused), true);
    }

    // The window closed: no handler, timer or frame callback stays behind.
    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        _input.PreviewMouseMove -= Moved;
        _input.PreviewMouseLeftButtonDown -= Pressed;
        _input.MouseLeave -= Left;
        _input.RemoveHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Focused));
        Reset();
        Detached?.Invoke();
    }

    private static DeckSettings? Settings => (Application.Current as App)?.Settings;
    private MotionTiming Timing => MotionTiming.From(Settings?.AnimationDuration ?? 360, _motion.Pace);
    private bool Active => !_detached && MotionGate.Interaction(MotionEnvironment.Conditions(), _motion) && _interactive();

    // ----- Theme -----

    private void EnsureDressed()
    {
        var accent = _kind == FxScope.Settings && _scope is FrameworkElement scope && scope.TryFindResource("SettingsAccent") is SolidColorBrush settingsAccent ? settingsAccent.Color : default;
        // "System" and the theme it stands for share a palette, so the choice itself is part of what the layer was dressed for.
        bool system = PanelThemes.NormalizeId(Settings?.PanelTheme) == PanelThemes.SystemId;
        if (_generation == PanelTheme.Generation && accent == _dressedAccent && system == _dressedSystem) return;
        bool restyled = _generation >= 0;
        _generation = PanelTheme.Generation; _dressedAccent = accent; _dressedSystem = system;
        if (restyled) Reset();
        Dress(accent);
    }

    private void Dress(Color settingsAccent)
    {
        var palette = PanelTheme.Current;
        var motion = palette.Motion;
        if (PanelThemes.NormalizeId(Settings?.PanelTheme) == PanelThemes.SystemId) motion = motion.ForSystem();
        _motion = motion;
        Color color, secondary, shade;
        if (_kind == FxScope.Settings && settingsAccent.A > 0)
        {
            // The settings window has its own light or dark look; the panel theme lends it only the styles.
            color = settingsAccent; secondary = motion.Secondary == motion.Color ? settingsAccent : Blend(settingsAccent, PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, motion.Secondary)));
            shade = Colors.Black;
        }
        else
        {
            var system = motion.Color == ThemeMotion.SystemAccent || motion.Secondary == ThemeMotion.SystemAccent ? WindowsAccent() : null;
            color = PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, motion.Color, system));
            secondary = PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, motion.Secondary, system));
            shade = palette.IsDark ? Colors.Black : PanelTheme.ColorOf("shadow");
        }
        double hover = motion.HoverStrength, press = motion.PressStrength;
        bool spot = motion.Hover is HoverFx.Spot or HoverFx.Neon or HoverFx.Lift, edge = motion.Hover is HoverFx.Edge or HoverFx.Neon, lift = motion.Hover == HoverFx.Lift;
        // A lift's sheen is half as strong as a light; the outline of a neon hover runs from the first color into the second.
        _spot.Fill = spot ? Radial(color, motion.Hover == HoverFx.Lift ? hover * .5 : hover) : null;
        _spot.Visibility = spot ? Visibility.Visible : Visibility.Collapsed;
        _edge.Stroke = !edge ? null : motion.Hover == HoverFx.Neon ? Linear(Alpha(color, Math.Min(.7, hover * 3)), Alpha(secondary, Math.Min(.7, hover * 3))) : Solid(Alpha(color, Math.Min(.6, hover * 2.2)));
        _edge.Visibility = edge ? Visibility.Visible : Visibility.Collapsed;
        _liftOuter.Stroke = lift ? Solid(Alpha(shade, hover * .45)) : null;
        _liftInner.Stroke = lift ? Solid(Alpha(shade, hover * .8)) : null;
        _liftOuter.Visibility = _liftInner.Visibility = lift ? Visibility.Visible : Visibility.Collapsed;
        _pulseFill = Solid(Alpha(color, press));
        _rippleFill = Radial(color, press, edge: .55);
        _ringStroke = Linear(Alpha(color, Math.Min(.8, press * 2.6)), Alpha(secondary, Math.Min(.8, press * 2.6)));
        _ringFill = Radial(secondary, press * .35);
        _trail.Fill = Radial(secondary, motion.Trail);
        (_focusOuter.Stroke, _focusInner.Stroke) = motion.Focus switch
        {
            FocusFx.Ring => (null, Solid(Alpha(color, .55))),
            FocusFx.Glow => (Solid(Alpha(color, .18)), Solid(Alpha(color, .45))),
            FocusFx.Neon => (Solid(Alpha(secondary, .22)), Solid(Alpha(color, .6))),
            _ => ((Brush?)null, (Brush?)null)
        };
    }

    // The Windows accent color, for the "system" theme; null where Windows does not say.
    private static ThemeColor? WindowsAccent()
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            return new ThemeColor(c.A, c.R, c.G, c.B);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static Color Alpha(Color color, double alpha) => Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), color.R, color.G, color.B);
    private static Color Blend(Color a, Color b) => Color.FromRgb((byte)((a.R + b.R) / 2), (byte)((a.G + b.G) / 2), (byte)((a.B + b.B) / 2));
    private static Brush Solid(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Brush Linear(Color from, Color to) { var brush = new LinearGradientBrush(from, to, 0); brush.Freeze(); return brush; }
    // A soft light: full strength in the middle, half by the given point, nothing at the edge.
    private static Brush Radial(Color color, double strength, double edge = .45)
    {
        var brush = new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(Alpha(color, strength), 0), new GradientStop(Alpha(color, strength * .5), edge), new GradientStop(Alpha(color, 0), 1)
        });
        brush.Freeze();
        return brush;
    }

    // ----- Pointer -----

    private void Moved(object sender, MouseEventArgs e)
    {
        if (!_detached) EnsureDressed();
        if (!Active) { Release(); return; }
        var p = e.GetPosition(this);
        Hover(TargetOf(e.OriginalSource as DependencyObject), p);
        if (_trailAllowed && _motion.Trail > 0) Trail(p);
    }

    private void Left(object sender, MouseEventArgs e) => Release();

    // What is shown fades out: the pointer left, or the panel closed.
    public void Release()
    {
        var timing = Timing;
        HideHover(timing);
        if (_trailShown) { _trailShown = false; Fade(_trail, 0, timing.HoverOut, Soft.InOut); }
    }

    private void Hover(FrameworkElement? target, Point p)
    {
        if (_motion.Hover == HoverFx.None || _motion.HoverStrength <= 0) target = null;
        var timing = Timing;
        if (target is null)
        {
            HideHover(timing);
            return;
        }
        var bounds = BoundsOf(target);
        if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4) { Hover(null, p); return; }
        double radius = Math.Min(CornerOf(target), Math.Min(bounds.Width, bounds.Height) / 2);
        if (!ReferenceEquals(target, _target))
        {
            // From one control to the next while the light still shows, it glides there; otherwise it appears in place.
            bool glide = _hasRect && _hover.Opacity > .05;
            _target = target;
            Place(_hoverRect, bounds, radius, glide ? timing.Glide : 0);
            double reach = Math.Clamp(Math.Max(bounds.Width, bounds.Height) * .75, 26, 150);
            if (Math.Abs(_spot.Width - reach * 2) > .5) _spot.Width = _spot.Height = reach * 2;
            _hasRect = true;
            if (!_hoverShown) { _hoverShown = true; Fade(_hover, 1, timing.HoverIn, Soft.Follow); }
        }
        // The control moved under a still light (the panel opening, a page or list moving): the light goes with it. A glide in
        // progress already ends on the control's box, which is the base value.
        else if (!Near(_hoverRect.Rect, bounds)) Place(_hoverRect, bounds, radius, 0);
        _spotShift.X = p.X - _spot.Width / 2; _spotShift.Y = p.Y - _spot.Height / 2;
        Settle();
    }

    // For a moment after the pointer moves, the light keeps checking where its control is, frame by frame: content that is still
    // moving (the panel opening, a page switching, a scroll gliding) gives no pointer events, and the light would stay behind.
    private const double SettleSeconds = .8;
    private double _settleUntil;
    private bool _settleHooked;

    private void Settle()
    {
        _settleUntil = Eased.Now + SettleSeconds;
        if (_settleHooked) return;
        _settleHooked = true;
        CompositionTarget.Rendering += SettleFrame;
    }

    private void SettleFrame(object? sender, EventArgs e)
    {
        if (_target is { } target && _hoverShown && !_detached)
        {
            var bounds = BoundsOf(target);
            if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4) HideHover(Timing);
            else if (!Near(_hoverRect.Rect, bounds)) Place(_hoverRect, bounds, Math.Min(CornerOf(target), Math.Min(bounds.Width, bounds.Height) / 2), 0);
        }
        if (_target is null || !_hoverShown || _detached || Eased.Now > _settleUntil) Unsettle();
    }

    private void Unsettle()
    {
        if (!_settleHooked) return;
        _settleHooked = false;
        CompositionTarget.Rendering -= SettleFrame;
    }

    private static bool Near(Rect a, Rect b) => !a.IsEmpty && !b.IsEmpty && Math.Abs(a.X - b.X) < .5 && Math.Abs(a.Y - b.Y) < .5 && Math.Abs(a.Width - b.Width) < .5 && Math.Abs(a.Height - b.Height) < .5;

    // Once per leave: a fade restarted on every move would never finish.
    private void HideHover(MotionTiming timing)
    {
        _target = null;
        if (!_hoverShown) return;
        _hoverShown = false;
        Fade(_hover, 0, timing.HoverOut, Soft.InOut);
    }

    private void Pressed(object sender, MouseButtonEventArgs e)
    {
        if (_detached) return;
        EnsureDressed();
        if (!Active || _motion.Press == PressFx.None || _motion.PressStrength <= 0) return;
        if (TargetOf(e.OriginalSource as DependencyObject) is not { } target) return;
        var bounds = BoundsOf(target);
        if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4) return;
        var p = e.GetPosition(this);
        double radius = Math.Min(CornerOf(target), Math.Min(bounds.Width, bounds.Height) / 2);
        int length = Timing.Press;
        var host = new Canvas { Clip = new RectangleGeometry(bounds, radius, radius), IsHitTestVisible = false, Opacity = 0 };
        if (_motion.Press == PressFx.Pulse)
        {
            var fill = new Rectangle { Width = bounds.Width, Height = bounds.Height, RadiusX = radius, RadiusY = radius, Fill = _pulseFill };
            SetLeft(fill, bounds.X); SetTop(fill, bounds.Y);
            host.Children.Add(fill);
        }
        else
        {
            // A disc or ring from the press to the farthest corner.
            double reach = Math.Max(Math.Max(Distance(p, bounds.TopLeft), Distance(p, bounds.TopRight)), Math.Max(Distance(p, bounds.BottomLeft), Distance(p, bounds.BottomRight)));
            var scale = new ScaleTransform(.15, .15);
            var wave = new Ellipse
            {
                Width = reach * 2, Height = reach * 2, RenderTransformOrigin = new Point(.5, .5), RenderTransform = scale,
                Fill = _motion.Press == PressFx.Ring ? _ringFill : _rippleFill,
                Stroke = _motion.Press == PressFx.Ring ? _ringStroke : null, StrokeThickness = 1.5
            };
            SetLeft(wave, p.X - reach); SetTop(wave, p.Y - reach);
            host.Children.Add(wave);
            var grow = new DoubleAnimation(.15, 1, TimeSpan.FromMilliseconds(length)) { EasingFunction = Soft.Out };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        if (_ripples.Children.Count >= RippleLimit) _ripples.Children.RemoveAt(0);
        _ripples.Children.Add(host);
        // Rises quickly, then fades over the rest: one soft change, never a blink.
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(length) };
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(_motion.Press == PressFx.Pulse ? .2 : .12), Soft.Out));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), Soft.InOut));
        fade.Completed += (_, _) => _ripples.Children.Remove(host);
        host.BeginAnimation(OpacityProperty, fade);
    }
    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ----- Trail -----

    private void Trail(Point p)
    {
        if (!_trailShown)
        {
            // It appears where the pointer is rather than flying in from the last place it was seen.
            _trailShown = true;
            _trailX = new Spring(p.X); _trailY = new Spring(p.Y);
            _trailShift.X = p.X - TrailRadius; _trailShift.Y = p.Y - TrailRadius;
            Fade(_trail, 1, (int)(Timing.HoverIn * 1.5), Soft.Follow);
        }
        _trailX.Target = p.X; _trailY.Target = p.Y;
        if (!_trailHooked) { _trailHooked = true; _lastFrame = Eased.Now; CompositionTarget.Rendering += Frame; }
    }

    // Only while the trailing light still moves toward the pointer.
    private void Frame(object? sender, EventArgs e)
    {
        double now = Eased.Now, dt = now - _lastFrame;
        _lastFrame = now;
        bool moving = _trailX.Step(dt) | _trailY.Step(dt);
        _trailShift.X = _trailX.Value - TrailRadius; _trailShift.Y = _trailY.Value - TrailRadius;
        if (!moving || !_trailShown) Unhook();
    }

    private void Unhook()
    {
        if (!_trailHooked) return;
        _trailHooked = false;
        CompositionTarget.Rendering -= Frame;
    }

    // ----- Focus -----

    private void Focused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_detached || InputManager.Current.MostRecentInputDevice is not KeyboardDevice) return;
        EnsureDressed();
        if (!Active || _motion.Focus == FocusFx.None) return;
        if (e.NewFocus is not FrameworkElement element || !element.IsVisible || !IsWithin(element)) return;
        var bounds = BoundsOf(element);
        if (bounds.IsEmpty || TooLarge(bounds)) return;
        double radius = Math.Min(CornerOf(element), Math.Min(bounds.Width, bounds.Height) / 2) + 3;
        bounds.Inflate(3, 3);
        Place(_focusRect, bounds, radius, 0);
        var bloom = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(Timing.Focus) };
        bloom.KeyFrames.Add(new EasingDoubleKeyFrame(_focus.Opacity, KeyTime.FromPercent(0)));
        bloom.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(.28), Soft.Out));
        bloom.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), Soft.InOut));
        _focus.BeginAnimation(OpacityProperty, bloom);
    }

    // ----- Shared -----

    // Ends everything at once, as if nothing had been shown.
    public void Reset()
    {
        Unhook(); Unsettle();
        _target = null; _hasRect = false; _hoverShown = false; _trailShown = false; _lastSource = null; _lastTarget = null;
        foreach (var element in new UIElement[] { _hover, _trail, _focus }) { element.BeginAnimation(OpacityProperty, null); element.Opacity = 0; }
        _hoverRect.BeginAnimation(RectangleGeometry.RectProperty, null);
        _ripples.Children.Clear();
    }

    private static void Fade(UIElement element, double to, int milliseconds, IEasingFunction easing)
    {
        if (milliseconds <= 0) { element.BeginAnimation(OpacityProperty, null); element.Opacity = to; return; }
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Place(RectangleGeometry geometry, Rect bounds, double radius, int milliseconds)
    {
        if (milliseconds > 0)
        {
            var from = geometry.Rect;
            geometry.Rect = bounds;
            // Stops at its end, where the base value already is, so later moves of the control (a scroll) can place it again.
            geometry.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(from, bounds, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
        }
        else { geometry.BeginAnimation(RectangleGeometry.RectProperty, null); geometry.Rect = bounds; }
        geometry.RadiusX = geometry.RadiusY = radius;
    }

    // The control a pointer event belongs to: a button of any kind, a list or combo box item, a tab or a slider, and only one that
    // draws a box of its own (a fill or a border) where the light and edge would go. Larger clickable areas (a usage card, a
    // monitor row) and buttons drawn as bare text or icons on a wider transparent box are left alone: an outline around their
    // layout box would not match anything on screen. So are scroll bars, text fields and whole surfaces (a notice, a page).
    private FrameworkElement? TargetOf(DependencyObject? source)
    {
        if (source is null) return null;
        if (ReferenceEquals(source, _lastSource)) return _lastTarget is { IsEnabled: true } && HasChrome(_lastTarget) ? _lastTarget : null;
        FrameworkElement? found = null;
        for (var node = source; node is not null && !ReferenceEquals(node, _scope); node = Up(node))
        {
            if (node is ScrollBar or TextBoxBase or PasswordBox) { found = null; break; }
            if (node is NoticeSurface or IslandBorder) break;
            if (found is null && node is ButtonBase or ListBoxItem or ComboBox or TabItem or Slider) found = (FrameworkElement)node;
        }
        if (found is not null && (!found.IsEnabled || !found.IsVisible || TooLarge(BoundsOf(found)))) found = null;
        _lastSource = source; _lastTarget = found;
        return found is not null && HasChrome(found) ? found : null;
    }

    // Whether the control shows a box now (its hover style included): a visible fill or border on itself or on the border its
    // template starts with.
    private static bool HasChrome(FrameworkElement element)
    {
        var border = element as Border ?? (VisualTreeHelper.GetChildrenCount(element) > 0 ? VisualTreeHelper.GetChild(element, 0) as Border : null);
        if (border is null) return element is Control control && Visible(control.Background);
        return Visible(border.Background) || border.BorderThickness.Left > 0 && Visible(border.BorderBrush);
    }
    private static bool Visible(Brush? brush) => brush is not null && brush.Opacity > 0 && (brush is not SolidColorBrush solid || solid.Color.A > 8);
    private static DependencyObject? Up(DependencyObject node) => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private bool IsWithin(DependencyObject element)
    {
        for (var node = element; node is not null; node = Up(node)) if (ReferenceEquals(node, _scope)) return true;
        return false;
    }

    // A control the size of a card or larger is a surface, not a control to light.
    private bool TooLarge(Rect bounds) => bounds.IsEmpty || bounds.Height > 64 || ActualWidth > 0 && ActualHeight > 0 && bounds.Width * bounds.Height > ActualWidth * ActualHeight * .12;

    // The element's box on this layer, cut to the scroll viewers it sits in, so a control scrolled half out of sight lights
    // only its visible half.
    private Rect BoundsOf(FrameworkElement element)
    {
        try
        {
            if (PresentationSource.FromVisual(element) is null || PresentationSource.FromVisual(this) is null) return Rect.Empty;
            var bounds = element.TransformToVisual(this).TransformBounds(new Rect(element.RenderSize));
            for (var node = VisualTreeHelper.GetParent(element); node is not null && !ReferenceEquals(node, _scope); node = VisualTreeHelper.GetParent(node))
                if (node is ScrollContentPresenter viewport) bounds.Intersect(viewport.TransformToVisual(this).TransformBounds(new Rect(viewport.RenderSize)));
            return bounds;
        }
        catch (InvalidOperationException) { return Rect.Empty; }
    }

    // The corner the control draws: its own, or that of the border its template starts with.
    private static double CornerOf(FrameworkElement element)
    {
        if (element is Border border) return border.CornerRadius.TopLeft;
        if (VisualTreeHelper.GetChildrenCount(element) > 0 && VisualTreeHelper.GetChild(element, 0) is Border inner) return inner.CornerRadius.TopLeft;
        return 6;
    }
}
