using Vortice.Direct3D11;

namespace BrimDeck.LiquidGlass;

// Liquid glass while the island holds still (GlassBackdrop's poll): the desktop under it is looked at now and then, and the glass
// drawn again only when what lies behind it changed (DesktopSource's comparison; our own windows' redraws are reported over the
// region, but their pixels are not in the capture, so they never count). How often: PollMs (30 a second) while the pointer is
// over the island and moves or presses (PointerActive); StillPollMs (15 a second) while the desktop behind changed in the last
// IdleAfterMs (a video) and the pointer is away; else IdlePollMs. While the island moves the glass follows it frame by frame
// (Frame), and the desktop is looked at on every frame its shape changes or at PollMs. A pointer resting on the island for
// PointerRestMs changes nothing of the glass, so it is polled as with the pointer away. A comparison sent on one tick is read on
// the next at the same pace: never faster, because while our own island animates (a scrolling title, the equalizer) Windows
// reports the region changed on every frame and a comparison is always out; polling at SettleMs then cost a capture and a
// comparison 125 times a second. After a tick that drew the glass, one tick SettleMs later reads the drawing's measure of the
// backdrop (automatic liquid glass) and looks at nothing (Settling): the next look keeps the pace from the last one. Counts
// what it did, for the debug log and the cost check (WindowsTests). The poll's timer (DispatcherTimer) fires on the system's
// timer tick, 15.6 ms unless something raised its resolution: StillPollMs is 62 so it lands on 4 ticks (62.5 ms, 16 a second);
// 66 waited 5 (78 ms, under 13 a second).
//
// render draws the captured background and says whether it could (false: the renderer is not on the device the background was
// captured on, LiquidHost); nothing counts as drawn then.
internal sealed class LiquidIdle
{
    public const int PollMs = 33, StillPollMs = 62, IdlePollMs = 100, SettleMs = 8, IdleAfterMs = 2000, PointerRestMs = 2000;
    private long _changedAt;
    public long Ticks { get; private set; }
    public long Renders { get; private set; }
    public long FrameRenders { get; private set; }
    // Followed frames drawn over the last capture, only the pointer's light or the press changed (E18).
    public long Reused { get; private set; }
    // Ticks that only read the measure (Settling), for the debug log and the cost check.
    public long Settles { get; private set; }
    // What the swap chain holds (the frame drawn, over which region), and when the desktop was last looked at (following or
    // polled); whether the poll owes a drawing whatever the comparison says (the swap chain holds nothing of it).
    private LiquidFrame _drawn;
    private RECT _drawnOver;
    private bool _holds, _owed = true, _lastChanged = true;
    private long _lookedAt = long.MinValue / 2;
    // A tick drew the glass and its measure is to be read SettleMs later (Interval); that tick is under way (Settling); a settle
    // tick was the last one, so the next look keeps the pace from the look before it.
    private bool _settleOwed, _settled;

    // The island just stopped moving: looked at often for a while, as after a change.
    public void Start(long now) => _changedAt = now;

    // Whether the coming tick only reads the measure of the last drawing (Settled), looking at nothing.
    public bool Settling { get; private set; }

    // The settle tick: nothing looked at, nothing drawn; the caller reads the measure (LiquidRenderer.TryBackdrop).
    public void Settled()
    {
        Settling = false;
        _settled = true;
        Settles++;
    }

    // One look at the desktop at now (milliseconds): the glass drawn (render) only when what lies behind changed, or when the
    // swap chain holds nothing yet. False when nothing could be captured or drawn.
    public bool Tick(DesktopSource source, RECT region, Func<ID3D11Texture2D, bool> render, long now)
    {
        Ticks++;
        Settling = false;
        _lookedAt = now;
        if (!source.Update(region, out bool changed) || source.Texture is not { } background) return false;
        _lastChanged = changed;
        if (changed || _owed)
        {
            if (!render(background)) return false;
            _owed = false;
            _settleOwed = true;
            Renders++;
            _changedAt = now;
        }
        return true;
    }

    // A frame the island is followed through (GlassBackdrop.Step): the glass drawn again only when it changed (its shape,
    // place, look, the pointer's light) or what lies behind it did. While nothing about it changes (following an animation
    // that moves nothing of the glass: the island's own content, the scrolling title, the equalizer), the desktop is looked at
    // no oftener than PollMs and nothing is drawn. False when nothing could be captured or drawn.
    public bool Frame(DesktopSource source, RECT region, in LiquidFrame frame, Func<ID3D11Texture2D, bool> render, long now)
    {
        bool same = _holds && frame == _drawn && region == _drawnOver;
        // E18: the pointer's light, the press, the glass's presence (its fade) and its eased plainness are shading over the same
        // backdrop: drawn again over the last capture at the frame rate, the desktop still looked at no oftener than PollMs - as
        // long as the last look found the desktop still (a video or a window moving behind keeps being looked at every frame).
        bool shading = !same && _holds && region == _drawnOver && !_lastChanged
            && frame with { PointerX = _drawn.PointerX, PointerY = _drawn.PointerY, Hover = _drawn.Hover, Press = _drawn.Press, Presence = _drawn.Presence, Plain = _drawn.Plain } == _drawn;
        bool changed = false, looked = false;
        if ((!same && !shading) || now - _lookedAt >= PollMs)
        {
            looked = true;
            _lookedAt = now;
            if (!source.Update(region, out changed) || source.Texture is null) return false;
            _lastChanged = changed;
        }
        if (same && !changed) return true;
        if (source.Texture is null || !render(source.Texture)) return false;
        if (shading && !looked) Reused++;
        _drawn = frame; _drawnOver = region; _holds = true; _owed = false;
        // Followed, every frame reads the measure: no settle tick owed when the poll takes over.
        _settleOwed = false;
        FrameRenders++;
        return true;
    }

