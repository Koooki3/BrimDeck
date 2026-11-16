using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

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
    private GlassEffect.AcrylicLook? _look;
    private bool _visible, _disposed;

    public bool Show(IntPtr owner, Int32Rect bounds, int radius, double opacity, GlassEffect.AcrylicLook look)
    {
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
                    ExtendedWindowStyle = 0x80 | 0x08000000 | 0x00200000 | 0x08,
                    PositionX = bounds.X, PositionY = bounds.Y, Width = bounds.Width, Height = bounds.Height
                });
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
            if (_look != look)
            {
                var previous = sprite.Brush;
                sprite.Brush = GlassEffect.Acrylic(compositor, look);
                GlassEffect.Release(previous);
                _look = look;
            }
            if (bounds.Width != _bounds.Width || bounds.Height != _bounds.Height || radius != _radius)
            {
                sprite.Size = _outline!.Size = new Vector2(bounds.Width, bounds.Height);
                _outline.CornerRadius = new Vector2(radius);
                // The region is only the outer limit; the composition clip draws the antialiased corners.
                var region = CreateRoundRectRgn(0, 0, bounds.Width + 1, bounds.Height + 1, Math.Max(1, 2 * radius - 4), Math.Max(1, 2 * radius - 4));
                if (region == IntPtr.Zero) { Hide(); return false; }
                if (SetWindowRgn(handle, region, true) == 0)
                {
                    DeleteObject(region);
                    Hide();
                    return false;
                }
                _radius = radius;
            }
            sprite.Opacity = (float)Math.Clamp(opacity, 0, 1);
            if (!_visible || _bounds != bounds)
            {
                if (!SetWindowPos(handle, owner, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10 | 0x40 | 0x200))
                {
                    Hide();
                    return false;
                }
                if (!_visible) SetWindowPos(handle, owner, 0, 0, 0, 0, 0x01 | 0x02 | 0x10 | 0x200);
                _visible = true;
                _bounds = bounds;
            }
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            Dispose();
            return false;
        }
    }

    public void Hide()
    {
        if (!_visible) return;
        _visible = false;
        if (_window is { } window) ShowWindow(window.Handle, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        if (_target is not null) { _target.Root = null; _target.Dispose(); }
        if (_sprite is not null)
        {
            var brush = _sprite.Brush;
            var clip = _sprite.Clip;
            _sprite.Brush = null;
            _sprite.Clip = null;
            GlassEffect.Release(brush);
            clip?.Dispose();
            _sprite.Dispose();
        }
        _outline?.Dispose();
        _window?.Dispose();
    }

    [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("gdi32")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr value);
}
