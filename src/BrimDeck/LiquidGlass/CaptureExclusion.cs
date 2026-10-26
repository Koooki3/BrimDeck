using System.Runtime.InteropServices;

namespace BrimDeck.LiquidGlass;

// Which of BrimDeck's windows Desktop Duplication leaves out (WCA_EXCLUDED_FROM_DDA), and for how long: only while liquid glass
// is in use (a DesktopSource opened for an island's glass, or automatic liquid glass's first look being measured), and only the
// windows that can lie over the island: the island, its glass window, notices, and the pop-ups of those (menus, tooltips,
// drop-down lists). The settings window and its pop-ups, and every ordinary window, stay in the capture. Windows shown while it
// is held are left out as they appear (a WinEvent hook on this process's EVENT_OBJECT_SHOW). When the last user lets go (the
// theme changed, liquid glass fell back to the blur, the island closed), every window left out is put back (the attribute set
// to 0 again) and the hook removed.
//
// While held, anything recording the screen through DXGI Desktop Duplication (a recorder's "display capture" in its DXGI mode,
// some remote desktop tools) does not show the island: it is missing from such recordings while liquid glass is shown.
// Screenshots, BitBlt and Windows.Graphics.Capture still show it.
//
// UI thread only: the hook's callbacks come through the installing thread's message loop.
internal static class CaptureExclusion
{
    private static int _users;
    private static IntPtr _hook;
    private static readonly HashSet<IntPtr> Excluded = [];

    // How many hold it now, and whether the hook is installed (for the tests).
    public static int Users => _users;
    public static bool Hooked => _hook != IntPtr.Zero;
    public static bool Holds(IntPtr hwnd) => Excluded.Contains(hwnd);

    // Holds the exclusion: the required windows (the island's, its glass window's) must be left out, or nothing is held and
    // false returned; the others (our panels already on screen) are left out where Windows takes it. On the first hold every
    // window of ours that can lie over the island is left out too, and the hook installed.
    public static bool Acquire(IntPtr[] required, IntPtr[]? also = null)
    {
        foreach (var window in required)
            if (!Exclude(window))
            {
                if (_users == 0) PutBack();
                return false;
            }
        if (also is not null) foreach (var window in also) Exclude(window);
        if (_users++ == 0)
        {
            EnumWindows(OurWindow, IntPtr.Zero);
            _hook = SetWinEventHook(ObjectShow, ObjectShow, IntPtr.Zero, Shown, (uint)Environment.ProcessId, 0, 0);
        }
        return true;
    }

    // Lets go of one hold; the last one puts every window back in the capture and removes the hook.
    public static void Release()
    {
        if (_users == 0 || --_users > 0) return;
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        PutBack();
    }

    private static void PutBack()
    {
        foreach (var window in Excluded) if (IsWindow(window)) Set(window, 0);
        Excluded.Clear();
    }

    // Leaves one window out: false where Windows refused it or does not keep it (the attribute read back is off), as the window
    // would then be in the capture, refracted under itself.
    public static bool Exclude(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !Set(hwnd, 1)) return false;
        if (Read(hwnd) == false) return false;
        Excluded.Add(hwnd);
        return true;
    }

    // Whether Windows leaves the window out now; null where it does not answer for the attribute.
    public static bool? Read(IntPtr hwnd)
    {
        var value = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            // A value Windows did not write (-1) says nothing either way.
            Marshal.WriteInt32(value, -1);
            var data = new CompositionAttribute { Attribute = ExcludedFromDda, Data = value, Size = sizeof(int) };
            if (GetWindowCompositionAttribute(hwnd, ref data) == 0) return null;
            int read = Marshal.ReadInt32(value);
            return read == -1 ? null : read != 0;
        }
        finally { Marshal.FreeHGlobal(value); }
    }

    private static bool Set(IntPtr hwnd, int on)
    {
        var value = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(value, on);
            var data = new CompositionAttribute { Attribute = ExcludedFromDda, Data = value, Size = sizeof(int) };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally { Marshal.FreeHGlobal(value); }
    }

    // Kept for the life of the process: Windows calls them back.
    private static readonly EnumWindowsProc OurWindow = static (hwnd, _) =>
    {
        GetWindowThreadProcessId(hwnd, out uint process);
        if (process == (uint)Environment.ProcessId && Overlay(hwnd)) Exclude(hwnd);
        return true;
    };
    private static readonly WinEventProc Shown = static (_, _, hwnd, objectId, child, _, _) =>
    {
        if (_users > 0 && objectId == 0 && child == 0 && Overlay(hwnd)) Exclude(hwnd);
    };

    // A top-level window that can sit over the island: topmost, a tool window, or one that never activates, and owned (if at
    // all) only by such windows. A pop-up of an ordinary window (the settings window's menus, tooltips and lists) is not.
    internal static bool Overlay(IntPtr hwnd)
    {
        if (GetAncestor(hwnd, 2) != hwnd) return false; // GA_ROOT
        for (var window = hwnd; window != IntPtr.Zero; window = GetWindow(window, 4)) // GW_OWNER
            if (!OverlayStyle(window)) return false;
        return true;
    }

    private static bool OverlayStyle(IntPtr hwnd) => (GetWindowLongPtr(hwnd, -20).ToInt64() & (0x8 | 0x80 | 0x08000000)) != 0; // GWL_EXSTYLE: TOPMOST | TOOLWINDOW | NOACTIVATE

    private const int ExcludedFromDda = 24;
    private const uint ObjectShow = 0x8002;
    [StructLayout(LayoutKind.Sequential)] private struct CompositionAttribute { public int Attribute; public IntPtr Data; public int Size; }
    [DllImport("user32")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttribute data);
    [DllImport("user32")] private static extern int GetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttribute data);
    [DllImport("user32")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr data);
    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int child, uint thread, uint time);
    [DllImport("user32")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32")] private static extern bool UnhookWinEvent(IntPtr hook);
}
