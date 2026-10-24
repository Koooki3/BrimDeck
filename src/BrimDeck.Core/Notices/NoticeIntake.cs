using BrimDeck.Protocol;

namespace BrimDeck.Core.Notices;

// Turns a protocol message into a notice input. The link a message arrived on decides its origin and distribution, never
// the message itself: a sender cannot claim to be a Windows application or another distribution.
public static class NoticeIntake
{
    // A message from the local pipe (BrimDeck.Notify.exe on Windows, or brim-notify in WSL through it while the agent is not
    // running). The pipe only admits the current user, so a distribution named by such a sender is taken as a label.
    // now and zone resolve a time-kind notice's times (NoticeTimeIntake); they default to the present and Windows' time zone.
    public static NoticeInput FromPipe(NoticeMessage message, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        string name = Name(message.AppName, "brim-notify");
        // Only a name WSL itself allows is taken as a label (sec-L4).
        string? distro = Blank(message.Distro) is { } named && IsDistroName(named) ? named : null;
        // Its instance keys are marked as the pipe's, apart from those the distribution's agent delivers (S3).
        string instance = CliInstance(distro, name, message);
        if (distro is not null) instance = PipePrefix + instance["cli:".Length..];
        return Build(message, NoticeOrigin.Cli, NoticeKeys.Cli(distro, name), name, distro, instance, NoticeCapabilities.None, now, zone);
    }

    private const string PipePrefix = "pipe:";

    // Whether a command-line notice naming a distribution came over the pipe rather than from that distribution's agent.
    public static bool ViaPipe(string instanceKey) => instanceKey.StartsWith(PipePrefix, StringComparison.Ordinal);

    // A message from a distribution's agent. Desktop notifications get the agent's capabilities: actions and dismissal reach
    // the application only while the agent is the session's notification service.
    public static NoticeInput FromAgent(NoticeMessage message, string distro, bool agentIsServer, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        if (message.Origin == "dbus")
        {
            string name = Name(message.AppName, Loc.T("Linux 应用", "Linux app"));
            var capabilities = agentIsServer ? NoticeCapabilities.ReportDismissal | (message.Actions is { Count: > 0 } ? NoticeCapabilities.Actions : 0) : NoticeCapabilities.None;
            string instance = $"wsl:{distro}:{message.Session ?? ""}:{message.NativeId ?? Guid.NewGuid().ToString("N")}";
            return Build(message, NoticeOrigin.Wsl, NoticeKeys.Wsl(distro, name), name, distro, instance, capabilities, now, zone);
        }
        string cliName = Name(message.AppName, "brim-notify");
        return Build(message, NoticeOrigin.Cli, NoticeKeys.Cli(distro, cliName), cliName, distro, CliInstance(distro, cliName, message), NoticeCapabilities.None, now, zone);
    }

    // The WSL link's own count of notices, on the bridge's reading thread before anything reaches the UI thread, as the pipe
    // counts its messages (W5): a program in the distribution calling Notify in a loop must not fill the UI thread's queue.
    // Whether the message goes on; refusal is the reply for a brim-notify waiting through the agent, null when nobody waits.
    // Only notices count: status reports and closes are few and bounded by the agent.
    public static bool AdmitFromAgent(IntakeGate gate, NoticeMessage message, long nowMilliseconds, out NoticeMessage? refusal)
    {
        refusal = null;
        if (message.Type != NoticeTypes.Notify || gate.Admit(nowMilliseconds)) return true;
        if (message.Origin == "cli" && message.EventId is { } id) refusal = NoticeProtocol.Reply(NoticeResults.Rejected, "rate-limited", id);
        return false;
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

    // A WSL distribution's name as WSL allows it: letters, digits, ".", "_" and "-". It is used in \\wsl.localhost paths and
    // passed to wsl.exe, so nothing else may get through.
    public static bool IsDistroName(string name)
        => name.Length is > 0 and <= 64 && name is not ("." or "..") && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static string Name(string? name, string fallback) => NoticeText.Clean(name ?? "", singleLine: true).Trim() is { Length: > 0 } clean ? clean : fallback;
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
