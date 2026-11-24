using System.Diagnostics;
using BrimDeck.Core;

// V5 S02: a capture of the area an opening covers is judged by what the panel covered while the capture ran. The capture is a
// hand-completed task and the clock the test's own; the panel's side is as MainWindow does it (heights as they are laid out,
// the read through GlassOpeningSession, the area cut by GlassReachHistory.Covered and GlassSceneSnapshot.Uncovered).
internal static class GlassReachHistoryTests
{
    private const double Margin = 24, Closed = 32, Open = 200;
    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    private readonly record struct Capture(long Started, long Ended, GlassSceneSnapshot Scene);

    private sealed class Panel
    {
        public readonly GlassReachHistory Reach = new();
        public readonly GlassOpeningSession Session = new();
        private readonly GlassReadBudget<object> _budget = new();
        private readonly object _key = new();
        public double Height = Closed;
        public (int X, int Y, int Width, int Height) Area = (0, 70, 650, 180);
        public GlassSceneSnapshot? Judged;
        public GlassOpeningSession.Outcome? Last;

        public void Layout(double ms, double height) { Height = height; Reach.Add(Ms(ms), height); }

        // An opening's intent and its read; the returned task is the capture.
        public TaskCompletionSource<Capture?> Begin(double ms)
        {
            Session.Intent();
            Reach.Add(Ms(ms), Height);
            Judged = null; Last = null;
            double from = Height + Margin;
            var capture = new TaskCompletionSource<Capture?>();
            if (Session.Begin(1, Area.X, Area.Y, Area.Width, Area.Height, 1.25, 0) is not { } ticket) throw new InvalidOperationException("a read is running");
            _ = Session.Read(ticket, _budget, _key, () => capture.Task, () => (1, 1.25, Area), (outcome, read) =>
            {
                Last = outcome;
                if (outcome == GlassOpeningSession.Outcome.Applied && read is { } r)
                    Judged = r.Scene.Uncovered(GlassSceneSnapshot.Side.Top, Reach.Covered(r.Started, r.Ended, Margin, from, Open));
            }, () => { });
            return capture;
        }
    }

    // A white page whose nearest rows show the panel's own dark glass.
    private static GlassSceneSnapshot Page(int own)
    {
        var rgb = new byte[24 * 24 * 3];
        Array.Fill(rgb, (byte)255);
        Array.Fill(rgb, (byte)30, 0, own * 24 * 3);
        return GlassSceneSnapshot.FromPixels(rgb, 24, 24)!;
    }

    private static bool TurnsLight(GlassSceneSnapshot? scene) => scene is not null && new GlassAppearance(true, true).Open(scene, 10_000);

