using System.Windows;
using System.Windows.Media.Animation;

namespace BrimDeck;

// One motion language for the whole app: the same few curves and durations for the panel, its pages, cards, hover and press
// feedback, and dragging. Moves start promptly and land slowly (Out); what changes in place eases both ways (InOut); what
// follows the pointer trails it on a spring (BrimDeck.Core.Spring). The curves are frozen and shared.
internal static class Soft
{
    // A soft landing: quick at first, a long gentle tail.
    public static readonly IEasingFunction Out = Frozen(new BezierEase(.22, .8, .28, 1));
    // For what turns or swaps in place.
    public static readonly IEasingFunction InOut = Frozen(new BezierEase(.45, 0, .25, 1));
    // The collapsing surface: a slow start, then it settles.
    public static readonly IEasingFunction Settle = Frozen(new BezierEase(.45, 0, .12, 1));

    // Hover and press feedback, and what leaves; small things that appear or swap. Values that follow readings take
    // BrimDeck.Core.Eased.Seconds, and the panel itself the duration chosen in the settings.
    public const int Quick = 160, Short = 240;

    public static DoubleAnimation Fade(double from, double to, int milliseconds, IEasingFunction? easing = null) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing ?? Out };

    private static IEasingFunction Frozen(BezierEase ease) { ease.Freeze(); return ease; }
}
