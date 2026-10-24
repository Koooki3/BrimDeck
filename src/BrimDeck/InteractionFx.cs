using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// The theme's answers to the pointer on BrimDeck's own windows (ThemeMotion): a mark on the control the pointer hovers, a press
// that answers as the button goes down, holds while it is held and completes (or backs out) on release, and a short accent
// where keyboard focus lands. Each theme has its own language (InteractionFx.Draw.cs). Everything is drawn on the control's
// visible shape (FxMark), never around its layout box and never following the pointer, on one layer above the content that
// takes no input: thin lines and brackets above the content cover nothing that matters, and the tints are faint enough that
// text shows through. Durations come from
// the theme's pace alone, not the panel's animation length (MotionTiming.For); with animations off (BrimDeck's or Windows'), or
// in high contrast, nothing is drawn. The settings window has its own hover and focus styles and gets none of this.
internal static class InteractionFx
{
    private static readonly List<FxSurface> Surfaces = [];
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached("Attached", typeof(bool), typeof(InteractionFx));
    private static readonly DependencyProperty SurfaceProperty = DependencyProperty.RegisterAttached("Surface", typeof(FxSurface), typeof(InteractionFx));

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

    // The panel's layer lies inside the island, above the content, so the island's outline clips it like the content.
    public static FxSurface Island(MainWindow window, IslandBorder island, Panel content, Func<bool> interactive)
    {
        var surface = new FxSurface(island, content, interactive);
        content.Children.Add(surface);
        Track(window, surface);
        return surface;
    }

