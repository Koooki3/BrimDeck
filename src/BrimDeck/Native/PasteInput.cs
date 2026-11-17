using System.Runtime.InteropServices;

namespace BrimDeck.Native;

// Pasting into the window the user is typing in: the panel takes no focus when clicked (WindowsHost.OverlayMessages), so that
// window keeps it, and Ctrl+V sent with SendInput reaches it. When the panel did take the focus for typing (the search box, a
// phrase), the window that had it before is given it back first.
internal static class PasteInput
{
    public static IntPtr Foreground => GetForegroundWindow();

    // A window of another process, which can take a paste.
    public static bool IsOther(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return false;
        GetWindowThreadProcessId(window, out uint pid);
        return pid != 0 && pid != Environment.ProcessId;
    }

    // Whether the window's program runs with more rights than this one: as administrator, when BrimDeck does not. Windows
    // drops the keys sent to such a window, and SendInput does not say that it did, so this is asked after a paste was tried.
    public static bool HasMoreRights(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return false;
        GetWindowThreadProcessId(window, out uint pid);
        return pid != 0 && pid != Environment.ProcessId && HasMoreRights(pid);
    }
    internal static bool HasMoreRights(uint pid)
    {
        int own = IntegrityLevel(GetCurrentProcess(), out _);
        IntPtr process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        // A process that may not even be asked about has more rights than this one.
        if (process == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;
        try
        {
            int level = IntegrityLevel(process, out bool denied);
            return level < 0 ? denied : own >= 0 && level > own;
        }
        finally { CloseHandle(process); }
    }
    // The process's integrity level (0x2000 for an ordinary program, 0x3000 for one run as administrator), or -1.
    internal static int IntegrityLevel(IntPtr process, out bool denied)
    {
        denied = false;
        if (!OpenProcessToken(process, 0x0008, out IntPtr token)) { denied = Marshal.GetLastWin32Error() == 5; return -1; } // TOKEN_QUERY
        try
        {
            GetTokenInformation(token, 25, IntPtr.Zero, 0, out int size); // TokenIntegrityLevel
            if (size <= 0) return -1;
            IntPtr label = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, 25, label, size, out _)) return -1;
                IntPtr sid = Marshal.ReadIntPtr(label);
                int parts = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return parts > 0 ? Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(parts - 1))) : -1;
            }
            finally { Marshal.FreeHGlobal(label); }
        }
        finally { CloseHandle(token); }
    }

    public static bool Activate(IntPtr window) => window != IntPtr.Zero && IsWindow(window) && (GetForegroundWindow() == window || SetForegroundWindow(window));

    // Ctrl, Shift, Alt or a Windows key still held from the click would turn Ctrl+V into another shortcut.
    public static bool ModifiersDown => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(KeyDown);
    public static bool KeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public static bool SendPaste()
    {
        INPUT Key(ushort key, uint flags) => new() { Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { Key = key, Flags = flags } } };
        INPUT[] inputs = [Key(0x11, 0), Key(0x56, 0), Key(0x56, 2), Key(0x11, 2)];
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        // Never leave Ctrl held down in the other application.
        if (sent is > 0 and < 4) { INPUT[] release = [Key(0x56, 2), Key(0x11, 2)]; SendInput((uint)release.Length, release, Marshal.SizeOf<INPUT>()); }
        return sent == inputs.Length;
    }

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Key, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr information, int length, out int needed);
    [DllImport("advapi32")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("user32", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
