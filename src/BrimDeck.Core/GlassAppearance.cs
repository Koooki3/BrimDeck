namespace BrimDeck.Core;

// Which look automatic liquid glass ("liquid-glass") takes, from the mean brightness of what lies behind the island (luma 0..1,
// sRGB): dark glass with light text over dark content, light glass with dark text over light content, as Apple's glass adapts.
// The text keeps its contrast either way, and the glass stays clear instead of being held to a slab.
// It never flickers: it turns dark only below DarkBelow and light only above LightAbove (between, it stays as it is), the
// brightness must stay past the threshold for Confirm (a cut in a video, a window passing), and after a change it holds for at
// least Hold. A jump of more than BigJump between two measures (a window opened or closed behind the island) needs no
// confirmation: the look turns on that measure, the hold still applying. Times are milliseconds on any steady clock.
public sealed class GlassAppearance(bool dark)
{
    public const double DarkBelow = .40, LightAbove = .60;
    public const long Confirm = 150, Hold = 1000;
    public const double BigJump = .45;

    public bool Dark { get; private set; } = dark;
    private long _changedAt = long.MinValue / 2, _pendingSince = -1;
    private double _fed = double.NaN;
    private bool _jumped;

    // A new measure at now. True when the look changed (Dark tells which).
    public bool Feed(double luma, long now)
    {
        if (!double.IsFinite(luma)) return false;
        bool jump = double.IsFinite(_fed) && Math.Abs(luma - _fed) > BigJump;
        _fed = luma;
        bool wants = Dark ? luma <= LightAbove : luma < DarkBelow;
        if (wants == Dark) { _pendingSince = -1; _jumped = false; return false; }
        if (_pendingSince < 0) { _pendingSince = now; _jumped = jump; }
        else _jumped |= jump;
        if (!_jumped && now - _pendingSince < Confirm || now - _changedAt < Hold) return false;
        Dark = wants; _changedAt = now; _pendingSince = -1; _jumped = false;
        return true;
    }

    // Set from outside (the Windows app theme before the first measure): counts as a change for the hold.
    public void Reset(bool dark, long now)
    {
        Dark = dark; _changedAt = now; _pendingSince = -1; _jumped = false;
    }
}
