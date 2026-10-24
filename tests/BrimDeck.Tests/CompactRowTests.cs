using BrimDeck.Core;

// The collapsed row (CompactMusicLayout) and the quota notice (CompactAlertLayout): every part whole or absent, what the row
// must show first, the rest in priority order, sizes from the height alone; and the notch and the capsule grow to what they
// must show (CollapsedGeometry).
internal static class CompactRowTests
{
    // The widths the collapsed row's readings offer at the default height (CPU, memory, core columns, download, GPU, upload).
    private static readonly double[] RowReadings = [44, 44, 56, 41, 44, 41];

    public static void Run(Action<string, bool> check)
    {
        Examples(check);
        Properties(check);
        Surfaces(check);
        Narrowing(check);
        Alert(check);
    }

    // The capsule (and the notch) dragged from wide down to the narrowest the grips allow (MainWindow.CompactWidthRange: the
    // surface for Required, rounded up a DIP), with media playing, two quota rings and every reading: the equalizer, cover, clock
    // and title stay at every width, every ring and reading leaves before any of them, and the narrowest surface still holds them.
    private static void Narrowing(Action<string, bool> check)
    {
        var screen = new DipRect(0, 0, 2560, 1440); var area = new DipRect(0, 0, 2560, 1392);
        bool required = true, ordered = true, floor = true; string first = "";
        void Fail(ref bool flag, string what) { if (flag && first.Length == 0) first = what; flag = false; }
        foreach (var style in new[] { CompactStyle.Capsule, CompactStyle.Notch })
        foreach (var height in new[] { 24d, 32, 40, 48 })
        foreach (var dpi in new[] { 1d, 1.25, 1.5, 2 })
        {
            double scale = height / CollapsedLimits.BaseHeight, face = Math.Ceiling(38 * scale - .001), inner = CompactMusicLayout.Whole(8 * scale);
            double[] offered = [.. RowReadings.Select(r => Math.Ceiling(r * scale))];
            double requiredWidth = Math.Ceiling(CompactMusicLayout.Calculate(0, scale, true, 2, true, face, offered, inner).Required) + 1;
            double fit = CollapsedGeometry.SurfaceWidth(style, height, requiredWidth);
            double narrowest = Math.Max(CollapsedGeometry.WidthRange(style, screen, area).Min, fit);
            int optionalBefore = int.MaxValue;
            var surfaces = new List<double>();
            for (double s = 900; s > narrowest; s--) surfaces.Add(s);
            surfaces.Add(narrowest);
            foreach (var surface in surfaces)
            {
                var settings = new DeckSettings { NotchWidth = surface, NotchHeight = height, CapsuleWidth = surface, CapsuleHeight = height };
                settings.Normalize();
                var shape = CollapsedGeometry.Resolve(settings, style, screen, area, dpi, 0, requiredWidth);
                double content = style == CompactStyle.Notch ? Math.Min(shape.ContentWidth, shape.Surface.Width - 2 * (shape.Shoulder + shape.BottomRadius)) : shape.ContentWidth;
                content = Math.Floor(content * dpi + .001) / dpi;
                var l = CompactMusicLayout.Calculate(content, scale, true, 2, true, face, offered, inner);
                string at = $"{style} h{height} dpi{dpi} surface{surface:0.##}";
                double minimum = CompactMusicLayout.Whole(CompactMusicLayout.MinimumText * scale);
                if (l.EqualizerWidth == 0 || l.CoverWidth == 0 || l.ClockWidth == 0 || l.TextWidth < minimum - 1e-9) Fail(ref required, at);
                // Rings and readings only leave as the row narrows, never come back, and never stay while a required part is gone.
                int optional = l.VisibleRings + l.MonitorItems;
                if (optional > optionalBefore || l.MonitorItems > 0 && l.VisibleRings < 2) Fail(ref ordered, at);
                optionalBefore = optional;
                if (Math.Abs(surface - narrowest) < 1e-9 && (narrowest == fit && optional != 0 || Math.Abs(shape.Surface.Width - narrowest) > 1e-9)) Fail(ref floor, at + " at the narrowest");
            }
        }
        string Detail(bool ok) => ok ? "" : ": " + first;
        check("Compact drag: down to the narrowest surface, the equalizer, cover, clock and the title at its shortest always show" + Detail(required), required);
        check("Compact drag: quota rings and readings leave before any required part, readings before rings" + Detail(ordered), ordered);
        check("Compact drag: the narrowest surface the grips allow holds the required parts, and no more where they set it" + Detail(floor), floor);
        // The row as seen on screen: 300 DIPs of capsule with two quota rings and every reading still has its equalizer.
        var seen = CompactMusicLayout.Calculate(300 - 32, 1, true, 2, true, 38, RowReadings, 8);
        check("Compact drag: a 300 DIP capsule shows the equalizer beside the rings and the readings that fit", seen is { EqualizerWidth: 18, VisibleRings: 2 } && seen.MonitorItems < RowReadings.Length);
    }

