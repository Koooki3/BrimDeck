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
    private SpriteVisual? _sprite;
    private CompositionRoundedRectangleGeometry? _outline;
    private Int32Rect _bounds;
    private int _radius;
    private PixelPoint[]? _contour;

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct PixelPoint(int X, int Y);
    // The material asked for: each look's luminosity opacity and the light look's share (GlassEffect.Graph's weights).
    internal readonly record struct Material(double DarkVeil, double LightVeil, double Palette);
    // What draws the glass: the persistent graph, the stepped fallback, an opaque ground (while the graph loads, or where
    // neither effect can be used), or nothing (no compositor).
    internal enum Backend { None, Opaque, Fallback, Graph }
    // The material as the backend draws it, for the readability guard: the request on the graph, the stepped weights on the
    // fallback, and on the opaque ground the thickest veils (a grey backdrop under a full veil shows the look's own colour,
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
    private bool _visible, _disposed;
    internal bool Visible => _visible;
    internal AppliedMaterial Applied => _visible ? _applied : default;
    // Diagnostics and tests: the brush the glass draws with now.
    internal CompositionBrush? Brush => _sprite?.Brush;

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
        }
        if (_graph is { } graph)
        {
            if (!_graphReady)
            {
                var status = GlassEffect.StatusOf(graph);
                if (status == CompositionEffectFactoryLoadStatus.Success) { _graphReady = true; GlassEffect.Succeeded(); }
                else if (status != CompositionEffectFactoryLoadStatus.Pending || GlassEffect.Clock() - _graphSince >= LoadingLimitMs)
                    DropGraph(compositor, material, permanent: status != CompositionEffectFactoryLoadStatus.Pending);
                else SchedulePoll();
            }
            if (_graphReady)
            {
                try
                {
                    GlassEffect.Present(graph, material.DarkVeil, material.LightVeil, material.Palette);
                    if (!ReferenceEquals(sprite.Brush, graph)) sprite.Brush = graph;
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
        if (Fallback(compositor, sprite, material, compile: _graph is null)) return _applied;
        sprite.Brush = Ground(compositor, material);
        // The fallback's step may still be loading too: looked at again with the same few reads.
        SchedulePoll();
        return _applied = new(Backend.Opaque, new(1, 1, material.Palette));
    }

    private bool Fallback(Compositor compositor, SpriteVisual sprite, Material material, bool compile)
    {
        var look = GlassEffect.StepLook(material.DarkVeil, material.LightVeil, material.Palette);
        if (_fallback is null || _fallbackLook != look)
        {
            if (compile && GlassEffect.FallbackAvailable(compositor) && GlassEffect.Acrylic(compositor, look) is { } brush)
            {
                var previous = _fallback;
                sprite.Brush = brush;
                GlassEffect.Release(previous);
                _fallback = brush;
                _fallbackLook = look;
                var (dark, light, palette) = GlassEffect.StepMaterial(material.DarkVeil, material.LightVeil, material.Palette);
                _fallbackShown = new(dark, light, palette);
            }
            // The new step is not to be had now (loading, refused, a graph loading): the step shown stays, reported as it is.
            if (_fallback is null) return false;
        }
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
    }

    // A loading graph (or fallback step) is looked at again a few times without a frame asking; a graph past LoadingLimitMs
    // counts as a failure.
    private void SchedulePoll()
    {
        if (_polls >= PollMs.Length || _poll is { IsEnabled: true }) return;
        if (_poll is null)
        {
            _poll = new DispatcherTimer(DispatcherPriority.Background);
            _poll.Tick += PollTick;
        }
        _poll.Interval = TimeSpan.FromMilliseconds(PollMs[_polls++]);
        _poll.Start();
    }

    private void StopPoll() => _poll?.Stop();
    private void PollTick(object? sender, EventArgs e) { StopPoll(); Poll(); }

    // Looks at a loading graph or fallback again (the timer, or a test with its own clock).
    internal void Poll()
    {
        if (_disposed || !_visible || _sprite is null || _requested is not { } material) return;
        if (_applied.Backend != Backend.Opaque && (_graph is null || _graphReady)) return;
        try { Apply(material); }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException) { Dispose(); }
    }

    public bool Show(IntPtr owner, Int32Rect bounds, int radius, double opacity, Material material, out AppliedMaterial applied, PixelPoint[]? contour = null)
    {
        applied = default;
        if (_disposed || owner == IntPtr.Zero || bounds.Width < 2 || bounds.Height < 2 || opacity <= 0)
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
                int enabled = 1;
                Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(_window.Handle, 17, ref enabled, sizeof(int)));
                _target = GlassComposition.Target(_window.Handle);
                _sprite = compositor.CreateSpriteVisual();
                _outline = compositor.CreateRoundedRectangleGeometry();
                _sprite.Clip = compositor.CreateGeometricClip(_outline);
                _target.Root = _sprite;
            }

            var handle = _window.Handle;
            var sprite = _sprite!;
            applied = Apply(material);
            if (applied.Backend == Backend.None) { Hide(); return false; }
            if (bounds.Width != _bounds.Width || bounds.Height != _bounds.Height || radius != _radius || !ReferenceEquals(_contour, contour))
            {
                sprite.Size = _outline!.Size = new Vector2(bounds.Width, bounds.Height);
                _outline.CornerRadius = new Vector2(radius);
                // The region is only the outer limit; the composition clip draws the antialiased corners.
                var region = contour is { Length: >= 3 } ? CreatePolygonRgn(contour, contour.Length, 1)
                    : CreateRoundRectRgn(0, 0, bounds.Width + 1, bounds.Height + 1, Math.Max(1, 2 * radius - 4), Math.Max(1, 2 * radius - 4));
                if (region == IntPtr.Zero) { Hide(); return false; }
                if (SetWindowRgn(handle, region, true) == 0)
                {
                    DeleteObject(region);
                    Hide();
                    return false;
                }
                _radius = radius;
                _contour = contour;
            }
            sprite.Opacity = (float)Math.Clamp(opacity, 0, 1);
            const uint NoSize = 0x01, NoMove = 0x02, NoActivate = 0x10, ShowWindowFlag = 0x40, NoOwnerZOrder = 0x200;
            const uint PositionOnly = NoSize | NoMove | NoActivate | NoOwnerZOrder;
            bool ownerTopmost = IsTopmost(owner);
            bool tierChanged = IsTopmost(handle) != ownerTopmost;
            if (tierChanged || !_visible || _bounds != bounds || GetWindow(owner, 2) != handle) // GW_HWNDNEXT
            {
                if ((tierChanged && !SetWindowPos(handle, new IntPtr(ownerTopmost ? -1 : -2), 0, 0, 0, 0, PositionOnly))
                    || !SetWindowPos(handle, owner, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoActivate | ShowWindowFlag | NoOwnerZOrder))
                {
                    Hide();
                    return false;
                }
                if (!_visible) SetWindowPos(handle, owner, 0, 0, 0, 0, PositionOnly);
                _visible = true;
                _bounds = bounds;
            }
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            applied = default;
            Dispose();
            return false;
        }
    }

    private static bool IsTopmost(IntPtr window) => (GetWindowLong(window, -20) & 0x08) != 0;

    private static IntPtr PassPointer(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE
        }
        if (message != 0x0084) return IntPtr.Zero; // WM_NCHITTEST
        handled = true;
        return new IntPtr(-1); // HTTRANSPARENT
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
            var clip = _sprite.Clip;
            _sprite.Brush = null;
            _sprite.Clip = null;
            clip?.Dispose();
            _sprite.Dispose();
        }
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
    [DllImport("user32")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("gdi32")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32")] private static extern IntPtr CreatePolygonRgn([In] PixelPoint[] points, int count, int fillMode);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr value);
}
