using System.Windows;
using System.Windows.Controls;

namespace BrimDeck;

// The recent notices page while the panel's edges are dragged (MainWindow.Resize.cs): its rows stretch with the page instead of
// being built again, but a row whose width changes lays its texts out again, the trimmed title and body above all, and doing
// that for every row at every step of the drag made the page lag behind the pointer. So the rows out of sight keep the width
// they had when the drag began; their heights stay as they were, so the scroll position does not move. The rows in view stretch,
// their texts in steps (SteppedWidth), and a row that the growing page brings near the view takes the page's width again before
// it shows. All take the exact width again when the drag ends (Release).
internal sealed class NoticeRowStretch
{
    // How far above and below the view a row already follows the page: more than the page grows in one frame.
    private const double Ahead = 32;
    private readonly List<FrameworkElement> _held = [];
    // The list's scroll bar, kept shown during the drag.
    private ScrollViewer? _scroll;

    // A drag is under way: rows built meanwhile start with their texts in steps.
    public bool Active { get; private set; }

    // Before each step of the drag, with the list as it was last laid out: the first step holds the rows out of sight, the
    // next ones let go of those that came near the view.
    public void Follow(StackPanel list, IEnumerable<SteppedWidth> texts)
    {
        if (list.Parent is not ScrollViewer scroll || !list.IsArrangeValid) return;
        double top = scroll.VerticalOffset - Ahead, bottom = scroll.VerticalOffset + scroll.ViewportHeight + Ahead;
        bool Near(FrameworkElement row) => row.TranslatePoint(new Point(), list).Y is var y && y + row.ActualHeight >= top && y <= bottom;
        if (Active)
        {
            _held.RemoveAll(row => { if (!Near(row)) return false; row.ClearValue(FrameworkElement.WidthProperty); return true; });
            return;
        }
        Active = true;
        // A scroll bar that shows only when needed has the list laid out twice at every step, once as if it were not there;
        // shown, it stays shown until the drag ends.
        if (scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible)
        {
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
            _scroll = scroll;
        }
        foreach (var text in texts) text.Stepping = true;
        foreach (var row in list.Children.OfType<FrameworkElement>())
        {
            if (Near(row)) continue;
            row.Width = row.ActualWidth;
            _held.Add(row);
        }
    }

    // The drag has ended: every row and text takes the page's exact width again.
    public void Release(IEnumerable<SteppedWidth> texts)
    {
        foreach (var row in _held) row.ClearValue(FrameworkElement.WidthProperty);
        foreach (var text in texts) text.Stepping = false;
        if (_scroll is not null) _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Forget();
    }

    // The page was built anew: the rows held belonged to the old one.
    public void Forget() { _held.Clear(); _scroll = null; Active = false; }
}

// A row's texts, which follow the row's width in steps while Stepping: a long title or body trimmed again at every frame cost
// more than a frame allows across the rows in view. Between steps they keep their place on the left and only the ellipsis waits
// for the next step; otherwise they take the exact width.
internal sealed class SteppedWidth : Decorator
{
    // Two Chinese characters or so: trimmed again at every step of 8 DIP, the texts in view still took more than a frame too often.
    public const double Step = 24;
    private bool _stepping;
    // The width the texts had when the steps began; the steps are counted from it, so beginning lays nothing out again.
    private double _from;

    public bool Stepping
    {
        get => _stepping;
        set
        {
            if (_stepping == value) return;
            _stepping = value;
            if (value) _from = RenderSize.Width;
            else InvalidateMeasure();
        }
    }

    private double Fit(double width) => _stepping && double.IsFinite(width) ? Math.Max(0, _from + Math.Floor((width - _from) / Step) * Step) : width;

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return default;
        Child.Measure(new Size(Fit(constraint.Width), constraint.Height));
        return Child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, Fit(arrangeSize.Width), arrangeSize.Height));
        return arrangeSize;
    }
}
