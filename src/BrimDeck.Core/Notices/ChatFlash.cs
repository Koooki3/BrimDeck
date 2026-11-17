namespace BrimDeck.Core.Notices;

// A chat application that tells the user about a new message only by flashing its taskbar button (desktop WeChat 4.x sends no
// Windows notification). Executables are matched by file name, case-insensitively; a row that is not Enabled is known but
// never reported, so another application is added by one row and one switch here. RestoreUri, when set, is the address the
// application's own URL scheme brings its window forward with (ChatRestore); only WeChat has one.
public sealed record ChatFlashApp(string Id, string NameZh, string NameEn, IReadOnlyList<string> Executables, bool Enabled, string? RestoreUri = null)
{
    public string Name => Loc.T(NameZh, NameEn);
}

public static class ChatFlashApps
{
    // Only WeChat is on. The others are rows waiting to be verified on a real machine (their executable names are not checked).
    public static readonly IReadOnlyList<ChatFlashApp> All =
    [
        new("wechat", "微信", "WeChat", ["Weixin.exe", "WeChat.exe"], true, ChatRestore.WeChatUri),
        new("wecom", "企业微信", "WeCom", ["WXWork.exe"], false),
        new("qq", "QQ", "QQ", ["QQ.exe"], false),
        new("feishu", "飞书", "Feishu", ["Feishu.exe", "Lark.exe"], false),
        new("dingtalk", "钉钉", "DingTalk", ["DingTalk.exe"], false)
    ];

    public static ChatFlashApp? Find(string? executable, IReadOnlyList<ChatFlashApp>? apps = null)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        string name = Path.GetFileName(executable.Trim());
        return (apps ?? All).FirstOrDefault(app => app.Executables.Any(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase)));
    }
}

// Decides what a flashing taskbar button means. The only things it is given are the flashing window and, through the two
// lookups, the file name of that window's process and whether the user is already in that application. It never sees a
// message, a sender or the application's memory or files. Flashes of one application within Window of the first of them make
// up one notice (the same instance, so the hub updates it instead of listing another); the next flash after that starts a new
// one. A window of an unknown or not enabled application, and a flash while the user is already in the application, give
// nothing. Runs on one thread (the UI thread).
public sealed class ChatFlashFilter(Func<nint, string?> executable, Func<nint, bool> inApplication, Func<DateTimeOffset> now, TimeSpan? window = null, IReadOnlyList<ChatFlashApp>? apps = null)
{
    // WeChat flashes again for each message until it is brought forward; three minutes keeps a busy chat to a few popups an hour
    // and still tells the user about a message that arrives after a quiet spell.
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(3);
    private readonly TimeSpan _window = window ?? DefaultWindow;
    private readonly Dictionary<string, (DateTimeOffset Start, int Group)> _groups = new(StringComparer.Ordinal);

    public NoticeInput? OnFlash(nint handle)
    {
        if (handle == 0 || ChatFlashApps.Find(executable(handle), apps) is not { Enabled: true } app) return null;
        if (inApplication(handle)) return null;
        var at = now();
        int group = 1;
        if (_groups.TryGetValue(app.Id, out var last))
        {
            // A clock that went back starts a new group rather than merging into one from the future.
            group = at >= last.Start && at - last.Start < _window ? last.Group : last.Group + 1;
            if (group == last.Group) return Input(app, group);
        }
        _groups[app.Id] = (at, group);
        return Input(app, group);
    }

    private static NoticeInput Input(ChatFlashApp app, int group) => new()
    {
        Origin = NoticeOrigin.Windows, AppKey = "flash:" + app.Id, AppName = app.Name, InstanceKey = $"flash:{app.Id}:{group}",
        Title = app.Name, Body = Loc.T("有新消息", "New message"), Category = NoticeCategory.Message
    };
}
