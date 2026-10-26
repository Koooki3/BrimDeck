using BrimDeck.Core;

// The collapsed row's spacing (CompactSpacing): every two neighbours one gap apart, the system readings included; spare width
// to the title up to its text, then to every gap alike up to twice the unit, then to a long title, then to both ends equally;
// and the readings' widths (HeldWidth) holding still while their figures flicker.
internal static class CompactSpacingTests
{
    // At the default height: the readings' widest widths in priority order (CPU, memory, core columns, download, GPU, upload).
    private static readonly double[] Readings = [44, 44, 56, 41, 44, 41];
    // The six usage and rate readings the widths above are for, as the Full preset shows them; temperature and storage are
    // chosen on their own and left out here.
    private static readonly List<CompactMonitorItems> Usage = [.. CompactMonitor.Order.Where(item => (CompactMonitorItems.All & item) != 0)];
    private static readonly List<CompactMonitorItems> UsagePriority = [.. CompactMonitor.Priority.Where(item => (CompactMonitorItems.All & item) != 0)];
    private static readonly double[] Dpis = [1, 1.25, 1.5, 1.75, 2];

    public static void Run(Action<string, bool> check)
    {
        Examples(check);
        Uniform(check);
        Surfaces(check);
        Motion(check);
        Passing(check);
        Held(check);
    }

