using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck;

// What the ambient light, the answers to the pointer and the panel's other loops need to know about Windows besides the
// settings: whether the session is locked or away, whether battery saver is on, whether the display is off, and when high
// contrast or Windows' "animation effects" change. Changed is raised on the UI thread.
internal static class MotionEnvironment
{
    public static bool Locked { get; private set; }
    public static bool BatterySaver { get; private set; }
    // The power plan turned the display off; the session need not be locked (many turn the screen off after minutes and never
    // lock), and nothing on it can be seen.
    public static bool DisplayOff { get; private set; }
    public static event Action? Changed;
    private static Dispatcher? _dispatcher;
    private static HwndSource? _power;
    private static IntPtr _displayNotification;

    public static void Install(Dispatcher dispatcher)
    {
        if (_dispatcher is not null) return;
        _dispatcher = dispatcher;
        // Started on a locked session (a scheduled start, a restart while away), the light waits for the unlock.
        Locked = SessionLocked() ?? false;
        BatterySaver = ReadBatterySaver();
        SystemEvents.SessionSwitch += SessionSwitch;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        SystemParameters.StaticPropertyChanged += SystemParameterChanged;
        WatchDisplay();
    }

    public static void Uninstall()
    {
        if (_dispatcher is null) return;
        SystemEvents.SessionSwitch -= SessionSwitch;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameterChanged;
        if (_displayNotification != IntPtr.Zero) { UnregisterPowerSettingNotification(_displayNotification); _displayNotification = IntPtr.Zero; }
        _power?.Dispose(); _power = null;
        _dispatcher = null; Changed = null;
    }

    private static void SessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        var reason = e.Reason;
        _dispatcher?.BeginInvoke(() =>
        {
            bool? locked = reason switch
            {
                SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect => true,
                SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon => false,
                // Back in front of a screen (a remote desktop connecting, a switch back to this session), perhaps at its lock
                // screen: Windows says which. No unlock follows a remote connection that signs straight in.
                SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect => SessionLocked() ?? false,
                _ => null
            };
            Set(locked, null);
        });
    }

    private static void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) return;
        _dispatcher?.BeginInvoke(() => { bool saver = ReadBatterySaver(); if (saver == BatterySaver) return; BatterySaver = saver; Changed?.Invoke(); });
    }

    // Raised by WPF when Windows changes a setting; passed on from the UI thread like every other change.
    private static void SystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation)) _dispatcher?.BeginInvoke(() => Changed?.Invoke());
    }

    private static void Set(bool? locked, bool? displayOff)
    {
        bool changed = false;
        if (locked is { } l && l != Locked) { Locked = l; changed = true; }
        if (displayOff is { } d && d != DisplayOff) { DisplayOff = d; changed = true; }
        if (changed) Changed?.Invoke();
    }

    // The display's state comes as WM_POWERBROADCAST to a window that asked for it: a hidden window of its own, which also
    // gets the current state at once. GUID_SESSION_DISPLAY_STATUS is the display of this session, so a remote desktop
    // session is not taken for off while the machine's own monitor sleeps.
    private static void WatchDisplay()
    {
        try
        {
            _power = new HwndSource(new HwndSourceParameters("BrimDeck power") { WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80 | 0x08000000, Width = 0, Height = 0 });
            _power.AddHook(PowerMessage);
            var setting = SessionDisplayStatus;
            _displayNotification = RegisterPowerSettingNotification(_power.Handle, ref setting, 0);
        }
        catch (Exception ex) when (ex is Win32Exception or EntryPointNotFoundException) { _power?.Dispose(); _power = null; }
    }

    private static IntPtr PowerMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int PowerBroadcast = 0x0218, PowerSettingChange = 0x8013;
        if (message != PowerBroadcast || wParam != PowerSettingChange || lParam == IntPtr.Zero) return IntPtr.Zero;
        // POWERBROADCAST_SETTING: the setting's GUID, the data's length, then the data: 0 off, 1 on, 2 dimmed (still seen).
        if (Marshal.PtrToStructure<Guid>(lParam) != SessionDisplayStatus || Marshal.ReadInt32(lParam, 16) < 4) return IntPtr.Zero;
        bool off = Marshal.ReadInt32(lParam, 20) == 0;
        // Coming back on, the lock state is asked again, in case an unlock went by while the light was stopped.
        Set(off ? null : SessionLocked() ?? Locked, off);
        return IntPtr.Zero;
    }

    // Whether this session's lock screen is up (WTSSessionInfoEx's session flags), or null when Windows does not say.
    // NoticeQuiet asks the same question for the notices.
    internal static bool? SessionLocked()
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

    // Battery saver (energy saver on Windows 11): SYSTEM_POWER_STATUS.SystemStatusFlag is 1 while it is on.
    private static bool ReadBatterySaver() => GetSystemPowerStatus(out var status) && (status.SystemStatusFlag & 1) != 0;
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    [DllImport("user32")] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
    [DllImport("user32")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("wtsapi32", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32")] private static extern void WTSFreeMemory(IntPtr memory);

    // The conditions shared by every window; the window adds whether it is on screen.
    public static MotionConditions Conditions(bool visible = true, bool suppressed = false, bool poweringOff = false, bool hasMaterial = true) => new(
        (Application.Current as App)?.Settings.Animations != false, SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast,
        visible, suppressed, poweringOff, Locked, BatterySaver, hasMaterial, DisplayOff);
}
