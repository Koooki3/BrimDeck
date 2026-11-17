using System.Text.RegularExpressions;
using BrimDeck.Core;

// The open page and the island's edges do not step a pixel to and fro in the tail of the opening (SurfaceSnap, SnappedAnimations.cs).
//
// The layout rounds the island's left margin, its width and the page's offset inside the island (half of what the width leaves
// over, the page being centred) each to whole device pixels on its own. When the surface moves less than a pixel per frame they
// cross their half pixels in different frames, and the page, whose place does not change in a centred opening, stepped one pixel left
// and right several times (measured with the real WPF layout, 60 Hz frames: up to four steps, three of them reversals, in the last
// 30 % of a move at 125 to 175 percent; none at 100 percent). Here the same three roundings are modelled and the page's place is
// followed over the whole move, on the curves the panel opens on (the spring) and the others it moves on, at several display scales,
// page widths, surfaces and centres, 60 and 144 Hz, and the shortest, the default and the longest animation duration.
internal static class ExpandSnapTests
{
    // WPF rounds a layout value with Math.Round(value * scale) / scale (to even at a half), in device pixels here.
    private static double R(double value) => Math.Round(value);

    // Where the island's edges and the page's left edge lie, in device pixels, for the margin left, the width and the page's width
    // (all DIPs): the island at R(left * s), its width R(width * s), the page centred in the island's width, offset rounded.
    private static (double Left, double Right, double Page) Place(double left, double width, double page, double scale)
    {
        double l = R(left * scale), w = R(width * scale), p = R(page * scale);
        return (l, l + w, l + R((w - p) / 2));
    }

    // One move sampled at frame times: the geometry as the plain animations give it (snapped: false), or as the snapped ones do.
    // From and To are the shares of the move in which the page is drawn: from 12 % of it opening (Track(.12, .8) in SetExpanded, the
    // page fades in then) and to the end, and the first 30 % closing (Track(0, .3): it has faded out and is hidden); a page that changes
    // its size in place is drawn throughout, and is checked from a quarter of the move on: the width it leaves from has the parity
    // of the old page, not the new, so the first frames settle onto the new parity by a step of at most a pixel.
    private sealed record Move(double X0, double W0, double X1, double W1, double Page, double Scale, Func<double, double> Curve, double From = 0, double To = 1)
    {
        // The spring passes its end by a third of a percent of the move and comes back. An island whose centre travels (opened by the
        // pointer off the middle of a wide surface) takes the page with it, so the page too may pass its place by a pixel and return:
        // that is the curve's give, one change of direction. Where the centre stays put the page does not move at all.
        public int Allowed => Curve(.7) > 1 && Math.Abs(X0 + W0 / 2 - (X1 + W1 / 2)) > .5 ? 1 : 0;
    }

    private static List<(double Left, double Right, double Page)> Frames(Move m, int duration, double hz, bool snapped)
    {
        var frames = new List<(double, double, double)>();
        var times = new List<double>();
        for (double t = 0; t < duration; t += 1000 / hz) times.Add(t);
        times.Add(duration);
        foreach (double t in times.Where(t => t >= duration * m.From - 1e-9 && t <= duration * m.To + 1e-9))
        {
            double e = m.Curve(Math.Clamp(t / duration, 0, 1));
            double left = snapped ? SurfaceSnap.Left(m.X0, m.X1, m.W0, m.W1, e, m.Scale, m.Page) : m.X0 + (m.X1 - m.X0) * e;
            double width = snapped ? SurfaceSnap.Width(m.W0, m.W1, e, m.Scale, m.Page) : m.W0 + (m.W1 - m.W0) * e;
            frames.Add(Place(left, width, m.Page, m.Scale));
        }
        return frames;
    }

    // How many times a series changes direction.
    private static int Reversals(IEnumerable<double> series)
    {
        int reversals = 0; double last = 0, before = double.NaN;
        foreach (double value in series)
        {
            if (!double.IsNaN(before) && value != before) { double d = value - before; if (last * d < 0) reversals++; last = d; }
            before = value;
        }
        return reversals;
    }

