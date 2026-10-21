using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// Resizing by dragging, one gesture for both forms, as ftop's panel resizes and its strip fits:
// - Open on the system monitor page, the bottom edge changes the page's height, a side edge its width (both sides move, so the
//   panel stays centred on the collapsed surface) and a bottom corner both. The page follows through its layout steps.
// - Collapsed, either end of the notch, the capsule or the line changes its width. Pointing at an end shows the resize cursor
//   instead of opening the panel. The content reflows as the width changes: the readings, the rings and the lyric take what
//   fits. A width set this way is kept, so the surface no longer widens to its content; a double click on an end gives that
//   back (the line returns to its usual length).
// The grips and their hints are a layer over the window (ResizeGripLayer); where they are is ResizeGrips's. The pointer is
// followed both by mouse messages on the layer and by TrackPointer's polling, which also sees it when the messages go to another
// window, and arms the collapsed surface's end before the pointer reaches it.
// The surface trails the pointer on a spring (Spring, in Core): it lags a little while dragging, settles with a slight give when the
// pointer stops, and near a limit the drag meets growing resistance instead of a wall. The size is saved once the spring has come
// to rest after the button is released. While dragging and settling, the window keeps the largest size either form can take, so
// each frame resizes only the surface, and the panel neither opens nor closes.
public partial class MainWindow
{
    private ResizeGripLayer _grips = null!;
    private ResizeEdges _resizeEdge, _hoverEdge;
    private bool _resizeHost, _resizeCompact, _resizeChanged, _resizeSettling, _springHooked;
    private Point _resizeStart;
    private (double Width, double Height) _resizeFrom;
    private Spring _springWidth, _springHeight;
    private CompactStyle _resizeStyle;
    private TimeSpan _springTime;
    private const double MonitorMinWidth = 440, MonitorMinHeight = 140, MonitorMaxHeight = 400;
    // How far inside the open panel, in DIPs, its handles start to show as the pointer nears an edge; how far outside its grips a
    // pointer that left the panel still counts as reaching for them, how long the panel then waits before it closes, and how
    // long at most it keeps waiting while the pointer stays there.
    private const double HintReach = 44, GripSlack = 36;
    private const int GripGrace = 420, GripHold = 1500;
    private long _leftAt;

    private bool Dragging => _resizeEdge != ResizeEdges.None;
    // Dragging, or settling after the button was released.
    private bool Resizing => Dragging || _resizeSettling;
    private double MonitorMaxWidth => Math.Max(MonitorMinWidth, Math.Min(1200, WorkDip.Width - 24));

    private void InitializeResize()
    {
        _grips = new ResizeGripLayer();
        Stage.Children.Add(_grips);
        _grips.MouseMove += GripMove;
        _grips.MouseLeftButtonDown += GripDown;
        _grips.MouseLeftButtonUp += (_, e) => { if (Dragging) { e.Handled = true; EndResize(); } };
        _grips.LostMouseCapture += (_, _) => { if (Dragging) EndResize(); };
        // The grips outside the panel are part of it: arriving there from the panel is not leaving it.
        _grips.MouseEnter += (_, _) => { if (_expanded) PointerChanged(true); };
        _grips.MouseLeave += (_, _) =>
        {
            if (Dragging) return;
            SetHoverEdge(ResizeEdges.None);
            if (_expanded && !Island.IsMouseOver) PointerChanged(InsideOpenPanel(WindowsHost.Cursor()));
        };
        Island.SizeChanged += (_, _) => UpdateGrips();
        Closed += (_, _) => StopSpring();
    }

    // Which form the grips take now: the open monitor page, the collapsed surface, or none (another page, a preview, hidden,
    // a quota alert widening the surface).
    private bool PanelResizable => _expanded && !_preview && IsMonitorPage && IsVisible && !IsSuppressed;
    private bool CompactResizable => !_expanded && !_preview && IsVisible && !IsSuppressed && _alert is null;