    private static void Examples(Action<string, bool> check)
    {
        var standard = CompactMusicLayout.Calculate(178, 1, true, 4, true, 33);
        // 33 + (8 + 20) + (8 + 18) + (8 + 48) = 143 must show; one ring more is 163, a second 183.
        check("Compact row: the default notch with a clock shows the clock, cover, equalizer, title and one rotating whole ring",
            standard is { ClockWidth: 33, CoverWidth: 20, EqualizerWidth: 18, VisibleRings: 1, RingWidth: 12, ScrollRings: true, Between: 8, Gap: 8, Required: 143, TextWidth: 63, Free: 0 }
            && standard.CoverSize == 20 && standard.EqualizerBar == 3 && standard.TextFont == 11.5 && standard.TextHeight == 22 && standard.RowHeight == 24
            && standard.RingThickness == 2 && standard.TextEdge == 6 && standard.RingEdge == 3);
        var wide = CompactMusicLayout.Calculate(400, 1, true, 4, true, 33);
        check("Compact row: a wider row shows every ring standing still and gives the title the rest", wide is { VisibleRings: 4, RingWidth: 72, ScrollRings: false, TextWidth: 225 });
        var tall = CompactMusicLayout.Calculate(400, 1.5, true, 4, true, 50);
        check("Compact row: a taller row scales the cover, equalizer, rings and type, in whole and half DIPs", tall.CoverSize == 30 && tall.CoverWidth == 30 && tall.EqualizerSize == 27
            && tall.EqualizerHeight == 21 && tall.EqualizerWidth == 27 && tall.ClockWidth == 50 && tall.Gap == 12 && tall.TextFont == 17.5 && tall.TextHeight == 33 && tall.RowHeight == 36
            && tall.RingSize == 18 && tall.RingThickness == 3 && tall.TextEdge == 9 && tall.Required == 50 + 12 + 30 + 12 + 27 + 12 + 72);
        var shortRow = CompactMusicLayout.Calculate(178, .75, true, 4, true, 25);
        check("Compact row: a shorter row scales down", shortRow.CoverSize == 15 && shortRow.TextFont == 8.5 && shortRow.RowHeight == 18 && shortRow.RingThickness == 1.5);

        var short1 = CompactMusicLayout.Calculate(142, 1, true, 4, true, 33);
        check("Compact row: a row a DIP short of what it must show gives the scrolling title what is left, and holds nothing more",
            short1 is { ClockWidth: 33, CoverWidth: 20, EqualizerWidth: 18, TextWidth: 47, RingWidth: 0, VisibleRings: 0, TextScrolls: true, Free: 0 }
            && CompactMusicLayout.Calculate(87 + 8 + 17, 1, true, 4, true, 33) is { TextWidth: 0, Free: 25 });
        check("Compact row: a clock that does not fit leaves the row empty", CompactMusicLayout.Calculate(20, 1, true, 4, true, 33) is { ClockWidth: 0, CoverWidth: 0, EqualizerWidth: 0, TextWidth: 0, RingWidth: 0 });
        check("Compact row: a lone clock shows whole, the rest of the row free", CompactMusicLayout.Calculate(100, 1, false, 0, false, 33) is { ClockWidth: 33, Free: 67, Required: 33 });
        check("Compact row: without media the row must show only the clock", CompactMusicLayout.Calculate(300, 1, false, 4, false, 33).Required == 33
            && CompactMusicLayout.Calculate(300, 1, false, 4, false, 0).Required == 0);
        check("Compact row: rings alone show as many whole rings as fit", CompactMusicLayout.Calculate(60, 1, false, 4, false, 0) is { VisibleRings: 3, RingWidth: 52, ScrollRings: true, Free: 8 }
            && CompactMusicLayout.Calculate(11, 1, false, 4, false, 0) is { VisibleRings: 0, RingWidth: 0 });

        // Readings come one by one in their priority order, after every ring.
        var readings = CompactMusicLayout.Calculate(300, 1, true, 2, true, 33, RowReadings, 10);
        // 143 + 2 rings (40) = 183; CPU 8 + 44 = 235; memory 10 + 44 = 289; columns 10 + 56 = 355 does not fit.
        check("Compact row: the readings after the rings, whole, in priority order, the first that does not fit ending the row",
            readings is { VisibleRings: 2, ScrollRings: false, MonitorItems: 2, MonitorWidth: 98, TextWidth: 59 });
        var stretched = CompactMusicLayout.Calculate(600, 1, true, 2, true, 33, RowReadings, 10);
        check("Compact row: a stretched row shows every reading and gives the title the rest", stretched.MonitorItems == 6 && stretched.MonitorWidth == 270 + 50
            && stretched.TextWidth == 600 - (143 - 48) - 40 - 8 - 320);
        var fewRings = CompactMusicLayout.Calculate(170, 1, true, 4, true, 33, RowReadings, 10);
        check("Compact row: no reading shows while a ring is still left out", fewRings is { VisibleRings: 1, MonitorItems: 0, MonitorWidth: 0 });
        check("Compact monitor: the row's priority is the leave order reversed",
            CompactMonitor.Priority.SequenceEqual([CompactMonitorItems.Cpu, CompactMonitorItems.Memory, CompactMonitorItems.Columns, CompactMonitorItems.Download, CompactMonitorItems.Gpu, CompactMonitorItems.Upload])
            && CompactMonitor.First(CompactMonitorItems.All, 2) == (CompactMonitorItems.Cpu | CompactMonitorItems.Memory)
            && CompactMonitor.First(CompactMonitorItems.Memory | CompactMonitorItems.Upload, 1) == CompactMonitorItems.Memory
            && CompactMonitor.First(CompactMonitorItems.All, 0) == CompactMonitorItems.None && CompactMonitor.First(CompactMonitorItems.Gpu, 5) == CompactMonitorItems.Gpu);
        double natural = CompactMusicLayout.NaturalWidth(1, true, 3, true, 40, 150);
        var fitted = CompactMusicLayout.Calculate(natural, 1, true, 3, true, 40, 150);
        check("Compact row: at its natural width the row shows every part, the title at its shortest",
            natural == 40 + 28 + 26 + 56 + 60 + 158 && fitted is { MonitorWidth: 150, MonitorItems: 1, VisibleRings: 3, ScrollRings: false, TextWidth: 48 });
    }

