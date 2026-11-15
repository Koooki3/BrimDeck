using System.Text.Json;
using BrimDeck.Core;

// Following Windows' "Text size" (TextScales, DeckSettings.FollowTextSize): the reading is clamped and rounded, it combines with
// the panel scale within a ceiling, a source that fails or misreads leaves the panel as it was, and the setting survives a file
// saved before it existed. The Windows reading (WindowsTextSize) and the panel redrawn by it are not covered here.
internal static class TextScaleTests
{
    private sealed class Source(Func<double> read) : ITextSizeSource
    {
        public double Read() => read();
    }

    public static void Run(Action<string, bool> check)
    {
        (double In, double Out)[] normalize =
        [
            (1, 1), (1.0001, 1), (1.02, 1), (1.03, 1.05), (1.1, 1.1), (1.25, 1.25), (1.5, 1.5), (1.74, 1.75), (2, 2), (2.25, 2.25),
            (2.3, 2.25), (3, 2.25), (100, 2.25), (double.MaxValue, 2.25), (double.PositiveInfinity, 1), (double.NegativeInfinity, 1),
            (double.NaN, 1), (0, 1), (-1, 1), (-1.5, 1), (0.5, 1), (0.99, 1), (double.Epsilon, 1)
        ];
        check($"Text size: {normalize.Length} readings normalize to 1 to {TextScales.Max} in steps of {TextScales.Step}",
            normalize.All(c => TextScales.Normalize(c.In) == c.Out));
        check("Text size: the percentages Windows offers (100 to 225) map to their factors",
            TextScales.FromPercent(100) == 1 && TextScales.FromPercent(150) == 1.5 && TextScales.FromPercent(225) == 2.25 && TextScales.FromPercent(300) == 2.25
            && TextScales.FromPercent(double.NaN) == 1 && TextScales.FromPercent(0) == 1 && TextScales.FromPercent(-100) == 1);

        (double Panel, double Text, double Out)[] combine =
        [
            (1, 1, 1), (1, 1.5, 1.5), (1.5, 1, 1.5), (1.5, 1.25, 1.875), (1.5, 1.5, 2), (1.5, 2.25, 2), (1, 2.25, 2), (1, 2, 2),
            (1, double.NaN, 1), (1, -3, 1), (1, 0, 1), (1.5, double.PositiveInfinity, 1.5), (double.NaN, 1.5, 1.5), (0, 1.25, 1.25), (-1, 1.25, 1.25)
        ];
        check($"Text size: {combine.Length} panel scale and text size pairs combine within 1 and {TextScales.MaxCombined}",
            combine.All(c => TextScales.Combine(c.Panel, c.Text) == c.Out));

        var settings = new DeckSettings();
        check("Text size: followed by default, and a plain panel scale is unchanged when the text size is 100 %",
            settings.FollowTextSize && PanelScales.Factor(settings, 1) == 1 && PanelScales.Factor(settings) == 1
            && PanelScales.Factor(new DeckSettings { PanelScale = 150 }, 1) == 1.5);
        check("Text size: the panel is drawn larger by it while it is followed, with the scale's own factor, and not when it is off",
            PanelScales.Factor(settings, 1.5) == 1.5 && PanelScales.Factor(new DeckSettings { PanelScale = 150 }, 1.25) == 1.875
            && PanelScales.Factor(new DeckSettings { FollowTextSize = false }, 1.5) == 1
            && PanelScales.Factor(new DeckSettings { FollowTextSize = false, PanelScale = 150 }, 2.25) == 1.5);

        var old = JsonSerializer.Deserialize<DeckSettings>("{\"PanelScale\":150}")!; old.Normalize();
        check("Text size: a file saved before the setting existed follows it, and keeps the rest", old.FollowTextSize && old.PanelScale == 150);
        var off = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { FollowTextSize = false }))!; off.Normalize();
        var on = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings()))!; on.Normalize();
        check("Text size: the choice survives a save and a load, and a copy", !off.FollowTextSize && on.FollowTextSize && !off.Copy().FollowTextSize && on.Copy().FollowTextSize);
        check("Text size: every profile shares it, as Windows' text size is one for the machine", Profiles.GlobalKeys.Contains(nameof(DeckSettings.FollowTextSize)));

        double value = 1;
        var source = new Source(() => value);
        var size = new TextSize(source);
        check("Text size: the reading at start is the one in effect", size.Factor == 1);
        check("Text size: refreshing with nothing changed says so", !size.Refresh() && size.Factor == 1);
        value = 1.5;
        check("Text size: a change is reported once", size.Refresh() && size.Factor == 1.5 && !size.Refresh());
        value = 1.51;
        check("Text size: a change inside a step is no change", !size.Refresh() && size.Factor == 1.5);
        value = double.NaN;
        check("Text size: an unreadable value is 100 %", size.Refresh() && size.Factor == 1);
        var failing = new TextSize(new Source(() => throw new InvalidOperationException("no UISettings")));
        check("Text size: a source that fails is 100 % and stays so", failing.Factor == 1 && !failing.Refresh());
        var starting = new TextSize(new Source(() => 1.75));
        check("Text size: a larger size already set when BrimDeck starts is in effect at once", starting.Factor == 1.75);
    }
}
