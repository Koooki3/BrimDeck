using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The theme's answer to the pointer on the open panel (ThemeMotion): a mark on the control the pointer hovers, drawn on the
// control's visible shape (FxMark), never around its layout box and never following the pointer, on one layer above the
// content that takes no input. The mark is off unless a theme turns it on (no built-in theme does), and a control that already
// shows its own hover never wears a mark over it (HoverOwners): only a control that shows nothing of its own is marked.
// With animations off (BrimDeck's or Windows'), or in high contrast, nothing is drawn.
internal static class InteractionFx
{
    // The panel's layer lies inside the island's content, above it, so the island's outline clips it like the content.
    public static FxSurface Island(Window window, UIElement input, Panel content, Func<bool> interactive, Func<bool> animates, Func<PanelLook> look)
    {
        var surface = new FxSurface(input, content, interactive, animates, look);
        content.Children.Add(surface);
        window.Closed += (_, _) => surface.Detach();
        return surface;
    }
}

// The layer: what is shown is state (the hovered mark and the few still fading out), drawn into one DrawingVisual on frames
// while a mark eases. The frame callback exists only then: once the mark has settled and its control has not moved for a few
// frames it is taken off the shared clock (Motion), so an idle panel costs nothing.
internal sealed partial class FxSurface : FrameworkElement
{
    private const int SettleFrames = 8;
    private readonly UIElement _input;
    private readonly UIElement _scope;
    private readonly Func<bool> _interactive;
    // The owning panel's animation rule (Soft.For its profile's settings) and look (its own theme, which another monitor's profile may differ in).
    private readonly Func<bool> _animates;
    private readonly Func<PanelLook> _look;
    private readonly DrawingVisual _visual = new();

    private ThemeMotion _motion = ThemeMotion.Default;
    private MotionTiming _timing = MotionTiming.For(1);
    private int _generation = -1;
    private bool _detached, _hooked;
    private int _stillFrames;
    private Rect _lastBox = Rect.Empty;

    private FxMark? _hover;
    // Marks still fading out after the pointer left them: a quick sweep across several controls leaves several fading at once.
    // A fixed handful, so nothing is allocated; when all are taken the faintest gives way.
    private readonly FxMark?[] _leaving = new FxMark?[3];
    private DependencyObject? _lastSource;
    private FrameworkElement? _lastTarget;

    public FxSurface(UIElement input, UIElement scope, Func<bool> interactive, Func<bool> animates, Func<PanelLook> look)
    {
        _input = input; _scope = scope; _interactive = interactive; _animates = animates; _look = look;
        IsHitTestVisible = false; Focusable = false;
        AddVisualChild(_visual);
        input.PreviewMouseMove += Moved;
        input.MouseLeave += Left;
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    // The window closed: no handler or frame callback stays behind.
    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        _input.PreviewMouseMove -= Moved;
        _input.MouseLeave -= Left;
        Reset();
    }

    // Motion allowed at all (the panel's BrimDeck and Windows animation settings, high contrast) and the theme drawing something.
    private bool Allowed => !_detached && MotionGate.Interaction(_animates(), _motion);
    private bool Active => Allowed && _interactive();

    // ----- Theme -----

    private void EnsureDressed()
    {
        var look = _look();
        int generation = look.Generation;
        if (_generation != generation)
        {
            bool restyled = _generation >= 0;
            _generation = generation;
            if (restyled) Reset();
            var palette = look.Current;
            _motion = palette.Motion;
            _timing = MotionTiming.For(_motion.Pace);
            Dress(palette);
        }
    }

    // After a change of settings or theme: what is shown ends at once when it may not be shown any more.
    public void Check()
    {
        if (_detached) return;
        EnsureDressed();
        if (!Allowed) Reset();
    }

    // ----- Pointer -----

