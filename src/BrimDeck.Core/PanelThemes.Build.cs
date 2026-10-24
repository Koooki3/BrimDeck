using System.Runtime.CompilerServices;

namespace BrimDeck.Core;

// From a theme file to a palette: the base chain is merged, the effects are fitted to their ranges, and every color with a
// contrast minimum is brought up to it.
public static partial class PanelThemes
{
    // Built-in palettes never change. A user file is resolved once per reading: an edited file is read again into a new entry.
    private static readonly Dictionary<string, PanelPalette> ResolvedBuiltIns = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<PanelThemeInfo, PanelPalette> ResolvedFiles = new();
    public static PanelPalette Resolve(PanelThemeInfo theme)
    {
        if (theme.BuiltIn ? ResolvedBuiltIns.TryGetValue(theme.Id, out var cached) : ResolvedFiles.TryGetValue(theme, out cached)) return cached;
        var palette = Build(theme.Id, theme.File);
        if (theme.BuiltIn) ResolvedBuiltIns[theme.Id] = palette; else ResolvedFiles.AddOrUpdate(theme, palette);
        return palette;
    }

    public static PanelPalette Build(string id, PanelThemeFile file)
    {
        var colors = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
        var apps = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
        var effects = new PanelThemeEffects();
        PanelThemeClock? clock = null;
        foreach (var layer in BaseChain(file))
        {
            foreach (var (key, value) in layer.Colors ?? [])
                if (Tokens.FirstOrDefault(token => string.Equals(token.Key, key, StringComparison.OrdinalIgnoreCase)) is { } token && ThemeColor.TryParse(value, out var color)) colors[token.Key] = color;
            foreach (var (key, value) in layer.Apps ?? [])
                if (AppKeys.FirstOrDefault(app => string.Equals(app, key, StringComparison.OrdinalIgnoreCase)) is { } app && ThemeColor.TryParse(value, out var color)) apps[app] = color;
            if (layer.Clock is { } layerClock && ClockProblem(layerClock) is null) clock = MergeClock(clock, layerClock);
            if (layer.Effects is { } layerEffects) MergeEffects(effects, layerEffects);
        }
        // The dark theme sets every token, so a missing one only happens if the embedded file is broken.
        foreach (var token in Tokens) if (!colors.ContainsKey(token.Key)) colors[token.Key] = token.Key == "surface" ? ThemeColor.Black : ThemeColor.White;
        var writtenColors = new Dictionary<string, ThemeColor>(colors, StringComparer.Ordinal);
        var writtenApps = new Dictionary<string, ThemeColor>(apps, StringComparer.Ordinal);
        // The surface is drawn opaque, as a translucent one would let the desktop through behind the text. A tooltip is a window of
        // its own and the seek bubble floats over lyrics and times, so both are drawn as they look over the surface.
        var surface = colors["surface"] = colors["surface"].Over(ThemeColor.Black);
        colors["tooltip"] = colors["tooltip"].Over(surface);
        colors["bubble"] = colors["bubble"].Over(surface);
        var values = EffectValues(effects);
        List<ThemeColor> surfaces = [surface, .. values.SurfaceGradient.Select(stop => stop.Over(surface))];

        var adjustments = new List<PanelThemeAdjustment>();
        var popup = colors["popup"].Over(surface);
        foreach (var token in Tokens.Where(token => token.Minimum > 0))
        {
            List<ThemeColor> against = token.Against switch
            {
                PanelThemeBackdrop.Surface => surfaces,
                PanelThemeBackdrop.SurfaceAndPopup => [.. surfaces, popup],
                PanelThemeBackdrop.Accent => [colors["accent"].Over(popup)],
                PanelThemeBackdrop.Indicator => [colors["indicator"].Over(ThemeColor.Black)],
                _ => []
            };
            var needs = against.Select(background => new ContrastNeed(background, token.Minimum)).ToList();
            // Text also stands on fills: the primary text on the selected segment, a pressed button, an input field, the date range,
            // tooltips and the seek time bubble; the secondary text on the segment strip and on hover. Those need the body-text minimum.
            List<ThemeColor> fills = token.Key switch
            {
                "textPrimary" => [colors["fillSelected"].Over(surface), colors["pressed"].Over(surface), colors["input"].Over(popup), colors["rangeFill"].Over(popup),
                    colors["tooltip"], colors["bubble"]],
                "textSecondary" => [colors["fill"].Over(surface), colors["hover"].Over(surface)],
                _ => []
            };
            needs.AddRange(fills.Select(fill => new ContrastNeed(fill, TextContrast)));
            Ensure(colors, token.Key, needs, adjustments);
        }
        // App colors are opaque, as the color picker and the settings store them, and read on every surface stop.
        var graphic = surfaces.Select(background => new ContrastNeed(background, GraphicContrast)).ToList();
        foreach (var key in apps.Keys.ToList())
        {
            apps[key] = apps[key].Over(surface);
            Ensure(apps, key, graphic, adjustments);
        }
        // A theme's own picker colors are offered as the panel draws them, like the ones derived from its palette.
        var swatches = file.Swatches?.Select(value => ThemeColor.TryParse(value, out var color) ? color.Over(surface).EnsureContrast(graphic) : (ThemeColor?)null)
            .OfType<ThemeColor>().Take(12).ToList();
        return new PanelPalette(id, colors, apps, values, adjustments, clock, swatches, writtenColors, writtenApps);
    }

