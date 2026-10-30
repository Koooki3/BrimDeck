using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck.Native;

// KuGou's installation and the change to its libcef.dll that opens the DevTools port (KugouPatch). Changing or restoring the
// file alone (the settings) does not touch KuGou: a file KuGou has loaded cannot be written, but it can be renamed, so the
// changed copy takes its place and the next start loads it; the renamed file is deleted once KuGou no longer holds it.
// Enabling full control (the panel's entries and the automatic setting) also restarts KuGou, so the change works at once. Under Program Files the file
// needs administrator rights; BrimDeck then starts itself elevated for the write alone, which shows a UAC prompt.
internal static class KugouInstall
{
    // Launcher: KGMusic\KuGou.exe, which starts the KuGou.exe of the version folder; Library: that folder's libcef.dll.
    internal sealed record Location(string Launcher, string Library);
    internal const string ElevatedSwitch = "--kugou-patch";

    public static Location? Find()
    {
        foreach (var path in RunningPaths())
        {
            string folder = Path.GetDirectoryName(path)!;
            string library = Path.Combine(folder, "libcef.dll");
            if (!File.Exists(library)) continue;
            string launcher = Path.Combine(Path.GetDirectoryName(folder)!, "KuGou.exe");
            return new(File.Exists(launcher) ? launcher : path, library);
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\KuGou");
            if (key?.GetValue("AppPath") is string { Length: > 0 } appPath && Installed(Path.Combine(appPath, "KuGou.exe"),
                key.GetValue("KuGou8") as string) is { } registered) return registered;
            foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.LocalMachine, RegistryView.Registry64) })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") as string != "酷狗音乐") continue;
                    if (entry.GetValue("DisplayIcon") is string { Length: > 0 } icon && Installed(icon.Trim('"'), null) is { } listed) return listed;
                }
            }
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    // The version folder named by KuGou's own record, else the one named by the launcher's version, else the highest one.
    private static Location? Installed(string launcher, string? folder)
    {
        if (!File.Exists(launcher)) return null;
        if (folder is { Length: > 0 } && File.Exists(Path.Combine(folder, "libcef.dll"))) return new(launcher, Path.Combine(folder, "libcef.dll"));
        string root = Path.GetDirectoryName(launcher)!;
        string? version = FileVersionInfo.GetVersionInfo(launcher).FileVersion;
        if (version is { Length: > 0 } && File.Exists(Path.Combine(root, version, "libcef.dll"))) return new(launcher, Path.Combine(root, version, "libcef.dll"));
        var highest = Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "libcef.dll")))
            .Select(d => (Folder: d, Version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null)).Where(d => d.Version is not null)
            .OrderByDescending(d => d.Version).FirstOrDefault();
        return highest.Folder is null ? null : new(launcher, Path.Combine(highest.Folder, "libcef.dll"));
    }

    // Whether full control can be switched on or off here: the installation's state, null when KuGou is not installed.
    public static KugouPatchState? State() => Find() is { } location ? State(location) ?? KugouPatchState.Unsupported : null;

    public static KugouPatchState? State(Location location)
    {
        CleanUp(location.Library);
        try
        {
            using var file = new FileStream(location.Library, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return KugouPatch.Classify(file);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // Changes or restores the file and leaves KuGou as it is; a running KuGou takes the change at its next start.
    // Throws with a short message for the user, as EnableAsync does.
    public static async Task ChangeFileAsync(bool patch, CancellationToken token) =>
        await WriteAsync(Changeable().Library, patch, token).ConfigureAwait(false);
    // Enables full control: closes KuGou, changes the file and starts KuGou again when it was running, or when start asks.
    public static async Task EnableAsync(bool start, CancellationToken token)
    {
        var location = Changeable();
        bool running = RunningPaths().Any();
        await CloseAsync(location, token).ConfigureAwait(false);
        await WriteAsync(location.Library, true, token).ConfigureAwait(false);
        if (start || running) Start(location);
    }
    private static Location Changeable()
    {
        var location = Find() ?? throw new InvalidOperationException(Loc.T("未找到酷狗音乐", "KuGou Music was not found"));
        return State(location) is KugouPatchState.Original or KugouPatchState.Patched or KugouPatchState.Partial ? location
            : throw new InvalidOperationException(Loc.T("不支持这个版本的酷狗音乐", "This version of KuGou Music is not supported"));
    }

    public static bool Running() => RunningPaths().Any();
    // The running KuGou processes, to tell one start of KuGou from the next, and when the first of them started; "" and null
    // while KuGou is not running.
    public static (string Key, DateTime? Started) Processes()
    {
        var processes = Process.GetProcessesByName("KuGou");
        try
        {
            DateTime? started = null;
            foreach (var process in processes)
                try { var at = process.StartTime.ToUniversalTime(); if (started is null || at < started) started = at; }
                catch (InvalidOperationException) { } catch (Win32Exception) { }
            return (string.Join(",", processes.Select(p => p.Id).Order()), started);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static void Start(Location location)
    {
        var info = new ProcessStartInfo(location.Launcher) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(location.Launcher)! };
        using var process = Process.Start(info);
    }

    // KuGou has no command that quits it; closing its window only hides it in the tray. Its processes are ended, as the
    // patching tools for it do, which leaves its song list and settings as they were saved.
    private static async Task CloseAsync(Location location, CancellationToken token)
    {
        string root = Path.GetDirectoryName(location.Launcher)!;
        var deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            var processes = Process.GetProcessesByName("KuGou");
            bool any = false;
            try
            {
                foreach (var process in processes)
                {
                    string? path = PathOf(process);
                    if (path is null || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    any = true;
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            if (!any) return;
            if (Environment.TickCount64 >= deadline) throw new InvalidOperationException(Loc.T("无法关闭酷狗音乐，请退出后重试", "KuGou could not be closed; quit it and try again"));
            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    // A file KuGou has only just released can still be locked for a moment; a write without rights is done elevated.
    private static async Task WriteAsync(string library, bool patch, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Write(library, patch); return; }
            catch (UnauthorizedAccessException) { await WriteElevatedAsync(library, patch, token).ConfigureAwait(false); return; }
            catch (IOException) when (attempt < 20 && File.Exists(library)) { await Task.Delay(250, token).ConfigureAwait(false); }
        }
    }

    private static void Write(string library, bool patch, bool elevated = false)
    {
        try
        {
            using var file = new FileStream(library, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            KugouPatch.Write(file, patch);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == SharingViolation) { Replace(library, patch, elevated); }
    }
    // KuGou holds the file: a changed copy is made and put in its place, and the held file is renamed until it can go.
    private static void Replace(string library, bool patch, bool elevated)
    {
        string copy = library + ".brimdeck-new", held = library + ".brimdeck-old-" + Environment.TickCount64.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.Copy(library, copy, overwrite: true);
        try
        {
            using (var file = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) KugouPatch.Write(file, patch);
            File.Move(library, held);
            File.Move(copy, library);
        }
        finally { if (File.Exists(copy)) File.Delete(copy); }
        // Without administrator rights later, the held file in a protected folder would stay; Windows removes it at restart.
        if (elevated) MoveFileEx(held, null, MoveFileDelayUntilReboot);
    }
    private static void CleanUp(string library)
    {
        try
        {
            foreach (var held in Directory.EnumerateFiles(Path.GetDirectoryName(library)!, Path.GetFileName(library) + ".brimdeck-old-*"))
                try { File.Delete(held); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private const int SharingViolation = 32;
    private const uint MoveFileDelayUntilReboot = 4;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool MoveFileEx(string existing, string? replacement, uint flags);

    private static async Task WriteElevatedAsync(string library, bool patch, CancellationToken token)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add(ElevatedSwitch); info.ArgumentList.Add(patch ? "apply" : "restore"); info.ArgumentList.Add(library);
        Process? process;
        try { process = Process.Start(info); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new InvalidOperationException(Loc.T("未取得管理员权限", "Administrator rights were not given")); }
        using (process)
        {
            if (process is null) throw new InvalidOperationException(Loc.T("无法以管理员身份修改文件", "The file could not be changed as administrator"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException(Loc.T("修改文件失败，文件保持原样", "The file could not be changed and was left as it was"));
        }
    }

    // The elevated instance: writes the one file named on its command line, which must be a libcef.dll that KugouPatch
    // recognizes, and exits with 0 on success.
    public static int RunElevated(string mode, string library)
    {
        if (mode is not ("apply" or "restore") || !string.Equals(Path.GetFileName(library), "libcef.dll", StringComparison.OrdinalIgnoreCase)) return 2;
        try { Write(library, mode == "apply", elevated: true); return 0; }
        catch { return 1; }
    }

    private static IEnumerable<string> RunningPaths()
    {
        var processes = Process.GetProcessesByName("KuGou");
        try { return processes.Select(PathOf).OfType<string>().ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static string? PathOf(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
