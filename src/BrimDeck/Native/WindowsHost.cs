using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck.Native;

public sealed class WindowsHost : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Func<IntPtr> _monitor;
    // The app the context was last detected for: the empty pair on the desktop. The app rules resolve by it.
    private string _appName = "", _appPath = "";
    public (string Name, string Path) App => (_appName, _appPath);
    // The foreground window changes rarely but is polled several times a second, so its process is read once per window.
    private IntPtr _identityWindow;
    private uint _identityPid;
    private string _identityName = "", _identityPath = "";
    public event Action<ScreenContext>? ContextChanged;
    // The foreground app changed while the context stayed, carrying the app the last answer was resolved for. The app
    // rules may now answer differently for the same context, but nothing about the panel's own state needs to change —
    // the panel does not fold away for an app switch, only for a context switch.
    public event Action<(string Name, string Path)>? AppChanged;
    // A panel that took the foreground for the keyboard (MainWindow.Hotkeys.cs). While it is the foreground window the user's
    // application has not changed: every panel keeps the context and app it had, so nothing closes or hides because of it.
    public static IntPtr KeyboardPanel { get => s_keyboardPanel; set => s_keyboardPanel = value; }
    private static volatile nint s_keyboardPanel;
    public ScreenContext Current { get; private set; }
    // monitor gives the monitor whose foreground window decides the context. The context is reported with the app it was
    // detected for, and a change of either is a change: two apps can read as the same context with different app rules.
    public WindowsHost(Func<IntPtr> monitor)
    {
        _monitor = monitor;
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }
    private string _raisedName = "", _raisedPath = "";
    private void Poll()
    {
        var context = DetectNow();
        var change = ScreenPolicy.WhatChanged(context, _appName, _appPath, Current, _raisedName, _raisedPath);
        if (change == ScreenPolicy.ForegroundChange.None) return;
        var previous = (_raisedName, _raisedPath);
        Current = context; _raisedName = _appName; _raisedPath = _appPath;
        if (change == ScreenPolicy.ForegroundChange.Context) ContextChanged?.Invoke(context);
        else AppChanged?.Invoke(previous);
    }
    // The context right now, without raising ContextChanged; a caller applies it itself, for instance at startup.
    public ScreenContext DetectNow() => Detect(_monitor());
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
    // Puts a window directly beneath another, without moving, sizing or activating it.
    public static void PlaceBelow(IntPtr window, IntPtr above)
        => SetWindowPos(window, above, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
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
    private ScreenContext Detect(IntPtr monitor)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground) || IsIconic(foreground)) { _appName = ""; _appPath = ""; return ScreenContext.Desktop; }
        if (foreground == KeyboardPanel) return Current;
        GetWindowThreadProcessId(foreground, out var pid);
        if (pid == Environment.ProcessId) { _appName = ""; _appPath = ""; return ScreenContext.Desktop; }
        if (MonitorFromWindow(foreground, 2) != monitor) { _appName = ""; _appPath = ""; return ScreenContext.Desktop; }
        var name = new System.Text.StringBuilder(256);
        GetClassName(foreground, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") { _appName = ""; _appPath = ""; return ScreenContext.Desktop; }
        // The window's app travels with its context, so the app rules can answer for it; the read is cached per window.
        Describe(foreground, pid, out _appName, out _appPath);
        if (SHQueryUserNotificationState(out var state) == 0 && state == 3) return ScreenContext.Exclusive;
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        if (DwmGetWindowAttribute(foreground, 9, out var rect, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(foreground, out rect);
        var style = GetWindowLongPtr(foreground, -16).ToInt64();
        bool caption = (style & 0x00C00000) != 0;
        bool covers = rect.Left <= info.Monitor.Left + 2 && rect.Top <= info.Monitor.Top + 2 && rect.Right >= info.Monitor.Right - 2 && rect.Bottom >= info.Monitor.Bottom - 2;
        return ScreenPolicy.Classify(false, covers, caption, IsZoomed(foreground));
    }
    // A window's process name and executable, read once per window. Both reads can fail when the process exited, runs as
    // administrator or has another bitness; the name alone still matches a saved exception.
    private void Describe(IntPtr window, uint pid, out string processName, out string imagePath)
    {
        if (window != _identityWindow || pid != _identityPid)
        {
            _identityWindow = window; _identityPid = pid; _identityName = ""; _identityPath = "";
            try
            {
                using var process = Process.GetProcessById((int)pid);
                _identityName = process.ProcessName;
                try { _identityPath = process.MainModule?.FileName ?? ""; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        }
        processName = _identityName; imagePath = _identityPath;
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
