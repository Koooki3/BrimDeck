using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

public enum PanelThemeKind { Basic, Extension, Custom }

// One color the theme sets. Minimum is the contrast it must keep against the backgrounds Against names; 0 means not checked.
public sealed record PanelThemeToken(string Key, string Chinese, string English, double Minimum = 0, PanelThemeBackdrop Against = PanelThemeBackdrop.None);
public enum PanelThemeBackdrop { None, Surface, SurfaceAndPopup, Accent, Indicator }

public sealed record PanelThemeInfo(string Id, PanelThemeKind Kind, PanelThemeFile File, string? Path, string? Error)
{
    public bool BuiltIn => Path is null;
    public bool Usable => Error is null;
    public string DisplayName => Loc.IsEnglish && File.NameEn.Length > 0 ? File.NameEn : File.Name.Length > 0 ? File.Name : Id;
    public string DisplayDescription => Loc.IsEnglish && File.DescriptionEn.Length > 0 ? File.DescriptionEn : File.Description;
}

[JsonSerializable(typeof(PanelThemeFile))]
[JsonSerializable(typeof(string))]
internal sealed partial class PanelThemeJson : JsonSerializerContext;

// The panel's color theme: the collapsed notch, capsule and indicator, the quota notice and the expanded panel with its pop-ups.
// A theme is a JSON file in the format of PanelThemeFile. The built-in themes are such files embedded in this assembly
// (Themes/*.json); themes the user adds are files in the data folder's "themes" folder (PanelThemes.Files.cs). Every theme names
// a base theme and sets only what it changes, so a file of a few colors is a complete theme. Readability does not depend on the
// theme: a resolved palette is held to the contrast minimums of Tokens, and any color that misses one is moved, along its own
// hue, just far enough to reach it (PanelThemes.Build.cs).
public static partial class PanelThemes
{
    public const string SystemId = "system", Dark = "dark", Light = "light", Demo = "cyberpunk-edgerunners";
    public const double TextContrast = 4.5, StrongTextContrast = 7, GraphicContrast = 3, IndicatorContrast = 2;

    public static readonly IReadOnlyList<PanelThemeToken> Tokens =
    [
        new("surface", "面板底色", "Panel surface"),
        new("textPrimary", "主要文字", "Primary text", StrongTextContrast, PanelThemeBackdrop.SurfaceAndPopup),
        new("textSecondary", "次要文字", "Secondary text", TextContrast, PanelThemeBackdrop.SurfaceAndPopup),
        new("textTertiary", "辅助文字", "Tertiary text", GraphicContrast, PanelThemeBackdrop.SurfaceAndPopup),
        new("accent", "强调色（键盘焦点、选中日期）", "Accent (keyboard focus, chosen dates)", GraphicContrast, PanelThemeBackdrop.SurfaceAndPopup),
        new("onAccent", "强调色上的文字（选中的日期）", "Text on the accent (chosen dates)", GraphicContrast, PanelThemeBackdrop.Accent),
        new("danger", "错误与暂停同步", "Errors and paused sync", TextContrast, PanelThemeBackdrop.SurfaceAndPopup),
        new("liked", "已喜欢", "Liked song", GraphicContrast, PanelThemeBackdrop.Surface),
        new("clock", "时间默认色", "Default clock color", TextContrast, PanelThemeBackdrop.Surface),
        new("equalizer", "律动条默认色", "Default equalizer color", GraphicContrast, PanelThemeBackdrop.Surface),
        new("hairline", "分隔线", "Dividers"),
        new("track", "进度条底轨", "Progress track"),
        new("ringTrack", "配额环底轨", "Quota ring track"),
        new("fill", "分段控件底色", "Segment background"),
        new("fillSelected", "选中项底色", "Selected segment"),
        new("hover", "悬停底色", "Hover"),
        new("pressed", "按下底色", "Pressed"),
        new("outline", "描边", "Outlines"),
        new("popup", "弹出层底色", "Pop-up surface"),
        new("popupBorder", "弹出层描边", "Pop-up border"),
        new("input", "输入框底色", "Input field"),
        new("rangeFill", "日期范围底色", "Date range fill"),
        new("coverPlaceholder", "无封面占位", "Missing cover"),
        new("coverOutline", "封面描边", "Cover outline"),
        new("scrollThumb", "滚动条", "Scroll bar"),
        new("rim", "边缘描线", "Rim"),
        new("shadow", "阴影或辉光", "Shadow or glow"),
        new("indicator", "指示条", "Indicator"),
        new("indicatorProgress", "指示条播放进度", "Indicator progress", IndicatorContrast, PanelThemeBackdrop.Indicator),
        new("switchFill", "页面切换底色", "Page switch background"),
        new("seekRail", "播放进度底轨", "Seek bar track"),
        new("rowDivider", "明细行分隔线", "Detail row dividers"),
        new("rowHover", "明细行悬停", "Detail row hover"),
        new("menuDetail", "菜单中的歌名", "Song titles in menus", GraphicContrast, PanelThemeBackdrop.SurfaceAndPopup),
        new("tooltip", "提示框底色", "Tooltip surface"),
        new("tooltipBorder", "提示框描边", "Tooltip border"),
        new("bubble", "进度时间气泡", "Seek time bubble"),
        new("bubbleBorder", "进度时间气泡描边", "Seek time bubble border"),
    ];
    // Applications keyed by the names the "apps" object uses. Sources entered by hand share "manual".
    public static readonly IReadOnlyList<string> AppKeys = ["claude", "codex", "antigravity", "cursor", "zcode", "dsh", "grokBot", "kimi", "manual", "other", "warning", "critical"];
    public static string AppKey(ProviderId id) => id switch
    {
        ProviderId.Claude => "claude", ProviderId.Codex => "codex", ProviderId.Antigravity => "antigravity", ProviderId.Cursor => "cursor",
        ProviderId.ZCode => "zcode", ProviderId.Dsh => "dsh", ProviderId.GrokBot => "grokBot", ProviderId.Kimi => "kimi",
        ProviderId.GlmChina or ProviderId.GlmGlobal or ProviderId.DeepSeek or ProviderId.KimiApi or ProviderId.NewApi or ProviderId.Sub2Api or ProviderId.Custom => "manual",
        _ => "other"
    };