    // A drag that narrows (or widens) the row while parts leave (or arrive): parts are taken away by CompactMusicLayout at once
    // and fade out over the next frames. At every frame no part lies over another, a part on its way in or out is drawn within its
    // own room, nothing runs past the row, and what the row must show (cover, equalizer, clock, the title at its shortest) stays.
    private static void Passing(Action<string, bool> check)
    {
        bool apart = true, inside = true, within = true, kept = true; string first = "";
        void Fail(ref bool flag, string what) { if (flag && first.Length == 0) first = what; flag = false; }
        // The capsule at the default height with two quota rings and every reading, as on screen.
        double clock = 38, gap = 8, title = 400;
        double[] readings = [.. Readings];
        foreach (var dpi in Dpis) foreach (var step in new[] { 2d, 7, 23, 61 }) foreach (var fade in new[] { .05, .2, .5 })
        foreach (var narrowing in new[] { true, false })
        {
            // Presence by part: 0 cover, 1 rings, 2 title, 3..8 readings in Order, 9 equalizer, 10 clock.
            var presence = new double[11];
            double required = CompactMusicLayout.Calculate(0, 1, true, 2, true, clock, readings, gap).Required;
            double from = narrowing ? 800 : Math.Ceiling(required) + 1, to = narrowing ? Math.Ceiling(required) + 1 : 800;
            bool started = false;
            for (double w = from; narrowing ? w >= to : w <= to; w += narrowing ? -step : step)
            {
                double row = Math.Floor(w * dpi + 1e-6) / dpi;
                var l = CompactMusicLayout.Calculate(row, 1, true, 2, true, clock, readings, gap);
                var shown = CompactMonitor.First(CompactMonitorItems.All, l.MonitorItems);
                // Readings offered in Priority order; placed in Order.
                double Offered(CompactMonitorItems item) => readings[UsagePriority.IndexOf(item)];
                bool[] wanted = [l.CoverWidth > 0, l.RingWidth > 0, l.TextWidth > 0, .. Usage.Select(item => (shown & item) != 0), l.EqualizerWidth > 0, l.ClockWidth > 0];
                for (int i = 0; i < presence.Length; i++)
                    presence[i] = !started ? (wanted[i] ? 1 : 0) : Math.Clamp(presence[i] + (wanted[i] ? fade : -fade), 0, 1);
                started = true;
                double[] widths = [20, Math.Max(l.RingWidth, 12), 0, .. Usage.Select(Offered), 18, clock];
                var parts = widths.Select((v, i) => new CompactSpacing.Part(v, presence[i])).ToArray();
                var p = CompactSpacing.Arrange(parts, 2, title, row, gap, 1, dpi);
                string at = $"dpi{dpi} step{step} fade{fade} {(narrowing ? "narrowing" : "widening")} w{w}";
                double px = 1 / dpi + 1e-9, end = -1;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (p.Presence[i] <= 0) continue;
                    if (p.Left[i] < end - px) Fail(ref apart, at + $" part{i}");
                    end = p.Left[i] + p.Room[i];
                    // What is drawn: the title fills its room; every other part at its width times Zoom, in the middle of its room.
                    if (i != 2 && widths[i] * CompactSpacing.Zoom(p.Presence[i]) > p.Room[i] + px) Fail(ref inside, at + $" part{i}");
                    if (p.Presence[i] > presence[i] + 1e-9) Fail(ref inside, at + $" more present part{i}");
                }
                if (end > row + 1e-9 || p.Left.Any(v => v < -1e-9)) Fail(ref within, at);
                if (presence[0] < 1 || presence[9] < 1 || presence[10] < 1 || p.Presence[0] < 1 || p.Presence[9] < 1 || p.Presence[10] < 1
                    || Math.Abs(p.Room[0] - 20) > px || Math.Abs(p.Room[9] - 18) > px || Math.Abs(p.Room[10] - clock) > px
                    || p.TitleRoom < Math.Min(CompactMusicLayout.MinimumText, l.TextWidth) - px)
                    Fail(ref kept, at + $" title{p.TitleRoom}");
            }
        }
        string Detail(bool ok) => ok ? "" : ": " + first;
        check("Compact spacing: while parts come and go in a drag, no part lies over another" + Detail(apart), apart);
        check("Compact spacing: a part on its way in or out is drawn within its own room" + Detail(inside), inside);
        check("Compact spacing: parts on their way out never push the row past its width" + Detail(within), within);
        check("Compact spacing: while parts come and go, the cover, equalizer, clock and the title at its shortest stay whole" + Detail(kept), kept);
        // The drawing of a part on its way out before: .82 to 1 of its width, in a room of its width times its presence.
        check("Compact spacing: a fading part is drawn no wider than its room", Enumerable.Range(0, 101).All(k => 44 * CompactSpacing.Zoom(k / 100.0) <= 44 * (k / 100.0) + 1e-9)
            && 44 * (.82 + .18 * .5) > 44 * .5);
    }

    private static CompactSpacing.Part[] Parts(params double[] widths) => [.. widths.Select(w => new CompactSpacing.Part(w, 1))];

    // The gaps between neighbours, from the right edge of one to the left edge of the next.
    private static double[] Gaps(CompactSpacing.Placement p) => [.. Enumerable.Range(0, p.Left.Length - 1).Select(i => p.Left[i + 1] - (p.Left[i] + p.Room[i]))];
    private static double Right(CompactSpacing.Placement p) => p.Left[^1] + p.Room[^1];

    private static void Examples(Action<string, bool> check)
    {
        // Cover 20, rings 32, title, equalizer 18, clock 33: 103 DIPs of parts and 4 gaps of 8 around the title.
        var parts = Parts(20, 32, 0, 18, 33);
        var tight = CompactSpacing.Arrange(parts, 2, 300, 200, 8, 1, 1);
        check("Compact spacing: a title longer than its room takes all the spare width, every gap one unit",
            tight.TitleRoom == 200 - 103 - 32 && Gaps(tight).All(g => g == 8) && tight.Left[0] == 0 && Right(tight) == 200);
        var fits = CompactSpacing.Arrange(parts, 2, 60, 200, 8, 1, 1);
        // 65 spare: 60 to the title, then 5 more over 4 gaps: one DIP each, the last DIP split between the ends.
        check("Compact spacing: a title that fits takes the width of its text, the rest widens every gap alike",
            fits.TitleRoom == 60 && Gaps(fits).All(g => g == 9) && fits.Left[0] == 0 && Right(fits) == 199);
        var wide = CompactSpacing.Arrange(parts, 2, 60, 400, 8, 1, 1);
        check("Compact spacing: gaps stop at twice the unit; beyond that the content stands centred",
            wide.TitleRoom == 60 && Gaps(wide).All(g => g == 16) && Math.Abs(wide.Left[0] - (400 - Right(wide))) <= 1);
        var longTitle = CompactSpacing.Arrange(parts, 2, 500, 600, 8, 1, 1);
        check("Compact spacing: a title longer than TitleMax gets more room once the gaps are at their widest, up to its text",
            Gaps(longTitle).All(g => g == 16) && longTitle.TitleRoom == 600 - 103 - 64 && longTitle.Left[0] == 0
            && CompactSpacing.Arrange(parts, 2, 300, 900, 8, 1, 1) is { TitleRoom: 300 } spread && Math.Abs(spread.Left[0] - (900 - Right(spread))) <= 1);
        var capped = CompactSpacing.Arrange(parts, 2, 500, 300, 8, 1, 1);
        check("Compact spacing: up to TitleMax the title comes before wider gaps", capped.TitleRoom == 161 && Gaps(capped).All(g => g == 9) && Right(capped) == 300);
        var none = CompactSpacing.Arrange(Parts(20, 18, 33), -1, 0, 300, 8, 1, 1);
        check("Compact spacing: without a title the parts stand centred, their gaps at most twice the unit",
            Gaps(none).All(g => g == 16) && Math.Abs(none.Left[0] - (300 - Right(none))) <= 1 && none.TitleRoom == 0);
        var scaled = CompactSpacing.Arrange(parts, 2, 1000, 2000, 12, 1.5, 1);
        check("Compact spacing: TitleMax follows the height", Gaps(scaled).All(g => g == 24) && scaled.TitleRoom == 1000
            && CompactSpacing.Arrange(parts, 2, 1000, 400, 12, 1.5, 1).TitleRoom == 241);
        check("Compact spacing: nonsense in gives nothing out of the row",
            CompactSpacing.Arrange([], -1, 0, 100, 8, 1, 1).Left.Length == 0
            && CompactSpacing.Arrange(Parts(double.NaN, -5, 20), -1, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN) is var odd
            && odd.Left.Concat(odd.Room).All(v => double.IsFinite(v) && v >= 0));
    }

    // At every width, height and display scale, every gap between neighbours is the same, at least the unit and at most twice it,
    // and the row never runs past its width.
    private static void Uniform(Action<string, bool> check)
    {
        bool even = true, bounded = true, within = true, monotonic = true; string first = "";
        void Fail(ref bool flag, string what) { if (flag && first.Length == 0) first = what; flag = false; }
        foreach (var dpi in Dpis) foreach (var s in new[] { .75, 1, 1.5, 2 }) foreach (var title in new[] { 0d, 40, 140, 600 })
        {
            int lastCount = 0;
            for (int w = 60; w <= 1400; w += 3)
            {
                double scale = CompactMusicLayout.SafeScale(s);
                double clock = Math.Ceiling(33 * scale - .001);
                double[] readings = [.. Readings.Select(r => Math.Ceiling(r * scale))];
                double row = Math.Floor(w * dpi + 1e-6) / dpi;
                var l = CompactMusicLayout.Calculate(row, s, true, 3, title > 0, clock, readings, CompactMusicLayout.Whole(8 * scale));
                // The parts the layout shows, left to right, as the row places them.
                var widths = new List<double>(); int t = -1;
                if (l.CoverWidth > 0) widths.Add(l.CoverWidth);
                if (l.RingWidth > 0) widths.Add(l.RingWidth);
                if (l.TextWidth > 0) { t = widths.Count; widths.Add(0); }
                double[] shown = [.. readings.Take(l.MonitorItems)];
                widths.AddRange(shown);
                if (l.EqualizerWidth > 0) widths.Add(l.EqualizerWidth);
                if (l.ClockWidth > 0) widths.Add(l.ClockWidth);
                if (widths.Count < 2) continue;
                int count = widths.Count;
                if (count < lastCount) Fail(ref monotonic, $"count dpi{dpi} s{s} t{title} w{w}");
                lastCount = count;
                var p = CompactSpacing.Arrange(Parts([.. widths]), t, title, row, l.Gap, s, dpi);
                var gaps = Gaps(p);
                string at = $"dpi{dpi} s{s} title{title} w{w} gaps[{string.Join(",", gaps.Select(g => g.ToString("0.###")))}]";
                if (gaps.Max() - gaps.Min() > .5 || gaps.Max() - gaps.Min() > 1e-9) Fail(ref even, at);
                double unit = Math.Round(l.Gap * dpi) / dpi;
                if (gaps.Min() < unit - 1 / dpi - 1e-9 || gaps.Max() > CompactSpacing.MaxGap * unit + 1e-9) Fail(ref bounded, at);
                if (p.Left[0] < -1e-9 || Right(p) > row + 1e-9) Fail(ref within, at);
                // The title never loses the room the layout promised it while the gaps are wider than the unit.
                if (t >= 0 && gaps[0] > unit + 1e-9 && p.Room[t] < Math.Min(title, CompactSpacing.TitleMax * CompactMusicLayout.SafeScale(s)) - 1 / dpi) Fail(ref bounded, "title " + at);
            }
        }
        string Detail(bool ok) => ok ? "" : ": " + first;
        check("Compact spacing: every gap in the row is the same, at every width, height and display scale" + Detail(even), even);
        check("Compact spacing: gaps are at least the unit and at most twice it, and the title fills first" + Detail(bounded), bounded);
        check("Compact spacing: the row never runs past its width" + Detail(within), within);
        check("Compact spacing: widening the row never takes a part away" + Detail(monotonic), monotonic);
    }

    // The notch and the capsule, stretched freely, place their parts as evenly.
    private static void Surfaces(Action<string, bool> check)
    {
        var screen = new DipRect(0, 0, 2560, 1440); var area = new DipRect(0, 0, 2560, 1392);
        bool even = true; string first = "";
        foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Capsule })
        foreach (var height in new[] { 24d, 32, 48 })
        for (int saved = 120; saved <= 900; saved += 11)
        {
            var settings = new DeckSettings { NotchWidth = saved, NotchHeight = height, CapsuleWidth = saved, CapsuleHeight = height };
            settings.Normalize();
            double scale = height / CollapsedLimits.BaseHeight, face = Math.Ceiling(33 * scale - .001);
            double[] readings = [.. Readings.Select(r => Math.Ceiling(r * scale))];
            double requiredWidth = CompactMusicLayout.Calculate(0, scale, true, 2, true, face).Required;
            var shape = CollapsedGeometry.Resolve(settings, style, screen, area, 1.5, null, Math.Ceiling(requiredWidth) + 1);
            double content = style == CompactStyle.Notch ? Math.Min(shape.ContentWidth, shape.Surface.Width - 2 * (shape.Shoulder + shape.BottomRadius)) : shape.ContentWidth;
            content = Math.Floor(content * 1.5 + .001) / 1.5;
            var l = CompactMusicLayout.Calculate(content, shape.Scale, true, 2, true, face, readings, CompactMusicLayout.Whole(8 * scale));
            var widths = new List<double> { l.CoverWidth };
            if (l.RingWidth > 0) widths.Add(l.RingWidth);
            int t = widths.Count; widths.Add(0);
            widths.AddRange(readings.Take(l.MonitorItems));
            widths.Add(l.EqualizerWidth); widths.Add(l.ClockWidth);
            var p = CompactSpacing.Arrange(Parts([.. widths]), t, 90 * scale, content, l.Gap, shape.Scale, 1.5);
            var gaps = Gaps(p);
            if (gaps.Max() - gaps.Min() > 1e-9 || Right(p) > content + 1e-9 || p.Left[0] < 0)
            { if (even) first = $"{style} h{height} saved{saved}"; even = false; }
        }
        check("Compact spacing: a notch or capsule stretched to any width keeps every gap the same, within its content" + (even ? "" : ": " + first), even);
    }

    // Parts coming and going move the others smoothly: a small step in presence is a small step in place.
    private static void Motion(Action<string, bool> check)
    {
        bool smooth = true, closes = true;
        foreach (var width in new[] { 260d, 300, 420 })
        {
            CompactSpacing.Placement? last = null;
            for (int k = 0; k <= 100; k++)
            {
                double p = k / 100.0;
                // The third reading on its way in beside the title.
                var parts = new[] { new CompactSpacing.Part(20, 1), new(0, 1), new(44, 1), new(44, p), new(18, 1), new(33, 1) };
                var now = CompactSpacing.Arrange(parts, 1, 70, width, 8, 1, 1);
                if (last is not null)
                    for (int i = 0; i < parts.Length; i++)
                        if ((i != 3 || k > 1) && Math.Abs(now.Left[i] - last.Left[i]) > 4) smooth = false;
                if (k == 0) closes &= now.Room[3] == 0 && Math.Abs(now.Left[4] - (now.Left[2] + 44 + now.Gap)) < 1e-9;
                last = now;
            }
        }
        check("Compact spacing: a part on its way in or out moves its neighbours a little at a time", smooth);
        check("Compact spacing: a part that has left takes no room and no gap", closes);
    }

    // A figure that flickers between short and long keeps the room of the long one; it gives it back after a while.
    private static void Held(Action<string, bool> check)
    {
        var held = new HeldWidth();
        double[] figures = [13, 27, 13, 20, 13, 27, 13];
        bool steady = true;
        double first = held.Update(figures[0], 0);
        for (int i = 1; i < figures.Length; i++) { double w = held.Update(figures[i], i); if (i >= 1) steady &= w == 27; }
        check("Compact readings: a figure that changes every second keeps its widest recent room", first == 13 && steady);
        double later = held.Update(13, 5 + HeldWidth.Hold - .1), after = held.Update(13, 5 + HeldWidth.Hold + .1);
        check("Compact readings: a narrower figure gives the room back once it has stayed narrower a while", later == 27 && after == 13);
        check("Compact readings: a wider figure takes its room at once", held.Update(40, 100) == 40 && held.Update(double.NaN, 101) == 40);
        // Readings whose width holds still leave every neighbour where it was.
        var a = CompactSpacing.Arrange(Parts(20, 0, 44, 44, 41, 18, 33), 1, 400, 330, 8, 1, 1);
        var b = CompactSpacing.Arrange(Parts(20, 0, 44, 44, 41, 18, 33), 1, 400, 330, 8, 1, 1);
        check("Compact readings: the same widths place every part in the same place", a.Left.SequenceEqual(b.Left) && a.Room.SequenceEqual(b.Room));
    }
}
