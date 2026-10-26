using BrimDeck.Core;

// The collapsed row's spacing (CompactSpacing): one gap everywhere, spare width to the title, then the gaps, then both ends;
// the carousels' windows of whole items; and the narrowest surface, which holds what the row must show.
internal static class CompactSpacingTests
{
    internal static void Run(Action<string, bool> check)
    {
        Arrange(check);
        Windows(check);
        Required(check);
    }

    private static bool Even(CompactSpacing.Placement place, double width, double dpi)
    {
        int n = place.Left.Length;
        bool whole = place.Left.Concat(place.Room).Append(place.Gap).All(v => Math.Abs(v * dpi - Math.Round(v * dpi)) < 1e-6);
        bool even = Enumerable.Range(0, Math.Max(0, n - 1)).All(i => Math.Abs(place.Left[i + 1] - place.Left[i] - place.Room[i] - place.Gap) < 1e-6);
        bool inside = n == 0 || place.Left[0] >= -1e-9 && place.Left[^1] + place.Room[^1] <= width + 1e-6;
        return whole && even && inside;
    }

    private static void Arrange(Action<string, bool> check)
    {
        // Cover, title, three rings (two gaps inside), equalizer and clock.
        CompactSpacing.Part[] parts = [new(20), new(0), new(36, 2), new(18), new(40)];
        var place = CompactSpacing.Arrange(parts, 1, 100, 18, 400, 8, 1, 1);
        check("Compact spacing: every gap in the row is the same", Even(place, 400, 1));
        check("Compact spacing: a short title takes its text, the gaps widen to twice, and the rest is split at both ends",
            place.Room[1] == 100 && place.Gap == 16 && place.Left[0] == 45 && 400 - place.Left[^1] - place.Room[^1] == 45 && place.Room[2] == 36 + 2 * 16);
        var shortRow = CompactSpacing.Arrange(parts, 1, 100, 18, 260, 8, 1, 1);
        check("Compact spacing: the title comes before the gaps", shortRow.Room[1] == 98 && shortRow.Gap == 8 && shortRow.Left[0] == 0 && Even(shortRow, 260, 1));
        var longTitle = CompactSpacing.Arrange(parts, 1, 500, 18, 400, 8, 1, 1);
        check("Compact spacing: a long title takes what the widest gaps leave", longTitle.Room[1] == 190 && longTitle.Gap == 16 && longTitle.Left[0] == 0
            && Math.Abs(longTitle.Left[^1] + longTitle.Room[^1] - 400) < 1e-9);
        var noTitle = CompactSpacing.Arrange([new(60, 3), new(40)], -1, 0, 0, 300, 8, 1, 1);
        check("Compact spacing: without a title the readings and the clock spread evenly, then centre",
            noTitle.Gap == 16 && Even(noTitle, 300, 1) && Math.Abs(noTitle.Left[0] - (300 - (60 + 3 * 16 + 16 + 40)) / 2) <= .5);
        check("Compact spacing: one part alone is centred", CompactSpacing.Arrange([new(40)], -1, 0, 0, 100, 8, 1, 1) is { } alone && alone.Left[0] == 30 && alone.Room[0] == 40);
        bool scaled = true;
        foreach (double dpi in new[] { 1, 1.25, 1.5, 1.75, 2 })
            for (double width = 120; width <= 700; width += 7)
            {
                var p = CompactSpacing.Arrange(parts, 1, 230, 18, width, 8, 1, dpi);
                scaled &= Even(p, width, dpi) && p.Room[1] >= Math.Min(18, Math.Floor(width * dpi) / dpi) - 1e-9 || width < 160;
            }
        check("Compact spacing: on whole device pixels at every scale, the gaps stay equal and the row stays inside", scaled);
        var tight = CompactSpacing.Arrange(parts, 1, 100, 18, 150, 8, 1, 1);
        check("Compact spacing: a row too short for its gaps narrows them alike and keeps the shortest title", Even(tight, 150, 1) && tight.Room[1] >= 18 && tight.Gap < 8);
    }

