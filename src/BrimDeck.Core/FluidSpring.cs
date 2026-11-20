namespace BrimDeck.Core;

// A critically damped spring toward a target, solved exactly (no integration step, so any frame rate, a 250 ms hitch or a
// resume from sleep gives the same curve): x(t) = T + (c1 + c2 t) e^(-w t), with c1 = x0 - T and c2 = v0 + w c1.
// Retarget keeps the current position and velocity and only moves the target, so a new goal never restarts from an old origin,
// never resets the speed and never queues behind the motion before it. Stiffness w is in rad/s: the motion is within 5 % of a
// step after about 4.74 / w seconds, within 2 % after about 5.83 / w. At rest (Settled) the value is exactly the target.
// Times are milliseconds on a steady clock; a time earlier than the last one counts as no time passed.
public struct FluidSpring
{
    private double _x0, _v0, _target, _omega;
    private long _t0;
    private bool _moving;

    public readonly double Target => _target;
    public readonly bool Moving => _moving;

    // Settles when within these of the target (the value's own unit, and per second).
    public const double Epsilon = 1e-4, VelocityEpsilon = 1e-3;

    public FluidSpring(double value, long now)
    {
        _x0 = _target = double.IsFinite(value) ? value : 0;
        _v0 = 0; _omega = 1; _t0 = now; _moving = false;
    }

    // Jumps to value at rest (reduced motion, a first frame, an outside change).
    public void Reset(double value, long now)
    {
        if (!double.IsFinite(value)) return;
        _x0 = _target = value; _v0 = 0; _t0 = now; _moving = false;
    }

    public readonly (double Position, double Velocity) Evaluate(long now)
    {
        if (!_moving) return (_target, 0);
        double t = Math.Max(0, now - _t0) / 1000d, w = _omega;
        double c1 = _x0 - _target, c2 = _v0 + w * c1, e = Math.Exp(-w * t);
        double x = _target + (c1 + c2 * t) * e, v = (c2 - w * (c1 + c2 * t)) * e;
        return double.IsFinite(x) && double.IsFinite(v) ? (x, v) : (_target, 0);
    }

    // Moves toward target from wherever it is now, at its current speed. omega: the stiffness for this leg.
    public void Retarget(double target, long now, double omega)
    {
        if (!double.IsFinite(target) || !(omega > 0) || !double.IsFinite(omega)) return;
        var (x, v) = Evaluate(now);
        if (now < _t0) now = _t0;
        _x0 = x; _v0 = v; _t0 = now; _omega = omega; _target = target;
        _moving = Math.Abs(x - target) > Epsilon || Math.Abs(v) > VelocityEpsilon;
        if (!_moving) _x0 = target;
    }

    // The value now; once within the thresholds it comes to rest exactly at the target.
    public double Sample(long now)
    {
        var (x, v) = Evaluate(now);
        if (_moving && Math.Abs(x - _target) <= Epsilon && Math.Abs(v) <= VelocityEpsilon) { Reset(_target, now); return _target; }
        return x;
    }
}
