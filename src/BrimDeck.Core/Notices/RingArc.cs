namespace BrimDeck.Core.Notices;

// The arc of a countdown's ring on the island (IslandRing): the part of a circle a countdown has run, starting at the top and going
// counterclockwise as the panel's other rings do (UI.Ring). A plain computation, so that the arc can be checked against the time
// without a window.
public readonly record struct RingArc(double Fraction, double Radius, double Center, double EndX, double EndY, bool Large, bool Full)
{
    // size, thickness: the ring's outer size and the stroke's width, in DIPs. Fractions under a thousandth are none and over 0.999 whole.
    public static RingArc Of(double fraction, double size, double thickness)
    {
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        double radius = Math.Max(0, size / 2 - thickness / 2), center = size / 2, angle = fraction * Math.PI * 2;
        return new(fraction, radius, center, center - radius * Math.Sin(angle), center - radius * Math.Cos(angle), fraction > .5, fraction >= .999);
    }

    public bool None => Fraction <= 0;
}
