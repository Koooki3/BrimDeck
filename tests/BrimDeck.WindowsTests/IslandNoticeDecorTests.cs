using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

// A theme's marks on a notice (IslandDecor.Draw with the words' area given, B8 2026-11-01): the cut-corner theme's signal bar and
// hatching show at the notice's height, and no mark of any theme lies on the icon or the words. Checked on the drawing itself, point
// by point, at the sizes the collapsed island has at 100, 150 and 200 percent. The countdown's ring is checked here too: the arc moves
// in place with the clock.
internal static class IslandNoticeDecorTests
{
    internal static void Run(Action<string, bool> check)
    {
        Marks(check);
        Hud(check);
        Ring(check);
    }

    // The island at a scale, laid out as IslandNotice does: the plate PlateSize tall at the scale, the words between the insets the
    // collapsed shape has (CollapsedLimits: 30 for the notch, 16 for the capsule, at the scale of its 32 DIP base height).
    private static (Size Size, CornerRadius Corners, double Shoulder, Rect Words) Notch(double scale, double width = 238, double height = 32)
        => Island(scale, width, height, new CornerRadius(0, 0, 12 * scale, 12 * scale), 16 * scale, CollapsedLimits.NotchInset * scale);

    private static (Size Size, CornerRadius Corners, double Shoulder, Rect Words) Capsule(double scale, double width = 196, double height = 32)
        => Island(scale, width, height, new CornerRadius(height * scale / 2), 0, CollapsedLimits.CapsuleInset * scale);

    private static (Size Size, CornerRadius Corners, double Shoulder, Rect Words) Island(double scale, double width, double height, CornerRadius corners, double shoulder, double inset)
    {
        double w = width * scale, h = height * scale, plate = Math.Round(NoticeMetrics.PlateSize * scale);
        return (new Size(w, h), corners, shoulder, new Rect(inset, (h - plate) / 2, w - 2 * inset, plate));
    }

