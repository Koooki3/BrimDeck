using BrimDeck.Protocol;

namespace BrimDeck.Core.Notices;

// Turns a protocol message into a notice input. The link a message arrived on decides its origin and distribution, never
// the message itself: a sender cannot claim to be a Windows application or another distribution.
public static class NoticeIntake
{
    // A message from the local pipe (BrimDeck.Notify.exe on Windows, or brim-notify in WSL through it while the agent is not
    // running). The pipe only admits the current user, so a distribution named by such a sender is taken as a label.
    public static NoticeInput FromPipe(NoticeMessage message)
    {
        string name = Name(message.AppName, "brim-notify");
        string? distro = Blank(message.Distro);
        return Build(message, NoticeOrigin.Cli, NoticeKeys.Cli(distro, name), name, distro, $"cli:{distro}:" + (message.EventId ?? Guid.NewGuid().ToString("N")), NoticeCapabilities.None);
    }

    // A message from a distribution's agent. Desktop notifications get the agent's capabilities: actions and dismissal reach
    // the application only while the agent is the session's notification service.
    public static NoticeInput FromAgent(NoticeMessage message, string distro, bool agentIsServer)
    {
        if (message.Origin == "dbus")
        {
            string name = Name(message.AppName, Loc.T("Linux 应用", "Linux app"));
            var capabilities = agentIsServer ? NoticeCapabilities.ReportDismissal | (message.Actions is { Count: > 0 } ? NoticeCapabilities.Actions : 0) : NoticeCapabilities.None;
            string instance = $"wsl:{distro}:{message.Session ?? ""}:{message.NativeId ?? Guid.NewGuid().ToString("N")}";
            return Build(message, NoticeOrigin.Wsl, NoticeKeys.Wsl(distro, name), name, distro, instance, capabilities);
        }
        string cliName = Name(message.AppName, "brim-notify");
        return Build(message, NoticeOrigin.Cli, NoticeKeys.Cli(distro, cliName), cliName, distro, $"cli:{distro}:" + (message.EventId ?? Guid.NewGuid().ToString("N")), NoticeCapabilities.None);
    }

    private static NoticeInput Build(NoticeMessage message, NoticeOrigin origin, string appKey, string appName, string? distro, string instance, NoticeCapabilities capabilities) => new()
    {
        Origin = origin, AppKey = appKey, AppName = appName, AppId = message.AppId ?? "", Distro = distro, InstanceKey = instance,
        ReplaceKey = Blank(message.ReplaceId), EventId = Blank(message.EventId),
        Title = message.Title ?? "", Body = message.Body ?? "", IconRef = IconRef(message.Icon, origin, distro),
        CreatedAt = message.CreatedAt, Category = NoticeKeys.Category(message.Category), PriorityHint = NoticeKeys.Priority(message.Priority),
        Progress = message.Progress, Capabilities = capabilities,
        Actions = (capabilities & NoticeCapabilities.Actions) != 0 && message.Actions is { } actions ? actions.Select(a => new NoticeAction(a.Key, a.Label)).ToList() : [],
        ExitCode = message.ExitCode, DurationMs = message.DurationMs, Command = Blank(message.Command)
    };

    // Icons are local only: a Windows file path for the Windows link, a Linux path or icon name inside the distribution for
    // WSL. Anything that looks like a web address is ignored.
    public static string? IconRef(string? icon, NoticeOrigin origin, string? distro)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        icon = icon.Trim();
        if (icon.Contains("://", StringComparison.Ordinal) && !icon.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        if (icon.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) icon = Uri.TryCreate(icon, UriKind.Absolute, out var uri) ? uri.LocalPath : "";
        if (icon.Length == 0 || icon.Length > 1024 || icon.Any(char.IsControl)) return null;
        if (distro is null) return IsLocalWindowsPath(icon) ? "file:" + icon : null;
        return IsLinuxIcon(icon) ? $"wsl:{distro}:{icon}" : null;
    }

    // A path on a local drive, written out in full: "C:\folder\icon.png". Network paths (\\server\share), device paths
    // (\\?\, \\.\), relative paths, alternate data streams and ".." are refused.
    public static bool IsLocalWindowsPath(string path)
    {
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return false;
        if (path.IndexOf(':', 2) >= 0 || path.Contains("..", StringComparison.Ordinal) || path.Contains("\\\\", StringComparison.Ordinal) || path.Contains("//", StringComparison.Ordinal)) return false;
        return path.IndexOfAny(['*', '?', '"', '<', '>', '|']) < 0;
    }

    // An absolute path inside the distribution, or an icon name; no "..", no Windows separators, no drive letters.
    public static bool IsLinuxIcon(string icon)
    {
        if (icon.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|']) >= 0) return false;
        if (icon.Split('/').Any(part => part == "..")) return false;
        return icon.StartsWith('/') ? !icon.StartsWith("//", StringComparison.Ordinal) : icon.IndexOf('/') < 0;
    }

    private static string Name(string? name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