    private static void Properties(Action<string, bool> check)
    {
        double[] scales = [.75, 1, 1.25, 1.5, 2, 0, double.NaN];
        double[] clocks = [0, 33, 61];
        int[] ringCounts = [0, 1, 4, 8];
        bool whole = true, within = true, required = true, ordered = true, monotonic = true, finite = true, sameSizes = true;
        string firstFailure = "";
        void Fail(ref bool flag, string what) { if (flag && firstFailure.Length == 0) firstFailure = what; flag = false; }
        foreach (var s in scales) foreach (var clock in clocks) foreach (var media in new[] { true, false }) foreach (var text in new[] { true, false })
        foreach (var rings in ringCounts) foreach (var monitor in new[] { false, true })
        {
            double scale = CompactMusicLayout.SafeScale(s);
            double[] offered = monitor ? [.. RowReadings.Select(r => r * scale)] : [];
            double inner = monitor ? CompactMusicLayout.Whole(10 * scale) : 0, face = clock * scale;
            CompactMusicLayout? last = null; string? sizes = null;
            for (int w = 0; w <= 900; w++)
            {
                var l = CompactMusicLayout.Calculate(w, s, media, rings, text, face, offered, inner);
                string at = $"s{s} clock{clock} media{media} text{text} rings{rings} monitor{monitor} w{w}";
                double[] values = [l.CoverWidth, l.EqualizerWidth, l.ClockWidth, l.RingWidth, l.TextWidth, l.MonitorWidth, l.Free, l.Required, l.Natural, l.Between, l.MiddleWidth];
                if (!values.All(v => double.IsFinite(v) && v >= 0)) Fail(ref finite, "finite " + at);
                // Every part is whole or absent; the title is never shorter than its shortest.
                double minimum = CompactMusicLayout.Whole(CompactMusicLayout.MinimumText * l.Scale);
                double ringSpan = l.VisibleRings == 0 ? 0 : l.VisibleRings * l.RingSize + (l.VisibleRings - 1) * l.RingGap;
                double readingSpan = 0;
                for (int i = 0; i < l.MonitorItems; i++) readingSpan += offered[i] + (i > 0 ? inner : 0);
                if (l.CoverWidth is not 0 && l.CoverWidth != l.CoverSize || l.EqualizerWidth is not 0 && l.EqualizerWidth != l.EqualizerSize
                    || l.ClockWidth is not 0 && Math.Abs(l.ClockWidth - face) > 1e-9 || Math.Abs(l.RingWidth - ringSpan) > 1e-9 || Math.Abs(l.MonitorWidth - readingSpan) > 1e-9
                    || l.TextWidth is > 0 && l.TextWidth < minimum - 1e-9 && (l.VisibleRings > 0 || l.MonitorItems > 0 || l.Free > 0 || l.TextWidth < CompactMusicLayout.Whole(CompactMusicLayout.ScrollingText * l.Scale) - 1e-9) || l.ScrollRings != (l.VisibleRings > 0 && l.VisibleRings < rings))
                    Fail(ref whole, "whole " + at);
                // The shown parts, their gaps and the free space make up the row exactly.
                var widths = new[] { l.CoverWidth, l.RingWidth, l.TextWidth, l.MonitorWidth, l.EqualizerWidth, l.ClockWidth }.Where(v => v > 0).ToArray();
                double total = widths.Sum() + Math.Max(0, widths.Length - 1) * l.Gap;
                if (widths.Length > 0 && Math.Abs(total + l.Free - w) > 1e-6 || total > w + 1e-6) Fail(ref within, "within " + at);
                // A row as wide as what it must show shows all of it.
                if (w >= l.Required && ((clock > 0 && l.ClockWidth == 0) || (media && (l.CoverWidth == 0 || l.EqualizerWidth == 0)) || (text && l.TextWidth == 0)))
                    Fail(ref required, "required " + at);
                // Optional parts only after every required one; readings only after every ring; readings in priority order.
                bool requiredShown = (clock == 0 || l.ClockWidth > 0) && (!media || l.CoverWidth > 0 && l.EqualizerWidth > 0) && (!text || l.TextWidth > 0);
                if ((l.VisibleRings > 0 || l.MonitorItems > 0) && !requiredShown || l.MonitorItems > 0 && l.VisibleRings < rings || l.MonitorItems > offered.Length)
                    Fail(ref ordered, "order " + at);
                // A DIP wider never hides a part.
                if (last is not null && (last.CoverWidth > 0 && l.CoverWidth == 0 || last.EqualizerWidth > 0 && l.EqualizerWidth == 0 || last.ClockWidth > 0 && l.ClockWidth == 0
                    || last.TextWidth > 0 && l.TextWidth == 0 || l.VisibleRings < last.VisibleRings || l.MonitorItems < last.MonitorItems || l.TextWidth < last.TextWidth - 1e-9 && l.VisibleRings == last.VisibleRings && l.MonitorItems == last.MonitorItems))
                    Fail(ref monotonic, "monotonic " + at);
                // Sizes follow the height alone.
                string key = string.Join("|", l.RingSize, l.RingGap, l.RingThickness, l.CoverSize, l.EqualizerSize, l.EqualizerHeight, l.EqualizerBar, l.TextFont, l.TextHeight, l.RowHeight, l.Gap, l.Required, l.Natural);
                sizes ??= key;
                if (key != sizes) Fail(ref sameSizes, "sizes " + at);
                last = l;
            }
            // At its natural width every part shows.
            var full = CompactMusicLayout.Calculate(CompactMusicLayout.Calculate(0, s, media, rings, text, face, offered, inner).Natural, s, media, rings, text, face, offered, inner);
            if (full.VisibleRings != rings || full.MonitorItems != offered.Length || clock > 0 && full.ClockWidth == 0) Fail(ref ordered, $"natural s{s} clock{clock} rings{rings}");
        }
        string Detail(bool ok) => ok ? "" : ": " + firstFailure;
        check("Compact row: every width and scale gives finite, non-negative widths" + Detail(finite), finite);
        check("Compact row: every part is whole or absent, at every width; only the title shows in part, alone after the required parts" + Detail(whole), whole);
        check("Compact row: the shown parts and gaps fill the row exactly, never more" + Detail(within), within);
        check("Compact row: a row as wide as Required shows the clock, and while media plays the cover, equalizer and title" + Detail(required), required);
        check("Compact row: rings only after the required parts, readings only after every ring, in priority order" + Detail(ordered), ordered);
        check("Compact row: widening the row never hides a part" + Detail(monotonic), monotonic);
        check("Compact row: every size and the required width depend on the height alone" + Detail(sameSizes), sameSizes);
    }

