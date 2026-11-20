using BrimDeck.Core;

// Where liquid glass's edge takes its light from (GlassLight): the theme's own light over a plain desktop, the brighter side
// otherwise, never a jump.
internal static class GlassLightTests
{
    internal static void Run(Action<string, bool> check)
    {
        static double Angle((double X, double Y) v) => Math.Atan2(v.Y, v.X);
        var plain = GlassLight.Direction((.5, .5, .5, .5));
        var off = GlassLight.Direction((double.NaN, double.NaN, double.NaN, double.NaN));
        check("A plain desktop or no reading keeps the theme's own light", plain == GlassLight.Default && off == GlassLight.Default);
        var right = GlassLight.Direction((.1, .9, .5, .5));
        var top = GlassLight.Direction((.5, .5, .9, .1));
        check("A bright right side moves the light to the right, a bright top to the top",
            Math.Abs(Math.Abs(Angle(right)) - Math.PI) < 1e-9 && Math.Abs(Angle(top) - Math.PI / 2) < 1e-9);
        var slight = GlassLight.Direction((.48, .52, .5, .5));
        check("A slight difference is no reason to move it", slight == GlassLight.Default);
        // Continuity: the side difference swept from nothing to clear never jumps the direction.
        double worst = 0; var last = GlassLight.Direction((.5, .5, .5, .5));
        for (int i = 1; i <= 400; i++)
        {
            double d = i / 400d * .5;
            var next = GlassLight.Direction((.5 - d, .5 + d, .5, .5));
            worst = Math.Max(worst, Math.Abs(Math.IEEERemainder(Angle(next) - Angle(last), 2 * Math.PI)));
            last = next;
        }
        check(FormattableString.Invariant($"Sweeping to the opposite side turns the light smoothly ({worst:0.000} rad per step at most)"), worst < .05);
        var followed = GlassLight.Follow(GlassLight.Default, right, 300);
        var twice = GlassLight.Follow(GlassLight.Follow(GlassLight.Default, right, 150), right, 150);
        check("The light turns by the time passed, the same in one step or two", Math.Abs(Angle(followed) - Angle(twice)) < 1e-9
            && Math.Abs(Angle(followed)) > .1 && Math.Abs(Angle(followed)) < Math.PI - .1);
        var (start, end) = GlassLight.Gradient(GlassLight.Default);
        check("The theme's light gives the theme's own gradient, left to right", start.X == 0 && end.X == 1 && start.Y == .5 && end.Y == .5);
        check("The bevel is thin on the capsule and wider open", GlassLight.BevelWidth(0) == 3 && GlassLight.BevelWidth(1) == 6 && GlassLight.BevelWidth(9) == 6);
    }
}
