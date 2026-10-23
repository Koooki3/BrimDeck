namespace BrimDeck.Core;

// Where a line too wide for its region stands: at rest at its start, gliding at a steady speed until its end is in view, at rest
// there, then back at its start. It moves by the time between display frames and keeps its place when the region's width
// changes, so a row that is built again (the collapsed row is, on every change of its width) carries on where the old one was,
// instead of starting over from rest.
public sealed class MarqueeScroll
{
    // How long the line rests at its start and at its end, in seconds.
    public const double Hold = 1.5;
    // The longest step one frame may take: after a stalled frame the line goes on from where it stood instead of jumping ahead.
    public const double MaxStep = .1;
    private enum Phase { Start, Glide, End }
    private Phase _phase;
    private double _offset, _rested, _last = double.NaN;

    // The line this place belongs to.
    public string Key { get; private set; } = "";

    // A new line starts at rest at its start; the same line keeps its place. Returns whether it started again.
    public bool Follow(string key)
    {
        if (key == Key) return false;
        Key = key; Restart();
        return true;
    }

    public void Restart() { _phase = Phase.Start; _offset = 0; _rested = 0; _last = double.NaN; }

    // Stops counting time; the next frame goes on from where the line stands.
    public void Pause() => _last = double.NaN;

    // How far the line is moved to the left, in DIPs, for a region it overflows by the given amount. At its end the line keeps its
    // end at the region's edge however the region changes; gliding, it never passes that.
    public double At(double overflow) => !(overflow > 0) ? 0 : _phase == Phase.End ? overflow : Math.Min(_offset, overflow);

    // Moves the line on to the given frame time, in seconds. Several views of the same line in one frame move it once.
    public double Advance(double time, double overflow, double speed)
    {
        if (!(overflow > 0) || !(speed > 0) || !double.IsFinite(time)) { Restart(); return 0; }
        if (!double.IsNaN(_last) && time <= _last) return At(overflow);
        double step = double.IsNaN(_last) ? 0 : Math.Min(time - _last, MaxStep);
        _last = time;
        // The region changed since the last frame: a glide that already shows the line's end rests there.
        if (_phase == Phase.Glide && _offset >= overflow) { _phase = Phase.End; _rested = 0; }
        if (_phase == Phase.End) _offset = overflow;
        // Every pass either uses up the step or moves to the next phase, and the two rests are longer than any step.
        while (step > 0)
        {
            if (_phase == Phase.Glide)
            {
                double left = (overflow - _offset) / speed;
                if (step < left) { _offset += step * speed; step = 0; }
                else { step -= left; _offset = overflow; _phase = Phase.End; _rested = 0; }
                continue;
            }
            double rest = Hold - _rested;
            if (step < rest) { _rested += step; step = 0; continue; }
            step -= rest; _rested = 0;
            if (_phase == Phase.Start) _phase = Phase.Glide;
            else { _phase = Phase.Start; _offset = 0; }
        }
        return At(overflow);
    }

    // How far each edge's fade has grown, 0 to 1. It follows how much of the line is hidden beyond that edge, up to the fade's
    // width: none on the left before the line moves, none on the right once its end is in view.
    public static (double Left, double Right) Fades(double offset, double overflow, double edge)
    {
        if (!(overflow > 0)) return (0, 0);
        double range = Math.Min(overflow, edge > 0 ? edge : double.Epsilon);
        return (Math.Clamp(offset / range, 0, 1), Math.Clamp((overflow - offset) / range, 0, 1));
    }
}
