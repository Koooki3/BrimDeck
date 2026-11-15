using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// Tells when an application asks for attention on the taskbar (its button flashes): Windows posts the shell hook message
// HSHELL_FLASH, with the flashing window, to a window registered with RegisterShellHookWindow. That is all it reads: the window and
// the file name of its process (ChatFlashFilter decides what it means). No timer, no polling, nothing read from the
// application. The window lives on the UI thread, so Flashed is raised there. Dispose deregisters and destroys it; while it is
// registered Windows also posts the shell's other window events to it (a window created, activated, closed), which are ignored
// at once without looking at them.
internal sealed class TaskbarFlashWatch : IDisposable
{
    private const int HShellFlash = 0x8006;
    private readonly HwndSource _window;
    private readonly uint _shellHook;
    private readonly ChatFlashFilter _filter;
    private readonly Action<NoticeInput> _flashed;
    private bool _disposed;

    public TaskbarFlashWatch(Action<NoticeInput> flashed)
    {
        _flashed = flashed;
        _filter = new ChatFlashFilter(ImageName, InApplication, () => DateTimeOffset.UtcNow);
        _shellHook = RegisterWindowMessage("SHELLHOOK");
        // A hidden pop-up window that is never shown (the tool window style keeps it off the taskbar): a message-only window
        // is not sent shell hook messages.
        _window = new HwndSource(new HwndSourceParameters("BrimDeck.TaskbarFlash") { WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80 | 0x08000000, Width = 0, Height = 0 });
        _window.AddHook(Hook);
        if (_shellHook == 0 || !RegisterShellHookWindow(_window.Handle))
        {
            int error = Marshal.GetLastWin32Error();
            _window.Dispose();
            throw new Win32Exception(error);
        }
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed || (uint)message != _shellHook || wParam.ToInt64() != HShellFlash) return IntPtr.Zero;
        try { if (_filter.OnFlash(lParam) is { } input) _flashed(input); }
        // A process that ended meanwhile, or one that cannot be opened, is no notice.
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException) { }
        return IntPtr.Zero;
    }

    private static string? ImageName(nint window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        return pid == 0 || pid == Environment.ProcessId ? null : System.IO.Path.GetFileName(AppIdentity.ImagePath((int)pid));
    }

    // The foreground window is the flashing one or belongs to the same process.
    private static bool InApplication(nint window)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return false;
        if (foreground == window) return true;
        GetWindowThreadProcessId(window, out uint flashing);
        GetWindowThreadProcessId(foreground, out uint front);
        return flashing != 0 && flashing == front;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeregisterShellHookWindow(_window.Handle);
        _window.RemoveHook(Hook);
        _window.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterShellHookWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
