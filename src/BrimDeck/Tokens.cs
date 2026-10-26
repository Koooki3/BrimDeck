namespace BrimDeck;

// The panel's design tokens: the few values its spacing, corners and text sizes are made of, so a page reads as one design
// rather than a collection of near-equal numbers. A layout that needs a value off the scale says why where it uses it.

// Spacing, on a 4 DIP grid; Hair and Tight are the fine steps of compact rows.
internal static class Space
{
    public const double Hair = 2, S4 = 4, Tight = 6, S8 = 8, S12 = 12, S16 = 16, S24 = 24, S32 = 32;
}

// Corners: Xs for bars and thin lines, Sm for tags and badges, Md for buttons, segments and row hovers, Lg for cards, pop-ups and
// notices, Xl for the panel's own bottom corners; Full makes a pill or a circle of anything. A shape inside another takes the
// outer radius less the padding between them. In a pixel theme every corner is square (PanelTheme.Corner).
internal static class Radius
{
    public const double Xs = 2, Sm = 4, Md = 8, Lg = 12, Xl = 18, Full = 999;
}

// Text sizes: Caption for small labels and units, Body for text, Strong for titles of rows and notices, Title for page titles,
// Figure for large readings.
internal static class TextSize
{
    public const double Caption = 11, Body = 12, Strong = 13, Title = 15, Figure = 20;
}
