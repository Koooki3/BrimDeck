using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.LiquidGlass;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace BrimDeck;

// The blur under a glass island or notice (PanelTheme.GlassShown): a window of its own, just below the panel's window and the
// size of it, showing the desktop behind it blurred (Windows.UI.Composition's host backdrop brush, which DWM blurs). The panel's
// window is layered, and Windows can neither blur behind a layered window nor cut a blur to a shape there, so the blur lives in
// this window instead: its content is clipped to the island's body with an antialiased rounded rectangle, and its window region
// (that rectangle) keeps it from taking clicks or showing anywhere else.
//
// The blur follows the island frame by frame while anything about it changes (opening, closing, a page of another size, the
// boot's dot and swell, the power-off, a fade into the indicator), so it is there all through the motion and never steps aside:
// its clip takes the island's shape as drawn (render transforms included) and its opacity the island's. The two windows are
// drawn by different compositors, and the blur's reaches the screen sooner: measured, about two frames before WPF shows the
// same frame of the island (a blur 70 px inside a power-off moving 33 px a frame). So the blur takes the island's shape from
// Lag frames back, and the clip is the part common to that shape and the ones a frame either side of it, so a lag a frame
// longer or shorter still never shows the blur outside the island. Once the island has held still for a few frames it stops following, and nothing here draws: the
// composition content is static, and only DWM's own blur of the desktop behind runs. Where Windows cannot draw it
// (transparency effects off, high contrast, battery saver, a remote session, an older Windows, any failure) there is no blur
// and the glass stays clear.
//
// A theme may hold the blur to a band of brightness (PanelGlass.Held, GlassEffect): its tint can then be thinner over the blur
// than over the bare desktop. Held reports, on every frame the blur is followed, how much of the held blur lies under the
// island's material (0 to 1), so the tint thins with it in the same frame. That lag between the compositors still varies by
// a frame, so while an edge moves fast (the power-off, the start of an opening) the blur may lie a few pixels off it: the
// tint stays whole while the island moves fast and thins as it slows, smoothly, so those pixels never show.
internal sealed class GlassBackdrop : IDisposable
{
    // The shapes the clip keeps inside: the island's last frames, so a frame between the two compositors never shows the blur
    // outside it. Frames held still before following stops.
    private const int Lag = 2, StillFrames = 12;
    // Movement of the island's edges, in device pixels a frame, at which the tint is thin (Slow) and whole (Fast); how much of
    // the way the tint goes toward that each frame.
    private const double Slow = 4, Fast = 24, Ease = .35;
    private double _moving;
    // Frames the tint stays whole after the blur is shown again: the blur reaches the screen Lag frames later, so the tint
    // starts thinning only once it is there.
    private int _arriving;
    private readonly Window _owner;
    // The surface the blur lies under (the island, a notice) and its shape now: corners, shoulder, and how much of its material
    // is there (0 while it is the indicator's color, 1 when it is all glass).
    private readonly FrameworkElement _island;
    private readonly Func<(CornerRadius Corners, double Shoulder, double Material)> _shape;
    private HwndSource? _window;
    private Windows.UI.Composition.ContainerVisual? _root;
    private SpriteVisual? _sprite;
    // The panel's window handle, which never changes once it has one.
    private IntPtr _ownerHandle;
    private CompositionRoundedRectangleGeometry? _clip;
    private DesktopWindowTarget? _target;
    private bool _following, _shown, _disposed;
    private int _still;
    // Following goes on at least until then (Environment.TickCount64): motion that moves no layout, the boot's dot, asks for it.
    private long _followUntil;
    private Frame? _last;
    // The island's last frames, newest last.
    private readonly List<Frame> _recent = [];
    private (int X, int Y, int Width, int Height)? _windowRect;
    private (int Left, int Top, int Right, int Bottom, int Radius)? _region;
    private double _reported = -1;
    // The glass the sprite's brush was made for, and whether that brush holds the blur to its band (the effect may be refused).
    private PanelGlass? _brushFor;
    private bool _brushHolds;

    // How much of the held blur lies under the island's material now (0 to 1): the tint thins to PanelTheme.HeldOpacity by as much.
    // While the surface is aside (SurfaceAside), how much of the island is liquid glass (its material), which the surface leaves
    // to the glass (SurfaceOpacity).
    public event Action<double>? Held;

    // The island as drawn, in device pixels from the panel window's corner: its bounds, corners and shoulder at that size, its
    // opacity (its own and its parents') and its material.
    private readonly record struct Frame(double Left, double Top, double Right, double Bottom, CornerRadius Corners, double Shoulder, double Opacity, double Material);

    public GlassBackdrop(Window owner, IslandBorder island) : this(owner, island, () => (island.CornerRadius, island.ShoulderRadius, island.MaterialOpacity)) { }

    public GlassBackdrop(Window owner, FrameworkElement surface, Func<(CornerRadius Corners, double Shoulder, double Material)> shape)
    {
        _owner = owner; _island = surface; _shape = shape;
        surface.LayoutUpdated += (_, _) => Update();
        owner.IsVisibleChanged += (_, _) => Update();
        owner.Closed += (_, _) => Dispose();
        if (PresentationSource.FromVisual(owner) is HwndSource main) main.AddHook(OwnerMessage); else owner.SourceInitialized += (_, _) => (PresentationSource.FromVisual(owner) as HwndSource)?.AddHook(OwnerMessage);
    }

    // The pointer over the island (DIP, the island's own coordinates; null when away) and whether its button is down: liquid
    // glass brightens and moves its rim light toward it, and lights up inside as it presses; hover and press ease on the frames
    // that follow (GlassFeedback).
    private Point? _pointer;
    private bool _pointerDown;
    // When the pointer last moved over the island: resting there, it changes nothing, and the poll slows down (LiquidIdle).
    private long _pointerMovedAt;
    public void Pointer(Point? at, bool down)
    {
        if (_pointer == at && _pointerDown == down) return;
        _pointer = at; _pointerDown = down;
        _pointerMovedAt = Environment.TickCount64;
        Follow(0);
    }

    // ----- Liquid glass (prototype) -----
    // Under the island, liquid glass themes (LiquidTheme) show true refraction instead of the blur: the desktop under the island,
    // captured without BrimDeck's own windows (DesktopSource), drawn through a slab of thick glass (LiquidRenderer) whose rim lights
    // from the pointer. It takes the blur's place on the same window and follows the same delayed shape; anything it cannot do
    // (no capture, protected content, a lost device) shows the blur instead, and liquid glass is tried again every second while
    // the blur stands in (Recover). BRIMDECK_LIQUID=0 turns it off, for comparing. While liquid glass is in use our windows over
    // the island are left out of Desktop Duplication (CaptureExclusion), so a recording through it does not show the island.
    // The island's surface tint (its alpha now, thickened as the panel opens), set by the island to turn liquid glass on.
    public Func<double>? LiquidTint { get; set; }
    // Whether liquid glass is what is shown now.
    public bool LiquidShown { get; private set; }
    // Whether the island's own surface steps aside (SurfaceOpacity): liquid glass draws its own tint, and the island's, nearly
    // opaque, would cover it. It steps aside with the frame liquid glass is first shown on, and comes back only with a frame
    // that shows the blur in its place, or once glass is not shown at all: a frame showing nothing (the island hidden, faded
    // out, collapsed to nothing) keeps it as it was, so the surface never flashes in between.
    public bool SurfaceAside { get; private set; }

    // What Held reports for a frame: aside, the island's material (all of it glass at 1; 0 while it is the indicator's color),
    // whatever the motion; else the share of the held blur under it, none while the island moves fast or the blur is not held.
    public static double HeldShare(bool aside, bool holds, double material, double moving) => aside ? material : holds ? material * (1 - moving) : 0;

    // The island's surface opacity over the glass, from what Held reported: aside, none where the island is liquid glass (its
    // own color, the indicator's, coming back as the glass goes); else thinned over a held blur by as much of it as lies
    // under the island toward the theme's blur opacity for a tint of this alpha.
    public static double SurfaceOpacity(bool aside, double held, double alpha) => aside ? 1 - held : 1 + held * (PanelTheme.HeldOpacity(alpha) - 1);

