namespace BrimDeck.Core;

public readonly record struct ShelfStackPalette(
    ThemeColor Front, ThemeColor FrontRim, ThemeColor Rest, ThemeColor RestRim, ThemeColor Hover,
    ThemeColor Pressed, ThemeColor Ink, ThemeColor InkLit, ThemeColor Faint, ThemeColor FaintLit)
{
    public static ShelfStackPalette For(PanelPalette palette)
    {
        // The front card masks the card below it. Use the same base as the page so the theme's
        // foreground and row colors keep the contrast they were validated against.
        var front = palette.Surface;
        var rest = palette["fill"].Over(front);
        var hover = palette["hover"].Over(front);
        var pressed = palette["pressed"].Over(front);
        ThemeColor Readable(string role, params ThemeColor[] fills) =>
            palette[role].EnsureContrast(fills, PanelThemes.TextContrast);
        return new(front, palette["outline"], rest, palette["hairline"], hover, pressed,
            Readable("textSecondary", rest), Readable("textPrimary", hover, pressed),
            Readable("textTertiary", rest), Readable("textSecondary", hover, pressed));
    }
}
