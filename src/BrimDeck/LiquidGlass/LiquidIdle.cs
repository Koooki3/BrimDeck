using Vortice.Direct3D11;

namespace BrimDeck.LiquidGlass;

// Liquid glass while the island holds still (GlassBackdrop's poll): the desktop under it is looked at now and then, and the glass
// drawn again only when what lies behind it changed (DesktopSource's comparison; our own windows' redraws are reported over the
// region, but their pixels are not in the capture, so they never count). How often: PollMs while the pointer is over the island
// or the desktop changed in the last IdleAfterMs (a video), else IdlePollMs; SettleMs once after a frame was drawn, to read its
// measure of the backdrop (automatic liquid glass). A comparison sent on one tick is read on the next at the same pace: never
// faster, because while our own island animates (a scrolling title, the equalizer) Windows reports the region changed on every
// frame and a comparison is always out; polling at SettleMs then cost a capture and a comparison 125 times a second.
// Counts what it did, for the debug log and the cost check (WindowsTests).
internal sealed class LiquidIdle
{
    public const int PollMs = 33, IdlePollMs = 100, SettleMs = 8, IdleAfterMs = 2000;
    private long _changedAt;
    public long Ticks { get; private set; }
    public long Renders { get; private set; }
    public long FrameRenders { get; private set; }
    // What the swap chain holds (the frame drawn, over which region), and when the desktop was last looked at while following.
    private LiquidFrame _drawn;
    private RECT _drawnOver;
    private bool _holds;
    private long _lookedAt = long.MinValue / 2;

    // The island just stopped moving: looked at often for a while, as after a change.
    public void Start(long now) => _changedAt = now;

    // One look at the desktop at now (milliseconds): the glass drawn (render) only when what lies behind changed. False when
    // nothing could be captured.
    public bool Tick(DesktopSource source, RECT region, Action<ID3D11Texture2D> render, long now)
    {
        Ticks++;
        if (!source.Update(region, out bool changed) || source.Texture is not { } background) return false;
        if (changed)
        {
            render(background);
            Renders++;
            _changedAt = now;
        }
        return true;
    }

    // A frame the island is followed through (GlassBackdrop.Step): the glass drawn again only when it changed (its shape,
    // place, look, the pointer's light) or what lies behind it did. While nothing about it changes (following an animation
    // that moves nothing of the glass: the island's own content, the scrolling title, the equalizer), the desktop is looked at
    // no oftener than PollMs and nothing is drawn. False when nothing could be captured.
    public bool Frame(DesktopSource source, RECT region, in LiquidFrame frame, Action<ID3D11Texture2D> render, long now)
    {
        bool same = _holds && frame == _drawn && region == _drawnOver;
        bool changed = false;
        if (!same || now - _lookedAt >= PollMs)
        {
            _lookedAt = now;
            if (!source.Update(region, out changed) || source.Texture is null) return false;
        }
        if (same && !changed) return true;
        render(source.Texture!);
        _drawn = frame; _drawnOver = region; _holds = true;
        FrameRenders++;
        return true;
    }

    // The swap chain no longer holds what was drawn (a new renderer, the glass let go).
    public void Forget() => _holds = false;

    // The wait before the next tick: pointer, whether the pointer is over the island; measuring, whether a measure of the
    // backdrop is out (LiquidRenderer.Measuring).
    public int Interval(bool pointer, bool measuring, long now)
        => measuring ? SettleMs : pointer || now - _changedAt < IdleAfterMs ? PollMs : IdlePollMs;
}