    // The notch and the capsule, at every height and saved width, are wide enough for what they must show, the notch clear of its
    // shoulders; and with the same content width both show the same parts.
    private static void Surfaces(Action<string, bool> check)
    {
        var screen = new DipRect(0, 0, 2560, 1440); var area = new DipRect(0, 0, 2560, 1392);
        bool fits = true, same = true, notchReadings = true; string first = "";
        foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Capsule })
        foreach (var height in new[] { 24d, 32, 40, 48, 64 })
        for (int saved = 100; saved <= 800; saved += 7)
        {
            var settings = new DeckSettings { NotchWidth = saved, NotchHeight = height, CapsuleWidth = saved, CapsuleHeight = height };
            settings.Normalize();
            double scale = height / CollapsedLimits.BaseHeight, face = 33 * scale;
            double requiredWidth = CompactMusicLayout.Calculate(0, scale, true, 4, true, face).Required;
            var shape = CollapsedGeometry.Resolve(settings, style, screen, area, 1.5, 0, Math.Ceiling(requiredWidth) + 1);
            // As the window reads the content area (MainWindow.CompactContentWidth): clear of the notch's shoulders and corners.
            double content = style == CompactStyle.Notch ? Math.Min(shape.ContentWidth, shape.Surface.Width - 2 * (shape.Shoulder + shape.BottomRadius)) : shape.ContentWidth;
            content = Math.Floor(content * 1.5 + .001) / 1.5;
            var l = CompactMusicLayout.Calculate(content, shape.Scale, true, 4, true, face, RowReadings.Select(r => r * scale).ToArray(), CompactMusicLayout.Whole(10 * scale));
            if (l.ClockWidth == 0 || l.CoverWidth == 0 || l.EqualizerWidth == 0 || l.TextWidth == 0 || shape.Surface.Width < Math.Min(saved, 800) - 1e-9)
            { if (fits) first = $"{style} h{height} saved{saved}"; fits = false; }
            var again = CompactMusicLayout.Calculate(content, shape.Scale, true, 4, true, face, RowReadings.Select(r => r * scale).ToArray(), CompactMusicLayout.Whole(10 * scale));
            same &= l == again;
            // A notch stretched past 620 DIPs at the default height shows every reading beside the media and four rings (551 DIPs of content), as a capsule does.
            if (style == CompactStyle.Notch && height == 32 && saved >= 620) notchReadings &= l.MonitorItems == RowReadings.Length;
        }
        check("Compact fit: every notch and capsule is wide enough for the clock, cover, equalizer and title" + (fits ? "" : ": " + first), fits);
        check("Compact fit: the same inputs give the same row", same);
        check("Compact fit: a stretched notch shows the system readings beside the media, as the capsule does", notchReadings);
        var capsule = new DeckSettings { CapsuleWidth = 140, CapsuleHeight = 32 };
        check("Compact fit: the surface grows to what it must show, never below the saved width", CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, 0, 152).Surface.Width == 184
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Capsule, screen, area, 1, 0, 50).Surface.Width == 140
            && CollapsedGeometry.Resolve(new DeckSettings { NotchHeight = 24 }, CompactStyle.Notch, screen, area, 1, 0, 300).Surface.Width == 356
            && CollapsedGeometry.Resolve(capsule, CompactStyle.Line, screen, area, 1, 0, 300).Surface.Width == CollapsedLimits.LineLengthDefault);
        check("Compact fit: surface width for a content width", CollapsedGeometry.SurfaceWidth(CompactStyle.Capsule, 32, 100) == 132 && CollapsedGeometry.SurfaceWidth(CompactStyle.Notch, 32, 100) == 160
            && CollapsedGeometry.SurfaceWidth(CompactStyle.Notch, 24, 100) == 156 && CollapsedGeometry.SurfaceWidth(CompactStyle.Line, 4, 100) == 0
            && CollapsedGeometry.SurfaceWidth(CompactStyle.Capsule, 32, 0) == 0 && CollapsedGeometry.SurfaceWidth(CompactStyle.Capsule, 32, double.NaN) == 0
            && CollapsedGeometry.SurfaceWidth(CompactStyle.Capsule, 32, 5000) == CollapsedLimits.CapsuleWidthMax);
    }

    // The quota notice: the used text always stays; the ring, the name and the reset time follow whole while they fit.
    private static void Alert(Action<string, bool> check)
    {
        var full = CompactAlertLayout.Calculate(300, 1, 60, 70, 40);
        check("Compact notice: with room every part shows at the default sizes", full.RingWidth == 22 && full.NameWidth == 68 && full.TextWidth == 70 && full.ResetWidth == 48
            && full.RingSize == 14 && full.FontSize == 11.5 && full.LineHeight == 16 && Math.Abs(full.RingThickness - 2.2) < 1e-9);
        check("Compact notice: the name keeps its 76 DIP cap", CompactAlertLayout.Calculate(300, 1, 120, 70, 0) is { NameWidth: 84, ResetWidth: 0 });
        check("Compact notice: a name that does not fit leaves whole, and the reset time with it",
            CompactAlertLayout.Calculate(150, 1, 60, 70, 40) is { RingWidth: 22, NameWidth: 0, TextWidth: 70, ResetWidth: 0 });
        check("Compact notice: the reset time leaves first", CompactAlertLayout.Calculate(170, 1, 60, 70, 40) is { RingWidth: 22, NameWidth: 68, TextWidth: 70, ResetWidth: 0 });
        check("Compact notice: only a row shorter than the used text cuts it", CompactAlertLayout.Calculate(60, 1, 60, 70, 40) is { RingWidth: 0, NameWidth: 0, TextWidth: 60, ResetWidth: 0 });
        check("Compact notice: a taller notice scales its ring and type", CompactAlertLayout.Calculate(1000, 2, 60, 70, 40) is { RingSize: 28, FontSize: 23, LineHeight: 32, Gap: 16, NameWidth: 76 });
        double[] widths = [0, .4, 1, 7, 17.9, 18, 36, 54, 84, 91.3, 115, 164, 178, 400, 1e6, double.NaN, -5, double.PositiveInfinity];
        double[] scales = [.75, 1, 1.25, 1.5, 2, 3, 0, -1, double.NaN, double.PositiveInfinity, 1e-9];
        bool sane = true, whole = true;
        foreach (var w in widths) foreach (var s in scales) foreach (var part in new[] { 0, 30, double.NaN, -4, 1e6 })
        {
            var a = CompactAlertLayout.Calculate(w, s, part, part, part);
            double row = double.IsFinite(w) && w > 0 ? w : 0, p = double.IsFinite(part) && part > 0 ? part : 0;
            sane &= new[] { a.RingWidth, a.NameWidth, a.TextWidth, a.ResetWidth, a.RingSize, a.FontSize }.All(v => double.IsFinite(v) && v >= 0) && a.Total <= row + 1e-9;
            whole &= a.RingWidth is 0 || a.RingWidth == a.RingSize + a.Gap;
            whole &= a.NameWidth is 0 || a.NameWidth == Math.Min(p, CompactMusicLayout.Whole(76 * a.Scale)) + a.Gap;
            whole &= a.ResetWidth is 0 || a.ResetWidth == a.Gap + p;
        }
        check("Compact notice: every width and scale gives finite, non-negative regions within the row", sane);
        check("Compact notice: the ring, name and reset time are whole or absent", whole);
    }
}
