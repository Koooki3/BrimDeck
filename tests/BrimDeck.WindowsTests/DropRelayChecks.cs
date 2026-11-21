using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Native;

// Files dragged over a lens of TaskbarMirror (DeckSettings.ForceTopmost, or a lifted taskbar) must reach the panel it shows (issue
// 361): the lens is registered as a drop target with WPF's own drop target for the panel's window (DropRelay). Here a plain window
// stands for the lens; OLE's calls on the registered target are made directly with a point on screen over an element of the WPF
// window, which must see the drag and the drop. No real drag, file or input is involved.
internal static class DropRelayChecks
{
    public static void Run(Action<string, bool> check)
    {
        var target = new Border { Width = 120, Height = 80, Background = Brushes.Gray, AllowDrop = true };
        var window = new Window { Left = 100, Top = 100, Width = 300, Height = 200, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Content = target };
        IntPtr lens = IntPtr.Zero;
        try
        {
            window.Show();
            Settle();
            var panel = new WindowInteropHelper(window).Handle;
            // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; WS_POPUP, never shown.
            lens = CreateWindowEx(0x80 | 0x08000000, "STATIC", "lens", 0x80000000, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            check("drop relay: a stand-in lens window is made", lens != IntPtr.Zero);
            if (lens == IntPtr.Zero) return;

            check("drop relay: the lens is registered as a drop target", DropRelay.Register(lens, panel));
            check("drop relay: OLE holds that registration", !DropRelay.Register(lens, panel));

            object? made = DropRelay.Make(panel);
            var calls = made?.GetType().GetInterfaces().FirstOrDefault(type => type.GUID == new Guid("00000122-0000-0000-C000-000000000046"));
            check("drop relay: WPF's drop target is made for the window", made is not null && calls is not null);
            if (made is null || calls is null) return;

            int entered = 0, dropped = 0;
            string[]? files = null;
            target.DragEnter += (_, e) => { entered++; e.Effects = DragDropEffects.Copy; e.Handled = true; };
            target.Drop += (_, e) => { dropped++; files = e.Data.GetData(DataFormats.FileDrop) as string[]; e.Handled = true; };
            var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\nowhere\file.txt" });
            var screen = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
            long point = ((long)(int)Math.Round(screen.Y) << 32) | (uint)(int)Math.Round(screen.X);

            object[] enter = [data, 1, point, (int)DragDropEffects.Copy]; // MK_LBUTTON
            calls.GetMethod("OleDragEnter")!.Invoke(made, enter);
            check("drop relay: the element under the point sees the files come in", entered == 1);
            check("drop relay: the element's answer goes back to the drag", (int)enter[3] == (int)DragDropEffects.Copy);
            object[] drop = [data, 0, point, (int)DragDropEffects.Copy];
            calls.GetMethod("OleDrop")!.Invoke(made, drop);
            check("drop relay: the element under the point takes the files", dropped == 1 && files is [@"C:\nowhere\file.txt"]);

            DropRelay.Revoke(lens);
            check("drop relay: revoking ends the registration", DropRelay.Register(lens, panel));
        }
        finally
        {
            if (lens != IntPtr.Zero) { DropRelay.Revoke(lens); DestroyWindow(lens); }
            window.Close();
        }
    }

    private static void Settle() { for (int i = 0; i < 3; i++) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }

    [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr window);
}
