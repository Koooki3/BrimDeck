using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// Resizing by dragging, one gesture for both forms, as ftop's panel resizes and its strip fits:
// - Open on the system monitor page, the bottom edge changes the page's height, a side edge its width (both sides move, so the
//   panel stays centred on the collapsed surface) and a bottom corner both. The page follows through its layout steps.
// - Collapsed, either end of the notch or the capsule changes its width. Pointing at an end shows the resize cursor instead of
//   opening the panel. The content reflows as the width changes: the readings, the rings and the lyric take what fits. A width
//   set this way is kept, so the surface no longer widens to its content; a double click on an end gives that back.
// The surface trails the pointer on a spring (Spring, in Core): it lags a little while dragging, settles with a slight give when the
// pointer stops, and near a limit the drag meets growing resistance instead of a wall. The size is saved once the spring has come
// to rest after the button is released. While dragging and settling, the window keeps the largest size either form can take, so
// each frame resizes only the surface, and the panel neither opens nor closes.
public partial class MainWindow
{
    [Flags] private enum ResizeEdge { None = 0, Left = 1, Right = 2, Bottom = 4 }
    private ResizeEdge _resizeEdge, _hoverEdge;
    private bool _resizeHost, _resizeCompact, _resizeChanged, _resizeSettling, _springHooked;
    private Point _resizeStart;
    private (double Width, double Height) _resizeFrom;
    private Spring _springWidth, _springHeight;
    private TimeSpan _springTime;
    // How close to an edge of the open page, in DIPs, the pointer grabs it; the collapsed surface's ends grab a little more.
    private const double ResizeGrip = 7, CompactGrip = 8;
    private const double MonitorMinWidth = 440, MonitorMinHeight = 140, MonitorMaxHeight = 400;

    private bool Dragging => _resizeEdge != ResizeEdge.None;
    // Dragging, or settling after the button was released.
    private bool Resizing => Dragging || _resizeSettling;
    private double MonitorMaxWidth => Math.Max(MonitorMinWidth, Math.Min(1200, WorkDip.Width - 24));

    private void InitializeResize()
    {
        Island.PreviewMouseMove += ResizeMove;
        Island.PreviewMouseLeftButtonDown += ResizeDown;
        Island.PreviewMouseLeftButtonUp += (_, e) => { if (Dragging) { e.Handled = true; EndResize(); } };
        Island.LostMouseCapture += (_, _) => { if (Dragging) EndResize(); };
        Island.MouseLeave += (_, _) => { if (!Dragging) SetHoverEdge(ResizeEdge.None); };
        Closed += (_, _) => StopSpring();
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
        bool compact = !_expanded;
        if (compact && e.ClickCount >= 2 && !_resizeSettling)
        {
            // A double click on an end lets the surface widen to its content again.
            var fitted = Settings.Copy(); fitted.SetCompactAutoWidth(EffectiveStyle, true); _app.UpdateSettings(fitted); return;
        }
        // A press while the surface still settles picks it up where it is heading.
        bool settling = _resizeSettling && _resizeCompact == compact;
        // The other form still settling: its size is saved first.
        if (_resizeSettling && !settling) { StopSpring(); FinishResize(); }
        if (!compact) _monitorView?.CloseCard();
        if (settling) _resizeFrom = (_springWidth.Target, _springHeight.Target);
        else
        {
            _resizeFrom = compact ? (Collapsed.Surface.Width, 0) : (Math.Max(Settings.MonitorWidth, MonitorMinWidth), Settings.MonitorHeight);
            _springWidth = new Spring(_resizeFrom.Width); _springHeight = new Spring(_resizeFrom.Height);
            _resizeChanged = false;
        }
        _resizeCompact = compact; _resizeSettling = false;
        _resizeEdge = edge; _resizeStart = WindowsHost.Cursor();
        // The window grows once to the largest size; from here on only the surface changes size.
        if (!_resizeHost) { _resizeHost = true; ApplySettings(); }
        Island.CaptureMouse();
    }

