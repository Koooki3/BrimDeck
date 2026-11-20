namespace BrimDeck.Core;

// Where the readout bubble of an indicator that peeks (WindowBehavior.Peek, WindowPeek) sits. The bubble is a small window of its
// own beside the indicator; the panel's collapsed surface never moves for it. Every rectangle is in DIPs relative to the monitor's
// top-left corner (the screen's frame, not the edge frame of EdgeFrame): the indicator is EdgeFrame.ToScreen of the line's surface.
public static class PeekBubblePlacement
{
    // The space between the indicator and the bubble, in DIPs.
    public const double Gap = 6;

    // The bubble on the indicator's inner side (below a top indicator, above a bottom one, right of a left one, left of a right one),
    // centred on the indicator along its edge, then moved to lie within the work area. A bubble larger than the work area is cut to
    // it. Whole device pixels (pixelsPerDip): the position rounds, the size grows to the next pixel unless the work area has no room.
    public static DipRect Place(DipRect indicator, ScreenEdge edge, double width, double height, DipRect work, double pixelsPerDip)
    {
        double ppd = pixelsPerDip > 0 && double.IsFinite(pixelsPerDip) ? pixelsPerDip : 1;
        double w = Size(width), h = Size(height);
        bool bounded = double.IsFinite(work.X) && double.IsFinite(work.Y) && double.IsFinite(work.Width) && double.IsFinite(work.Height) && work.Width > 0 && work.Height > 0;
        double left = bounded ? Math.Ceiling(work.X * ppd - 1e-6) : double.NegativeInfinity, top = bounded ? Math.Ceiling(work.Y * ppd - 1e-6) : double.NegativeInfinity;
        double right = bounded ? Math.Floor(work.Right * ppd + 1e-6) : double.PositiveInfinity, bottom = bounded ? Math.Floor(work.Bottom * ppd + 1e-6) : double.PositiveInfinity;
        double pw = Math.Min(Math.Ceiling(w * ppd - 1e-6), Math.Max(0, right - left)), ph = Math.Min(Math.Ceiling(h * ppd - 1e-6), Math.Max(0, bottom - top));
        double gap = Gap * ppd, cx = (indicator.X + indicator.Width / 2) * ppd, cy = (indicator.Y + indicator.Height / 2) * ppd;
        double x, y;
        switch (edge)
        {
            case ScreenEdge.Bottom: x = cx - pw / 2; y = indicator.Y * ppd - gap - ph; break;
            case ScreenEdge.Left: x = indicator.Right * ppd + gap; y = cy - ph / 2; break;
            case ScreenEdge.Right: x = indicator.X * ppd - gap - pw; y = cy - ph / 2; break;
            default: x = cx - pw / 2; y = indicator.Bottom * ppd + gap; break;
        }
        x = Math.Round(x); y = Math.Round(y);
        if (bounded) { x = Math.Clamp(x, left, Math.Max(left, right - pw)); y = Math.Clamp(y, top, Math.Max(top, bottom - ph)); }
        return new(x / ppd, y / ppd, pw / ppd, ph / ppd);
    }

    // The room between the indicator and the bubble, across the span both cover along the edge: the pointer going from one to the
    // other stays on the peek. Empty (zero thickness) when they touch or overlap.
    public static DipRect Between(DipRect indicator, ScreenEdge edge, DipRect bubble)
    {
        double along0, along1, across0, across1;
        switch (edge)
        {
            case ScreenEdge.Bottom: (along0, along1) = (Math.Min(indicator.X, bubble.X), Math.Max(indicator.Right, bubble.Right)); (across0, across1) = (bubble.Bottom, indicator.Y); break;
            case ScreenEdge.Left: (along0, along1) = (Math.Min(indicator.Y, bubble.Y), Math.Max(indicator.Bottom, bubble.Bottom)); (across0, across1) = (indicator.Right, bubble.X); break;
            case ScreenEdge.Right: (along0, along1) = (Math.Min(indicator.Y, bubble.Y), Math.Max(indicator.Bottom, bubble.Bottom)); (across0, across1) = (bubble.Right, indicator.X); break;
            default: (along0, along1) = (Math.Min(indicator.X, bubble.X), Math.Max(indicator.Right, bubble.Right)); (across0, across1) = (indicator.Bottom, bubble.Y); break;
        }
        double thickness = Math.Max(0, across1 - across0);
        return edge is ScreenEdge.Left or ScreenEdge.Right ? new(across0, along0, thickness, along1 - along0) : new(along0, across0, along1 - along0, thickness);
    }

    // Whether a point (DIPs from the monitor's corner) is on the bubble or in the room between it and the indicator.
    public static bool Holds(DipRect bubble, DipRect between, double x, double y)
        => Contains(bubble, x, y) || between.Width > 0 && between.Height > 0 && Contains(between, x, y);

    private static bool Contains(DipRect r, double x, double y) => x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;
    private static double Size(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
