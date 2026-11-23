using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using BrimDeck.Core;

namespace BrimDeck;

// The DWM glass layer has its own non-activating window, placed behind the WPF content.
internal sealed class GlassSurface : IDisposable
{
    private HwndSource? _window;
    private DesktopWindowTarget? _target;
    // The glass: a sprite clipped to the outline, in a container that carries its place in the window. The clip is the sprite's
    // own (a clip on the container does not reach the backdrop): a rounded rectangle, or the outline's path (shoulders).
    private ContainerVisual? _frame;
    private SpriteVisual? _sprite;
    private CompositionRoundedRectangleGeometry? _outline;
    private CompositionGeometricClip? _roundedClip, _pathClip;
    private CompositionPathGeometry? _pathGeometry;
    // Where the window is (_placed), where the outline asks to be (_need, the bounds of the latest Show), the outline's clip
    // within _need and the clip as set on the geometry; or the outline's shape and place for a path, and the shape the path
    // geometry now holds.
    private Int32Rect _placed, _need;
    private GlassBoundaryMath.GlassClip _clip, _shown;
    private Outline? _shape;
    private GlassBoundaryMath.Layout? _pathLayout;
    private IntPtr _owner;

    // An outline drawn as a path: its layout (GlassBoundaryMath.Measure, in the island's own units) and where those units fall in
    // the bounds' pixels (x' = x M11 + y M21 + M31, y' = x M12 + y M22 + M32), a fraction of a pixel kept.
    internal readonly record struct Outline(GlassBoundaryMath.Layout Layout, Matrix3x2 Place);
    // The material asked for: each look's luminosity opacity and the light look's share (GlassEffect.Graph's weights).
    // Tint and Saturation: the clarity's (GlassClarityPolicy), the same for both looks.
    internal readonly record struct Material(double DarkVeil, double LightVeil, double Palette,
        double Tint = GlassRecipe.DefaultTint, double Saturation = GlassRecipe.DefaultSaturation);
    // What draws the glass: the persistent graph, the stepped fallback, an opaque ground (while the graph loads, or where
    // neither effect can be used), or nothing (no compositor).
    internal enum Backend { None, Opaque, Fallback, Graph }
    // The material as the backend draws it, for the readability guard: the request on the graph, the stepped weights (all five)
    // on the fallback, and on the opaque ground the thickest veils (a grey backdrop under a full veil shows the look's own colour,
    // which is what the ground is painted).
    internal readonly record struct AppliedMaterial(Backend Backend, Material Material);

    private Material? _requested;
    private AppliedMaterial _applied;
    private CompositionEffectBrush? _graph;
    private bool _graphReady;
    private long _graphSince;
    private CompositionBrush? _fallback;
    private GlassRecipe.Look? _fallbackLook;
    private Material _fallbackShown;
    private CompositionColorBrush? _ground;
    private DispatcherTimer? _poll;
    private int _polls;
    // The fallback step asked for that is not shown yet (loading, or its device backing off) and the reads it had; the graph's
    // next try (GlassEffect.RetryAt) the timer was set for and how often.
    private GlassRecipe.Look? _pendingLook;
    private int _stepPolls, _retryWakes;
    private long _wokenFor;
    private bool _visible, _disposed;
    internal bool Visible => _visible;
    internal AppliedMaterial Applied => _visible ? _applied : default;
    // Diagnostics and tests: the brush the glass draws with now, and the clip it is cut by.
    internal CompositionBrush? Brush => _sprite?.Brush;
    internal CompositionClip? Clip => _sprite?.Clip;
    // Diagnostics and tests: whether the surface's own timer will look again (something it waits for is pending).
    internal bool Waking => _poll is { IsEnabled: true };

    // How long a loading graph may stay Pending (a factory loads while the UI thread pumps, about 70 ms here), and when the
    // surface looks again without a frame asking: four reads, the last past the limit.
    private const int LoadingLimitMs = 2000;
    private static readonly int[] PollMs = [100, 300, 1000, 1000];

    // Sets the material only (every frame of a material motion): the persistent graph's weights, nothing compiled, no window
    // touched. False where the surface is not shown or there is no compositor; applied is what the glass shows.
    public bool Present(Material material, out AppliedMaterial applied)
    {
        applied = default;
        if (_disposed || _sprite is null || !_visible) return false;
        try
        {
            applied = Apply(material);
            return applied.Backend != Backend.None;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException) { Dispose(); return false; }
    }

