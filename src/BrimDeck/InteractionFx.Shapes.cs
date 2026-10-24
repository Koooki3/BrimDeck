using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// A control the answers are drawn on, and where its visible shape is on the layer right now. The shape is found once, when the
// control is first answered (FxShapes.Resolve over its visual tree: the hover fill its template shows, a round button's circle,
// its face, or the ink it draws), and kept in the coordinates of the element that draws it (Anchor). Its place on the layer is
// worked out again on every frame from the visual tree's own transforms and offsets, so a mark rides along as the panel opens,
// a page slides or a list scrolls, and is never left where the control was. That walk allocates nothing.
internal sealed class FxMark
{
    private const int ViewportLimit = 4;
    public readonly FrameworkElement Control;
    public readonly FrameworkElement Anchor;
    public readonly Rect Local;
    public readonly double LocalRadius;
    public readonly bool Ellipse;
    public readonly FxShapeSource Source;
    // Live, on the layer: the shape's box (snapped to device pixels), its corner, the box it shows through (scroll viewports),
    // and the product of the opacities above it (it fades with the content it belongs to).
    public Rect Box = Rect.Empty;
    public double Radius;
    public Rect Clip = Rect.Empty;
    public readonly RectangleGeometry ClipGeometry = new();
    public double Opacity;
    public bool Valid;
    // Hover: when it arrived or started to leave, and the presence it left from; a glide from where the previous mark was.
    public double ShownAt = double.NaN, ShownFrom, LeftAt = double.NaN, LeftFrom;
    public Rect GlideFrom = Rect.Empty;
    public double GlideFromRadius, GlideAt = double.NaN;
    public int Serial;
    private readonly ScrollContentPresenter?[] _viewports = new ScrollContentPresenter?[ViewportLimit];

    private FxMark(FrameworkElement control, FrameworkElement anchor, Rect local, double radius, bool ellipse, FxShapeSource source)
    {
        Control = control; Anchor = anchor; Local = local; LocalRadius = radius; Ellipse = ellipse; Source = source;
    }

    // The control's visible shape; null when it shows nothing that can carry a mark.
    public static FxMark? For(FrameworkElement control)
    {
        if (control.RenderSize.Width <= 0 || control.RenderSize.Height <= 0) return null;
        int budget = 96;
        var root = Node(control, control, 0, ref budget);
        if (root is null) return null;
        var shape = FxShapes.Resolve(root, new DipRect(0, 0, control.RenderSize.Width, control.RenderSize.Height));
        if (shape.IsEmpty) return null;
        if (shape.Element is FrameworkElement anchor && !ReferenceEquals(anchor, control) && anchor.RenderSize is { Width: > 0, Height: > 0 } size)
        {
            // Kept in the drawing element's own coordinates, so a transform inside the template (a button that shrinks as it is
            // pressed) moves the mark with it.
            var local = new Rect(size);
            if (shape.Ellipse)
            {
                double side = Math.Min(size.Width, size.Height);
                local = new Rect((size.Width - side) / 2, (size.Height - side) / 2, side, side);
            }
            return new FxMark(control, anchor, local, shape.Ellipse ? local.Width / 2 : shape.Radius, shape.Ellipse, shape.Source);
        }
        return new FxMark(control, control, new Rect(shape.Bounds.X, shape.Bounds.Y, shape.Bounds.Width, shape.Bounds.Height), shape.Radius, shape.Ellipse, shape.Source);
    }