    // The curves a surface moves on: the opening spring, and the plain landings (Soft.Out, Soft.InOut, Soft.Drawer).
    private static readonly (string Name, Func<double, double> Curve)[] Curves_ =
    [
        ("spring", Curves.Bounce), ("out", x => Curves.Bezier(.22, .8, .28, 1, x)), ("in-out", x => Curves.Bezier(.45, 0, .25, 1, x)),
        ("drawer", x => Curves.Bezier(.32, .72, 0, 1, x)),
    ];

    // The moves the panel makes: a collapsed capsule opening into pages of several widths, centred and (pointer-centred) off centre,
    // closing back, and a page changing its width in place.
    private static IEnumerable<(string Name, Move Move)> Moves(double scale, Func<double, double> curve)
    {
        foreach (double page in new[] { 440d, 521.3, 620, 777.7, 800 })
            foreach (double shift in new[] { 0d, 13.7, -41.2, 72.9 })
            {
                double w0 = 238.4, x0 = 500 - w0 / 2, x1 = 500 + shift - page / 2;
                yield return ($"open {page} shift {shift}", new Move(x0, w0, x1, page, page, scale, curve, From: .12));
                yield return ($"close {page} shift {shift}", new Move(x1, page, x0, w0, page, scale, curve, To: .3));
            }
        yield return ("reshape 520 to 620", new Move(240, 520, 190, 620, 620, scale, curve, From: .25));
        yield return ("reshape 620 to 440", new Move(190, 620, 280, 440, 440, scale, curve, From: .25));
    }

    // What a series of frames must be: the page's place never changes direction.
    private static string? Breaks(Move move, int duration, double hz, bool snapped)
    {
        var frames = Frames(move, duration, hz, snapped);
        int pageSteps = Reversals(frames.Select(f => f.Page));
        if (pageSteps > move.Allowed) return $"the page steps to and fro {pageSteps} times";
        return null;
    }

