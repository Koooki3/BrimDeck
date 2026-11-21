using System.Text.Json.Nodes;
using NodeValue = System.Text.Json.Nodes.JsonValue;

namespace BrimDeck.Core;

// Builds of this fork once offered a fourth window behavior, the indicator with a read-only preview bubble ("Peek", saved as
// the number 3). It is gone; a setting or an app rule saved with it reads as the plain indicator (WindowBehavior.Line), and
// every other member of the rule is left as it was. WindowBehavior is saved as its number; the name is matched too, so a file
// edited by hand reads the same. Not a versioned step (SettingsMigrations): those builds carried the version of the release
// they were built on, so the change has no version of its own. It runs on every read and touches only the retired value.
// Upstream may yet give WindowBehavior a member of its own numbered 3 (or named Peek): from then on that value is the new
// member's and is read as saved. So the number 3 is moved only while WindowBehavior defines no 3, and the name "Peek" only
// while it has no member of that name (RetiredPeekRuleTests checks both with a stand-in for a newer enum).
public static class RetiredWindowBehaviors
{
    private const int Peek = 3;
    private static readonly string[] Global = ["Maximized", "Borderless", "Exclusive", "Occupied"];
    private static readonly string[] Rule = ["Maximized", "Borderless", "Exclusive"];

    public static void PeekToLine(JsonObject settings) => PeekToLine(settings, NumberDefined, NameDefined);

    // Whether WindowBehavior has a member of this number, or of this name (in any case, as a file edited by hand may write it).
    internal static bool NumberDefined(int number) => Enum.IsDefined((WindowBehavior)number);
    internal static bool NameDefined(string name) => Enum.GetNames<WindowBehavior>().Any(member => string.Equals(member, name, StringComparison.OrdinalIgnoreCase));

    // numberDefined and nameDefined stand for WindowBehavior; the tests pass those of a newer enum.
    internal static void PeekToLine(JsonObject settings, Func<int, bool> numberDefined, Func<string, bool> nameDefined)
    {
        bool number = !numberDefined(Peek), name = !nameDefined(nameof(Peek));
        if (!number && !name) return;
        foreach (var key in Global) Replace(settings, key, number, name);
        if (settings["FullscreenRules"] is not JsonArray rules) return;
        foreach (var rule in rules.OfType<JsonObject>())
            foreach (var key in Rule) Replace(rule, key, number, name);
    }

    private static void Replace(JsonObject owner, string key, bool number, bool name)
    {
        if (owner[key] is not NodeValue value) return;
        bool peek = number && value.TryGetValue(out int saved) && saved == Peek
            || name && value.TryGetValue(out string? text) && string.Equals(text?.Trim(), nameof(Peek), StringComparison.OrdinalIgnoreCase);
        if (peek) owner[key] = (int)WindowBehavior.Line;
    }
}
