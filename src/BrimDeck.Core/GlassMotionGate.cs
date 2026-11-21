namespace BrimDeck.Core;

// V3 C4: when liquid glass's ring reads (AcrylicProbe: GDI screen reads on a worker) may run. Each read stretches the display's
// frames on the render/DWM side, and while the island opens, closes or resizes a ring read is for a size the panel is leaving
// (dropped as a mismatch anyway). So none runs while the island moves; one catch-up read is owed for the motion and runs once
// the island has been still for Quiet. A motion that goes on longer than MaxPause (rapid cycles) lets the reads run again at
// their interval, and GlassSampleValidity counts at most MaxPause of a pause as no age. The opening's cover read is not held
// here: it begins before the opening's first frame (GlassOpeningSession).
// Times are milliseconds on any steady clock.
public sealed class GlassMotionGate
{
    // Quiet: no change of the island's size or shape for this long and the motion has settled (a frame or two missed is not a
    // stop). MaxPause: the longest motion the reads wait for.
    public const long Quiet = 120, MaxPause = 1000;

    private long _last, _since;
    private bool _moved, _owed;

    // When the motion seen so far settles (no later move).
    public long SettlesAt => _last + Quiet;

    // The island's size or shape changed at now; true when this starts a motion (the reads pause from now).
    public bool Moved(long now)
    {
        bool starts = !Moving(now);
        if (starts) _since = now;
        _last = now;
        _moved = _owed = true;
        return starts;
    }

    public bool Moving(long now) => _moved && now - _last < Quiet;

    // A ring read may start now: not while the island moves, unless it has moved for MaxPause already.
    public bool MayRead(long now) => !Moving(now) || now - _since >= MaxPause;

    // The motion has settled at now and its catch-up read is due (once per motion).
    public bool CatchUp(long now)
    {
        if (!_owed || Moving(now)) return false;
        _owed = false;
        return true;
    }

    public void Reset() { _moved = _owed = false; }
}
