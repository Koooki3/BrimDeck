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
// and the gap after it in proportion, so the row closes up smoothly.
public static class CompactSpacing
{
    // The widest a gap grows to, in units.
    public const double MaxGap = 2;
    // The room a title fits in before the gaps widen, at the default height.
    public const double TitleMax = 160;

    public readonly record struct Part(double Width, double Presence);

    // Left and Room are in DIPs, one per part (both 0 for a part that is absent); Gap is the gap between two fully present parts.
    public sealed record Placement(double[] Left, double[] Room, double Gap, double TitleRoom);

    // parts: left to right, each at its full width. title: the index of the shown title, whose room this works out (-1 for none;
    // a title on its way out is an ordinary part with its width). titleText: the width its text would like. scale: the height scale.
    public static Placement Arrange(IReadOnlyList<Part> parts, int title, double titleText, double width, double unit, double scale, double dpi)
    {
        dpi = double.IsFinite(dpi) && dpi > 0 ? dpi : 1;
        int n = parts?.Count ?? 0;
        var left = new double[n]; var room = new double[n];
        double Px(double dips) => double.IsFinite(dips) && dips > 0 ? dips * dpi : 0;
        double available = Math.Floor(Px(width) + 1e-6), u = Math.Round(Px(unit));
        Span<double> presence = n <= 64 ? stackalloc double[n] : new double[n];
        // Each present part takes the gap after it in proportion to how present it is; the last one takes none.
        double fixedRoom = 0, gaps = 0, last = 0;
        bool hasTitle = false;
        for (int i = 0; i < n; i++)
        {
            double p = double.IsFinite(parts![i].Presence) ? Math.Clamp(parts[i].Presence, 0, 1) : 0;
            presence[i] = p;
            if (p <= 0) continue;
            if (i == title) hasTitle = true;
            else { room[i] = Math.Round(Px(parts[i].Width) * p); fixedRoom += room[i]; }
            gaps += p; last = p;
        }
        gaps = Math.Max(0, gaps - last);
        // Parts rounded to whole pixels may come to a pixel or two more than the row: the gaps give it up, alike.
        if (available - fixedRoom - u * gaps < 0 && gaps > 1e-9) u = Math.Max(0, Math.Min(u, Math.Floor((available - fixedRoom) / gaps)));
        double free = Math.Max(0, available - fixedRoom - u * gaps);
        double titleRoom = 0;
        double want = Math.Ceiling(Px(titleText) - 1e-6), cap = Math.Round(Px(TitleMax * CompactMusicLayout.SafeScale(scale)));
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
            if (presence[i] <= 0) continue;
            if (i == title) room[i] = titleRoom;
            left[i] = x;
            x += room[i] + gap * presence[i];
        }
        for (int i = 0; i < n; i++) { left[i] /= dpi; room[i] /= dpi; }
        return new(left, room, gap / dpi, titleRoom / dpi);
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
