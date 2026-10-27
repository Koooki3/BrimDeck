using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BrimDeck;

// The industrial drawing of icons on a plate (PanelTheme.Plated, a theme with cut corners): the same drawings with every curve turned
// into a few straight edges (a circle becomes a hexagon or a decagon by its size, a rounded corner a chamfer), stroked heavier with
// mitered corners and square ends, as the game's icons are thick-lined geometry of crates, gears and hexagons rather than the
// soft line art of the plain themes. One transform for every icon, so the toolbar, the devices, the notices and the monitor
// glyphs stay one family and an icon added later is covered without a drawing of its own.
internal static class IconStyle
{
    // How far a straight edge may lie from the curve it stands for, as a share of the icon's grid (1 on a 24 unit grid).
    private const double Fit = .042;
    // The stroke's weight on a plate, against the plain icons'.
    public const double Heavier = 1.14;

    // The geometry with its curves flattened, frozen. Lines and corners stay as they are.
    public static PathGeometry Angular(Geometry geometry, double grid)
    {
        var flat = geometry.GetFlattenedPathGeometry(Math.Max(.05, grid * Fit), ToleranceType.Absolute);
        flat.Freeze();
        return flat;
    }

    // A pen for an angular icon: mitered corners, square ends.
    public static Pen Pen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness * Heavier) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Miter, MiterLimit = 4 };
        pen.Freeze();
        return pen;
    }

    // What a path was drawn with before the style took it, so the plain look comes back with a theme that is not plated.
    private sealed record Plain(Geometry Data, double Thickness, PenLineCap Start, PenLineCap End, PenLineJoin Join);
    private static readonly ConditionalWeakTable<Path, Plain> Saved = new();

    // Styles every stroked path under the root (the toolbar's icons, the page switch, the pin and the sync arrows), or gives them back
    // their plain look. Run when the theme changes, not on every frame.
    public static void Apply(DependencyObject root, bool plated)
    {
        if (root is Path path && path.Stroke is not null && path.Data is { } data)
        {
            if (!Saved.TryGetValue(path, out var plain)) Saved.Add(path, plain = new Plain(data, path.StrokeThickness, path.StrokeStartLineCap, path.StrokeEndLineCap, path.StrokeLineJoin));
            if (plated)
            {
                double grid = plain.Data.Bounds is { IsEmpty: false } bounds && Math.Max(bounds.Right, bounds.Bottom) > 17 ? 24 : 16;
                path.Data = Angular(plain.Data, grid);
                path.StrokeStartLineCap = path.StrokeEndLineCap = PenLineCap.Square; path.StrokeLineJoin = PenLineJoin.Miter;
                path.StrokeThickness = plain.Thickness * Heavier;
            }
            else
            {
                path.Data = plain.Data; path.StrokeStartLineCap = plain.Start; path.StrokeEndLineCap = plain.End; path.StrokeLineJoin = plain.Join;
                path.StrokeThickness = plain.Thickness;
            }
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Apply(VisualTreeHelper.GetChild(root, i), plated);
    }
}
