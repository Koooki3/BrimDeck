namespace BrimDeck.Core;

// A panel theme file as JSON (see PanelThemes). Property names are camelCase in the file; unknown properties are ignored, and
// comments and trailing commas are allowed. Everything left out comes from the base theme.
public sealed class PanelThemeFile
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string Description { get; set; } = "";
    public string DescriptionEn { get; set; } = "";
    public string Author { get; set; } = "";
    public string License { get; set; } = "";
    public string Homepage { get; set; } = "";
    // A built-in theme id. Unset means "dark".
    public string Base { get; set; } = "";
    public Dictionary<string, string>? Colors { get; set; }
    public Dictionary<string, string>? Apps { get; set; }
    public PanelThemeEffects? Effects { get; set; }
    // The clock style and color that suit the theme; choosing the theme in settings switches the clock to them.
    public PanelThemeClock? Clock { get; set; }
    // The twelve preset colors of the color picker in settings. Unlike everything else these are not inherited: a theme without
    // its own list offers its own app, warning, critical and clock colors (see PanelPalette.Swatches).
    public List<string>? Swatches { get; set; }
}

public sealed class PanelThemeClock
{
    // A ClockStyle name: minimal, stacked, digits, dots, segment, neon, serif, wide, condensed, pixel.
    public string Style { get; set; } = "";
    // "default" (the theme's clock color), amber, cyan, pink, lime, custom, gradient or cover.
    public string Color { get; set; } = "";
    public string Custom { get; set; } = "";
    public List<string>? Gradient { get; set; }
    // The colors of the amber, cyan, pink and lime swatches in this theme, by those names.
    public Dictionary<string, string>? Palette { get; set; }
    // The preset gradients this theme offers in place of the built-in five.
    public List<PanelThemeGradient>? Gradients { get; set; }
}

public sealed class PanelThemeGradient
{
    public string Name { get; set; } = "";
    public string NameEn { get; set; } = "";
    public List<string> Stops { get; set; } = [];
    public string DisplayName => Loc.IsEnglish && NameEn.Length > 0 ? NameEn : Name;
}

public sealed class PanelThemeEffects
{
    // Two or more colors laid over the surface from top to bottom.
    public List<string>? SurfaceGradient { get; set; }
    // Two or more colors for the rim from left to right; replaces the single "rim" color.
    public List<string>? RimGradient { get; set; }
    public double? RimWidth { get; set; }
    // The shadow under the collapsed notch and capsule; a colored shadow with no depth reads as a glow.
    public double? ShadowBlur { get; set; }
    public double? ShadowDepth { get; set; }
    public double? ShadowOpacity { get; set; }
    // Horizontal scan lines over the surface, as an opacity of the primary text color.
    public double? Scanlines { get; set; }
    // Pixel art: corners become steps of this many DIP (1 to 4), the rim a bevel and the text unsmoothed. 0 turns it off.
    public double? Pixel { get; set; }
    // A pixel theme's block texture: squares of the pixel size a little lighter or darker than the surface, at most this opacity
    // (0 to 0.12), as a stone or dirt block is mottled. Without pixel art there are no blocks to mottle, so it is ignored.
    public double? Texture { get; set; }
}
