using BrimDeck;
using BrimDeck.Core;

// What a panel look compares to tell an edited theme file from the one it shows (PanelLook.Signature): a reload makes the edit
// current only when the signature changes. Themes are built in memory from file text; nothing is read from the themes folder.
internal static class ThemeLookChecks
{
    public static void Run(Action<string, bool> check)
    {
        static string Signature(string json)
        {
            var info = new PanelThemeInfo("motion-look", PanelThemeKind.Custom, PanelThemes.Parse(json), "motion-look.json", null);
            return PanelLook.Signature(info, PanelThemes.Resolve(info));
        }
        const string Start = """{ "id": "motion-look", "motion": { "hover": "underline", "hoverStrength": 0.2, "pace": 1, "color": "accent", "secondary": "rim" } }""";
        string before = Signature(Start);
        check("theme look: the same file gives the same signature", Signature(Start) == before);
        foreach (var (field, edited) in new[]
        {
            ("hover style", Start.Replace("\"underline\"", "\"lift\"")),
            ("hover strength", Start.Replace("0.2", "0.25")),
            ("pace", Start.Replace("\"pace\": 1", "\"pace\": 1.4")),
            ("main color", Start.Replace("\"accent\"", "\"#336699\"")),
            ("second color", Start.Replace("\"rim\"", "\"#996633\""))
        })
            check($"theme look: an edit of only the motion's {field} is a new signature, so a reload shows it", Signature(edited) != before);
    }
}
