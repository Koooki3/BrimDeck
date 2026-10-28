namespace BrimDeck.Core;

// How the power reading looks in a panel theme: the shape of its marks, the shape of the curve and its color. Every built-in theme has a
// look of its own (PowerLooks.Table); a theme without one takes the closest by what it is made of (pixel art). A new theme adds one row
// to the table, and the theme tests (ThemeCoverageTests) then hold its colors to the same contrast as the rest.

// The shapes of the marks: Pill (rounded outlines), Round (the island's soft, heavier line), Chamfer (HUD cuts across two corners),
// Blocks (stepped squares, as pixel art).
public enum PowerGauge { Pill, Round, Chamfer, Blocks }

// The curve: Smooth (the monitor's own), Wave (smooth, over a deeper fill), Sharp (straight segments, a bright head), Steps (stairs, as
// pixels), Fine (a hairline with the faintest fill).
public enum PowerCurve { Smooth, Wave, Sharp, Steps, Fine }

// A color is named by what it takes from: a monitor color scheme role ("cpu", "efficiency", "memory", "gpu", "warning", "critical")
// or a theme token ("accent", "liked", "equalizer", "clock", "textPrimary", "textTertiary"). Draw is the power curve and the bolt badge:
// a cool color, never the warning or critical color (PowerTests holds every theme to it).
public sealed record PowerLook(PowerGauge Gauge, PowerCurve Curve, string Draw)
{
    // The names a look may use; the theme tests resolve each on every theme.
    public static readonly IReadOnlyList<string> Roles = ["cpu", "efficiency", "memory", "gpu", "warning", "critical"];
    public static readonly IReadOnlyList<string> Tokens = ["accent", "liked", "equalizer", "clock", "textPrimary", "textTertiary"];
    public IEnumerable<string> References => [Draw];
}

public static class PowerLooks
{
    public static readonly PowerLook Default = new(PowerGauge.Pill, PowerCurve.Smooth, "accent");

    // High contrast: text colors only, so the system's colors carry everything.
    public static readonly PowerLook HighContrast = new(PowerGauge.Pill, PowerCurve.Fine, "textPrimary");

    // Built-in themes by id. The plain themes (dark, light, GitHub, Dracula, Night Owl, One Dark Pro) share the default.
    //   Cyberpunk: HUD-cut marks with a sharp scope-like curve with a bright head, in neon cyan.
    //   Island Holiday (both): rounder, heavier marks and a wave curve over a deeper fill, in the sea's own color.
    //   Pixel Blocks: marks in 8 by 8 squares and a stair curve, in the chat palette's cyan.
    // The curve takes the theme's accent or its own cool color: the monitor scheme's efficiency color can land on a theme's warning or
    // critical color, and orange and red belong to the low levels alone.
    private static readonly Dictionary<string, PowerLook> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        [PanelThemes.Dark] = Default,
        [PanelThemes.Light] = Default,
        ["cyberpunk-edgerunners"] = new(PowerGauge.Chamfer, PowerCurve.Sharp, "accent"),
        [PanelThemes.IslandHoliday] = new(PowerGauge.Round, PowerCurve.Wave, "equalizer"),
        [PanelThemes.IslandHoliday2] = new(PowerGauge.Round, PowerCurve.Wave, "equalizer"),
        [PanelThemes.PixelBlocks] = new(PowerGauge.Blocks, PowerCurve.Steps, "equalizer"),
    };

    // The built-in themes with a look of their own.
    public static IReadOnlyCollection<string> Ids => Table.Keys;

    // The look for a palette.
    public static PowerLook For(PanelPalette palette, bool highContrast = false)
    {
        if (highContrast) return HighContrast;
        if (Table.TryGetValue(palette.Id, out var known)) return known;
        // A theme of the user's, or one added later: by what it is made of.
        if (palette.Effects.Pixel > 0) return Table[PanelThemes.PixelBlocks];
        return Default;
    }
}
