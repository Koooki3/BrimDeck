using System.Diagnostics;

namespace BrimDeck.Core;

// A value that eases to each new target over a short move, ftop's columns' "follow" made cheap: a move takes a little under half
// a second and the panel rests between readings. The move is a cubic Hermite curve that starts gently and lands softly; a new
// target that arrives mid-move keeps the speed the value already has toward it, so the motion bends instead of jolting. The
// starting speed stays within what never passes the target, so a bar never overshoots and comes back.
public struct Eased
{
    public const double Seconds = .42, Hover = .16;
    // A move's starting speed as a multiple of its average: 0 is a smoothstep, 3 an ease-out cubic, the most that never overshoots.
    // A change too small to see (a third of a percent; the values are shares, slots or opacities) is taken at once, so an idle
    // core does not keep its graph redrawing.
    private const double Launch = 1.5, Fastest = 3, Unseen = .0035;
    private double _from, _to, _start, _speed, _seconds;
    public readonly double Target => _to;
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public Eased(double value, double seconds = Seconds) { _from = _to = value; _start = _speed = 0; _seconds = seconds; }

    private readonly double Duration => _seconds > 0 ? _seconds : Seconds;

    // Returns whether the value moves.
    public bool Set(double target, bool motion) => Set(target, motion, Now);
    public bool Set(double target, bool motion, double now)
    {
        if (!double.IsFinite(target)) target = 0;
        bool moving = Moving(now);
        if (Math.Abs(target - _to) < 1e-6 && !moving) return false;
        if (!motion) { _from = _to = target; _start = _speed = 0; return false; }
        if (Math.Abs(target - _to) < 1e-6) return true;
        double current = At(now), distance = target - current, d = Duration;
        if (Math.Abs(distance) < Unseen && !moving) { _from = _to = target; _start = _speed = 0; return false; }
        double carried = moving ? Speed(now) * d / distance : 0;
        _speed = Math.Clamp(Math.Max(Launch, carried), 0, Fastest) * distance / d;
        _from = current; _to = target; _start = now;
        return true;
    }

    public readonly double At(double now)
    {
        if (_start <= 0) return _to;
        double s = (now - _start) / Duration;
        if (s >= 1) return _to;
        s = Math.Max(0, s);
        double s2 = s * s, s3 = s2 * s;
        return (2 * s3 - 3 * s2 + 1) * _from + (s3 - 2 * s2 + s) * Duration * _speed + (3 * s2 - 2 * s3) * _to;
    }
    public readonly double Speed(double now)
    {
        if (!Moving(now)) return 0;
        double s = Math.Max(0, (now - _start) / Duration), s2 = s * s, d = Duration;
        return (6 * s2 - 6 * s) * _from / d + (3 * s2 - 4 * s + 1) * _speed + (6 * s - 6 * s2) * _to / d;
    }
    public readonly bool Moving(double now) => _start > 0 && now - _start < Duration;
}

// A spring that trails its target, for what follows the pointer: slightly under-damped, so a fast drag that stops overshoots a
// little and settles back, and slow enough (a period of about 0.4 s) that the motion reads as weight rather than lag.
public struct Spring
{
    private const double Frequency = 16, DampingRatio = .7;
    public double Value, Velocity, Target;

    public Spring(double value) { Value = Target = value; Velocity = 0; }

    // Advances by dt seconds, in steps short enough to stay stable after a stalled frame. Returns whether it still moves.
    public bool Step(double dt)
    {
        dt = Math.Clamp(dt, 0, .1);
        while (dt > 0)
        {
            double h = Math.Min(dt, 1 / 240.0);
            Velocity += (-Frequency * Frequency * (Value - Target) - 2 * DampingRatio * Frequency * Velocity) * h;
            Value += Velocity * h;
            dt -= h;
        }
        if (Math.Abs(Value - Target) < .25 && Math.Abs(Velocity) < 4) { Value = Target; Velocity = 0; return false; }
        return true;
    }

    // Near a limit the target gives way more and more, as a stretched band does, and never passes it.
    public static double Resist(double raw, double min, double max, double zone = 36)
    {
        if (max <= min) return min;
        zone = Math.Max(1e-3, Math.Min(zone, (max - min) / 3));
        if (raw > max - zone) return max - zone + zone * (1 - Math.Exp(-(raw - (max - zone)) / zone));
        if (raw < min + zone) return min + zone - zone * (1 - Math.Exp(-(min + zone - raw) / zone));
        return raw;
    }
}
