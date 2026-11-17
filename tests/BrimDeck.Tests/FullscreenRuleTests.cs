using System.Text.Json;
using BrimDeck.Core;

internal static class FullscreenRuleTests
{
    internal static void Run(Action<string, bool> check)
    {
        // Matching, unchanged from the exceptions it grew out of.
        var byName = new FullscreenRule("Sahhpire");
        check("A rule matches its process name regardless of case",
            ScreenPolicy.Matches(byName, "sahhpire", "") && ScreenPolicy.Matches(byName, "SAHHPIRE", null));
        check("A rule ignores the .exe suffix on either side",
            ScreenPolicy.Matches(byName, "Sahhpire.exe", "") && ScreenPolicy.Matches(new FullscreenRule("Sahhpire.exe"), "Sahhpire", ""));
        check("Another app does not match a rule", !ScreenPolicy.Matches(byName, "Sahhpire2", ""));
        var byPath = new FullscreenRule("Other", @"F:\Apps\Sahhpire\Sahhpire.exe");
        check("A rule matches its executable path whichever slash direction is reported",
            ScreenPolicy.Matches(byPath, "unrelated", @"f:\apps\sahhpire\SAHHPIRE.EXE") && ScreenPolicy.Matches(byPath, "unrelated", "F:/Apps/Sahhpire/Sahhpire.exe"));
        check("A rule still matches by name when the executable path cannot be read",
            ScreenPolicy.Matches(byPath, "Other", "") && ScreenPolicy.Matches(byPath, "Other", null));
        check("A rule does not match a different executable", !ScreenPolicy.Matches(byPath, "unrelated", @"F:\Other\Sahhpire.exe"));

        // The behavior a context calls for: the app's own rule first, the global rules everywhere else.
        var global = new DeckSettings();  // Maximized Line, Borderless Hide, Exclusive Hide
        check("Without a rule the global rules answer",
            global.Behavior(ScreenContext.Maximized) == WindowBehavior.Line && global.Behavior(ScreenContext.Borderless) == WindowBehavior.Hide
            && global.Behavior(ScreenContext.Desktop) == WindowBehavior.Normal && global.Behavior(ScreenContext.Maximized, "Any", "") == WindowBehavior.Line);
        var exempt = new FullscreenRule("Sahhpire") { Maximized = WindowBehavior.Normal, Borderless = WindowBehavior.Normal, Exclusive = WindowBehavior.Normal };
        var blanket = new DeckSettings { FullscreenRules = [exempt] };
        check("A rule that keeps the style everywhere exempts the app from all three",
            blanket.Behavior(ScreenContext.Maximized, "sahhpire", "") == WindowBehavior.Normal
            && blanket.Behavior(ScreenContext.Borderless, "Sahhpire", @"F:\x\Sahhpire.exe") == WindowBehavior.Normal
            && blanket.Behavior(ScreenContext.Exclusive, "sahhpire") == WindowBehavior.Normal
            && blanket.Behavior(ScreenContext.Borderless, "Other", "") == WindowBehavior.Hide);
        var mixed = new DeckSettings { FullscreenRules = [new FullscreenRule("Sahhpire") { Maximized = WindowBehavior.Hide, Exclusive = WindowBehavior.Line }] };
        check("A context left on Follow answers with the global rules",
            mixed.Behavior(ScreenContext.Maximized, "Sahhpire") == WindowBehavior.Hide
            && mixed.Behavior(ScreenContext.Borderless, "Sahhpire") == WindowBehavior.Hide   // follows the global rule
            && mixed.Behavior(ScreenContext.Exclusive, "Sahhpire") == WindowBehavior.Line);
        check("A desktop context has no rule and no override",
            mixed.Behavior(ScreenContext.Desktop, "Sahhpire") == WindowBehavior.Normal);
        // A panel that is an ordinary window follows the same rules: the global ones, and an app's own first.
        var ordinary = new DeckSettings
        {
            AlwaysOnTop = false, Maximized = WindowBehavior.Normal, Borderless = WindowBehavior.Line, Exclusive = WindowBehavior.Hide,
            FullscreenRules = [new FullscreenRule("Sahhpire") { Maximized = WindowBehavior.Line }]
        };
        check("A panel that is not kept above other windows follows the global window rules",
            ordinary.Behavior(ScreenContext.Maximized) == WindowBehavior.Normal && ordinary.Behavior(ScreenContext.Borderless) == WindowBehavior.Line
            && ordinary.Behavior(ScreenContext.Exclusive) == WindowBehavior.Hide && ordinary.Behavior(ScreenContext.Desktop) == WindowBehavior.Normal);
        check("A panel that is not kept above other windows follows an app's own rule",
            ordinary.Behavior(ScreenContext.Maximized, "Sahhpire") == WindowBehavior.Line
            && ordinary.Behavior(ScreenContext.BackgroundFullscreen, "Sahhpire", "", ScreenContext.Maximized) == WindowBehavior.Line
            && ordinary.Behavior(ScreenContext.Maximized, "Other") == WindowBehavior.Normal);
        // 1.3.0: before, an ordinary panel followed no rule and was left covered; a file saved so keeps that.
        var before = SettingsMigrations.Read("""{"AppVersion":"1.2.5","AlwaysOnTop":false,"Maximized":1,"Borderless":2,"Exclusive":2}""", new Version(1, 3, 0), out _);
        check("An ordinary panel saved before 1.3.0 keeps the style in all three global rules",
            !before.AlwaysOnTop && before.Maximized == WindowBehavior.Normal && before.Borderless == WindowBehavior.Normal && before.Exclusive == WindowBehavior.Normal);
        var defaults = SettingsMigrations.Read("""{"AppVersion":"1.2.5","AlwaysOnTop":false}""", new Version(1, 3, 0), out _);
        check("An ordinary panel saved before 1.3.0 with the default rules keeps the style too",
            defaults.Maximized == WindowBehavior.Normal && defaults.Borderless == WindowBehavior.Normal && defaults.Exclusive == WindowBehavior.Normal);
        var above = SettingsMigrations.Read("""{"AppVersion":"1.2.5","Maximized":1,"Borderless":2,"Exclusive":0}""", new Version(1, 3, 0), out _);
        check("A panel kept above other windows keeps its rules through 1.3.0",
            above.AlwaysOnTop && above.Maximized == WindowBehavior.Line && above.Borderless == WindowBehavior.Hide && above.Exclusive == WindowBehavior.Normal);
        var later = SettingsMigrations.Read("""{"AppVersion":"1.3.0","AlwaysOnTop":false,"Maximized":1}""", new Version(1, 3, 0), out _);
        check("An ordinary panel saved by 1.3.0 keeps the rules it chose", later.Maximized == WindowBehavior.Line);
        check("A file saved before the switch keeps the panel above other windows",
            JsonSerializer.Deserialize<DeckSettings>("{}")!.AlwaysOnTop);

        // Saved settings: the rules round-trip, and the exceptions of an older file become rules.
        var saved = JsonSerializer.Deserialize<DeckSettings>("""{"FullscreenRules":[{"Name":"Sahhpire","Path":"","Maximized":2,"Borderless":null,"Exclusive":1}]}""")!;
        saved.Normalize();
        check("A rule's choices are saved and read back per context",
            saved.FullscreenRules is [var round] && round.Name == "Sahhpire" && round.Maximized == WindowBehavior.Hide
            && round.Borderless is null && round.Exclusive == WindowBehavior.Line);
        var legacy = JsonSerializer.Deserialize<DeckSettings>("""{"FullscreenExceptions":[{"Name":"Sahhpire","Path":"D:\\Sapphire\\Sapphire.exe"}]}""")!;
        legacy.Normalize();
        check("Exceptions saved before app rules become rules that keep the style in all three",
            legacy.FullscreenRules is [var moved] && moved.Name == "Sahhpire" && moved.Maximized == WindowBehavior.Normal
            && moved.Borderless == WindowBehavior.Normal && moved.Exclusive == WindowBehavior.Normal
            && legacy.FullscreenExceptions.Count == 0
            && legacy.Behavior(ScreenContext.Borderless, "Sapphire", @"D:\Sapphire\Sapphire.exe") == WindowBehavior.Normal);
        var settings = new DeckSettings
        {
            FullscreenRules =
            [
                new(" Sahhpire ", @"F:\Apps\Sahhpire\Sahhpire.EXE"),
                new("sahhpire", @"f:\apps\sahhpire\sahhpire.exe"),
                new("", ""),
                new("", @"F:\Apps\Other\Other.exe")
            ]
        };
        settings.Normalize();
        check("Duplicate and empty rules are cleaned up and a path-only rule takes its file name",
            settings.FullscreenRules.Count == 2
            && settings.FullscreenRules[0] is { Name: "Sahhpire", Maximized: null }
            && settings.FullscreenRules[1] is { Name: "Other" });
        var copy = settings.Copy();
        copy.FullscreenRules.Add(new FullscreenRule("Extra"));
        check("A settings copy keeps its own rules", settings.FullscreenRules.Count == 2 && copy.FullscreenRules.Count == 3);
        check("What changed: a context change outranks an app change",
            ScreenPolicy.WhatChanged(ScreenContext.Maximized, "a", "p", ScreenContext.Desktop, "b", "q") == ScreenPolicy.ForegroundChange.Context);
        check("What changed: the same context with another app is an app change",
            ScreenPolicy.WhatChanged(ScreenContext.Maximized, "app2", "p", ScreenContext.Maximized, "app1", "p") == ScreenPolicy.ForegroundChange.App
            && ScreenPolicy.WhatChanged(ScreenContext.Maximized, "app", "q", ScreenContext.Maximized, "app", "p") == ScreenPolicy.ForegroundChange.App);
        check("What changed: identical readings change nothing",
            ScreenPolicy.WhatChanged(ScreenContext.Desktop, "app", "p", ScreenContext.Desktop, "app", "p") == ScreenPolicy.ForegroundChange.None);
        // The panel read the lock screen's full screen for itself as it started and no poll saw it: the first poll, on the desktop
        // like the host's own start, still reports the change, and a context the panel read that still holds reports none.
        check("What changed: a context the panel read for itself and no poll saw is a context change",
            ScreenPolicy.WhatChanged(ScreenContext.Desktop, "", "", ScreenContext.Desktop, "", "", ScreenContext.Borderless) == ScreenPolicy.ForegroundChange.Context
            && ScreenPolicy.WhatChanged(ScreenContext.Desktop, "app", "p", ScreenContext.Desktop, "app", "p", ScreenContext.Desktop) == ScreenPolicy.ForegroundChange.None
            && ScreenPolicy.WhatChanged(ScreenContext.Desktop, "app2", "p", ScreenContext.Desktop, "app", "p", ScreenContext.Desktop) == ScreenPolicy.ForegroundChange.App);
        var many = new DeckSettings { FullscreenRules = Enumerable.Range(0, 40).Select(i => new FullscreenRule("App" + i)).ToList() };
        many.Normalize();
        check("The rule list is capped", many.FullscreenRules.Count == AppPresets.MaximumFullscreenExceptions);
        // A full-screen window behind the foreground one has no rules of its own: the way it fills the screen picks the
        // rule, the app's own first and the global one after it, as if that window were in front.
        var bg = new DeckSettings { Maximized = WindowBehavior.Line, Borderless = WindowBehavior.Hide };
        check("A background full screen follows the global rule for the way it fills the screen",
            bg.Behavior(ScreenContext.BackgroundFullscreen, null, null, ScreenContext.Maximized) == WindowBehavior.Line
            && bg.Behavior(ScreenContext.BackgroundFullscreen, null, null, ScreenContext.Borderless) == WindowBehavior.Hide
            && bg.Behavior(ScreenContext.BackgroundFullscreen, "Player", @"C:\Apps\Player.exe", ScreenContext.Maximized) == WindowBehavior.Line);
        bg.Maximized = WindowBehavior.Normal;
        check("Changing the global rule changes the background answer with it",
            bg.Behavior(ScreenContext.BackgroundFullscreen, null, null, ScreenContext.Maximized) == WindowBehavior.Normal);
        check("A background full screen without a kind, and the desktop itself, leave the panel as it is",
            bg.Behavior(ScreenContext.BackgroundFullscreen) == WindowBehavior.Normal
            && bg.Behavior(ScreenContext.Desktop) == WindowBehavior.Normal);
        var browser = new DeckSettings
        {
            Maximized = WindowBehavior.Line, Borderless = WindowBehavior.Hide,
            FullscreenRules = [new FullscreenRule("chrome") { Maximized = WindowBehavior.Normal }]
        };
        check("An app that keeps the style while maximised keeps it behind the foreground window too",
            browser.Behavior(ScreenContext.BackgroundFullscreen, "chrome", "", ScreenContext.Maximized) == WindowBehavior.Normal
            && browser.Behavior(ScreenContext.Maximized, "chrome", "") == WindowBehavior.Normal);
        check("A background window its app's rule says nothing about takes the global rule",
            browser.Behavior(ScreenContext.BackgroundFullscreen, "firefox", "", ScreenContext.Maximized) == WindowBehavior.Line
            && browser.Behavior(ScreenContext.BackgroundFullscreen, "chrome", "", ScreenContext.Borderless) == WindowBehavior.Hide);

        // The indicator that peeks: a behavior of its own, saved as a number that older ones do not use, and a rule can choose it.
        check("The new behavior is appended: the saved numbers of the others keep their meaning",
            (int)WindowBehavior.Normal == 0 && (int)WindowBehavior.Line == 1 && (int)WindowBehavior.Hide == 2 && (int)WindowBehavior.Peek == 3);
        check("The window rules list every behavior once, the peeking indicator after the plain one",
            WindowBehaviors.All.Count == Enum.GetValues<WindowBehavior>().Length && WindowBehaviors.All.Distinct().Count() == WindowBehaviors.All.Count
            && WindowBehaviors.All.ToList().IndexOf(WindowBehavior.Peek) == WindowBehaviors.All.ToList().IndexOf(WindowBehavior.Line) + 1);
        check("Only the line and the peeking line are indicators",
            WindowBehavior.Line.IsIndicator() && WindowBehavior.Peek.IsIndicator() && !WindowBehavior.Normal.IsIndicator() && !WindowBehavior.Hide.IsIndicator());
        var peeking = JsonSerializer.Deserialize<DeckSettings>("""{"Maximized":3,"Borderless":1,"FullscreenRules":[{"Name":"Sahhpire","Maximized":3,"Exclusive":3}]}""")!;
        peeking.Normalize();
        check("The peeking behavior is saved and read back, globally and in an app's rule",
            peeking.Maximized == WindowBehavior.Peek && peeking.Borderless == WindowBehavior.Line
            && peeking.FullscreenRules is [{ Maximized: WindowBehavior.Peek, Borderless: null, Exclusive: WindowBehavior.Peek }]
            && peeking.Behavior(ScreenContext.Maximized, "Sahhpire") == WindowBehavior.Peek && peeking.Behavior(ScreenContext.Maximized, "Other") == WindowBehavior.Peek);
        var roundTrip = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(peeking))!;
        check("The peeking behavior survives a save", roundTrip.Maximized == WindowBehavior.Peek && roundTrip.FullscreenRules[0].Exclusive == WindowBehavior.Peek);
        var unknown = JsonSerializer.Deserialize<DeckSettings>("""{"Maximized":9,"FullscreenRules":[{"Name":"X","Maximized":9}]}""")!;
        unknown.Normalize();
        check("A number no build knows falls back to the default or to Follow",
            unknown.Maximized == WindowBehavior.Line && unknown.FullscreenRules[0].Maximized is null);
        check("The defaults are unchanged",
            new DeckSettings() is { Maximized: WindowBehavior.Line, Borderless: WindowBehavior.Hide, Exclusive: WindowBehavior.Hide });
        check("Media is wanted for the indicator's progress under the peeking rule as under the plain one",
            new DeckSettings { MusicIndicatorProgress = true, MusicPage = false, Maximized = WindowBehavior.Peek }.MediaWanted
            == new DeckSettings { MusicIndicatorProgress = true, MusicPage = false, Maximized = WindowBehavior.Line }.MediaWanted);

