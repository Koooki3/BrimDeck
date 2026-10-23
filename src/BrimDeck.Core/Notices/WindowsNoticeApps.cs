namespace BrimDeck.Core.Notices;

// Who a Windows notification is from, and its text, from what Windows tells about it. The application id (AUMID) is not
// always helpful on its own: a site installed as an app from Edge uses the browser's package with the site's address, a
// site's push notice in Chrome carries the site only as its last line (the attribution), a desktop program registered for
// notifications without a Start menu shortcut has its name only in the registry, and Windows names the tray balloons of older
// programs by a number. Everything read here comes from the sender, so names are clipped and never used as paths or commands.
public static class WindowsNoticeApps
{
    // What Windows tells about one notification. DisplayName is AppInfo.DisplayInfo's; RegisteredName the DisplayName under
    // HKCU\Software\Classes\AppUserModelId\<id>. Texts are the notification's text elements in order, empty ones left out.
    public sealed record Reading(string AppId, string? DisplayName, string? RegisteredName, IReadOnlyList<string> Texts);

    public sealed record Identity(string AppKey, string AppName, string Title, string Body, NoticeCategory Category);

    // Chromium browsers whose notifications end with the site's host as an attribution line: by application id, or the
    // start of a packaged browser's id.
    private static readonly string[] Browsers = ["Chrome", "ChromeBeta", "ChromeDev", "ChromeCanary", "Chromium", "MSEdge", "MSEdgeBeta", "MSEdgeDev", "MSEdgeCanary", "Brave", "Vivaldi", "Opera"];
    private static readonly string[] BrowserPackages = ["Microsoft.MicrosoftEdge"];

    // Well-known sites by host (or a parent domain of it), so a web app reads as "WhatsApp" rather than its address.
    // Names in Chinese and English where they differ; picked when a notice arrives.
    private static readonly (string Host, string Name, string? English)[] Sites =
    [
        ("whatsapp.com", "WhatsApp", null), ("facebook.com", "Facebook", null), ("messenger.com", "Messenger", null), ("instagram.com", "Instagram", null),
        ("telegram.org", "Telegram", null), ("discord.com", "Discord", null), ("slack.com", "Slack", null), ("mail.google.com", "Gmail", null),
        ("calendar.google.com", "Google Calendar", null), ("chat.google.com", "Google Chat", null), ("gemini.google.com", "Gemini", null),
        ("outlook.live.com", "Outlook", null), ("outlook.office.com", "Outlook", null), ("outlook.office365.com", "Outlook", null),
        ("teams.microsoft.com", "Teams", null), ("teams.live.com", "Teams", null), ("youtube.com", "YouTube", null), ("x.com", "X", null), ("twitter.com", "X", null),
        ("github.com", "GitHub", null), ("gitlab.com", "GitLab", null), ("notion.so", "Notion", null), ("chatgpt.com", "ChatGPT", null), ("claude.ai", "Claude", null),
        ("feishu.cn", "飞书", "Feishu"), ("larksuite.com", "Lark", null), ("dingtalk.com", "钉钉", "DingTalk"), ("bilibili.com", "哔哩哔哩", "Bilibili")
    ];

    // The status a notice from these applications gets: Windows notifications carry no category of their own. Matched by
    // the start of the application id; anything not listed (chat, mail, browsers) stays a message.
    private static readonly (string Prefix, NoticeCategory Category)[] Categories =
    [
        // Coding agents: their notifications say that a task finished or waits for the user.
        ("Claude_", NoticeCategory.Task), ("OpenAI.Codex_", NoticeCategory.Task),
        // System and utility notices: devices, settings, security summaries, driver and app updates, proxy state, screenshots.
        ("Windows.SystemToast.", NoticeCategory.Info), ("Windows.Defender.", NoticeCategory.Info), ("Windows.ActionCenter.", NoticeCategory.Info),
        ("windows.immersivecontrolpanel_", NoticeCategory.Info), ("Microsoft.WindowsStore_", NoticeCategory.Info), ("com.nvidia.", NoticeCategory.Info),
        ("Adobe.CreativeCloud", NoticeCategory.Info), ("party.mihomo.app", NoticeCategory.Info), ("Microsoft.ScreenSketch_", NoticeCategory.Info)
    ];

    public const string TrayPrefix = "NotifyIconGeneratedAumid_";

