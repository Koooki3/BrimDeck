namespace BrimDeck.Core;

// The veil while the panel opens (user 11-21 on the deployed build: a capsule decided dark over a dark tab bar opened over a
// white page, and its dark veil thickened from .30 to .81 over 350-500 ms after the panel had opened, a second darkening). The
// area the open panel will cover is read when the opening begins; the luminosity it asks for (Covered) is reached in step with
// the opening shown (openness 0..1), from what the capsule asks for (Capsule), so the veil moves with the panel and never
// catches up after it. NaN Covered: nothing read for the covered area (the capsule's alone).
//   Review v3 V305: a covered area that asks for less (a white page under the light look) thins the glass with the opening, as
// one that asks for more thickens it; the capsule's own strip stays under the open panel, so the thinning stops CapsuleShare of
// the way short of the covered area's (controlled: no thinner than the area the text mostly lies over allows).
public static class GlassOpening
{
    public const double CapsuleShare = .25;

    public static double Required(double capsule, double covered, double openness)
    {
        if (!double.IsFinite(covered)) return capsule;
        double o = Math.Clamp(double.IsFinite(openness) ? openness : 0, 0, 1);
        double open = covered >= capsule ? covered : covered + CapsuleShare * (capsule - covered);
        return capsule + (open - capsule) * o;
    }

    // Review v3b (the first opening over a white page): the covered area's read turned the look about 20 ms after the intent, the
    // panel still almost shut, and the palette's turn from the old look (about 365 ms) then ran over the panel's first 100-200 ms,
    // its body shown dark before it turned light. A look the opening itself decides (GlassAppearance.Open) while the panel shown
    // is less than TurnAtOnceBelow open is taken at once: the opening is the panel's new scene, and the glass it grows into is
    // already the new look's. A turn decided over a panel mostly open (a late read) still goes on the palette's spring.
    public const double TurnAtOnceBelow = .5;

    public static bool TurnsAtOnce(double openness, bool animates)
        => !animates || !(Math.Clamp(double.IsFinite(openness) ? openness : 0, 0, 1) >= TurnAtOnceBelow);
}