    // The control's visual tree as FxShapes reads it, boxes in the control's coordinates.
    private static FxNode? Node(Visual visual, FrameworkElement control, int depth, ref int budget)
    {
        if (--budget < 0 || depth > 10) return null;
        DipRect box = default; DipRect? ink = null;
        bool visible = true, paints = false, background = false; double radius = 0; string name = ""; var kind = FxNodeKind.Container;
        if (visual is FrameworkElement element)
        {
            visible = element.Visibility == Visibility.Visible;
            name = element.Name ?? "";
            if (visible)
            {
                Rect bounds;
                try { bounds = element.TransformToAncestor(control).TransformBounds(new Rect(element.RenderSize)); }
                catch (InvalidOperationException) { return null; }
                box = new DipRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                bool shows = element.Opacity > .01;
                switch (element)
                {
                    case Border border:
                        kind = FxNodeKind.Border; radius = border.CornerRadius.TopLeft; background = border.Background is not null;
                        paints = shows && (Shows(border.Background) || Shows(border.BorderBrush) && border.BorderThickness.Left + border.BorderThickness.Top > 0);
                        break;
                    case Ellipse ellipse:
                        kind = FxNodeKind.Ellipse; paints = shows && (Shows(ellipse.Fill) || Shows(ellipse.Stroke) && ellipse.StrokeThickness > 0);
                        break;
                    case Shape shape:
                        kind = FxNodeKind.Ink; paints = shows && (Shows(shape.Fill) || Shows(shape.Stroke) && shape.StrokeThickness > 0);
                        ink = InkBounds(shape, control);
                        break;
                    case TextBlock text:
                        kind = FxNodeKind.Ink; paints = shows && Shows(text.Foreground) && !string.IsNullOrEmpty(text.Text);
                        ink = InkBounds(text, control);
                        break;
                    case Image image:
                        kind = FxNodeKind.Ink; paints = shows && image.Source is not null;
                        break;
                    case Panel panel:
                        paints = shows && Shows(panel.Background);
                        if (paints) kind = FxNodeKind.Ink;
                        break;
                    case Control inner when !ReferenceEquals(inner, control):
                        background = inner.Background is not null;
                        break;
                }
            }
        }
        var node = new FxNode { Name = name, Kind = kind, Box = box, Ink = ink, Radius = radius, Visible = visible, Paints = paints, HasBackground = background, Element = visual };
        if (!visible) return node;
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(visual); i < n; i++)
            if (VisualTreeHelper.GetChild(visual, i) is Visual child && Node(child, control, depth + 1, ref budget) is { } childNode) node.Children.Add(childNode);
        return node;
    }

    // What an element draws, from its rendered content, in the control's coordinates.
    private static DipRect? InkBounds(Visual visual, FrameworkElement control)
    {
        var content = VisualTreeHelper.GetContentBounds(visual);
        if (content.IsEmpty || content.Width <= 0 || content.Height <= 0) return null;
        try
        {
            var bounds = visual.TransformToAncestor(control).TransformBounds(content);
            return new DipRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
        catch (InvalidOperationException) { return null; }
    }

    private static bool Shows(Brush? brush) => brush switch
    {
        null => false,
        SolidColorBrush solid => solid.Color.A > 0 && solid.Opacity > 0,
        _ => brush.Opacity > 0
    };

    // Where the shape is now on the layer (the layer's origin is the scope's). Invalid once the control is gone, hidden,
    // disabled or outside the scope.
    public void Update(Visual scope, DpiScale dpi)
    {
        Valid = false;
        if (!Control.IsVisible || !Control.IsEnabled) return;
        var matrix = Matrix.Identity;
        double opacity = 1;
        int viewports = 0;
        Array.Clear(_viewports);
        bool anchorPassed = false;
        for (DependencyObject? node = Anchor; ; node = VisualTreeHelper.GetParent(node))
        {
            if (node is null) return;
            if (ReferenceEquals(node, scope)) break;
            if (node is not Visual visual) return;
            if (ReferenceEquals(node, Control)) anchorPassed = true;
            if (node is UIElement element)
            {
                if (element.Visibility != Visibility.Visible) return;
                // Inside the control, opacity is its own hover and press feedback (a hover layer at rest is transparent);
                // from the control up it is the content fading in or out.
                if (anchorPassed) opacity *= element.Opacity;
                if (element is ScrollContentPresenter viewport && viewports < ViewportLimit) _viewports[viewports++] = viewport;
            }
            if (VisualTreeHelper.GetTransform(visual) is { } transform) matrix.Append(transform.Value);
            var offset = VisualTreeHelper.GetOffset(visual);
            matrix.Translate(offset.X, offset.Y);
        }
        if (!anchorPassed) return;
        var box = Rect.Transform(Local, matrix);
        double scale = Math.Sqrt(Math.Abs(matrix.M11 * matrix.M22));
        var clip = Rect.Empty;
        for (int i = 0; i < viewports; i++)
        {
            var area = Through(_viewports[i]!, scope);
            if (area.IsEmpty) return;
            if (!clip.IsEmpty)
            {
                var both = FxShapes.Clip(new DipRect(clip.X, clip.Y, clip.Width, clip.Height), new DipRect(area.X, area.Y, area.Width, area.Height));
                if (both.Width <= 0) return;
                area = new Rect(both.X, both.Y, both.Width, both.Height);
            }
            clip = area;
        }
        var snapped = FxShapes.Snap(new DipRect(box.X, box.Y, box.Width, box.Height), dpi.DpiScaleX, dpi.DpiScaleY);
        Box = new Rect(snapped.X, snapped.Y, snapped.Width, snapped.Height);
        Radius = Math.Min(LocalRadius * scale, Math.Min(Box.Width, Box.Height) / 2);
        // Set only when it changes: a geometry's new rectangle is a small allocation.
        if (!clip.IsEmpty && clip != Clip) ClipGeometry.Rect = clip;
        Clip = clip;
        Opacity = Math.Clamp(opacity, 0, 1);
        Valid = Box.Width >= 1 && Box.Height >= 1;
    }

    // A viewport's box on the layer.
    private static Rect Through(ScrollContentPresenter viewport, Visual scope)
    {
        var matrix = Matrix.Identity;
        for (DependencyObject? node = viewport; ; node = VisualTreeHelper.GetParent(node))
        {
            if (node is null) return Rect.Empty;
            if (ReferenceEquals(node, scope)) break;
            if (node is not Visual visual) return Rect.Empty;
            if (VisualTreeHelper.GetTransform(visual) is { } transform) matrix.Append(transform.Value);
            var offset = VisualTreeHelper.GetOffset(visual);
            matrix.Translate(offset.X, offset.Y);
        }
        return Rect.Transform(new Rect(viewport.RenderSize), matrix);
    }
}
