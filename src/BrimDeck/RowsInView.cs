using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrimDeck;

// The model details' rows (MainWindow.RenderModels), stacked top to bottom in a scroll viewer, each as tall as its own Height.
// Only the rows that show are laid out. Stacked whole, a long list had every row measured and arranged again on each frame of a
// resize, which made the drag stutter. The others keep their last layout until they scroll into view, where they are laid out at
// the current width before they are drawn; a row that has never been in view has never been drawn.
internal sealed class RowsInView : Panel
{
    // Laid out beyond the viewport on each side as well, so a row coming into view is ready a little early.
    private const double Spare = 32;

    protected override Size MeasureOverride(Size available)
    {
        double height = 0;
        foreach (UIElement child in InternalChildren) height += HeightOf(child);
        return new Size(double.IsFinite(available.Width) ? available.Width : 0, height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var (top, bottom) = Shown();
        double y = 0;
        foreach (UIElement child in InternalChildren)
        {
            double height = HeightOf(child);
            if (y + height > top && y < bottom)
            {
                child.Measure(new Size(size.Width, height));
                child.Arrange(new Rect(0, y, size.Width, height));
            }
            y += height;
        }
        return size;
    }

    private static double HeightOf(UIElement child) => child is FrameworkElement { Height: var height } && double.IsFinite(height) ? height : 0;

    // The part of the rows the scroll viewer shows. Its presenter has taken the viewport and the offset before it arranges the
    // rows; outside a scroll viewer every row is laid out.
    private (double Top, double Bottom) Shown() => VisualTreeHelper.GetParent(this) is ScrollContentPresenter { ViewportHeight: > 0 } presenter
        ? (presenter.VerticalOffset - Spare, presenter.VerticalOffset + presenter.ViewportHeight + Spare)
        : (double.NegativeInfinity, double.PositiveInfinity);
}
