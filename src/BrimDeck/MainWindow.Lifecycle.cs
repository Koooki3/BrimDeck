using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// Booting and powering off, as the iPhone's island starts and shuts down: the collapsed surface swells out of a dot at its
// middle and gives a little before it settles, and on the way out it narrows back into that dot and fades away. A panel boots
// when it first appears (BrimDeck starts, a monitor is connected) and whenever it shows again after being hidden (the tray's
// hide, a full-screen rule); it powers off when BrimDeck exits and when it is hidden. Without animations it appears and goes at once.
public partial class MainWindow
{
    // _booting asks the next SetExpanded for the boot's tracks; _poweringOff holds the panel still while it goes.
    private bool _booting, _poweringOff, _bootWaiting;
    // Pointing at the surface does not open the panel while it waits out of sight for its boot, or until the boot has played
    // (TrackPointer): a pointer already resting there would open an invisible panel, or cut the swell short.
    private long _bootEnds;
    private bool BootHoldsPointer => _bootWaiting || Environment.TickCount64 < _bootEnds;
    // The boot and the power-off, as multiples of the duration set for the panel.
    private const double BootStretch = 1.8, PowerOffStretch = 1.1;
    // The part of the boot in which the dot fades in, before the surface starts to swell.
    private const double BootDot = .14;

    // The dot the surface swells from and shrinks into: as wide as the surface is tall, a pill's round end.
    private static double DotWidth(CollapsedShape shape) => Math.Min(shape.Surface.Width, Math.Max(8, shape.Surface.Height));
    // The dot's left edge in the window, centred on the surface.
    private double DotLeft(CollapsedShape shape) => shape.Surface.X + (shape.Surface.Width - DotWidth(shape)) / 2 - _windowOffset;

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
    // longer than BootWait boots anyway.
    private void Boot()
    {
        if (_expanded || IsSuppressed || _poweringOff || _app.Exiting || _retiring || !CanAnimateLifecycle) return;
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

    // The dot appears at the middle of the surface, then swells into the collapsed shape on the opening spring (SetExpanded's
    // boot tracks) while the compact content arrives last.
    private void BeginBoot()
    {
        _boot++; _bootWaiting = false;
        if (_poweringOff || _app.Exiting || _retiring) return;
        if (_expanded || IsSuppressed || !CanAnimateLifecycle) { SetImmediate(HoverZone, OpacityProperty, 1d); return; }
        var shape = Collapsed = ShapeFor(EffectiveStyle);
        SetImmediate(HoverZone, MarginProperty, new Thickness(DotLeft(shape), 0, 0, 0));
        SetImmediate(Island, WidthProperty, DotWidth(shape));
        SetImmediate(IslandShadow, OpacityProperty, 0d);
        if (Compact.Visibility == Visibility.Visible)
        {
            SetImmediate(Compact, OpacityProperty, 0d);
            SetImmediate(CompactScale, ScaleTransform.ScaleXProperty, .85); SetImmediate(CompactScale, ScaleTransform.ScaleYProperty, .85);
        }
        // The dot is in full view before it starts to widen (SetExpanded's surface track starts at BootDot).
        SetImmediate(HoverZone, OpacityProperty, 1d);
        HoverZone.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Settings.AnimationDuration * BootStretch * BootDot))
        { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
        _booting = true;
        _bootEnds = Environment.TickCount64 + (long)(Settings.AnimationDuration * BootStretch);
        SetExpanded(false);
    }

    // Takes the panel off the screen: an open panel closes at once, the compact content leaves first, then the surface narrows
    // into the dot on the settling curve and fades. gone runs once it has, or at once when nothing animates. Nothing opens it
    // meanwhile; ApplySettings and SetExpanded wait for it (see RestoreAfterPowerOff).
    internal void PowerOff(Action gone)
    {
        if (_poweringOff) { _whenGone += gone; return; }
        if (!CanAnimateLifecycle) { gone(); return; }
        _boot++; _bootWaiting = false; _bootEnds = 0;
        if (_expanded) SetExpanded(false, instant: true);
        _hover.Stop(); _leave.Stop(); _pointerInside = false; _cursorInCompact = false;
        _poweringOff = true; _whenGone = gone;
        UpdateGrips();
        var shape = Collapsed;
        int duration = (int)(Settings.AnimationDuration * PowerOffStretch);
        Track compact = new(0, .35), surface = new(.12, .8), shadow = new(0, .45), fade = new(.5, 1);
        Animate(Compact, OpacityProperty, 0, duration, compact, Soft.InOut);
        Animate(CompactScale, ScaleTransform.ScaleXProperty, .85, duration, compact, Soft.InOut);
        Animate(CompactScale, ScaleTransform.ScaleYProperty, .85, duration, compact, Soft.InOut);
        Animate(IslandShadow, OpacityProperty, 0, duration, shadow, Soft.Out);
        Animate(Island, WidthProperty, DotWidth(shape), duration, surface, Soft.Settle);
        HoverZone.BeginAnimation(MarginProperty, Timed(new ThicknessAnimation { To = new Thickness(DotLeft(shape), 0, 0, 0), EasingFunction = Soft.Settle }, duration, surface), HandoffBehavior.SnapshotAndReplace);
        Animate(HoverZone, OpacityProperty, 0, duration, fade, Soft.InOut, () =>
        {
            if (!_poweringOff) return;
            _poweringOff = false;
            var whenGone = _whenGone; _whenGone = null;
            whenGone?.Invoke();
        });
    }
    private Action? _whenGone;

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
        SetImmediate(HoverZone, OpacityProperty, 1d);
        ApplySettings();
        if (!IsSuppressed && IsVisible && !_expanded) Boot();
    }
}
