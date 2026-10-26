using System.Windows;
using System.Windows.Controls;
using BrimDeck;

// The recent notices page while the panel's edges are dragged (NoticeRowStretch): a list shaped like the page's, off screen.
// Its texts count how often they are laid out, which is the work that made the drag lag; no timing is checked.
internal static class NoticeRowStretchTests
{
    // A text that counts its layouts: WPF measures it, and the text inside, again only when the width it is given changes.
    private sealed class CountedText : Decorator
    {
        public static int Measures;
        public CountedText(string text, double size) => Child = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
        protected override Size MeasureOverride(Size constraint) { Measures++; Child.Measure(constraint); return Child.DesiredSize; }
    }

    public static void Run(Action<string, bool> check)
    {
        var (root, scroll, list, texts) = Page(40);
        Lay(root);
        scroll.ScrollToVerticalOffset(400);
        Lay(root);
        double offset = scroll.VerticalOffset;
        var rows = list.Children.OfType<FrameworkElement>().ToList();
        bool InView(FrameworkElement row) => row.TranslatePoint(new Point(), list).Y is var y && y + row.ActualHeight > offset && y < offset + scroll.ViewportHeight;
        var shown = rows.Where(InView).ToList();
        double before = rows[0].ActualWidth;

        // The drag widens the page one DIP at a time, as the spring moves it.
        var stretch = new NoticeRowStretch();
        CountedText.Measures = 0;
        for (int i = 1; i <= 12; i++)
        {
            stretch.Follow(list, texts);
            root.Width += 1;
            Lay(root);
        }
        var held = rows.Where(row => !double.IsNaN(row.Width)).ToList();
        check("row stretch: the rows out of sight keep their width, those in view follow the page", held.Count > 20 && held.All(row => row.ActualWidth == before)
            && shown.All(row => double.IsNaN(row.Width) && row.ActualWidth == before + 12));
        check("row stretch: the scroll position does not move", scroll.VerticalOffset == offset);
        // Without it, 12 steps lay out both texts of all 40 rows 12 times (twice each, see below); with it, none within a step.
        check("row stretch: within a step no text is laid out again", CountedText.Measures == 0);
        check("row stretch: the scroll bar stays shown during the drag", scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Visible);
        // Crossing a step lays out again only the texts of the rows that follow the page, once.
        int following = rows.Count(row => double.IsNaN(row.Width));
        stretch.Follow(list, texts);
        root.Width += SteppedWidth.Step;
        Lay(root);
        check("row stretch: a step lays out again only the texts in view, once", CountedText.Measures > 0 && CountedText.Measures <= following * 2);
        root.Width -= SteppedWidth.Step;
        stretch.Follow(list, texts);
        Lay(root);

        // The page grows taller: the rows it brings into view take its width before they show.
        for (int i = 0; i < 12; i++)
        {
            stretch.Follow(list, texts);
            root.Height += 10;
            Lay(root);
        }
        check("row stretch: rows coming into view are let go", rows.Where(InView).All(row => double.IsNaN(row.Width) && row.ActualWidth == before + 12));

        // Released, everything takes the exact width again.
        stretch.Release(texts);
        Lay(root);
        check("row stretch: at rest every row and text has the page's exact width", rows.All(row => double.IsNaN(row.Width) && row.ActualWidth == before + 12)
            && texts.All(text => !text.Stepping && text.Child is FrameworkElement child && child.ActualWidth == text.ActualWidth) && !stretch.Active
            && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);

        // A control page without it: every text of every row is laid out at every step.
        var (plainRoot, _, _, _) = Page(40);
        Lay(plainRoot);
        CountedText.Measures = 0;
        for (int i = 1; i <= 12; i++) { plainRoot.Width += 1; Lay(plainRoot); }
        check("row stretch: without it every row's texts are laid out at every step", CountedText.Measures >= 40 * 2 * 12);
    }

    // A page like the recent notices: a scrolling list of rows with an icon plate, a trimmed title and body, and the time.
    private static (Border Root, ScrollViewer Scroll, StackPanel List, List<SteppedWidth> Texts) Page(int count)
    {
        var list = new StackPanel();
        var texts = new List<SteppedWidth>();
        for (int i = 0; i < count; i++)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new Border { Width = 34, Height = 34, Margin = new Thickness(0, 1, 10, 0) });
            var lines = new StackPanel();
            lines.Children.Add(new CountedText("Notice " + i + ": a title long enough to be trimmed when the panel is narrow", 12.5));
            lines.Children.Add(new CountedText("Body of notice " + i + ", with text that is trimmed as the width changes during the drag.", 11.5));
            var column = new SteppedWidth { Child = lines };
            Grid.SetColumn(column, 1); grid.Children.Add(column);
            var time = new TextBlock { Text = "16 min", FontSize = 10.5, Margin = new Thickness(8, 1, 0, 0) };
            Grid.SetColumn(time, 2); grid.Children.Add(time);
            list.Children.Add(new Border { Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(0, 0, 0, 2), Child = grid });
            texts.Add(column);
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return (new Border { Width = 460, Height = 260, Child = scroll }, scroll, list, texts);
    }

    private static void Lay(FrameworkElement root)
    {
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
    }
}