        // The peek's state: begins once, ends once, and rests until the pointer has left the line.
        var peek = new WindowPeek();
        check("A line at rest begins a peek", !peek.Active && peek.Begin() && peek.Active);
        check("A peek does not begin twice", !peek.Begin());
        check("A peek ends once and the line then rests", peek.End() && !peek.Active && peek.Resting && !peek.End());
        peek.Observe(true);
        check("A resting line does not peek again while the pointer is on it", peek.Resting && !peek.Begin());
        peek.Observe(false);
        check("A resting line wakes when the pointer has left it, and peeks again", !peek.Resting && peek.Begin());
        peek.Observe(false);
        check("Observing the pointer does nothing to a peek that is showing", peek.Active && !peek.Resting);
        var opened = new WindowPeek();
        opened.Begin();
        check("A panel closed from the peeking row ends the peek at once and the line rests, once",
            opened.PanelClosed() && !opened.Active && opened.Resting && !opened.PanelClosed() && !opened.Begin());
        peek.End(); peek.Reset();
        check("A rule that is no longer the peeking one clears the state", !peek.Active && !peek.Resting && peek.Begin());

        // A window over the panel's place: off by default, the global choice everywhere, the app rules say nothing about it.
        check("A window over the panel keeps the style by default", new DeckSettings().Behavior(ScreenContext.Occupied, "Any", "") == WindowBehavior.Normal);
        var occupied = new DeckSettings
        {
            Occupied = WindowBehavior.Line, AlwaysOnTop = false,
            FullscreenRules = [new FullscreenRule("chrome") { Maximized = WindowBehavior.Normal, Borderless = WindowBehavior.Normal, Exclusive = WindowBehavior.Normal }]
        };
        check("A window over the panel follows the global choice, whatever its app's rule",
            occupied.Behavior(ScreenContext.Occupied) == WindowBehavior.Line
            && occupied.Behavior(ScreenContext.Occupied, "chrome", "") == WindowBehavior.Line
            && occupied.Behavior(ScreenContext.Desktop, "chrome", "") == WindowBehavior.Normal);
        var savedChoice = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { Occupied = WindowBehavior.Hide }))!;
        var broken = JsonSerializer.Deserialize<DeckSettings>("""{"Occupied":7}""")!;
        broken.Normalize();
        check("The choice for a window over the panel is saved, and an unknown one falls back to keeping the style",
            savedChoice.Occupied == WindowBehavior.Hide && broken.Occupied == WindowBehavior.Normal
            && JsonSerializer.Deserialize<DeckSettings>("{}")!.Occupied == WindowBehavior.Normal);
    }
}
