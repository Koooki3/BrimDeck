using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using static BrimDeck.Tracks;
using Track = BrimDeck.Core.Notices.NoticeMotion.Track;

namespace BrimDeck;

// Booting and powering off, as the iPhone's island starts and shuts down. Every form (notch, capsule, line) starts as the same
// small round dot at the middle of its surface: the dot pops up with two little bounces, then quickly swells into the collapsed
// shape. On the way out the surface draws back into that dot, which bounces once and sinks like a drop into water, leaving a
// ripple. A panel boots when it first appears (BrimDeck starts, a monitor is connected) and whenever it shows again after being
// hidden (the tray's hide, a full-screen rule); it powers off when BrimDeck exits and when it is hidden. Without animations it
// appears and goes at once.
public partial class MainWindow
{
    // _booting asks the next SetExpanded for the boot's tracks; _poweringOff holds the panel still while it goes.
    private bool _booting, _poweringOff, _bootWaiting;
    // Pointing at the surface does not open the panel while it waits out of sight for its boot, or until the boot has played
    // (TrackPointer): a pointer already resting there would open an invisible panel, or cut the swell short.
    private long _bootEnds;
    private bool BootHoldsPointer => _bootWaiting || Environment.TickCount64 < _bootEnds;
    // The parts of the boot and the power-off, as multiples of the duration set for the panel: the dot's pop, the swell into the
    // shape (SetExpanded); the draw back into the dot, its bounce and its drop.
    private const double BootPop = 1.1, BootSwell = 1.0, PowerOffShrink = .9, PowerOffBounce = .5, PowerOffDrop = .9;
    // How long BrimDeck's exit waits at most for the panels to power off (App.ExitApplication).
    internal static int PowerOffLength(int duration) => (int)(duration * (PowerOffShrink + PowerOffBounce + PowerOffDrop));

    // The dot: a small circle, the same for every form, at the middle of the surface (kept inside the window at the top).
    private static double DotSize(CollapsedShape shape) => Math.Clamp(Math.Round(shape.Surface.Height * .5), 8, 16);
    private double DotLeft(CollapsedShape shape) => shape.Surface.X + (shape.Surface.Width - DotSize(shape)) / 2 - _windowOffset;
    private static double DotTop(CollapsedShape shape) => Math.Max(0, shape.Surface.Y + (shape.Surface.Height - DotSize(shape)) / 2);