    // Source-generated metadata, so the first theme read at startup does not build it by reflection.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, RespectNullableAnnotations = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = PanelThemeJson.Default
    };
    public static PanelThemeFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<PanelThemeFile>(json, Json) ?? throw new JsonException("The theme file is empty.");
        // Properties are checked for null by the serializer; values inside the colors, apps and gradient collections are not.
        static bool HasNull(IEnumerable<string?>? values) => values?.Any(value => value is null) == true;
        if (HasNull(file.Colors?.Values) || HasNull(file.Apps?.Values) || HasNull(file.Swatches)
            || HasNull(file.Effects?.SurfaceGradient) || HasNull(file.Effects?.RimGradient)
            || HasNull(file.Clock?.Gradient) || HasNull(file.Clock?.Palette?.Values) || file.Clock?.Gradients?.Any(gradient => gradient is null || HasNull(gradient.Stops)) == true)
            throw new JsonException(Loc.T("颜色值不能为 null", "A color cannot be null"));
        return file;
    }

    // Built-in themes are read on demand: startup reads only the chosen theme and its base themes. The id of each is its resource
    // name, which a test keeps equal to the id inside the file.
    private const string ThemeResources = ".Themes.";
    // Dark and light first, then the ported editor themes, then the custom demo.
    private static readonly string[] BuiltInOrder = [Dark, Light, "one-dark-pro", "github-dark", "github-light", "dracula", "night-owl", Demo];
    private static (string Id, string Resource)[]? _builtInIndex;
    private static (string Id, string Resource)[] BuiltInIndex => _builtInIndex ??= [.. typeof(PanelThemes).Assembly.GetManifestResourceNames()
        .Where(name => name.Contains(ThemeResources, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
        .Select(name => (Id: NormalizeId(name[(name.IndexOf(ThemeResources, StringComparison.Ordinal) + ThemeResources.Length)..^".json".Length]), Resource: name))
        .OrderBy(entry => Array.IndexOf(BuiltInOrder, entry.Id) is var i and >= 0 ? i : int.MaxValue).ThenBy(entry => entry.Id, StringComparer.Ordinal)];
    private static readonly Dictionary<string, PanelThemeInfo> ParsedBuiltIns = new(StringComparer.Ordinal);
    public static bool IsBuiltIn(string id) => BuiltInIndex.Any(entry => entry.Id == id);
    public static PanelThemeInfo? BuiltIn(string id)
    {
        if (ParsedBuiltIns.TryGetValue(id, out var known)) return known;
        foreach (var (key, resource) in BuiltInIndex)
        {
            if (key != id) continue;
            // The demo theme sits with the extensions, so the custom section holds only the user's own themes.
            var kind = id is Dark or Light ? PanelThemeKind.Basic : PanelThemeKind.Extension;
            return ParsedBuiltIns[id] = new PanelThemeInfo(id, kind, Parse(Resource(resource)), null, null);
        }
        return null;
    }
    private static IReadOnlyList<PanelThemeInfo>? _builtIns;
    public static IReadOnlyList<PanelThemeInfo> BuiltIns => _builtIns ??= [.. BuiltInIndex.Select(entry => BuiltIn(entry.Id)!)];
    private static string Resource(string name)
    {
        using var stream = typeof(PanelThemes).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"[^a-z0-9._-]+")] private static partial Regex IdSeparators();
    public static string NormalizeId(string? id)
    {
        var text = IdSeparators().Replace((id ?? "").Trim().ToLowerInvariant(), "-").Trim('-', '.');
        return text.Length > 64 ? text[..64] : text;
    }

    // The theme an id stands for, among the built-in themes and the given user themes. "system" follows the Windows app theme.
    // An id that names nothing, such as a theme whose file was removed, falls back to the dark theme; the setting is kept.
    public static PanelThemeInfo Find(string id, bool systemDark, IReadOnlyList<PanelThemeInfo> user)
    {
        id = NormalizeId(id);
        if (id == SystemId) id = systemDark ? Dark : Light;
        return BuiltIn(id) ?? user.FirstOrDefault(theme => theme.Usable && theme.Id == id) ?? BuiltIn(Dark)!;
    }
}