    internal static void Run(Action<string, bool> check)
    {
        // Captured early, answered late: by the answer the panel covers all of the area, but the capture shows it untouched.
        var late = new Panel();
        late.Layout(-1000, Closed);
        var capture = late.Begin(0);
        late.Layout(8, Closed + 4);
        foreach (var (ms, height) in new[] { (25.0, 90.0), (42, 150), (60, 190), (300, 200) }) late.Layout(ms, height);
        capture.SetResult(new(Ms(9), Ms(16), Page(0)));
        check("A cover capture answered after the panel grew over the area is judged by what the panel covered while it ran",
            late.Last == GlassOpeningSession.Outcome.Applied && late.Judged is { Visible: > .95 } && TurnsLight(late.Judged));

        // The panel grew while the capture ran: what it may have reached by its end is left out, and no more.
        var during = new Panel();
        during.Layout(-1000, Closed);
        capture = during.Begin(0);
        during.Layout(17, 72);
        during.Layout(34, 190);
        capture.SetResult(new(Ms(.1), Ms(20), Page(6)));
        check("A cover capture the panel grew into leaves out what the panel may have reached by its end, and no more",
            during.Judged is { Visible: > .6 and < .8, DarkShare: 0 } && TurnsLight(during.Judged));

        // No motion: the first layout is the open panel, while the capture runs; it may show nothing but the panel.
        var instant = new Panel();
        instant.Layout(-1000, Closed);
        capture = instant.Begin(0);
        instant.Layout(2, Open);
        capture.SetResult(new(Ms(.1), Ms(8), Page(24)));
        check("A cover capture over which a panel without motion opened is Unknown, not read as a dark page",
            instant.Last == GlassOpeningSession.Outcome.Applied && instant.Judged is null);

        // Turned back part way through a closing: the frame still on the screen is the taller panel's, whatever is laid out now.
        var turned = new Panel();
        turned.Layout(-1000, Open);
        turned.Layout(-34, 190);
        turned.Layout(-17, 170);
        turned.Layout(-1, 102);
        capture = turned.Begin(0);
        turned.Layout(16, 110);
        capture.SetResult(new(Ms(.1), Ms(7), Page(13)));
        bool fresh = turned.Last == GlassOpeningSession.Outcome.Applied && turned.Judged is null;
        // Closed and opened again once the closing is off the screen: all of the area is judged.
        turned.Session.Close();
        turned.Layout(100, Closed);
        capture = turned.Begin(140);
        turned.Layout(156, 90);
        capture.SetResult(new(Ms(140.1), Ms(147), Page(0)));
        check("An opening turned back from a closing does not read the taller panel still shown; one after the closing left the screen reads all of the area",
            fresh && turned.Judged is { Visible: 1 } && TurnsLight(turned.Judged));

        // Opened, closed while the capture runs, opened again: the first capture never applies, the second is judged on its own time.
        var again = new Panel();
        again.Layout(-1000, Closed);
        var first = again.Begin(0);
        again.Layout(17, 120);
        again.Session.Close();
        again.Layout(34, 60);
        again.Session.Intent();
        first.SetResult(new(Ms(1), Ms(7), Page(0)));
        bool stale = again.Last == GlassOpeningSession.Outcome.Superseded && again.Judged is null;
        again.Layout(300, Closed);
        var second = again.Begin(350);
        again.Layout(370, 190);
        second.SetResult(new(Ms(350.2), Ms(356), Page(0)));
        check("A capture of an opening closed meanwhile stays out; the next opening's is judged on its own heights", stale && again.Judged is { Visible: 1 });

        // The panel moved on the screen: heights are the panel's own, but a capture of the pixels it left is dropped all the same.
        var moved = new Panel();
        moved.Layout(-1000, Closed);
        capture = moved.Begin(0);
        moved.Area = (-1285, 70, 650, 180);
        capture.SetResult(new(Ms(1), Ms(7), Page(0)));
        bool dropped = moved.Last == GlassOpeningSession.Outcome.StaleArea && moved.Judged is null;
        capture = moved.Begin(400);
        capture.SetResult(new(Ms(400.1), Ms(407), Page(0)));
        check("A capture of screen pixels the panel left is dropped, and the next one where it is now (a display left of the primary) is judged",
            dropped && moved.Judged is { Visible: 1 });

        // The buffer is bounded: what it has forgotten, and a history with nothing in it yet, is not guessed.
        var full = new GlassReachHistory();
        bool empty = full.Covered(Ms(0), Ms(5), Margin, 56, Open) == 1;
        for (int i = 0; i < GlassReachHistory.Capacity + 1; i++) full.Add(Ms(1000 + i), Closed + i % 7);
        check("A capture from before the heights remembered (or with none yet) counts as all covered; one within them is told by the heights of its time",
            empty && full.Covered(Ms(1001), Ms(1010), Margin, 56, Open) == 1 && full.Farthest(Ms(1100), Ms(1101)) == Closed + 6
            && !double.IsFinite(full.Farthest(Ms(0), Ms(5))));
        var still = new GlassReachHistory();
        still.Add(Ms(0), Closed);
        for (int i = 0; i < 1000; i++) { still.Add(Ms(i), Closed); still.Add(Ms(i), double.NaN); }
        check("A still island's height is kept for as long as it is still (the same height again adds nothing)", still.Farthest(Ms(900), Ms(910)) == Closed);

        // The layout pass adds a height and an answer asks for one, on the window's thread: nothing is allocated.
        var hot = new GlassReachHistory();
        hot.Add(0, 1);
        hot.Covered(0, 1, Margin, 56, Open);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i < 20_000; i++) { hot.Add(Ms(i), Closed + i % 170); hot.Covered(Ms(i - 10), Ms(i), Margin, 56, Open); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        check("Keeping the island's heights and asking for what they covered allocates nothing", allocated == 0);
    }
}
