using System.Windows;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

// One motion language for the whole app: the same few curves and durations for the panel, its pages, cards, hover and press
// feedback, and dragging. Moves start promptly and land slowly (Out); what changes in place eases both ways (InOut); what
// opens or appears leaves at full speed and lands with a slight give (Bounce), and what closes or goes away starts slowly and
// settles without one (Settle), as the iPhone's island does; what follows the pointer trails it on a spring (BrimDeck.Core.Spring).
// The curves are frozen and shared.
internal static class Soft
{
    // A soft landing: quick at first, a long gentle tail.
    public static readonly IEasingFunction Out = Frozen(new BezierEase(.22, .8, .28, 1));
    // For what turns or swaps in place.
    public static readonly IEasingFunction InOut = Frozen(new BezierEase(.45, 0, .25, 1));
    // The collapsing surface: a slow start, then it settles.
    public static readonly IEasingFunction Settle = Frozen(new BezierEase(.45, 0, .12, 1));
    // The system monitor's move (Curves.Follow): content that moves or swaps, selections that glide, hover feedback.
    public static readonly IEasingFunction Follow = Frozen(new FollowEase());
    // The opening surface: a fast attack that passes its size by under 2 percent and settles back (Curves.Bounce).
    public static readonly IEasingFunction Bounce = Frozen(new BounceEase());

    // Hover and press feedback, and what leaves; small things that appear or swap. Values that follow readings take
    // BrimDeck.Core.Eased.Seconds, and the panel itself the duration chosen in the settings. Hover is the monitor's (Eased.Hover),
    // a glide between choices the monitor's sort pill (Choose), content that moves in or out the monitor's readings (Move).
    public const int Quick = 160, Short = 240, Hover = (int)(Eased.Hover * 1000), Choose = (int)(Eased.Seconds * 750), Move = (int)(Eased.Seconds * 1000);

    // Whether anything moves: the setting in BrimDeck and Windows' own "animation effects".
    public static bool Enabled => SystemParameters.ClientAreaAnimation && (Application.Current as App)?.Settings.Animations != false;

    public static DoubleAnimation Fade(double from, double to, int milliseconds, IEasingFunction? easing = null) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing ?? Out };

    private static IEasingFunction Frozen(Freezable ease) { ease.Freeze(); return (IEasingFunction)ease; }
}

// Cubic Bézier easing with CSS semantics, so the surface follows the same curve as the approved mock-up. EasingFunctionBase
// mirrors EaseInCore unless the mode is EaseIn (its default is EaseOut), which turned every curve here back to front: the
// soft landing started slowly and stopped hard. The curve is the whole easing, so the mode is EaseIn.
public sealed class BezierEase : EasingFunctionBase
{
    private readonly double _x1, _y1, _x2, _y2;
    public BezierEase(double x1, double y1, double x2, double y2) { _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2; EasingMode = EasingMode.EaseIn; }
    protected override Freezable CreateInstanceCore() => new BezierEase(_x1, _y1, _x2, _y2);
    protected override double EaseInCore(double normalizedTime) => Curves.Bezier(_x1, _y1, _x2, _y2, normalizedTime);
}

// Curves.Follow as a WPF easing; see BezierEase for the mode.
public sealed class FollowEase : EasingFunctionBase
{
    public FollowEase() { EasingMode = EasingMode.EaseIn; }
    protected override Freezable CreateInstanceCore() => new FollowEase();
    protected override double EaseInCore(double normalizedTime) => Curves.Follow(normalizedTime);
}

// Curves.Bounce as a WPF easing; see BezierEase for the mode.
public sealed class BounceEase : EasingFunctionBase
{
    public BounceEase() { EasingMode = EasingMode.EaseIn; }
    protected override Freezable CreateInstanceCore() => new BounceEase();
    protected override double EaseInCore(double normalizedTime) => Curves.Bounce(normalizedTime);
}
