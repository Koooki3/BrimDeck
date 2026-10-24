namespace BrimDeck.Core;

// The cycle of overflowing quota rings (QuotaCarousel) with animations on, as plain functions of the time within it: rest at
// the first ring for StartHold, glide by Travel on the (.4, 0, .6, 1) curve at Speed DIP a second, rest at the last ring for
// EndHold, then fade out over Fade, jump back to the first ring and fade in over Fade. These are the values of the storyboard
// it replaces (win2 P1): the carousel now waits out the rests on a timer and runs an animation clock only while the rings move.
public readonly record struct QuotaCycle(double Travel)
{
    public const double StartHold = 2.5, EndHold = 2, Speed = 12, Fade = .25;
    public const double CurveX1 = .4, CurveY1 = 0, CurveX2 = .6, CurveY2 = 1;

    public double GlideEnd => StartHold + Travel / Speed;
    public double ResetStart => GlideEnd + EndHold;
    public double Period => ResetStart + 2 * Fade;

    // A time in seconds from any start, within one cycle.
    public double Wrap(double seconds) => Period > 0 ? (seconds % Period + Period) % Period : 0;

    // The rings' shift (0 at the first ring, -Travel at the last) and the strip's opacity at a time within the cycle.
    public double Shift(double at) => at < StartHold ? 0
        : at < GlideEnd ? -Travel * Curves.Bezier(CurveX1, CurveY1, CurveX2, CurveY2, (at - StartHold) / (GlideEnd - StartHold))
        : at < ResetStart + Fade ? -Travel : 0;
    public double Opacity(double at) => at < ResetStart ? 1 : at < ResetStart + Fade ? 1 - (at - ResetStart) / Fade : Math.Min(1, (at - ResetStart - Fade) / Fade);

    // The stretch a time falls in: whether the rings move in it, when it started and when it ends.
    public (bool Moving, double Start, double End) StretchAt(double at)
        => at < StartHold ? (false, 0, StartHold) : at < GlideEnd ? (true, StartHold, GlideEnd) : at < ResetStart ? (false, GlideEnd, ResetStart) : (true, ResetStart, Period);
}
