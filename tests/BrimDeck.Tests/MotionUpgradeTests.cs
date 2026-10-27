using BrimDeck.Core;

// The motion upgrade: spring tiers on the monitor's spring.
internal static class MotionUpgradeTests
{
    internal static void Run(Action<string, bool> check)
    {
        static PanelPalette Palette(string id) => PanelThemes.Resolve(PanelThemes.BuiltIn(id)!);
        static PanelPalette Custom(string json) => PanelThemes.Build("custom", PanelThemes.Parse(json));
        Springs(check, Palette, Custom);
    }

    private static void Springs(Action<string, bool> check, Func<string, PanelPalette> palette, Func<string, PanelPalette> custom)
    {
        check("Spring tiers: firm 0.85, default the monitor's 0.70, jelly 0.50, all at 16 rad/s",
            SpringTiers.Damping(SpringTier.Firm) == .85 && SpringTiers.Damping(SpringTier.Default) == .7 && SpringTiers.Damping(SpringTier.Jelly) == .5
            && Spring.Frequency == 16 && Spring.DampingRatio == .7);
        check("Only the cyberpunk demo and Endfield choose a tier (firm); every other built-in theme keeps the monitor's spring",
            PanelThemes.BuiltIns.All(theme => palette(theme.Id).Motion.Spring == (theme.Id is PanelThemes.Demo or PanelThemes.Endfield ? SpringTier.Firm : SpringTier.Default))
            && ThemeMotion.Default.Spring == SpringTier.Default);
        check("motion.spring is read by name, inherits, and an unknown or numeric name keeps the base's",
            custom("""{ "base": "dark", "motion": { "spring": "jelly" } }""").Motion.Spring == SpringTier.Jelly
            && custom("""{ "base": "dark", "motion": { "spring": "FIRM" } }""").Motion.Spring == SpringTier.Firm
            && custom("""{ "base": "dark", "motion": { "spring": "wobbly" } }""").Motion.Spring == SpringTier.Default
            && custom("""{ "base": "dark", "motion": { "spring": "2" } }""").Motion.Spring == SpringTier.Default);
        check("System keeps the monitor's spring whatever the theme chose",
            custom("""{ "base": "dark", "motion": { "spring": "jelly" } }""").Motion.ForSystem().Spring == SpringTier.Default);
        check("The theme template writes the tier out", PanelThemes.FillTemplate("t", custom("""{ "base": "dark", "motion": { "spring": "jelly" } }""")).Contains("\"spring\": \"jelly\""));

        // The default behaviour is unchanged: a spring made as before and one given the default tier move identically.
        var before = new Spring(0) { Target = 100 };
        var tier = new Spring(0, SpringTiers.Damping(SpringTier.Default)) { Target = 100 };
        bool same = true;
        for (int i = 0; i < 120; i++) { before.Step(1 / 60.0); tier.Step(1 / 60.0); same &= before.Value == tier.Value && before.Velocity == tier.Velocity; }
        check("The default tier moves exactly as the spring did before", same);

        // Overshoot and how often each tier passes its target, from the stepped spring and the closed form alike.
        static (double Peak, int Crossings) Path(double damping)
        {
            var s = new Spring(0, damping) { Target = 100 };
            double peak = 0; int crossings = 0; double last = -100;
            for (int i = 0; i < 240; i++)
            {
                if (!s.Step(1 / 240.0 * 2)) break;
                double offset = s.Value - 100;
                if (Math.Sign(offset) != Math.Sign(last) && Math.Abs(offset) > .3) { crossings++; last = offset; }
                peak = Math.Max(peak, s.Value - 100);
            }
            return (peak / 100, crossings);
        }
        var firm = Path(.85); var normal = Path(.7); var jelly = Path(.5);
        check($"Firm barely passes its target ({firm.Peak:P1}), default slightly ({normal.Peak:P1}), jelly clearly ({jelly.Peak:P1})",
            firm.Peak < .01 && normal.Peak is > .03 and < .06 && jelly.Peak is > .14 and < .19);
        check("The closed form agrees with the stepped spring on the first overshoot",
            Math.Abs(SpringTiers.Overshoot(.5) - jelly.Peak) < .01 && Math.Abs(SpringTiers.Overshoot(.7) - normal.Peak) < .01);
        int Swings(double damping)
        {
            int n = 0; double last = 1;
            for (double t = 0; t < 1; t += 1 / 1000.0)
            {
                double x = SpringTiers.Response(damping, t);
                if (Math.Abs(x) > .01 && Math.Sign(x) != Math.Sign(last)) { n++; last = x; }
            }
            return n;
        }
        check("Let go, jelly springs past its shape twice or more, default once, firm at most once and by under a percent",
            Swings(.5) >= 2 && Swings(.7) == 1 && Swings(.85) <= 1 && SpringTiers.Overshoot(.85) < .01);
        check("Every tier has the same rhythm: the same 16 rad/s, so the same period within the damping's own stretch",
            Math.Abs(SpringTiers.Response(.5, 0) - 1) < 1e-12 && Math.Abs(SpringTiers.Response(.85, 0) - 1) < 1e-12 && SpringTiers.Response(.5, -1) == 1);
    }
}