    // The file and its base themes, from the dark theme up to the file. The length bound guards against a cycle.
    private static List<PanelThemeFile> BaseChain(PanelThemeFile file)
    {
        var chain = new List<PanelThemeFile> { file };
        var baseId = NormalizeId(file.Base);
        while (chain.Count <= 8)
        {
            if (baseId.Length == 0) baseId = Dark;
            if (BuiltIn(baseId) is not { } parent || chain.Contains(parent.File)) break;
            chain.Add(parent.File);
            if (baseId == Dark) break;
            baseId = NormalizeId(parent.File.Base);
        }
        chain.Reverse();
        return chain;
    }

    // Field by field, like the colors: a file that names only a style keeps its base's clock color. Colors are kept opaque, in the
    // "#RRGGBB" form the settings store, so they can be compared with what the settings hold.
    private static PanelThemeClock MergeClock(PanelThemeClock? inherited, PanelThemeClock layer) => new()
    {
        Style = layer.Style.Length > 0 ? layer.Style : inherited?.Style ?? "",
        Color = layer.Color.Length > 0 ? layer.Color : inherited?.Color ?? "",
        Custom = layer.Custom.Length > 0 ? ThemeColor.Opaque(layer.Custom) : inherited?.Custom ?? "",
        Gradient = layer.Gradient is { } own ? [.. own.Select(ThemeColor.Opaque)] : inherited?.Gradient,
        Palette = layer.Palette is null ? inherited?.Palette : new Dictionary<string, string>((inherited?.Palette ?? []).Concat(layer.Palette.Select(pair => KeyValuePair.Create(pair.Key, ThemeColor.Opaque(pair.Value))))
            .GroupBy(pair => pair.Key).Select(group => group.Last()), StringComparer.OrdinalIgnoreCase),
        Gradients = layer.Gradients?.Select(g => new PanelThemeGradient { Name = g.Name, NameEn = g.NameEn, Stops = [.. g.Stops.Select(ThemeColor.Opaque)] }).ToList() ?? inherited?.Gradients
    };

    private static void MergeEffects(PanelThemeEffects merged, PanelThemeEffects layer)
    {
        if (layer.SurfaceGradient is not null) merged.SurfaceGradient = layer.SurfaceGradient;
        if (layer.RimGradient is not null) merged.RimGradient = layer.RimGradient;
        merged.RimWidth = layer.RimWidth ?? merged.RimWidth;
        merged.ShadowBlur = layer.ShadowBlur ?? merged.ShadowBlur;
        merged.ShadowDepth = layer.ShadowDepth ?? merged.ShadowDepth;
        merged.ShadowOpacity = layer.ShadowOpacity ?? merged.ShadowOpacity;
        merged.Scanlines = layer.Scanlines ?? merged.Scanlines;
    }