    private static readonly bool LiquidOff = Environment.GetEnvironmentVariable("BRIMDECK_LIQUID") == "0";
    // Round 16: the liquid glass themes' body is the system acrylic's recipe over DWM's own blur (GlassEffect.Acrylic) with a
    // light rim drawn over it (the rim visuals below), no capture. The refraction renderer of rounds 5 to 15 (DesktopSource,
    // LiquidRenderer) stays reachable for comparison behind BRIMDECK_LIQUID_REFRACT=1.
    internal static bool Refraction { get; set; } = Environment.GetEnvironmentVariable("BRIMDECK_LIQUID_REFRACT") == "1";
    private bool AcrylicBody => LiquidTint is not null && !LiquidOff && !Refraction && LiquidTheme.Renders;
    // Whether the liquid glass themes' glass is shown now: the acrylic body (its surface stepped aside for it), or the
    // refraction renderer's.
    public bool BodyShown => LiquidShown || SurfaceAside && AcrylicBody;
    private ShapeVisual? _rim;
    private CompositionRoundedRectangleGeometry? _rimHair, _rimLight;
    private CompositionSpriteShape? _hairShape, _lightShape;
    private CompositionColorBrush? _hairBrush;
    private CompositionLinearGradientBrush? _lightBrush;
    private CompositionColorGradientStop? _lightTop, _lightFade;
    private bool? _rimDark;
    // The acrylic body's noise (AcrylicNoise): a grid of tiles sharing one brush, inside the body's clip, made for the window's
    // size and again only when the window grows.
    private Windows.UI.Composition.ContainerVisual? _noise;
    private CompositionSurfaceBrush? _noiseBrush;
    private (int Columns, int Rows) _noiseGrid;
    // The pointer's soft light over the acrylic body (round 16, step 6): a radial glow following the pointer at hover, a little
    // brighter while pressed (GlassFeedback's eased hover and press), inside the body's clip; only its properties change.
    // The notch's shoulders over acrylic (round 16, step 8): the body is the rounded rectangle of the island's body, and each
    // shoulder - the flare from the screen's edge down to the body's side - a square of the same acrylic beside its top corner,
    // masked by a hard-edged radial gradient that leaves the quarter circle under the flare clear.
    private SpriteVisual? _shoulderLeft, _shoulderRight;
    private SpriteVisual? _glow;
    private CompositionRadialGradientBrush? _glowBrush;
    private CompositionColorGradientStop? _glowCentre;
    // The acrylic body's luminosity opacity now (GlassEffect.LuminosityFor) and the probe that sets it (AcrylicProbe): a couple
    // of looks a second at what lies around the island, never in the frame's path. It rises at once when the text needs more
    // and comes down only after the backdrop stayed calmer for ProbeDwellMs (a video's flashes never make it pump); automatic
    // liquid glass's look takes the same reading through GlassAppearance (its prior, hysteresis, dwell and video hold).
    private double _luminosity = GlassEffect.LuminosityCapsule, _luminosityShown = GlassEffect.LuminosityBase;
    private long _lowerSince = -1;
    private bool _riseSeen;
    private DispatcherTimer? _probe;
    private GlassAppearance? _probeLook;
    private const int ProbeMs = 500, ProbeDwellMs = 2500;
    private LiquidHost? _host;
    private SpriteVisual? _liquid;
    private CompositionRoundedRectangleGeometry? _liquidClip;
    // A failure (no capture, the renderer refused): tried again after _retryAt, sooner after one failure than after many. A
    // window Windows will not leave out of the capture (DesktopSource.Exclude) turns liquid glass off for the run.
    private bool _excludeRefused;
    // Liquid glass could not be shown on this window (no capture, a lost device): the blur is shown until a retry succeeds.
    // LiquidUsable tells the app, and LiquidFailed when it happens, so automatic liquid glass, which then has nothing to
    // measure, follows the Windows app theme meanwhile (App.GlassMeasures).
    private bool _liquidFailed { get; set { if (field == value) return; field = value; if (value) LiquidFailed?.Invoke(); } }
    public bool LiquidUsable => !LiquidOff && !_liquidFailed && !_excludeRefused;
    public static event Action? LiquidFailed;
    private int _failures;
    private long _retryAt;
    private const int RetryMs = 1000, RetryMaxMs = 60_000;
    // While liquid glass is wanted but the blur is shown instead (a failure, the lock screen, a UAC prompt, protected content):
    // looked at again every RetryMs (Recover).
    private DispatcherTimer? _retry;
    private LiquidGlass.RECT _tryRegion;
    // Counted for the debug log: tries again, failures.
    private long _retries, _failed;
    // Hover and press, eased toward the pointer's state on the monitor's hover and BrimDeck's press times (GlassFeedback).
    private readonly GlassFeedback _feedback = new(Soft.Hover, Soft.Press, Soft.Quick);
    // How dark the glass's look is now (0 light .. 1 dark): when the theme turns between them (automatic liquid glass), the
    // glass eases across over DarkFadeMs (Soft.InOut), as calmly as WinUI's acrylic tint transition (some 500 ms), and the
    // text turns in one step where both looks' text reads (Crossed).
    private double _dark = -1, _darkFrom, _darkTo;
    private long _darkSince;
    internal const double DarkFadeMs = 500;
    // Automatic liquid glass: whether it is chosen (set by the island), and the look decided from what lies behind the island
    // (BackdropLook, stepped on every frame drawn and every tick of the poll). Look is raised when the look should change, at
    // once (no fade) for the first decision.
    public Func<bool>? AutoLook { get; set; }
    public event Action<bool, bool>? Look;
    // A turn of automatic liquid glass: the glass eases toward the new look first (Lead), and once what it shows over the
    // backdrop reaches the brightness where either look's text reads (LiquidFrame.TextTurn), Crossed is raised and the theme,
    // the text with it, turns at once; the glass goes on easing. The text is never shown twice, and keeps its contrast.
    public event Action? Crossed;
    private bool? _lead;
    private bool _crossed;
    // null: no turn under way (automatic liquid glass no longer chosen), the glass follows the theme again.
    public void Lead(bool? dark)
    {
        _lead = dark; _crossed = false;
        Follow(0);
    }
    private readonly BackdropLook _look = new();
    // BRIMDECK_LIQUID_DEBUG=1: a line in %LOCALAPPDATA%\BrimDeck\liquid.log every DebugEveryMs and at every decision.
    private static readonly bool LiquidDebug = Environment.GetEnvironmentVariable("BRIMDECK_LIQUID_DEBUG") == "1";
    private const long DebugEveryMs = 2000;
    private long _debugAt;
    // While the island holds still, the desktop under it is looked at for a change (a video, a window moving): LiquidIdle.
    private DispatcherTimer? _poll;
    private LiquidFrame _liquidFrame;
    private LiquidGlass.RECT _liquidRegion;
    private int _pad;
    // The poll's tick, made once (the timer keeps it).
    private EventHandler? _tick;
    // Frames followed and times following started, for the debug log.
    private long _steps, _followStarts;

    // Windows can draw the blur now: the glass theme is shown (transparency effects on, no high contrast), battery saver is off,
    // the session is local, and composition started.
    public static bool Available => PanelTheme.GlassShown && !MotionEnvironment.BatterySaver && GetSystemMetrics(RemoteSession) == 0 && Composition.Ready;

    // After anything that can change the island's shape, place or look; cheap when nothing changed.
    public void Update()
    {
        if (_disposed) return;
        if (StandIn is not null) { StandInFrame(); return; }
        if (_following) return;
        if (!Available || !_owner.IsVisible) { Hide(); return; }
        // An island not shown (hidden, collapsed to nothing) has nothing to follow: following it would stop on its first frame,
        // and every layout pass (a scrolling title redraws many a second) would start it again.
        if (Measure() is not { } now) { Hide(); return; }
        if (now == _last && Equals(_brushFor, PanelTheme.Current.Effects.Glass)) return;
        Follow(0);
    }

    // For the offscreen page sheets only (MainWindow.Offscreen.cs): a stand-in for the window, the capture and the renderer,
    // which need a window on screen. Set, every update decides through it whether liquid glass is shown for this frame, and the
    // rest goes as on screen (LiquidShown, SurfaceAside, Held). In the app it is always null.
    internal Func<bool>? StandIn { get; set; }
    // How far the island is open, 0 (collapsed) to 1 (the open panel), following its opening motion; null: never open (a notice).
    internal Func<double>? Openness { get; set; }
    // Where the collapsed island's content row lies (its text and glyphs), in DIPs from the island's top-left; null or empty for
    // none (the open panel, a notice). Behind it the glass calms the backdrop (Liquid.hlsl, the content row).
    internal Func<Rect?>? ContentArea { get; set; }
    // The open panel's text and content regions (island DIPs), where the glass covers as far as the text needs; null: none.
    internal Func<IReadOnlyList<Rect>?>? TextRegions { get; set; }
    private IReadOnlyList<Rect>? _textFor;
    private float[]? _textRects;

    private void StandInFrame()
    {
        if (!PanelTheme.GlassShown) { ShowLiquid(false); SurfaceAside = false; Report(0, always: false); return; }
        bool acrylic = AcrylicBody;
        bool liquid = ShowLiquid(!acrylic && LiquidTint is not null && !LiquidOff && LiquidTheme.Renders && StandIn!());
        Decided(liquid || acrylic, Math.Clamp(_shape().Material, 0, 1));
    }

    // Follows the island frame by frame for at least the given milliseconds, and after that until it holds still: for motion
    // that changes no layout (a render transform, an opacity), which Update does not see.
    public void Follow(double milliseconds)
    {
        if (_disposed) return;
        // Offscreen there is no window to follow: the stand-in decides, as on every update.
        if (StandIn is not null) { StandInFrame(); return; }
        _followUntil = Math.Max(_followUntil, Environment.TickCount64 + (long)Math.Max(0, milliseconds));
        _still = 0;
        if (_following) return;
        _following = true;
        _followStarts++;
        Poll(false);
        System.Windows.Media.CompositionTarget.Rendering += OnRendering;
        Step();
    }

