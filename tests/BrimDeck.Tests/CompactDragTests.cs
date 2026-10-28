using BrimDeck.Core;

// The collapsed row while its surface is dragged (CompactSpacing.ShareFlowing, CutFade): the windows of the rings and the readings
// follow the room one for one instead of stepping by whole items, so the row moves with the pointer, as 1.2.1's did, and what
// jumped (a window by an item, the gaps and the centring taking the difference back) does not. At the rooms where the whole-item
// windows stand (Share), the flowing ones are the same.
internal static class CompactDragTests
{
    private static readonly double[] Rings = [12, 12, 12, 12];
    private static readonly double[] Readings = [48, 58, 52, 50, 58, 52, 56];
    private const double RingGap = 8, ReadingGap = 10;

    internal static void Run(Action<string, bool> check)
    {
        Flowing(check);
        Cut(check);
        Drag(check);
    }

    private static void Flowing(Action<string, bool> check)
    {
        bool grows = true, continuous = true, fits = true, wider = true, same = true, stepped = false;
        double previousFirst = 0, previousSecond = 0, previousWhole = 0;
        for (double room = 0; room <= 520; room += .5)
        {
            var flowing = CompactSpacing.ShareFlowing(Rings, RingGap, Readings, ReadingGap, room);
            var whole = CompactSpacing.Share(Rings, RingGap, Readings, ReadingGap, room);
            double a = flowing.First.Window, b = flowing.Second.Window;
            grows &= a >= previousFirst - 1e-9 && b >= previousSecond - 1e-9;
            // One for one: together the windows never move by more than the room did.
            continuous &= room == 0 || a + b - previousFirst - previousSecond <= .5 + 1e-9;
            fits &= a + b <= room + 1e-9 && a >= 0 && b >= 0;
            // Wider than the whole-item windows, never narrower.
            wider &= a + b >= whole.First.Window + whole.Second.Window - 1e-9;
            // Where the whole-item windows fill the room exactly, they are the flowing ones.
            if (whole.First.Count > 0 && whole.Second.Count > 0 && Math.Abs(whole.First.Window + whole.Second.Window - room) < 1e-9)
                same &= Math.Abs(a - whole.First.Window) < 1e-9 && Math.Abs(b - whole.Second.Window) < 1e-9;
            stepped |= room > 0 && whole.First.Window + whole.Second.Window - previousWhole > 20;
            previousFirst = a; previousSecond = b; previousWhole = whole.First.Window + whole.Second.Window;
        }
        check("Dragging: the windows only grow as the room grows", grows);
        check("Dragging: the windows grow one for one with the room, never by a step", continuous);
        check("Dragging: the windows fit in the room", fits);
        check("Dragging: the windows are never narrower than the whole-item windows of the rest", wider);
        check("Dragging: where the whole-item windows fill the room exactly, the windows are the same", same);
        check("Dragging: the whole-item windows do step by an item as the room grows (what the flowing windows spare the row)", stepped);
        var none = CompactSpacing.ShareFlowing([], 8, [], 8, 100);
        check("Dragging: nothing to show takes no room", none.First.Window == 0 && none.Second.Window == 0);
        var only = CompactSpacing.ShareFlowing([], 8, Readings, ReadingGap, 90);
        check("Dragging: readings alone take the room", Math.Abs(only.Second.Window - 90) < 1e-9 && only.First.Window == 0);
        var all = CompactSpacing.ShareFlowing(Rings, RingGap, Readings, ReadingGap, 5000);
        double ringsAll = Rings.Sum() + (Rings.Length - 1) * RingGap, readingsAll = Readings.Sum() + (Readings.Length - 1) * ReadingGap;
        check("Dragging: with room to spare every item shows whole", Math.Abs(all.First.Window - ringsAll) < 1e-9 && Math.Abs(all.Second.Window - readingsAll) < 1e-9
            && all.First.Count == Rings.Length && all.Second.Count == Readings.Length);
    }

    private static void Cut(Action<string, bool> check)
    {
        double[] items = [40, 30, 50];
        check("Cut fade: none where the window ends in a gap or at an item's edge", CompactSpacing.CutFade(items, 10, 40, 6) == 0 && CompactSpacing.CutFade(items, 10, 45, 6) == 0
            && CompactSpacing.CutFade(items, 10, 80, 6) == 0 && CompactSpacing.CutFade(items, 10, 0, 6) == 0);
        check("Cut fade: the width of the cut item's part shown or hidden when that is less than the edge",
            Math.Abs(CompactSpacing.CutFade(items, 10, 53, 6) - 3) < 1e-9 && Math.Abs(CompactSpacing.CutFade(items, 10, 78, 6) - 2) < 1e-9);
        check("Cut fade: the edge's width in the middle of an item", Math.Abs(CompactSpacing.CutFade(items, 10, 70, 6) - 6) < 1e-9);
        double worst = 0, last = 0;
        for (double window = 0; window <= 190; window += .25) { double fade = CompactSpacing.CutFade(items, 10, window, 6); worst = Math.Max(worst, Math.Abs(fade - last)); last = fade; }
        check("Cut fade: it never appears or goes in a step, only as fast as the window moves", worst <= .25 + 1e-9);
    }

    // The collapsed row of readings and the clock as the surface is dragged: the surface follows a ramp on the drag's spring, the
    // readings take the room that is left, and the clock stands at the row's end. What the user sees is where the readings' window ends and
    // where the clock starts, frame by frame.
    private static void Drag(Action<string, bool> check)
    {
        double clock = 36, unit = 8;
        (double ReadingsRight, double ClockLeft) Row(double width, bool flowing)
        {
            double pool = Math.Max(0, width - clock - unit);
            var shared = flowing ? CompactSpacing.ShareFlowing([], RingGap, Readings, ReadingGap, pool).Second : CompactSpacing.Share([], RingGap, Readings, ReadingGap, pool).Second;
            var place = CompactSpacing.Arrange([new(shared.Window), new(clock)], -1, 0, 0, width, unit, 1, 1);
            return (place.Left[0] + place.Room[0], place.Left[1]);
        }
        foreach (var (name, rampMs) in new[] { ("slow", 1500), ("quick", 300) })
        {
            double jumpFlowing = 0, jumpWhole = 0;
            foreach (bool flowing in new[] { true, false })
            {
                var spring = new Spring(220);
                var previous = Row(220, flowing); double previousWidth = 220, worst = 0;
                double frame = 1.0 / 144;
                for (int i = 0; i < 2 * 144 * 3; i++)
                {
                    double t = i * frame, phase = t % (2 * rampMs / 1000.0);
                    double ramp = phase < rampMs / 1000.0 ? phase / (rampMs / 1000.0) : 2 - phase / (rampMs / 1000.0);
                    spring.Target = 220 + 360 * ramp;
                    spring.Step(frame);
                    var now = Row(Math.Round(spring.Value), flowing);
                    double moved = Math.Abs(Math.Round(spring.Value) - previousWidth);
                    worst = Math.Max(worst, Math.Max(Math.Abs(now.ReadingsRight - previous.ReadingsRight), Math.Abs(now.ClockLeft - previous.ClockLeft)) - moved);
                    previous = now; previousWidth = Math.Round(spring.Value);
                }
                if (flowing) jumpFlowing = worst; else jumpWhole = worst;
            }
            check($"Dragging ({name}): from frame to frame the readings' end and the clock move no more than the surface did, within 2 DIPs (the window follows the room)", jumpFlowing <= 2);
            check($"Dragging ({name}): whole-item windows jump the row by more than 10 DIPs in a frame (what the flowing windows replace)", jumpWhole > 10);
        }
    }
}