    // The effects in their ranges; a gradient needs two stops and keeps at most eight.
    private static PanelThemeEffectValues EffectValues(PanelThemeEffects effects)
    {
        var surfaceGradient = Stops(effects.SurfaceGradient);
        var rimGradient = Stops(effects.RimGradient);
        double Value(string key) { var (min, max, fallback) = EffectRange(key); return Fit(Effect(effects, key), min, max, fallback); }
        return new(surfaceGradient.Count >= 2 ? surfaceGradient : [], rimGradient.Count >= 2 ? rimGradient : [],
            Value("rimWidth"), Value("shadowBlur"), Value("shadowDepth"), Value("shadowOpacity"), Value("scanlines"));
    }

    // The numeric effects by their names in the file, with their ranges and the values used when no theme sets them.
    public static readonly IReadOnlyList<string> EffectKeys = ["rimWidth", "shadowBlur", "shadowDepth", "shadowOpacity", "scanlines"];
    public static (double Min, double Max, double Fallback) EffectRange(string key) => key switch
    {
        "rimWidth" => (0, 3, 1), "shadowBlur" => (0, 40, 14), "shadowDepth" => (0, 12, 4), "shadowOpacity" => (0, 1, .28), "scanlines" => (0, .12, 0),
        _ => throw new ArgumentException("Unknown effect: " + key, nameof(key))
    };
    public static double? Effect(PanelThemeEffects? effects, string key) => key switch
    {
        "rimWidth" => effects?.RimWidth, "shadowBlur" => effects?.ShadowBlur, "shadowDepth" => effects?.ShadowDepth,
        "shadowOpacity" => effects?.ShadowOpacity, "scanlines" => effects?.Scanlines, _ => throw new ArgumentException("Unknown effect: " + key, nameof(key))
    };
    public static double Effect(PanelThemeEffectValues values, string key) => key switch
    {
        "rimWidth" => values.RimWidth, "shadowBlur" => values.ShadowBlur, "shadowDepth" => values.ShadowDepth,
        "shadowOpacity" => values.ShadowOpacity, "scanlines" => values.Scanlines, _ => throw new ArgumentException("Unknown effect: " + key, nameof(key))
    };
    private static void SetEffect(PanelThemeEffects effects, string key, double value)
    {
        switch (key)
        {
            case "rimWidth": effects.RimWidth = value; break;
            case "shadowBlur": effects.ShadowBlur = value; break;
            case "shadowDepth": effects.ShadowDepth = value; break;
            case "shadowOpacity": effects.ShadowOpacity = value; break;
            case "scanlines": effects.Scanlines = value; break;
            default: throw new ArgumentException("Unknown effect: " + key, nameof(key));
        }
    }
    private static List<ThemeColor> Stops(List<string>? values) => values is null ? [] : values.Select(value => ThemeColor.TryParse(value, out var c) ? c : (ThemeColor?)null).OfType<ThemeColor>().Take(8).ToList();
    private static double Fit(double? value, double min, double max, double fallback) => value is { } v && double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    // Brings one color up to every need and records the change, with the ratio of the need it fell furthest short of.
    private static void Ensure(Dictionary<string, ThemeColor> colors, string key, List<ContrastNeed> needs, List<PanelThemeAdjustment> adjustments)
    {
        var before = colors[key];
        var after = before.EnsureContrast(needs);
        if (after == before) return;
        var worst = needs.MinBy(need => ThemeColor.Contrast(before, need.Background) / need.Minimum);
        adjustments.Add(new(key, before.ToString(), after.ToString(), ThemeColor.Contrast(before, worst.Background), worst.Minimum, after.ContrastScore(needs) >= 1));
        colors[key] = after;
    }
}
