namespace BrimDeck.Protocol;

// Where BrimDeck looks for a WSL notice's icon inside the distribution, and how the agent turns an icon that is a symbolic
// link into the file it leads to. BrimDeck never reads an icon through a link (it could lead anywhere, a network share
// included), but Debian and Ubuntu install many theme icons as links within the icon folders; the agent resolves those in
// the distribution and sends the real file's path instead (I1). Logic without I/O, so that it can be tested anywhere.
public static class LinuxIcons
{
    // The trees a link may lead within; anything else stays a link, which BrimDeck refuses as before.
    public static readonly string[] Trees = ["/usr/share/icons/", "/usr/share/pixmaps/"];
    // A chain of links longer than this is not followed (a loop included).
    public const int MaxLinks = 8;

    // An icon name in the usual theme and pixmap folders, at sizes WPF can read (PNG, not SVG); an absolute path as given.
    public static IEnumerable<string> Candidates(string icon)
    {
        if (icon.StartsWith('/')) { yield return icon; yield break; }
        foreach (var size in new[] { "48x48", "64x64", "32x32", "128x128" })
            yield return $"/usr/share/icons/hicolor/{size}/apps/{icon}.png";
        yield return $"/usr/share/pixmaps/{icon}.png";
    }

    // The icon to send for one a notice names. linkTarget gives a link's target as written (null for anything that is not a
    // link); exists tells whether a path leads to a file. For the first candidate BrimDeck would read: a plain file keeps the
    // icon as it is; a link whose whole chain stays within Trees becomes the path of the file it leads to. A link that leaves
    // them, or a candidate that does not exist, is passed over as BrimDeck passes it over.
    public static string? Resolve(string? icon, Func<string, string?> linkTarget, Func<string, bool> exists)
    {
        // Only what BrimDeck would look up itself (NoticeIntake.IsLinuxIcon): an absolute path, or a name without a slash.
        if (string.IsNullOrEmpty(icon) || icon.Contains("://", StringComparison.Ordinal) || !icon.StartsWith('/') && icon.Contains('/')
            || icon.Split('/').Any(part => part is "." or "..")) return icon;
        foreach (var candidate in Candidates(icon))
        {
            if (linkTarget(candidate) is null)
            {
                if (exists(candidate)) return icon;
                continue;
            }
            if (Follow(candidate, linkTarget) is { } real && exists(real)) return real;
        }
        return icon;
    }

    // The end of a chain of links that starts and stays within Trees; null when it leaves them or is too long.
    public static string? Follow(string path, Func<string, string?> linkTarget)
    {
        if (!Within(path)) return null;
        for (int hop = 0; hop <= MaxLinks; hop++)
        {
            if (linkTarget(path) is not { } target) return path;
            if (target.Length == 0) return null;
            if (Normalize(target.StartsWith('/') ? target : path[..(path.LastIndexOf('/') + 1)] + target) is not { } next || !Within(next)) return null;
            path = next;
        }
        return null;
    }

    private static bool Within(string path) => Trees.Any(tree => path.StartsWith(tree, StringComparison.Ordinal) && path.Length > tree.Length);

    // An absolute Linux path with "." and ".." taken out and repeated slashes joined; null for one that climbs above "/".
    // Written out, not Path.GetFullPath, which on Windows (the tests) would turn it into a Windows path.
    public static string? Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }
}
