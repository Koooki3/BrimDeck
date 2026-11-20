using System.Text.Json;
using BrimDeck.Core;

// The indicator that opens into the capsule or the notch (DeckSettings.LineOpens): its setting, its state and where it sits.
internal static class IndicatorCapsuleTests
{
    internal static void Run(Action<string, bool> check)
    {
        // The setting: the panel by default and in older settings, saved and read back; an unknown choice opens the panel.
        check("The indicator opens the panel by default", new DeckSettings().LineOpens == IndicatorOpens.Panel);
        check("Older settings without the choice open the panel", JsonSerializer.Deserialize<DeckSettings>("""{"Style":2}""")!.LineOpens == IndicatorOpens.Panel);
        var saved = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { LineOpens = IndicatorOpens.Notch }))!;
        check("The choice survives a save", saved.LineOpens == IndicatorOpens.Notch);
        check("A copy keeps the choice", new DeckSettings { LineOpens = IndicatorOpens.Capsule }.Copy().LineOpens == IndicatorOpens.Capsule);
        var unknown = new DeckSettings { LineOpens = (IndicatorOpens)9 };
        unknown.Normalize();
        check("An unknown choice opens the panel", unknown.LineOpens == IndicatorOpens.Panel);

        // The content of the style the indicator opens into is read only while an indicator can open into it.
        var notch = new DeckSettings { Style = CompactStyle.Notch, NotchSummary = false, CapsuleSummary = true, UsagePage = false, Maximized = WindowBehavior.Normal,
            Borderless = WindowBehavior.Normal, Exclusive = WindowBehavior.Normal, Occupied = WindowBehavior.Normal, LineOpens = IndicatorOpens.Capsule };
        check("No indicator can show: nothing is read for the capsule", !notch.IndicatorMayShow && notch.IslandFromIndicator is null && !notch.QuotaWanted);
        var rule = notch.Copy(); rule.Maximized = WindowBehavior.Line;
        check("A global rule that shows the indicator reads the capsule's rings", rule.IndicatorMayShow && rule.IslandFromIndicator == CompactStyle.Capsule && rule.QuotaWanted);
        var appRule = notch.Copy(); appRule.FullscreenRules = [new FullscreenRule("Game") { Exclusive = WindowBehavior.Line }];
        check("An app's rule that shows the indicator counts too", appRule.IndicatorMayShow && appRule.QuotaWanted);
        var line = notch.Copy(); line.Style = CompactStyle.Line;
        check("The indicator chosen as the style reads the capsule's rings", line.QuotaWanted);
        var toNotch = line.Copy(); toNotch.LineOpens = IndicatorOpens.Notch;
        check("An indicator that opens into the notch reads the notch's rings, not the capsule's", toNotch.IslandFromIndicator == CompactStyle.Notch && !toNotch.QuotaWanted);
        toNotch.NotchSummary = true;
        check("The notch's rings are read for it", toNotch.QuotaWanted);
        var off = line.Copy(); off.LineOpens = IndicatorOpens.Panel;
        check("An indicator that opens the panel reads nothing for the capsule", off.IslandFromIndicator is null && !off.QuotaWanted);
        var music = line.Copy(); music.MusicPage = false; music.MusicIndicatorProgress = false; music.CapsuleMusic = true;
        check("The capsule's music is detected for an indicator that opens into it", music.MediaWanted);
        music.LineOpens = IndicatorOpens.Panel;
        check("Without the choice the indicator alone asks for no music", !music.MediaWanted);

        // The state: begins once, ends once, and rests until the pointer has left the line.
        var peek = new IndicatorPeek();
        check("A line at rest opens into the capsule", !peek.Active && peek.Begin() && peek.Active);
        check("It does not open twice", !peek.Begin());
        check("It ends once and the line then rests", peek.End() && !peek.Active && peek.Resting && !peek.End());
        peek.Observe(true);
        check("A resting line does not open again while the pointer is on it", peek.Resting && !peek.Begin());
        peek.Observe(false);
        check("A resting line wakes when the pointer has left it, and opens again", !peek.Resting && peek.Begin());
        peek.Observe(false);
        check("Observing the pointer does nothing to the capsule on show", peek.Active && !peek.Resting);
        peek.Reset();
        check("Reset clears the state", !peek.Active && !peek.Resting && peek.Begin());

        // Where the capsule sits: against the line's edge, centred on the line, within the work area.
        var work = new DipRect(0, 0, 1920, 1032);
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        static bool Inside(DipRect r, DipRect area) => r.X >= area.X - 1e-6 && r.Y >= area.Y - 1e-6 && r.Right <= area.Right + 1e-6 && r.Bottom <= area.Bottom + 1e-6;
        var top = IndicatorCapsule.Place(new DipRect(860, 0, 200, 4), ScreenEdge.Top, 240, 32, 5, work);
        check("A top line: centred on the line, at the capsule's distance from the top", top == new DipRect(840, 5, 240, 32));
        var bottom = IndicatorCapsule.Place(new DipRect(860, 1028, 200, 4), ScreenEdge.Bottom, 240, 32, 5, work);
        check("A bottom line: at the same distance from the work area's bottom", bottom == new DipRect(840, 1032 - 5 - 32, 240, 32));
        var left = IndicatorCapsule.Place(new DipRect(0, 400, 4, 200), ScreenEdge.Left, 240, 32, 5, work);
        check("A left line: by the left side, centred on the line down the edge", left == new DipRect(5, 484, 240, 32));
        var right = IndicatorCapsule.Place(new DipRect(1916, 400, 4, 200), ScreenEdge.Right, 240, 32, 5, work);
        check("A right line: by the right side", right == new DipRect(1920 - 5 - 240, 484, 240, 32));
        var corner = IndicatorCapsule.Place(new DipRect(0, 0, 60, 4), ScreenEdge.Top, 240, 32, 5, work);
        check("A line in a corner keeps the capsule within the work area", Near(corner.X, 0) && Inside(corner, work));
        var low = IndicatorCapsule.Place(new DipRect(0, 1000, 4, 32), ScreenEdge.Left, 240, 32, 5, work);
        check("A line low on a side keeps the capsule above the work area's bottom", Near(low.Bottom, 1032) && Inside(low, work));
        var taskbarTop = new DipRect(0, 48, 1920, 1032);
        var overTaskbar = IndicatorCapsule.Place(new DipRect(860, 0, 200, 4), ScreenEdge.Top, 240, 32, 5, taskbarTop);
        check("A line over a top taskbar opens the capsule below the taskbar", Near(overTaskbar.Y, 53));
        check("The distance is held within the bridge to the edge",
            Near(IndicatorCapsule.Place(new DipRect(860, 0, 200, 4), ScreenEdge.Top, 240, 32, 4000, work).Y, CollapsedHover.BridgeMaxGap)
            && Near(IndicatorCapsule.Place(new DipRect(860, 0, 200, 4), ScreenEdge.Top, 240, 32, -3, work).Y, 0)
            && Near(IndicatorCapsule.Place(new DipRect(860, 0, 200, 4), ScreenEdge.Top, 240, 32, double.NaN, work).Y, 0));
        var huge = IndicatorCapsule.Place(new DipRect(860, 0, 200, 4), ScreenEdge.Top, 5000, 2000, 5, work);
        check("A capsule larger than the work area is cut to it", huge == work);
        check("No work area places nothing", IndicatorCapsule.Place(new DipRect(0, 0, 10, 4), ScreenEdge.Top, 240, 32, 5, new DipRect(0, 0, 0, 0)).Width == 0);
        check("A line with no place centres the capsule", IndicatorCapsule.Place(new DipRect(double.NaN, double.NaN, 0, 0), ScreenEdge.Top, 240, 32, 5, work).X == 840);

        // Along the edge: centred on the line, kept within the span (the notch along the line's edge).
        check("A surface is centred on the line along the edge", IndicatorCapsule.Along(960, 200, 1920) == 860);
        check("A surface near an end stays within the span", IndicatorCapsule.Along(20, 200, 1920) == 0 && IndicatorCapsule.Along(1910, 200, 1920) == 1720);
        check("A surface longer than the span starts at its beginning", IndicatorCapsule.Along(960, 3000, 1920) == 0);
        check("A centre that is not a number centres the surface", IndicatorCapsule.Along(double.NaN, 200, 1920) == 860);

        // The area that keeps the capsule: the capsule, the line and the room between them.
        var holds = IndicatorCapsule.Holds(new DipRect(0, 400, 4, 200), left);
        check("The area reaches from the line to the capsule", holds == new DipRect(0, 400, 245, 200));
        check("Without a line the capsule alone keeps itself", IndicatorCapsule.Holds(new DipRect(0, 0, 0, 0), top) == top);
        // The line on the right edge, 4 DIPs from it: the pointer pushed against the screen's edge beside the line keeps the capsule.
        var rightFrame = EdgeFrame.For(ScreenEdge.Right, 1920, 1080, work, true);
        var reach = IndicatorCapsule.LineReach(rightFrame, new DipRect(440, 4, 200, 4));
        var rightHolds = IndicatorCapsule.Holds(reach, IndicatorCapsule.Place(rightFrame.ToScreen(new DipRect(440, 4, 200, 4)), ScreenEdge.Right, 240, 32, 5, work));
        check("The line's reach runs to the screen's edge", Near(reach.Right, 1920) && Near(reach.X, 1910));
        check("The area keeps the strip between the line and the screen's edge", Near(rightHolds.Right, 1920) && rightHolds.Y <= 440 && rightHolds.Bottom >= 640);
    }
}
