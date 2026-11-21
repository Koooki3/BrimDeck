using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core;

// How a theme answers the pointer on BrimDeck's own panel: a mark on the control the pointer hovers, drawn on the control's
// own visible shape (InteractionFx in the window project). The file's "motion" section is merged field by field along the
// base chain, like the effects, and every value is held to MotionSafety's limits. Nothing here runs by itself: BrimDeck only
// animates while the app-wide rule allows it (Soft.Enabled), and the mark is off unless a theme turns it on: no built-in theme
// has a motion section, and one without draws nothing over the controls, which keep their own hover looks.

// The mark on a hovered control. reveal: a faint fill and a 1 px edge lit from the top (Fluent); lift: a soft shadow outside
// the shape and a light top edge (a light theme's raised card); underline: a subtle fill and a 2 px bar along the bottom (Primer).
public enum HoverFx { None, Reveal, Lift, Underline }

// Style names in theme files.
public static class MotionStyles
{
    public static HoverFx Hover(string? value, HoverFx fallback) => TryHover(value, out var style) ? style : fallback;

    // A name only: numbers ("2") are not styles, whatever the enum would make of them.
    public static bool TryHover(string? value, out HoverFx style)
    {
        style = HoverFx.None;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        return char.IsLetter(value[0]) && Enum.TryParse(value, true, out style) && Enum.IsDefined(style);
    }
}

// Colors are theme color keys ("accent", "rim", "shadow", ...) or "#RRGGBB" colors. Color is the main mark; Secondary its
// second tone (an underline's bar, a lift's shadow). Pace scales the hover durations.
public sealed record ThemeMotion(HoverFx Hover, double HoverStrength, double Pace, string Color, string Secondary)
{
    // Without a motion section anywhere in the base chain nothing is drawn over the controls.
    public static readonly ThemeMotion Default = new(HoverFx.None, .12, 1, "accent", "accent");
    public bool AnyInteraction => Hover != HoverFx.None && HoverStrength > 0;

    // A motion color as the palette draws it: a key of the palette or a color (the palette's accent when unknown). Kept
    // readable on the surface like any color chosen outside the theme.
    public static ThemeColor ResolveColor(PanelPalette palette, string value)
    {
        ThemeColor color;
        if (palette.Colors.Keys.FirstOrDefault(key => string.Equals(key, value, StringComparison.OrdinalIgnoreCase)) is { } key) color = palette[key];
        else if (!ThemeColor.TryParse(value, out color)) color = palette["accent"];
        return palette.Readable(color.Over(palette.Surface), PanelThemes.GraphicContrast);
    }
}

// The limits every theme is held to.
public static class MotionSafety
{
    public const double HoverMax = .3, PaceMin = .6, PaceMax = 1.6;

    public static double Amount(double? value, double max, double fallback) => value is { } v && double.IsFinite(v) ? Math.Clamp(v, 0, max) : fallback;
    public static double Pace(double? value, double fallback) => value is { } v && double.IsFinite(v) ? Math.Clamp(v, PaceMin, PaceMax) : fallback;
}

// How strongly parts of a hover mark are drawn (InteractionFx.Draw), from the theme's strength and the mark's presence (0 to 1).
public static class HoverMarkOpacity
{
    // lift: the light top edge of a raised card on a light theme. It fades with the strength as the shadow does, to nothing at
    // zero, and reaches 0.9 only at the strongest a theme may be.
    public static double LiftHighlight(double strength, double presence)
        => .9 * Math.Clamp(strength, 0, MotionSafety.HoverMax) / MotionSafety.HoverMax * Math.Clamp(presence, 0, 1);
}

public static class MotionGate
{
    // allowed: the app-wide rule (MotionConditions.Allowed, which Soft.Enabled applies).
    public static bool Interaction(bool allowed, ThemeMotion motion) => allowed && motion.AnyInteraction;
}

