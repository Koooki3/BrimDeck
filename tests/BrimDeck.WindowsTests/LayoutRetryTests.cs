using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck;

// The 2026-10-28 hang: a retry that posted itself to the dispatcher while its button sat under a collapsed parent spun the UI thread.
internal static class LayoutRetryTests
{
    public static void Run(Action<string, bool> check)
    {
        var button = new Button { Content = "Usage", Width = 60, Height = 20 };
        var parent = new Border { Child = button };
        var window = new Window { Width = 200, Height = 100, ShowInTaskbar = false, ShowActivated = false, Content = parent, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000 };
        try
        {
            window.Show();
            Pump(DispatcherPriority.ApplicationIdle);
            // The button is laid out once, then its parent is collapsed and the button changes: invalid measure, old width.
            parent.Visibility = Visibility.Collapsed;
            button.Width = 80;
            Pump(DispatcherPriority.ApplicationIdle);
            bool ready() => LayoutRetry.Laid(button);
            check("a button under a collapsed parent is not ready although it has a width", !ready() && button.ActualWidth > 0);

            int ran = 0, posted = 0;
            var hooks = Dispatcher.CurrentDispatcher.Hooks;
            DispatcherHookEventHandler count = (_, e) => { if (e.Operation.Priority == DispatcherPriority.Loaded) posted++; };
            hooks.OperationPosted += count;
            LayoutRetry.WhenReady(window, ready, () => ran++);
            // One frame ended by a timer, so the pump itself posts next to nothing while the retry is watched.
            var wait = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) => { timer.Stop(); wait.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(wait);
            hooks.OperationPosted -= count;
            Console.WriteLine($"  (Loaded-priority posts while waiting: {posted})");
            check("waiting for a collapsed parent does not keep posting to the dispatcher", posted < 5);
            check("it has not run while the button cannot be laid out", ran == 0);

            parent.Visibility = Visibility.Visible;
            Pump(DispatcherPriority.ApplicationIdle);
            check("it runs once the button is laid out", ran == 1);
            parent.InvalidateMeasure(); Pump(DispatcherPriority.ApplicationIdle);
            check("and only once", ran == 1);
        }
        finally { window.Close(); }
    }

    private static void Pump(DispatcherPriority priority)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