    public static void Run(Action<string, bool> check)
    {
        // The model itself: with the plain animations the page steps to and fro on a move that is known to show it, and the same move
        // on the snapped values does not, so that what is asserted below can fail.
        var known = new Move(500 - 238.4 / 2, 238.4, 500 - 41.2 - 777.7 / 2, 777.7, 777.7, 1.75, Curves.Bounce);
        check("Expand snap: the model finds the page stepping to and fro on the plain animations (777.7 wide, 175 %, 1000 ms)",
            Breaks(known, 1000, 60, snapped: false) is not null);
        check("Expand snap: the same move on the snapped values does not", Breaks(known, 1000, 60, snapped: true) is null);

        // Whole moves, every curve, scale, duration and rate.
        var failures = new List<string>();
        int moves = 0;
        foreach (double scale in new[] { 1d, 1.1, 1.25, 1.5, 1.75, 2, 2.25 })
            foreach (var (curveName, curve) in Curves_)
                foreach (var (name, move) in Moves(scale, curve))
                    foreach (int duration in new[] { 100, 360, 1000 })
                        foreach (double hz in new[] { 60d, 144 })
                        {
                            moves++;
                            if (Breaks(move, duration, hz, snapped: true) is { } why && failures.Count < 6) failures.Add($"{curveName} {name} at {scale * 100:0} % {duration} ms {hz:0} Hz: {why}");
                        }
        check($"Expand snap: on {moves} moves (7 scales, 4 curves, 3 durations, 2 rates) the page never steps to and fro: " + string.Join("; ", failures), failures.Count == 0);

        // The edge and the width land where the plain ones do: no step on the last frame, and the end is exact.
        check("Expand snap: the first and last values are the move's own (no step at either end)",
            Enumerable.Range(0, 400).All(i =>
            {
                var r = new Random(i);
                double scale = new[] { 1d, 1.25, 1.5, 1.75, 2 }[r.Next(5)], x0 = r.NextDouble() * 500, w0 = 100 + r.NextDouble() * 300, x1 = r.NextDouble() * 500, w1 = 300 + r.NextDouble() * 500;
                double page = r.NextDouble() < .5 ? w1 : w0;
                return SurfaceSnap.Width(w0, w1, 0, scale, page) == w0 && SurfaceSnap.Width(w0, w1, 1, scale, page) == w1
                    && SurfaceSnap.Left(x0, x1, w0, w1, 0, scale, page) == x0 && SurfaceSnap.Left(x0, x1, w0, w1, 1, scale, page) == x1;
            }));
        // Just before the end the edge is where the layout puts the end's: the last frame does not move anything by a pixel.
        int jumps = 0;
        for (int i = 0; i < 2000; i++)
        {
            var r = new Random(i);
            double scale = new[] { 1d, 1.25, 1.5, 1.75, 2 }[r.Next(5)], w0 = 150 + r.NextDouble() * 200, x0 = 300 + r.NextDouble() * 100, w1 = 400 + r.NextDouble() * 400, x1 = 500 - w1 / 2 + (r.NextDouble() - .5) * 100;
            double e = 1 - 1e-9;
            var before = Place(SurfaceSnap.Left(x0, x1, w0, w1, e, scale, w1), SurfaceSnap.Width(w0, w1, e, scale, w1), w1, scale);
            var after = Place(x1, w1, w1, scale);
            if (before != after) jumps++;
        }
        check($"Expand snap: just before the end the island and the page are where the end puts them ({jumps} of 2000 random moves step on the last frame)", jumps == 0);

        // A surface at rest taking its content's width (a lyric line of another length, MusicLyricAutoWidth): what is centred in the
        // island is the collapsed row, built for the end width, and not the open page. Given the row, the moving width keeps the row's
        // parity: the row stays in the middle of the island from a quarter of the move on (the width it leaves has the old row's
        // parity) and nothing steps on the last frame. Given an open page of the other parity, as SetExpanded gave it, the row
        // stood half a pixel off the middle, rounded a pixel to one side or the other, and stepped on the last frame. The capsule's
        // insets are a whole, even number of pixels at the default height (32 DIP at 100 to 200 percent); with two pixels more the
        // layout rounds that half pixel the other way, and the row moves within the island on the last frame.
        int resizes = 0, offMiddle = 0, lastSteps = 0, toAndFro = 0, offMiddleOnPage = 0, lastStepsOnPage = 0, toAndFroOnPage = 0;
        foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2 })
            for (int fromPixels = 300; fromPixels <= 330; fromPixels += 3)
                for (int change = -12; change <= 12; change++)
                foreach (double insets in new[] { 32 * scale, 32 * scale + 2 })
                {
                    if (Math.Abs(change) < 3) continue;
                    resizes++;
                    double toPixels = fromPixels + change, rowPixels = toPixels - insets;
                    double w0 = fromPixels / scale, w1 = toPixels / scale, row = rowPixels / scale;
                    double x0 = (900 - fromPixels / 2d) / scale, x1 = (900 - toPixels / 2d) / scale;
                    // The frames of a 300 ms move at 60 Hz, from a quarter of it on: whether the row's middle leaves the island's,
                    // whether the row moves within the island on the last frame, and how often its place on screen turns back.
                    (bool Off, bool Step, int Turns) Follow(double page)
                    {
                        bool off = false; double last = 0, before = 0;
                        var places = new List<double>();
                        for (int frame = 5; frame <= 18; frame++)
                        {
                            double e = frame == 18 ? 1 : Curves.Bezier(.45, 0, .25, 1, frame / 18d);
                            var place = Place(SurfaceSnap.Left(x0, x1, w0, w1, e, scale, page), SurfaceSnap.Width(w0, w1, e, scale, page), row, scale);
                            if (Math.Abs(place.Page + rowPixels / 2 - (place.Left + place.Right) / 2) > 1e-6) off = true;
                            before = last; last = place.Page - place.Left;
                            places.Add(place.Page);
                        }
                        return (off, Math.Abs(last - before) > 1e-6, Reversals(places));
                    }
                    var onRow = Follow(row);
                    if (onRow.Off) offMiddle++;
                    if (onRow.Step) lastSteps++;
                    if (onRow.Turns > 0) toAndFro++;
                    // An open page whose width in pixels has the other parity.
                    var onPage = Follow((toPixels + 201) / scale);
                    if (onPage.Off) offMiddleOnPage++;
                    if (onPage.Step) lastStepsOnPage++;
                    if (onPage.Turns > 0) toAndFroOnPage++;
                }
        check($"Expand snap: a collapsed row keeps the middle of a surface that takes its content's width (of {resizes} moves, {offMiddle} leave it, {lastSteps} step on the last frame, {toAndFro} step to and fro)",
            offMiddle == 0 && lastSteps == 0 && toAndFro == 0);
        check($"Expand snap: on an open page's parity the model finds the row off the middle, stepping on the last frame and to and fro ({offMiddleOnPage}, {lastStepsOnPage} and {toAndFroOnPage} of {resizes} moves)",
            offMiddleOnPage == resizes && lastStepsOnPage > 0 && toAndFroOnPage > 0);

        // A width that does not change does not move: no parity step in the middle of a move that only shifts the island.
        check("Expand snap: a width that stays the same stays the same, and the edge follows the curve",
            Enumerable.Range(1, 99).All(i => SurfaceSnap.Width(520, 520, i / 100d, 1.5, 520) == 520 && Math.Abs(SurfaceSnap.Left(240, 200, 520, 520, i / 100d, 1.5, 520) - (240 - 40 * i / 100d)) <= 1 / 1.5));
        check("Expand snap: values that cannot be snapped (no scale, not a number) are the move's own",
            SurfaceSnap.Width(100, 200, .5, 0, 200) == 100 && SurfaceSnap.Width(double.NaN, 200, .5, 1.5, 200) is double.NaN && SurfaceSnap.Left(10, 20, 100, 200, .5, double.NaN, 200) == 10);

        // The panel uses them: the island's width and its place animate through the snapped animations, and not through the plain
        // ones (a plain animation of either brings the stepping back).
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "BrimDeck.sln"))) source = source.Parent;
        check("Expand snap: the source tree is found", source is not null);
        if (source is null) return;
        string text = File.ReadAllText(Path.Combine(source.FullName, "src", "BrimDeck", "MainWindow.xaml.cs"));
        check("Expand snap: SetExpanded animates the island's width with SnappedWidthAnimation and its place with SnappedLeftAnimation",
            Uses(text));
        check("Expand snap: the finder rejects the plain animations the panel had",
            !Uses(text.Replace("new SnappedWidthAnimation", "new DoubleAnimation").Replace("new SnappedLeftAnimation", "new ThicknessAnimation"))
            && !Uses("Animate(Island, WidthProperty, width, duration, surface, settle); HoverZone.BeginAnimation(MarginProperty, Timed(new ThicknessAnimation { To = new Thickness(left, 0, 0, 0) }, duration, surface));"));
        // Both take what is centred in the island: the collapsed row while the surface at rest takes its content's width, else the page.
        check("Expand snap: a content resize gives the snapped animations the collapsed row's width, every other move the page's",
            Regex.IsMatch(text, @"double centred = resizing\b[^;]*\? Compact\.Width : pageWidth;")
            && Regex.IsMatch(text, @"new SnappedWidthAnimation \{ To = width, Page = centred,")
            && Regex.IsMatch(text, @"new SnappedLeftAnimation \{ To = left, FromWidth = fromWidth, ToWidth = width, Page = centred,"));
    }

    private static bool Uses(string text) =>
        Regex.IsMatch(text, @"Island\.BeginAnimation\(WidthProperty,\s*Timed\(new SnappedWidthAnimation")
        && Regex.IsMatch(text, @"HoverZone\.BeginAnimation\(MarginProperty,\s*Timed\(new SnappedLeftAnimation")
        && !Regex.IsMatch(text, @"Animate\(Island,\s*WidthProperty")
        && !Regex.IsMatch(text, @"HoverZone\.BeginAnimation\(MarginProperty,\s*Timed\(new ThicknessAnimation");
}