    private AppliedMaterial Apply(Material material)
    {
        if (GlassComposition.Compositor is not { } compositor) return default;
        if (_graphReady && _applied.Backend == Backend.Graph && _requested == material) return _applied;
        _requested = material;
        var sprite = _sprite!;
        // The graph is tried again after a fallback once its backoff passed or the generation changed (GlassEffect decides).
        if (_graph is null && GlassEffect.CanTryGraph(compositor) && (_graph = GlassEffect.Graph(compositor)) is not null)
        {
            _graphReady = false;
            _graphSince = GlassEffect.Clock();
            _polls = 0;
            StopPoll();
        }
        if (_graph is { } graph)
        {
            if (!_graphReady)
            {
                var status = GlassEffect.StatusOf(graph);
                if (status == CompositionEffectFactoryLoadStatus.Success) _graphReady = true;
                else if (status != CompositionEffectFactoryLoadStatus.Pending || GlassEffect.Clock() - _graphSince >= LoadingLimitMs)
                    DropGraph(compositor, material, permanent: status != CompositionEffectFactoryLoadStatus.Pending);
            }
            if (_graphReady)
            {
                try
                {
                    GlassEffect.Present(graph, material.DarkVeil, material.LightVeil, material.Palette, material.Tint, material.Saturation);
                    // Only a brush that presented has worked: a load alone ends no backoff.
                    if (!ReferenceEquals(sprite.Brush, graph)) { sprite.Brush = graph; GlassEffect.Succeeded(); }
                    ReleaseFallback();
                    return _applied = new(Backend.Graph, material);
                }
                catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    DropGraph(compositor, material, permanent: false);
                }
            }
        }
        // A safe ground while the graph loads, backs off or is refused: the stepped fallback (only compiled while no graph is
        // loading), else an opaque colour.
        if (!Fallback(compositor, sprite, material, compile: _graph is null))
        {
            sprite.Brush = Ground(compositor, material);
            _applied = new(Backend.Opaque, new(1, 1, material.Palette));
        }
        // What is not shown yet (a loading graph or step, the graph's next try) is looked at again without a frame asking.
        Wake(compositor);
        return _applied;
    }

    private bool Fallback(Compositor compositor, SpriteVisual sprite, Material material, bool compile)
    {
        var look = GlassEffect.StepLook(material.DarkVeil, material.LightVeil, material.Palette, material.Tint, material.Saturation);
        if (_fallback is null || _fallbackLook != look)
        {
            if (compile && GlassEffect.FallbackAvailable(compositor) && GlassEffect.Acrylic(compositor, look) is { } brush)
            {
                var previous = _fallback;
                sprite.Brush = brush;
                GlassEffect.Release(previous);
                _fallback = brush;
                _fallbackLook = look;
                var (dark, light, palette, tint, saturation) = GlassEffect.StepMaterial(material.DarkVeil, material.LightVeil, material.Palette,
                    material.Tint, material.Saturation);
                _fallbackShown = new(dark, light, palette, tint, saturation);
            }
        }
        // The new step is not to be had now (loading, refused, a graph loading): the step shown stays, reported as it is, and the
        // step asked for is looked at again (Wake).
        GlassRecipe.Look? pending = _fallbackLook == look ? null : look;
        if (pending != _pendingLook)
        {
            _pendingLook = pending;
            _stepPolls = 0;
            if (pending is not null && _graph is null) StopPoll();
        }
        if (_fallback is null) return false;
        if (!ReferenceEquals(sprite.Brush, _fallback)) sprite.Brush = _fallback;
        _applied = new(Backend.Fallback, _fallbackShown);
        return true;
    }

    // The opaque ground: the look's own colour under its thickest veil (GlassRecipe.Shown over grey at veil one).
    private CompositionColorBrush Ground(Compositor compositor, Material material)
    {
        var (r, g, b) = GlassRecipe.Shown((.5, .5, .5), 1, 1, material.Palette);
        static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        var color = new Windows.UI.Color { A = 255, R = Byte(r), G = Byte(g), B = Byte(b) };
        if (_ground is null) _ground = compositor.CreateColorBrush(color);
        else if (_ground.Color != color) _ground.Color = color;
        return _ground;
    }

    // The graph failed for this surface: the ground goes under first (no frame without a brush), then the brush is let go.
    private void DropGraph(Compositor compositor, Material material, bool permanent)
    {
        if (_graph is not { } graph) return;
        _graph = null;
        _graphReady = false;
        StopPoll();
        _polls = 0;
        GlassEffect.Failed(graph, permanent);
        if (ReferenceEquals(_sprite?.Brush, graph)) _sprite!.Brush = _fallback ?? Ground(compositor, material);
        GlassEffect.Release(graph);
    }

    private void ReleaseFallback()
    {
        if (_fallback is null) return;
        GlassEffect.Release(_fallback);
        _fallback = null;
        _fallbackLook = null;
        _pendingLook = null;
    }

    // What the surface waits for is looked at again without a frame asking (an animation's last frame may ask for what is not
    // to be had yet): a loading graph a few times (PollMs; past LoadingLimitMs it counts as a failure), a fallback step not yet
    // loaded the same few times, and the graph's next try once its backoff has passed (GlassEffect bounds the tries). Nothing
    // pending (shown, or refused for good), hidden or disposed: no timer.
    private void Wake(Compositor compositor)
    {
        if (_poll is { IsEnabled: true }) return;
        int? due = null;
        if (_graph is not null)
        {
            if (!_graphReady && _polls < PollMs.Length) due = PollMs[_polls++];
        }
        else
        {
            long now = GlassEffect.Clock(), retryAt = GlassEffect.RetryAt;
            if (retryAt != _wokenFor) { _wokenFor = retryAt; _retryWakes = 0; }
            int? step = _pendingLook is not null && _stepPolls < PollMs.Length && GlassEffect.FallbackAvailable(compositor) ? PollMs[_stepPolls] : null;
            // A timer may tick a little early: a few wakes for each backoff, the first just after it ends.
            int? retry = GlassEffect.GraphAvailable && retryAt > now && _retryWakes < 3 ? (int)Math.Min(retryAt - now + 16, 60_000) : null;
            if (step is { } s && (retry is not { } r || s <= r)) { _stepPolls++; due = s; }
            else if (retry is { } wait) { _retryWakes++; due = wait; }
        }
        if (due is not { } ms) return;
        if (_poll is null)
        {
            _poll = new DispatcherTimer(DispatcherPriority.Background);
            _poll.Tick += PollTick;
        }
        _poll.Interval = TimeSpan.FromMilliseconds(ms);
        _poll.Start();
    }

    private void StopPoll() => _poll?.Stop();
    private void PollTick(object? sender, EventArgs e) { StopPoll(); Poll(); }

    // Looks again at what the surface waits for (the timer, or a test with its own clock).
    internal void Poll()
    {
        if (_disposed || !_visible || _sprite is null || _requested is not { } material) return;
        if (_applied.Backend == Backend.Graph && _graphReady) return;
        try { Apply(material); }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException) { Dispose(); }
    }

    // A rounded rectangle filling the bounds (the glass checks).
    public bool Show(IntPtr owner, Int32Rect bounds, int radius, double opacity, Material material, out AppliedMaterial applied)
        => Show(owner, bounds, new GlassBoundaryMath.GlassClip(new(0, 0, bounds.Width, bounds.Height, radius)), null, opacity, material, out applied);

    // The outline is the sprite's own clip, the composition's antialiased geometry, either way; the window never has a region.
    // A rounded clip (GlassBoundaryMath.TryRoundedClip) is a rounded rectangle, a move or a new size a few numbers on it. Any
    // other outline (shoulders) is its path (GlassPath, written as the WPF outline is), made anew only for a new shape; a move
    // changes only the clip's transform. The backdrop is drawn inside the clip only, so its edge is the WPF outline's curve.
    public bool Show(IntPtr owner, Int32Rect bounds, GlassBoundaryMath.GlassClip? clip, Outline? outline, double opacity, Material material, out AppliedMaterial applied)
    {
        applied = default;
        if (_disposed || owner == IntPtr.Zero || bounds.Width < 2 || bounds.Height < 2 || opacity <= 0 || clip is null && outline is null)
        {
            Hide();
            return false;
        }
        if (GlassComposition.Compositor is not { } compositor) return false;
        try
        {
            if (_window is null)
            {
                _window = new HwndSource(new HwndSourceParameters("BrimDeck glass")
                {
                    WindowStyle = unchecked((int)0x80000000),
                    ExtendedWindowStyle = 0x80 | 0x08000000 | 0x00200000 | 0x08 | 0x20,
                    PositionX = bounds.X, PositionY = bounds.Y, Width = bounds.Width, Height = bounds.Height
                });
                _window.AddHook(PassPointer);
                PassOver(_window.Handle);
                int enabled = 1;
                Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(_window.Handle, 17, ref enabled, sizeof(int)));
                _target = GlassComposition.Target(_window.Handle);
                _frame = compositor.CreateContainerVisual();
                _sprite = compositor.CreateSpriteVisual();
                _outline = compositor.CreateRoundedRectangleGeometry();
                _sprite.Clip = _roundedClip = compositor.CreateGeometricClip(_outline);
                _frame.Children.InsertAtTop(_sprite);
                _target.Root = _frame;
            }

            applied = Apply(material);
            if (applied.Backend == Backend.None) { Hide(); return false; }
            _sprite!.Opacity = (float)Math.Clamp(opacity, 0, 1);
            // The window covers its owner (the panel's window, which holds the island at every size), and the glass inside it is a
            // visual placed and clipped to the outline: an opening or closing, or a new shape, changes only the visual and its clip,
            // in one composition commit, with no window message on any frame. Resizing the window on every frame cost a few
            // milliseconds each and let the window's place and its content reach the screen on different frames.
            _owner = owner;
            _need = bounds;
            _clip = clip ?? new GlassBoundaryMath.GlassClip(new(0, 0, bounds.Width, bounds.Height, 0));
            _shape = clip is null ? outline : null;
            if (!Place(Frame(owner, bounds))) { Hide(); return false; }
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            applied = default;
            Dispose();
            return false;
        }
    }

    // Puts the window at place, the visual and its clip where the outline asked for (_need) falls in it, and keeps the window
    // right behind the owner. Each part is touched only when it changed.
    private bool Place(Int32Rect place)
    {
        var handle = _window!.Handle;
        int dx = _need.X - place.X, dy = _need.Y - place.Y;
        var frame = _frame!;
        var offset = new Vector3(dx, dy, 0);
        if (frame.Offset != offset) frame.Offset = offset;
        var sprite = _sprite!;
        var extent = new Vector2(_need.Width, _need.Height);
        if (sprite.Size != extent) sprite.Size = extent;
        CompositionClip wanted;
        if (_shape is { } shape)
        {
            if (_pathLayout != shape.Layout)
            {
                if (GlassPath.Create(shape.Layout) is not { } path) return false;
                if (_pathGeometry is null)
                {
                    var compositor = sprite.Compositor;
                    _pathGeometry = compositor.CreatePathGeometry(path);
                    _pathClip = compositor.CreateGeometricClip(_pathGeometry);
                }
                else _pathGeometry.Path = path;
                _pathLayout = shape.Layout;
            }
            if (_pathClip!.TransformMatrix != shape.Place) _pathClip.TransformMatrix = shape.Place;
            wanted = _pathClip;
        }
        else
        {
            if (_clip != _shown)
            {
                _shown = _clip;
                Set(_outline!, _clip.First);
            }
            wanted = _roundedClip!;
        }
        if (!ReferenceEquals(sprite.Clip, wanted)) sprite.Clip = wanted;
        const uint NoSize = 0x01, NoMove = 0x02, NoActivate = 0x10, ShowWindowFlag = 0x40, NoOwnerZOrder = 0x200;
        const uint PositionOnly = NoSize | NoMove | NoActivate | NoOwnerZOrder;
        var owner = _owner;
        bool ownerTopmost = IsTopmost(owner);
        bool tierChanged = IsTopmost(handle) != ownerTopmost;
        if (tierChanged || !_visible || _placed != place || GetWindow(owner, 2) != handle) // GW_HWNDNEXT
        {
            // A move alone leaves the size: no WM_SIZE for the window and its target.
            uint size = _visible && place.Width == _placed.Width && place.Height == _placed.Height ? NoSize : 0;
            if ((tierChanged && !SetWindowPos(handle, new IntPtr(ownerTopmost ? -1 : -2), 0, 0, 0, 0, PositionOnly))
                || !SetWindowPos(handle, owner, place.X, place.Y, place.Width, place.Height, NoActivate | ShowWindowFlag | NoOwnerZOrder | size))
                return false;
            if (!_visible) SetWindowPos(handle, owner, 0, 0, 0, 0, PositionOnly);
            _visible = true;
            _placed = place;
        }
        return true;
    }

    private static void Set(CompositionRoundedRectangleGeometry geometry, GlassBoundaryMath.RoundedClip clip)
    {
        geometry.Offset = new Vector2((float)clip.X, (float)clip.Y);
        geometry.Size = new Vector2((float)clip.Width, (float)clip.Height);
        geometry.CornerRadius = new Vector2((float)clip.Radius);
    }

    private static bool Contains(Int32Rect outer, Int32Rect inner)
        => inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    // The owner's window rectangle when it holds the bounds (it always does for a panel), else the bounds themselves.
    private static Int32Rect Frame(IntPtr owner, Int32Rect bounds)
    {
        if (!GetWindowRect(owner, out var r)) return bounds;
        var frame = new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return frame.Width > 0 && frame.Height > 0 && Contains(frame, bounds) ? frame : bounds;
    }

    private static bool IsTopmost(IntPtr window) => (GetWindowLong(window, -20) & 0x08) != 0;

    // The window covers the panel's whole window (Frame), so it must never take the pointer. WS_EX_TRANSPARENT alone does not
    // take a top-level window out of the system's hit test, and HTTRANSPARENT hands the mouse on only to windows of the same
    // thread: the apps beneath the panel's clear parts would take no clicks while the glass shows. Layered and transparent
    // (fully opaque, so what it composes shows as before), it is passed over everywhere. HwndSource takes WS_EX_LAYERED off a
    // window without per-pixel opacity again when it is shown, so PassPointer keeps it on every change of the extended style.
    private static void PassOver(IntPtr handle)
    {
        SetWindowLong(handle, -20, GetWindowLong(handle, -20) | PassingStyle);
        SetLayeredWindowAttributes(handle, 0, 255, 0x2); // LWA_ALPHA
    }

    private const int PassingStyle = 0x80000 | 0x20; // WS_EX_LAYERED | WS_EX_TRANSPARENT

    private static IntPtr PassPointer(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case 0x0021: // WM_MOUSEACTIVATE
                handled = true;
                return new IntPtr(3); // MA_NOACTIVATE
            case 0x0084: // WM_NCHITTEST
                handled = true;
                return new IntPtr(-1); // HTTRANSPARENT
            case 0x007C when wParam.ToInt64() == -20: // WM_STYLECHANGING, GWL_EXSTYLE: STYLESTRUCT { styleOld, styleNew }
                Marshal.WriteInt32(lParam, 4, Marshal.ReadInt32(lParam, 4) | PassingStyle);
                handled = true;
                return IntPtr.Zero;
            default:
                return IntPtr.Zero;
        }
    }

    public void Hide()
    {
        if (!_visible) return;
        _visible = false;
        StopPoll();
        if (_window is { } window) ShowWindow(window.Handle, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        if (_poll is not null) { _poll.Stop(); _poll.Tick -= PollTick; }
        if (_target is not null) { _target.Root = null; _target.Dispose(); }
        if (_sprite is not null)
        {
            _sprite.Brush = null;
            _sprite.Clip = null;
            _roundedClip?.Dispose();
            _pathClip?.Dispose();
            _pathGeometry?.Dispose();
            _sprite.Dispose();
        }
        _frame?.Dispose();
        GlassEffect.Release(_graph);
        _graph = null;
        ReleaseFallback();
        _ground?.Dispose();
        _ground = null;
        _applied = default;
        _outline?.Dispose();
        _window?.Dispose();
    }

    [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern IntPtr GetWindow(IntPtr window, uint relation);
    [DllImport("user32", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32")] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr window, int command);
    [StructLayout(LayoutKind.Sequential)] private struct Rect32 { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out Rect32 rect);
}
