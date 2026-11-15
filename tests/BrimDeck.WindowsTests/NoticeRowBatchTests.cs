using System.Windows.Controls;
using BrimDeck.Core.Notices;

// The list's panel as the notices page fills it (MainWindow.Notices.cs ListColumn): rows are inserted at their index, so the
// footer button ("Show earlier notices"), added before the later rows exist, stays last while the rest arrive.
// What needs a window and is not checked here: scrolling, keyboard focus, the look of the frame in which rows arrive.
internal static class NoticeRowBatchTests
{
    public static void Run(Action<string, bool> check)
    {
        var queue = new Queue<Action>();
        var panel = new StackPanel();
        var items = Enumerable.Range(0, 14).Select(i => "n" + i).ToList();
        var batches = new RowBatches<string>(items, (i, n) => panel.Children.Insert(i, new TextBlock { Text = n }), queue.Enqueue, 3);
        batches.Start(8);
        var footer = new Button { Content = "footer" };
        panel.Children.Add(footer);
        check("The footer comes after the first rows and before the rest exist", panel.Children.Count == 9 && ReferenceEquals(panel.Children[8], footer));
        while (queue.Count > 0) queue.Dequeue()();
        var texts = panel.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
        check("The rows that arrive later go above the footer, in the list's order",
            panel.Children.Count == 15 && ReferenceEquals(panel.Children[14], footer) && texts.SequenceEqual(items));
    }
}
