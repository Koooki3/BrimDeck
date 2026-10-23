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

    // The panel's layers lie inside the island, so the island's outline clips them like the content. The particles go under the
    // content, on the surface's own material (after the ambient scan line, which is first), so text and controls stay above
    // them as above the theme's gradient and scan lines; the hover, press and focus answers go above, on what they answer.
    public static FxSurface Island(MainWindow window, IslandBorder island, Panel content, Func<bool> interactive)
    {
        var surface = new FxSurface(island, content, interactive, particles: true);
        content.Children.Insert(Math.Min(1, content.Children.Count), surface.Backdrop);
        content.Children.Add(surface);
        Track(window, surface);
        return surface;
    }

    // Notices get the answers too; the settings window keeps its own hover and focus styles, and a second layer over them
    // reads as a stray outline.
    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not NoticeWindow window || !ReferenceEquals(e.OriginalSource, window) || (bool)window.GetValue(AttachedProperty)) return;
        // Above the content in the window's own adorner layer, over the content presenter, which stays when the content is replaced.
        var decorator = FindDecorator(window, 4);
        if (decorator?.Child is not UIElement adorned || decorator.AdornerLayer is not { } layer) return;
        window.SetValue(AttachedProperty, true);
        var surface = new FxSurface(window, adorned, () => true, particles: false);
        layer.Add(new FxAdorner(adorned, surface));
        window.SetValue(SurfaceProperty, surface);
        // The layer is not clipped by the notice's outline: whatever it shows ends with the notice, never lingers into the next.
        window.IsVisibleChanged += (_, _) => surface.Reset();
        Track(window, surface);
    }

    private static readonly DependencyProperty SurfaceProperty = DependencyProperty.RegisterAttached("Surface", typeof(FxSurface), typeof(InteractionFx));

    // A notice leaves or hands over to the panel: its light, ripples and focus bloom end at once rather than playing on where
    // its buttons were.
    public static void Clear(Window window) => (window.GetValue(SurfaceProperty) as FxSurface)?.Reset();

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

// The answers to the pointer. Nothing follows the pointer and nothing is drawn around a control's box (a control's layout box
// often differs from what it draws, and an outline around it reads as a stray frame). Hovering something that can be clicked
// gives the theme's mark at that control, centred on it (ThemeMotion): a soft light, a shadow pool under a light theme, a small
// crisp dot under the control, or a neon light with a two-color ring around a small button; it fades when the pointer leaves the
// control. A press spreads from where it was pressed (a pulse, a ripple or a ring) and throws a few of the theme's particles. In
// the panel all of it lies under the content (Backdrop), on the surface's own material; on a notice, above it.
internal sealed partial class FxSurface : Canvas
{
    private const int RippleLimit = 4;
    private readonly UIElement _input;
    private readonly UIElement _scope;
    private readonly Func<bool> _interactive;
    private readonly bool _particlesAllowed;

    // The hovered control's light and ring (or dot), scaled about their centres and placed on the control.
    private readonly Ellipse _light = new() { IsHitTestVisible = false, Opacity = 0, RenderTransformOrigin = new Point(.5, .5) };
    private readonly Ellipse _ring = new() { IsHitTestVisible = false, Opacity = 0, RenderTransformOrigin = new Point(.5, .5) };
    private readonly ScaleTransform _lightScale = new(), _ringScale = new();
    private readonly TranslateTransform _lightShift = new(), _ringShift = new();
    private readonly Canvas _ripples = new() { IsHitTestVisible = false };
    private readonly Canvas _focus = new() { Opacity = 0 };
    private readonly RectangleGeometry _focusRect = new();
    private readonly Path _focusOuter = new() { StrokeThickness = 6 }, _focusInner = new() { StrokeThickness = 1.5 };

    private ThemeMotion _motion = ThemeMotion.Default;
    private int _generation = -1;
    private bool _dressedSystem;
    private Brush? _pulseFill, _rippleFill, _ringFill, _ringStroke;
    private double _lightRadius, _ringRadius;
    private DependencyObject? _lastSource;
    private FrameworkElement? _lastTarget, _lastShown;
    private bool _shown, _detached;

    public event Action? Detached;

