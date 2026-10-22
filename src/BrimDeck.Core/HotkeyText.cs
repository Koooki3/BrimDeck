namespace BrimDeck.Core;

// Notices opens the shown notice's details for the keyboard, or the recent notices page.
public enum HotkeyKind { Hide, Expand, Notices }

// Unset: no combination saved. Active: registered. InUse: another program holds the combination. Invalid: the text is not
// an allowed combination. Duplicate: the same combination as the other hotkey, which keeps it.
public enum HotkeyStatus { Unset, Active, InUse, Invalid, Duplicate }

[Flags]
public enum HotkeyModifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

// The saved text of a global hotkey: modifiers in the fixed order Ctrl+Alt+Shift+Win, then the main key's name (a WPF Key
// name such as "K", "D1" or "F9"), joined by "+". Only F1 to F24 may stand without a modifier. Left and right modifiers are
// not told apart. Whether the key name names a real key is decided by the caller (HotkeyGesture in the app).
public static class HotkeyText
{
    private static readonly (HotkeyModifiers Flag, string Name)[] Order =
        [(HotkeyModifiers.Ctrl, "Ctrl"), (HotkeyModifiers.Alt, "Alt"), (HotkeyModifiers.Shift, "Shift"), (HotkeyModifiers.Win, "Win")];

    // Accepts the modifiers in any order and letter case, and the aliases Control and Windows; rejects repeated modifiers,
    // a missing or repeated main key and spaces inside a part. An empty text is not a hotkey (see IsUnset).
    public static bool TryParse(string? text, out HotkeyModifiers modifiers, out string key)
    {
        modifiers = HotkeyModifiers.None; key = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length == 0 || part.Any(char.IsWhiteSpace)) return false;
            var flag = Modifier(part);
            if (i < parts.Length - 1)
            {
                if (flag == HotkeyModifiers.None || modifiers.HasFlag(flag)) return false;
                modifiers |= flag;
            }
            else
            {
                if (flag != HotkeyModifiers.None) return false;
                key = part;
            }
        }
        return IsAllowed(modifiers, key);
    }

    public static string Format(HotkeyModifiers modifiers, string key)
        => string.Join("+", Order.Where(o => modifiers.HasFlag(o.Flag)).Select(o => o.Name).Append(key));

    // The canonical form of a saved text, or "" when it is empty or not an allowed combination.
    public static string Normalize(string? text) => TryParse(text, out var modifiers, out var key) ? Format(modifiers, key) : "";

    public static bool IsUnset(string? text) => string.IsNullOrWhiteSpace(text);

    public static bool IsAllowed(HotkeyModifiers modifiers, string key)
        => key.Length > 0 && (modifiers & ~(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win)) == 0
           && (modifiers != HotkeyModifiers.None || IsFunctionKey(key));

    // F1 to F24, which may be used alone.
    public static bool IsFunctionKey(string key)
        => key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), System.Globalization.NumberStyles.None, null, out int n)
           && n is >= 1 and <= 24 && key[1] != '0';

    // Whether two saved texts name the same combination, whatever their spelling. Two unset or invalid texts are not the same.
    public static bool Same(string? first, string? second)
        => Normalize(first) is { Length: > 0 } a && string.Equals(a, Normalize(second), StringComparison.OrdinalIgnoreCase);

    // What each hotkey should do before registration: Unset, Invalid, Duplicate (a later hotkey yields to an earlier one that
    // names the same combination: expand to hide, notices to both), or null to register its canonical text. isKey checks the
    // main key's name.
    public static (HotkeyStatus? Status, string Text) Plan(HotkeyKind kind, string? hide, string? expand, Func<string, bool> isKey)
        => Plan(kind, hide, expand, null, isKey);
    public static (HotkeyStatus? Status, string Text) Plan(HotkeyKind kind, string? hide, string? expand, string? notices, Func<string, bool> isKey)
    {
        string?[] texts = [hide, expand, notices];
        string own = texts[(int)kind] ?? "";
        if (IsUnset(own)) return (HotkeyStatus.Unset, "");
        if (!TryParse(own, out var modifiers, out var key) || !isKey(key)) return (HotkeyStatus.Invalid, "");
        string text = Format(modifiers, key);
        for (int earlier = 0; earlier < (int)kind; earlier++)
            if (Normalize(texts[earlier]) is { Length: > 0 } other && string.Equals(other, text, StringComparison.OrdinalIgnoreCase)
                && TryParse(texts[earlier], out _, out var otherKey) && isKey(otherKey))
                return (HotkeyStatus.Duplicate, text);
        return (null, text);
    }

    private static HotkeyModifiers Modifier(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HotkeyModifiers.Ctrl,
        "alt" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        "win" or "windows" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None
    };
}
