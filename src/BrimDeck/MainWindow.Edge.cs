using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

// The edge the panel hangs from (DeckSettings.DockEdge). Everything is laid out in the edge's frame (EdgeFrame), as if the panel
// hung from the top: MonitorBoundsDip and WorkDip are the frame's, the collapsed shape, the hover area, the open panel and the
// resize grips are computed in it, and Stage draws them through the frame's mapping (its layout transform). The island's content is
// turned back upright (IslandContent), so text reads as usual on every edge and its pop-ups, tooltips and screen points need no
// change. FrameWidth runs along the edge and FrameHeight away from it; on the left and right edges they are the window's height
// and width.
public partial class MainWindow
{
    private EdgeFrame _frame = EdgeFrame.For(ScreenEdge.Top, 1920, 1080, new DipRect(0, 0, 1920, 1032));
    // The edge the transforms were last set for; null before the first time.
    private ScreenEdge? _framedEdge;

    private double FrameWidth
    {
        get => _frame.Across ? Height : Width;
        set { if (_frame.Across) Height = value; else Width = value; }
    }
    private double FrameHeight
    {
        get => _frame.Across ? Width : Height;
        set { if (_frame.Across) Width = value; else Height = value; }
    }

    // The collapsed content stands as a column: the notch on a side edge (CompactColumn).
    private bool ColumnFor(CompactStyle style) => _frame.Across && style == CompactStyle.Notch;
    private bool CompactColumnMode => ColumnFor(Collapsed.Style);

    // Sets the transforms for the frame's edge. Returns whether the edge changed since the last time; the first time does not count.
    private bool ApplyFrame()
    {
        var edge = _frame.Edge;
        if (_framedEdge == edge) return false;
        bool changed = _framedEdge is not null;
        _framedEdge = edge;
        Stage.LayoutTransform = Turn(_frame.Matrix);
        IslandContent.LayoutTransform = Turn(_frame.Upright);
        // On the top and bottom edges the open page follows the island's width (InitializeScale). On a side edge it keeps the page's
        // size (SetExpanded), centred along the edge, so the header and the page stay together while the island changes its length.
        // It hangs from the edge, so it is anchored there and is not scaled as the panel opens or closes (it only fades).
        if (_frame.Across) BindingOperations.ClearBinding(ExpandedContent, WidthProperty);
        else if (BindingOperations.GetBindingExpression(ExpandedContent, WidthProperty) is null)
            ExpandedContent.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = Island, Converter = _unscaled });
        (ExpandedContent.HorizontalAlignment, ExpandedContent.VerticalAlignment) = edge switch
        {
            ScreenEdge.Bottom => (HorizontalAlignment.Center, VerticalAlignment.Bottom),
            ScreenEdge.Left => (HorizontalAlignment.Left, VerticalAlignment.Center),
            ScreenEdge.Right => (HorizontalAlignment.Right, VerticalAlignment.Center),
            _ => (HorizontalAlignment.Center, VerticalAlignment.Top)
        };
        _grips.Upright = Turn(_frame.Upright);
        // The line's playback progress runs along the edge; its length and thickness are set again for it.
        MusicIndicator.ClearValue(WidthProperty); MusicIndicator.ClearValue(HeightProperty);
        MusicIndicator.HorizontalAlignment = _frame.Across ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        MusicIndicator.VerticalAlignment = _frame.Across ? VerticalAlignment.Top : VerticalAlignment.Center;
        // The row was built for the other orientation.
        _compactRendered = null; _alertRendered = null; _compactWanted = double.NaN; _compactColumn = double.NaN;
        return changed;
    }

    private static Transform Turn((double M11, double M12, double M21, double M22) m)
    {
        if (m == (1, 0, 0, 1)) return Transform.Identity;
        var transform = new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, 0, 0));
        transform.Freeze();
        return transform;
    }

    // The panel moved to another edge while it was on show: it comes in there with a short fade rather than jumping.
    private void FadeInOnNewEdge()
    {
        if (_bootWaiting || _bootDot || _poweringOff || !Soft.Enabled || Settings.AnimationDuration <= 0) return;
        HoverZone.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Math.Max(120, Settings.AnimationDuration * .6))) { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
    }
}
