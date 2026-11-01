using System.Text.RegularExpressions;
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

        // The room: the line less the bars and the gap, so it shrinks as the bars grow and never goes below nothing.
        const double line = 320;
        var rooms = Enumerable.Range(3, 18).Select(bars => MusicTitleFit.HeaderRoom(line, MusicTitleFit.EqualizerWidth(metrics.Equalizer, bars))).ToList();
        check("Music title: the room is the line less the bars and the gap", rooms[2] == line - five - MusicTitleFit.EqualizerGap);
        check("Music title: the room shrinks as the bars grow", rooms.Zip(rooms.Skip(1), (a, b) => b < a).All(x => x));
        check("Music title: a line narrower than its bars leaves no room, not a negative one", MusicTitleFit.HeaderRoom(10, 60) == 0);

        // A title scrolls exactly when it overhangs its room, for every bar count and every scale.
        foreach (double scale in new[] { 1, 1.25, 1.5, 2 })
        {
            double room = MusicTitleFit.HeaderRoom(line, five);
            check($"Music title: text as wide as its room rests at {scale * 100:0}%", !MusicTitleFit.Scrolls(room, room, scale, true));
            check($"Music title: text a device pixel wider than its room scrolls at {scale * 100:0}%", MusicTitleFit.Scrolls(room + 1 / scale, room, scale, true));
            check($"Music title: rounding noise does not start a one-pixel scroll at {scale * 100:0}%", !MusicTitleFit.Scrolls(room + 1e-4, room, scale, true));
            double over = MusicTitleFit.Overflow(room + 10.3, room, scale);
            check($"Music title: the overhang is whole device pixels at {scale * 100:0}%", over >= 10.3 && Math.Abs(over * scale - Math.Round(over * scale)) < 1e-9);
        }
        check("Music title: with animation off the title rests, cut at its edge, whatever its length", !MusicTitleFit.Scrolls(900, 100, 1.5, false));
        check("Music title: no text, no room or no scale never scrolls and never fails",
            !MusicTitleFit.Scrolls(0, 100, 1, true) && MusicTitleFit.Overflow(50, 0, 0) == 0 && MusicTitleFit.Overflow(double.NaN, 10, 1) == 0 && MusicTitleFit.Overflow(10, double.NaN, 1) == 0);

        // Short title rests and long title scrolls at every bar count; a title in between starts scrolling at some count and then
        // keeps scrolling for every longer bar run, at both panel scales.
        foreach (double scale in new[] { 1, 1.5 })
        {
            bool Scrolls(double text, int bars) => MusicTitleFit.Scrolls(text, MusicTitleFit.HeaderRoom(line, MusicTitleFit.EqualizerWidth(metrics.Equalizer, bars)), scale, true);
            var counts = Enumerable.Range(3, 18).ToList();
            check($"Music title: a short title rests at every bar count at {scale * 100:0}%", counts.All(bars => !Scrolls(80, bars)));
            check($"Music title: a long title scrolls at every bar count at {scale * 100:0}%", counts.All(bars => Scrolls(900, bars)));
            double between = MusicTitleFit.HeaderRoom(line, MusicTitleFit.EqualizerWidth(metrics.Equalizer, 10)) + 1;
            var states = counts.Select(bars => Scrolls(between, bars)).ToList();
            check($"Music title: a title between fits few bars and scrolls with more at {scale * 100:0}%",
                !states[0] && states[^1] && states.Zip(states.Skip(1), (a, b) => !a || b).All(x => x));
        }

        // The page lays its title through the one marquee, and the marquee measures its overhang with the same rule.
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "BrimDeck.sln"))) source = source.Parent;
        check("Music title: the source tree is found", source is not null);
        if (source is null) return;
        string page = File.ReadAllText(Path.Combine(source.FullName, "src", "BrimDeck", "MainWindow.Music.cs"));
        string visuals = File.ReadAllText(Path.Combine(source.FullName, "src", "BrimDeck", "MusicVisuals.cs"));
        check("Music title: the open page builds its title as a MusicMarquee, in both layouts, and scrolls it whenever the page is open (not only while a song plays)", Uses(page));
        // Put the old layout back: a plain ellipsis TextBlock for the title, beside the equalizer or in the column.
        string oldHeader = page.Replace("PageTitle(track, c, center: false, merge: layout.MergeTitle)", "UI.Fixed(track.Title, c.Title, c.TitleLine, UI.Primary, FontWeights.SemiBold)");
        string oldColumn = page.Replace("PageTitle(track, c, center: true, merge: false)", "UI.Fixed(track.Title, c.Title, c.TitleLine, UI.Primary, FontWeights.SemiBold)");
        check("Music title: the finder rejects the old ellipsis title beside the equalizer", !Uses(oldHeader));
        check("Music title: the finder rejects the old ellipsis title in the column layout", !Uses(oldColumn));
        check("Music title: the finder rejects a title that is never told when to scroll", !Uses(page.Replace("_musicTitle?.SetScrolling(open)", "")));
        check("Music title: the finder rejects a title that scrolls only while a song plays", !Uses(page.Replace("_musicTitle?.SetScrolling(open)", "_musicTitle?.SetScrolling(open && MusicAudioActive)")));
        check("Music title: the finder rejects a title with no fade over the page's own surface",
            !Uses(Regex.Replace(page, @"(private MusicMarquee PageTitle\(.*?)Backdrop = PanelTheme\.MarqueeBackdrop, InkFade = PanelTheme\.Current\.HasOverlay", "$1", RegexOptions.Singleline)));
        check("Music title: the marquee takes its overhang from MusicTitleFit", Regex.IsMatch(visuals, @"double overflow = MusicTitleFit\.Overflow\(text\.Width, size\.Width, scale\);"));
        check("Music title: the finder rejects the marquee's old inline rule", !Regex.IsMatch(
            visuals.Replace("MusicTitleFit.Overflow(text.Width, size.Width, scale)", "Math.Max(0, Math.Ceiling((text.Width - size.Width) * scale - .01) / scale)"),
            @"double overflow = MusicTitleFit\.Overflow\("));
    }

    private static bool Uses(string page)
    {
        var title = Regex.Match(page, @"private MusicMarquee PageTitle\(.*?return title;", RegexOptions.Singleline);
        return title.Success
            && Regex.IsMatch(page, @"PageTitle\(track, c, center: false, merge: layout\.MergeTitle\)")
            && Regex.IsMatch(page, @"PageTitle\(track, c, center: true, merge: false\)")
            && !Regex.IsMatch(page, @"UI\.Fixed\(track\.Title")
            && Regex.IsMatch(title.Value, @"new MusicMarquee \{")
            && Regex.IsMatch(title.Value, @"Backdrop = PanelTheme\.MarqueeBackdrop, InkFade = PanelTheme\.Current\.HasOverlay")
            && Regex.IsMatch(title.Value, @"AutomationProperties\.SetName\(title, Loc\.T\(""歌名 "", ""Title ""\) \+ track\.Title\)")
            && Regex.IsMatch(page, @"_musicTitle\?\.SetScrolling\(open\)") && !Regex.IsMatch(page, @"_musicTitle\?\.SetScrolling\(open && MusicAudioActive\)")
            && Regex.IsMatch(page, @"equalizerWidth \+ MusicTitleFit\.EqualizerGap");
    }
}
