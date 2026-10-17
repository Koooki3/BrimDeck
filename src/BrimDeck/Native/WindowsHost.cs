using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck.Native;

public sealed class WindowsHost : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public event Action<ScreenContext>? ContextChanged;
    public ScreenContext Current { get; private set; }
    // monitor gives the monitor whose foreground window decides the context.
    public WindowsHost(Func<IntPtr> monitor)
    {
        _timer.Tick += (_, _) =>
        {
            var context = DetectContext(monitor());
            if (context == Current) return;
            Current = context; ContextChanged?.Invoke(context);
        };
        _timer.Start();
    }
    public static IntPtr PrimaryMonitor() => MonitorFromPoint(new POINT(), 1);
    public static (Rect Bounds, Rect Work, double Scale) PrimaryScreen() => Screen(PrimaryMonitor());
    // Null when the handle no longer names a connected monitor.
    public static (Rect Bounds, Rect Work, double Scale)? MonitorScreen(IntPtr monitor)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? Screen(monitor) : null;
    }
    // Moves the window without resizing it, in one step and in device pixels.
    public static void MoveTo(Window window, int x, int y)
        => SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
    // The monitor under the mouse pointer, where a window opened with CenterScreen appears.
    public static (Rect Bounds, Rect Work, double Scale) CursorScreen()
    { GetCursorPos(out var point); return Screen(MonitorFromPoint(point, 2)); }
    // The work area of the monitor nearest to a point given in device pixels.
    public static Rect WorkAreaAt(Point point) => Screen(MonitorFromPoint(new POINT { X = (int)point.X, Y = (int)point.Y }, 2)).Work;
    // The monitor that holds most of the window; the handle tells whether the window has moved to another monitor.
    public static (IntPtr Monitor, Rect Bounds, Rect Work, double Scale) WindowScreen(Window window)
    {
        var monitor = MonitorFromWindow(new WindowInteropHelper(window).Handle, 2);
        var (bounds, work, scale) = Screen(monitor);
        return (monitor, bounds, work, scale);
    }
    private static (Rect Bounds, Rect Work, double Scale) Screen(IntPtr monitor)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        GetDpiForMonitor(monitor, 0, out var dpi, out _);
        return (info.Monitor.ToRect(), info.Work.ToRect(), dpi > 0 ? dpi / 96.0 : 1);
    }
    public static Point Cursor()
    { GetCursorPos(out var point); return new Point(point.X, point.Y); }
    // Only a foreground window on the given monitor counts; each panel follows the windows on its own monitor.
    public static ScreenContext DetectContext(IntPtr monitor)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground) || IsIconic(foreground)) return ScreenContext.Desktop;
        GetWindowThreadProcessId(foreground, out var pid);
        if (pid == Environment.ProcessId) return ScreenContext.Desktop;
        if (MonitorFromWindow(foreground, 2) != monitor) return ScreenContext.Desktop;
        var name = new System.Text.StringBuilder(256);
        GetClassName(foreground, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return ScreenContext.Desktop;
        if (SHQueryUserNotificationState(out var state) == 0 && state == 3) return ScreenContext.Exclusive;
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        if (DwmGetWindowAttribute(foreground, 9, out var rect, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(foreground, out rect);
        var style = GetWindowLongPtr(foreground, -16).ToInt64();
        bool caption = (style & 0x00C00000) != 0;
        bool covers = rect.Left <= info.Monitor.Left + 2 && rect.Top <= info.Monitor.Top + 2 && rect.Right >= info.Monitor.Right - 2 && rect.Bottom >= info.Monitor.Bottom - 2;
        return ScreenPolicy.Classify(false, covers, caption, IsZoomed(foreground));
    }
    public static void ConfigureOverlay(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr(style | 0x08000000)); // WS_EX_NOACTIVATE: hover must not steal focus.
    }
    public void Dispose() => _timer.Stop();
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT
    { public int Left, Top, Right, Bottom; public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top); }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public int Flags; }
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int max);
    [DllImport("user32")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("shell32")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("dwmapi")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out RECT rect, int size);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);}
