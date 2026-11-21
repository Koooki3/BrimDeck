using System.Text.Json.Nodes;
using NodeValue = System.Text.Json.Nodes.JsonValue;

namespace BrimDeck.Core;

// Builds of this fork once offered a fourth window behavior, the indicator with a read-only preview bubble ("Peek", saved as
// the number 3). It is gone; a setting or an app rule saved with it reads as the plain indicator (WindowBehavior.Line), and
// every other member of the rule is left as it was. WindowBehavior is saved as its number; the name is matched too, so a file
// edited by hand reads the same. Not a versioned step (SettingsMigrations): those builds carried the version of the release
// they were built on, so the change has no version of its own. It runs on every read and touches only the retired value.
public static class RetiredWindowBehaviors
{
    private const int Peek = 3;
    private static readonly string[] Global = ["Maximized", "Borderless", "Exclusive", "Occupied"];
    private static readonly string[] Rule = ["Maximized", "Borderless", "Exclusive"];

    public static void PeekToLine(JsonObject settings)
    {
        foreach (var name in Global) Replace(settings, name);
        if (settings["FullscreenRules"] is not JsonArray rules) return;
        foreach (var rule in rules.OfType<JsonObject>())
            foreach (var name in Rule) Replace(rule, name);
    }

    private static void Replace(JsonObject owner, string name)
    {
        if (owner[name] is not NodeValue value) return;
        bool peek = value.TryGetValue(out int number) && number == Peek
            || value.TryGetValue(out string? text) && string.Equals(text?.Trim(), "Peek", StringComparison.OrdinalIgnoreCase);
        if (peek) owner[name] = (int)WindowBehavior.Line;
    }
}