    // The swap chain no longer holds what was drawn (a new renderer, the glass let go or not shown for a while).
    public void Forget() { _holds = false; _owed = true; _lastChanged = true; }

    // The wait before the next tick, and whether that tick only reads the measure (Settling): pointer, whether the pointer is
    // over the island and moves or presses (PointerActive); measuring, whether a measure of the backdrop is out
    // (LiquidRenderer.Measuring). After a settle tick, the pace is kept from the last look.
    public int Interval(bool pointer, bool measuring, long now)
    {
        if (measuring && _settleOwed)
        {
            _settleOwed = false;
            Settling = true;
            return SettleMs;
        }
        _settleOwed = false;
        Settling = false;
        int pace = pointer ? PollMs : now - _changedAt < IdleAfterMs ? StillPollMs : IdlePollMs;
        if (!_settled) return pace;
        _settled = false;
        return (int)Math.Clamp(pace - (now - _lookedAt), 1, pace);
    }

    // Whether the pointer counts for the poll's pace: over the island and pressed, or moved within PointerRestMs.
    public static bool PointerActive(bool over, bool down, long movedAt, long now) => over && (down || now - movedAt < PointerRestMs);
}

// One glass's capture and renderer, kept on one device (GlassBackdrop's liquid glass; the WindowsTests drive it the same way).
// The renderer is made on the source's device, and made again when the source's device was replaced (DesktopSource.Generation:
// another adapter, a lost device). Never inside DesktopSource.Update, where the replacement happens, so no frame or tick still
// holding the old renderer finds it disposed under it: a background captured on the new device is refused by the old renderer
// (the draw says false), the renderer is made again right after Update returned, and the frame drawn with the new one. Made
// (again), Rebuilt is raised: what showed the old renderer's swap chain must take the new one's (CreateBrush). Nothing is
// allocated per frame: the drawing callback is made once and reads the frame from fields.
internal sealed class LiquidHost : IDisposable
{
    public DesktopSource Source { get; }
    public LiquidRenderer? Renderer { get; private set; }
    public LiquidIdle Idle { get; } = new();
    // Renderers made again for a new device, for the debug log and the tests.
    public long Rebuilds { get; private set; }
    public event Action<LiquidRenderer>? Rebuilt;
    private int _generation = -1;
    private int _pad;
    private LiquidFrame _frame;
    private readonly Func<ID3D11Texture2D, bool> _render;

    public LiquidHost(DesktopSource source)
    {
        Source = source;
        _render = Draw;
    }

    // The renderer for the source's device now, made (again) where needed.
    public void Ensure()
    {
        if (Renderer is not null && _generation == Source.Generation) return;
        if (Renderer is not null) Rebuilds++;
        Renderer?.Dispose(); Renderer = null;
        Idle.Forget();
        _generation = Source.Generation;
        Renderer = new LiquidRenderer(Source.Device);
        Rebuilt?.Invoke(Renderer);
    }

    // A followed frame (LiquidIdle.Frame), pad being the background's padding around the island.
    public bool Frame(RECT region, int pad, in LiquidFrame frame, long now)
    {
        Ensure();
        _pad = pad; _frame = frame;
        if (Idle.Frame(Source, region, frame, _render, now)) return true;
        if (_generation == Source.Generation) return false;
        Ensure();
        return Idle.Frame(Source, region, frame, _render, now);
    }

    // A tick of the poll (LiquidIdle.Tick).
    public bool Tick(RECT region, int pad, in LiquidFrame frame, long now)
    {
        Ensure();
        _pad = pad; _frame = frame;
        if (Idle.Tick(Source, region, _render, now)) return true;
        if (_generation == Source.Generation) return false;
        Ensure();
        return Idle.Tick(Source, region, _render, now);
    }

    private bool Draw(ID3D11Texture2D background)
    {
        if (Renderer is not { } renderer || _generation != Source.Generation) return false;
        renderer.Render(background, _pad, _frame);
        return true;
    }

    public void Dispose()
    {
        Renderer?.Dispose(); Renderer = null;
        Source.Dispose();
    }
}