    // Notices and the date range picker's pop-up get the answers too, above their content in the window's own adorner layer,
    // over the content presenter, which stays when the content is replaced. The picker's layer is clipped to its rounded face:
    // its window keeps transparent room below it (ModelDateWindow), where nothing may show.
    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not (NoticeWindow or ModelDateWindow) || sender is not Window window || !ReferenceEquals(e.OriginalSource, window) || (bool)window.GetValue(AttachedProperty)) return;
        var decorator = FindDecorator(window, 4);
        if (decorator?.Child is not UIElement adorned || decorator.AdornerLayer is not { } layer) return;
        window.SetValue(AttachedProperty, true);
        // A pop-up that is leaving takes no input (ModelDetails.ShowModelPopup) and answers nothing more.
        var surface = new FxSurface(window, adorned, () => window.IsHitTestVisible);
        var adorner = new FxAdorner(adorned, surface);
        if (window is ModelDateWindow { Picker: var picker }) ClipTo(adorner, picker, adorned);
        layer.Add(adorner);
        window.SetValue(SurfaceProperty, surface);
        // Whatever it shows ends with the notice, never lingers into the next.
        window.IsVisibleChanged += (_, _) => surface.Reset();
        Track(window, surface);
    }

    // Keeps the layer inside a face that changes height (the picker's message opening). ui-8: the face slides as the pop-up
    // enters and leaves (Soft.Enter and Leave move it on its own TranslateTransform); the clip is laid out without that move
    // and takes the same transform, so it follows the slide on every frame instead of only when the face changes size.
    private static void ClipTo(UIElement layer, Border face, UIElement adorned)
    {
        // Soft.Enter and Leave reuse a TranslateTransform that is there; only an identity one is replaced.
        if (face.RenderTransform is not TranslateTransform { IsFrozen: false } shift) face.RenderTransform = shift = new TranslateTransform();
        var clip = new RectangleGeometry { RadiusX = face.CornerRadius.TopLeft, RadiusY = face.CornerRadius.TopLeft, Transform = shift };
        void Fit()
        {
            if (PresentationSource.FromVisual(face) is null || !adorned.IsAncestorOf(face)) return;
            var bounds = face.TransformToAncestor(adorned).TransformBounds(new Rect(face.RenderSize));
            bounds.Offset(-shift.X, -shift.Y);
            clip.Rect = bounds;
        }
        layer.Clip = clip;
        Fit();
        face.SizeChanged += (_, _) => Fit();
    }

    // A notice leaves or hands over to the panel: what it showed ends at once rather than playing on where its buttons were.
    public static void Clear(Window window) => (window.GetValue(SurfaceProperty) as FxSurface)?.Reset();

    // Whether the window's layer marks the control under the pointer now (FxSurface.AnswersHover).
    public static bool AnswersHover(Window window) => window.GetValue(SurfaceProperty) is FxSurface { AnswersHover: true };

    private static void Track(Window window, FxSurface surface)
    {
        Surfaces.Add(surface);
        window.Closed += (_, _) => surface.Detach();
        // C-L9: a window that stops being active takes no more keys, whether or not its focus moved.
        window.Deactivated += (_, _) => surface.KeyboardLeft();
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

// The layer: what is shown is state (the hovered mark, the one leaving, the press, the focus accent, a few particles), drawn
// into one DrawingVisual on frames while anything moves. The frame callback runs only while something is changing: once a
// hover has settled and its control has not moved for a few frames it stops, and pointer moves and layout changes wake it.
internal sealed partial class FxSurface : FrameworkElement
{
    private const int SettleFrames = 8;
    private readonly UIElement _input;
    private readonly UIElement _scope;
    private readonly Func<bool> _interactive;
    private readonly DrawingVisual _visual = new();

    private ThemeMotion _motion = ThemeMotion.Default;
    private MotionTiming _timing = MotionTiming.From(360, 1);
    private int _generation = -1;
    private bool _dressedSystem, _detached, _hooked, _watchingLayout;
    private int _stillFrames;
    private Rect _lastBox = Rect.Empty;

    private FxMark? _hover, _leaving, _pressed, _focus;
    private readonly PressTrack _press = new();
    private readonly FlashBudget _flashes = new();
    private double _focusAt = double.NaN;
    private readonly FocusAccentGate _focusGate = new();
    private int _serial;
    private DependencyObject? _lastSource;
    private FrameworkElement? _lastTarget;

    // Pressed by the keyboard (Space or Enter on a focused control) rather than by the mouse button; or by touch or a pen, which
    // the mouse button's own state does not show.
    private bool _keyPress, _stylusPress;
    // The pointer last moved by touch or a pen: a panel that opens under it marks nothing as hovered (motion-review F4).
    private bool _stylusMoved;
    // A release seen only on the physical button waits for the queued input before it lets the press go (Frame).
    private bool _liftCheck;

    public event Action? Detached;

    public FxSurface(UIElement input, UIElement scope, Func<bool> interactive)
    {
        _input = input; _scope = scope; _interactive = interactive;
        IsHitTestVisible = false; Focusable = false;
        AddVisualChild(_visual);
        input.PreviewMouseMove += Moved;
        input.PreviewMouseLeftButtonDown += Pressed;
        input.PreviewMouseLeftButtonUp += Lifted;
        input.MouseLeave += Left;
        input.PreviewKeyDown += KeyWentDown;
        input.PreviewKeyUp += KeyWentUp;
        input.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Focused), true);
        input.AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Unfocused), true);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    // The window closed: no handler, timer or frame callback stays behind.
    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        _input.PreviewMouseMove -= Moved;
        _input.PreviewMouseLeftButtonDown -= Pressed;
        _input.PreviewMouseLeftButtonUp -= Lifted;
        _input.MouseLeave -= Left;
        _input.PreviewKeyDown -= KeyWentDown;
        _input.PreviewKeyUp -= KeyWentUp;
        _input.RemoveHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Focused));
        _input.RemoveHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Unfocused));
        Reset();
        Detached?.Invoke();
    }

    private static DeckSettings? Settings => (Application.Current as App)?.Settings;
    // Motion allowed at all (BrimDeck's and Windows' animation settings, high contrast), and the window answering now.
    private bool Allowed => !_detached && MotionGate.Interaction(Preview?.Conditions?.Invoke() ?? MotionEnvironment.Conditions(), _motion);
    private bool Active => Allowed && _interactive();
    private double Clock() => Preview?.Clock() ?? Eased.Now;

    // Whether this layer marks the control under the pointer now: a window that has its own hover look shows it only when not
    // (NoticeWindow.TextButton), so a control never wears two.
    internal bool AnswersHover
    {
        get { EnsureDressed(); return Allowed && _motion.Hover != HoverFx.None && _motion.HoverStrength > 0; }
    }

    // ----- Theme -----

    private void EnsureDressed()
    {
        // "System" and the theme it stands for share a palette, so the choice itself is part of what the layer was dressed for.
        bool system = Preview?.SystemTheme ?? PanelThemes.NormalizeId(Settings?.PanelTheme) == PanelThemes.SystemId;
        int generation = Preview?.Palette is null ? PanelTheme.Generation : _generation < 0 ? 0 : _generation;
        if (_generation != generation || system != _dressedSystem || _generation < 0)
        {
            bool restyled = _generation >= 0;
            _generation = generation; _dressedSystem = system;
            if (restyled) Reset();
            var palette = Preview?.Palette?.Invoke() ?? PanelTheme.Current;
            _motion = system ? palette.Motion.ForSystem() : palette.Motion;
            Dress(palette, system);
        }
        // The theme's pace and steps, worked out once; the panel's animation length plays no part (motion-review C5).
        _timing = MotionTiming.For(_motion.Pace, _motion.Steps);
    }

    // ----- Pointer -----

    private void Moved(object sender, MouseEventArgs e)
    {
        if (_detached) return;
        EnsureDressed();
        if (!Active) { Release(); return; }
        // Touch and a pen stand in for the pointer only while they press: they leave no hover mark (motion-review F4).
        _stylusMoved = e.StylusDevice is not null;
        HoverOn(_stylusMoved ? null : TargetOf(e.OriginalSource as DependencyObject));
        if (_press.Down && _pressed is not null) _press.Arm(Clock(), Inside(_pressed.Control, e.GetPosition(_pressed.Control)), _timing, _motion.Steps);
        Wake();
    }

    private void Left(object sender, MouseEventArgs e) => HoverOn(null);

    // ui-10: a control activated from the keyboard answers as a click does: Space presses while it is held, Enter (which
    // clicks as it goes down) presses and completes at once.
    private void KeyWentDown(object sender, KeyEventArgs e)
    {
        if (_detached || e.IsRepeat || e.Key is not (Key.Space or Key.Enter) || _press.Down) return;
        EnsureDressed();
        if (!Active || TargetOf(Keyboard.FocusedElement as DependencyObject) is not { } target || !IsWithin(target)) return;
        _keyPress = true;
        PressOn(target);
        // A theme without a press leaves nothing held.
        if (!_press.Down) _keyPress = false;
        else if (e.Key == Key.Enter) LiftOff(true);
    }

    private void KeyWentUp(object sender, KeyEventArgs e)
    {
        if (_keyPress && e.Key == Key.Space) LiftOff(true);
    }

    // win2 C-L9: Space held while the focus leaves (Alt+Tab, a click on another window, Tab) never sees its key come up here;
    // the button drops its own press then (Feedback follows it), and the layer backs out with it.
    private void Unfocused(object sender, KeyboardFocusChangedEventArgs e) => KeyboardLeft();
    internal void KeyboardLeft() { if (_keyPress) LiftOff(false); }

    private void Pressed(object sender, MouseButtonEventArgs e)
    {
        if (_detached) return;
        EnsureDressed();
        if (!Active) return;
        if (TargetOf(e.OriginalSource as DependencyObject) is not { } target) return;
        _stylusPress = e.StylusDevice is not null;
        _stylusMoved = _stylusPress;
        PressOn(target);
    }

    private void Lifted(object sender, MouseButtonEventArgs e)
    {
        if (_pressed is null || !_press.Down || _keyPress) return;
        LiftOff(Inside(_pressed.Control, e.GetPosition(_pressed.Control)));
    }

    private static bool Inside(FrameworkElement control, Point point) => point.X >= 0 && point.Y >= 0 && point.X <= control.ActualWidth && point.Y <= control.ActualHeight;

    // What is shown fades out: the pointer left, or the panel closed. A press still held backs out.
    public void Release()
    {
        HoverOn(null);
        if (_press.Down) LiftOff(false);
    }

    // The panel opened or moved under a pointer that has not moved: the control under it is answered as if it had.
    public void Refresh()
    {
        if (_detached || !_input.IsMouseOver) return;
        EnsureDressed();
        if (!Active) { Release(); return; }
        bool stylus = _stylusMoved || InputManager.Current.MostRecentInputDevice is StylusDevice;
        HoverOn(stylus ? null : TargetOf(Mouse.DirectlyOver as DependencyObject));
        Wake();
    }

    // The mark moves to the hovered control: it glides from a neighbor it was on, as the monitor's sort pill glides, or
    // arrives on its own while the previous one fades.
    internal void HoverOn(FrameworkElement? target)
    {
        if (_motion.Hover == HoverFx.None || _motion.HoverStrength <= 0) target = null;
        if (ReferenceEquals(target, _hover?.Control)) return;
        double now = Clock();
        FxMark? next = null;
        if (target is not null)
        {
            next = ReferenceEquals(target, _pressed?.Control) ? _pressed : ReferenceEquals(target, _leaving?.Control) ? _leaving : FxMark.For(target);
            if (next is not null)
            {
                next.Update(_scope, Dpi);
                if (!next.Valid || !FxShapes.Accepts(Dip(next.Box), new DipRect(0, 0, ActualWidth, ActualHeight))) next = null;
            }
        }
        var previous = _hover;
        if (next is null && previous is null) return;
        double from = 0;
        if (next is not null)
        {
            // Coming back to a control whose mark is still fading: from where it is.
            if (!double.IsNaN(next.LeftAt)) from = LeavingPresence(next, now);
            if (ReferenceEquals(next, _leaving)) _leaving = null;
            next.LeftAt = double.NaN; next.GlideAt = double.NaN;
        }
        if (previous is not null)
        {
            double presence = HoverPresence(previous, now);
            if (next is not null && Glides(previous, next, presence))
            {
                // The mark itself moves over: the previous one is not drawn any more.
                next.GlideFrom = Drawn(previous, now, out next.GlideFromRadius); next.GlideAt = now;
                from = Math.Max(from, presence);
            }
            else
            {
                previous.LeftFrom = presence; previous.LeftAt = now;
                _leaving = previous;
            }
        }
        if (next is not null) { next.ShownAt = now; next.ShownFrom = from; }
        _hover = next;
        Wake();
    }

    // Glide to a neighbor only: a long way across the panel would sweep over everything between.
    private static bool Glides(FxMark from, FxMark to, double presence)
    {
        if (presence < .3 || !from.Valid || !to.Valid || from.Ellipse != to.Ellipse) return false;
        double dx = (from.Box.X + from.Box.Width / 2) - (to.Box.X + to.Box.Width / 2), dy = (from.Box.Y + from.Box.Height / 2) - (to.Box.Y + to.Box.Height / 2);
        double reach = Math.Max(Math.Max(from.Box.Width, to.Box.Width), Math.Max(from.Box.Height, to.Box.Height)) * 1.6 + 8;
        return Math.Sqrt(dx * dx + dy * dy) <= reach;
    }

    // The button went down on a control: it answers at once (this frame), and holds.
    internal void PressOn(FrameworkElement target)
    {
        if (_motion.Press == PressFx.None || _motion.PressStrength <= 0) return;
        double now = Clock();
        var mark = ReferenceEquals(target, _hover?.Control) ? _hover : ReferenceEquals(target, _pressed?.Control) ? _pressed : FxMark.For(target);
        if (mark is null) return;
        mark.Update(_scope, Dpi);
        if (!mark.Valid || !FxShapes.Accepts(Dip(mark.Box), new DipRect(0, 0, ActualWidth, ActualHeight))) return;
        // A quick second press on the same control goes down from where the first one is, never from nothing.
        double from = ReferenceEquals(mark, _pressed) ? _press.At(now, _timing, _motion.Steps).Held : 0;
        Unwatch();
        _pressed = mark;
        mark.Serial = ++_serial;
        bool glitch = _motion.Press == PressFx.Lock && _flashes.TryTake(now);
        _press.Press(now, from, glitch);
        if (glitch) PrepareGlitch(mark);
        mark.Control.LostMouseCapture += CaptureLost;
        Wake();
        // The first pressed frame is drawn now, not on the next frame callback.
        Frame(now);
    }

    // The button came up: on the control the press completes, elsewhere it only lets go.
    internal void LiftOff(bool inside)
    {
        if (_pressed is null || !_press.Down) return;
        double now = Clock();
        // A press from the keyboard answers as a click does but leaves no particles (motion-review C7).
        bool byKey = _keyPress;
        _keyPress = false;
        Unwatch();
        bool scan = inside && _motion.Press == PressFx.Lock && _flashes.TryTake(now);
        _press.Release(now, inside, scan, _timing, _motion.Steps);
        if (_press.Committed && !byKey && _motion.Particles != ParticleFx.None && _motion.ParticleStrength > 0 && _pressed.Valid) Burst(_pressed, now);
        Wake();
    }

    private void CaptureLost(object sender, MouseEventArgs e)
    {
        if (_keyPress) return;
        // The button lets go of the mouse as it is released too; that release has been seen already (Lifted runs first).
        if (_press.Down) LiftOff(Mouse.LeftButton == MouseButtonState.Pressed ? false : _pressed is { } p && p.Control.IsMouseOver);
    }

    private void Unwatch() { if (_pressed is not null) _pressed.Control.LostMouseCapture -= CaptureLost; }

    // ----- Focus -----

    private void Focused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_detached || InputManager.Current.MostRecentInputDevice is not KeyboardDevice) return;
        EnsureDressed();
        if (!Active || _motion.Focus == FocusFx.None) return;
        if (e.NewFocus is not FrameworkElement element || !element.IsVisible || !IsWithin(element)) return;
        // motion-review C7: Tab after Tab shows the control's own focus frame; only the first move after a pause plays the
        // accent, and an accent still playing on the control left goes with it.
        if (_focusGate.Take(Clock())) FocusOn(element);
        else if (_focus is not null) { _focus = null; Wake(); }
    }

    internal void FocusOn(FrameworkElement element)
    {
        var mark = FxMark.For(element);
        if (mark is null) return;
        mark.Update(_scope, Dpi);
        if (!mark.Valid || !FxShapes.Accepts(Dip(mark.Box), new DipRect(0, 0, ActualWidth, ActualHeight))) return;
        _focus = mark; _focusAt = Clock();
        Wake();
    }

    // ----- Frames -----

    private DpiScale Dpi => VisualTreeHelper.GetDpi(this);
    private static DipRect Dip(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    // Frames come from the panel's shared clock (Motion, in MonitorDraw.cs), paced to the display as the monitor's are (q-P1.2);
    // the step lets go once the layer sleeps.
    private void Wake()
    {
        _stillFrames = 0;
        if (_hooked || _detached) return;
        _hooked = true;
        Motion.Start(this, _ => { if (!_hooked) return false; Frame(Clock()); return _hooked; });
    }

    private void Sleep() => _hooked = false;

    // One frame: the theme is checked, every mark is placed anew, the state is drawn, and the frame callback stops once
    // nothing changes.
    internal void Frame(double now)
    {
        if (_detached) return;
        EnsureDressed();
        // Animations turned off or high contrast: nothing decorative, at once. The panel closing: what is shown fades with it.
        if (!Allowed) { Reset(); return; }
        if (!_interactive() && (_hover is not null || _press.Down)) Release();
        // ui-4: a control that does not capture the mouse (a tab, a list item) never hears the button come up off the window,
        // which left the press held and the frames running; the button's own state lets it go. win2 C-L10: frames run ahead of
        // input, so the button can read as up while its own release still waits in the queue; the press backs out only if it
        // is still held once the queued input has gone through (below Input priority, the dispatcher waits for pending input).
        // A control holding the mouse capture always hears its release, so it is left to Lifted and CaptureLost.
        if (_press.Down && !_keyPress && !_stylusPress && Preview is null && !_liftCheck && _pressed is { } held && !held.Control.IsMouseCaptured && !LeftButtonDown())
        {
            _liftCheck = true;
            int serial = held.Serial;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _liftCheck = false;
                if (_press.Down && !_keyPress && _pressed?.Serial == serial && !LeftButtonDown()) LiftOff(false);
            });
        }
        var dpi = Dpi;
        _hover?.Update(_scope, dpi);
        _leaving?.Update(_scope, dpi);
        if (_pressed is not null && !ReferenceEquals(_pressed, _hover)) _pressed.Update(_scope, dpi);
        if (_focus is not null && !ReferenceEquals(_focus, _hover) && !ReferenceEquals(_focus, _pressed)) _focus.Update(_scope, dpi);

        // A control that has gone (a page swapped, the panel closed) takes its marks with it at once; one disabled or hidden
        // while hovered fades.
        if (_hover is { Valid: false }) { if (!_hover.Control.IsVisible || PresentationSource.FromVisual(_hover.Control) is null) _hover = null; else HoverOn(null); }
        if (_leaving is not null && (!_leaving.Valid || LeavingPresence(_leaving, now) <= 0)) _leaving = null;
        if (_pressed is not null && (!_pressed.Valid || !_press.Alive(now, _timing))) { if (_press.Down) Unwatch(); _press.Clear(); _pressed = null; _slice.Visual = null; }
        if (_focus is not null && (!_focus.Valid || now - _focusAt > _timing.Focus / 1000.0)) _focus = null;
        bool particles = StepParticles(now);

        using (var dc = _visual.RenderOpen()) Draw(dc, now);

        bool moving = _leaving is not null || _pressed is not null || _focus is not null || particles
            || _hover is not null && (now - _hover.ShownAt < _timing.HoverIn / 1000.0 || !double.IsNaN(_hover.GlideAt) && now - _hover.GlideAt < _timing.Glide / 1000.0);
        var box = _hover?.Box ?? Rect.Empty;
        if (moving || box != _lastBox) _stillFrames = 0; else _stillFrames++;
        _lastBox = box;
        if (_stillFrames >= SettleFrames) Sleep();
        // While a mark rests on a control, a layout change anywhere (a scroll, a resize) may move it: look again then.
        WatchLayout(_hover is not null && !_hooked);
    }

    private void WatchLayout(bool watch)
    {
        if (watch == _watchingLayout) return;
        _watchingLayout = watch;
        if (watch) LayoutUpdated += LayoutChanged; else LayoutUpdated -= LayoutChanged;
    }

    // Only a mark that has really moved wakes the frames (drawing never changes layout, so this cannot loop).
    private void LayoutChanged(object? sender, EventArgs e)
    {
        if (_hover is null) { WatchLayout(false); return; }
        var before = _hover.Box;
        _hover.Update(_scope, Dpi);
        if (!_hover.Valid || _hover.Box != before) Wake();
    }

    // Ends everything at once, as if nothing had been shown, and leaves no frame callback or handler behind.
    public void Reset()
    {
        Unwatch();
        _hover = _leaving = _pressed = _focus = null;
        _press.Clear(); _flashes.Clear(); _focusGate.Clear(); _slice.Visual = null; _keyPress = false;
        _lastSource = null; _lastTarget = null;
        ClearParticles();
        Sleep(); WatchLayout(false);
        _stillFrames = 0; _lastBox = Rect.Empty;
        using (_visual.RenderOpen()) { }
    }

    // ----- Targets -----

    // Something that can be clicked: a button of any kind, a list or combo box item or a tab. Scroll bars, sliders, text fields
    // and whole surfaces are not; neither is a disabled control.
    private FrameworkElement? TargetOf(DependencyObject? source)
    {
        if (source is null) return null;
        if (ReferenceEquals(source, _lastSource)) return _lastTarget is { IsEnabled: true, IsVisible: true } ? _lastTarget : null;
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

    private static DependencyObject? Up(DependencyObject node) => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private bool IsWithin(DependencyObject element)
    {
        for (var node = element; node is not null; node = Up(node)) if (ReferenceEquals(node, _scope)) return true;
        return false;
    }

    // The physical button behind the primary (left) one, read now: Mouse.LeftButton follows this thread's messages, which an
    // up over another window never reaches.
    private static bool LeftButtonDown() => (GetAsyncKeyState(SystemParameters.SwapButtons ? 0x02 : 0x01) & 0x8000) != 0;
    [DllImport("user32")] private static extern short GetAsyncKeyState(int key);
}
