namespace BrimDeck.Core.Notices;

// How a dynamic notice moves, on the panel's own clock (MainWindow.SetExpanded), so that the notice and the panel read as one
// object. A move lasts the animation duration set for the panel. The notice grows out of the island as the panel opens: the
// whole move on the opening spring, the island's flat indicator color gone within its first frames. It goes back as the panel
// closes: its content leaves first, the surface follows on the drawer's curve, the indicator's color returns only at the line's
// size, and the island's own content lands last. The details open and close the same way. The content that leaves and the
// content that arrives never show at once, so two figures are never faded over each other.
// Tracks are fractions of a move (Start to End).
public static class NoticeMotion
{
    public readonly record struct Track(double Start, double End)
    {
        public double Length => End - Start;
    }

    // The panel's own opening and closing tracks (MainWindow.SetExpanded), the one place they are written down: the notice's
    // moves below take them wherever it moves as the panel does (q-P1.1).
    public static readonly Track PanelOpenSurface = new(0, 1);
    // The opening spring is most of the way there within a fifth of the move, so the page follows soon after it.
    public static readonly Track PanelOpenContent = new(.12, .8);
    public static readonly Track PanelOpenTint = new(0, .12);
    public static readonly Track PanelCloseContent = new(0, .3);
    public static readonly Track PanelCloseSurface = new(.12, 1);
    public static readonly Track PanelCloseTint = new(.85, 1);
    public static readonly Track PanelLandCompact = new(.62, 1);

    // From the island (or the details) to the arriving notice, and into the details: the panel's opening tracks. The toast's
    // text keeps its own, slightly later track (ArriveContent, against the page's PanelOpenContent).
    public static readonly Track Surface = PanelOpenSurface;
    public static readonly Track ArriveContent = new(.2, .85);
    public static readonly Track ArriveTint = PanelOpenTint;
    public static readonly Track ArriveGlow = new(.3, 1);
    // A notice that follows another in place: what showed leaves before the new text arrives.
    public static readonly Track FollowContent = new(.3, .9);

    // Back into the island, and from the details back to the notice: the panel's closing tracks.
    public static readonly Track ReturnContent = PanelCloseContent;
    public static readonly Track ReturnSurface = PanelCloseSurface;
    public static readonly Track ReturnGlow = new(0, .6);
    public static readonly Track ReturnTint = PanelCloseTint;
    // What arrives as something closes (the notice's text as the details close) lands as the panel's compact content does.
    public static readonly Track Landing = PanelLandCompact;

    // Into the details: the notice's text goes quickly, the details arrive once the card is mostly open.
    public static readonly Track OpenOut = new(0, .2);
    public static readonly Track OpenIn = new(.3, .9);

    // Once the notice has gone, the island's own content lands for as long as the panel's compact content takes (Landing).
    public static double RevealLength => Landing.Length;

    // The length of a move in milliseconds: the panel's duration (kept to the settings' range), or 0 without animations.
    public static int Length(int animationDuration, bool animate) => animate ? AnimationLength.Clamp(animationDuration) : 0;

    // motion-review C1: the panel closes in three quarters of the time it takes to open, as it draws back into the dot on
    // powering off. C4: a move the keyboard asked for (a hotkey, Escape, the notices hotkey) takes 0.6 of it and lands without
    // the opening spring's give.
    public const double CloseFactor = .75, KeyFactor = .6;
    public static int PanelLength(int length, bool closing, bool keyboard)
        => length <= 0 ? 0 : (int)Math.Round(length * (keyboard ? KeyFactor : closing ? CloseFactor : 1));
    // A notice going back (into the island, or from its details to the notice) closes as the panel does.
    public static double ReturnLength(double length, bool keyboard) => PanelLength((int)Math.Round(length), closing: true, keyboard);

    // motion-review F1: a closing that turns back a move still under way starts its surface at once (no slow, delayed start
    // from where the opening was still going), on the whole of its shorter move.
    public static readonly Track Redirected = new(0, 1);
    public static Track ReturnSurfaceFor(bool redirected) => redirected ? Redirected : ReturnSurface;

    // motion-review F2: a move turned back part way only has the way already covered to go back: its length is that share of a
    // whole move (the distance left over the distance between the two ends), never under RedirectMin of it, so a turn just
    // after the start still reads as a move. Each pair is (now, the target being left, the new target); the largest share of
    // the sizes that change counts. Nothing changing at all takes the whole move.
    public const double RedirectMin = .4;
    public static double RemainingShare(params (double Now, double Left, double Target)[] sizes)
    {
        double share = -1;
        foreach (var (now, left, target) in sizes)
        {
            double whole = Math.Abs(left - target);
            if (!double.IsFinite(whole) || !double.IsFinite(now) || whole < 1) continue;
            share = Math.Max(share, Math.Abs(now - target) / whole);
        }
        return share < 0 ? 1 : Math.Clamp(share, RedirectMin, 1);
    }
}

// The last opening or closing of the panel or of a notice, so that the next one can tell whether it turns that one back while
// it is still under way (motion-review F1, F2). Only those moves count: the panel laid out again while it rests open or closed
// (a page's new size, a window coming to the front, a reading) never makes the next closing look turned back, which cut a
// closing after such a lay-out short; and only a move the other way turns one back (a notice going into the island while it
// still leaves its details does not). Times are milliseconds on one clock (Environment.TickCount64).
public sealed class MoveTurn
{
    private long _ends;
    private bool _opening;
    private double _width = double.NaN, _height = double.NaN;

    // An opening (or a closing) of the given length toward the given size starts at now.
    public void Started(long now, double length, bool opening, double width, double height)
    {
        _ends = length > 0 ? now + (long)Math.Ceiling(length) : 0;
        _opening = opening; _width = width; _height = height;
    }

    // Whether an opening (or a closing) starting at now turns back the last move: only one the other way, still under way.
    public bool Turns(long now, bool opening) => now < _ends && opening != _opening;

    // The share of a whole move the turning move takes (NoticeMotion.RemainingShare), from the size the surface has now to the
    // new target; 1 when it turns nothing back.
    public double Share(long now, bool opening, double width, double height, double toWidth, double toHeight)
        => Turns(now, opening) ? NoticeMotion.RemainingShare((width, _width, toWidth), (height, _height, toHeight)) : 1;
}
