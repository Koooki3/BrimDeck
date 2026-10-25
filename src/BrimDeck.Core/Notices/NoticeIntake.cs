namespace BrimDeck.Core.Notices;

// What a source hands BrimDeck about a notice's icon, checked before it is used.
public static class NoticeIntake
{
    // Icons are local only: a Windows file path, written out in full. Anything that looks like a web address is ignored, and a
    // notice that names a distribution has no icon file on Windows.
    public static string? IconRef(string? icon, NoticeOrigin origin, string? distro)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        icon = icon.Trim();
        if (icon.Contains("://", StringComparison.Ordinal) && !icon.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        if (icon.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) icon = Uri.TryCreate(icon, UriKind.Absolute, out var uri) ? uri.LocalPath : "";
        if (icon.Length == 0 || icon.Length > 1024 || icon.Any(char.IsControl)) return null;
        return distro is null && IsLocalWindowsPath(icon) ? "file:" + icon : null;
    }

    // A path on a local drive, written out in full: "C:\folder\icon.png". Network paths (\\server\share), device paths
    // (\\?\, \\.\), relative paths, alternate data streams and ".." are refused.
    public static bool IsLocalWindowsPath(string path)
    {
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return false;
        if (path.IndexOf(':', 2) >= 0 || path.Contains("..", StringComparison.Ordinal) || path.Contains("\\\\", StringComparison.Ordinal) || path.Contains("//", StringComparison.Ordinal)) return false;
        return path.IndexOfAny(['*', '?', '"', '<', '>', '|']) < 0;
    }
}
