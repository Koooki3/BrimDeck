using BrimDeck.Core;

namespace BrimDeck.LiquidGlass;

// Automatic liquid glass's look from what lies behind the island, as the live host runs it (GlassBackdrop, on every frame drawn
// and every tick of its poll): the renderer's latest measure of the backdrop (LiquidRenderer.TryBackdrop, which only has a new
// one after a frame was drawn) is kept, and the last one known is fed to the decision on every step, so a backdrop that holds
// still (a window that stays under the island, which draws no new frame) still gets its look once the confirmation has passed.
// The first measure decides at once (instant: shown without a fade).
internal sealed class BackdropLook
{
    private GlassAppearance? _look;
    // The last measure (NaN before the first), and how many have come in.
    public double Luma { get; private set; } = double.NaN;
    public int Measures { get; private set; }
    public bool? Dark => _look?.Dark;

    // One step at now (milliseconds), the look shown now being current. True when the look should change: to Dark, at once
    // when instant.
    public bool Step(LiquidRenderer renderer, long now, bool current, out bool instant)
    {
        instant = false;
        if (renderer.TryBackdrop(out double luma)) { Luma = luma; Measures++; }
        if (double.IsNaN(Luma)) return false;
        if (_look is null)
        {
            bool dark = Luma < (GlassAppearance.DarkBelow + GlassAppearance.LightAbove) / 2;
            _look = new GlassAppearance(dark);
            _look.Reset(dark, now);
            instant = true;
            return dark != current;
        }
        // The theme was turned by someone else meanwhile (its own hours, a reload): the decision starts from what is shown.
        if (_look.Dark != current) _look.Reset(current, now);
        return _look.Feed(Luma, now);
    }

    // Automatic liquid glass is not chosen now: the next time it is, its first measure decides at once again.
    public void Forget() => _look = null;
}
