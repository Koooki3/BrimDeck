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
    // How many whole cycles (rest, glide, rest, back to the start) a Limited line scrolls before it rests at its start.
    public const int Laps = 3;
    private enum Phase { Start, Glide, End }
    private Phase _phase;
    private double _offset, _rested, _last = double.NaN;
    private bool _waiting;
    private int _laps;

    // The collapsed row's title scrolls Laps cycles and then rests at its start, with no fade on the left and the right fade over
    // the hidden text: a song plays for minutes, and the glide's frames were most of what the row cost while it played. A new
    // line (Follow) or Replay lets it scroll again; the open page's lyric is not limited.
    public bool Limited { get; init; }
    // The whole cycles scrolled since the line last started.
    public int LapsDone => _laps;
    // Whether a Limited line has scrolled its laps: it then stays at its start and Advance moves it no more.
    public bool Rested => Limited && _laps >= Laps;

    // The line this place belongs to.
    public string Key { get; private set; } = "";

    // A new line starts at rest at its start; the same line keeps its place. Returns whether it started again.
    public bool Follow(string key)
    {
        if (key == Key) return false;
        Key = key; Restart();
        return true;
    }

    public void Restart() { Home(); _laps = 0; }
    private void Home() { _phase = Phase.Start; _offset = 0; _rested = 0; _last = double.NaN; _waiting = false; }

    // Lets a line that has rested scroll its laps again from its start (the pointer came over the island). Returns whether it was
    // resting.
    public bool Replay()
    {
        if (!Rested) return false;
        Restart();
        return true;
    }

    // Stops counting time; the next frame goes on from where the line stands.
    public void Pause() { _last = double.NaN; _waiting = false; }

    // How much of the rest the line is in is still to come, in seconds; 0 while it glides.
    public double RestLeft => _phase == Phase.Glide ? 0 : Math.Max(0, Hold - _rested);

    // The view waits out the rest without frames (nothing moves in it) and comes back for its end: the first step after this
    // may cover the rest that was left and one ordinary step more, so the line leaves its rest when it would have with every
    // frame drawn. Time still counts while waiting, unlike Pause.
    public void Wait() => _waiting = true;

    // How far the line is moved to the left, in DIPs, for a region it overflows by the given amount. At its end the line keeps its
    // end at the region's edge however the region changes; gliding, it never passes that.
    public double At(double overflow) => !(overflow > 0) ? 0 : _phase == Phase.End ? overflow : Math.Min(_offset, overflow);

    // Moves the line on to the given frame time, in seconds. Several views of the same line in one frame move it once.
    public double Advance(double time, double overflow, double speed)
    {
        if (Rested) { Home(); return 0; }
        // The laps counted so far stay: the same line in a wider region scrolls only what is left of them.
        if (!(overflow > 0) || !(speed > 0) || !double.IsFinite(time)) { Home(); return 0; }
        if (!double.IsNaN(_last) && time <= _last) return At(overflow);
        double step = double.IsNaN(_last) ? 0 : Math.Min(time - _last, MaxStep + (_waiting ? RestLeft : 0));
        _last = time; _waiting = false;
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
            else
            {
                _phase = Phase.Start; _offset = 0; _laps++;
                // Its laps done, the line rests at its start; the time left over is not counted.
                if (Rested) { _last = double.NaN; break; }
            }
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
