using BrimDeck.Core;

internal static class HotkeyTests
{
    internal static void Run(Action<string, bool> check)
    {
        static bool Key(string name) => name.Length > 0 && !name.All(char.IsDigit);
        check("Hotkey text keeps the fixed modifier order",
            HotkeyText.Normalize("Ctrl+Alt+K") == "Ctrl+Alt+K" && HotkeyText.Normalize("shift+control+D1") == "Ctrl+Shift+D1"
            && HotkeyText.Normalize("Win+Shift+Alt+Ctrl+Space") == "Ctrl+Alt+Shift+Win+Space" && HotkeyText.Normalize(" Ctrl + Alt + K ") == "Ctrl+Alt+K"
            && HotkeyText.Format(HotkeyModifiers.Win | HotkeyModifiers.Ctrl, "F5") == "Ctrl+Win+F5");
        check("Only F1 to F24 stand without a modifier",
            HotkeyText.Normalize("F9") == "F9" && HotkeyText.Normalize("F24") == "F24" && HotkeyText.Normalize("F1") == "F1"
            && HotkeyText.Normalize("F25") == "" && HotkeyText.Normalize("F0") == "" && HotkeyText.Normalize("F01") == "" && HotkeyText.Normalize("K") == ""
            && HotkeyText.Normalize("Escape") == "" && HotkeyText.Normalize("Shift+K") == "Shift+K");
        check("Malformed hotkey texts are rejected",
            HotkeyText.Normalize("Ctrl+Ctrl+K") == "" && HotkeyText.Normalize("Ctrl+") == "" && HotkeyText.Normalize("Ctrl+Alt") == ""
            && HotkeyText.Normalize("K+Ctrl") == "" && HotkeyText.Normalize("Ctrl+K+L") == "" && HotkeyText.Normalize("Ctrl+Page Up") == ""
            && HotkeyText.Normalize("Hyper+K") == "" && HotkeyText.Normalize(null) == "");
        check("An empty hotkey is unset, not invalid",
            HotkeyText.IsUnset("") && HotkeyText.IsUnset("  ") && HotkeyText.IsUnset(null) && !HotkeyText.IsUnset("Ctrl+K")
            && HotkeyText.Plan(HotkeyKind.Hide, "", "", Key).Status == HotkeyStatus.Unset
            && HotkeyText.Plan(HotkeyKind.Hide, "Ctrl+", "", Key).Status == HotkeyStatus.Invalid
            && HotkeyText.Plan(HotkeyKind.Hide, "Ctrl+5", "", Key).Status == HotkeyStatus.Invalid);
        check("The same combination in any spelling is a duplicate; the expand hotkey yields",
            HotkeyText.Same("Ctrl+Alt+K", "alt+ctrl+k") && !HotkeyText.Same("", "") && !HotkeyText.Same("Ctrl+K", "Ctrl+Shift+K")
            && HotkeyText.Plan(HotkeyKind.Hide, "Ctrl+Alt+K", "alt+ctrl+K", Key) == (null, "Ctrl+Alt+K")
            && HotkeyText.Plan(HotkeyKind.Expand, "Ctrl+Alt+K", "alt+ctrl+K", Key) == (HotkeyStatus.Duplicate, "Ctrl+Alt+K")
            && HotkeyText.Plan(HotkeyKind.Expand, "Ctrl+Alt+J", "Ctrl+Alt+K", Key) == (null, "Ctrl+Alt+K")
            && HotkeyText.Plan(HotkeyKind.Expand, "Ctrl+", "Ctrl+Alt+K", Key) == (null, "Ctrl+Alt+K"));
        // life-T2: the notices hotkey yields to both earlier ones.
        check("The notices hotkey yields to the hide and expand hotkeys",
            HotkeyText.Plan(HotkeyKind.Notices, "Ctrl+Alt+K", "", "Ctrl+Alt+K", Key) == (HotkeyStatus.Duplicate, "Ctrl+Alt+K")
            && HotkeyText.Plan(HotkeyKind.Notices, "", "Ctrl+Alt+J", "Ctrl+Alt+J", Key) == (HotkeyStatus.Duplicate, "Ctrl+Alt+J")
            && HotkeyText.Plan(HotkeyKind.Notices, "Ctrl+Alt+K", "Ctrl+Alt+J", "alt+ctrl+K", Key) == (HotkeyStatus.Duplicate, "Ctrl+Alt+K")
            && HotkeyText.Plan(HotkeyKind.Notices, "Ctrl+Alt+K", "Ctrl+Alt+J", "Ctrl+Alt+N", Key) == (null, "Ctrl+Alt+N"));
        check("When all three hotkeys match, only the hide hotkey registers",
            HotkeyText.Plan(HotkeyKind.Hide, "Ctrl+Alt+K", "Ctrl+Alt+K", "Ctrl+Alt+K", Key) == (null, "Ctrl+Alt+K")
            && HotkeyText.Plan(HotkeyKind.Expand, "Ctrl+Alt+K", "Ctrl+Alt+K", "Ctrl+Alt+K", Key) == (HotkeyStatus.Duplicate, "Ctrl+Alt+K")
            && HotkeyText.Plan(HotkeyKind.Notices, "Ctrl+Alt+K", "Ctrl+Alt+K", "Ctrl+Alt+K", Key) == (HotkeyStatus.Duplicate, "Ctrl+Alt+K"));
        check("The notices hotkey is unset or invalid on its own, and an invalid earlier hotkey does not take its combination",
            HotkeyText.Plan(HotkeyKind.Notices, "Ctrl+Alt+K", "", "", Key).Status == HotkeyStatus.Unset
            && HotkeyText.Plan(HotkeyKind.Notices, "Ctrl+Alt+K", "", null, Key).Status == HotkeyStatus.Unset
            && HotkeyText.Plan(HotkeyKind.Notices, "", "", "Ctrl+5", Key).Status == HotkeyStatus.Invalid
            && HotkeyText.Plan(HotkeyKind.Notices, "Ctrl+5", "Ctrl+", "Ctrl+Alt+N", Key) == (null, "Ctrl+Alt+N")
            && HotkeyText.Plan(HotkeyKind.Expand, "Ctrl+Alt+K", "Ctrl+Alt+J", "Ctrl+Alt+J", Key) == (null, "Ctrl+Alt+J"));
        // The audit group "win" keeps its tests in their own file; they run from here so that Program.cs stays untouched.
        AuditWinTests.Run(check);
    }
}
