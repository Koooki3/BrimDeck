using System.Text.Json.Serialization;

namespace BrimDeck.Core.Notices;

// Where a notice came from: the Windows notification center, a WSL desktop notification, brim-notify (from Windows or WSL),
// or BrimDeck's own test notice.
public enum NoticeOrigin { Windows, Wsl, Cli, Test }
// Shown as a small status mark beside the application's icon; never as the application's color.
public enum NoticeCategory { Message, Task, Success, Warning, Error, Info }
public enum NoticePriority { Low, Normal, High }
// How much an arriving notice shows: icon, title and summary; icon and title; or the application's icon and name only.
public enum NoticePrivacy { Full, TitleOnly, AppOnly }

// Clears the whole recent list on a schedule, besides dropping each notice once it is older than the retention.
public enum NoticeAutoClear { Off, OnExit, Daily, Weekly }

// What BrimDeck can do with a notice's source, by what the source offers. Nothing is shown that cannot be done.
[Flags]
public enum NoticeCapabilities
{
    None = 0,
    // The source application can be started or brought forward.
    OpenApp = 1,
    // The notice has actions BrimDeck can pass back to the source (a WSL notice while the agent is the notification service).
    Actions = 2,
    // The source can be told that the user dismissed the notice.
    ReportDismissal = 4
}

public sealed record NoticeAction(string Key, string Label);

// A notice as a source delivers it, before BrimDeck's rules. AppKey identifies the application across notices
// ("windows:<AUMID>", "windows:<browser AUMID>|<site>" for a site's notice in a browser, "wsl:<distro>:<name>", "cli:<name>"). InstanceKey identifies this notice at its source; an existing
// notice with the same InstanceKey, or with the same AppKey and ReplaceKey, is updated instead of added. Replaces names the
// InstanceKey of a notice this one took the place of at its source (see NoticeReplacements); that notice is updated too.
public sealed record NoticeInput
{
    public required NoticeOrigin Origin { get; init; }
    public required string AppKey { get; init; }
    public required string AppName { get; init; }
    public required string InstanceKey { get; init; }
    public string AppId { get; init; } = "";
    public string? Distro { get; init; }
    public string? ReplaceKey { get; init; }
    public string? Replaces { get; init; }
    public string? EventId { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string? IconRef { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    // How long the message had waited before it was sent, by the sender's clock (NoticeMessage.AgeMs); null when not told.
    public TimeSpan? Age { get; init; }
    public NoticeCategory Category { get; init; } = NoticeCategory.Message;
    public NoticePriority PriorityHint { get; init; } = NoticePriority.Normal;
    public double? Progress { get; init; }
    public IReadOnlyList<NoticeAction> Actions { get; init; } = [];
    public NoticeCapabilities Capabilities { get; init; }
    // Set for the result of "brim-notify run"; the hub words the title and body from them when the sender gave none.
    public int? ExitCode { get; init; }
    public long? DurationMs { get; init; }
    public string? Command { get; init; }
    // A time-kind notice's times (NoticeTime.cs); null for every other notice.
    public NoticeTiming? Timing { get; init; }
    // Ends the timing of the earlier notice with the same AppKey and ReplaceKey (brim-notify stop) instead of adding one.
    public bool StopTiming { get; init; }
    // Arrived while BrimDeck could not read its source (the PC slept): recorded, unviewed, but not shown as it arrives (core-M5).
    public bool Missed { get; init; }
    // An incoming call, as BrimDeck reads a Windows notification (WindowsNoticeApps.ReadsAsCall); never taken from a sender.
    public bool Call { get; init; }
    // A desktop notification its application marked "transient" (NoticeMessage.Transient): shown and listed, never saved.
    public bool Transient { get; init; }
}

// A notice BrimDeck keeps. Id is BrimDeck's own; Revision counts updates, so a view can tell a changed notice apart.
public sealed class Notice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string InstanceKey { get; set; } = "";
    public string AppKey { get; set; } = "";
    public string AppName { get; set; } = "";
    public string AppId { get; set; } = "";
    public NoticeOrigin Origin { get; set; }
    public string? Distro { get; set; }
    public string? ReplaceKey { get; set; }
    // The sender's id of the message that last added or changed the notice. Kept with the notice, so that a retry is
    // recognized after a restart too.
    public string? EventId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? IconRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public NoticeCategory Category { get; set; }
    public NoticePriority Priority { get; set; } = NoticePriority.Normal;
    public double? Progress { get; set; }
    public List<NoticeAction> Actions { get; set; } = [];
    public NoticeCapabilities Capabilities { get; set; }
    // Whether the notice still exists at its source (still in the notification center, not closed by the application).
    public bool SourcePresent { get; set; } = true;
    // Seen in BrimDeck: its details were opened or it was listed on the recent notices page. Says nothing about the source
    // application's own read state.
    public bool Viewed { get; set; }
    public int Revision { get; set; }
    public NoticeTiming? Timing { get; set; }
    // An incoming call (NoticeInput.Call): shown with the call mark and as urgent as a ringing alarm. A plain flag, so that
    // an older BrimDeck still reads the saved notices.
    public bool Call { get; set; }
    // Not to be saved (NoticeInput.Transient): NoticeHistory.Save leaves it out, so it is gone once BrimDeck restarts. A
    // plain flag, as Call is.
    public bool Transient { get; set; }
    [JsonIgnore] public bool HasProgress => Progress is not null;
    [JsonIgnore] public NoticeTimeKind TimeKind => Timing?.Kind ?? NoticeTimeKind.None;
    public Notice Copy() { var copy = (Notice)MemberwiseClone(); copy.Actions = [.. Actions]; copy.Timing = Timing?.Copy(); return copy; }
}

// The user's choices for one application. Disabled drops its notices entirely; Muted keeps them in the recent notices
// without showing them as they arrive. Privacy null follows the default.
public sealed class NoticeAppRule
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Muted { get; set; }
    public NoticePriority Priority { get; set; } = NoticePriority.Normal;
    public NoticePrivacy? Privacy { get; set; }
    // "#RRGGBB" chosen by hand; empty takes the color read from the icon.
    public string AccentColor { get; set; } = "";
    public NoticeAppRule Copy() => (NoticeAppRule)MemberwiseClone();
}

// An application BrimDeck has received notices from, so that the settings can list it before the user has made any rule.
public sealed class NoticeKnownApp
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public NoticeOrigin Origin { get; set; }
    public string? IconRef { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public static class NoticeKeys
{
    public static string Windows(string appUserModelId) => "windows:" + appUserModelId;
    public static string Wsl(string distro, string appName) => "wsl:" + distro + ":" + appName;
    public static string Cli(string? distro, string appName) => distro is null ? "cli:" + appName : "cli:" + distro + ":" + appName;
    public static NoticeCategory Category(string? text) => text switch
    {
        "task" => NoticeCategory.Task, "success" => NoticeCategory.Success, "warning" => NoticeCategory.Warning,
        "error" => NoticeCategory.Error, "info" => NoticeCategory.Info, _ => NoticeCategory.Message
    };
    public static NoticePriority Priority(string? text) => text switch { "low" => NoticePriority.Low, "high" => NoticePriority.High, _ => NoticePriority.Normal };
}
