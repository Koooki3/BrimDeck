using BrimDeck.Core;

internal static class DisplayLayoutTests
{
    internal static void Run(Action<string, bool> check)
    {
        (string, bool)[] three = [("left", false), ("main", true), ("below", false)];
        var primary = DisplayLayout.Arrange(DisplayPlacement.Primary, ["left", "below"], three);
        check("Primary placement uses the primary monitor only, whatever is chosen", primary.Main == "main" && primary.Others.Count == 0);
        var one = DisplayLayout.Arrange(DisplayPlacement.Chosen, ["below"], three);
        check("One chosen monitor takes the panel", one.Main == "below" && one.Others.Count == 0);
        var two = DisplayLayout.Arrange(DisplayPlacement.Chosen, ["below", "left"], three);
        check("Two chosen monitors without the primary: the first in display order is the main one", two.Main == "left" && two.Others.SequenceEqual(["below"]));
        var withPrimary = DisplayLayout.Arrange(DisplayPlacement.Chosen, ["below", "main"], three);
        check("A chosen primary monitor holds the main panel", withPrimary.Main == "main" && withPrimary.Others.SequenceEqual(["below"]));
        var all = DisplayLayout.Arrange(DisplayPlacement.Chosen, ["left", "main", "below"], three);
        check("All three chosen gives one panel per monitor", all.Main == "main" && all.Others.SequenceEqual(["left", "below"]));
        var partly = DisplayLayout.Arrange(DisplayPlacement.Chosen, ["gone", "below"], three);
        check("A disconnected chosen monitor is skipped", partly.Main == "below" && partly.Others.Count == 0);
        var missing = DisplayLayout.Arrange(DisplayPlacement.Chosen, ["gone"], three);
        check("No chosen monitor connected falls back to the primary", missing.Main == "main" && missing.Others.Count == 0);
        check("Without a primary flag the first monitor is the main one", DisplayLayout.Arrange(DisplayPlacement.Primary, [], [("a", false), ("b", false)]).Main == "a");
        check("No monitors gives no placement", DisplayLayout.Arrange(DisplayPlacement.Chosen, ["a"], []).Main == "");

        var settings = new DeckSettings { Display = DisplayPlacement.Chosen, DisplayMonitors = [new("  ", "x")] };
        settings.Normalize();
        check("Choosing displays without naming one means the primary monitor", settings.Display == DisplayPlacement.Primary && settings.DisplayMonitors.Count == 0);
        settings = new DeckSettings { Display = (DisplayPlacement)9 };
        settings.Normalize();
        check("An unknown placement is reset", settings.Display == DisplayPlacement.Primary);
        settings = new DeckSettings { Display = DisplayPlacement.Chosen, DisplayMonitors = [new(@"\\?\DISPLAY#DEL1", "DELL G2724D"), new(@"\\?\DISPLAY#DEL1", "again"), new(@"\\?\DISPLAY#HKC2", "G27M2")] };
        settings.Normalize();
        check("Chosen monitors keep their names and lose duplicates",
            settings.Display == DisplayPlacement.Chosen && settings.DisplayMonitors.Count == 2 && settings.DisplayMonitors[0].Name == "DELL G2724D");
        var copy = settings.Copy(); copy.DisplayMonitors.Clear();
        check("A settings copy has its own list of monitors", settings.DisplayMonitors.Count == 2);
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "BrimDeck-Display-" + Guid.NewGuid().ToString("N")), "1.0.4");
        try
        {
            store.Save(settings);
            var loaded = store.Load();
            check("Chosen monitors survive saving and loading", loaded.Display == DisplayPlacement.Chosen && loaded.DisplayMonitors.SequenceEqual(settings.DisplayMonitors));
        }
        finally { Directory.Delete(store.DirectoryPath, true); }
    }
}
