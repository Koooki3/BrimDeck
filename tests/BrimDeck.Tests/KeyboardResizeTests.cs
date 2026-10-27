using BrimDeck.Core;

// The resize shortcut (HotkeyKind.Resize, KeyboardResize): its saved text and the four combinations it stands for, how it
// yields to the other shortcuts, and the steps it takes within a page's limits without touching the other pages' sizes.
internal static class KeyboardResizeTests
{
    internal static void Run(Action<string, bool> check)
    {
        static bool Key(string name) => name.Length > 0 && !name.All(char.IsDigit);

        check("Resize shortcut: modifiers with Arrows, in the fixed order",
            HotkeyText.TryParseArrows("alt+ctrl+arrows", out var modifiers) && modifiers == (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)
            && HotkeyText.PlanArrows("alt+ctrl+arrows", Key, "", "") == (null, "Ctrl+Alt+Arrows"));
        check("Resize shortcut: the arrow keys never stand alone, and a single arrow is not the shortcut",
            !HotkeyText.TryParseArrows("Arrows", out _) && !HotkeyText.TryParseArrows("Ctrl+Alt+Left", out _)
            && HotkeyText.PlanArrows("Arrows", Key, "", "").Status == HotkeyStatus.Invalid
            && HotkeyText.PlanArrows("Ctrl+Alt+Left", Key, "", "").Status == HotkeyStatus.Invalid
            && HotkeyText.PlanArrows("", Key, "", "").Status == HotkeyStatus.Unset);
        // Shift, Ctrl and Win with the arrow keys select and move through text or arrange windows in every program.
        check("Resize shortcut: Alt and one more modifier, never the system's own arrow shortcuts",
            HotkeyText.TryParseArrows("Ctrl+Alt+Arrows", out _) && HotkeyText.TryParseArrows("Alt+Shift+Arrows", out _) && HotkeyText.TryParseArrows("Alt+Win+Arrows", out _)
            && !HotkeyText.TryParseArrows("Alt+Arrows", out _) && !HotkeyText.TryParseArrows("Shift+Arrows", out _) && !HotkeyText.TryParseArrows("Ctrl+Arrows", out _)
            && !HotkeyText.TryParseArrows("Ctrl+Shift+Arrows", out _) && !HotkeyText.TryParseArrows("Win+Arrows", out _) && !HotkeyText.TryParseArrows("Ctrl+Win+Arrows", out _)
            && HotkeyText.PlanArrows("Shift+Arrows", Key, "", "").Status == HotkeyStatus.Invalid
            && HotkeyText.Combinations("Arrows").Length == 0);
        // The settings row reads "Alt + [box] + arrow keys": the box shows and records only the modifiers other than Alt.
        check("Resize shortcut: the box shows only the modifiers besides Alt, in the fixed order",
            HotkeyText.ArrowExtras("Ctrl+Alt+Arrows") == "Ctrl" && HotkeyText.ArrowExtras("win+shift+alt+ctrl+arrows") == "Ctrl+Shift+Win"
            && HotkeyText.ArrowExtras("Alt+Win+Arrows") == "Win" && HotkeyText.ArrowExtras("") == "" && HotkeyText.ArrowExtras("Ctrl+Alt+Left") == ""
            && HotkeyText.ArrowExtras("Ctrl+Shift+Arrows") == "" && HotkeyText.ArrowExtras("Alt+Arrows") == ""
            && HotkeyText.FormatModifiers(HotkeyModifiers.None) == "" && HotkeyText.FormatModifiers(HotkeyModifiers.Win | HotkeyModifiers.Ctrl) == "Ctrl+Win");
        check("Resize shortcut: the recorded modifiers save with Alt and Arrows; Alt among them changes nothing, Alt alone saves nothing",
            HotkeyText.FromArrowExtras(HotkeyModifiers.Ctrl) == "Ctrl+Alt+Arrows"
            && HotkeyText.FromArrowExtras(HotkeyModifiers.Shift | HotkeyModifiers.Ctrl) == "Ctrl+Alt+Shift+Arrows"
            && HotkeyText.FromArrowExtras(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt) == "Ctrl+Alt+Arrows"
            && HotkeyText.FromArrowExtras(HotkeyModifiers.Win) == "Alt+Win+Arrows"
            && HotkeyText.FromArrowExtras(HotkeyModifiers.Alt) == "" && HotkeyText.FromArrowExtras(HotkeyModifiers.None) == ""
            && HotkeyText.ArrowExtras(HotkeyText.FromArrowExtras(HotkeyModifiers.Shift | HotkeyModifiers.Win)) == "Shift+Win"
            && HotkeyText.PlanArrows(HotkeyText.FromArrowExtras(HotkeyModifiers.Ctrl), Key, "", "") == (null, "Ctrl+Alt+Arrows"));
        check("Resize shortcut: it stands for its four arrow keys, Left, Right, Up, Down",
            HotkeyText.Combinations("Ctrl+Alt+Arrows").SequenceEqual(["Ctrl+Alt+Left", "Ctrl+Alt+Right", "Ctrl+Alt+Up", "Ctrl+Alt+Down"])
            && HotkeyText.Combinations("ctrl+k").SequenceEqual(["Ctrl+k"]) && HotkeyText.Combinations("").Length == 0);
        check("Resize shortcut: it clashes with a shortcut on one of its arrows and yields to the hide and expand shortcuts",
            HotkeyText.Overlap("Ctrl+Alt+Arrows", "alt+ctrl+Down") && !HotkeyText.Overlap("Ctrl+Alt+Arrows", "Ctrl+Shift+Down")
            && !HotkeyText.Overlap("Ctrl+Alt+Arrows", "Ctrl+Alt+K") && HotkeyText.Overlap("Ctrl+Alt+K", "ctrl+alt+k")
            && HotkeyText.PlanArrows("Ctrl+Alt+Arrows", Key, "Ctrl+Alt+Up", "") == (HotkeyStatus.Duplicate, "Ctrl+Alt+Arrows")
            && HotkeyText.PlanArrows("Ctrl+Alt+Arrows", Key, "", "Ctrl+Alt+Left") == (HotkeyStatus.Duplicate, "Ctrl+Alt+Arrows")
            && HotkeyText.PlanArrows("Ctrl+Alt+Arrows", Key, "Ctrl+Alt+K", "Ctrl+Shift+Left") == (null, "Ctrl+Alt+Arrows")
            && HotkeyText.Plan(HotkeyKind.Expand, "", "Ctrl+Alt+Left", Key) == (null, "Ctrl+Alt+Left")
            && HotkeyText.Plan(HotkeyKind.Hide, "Ctrl+Alt+Up", "", Key) == (null, "Ctrl+Alt+Up"));
        check("Resize shortcut: a shortcut that cannot be registered takes nothing from it",
            HotkeyText.PlanArrows("Ctrl+Alt+Arrows", Key, "Ctrl+Alt+5", "Ctrl+") == (null, "Ctrl+Alt+Arrows"));

        check("Resize shortcut: the arrows map to narrower, wider, shorter, taller",
            KeyboardResize.Of(0) == KeyboardResize.Direction.Narrower && KeyboardResize.Of(1) == KeyboardResize.Direction.Wider
            && KeyboardResize.Of(2) == KeyboardResize.Direction.Shorter && KeyboardResize.Of(3) == KeyboardResize.Direction.Taller
            && KeyboardResize.Horizontal(KeyboardResize.Direction.Wider) && !KeyboardResize.Horizontal(KeyboardResize.Direction.Taller));
        check("Resize shortcut: each press moves the size by one step",
            KeyboardResize.Next(460, KeyboardResize.Direction.Wider, 440, 1200) == 460 + KeyboardResize.Step
            && KeyboardResize.Next(300, KeyboardResize.Direction.Shorter, 140, 400) == 300 - KeyboardResize.Step
            && KeyboardResize.Next(300.4, KeyboardResize.Direction.Taller, 140, 400) == 324);
        // Held down, the key repeats: the steps add up from where the size is heading, and stop at the limits.
        double width = 460;
        for (int i = 0; i < 40; i++) width = KeyboardResize.Next(width, KeyboardResize.Direction.Wider, 440, 1200);
        double narrow = 460;
        for (int i = 0; i < 40; i++) narrow = KeyboardResize.Next(narrow, KeyboardResize.Direction.Narrower, 440, 1200);
        check("Resize shortcut: held down it stops at the page's limits",
            width == 1200 && narrow == 440 && KeyboardResize.Next(1300, KeyboardResize.Direction.Wider, 440, 1200) == 1200
            && KeyboardResize.Next(100, KeyboardResize.Direction.Narrower, 440, 1200) == 440
            && KeyboardResize.Next(500, KeyboardResize.Direction.Wider, 440, 300) == 440);

        // Each page keeps its own size, as dragging an edge does (DeckPages.SetSize).
        var settings = new DeckSettings();
        var usage = DeckPages.Size(settings, DeckPage.Usage);
        var monitor = DeckPages.Size(settings, DeckPage.Monitor);
        var (minWidth, maxWidth, minHeight, maxHeight) = DeckPages.Limits(settings, DeckPage.Music);
        var music = DeckPages.Size(settings, DeckPage.Music);
        double musicWidth = KeyboardResize.Next(music.Width, KeyboardResize.Direction.Wider, minWidth, maxWidth);
        double musicHeight = KeyboardResize.Next(music.Height, KeyboardResize.Direction.Taller, minHeight, maxHeight);
        DeckPages.SetSize(settings, DeckPage.Music, musicWidth, musicHeight);
        check("Resize shortcut: a step changes only the page it was taken on",
            DeckPages.Size(settings, DeckPage.Music) == (music.Width + KeyboardResize.Step, music.Height + KeyboardResize.Step)
            && DeckPages.Size(settings, DeckPage.Usage) == usage && DeckPages.Size(settings, DeckPage.Monitor) == monitor);
        check("Resize shortcut: unset by default, and a missing value reads as unset",
            new DeckSettings().ResizeHotkey == "" && Normalized(new DeckSettings { ResizeHotkey = null! }).ResizeHotkey == "");
    }

    private static DeckSettings Normalized(DeckSettings settings) { settings.Normalize(); return settings; }
}
