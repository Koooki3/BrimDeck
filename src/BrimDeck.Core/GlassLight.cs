namespace BrimDeck.Core;

// Where liquid glass's edge catches its light: an edge facing a brighter part of the desktop reflects more of it. The probe's ring
// tells how bright each side around the island is (AcrylicProbe.Reading.Sides); the rim's gradient and the inner bevel's glow
// start on the brighter side. Where the sides hardly differ the light stays the theme's own, from the left (the gradient as
// the theme draws it), so a plain desktop shows the designed rim. The direction moves with a time constant, never in a jump.
public static class GlassLight
{
    // The theme's own light: the rim gradient runs from the left edge to the right.
    public static readonly (double X, double Y) Default = (1, 0);
    // A difference between opposite sides below Calm leaves the theme's light; above Clear the sides' own direction is taken.
    public const double Calm = .04, Clear = .20;
    public const double Tau = 600;

    // A unit vector from the brighter side of the ring toward the darker one, blended with Default by how clear the difference is.
    public static (double X, double Y) Direction((double Left, double Right, double Top, double Bottom) sides)
    {
        double dx = Difference(sides.Left, sides.Right), dy = Difference(sides.Top, sides.Bottom);
        double length = Math.Sqrt(dx * dx + dy * dy);
        double trust = Math.Clamp((length - Calm) / (Clear - Calm), 0, 1);
        if (length < 1e-9 || trust <= 1e-9) return Default;
        // Turned from the theme's light toward the sides' own by the trust, along the shorter way round: no jump at any trust.
        return Turn(Default, Math.Atan2(dy, dx), trust);

        static double Difference(double a, double b) => double.IsFinite(a) && double.IsFinite(b) ? a - b : 0;
    }

    // Moves the shown direction toward the target by the time passed (an exponential step along the shorter way round, the same
    // at any sampling rate).
    public static (double X, double Y) Follow((double X, double Y) shown, (double X, double Y) target, double elapsedMs)
    {
        if (!double.IsFinite(elapsedMs) || elapsedMs <= 0) return shown;
        return Turn(shown, Math.Atan2(target.Y, target.X), 1 - Math.Exp(-elapsedMs / Tau));
    }

    private static (double X, double Y) Turn((double X, double Y) from, double toAngle, double share)
    {
        double start = Math.Atan2(from.Y, from.X);
        double delta = Math.IEEERemainder(toAngle - start, 2 * Math.PI);
        double angle = start + delta * Math.Clamp(share, 0, 1);
        return (Math.Cos(angle), Math.Sin(angle));
    }

    // The gradient's start and end in the island's box (0..1) for a direction.
    public static ((double X, double Y) Start, (double X, double Y) End) Gradient((double X, double Y) direction)
        => ((.5 - .5 * direction.X, .5 - .5 * direction.Y), (.5 + .5 * direction.X, .5 + .5 * direction.Y));

    // The inner bevel's width in DIP: a thin optical edge on the capsule, a wider one on the open panel.
    public static double BevelWidth(double openness) => 3 + 3 * Math.Clamp(openness, 0, 1);
}
