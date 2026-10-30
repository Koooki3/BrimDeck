namespace BrimDeck.Core;

// The panel's own coordinates for the edge it hangs from. Everything the collapsed shape, the open panel and their animations
// compute (CollapsedGeometry, CollapsedHover, MainWindow.SetExpanded) is laid out as if the panel hung from the top of the
// screen: x runs along the edge and y away from it, into the screen. The frame maps that onto the edge actually used, and the
// window draws through the same mapping (a layout transform on its content), with the island's content turned back upright.
// - Top: the frame is the monitor, unchanged; the notch sits against the monitor's top, over a top taskbar as before.
// - Bottom: y runs up from the edge, x stays left to right.
// - Left: x runs down the edge, y right from it; the frame is the monitor mirrored about its diagonal.
// - Right: x runs down the edge, y left from it; the frame is the monitor turned a quarter clockwise.
// On the bottom, left and right the frame starts at the work area's edge, so the notch hangs from a taskbar there rather than
// covering it. The frame's along span is the monitor's (the notch may be as long as the edge); its depth reaches from that
// edge to the opposite side of the monitor.
// Rectangles are in DIPs relative to the monitor's top-left corner (screen) or to the frame's origin (frame).
public readonly record struct EdgeFrame(ScreenEdge Edge, double ScreenWidth, double ScreenHeight, double Line)
{
    // The work area is the monitor's, relative to its top-left corner.
    public static EdgeFrame For(ScreenEdge edge, double screenWidth, double screenHeight, DipRect work)
    {
        double w = Finite(screenWidth), h = Finite(screenHeight);
        bool valid = double.IsFinite(work.X) && double.IsFinite(work.Y) && double.IsFinite(work.Width) && double.IsFinite(work.Height) && work.Width > 0 && work.Height > 0;
        double line = !valid ? edge switch { ScreenEdge.Bottom => h, ScreenEdge.Right => w, _ => 0 } : edge switch
        {
            ScreenEdge.Bottom => Math.Clamp(work.Bottom, 0, h),
            ScreenEdge.Left => Math.Clamp(work.X, 0, w),
            ScreenEdge.Right => Math.Clamp(work.Right, 0, w),
            _ => 0
        };
        return new(Enum.IsDefined(edge) ? edge : ScreenEdge.Top, w, h, line);
    }

    // The left and right edges: the frame's x runs down the screen, so its width is the screen's height.
    public bool Across => Edge is ScreenEdge.Left or ScreenEdge.Right;

    // The monitor in the frame, cut at the frame's edge: (0, 0, along, depth).
    public DipRect Monitor => Edge switch
    {
        ScreenEdge.Bottom => new(0, 0, ScreenWidth, Line),
        ScreenEdge.Left => new(0, 0, ScreenHeight, Math.Max(0, ScreenWidth - Line)),
        ScreenEdge.Right => new(0, 0, ScreenHeight, Line),
        _ => new(0, 0, ScreenWidth, ScreenHeight)
    };

    // A screen rectangle in the frame.
    public DipRect FromScreen(DipRect r) => Edge switch
    {
        ScreenEdge.Bottom => new(r.X, Line - r.Bottom, r.Width, r.Height),
        ScreenEdge.Left => new(r.Y, r.X - Line, r.Height, r.Width),
        ScreenEdge.Right => new(r.Y, Line - r.Right, r.Height, r.Width),
        _ => r
    };

    // A frame rectangle on the screen.
    public DipRect ToScreen(DipRect r) => Edge switch
    {
        ScreenEdge.Bottom => new(r.X, Line - r.Bottom, r.Width, r.Height),
        ScreenEdge.Left => new(r.Y + Line, r.X, r.Height, r.Width),
        ScreenEdge.Right => new(Line - r.Bottom, r.X, r.Height, r.Width),
        _ => r
    };

    // A movement on the screen (a drag) as a movement in the frame.
    public (double X, double Y) FromScreen(double dx, double dy) => Edge switch
    {
        ScreenEdge.Bottom => (dx, -dy),
        ScreenEdge.Left => (dy, dx),
        ScreenEdge.Right => (dy, -dx),
        _ => (dx, dy)
    };

    // A direction in the frame as a direction on the screen; the inverse of the movement above.
    public (double X, double Y) ToScreen(double fx, double fy) => Edge switch
    {
        ScreenEdge.Bottom => (fx, -fy),
        ScreenEdge.Left => (fy, fx),
        ScreenEdge.Right => (-fy, fx),
        _ => (fx, fy)
    };

    // The linear part of the frame-to-screen mapping as a matrix (x' = x M11 + y M21, y' = x M12 + y M22). A layout transform
    // places its result at the origin itself, so the translation is left out. Turned is the inverse, which sets content upright.
    public (double M11, double M12, double M21, double M22) Matrix => Edge switch
    {
        ScreenEdge.Bottom => (1, 0, 0, -1),
        ScreenEdge.Left => (0, 1, 1, 0),
        ScreenEdge.Right => (0, 1, -1, 0),
        _ => (1, 0, 0, 1)
    };
    public (double M11, double M12, double M21, double M22) Upright => Edge switch
    {
        ScreenEdge.Bottom => (1, 0, 0, -1),
        ScreenEdge.Left => (0, 1, 1, 0),
        ScreenEdge.Right => (0, -1, 1, 0),
        _ => (1, 0, 0, 1)
    };

    private static double Finite(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