    private static void Windows(Action<string, bool> check)
    {
        double[] widths = [30, 40, 20, 50];
        check("Compact carousel: the window holds whole items from the first, and the strip moves to the item from which the rest fits",
            CompactSpacing.Window(widths, 8, 100) == (2, 78, 1, 86) && CompactSpacing.Window(widths, 8, 164) == (4, 164, 3, 0)
            && CompactSpacing.Window(widths, 8, 29) == (0, 0, 0, 0) && CompactSpacing.Window([], 8, 100) == (0, 0, 0, 0) && CompactSpacing.Window(widths, 8, double.NaN) == (0, 0, 0, 0));
        check("Compact carousel: a last item a little wider than the first ones widens the window to it; one that no rest can show whole is left out",
            CompactSpacing.Window([30, 40, 20, 36], 8, 60) == (1, 36, 0, 30 + 8 + 40 + 8 + 20 + 8) && CompactSpacing.Window(widths, 8, 60) == (1, 30, 0, 0)
            && CompactSpacing.Window([30, 50, 20, 20], 8, 40) == (1, 30, 0, 30 + 8 + 50 + 8 + 20 + 8));
        check("Compact carousel: the travel follows a wider gap", CompactSpacing.Travel(widths, 16, 30 + 16 + 40) == 30 + 16 + 40 + 16 && CompactSpacing.Travel(widths, 8, 164) == 0);
        // At both rests every item is wholly inside the window or wholly outside it.
        var random = new Random(86);
        bool whole = true;
        for (int round = 0; round < 2000; round++)
        {
            var items = Enumerable.Range(0, random.Next(1, 9)).Select(_ => (double)random.Next(8, 70)).ToArray();
            double gap = random.Next(4, 17), span = random.Next(0, 400);
            var (count, window, _, travel) = CompactSpacing.Window(items, gap, span);
            var lefts = items.Select((_, i) => items.Take(i).Sum() + i * gap).ToArray();
            bool Rest(double shift) => lefts.Select((x, i) => (x - shift, x - shift + items[i])).All(item => item.Item2 <= 1e-6 || item.Item1 >= window - 1e-6 || item.Item1 >= -1e-6 && item.Item2 <= window + 1e-6);
            whole &= window <= span + 1e-6 && Rest(0) && Rest(travel) && (travel == 0 || lefts.Contains(travel));
        }
        check("Compact carousel: no item is cut at either rest", whole);
    }

    private static void Required(Action<string, bool> check)
    {
        check("Compact required: the clock, and while media plays the cover, equalizer and shortest title, with their gaps",
            CompactMusicLayout.RequiredWidth(1, true, true, 40) == 20 + 8 + 18 + 8 + 18 + 8 + 40 && CompactMusicLayout.RequiredWidth(1, false, false, 40) == 40
            && CompactMusicLayout.RequiredWidth(1, true, false, 0) == 20 + 8 + 18 && CompactMusicLayout.RequiredWidth(1, false, true, 0) == 0
            && CompactMusicLayout.RequiredWidth(2, true, true, 80) == 2 * (20 + 8 + 18 + 8 + 18 + 8) + 80);
        var layout = CompactMusicLayout.Calculate(CompactMusicLayout.RequiredWidth(1, true, true, 40), 1, true, 3, true, 40, 120);
        check("Compact required: at that width nothing the row must show is cut", layout.CoverWidth == 28 && layout.EqualizerWidth == 26 && layout.ClockWidth == 48
            && layout.TextWidth == 18 && layout.MonitorWidth == 0 && layout.RingWidth == 0);
        var screen = new DipRect(0, 0, 1920, 1080); var area = new DipRect(0, 0, 1920, 1032);
        var capsule = new DeckSettings { CapsuleWidth = 140, CapsuleHeight = 32 };
        check("Compact required: the surface grows to what it must show, never below the saved width",
            CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, null, 152).Surface.Width == 184
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, null, 50).Surface.Width == 140
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, 10, 152).Surface.Width == 184
            && CollapsedGeometry.Resolve(new DeckSettings { NotchHeight = 24 }, CompactStyle.Notch, screen, area, 1, null, 300).Surface.Width == 356
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Line, screen, area, 1, null, 300).Surface.Width == CollapsedLimits.LineLengthDefault
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area).Surface.Width == 140);
        check("Compact required: surface width for a content width", CollapsedGeometry.SurfaceWidth(CompactStyle.Capsule, 32, 100) == 132
            && CollapsedGeometry.SurfaceWidth(CompactStyle.Notch, 32, 100) == 160 && CollapsedGeometry.SurfaceWidth(CompactStyle.Notch, 24, 100) == 156
            && CollapsedGeometry.SurfaceWidth(CompactStyle.Line, 4, 100) == 0 && CollapsedGeometry.SurfaceWidth(CompactStyle.Capsule, 32, 0) == 0
            && CollapsedGeometry.SurfaceWidth(CompactStyle.Capsule, 32, double.NaN) == 0);
    }
}