    private void Stop()
    {
        if (!_following) return;
        _following = false;
        System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
        Poll(true);
    }

    private void OnRendering(object? sender, EventArgs e) => Step();

    private void Step()
    {
        _steps++;
        if (_disposed) { Stop(); return; }
        if (!Available || !_owner.IsVisible || Measure() is not { } frame) { Hide(); Stop(); return; }
        bool changed = frame != _last || !Equals(_brushFor, PanelTheme.Current.Effects.Glass);
        bool easing = EaseLiquid();
        if (!changed && !easing && _moving == 0 && ++_still >= StillFrames && Environment.TickCount64 >= _followUntil) { Stop(); return; }
        if (changed) _still = 0;
        _last = frame;
        if (!EnsureWindow()) { Stop(); return; }
        try
        {
            Dress();
            Show(frame);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Composition.Fail(); Hide(); Stop();
        }
    }

    // Hover and press move toward the pointer's state on time, not frames (GlassFeedback); the theme's turn between the looks
    // likewise. True while anything moves.
    private bool EaseLiquid()
    {
        long now = Environment.TickCount64;
        if (_lead == PanelTheme.Current.IsDark) _lead = null;
        double dark = (_lead ?? PanelTheme.Current.IsDark) ? 1 : 0;
        if (_dark < 0 || !Soft.Enabled) _dark = _darkFrom = _darkTo = dark;
        else if (dark != _darkTo && PanelTheme.Generation == LiquidTheme.SnapGeneration) _dark = _darkFrom = _darkTo = dark;
        else if (dark != _darkTo) { _darkFrom = _dark; _darkTo = dark; _darkSince = now; }
        bool turning = _dark != _darkTo;
        if (turning)
        {
            double progress = Math.Clamp((now - _darkSince) / DarkFadeMs, 0, 1);
            _dark = progress >= 1 ? _darkTo : _darkFrom + (_darkTo - _darkFrom) * Soft.InOut.Ease(progress);
        }
        if (_lead is { } lead && !_crossed && LiquidFrame.TextTurns(double.IsNaN(_look.Luma) ? .5 : _look.Luma, _dark, lead, _dark == _darkTo))
        {
            _crossed = true;
            Crossed?.Invoke();
        }
        bool feedback = _feedback.Step(_pointer is not null, _pointerDown, _pointer?.X ?? double.NaN, _pointer?.Y ?? double.NaN, Soft.Enabled, Eased.Now);
        return feedback || turning;
    }

    // The pointer's answer for a glass of this size (device pixels): hover and press, calmed on the open panel
    // (GlassFeedback.Calm), and the light's place in device pixels from the glass's corner (NaN with no light).
    private (float Hover, float Press, float X, float Y) Answer(double width, double height, double open)
    {
        double hover = GlassFeedback.Calm(_feedback.Hover, open), press = GlassFeedback.Calm(_feedback.Press, open);
        double w = _island.ActualWidth, h = _island.ActualHeight;
        if (hover <= 0 && press <= 0 || !double.IsFinite(_feedback.LightX) || !(w > 0) || !(h > 0)) return ((float)hover, (float)press, float.NaN, float.NaN);
        return ((float)hover, (float)press, (float)(_feedback.LightX / w * width), (float)(_feedback.LightY / h * height));
    }

    private Frame? Measure()
    {
        try
        {
            if (!_island.IsVisible || PresentationSource.FromVisual(_island) is null || PresentationSource.FromVisual(_owner) is null) return null;
            double scale = VisualTreeHelper.GetDpi(_island).DpiScaleX;
            var bounds = _island.TransformToAncestor(_owner).TransformBounds(new Rect(_island.RenderSize));
            if (bounds.Width < 1 || bounds.Height < 1) return null;
            // The render transforms (the boot's squash) scale the corners with the shape.
            double squash = Math.Min(bounds.Width / Math.Max(1, _island.ActualWidth), bounds.Height / Math.Max(1, _island.ActualHeight));
            var shape = _shape();
            double opacity = 1;
            for (DependencyObject? node = _island; node is not null && !ReferenceEquals(node, _owner); node = VisualTreeHelper.GetParent(node))
                if (node is UIElement element) opacity *= element.Opacity;
            var c = shape.Corners;
            return new Frame(bounds.Left * scale, bounds.Top * scale, bounds.Right * scale, bounds.Bottom * scale,
                new CornerRadius(c.TopLeft * squash * scale, c.TopRight * squash * scale, c.BottomRight * squash * scale, c.BottomLeft * squash * scale),
                shape.Shoulder * squash * scale, Math.Clamp(opacity, 0, 1), Math.Clamp(shape.Material, 0, 1));
        }
        catch (InvalidOperationException) { return null; }
    }