    private void ResizeMove(object sender, MouseEventArgs e)
    {
        if (!Dragging) { if (!_resizeSettling) SetHoverEdge(EdgeAt(e.GetPosition(Island))); return; }
        e.Handled = true;
        var cursor = WindowsHost.Cursor();
        double scale = _pixelsPerDip > 0 ? _pixelsPerDip : 1;
        double dx = (cursor.X - _resizeStart.X) / scale, dy = (cursor.Y - _resizeStart.Y) / scale;
        // Both sides move, so the width changes by twice the pointer's move.
        double width = _resizeFrom.Width + (_resizeEdge.HasFlag(ResizeEdge.Right) ? 2 * dx : _resizeEdge.HasFlag(ResizeEdge.Left) ? -2 * dx : 0);
        if (_resizeCompact)
        {
            var (min, max) = CollapsedGeometry.WidthRange(EffectiveStyle, MonitorBoundsDip, WorkDip);
            _springWidth.Target = Spring.Resist(width, min, max);
        }
        else
        {
            double height = _resizeFrom.Height + (_resizeEdge.HasFlag(ResizeEdge.Bottom) ? dy : 0);
            _springWidth.Target = Spring.Resist(width, MonitorMinWidth, MonitorMaxWidth);
            _springHeight.Target = Spring.Resist(height, MonitorMinHeight, MonitorMaxHeight);
        }
        StartSpring();
    }

    private void StartSpring()
    {
        if (_springHooked) return;
        _springHooked = true; _springTime = TimeSpan.Zero;
        CompositionTarget.Rendering += SpringFrame;
    }
    private void StopSpring()
    {
        if (!_springHooked) return;
        _springHooked = false;
        CompositionTarget.Rendering -= SpringFrame;
    }

    // One frame of the spring: the surface takes the spring's size in whole DIPs, and only when that changed. It runs while the
    // spring moves; a pointer held still unhooks it until the next move.
    private void SpringFrame(object? sender, EventArgs e)
    {
        // Something else opened or closed the panel while it settled (a hotkey, a full-screen rule): that wins; the size so far is kept.
        if (_resizeSettling && _resizeCompact == _expanded) { StopSpring(); FinishResize(); return; }
        var time = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.Zero;
        // WPF may raise Rendering more than once for one frame.
        if (_springTime != TimeSpan.Zero && time == _springTime) return;
        double dt = _springTime == TimeSpan.Zero ? 1 / 120.0 : (time - _springTime).TotalSeconds;
        _springTime = time;
        bool moving = _springWidth.Step(dt);
        if (!_resizeCompact) moving |= _springHeight.Step(dt);
        ApplyResize(Math.Round(_springWidth.Value), Math.Round(_springHeight.Value));
        if (moving) return;
        StopSpring();
        if (_resizeSettling) FinishResize();
    }

    private void ApplyResize(double width, double height)
    {
        if (_resizeCompact)
        {
            var style = EffectiveStyle;
            var (min, max) = CollapsedGeometry.WidthRange(style, MonitorBoundsDip, WorkDip);
            width = Math.Clamp(width, min, max);
            if (width == Collapsed.Surface.Width && !Settings.CompactAutoWidth(style)) return;
            _resizeChanged = true;
            Settings.SetCompactWidth(style, width); Settings.SetCompactAutoWidth(style, false);
            UpdateCollapsedGeometry();
            SetExpanded(false, instant: true);
            return;
        }
        width = Math.Clamp(width, MonitorMinWidth, MonitorMaxWidth);
        height = Math.Clamp(height, MonitorMinHeight, MonitorMaxHeight);
        if (width == Settings.MonitorWidth && height == Settings.MonitorHeight) return;
        _resizeChanged = true;
        Settings.MonitorWidth = width; Settings.MonitorHeight = height;
        _pageWidth = width;
        SetExpanded(true, instant: true);
        RenderMonitor();
    }

    // The button is up: the spring settles where the pointer left it, and then the size is saved.
    private void EndResize()
    {
        _resizeEdge = ResizeEdge.None; _resizeSettling = true;
        if (Island.IsMouseCaptured) Island.ReleaseMouseCapture();
        SetHoverEdge(ResizeEdge.None);
        if (_springHooked) return;
        FinishResize();
    }

    private void FinishResize()
    {
        bool compact = _resizeCompact;
        _resizeSettling = false; _resizeHost = false; _resizeCompact = false;
        // Saved like any change from the settings window, which applies it to every panel and fits the window again. A press that
        // moved nothing only fits the window again.
        if (_resizeChanged) _app.UpdateSettings(Settings.Copy()); else ApplySettings();
        _resizeChanged = false;
        if (!compact) PointerChanged(Island.IsMouseOver || InsideOpenPanel(WindowsHost.Cursor()));
    }
}
