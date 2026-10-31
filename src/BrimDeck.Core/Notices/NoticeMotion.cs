namespace BrimDeck.Core.Notices;

// The timing of what a notice moves on the panel's own clock: a move lasts the animation duration set for the panel, and once a
// notice has left the island, the island's own content lands for as long as the panel's compact content takes when it closes.
// Tracks are fractions of a move (Start to End).
public static class NoticeMotion
{
    public readonly record struct Track(double Start, double End)
    {
        public double Length => End - Start;
    }

    // The panel's own opening and closing tracks (MainWindow.SetExpanded), the one place they are written down: what moves as the
    // panel does (the notices page's row swap, a notice going back into the island) takes them (q-P1.1).
    public static readonly Track PanelOpenSurface = new(0, 1);
    // The opening spring is most of the way there within a fifth of the move, so the page follows soon after it.
    public static readonly Track PanelOpenContent = new(.12, .8);
    public static readonly Track PanelOpenTint = new(0, .12);
    public static readonly Track PanelCloseContent = new(0, .3);
    public static readonly Track PanelCloseSurface = new(.12, 1);
    public static readonly Track PanelCloseTint = new(.85, 1);
    public static readonly Track PanelLandCompact = new(.62, 1);

    // Back into the island: the panel's closing tracks. A notice that follows another in place (a row swap): what showed leaves
    // before the new text arrives.
    public static readonly Track ReturnContent = PanelCloseContent;
    public static readonly Track ReturnSurface = PanelCloseSurface;
    public static readonly Track FollowContent = new(.3, .9);

    // Once the notice has gone, the island's own content lands for as long as the panel's compact content takes.
    public static double RevealLength => PanelLandCompact.Length;

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

    // A lay-out the same way as the move still under way (the closing surface given a new collapsed width by its content)
    // carries that move on rather than starting a whole one: from where the surface is, at once, over what is left of the move
    // and never under RedirectMin of a whole one (length), so it reads as one move and not as a closing that stops and starts
    // over. 0 when no such move is under way: the lay-out is then a move of its own.
    public int Carried(long now, bool opening, int length)
        => length > 0 && Underway(now, opening) ? (int)Math.Max(_ends - now, Math.Round(length * NoticeMotion.RedirectMin)) : 0;

    // Whether an opening (or a closing) is still under way at now.
    public bool Underway(long now, bool opening) => now < _ends && opening == _opening;
}
