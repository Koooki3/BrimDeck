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
    // glass lights its rim from it and presses with it. The light eases toward it on the frames that follow.
    private Point? _pointer;
    private bool _pointerDown;
    public void Pointer(Point? at, bool down)
    {
        if (_pointer == at && _pointerDown == down) return;
        _pointer = at; _pointerDown = down;
        Follow(0);
    }

    // ----- Liquid glass (prototype) -----
    // Under the island, glass themes can show true refraction instead of the blur: the desktop under the island, captured
    // without BrimDeck's own windows (DesktopSource), drawn through a slab of thick glass (LiquidRenderer) whose rim lights
    // from the pointer. It takes the blur's place on the same window and follows the same delayed shape; anything it cannot do
    // (no capture, protected content, a lost device) shows the blur instead. BRIMDECK_LIQUID=0 turns it off, for comparing.
    // The island's surface tint (its alpha now, thickened as the panel opens), set by the island to turn liquid glass on.
    public Func<double>? LiquidTint { get; set; }
    // Whether liquid glass is what is shown now: the island's own tint then steps aside, as the glass draws it.
    public bool LiquidShown { get; private set; }
    private static readonly bool LiquidOff = Environment.GetEnvironmentVariable("BRIMDECK_LIQUID") == "0";
    private DesktopSource? _source;
    private LiquidRenderer? _renderer;
    private SpriteVisual? _liquid;
    private CompositionRoundedRectangleGeometry? _liquidClip;
    private bool _liquidFailed;
    // Hover and press, eased toward the pointer's state; the time they were last eased.
    private double _hover, _press;
    private long _easedAt;
    // How dark the glass's look is now (0 light .. 1 dark): when the theme turns between them (automatic liquid glass), the
    // glass eases across over the theme's own fade (MainWindow.ThemeFadeMs, Soft.InOut) while the old text fades out over it.
    private double _dark = -1, _darkFrom, _darkTo;
    private long _darkSince;
    private const double DarkFadeMs = 320;
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
    public void Lead(bool dark)
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
    // How often, and what a tick does: LiquidIdle.
    private readonly LiquidIdle _idle = new();
    // Frames followed and times following started, for the debug log.
    private long _steps, _followStarts;
    private const double HoverInMs = 120, HoverOutMs = 260, PressDownMs = 60, PressUpMs = 220;

    // Windows can draw the blur now: the glass theme is shown (transparency effects on, no high contrast), battery saver is off,
    // the session is local, and composition started.
    public static bool Available => PanelTheme.GlassShown && !MotionEnvironment.BatterySaver && GetSystemMetrics(RemoteSession) == 0 && Composition.Ready;

    // After anything that can change the island's shape, place or look; cheap when nothing changed.
    public void Update()
    {
        if (_disposed || _following) return;
        if (!Available || !_owner.IsVisible) { Hide(); return; }
        if (Measure() is { } now && now == _last && Equals(_brushFor, PanelTheme.Current.Effects.Glass)) return;
        Follow(0);
    }

    // Follows the island frame by frame for at least the given milliseconds, and after that until it holds still: for motion
    // that changes no layout (a render transform, an opacity), which Update does not see.
    public void Follow(double milliseconds)
    {
        if (_disposed) return;
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

    // Hover and press move toward the pointer's state, quickly in and more slowly out, on time not frames. True while moving.
    private bool EaseLiquid()
    {
        long now = Environment.TickCount64;
        double dt = _easedAt == 0 ? 16 : Math.Clamp(now - _easedAt, 0, 100);
        _easedAt = now;
        double hover = _pointer is null ? 0 : 1, press = _pointerDown ? 1 : 0;
        if (_lead == PanelTheme.Current.IsDark) _lead = null;
        double dark = (_lead ?? PanelTheme.Current.IsDark) ? 1 : 0;
        if (_dark < 0 || !Soft.Enabled) _dark = _darkFrom = _darkTo = dark;
        else if (dark != _darkTo && PanelTheme.Generation == PanelTheme.SnapGeneration) _dark = _darkFrom = _darkTo = dark;
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
        if (!Soft.Enabled) { _hover = hover; _press = press; return false; }
        _hover += (hover - _hover) * (1 - Math.Exp(-dt / (hover > _hover ? HoverInMs : HoverOutMs) * 3));
        _press += (press - _press) * (1 - Math.Exp(-dt / (press > _press ? PressDownMs : PressUpMs) * 3));
        if (Math.Abs(_hover - hover) < .003) _hover = hover;
        if (Math.Abs(_press - press) < .003) _press = press;
        return _hover != hover || _press != press || turning;
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
        Report(_brushHolds ? frame.Material * (1 - _moving) : 0);
        if (opacity < .002) { Conceal(); return; }
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
        for (int i = first; i <= last; i++)
        {
            var shape = _recent[i];
            var c = shape.Corners;
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
        if (right - left < 1 || bottom - Math.Max(top, clipTop) < 1) { Conceal(); return; }
        _clip!.Offset = new Vector2((float)left, (float)clipTop);
        _clip.Size = new Vector2((float)(right - left), (float)(bottom - clipTop));
        _clip.CornerRadius = new Vector2((float)corner);
        // Liquid glass covers the whole outline, shoulders too (its shader draws the shape), inside the same recent shapes.
        double outerLeft = left - shoulder - inset, outerRight = right + shoulder + inset, outerBottom = bottom + inset;
        bool liquid = Liquid(shown, outerLeft, clipTop, outerRight, outerBottom, radius, opacity);
        _sprite!.Opacity = liquid ? 0 : (float)opacity;
        if (liquid) { left = outerLeft; right = outerRight; bottom = outerBottom; corner = radius; }
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
        if (LiquidTint is not { } tint || LiquidOff || _liquidFailed || Composition.Compositor is not { } compositor || _root is null
            || PanelTheme.Current.Effects.Glass is not { } glass || _windowRect is not { } window)
            return ShowLiquid(false);
        try
        {
            if (_source is null)
            {
                _source = DesktopSource.TryCreate();
                if (_source is null) { _liquidFailed = true; return ShowLiquid(false); }
                _source.DeviceChanged += Rebuild;
                DesktopSource.Exclude(OwnerHandle);
                if (_window is not null) DesktopSource.Exclude(_window.Handle);
            }
            if (_renderer is null)
            {
                _renderer = new LiquidRenderer(_source.Device); _idle.Forget();
                _liquid ??= compositor.CreateSpriteVisual();
                _liquidClip ??= compositor.CreateRoundedRectangleGeometry();
                _liquid.Clip ??= compositor.CreateGeometricClip(_liquidClip);
                var previous = _liquid.Brush;
                _liquid.Brush = _renderer.CreateBrush(compositor);
                previous?.Dispose();
                if (_liquid.Parent is null) _root.Children.InsertAtTop(_liquid);
            }
            double scale = VisualTreeHelper.GetDpi(_island).DpiScaleX;
            int x = (int)Math.Round(shown.Left), y = (int)Math.Round(shown.Top);
            int width = Math.Max(1, (int)Math.Round(shown.Right - shown.Left)), height = Math.Max(1, (int)Math.Round(shown.Bottom - shown.Top));
            _pad = (int)Math.Ceiling(48 * scale);
            var region = new LiquidGlass.RECT(window.X + x - _pad, window.Y + y - _pad, window.X + x + width + _pad, window.Y + y + height + _pad);
            // The glass's own preset: clear glass held to the theme's band only where the background lies outside it (the
            // band is the one the theme's text was fitted to, never wider). The island's tint thickening as the panel opens
            // says how open it is, which takes a little more frost; the tint itself is not drawn over the glass.
            double alpha = tint(), whole = glass.Tint.A / 255d;
            float open = whole < 1 ? (float)Math.Clamp((alpha - whole) / ((1 - whole) * .35), 0, 1) : 0;
            var c = shown.Corners;
            // Between the looks while the theme turns between light and dark glass; the theme's band only once there.
            double darkness = _dark < 0 ? (PanelTheme.Current.IsDark ? 1 : 0) : _dark;
            var preset = LiquidFrame.Blend(LiquidFrame.Preset(false, width, height, (float)c.TopLeft, (float)c.BottomLeft, (float)shown.Shoulder, (float)scale),
                LiquidFrame.Preset(true, width, height, (float)c.TopLeft, (float)c.BottomLeft, (float)shown.Shoulder, (float)scale), (float)darkness);
            bool settled = darkness is 0 or 1;
            var pointer = _pointer;
            var next = preset with
            {
                BandLow = glass.Held && settled ? MathF.Max(preset.BandLow, (float)glass.Low) : preset.BandLow,
                BandHigh = glass.Held && settled ? MathF.Min(preset.BandHigh, (float)glass.High) : preset.BandHigh,
                Open = open,
                PointerX = pointer is { } p ? (float)(p.X * scale) : float.NaN, PointerY = pointer is { } q ? (float)(q.Y * scale) : float.NaN,
                Hover = (float)_hover, Press = (float)_press, Morph = (float)_moving
            };
            // Drawn again only when the glass changed (its shape, place, look, the pointer's light) or what lies behind it did;
            // while nothing about the glass changes (the island following frames for an animation that moves nothing of it),
            // the desktop is looked at no oftener than the idle poll's PollMs.
            var renderer = _renderer;
            int pad = _pad;
            if (!_idle.Frame(_source, region, next, background => renderer.Render(background, pad, next), Environment.TickCount64)) return ShowLiquid(false);
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
            _liquidFailed = true; ReleaseLiquid();
            return ShowLiquid(false);
        }
    }

    // A step of automatic liquid glass's look: the latest measure of what lies behind, once its answer is in, or the last one.
    private void ReportBackdrop()
    {
        if (_renderer is null) return;
        long now = Environment.TickCount64;
        bool auto = AutoLook?.Invoke() == true;
        bool current = PanelTheme.Current.IsDark;
        bool turn = false, instant = false;
        if (auto) turn = _look.Step(_renderer, now, current, out instant);
        else { _look.Forget(); _renderer.TryBackdrop(out _); }
        if (LiquidDebug && (turn || now - _debugAt >= DebugEveryMs))
        {
            _debugAt = now;
            DebugLine(FormattableString.Invariant($"{DateTime.Now:HH:mm:ss.fff} poll ticks {_idle.Ticks} renders {_idle.Renders} following {_following} starts {_followStarts} frames {_steps} drawn {_idle.FrameRenders} auto {auto} luma {_look.Luma:0.000} measures {_look.Measures} shown {(current ? "dark" : "light")} decided {(_look.Dark is { } d ? d ? "dark" : "light" : "-")}{(turn ? instant ? " -> turn now" : " -> turn" : "")}"));
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
        if (!shown && _liquid is not null) _liquid.Opacity = 0;
        LiquidShown = shown;
        return shown;
    }

    // A new device (another adapter, a lost one): the renderer is made again on the next frame.
    private void Rebuild()
    {
        _idle.Forget();
        _renderer?.Dispose(); _renderer = null;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => Follow(0));
    }

    // The island holds still: the desktop under it may still change. Looked at as often as PollInterval says; a change redraws
    // the glass. Our own windows' redraws (the ambient rim light) are reported as changes too, but only cost a comparison.
    private void Poll(bool on)
    {
        if (!on || !LiquidShown) { _poll?.Stop(); return; }
        if (_poll is null)
        {
            _poll = new DispatcherTimer(DispatcherPriority.Render, _owner.Dispatcher);
            _poll.Tick += (_, _) =>
            {
                if (_following || _disposed || _source is null || _renderer is null || !LiquidShown) { _poll!.Stop(); return; }
                try
                {
                    if (!_idle.Tick(_source, _liquidRegion, background => _renderer.Render(background, _pad, _liquidFrame), Environment.TickCount64)) { _poll!.Stop(); Follow(0); return; }
                    ReportBackdrop();
                    var interval = PollInterval();
                    if (_poll!.Interval != interval) _poll.Interval = interval;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or SharpGen.Runtime.SharpGenException)
                {
                    _poll!.Stop(); _liquidFailed = true; ReleaseLiquid(); Follow(0);
                }
            };
        }
        _idle.Start(Environment.TickCount64);
        _poll.Interval = PollInterval();
        _poll.Start();
    }

    private TimeSpan PollInterval() => TimeSpan.FromMilliseconds(_idle.Interval(_pointer is not null, _renderer is { Measuring: true }, Environment.TickCount64));

    private void ReleaseLiquid()
    {
        _idle.Forget();
        _poll?.Stop();
        LiquidShown = false;
        _renderer?.Dispose(); _renderer = null;
        if (_source is not null) { _source.DeviceChanged -= Rebuild; _source.Dispose(); _source = null; }
        if (_liquid is not null) { _liquid.Brush?.Dispose(); _liquid.Clip?.Dispose(); _liquid.Dispose(); _liquid = null; }
        _liquidClip?.Dispose(); _liquidClip = null;
    }

    // On every frame followed: the island's tint may be changing too (thickening as the panel opens).
    private void Report(double held, bool always = true)
    {
        if (!always && Math.Abs(held - _reported) < .001) return;
        _reported = held;
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

    // No blur now, the window kept for the next time.
    private void Conceal()
    {
        ShowLiquid(false); Poll(false);
        if (_sprite is not null) _sprite.Opacity = 0;
        if (_shown && _window is not null) ShowWindowNative(_window.Handle, Hidden);
        _shown = false;
    }

    // No blur and nothing remembered: the next frame starts afresh.
    private void Hide()
    {
        Report(0, always: false);
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
        // The composition objects are let go with the window: a panel made again (a monitor added, settings applied) starts afresh.
        if (_sprite is not null) { GlassEffect.Release(_sprite.Brush); _sprite.Clip?.Dispose(); _sprite.Dispose(); }
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
