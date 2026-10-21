using System.Windows;
using System.Windows.Input;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// Resizing by dragging, one gesture for both forms, as ftop's panel resizes and its strip fits:
// - Open on the system monitor page, the bottom edge changes the page's height, a side edge its width (both sides move, so the
//   panel stays centred on the collapsed surface) and a bottom corner both. The page follows through its layout steps.
// - Collapsed, either end of the notch or the capsule changes its width. Pointing at an end shows the resize cursor instead of
//   opening the panel. The content reflows as the width changes: the readings, the rings and the lyric take what fits. A width
//   set this way is kept, so the surface no longer widens to its content; a double click on an end gives that back.
// Sizes follow the pointer live and are saved when the button is released. While dragging, the window keeps the largest size
// either form can take, so each move resizes only the surface, and the panel neither opens nor closes.
public partial class MainWindow
{
    [Flags] private enum ResizeEdge { None = 0, Left = 1, Right = 2, Bottom = 4 }
    private ResizeEdge _resizeEdge, _hoverEdge;
    private bool _resizeHost, _resizeCompact, _resizeChanged;
    private Point _resizeStart;
    private (double Width, double Height) _resizeFrom;
    private DateTime _resizeApplied;
    // How close to an edge of the open page, in DIPs, the pointer grabs it; the collapsed surface's ends grab a little more.
    private const double ResizeGrip = 7, CompactGrip = 8;

    private bool Resizing => _resizeEdge != ResizeEdge.None;

    private void InitializeResize()
    {
        Island.PreviewMouseMove += ResizeMove;
        Island.PreviewMouseLeftButtonDown += ResizeDown;
        Island.PreviewMouseLeftButtonUp += (_, e) => { if (Resizing) { e.Handled = true; EndResize(); } };
        Island.LostMouseCapture += (_, _) => { if (Resizing) EndResize(); };
        Island.MouseLeave += (_, _) => { if (!Resizing) SetHoverEdge(ResizeEdge.None); };
    }

    // Whether a screen point lies on an end of the collapsed notch or capsule, where a press resizes instead of the panel opening.
    // TrackPointer does not count the ends as hovering the surface: neither the grip on the surface nor the part of the hover area
    // beyond it, so the pointer can come to an end from the side without the panel opening on the way.
    internal bool CompactGripAt(Point screen)
    {
        if (_expanded || _preview || IsSuppressed || !IsVisible || EffectiveStyle == CompactStyle.Line || PresentationSource.FromVisual(Island) is null) return false;
        var bounds = ScreenBounds(Island);
        var area = CompactHoverScreenRect(); area.Union(bounds);
        double grip = CompactGrip * (_pixelsPerDip > 0 ? _pixelsPerDip : 1);
        return area.Contains(screen) && screen.Y >= bounds.Top && screen.Y <= bounds.Bottom && (screen.X <= bounds.Left + grip || screen.X >= bounds.Right - grip);
    }

    // The edge under the pointer: on the open monitor page below its header, or an end of the collapsed surface.
    private ResizeEdge EdgeAt(Point point)
    {
        double w = Island.ActualWidth, h = Island.ActualHeight;
        if (!_expanded)
            return point.X >= 0 && point.X <= w && CompactGripAt(Island.PointToScreen(point)) ? point.X < w / 2 ? ResizeEdge.Left : ResizeEdge.Right : ResizeEdge.None;
        if (_preview || !IsMonitorPage || IsSuppressed) return ResizeEdge.None;
        if (point.Y < _header.Row || point.X < 0 || point.Y < 0 || point.X > w || point.Y > h) return ResizeEdge.None;
        // The bottom corners are rounded: there the grip is a band just inside the arc, as the panel's outline runs.
        double radius = PanelHeader.PanelRadius;
        bool left = point.X < radius, right = point.X > w - radius;
        if ((left || right) && point.Y > h - radius)
        {
            var center = new Point(left ? radius : w - radius, h - radius);
            double distance = (point - center).Length;
            return distance >= radius - ResizeGrip - 2 && distance <= radius + 1 ? ResizeEdge.Bottom | (left ? ResizeEdge.Left : ResizeEdge.Right) : ResizeEdge.None;
        }
        var edge = ResizeEdge.None;
        if (point.X <= ResizeGrip) edge |= ResizeEdge.Left; else if (point.X >= w - ResizeGrip) edge |= ResizeEdge.Right;
        if (point.Y >= h - ResizeGrip) edge |= ResizeEdge.Bottom;
        return edge;
    }

