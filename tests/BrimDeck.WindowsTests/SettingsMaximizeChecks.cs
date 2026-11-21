using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BrimDeck.Native;

// A maximized settings window fills the work area of its screen, on every screen of this machine, at that screen's resolution
// and scale: not past the screen's edges, and not under the taskbar (issue #368). The window is made fully transparent before
// it is shown, so nothing appears, and it is not listed on the taskbar.
internal static class SettingsMaximizeChecks
{
    public static void Run(Action<string, bool> check)
    {
        // The app is per-monitor aware (PerMonitorV2 in its manifest); this test program is not. Its windows are made the
        // app's way, so every screen is seen in its own pixels and scale rather than scaled from the primary screen's.
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        try { RunPerMonitor(check); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    private static void RunPerMonitor(Action<string, bool> check)
    {
        var screens = new List<(IntPtr Monitor, RECT Bounds, RECT Work)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info)) screens.Add((monitor, info.Monitor, info.Work));
            return true;
        }, IntPtr.Zero);
        check("This machine has a screen to maximize on", screens.Count > 0);
        foreach (var (monitor, bounds, work) in screens)
        {
            uint dpi = GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 ? x : 96;
            string name = $"{bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top} at {dpi * 100 / 96}%, work area {work.Left},{work.Top} {work.Right - work.Left}x{work.Bottom - work.Top}";
            var window = new Window { Width = 400, Height = 300, ShowInTaskbar = false, ShowActivated = false };
            SettingsChrome.Apply(window);
            var handle = new WindowInteropHelper(window).EnsureHandle();
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x00080000); // GWL_EXSTYLE, WS_EX_LAYERED
            SetLayeredWindowAttributes(handle, 0, 0, 2); // LWA_ALPHA, fully transparent
            // Placed on this screen before it is maximized, so Windows maximizes it there.
            SetWindowPos(handle, IntPtr.Zero, work.Left + 40, work.Top + 40, 400, 300, 0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
            try
            {
                window.Show();
                // WPF does not show a window maximized without activating it; Windows maximizes it here, as the maximize button does.
                ShowWindow(handle, 3); // SW_MAXIMIZE
                window.UpdateLayout();
                GetWindowRect(handle, out var rect);
                bool fits = rect.Left == work.Left && rect.Top == work.Top && rect.Right == work.Right && rect.Bottom == work.Bottom;
                if (!fits) Console.WriteLine($"  window {rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
                check($"A maximized settings window fills the work area of its screen ({name})", fits);
                var scale = VisualTreeHelperDpi(window);
                check($"Its content is laid out for the work area, not for the whole screen ({name})",
                    Math.Abs(window.ActualHeight * scale - (work.Bottom - work.Top)) <= 1 && Math.Abs(window.ActualWidth * scale - (work.Right - work.Left)) <= 1);
                window.WindowState = WindowState.Normal;
                GetWindowRect(handle, out rect);
                check($"Restored, it goes back to its normal size ({name})", rect.Right - rect.Left < work.Right - work.Left);
            }
            finally { window.Close(); }
        }
    }

    private static double VisualTreeHelperDpi(Window window) => System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleY;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32")] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);
    [DllImport("user32")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
