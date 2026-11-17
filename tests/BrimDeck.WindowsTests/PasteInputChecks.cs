using System.Diagnostics;
using BrimDeck.Native;

// Telling a window that takes no paste from an ordinary program: one whose program has more rights. The checks read the rights
// of this process and of a system process; nothing is sent to any window.
internal static class PasteInputChecks
{
    public static void Run(Action<string, bool> check)
    {
        using var self = Process.GetCurrentProcess();
        int own = PasteInput.IntegrityLevel(self.Handle, out bool denied);
        Console.WriteLine($"MEASURE: this process's integrity level 0x{own:X}");
        check("Paste: this process's rights can be read", own is >= 0x1000 and <= 0x4000 && !denied);
        check("Paste: this process has no more rights than itself", !PasteInput.HasMoreRights((uint)Environment.ProcessId));
        // The session's logon process runs as the system, above an administrator.
        var system = Process.GetProcessesByName("winlogon");
        try
        {
            if (system.Length == 0) Console.WriteLine("EXCLUDED: no winlogon process to compare with.");
            else check("Paste: a system process has more rights than an ordinary one", own >= 0x3000 || PasteInput.HasMoreRights((uint)system[0].Id));
        }
        finally { foreach (var process in system) process.Dispose(); }
    }
}