// The hover durations in milliseconds: a mark arrives in the monitor page's hover time (Eased.Hover) times the theme's pace and
// leaves in seven tenths of it (Eased.HoverOut). Glide: a mark moving over to a neighboring control.
public readonly record struct MotionTiming(int HoverIn, int HoverOut, int Glide)
{
    public static MotionTiming For(double pace)
    {
        double p = Math.Clamp(double.IsFinite(pace) ? pace : 1, MotionSafety.PaceMin, MotionSafety.PaceMax);
        int hover = Ms(Eased.Hover * 1000 * p, 80, 450);
        return new(hover, Ms(hover * Eased.HoverOut / Eased.Hover, 56, 315), Ms(GlideBase * p, 90, 600));
    }

    // The default animation length (360 ms) times six tenths.
    private const double GlideBase = 360 * .6;
    private static int Ms(double value, double min, double max) => (int)Math.Round(Math.Clamp(value, min, max));
}

// Progress along a timeline.
public static class MotionCurve
{
    public static double Linear(double elapsed, double duration) => duration <= 0 ? 1 : Math.Clamp(elapsed / duration, 0, 1);
    // The soft landing the rest of BrimDeck uses.
    public static double Ease(double t) => Curves.Bezier(.22, .8, .28, 1, Math.Clamp(t, 0, 1));
    // A hover mark leaving: the same landing. Its flat end is taken as the end, so the mark is gone exactly when its time is up
    // rather than a rounding error later.
    public static double HoverOut(double t) { double e = Ease(t); return e > 1 - 1e-9 ? 1 : e; }
}

// One control's hover mark over time, on one clock in seconds. It arrives from `from` (0, or where a fading mark of the same
// control was), holds while hovered, and leaves from where it was. Nothing here reads a clock: the caller passes the time.
public sealed class HoverTimeline
{
    public double ShownAt { get; private set; } = double.NaN;
    public double ShownFrom { get; private set; }
    public double LeftAt { get; private set; } = double.NaN;
    public double LeftFrom { get; private set; }
    public bool Leaving => !double.IsNaN(LeftAt);

    public void Arrive(double now, double from) { ShownAt = now; ShownFrom = Math.Clamp(from, 0, 1); LeftAt = double.NaN; }
    public void Leave(double now, double presence) { LeftFrom = Math.Clamp(presence, 0, 1); LeftAt = now; }

    // 0 to 1: how much of the mark shows.
    public double Presence(double now, MotionTiming timing)
    {
        if (Leaving) return LeftFrom * (1 - MotionCurve.HoverOut(MotionCurve.Linear(now - LeftAt, timing.HoverOut / 1000.0)));
        if (double.IsNaN(ShownAt)) return 0;
        return ShownFrom + (1 - ShownFrom) * MotionCurve.Ease(MotionCurve.Linear(now - ShownAt, timing.HoverIn / 1000.0));
    }

    // Whether the mark is still changing: arriving, or leaving and not yet gone.
    public bool Moving(double now, MotionTiming timing)
        => Leaving ? Presence(now, timing) > 0 : !double.IsNaN(ShownAt) && now - ShownAt < timing.HoverIn / 1000.0;
}

// The "motion" section of a theme file. Everything is optional and comes from the base theme when left out. A value BrimDeck
// cannot use (a word where a number goes, a style it does not know) is left out too and listed as a warning (PanelThemes.Warnings):
// a hover mark is never worth refusing the whole theme, its colors and its other look included.
[JsonConverter(typeof(PanelThemeMotionConverter))]
public sealed class PanelThemeMotion
{
    // none, reveal, lift, underline (HoverFx)
    public string? Hover { get; set; }
    // 0 to 0.3
    public double? HoverStrength { get; set; }
    // 0.6 to 1.6: scales how long the mark takes
    public double? Pace { get; set; }
    // A theme color key or "#RRGGBB"
    public string? Color { get; set; }
    public string? Secondary { get; set; }
    // The values read from the file and left out, each with why. Not written back.
    [JsonIgnore] public List<string> Problems { get; } = [];
}

