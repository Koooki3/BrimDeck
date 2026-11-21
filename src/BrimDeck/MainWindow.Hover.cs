using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The collapsed surface's pointer area (see CollapsedHover), in the two forms the window needs: a screen rectangle in device
// pixels for the cursor checks, and the box of the nearly transparent HoverExtension layer that receives the mouse there.
public partial class MainWindow
{
    // The page the part under the pointer on the collapsed surface stands for, taken as the panel opens (OpenAtPointer) and
    // spent by the page choice that opening makes (ApplyDefaultMusicPage). Null when the pointer rested on no part.
    private DeckPage? _hoverPage;

    // The sensing area in device pixels, comparable with GetCursorPos. Empty while the window has no presentation source.
    private Rect CompactHoverScreenRect()
    {
        if (PresentationSource.FromVisual(Stage) is null) return Rect.Empty;
        var zone = HoverZoneOnStage();
        return new Rect(Stage.PointToScreen(new Point(zone.Left, zone.Top)), Stage.PointToScreen(new Point(zone.Right, zone.Bottom)));
    }

    // The HoverExtension box in DIPs relative to Stage. Collapsed, it is the sensing area: from the screen edge down to the
    // bottom of the surface while bridged, otherwise the surface alone, so the gap above a lowered capsule lets clicks through.
    // Open, the panel uses its own outline as before: no height, and a NaN Left and Width that keep the stretched layout.
    private (double Top, double Height, double Left, double Width) HoverExtensionBox(bool expanded)
    {
        if (expanded) return (0, 0, double.NaN, double.NaN);
        var zone = HoverZoneOnStage();
        return (zone.Top, zone.Height, zone.Left, zone.Width);
    }

    // The collapsed shape is relative to the monitor's top-left corner. Stage starts at the window's corner, which Place puts
    // at the top of the monitor and _windowOffset DIPs to the right of its left edge; the island is drawn with the same offset.
    private Rect HoverZoneOnStage()
    {
        // A capsule docked with the bottom bridges to the line it is docked with (MainWindow.Edge.cs), the frame's edge.
        var zone = CollapsedHover.Zone(Collapsed.Style, Collapsed.Surface, Collapsed.Style == CompactStyle.Capsule && _frame.Edge != ScreenEdge.Top ? 0 : WorkDip.Y);
        return new Rect(zone.X - _windowOffset, zone.Y, Math.Max(0, zone.Width), Math.Max(0, zone.Height));
    }

    // The page the pointer resting on the collapsed surface asks for: the row's own parts carry the page each one stands for
    // (CompactRegion's tag, set where the row is built in MainWindow.Music.cs), so a part that moves, narrows or is left out of
    // the row follows by itself and no coordinate table goes stale. The smallest region holding the point wins, which keeps a
    // part inside another one (a figure within the readings' window) with its own page. No region there: null, and the panel opens
    // on whatever it opened on before.
    private DeckPage? CompactPageAt(Point stagePoint)
    {
        DeckPage? found = null;
        double smallest = double.MaxValue;
        // A part that cannot be measured simply names no page: the panel must open whatever this walk finds, so nothing here
        // is allowed to escape into the opening itself.
        try
        {
            foreach (var region in TaggedRegions(CompactRings))
            {
                if (!region.IsVisible || region.ActualWidth <= 0 || region.ActualHeight <= 0) continue;
                Rect box;
                try { box = region.TransformToAncestor(Stage).TransformBounds(new Rect(0, 0, region.ActualWidth, region.ActualHeight)); }
                catch (InvalidOperationException) { continue; }
                catch (ArgumentException) { continue; }
                if (!box.Contains(stagePoint) || box.Width * box.Height >= smallest) continue;
                smallest = box.Width * box.Height;
                found = (DeckPage)region.Tag;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return found;
        }
        return found;
    }

    // Every element of the collapsed row that names a page, however deep in it. Only visuals are walked: a text run or another
    // non-visual child has no children of its own to count.
    private static IEnumerable<FrameworkElement> TaggedRegions(DependencyObject root)
    {
        if (root is not Visual) yield break;
        int count = ChildCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Tag: DeckPage } element) yield return element;
            foreach (var inner in TaggedRegions(child)) yield return inner;
        }
    }

    // The children of a visual, or none where it has no child list to give.
    private static int ChildCount(DependencyObject node)
    {
        try { return VisualTreeHelper.GetChildrenCount(node); }
        catch (ArgumentException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }
}