    // The dot pops and bounces by scaling the island about its middle; outside the boot and the power-off the scale is 1.
    private ScaleTransform? _dotScale;
    private ScaleTransform DotScale()
    {
        if (_dotScale is null) { _dotScale = new ScaleTransform(1, 1); Island.RenderTransformOrigin = new Point(.5, .5); Island.RenderTransform = _dotScale; }
        return _dotScale;
    }
    private void ResetDotScale()
    {
        if (_dotScale is null) return;
        SetImmediate(_dotScale, ScaleTransform.ScaleXProperty, 1d); SetImmediate(_dotScale, ScaleTransform.ScaleYProperty, 1d);
    }
    private void ScaleDot(AnimationTimeline animation)
    {
        var scale = DotScale();
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    // Whether the island can pass a size of width x height at left and gap (window coordinates) by the opening spring's give
    // without leaving the window, which would cut it off for those frames. Width and Height are the values on screen now.
    private bool HasRoomToBounce(double width, double height, double left, double gap)
    {
        double give = Curves.BounceOvershoot;
        double grow = Math.Max(0, width - Island.Width) * give, rise = Math.Max(0, height - Island.Height) * give;
        return double.IsFinite(grow) && double.IsFinite(rise) && left - grow / 2 >= 0 && left + width + grow / 2 <= Width && gap + height + rise <= Height;
    }

    private bool CanAnimateLifecycle => IsVisible && Soft.Enabled && Settings.AnimationDuration > 0 && !_preview;

    // Starts the boot. The panel stays out of sight until the dispatcher is idle: on BrimDeck's start the first frames come
    // late while the panel is built and the data read, which ate the dot and the first half of the swell. A panel that is busy
    // longer than BootWait boots anyway. The panel is out of sight from the start (the constructor), so the window is never
    // seen before it has its place on the monitor.
    private void Boot()
    {
        if (_poweringOff || _app.Exiting || _retiring) return;
        if (_expanded || IsSuppressed || !CanAnimateLifecycle) { SetImmediate(HoverZone, OpacityProperty, 1d); return; }
        SetImmediate(HoverZone, OpacityProperty, 0d);
        _bootWaiting = true;
        int boot = ++_boot;
        var wait = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(BootWait) };
        void Start() { wait.Stop(); if (boot == _boot) BeginBoot(); }
        wait.Tick += (_, _) => Start();
        wait.Start();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Start);
    }
    private int _boot;
    private const int BootWait = 700;

    // The dot pops up at the middle of the surface with two little bounces, then swells into the collapsed shape on the opening
    // spring (SetExpanded's boot tracks) while the compact content arrives last.
    private void BeginBoot()
    {
        int boot = ++_boot; _bootWaiting = false;
        if (_poweringOff || _app.Exiting || _retiring) return;
        if (_expanded || IsSuppressed || !CanAnimateLifecycle) { SetImmediate(HoverZone, OpacityProperty, 1d); return; }
        var shape = Collapsed = ShapeFor(EffectiveStyle);
        PlaceDot(shape);
        SetImmediate(IslandShadow, OpacityProperty, 0d);
        if (Compact.Visibility == Visibility.Visible)
        {
            SetImmediate(Compact, OpacityProperty, 0d);
            SetImmediate(CompactScale, ScaleTransform.ScaleXProperty, .85); SetImmediate(CompactScale, ScaleTransform.ScaleYProperty, .85);
        }
        int pop = (int)(Settings.AnimationDuration * BootPop);
        _bootEnds = Environment.TickCount64 + pop + (long)(Settings.AnimationDuration * BootSwell);
        SetImmediate(HoverZone, OpacityProperty, 1d);
        HoverZone.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(pop * .2)) { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
        // Two bounces, the second smaller, and no still tail before the swell.
        var popUp = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(pop) };
        popUp.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        foreach (var (at, size, ease) in new[] { (.35, 1.28, Soft.Out), (.55, .86, Soft.InOut), (.72, 1.1, Soft.InOut), (.87, .97, Soft.InOut), (1, 1, Soft.InOut) })
            popUp.KeyFrames.Add(new EasingDoubleKeyFrame(size, KeyTime.FromPercent(at), ease));
        popUp.Completed += (_, _) =>
        {
            if (boot != _boot || _poweringOff || _app.Exiting || _retiring) return;
            ResetDotScale();
            // Opened meanwhile (a hotkey), the panel is already on its way; otherwise the dot swells into the shape.
            if (_expanded) return;
            _booting = true;
            SetExpanded(false);
        };
        ScaleDot(popUp);
    }

    // The surface as the dot, at once: round, without shoulders, centred on the surface.
    private void PlaceDot(CollapsedShape shape)
    {
        double dot = DotSize(shape);
        SetImmediate(HoverZone, MarginProperty, new Thickness(DotLeft(shape), 0, 0, 0));
        SetImmediate(Island, WidthProperty, dot); SetImmediate(Island, HeightProperty, dot);
        SetImmediate(Island, MarginProperty, new Thickness(0, DotTop(shape), 0, 0));
        SetImmediate(Island, Border.CornerRadiusProperty, new CornerRadius(dot / 2));
        SetImmediate(Island, IslandBorder.ShoulderRadiusProperty, 0d);
        ResetDotScale();
    }

    // Takes the panel off the screen: an open panel closes at once, the compact content leaves first, then the surface draws
    // back into the dot on the settling curve, the dot bounces once and sinks into a ripple. gone runs once it has, or at once
    // when nothing animates. Nothing opens it meanwhile; ApplySettings and SetExpanded wait for it (see RestoreAfterPowerOff).
    internal void PowerOff(Action gone)
    {
        if (_poweringOff) { _whenGone += gone; return; }
        if (!CanAnimateLifecycle) { gone(); return; }
        int boot = ++_boot; _bootWaiting = false; _bootEnds = 0;
        _noticeWindow?.Vanish();
        if (_expanded) SetExpanded(false, instant: true);
        _hover.Stop(); _leave.Stop(); _pointerInside = false; _cursorInCompact = false;
        _poweringOff = true; _whenGone = gone;
        UpdateGrips();
        ResetDotScale();
        var shape = Collapsed;
        double dot = DotSize(shape);
        int shrink = (int)(Settings.AnimationDuration * PowerOffShrink);
        // The shoulders and the flat top go early, so the surface is round well before it is small.
        Track compact = new(0, .35), surface = new(.15, 1), corners = new(.15, .6), shadow = new(0, .45);
        Animate(Compact, OpacityProperty, 0, shrink, compact, Soft.InOut);
        Animate(CompactScale, ScaleTransform.ScaleXProperty, .85, shrink, compact, Soft.InOut);
        Animate(CompactScale, ScaleTransform.ScaleYProperty, .85, shrink, compact, Soft.InOut);
        Animate(IslandShadow, OpacityProperty, 0, shrink, shadow, Soft.Out);
        Animate(Island, IslandBorder.ShoulderRadiusProperty, 0, shrink, corners, Soft.InOut);
        Island.BeginAnimation(Border.CornerRadiusProperty, Timed(new CornerRadiusAnimation { To = new CornerRadius(dot / 2), EasingFunction = Soft.Out }, shrink, corners), HandoffBehavior.SnapshotAndReplace);
        Island.BeginAnimation(MarginProperty, Timed(new ThicknessAnimation { To = new Thickness(0, DotTop(shape), 0, 0), EasingFunction = Soft.Settle }, shrink, surface), HandoffBehavior.SnapshotAndReplace);
        HoverZone.BeginAnimation(MarginProperty, Timed(new ThicknessAnimation { To = new Thickness(DotLeft(shape), 0, 0, 0), EasingFunction = Soft.Settle }, shrink, surface), HandoffBehavior.SnapshotAndReplace);
        Animate(Island, HeightProperty, dot, shrink, surface, Soft.Settle);
        Animate(Island, WidthProperty, dot, shrink, surface, Soft.Settle);
        Sink(shape, boot, shrink);
    }
    private Action? _whenGone;

    // Once the surface is the dot (after `after` ms), it bounces once, then sinks: it shrinks away while a ripple spreads from
    // where it was and fades. Everything is set up now, with its start delayed: built at the moment the dot is reached, the
    // rings and their animations held up the frames there.
    private void Sink(CollapsedShape shape, int boot, int after)
    {
        int bounce = (int)(Settings.AnimationDuration * PowerOffBounce), drop = (int)(Settings.AnimationDuration * PowerOffDrop);
        var hop = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(after), Duration = TimeSpan.FromMilliseconds(bounce + drop) };
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(1.25, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(bounce * .45)), Soft.Out));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(bounce)), Soft.InOut));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(bounce + drop * .55)), new BezierEase(.55, 0, .9, .5)));
        hop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(bounce + drop))));
        int total = after + bounce + drop;
        // life-L12: the hop's end is what lets the panel go, and a hop whose clock was replaced, or whose frames stopped (a
        // locked session), never ends; the panel then ignored every later ApplySettings. It goes anyway a little after.
        var fallback = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(total + 500) };
        void Gone()
        {
            fallback.Stop();
            if (boot != _boot || !_poweringOff) return;
            HideRipple();
            _poweringOff = false;
            var whenGone = _whenGone; _whenGone = null;
            whenGone?.Invoke();
        }
        hop.Completed += (_, _) => Gone();
        fallback.Tick += (_, _) => Gone();
        fallback.Start();
        ScaleDot(hop);
        HoverZone.BeginAnimation(OpacityProperty, Timed(new DoubleAnimation { To = 0, EasingFunction = Soft.InOut }, total, new((after + bounce + drop * .35) / total, (after + bounce + drop * .6) / total)), HandoffBehavior.SnapshotAndReplace);
        Ripple(shape, after + bounce + (int)(drop * .2), drop);
    }

    // Two rings spread from the dot's middle and fade, one a little after the other: in the theme's rim when it has one, else
    // in a light or dark tint against the surface, so they show on the desktop whatever the surface's own color.
    private readonly Ellipse[] _ripples = new Ellipse[2];
    private Brush RippleBrush()
    {
        if (Island.RimBrush is not SolidColorBrush { Color.A: < 0x40 }) return Island.RimBrush.CloneCurrentValue();
        var surface = _surface.Color;
        bool dark = .299 * surface.R + .587 * surface.G + .114 * surface.B < 128;
        return new SolidColorBrush(dark ? Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x90, 0, 0, 0));
    }
    private void Ripple(CollapsedShape shape, int start, int length)
    {
        double dot = DotSize(shape), reach = dot * 4.5;
        double x = DotLeft(shape) + dot / 2, y = DotTop(shape) + dot / 2;
        var brush = RippleBrush();
        for (int i = 0; i < _ripples.Length; i++)
        {
            var ring = _ripples[i] ??= new Ellipse { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform() };
            if (ring.Parent is null) Stage.Children.Add(ring);
            ring.Width = ring.Height = reach;
            ring.Margin = new Thickness(x - reach / 2, y - reach / 2, 0, 0);
            ring.Stroke = brush; ring.StrokeThickness = 1.6 - i * .4;
            ring.Visibility = Visibility.Visible;
            var delay = TimeSpan.FromMilliseconds(start + i * length * .22);
            var span = TimeSpan.FromMilliseconds(length * (1 - i * .22));
            var grow = new DoubleAnimation(dot / reach, 1, span) { BeginTime = delay, EasingFunction = Soft.Out };
            var scale = (ScaleTransform)ring.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow); scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            SetImmediate(ring, OpacityProperty, 0d);
            var fade = new DoubleAnimationUsingKeyFrames { BeginTime = delay, Duration = span };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(.85 - i * .25, KeyTime.FromPercent(.12)));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), Soft.Out));
            ring.BeginAnimation(OpacityProperty, fade);
        }
    }
    private void HideRipple()
    {
        foreach (var ring in _ripples) if (ring is not null) { SetImmediate(ring, OpacityProperty, 0d); ring.Visibility = Visibility.Collapsed; }
    }

    // A new collapsed style from the settings (notch, capsule or line): the surface changes into the new form instead of
    // switching (see SetExpanded). The panel stays as it is when it is open or hidden.
    private bool _morphingShape;
    internal void MorphShape()
    {
        // A preview from the settings window morphs too: the style chips sit on the page that previews the collapsed size.
        _morphingShape = !_expanded && !_poweringOff && IsVisible && Soft.Enabled && Settings.AnimationDuration > 0 && !_bootWaiting;
        ApplySettings(true);
        _morphingShape = false;
    }

    // After a power-off that hid the panel (or was overtaken by the panel being wanted again): the surface gets back its shape and
    // opacity out of sight, and a panel that is still meant to show boots again.
    private void RestoreAfterPowerOff()
    {
        // Exiting meanwhile, or its monitor gone (Retire closed the window), the panel stays gone.
        if (_app.Exiting || _retiring) return;
        ResetDotScale(); HideRipple();
        SetImmediate(HoverZone, OpacityProperty, 1d);
        ApplySettings();
        if (!IsSuppressed && IsVisible && !_expanded) Boot();
    }
}
