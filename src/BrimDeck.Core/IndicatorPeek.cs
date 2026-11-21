namespace BrimDeck.Core;

// The indicator that opens into the capsule or the notch (DeckSettings.LineOpens): hovering the line, or clicking it with
// ClickToExpand, turns it into that style in its place (IndicatorCapsule), read-only; a click on it opens the panel, and leaving it
// turns it back into the line. It holds no timers; the window starts its open and close delays and tells this what happened.
//   Line      the indicator at rest
//   Active    the capsule or the notch shows in the indicator's place, and the panel a click on it opens
// After it ends, the pointer may still be on the line: the line rests until the pointer has left it, so that it does not
// open again at once.
public sealed class IndicatorPeek
{
    public bool Active { get; private set; }
    public bool Resting { get; private set; }
    // The pointer is on the indicator and the open delay has run out, or it was clicked: whether the capsule or the notch is to be shown.
    public bool Begin()
    {
        if (Active || Resting) return false;
        Active = true; return true;
    }
    // The pointer has been away from the capsule or the notch for the close delay, or the panel opened from it has closed: back to the line,
    // which rests until the pointer has left it.
    public bool End()
    {
        if (!Active) return false;
        Active = false; Resting = true; return true;
    }
    // Each pointer check at rest: a resting line wakes once the pointer is off it.
    public void Observe(bool overIndicator)
    {
        if (Resting && !overIndicator) Resting = false;
    }
    // The indicator no longer opens into the capsule or the notch (a setting, a window rule, the panels hidden).
    public void Reset() { Active = false; Resting = false; }
}

// Where the capsule or the notch an indicator opens into sits. Every rectangle is in DIPs relative to the monitor's top-left corner (the
// screen's frame, not the edge frame of EdgeFrame).
public static class IndicatorCapsule
{
    // The capsule against the indicator's edge, gap from the work area's side there (at most CollapsedHover.BridgeMaxGap, so a
    // capsule at the top or the bottom keeps its bridge to the edge), centred on the indicator along the edge and kept within the
    // work area. A capsule larger than the work area is cut to it.
    public static DipRect Place(DipRect line, ScreenEdge edge, double width, double height, double gap, DipRect work)
    {
        if (!(work.Width > 0) || !(work.Height > 0) || !double.IsFinite(work.X) || !double.IsFinite(work.Y)) return new(0, 0, 0, 0);
        double w = Math.Min(Size(width), work.Width), h = Math.Min(Size(height), work.Height);
        double g = double.IsFinite(gap) ? Math.Clamp(gap, 0, CollapsedHover.BridgeMaxGap) : 0;
        double cx = Finite(line.X + line.Width / 2, work.X + work.Width / 2), cy = Finite(line.Y + line.Height / 2, work.Y + work.Height / 2);
        return edge switch
        {
            ScreenEdge.Bottom => new(work.X + Along(cx - work.X, w, work.Width), Math.Max(work.Y, work.Bottom - g - h), w, h),
            ScreenEdge.Left => new(Math.Min(work.X + g, work.Right - w), work.Y + Along(cy - work.Y, h, work.Height), w, h),
            ScreenEdge.Right => new(Math.Max(work.X, work.Right - g - w), work.Y + Along(cy - work.Y, h, work.Height), w, h),
            _ => new(work.X + Along(cx - work.X, w, work.Width), Math.Min(work.Y + g, work.Bottom - h), w, h)
        };
    }

    // Where a surface of this size starts along a span of this length to be centred on centre, kept within the span; a surface
    // longer than the span starts at its beginning. Used for the notch along the line's edge too.
    public static double Along(double centre, double size, double span)
    {
        double free = double.IsFinite(span) && double.IsFinite(size) ? Math.Max(0, span - size) : 0;
        return double.IsFinite(centre) && double.IsFinite(size) ? Math.Clamp(centre - size / 2, 0, free) : free / 2;
    }

    // Where the line senses the pointer (CollapsedHover.Zone), on the screen: the line, a little around it, and the strip between it
    // and the screen's edge. line is in the frame of its edge.
    public static DipRect LineReach(EdgeFrame frame, DipRect line) => frame.ToScreen(CollapsedHover.Zone(CompactStyle.Line, line, 0));

    // The area that keeps the capsule while the pointer is in it: the capsule and the indicator it came from, and the room between
    // them, so the pointer that opened it from the line does not have to reach the capsule first. line is the line's LineReach, so
    // the pointer that opened it from the strip between the line and the screen's edge keeps it too.
    public static DipRect Holds(DipRect line, DipRect capsule)
    {
        if (!(line.Width > 0) || !(line.Height > 0)) return capsule;
        double left = Math.Min(line.X, capsule.X), top = Math.Min(line.Y, capsule.Y);
        return new(left, top, Math.Max(line.Right, capsule.Right) - left, Math.Max(line.Bottom, capsule.Bottom) - top);
    }

    private static double Size(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
