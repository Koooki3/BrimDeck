namespace BrimDeck.Core;

// Whether the theme's focus accent plays where keyboard focus lands (motion-review C7). Moving through the controls with Tab is
// done many times in a row: only the first move after a pause plays the accent; the moves that follow within Window seconds
// of the one before show the control's static focus frame alone. Times are seconds on one clock.
public sealed class FocusAccentGate
{
    public const double Window = 1;
    private double _last = double.NegativeInfinity;

    // A focus move by the keyboard at now: whether it plays the accent.
    public bool Take(double now)
    {
        bool play = !(now - _last < Window);
        _last = now;
        return play;
    }

    public void Clear() => _last = double.NegativeInfinity;
}