    private void Moved(object sender, MouseEventArgs e)
    {
        if (_detached) return;
        EnsureDressed();
        if (!Active) { Release(); return; }
        // Touch and a pen stand in for the pointer only while they press: they leave no hover mark (as Feedback).
        var target = e.StylusDevice is not null ? null : TargetOf(e.OriginalSource as DependencyObject);
        if (ReferenceEquals(target, _hover?.Control))
        {
            // The same control: only a mark whose control has moved (a list scrolling under the pointer) needs frames again.
            if (_hover is { } mark) { var before = mark.Box; mark.Update(_scope, Dpi); if (!mark.Valid || mark.Box != before) Wake(); }
            return;
        }
        HoverOn(target);
    }

    private void Left(object sender, MouseEventArgs e) => HoverOn(null);

    // What is shown fades out: the pointer left, or the panel closed.
    public void Release() => HoverOn(null);

    // The panel opened or moved under a pointer that has not moved: the control under it is marked as if it had.
    public void Refresh()
    {
        if (_detached || !_input.IsMouseOver) return;
        EnsureDressed();
        if (!Active) { Release(); return; }
        bool stylus = InputManager.Current.MostRecentInputDevice is StylusDevice;
        HoverOn(stylus ? null : TargetOf(Mouse.DirectlyOver as DependencyObject));
    }

    // The mark moves to the hovered control: it glides from a neighbor it was on, as the monitor's sort pill glides, or
    // arrives on its own while the previous one fades.
    internal void HoverOn(FrameworkElement? target)
    {
        if (!_motion.AnyInteraction) target = null;
        if (ReferenceEquals(target, _hover?.Control)) return;
        double now = Eased.Now;
        FxMark? next = null;
        if (target is not null)
        {
            next = LeavingFor(target) ?? FxMark.For(target);
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
            if (next.Timeline.Leaving) from = next.Timeline.Presence(now, _timing);
            for (int i = 0; i < _leaving.Length; i++) if (ReferenceEquals(_leaving[i], next)) _leaving[i] = null;
            next.GlideAt = double.NaN;
        }
        if (previous is not null)
        {
            double presence = previous.Timeline.Presence(now, _timing);
            if (next is not null && Glides(previous, next, presence))
            {
                // The mark itself moves over: the previous one is not drawn any more.
                next.GlideFrom = Drawn(previous, now, out next.GlideFromRadius); next.GlideAt = now;
                from = Math.Max(from, presence);
            }
            else
            {
                previous.Timeline.Leave(now, presence);
                AddLeaving(previous, now);
            }
        }
        next?.Timeline.Arrive(now, from);
        _hover = next;
        if (next is not null || AnyLeaving()) Wake();
    }

    private FxMark? LeavingFor(FrameworkElement target)
    {
        foreach (var mark in _leaving) if (ReferenceEquals(mark?.Control, target)) return mark;
        return null;
    }

    private void AddLeaving(FxMark mark, double now)
    {
        int slot = -1;
        double faintest = double.MaxValue;
        for (int i = 0; i < _leaving.Length; i++)
        {
            if (_leaving[i] is not { } other) { slot = i; break; }
            double presence = other.Timeline.Presence(now, _timing);
            if (presence < faintest) { faintest = presence; slot = i; }
        }
        _leaving[slot] = mark;
    }

    private bool AnyLeaving()
    {
        foreach (var mark in _leaving) if (mark is not null) return true;
        return false;
    }

    // Glide to a neighbor only: a long way across the panel would sweep over everything between.
    private static bool Glides(FxMark from, FxMark to, double presence)
    {
        if (presence < .3 || !from.Valid || !to.Valid || from.Ellipse != to.Ellipse) return false;
        double dx = (from.Box.X + from.Box.Width / 2) - (to.Box.X + to.Box.Width / 2), dy = (from.Box.Y + from.Box.Height / 2) - (to.Box.Y + to.Box.Height / 2);
        double reach = Math.Max(Math.Max(from.Box.Width, to.Box.Width), Math.Max(from.Box.Height, to.Box.Height)) * 1.6 + 8;
        return Math.Sqrt(dx * dx + dy * dy) <= reach;
    }

