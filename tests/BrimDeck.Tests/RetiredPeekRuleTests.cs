using System.Text.Json;
using System.Text.Json.Nodes;
using BrimDeck.Core;

// The retired "indicator + preview" window behavior (RetiredWindowBehaviors): saved as a number or a name, globally, in an app
// rule or in another profile, it reads as the plain indicator, and nothing else of the file changes.
internal static class RetiredPeekRuleTests
{
    private const string Profile = "11111111-1111-1111-1111-111111111111", Other = "22222222-2222-2222-2222-222222222222";

    private static string File(string peek) => $$$"""
        {"Width":900,"AlwaysOnTop":true,"ClickToExpand":true,"OpenDelay":250,
         "Maximized":{{{peek}}},"Borderless":{{{peek}}},"Exclusive":2,"Occupied":{{{peek}}},
         "FullscreenRules":[{"Name":"game","Path":"C:\\Games\\game.exe","Maximized":{{{peek}}},"Borderless":0,"Exclusive":null},
                            {"Name":"video","Maximized":2,"Borderless":{{{peek}}}}],
         "CurrentProfile":"{{{Profile}}}",
         "Profiles":[{"Id":"{{{Profile}}}","Name":"Main"},{"Id":"{{{Other}}}","Name":"Game","Values":{"Maximized":{{{peek}}},"Exclusive":{{{peek}}},"Width":700,
           "FullscreenRules":[{"Name":"tool","Exclusive":{{{peek}}}}]}}]}
        """;

    public static void Run(Action<string, bool> check)
    {
        // Every read gives the default usage rows new ids of their own; the rest is compared as saved.
        static string Saved(DeckSettings settings) { var saved = JsonSerializer.SerializeToNode(settings)!.AsObject(); saved.Remove(nameof(DeckSettings.Apps)); return saved.ToJsonString(); }
        var line = SettingsMigrations.Read(File("1"));
        foreach (var (label, peek) in new[] { ("its number", "3"), ("its name", "\"Peek\""), ("its name in another case", "\" peek \"") })
        {
            DeckSettings? read = null;
            try { read = SettingsMigrations.Read(File(peek)); } catch (Exception) { }
            check($"A file with the retired preview rule saved as {label} reads without an error", read is not null);
            if (read is null) continue;
            check($"The retired preview rule saved as {label} reads as the plain indicator, globally",
                read is { Maximized: WindowBehavior.Line, Borderless: WindowBehavior.Line, Exclusive: WindowBehavior.Hide, Occupied: WindowBehavior.Line });
            check($"The retired preview rule saved as {label} reads as the plain indicator in an app rule, which keeps its other members",
                read.FullscreenRules is [{ Name: "game", Maximized: WindowBehavior.Line, Borderless: WindowBehavior.Normal, Exclusive: null } game,
                    { Name: "video", Maximized: WindowBehavior.Hide, Borderless: WindowBehavior.Line, Exclusive: null }]
                && game.Path == ScreenPolicy.NormalizePath(@"C:\Games\game.exe") && game.Path.Length > 0);
            var values = read.Profiles.Single(profile => profile.Id == Guid.Parse(Other)).Values!;
            check($"The retired preview rule saved as {label} reads as the plain indicator in another profile",
                (int?)values["Maximized"] == (int)WindowBehavior.Line && (int?)values["Exclusive"] == (int)WindowBehavior.Line && (int?)values["Width"] == 700
                && values["FullscreenRules"]![0]!["Exclusive"]!.GetValue<int>() == (int)WindowBehavior.Line
                && Profiles.With(read, values) is { Maximized: WindowBehavior.Line, Exclusive: WindowBehavior.Line });
            check($"Apart from the retired rule saved as {label}, the settings read are those of the same file with the plain indicator",
                Saved(read) == Saved(line) && read is { Width: 900, ClickToExpand: true, OpenDelay: 250, AlwaysOnTop: true });
        }
        var untouched = JsonNode.Parse("""{"Maximized":0,"Borderless":2,"Exclusive":"Hide","Occupied":1,"FullscreenRules":[{"Name":"x","Maximized":0}]}""")!.AsObject();
        var before = untouched.ToJsonString();
        RetiredWindowBehaviors.PeekToLine(untouched);
        check("The other window behaviors are left as they were saved", untouched.ToJsonString() == before);
        check("Every window behavior that remains keeps its saved number",
            (int)WindowBehavior.Normal == 0 && (int)WindowBehavior.Line == 1 && (int)WindowBehavior.Hide == 2 && Enum.GetValues<WindowBehavior>().Length == 3);

        // The migration holds only while WindowBehavior has no 3 and no Peek of its own. Should upstream add one (an indicator
        // that opens into a capsule, say, numbered 3), a saved 3 is that member's and must be read as saved, not made the indicator.
        check("Today WindowBehavior defines neither the number 3 nor the name Peek, so both are migrated",
            !RetiredWindowBehaviors.NumberDefined(3) && !RetiredWindowBehaviors.NameDefined("Peek")
            && RetiredWindowBehaviors.NumberDefined(1) && RetiredWindowBehaviors.NameDefined("line"));
        static JsonObject Saved3() => JsonNode.Parse("""{"Maximized":3,"Borderless":"Peek","Occupied":3,"FullscreenRules":[{"Name":"x","Maximized":3,"Exclusive":"peek"}]}""")!.AsObject();
        var newer3 = Saved3();
        RetiredWindowBehaviors.PeekToLine(newer3, number => number is >= 0 and <= 3, _ => false);
        check("With a newer WindowBehavior that defines 3, a saved 3 is left as it was, globally and in an app rule",
            (int?)newer3["Maximized"] == 3 && (int?)newer3["Occupied"] == 3 && (int?)newer3["FullscreenRules"]![0]!["Maximized"] == 3);
        check("With a newer WindowBehavior that defines 3 but no Peek, the name Peek still reads as the plain indicator",
            (int?)newer3["Borderless"] == (int)WindowBehavior.Line && (int?)newer3["FullscreenRules"]![0]!["Exclusive"] == (int)WindowBehavior.Line);
        var newerPeek = Saved3();
        RetiredWindowBehaviors.PeekToLine(newerPeek, number => number is >= 0 and <= 2, name => string.Equals(name, "Peek", StringComparison.OrdinalIgnoreCase));
        check("With a newer WindowBehavior that has a member named Peek, the name is left as written and only the number 3 is migrated",
            (string?)newerPeek["Borderless"] == "Peek" && (string?)newerPeek["FullscreenRules"]![0]!["Exclusive"] == "peek"
            && (int?)newerPeek["Maximized"] == (int)WindowBehavior.Line && (int?)newerPeek["FullscreenRules"]![0]!["Maximized"] == (int)WindowBehavior.Line);
        var both = Saved3(); var bothBefore = both.ToJsonString();
        RetiredWindowBehaviors.PeekToLine(both, number => number is >= 0 and <= 3, name => string.Equals(name, "Peek", StringComparison.OrdinalIgnoreCase));
        check("With both defined, nothing is migrated", both.ToJsonString() == bothBefore);
    }
}
