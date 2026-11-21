using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace BrimDeck.Native;

internal static class SettingsChrome
{
    // These DWM attributes are documented only on Windows 11. Windows 10 uses
    // the existing WPF frame and palette, with square outer corners.
    private static readonly bool SupportsDwmStyling = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static void Apply(Window window)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 52, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(SupportsDwmStyling ? 12 : 0), UseAeroCaptionButtons = false
        });
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(KeepMaximizedInWorkArea);
            if (!SupportsDwmStyling) return;
            int round = 2;
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        };
    }

    // Maximized, the window reached past the edges of the screen and under the taskbar, and WindowChrome only clips what shows
    // to the work area, so the bottom of the page and of the list of pages could not be reached (issue #368). A maximized
    // window is given the work area of its screen instead. WM_GETMINMAXINFO cannot do this on every screen: Windows adjusts
    // the maximized size given there from the primary screen to the window's screen.
    private static IntPtr KeepMaximizedInWorkArea(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_WINDOWPOSCHANGING = 0x0046, GWL_STYLE = -16;
        const uint WS_MAXIMIZE = 0x01000000, WS_MINIMIZE = 0x20000000, SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
        if (message != WM_WINDOWPOSCHANGING) return IntPtr.Zero;
        uint style = (uint)GetWindowLong(handle, GWL_STYLE);
        if ((style & WS_MAXIMIZE) == 0 || (style & WS_MINIMIZE) != 0) return IntPtr.Zero;
        var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((position.Flags & (SWP_NOSIZE | SWP_NOMOVE)) == (SWP_NOSIZE | SWP_NOMOVE)) return IntPtr.Zero;
        // Snapped or dragged to another screen, the new position names the screen.
        var target = new RECT { Left = position.X, Top = position.Y, Right = position.X + position.Width, Bottom = position.Y + position.Height };
        IntPtr monitor = (position.Flags & (SWP_NOSIZE | SWP_NOMOVE)) == 0 ? MonitorFromRect(ref target, 2) : MonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var work = info.Work;
        position.X = work.Left; position.Y = work.Top; position.Width = work.Right - work.Left; position.Height = work.Bottom - work.Top;
        position.Flags &= ~(SWP_NOSIZE | SWP_NOMOVE);
        Marshal.StructureToPtr(position, lParam, false);
        return IntPtr.Zero;
    }
    public static void SetTheme(Window window, bool isDark)
    {
        if (!SupportsDwmStyling) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = isDark ? 1 : 0, border = isDark ? 0x00424248 : 0x00D3CBC9;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
    }

    [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWPOS { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
