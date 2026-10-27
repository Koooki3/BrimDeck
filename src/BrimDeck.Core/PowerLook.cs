namespace BrimDeck.Core;

// How the power and battery readings look in a panel theme: the shape of the battery, the shape of the curve and the colors of its
// states. Every built-in theme has a look of its own (PowerLooks.Table); a theme without one takes the closest by what it is made
// of (pixel art, glass, HUD cuts). A new theme adds one row to the table, and the theme tests (ThemeCoverageTests) then hold its
// colors to the same contrast as the rest.

// The battery's body: Pill (a rounded outline with a tip), Round (a capsule), Chamfer (HUD cuts across two corners and a tick for
// the tip), Blocks (stepped squares, a segment per tenth), Glass (a thin outline over a translucent fill with a light edge).
public enum PowerGauge { Pill, Round, Chamfer, Blocks, Glass }

// The curve: Smooth (the monitor's own), Wave (smooth, over a deeper fill), Sharp (straight segments, a bright head), Steps (stairs, as
// pixels), Fine (a hairline with the faintest fill).
public enum PowerCurve { Smooth, Wave, Sharp, Steps, Fine }

// A color is named by what it takes from: a monitor color scheme role ("cpu", "efficiency", "memory", "gpu", "warning", "critical")
// or a theme token ("accent", "liked", "equalizer", "clock", "textPrimary", "textTertiary"). Normal fills a battery on its own, Charge
// while charging, Saver is the mark and outline of Battery saver, Draw the power curve.
// Normal is the text color or a cool one: orange and red belong to the low levels and to nothing else (PowerTests holds every theme to it).
public sealed record PowerLook(PowerGauge Gauge, PowerCurve Curve, string Normal, string Charge, string Saver, string Draw)
{
    // The names a look may use; the theme tests resolve each on every theme.
    public static readonly IReadOnlyList<string> Roles = ["cpu", "efficiency", "memory", "gpu", "warning", "critical"];
    public static readonly IReadOnlyList<string> Tokens = ["accent", "liked", "equalizer", "clock", "textPrimary", "textTertiary"];
    public IEnumerable<string> References => new[] { Normal, Charge, Saver, Draw }.Distinct();
}

public static class PowerLooks
{
    public static readonly PowerLook Default = new(PowerGauge.Pill, PowerCurve.Smooth, "textPrimary", "efficiency", "efficiency", "efficiency");

    // High contrast: outlines and text colors only, so the system's colors carry everything; the states are told by the mark and the
    // words (bolt, exclamation, leaf), never by color alone.
    public static readonly PowerLook HighContrast = new(PowerGauge.Pill, PowerCurve.Fine, "textPrimary", "textPrimary", "textPrimary", "textPrimary");

    // Built-in themes by id. The plain themes (dark, light, GitHub, Dracula, Night Owl, One Dark Pro) share the default.
    //   Cyberpunk: HUD-cut battery with neon cyan, charging in the pale glow of a lit window (the magenta is the curve's, and the low battery's red is not to be mistaken for it), a sharp scope-like curve with a bright head.
    //   Island Holiday (both): a capsule like a lagoon bubble, charging in the sea's own color, a wave curve over a deeper fill.
    //   Pixel Blocks: ten stepped squares in the bright chat palette, charging cyan, a stair curve.
    //   Liquid Glass: a thin glass body with a light edge, a hairline curve.
    private static readonly Dictionary<string, PowerLook> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        [PanelThemes.Dark] = Default,
        [PanelThemes.Light] = Default,
        ["cyberpunk-edgerunners"] = new(PowerGauge.Chamfer, PowerCurve.Sharp, "accent", "clock", "clock", "liked"),
        [PanelThemes.Endfield] = new(PowerGauge.Chamfer, PowerCurve.Sharp, "textPrimary", "efficiency", "efficiency", "efficiency"),
        [PanelThemes.IslandHoliday] = new(PowerGauge.Round, PowerCurve.Wave, "textPrimary", "equalizer", "equalizer", "equalizer"),
        [PanelThemes.IslandHoliday2] = new(PowerGauge.Round, PowerCurve.Wave, "textPrimary", "equalizer", "equalizer", "equalizer"),
        [PanelThemes.PixelBlocks] = new(PowerGauge.Blocks, PowerCurve.Steps, "textPrimary", "equalizer", "equalizer", "equalizer"),
        [PanelThemes.GlassLight] = new(PowerGauge.Glass, PowerCurve.Fine, "textPrimary", "efficiency", "efficiency", "accent"),
        [PanelThemes.GlassDark] = new(PowerGauge.Glass, PowerCurve.Fine, "textPrimary", "efficiency", "efficiency", "accent"),
    };

    // The built-in themes with a look of their own.
    public static IReadOnlyCollection<string> Ids => Table.Keys;

    // The look for a palette. A daylight theme's night and dusk looks (ids carry the suffix in places) take their theme's.
    public static PowerLook For(PanelPalette palette, bool highContrast = false)
    {
        if (highContrast) return HighContrast;
        string id = palette.Id;
        foreach (var suffix in new[] { "-night", "-dusk", "-day" })
            if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { id = id[..^suffix.Length]; break; }
        if (Table.TryGetValue(id, out var known)) return known;
        // A theme of the user's, or one added later: by what it is made of.
        if (palette.Effects.Pixel > 0) return Table[PanelThemes.PixelBlocks];
        if (palette.Effects.Glass is not null) return Table[palette.IsDark ? PanelThemes.GlassDark : PanelThemes.GlassLight];
        if (palette.Effects.Decor?.Hud is not null) return Table["cyberpunk-edgerunners"];
        return Default;
    }
}