    private void SetHoverEdge(ResizeEdge edge)
    {
        if (edge == _hoverEdge) return;
        _hoverEdge = edge;
        Island.Cursor = edge switch
        {
            ResizeEdge.Bottom => Cursors.SizeNS, ResizeEdge.Left or ResizeEdge.Right => Cursors.SizeWE,
            ResizeEdge.Bottom | ResizeEdge.Left => Cursors.SizeNESW, ResizeEdge.Bottom | ResizeEdge.Right => Cursors.SizeNWSE, _ => null
        };
        // At an edge the resize cursor wins over the hand of a button beneath it.
        Island.ForceCursor = edge != ResizeEdge.None;
    }

    private void ResizeDown(object sender, MouseButtonEventArgs e)
    {
        var edge = EdgeAt(e.GetPosition(Island));
        if (edge == ResizeEdge.None) return;
        e.Handled = true;
        if (!_expanded)
        {
            var style = EffectiveStyle;
            // A double click on an end lets the surface widen to its content again.
            if (e.ClickCount >= 2) { var fitted = Settings.Copy(); fitted.SetCompactAutoWidth(style, true); _app.UpdateSettings(fitted); return; }
            _resizeCompact = true; _resizeFrom = (Collapsed.Surface.Width, Collapsed.Surface.Height);
        }
        else { _monitorView?.CloseCard(); _resizeCompact = false; _resizeFrom = (Math.Max(Settings.MonitorWidth, 440), Settings.MonitorHeight); }
        _resizeEdge = edge; _resizeStart = WindowsHost.Cursor(); _resizeChanged = false;
        // The window grows once to the largest size; from here on only the surface changes size.
        _resizeHost = true; ApplySettings();
        Island.CaptureMouse();
    }

    private void ResizeMove(object sender, MouseEventArgs e)
    {
        if (!Resizing) { SetHoverEdge(EdgeAt(e.GetPosition(Island))); return; }
        e.Handled = true;
        // About a frame between steps: the pointer reports far more often than the surface can usefully redraw.
        if ((DateTime.UtcNow - _resizeApplied).TotalMilliseconds < 15) return;
        var cursor = WindowsHost.Cursor();
        double scale = _pixelsPerDip > 0 ? _pixelsPerDip : 1;
        double dx = (cursor.X - _resizeStart.X) / scale, dy = (cursor.Y - _resizeStart.Y) / scale;
        double width = _resizeFrom.Width + (_resizeEdge.HasFlag(ResizeEdge.Right) ? 2 * dx : _resizeEdge.HasFlag(ResizeEdge.Left) ? -2 * dx : 0);
        if (_resizeCompact)
        {
            var style = EffectiveStyle;
            var (min, max) = CollapsedGeometry.WidthRange(style, MonitorBoundsDip, WorkDip);
            width = Math.Round(Math.Clamp(width, min, max));
            if (width == Collapsed.Surface.Width && !Settings.CompactAutoWidth(style)) return;
            _resizeApplied = DateTime.UtcNow; _resizeChanged = true;
            Settings.SetCompactWidth(style, width); Settings.SetCompactAutoWidth(style, false);
            UpdateCollapsedGeometry();
            SetExpanded(false, instant: true);
            return;
        }
        double height = _resizeFrom.Height + (_resizeEdge.HasFlag(ResizeEdge.Bottom) ? dy : 0);
        width = Math.Round(Math.Clamp(width, 440, Math.Max(440, Math.Min(1200, WorkDip.Width - 24))));
        height = Math.Round(Math.Clamp(height, 140, 400));
        if (width == Settings.MonitorWidth && height == Settings.MonitorHeight) return;
        _resizeApplied = DateTime.UtcNow; _resizeChanged = true;
        Settings.MonitorWidth = width; Settings.MonitorHeight = height;
        _pageWidth = width;
        SetExpanded(true, instant: true);
        RenderMonitor();
    }

    private void EndResize()
    {
        bool compact = _resizeCompact;
        _resizeEdge = ResizeEdge.None; _resizeHost = false; _resizeCompact = false;
        if (Island.IsMouseCaptured) Island.ReleaseMouseCapture();
        SetHoverEdge(ResizeEdge.None);
        // Saved like any change from the settings window, which applies it to every panel and fits the window again. A press that
        // moved nothing only fits the window again.
        if (_resizeChanged) _app.UpdateSettings(Settings.Copy()); else ApplySettings();
        if (!compact) PointerChanged(Island.IsMouseOver || InsideOpenPanel(WindowsHost.Cursor()));
    }
}
