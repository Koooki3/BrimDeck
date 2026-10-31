using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using BrimDeck;

// How the drawn parts read to UI Automation (DrawnPeer) and the size of a stacked footer row's pointer area (HitArea.Stacked). The peers
// are the real ones WPF creates for an element; nothing is shown and no client is attached.
internal static class DrawnAutomationTests
{
    // An element that lists its parts from a list the test replaces, as the monitor's list and graph do from a reading.
    private sealed class PartsHost : FrameworkElement
    {
        public List<DrawnPart> Rows { get; set; } = [];
        protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.List, () => "list", parts: () => Rows);
    }

    private sealed class ShareHost(double? share) : FrameworkElement
    {
        public double? Share { get; set; } = share;
        protected override AutomationPeer OnCreateAutomationPeer() => new DrawnPeer(this, AutomationControlType.ProgressBar, () => "gauge", share: () => Share);
    }

    private static DrawnPart Row(string key, string name) => new(key, name, new Rect(0, 0, 10, 10));

    internal static void Run(Action<string, bool> check)
    {
        // A share with no reading offers no range value, so a client never gets a zero where there is none; a real zero is still a value.
        var gauge = new ShareHost(null);
        var peer = (AutomationPeer)UIElementAutomationPeer.CreatePeerForElement(gauge);
        check("A gauge without a reading offers no range value", peer.GetPattern(PatternInterface.RangeValue) is null);
        gauge.Share = 0;
        check("A gauge reading zero offers the value zero", peer.GetPattern(PatternInterface.RangeValue) is IRangeValueProvider zero && zero.Value == 0);
        gauge.Share = .473;
        check("A gauge reading 47% offers the value 47", peer.GetPattern(PatternInterface.RangeValue) is IRangeValueProvider part && part.Value == 47 && part.IsReadOnly);
        gauge.Share = null;
        check("A gauge that loses its reading stops offering the value", peer.GetPattern(PatternInterface.RangeValue) is null);

        // A peer's list is kept until it is told the parts changed; Refresh brings it and the parts a client holds up to date.
        var host = new PartsHost { Rows = [Row("a", "first"), Row("b", "second")] };
        check("A peer nobody asked for does not exist, and refreshing it does nothing", UIElementAutomationPeer.FromElement(host) is null && Refreshes(host));
        var list = (AutomationPeer)UIElementAutomationPeer.CreatePeerForElement(host);
        var first = list.GetChildren();
        check("The list's parts are listed once asked for", first.Count == 2 && first[0].GetName() == "first" && first[1].GetName() == "second");

        host.Rows = [Row("a", "first, changed"), Row("c", "third")];
        check("Without a refresh the list keeps what it listed", list.GetChildren().Count == 2 && list.GetChildren()[1].GetName() == "second");
        DrawnPeer.Refresh(host);
        check("A part a client holds reads its new name after a refresh", first[0].GetName() == "first, changed");
        var again = list.GetChildren();
        check("After a refresh the list is listed again: a part that went is gone and a new one is there",
            again.Count == 2 && again[0].GetName() == "first, changed" && again[1].GetName() == "third");
        check("A part that stays keeps its peer", ReferenceEquals(again[0], first[0]));

        host.Rows = [];
        DrawnPeer.Refresh(host);
        check("A list that empties lists nothing after a refresh", list.GetChildren().Count == 0);

        // The footer's two stacked rows, each given HitArea.Minimum (24 DIP) tall for the pointer. Today's can grow up only over the room
        // above it. Dense tier: the row is 17 DIP, 5 below the hairline, 1 for it and 6 over it.
        var (up, down) = HitArea.Stacked(17, 5 + 1 + 6);
        check("Stacked rows each reach 24 DIP in the dense tier, today's growing up over the hairline's gap too", 17 + up >= HitArea.Minimum && 17 + down >= HitArea.Minimum);
        check("Limiting today's growth to the gap under the hairline alone (5 DIP) left it at 22, short of 24", 17 + Math.Min(7, 5) == 22);
        foreach (var (row, room) in new[] { (18.0, 7 + 1 + 8), (21.0, 7 + 1 + 8), (22.0, 8 + 1 + 10), (17.0, 3 + 1 + 4) })
        {
            var (u, d) = HitArea.Stacked(row, room);
            check($"Stacked rows of {row} DIP with {room} DIP above: the lower reaches 24, the upper does where the room allows",
                row + d >= HitArea.Minimum && row + u >= HitArea.Minimum);
        }
        check("A row already 24 DIP tall grows nowhere", HitArea.Stacked(24, 10) == (0, 0) && HitArea.Stacked(30, 10) == (0, 0));
        check("With no room above, today does not grow up and the week still reaches 24", HitArea.Stacked(17, 0) == (0, 7) && HitArea.Stacked(17, -3) == (0, 7));
    }

    private static bool Refreshes(UIElement element) { DrawnPeer.Refresh(element); return UIElementAutomationPeer.FromElement(element) is null; }
}
