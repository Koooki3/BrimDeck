using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Native;

// The music page's source menu (issue #401) hangs below its button with the two right edges in line, as the product places it
// (SourceMenuPlacement), on a real window on the secondary display: in the middle of the screen, against its right edge and
// against its bottom edge (where there is no room below, it opens above instead), at 100% and 150% panel scale, and with
// Windows opening menus left- or right-aligned (SystemParameters.MenuDropAlignment, forced in WPF's own cache and put back).
internal static class SourceMenuChecks
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private const uint NoSize = 0x1, NoZOrder = 0x4, NoActivate = 0x10;

    private static RECT Bounds(Visual visual)
    {
        GetWindowRect(((HwndSource)PresentationSource.FromVisual(visual)).Handle, out var rect);
        return rect;
    }

    private static void Settle() { for (int i = 0; i < 4; i++) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }

    private static readonly ControlTemplate Plain = (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate TargetType=\"ContextMenu\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
        "<Border Background=\"#202020\" BorderBrush=\"#404040\" BorderThickness=\"1\" Padding=\"3\"><ItemsPresenter/></Border></ControlTemplate>");

    public static void Run(Action<string, bool> check)
    {
        var display = Displays.Current().FirstOrDefault(d => !d.Primary);
        check("source menu: a secondary display is there to open the windows on", display is not null);
        if (display is null) return;
        var screen = display.Bounds;
        foreach (bool right in new[] { false, true })
        {
            bool forced = PopupGhostChecks.Force(right, out bool before);
            try
            {
                check($"source menu, {(right ? "right" : "left")}-aligned menus: the setting could be set for the check", forced);
                if (!forced) continue;
                foreach (double scale in new[] { 1d, 1.5 })
                    foreach (string where in new[] { "middle", "right edge", "bottom edge" })
                        Case(check, screen, right, scale, where);
            }
            finally { PopupGhostChecks.Force(before, out _); }
        }
    }

    private static void Case(Action<string, bool> check, Rect screen, bool rightAligned, double scale, string where)
    {
        string name = $"source menu, {(rightAligned ? "right" : "left")}-aligned menus, {scale * 100:0}%, {where}";
        var button = new Button { Width = 115, Height = 24, Content = "QQ", HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 10, 0) };
        var content = new Grid { Children = { button } };
        UI.Scale(content, scale);
        var window = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Width = 460 * scale, Height = 60 * scale,
            ShowInTaskbar = false, ShowActivated = false, Content = content, Left = -32000, Top = -32000 };
        ContextMenu? menu = null;
        try
        {
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            SetWindowPos(hwnd, IntPtr.Zero, (int)(screen.Left + screen.Width / 2), (int)(screen.Top + screen.Height / 2), 0, 0, NoSize | NoZOrder | NoActivate);
            Settle();
            GetWindowRect(hwnd, out var size);
            int w = size.Right - size.Left, h = size.Bottom - size.Top;
            (int x, int y) = where switch
            {
                "right edge" => ((int)screen.Right - w, (int)(screen.Top + screen.Height / 3)),
                "bottom edge" => ((int)(screen.Left + (screen.Width - w) / 2), (int)screen.Bottom - h),
                _ => ((int)(screen.Left + (screen.Width - w) / 2), (int)(screen.Top + screen.Height / 3)),
            };
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, NoSize | NoZOrder | NoActivate);
            Settle();
            // As the product opens it (MainWindow.MediaSource_Click, then ResizeSourceMenu).
            menu = new ContextMenu { Template = Plain, HasDropShadow = false, UseLayoutRounding = true };
            for (int i = 0; i < 3; i++) menu.Items.Add(new MenuItem { Header = $"QQ Music - Feels Like You {i}", Height = 24 });
            button.ContextMenu = menu;
            SourceMenuPlacement.Attach(menu, button, scale);
            UI.Scale(menu, scale);
            menu.Width = 210;
            menu.IsOpen = true;
            Settle();
            var list = Bounds(menu);
            var topLeft = button.PointToScreen(new Point());
            var bottomRight = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
            // One DIP of the screen in pixels, the tolerance; the gap is the panel's own DIPs, scaled with it.
            double dip = VisualTreeHelper.GetDpi(button).DpiScaleX, tolerance = dip + 0.5, gap = SourceMenuPlacement.Gap * scale * dip;
            bool roomBelow = bottomRight.Y + (list.Bottom - list.Top) + gap <= screen.Bottom;
            check($"{name}: right edges in line (button {bottomRight.X:0}, menu {list.Right})", Math.Abs(list.Right - bottomRight.X) <= tolerance);
            check(roomBelow ? $"{name}: {gap:0.#} px below the button (button bottom {bottomRight.Y:0}, menu top {list.Top})"
                : $"{name}: {gap:0.#} px above the button where there is no room below (button top {topLeft.Y:0}, menu bottom {list.Bottom})",
                Math.Abs((roomBelow ? list.Top - bottomRight.Y : topLeft.Y - list.Bottom) - gap) <= tolerance);
            check($"{name}: on the screen ({list.Left}..{list.Right}, {list.Top}..{list.Bottom})",
                list.Left >= screen.Left && list.Right <= screen.Right && list.Top >= screen.Top && list.Bottom <= screen.Bottom);
        }
        finally
        {
            if (menu is not null) menu.IsOpen = false;
            window.Close();
            Settle();
        }
    }
}
