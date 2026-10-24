namespace BrimDeck.Core.Notices;

// How a dynamic notice moves, on the panel's own clock (MainWindow.SetExpanded), so that the notice and the panel read as one
// object. A move lasts the animation duration set for the panel. The notice grows out of the island as the panel opens: the
// whole move on the opening spring, the island's flat indicator color gone within its first frames. It goes back as the panel
// closes: its content leaves first, the surface settles with a slow start, the indicator's color returns only at the line's
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
    public static int Length(int animationDuration, bool animate) => animate ? Math.Clamp(animationDuration, 100, 1000) : 0;
}
