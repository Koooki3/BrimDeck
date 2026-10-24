namespace BrimDeck.Core;

// Where the collapsed row's parts stand. Every two neighbours are the same gap apart, the system readings included, so the row
// reads as one even line at any width. The gap starts at one unit (CompactMusicLayout.Gap, which follows the height), and the
// width the parts leave over is handed out in this order:
//   1. the title, up to the width of its text but no more than TitleMax, so a short title is not left floating in a wide room;
//   2. every gap alike, up to MaxGap units, so the row widens evenly instead of opening one hole;
//   3. the title again, up to the width of its text, while its text is still wider than its room;
//   4. what is still left stands equally at both ends: the content is centred.
// Positions are worked out in device pixels: the unit, every part's room, the gap and the start are whole pixels, so every gap
// between two parts that are fully present is exactly the same. A part that comes or goes (presence below 1) takes its room
// and the gap after it in proportion, so the row closes up smoothly. Such a part never pushes the row past its width, nor the
// title below its shortest: when the row is too short for it (a drag that narrows the row faster than the part leaves), it gives
// up its room sooner. The presence each part is placed with comes back (Placement.Presence), and a part is drawn no larger than
// that (Zoom), so a part on its way in or out shrinks within its own room and never over a neighbour.
public static class CompactSpacing
{
    // The widest a gap grows to, in units.
    public const double MaxGap = 2;
    // The room a title fits in before the gaps widen, at the default height.
    public const double TitleMax = 160;

    public readonly record struct Part(double Width, double Presence);

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

    // Left and Room are in DIPs, one per part (both 0 for a part that is absent); Gap is the gap between two fully present parts.
    // Presence: how present each part is placed, its own presence or less for a part that gives up its room sooner.
    public sealed record Placement(double[] Left, double[] Room, double Gap, double TitleRoom, double[] Presence);

    // The scale a part that is placed this present is drawn at: its room is its width times its presence, so drawn at this scale
    // about the middle of its room it stays within it.
    public static double Zoom(double presence) => double.IsFinite(presence) ? Math.Clamp(presence, 0, 1) : 0;

    // parts: left to right, each at its full width. title: the index of the shown title, whose room this works out (-1 for none;
    // a title on its way out is an ordinary part with its width). titleText: the width its text would like. scale: the height scale.
    public static Placement Arrange(IReadOnlyList<Part> parts, int title, double titleText, double width, double unit, double scale, double dpi)
    {
        dpi = double.IsFinite(dpi) && dpi > 0 ? dpi : 1;
        int n = parts?.Count ?? 0;
        var left = new double[n]; var room = new double[n]; var placed = new double[n];
        double Px(double dips) => double.IsFinite(dips) && dips > 0 ? dips * dpi : 0;
        double available = Math.Floor(Px(width) + 1e-6), u = Math.Round(Px(unit));
        var presence = new double[n];
        bool hasTitle = false, passing = false;
        for (int i = 0; i < n; i++)
        {
            double p = double.IsFinite(parts![i].Presence) ? Math.Clamp(parts[i].Presence, 0, 1) : 0;
            presence[i] = p;
            if (p <= 0) continue;
            if (i == title) hasTitle = true;
            else if (p < 1) passing = true;
        }
        double want = Math.Ceiling(Px(titleText) - 1e-6), cap = Math.Round(Px(TitleMax * CompactMusicLayout.SafeScale(scale)));
        // Parts on their way in or out are placed share times as present as they are: as present as they are while the row holds
        // them beside every other part, the gaps and the title at its shortest (or its text, if shorter); less when it does not.
        double Present(int i, double share) => i != title && presence[i] < 1 ? presence[i] * share : presence[i];
        double Need(double share)
        {
            double total = 0, count = 0, last = 0;
            for (int i = 0; i < n; i++)
            {
                double q = Present(i, share);
                if (q <= 0) continue;
                if (i != title) total += Math.Round(Px(parts![i].Width) * q);
                count += q; last = q;
            }
            return total + u * Math.Max(0, count - last);
        }
        double share = 1;
        if (passing)
        {
            double shortest = hasTitle ? Math.Min(want, Math.Round(Px(CompactMusicLayout.MinimumText * CompactMusicLayout.SafeScale(scale)))) : 0;
            shortest = Math.Min(shortest, Math.Max(0, available - Need(0)));
            if (Need(1) + shortest > available)
            {
                double low = 0, high = 1;
                for (int step = 0; step < 24; step++) { double mid = (low + high) / 2; if (Need(mid) + shortest <= available) low = mid; else high = mid; }
                share = low;
            }
        }
        // Each present part takes the gap after it in proportion to how present it is placed; the last one takes none.
        double fixedRoom = 0, gaps = 0, lastPresence = 0;
        for (int i = 0; i < n; i++)
        {
            double q = placed[i] = Present(i, share);
            if (q <= 0) continue;
            if (i != title) { room[i] = Math.Round(Px(parts![i].Width) * q); fixedRoom += room[i]; }
            gaps += q; lastPresence = q;
        }
        gaps = Math.Max(0, gaps - lastPresence);
        // Parts rounded to whole pixels may come to a pixel or two more than the row: the gaps give it up, alike.
        if (available - fixedRoom - u * gaps < 0 && gaps > 1e-9) u = Math.Max(0, Math.Min(u, Math.Floor((available - fixedRoom) / gaps)));
        double free = Math.Max(0, available - fixedRoom - u * gaps);
        double titleRoom = 0;
        if (hasTitle)
        {
            // A title too long for even the room it is promised still takes what there is.
            titleRoom = Math.Floor(Math.Min(free, Math.Min(want, cap)));
            free -= titleRoom;
        }
        double extra = gaps > 1e-9 ? Math.Max(0, Math.Min(Math.Floor(u * (MaxGap - 1)), Math.Floor(free / gaps + 1e-9))) : 0;
        free -= extra * gaps;
        if (hasTitle && want > titleRoom) { double more = Math.Floor(Math.Min(free, want - titleRoom)); titleRoom += more; free -= more; }
        double gap = u + extra;
        double x = Math.Floor(Math.Max(0, free) / 2);
        for (int i = 0; i < n; i++)
        {
            if (placed[i] <= 0) continue;
            if (i == title) room[i] = titleRoom;
            left[i] = x;
            x += room[i] + gap * placed[i];
        }
        for (int i = 0; i < n; i++) { left[i] /= dpi; room[i] /= dpi; }
        return new(left, room, gap / dpi, titleRoom / dpi, placed);
    }
}

// The room a changing figure keeps (a system reading in the collapsed row). It grows at once to a wider figure and shrinks
// back only once the figures have stayed narrower for Hold seconds, so a rate that moves between "9K" and "386K" does not
// push its neighbours about every second, and a quiet reading does not hold room it no longer needs.
public struct HeldWidth
{
    public const double Hold = 8;
    private double _width, _since;
    private bool _set;
    public readonly double Width => _width;

    // Returns the room to keep for a figure this wide at this time (seconds).
    public double Update(double width, double now)
    {
        width = double.IsFinite(width) && width > 0 ? width : 0;
        if (!_set || width >= _width - 1e-9) { _width = width; _since = now; _set = true; }
        else if (now - _since >= Hold) { _width = width; _since = now; }
        return _width;
    }
}
