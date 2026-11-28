using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;

// The picture a leaving page is replaced by, taken offscreen inside a panel drawn at a scale other than
// 100 % (UI.Scale, inherited by the page). Nothing is shown. The picture must hold as many pixels as the page covers on the
// screen, the monitor's scale times the panel's, and keep the page's size in DIPs, or the leaving page is drawn enlarged from
// fewer pixels and looks blurred for the length of the handover.
internal static class HandoverScaleChecks
{
    public static void Run(Action<string, bool> check)
    {
        const double width = 120, height = 40;
        foreach (var (monitor, panel) in new[] { (1.0, 1.5), (1.25, 1.5), (1.5, 1.25), (1.25, 1.0) })
        {
            var page = new Border { Width = width, Height = height, Background = Brushes.White };
            var host = new Grid();
            host.Children.Add(page);
            UI.Scale(host, panel);
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            var picture = PageHandover.Snapshot(page, width, height, new DpiScale(monitor, monitor));
            int across = (int)Math.Ceiling(width * monitor * panel), down = (int)Math.Ceiling(height * monitor * panel);
            Console.WriteLine($"  monitor {monitor:P0}, panel {panel:P0}: picture {picture.PixelWidth}x{picture.PixelHeight} px, {picture.Width:R}x{picture.Height:R} DIP (page covers {across}x{down} px)");
            check($"handover picture at monitor {monitor:P0} and panel {panel:P0} has the pixels the page covers on screen",
                picture.PixelWidth == across && picture.PixelHeight == down);
            check($"handover picture at monitor {monitor:P0} and panel {panel:P0} keeps the page's size in DIPs",
                Math.Abs(picture.Width - width) < 1e-3 && Math.Abs(picture.Height - height) < 1e-3);
        }
    }
}
