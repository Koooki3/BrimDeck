using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BrimDeck.Native;

// When Windows asks for quiet: the session is locked or switched to another user, a focus session runs, or presentation
// mode (or the first-run quiet time) is on. A focus session is what FocusSessionManager reports; Windows' separate "do not
// disturb" switch has no public API and is not detected. Changed is raised on the UI thread for the lock and the focus session;
// presentation mode is only read when asked (SHQueryUserNotificationState is cheap).
internal sealed class NoticeQuiet : IDisposable
{
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private Windows.UI.Shell.FocusSessionManager? _focus;
    // Locked: the session's lock screen is up. Away: the session is not on the screen (another user's is, or a remote
    // connection ended). Either keeps notices back.
    private bool _sessionLocked, _away;
    public bool Locked => _sessionLocked || _away;
    public event Action? Changed;

    public NoticeQuiet(System.Windows.Threading.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _sessionLocked = SessionLocked() ?? false;
        SystemEvents.SessionSwitch += SessionSwitch;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            try
            {
                if (Windows.UI.Shell.FocusSessionManager.IsSupported)
                {
                    _focus = Windows.UI.Shell.FocusSessionManager.GetDefault();
                    _focus.IsFocusActiveChanged += FocusChanged;
                }
            }
            catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidCastException or UnauthorizedAccessException) { _focus = null; }
        }
    }

    public bool FocusSupported => _focus is not null;

    public bool FocusActive
    {
        get
        {
            if (_focus is not { } focus || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return false;
            try { return focus.IsFocusActive; } catch (COMException) { return false; }
        }
    }

    // Presentation mode, or the quiet hours after Windows was set up.
    public static bool PresentationOrQuietTime()
    {
        const int Presentation = 4, QuietTime = 6;
        return SHQueryUserNotificationState(out int state) == 0 && state is Presentation or QuietTime;
    }

    private void SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var reason = e.Reason;
        _dispatcher.BeginInvoke(() =>
        {
            bool before = Locked;
            switch (reason)
            {
                case SessionSwitchReason.SessionLock: _sessionLocked = true; break;
                case SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon: _sessionLocked = false; break;
                case SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect: _away = true; break;
                // Back on the screen, but perhaps at its lock screen (switching back to a locked session): Windows says which.
                case SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect: _away = false; _sessionLocked = SessionLocked() ?? _sessionLocked; break;
                default: return;
            }
            if (Locked != before) Changed?.Invoke();
        });
    }

    // While held back for a lock, asks Windows again (from the periodic quiet check), so a missed unlock does not keep
    // notices back for good. The caller recomputes the quiet state.
    public void Recheck()
    {
        if (_sessionLocked && SessionLocked() == false) _sessionLocked = false;
    }

    // Whether this session's lock screen is up (WTSSessionInfoEx's session flags), or null when Windows does not say.
    private static bool? SessionLocked()
    {
        const int SessionInfoEx = 25, Level1 = 1, LockedFlag = 0, UnlockedFlag = 1;
        const uint CurrentSession = uint.MaxValue;
        if (!WTSQuerySessionInformationW(IntPtr.Zero, CurrentSession, SessionInfoEx, out var buffer, out int bytes)) return null;
        try
        {
            // WTSINFOEXW: the level, then (8-byte aligned) the session id, its connect state and its flags.
            if (bytes < 20 || Marshal.ReadInt32(buffer) != Level1) return null;
            return Marshal.ReadInt32(buffer, 16) switch { LockedFlag => true, UnlockedFlag => false, _ => null };
        }
        finally { WTSFreeMemory(buffer); }
    }
    [DllImport("wtsapi32", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32")] private static extern void WTSFreeMemory(IntPtr memory);

    private void FocusChanged(Windows.UI.Shell.FocusSessionManager sender, object args) => _dispatcher.BeginInvoke(() => Changed?.Invoke());

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= SessionSwitch;
        if (_focus is { } focus && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            try { focus.IsFocusActiveChanged -= FocusChanged; } catch (COMException) { }
        _focus = null;
    }

    [DllImport("shell32")] private static extern int SHQueryUserNotificationState(out int state);
}

// Window and monitor calls the notices need besides WindowsHost's.
internal static class NoticeNative
{
    // The monitor of the foreground window, or zero when there is none.
    public static IntPtr ForegroundMonitor()
    {
        var window = GetForegroundWindow();
        return window == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(window, 0);
    }

    // Puts a window above the other always-on-top windows (the panel among them) without activating it.
    public static void BringToTop(IntPtr window) => SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

    // The notice window normally never activates (WS_EX_NOACTIVATE); the notices hotkey lets it take the keyboard.
    public static void SetActivatable(IntPtr window, bool activatable)
    {
        const long NoActivate = 0x08000000;
        long style = GetWindowLongPtr(window, -20).ToInt64();
        SetWindowLongPtr(window, -20, new IntPtr(activatable ? style & ~NoActivate : style | NoActivate));
    }

    public static IntPtr Foreground() => GetForegroundWindow();

    // Gives the keyboard back to the window that had it before the notices hotkey, if this process still has the foreground
    // (Windows lets the foreground process pass it on) and that window still exists.
    public static void ReturnForeground(IntPtr own, IntPtr previous)
    {
        if (previous == IntPtr.Zero || previous == own || !IsWindow(previous) || GetForegroundWindow() != own) return;
        SetForegroundWindow(previous);
    }

    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