    // The island's box in the window's coordinates, where the grip layer draws.
    private DipRect IslandBox()
    {
        if (PresentationSource.FromVisual(Island) is null || Island.ActualWidth <= 0) return default;
        var origin = Island.TranslatePoint(new Point(), Stage);
        return new DipRect(origin.X, origin.Y, Island.ActualWidth, Island.ActualHeight);
    }

    // Brings the layer up to date with the island; cheap when nothing changed.
    private void UpdateGrips()
    {
        if (_grips is null) return;
        var form = PanelResizable ? ResizeGripLayer.Form.Panel : CompactResizable || Resizing && _resizeCompact ? ResizeGripLayer.Form.Compact : ResizeGripLayer.Form.None;
        if (Resizing && !_resizeCompact) form = ResizeGripLayer.Form.Panel;
        _grips.Show(form, IslandBox(), _header.Row, PanelHeader.PanelRadius, EffectiveStyle == CompactStyle.Line);
        if (form == ResizeGripLayer.Form.None && _hoverEdge != ResizeEdges.None && !Dragging) SetHoverEdge(ResizeEdges.None);
    }

    // From TrackPointer, which polls the cursor: arms the collapsed surface's end the pointer is at, and shows the open panel's
    // handles as the pointer nears them. Returns the grip under the pointer.
    private ResizeEdges TrackGrips(Point screen)
    {
        UpdateGrips();
        if (_grips.Shape == ResizeGripLayer.Form.None || PresentationSource.FromVisual(Stage) is null) return ResizeEdges.None;
        var point = Stage.PointFromScreen(screen);
        var edge = _grips.EdgeAt(point);
        if (_grips.Shape == ResizeGripLayer.Form.Compact) { _grips.Arm(edge); return edge; }
        var r = _grips.Rect;
        // The distance to the nearest grip: zero on one, growing inside the panel; nothing above the header.
        double near = 0;
        if (point.Y >= r.Y + _header.Row && point.X >= r.X - ResizeGrips.PanelOuter && point.X <= r.Right + ResizeGrips.PanelOuter && point.Y <= r.Bottom + ResizeGrips.PanelOuter)
        {
            double distance = Math.Max(0, Math.Min(Math.Min(point.X - r.X, r.Right - point.X), r.Bottom - point.Y) - ResizeGrips.PanelInner);
            near = 1 - Math.Clamp(distance / HintReach, 0, 1);
        }
        _grips.Near(near);
        // Messages reach the layer while it is on top; when another window has them, the polling still lights the edge.
        if (!_grips.IsMouseOver) SetHoverEdge(edge);
        return edge;
    }

    private bool NearGrips(Point screen)
    {
        if (!PanelResizable || PresentationSource.FromVisual(Stage) is null) return false;
        var area = ResizeGrips.PanelArea(IslandBox());
        var point = Stage.PointFromScreen(screen);
        return point.X >= area.X - GripSlack && point.X <= area.Right + GripSlack && point.Y >= area.Y + _header.Row && point.Y <= area.Bottom + GripSlack;
    }

    private void SetHoverEdge(ResizeEdges edge)
    {
        if (edge == _hoverEdge) return;
        _hoverEdge = edge;
        _grips.Cursor = edge switch
        {
            ResizeEdges.Bottom => Cursors.SizeNS, ResizeEdges.Left or ResizeEdges.Right => Cursors.SizeWE,
            ResizeEdges.Bottom | ResizeEdges.Left => Cursors.SizeNESW, ResizeEdges.Bottom | ResizeEdges.Right => Cursors.SizeNWSE, _ => null
        };
        if (!Resizing) _grips.Light(edge, false, "");
    }

