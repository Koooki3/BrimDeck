using BrimDeck.Protocol;

namespace BrimDeck.Core.Notices;

// Turns a protocol message into a notice input. The link a message arrived on decides its origin, never the message itself:
// a sender cannot claim to be a Windows application.
public static class NoticeIntake
{
    // A message from the local pipe (BrimDeck.Notify.exe). The pipe only admits the current user.
    // now and zone resolve a time-kind notice's times (NoticeTimeIntake); they default to the present and Windows' time zone.
    public static NoticeInput FromPipe(NoticeMessage message, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        string name = Name(message.AppName, "brim-notify");
        return Build(message, NoticeOrigin.Cli, NoticeKeys.Cli(null, name), name, null, CliInstance(null, name, message), NoticeCapabilities.None, now, zone);
    }

    // A command-line notice's instance at its source: the sender's event id within its own name (core-M1), so the same
    // --id from another -s is another notice; a blank id is no id, and the notice is its own.
    private static string CliInstance(string? distro, string name, NoticeMessage message)
        => $"cli:{distro}:{name}:" + (Blank(message.EventId) ?? Guid.NewGuid().ToString("N"));

    private static NoticeInput Build(NoticeMessage message, NoticeOrigin origin, string appKey, string appName, string? distro, string instance, NoticeCapabilities capabilities,
        DateTimeOffset? now, TimeZoneInfo? zone) => new()
    {
        Origin = origin, AppKey = appKey, AppName = appName, AppId = NoticeText.Clean(message.AppId ?? "", singleLine: true), Distro = distro, InstanceKey = instance,
        ReplaceKey = Blank(message.ReplaceId), EventId = Blank(message.EventId),
        Title = message.Title ?? "", Body = message.Body ?? "", IconRef = IconRef(message.Icon, origin, distro),
        CreatedAt = message.CreatedAt, Age = message.AgeMs is >= 0 and var age ? TimeSpan.FromMilliseconds(age) : null, Category = NoticeKeys.Category(message.Category), PriorityHint = NoticeKeys.Priority(message.Priority),
        Progress = message.Progress, Capabilities = capabilities,
        // Action labels show as buttons: cleaned as a title is (C-L11).
        Actions = (capabilities & NoticeCapabilities.Actions) != 0 && message.Actions is { } actions ? actions.Select(a => new NoticeAction(a.Key, Name(a.Label, a.Key))).ToList() : [],
        ExitCode = message.ExitCode, DurationMs = message.DurationMs, Command = Blank(message.Command),
        Timing = NoticeTimeIntake.Resolve(message, now ?? DateTimeOffset.UtcNow, zone ?? TimeZoneInfo.Local),
        StopTiming = message.Stop == true,
        Transient = message.Transient == true
    };

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

    private static string Name(string? name, string fallback) => NoticeText.Clean(name ?? "", singleLine: true).Trim() is { Length: > 0 } clean ? clean : fallback;
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
