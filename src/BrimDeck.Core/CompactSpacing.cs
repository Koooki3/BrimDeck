namespace BrimDeck.Core;

// Where the collapsed row's parts stand once CompactMusicLayout has given each its share. Every gap in the row is the same:
// between two parts, and between two quota rings or two system readings inside theirs. It starts at one unit
// (CompactMusicLayout.Gap), and the width the parts leave over is handed out in this order:
//   1. the title, up to the width of its text but no more than TitleMax, so a short title is not left floating in a wide room;
//   2. every gap alike, up to MaxGap units, so the row widens evenly instead of opening one hole;
//   3. the title again, up to the width of its text, while its text is still wider than its room;
//   4. what is still left stands equally at both ends: the content is centred.
// Positions are worked out in device pixels: the unit, every part's width, the gap and the start are whole pixels, so every
// gap between two parts is exactly the same.
public static class CompactSpacing
{
    // The widest a gap grows to, in units.
    public const double MaxGap = 2;
    // The room a title fits in before the gaps widen, at the default height.
    public const double TitleMax = 160;

    // How many core columns the readings draw in a width: one per core while each column gets at least minColumn DIPs with the
    // gap after it; past that, neighbouring cores share a column, so a machine with many logical processors never draws past its
    // reading into the next one.
    public static int CoreColumns(int cores, double width, double gap, double minColumn = 1)
    {
        if (cores <= 0) return 0;
        if (!(width > 0) || !(minColumn > 0) || !double.IsFinite(gap)) return 1;
        return (int)Math.Clamp(Math.Floor((width + Math.Max(0, gap)) / (minColumn + Math.Max(0, gap)) + 1e-9), 1, cores);
    }

    // The cores a column stands for when columns share them out (CoreColumns): from First up to, not including, End, in order.
    public static (int First, int End) CoreRange(int column, int columns, int cores)
        => columns <= 0 ? (0, 0) : (column * cores / columns, (column + 1) * cores / columns);

    // The scale a reading that is this present (MonitorStrip, as it comes or goes) is drawn at: its room is its width times its presence, so drawn at this scale
    // about the middle of the room it takes it stays within it.
    public static double Zoom(double presence) => double.IsFinite(presence) ? Math.Clamp(presence, 0, 1) : 0;

    // Width: the part without the gaps inside it; Gaps: how many gaps it has inside (a ring or a reading fewer than it shows).
    public readonly record struct Part(double Width, int Gaps = 0);

    // Left and Room are in DIPs, one per part; a part's room holds its gaps inside. Gap is every gap in the row.
    public sealed record Placement(double[] Left, double[] Room, double Gap);

    // parts: left to right. title: the index of the title, whose room this works out (-1 for none); titleText: the width its
    // text would like, and titleShortest the least room it keeps while there is any. scale: the height scale.
    public static Placement Arrange(IReadOnlyList<Part> parts, int title, double titleText, double titleShortest, double width, double unit, double scale, double dpi)
    {
        dpi = double.IsFinite(dpi) && dpi > 0 ? dpi : 1;
        int n = parts?.Count ?? 0;
        var left = new double[n]; var room = new double[n];
        double Px(double dips) => double.IsFinite(dips) && dips > 0 ? dips * dpi : 0;
        double available = Math.Floor(Px(width) + 1e-6), u = Math.Round(Px(unit));
        bool hasTitle = title >= 0 && title < n;
        double fixedRoom = 0, gaps = Math.Max(0, n - 1);
        for (int i = 0; i < n; i++)
        {
            gaps += Math.Max(0, parts![i].Gaps);
            if (i != title) fixedRoom += room[i] = Math.Round(Px(parts[i].Width));
        }
        // The title keeps its shortest room (or its text, if shorter); parts rounded to whole pixels may come to a pixel or two
        // more than the row, and the gaps give that up, alike.
        double want = Math.Ceiling(Px(titleText) - 1e-6), shortest = hasTitle ? Math.Min(want, Math.Round(Px(titleShortest))) : 0;
        if (available - fixedRoom - shortest - u * gaps < 0 && gaps > 0) u = Math.Max(0, Math.Floor((available - fixedRoom - shortest) / gaps));
        double free = Math.Max(0, available - fixedRoom - u * gaps);
        double maxExtra = Math.Floor(u * (MaxGap - 1));
        double Extra(double rest) => gaps > 0 ? Math.Max(0, Math.Min(maxExtra, Math.Floor(rest / gaps + 1e-9))) : 0;
        // The title's room: up to TitleMax first, then, once the gaps are at their widest, up to its text.
        double titleRoom = 0;
        if (hasTitle)
        {
            double cap = Math.Round(Px(TitleMax * CompactMusicLayout.SafeScale(scale)));
            titleRoom = Math.Floor(Math.Min(free, Math.Max(shortest, Math.Min(want, cap))));
            double rest = free - titleRoom;
            rest -= Extra(rest) * gaps;
            if (want > titleRoom) titleRoom += Math.Floor(Math.Max(0, Math.Min(rest, want - titleRoom)));
            room[title] = titleRoom;
            free -= titleRoom;
        }
        double extra = Extra(free);
        free -= extra * gaps;
        double gap = u + extra;
        double x = Math.Floor(Math.Max(0, free) / 2);
        for (int i = 0; i < n; i++)
        {
            if (i != title) room[i] += gap * Math.Max(0, parts![i].Gaps);
            left[i] = x / dpi;
            x += room[i] + gap;
            room[i] /= dpi;
        }
        return new(left, room, gap / dpi);
    }

