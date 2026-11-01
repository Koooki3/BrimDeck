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

        // A screen reader reads the whole title with its name, and the artist when the short page merges it into the line.
        check("Music title: the reading has the title's name and the whole title", MusicTitleFit.ReadAs("【IGN】《影之刃零》官方实机演示预告片") is { } read && read.EndsWith("【IGN】《影之刃零》官方实机演示预告片") && (read.StartsWith("歌名 ", StringComparison.Ordinal) || read.StartsWith("Title ", StringComparison.Ordinal)));
        check("Music title: a merged line reads with its artist", MusicTitleFit.ReadAs("夜曲", " · IGN China").EndsWith("夜曲 · IGN China"));

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
        // Screen readers: the marquee's peer reads its own reading (DrawnPeer used the bare text, so the name and the artist were lost).
        check("Music title: the marquee's peer reads the title's name and merged artist, and the page gives it", Reads(visuals, page));
        check("Music title: the finder rejects a peer that reads only the bare text", !Reads(visuals.Replace("AutomationControlType.Text, () => Reading)", "AutomationControlType.Text, () => _text)"), page));
        check("Music title: the finder rejects a title built without the reading", !Reads(visuals, page.Replace("AccessibleName = MusicTitleFit.ReadAs(track.Title, tail), ", "")));
        // The merged artist keeps one color whether the line fits or scrolls: its own, faded at the ends, never the text's at a lower opacity.
        check("Music title: the artist fades in its own color on the shared clocks", Colors(visuals));
        check("Music title: the finder rejects the artist's old 70 % copy of the text's gradient", !Colors(visuals.Replace("_tailLeft.ApplyAnimationClock(GradientStop.OffsetProperty, (AnimationClock)_glide.Children[1]);", "")
            .Replace("var tailColor = (TailForeground as SolidColorBrush)?.Color ?? ink;", "var tailColor = ink;")) && !Colors(visuals + "Opacity = TailInkOpacity"));
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
            && Regex.IsMatch(page, @"bool open = Settings\.Animations && SystemParameters\.ClientAreaAnimation")
            && Regex.IsMatch(page, @"_musicTitle\?\.SetScrolling\(open\)") && !Regex.IsMatch(page, @"_musicTitle\?\.SetScrolling\(open && MusicAudioActive\)")
            && Regex.IsMatch(page, @"equalizerWidth \+ MusicTitleFit\.EqualizerGap");
    }

    private static bool Reads(string visuals, string page)
    {
        var title = Regex.Match(page, @"private MusicMarquee PageTitle\(.*?return title;", RegexOptions.Singleline);
        return Regex.IsMatch(visuals, @"OnCreateAutomationPeer\(\) => new DrawnPeer\(this, AutomationControlType\.Text, \(\) => Reading\)")
            && Regex.IsMatch(visuals, @"Reading => AccessibleName\.Length > 0 \? AccessibleName : _text \+ Tail")
            && title.Success && Regex.IsMatch(title.Value, @"AccessibleName = MusicTitleFit\.ReadAs\(track\.Title, tail\)");
    }

    private static bool Colors(string visuals) =>
        Regex.IsMatch(visuals, @"var tailColor = \(TailForeground as SolidColorBrush\)\?\.Color \?\? ink;")
        && Regex.IsMatch(visuals, @"_tailLeft\.ApplyAnimationClock\(GradientStop\.OffsetProperty, \(AnimationClock\)_glide\.Children\[1\]\)")
        && !visuals.Contains("TailInkOpacity");
}