    private static DrawingVisual Draw(IslandDecor decor, (Size Size, CornerRadius Corners, double Shoulder, Rect Words) island, double cut, bool notice)
    {
        var outline = cut > 0 ? IslandBorder.BuildCut(island.Size.Width, island.Size.Height, island.Shoulder, island.Corners, 0, cut) : IslandBorder.BuildOutline(island.Size.Width, island.Size.Height, island.Shoulder, island.Corners, 0);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) decor.Draw(dc, island.Size, island.Corners, island.Shoulder, outline, notice ? island.Words : null);
        return visual;
    }

    private static int Hits(DrawingVisual visual, Rect area, double step = .5)
    {
        int hits = 0;
        for (double x = area.Left; x <= area.Right; x += step)
            for (double y = area.Top; y <= area.Bottom; y += step)
                if (VisualTreeHelper.HitTest(visual, new Point(x, y)) is not null) hits++;
        return hits;
    }

    private static void Marks(Action<string, bool> check)
    {
        foreach (var look in new[] { DaylightLook.Day, DaylightLook.Night })
        {
            var palette = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Endfield)!, look);
            var decor = IslandDecor.For(palette)!;
            bool onIsland = true, clear = true, bar = true, oldNone = true;
            foreach (var (form, scale) in new[] { 1d, 1.5, 2 }.SelectMany(scale => new[] { ("notch", scale), ("capsule", scale) }))
            {
                var island = form == "notch" ? Notch(scale) : Capsule(scale);
                // Before B8: the island's own call, at this height, draws no mark at all.
                if (island.Size.Height <= IslandDecor.MarksMinHeight) oldNone &= Hits(Draw(decor, island, palette.Effects.Cut, notice: false), new Rect(0, 0, island.Size.Width, island.Size.Height), 1) == 0;
                var visual = Draw(decor, island, palette.Effects.Cut, notice: true);
                double bottom = island.Size.Height;
                // The signal bar under the icon's side, the hatching under the words' other end: both lie on the bottom edge.
                double tabX = island.Shoulder + island.Corners.BottomLeft + 4 + IslandDecor.TabWidth / 2, hatchX = island.Size.Width - island.Shoulder - island.Corners.BottomRight - 4 - IslandDecor.HatchWidth / 2;
                bar &= VisualTreeHelper.HitTest(visual, new Point(tabX, bottom - 1)) is not null && VisualTreeHelper.HitTest(visual, new Point(hatchX, bottom - 1)) is not null;
                onIsland &= Hits(visual, new Rect(0, bottom - 3, island.Size.Width, 2), 1) > 0;
                // Nothing lies on the icon or the words.
                clear &= Hits(visual, island.Words) == 0;
            }
            check($"Island notice marks ({look}): the island's own call draws no mark below 56 DIP (the signal bar and hatching are for the open panel)", oldNone);
            check($"Island notice marks ({look}): a notice shows the theme's signal bar and hatching along its bottom edge at 100, 150 and 200 percent", bar && onIsland);
            check($"Island notice marks ({look}): no mark lies on the icon or the words at 100, 150 and 200 percent", clear);
        }

        // A taller island (a larger text scale) gives the marks their whole band; one with no room under the words gets none.
        var tall = Notch(1, height: 48);
        var palette2 = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Endfield)!, DaylightLook.Day);
        var decor2 = IslandDecor.For(palette2)!;
        var tallDrawn = Draw(decor2, tall, palette2.Effects.Cut, notice: true);
        check("Island notice marks: with room under the words they take the full band (5 DIP), and still leave the words alone",
            VisualTreeHelper.HitTest(tallDrawn, new Point(tall.Shoulder + tall.Corners.BottomLeft + 4 + 14, tall.Size.Height - IslandDecor.MarkBand + .5)) is not null && Hits(tallDrawn, tall.Words) == 0);
        var cramped = Notch(1, height: 22);
        var crampedWords = new Rect(cramped.Words.Left, 1, cramped.Words.Width, cramped.Size.Height - 2);
        var crampedDrawn = Draw(decor2, (cramped.Size, cramped.Corners, cramped.Shoulder, crampedWords), palette2.Effects.Cut, notice: true);
        check("Island notice marks: an island with no room under the words draws no mark at all", Hits(crampedDrawn, new Rect(0, 0, cramped.Size.Width, cramped.Size.Height), 1) == 0);

        // The scenery of the open panel (the sea, the palm) never lies under a notice's words, however tall the island.
        var holiday = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.IslandHoliday2)!, DaylightLook.Day);
        var scenery = IslandDecor.For(holiday)!;
        var big = Notch(1, height: 120);
        check("Island notice marks: a theme's sea and palm are left out of a notice, tall or not", Hits(Draw(scenery, big, holiday.Effects.Cut, notice: true), new Rect(0, 0, big.Size.Width, big.Size.Height), 2) == 0
            && Hits(Draw(scenery, big, holiday.Effects.Cut, notice: false), new Rect(0, 0, big.Size.Width, big.Size.Height), 2) > 0);
    }

    private static void Hud(Action<string, bool> check)
    {
        var palette = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Demo)!);
        var decor = IslandDecor.For(palette)!;
        bool clear = true, seen = true, shown = true;
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            // Tall enough for the HUD to be drawn at all (IslandDecor.HudMinHeight), and the notice's plate spans most of it.
            var island = Notch(scale, height: 44);
            var plain = Draw(decor, island, palette.Effects.Cut, notice: false);
            var withWords = Draw(decor, island, palette.Effects.Cut, notice: true);
            // Without the words' area the HUD's hexagon lies on them; with it, nothing does.
            // (The hexagon keeps its size in DIP while the words' area grows with the scale, so it is on them at the smaller scales.)
            if (scale <= 1.25) seen &= Hits(plain, island.Words) > 0;
            clear &= Hits(withWords, island.Words) == 0;
            // The cut and its leads along the edges are still there.
            shown &= Hits(withWords, new Rect(0, 0, island.Size.Width, island.Size.Height), 1) > 0;
        }
        check("Island notice HUD: the island's own drawing puts a corner's hexagon on the words' area at 100 and 125 percent (what the notice must avoid)", seen);
        check("Island notice HUD: on a notice no cut, lead or hexagon of the HUD lies on the icon or the words at 100 to 200 percent", clear);
        check("Island notice HUD: the corner cuts along the edges are still drawn", shown);
    }

    private static void Ring(Action<string, bool> check)
    {
        var ring = new CountdownRing(0, 18, 2, Colors.White, Colors.Gray);
        var path = ring.Children.OfType<Path>().Single();
        var data = path.Data;
        var segment = ((PathGeometry)data).Figures[0].Segments.OfType<ArcSegment>().Single();
        check("Countdown ring: at 0 nothing but the track is drawn", ring.Shown.None && path.Visibility != Visibility.Visible && ring.Children.OfType<Ellipse>().All(e => e.Visibility != Visibility.Visible || e.Stroke is not null));
        ring.Set(.25);
        check("Countdown ring: a quarter run, the arc ends at the left of the circle, the short way", path.Visibility == Visibility.Visible && Math.Abs(segment.Point.X - 1) < 1e-9 && Math.Abs(segment.Point.Y - 9) < 1e-9 && !segment.IsLargeArc);
        ring.Set(.75);
        check("Countdown ring: three quarters run, it ends at the right, the long way", Math.Abs(segment.Point.X - 17) < 1e-9 && Math.Abs(segment.Point.Y - 9) < 1e-9 && segment.IsLargeArc);
        check("Countdown ring: the arc moves in place (the same geometry and segment, nothing made again)", ReferenceEquals(path.Data, data) && ReferenceEquals(((PathGeometry)path.Data).Figures[0].Segments[0], segment));
        ring.Set(1);
        check("Countdown ring: a full countdown draws the whole circle", ring.Shown.Full && path.Visibility != Visibility.Visible && ring.Children.OfType<Ellipse>().Count(e => e.Visibility == Visibility.Visible) == 2);
        ring.Set(.5);
        check("Countdown ring: and back again", !ring.Shown.Full && path.Visibility == Visibility.Visible && Math.Abs(segment.Point.Y - 17) < 1e-9);
    }
}
