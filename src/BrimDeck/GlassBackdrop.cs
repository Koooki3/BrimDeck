using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace BrimDeck;

// The blur under a glass island or notice (PanelTheme.GlassShown): a window of its own, just below the panel's window, the size of the
// island, showing the desktop behind it blurred (Windows.UI.Composition's host backdrop brush, which DWM blurs). The panel's
// window is layered, and Windows can neither blur behind a layered window nor cut a blur to a shape there, so the blur lives in
// this window instead: its content is clipped to the island's outline with an antialiased rounded rectangle, and its window
// region (the outline, a pixel larger) keeps it from taking clicks or showing outside the island.
//
// The two windows are drawn by different compositors, so they cannot change shape on the same frame. While the island changes
// shape or place (opening, closing, a page of another size, booting, a resize) the blur therefore steps aside, the glass shows
// as clear glass meanwhile, and the blur fades back once the island has held still for SettleMs: it fades out when the island
// grows (the old shape lies inside the new one) and goes at once when it shrinks or moves, so it never shows outside the island.
// Nothing here draws while the island is still: the composition content is static, and only DWM's own blur of the desktop
// behind runs. Where Windows cannot draw it (transparency effects off, high contrast, battery saver, a remote session, an older
// Windows, any failure) there is no blur and the glass stays clear.
internal sealed class GlassBackdrop : IDisposable
{
    // The blur comes back as the island's own motion lands (Soft.Out's curve), soon after it has held still.
    private const int SettleMs = 90, FadeInMs = 200, FadeOutMs = 90;
    private readonly Window _owner;
    // The surface the blur lies under (the island, a notice) and its shape now: corners, shoulder, and whether its material is
    // fully there (not fading into the indicator or out of sight).
    private readonly FrameworkElement _island;
    private readonly Func<(CornerRadius Corners, double Shoulder, bool Solid)> _shape;
    private readonly DispatcherTimer _settle;
    private HwndSource? _window;
    private SpriteVisual? _sprite;
    private CompositionRoundedRectangleGeometry? _clip;
    private DesktopWindowTarget? _target;
    // The shape the window has now, in device pixels (null while it is hidden); the shape waiting to settle; whether the blur
    // is shown or on its way in.
    private Key? _placed, _pending;
    private bool _visible, _disposed;

    private readonly record struct Key(int X, int Y, int Width, int Height, CornerRadius Corners, double Shoulder, double Scale);

    public GlassBackdrop(Window owner, IslandBorder island) : this(owner, island, () => (island.CornerRadius, island.ShoulderRadius, island.MaterialOpacity >= .999)) { }

    public GlassBackdrop(Window owner, FrameworkElement surface, Func<(CornerRadius Corners, double Shoulder, bool Solid)> shape)
    {
        _owner = owner; _island = surface; _shape = shape;
        _settle = new DispatcherTimer(DispatcherPriority.Render, owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(SettleMs) };
        _settle.Tick += (_, _) => Settle();
        surface.LayoutUpdated += (_, _) => Update();
        owner.IsVisibleChanged += (_, _) => Update();
        owner.Closed += (_, _) => Dispose();
        if (PresentationSource.FromVisual(owner) is HwndSource main) main.AddHook(OwnerMessage); else owner.SourceInitialized += (_, _) => (PresentationSource.FromVisual(owner) as HwndSource)?.AddHook(OwnerMessage);
    }

    // Windows can draw the blur now: the glass theme is shown (transparency effects on, no high contrast), battery saver is off,
    // the session is local, and composition started.
    public static bool Available => PanelTheme.GlassShown && !MotionEnvironment.BatterySaver && GetSystemMetrics(RemoteSession) == 0 && Composition.Ready;

    // After anything that can change the island's shape, place or look; cheap when nothing changed.
    public void Update()
    {
        if (_disposed) return;
        var key = Wanted() ? Measure() : null;
        if (key is null) { _pending = null; _settle.Stop(); Hide(); return; }
        if (key == _pending || key == _placed && _visible && !_settle.IsEnabled) return;
        // The island moved or changed shape: out of the way now, back when it holds still. A blur still lying inside the new
        // shape fades; one that would stick out goes at once.
        if (_placed is { } placed && key != placed)
        {
            if (Contains(key.Value, placed)) FadeOut(); else Hide();
        }
        _pending = key;
        _settle.Stop(); _settle.Start();
    }