    private void GripMove(object sender, MouseEventArgs e)
    {
        if (!Dragging) { if (!_resizeSettling) SetHoverEdge(_grips.EdgeAt(e.GetPosition(_grips))); return; }
        e.Handled = true;
        if (ResizeOvertaken()) return;
        var cursor = WindowsHost.Cursor();
        double scale = _pixelsPerDip > 0 ? _pixelsPerDip : 1;
        double dx = (cursor.X - _resizeStart.X) / scale, dy = (cursor.Y - _resizeStart.Y) / scale;
        // Both sides move, so the width changes by twice the pointer's move.
        double width = _resizeFrom.Width + (_resizeEdge.HasFlag(ResizeEdges.Right) ? 2 * dx : _resizeEdge.HasFlag(ResizeEdges.Left) ? -2 * dx : 0);
        if (_resizeCompact)
        {
            var (min, max) = CollapsedGeometry.WidthRange(EffectiveStyle, MonitorBoundsDip, WorkDip);
            _springWidth.Target = Spring.Resist(width, min, max);
        }
        else
        {
            double height = _resizeFrom.Height + (_resizeEdge.HasFlag(ResizeEdges.Bottom) ? dy : 0);
            _springWidth.Target = Spring.Resist(width, MonitorMinWidth, MonitorMaxWidth);
            _springHeight.Target = Spring.Resist(height, MonitorMinHeight, MonitorMaxHeight);
        }
        StartSpring();
    }

    private void GripDown(object sender, MouseButtonEventArgs e)
    {
        var edge = _grips.EdgeAt(e.GetPosition(_grips));
        if (edge == ResizeEdges.None) return;
        e.Handled = true;
        bool compact = _grips.Shape == ResizeGripLayer.Form.Compact;
        var style = EffectiveStyle;
        if (compact && e.ClickCount >= 2 && !_resizeSettling)
        {
            // A double click on an end lets the surface widen to its content again; the line returns to its usual length.
            var fitted = Settings.Copy();
            if (style == CompactStyle.Line) fitted.LineLength = CollapsedLimits.LineLengthDefault; else fitted.SetCompactAutoWidth(style, true);
            _app.UpdateSettings(fitted); return;
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
        _resizeCompact = compact; _resizeStyle = style; _resizeSettling = false;
        _resizeEdge = edge; _resizeStart = WindowsHost.Cursor();
        SetHoverEdge(edge);
        _grips.Light(edge, true, SizeLabel(_resizeFrom.Width, _resizeFrom.Height));
        // The window grows once to the largest size; from here on only the surface changes size.
        if (!_resizeHost) { _resizeHost = true; ApplySettings(); }
        _grips.CaptureMouse();
    }

    private string SizeLabel(double width, double height) => _resizeCompact ? $"{width:0}" : $"{width:0} × {height:0}";

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
        if (ResizeOvertaken()) return;
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

    // Something else opened or closed the panel during the drag or while it settled (a hotkey, a full-screen rule), or turned the
    // collapsed surface into another form (a maximized window turns it into the line): that wins. The drag ends where it is and
    // the size reached so far is kept; it is never applied to the other form.
    private bool ResizeOvertaken()
    {
        if (!Resizing || _resizeCompact != _expanded && (!_resizeCompact || EffectiveStyle == _resizeStyle)) return false;
        StopSpring();
        _resizeEdge = ResizeEdges.None; _resizeSettling = true;
        if (_grips.IsMouseCaptured) _grips.ReleaseMouseCapture();
        FinishResize();
        return true;
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
            _grips.Light(_resizeEdge == ResizeEdges.None ? _hoverEdge : _resizeEdge, true, SizeLabel(Collapsed.Surface.Width, 0));
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
        _grips.Light(_resizeEdge == ResizeEdges.None ? _hoverEdge : _resizeEdge, true, SizeLabel(width, height));
    }

    // The button is up: the spring settles where the pointer left it, and then the size is saved.
    private void EndResize()
    {
        var edge = _resizeEdge;
        _resizeEdge = ResizeEdges.None; _resizeSettling = true;
        if (_grips.IsMouseCaptured) _grips.ReleaseMouseCapture();
        _grips.Light(edge, true, "");
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
        // The label leaves; the edge stays lit while the pointer is still on it.
        _hoverEdge = ResizeEdges.None;
        UpdateGrips();
        var cursor = WindowsHost.Cursor();
        SetHoverEdge(TrackGrips(cursor));
        _grips.Light(_hoverEdge, false, "");
        if (!compact) PointerChanged(Island.IsMouseOver || InsideOpenPanel(cursor));
    }
}
