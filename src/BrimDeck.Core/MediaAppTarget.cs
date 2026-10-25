namespace BrimDeck.Core;

// The application that plays the current song: the exact process when a player connection knows it, otherwise only the
// media session's AppUserModelID. Win32 players report their executable ("cloudmusic.exe") or a short name ("Chrome",
// "MSEdge"); packaged applications report "PackageFamilyName!App".
public sealed record MediaAppOwner(int? ProcessId, string ProcessName, string AppId)
{
    public string? PackageFamily => AppId.IndexOf('!') is > 0 and var bang ? AppId[..bang] : null;
}

// A top-level window as the window selection sees it, listed from the top of the Z-order down.
public sealed record MediaAppWindow(nint Handle, int ProcessId, string ProcessName, string? PackageFamily, string AppId,
    bool Visible, bool Minimized, bool Tool, bool Owned, bool Cloaked, string Title);

public static class MediaAppTarget
{
    // The process name a media session's AppUserModelID most likely belongs to. Packaged applications are matched by
    // package family instead (see Belongs); the name derived from their id is only a guess.
    public static string ProcessName(string appId)
    {
        string lower = appId.ToLowerInvariant();
        if (lower.Contains("cloudmusic") || lower.Contains("netease")) return "cloudmusic";
        if (lower.Contains("qqmusic")) return "QQMusic";
        if (lower.Contains("msedge")) return "msedge";
        if (lower.Contains("chrome")) return "chrome";
        // "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App" would otherwise become "AppleInc". Apple Music's sound comes from its
        // AMPLibraryAgent.exe, not AppleMusic.exe (measured in the audio sessions); its window is matched by package family.
        if (lower.Contains("applemusic")) return "AMPLibraryAgent";
        return Path.GetFileNameWithoutExtension(appId.Split('!')[0]);
    }

    public static bool Belongs(MediaAppOwner owner, MediaAppWindow window)
    {
        if (owner.ProcessId is { } pid) return window.ProcessId == pid;
        if (owner.AppId.Length > 0 && string.Equals(window.AppId, owner.AppId, StringComparison.OrdinalIgnoreCase)) return true;
        if (owner.PackageFamily is { } family) return string.Equals(window.PackageFamily, family, StringComparison.OrdinalIgnoreCase);
        return owner.ProcessName.Length > 0 && string.Equals(window.ProcessName, owner.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    // The window to bring forward: an ordinary application window (shown or minimized, titled, not owned, not a tool window)
    // of the owner. A window the shell hides on another virtual desktop counts only when there is no other; among the rest
    // the Z-order decides, so the most recently active one wins. Null when the player only has a tray icon left.
    public static MediaAppWindow? Choose(MediaAppOwner owner, IEnumerable<MediaAppWindow> zOrder) =>
        zOrder.Where(w => w.Visible && !w.Tool && !w.Owned && w.Title.Length > 0 && Belongs(owner, w))
            .OrderBy(w => w.Cloaked).FirstOrDefault();
}