    private bool Wanted()
    {
        if (!Available || !_owner.IsVisible || !_island.IsVisible || !_shape().Solid || _island.ActualWidth < 4 || _island.ActualHeight < 4) return false;
        // A fade of the island or the window (booting, powering off, a notice taking its place) has no blur under it.
        for (DependencyObject? node = _island; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element && element.Opacity < .999) return false;
        return PresentationSource.FromVisual(_island) is not null;
    }

    private Key? Measure()
    {
        try
        {
            double scale = VisualTreeHelper.GetDpi(_island).DpiScaleX;
            var at = _island.PointToScreen(new Point());
            var shape = _shape();
            return new Key((int)Math.Round(at.X), (int)Math.Round(at.Y), (int)Math.Round(_island.ActualWidth * scale), (int)Math.Round(_island.ActualHeight * scale),
                shape.Corners, shape.Shoulder, scale);
        }
        catch (InvalidOperationException) { return null; }
    }

    private static bool Contains(Key outer, Key inner) => inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    // The island has held still: the blur takes its shape and fades in.
    private void Settle()
    {
        _settle.Stop();
        if (_disposed || _pending is not { } key) return;
        // Still the same shape (a frame may have come between the last change and the tick)?
        var now = Wanted() ? Measure() : null;
        if (now != key) { Update(); return; }
        _pending = null;
        if (!EnsureWindow(key)) return;
        try
        {
            Place(key);
            _placed = key;
            if (!_visible) { _visible = true; Fade(1, FadeInMs); }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Composition.Fail(); Hide();
        }
    }

    private void Place(Key key)
    {
        var handle = _window!.Handle;
        var owner = new WindowInteropHelper(_owner).Handle;
        // Just below the panel's window: above whatever the panel stands over, never above the panel itself.
        SetWindowPos(handle, owner, key.X, key.Y, key.Width, key.Height, NoActivate | ShowWindow | NoOwnerZOrder);
        // Shown for the first time, a topmost window goes to the top whatever it was placed after (seen on Windows 11): a
        // second call, z-order only, puts it under the panel.
        SetWindowPos(handle, owner, 0, 0, 0, 0, NoActivate | NoMove | NoSize | NoOwnerZOrder);
        // The clip: the island's body as an antialiased rounded rectangle, a device pixel inside its edge, which the island's own
        // edge and rim cover. A shape square at the top (the open panel, the notch) has its rectangle reach above the window, so
        // only the bottom corners round. The notch's shoulders, thinner than its corners, stay clear glass.
        var c = key.Corners;
        bool squareTop = c.TopLeft < .5 && c.TopRight < .5;
        double radius = (squareTop ? Math.Max(c.BottomLeft, c.BottomRight) : Math.Max(Math.Max(c.TopLeft, c.TopRight), Math.Max(c.BottomLeft, c.BottomRight))) * key.Scale;
        double shoulder = Math.Clamp(key.Shoulder, 0, Math.Min(key.Width / key.Scale / 4, key.Height / key.Scale / 2)) * key.Scale;
        float inset = 1, top = squareTop ? -(float)radius - 2 : inset;
        _clip!.Offset = new Vector2((float)shoulder + inset, top);
        _clip.Size = new Vector2(Math.Max(0, key.Width - 2 * (float)shoulder - 2 * inset), Math.Max(0, key.Height - inset - top));
        _clip.CornerRadius = new Vector2((float)Math.Max(0, radius - inset));
        // The window region: the outline, so the corners outside it neither show nor take clicks.
        var outline = IslandBorder.BuildOutline(key.Width / key.Scale, key.Height / key.Scale, key.Shoulder, key.Corners, 0).GetFlattenedPathGeometry(.25, ToleranceType.Absolute);
        var points = new List<POINT>();
        foreach (var figure in outline.Figures)
        {
            points.Add(Device(figure.StartPoint, key.Scale));
            foreach (var segment in figure.Segments)
                if (segment is System.Windows.Media.PolyLineSegment poly) points.AddRange(poly.Points.Select(p => Device(p, key.Scale)));
                else if (segment is System.Windows.Media.LineSegment line) points.Add(Device(line.Point, key.Scale));
        }
        var region = points.Count >= 3 ? CreatePolygonRgn([.. points], points.Count, 2) : IntPtr.Zero;
        // The window owns the region from here on.
        if (region != IntPtr.Zero && SetWindowRgn(handle, region, true) == 0) DeleteObject(region);
    }

