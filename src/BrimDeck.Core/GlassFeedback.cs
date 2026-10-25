namespace BrimDeck.Core;

// Liquid glass's answer to the pointer (the design reference's interaction table): hovering brightens the glass a little and
// moves its rim light toward the pointer, a press lights it more strongly from inside. Both ease as the system monitor's hover
// does (Eased, on the monitor's hover time): the hover both ways in hoverMs, a press going down in pressMs and letting go in
// releaseMs (Soft.Press and Soft.Quick, as every press in BrimDeck). With animations off they are at once. The light's place
// is the pointer's, kept while the hover fades out after the pointer left, and NaN once it has.
public sealed class GlassFeedback(int hoverMs, int pressMs, int releaseMs)
{
    private Eased _hover = new(0, hoverMs / 1000d), _press = new(0, pressMs / 1000d);
    private bool _down;

    public double Hover { get; private set; }
    public double Press { get; private set; }
    // Where the light comes from (the units the pointer was given in); NaN when there is none.
    public double LightX { get; private set; } = double.NaN;
    public double LightY { get; private set; } = double.NaN;

    // One step at now (seconds, Eased.Now): over, whether the pointer is over the glass, at x, y; down, whether its button is
    // held there. Returns whether either value is still on its way.
    public bool Step(bool over, bool down, double x, double y, bool motion, double now)
    {
        down &= over;
        if (over && double.IsFinite(x) && double.IsFinite(y)) { LightX = x; LightY = y; }
        _hover.Set(over ? 1 : 0, motion, now);
        if (down != _down)
        {
            // Each way has its own time: the move starts again from where the press is now.
            _down = down;
            _press = new Eased(_press.At(now), (down ? pressMs : releaseMs) / 1000d);
        }
        _press.Set(down ? 1 : 0, motion, now);
        bool hovering = _hover.Moving(now), pressing = _press.Moving(now);
        Hover = hovering ? Math.Clamp(_hover.At(now), 0, 1) : _hover.Target;
        Press = pressing ? Math.Clamp(_press.At(now), 0, 1) : _press.Target;
        if (!over && Hover <= 0) { LightX = LightY = double.NaN; }
        return hovering || pressing;
    }

    // The answer on a surface open this far (0 collapsed .. 1 the open panel): the open panel is the content layer, a stable
    // ground for its pages, and only the collapsed island (the control) answers the pointer; between, in proportion.
    public static double Calm(double value, double open) => value * (1 - Math.Clamp(double.IsFinite(open) ? open : 0, 0, 1));
}
