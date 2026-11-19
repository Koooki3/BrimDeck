using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

// The 2026-11-19 report: at 125% the What's new window changed height on every frame. Its Close button's label asked for one
// device pixel more to centre its letters, layout rounding grew the button by two, and the label then dropped the pixel and
// asked again, without end. A window sized to its content followed the button each time.
internal static class InkLabelSettleTests
{
    [DllImport("user32")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref Rect32 lParam);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out Rect32 rect);
    [StructLayout(LayoutKind.Sequential)] private struct Rect32 { public int Left, Top, Right, Bottom; }

    public static void Run(Action<string, bool> check)
    {
        foreach (int dpi in new[] { 120, 216 })
            foreach (var text in new[] { "关闭", "取消", "Close" })
            {
                int changes = Changes(dpi, text, out double scale);
                Console.WriteLine($"  ({text} at {scale:0.##}x: {changes} size changes)");
                check($"a window sized to a settings button \"{text}\" settles at {dpi * 100 / 96}%", scale == dpi / 96.0 && changes < 10);
            }
    }

    // How often the window changed size within half a second of being shown, told it is at dpi; counting stops at 10.
    private static int Changes(int dpi, string text, out double scale)
    {
        var button = new Button { Content = text, FontSize = 12, Padding = new Thickness(10, 5, 10, 5), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Right };
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.ContentTemplateSelectorProperty, InkCentredLabel.Selector);
        chrome.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 24) };
        panel.Children.Add(new TextBlock { Text = "更新内容", FontSize = 21, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(button);
        // The settings window's own fonts, which the report's window used at 125%.
        var window = new Window { Width = 300, SizeToContent = SizeToContent.Height, UseLayoutRounding = true, Content = panel, FontFamily = Fonts.PlainOf(UiFontRole.Panel),
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000 };
        int changes = 0;
        // A window that keeps changing size never lets the timer below run (layout outranks it), so the count also ends the wait.
        var wait = new DispatcherFrame();
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            GetWindowRect(handle, out var rect);
            // WPF lays the window out at the DPI this message names, whatever the display's own scale.
            SendMessage(handle, 0x02E0, (IntPtr)((dpi << 16) | dpi), ref rect);
        };
        // Counted from the first layout: sizing to the content and the DPI message take a few changes before the window settles.
        window.SizeChanged += (_, _) => { if (++changes >= 10) wait.Continue = false; };
        try
        {
            window.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => { timer.Stop(); wait.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(wait);
            scale = VisualTreeHelperDpi(window);
            return changes;
        }
        finally { window.Close(); }
    }

    private static double VisualTreeHelperDpi(System.Windows.Media.Visual visual) => System.Windows.Media.VisualTreeHelper.GetDpi(visual).DpiScaleY;
}