    // ----- Frames -----

    private DpiScale Dpi => VisualTreeHelper.GetDpi(this);
    private static DipRect Dip(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    // Frames come from the panel's shared clock (Motion, in MonitorDraw.cs), paced to the display as the monitor's are;
    // the step lets go once the layer sleeps.
    private void Wake()
    {
        _stillFrames = 0;
        if (_hooked || _detached) return;
        _hooked = true;
        Motion.Start(this, _ => { if (!_hooked) return false; Frame(Eased.Now); return _hooked; });
    }

    private void Sleep() => _hooked = false;

    // One frame: the theme is checked, every mark is placed anew, the state is drawn, and the frame callback stops once
    // nothing changes.
    internal void Frame(double now)
    {
        if (_detached) return;
        EnsureDressed();
        // Animations turned off or high contrast: nothing decorative, at once.
        if (!Allowed) { Reset(); return; }
        // The panel closing: what is shown fades with it.
        if (!_interactive() && _hover is not null) Release();
        var dpi = Dpi;
        _hover?.Update(_scope, dpi);
        foreach (var mark in _leaving) mark?.Update(_scope, dpi);

        // A control that has gone (a page swapped, the panel closed) takes its mark with it at once; one disabled or hidden
        // while hovered fades.
        if (_hover is { Valid: false }) { if (!_hover.Control.IsVisible || PresentationSource.FromVisual(_hover.Control) is null) _hover = null; else HoverOn(null); }
        for (int i = 0; i < _leaving.Length; i++)
            if (_leaving[i] is { } mark && (!mark.Valid || mark.Timeline.Presence(now, _timing) <= 0)) _leaving[i] = null;

        using (var dc = _visual.RenderOpen()) Draw(dc, now);

        bool moving = AnyLeaving()
            || _hover is not null && (_hover.Timeline.Moving(now, _timing) || !double.IsNaN(_hover.GlideAt) && now - _hover.GlideAt < _timing.Glide / 1000.0);
        var box = _hover?.Box ?? Rect.Empty;
        if (moving || box != _lastBox) _stillFrames = 0; else _stillFrames++;
        _lastBox = box;
        if (_stillFrames >= SettleFrames) Sleep();
    }

    // Ends everything at once, as if nothing had been shown, and leaves no frame callback behind.
    public void Reset()
    {
        _hover = null;
        Array.Clear(_leaving);
        _lastSource = null; _lastTarget = null;
        Sleep();
        _stillFrames = 0; _lastBox = Rect.Empty;
        using (_visual.RenderOpen()) { }
    }

    // ----- Targets -----

    // Something that can be clicked: a button of any kind, a list or combo box item or a tab. Scroll bars, sliders, text fields
    // and whole surfaces are not; neither is a disabled control, nor one that shows its own hover (HoverOwners.DrawsOwn).
    private FrameworkElement? TargetOf(DependencyObject? source)
    {
        if (source is null) return null;
        if (ReferenceEquals(source, _lastSource)) return _lastTarget is { IsEnabled: true, IsVisible: true } ? _lastTarget : null;
        FrameworkElement? found = null;
        for (var node = source; node is not null && !ReferenceEquals(node, _scope); node = Up(node))
        {
            if (node is ScrollBar or TextBoxBase or PasswordBox or RepeatButton or Thumb or Slider) { found = null; break; }
            if (node is IslandBorder) break;
            if (found is null && node is ButtonBase or ListBoxItem or ComboBox or TabItem) found = (FrameworkElement)node;
        }
        if (found is not null && (!found.IsEnabled || !found.IsVisible || HoverOwners.DrawsOwn(found))) found = null;
        _lastSource = source; _lastTarget = found;
        return found;
    }

    private static DependencyObject? Up(DependencyObject node) => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
