using BrimDeck.Core;

// Liquid glass's answer to the pointer (GlassFeedback): hover and press eased on the monitor's hover and BrimDeck's press times,
// at once with animations off, the light kept at the pointer while the hover fades, and none on the open panel.
internal static class GlassFeedbackTests
{
    public static void Run(Action<string, bool> check)
    {
        // Soft.Hover (the monitor's Eased.Hover), Soft.Press and Soft.Quick.
        const int Hover = (int)(Eased.Hover * 1000), Press = 100, Release = 160;
        var glass = new GlassFeedback(Hover, Press, Release);
        double t = 100;
        bool moving = glass.Step(false, false, double.NaN, double.NaN, true, t);
        check("GlassFeedback: at rest nothing lights and nothing moves", !moving && glass.Hover == 0 && glass.Press == 0 && double.IsNaN(glass.LightX));

        moving = glass.Step(true, false, 30, 8, true, t);
        check("GlassFeedback: hovering starts easing in, its light at the pointer", moving && glass.Hover < .05 && glass.LightX == 30 && glass.LightY == 8);
        glass.Step(true, false, 30, 8, true, t + Hover / 2000d);
        double half = glass.Hover;
        moving = glass.Step(true, false, 40, 9, true, t + Hover / 1000d + 1e-6);
        check("GlassFeedback: the hover takes the monitor's hover time", half is > .2 and < .95 && glass.Hover == 1 && !moving);
        check("GlassFeedback: the light follows the pointer", glass.LightX == 40 && glass.LightY == 9);

        t += 1;
        glass.Step(true, true, 40, 9, true, t);
        glass.Step(true, true, 40, 9, true, t + Press / 2000d);
        double pressing = glass.Press;
        moving = glass.Step(true, true, 40, 9, true, t + Press / 1000d + 1e-6);
        check("GlassFeedback: a press goes down in Soft.Press", pressing is > .2 and < 1 && glass.Press == 1 && !moving);
        t += 1;
        glass.Step(true, false, 40, 9, true, t);
        glass.Step(true, false, 40, 9, true, t + Press / 1000d + 1e-6);
        double letting = glass.Press;
        glass.Step(true, false, 40, 9, true, t + Release / 1000d + 1e-6);
        check("GlassFeedback: let go, it comes back in Soft.Quick, slower than it went down", letting > 0 && glass.Press == 0);

        t += 1;
        glass.Step(false, false, double.NaN, double.NaN, true, t);
        glass.Step(false, false, double.NaN, double.NaN, true, t + .05);
        check("GlassFeedback: the pointer gone, the hover fades with its light where it was", glass.Hover is > 0 and < 1 && glass.LightX == 40);
        glass.Step(false, false, double.NaN, double.NaN, true, t + Hover / 1000d + 1e-6);
        check("GlassFeedback: faded out, there is no light", glass.Hover == 0 && double.IsNaN(glass.LightX));
        glass.Step(false, true, double.NaN, double.NaN, true, t + 1);
        check("GlassFeedback: a button held away from the glass presses nothing", glass.Press == 0);

        var instant = new GlassFeedback(Hover, Press, Release);
        moving = instant.Step(true, true, 5, 5, false, 200);
        check("GlassFeedback: with animations off hover and press are whole at once", !moving && instant.Hover == 1 && instant.Press == 1);
        moving = instant.Step(false, false, double.NaN, double.NaN, false, 200.001);
        check("GlassFeedback: with animations off they go at once, the light with them", !moving && instant.Hover == 0 && instant.Press == 0 && double.IsNaN(instant.LightX));

        check("GlassFeedback: the open panel (the content layer) does not answer the pointer; the collapsed island does in full",
            GlassFeedback.Calm(1, 1) == 0 && GlassFeedback.Calm(1, 0) == 1 && Math.Abs(GlassFeedback.Calm(1, .25) - .75) < 1e-9 && GlassFeedback.Calm(.5, double.NaN) == .5);
    }
}
