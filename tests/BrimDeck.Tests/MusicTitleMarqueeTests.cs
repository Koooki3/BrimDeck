using BrimDeck.Core;

// The title on the open music page (W22): it rests when it fits the room beside the equalizer and scrolls, as the collapsed
// row's title does, when it does not. The room follows the bar count (3 to 20), the type size and the panel width.
internal static class MusicTitleMarqueeTests
{
    public static void Run(Action<string, bool> check)
    {
        // The bars: the old width at the default five, growing with the count and never below the bars' own spacing.
        var metrics = MusicMetrics.At(1);
        double five = MusicTitleFit.EqualizerWidth(metrics.Equalizer, 5);
        check("Music title: five bars keep the width the page always gave them", five == Math.Round(Math.Max(27, Math.Round(metrics.Equalizer * 1.7))));
        var widths = Enumerable.Range(CompactMusicLayout.MinimumBars, CompactMusicLayout.MaximumBars - CompactMusicLayout.MinimumBars + 1)
            .Select(bars => MusicTitleFit.EqualizerWidth(metrics.Equalizer, bars)).ToList();
        check("Music title: more bars are wider, from three to twenty", widths.Zip(widths.Skip(1), (a, b) => b > a).All(x => x) && widths[0] < five && widths[^1] > five);
        check("Music title: a bar count out of range counts as the nearest allowed one",
            MusicTitleFit.EqualizerWidth(metrics.Equalizer, 0) == widths[0] && MusicTitleFit.EqualizerWidth(metrics.Equalizer, 99) == widths[^1]);

        // The marquee measures its text against its room with Overflow (the grid decides the room): none when it fits, whole device
        // pixels when it does not, and nothing starts for rounding noise, at every scale.
        const double room = 258;
        foreach (double scale in new[] { 1, 1.25, 1.5, 2 })
        {
            check($"Music title: text as wide as its room does not overhang at {scale * 100:0}%", MusicTitleFit.Overflow(room, room, scale) == 0);
            check($"Music title: text a device pixel wider than its room overhangs at {scale * 100:0}%", MusicTitleFit.Overflow(room + 1 / scale, room, scale) > 0);
            check($"Music title: rounding noise does not start a one-pixel scroll at {scale * 100:0}%", MusicTitleFit.Overflow(room + 1e-4, room, scale) == 0);
            double over = MusicTitleFit.Overflow(room + 10.3, room, scale);
            check($"Music title: the overhang is whole device pixels at {scale * 100:0}%", over >= 10.3 && Math.Abs(over * scale - Math.Round(over * scale)) < 1e-9);
        }
        check("Music title: no text, no room or no scale never overhangs and never fails",
            MusicTitleFit.Overflow(0, 100, 1) == 0 && MusicTitleFit.Overflow(50, 0, 0) == 0 && MusicTitleFit.Overflow(double.NaN, 10, 1) == 0 && MusicTitleFit.Overflow(10, double.NaN, 1) == 0);
        // The room the grid leaves beside the equalizer shrinks as the bars grow, so the same title goes from fitting to overhanging.
        const double line = 320;
        var overhangs = Enumerable.Range(3, 18).Select(bars => MusicTitleFit.Overflow(250, line - MusicTitleFit.EqualizerWidth(metrics.Equalizer, bars) - MusicTitleFit.EqualizerGap, 1.5) > 0).ToList();
        check("Music title: a title that fits few bars overhangs with more", !overhangs[0] && overhangs[^1] && overhangs.Zip(overhangs.Skip(1), (a, b) => !a || b).All(x => x));

        // Centred without the cover, the title leaves the equalizer's width and gap free at both ends of the line, so its room is
        // centred on the page and ends where the equalizer's gap begins, at every bar count.
        foreach (int bars in new[] { 3, 5, 20 })
        {
            double equalizer = MusicTitleFit.EqualizerWidth(metrics.Equalizer, bars), inset = MusicTitleFit.CenteredInset(equalizer), centredRoom = MusicTitleFit.CenteredRoom(line, equalizer);
            check($"Music title: centred, the room ends a gap before the {bars} bars", inset + centredRoom == line - equalizer - MusicTitleFit.EqualizerGap);
            check($"Music title: centred, the room is as far from the left as from the right with {bars} bars", inset == line - (inset + centredRoom));
        }
        check("Music title: centred, a line narrower than the two insets leaves no room, never a negative one", MusicTitleFit.CenteredRoom(40, 117) == 0);
        // The narrowest page with the longest equalizer still shows a stretch of the title, at every page size.
        double least = double.MaxValue;
        for (double width = DeckPages.MinWidth; width <= DeckPages.MaxWidth; width += 20)
            for (double height = DeckPages.MinHeight; height <= DeckPages.MaxHeight; height += 20)
            {
                var page = MusicLayout.Calculate(width, height, lyrics: true, timeline: true);
                double titleRoom = MusicTitleFit.CenteredRoom(page.BodyWidth, MusicTitleFit.EqualizerWidth(page.Metrics.Equalizer, CompactMusicLayout.MaximumBars));
                least = Math.Min(least, titleRoom / page.Metrics.Title);
            }
        check($"Music title: centred, twenty bars leave room for five characters of the title at every page size (least {least:0.0})", least >= 5);

        // A screen reader reads the whole title with its name, and the artist when the short page merges it into the line.
        check("Music title: the reading has the title's name and the whole title", MusicTitleFit.ReadAs("【IGN】《影之刃零》官方实机演示预告片") is { } read && read.EndsWith("【IGN】《影之刃零》官方实机演示预告片") && (read.StartsWith("歌名 ", StringComparison.Ordinal) || read.StartsWith("Title ", StringComparison.Ordinal)));
        check("Music title: a merged line reads with its artist", MusicTitleFit.ReadAs("夜曲", " · IGN China").EndsWith("夜曲 · IGN China"));
    }
}
