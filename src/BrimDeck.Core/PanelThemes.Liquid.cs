namespace BrimDeck.Core;

// Liquid glass as a base theme of its own (#55): the refraction renderer (BrimDeck.LiquidGlass) is a material only BrimDeck's
// built-in liquid glass themes have, not something any theme gets from its glass. A theme's "glass" effect is the plain blur
// material (Windows' blur under a tint, GlassBackdrop) for every theme, built-in or the user's; liquid glass is the built-in
// liquid-glass-light and liquid-glass-dark, named here in code (no key of the theme file says it, so no file of the user's
// can ask for it), and the automatic "liquid-glass" that shows one of the two. The app's side, the automatic look's state and
// turns, is BrimDeck.LiquidTheme.
public static partial class PanelThemes
{
    // "liquid-glass": liquid glass that takes its light or dark look from what lies behind the island (GlassAppearance). Like
    // "system", it is a choice rather than a theme file, and no theme file can take its id (LoadFolder).
    public const string GlassAuto = "liquid-glass";

    // The backdrop's brightness (sRGB luma, before the glass's own tint) where the liquid glass themes' text keeps 4.5:1 with a
    // little to spare: dark text (#101217) over at least LiquidTextLight, light text (#F6F7F9) over at most LiquidTextDark (with
    // the tints, about .56 and .33). The renderer holds the backdrop to these (LiquidFrame.Preset, Liquid.hlsl's Legibility).
    public const float LiquidTextLight = .52f, LiquidTextDark = .40f;

    // Whether the theme with this id is drawn as liquid glass: only the two built-in liquid glass themes. A user's theme never
    // is, whatever its file says or whichever theme it is based on.
    public static bool IsLiquid(string? id) => NormalizeId(id) is GlassLight or GlassDark && BuiltIn(NormalizeId(id)) is { BuiltIn: true };
    // Whether a palette is drawn as liquid glass: a liquid glass theme's, with its glass.
    public static bool IsLiquid(PanelPalette palette) => palette.IsGlass && IsLiquid(palette.Id);

    public static bool IsGlassAuto(string? id) => NormalizeId(id) == GlassAuto;
    // The theme id automatic liquid glass stands for now (its light or dark look, glassDark); any other id as it is.
    public static string LiquidLook(string id, bool glassDark) => IsGlassAuto(id) ? glassDark ? GlassDark : GlassLight : id;

    // A turn of automatic liquid glass carries a preset clock gradient to the other look's, as Windows switching "system"
    // does (CarryGradient), so the clock stays in the look's own colors (one look's gradients are unreadable on the other's
    // glass). It never asks for the settings to be saved: over a flashing video a turn comes every few seconds, and the look
    // is decided again at every start, so the carried gradient is kept in memory until something else is saved.
    public static void CarryOnGlassTurn(PanelPalette previous, PanelPalette next, DeckSettings settings) => CarryGradient(previous, next, settings);
}
