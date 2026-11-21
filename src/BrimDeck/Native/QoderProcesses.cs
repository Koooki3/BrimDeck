using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Whether the Qoder CLI and desktop app that run now were started with the token switch (QoderUsage.TokenSwitch). A process keeps
// the environment it started with, so one started before the switch was set records no tokens until it is opened again. The
// environment is read from the process itself: its PEB names its RTL_USER_PROCESS_PARAMETERS, whose Environment field (offset
// 0x80 on x64) points at the block of "NAME=value" strings, each ending in a null, the block in two. Only processes of the
// current user can be opened, which Qoder's are.
internal static class QoderProcesses
{
    // The programs of the edition that run without the switch: "Qoder CLI", "Qoder desktop app". A program counts once however
    // many processes it runs; one whose environment cannot be read is left out.
    public static List<string> WithoutSwitch(QoderUsage.Edition edition, string userHome)
    {
        var missing = new List<string>();
        var variable = QoderUsage.TokenSwitch(edition);
        var cliFolder = Path.GetDirectoryName(QoderUsage.CliPath(Path.Combine(userHome, edition.China ? ".qoder-cn" : ".qoder"), edition))!;
        var desktop = QoderDesktopReader.Find(edition.China);
        void Check(string processName, Func<string, bool> belongs, string program)
        {
            foreach (var process in Process.GetProcessesByName(processName))
                using (process)
                {
                    string? path;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { continue; }
                    if (path is null || !belongs(path)) continue;
                    // The daemons BrimDeck starts to read the credits are not the user's Qoder.
                    if (Parent(process.Id) == Environment.ProcessId) continue;
                    if (ReadVariable(process.Id, variable) is { } value) { if (!QoderUsage.SwitchOn(value)) missing.Add(program); return; }
                    if (Readable(process.Id)) { missing.Add(program); return; }
                }
        }
        Check(edition.Cli, path => Path.GetDirectoryName(path)!.Equals(cliFolder, StringComparison.OrdinalIgnoreCase) || path.Contains(edition.Cli, StringComparison.OrdinalIgnoreCase),
            QoderUsage.RouteName(edition, QoderUsage.Route.Cli));
        if (desktop is not null)
            Check(Path.GetFileNameWithoutExtension(edition.DesktopExe), path => Path.GetDirectoryName(path)!.Equals(desktop, StringComparison.OrdinalIgnoreCase),
                QoderUsage.RouteName(edition, QoderUsage.Route.Desktop));
        return missing;
    }

    private static int? Parent(int id)
    {
        var process = OpenProcess(0x1000, false, id); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return null;
        try
        {
            var basic = new ProcessBasicInformation();
            return NtQueryInformationProcess(process, 0, ref basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0 ? (int)basic.InheritedFromUniqueProcessId : null;
        }
        finally { CloseHandle(process); }
    }

    // Whether the process's environment can be read at all, so that a variable it does not hold can be told from one not read.
    private static bool Readable(int id) => ReadEnvironment(id) is not null;

    private static string? ReadVariable(int id, string name)
    {
        var block = ReadEnvironment(id);
        if (block is null) return null;
        foreach (var entry in block.Split('\0'))
            if (entry.Length > name.Length && entry[name.Length] == '=' && entry.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return entry[(name.Length + 1)..];
        return null;
    }

    private static string? ReadEnvironment(int id)
    {
        if (!Environment.Is64BitProcess) return null;
        var process = OpenProcess(0x0400 | 0x0010, false, id); // PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
        if (process == IntPtr.Zero) return null;
        try
        {
            // A 32-bit process keeps another layout; Qoder ships only 64-bit builds.
            if (IsWow64Process(process, out bool wow64) && wow64) return null;
            var basic = new ProcessBasicInformation();
            if (NtQueryInformationProcess(process, 0, ref basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0 || basic.PebBaseAddress == IntPtr.Zero) return null;
            if (ReadPointer(process, basic.PebBaseAddress + 0x20) is not { } parameters) return null;
            if (ReadPointer(process, parameters + 0x80) is not { } environment) return null;
            long size = ReadPointer(process, parameters + 0x3F0) is { } stated ? (long)stated : 0;
            if (size is <= 0 or > 4 << 20) size = 64 << 10;
            var buffer = new byte[size];
            if (!ReadProcessMemory(process, environment, buffer, (IntPtr)buffer.Length, out var read) && read == IntPtr.Zero) return null;
            var text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)read & ~1);
            int end = text.IndexOf("\0\0", StringComparison.Ordinal);
            return end >= 0 ? text[..end] : text;
        }
        finally { CloseHandle(process); }
    }

    private static IntPtr? ReadPointer(IntPtr process, IntPtr address)
    {
        var buffer = new byte[8];
        if (!ReadProcessMemory(process, address, buffer, 8, out var read) || read != 8) return null;
        var value = (IntPtr)BitConverter.ToInt64(buffer);
        return value == IntPtr.Zero ? null : value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool IsWow64Process(IntPtr process, out bool wow64);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int information, ref ProcessBasicInformation result, int length, out int returned);
}
