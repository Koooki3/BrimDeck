namespace BrimDeck.Core;

// The veil while the panel opens (user 11-21 on the deployed build: a capsule decided dark over a dark tab bar opened over a
// white page, and its dark veil thickened from .30 to .81 over 350-500 ms after the panel had opened, a second darkening). The
// area the open panel will cover is read when the opening begins; the luminosity it asks for (Covered) is reached in step with
// the opening shown (openness 0..1), from what the capsule asks for (Capsule), so the veil rises with the panel and never
// catches up after it: never below the capsule's, and the whole of Covered only once open. NaN Covered: nothing read for the
// covered area (the capsule's alone).
public static class GlassOpening
{
    public static double Required(double capsule, double covered, double openness)
    {
        if (!double.IsFinite(covered)) return capsule;
        double o = Math.Clamp(double.IsFinite(openness) ? openness : 0, 0, 1);
        return Math.Max(capsule, capsule + (covered - capsule) * o);
    }
}
