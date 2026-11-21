namespace BrimDeck.Core;

// Where the answers to the pointer are drawn: on the shape a control visibly draws, never on its layout box. A control's box
// is often wider or taller than what it shows (a transparent icon button around a small glyph, a text button whose fill is
// inset), and a mark around the box reads as a stray frame. The window side describes the control's visual tree as FxNodes
// (boxes in the control's coordinates) and Resolve picks the shape:
//   1. a template part named "HoverLayer": the fill the control shows on hover, so the mark lies exactly on it;
//   2. an ellipse named "Hover" (round media buttons): a circle in its box;
//   3. a border that fills most of the control and paints (a visible fill or edge, or a rounded face whose fill the control
//      changes on hover): the control's face;
//   4. otherwise the union of what draws ink (glyphs, text, shapes, images, painted panels), with a little room around it.
public enum FxNodeKind { Container, Border, Ellipse, Ink }
public enum FxShapeSource { HoverLayer, Ellipse, Face, Ink }

public sealed class FxNode
{
    public string Name { get; init; } = "";
    public FxNodeKind Kind { get; init; }
    // The node's box in the control's coordinates.
    public DipRect Box { get; init; }
    // For ink: what it draws, in the control's coordinates (its box when not given).
    public DipRect? Ink { get; init; }
    public double Radius { get; init; }
    public bool Visible { get; init; } = true;
    // Draws something that shows: a fill or edge that is not transparent, a glyph in a visible color.
    public bool Paints { get; init; }
    // Has a background at all, even a transparent one the control may fill on hover.
    public bool HasBackground { get; init; }
    public object? Element { get; init; }
    public List<FxNode> Children { get; init; } = [];
}

public readonly record struct FxShape(FxShapeSource Source, DipRect Bounds, double Radius, bool Ellipse, object? Element)
{
    public bool IsEmpty => Bounds.Width <= 0 || Bounds.Height <= 0;
}

public static class FxShapes
{
    // Room left around ink, and the corner of that room.
    public const double InkPad = 3, InkRadius = 4;
    // A face must cover this much of the control to be its face.
    public const double FaceCover = .8;
    // Larger than this, a control is a surface (a card, a whole list) and gets no mark.
    public const double MaxHeight = 72, MaxShare = .35, MinSide = 4;

    public static FxShape Resolve(FxNode root, DipRect control)
    {
        if (Find(root, n => n.Visible && n.Name == "HoverLayer" && n.Kind == FxNodeKind.Border) is { } layer)
            return new(FxShapeSource.HoverLayer, layer.Box, Math.Min(layer.Radius, Math.Min(layer.Box.Width, layer.Box.Height) / 2), false, layer.Element);
        if (Find(root, n => n.Visible && n.Name == "Hover" && n.Kind == FxNodeKind.Ellipse) is { } round)
        {
            // An ellipse that keeps its proportions (Stretch Uniform) is a circle in the middle of its box.
            double side = Math.Min(round.Box.Width, round.Box.Height);
            var circle = new DipRect(round.Box.X + (round.Box.Width - side) / 2, round.Box.Y + (round.Box.Height - side) / 2, side, side);
            return new(FxShapeSource.Ellipse, circle, side / 2, true, round.Element);
        }
        double area = Math.Max(1, control.Width * control.Height);
        if (Find(root, n => n.Visible && n.Kind == FxNodeKind.Border && (n.Paints || n.Radius > 0 && n.HasBackground) && n.Box.Width * n.Box.Height >= area * FaceCover) is { } face)
            return new(FxShapeSource.Face, face.Box, Math.Min(face.Radius, Math.Min(face.Box.Width, face.Box.Height) / 2), false, face.Element);
        var ink = InkOf(root);
        if (ink is not { } inked) return new(FxShapeSource.Ink, default, 0, false, null);
        // With room around it, kept inside the control (the control is where it can be pressed).
        double x = Math.Max(control.X, inked.X - InkPad), y = Math.Max(control.Y, inked.Y - InkPad);
        double right = Math.Min(control.Right, inked.Right + InkPad), bottom = Math.Min(control.Bottom, inked.Bottom + InkPad);
        var box = new DipRect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
        return new(FxShapeSource.Ink, box, Math.Min(InkRadius, Math.Min(box.Width, box.Height) / 2), false, null);
    }

    // Whether a shape gets a mark at all: big enough to carry one, small enough not to be a surface.
    public static bool Accepts(DipRect shape, DipRect surface)
        => shape.Width >= MinSide && shape.Height >= MinSide && shape.Height <= MaxHeight
           && (surface.Width <= 0 || surface.Height <= 0 || shape.Width * shape.Height <= surface.Width * surface.Height * MaxShare);

    // Edges on device pixels, so a 1 px line is one pixel and nothing shimmers at fractional scales. Halves always round the
    // same way (up): rounding them to even would widen a shape that sits on half pixels (at 175 percent a 36 px circle at
    // x.5 became 64 device pixels wide instead of 63).
    public static DipRect Snap(DipRect rect, double scaleX, double scaleY)
    {
        if (scaleX <= 0 || scaleY <= 0) return rect;
        double left = Pixel(rect.X * scaleX) / scaleX, top = Pixel(rect.Y * scaleY) / scaleY;
        double right = Pixel(rect.Right * scaleX) / scaleX, bottom = Pixel(rect.Bottom * scaleY) / scaleY;
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    // The middle of a line of the given width placed on device pixels: whole pixels for an even number of them, half for odd.
    public static double SnapLine(double at, double width, double scale)
    {
        if (scale <= 0) return at;
        int pixels = Math.Max(1, (int)Math.Round(width * scale));
        return pixels % 2 == 0 ? Pixel(at * scale) / scale : (Math.Floor(at * scale) + .5) / scale;
    }

    // The nearest device pixel boundary, halves up.
    public static double Pixel(double devicePixels) => Math.Floor(devicePixels + .5);

    // Clipped to the viewports the control is shown through; empty when nothing of it shows.
    public static DipRect Clip(DipRect rect, DipRect viewport)
    {
        double x = Math.Max(rect.X, viewport.X), y = Math.Max(rect.Y, viewport.Y);
        double right = Math.Min(rect.Right, viewport.Right), bottom = Math.Min(rect.Bottom, viewport.Bottom);
        return right <= x || bottom <= y ? default : new(x, y, right - x, bottom - y);
    }

    private static FxNode? Find(FxNode node, Func<FxNode, bool> match)
    {
        // Breadth first: the outermost part of the template wins.
        var queue = new Queue<FxNode>();
        queue.Enqueue(node);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (!next.Visible) continue;
            if (match(next)) return next;
            foreach (var child in next.Children) queue.Enqueue(child);
        }
        return null;
    }

    private static DipRect? InkOf(FxNode node)
    {
        if (!node.Visible) return null;
        DipRect? union = null;
        if (node.Paints && node.Kind != FxNodeKind.Container) union = node.Ink ?? node.Box;
        foreach (var child in node.Children)
            if (InkOf(child) is { } ink) union = union is { } u ? Union(u, ink) : ink;
        return union is { Width: > 0, Height: > 0 } ? union : null;
    }

    private static DipRect Union(DipRect a, DipRect b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }
}
