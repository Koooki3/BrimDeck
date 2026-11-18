namespace BrimDeck.Core;

// The row above both pages of the open panel: the page switch at the left, the clock in the centre, and the media source,
// sync and settings buttons at the right. All values are whole DIPs.
// Row: the height the page gives its header. The music page gives it Toolbar + Top, so its content starts right below.
// Height: the toolbar's even height. Side: the shared inset of the switch's left edge and the gear's visible right edge.
// SwitchHeight: 28, or 24 in a row below 36. ButtonHeight: each button inside the switch. SwitchTop: from the panel's top
// edge to the switch. RightMargin: the right group's margin that puts the gear's ink at Side. SettingsHeight: the settings
// button, 24, or 20 in a row below 32, which keeps its hover background inside a rounded top corner.
public sealed record PanelHeader(double Row, double Height, double Side, double SwitchHeight, double ButtonHeight, double SwitchTop,
    double RightMargin, double SettingsHeight)
{
    public double ContentSide { get; init; } = Side;

    // The open panel's corner radius. Its top corners are rounded whenever it does not start at the monitor's top edge.
    public const double PanelRadius = 24;
    // The switch's corners; the buttons inside it are concentric with them.
    public const double SwitchRadius = 8, SwitchPadding = 2, ButtonRadius = SwitchRadius - SwitchPadding;
    // From this inset on, the centre of the switch's corner lies beyond the centre of the panel's rounded corner, so that
    // corner comes no closer to the switch than the straight top edge does.
    public const double CornerClear = PanelRadius - SwitchRadius;
    // The gear glyph (Segoe MDL2 Assets U+E713 at 16) fills its 16 DIP box (ink 0.05 to 15.95), which is centred in the
    // 28 DIP settings button.
    public const double SettingsWidth = 28, SettingsRadius = 6, GearSize = 16, GearInset = (SettingsWidth - GearSize) / 2;

    public static PanelHeader For(double row, double side)
    {
        row = double.IsFinite(row) ? Math.Max(0, Math.Round(row)) : 0;
        side = double.IsFinite(side) ? Math.Round(side) : 0;
        double height = row - row % 2, inset = Math.Max(side, CornerClear);
        double switchHeight = height >= 36 ? 28 : 24;
        return new(row, height, inset, switchHeight, switchHeight - 2 * SwitchPadding, Math.Max(0, (height - switchHeight) / 2),
            inset - GearInset, height >= 32 ? 24 : 20);
    }

    public static PanelHeader Music(MusicMetrics metrics) => For(metrics.Toolbar + metrics.Top, metrics.Side);

    public static double ToolbarFor(double panelHeight) => panelHeight < 150 ? 28 : panelHeight >= 290 ? 40 : 36;
    public static PanelHeader ForPanel(double height) => For(ToolbarFor(height), height < 170 ? 14 : 20);

    // The body and the side grips start at this page's row. Only the toolbar uses the shared geometry.
    public PanelHeader WithControlsOf(PanelHeader shared) => shared with { Row = Row, ContentSide = ContentSide };

    // The space between a rounded rectangle and the outline of a panel corner of the given radius, for a rectangle x and y
    // from the panel's edges with corners of radius r. A negative value means the panel clips the rectangle.
    public static double CornerGap(double radius, double x, double y, double r)
    {
        double cx = x + r, cy = y + r;
        if (cx >= radius && cy >= radius) return Math.Min(x, y);
        if (cx >= radius) return y;
        if (cy >= radius) return x;
        return radius - Math.Sqrt((radius - cx) * (radius - cx) + (radius - cy) * (radius - cy)) - r;
    }
}