// Reads the motion section field by field: a field of the wrong type is left out with a problem rather than failing the file.
internal sealed class PanelThemeMotionConverter : JsonConverter<PanelThemeMotion>
{
    public override PanelThemeMotion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var motion = new PanelThemeMotion();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            string raw = Raw(ref reader);
            motion.Problems.Add(Loc.T($"motion 应为对象，现在是 {raw}，已忽略", $"\"motion\" must be an object, not {raw}; it is ignored"));
            return motion;
        }
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string name = reader.GetString() ?? "";
            reader.Read();
            switch (name.ToLowerInvariant())
            {
                case "hover": motion.Hover = Text(ref reader, "hover", motion.Problems); break;
                case "hoverstrength": motion.HoverStrength = Number(ref reader, "hoverStrength", motion.Problems); break;
                case "pace": motion.Pace = Number(ref reader, "pace", motion.Problems); break;
                case "color": motion.Color = Text(ref reader, "color", motion.Problems); break;
                case "secondary": motion.Secondary = Text(ref reader, "secondary", motion.Problems); break;
                // Members of later versions (press, focus) are skipped.
                default: reader.Skip(); break;
            }
        }
        return motion;
    }

    private static string? Text(ref Utf8JsonReader reader, string field, List<string> problems)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        if (reader.TokenType != JsonTokenType.Null) problems.Add(Wrong(field, Raw(ref reader), Loc.T("文本", "text")));
        return null;
    }

    private static double? Number(ref Utf8JsonReader reader, string field, List<string> problems)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value)) return value;
        if (reader.TokenType != JsonTokenType.Null) problems.Add(Wrong(field, Raw(ref reader), Loc.T("数字", "a number")));
        return null;
    }

    private static string Wrong(string field, string raw, string kind)
        => Loc.T($"动效项“{field}”的值 {raw} 不是{kind}，已忽略", $"The motion value \"{field}\" is {raw}, not {kind}; it is ignored");

    // The value as written, shortened; reading it moves the reader to the value's end.
    private static string Raw(ref Utf8JsonReader reader)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        string raw = value.RootElement.GetRawText();
        return raw.Length > 40 ? raw[..40] + "…" : raw;
    }

    public override void Write(Utf8JsonWriter writer, PanelThemeMotion value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Hover is { } hover) writer.WriteString("hover", hover);
        if (value.HoverStrength is { } strength) writer.WriteNumber("hoverStrength", strength);
        if (value.Pace is { } pace) writer.WriteNumber("pace", pace);
        if (value.Color is { } color) writer.WriteString("color", color);
        if (value.Secondary is { } secondary) writer.WriteString("secondary", secondary);
        writer.WriteEndObject();
    }
}

public static partial class PanelThemes
{
    // What a usable file has wrong that is left out rather than refused, for the settings page: the motion values of each look.
    public static IReadOnlyList<string> Warnings(PanelThemeFile file)
    {
        var warnings = new List<string>();
        void Look(PanelThemeFile? look, string prefix)
        {
            if (look?.Motion is not { } motion) return;
            warnings.AddRange(motion.Problems.Select(problem => prefix + problem));
            if (motion.Hover is { } hover && !string.IsNullOrWhiteSpace(hover) && !MotionStyles.TryHover(hover, out _))
                warnings.Add(prefix + Loc.T($"未知的悬停样式“{hover}”（可用：none、reveal、lift、underline），已忽略", $"Unknown hover style \"{hover}\" (use none, reveal, lift or underline); it is ignored"));
        }
        Look(file, "");
        Look(file.Night, Loc.T("夜间外观：", "Night look: "));
        Look(file.Dusk, Loc.T("黄昏外观：", "Dusk look: "));
        return warnings;
    }

    // The base chain's motion sections, merged from the dark theme up and held to MotionSafety.
    private static ThemeMotion MotionValues(IEnumerable<PanelThemeFile> chain)
    {
        var m = ThemeMotion.Default;
        foreach (var layer in chain)
        {
            if (layer.Motion is not { } motion) continue;
            m = m with
            {
                Hover = MotionStyles.Hover(motion.Hover, m.Hover),
                HoverStrength = MotionSafety.Amount(motion.HoverStrength, MotionSafety.HoverMax, m.HoverStrength),
                Pace = MotionSafety.Pace(motion.Pace, m.Pace),
                Color = motion.Color is { Length: > 0 } color && color.Trim() is { Length: > 0 } c ? c : m.Color,
                Secondary = motion.Secondary is { Length: > 0 } secondary && secondary.Trim() is { Length: > 0 } s ? s : m.Secondary
            };
        }
        return m;
    }
}
