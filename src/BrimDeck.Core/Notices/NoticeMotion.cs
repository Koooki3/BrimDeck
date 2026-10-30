namespace BrimDeck.Core.Notices;

// The timing of what a notice moves on the panel's own clock: a move lasts the animation duration set for the panel, and once a
// notice has left the island, the island's own content lands for as long as the panel's compact content takes when it closes.
public static class NoticeMotion
{
    public readonly record struct Track(double Start, double End)
    {
        public double Length => End - Start;
    }

    // The share of a move the island's content takes to land (the panel's compact track, .62 to 1).
    public const double RevealLength = .38;

    // The length of a move in milliseconds: the panel's duration (kept to the settings' range), or 0 without animations.
    public static int Length(int animationDuration, bool animate) => animate ? Math.Clamp(animationDuration, 100, 1000) : 0;
}
