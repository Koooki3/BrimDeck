namespace BrimDeck.Core;

// Turns the horizontal wheel into single swipes. A precision touchpad reports a two-finger swipe as many small parts and keeps
// sending them while the swipe coasts; a tilting mouse wheel repeats whole notches while it is held. Either way one gesture acts
// once: the parts add up until a whole notch, and the rest of the gesture is passed over until the wheel has been quiet for
// Quiet. Swipes made one after another while the last still coasts are swipes of their own, though: the coasting parts keep the
// wheel from ever being quiet for a second or so, and a swipe made then was lost (one had to wait, or move the pointer, which ends
// the coasting).
// - A swipe the other way: its parts add up by themselves, and a whole notch of at least two of them acts. A single part the other
//   way, as a touchpad sends now and then in the middle of a swipe, does not.
// - A swipe the same way: told from the coasting by the pace of the parts. Recorded on a precision touchpad, the parts come every
//   5 to 12 ms while the fingers move and every 10 to 25 ms as the swipe coasts, so two slow parts in a row are coasting, and three
//   quick ones in a row after that are the fingers moving again. A device whose pace differs gets no more than before: the
//   swipe the same way waits for the quiet.
// A gesture that also scrolls up or down is a scroll, not a swipe, and never acts.
public sealed class HorizontalSwipe
{
    // One notch (WHEEL_DELTA).
    public const int Threshold = 120;
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Touching = TimeSpan.FromMilliseconds(9), Coasting = TimeSpan.FromMilliseconds(13);
    // _acted: the way the gesture last acted, 0 while it has not (or was a scroll). _back, _backParts: the parts the other way
    // since. _slow, _quick: the parts in a row that came slowly or quickly the way it acted; _coasting once it has slowed down.
    private int _rest, _back, _backParts, _acted, _slow, _quick;
    private bool _spent, _coasting;
    private DateTimeOffset _last = DateTimeOffset.MinValue, _vertical = DateTimeOffset.MinValue;

    // A vertical wheel part arrived.
    public void Vertical(DateTimeOffset now) => _vertical = now;

    // Takes one horizontal part (positive: to the right, as WM_MOUSEHWHEEL reports it) and answers 1 for a swipe to the right,
    // -1 for one to the left, or 0 while the gesture has not acted or has already acted.
    public int Feed(int delta, DateTimeOffset now)
    {
        var gap = now - _last;
        if (gap >= Quiet) { _rest = 0; _acted = 0; _spent = false; Restart(); }
        _last = now;
        if (now - _vertical < Quiet) { _spent = true; _acted = 0; }
        if (delta == 0) return 0;
        if (_spent)
        {
            if (_acted == 0) return 0;
            if (Math.Sign(delta) == _acted)
            {
                // A flick of the fingers the other way as they lift, between such parts, never adds up to a notch.
                _back = 0; _backParts = 0;
                _slow = gap >= Coasting ? _slow + 1 : 0;
                _quick = gap <= Touching ? _quick + 1 : 0;
                if (_slow >= 2) _coasting = true;
                if (!_coasting || _quick < 3) return 0;
                Restart();
                return _acted;
            }
            _back += delta; _backParts++;
            if (_backParts < 2 || Math.Abs(_back) < Threshold) return 0;
            _acted = -_acted; Restart();
            return _acted;
        }
        if (Math.Sign(delta) != Math.Sign(_rest)) _rest = 0;
        _rest += delta;
        if (Math.Abs(_rest) < Threshold) return 0;
        int direction = Math.Sign(_rest);
        _rest = 0; _spent = true; _acted = direction; Restart();
        return direction;
    }

    private void Restart() { _back = 0; _backParts = 0; _slow = 0; _quick = 0; _coasting = false; }
}