    public static Identity Identify(Reading reading, int maxName = 256)
    {
        string appId = reading.AppId;
        var texts = reading.Texts.ToList();
        string appKey = NoticeKeys.Windows(appId);
        string name = Name(reading);
        if (WebAppSite(appId) is { } webSite)
        {
            // An installed web app: named after its site, kept apart from the browser and from other sites' apps.
            name = SiteName(webSite);
            if (texts.Count >= 2 && string.Equals(texts[^1], webSite, StringComparison.OrdinalIgnoreCase)) texts.RemoveAt(texts.Count - 1);
        }
        else if (IsBrowser(appId) && texts.Count >= 3 && Host(texts[^1]) is { } site)
        {
            // A site's push notice in the browser: the last line is the site, which Chrome adds itself. The site gets its own
            // name and key, so it can be muted without muting every other site.
            texts.RemoveAt(texts.Count - 1);
            appKey += "|" + site;
            name = SiteName(site) + " · " + name;
        }
        string title = texts.Count > 0 ? texts[0] : "";
        string body = string.Join("\n", texts.Skip(1));
        return new Identity(appKey, Clip(name, maxName), title, body, Category(appId));
    }

    // The application's name: Windows' display name, else the registered one, else something better than the raw id.
    public static string Name(Reading reading)
    {
        string appId = reading.AppId;
        if (Usable(reading.DisplayName, appId) is { } display) return display;
        if (Usable(reading.RegisteredName, appId) is { } registered) return registered;
        if (appId.StartsWith(TrayPrefix, StringComparison.OrdinalIgnoreCase)) return Loc.T("托盘程序", "Tray app");
        // A desktop program without a registered id is named by its path, "{known folder GUID}\folder\app.exe".
        int slash = appId.LastIndexOf('\\');
        if (slash >= 0 && slash < appId.Length - 1 && appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return appId[(slash + 1)..^4];
        return appId;
    }

    private static string? Usable(string? name, string appId)
    {
        name = name?.Trim();
        return string.IsNullOrEmpty(name) || string.Equals(name, appId, StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    // The site of a web app installed from a browser: "Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!https://web.whatsapp.com/",
    // or with a profile token before the address ("…!CgdEZWZhdWx0 https://web.whatsapp.com/"). Null for anything else.
    public static string? WebAppSite(string appId)
    {
        int bang = appId.IndexOf('!');
        if (bang < 0 || !IsBrowser(appId[..bang])) return null;
        string rest = appId[(bang + 1)..];
        int start = rest.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = rest.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || start > 0 && rest[start - 1] != ' ') return null;
        return Uri.TryCreate(rest[start..], UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && Host(uri.IdnHost) is { } host ? host : null;
    }

    public static bool IsBrowser(string appId)
    {
        int bang = appId.IndexOf('!');
        string head = bang < 0 ? appId : appId[..bang];
        return Browsers.Contains(head, StringComparer.OrdinalIgnoreCase)
            || BrowserPackages.Any(package => head.StartsWith(package, StringComparison.OrdinalIgnoreCase));
    }

    // A text that is a bare host name ("web.whatsapp.com", "localhost:3000" is not), lower-cased; null otherwise.
    public static string? Host(string text)
    {
        text = text.Trim();
        if (text.Length is < 3 or > 253 || !text.Contains('.') || text[0] is '.' or '-' || text[^1] is '.' or '-') return null;
        foreach (char c in text)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) return null;
        if (text.Contains("..", StringComparison.Ordinal) || text.Split('.')[^1].All(char.IsAsciiDigit)) return null;
        return text.ToLowerInvariant();
    }

    // A well-known site's name, else its host without "www.".
    public static string SiteName(string host)
    {
        foreach (var (known, name, english) in Sites)
            if (host == known || host.EndsWith("." + known, StringComparison.Ordinal)) return english is null ? name : Loc.T(name, english);
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    public static NoticeCategory Category(string appId)
    {
        foreach (var (prefix, category) in Categories)
            if (appId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return category;
        return NoticeCategory.Message;
    }

    private static string Clip(string text, int limit) => BrimDeck.Protocol.NoticeProtocol.Clip(text, limit)!;
}

// Windows replaces a notification that is sent again with the same tag and group (a reminder that moves on, a chat that
// gathers its new messages into one notification): the old one leaves the list and the new one arrives with a new id, in
// the same change. Within one reading, a new notice from the same application with the same title as one that left is
// taken as its replacement, so BrimDeck updates its notice instead of keeping both.
public static class NoticeReplacements
{
    public sealed record Gone(string InstanceKey, string AppKey, string Title);

    public static (List<NoticeInput> Added, List<string> Removed) Pair(IReadOnlyList<NoticeInput> added, IReadOnlyList<Gone> removed)
    {
        var left = removed.ToList();
        var result = new List<NoticeInput>(added.Count);
        foreach (var input in added)
        {
            int match = input.Title.Length == 0 ? -1 : left.FindIndex(gone => gone.AppKey == input.AppKey && gone.Title == input.Title);
            if (match < 0) { result.Add(input); continue; }
            result.Add(input with { Replaces = left[match].InstanceKey });
            left.RemoveAt(match);
        }
        return (result, left.Select(gone => gone.InstanceKey).ToList());
    }
}