    // A carousel's window over items side by side with this gap: the most whole items from the first that fit in the span, and
    // how far the strip moves for its other rest. Both rests start on an item's left edge, and at both no item crosses the
    // window's edges: what shows is whole, and what is past the right edge is hidden. The strip moves as far as shows the most of
    // the items after the first ones, ideally the last of them (Travel); the items between pass through while it glides. Count
    // is how many items the window holds at the start (0: not even the first fits, and nothing shows). The window is as wide as
    // those items, or as the last item when that is wider and fits before the next item would show; Gaps is how many gaps it
    // holds, which widen with the row's (CompactSpacing.Arrange): Count - 1, or 0 for the last item's width. Travel is 0 when
    // the strip rests.
    public static (int Count, double Window, int Gaps, double Travel) Window(IReadOnlyList<double> widths, double gap, double span)
    {
        int n = widths?.Count ?? 0;
        if (n == 0 || !double.IsFinite(span) || span <= 0) return (0, 0, 0, 0);
        gap = Safe(gap);
        int count = 0; double window = 0;
        for (int i = 0; i < n; i++)
        {
            double next = window + (i > 0 ? gap : 0) + Safe(widths![i]);
            if (next > span + 1e-6) break;
            window = next; count++;
        }
        if (count == 0 || count == n) return (count, window, Math.Max(0, count - 1), 0);
        double last = Safe(widths![n - 1]);
        // Widened to the last item, the window still ends before the next of the first items begins.
        if (last > window && last <= Math.Min(span, window + gap) + 1e-6) return (count, last, 0, Travel(widths, gap, last));
        return (count, window, count - 1, Travel(widths, gap, window));
    }

    // How far a strip of these items moves for its second rest in a window this wide: to the left edge of an item from which no
    // item crosses the window's right edge, the one that shows the furthest item whole, and of those the one that shows the most;
    // 0 when the first rest already shows as far.
    public static double Travel(IReadOnlyList<double> widths, double gap, double window)
    {
        int n = widths?.Count ?? 0;
        gap = Safe(gap);
        var lefts = new double[n];
        for (int i = 1; i < n; i++) lefts[i] = lefts[i - 1] + Safe(widths![i - 1]) + gap;
        int furthest = -1; double best = 0;
        for (int j = 0; j < n; j++)
        {
            double edge = lefts[j] + window;
            int k = j - 1; bool clean = true;
            for (int i = j; i < n && clean; i++)
            {
                double right = lefts[i] + Safe(widths![i]);
                if (right <= edge + 1e-6) k = i;
                else clean = lefts[i] >= edge - 1e-6;
            }
            if (clean && k > furthest) { furthest = k; best = lefts[j]; }
        }
        return best;
    }

    private static double Safe(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
