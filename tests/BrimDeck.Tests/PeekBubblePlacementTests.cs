using BrimDeck.Core;

internal static class PeekBubblePlacementTests
{
    private static bool Same(DipRect a, double x, double y, double w, double h)
        => Math.Abs(a.X - x) < 1e-6 && Math.Abs(a.Y - y) < 1e-6 && Math.Abs(a.Width - w) < 1e-6 && Math.Abs(a.Height - h) < 1e-6;

    internal static void Run(Action<string, bool> check)
    {
        var work = new DipRect(0, 0, 1920, 1080);
        // The line's default 56 x 4 four DIPs in from each edge, centred along it; a bubble of 200 x 38.
        var top = new DipRect(932, 4, 56, 4);
        var bottom = new DipRect(932, 1072, 56, 4);
        var left = new DipRect(4, 512, 4, 56);
        var right = new DipRect(1912, 512, 4, 56);
        const double gap = PeekBubblePlacement.Gap;

        check("The bubble lies on the inner side of a top indicator, centred on it", Same(PeekBubblePlacement.Place(top, ScreenEdge.Top, 200, 38, work, 1), 860, 4 + 4 + gap, 200, 38));
        check("The bubble lies above a bottom indicator", Same(PeekBubblePlacement.Place(bottom, ScreenEdge.Bottom, 200, 38, work, 1), 860, 1072 - gap - 38, 200, 38));
        check("The bubble lies right of a left indicator, centred on it", Same(PeekBubblePlacement.Place(left, ScreenEdge.Left, 200, 38, work, 1), 4 + 4 + gap, 521, 200, 38));
        check("The bubble lies left of a right indicator", Same(PeekBubblePlacement.Place(right, ScreenEdge.Right, 200, 38, work, 1), 1912 - gap - 200, 521, 200, 38));

        // Near the corners the bubble keeps within the work area along the edge.
        check("An indicator at the start of the top edge keeps the bubble inside the work area",
            Same(PeekBubblePlacement.Place(new DipRect(0, 4, 56, 4), ScreenEdge.Top, 200, 38, work, 1), 0, 14, 200, 38));
        check("An indicator at the end of the top edge keeps the bubble inside the work area",
            Same(PeekBubblePlacement.Place(new DipRect(1864, 4, 56, 4), ScreenEdge.Top, 200, 38, work, 1), 1720, 14, 200, 38));
        check("A left indicator near the bottom corner keeps the bubble inside the work area",
            Same(PeekBubblePlacement.Place(new DipRect(4, 1060, 4, 20), ScreenEdge.Left, 200, 38, work, 1), 14, 1042, 200, 38));
        check("A right indicator near the top corner keeps the bubble inside the work area",
            Same(PeekBubblePlacement.Place(new DipRect(1912, 0, 4, 20), ScreenEdge.Right, 200, 38, work, 1), 1706, 0, 200, 38));

        // A taskbar at the top: the indicator hangs over it, the bubble stays in the work area below.
        var taskbarTop = new DipRect(0, 40, 1920, 1040);
        check("A bubble that would lie over a top taskbar is moved down to the work area",
            Same(PeekBubblePlacement.Place(new DipRect(932, 0, 56, 4), ScreenEdge.Top, 200, 38, taskbarTop, 1), 860, 40, 200, 38));
        var taskbarBottom = new DipRect(0, 0, 1920, 1032);
        check("A bubble that would lie over a bottom taskbar is moved up to the work area",
            Same(PeekBubblePlacement.Place(new DipRect(932, 1076, 56, 4), ScreenEdge.Bottom, 200, 38, taskbarBottom, 1), 860, 1032 - 38, 200, 38));

        // Larger than the room: cut to the work area.
        check("A bubble wider and taller than the work area is cut to it",
            Same(PeekBubblePlacement.Place(top, ScreenEdge.Top, 3000, 2000, work, 1), 0, 0, 1920, 1080));
        check("A bubble wider than the work area keeps its depth and the gap",
            Same(PeekBubblePlacement.Place(top, ScreenEdge.Top, 3000, 38, work, 1), 0, 14, 1920, 38));

        // Device pixels: sizes grow to the next pixel, positions are whole pixels.
        var scaled = PeekBubblePlacement.Place(top, ScreenEdge.Top, 200.2, 38, work, 1.5);
        check("At 150 percent the size is whole device pixels and the position stays centred",
            Math.Abs(scaled.Width * 1.5 - Math.Round(scaled.Width * 1.5)) < 1e-6 && Math.Abs(scaled.X * 1.5 - Math.Round(scaled.X * 1.5)) < 1e-6
            && Math.Abs(scaled.Y * 1.5 - Math.Round(scaled.Y * 1.5)) < 1e-6 && scaled.Width >= 200.2 && scaled.Width < 200.2 + 1 / 1.5 + 1e-6 && Math.Abs(scaled.X + scaled.Width / 2 - 960) <= 1 / 1.5);
        var doubled = PeekBubblePlacement.Place(top, ScreenEdge.Top, 200, 38, work, 2);
        check("At 200 percent a whole-pixel bubble keeps its size", Same(doubled, 860, 14, 200, 38));
        check("A scale that is not a number is taken as 1", Same(PeekBubblePlacement.Place(top, ScreenEdge.Top, 200, 38, work, double.NaN), 860, 14, 200, 38)
            && Same(PeekBubblePlacement.Place(top, ScreenEdge.Top, 200, 38, work, 0), 860, 14, 200, 38));

        // A smaller monitor: the work area is its own.
        var smallWork = new DipRect(0, 0, 1280, 720);
        check("A smaller monitor keeps the bubble within its own work area",
            Same(PeekBubblePlacement.Place(new DipRect(1250, 4, 56, 4), ScreenEdge.Top, 200, 38, smallWork, 1), 1080, 14, 200, 38));

        // Nothing known about the work area, or about the size: no clamping, no size.
        check("Without a usable work area the bubble is only centred", Same(PeekBubblePlacement.Place(top, ScreenEdge.Top, 200, 38, new DipRect(0, 0, 0, 0), 1), 860, 14, 200, 38));
        check("A size that is not a number is no size", PeekBubblePlacement.Place(top, ScreenEdge.Top, double.NaN, -5, work, 1) is { Width: 0, Height: 0 });

        // The pointer's way from the indicator to the bubble.
        var topBubble = PeekBubblePlacement.Place(top, ScreenEdge.Top, 200, 38, work, 1);
        var between = PeekBubblePlacement.Between(top, ScreenEdge.Top, topBubble);
        check("The room between a top indicator and its bubble spans both along the edge", Same(between, 860, 8, 200, gap));
        check("The room between a bottom indicator and its bubble", Same(PeekBubblePlacement.Between(bottom, ScreenEdge.Bottom, PeekBubblePlacement.Place(bottom, ScreenEdge.Bottom, 200, 38, work, 1)), 860, 1066, 200, gap));
        check("The room between a left indicator and its bubble", Same(PeekBubblePlacement.Between(left, ScreenEdge.Left, PeekBubblePlacement.Place(left, ScreenEdge.Left, 200, 38, work, 1)), 8, 512, gap, 56));
        check("The room between a right indicator and its bubble", Same(PeekBubblePlacement.Between(right, ScreenEdge.Right, PeekBubblePlacement.Place(right, ScreenEdge.Right, 200, 38, work, 1)), 1906, 512, gap, 56));
        check("The pointer is held on the bubble and in the room between, not beside them",
            PeekBubblePlacement.Holds(topBubble, between, 900, 30) && PeekBubblePlacement.Holds(topBubble, between, 900, 10) && !PeekBubblePlacement.Holds(topBubble, between, 850, 30)
            && !PeekBubblePlacement.Holds(topBubble, between, 900, 60) && !PeekBubblePlacement.Holds(topBubble, between, 900, 7));
        var touching = PeekBubblePlacement.Between(new DipRect(932, 4, 56, 4), ScreenEdge.Top, new DipRect(860, 8, 200, 38));
        check("A bubble touching the indicator leaves no room between", touching.Height == 0 && !PeekBubblePlacement.Holds(new DipRect(860, 8, 200, 38), touching, 900, 6));

        // The peek's state under a bubble: nothing ends a peek that is not showing, and a bubble that could not be shown ends it.
        var peek = new WindowPeek();
        check("A peek that is not showing cannot end or be closed", !peek.End() && !peek.PanelClosed() && !peek.Resting);
        check("A bubble that could not be shown ends the peek and the line rests until the pointer leaves", peek.Begin() && peek.End() && peek.Resting && !peek.Begin());
    }
}
