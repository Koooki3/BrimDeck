using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

// The phone's battery as Phone Link's Start card tells it. Phone Link keeps the card in the app package's local state
// (StartMenu\StartMenuCompanion.json), an Adaptive Card whose texts carry the phone's name, "87%" and the charging state
// in a tooltip; while the phone is away the card says it is disconnected and carries no percentage. Read only: this is
// another app's file, never written here. Parsing is a pure function so the card shapes can be checked without a phone.
public sealed record PhoneLinkReading(string Name, int Percent, bool Charging);

public static class PhoneLinkCard
{
    // Phone Link rewrites the card only when the phone's state changes, so a connected phone that sits idle keeps the same
    // card for hours. Past the first span the card is trusted only while Phone Link itself is running; past the second it is
    // abandoned whatever runs, so a reading never outlives the app by more than this. A card that says the phone is
    // disconnected carries no percentage and clears the reading by itself.
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(2);
    public static readonly TimeSpan Unverified = TimeSpan.FromMinutes(30);

    // Past MaximumAge the card is abandoned whatever runs; past Unverified it stands only while Phone Link is running.
    public static bool Expired(TimeSpan age) => age > MaximumAge;
    public static bool NeedsLiveness(TimeSpan age) => age > Unverified;

    private static readonly string[] TextKeys = ["text", "title", "tooltip", "altText", "speak"];
    // Words of the card's own interface, never a phone's name.
    private static readonly string[] Interface =
    [
        "手机连接", "phone link", "打开", "open", "设置", "settings", "电池", "battery", "蓝牙", "bluetooth",
        "短信", "messages", "呼叫", "call", "照片", "photos", "发送文件", "send files", "最近", "recent", "功能", "features",
        "连接", "connect", "不可用", "unavailable"
    ];
    // The same words ready to match: English ones as whole words, so "call" is not found inside "Callie's" and "open" is
    // found only as a word of its own; Chinese ones as they stand, a run of characters without word breaks.
    private static readonly Regex InterfaceWords = new(@"\b(" + string.Join("|",
        Interface.Where(word => word.All(c => c <= '\u007f')).Select(Regex.Escape)) + @")\b", RegexOptions.CultureInvariant);
    private static readonly string[] InterfaceCjk = Interface.Where(word => word.Any(c => c > '\u007f')).ToArray();
    private static readonly Regex PercentText = new(@"^\s*(\d{1,3})\s*%\s*$", RegexOptions.CultureInvariant);
    private static readonly string[] NotCharging = ["discharging", "not charging", "未充电", "未充電", "不充电", "没有充电", "放电", "放電"];
    private static readonly string[] ChargingWords = ["charging", "正在充电", "正在充電", "充电中", "充電中", "已接通电源", "已接通電源"];

    public static PhoneLinkReading? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return null; }
        using (document)
        {
            var texts = new List<string>();
            var spoken = new List<string>();
            Collect(document.RootElement, texts, spoken);
            int? percent = null;
            foreach (var text in texts) if (Percent(text) is { } value) { percent = value; break; }
            // No percentage: the phone is away, or the card has nothing to say yet. The Bluetooth reading, if any, stands in.
            if (percent is not { } level) return null;
            return new PhoneLinkReading(Name(texts, spoken), level, Charging(texts));
        }
    }

    // Whether the card already read is still the one on disk: Phone Link rewrites the file whenever the phone's state
    // changes, so an unchanged write time means the last reading stands and the file need not be read again.
    public static bool IsCurrent(DateTime stamp, DateTime? read, bool force) => !force && read is { } at && at == stamp;

    private static void Collect(JsonElement element, List<string> texts, List<string> spoken)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && TextKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                        && property.Value.GetString() is { Length: > 0 } text)
                    {
                        texts.Add(text.Trim());
                        if (property.Name.Equals("speak", StringComparison.OrdinalIgnoreCase)) spoken.Add(text.Trim());
                    }
                    else Collect(property.Value, texts, spoken);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Collect(item, texts, spoken);
                break;
        }
    }

    private static int? Percent(string text)
    {
        var match = PercentText.Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            && value is >= 0 and <= 100 ? value : null;
    }

    // The card says what the phone is doing in its tooltips; a negation is checked first, "discharging" carries "charging".
    private static bool Charging(IReadOnlyList<string> texts)
    {
        foreach (var text in texts)
        {
            var lower = text.ToLowerInvariant();
            if (NotCharging.Any(word => lower.Contains(word, StringComparison.Ordinal))) return false;
        }
        foreach (var text in texts)
        {
            var lower = text.ToLowerInvariant();
            if (ChargingWords.Any(word => lower.Contains(word, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    // The name the card shows: the part of its spoken line before the dash ("Xiaomi 13 Ultra - connected to your PC"),
    // else the first text that is not the card's own interface or a figure.
    private static string Name(IReadOnlyList<string> texts, IReadOnlyList<string> spoken)
    {
        foreach (var text in spoken)
        {
            int dash = text.IndexOf(" - ", StringComparison.Ordinal);
            if (dash < 2) dash = text.IndexOf(" – ", StringComparison.Ordinal);
            if (dash >= 2 && Looks(text[..dash].Trim())) return text[..dash].Trim();
        }
        foreach (var text in texts) if (Looks(text)) return text;
        return Loc.T("手机", "Phone");
    }

    private static bool Looks(string text)
    {
        if (text.Length is < 2 or > 60 || Percent(text) is not null) return false;
        var lower = text.ToLowerInvariant();
        // What the phone is doing is said in words of its own ("正在充电", "discharging"), which are never its name.
        if (ChargingWords.Any(word => lower.Contains(word, StringComparison.Ordinal))
            || NotCharging.Any(word => lower.Contains(word, StringComparison.Ordinal))) return false;
        if (InterfaceText(lower)) return false;
        if (text.EndsWith('。') || text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('！')) return false;
        foreach (var c in text) if (char.IsLetterOrDigit(c)) return true;
        return false;
    }

    // Whether the text is the card speaking rather than naming the phone: the words of the interface make up half of it or
    // more. A name that happens to carry one of them stays a name — "OnePlus Open" is a phone, "Open settings" is not.
    private static bool InterfaceText(string lower)
    {
        int letters = 0;
        foreach (var c in lower) if (char.IsLetterOrDigit(c)) letters++;
        int claimed = 0;
        foreach (var word in InterfaceCjk)
            if (lower.Contains(word, StringComparison.Ordinal)) claimed += word.Length;
        foreach (Match match in InterfaceWords.Matches(lower)) claimed += match.Length;
        return claimed > 0 && claimed * 2 >= letters;
    }
}
