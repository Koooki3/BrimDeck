using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BrimDeck.Native;

internal static class FileDropTarget
{
    // Inspect the window under the drop, not the foreground window (the panel
    // does not activate). Never consume entries for our own category drops or
    // Explorer's copies/shortcuts. Unknown targets are conservatively retained.
    public static bool IsExternalCopyTarget()
    {
        if (!GetCursorPos(out var point)) return false;
        GetWindowThreadProcessId(WindowFromPoint(point), out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return !process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
}
