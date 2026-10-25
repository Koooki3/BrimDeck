namespace BrimDeck.Core;

// Which look automatic liquid glass ("liquid-glass") takes, from the mean brightness of what lies behind the island (luma 0..1,
// sRGB): dark glass with light text over dark content, light glass with dark text over light content, as Apple's glass adapts.
// The text keeps its contrast either way, and the glass stays clear instead of being held to a slab.
// It never flickers: it turns dark only below DarkBelow and light only above LightAbove (between, it stays as it is), the
// brightness must stay past the threshold for Confirm (a cut in a video, a window passing), and after a change it holds for at
// least the hold. A jump of more than BigJump between two measures (a window opened or closed behind the island) needs no
// waiting for Confirm, but still two new measures past the threshold in a row (JumpMeasures), so one odd frame (a flash in a
// video, a measure taken mid-repaint) never turns it. The hold starts at Hold and doubles, up to MaxHold, for every change that
// comes within Restless of the hold ending (a video flashing between black and white would otherwise turn the theme every
// second); a change after a quieter while starts from Hold again. Times are milliseconds on any steady clock.
public sealed class GlassAppearance(bool dark)
{
    public const double DarkBelow = .40, LightAbove = .60;
    public const long Confirm = 150, Hold = 1000, MaxHold = 8000, Restless = 4000;
    public const double BigJump = .45;
    public const int JumpMeasures = 2;

    // The look a first measure, with nothing shown before it, decides: whichever side of the middle of the two thresholds it is.
    public static bool FirstLook(double luma) => luma < (DarkBelow + LightAbove) / 2;

    public bool Dark { get; private set; } = dark;
    // How long the look holds after its last change now.
    public long HoldFor { get; private set; } = Hold;
    // The last change of either kind (the hold runs from it), and the last one this decided (restlessness counts from it).
    private long _changedAt = long.MinValue / 2, _turnedAt = long.MinValue / 2, _pendingSince = -1;
    private double _fed = double.NaN;
    private bool _jumped;
    // New measures past the threshold since the pending change started.
    private int _fresh;

    // A measure at now. Fresh tells a new measure from the last one fed again (the backdrop holding still is fed on every step,
    // so the confirmation can pass without a new frame): only new ones count toward a jump. True when the look changed (Dark
    // tells which).
    public bool Feed(double luma, long now, bool fresh = true)
    {
        if (!double.IsFinite(luma)) return false;
        bool jump = fresh && double.IsFinite(_fed) && Math.Abs(luma - _fed) > BigJump;
        if (fresh) _fed = luma;
        bool wants = Dark ? luma <= LightAbove : luma < DarkBelow;
        if (wants == Dark) { _pendingSince = -1; _jumped = false; _fresh = 0; return false; }
        if (_pendingSince < 0) { _pendingSince = now; _jumped = jump; _fresh = fresh ? 1 : 0; }
        else { _jumped |= jump; if (fresh) _fresh++; }
        bool confirmed = now - _pendingSince >= Confirm || _jumped && _fresh >= JumpMeasures;
        if (!confirmed || now - _changedAt < HoldFor) return false;
        HoldFor = now - _turnedAt < HoldFor + Restless ? Math.Min(HoldFor * 2, MaxHold) : Hold;
        Dark = wants; _changedAt = _turnedAt = now; _pendingSince = -1; _jumped = false; _fresh = 0;
        return true;
    }

    // Set from outside (the Windows app theme before the first measure, a theme turned by someone else): counts as a change for
    // the hold, without lengthening it.
    public void Reset(bool dark, long now)
    {
        Dark = dark; _changedAt = now; _pendingSince = -1; _jumped = false; _fresh = 0;
    }
}
