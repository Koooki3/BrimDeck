using System.Text.RegularExpressions;

namespace BrimDeck.Core.Notices;

// What the system says about one top-level window; the facts the choice of a window to bring forward is made from.
public sealed record AppWindowInfo(
    int Pid, bool Visible, bool Cloaked, bool ToolWindow, bool AppWindowStyle, bool HasOwner, bool HasTitle, bool Minimized, bool IsShell);

// The window system as NoticeAppWindows uses it: the Win32 binding is AppWindowSystem; tests give a fake.
public interface IAppWindowSystem
{
    // Top-level windows in z-order, topmost first.
    IReadOnlyList<nint> TopLevelWindows();
    // Null when the window no longer exists.
    AppWindowInfo? Inspect(nint handle);
    // The full path of the executable a process runs; null or empty when it cannot be read.
    string? ImagePath(int pid);
    // Restores the window first when restore is set. True only when the window is the foreground window afterwards.
    bool Foreground(nint handle, bool restore);
}

// Which running programs may be the application a notice came from: the file names (without ".exe") its application id
// suggests, and for a packaged app the folder it is installed in. Never a path or command to run, only something to compare.
public sealed record AppProcessHint(IReadOnlyList<string> ProcessNames, string? InstallDirectory)
{
    public bool IsEmpty => ProcessNames.Count == 0 && string.IsNullOrEmpty(InstallDirectory);

    public bool Matches(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return false;
        if (!string.IsNullOrEmpty(InstallDirectory))
        {
            string dir = InstallDirectory.TrimEnd('\\', '/') + "\\";
            if (imagePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;
        }
        string name = imagePath[(imagePath.LastIndexOfAny(['\\', '/']) + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return ProcessNames.Any(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase));
    }
}

// Brings forward the window an application already has, so that opening the source of a notice does not start a second copy
// of a program that sits in the tray. Only a window that is visible, ordinary, not cloaked and still belongs to a process
// running the expected executable is used; anything else (no process, tray-only, hidden) is left to the launch.
public static partial class NoticeAppWindows
{
    // Application ids whose program has a different file name than the id suggests.
    private static readonly (string Id, string[] Names)[] Known =
    [
        ("Weixin", ["Weixin", "WeChat"]), ("WeChat", ["Weixin", "WeChat"]), ("ali.dingtalk.release", ["DingTalk"]), ("BiliBiliPC", ["bilibili"]),
        ("Microsoft.VisualStudioCode", ["Code"]), ("Anysphere.Cursor", ["Cursor"]), ("md.obsidian", ["Obsidian"])
    ];

    [GeneratedRegex(@"^Microsoft\.Office\.(?<name>[A-Za-z0-9]+)\.EXE\.\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex OfficeId();

    // The hint for an application id; packageDirectory is the install folder when Windows says the app is packaged.
    public static AppProcessHint Hint(string appId, string? packageDirectory)
    {
        var names = new List<string>();
        string? dir = string.IsNullOrWhiteSpace(packageDirectory) ? null : packageDirectory;
        // A packaged app is told by where it is installed; the part of its id after "!" is an entry name, not a file.
        if (dir is null && appId.Length > 0 && !appId.Contains('!') && !appId.StartsWith(WindowsNoticeApps.TrayPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var known = Known.FirstOrDefault(entry => string.Equals(entry.Id, appId, StringComparison.OrdinalIgnoreCase));
            if (known.Names is not null) names.AddRange(known.Names);
            else if (OfficeId().Match(appId) is { Success: true } office) names.Add(office.Groups["name"].Value);
            else if (appId.Contains('\\') || appId.Contains('/') || appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // "{known folder id}\vendor\program.exe": the registered program's file name.
                string file = appId[(appId.LastIndexOfAny(['\\', '/']) + 1)..];
                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
                if (file.Length > 0) names.Add(file);
            }
            else if (!appId.Contains('.')) names.Add(appId);
        }
        return new AppProcessHint(names, dir);
    }

    // An ordinary main window: shown, not hidden by the system (another desktop, a suspended app), no owner, a title, not a
    // tool window (unless it asks to be listed on the taskbar) and not the desktop or taskbar.
    private static bool Usable(AppWindowInfo? info, int selfPid)
        => info is { Visible: true, Cloaked: false, HasOwner: false, HasTitle: true, IsShell: false } && info.Pid != selfPid
           && (!info.ToolWindow || info.AppWindowStyle);

    // Brings the application's existing main window to the foreground. False when there is none, or the system would not
    // bring it forward; nothing was started or changed then except possibly a restored window (the caller launches the app).
    public static bool TryFocus(AppProcessHint hint, IAppWindowSystem system, int selfPid)
    {
        if (hint.IsEmpty) return false;
        var paths = new Dictionary<int, bool>();
        foreach (var handle in system.TopLevelWindows())
        {
            var info = system.Inspect(handle);
            if (!Usable(info, selfPid)) continue;
            if (!paths.TryGetValue(info!.Pid, out bool runs)) paths[info.Pid] = runs = hint.Matches(system.ImagePath(info.Pid));
            if (!runs) continue;
            // The window list is a moment old: read the window and its process once more, so a handle that was closed or
            // reused (or a process id taken by another program) is never acted on.
            var again = system.Inspect(handle);
            if (!Usable(again, selfPid) || again!.Pid != info.Pid || !hint.Matches(system.ImagePath(info.Pid))) continue;
            return system.Foreground(handle, again.Minimized);
        }
        return false;
    }
}
