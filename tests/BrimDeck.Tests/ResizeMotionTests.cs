using BrimDeck.Core;

// Resizing the monitor page by dragging: a frame every 16 ms, a few DIPs at a time. Within a layout step every part keeps its place
// and size except the graph, which takes exactly what the drag adds; a step is taken only past the hysteresis band, once per
// crossing, and never back and forth at a step; the readings' foot holds during the drag and eases to the list's afterwards,
// never moving more than a little between two frames.
static class ResizeMotionTests
{
    private const double Frame = 1 / 60.0, Step = 2;

    public static void Run(Action<string, bool> check)
    {
        Hysteresis(check);
        Parts(check);
        Foot(check);
    }

    private static List<MonitorLayout> Sweep(IEnumerable<double> heights, double width, int drives, bool hysteresis)
    {
        MonitorLayout? shown = null;
        var layouts = new List<MonitorLayout>();
        foreach (double height in heights)
        {
            shown = hysteresis ? MonitorLayout.For(width, height, true, drives, shown) : MonitorLayout.For(width, height, true, drives);
            layouts.Add(shown);
        }
        return layouts;
    }
    private static int Changes(List<MonitorLayout> layouts) => layouts.Zip(layouts.Skip(1)).Count(pair => pair.First != pair.Second);
    private static IEnumerable<double> Range(double from, double to, double step)
    {
        for (double value = from; step > 0 ? value <= to : value >= to; value += step) yield return value;
    }

    private static void Hysteresis(Action<string, bool> check)
    {
        foreach (int drives in new[] { 0, 2, 5 })
            foreach (double width in new[] { 450.0, 700 })
            {
                var up = Range(100, 420, Step).ToList();
                int plain = Changes(Sweep(up, width, drives, hysteresis: false)), held = Changes(Sweep(up, width, drives, hysteresis: true));
                check($"Resize ({width:0}, {drives} drives): a drag across every step takes each step once", held <= plain && held > 0);
                var down = Sweep(Range(420, 100, -Step), width, drives, hysteresis: true);
                check($"Resize ({width:0}, {drives} drives): the steps are the same both ways at the ends", down[^1] == MonitorLayout.For(width, 100, true, drives) || Changes(down) <= plain);
                // Holding the size at each step and wobbling a few DIPs never takes the step back.
                bool steady = true;
                foreach (double edge in up.Where((height, i) => i > 0 && MonitorLayout.For(width, height, true, drives) != MonitorLayout.For(width, up[i - 1], true, drives)))
                {
                    var wobble = Sweep(new[] { edge - 20, edge + 2 * MonitorLayout.Hysteresis }.Concat(Enumerable.Range(0, 30).Select(i => edge + (i % 2 == 0 ? 3 : -3))), width, drives, hysteresis: true);
                    steady &= Changes(wobble.Skip(4).ToList()) == 0;
                }
                check($"Resize ({width:0}, {drives} drives): a size held at a step does not flip back and forth", steady);
            }
        check("Resize: far from a step the layout is the step's own", MonitorLayout.For(700, 330, true, 2, MonitorLayout.For(700, 140, true, 2)) == MonitorLayout.For(700, 330, true, 2)
            && MonitorLayout.For(700, 330, true, 2, null) == MonitorLayout.For(700, 330, true, 2));
    }

    // Between two frames of a drag within a step: every part above the graph stays, the graph grows or shrinks by what the drag
    // gives (the foot holding), and the parts under it move with the page's foot by the same amount.
    private static void Parts(Action<string, bool> check)
    {
        foreach (int drives in new[] { 0, 1, 2, 3 })
        {
            bool smooth = true; int steps = 0;
            MonitorLayout? shown = null; IReadOnlyList<(double Top, double Height)>? before = null; double previous = 0;
            foreach (double height in Range(110, 400, Step))
            {
                var layout = MonitorLayout.For(700, height, true, drives, shown);
                var parts = layout.Parts(height);
                if (layout != shown) { steps++; shown = layout; before = parts; previous = height; continue; }
                double grown = height - previous;
                smooth &= parts[0] == before![0] && Math.Abs(parts[1].Height - before[1].Height - grown) < 1e-9 && parts[1].Top == before[1].Top
                    && Math.Abs(parts[2].Top - before[2].Top - grown) < 1e-9 && parts[2].Height == before[2].Height
                    && Math.Abs(parts[3].Top - before[3].Top - grown) < 1e-9 && parts[3].Height == before[3].Height;
                before = parts; previous = height;
            }
            check($"Resize parts ({drives} drives): within a step only the graph stretches, by what each frame adds", smooth && steps > 1);
        }
        var page = MonitorLayout.For(700, 290, true, 2);
        var held = page.Parts(290, foot: 10);
        check("Resize parts: the foot gives its room from the graph", Math.Abs(page.Parts(290)[1].Height - held[1].Height - 10) < 1e-9 && held[3].Height == page.Parts(290)[3].Height);
        check("Resize parts: the drives' lines take their rows, tighter than the list's", page.StorageLines == 2 && held[3].Height == 8 + 2 * page.StorageRow && page.StorageRow < page.RowHeight);
    }

    // The list's foot steps by a row as the page grows; the readings' foot holds through the drag, then eases there in small steps.
    private static void Foot(Action<string, bool> check)
    {
        var foot = new FootAlignment();
        double now = 100, row = 22, listHead = MonitorLayout.ListHead;
        double ListFoot(double height) => height - listHead - Math.Floor((height - listHead) / row) * row;
        foot.Resize(ListFoot(200), now);
        check("Foot: the first size is taken at once", Math.Abs(foot.At(now) - ListFoot(200)) < 1e-9);
        double start = foot.At(now), most = 0, last = start;
        bool held = true;
        for (double height = 200; height <= 320; height += Step)
        {
            now += Frame;
            foot.Resize(ListFoot(height), now);
            foot.Step(now, motion: true);
            held &= foot.At(now) == start;
        }
        check("Foot: it holds while the page is being resized, so the readings stretch with the drag", held);
        double target = ListFoot(320);
        int frames = 0;
        while (foot.Step(now += Frame, motion: true) && frames++ < 200)
        {
            double at = foot.At(now);
            most = Math.Max(most, Math.Abs(at - last)); last = at;
        }
        check("Foot: once the size settles it eases to the list's foot", Math.Abs(foot.At(now) - target) < 1e-6 && frames > 5);
        check("Foot: no frame moves it by more than 2 DIP", most <= 2);
        var still = new FootAlignment();
        still.Resize(5, 0); still.Resize(15, 0.01);
        while (still.Step(0.01 + FootAlignment.Settle, motion: false)) { }
        check("Foot: without animations it goes there at once after the drag", Math.Abs(still.At(1) - 15) < 1e-9);
    }
}
