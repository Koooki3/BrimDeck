using System.Runtime.InteropServices;
using System.Windows.Interop;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Global hotkeys registered on a message-only window that the app owns for its whole run. The panels are closed and opened
// again when monitors change, so their windows cannot hold the registrations; this window has no monitor, no position and
// no DPI, and Explorer restarting does not affect it.
internal sealed class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312, ErrorHotkeyAlreadyRegistered = 1409;
    private readonly HwndSource _source;
    private readonly HashSet<HotkeyKind> _registered = [];
    public event Action<HotkeyKind>? Pressed;

    public GlobalHotkeys()
    {
        _source = new HwndSource(new HwndSourceParameters("BrimDeck.Hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 }); // HWND_MESSAGE
        _source.AddHook(Hook);
    }

    // Registers a combination given as RegisterHotKey's values; Active, InUse when another program holds it, Invalid otherwise.
    public HotkeyStatus Register(HotkeyKind kind, uint fsModifiers, uint vk)
    {
        Unregister(kind);
        if (RegisterHotKey(_source.Handle, Id(kind), fsModifiers, vk)) { _registered.Add(kind); return HotkeyStatus.Active; }
        return Marshal.GetLastWin32Error() == ErrorHotkeyAlreadyRegistered ? HotkeyStatus.InUse : HotkeyStatus.Invalid;
    }

    public void Unregister(HotkeyKind kind)
    {
        if (_registered.Remove(kind)) UnregisterHotKey(_source.Handle, Id(kind));
    }

    // The monitor under the mouse pointer, or the nearest one.
    public static IntPtr CursorMonitor() { GetCursorPos(out var point); return MonitorFromPoint(point, 2); }

    public void Dispose()
    {
        foreach (var kind in _registered.ToList()) Unregister(kind);
        _source.RemoveHook(Hook); _source.Dispose();
    }

    private static int Id(HotkeyKind kind) => 0xB100 + (int)kind;

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey) return IntPtr.Zero;
        foreach (var kind in _registered)
            if (wParam.ToInt32() == Id(kind)) { handled = true; Pressed?.Invoke(kind); break; }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
}