    private static POINT Device(Point p, double scale) => new((int)Math.Round(p.X * scale), (int)Math.Round(p.Y * scale));

    private void FadeOut()
    {
        if (!_visible) return;
        _visible = false;
        Fade(0, FadeOutMs);
    }

    private void Hide()
    {
        _visible = false;
        if (_placed is null) return;
        _placed = null;
        if (_sprite is not null) { _sprite.StopAnimation("Opacity"); _sprite.Opacity = 0; }
        if (_window is not null) ShowWindowNative(_window.Handle, Hidden);
    }

    private void Fade(float to, int milliseconds)
    {
        if (_sprite is null || Composition.Compositor is not { } compositor) return;
        // With animations off (BrimDeck's setting, Windows', high contrast) the blur comes and goes at once.
        if (!Soft.Enabled) { _sprite.StopAnimation("Opacity"); _sprite.Opacity = to; return; }
        var animation = compositor.CreateScalarKeyFrameAnimation();
        // In: the soft landing the island's motion uses (Soft.Out); out: quick and even, it only steps aside.
        if (to > 0) animation.InsertKeyFrame(1, to, compositor.CreateCubicBezierEasingFunction(new Vector2(.22f, .8f), new Vector2(.28f, 1)));
        else animation.InsertKeyFrame(1, to, compositor.CreateLinearEasingFunction());
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        _sprite.StartAnimation("Opacity", animation);
    }

    // The window, its composition target and the blurred sprite, made on first use.
    private bool EnsureWindow(Key key)
    {
        if (_window is not null) return true;
        if (Composition.Compositor is not { } compositor) return false;
        try
        {
            var parameters = new HwndSourceParameters("BrimDeck glass")
            {
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                // A tool window that never takes focus or a taskbar button, with no redirection surface: composition draws it.
                ExtendedWindowStyle = 0x80 | 0x08000000 | 0x00200000 | 0x08, // TOOLWINDOW | NOACTIVATE | NOREDIRECTIONBITMAP | TOPMOST
                // Made where the island is, on its monitor: made elsewhere (another monitor at another scale) and moved over,
                // it would be resized for the change of scale.
                Width = Math.Max(1, key.Width), Height = Math.Max(1, key.Height), PositionX = key.X, PositionY = key.Y
            };
            _window = new HwndSource(parameters);
            _window.AddHook(OwnMessage);
            int on = 1;
            DwmSetWindowAttribute(_window.Handle, UseHostBackdropBrush, ref on, sizeof(int));
            _target = Composition.Target(_window.Handle);
            var root = compositor.CreateContainerVisual(); root.RelativeSizeAdjustment = Vector2.One;
            _sprite = compositor.CreateSpriteVisual(); _sprite.RelativeSizeAdjustment = Vector2.One; _sprite.Opacity = 0;
            _sprite.Brush = compositor.CreateHostBackdropBrush();
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
    // the blur ending short of a notice on a second monitor at 125 %). It is placed again for the island as it is now.
    private IntPtr OwnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int DpiChanged = 0x02E0;
        if (message != DpiChanged) return IntPtr.Zero;
        handled = true;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => { if (_placed is { } placed && _window is not null && !_disposed) Place(placed); });
        return IntPtr.Zero;
    }

    // The panel's window moved in the z-order (it is put back on top now and then): the blur goes back just under it.
    private IntPtr OwnerMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WindowPosChanged = 0x0047;
        if (message == WindowPosChanged && _placed is not null && _window is not null)
            SetWindowPos(_window.Handle, hwnd, 0, 0, 0, 0, NoActivate | NoMove | NoSize | NoOwnerZOrder);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settle.Stop();
        Release();
    }

    private void Release()
    {
        _target?.Dispose(); _target = null; _sprite = null; _clip = null;
        _window?.Dispose(); _window = null;
        _visible = false; _placed = null;
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
    [StructLayout(LayoutKind.Sequential)] private readonly record struct POINT(int X, int Y);
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32", EntryPoint = "ShowWindow")] private static extern bool ShowWindowNative(IntPtr window, int command);
    [DllImport("user32")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("gdi32")] private static extern IntPtr CreatePolygonRgn(POINT[] points, int count, int mode);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr handle);
}