    public FxSurface(UIElement input, UIElement scope, Func<bool> interactive, bool particles)
    {
        _input = input; _scope = scope; _interactive = interactive; _particlesAllowed = particles;
        IsHitTestVisible = false; Focusable = false; ClipToBounds = false;
        _light.RenderTransform = new TransformGroup { Children = { _lightScale, _lightShift } };
        _ring.RenderTransform = new TransformGroup { Children = { _ringScale, _ringShift } };
        // In the panel, under the content; on a notice, on this layer.
        var host = particles ? _particleLayer : (Panel)this;
        host.Children.Add(_light); host.Children.Add(_ring); host.Children.Add(_ripples);
        _focusOuter.Data = _focusInner.Data = _focusRect;
        _focus.Children.Add(_focusOuter); _focus.Children.Add(_focusInner);
        Children.Add(_focus);
        input.PreviewMouseMove += Moved;
        input.PreviewMouseLeftButtonDown += Pressed;
        input.MouseLeave += Left;
        input.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Focused), true);
    }

    private Panel PointerHost => _particlesAllowed ? _particleLayer : this;

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
        // "System" and the theme it stands for share a palette, so the choice itself is part of what the layer was dressed for.
        bool system = PanelThemes.NormalizeId(Settings?.PanelTheme) == PanelThemes.SystemId;
        if (_generation == PanelTheme.Generation && system == _dressedSystem) return;
        bool restyled = _generation >= 0;
        _generation = PanelTheme.Generation; _dressedSystem = system;
        if (restyled) Reset();
        Dress();
    }

    private void Dress()
    {
        var palette = PanelTheme.Current;
        var motion = palette.Motion;
        if (PanelThemes.NormalizeId(Settings?.PanelTheme) == PanelThemes.SystemId) motion = motion.ForSystem();
        _motion = motion;
        var system = motion.Color == ThemeMotion.SystemAccent || motion.Secondary == ThemeMotion.SystemAccent ? WindowsAccent() : null;
        Color color = PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, motion.Color, system));
        Color secondary = PanelTheme.ToColor(ThemeMotion.ResolveColor(palette, motion.Secondary, system));
        Color shade = palette.IsDark ? Colors.Black : PanelTheme.ColorOf("shadow");
        double hover = motion.HoverStrength, press = motion.PressStrength;
        // Each style's light and ring (radii in DIP); a stronger light where the hover is the light itself.
        (Brush? light, double lightRadius, Brush? ring, double ringRadius, double ringWidth) = motion.Hover switch
        {
            HoverFx.Spot => (Radial(color, Math.Min(.6, hover * 1.6)), 58d, (Brush?)null, 0d, 0d),
            // A light theme's pointer casts a soft pool of shade, with a faint glint of the accent at its middle.
            HoverFx.Lift => (Pool(shade, Math.Min(.4, hover * 2.2), color, Math.Min(.25, hover * 1.2)), 46d, null, 0d, 0d),
            // GitHub's: a crisp small dot under the control, as its selected tabs are marked, and a faint light behind it.
            HoverFx.Edge => (Radial(color, Math.Min(.3, hover * 1)), 22d, Solid(Alpha(color, Math.Min(.9, hover * 4))), 2.5d, 5d),
            HoverFx.Neon => (Radial(color, Math.Min(.55, hover * 2.2)), 50d, Linear(Alpha(color, Math.Min(.8, hover * 4.5)), Alpha(secondary, Math.Min(.8, hover * 4.5))), 15d, 1.6d),
            _ => ((Brush?)null, 0d, (Brush?)null, 0d, 0d)
        };
        _light.Fill = light; _lightRadius = lightRadius; _light.Width = _light.Height = lightRadius * 2;
        _ring.Stroke = ring; _ringRadius = ringRadius; _ring.StrokeThickness = ringWidth; _ring.Width = _ring.Height = Math.Max(0, ringRadius * 2);
        _light.Visibility = light is null ? Visibility.Collapsed : Visibility.Visible;
        _ring.Visibility = ring is null ? Visibility.Collapsed : Visibility.Visible;
        _pulseFill = Radial(color, Math.Min(.6, press * 2.4), edge: .7);
        _rippleFill = Radial(color, Math.Min(.55, press * 2), edge: .55);
        _ringStroke = Linear(Alpha(color, Math.Min(.85, press * 3)), Alpha(secondary, Math.Min(.85, press * 3)));
        _ringFill = Radial(secondary, press * .5);
        DressParticles(color, secondary);
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
    private static Brush Pool(Color shade, double strength, Color glint, double glintStrength)
    {
        var brush = new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(Alpha(glint, glintStrength), 0), new GradientStop(Alpha(shade, strength), .28), new GradientStop(Alpha(shade, strength * .45), .6), new GradientStop(Alpha(shade, 0), 1)
        });
        brush.Freeze();
        return brush;
    }

    // ----- Pointer -----

    private void Moved(object sender, MouseEventArgs e)
    {
        if (!_detached) EnsureDressed();
        if (!Active) { Release(); return; }
        HoverOn(TargetOf(e.OriginalSource as DependencyObject));
    }

    private void Left(object sender, MouseEventArgs e) => Release();

    // What is shown fades out: the pointer left, or the panel closed.
    public void Release() => HoverOn(null);

    // The theme's mark on the hovered control, placed from the control's box each time the pointer moves over it (so it stays on the
    // control while the panel or a list moves); it comes and goes with the control, never trailing the pointer.
    private void HoverOn(FrameworkElement? target)
    {
        if (_motion.Hover == HoverFx.None || _motion.HoverStrength <= 0) target = null;
        var bounds = target is null ? Rect.Empty : BoundsOf(target, PointerHost);
        if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4) target = null;
        var timing = Timing;
        if (target is null)
        {
            if (!_shown) return;
            _shown = false; _lastShown = null;
            Fade(_light, 0, timing.HoverOut, Soft.InOut); Fade(_ring, 0, timing.HoverOut, Soft.InOut);
            Scale(_lightScale, .8, timing.HoverOut); Scale(_ringScale, .8, timing.HoverOut);
            return;
        }
        double cx = bounds.X + bounds.Width / 2, cy = bounds.Y + bounds.Height / 2;
        // The light reaches a little past the control; a small control gets a small light.
        double lightScale = Math.Clamp(Math.Max(bounds.Width, bounds.Height) * .7 / Math.Max(1, _lightRadius), .45, 1.4);
        _lightShift.X = cx - _lightRadius; _lightShift.Y = cy - _lightRadius;
        bool dot = _motion.Hover == HoverFx.Edge;
        // The dot sits under the control; the neon ring goes only around a small, roughly square button, which it can follow.
        bool ringFits = dot || Math.Max(bounds.Width, bounds.Height) <= 40 && Math.Abs(bounds.Width - bounds.Height) <= 12;
        double ringScale = dot ? 1 : Math.Max(bounds.Width, bounds.Height) / 2 / Math.Max(1, _ringRadius) + .25;
        _ringShift.X = cx - _ringRadius; _ringShift.Y = (dot ? bounds.Bottom + 3 : cy) - _ringRadius;
        if (!ReferenceEquals(target, _lastShown))
        {
            bool first = !_shown;
            _shown = true; _lastShown = target;
            Fade(_light, 1, timing.HoverIn, Soft.Follow);
            Fade(_ring, ringFits ? 1 : 0, timing.HoverIn, Soft.Follow);
            // It blooms from a little smaller as it arrives; moving to the next control it simply takes the new size.
            if (first) { SetScale(_lightScale, lightScale * .8); SetScale(_ringScale, ringScale * .8); }
            Scale(_lightScale, lightScale, timing.HoverIn); Scale(_ringScale, ringScale, timing.HoverIn);
        }
    }

    private static void SetScale(ScaleTransform scale, double value)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = value;
    }

    private static void Scale(ScaleTransform scale, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(Math.Max(1, milliseconds))) { EasingFunction = Soft.Follow };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    // A press spreads from the pointer in the theme's way and throws a few of its particles; no outline of the control.
    private void Pressed(object sender, MouseButtonEventArgs e)
    {
        if (_detached) return;
        EnsureDressed();
        if (!Active || _motion.Press == PressFx.None || _motion.PressStrength <= 0) return;
        if (IsSliderPart(e.OriginalSource as DependencyObject)) return;
        var p = e.GetPosition(PointerHost);
        int length = Timing.Press;
        double reach = _motion.Press switch { PressFx.Pulse => 30, PressFx.Ring => 40, _ => 46 };
        var scale = new ScaleTransform(_motion.Press == PressFx.Pulse ? .35 : .15, _motion.Press == PressFx.Pulse ? .35 : .15);
        var wave = new Ellipse
        {
            Width = reach * 2, Height = reach * 2, RenderTransformOrigin = new Point(.5, .5), RenderTransform = scale, IsHitTestVisible = false,
            Fill = _motion.Press switch { PressFx.Ring => _ringFill, PressFx.Pulse => _pulseFill, _ => _rippleFill },
            Stroke = _motion.Press == PressFx.Ring ? _ringStroke : null, StrokeThickness = 1.6, Opacity = 0
        };
        SetLeft(wave, p.X - reach); SetTop(wave, p.Y - reach);
        var grow = new DoubleAnimation(scale.ScaleX, 1, TimeSpan.FromMilliseconds(length)) { EasingFunction = Soft.Out };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        if (_ripples.Children.Count >= RippleLimit) _ripples.Children.RemoveAt(0);
        _ripples.Children.Add(wave);
        // Rises quickly, then fades over the rest: one soft change, never a blink.
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(length) };
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(_motion.Press == PressFx.Pulse ? .2 : .12), Soft.Out));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), Soft.InOut));
        fade.Completed += (_, _) => _ripples.Children.Remove(wave);
        wave.BeginAnimation(OpacityProperty, fade);
        if (_particlesAllowed && _motion.Trail > 0 && _motion.Particles != ParticleFx.None) Burst(p);
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
        _focusRect.Rect = bounds; _focusRect.RadiusX = _focusRect.RadiusY = radius;
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
        ClearParticles();
        _shown = false; _lastShown = null; _lastSource = null; _lastTarget = null;
        foreach (var element in new UIElement[] { _light, _ring, _focus }) { element.BeginAnimation(OpacityProperty, null); element.Opacity = 0; }
        _ripples.Children.Clear();
    }

    private static void Fade(UIElement element, double to, int milliseconds, IEasingFunction easing)
    {
        if (milliseconds <= 0) { element.BeginAnimation(OpacityProperty, null); element.Opacity = to; return; }
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
    }

    // Whether the pointer is over something that can be clicked: a button of any kind, a list or combo box item or a tab. Scroll
    // bars, sliders, text fields and whole surfaces are not.
    private FrameworkElement? TargetOf(DependencyObject? source)
    {
        if (source is null) return null;
        if (ReferenceEquals(source, _lastSource)) return _lastTarget is { IsEnabled: true } ? _lastTarget : null;
        FrameworkElement? found = null;
        for (var node = source; node is not null && !ReferenceEquals(node, _scope); node = Up(node))
        {
            if (node is ScrollBar or TextBoxBase or PasswordBox or RepeatButton or Thumb or Slider) { found = null; break; }
            if (node is NoticeSurface or IslandBorder) break;
            if (found is null && node is ButtonBase or ListBoxItem or ComboBox or TabItem) found = (FrameworkElement)node;
        }
        if (found is not null && (!found.IsEnabled || !found.IsVisible)) found = null;
        _lastSource = source; _lastTarget = found;
        return found;
    }

    private bool IsSliderPart(DependencyObject? source)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, _scope); node = Up(node))
            if (node is ScrollBar or RepeatButton or Thumb or Slider) return true;
        return false;
    }

    private static DependencyObject? Up(DependencyObject node) => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private bool IsWithin(DependencyObject element)
    {
        for (var node = element; node is not null; node = Up(node)) if (ReferenceEquals(node, _scope)) return true;
        return false;
    }

    // A control the size of a card or larger is a surface: keyboard focus on it gets no bloom.
    private bool TooLarge(Rect bounds) => bounds.IsEmpty || bounds.Height > 64 || ActualWidth > 0 && ActualHeight > 0 && bounds.Width * bounds.Height > ActualWidth * ActualHeight * .12;

    // The element's box on this layer (or another one), cut to the scroll viewers it sits in.
    private Rect BoundsOf(FrameworkElement element, Visual? on = null)
    {
        on ??= this;
        try
        {
            if (PresentationSource.FromVisual(element) is null || PresentationSource.FromVisual(on) is null) return Rect.Empty;
            var bounds = element.TransformToVisual(on).TransformBounds(new Rect(element.RenderSize));
            for (var node = VisualTreeHelper.GetParent(element); node is not null && !ReferenceEquals(node, _scope); node = VisualTreeHelper.GetParent(node))
                if (node is ScrollContentPresenter viewport) bounds.Intersect(viewport.TransformToVisual(on).TransformBounds(new Rect(viewport.RenderSize)));
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