    // The blur for this frame: the window over the panel's, the clip and region inside the island's recent shapes, the
    // island's opacity.
    private void Show(Frame frame)
    {
        _recent.Add(frame);
        if (_recent.Count > Lag + 2) _recent.RemoveAt(0);
        // The frames Lag back and one either side, as far as there are any; the newest only on the first frame.
        int end = _recent.Count - 1, at = Math.Max(0, end - Lag);
        int first = Math.Max(0, at - 1), last = Math.Min(end, at + 1);
        // The acrylic body (round 16, live check 10-26: opening, the light glass trailed the island by a frame and more, its
        // content showing without glass, then caught up in one frame): the shape a frame back, kept inside the newest one too.
        if (AcrylicBody) { at = Math.Max(0, end - 1); first = at; last = end; }
        var shown = _recent[at];
        double opacity = shown.Opacity * shown.Material;
        double speed = 0;
        if (_recent.Count > 1 && _recent[^2] is var before)
            speed = Math.Max(Math.Max(Math.Abs(frame.Left - before.Left), Math.Abs(frame.Top - before.Top)), Math.Max(Math.Abs(frame.Right - before.Right), Math.Abs(frame.Bottom - before.Bottom)));
        if (!_shown && opacity >= .002) _arriving = Lag + 1;
        if (_arriving > 0) { _arriving--; _moving = 1; }
        else
        {
            double fast = Math.Clamp((speed - Slow) / (Fast - Slow), 0, 1);
            _moving += (fast * fast * (3 - 2 * fast) - _moving) * Ease;
            if (_moving < .002) _moving = 0;
        }
        // Reported once this frame has decided between liquid glass and the blur (SurfaceAside), at the end.
        if (opacity < .002) { Conceal(); Report(frame.Material); return; }
        var handle = _window!.Handle;
        var owner = OwnerHandle;
        GetWindowRect(owner, out var rect);
        var place = (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (!_shown || place != _windowRect)
        {
            // Just below the panel's window: above whatever the panel stands over, never above the panel itself. Shown for
            // the first time, a topmost window goes to the top whatever it was placed after (seen on Windows 11): a second
            // call, z-order only, puts it under the panel.
            SetWindowPos(handle, owner, place.Left, place.Top, Math.Max(1, place.Item3), Math.Max(1, place.Item4), NoActivate | ShowWindow | NoOwnerZOrder);
            if (!_shown) SetWindowPos(handle, owner, 0, 0, 0, 0, NoActivate | NoMove | NoSize | NoOwnerZOrder);
            _windowRect = place; _shown = true;
        }
        // The body common to the recent shapes, a device pixel inside its edge, which the island's own edge and rim cover. A
        // shape square at the top (the open panel, the notch) has its rectangle reach above, so only the bottom corners round.
        // The notch's shoulders, thinner than its corners, stay clear glass.
        double left = double.MinValue, top = double.MinValue, right = double.MaxValue, bottom = double.MaxValue, radius = 0, shoulder = 0;
        bool squareTop = true;
        double topRound = double.MaxValue, bottomRound = 0;
        for (int i = first; i <= last; i++)
        {
            var shape = _recent[i];
            var c = shape.Corners;
            topRound = Math.Min(topRound, Math.Max(c.TopLeft, c.TopRight)); bottomRound = Math.Max(bottomRound, Math.Max(c.BottomLeft, c.BottomRight));
            bool square = c.TopLeft < .5 && c.TopRight < .5;
            squareTop &= square;
            radius = Math.Max(radius, square ? Math.Max(c.BottomLeft, c.BottomRight) : Math.Max(Math.Max(c.TopLeft, c.TopRight), Math.Max(c.BottomLeft, c.BottomRight)));
            double width = shape.Right - shape.Left, height = shape.Bottom - shape.Top;
            shoulder = Math.Max(shoulder, Math.Clamp(shape.Shoulder, 0, Math.Min(width / 4, height / 2)));
            left = Math.Max(left, shape.Left); top = Math.Max(top, shape.Top); right = Math.Min(right, shape.Right); bottom = Math.Min(bottom, shape.Bottom);
        }
        const double inset = 1;
        left += shoulder + inset; right -= shoulder + inset; bottom -= inset;
        double clipTop = squareTop ? top - radius - 2 : top + inset;
        double corner = Math.Max(0, radius - inset);
        if (right - left < 1 || bottom - Math.Max(top, clipTop) < 1) { Conceal(); Report(frame.Material); return; }
        _clip!.Offset = new Vector2((float)left, (float)clipTop);
        _clip.Size = new Vector2((float)(right - left), (float)(bottom - clipTop));
        _clip.CornerRadius = new Vector2((float)corner);
        // Liquid glass covers the whole outline, shoulders too (its shader draws the shape), inside the same recent shapes.
        double outerLeft = left - shoulder - inset, outerRight = right + shoulder + inset, outerBottom = bottom + inset;
        bool acrylic = AcrylicBody;
        bool liquid = !acrylic && Liquid(shown, outerLeft, clipTop, outerRight, outerBottom, radius, opacity);
        if (acrylic)
        {
            // The acrylic body covers the island's whole outline as liquid glass did (its surface steps aside), and the rim
            // lies along that outline.
            if (_brushFor is not null && _rimDark is { } was && was != PanelTheme.Current.IsDark) _brushFor = null;
            // The luminosity layer for the text (the probe's) and the thickness for the island's size (GlassEffect.Shown).
            double shownLuminosity = GlassEffect.Shown(_luminosity, Openness?.Invoke() ?? 0);
            if (shownLuminosity != _luminosityShown) { _luminosityShown = shownLuminosity; _brushFor = null; }
            Dress();
            // The body's sides (the shoulders are not the body: a rectangle as wide as the flare would stand out past the sides);
            // the shoulders beside its top corners.
            double bodyLeft = left - inset, bodyRight = right + inset;
            Shoulders(bodyLeft, bodyRight, top, shoulder, opacity);
            // Its top follows the island's top corners continuously (live check 10-26: expanding, the top edge flickered - the
            // blur's clip jumped from a rounded top at the island's edge to a square one reaching above the screen the frame
            // every recent shape was square, which the island's own surface hid under the blur but nothing hides over acrylic):
            // the rectangle rises above the island's top by as much as its top corners are flatter than its bottom ones, so the
            // rounding it shows at the top is the island's, from the capsule's to none, with no step.
            double round = Math.Max(topRound, bottomRound), raise = bottomRound > topRound ? bottomRound - topRound + 2 * (1 - topRound / Math.Max(bottomRound, 1)) : 0;
            double acrylicTop = top - raise;
            _clip.Offset = new Vector2((float)bodyLeft, (float)acrylicTop);
            _clip.Size = new Vector2((float)Math.Max(0, bodyRight - bodyLeft), (float)Math.Max(0, outerBottom - acrylicTop));
            _clip.CornerRadius = new Vector2((float)round);
            clipTop = acrylicTop;
            Rim(bodyLeft, acrylicTop, bodyRight, outerBottom, round, opacity);
            Noise(opacity);
            Glow(shown, opacity);
            Probe(true);
        }
        else
        {
            if (_rim is not null) _rim.IsVisible = false;
            if (_noise is not null) _noise.IsVisible = false;
            if (_glow is not null) _glow.IsVisible = false;
            if (_shoulderLeft is not null) _shoulderLeft.IsVisible = false;
            if (_shoulderRight is not null) _shoulderRight.IsVisible = false;
        }
        _sprite!.Opacity = liquid ? 0 : (float)opacity;
        Decided(liquid || acrylic, frame.Material);
        if (liquid || acrylic) { left = outerLeft; right = outerRight; bottom = outerBottom; corner = radius; }
        // The window region: the clip's rectangle, rounded alike, so the window takes no clicks and shows nothing outside it.
        // A square top reaches above the window, as the clip does, so the region's rounded top corners fall outside it.
        var region = ((int)Math.Floor(left), (int)Math.Floor(clipTop), (int)Math.Ceiling(right), (int)Math.Ceiling(bottom), (int)Math.Round(corner));
        if (region != _region)
        {
            _region = region;
            var handleRegion = CreateRoundRectRgn(region.Item1, region.Item2, region.Item3 + 1, region.Item4 + 1, 2 * region.Item5, 2 * region.Item5);
            // The window owns the region from here on.
            if (handleRegion != IntPtr.Zero && SetWindowRgn(handle, handleRegion, true) == 0) DeleteObject(handleRegion);
        }
    }

    // Liquid glass for this frame, at the delayed shape, clipped to the recent shapes (window pixels); false shows the blur.
    private bool Liquid(Frame shown, double left, double top, double right, double bottom, double radius, double opacity)
    {
        // Another glass theme (only the liquid glass themes render, LiquidTheme): the blur, and liquid glass's capture goes with it.
        if (!LiquidTheme.Renders) { ReleaseLiquid(); return ShowLiquid(false); }
        if (LiquidTint is not { } tint || LiquidOff || _liquidFailed || _excludeRefused || Composition.Compositor is null || _root is null
            || PanelTheme.Current.Effects.Glass is not { } glass || _windowRect is not { } window)
            return ShowLiquid(false);
        try
        {
            double scale = VisualTreeHelper.GetDpi(_island).DpiScaleX;
            int x = (int)Math.Round(shown.Left), y = (int)Math.Round(shown.Top);
            int width = Math.Max(1, (int)Math.Round(shown.Right - shown.Left)), height = Math.Max(1, (int)Math.Round(shown.Bottom - shown.Top));
            _pad = (int)Math.Ceiling(48 * scale);
            var region = new LiquidGlass.RECT(window.X + x - _pad, window.Y + y - _pad, window.X + x + width + _pad, window.Y + y + height + _pad);
            _tryRegion = region;
            if (_host is null)
            {
                // The capture is made off the UI thread (SourceStart): the blur stands in until it is there, then this follows.
                var source = DesktopSource.Open(OwnerHandle, _window?.Handle ?? IntPtr.Zero, region, _sourceDone ??= SourceDone, out var opening);
                if (source is null)
                {
                    if (opening == DesktopSource.Opening.Refused) _excludeRefused = true;
                    else if (opening == DesktopSource.Opening.Failed) Failed();
                    return ShowLiquid(false);
                }
                _host = new LiquidHost(source);
                _host.Rebuilt += Rebuilt;
            }
            // The glass's own preset (the ported glass, its band for the theme's text); the island's tint is not drawn over it.
            var c = shown.Corners;
            // Between the looks while the theme turns between light and dark glass.
            double darkness = _dark < 0 ? (PanelTheme.Current.IsDark ? 1 : 0) : _dark;
            var preset = LiquidFrame.Blend(LiquidFrame.Preset(false, width, height, (float)c.TopLeft, (float)c.BottomLeft, (float)shown.Shoulder, (float)scale),
                LiquidFrame.Preset(true, width, height, (float)c.TopLeft, (float)c.BottomLeft, (float)shown.Shoulder, (float)scale), (float)darkness);
            // The band is the glass's own (LiquidFrame.TextLight, TextDark: the theme's text kept at 4.5:1 by an affine lift that
            // keeps the backdrop's detail), not the held blur's narrower one, which the blur needs for its own sake.
            // D13: the glass's presence is the island's opacity as it comes in and goes, its material's blend included (the
            // indicator's, a notice's handoff), in steps of 1/64 so a held opacity draws nothing more; its refraction, rim light
            // and edge grow with it.
            var next = Pointered(preset, width, height, scale) with { Presence = (float)(Math.Round(opacity * 64) / 64), Plain = PlainShown() };
            // Drawn again only when the glass changed (its shape, place, look, the press) or what lies behind it did;
            // while nothing about the glass changes (the island following frames for an animation that moves nothing of it),
            // the desktop is looked at no oftener than the idle poll's PollMs.
            if (!_host.Frame(region, _pad, next, Environment.TickCount64)) return ShowLiquid(false);
            _liquidFrame = next; _liquidRegion = region;
            ReportBackdrop();
            _liquid!.Offset = new Vector3(x, y, 0);
            _liquid.Size = new Vector2(width, height);
            _liquidClip!.Offset = new Vector2((float)(left - x), (float)(top - y));
            _liquidClip.Size = new Vector2((float)Math.Max(0, right - left), (float)Math.Max(0, bottom - top));
            _liquidClip.CornerRadius = new Vector2((float)radius);
            _liquid.Opacity = (float)opacity;
            return ShowLiquid(true);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException or SharpGen.Runtime.SharpGenException or DllNotFoundException)
        {
            ReleaseLiquid(); Failed();
            return ShowLiquid(false);
        }
    }

    // The pointer's answer, the opening and the content row over a preset: hover brightens the glass and moves its rim light
    // toward the pointer, a press lights it from inside (both eased, GlassFeedback; only on the collapsed island). The morph
    // stays 0: no pulse while the shape changes. A pointer resting on the glass changes nothing, so nothing is drawn again.
    private LiquidFrame Pointered(in LiquidFrame preset, int width, int height, double scale)
    {
        // How far the panel is open (its frost and the calmer backdrop behind its text, Liquid.hlsl), with its opening.
        double open = Math.Clamp(Openness?.Invoke() ?? 0, 0, 1);
        var (hover, press, x, y) = Answer(width, height, open);
        var next = preset with { Hover = hover, Press = press, PointerX = x, PointerY = y, Open = (float)open };
        // The collapsed island's content row (island DIPs, from ContentArea), in device pixels from the glass's corner.
        // The open panel's text regions, in device pixels from the glass's corner: a new array only when they changed.
        var regions = TextRegions?.Invoke();
        if (!ReferenceEquals(regions, _textFor) || regions is null)
        {
            _textFor = regions;
            _textRects = regions is null ? null : new float[regions.Count * 4];
            for (int i = 0; regions is not null && i < regions.Count; i++)
            {
                _textRects![i * 4] = (float)(regions[i].Left * scale); _textRects[i * 4 + 1] = (float)(regions[i].Top * scale);
                _textRects[i * 4 + 2] = (float)(regions[i].Right * scale); _textRects[i * 4 + 3] = (float)(regions[i].Bottom * scale);
            }
        }
        if (_textRects is not null) next = next with { TextRects = _textRects };
        if (ContentArea?.Invoke() is { IsEmpty: false } row)
            next = next with
            {
                ContentLeft = (float)(row.Left * scale), ContentTop = (float)(row.Top * scale),
                ContentRight = (float)(row.Right * scale), ContentBottom = (float)(row.Bottom * scale)
            };
        return next;
    }

    // For the offscreen checks only (tests/BrimDeck.FxPreview --glass-access): the frame the glass would draw for the island as
    // laid out now (its preset at its size, the pointer's answer eased to now, the opening, the content row), and whether this
    // glass holds a window or a capture of its own (neither while glass is not shown: transparency effects off).
    internal LiquidFrame PreviewFrame()
    {
        EaseLiquid();
        double scale = VisualTreeHelper.GetDpi(_island).DpiScaleX;
        int width = Math.Max(1, (int)Math.Round(_island.ActualWidth * scale)), height = Math.Max(1, (int)Math.Round(_island.ActualHeight * scale));
        var shape = _shape();
        var c = shape.Corners;
        return Pointered(LiquidFrame.Preset(PanelTheme.Current.IsDark, width, height, (float)(c.TopLeft * scale), (float)(c.BottomLeft * scale), (float)(shape.Shoulder * scale), (float)scale),
            width, height, scale);
    }
    internal bool HoldsWindowOrCapture => _window is not null || _host is not null;

    // The capture being made off the UI thread is done: liquid glass is tried again at once.
    private Action? _sourceDone;
    private void SourceDone()
    {
        if (!_disposed && LiquidWanted) Follow(0);
    }

    // A failure: liquid glass is tried again after RetryMs, twice as long after each failure in a row, up to RetryMaxMs.
    private void Failed()
    {
        _liquidFailed = true; _failed++;
        _retryAt = Environment.TickCount64 + Math.Min(RetryMaxMs, RetryMs << Math.Min(_failures++, 6));
    }

    // The renderer made (again, on a new device): the glass shows its swap chain.
    private void Rebuilt(LiquidRenderer renderer)
    {
        if (Composition.Compositor is not { } compositor || _root is null) return;
        _liquid ??= compositor.CreateSpriteVisual();
        _liquidClip ??= compositor.CreateRoundedRectangleGeometry();
        _liquid.Clip ??= compositor.CreateGeometricClip(_liquidClip);
        var previous = _liquid.Brush;
        _liquid.Brush = renderer.CreateBrush(compositor);
        previous?.Dispose();
        if (_liquid.Parent is null) _root.Children.InsertAtTop(_liquid);
    }

    // The last brightness any liquid glass measured behind its island (NaN before one): automatic liquid glass chosen while
    // another glass theme shows takes its first look from it at once (App.StartGlance).
    public static double SeenLuma { get; private set; } = double.NaN;
    private double _plain = double.NaN, _plainTarget;
    private long _plainAt;
    // The plainness as drawn: in steps of 1/64 (-1 before any measure: the shader takes it from the band).
    private float PlainShown() => double.IsNaN(_plain) ? -1 : (float)(Math.Round(_plain * 64) / 64);
    // Each new measure of what lies behind this island (read only): the 2nd and 98th percentiles of its luma (LiquidRenderer.Band).
    // The island's shadow under liquid glass deepens over a busy backdrop and lightens over a plain light one (MainWindow.Shadow).
    public event Action<float, float>? Measured;

    // A step of automatic liquid glass's look: the latest measure of what lies behind, once its answer is in, or the last one.
    // The look shown is the one the glass is turning to, if it is (Lead). Nothing turns while the panel is open or opening
    // (its look is the one of the moment it opened, Apple's rule for large glass) or while the island is pressed, so a turn
    // never runs over those motions; adaptivity resumes once they are over (GlassAppearance's held).
    private void ReportBackdrop()
    {
        if (_host?.Renderer is not { } renderer) return;
        long now = Environment.TickCount64;
        bool auto = AutoLook?.Invoke() == true;
        bool current = _lead ?? PanelTheme.Current.IsDark;
        bool turn = false, instant = false;
        bool measured;
        if (auto)
        {
            bool held = Openness?.Invoke() > .001 || _pointerDown || _feedback.Press > .01;
            _look.AppDark = LiquidTheme.AppDark;
            int measures = _look.Measures;
            turn = _look.Step(renderer, now, current, out instant, _host.Source, held);
            measured = _look.Measures != measures;
            if (!double.IsNaN(_look.Middle)) SeenLuma = _look.Middle;
        }
        else
        {
            _look.Forget();
            measured = renderer.TryBackdrop(out _);
            if (measured && GlassAppearance.RangeMean(renderer.Histogram) is var middle && double.IsFinite(middle)) SeenLuma = middle;
        }
        // A new measure is in: its band (the 2nd and 98th percentiles of the luma behind the island) for the island's shadow.
        if (measured) Measured?.Invoke(renderer.Band.Low, renderer.Band.High);
        // How plain the backdrop is (the open panel's even cover, Liquid.hlsl), eased toward the latest measure's over about
        // DarkFadeMs, independent of the frame rate: a backdrop near the threshold or a low-contrast video does not pump it.
        // With some hysteresis: a new target only once it is a fifth away from the one held, or plainly one end (a plainness
        // drifting in smaller steps keeps the target held until then: harmless, the cover between the two is alike).
        if (measured && LiquidFrame.PlainOf(renderer.Band.Low, renderer.Band.High) is var plain
            && (Math.Abs(plain - _plainTarget) > .2 || plain is 0 or 1)) _plainTarget = plain;
        if (double.IsNaN(_plain)) _plain = _plainTarget;
        else _plain += (_plainTarget - _plain) * (1 - Math.Exp(-Math.Clamp(now - _plainAt, 0, 1000) / (DarkFadeMs / 2)));
        _plainAt = now;
        if (LiquidDebug && (turn || now - _debugAt >= DebugEveryMs))
        {
            _debugAt = now;
            var idle = _host.Idle;
            DebugLine(FormattableString.Invariant($"{DateTime.Now:HH:mm:ss.fff} poll ticks {idle.Ticks} settles {idle.Settles} renders {idle.Renders} following {_following} starts {_followStarts} frames {_steps} drawn {idle.FrameRenders} rebuilds {_host.Rebuilds} failures {_failed} retries {_retries} auto {auto} luma {_look.Luma:0.000} middle {_look.Middle:0.000} smoothed {_look.Smoothed:0.000} video {_look.Video} app {(LiquidTheme.AppDark ? "dark" : "light")} measures {_look.Measures} shown {(current ? "dark" : "light")} decided {(_look.Dark is { } d ? d ? "dark" : "light" : "-")}{(turn ? instant ? " -> turn now" : " -> turn" : "")}"));
        }
        if (turn) Look?.Invoke(!current, instant);
    }

    private static void DebugLine(string line)
    {
        try
        {
            var file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "liquid.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            if (System.IO.File.Exists(file) && new System.IO.FileInfo(file).Length > 1 << 20) System.IO.File.Move(file, file + ".1", true);
            System.IO.File.AppendAllText(file, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }

    private bool ShowLiquid(bool shown)
    {
        bool was = LiquidShown;
        LiquidShown = shown;
        if (shown) { _failures = 0; _retry?.Stop(); }
        else
        {
            if (_liquid is not null) _liquid.Opacity = 0;
            // What the swap chain holds is not shown now, and may be stale when it is again: drawn anew then.
            if (was) _host?.Idle.Forget();
            if (LiquidWanted) Recover();
        }
        return shown;
    }

    // Liquid glass would be shown but the blur is, in its place (a failure, no capture for now).
    private bool LiquidWanted => LiquidTint is not null && !LiquidOff && !_excludeRefused && !_disposed && _shown && !LiquidShown
        && LiquidTheme.Renders && Available;

    // Tries liquid glass again every RetryMs while it is wanted: after a failure once its wait is over, else once the capture
    // answers again (the lock screen or a UAC prompt gone, protected content closed); following the island then shows it.
    private void Recover()
    {
        if (_retry is null)
        {
            _retry = new DispatcherTimer(DispatcherPriority.Background, _owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(RetryMs) };
            _retry.Tick += (_, _) =>
            {
                if (!LiquidWanted) { _retry!.Stop(); return; }
                try
                {
                    if (_liquidFailed) { if (Environment.TickCount64 < _retryAt) return; _liquidFailed = false; }
                    else if (_host is not null && !_tryRegion.IsEmpty && !_host.Source.Update(_tryRegion, out _)) return;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or SharpGen.Runtime.SharpGenException) { return; }
                _retries++;
                Follow(0);
            };
        }
        if (!_retry.IsEnabled) _retry.Start();
    }

    // The island holds still: the desktop under it may still change. Looked at as often as PollInterval says; a change redraws
    // the glass. Our own windows' redraws (the ambient rim light) are reported as changes too, but only cost a comparison.
    private void Poll(bool on)
    {
        if (!on || !LiquidShown || _host is null) { _poll?.Stop(); return; }
        if (_poll is null)
        {
            _poll = new DispatcherTimer(DispatcherPriority.Render, _owner.Dispatcher);
            _tick = (_, _) =>
            {
                if (_following || _disposed || _host is null || !LiquidShown) { _poll!.Stop(); return; }
                try
                {
                    // A settle tick only reads the last drawing's measure (LiquidIdle.Settling); the others look at the desktop.
                    if (_host.Idle.Settling) _host.Idle.Settled();
                    else if (!_host.Tick(_liquidRegion, _pad, _liquidFrame, Environment.TickCount64)) { _poll!.Stop(); Follow(0); return; }
                    ReportBackdrop();
                    // The eased plainness (Liquid.hlsl's even cover) is drawn on the poll too, so it eases while the island holds
                    // still rather than jumping at the next followed frame; the poll keeps at least PollMs's pace until it is there.
                    float plain = PlainShown();
                    if (plain != _liquidFrame.Plain && _host.Frame(_liquidRegion, _pad, _liquidFrame with { Plain = plain }, Environment.TickCount64))
                        _liquidFrame = _liquidFrame with { Plain = plain };
                    var interval = PollInterval();
                    if (Math.Abs(_plain - _plainTarget) > 1 / 128d && interval > TimeSpan.FromMilliseconds(LiquidIdle.PollMs)) interval = TimeSpan.FromMilliseconds(LiquidIdle.PollMs);
                    if (_poll!.Interval != interval) _poll.Interval = interval;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException or SharpGen.Runtime.SharpGenException)
                {
                    _poll!.Stop(); ReleaseLiquid(); Failed(); Follow(0);
                }
            };
            _poll.Tick += _tick;
        }
        _host.Idle.Start(Environment.TickCount64);
        _poll.Interval = PollInterval();
        _poll.Start();
    }

    private TimeSpan PollInterval()
    {
        long now = Environment.TickCount64;
        bool pointer = LiquidIdle.PointerActive(_pointer is not null, _pointerDown, _pointerMovedAt, now);
        return TimeSpan.FromMilliseconds(_host?.Idle.Interval(pointer, _host.Renderer is { Measuring: true }, now) ?? LiquidIdle.IdlePollMs);
    }

    private void ReleaseLiquid()
    {
        _poll?.Stop();
        LiquidShown = false;
        if (_host is not null) { _host.Rebuilt -= Rebuilt; _host.Dispose(); _host = null; }
        if (_liquid is not null) { _liquid.Brush?.Dispose(); _liquid.Clip?.Dispose(); _liquid.Dispose(); _liquid = null; }
        _liquidClip?.Dispose(); _liquidClip = null;
    }

    // A frame decided between liquid glass and the blur: the surface steps aside with liquid glass, and Held tells the island.
    private void Decided(bool liquid, double material)
    {
        SurfaceAside = liquid;
        Report(material);
    }

    // On every frame followed: the island's tint may be changing too (thickening as the panel opens). Always when the surface
    // stepped aside or came back since the last report.
    private bool _reportedAside;
    private void Report(double material, bool always = true)
    {
        double held = HeldShare(SurfaceAside, _brushHolds, material, _moving);
        if (!always && Math.Abs(held - _reported) < .001 && _reportedAside == SurfaceAside) return;
        _reported = held; _reportedAside = SurfaceAside;
        Held?.Invoke(held);
    }

    private IntPtr OwnerHandle => _ownerHandle != IntPtr.Zero ? _ownerHandle : _ownerHandle = new WindowInteropHelper(_owner).Handle;

    // The sprite's brush for the theme's glass: the blur held to its band where the theme asks for it and Windows takes the
    // effect, else the blur as it comes.
    private void Dress()
    {
        var glass = PanelTheme.Current.Effects.Glass;
        if (_sprite is null || Equals(glass, _brushFor) && _sprite.Brush is not null || Composition.Compositor is not { } compositor) return;
        _brushFor = glass;
        _brushHolds = false;
        // The brush made for the theme before is let go: a switch between glass themes would otherwise keep each one.
        var previous = _sprite.Brush;
        _sprite.Brush = null;
        GlassEffect.Release(previous);
        if (AcrylicBody)
            try
            {
                var look = (PanelTheme.Current.IsDark ? GlassEffect.AcrylicDark : GlassEffect.AcrylicLight) with { LuminosityOpacity = _luminosityShown };
                _sprite.Brush = GlassEffect.Acrylic(compositor, look);
                DressShoulder(_shoulderLeft, compositor, look, left: true);
                DressShoulder(_shoulderRight, compositor, look, left: false);
                return;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or InvalidCastException) { }
        if (glass is { Held: true })
            try
            {
                _sprite.Brush = GlassEffect.Brush(compositor, glass.Low, glass.High, glass.Saturation);
                _brushHolds = true;
                return;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or InvalidCastException) { }
        _sprite.Brush = compositor.CreateHostBackdropBrush();
    }

    // ----- Round 16: the rim over the acrylic body (no capture) -----
    // A hairline just inside the outline (about a third of round 14's, round 14b's strength: light on the dark look, dark on the
    // light one) and a Fresnel light along the top edge fading down the sides, as the light from above catches the glass's edge
    // (Lens, liquid-glass-studio: the light's place fixed, the rim brightest where the surface turns toward it). Both are
    // composition shapes in the blur's window, set only when the outline or the look changes.
    private void MakeRim(Compositor compositor, Windows.UI.Composition.ContainerVisual root)
    {
        try
        {
            _rim = compositor.CreateShapeVisual(); _rim.RelativeSizeAdjustment = Vector2.One;
            _rimHair = compositor.CreateRoundedRectangleGeometry(); _rimLight = compositor.CreateRoundedRectangleGeometry();
            _hairBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            _lightBrush = compositor.CreateLinearGradientBrush();
            _lightBrush.StartPoint = new Vector2(0, 0); _lightBrush.EndPoint = new Vector2(0, 1);
            _lightTop = compositor.CreateColorGradientStop(0, Windows.UI.Color.FromArgb(0, 255, 255, 255));
            _lightFade = compositor.CreateColorGradientStop(.5f, Windows.UI.Color.FromArgb(0, 255, 255, 255));
            _lightBrush.ColorStops.Add(_lightTop); _lightBrush.ColorStops.Add(_lightFade);
            _hairShape = compositor.CreateSpriteShape(_rimHair); _hairShape.StrokeBrush = _hairBrush;
            _lightShape = compositor.CreateSpriteShape(_rimLight); _lightShape.StrokeBrush = _lightBrush;
            _rim.Shapes.Add(_hairShape); _rim.Shapes.Add(_lightShape);
            _rim.IsVisible = false;
            root.Children.InsertAtTop(_rim);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException) { _rim = null; }
    }

    private void DressShoulder(SpriteVisual? sprite, Compositor compositor, GlassEffect.AcrylicLook look, bool left)
    {
        if (sprite is null) return;
        if (sprite.Brush is CompositionMaskBrush old) { GlassEffect.Release(old.Source); old.Mask?.Dispose(); old.Dispose(); }
        var mask = compositor.CreateRadialGradientBrush();
        // Relative to the square: clear inside the quarter circle about the corner under the flare, the acrylic outside it.
        mask.EllipseCenter = new Vector2(left ? 0 : 1, 1);
        mask.EllipseRadius = new Vector2(1, 1);
        mask.ColorStops.Add(compositor.CreateColorGradientStop(0, Windows.UI.Color.FromArgb(0, 0, 0, 0)));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(.985f, Windows.UI.Color.FromArgb(0, 0, 0, 0)));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(1, Windows.UI.Color.FromArgb(255, 0, 0, 0)));
        var brush = compositor.CreateMaskBrush();
        brush.Source = GlassEffect.Acrylic(compositor, look);
        brush.Mask = mask;
        sprite.Brush = brush;
    }

    private void Shoulders(double bodyLeft, double bodyRight, double top, double shoulder, double opacity)
    {
        if (_shoulderLeft is null || _shoulderRight is null) return;
        bool shown = shoulder >= 1;
        _shoulderLeft.IsVisible = _shoulderRight.IsVisible = shown;
        if (!shown) return;
        var size = new Vector2((float)shoulder, (float)shoulder);
        _shoulderLeft.Size = _shoulderRight.Size = size;
        _shoulderLeft.Offset = new Vector3((float)(bodyLeft - shoulder), (float)top, 0);
        _shoulderRight.Offset = new Vector3((float)bodyRight, (float)top, 0);
        _shoulderLeft.Opacity = _shoulderRight.Opacity = (float)opacity;
    }

    private void MakeGlow(Compositor compositor, Windows.UI.Composition.ContainerVisual root)
    {
        try
        {
            _glowBrush = compositor.CreateRadialGradientBrush();
            _glowBrush.MappingMode = CompositionMappingMode.Absolute;
            _glowCentre = compositor.CreateColorGradientStop(0, Windows.UI.Color.FromArgb(255, 255, 255, 255));
            _glowBrush.ColorStops.Add(_glowCentre);
            _glowBrush.ColorStops.Add(compositor.CreateColorGradientStop(1, Windows.UI.Color.FromArgb(0, 255, 255, 255)));
            _glow = compositor.CreateSpriteVisual(); _glow.RelativeSizeAdjustment = Vector2.One; _glow.Brush = _glowBrush; _glow.IsVisible = false;
            _glow.Clip = compositor.CreateGeometricClip(_clip);
            root.Children.InsertAtTop(_glow);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException) { _glow = null; }
    }

    // The glow at the pointer: hover and press as GlassFeedback eases them (calmed on the open panel), its place in the glass's
    // device pixels from the island's corner (Answer), some 90 DIPs wide; soft on the dark look, a little more on the light.
    private void Glow(Frame shown, double opacity)
    {
        if (_glow is null || _glowBrush is null || _glowCentre is null) return;
        double width = shown.Right - shown.Left, height = shown.Bottom - shown.Top;
        var (hover, press, x, y) = Answer(width, height, Openness?.Invoke() ?? 0);
        double strength = Math.Clamp(hover * .6 + press * .4, 0, 1);
        if (strength <= .002 || !float.IsFinite(x) || !float.IsFinite(y)) { _glow.IsVisible = false; return; }
        double scale = VisualTreeHelper.GetDpi(_island).DpiScaleX;
        bool dark = PanelTheme.Current.IsDark;
        _glowCentre.Color = Windows.UI.Color.FromArgb((byte)(dark ? 34 : 70), 255, 255, 255);
        _glowBrush.EllipseCenter = new Vector2((float)(shown.Left + x), (float)(shown.Top + y));
        _glowBrush.EllipseRadius = new Vector2((float)(90 * scale));
        _glow.Opacity = (float)(strength * opacity);
        _glow.IsVisible = true;
    }

    private void Noise(double opacity)
    {
        if (_noise is null || _windowRect is not { } window || Composition.Compositor is not { } compositor) return;
        if (_noiseBrush is null)
        {
            if (LiquidGlass.AcrylicNoise.Surface(compositor) is not { } surface) return;
            _noiseBrush = compositor.CreateSurfaceBrush(surface);
            _noiseBrush.Stretch = CompositionStretch.None;
            _noiseBrush.HorizontalAlignmentRatio = 0; _noiseBrush.VerticalAlignmentRatio = 0;
        }
        int tile = LiquidGlass.AcrylicNoise.Tile;
        var grid = ((window.Width + tile - 1) / tile, (window.Height + tile - 1) / tile);
        if (grid.Item1 > _noiseGrid.Columns || grid.Item2 > _noiseGrid.Rows)
        {
            _noise.Children.RemoveAll();
            for (int row = 0; row < grid.Item2; row++)
                for (int column = 0; column < grid.Item1; column++)
                {
                    var sprite = compositor.CreateSpriteVisual();
                    sprite.Size = new Vector2(tile, tile);
                    sprite.Offset = new Vector3(column * tile, row * tile, 0);
                    sprite.Brush = _noiseBrush;
                    _noise.Children.InsertAtTop(sprite);
                }
            _noiseGrid = grid;
        }
        _noise.Opacity = (float)opacity;
        _noise.IsVisible = true;
    }

    private (double, double, double, double, double, double, bool)? _rimFor;
    private void Rim(double left, double top, double right, double bottom, double radius, double opacity)
    {
        if (_rim is null || _rimHair is null || _rimLight is null || _hairShape is null || _lightShape is null) return;
        bool dark = PanelTheme.Current.IsDark;
        // The rim grows with the island's presence, behind its body (round 15's condensation): faint while it fades in.
        _rim.Opacity = (float)(opacity * opacity);
        _rim.IsVisible = true;
        if (_rimFor == (left, top, right, bottom, radius, VisualTreeHelper.GetDpi(_island).DpiScaleX, dark)) return;
        double scale = VisualTreeHelper.GetDpi(_island).DpiScaleX;
        _rimFor = (left, top, right, bottom, radius, scale, dark);
        if (_rimDark != dark)
        {
            _rimDark = dark;
            // The hairline: about a third of round 14's (.05 light, .09 dark of the glass's own edge).
            _hairBrush!.Color = dark ? Windows.UI.Color.FromArgb(26, 255, 255, 255) : Windows.UI.Color.FromArgb(20, 0, 0, 0);
            _lightTop!.Color = Windows.UI.Color.FromArgb((byte)(dark ? 64 : 150), 255, 255, 255);
        }
        // 0.75 pt for the hairline (a device pixel at least), a little over a DIP for the light just inside it.
        float hair = (float)Math.Max(1, .75 * scale), light = (float)(1.25 * scale);
        Set(_rimHair, hair, 0); Set(_rimLight, light, hair);
        _hairShape.StrokeThickness = hair; _lightShape.StrokeThickness = light;

        void Set(CompositionRoundedRectangleGeometry geometry, float stroke, float inset)
        {
            // A stroke is centred on its path: the path lies half a stroke (and the inset) inside the outline.
            float off = stroke / 2 + inset;
            geometry.Offset = new Vector2((float)left + off, (float)top + off);
            geometry.Size = new Vector2((float)Math.Max(0, right - left - 2 * off), (float)Math.Max(0, bottom - top - 2 * off));
            geometry.CornerRadius = new Vector2((float)Math.Max(0, radius - off));
        }
    }

    private void Probe(bool on)
    {
        if (!on) { _probe?.Stop(); return; }
        if (_probe is null)
        {
            _probe = new DispatcherTimer(DispatcherPriority.Background, _owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(ProbeMs) };
            _probe.Tick += (_, _) => ProbeTick();
        }
        if (!_probe.IsEnabled) { _probe.Start(); ProbeTick(); }
    }

    private void ProbeTick()
    {
        if (_disposed || !AcrylicBody || _last is not { } frame || _windowRect is not { } window) { _probe?.Stop(); return; }
        double scale = VisualTreeHelper.GetDpi(_island).DpiScaleX;
        var island = new LiquidGlass.RECT(window.X + (int)frame.Left, window.Y + (int)frame.Top, window.X + (int)frame.Right, window.Y + (int)frame.Bottom);
        if (LiquidGlass.AcrylicProbe.Read(island, (int)(24 * scale), (int)(40 * scale)) is not { } reading) return;
        long now = Environment.TickCount64;
        bool dark = PanelTheme.Current.IsDark;
        double openness = Openness?.Invoke() ?? 0;
        // The island's shadow takes its tone from the same reading (MainWindow.ShadowLook: deeper over a busy backdrop, lighter
        // over a plain light one), as it took it from the refraction renderer's measure.
        Measured?.Invoke((float)reading.Dark, (float)reading.Bright);
        double wanted = GlassEffect.LuminosityFor(dark, reading.BrightFor(openness), reading.DarkFor(openness));
        // Up after two readings in a row ask for it (a second: one flash of a video or a window passing never lifts it), down
        // after the backdrop stayed calmer for ProbeDwellMs.
        if (wanted > _luminosity && _riseSeen) { _luminosity = wanted; _lowerSince = -1; _riseSeen = false; Redress(); }
        else if (wanted > _luminosity) _riseSeen = true;
        else if (wanted < _luminosity)
        {
            _riseSeen = false;
            if (_lowerSince < 0) _lowerSince = now;
            else if (now - _lowerSince >= ProbeDwellMs) { _luminosity = Math.Max(wanted, _luminosity - .05); _lowerSince = now; Redress(); }
        }
        else { _lowerSince = -1; _riseSeen = false; }
        // Automatic liquid glass: its look from the same reading, held while the panel is open or pressed.
        if (AutoLook?.Invoke() == true)
        {
            _probeLook ??= new GlassAppearance(dark, LiquidTheme.AppDark);
            _probeLook.Prior(LiquidTheme.AppDark);
            if (_probeLook.Dark != dark) _probeLook.Reset(dark, now);
            _probeLook.Changed(reading.Changed, now);
            bool held = Openness?.Invoke() > .001 || _pointerDown;
            if (_probeLook.Feed(reading.Mean, now, held)) Look?.Invoke(_probeLook.Dark, false);
            if (double.IsFinite(reading.Mean)) SeenLuma = reading.Mean;
        }
        else _probeLook = null;

        void Redress()
        {
            double shown = GlassEffect.Shown(_luminosity, Openness?.Invoke() ?? 0);
            if (shown == _luminosityShown) return;
            _luminosityShown = shown; _brushFor = null; Dress();
        }
    }

    // No blur now, the window kept for the next time.
    private void Conceal()
    {
        bool shown = _shown;
        _shown = false;
        ShowLiquid(false); Poll(false); Probe(false);
        if (_sprite is not null) _sprite.Opacity = 0;
        if (shown && _window is not null) ShowWindowNative(_window.Handle, Hidden);
    }

    // No blur and nothing remembered: the next frame starts afresh. The island's surface comes back only where glass is not
    // shown at all now (another theme, transparency effects off): an island merely hidden keeps it aside (SurfaceAside).
    private void Hide()
    {
        // Glass not shown at all now (another theme, transparency effects off, battery saver): liquid glass goes, its capture
        // with it, and our windows are back in Desktop Duplication (CaptureExclusion).
        if (!Available) { SurfaceAside = false; ReleaseLiquid(); }
        if (!SurfaceAside) Report(0, always: false);
        Conceal();
        _recent.Clear(); _last = null; _moving = 0; _arriving = 0;
    }

    // The window, its composition target and the blurred sprite, made on first use, where the panel's window is: made
    // elsewhere (another monitor at another scale) and moved over, it would be resized for the change of scale.
    private bool EnsureWindow()
    {
        if (_window is not null) return true;
        if (Composition.Compositor is not { } compositor) return false;
        try
        {
            GetWindowRect(new WindowInteropHelper(_owner).Handle, out var rect);
            var parameters = new HwndSourceParameters("BrimDeck glass")
            {
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                // A tool window that never takes focus or a taskbar button, with no redirection surface: composition draws it.
                ExtendedWindowStyle = 0x80 | 0x08000000 | 0x00200000 | 0x08, // TOOLWINDOW | NOACTIVATE | NOREDIRECTIONBITMAP | TOPMOST
                Width = Math.Max(1, rect.Right - rect.Left), Height = Math.Max(1, rect.Bottom - rect.Top), PositionX = rect.Left, PositionY = rect.Top
            };
            _window = new HwndSource(parameters);
            _window.AddHook(OwnMessage);
            int on = 1;
            DwmSetWindowAttribute(_window.Handle, UseHostBackdropBrush, ref on, sizeof(int));
            _target = Composition.Target(_window.Handle);
            var root = _root = compositor.CreateContainerVisual(); root.RelativeSizeAdjustment = Vector2.One;
            _sprite = compositor.CreateSpriteVisual(); _sprite.RelativeSizeAdjustment = Vector2.One; _sprite.Opacity = 0;
            _clip = compositor.CreateRoundedRectangleGeometry();
            _sprite.Clip = compositor.CreateGeometricClip(_clip);
            root.Children.InsertAtTop(_sprite);
            _noise = compositor.CreateContainerVisual(); _noise.RelativeSizeAdjustment = Vector2.One; _noise.IsVisible = false;
            _noise.Clip = compositor.CreateGeometricClip(_clip);
            root.Children.InsertAtTop(_noise);
            _shoulderLeft = compositor.CreateSpriteVisual(); _shoulderLeft.IsVisible = false; root.Children.InsertAtTop(_shoulderLeft);
            _shoulderRight = compositor.CreateSpriteVisual(); _shoulderRight.IsVisible = false; root.Children.InsertAtTop(_shoulderRight);
            MakeGlow(compositor, root);
            MakeRim(compositor, root);
            _target.Root = root;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or EntryPointNotFoundException or DllNotFoundException)
        {
            Composition.Fail(); Release();
            return false;
        }
    }

    // The window is placed in device pixels and never follows a change of scale: WPF would resize it for the new scale (seen with
    // the blur ending short of a notice on a second monitor at 125 %). It is placed again for the panel as it is now.
    private IntPtr OwnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int DpiChanged = 0x02E0;
        if (message != DpiChanged) return IntPtr.Zero;
        handled = true;
        _windowRect = null;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => Follow(0));
        return IntPtr.Zero;
    }

    // The panel's window moved in the z-order (it is put back on top now and then): the blur goes back just under it; moved or
    // resized, the blur follows.
    private IntPtr OwnerMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WindowPosChanged = 0x0047;
        if (message == WindowPosChanged && _shown && _window is not null)
        {
            SetWindowPos(_window.Handle, hwnd, 0, 0, 0, 0, NoActivate | NoMove | NoSize | NoOwnerZOrder);
            GetWindowRect(hwnd, out var rect);
            if ((rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) != _windowRect) _owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => Follow(0));
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        Release();
    }

    private void Release()
    {
        ReleaseLiquid();
        _retry?.Stop();
        SurfaceAside = false;
        // The composition objects are let go with the window: a panel made again (a monitor added, settings applied) starts afresh.
        if (_sprite is not null) { GlassEffect.Release(_sprite.Brush); _sprite.Clip?.Dispose(); _sprite.Dispose(); }
        foreach (var shoulderSprite in new[] { _shoulderLeft, _shoulderRight }) { if (shoulderSprite?.Brush is CompositionMaskBrush mask) { GlassEffect.Release(mask.Source); mask.Mask?.Dispose(); mask.Dispose(); } shoulderSprite?.Dispose(); }
        _shoulderLeft = _shoulderRight = null;
        _glow?.Clip?.Dispose(); _glow?.Dispose(); _glow = null; _glowBrush?.Dispose(); _glowBrush = null; _glowCentre?.Dispose(); _glowCentre = null;
        _noise?.Clip?.Dispose(); _noise?.Dispose(); _noise = null; _noiseBrush?.Dispose(); _noiseBrush = null; _noiseGrid = default;
        _rim?.Dispose(); _rimHair?.Dispose(); _rimLight?.Dispose(); _hairShape?.Dispose(); _lightShape?.Dispose();
        _hairBrush?.Dispose(); _lightBrush?.Dispose(); _lightTop?.Dispose(); _lightFade?.Dispose();
        _rim = null; _rimHair = _rimLight = null; _hairShape = _lightShape = null; _hairBrush = null; _lightBrush = null; _lightTop = _lightFade = null; _rimDark = null;
        _clip?.Dispose(); _root?.Dispose();
        _target?.Dispose(); _target = null; _sprite = null; _clip = null; _root = null;
        _window?.Dispose(); _window = null;
        _shown = false; _windowRect = null; _region = null; _recent.Clear(); _last = null; _brushFor = null;
    }

    // One compositor for the UI thread, on a dispatcher queue of its own that WPF's message loop pumps. Started on first use;
    // a failure turns the blur off for the rest of the run.
    private static class Composition
    {
        private static Compositor? _compositor;
        private static IntPtr _queue;
        private static bool _failed;
        public static bool Ready => !_failed && (_compositor is not null || Start());
        public static Compositor? Compositor => Ready ? _compositor : null;
        public static void Fail() => _failed = true;

        private static bool Start()
        {
            try
            {
                var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), Type = 2, Apartment = 2 }; // current thread, STA
                if (Windows.System.DispatcherQueue.GetForCurrentThread() is null && CreateDispatcherQueueController(options, out _queue) != 0) { _failed = true; return false; }
                _compositor = new Compositor();
                return true;
            }
            catch (Exception ex) when (ex is COMException or EntryPointNotFoundException or DllNotFoundException or TypeLoadException or InvalidOperationException)
            {
                _failed = true; return false;
            }
        }

        // ICompositorDesktopInterop.CreateDesktopWindowTarget, the first method after IUnknown's, called through its vtable.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateDesktopWindowTarget(IntPtr self, IntPtr window, int topmost, out IntPtr target);
        public static DesktopWindowTarget Target(IntPtr window)
        {
            var unknown = ((IWinRTObject)_compositor!).NativeObject.ThisPtr;
            var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var interop));
            try
            {
                var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(interop), 3 * IntPtr.Size);
                var create = Marshal.GetDelegateForFunctionPointer<CreateDesktopWindowTarget>(method);
                Marshal.ThrowExceptionForHR(create(interop, window, 0, out var target));
                try { return DesktopWindowTarget.FromAbi(target); }
                finally { Marshal.Release(target); }
            }
            finally { Marshal.Release(interop); }
        }
    }

    private const int RemoteSession = 0x1000, UseHostBackdropBrush = 17, Hidden = 0;
    private const uint NoSize = 0x1, NoMove = 0x2, NoActivate = 0x10, ShowWindow = 0x40, NoOwnerZOrder = 0x200;
    [StructLayout(LayoutKind.Sequential)] private struct DispatcherQueueOptions { public int Size, Type, Apartment; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32", EntryPoint = "ShowWindow")] private static extern bool ShowWindowNative(IntPtr window, int command);
    [DllImport("user32")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("gdi32")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr handle);
}
