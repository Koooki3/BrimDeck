namespace BrimDeck.Core.Notices;

// The notice glyphs: BrimDeck's own line drawings for what a notice is about (its kind or state), where it came from, and
// what kind of application sent it when that application gives no icon. Every glyph is drawn on one 20 unit grid with its
// lines inside 2..18, stroked 1.5 units wide with round caps and joins and never filled, so the set reads as one family at
// 14 to 40 px and takes any color; a dot is a zero-length stroke. No logo of any product is drawn: sources and app types are
// generic objects (a terminal, a phone, a globe, a penguin, a sparkle).
public enum NoticeGlyph
{
    // Kinds and states.
    Bell, Message, Chat, Mail, Alarm, Timer, Countdown, Calendar, Meeting, Call, Task, Progress, Success, Warning, Error, Info,
    Download, Upload, Update, Security, System, Battery, Power, Network, Media,
    // Sources.
    Desktop, Terminal, Linux, Phone, Browser, Assistant, Build,
    // Application types.
    Game, Music, Video, Cloud, Code
}

// How a glyph is colored: with the text colors, the accent, or the state colors, all made readable on the theme's surface.
public enum NoticeGlyphTone { Primary, Secondary, Accent, Success, Warning, Danger, Info }

public static class NoticeGlyphs
{
    public const double Grid = 20, Stroke = 1.5, MarkStroke = 2.25;

    private static readonly Dictionary<NoticeGlyph, string> Paths = new()
    {
        [NoticeGlyph.Bell] = "M10,2.75 C7.4,2.75 5.6,4.9 5.6,7.5 V10.6 L4.1,13.4 H15.9 L14.4,10.6 V7.5 C14.4,4.9 12.6,2.75 10,2.75 Z M8,15.9 A2,2 0 0 0 12,15.9",
        [NoticeGlyph.Message] = "M4.5,3.75 H15.5 Q17.25,3.75 17.25,5.5 V11.75 Q17.25,13.5 15.5,13.5 H9.25 L5.75,16.5 V13.5 H4.5 Q2.75,13.5 2.75,11.75 V5.5 Q2.75,3.75 4.5,3.75 Z M7,8.6 V8.61 M10,8.6 V8.61 M13,8.6 V8.61",
        [NoticeGlyph.Chat] = "M4.25,2.75 H11.75 Q13.25,2.75 13.25,4.25 V8.75 Q13.25,10.25 11.75,10.25 H7.25 L4.75,12.25 V10.25 H4.25 Q2.75,10.25 2.75,8.75 V4.25 Q2.75,2.75 4.25,2.75 Z M15.75,7.25 Q17.25,7.25 17.25,8.75 V12.75 Q17.25,14.25 15.75,14.25 H15.25 V16.5 L12.5,14.25 H9.75 Q8.25,14.25 8.25,12.75 V12.5",
        [NoticeGlyph.Mail] = "M4,4.75 H16 Q17.25,4.75 17.25,6 V14 Q17.25,15.25 16,15.25 H4 Q2.75,15.25 2.75,14 V6 Q2.75,4.75 4,4.75 Z M3.5,5.75 L10,10.75 L16.5,5.75",
        [NoticeGlyph.Alarm] = "M10,5.25 A5.75,5.75 0 1 1 9.99,5.25 Z M10,8 V11 L12,12.5 M3,5 L5.25,2.75 M17,5 L14.75,2.75 M5.9,15.1 L4.5,16.75 M14.1,15.1 L15.5,16.75",
        [NoticeGlyph.Timer] = "M10,5.5 A5.75,5.75 0 1 1 9.99,5.5 Z M8,2.75 H12 M10,2.75 V5.5 M14.6,6.9 L15.9,5.6 M10,11.25 L12.6,8.65",
        [NoticeGlyph.Countdown] = "M5.25,2.75 H14.75 M5.25,17.25 H14.75 M6.25,2.75 V4.75 Q6.25,7.75 10,10 Q13.75,7.75 13.75,4.75 V2.75 M6.25,17.25 V15.25 Q6.25,12.25 10,10 Q13.75,12.25 13.75,15.25 V17.25 M8.25,15.5 H11.75",
        [NoticeGlyph.Calendar] = "M4.5,4.25 H15.5 Q17,4.25 17,5.75 V15.5 Q17,17 15.5,17 H4.5 Q3,17 3,15.5 V5.75 Q3,4.25 4.5,4.25 Z M3,8.25 H17 M6.75,2.5 V5.75 M13.25,2.5 V5.75 M6.75,11.5 V11.51 M10,11.5 V11.51 M13.25,11.5 V11.51 M6.75,14.25 V14.26 M10,14.25 V14.26",
        [NoticeGlyph.Meeting] = "M7.25,4.25 A2.5,2.5 0 1 1 7.24,4.25 Z M2.75,16 Q2.75,11.5 7.25,11.5 Q11.75,11.5 11.75,16 M13.5,4.75 A2.25,2.25 0 1 1 13.49,4.75 Z M13.25,11.5 Q17.25,11.5 17.25,15.5",
        [NoticeGlyph.Call] = "M6.4,3 L7.9,3.25 L9,6.6 L7.6,8 Q8.9,10.9 12,12.4 L13.4,11 L16.75,12.1 L17,13.6 Q17,16.25 14.6,16.75 Q8.5,16.5 3.25,5.4 Q3.75,3 6.4,3 Z",
        [NoticeGlyph.Task] = "M8.75,5.5 H16.5 M8.75,10 H16.5 M8.75,14.5 H16.5 M3.5,5.25 L4.75,6.5 L6.75,4.25 M3.5,9.75 L4.75,11 L6.75,8.75 M5,14.5 V14.51",
        [NoticeGlyph.Progress] = "M10,2.75 A7.25,7.25 0 1 1 2.75,10 M10,10 V10.01 M10,2.75 V2.76",
        [NoticeGlyph.Success] = "M10,2.75 A7.25,7.25 0 1 1 9.99,2.75 Z M6.75,10.25 L9,12.5 L13.25,7.75",
        [NoticeGlyph.Warning] = "M8.7,3.5 Q10,1.6 11.3,3.5 L17.2,14.4 Q18.3,16.5 16,16.5 H4 Q1.7,16.5 2.8,14.4 Z M10,7.5 V11 M10,13.75 V13.76",
        [NoticeGlyph.Error] = "M10,2.75 A7.25,7.25 0 1 1 9.99,2.75 Z M7.5,7.5 L12.5,12.5 M12.5,7.5 L7.5,12.5",
        [NoticeGlyph.Info] = "M10,2.75 A7.25,7.25 0 1 1 9.99,2.75 Z M10,9 V14 M10,6.25 V6.26",
        [NoticeGlyph.Download] = "M10,2.75 V12.5 M6,8.75 L10,12.75 L14,8.75 M3.5,13 V15.5 Q3.5,17 5,17 H15 Q16.5,17 16.5,15.5 V13",
        [NoticeGlyph.Upload] = "M10,12.75 V3 M6,7 L10,3 L14,7 M3.5,13 V15.5 Q3.5,17 5,17 H15 Q16.5,17 16.5,15.5 V13",
        [NoticeGlyph.Update] = "M16.25,8.5 A6.5,6.5 0 0 0 4.4,6.25 M3.75,11.5 A6.5,6.5 0 0 0 15.6,13.75 M4.25,2.75 V6.5 H8 M15.75,17.25 V13.5 H12",
        [NoticeGlyph.Security] = "M10,2.25 L16.75,4.75 V9.25 C16.75,13.4 13.9,16.6 10,17.9 C6.1,16.6 3.25,13.4 3.25,9.25 V4.75 Z M7,10 L9.25,12.25 L13.25,8",
        [NoticeGlyph.System] = "M4,3.5 H16 Q17.25,3.5 17.25,4.75 V12.25 Q17.25,13.5 16,13.5 H4 Q2.75,13.5 2.75,12.25 V4.75 Q2.75,3.5 4,3.5 Z M10,13.5 V16.5 M6.5,16.5 H13.5",
        [NoticeGlyph.Battery] = "M3.75,6 H14.5 Q15.75,6 15.75,7.25 V12.75 Q15.75,14 14.5,14 H3.75 Q2.5,14 2.5,12.75 V7.25 Q2.5,6 3.75,6 Z M17.5,8.5 V11.5 M5,8.5 V11.5 M7.5,8.5 V11.5",
        [NoticeGlyph.Power] = "M11,2.5 L4.75,11 H9.5 L8.75,17.5 L15.25,9 H10.5 Z",
        [NoticeGlyph.Network] = "M2.75,8 Q10,1.5 17.25,8 M5.25,10.75 Q10,6.5 14.75,10.75 M7.75,13.5 Q10,11.75 12.25,13.5 M10,16.25 V16.26",
        [NoticeGlyph.Media] = "M10,2.75 A7.25,7.25 0 1 1 9.99,2.75 Z M8.5,6.9 L13.25,10 L8.5,13.1 Z",
        [NoticeGlyph.Desktop] = "M4.25,3.5 H15.75 Q17,3.5 17,4.75 V15.25 Q17,16.5 15.75,16.5 H4.25 Q3,16.5 3,15.25 V4.75 Q3,3.5 4.25,3.5 Z M3,7 H17 M5.5,5.25 V5.26 M7.75,5.25 V5.26",
        [NoticeGlyph.Terminal] = "M4.25,3.5 H15.75 Q17,3.5 17,4.75 V15.25 Q17,16.5 15.75,16.5 H4.25 Q3,16.5 3,15.25 V4.75 Q3,3.5 4.25,3.5 Z M6,7.5 L8.75,10 L6,12.5 M10.5,12.5 H14",
        [NoticeGlyph.Linux] = "M10,2.75 Q12.75,2.75 12.75,6 Q12.75,7.5 14,9.5 Q16.75,13 15,16 Q14,17.25 10,17.25 Q6,17.25 5,16 Q3.25,13 6,9.5 Q7.25,7.5 7.25,6 Q7.25,2.75 10,2.75 Z M8.75,5.5 V5.51 M11.25,5.5 V5.51 M9.25,7.25 L10,7.9 L10.75,7.25 M7.75,12.25 Q10,15.25 12.25,12.25",
        [NoticeGlyph.Phone] = "M6.25,2.5 H13.75 Q15,2.5 15,3.75 V16.25 Q15,17.5 13.75,17.5 H6.25 Q5,17.5 5,16.25 V3.75 Q5,2.5 6.25,2.5 Z M8.75,15 H11.25",
        [NoticeGlyph.Browser] = "M10,2.75 A7.25,7.25 0 1 1 9.99,2.75 Z M2.75,10 H17.25 M10,2.75 Q6.5,6.25 6.5,10 Q6.5,13.75 10,17.25 M10,2.75 Q13.5,6.25 13.5,10 Q13.5,13.75 10,17.25",
        [NoticeGlyph.Assistant] = "M9.25,4.25 Q10,10 15.75,10.75 Q10,11.5 9.25,17.25 Q8.5,11.5 2.75,10.75 Q8.5,10 9.25,4.25 Z M15.25,2.5 V6 M13.5,4.25 H17",
        [NoticeGlyph.Build] = "M5,3 A2,2 0 1 1 4.99,3 Z M5,13 A2,2 0 1 1 4.99,13 Z M15,8 A2,2 0 1 1 14.99,8 Z M5,7 V13 M6.9,5.6 Q11,6.25 13.4,8.8",
        [NoticeGlyph.Game] = "M6.5,6 H13.5 Q17,6 17.5,10.5 L17.75,13 Q17.9,15 16.25,15 Q15,15 14,13.5 L13.25,12.5 H6.75 L6,13.5 Q5,15 3.75,15 Q2.1,15 2.25,13 L2.5,10.5 Q3,6 6.5,6 Z M6.25,8.25 V10.75 M5,9.5 H7.5 M12.75,9 V9.01 M14.5,10.5 V10.51",
        [NoticeGlyph.Music] = "M7.5,15 V4.5 L16,2.75 V12.75 M7.5,15 A2.25,2.25 0 1 1 3,15 A2.25,2.25 0 1 1 7.5,15 M16,12.75 A2.25,2.25 0 1 1 11.5,12.75 A2.25,2.25 0 1 1 16,12.75",
        [NoticeGlyph.Video] = "M3.75,5 H12.25 Q13.5,5 13.5,6.25 V13.75 Q13.5,15 12.25,15 H3.75 Q2.5,15 2.5,13.75 V6.25 Q2.5,5 3.75,5 Z M13.5,8.5 L17.5,6.25 V13.75 L13.5,11.5",
        [NoticeGlyph.Cloud] = "M6,15.5 Q2.5,15.5 2.5,12.25 Q2.5,9.25 5.5,9 Q6,4.75 10.25,4.75 Q14,4.75 14.75,8.5 Q17.5,8.75 17.5,12 Q17.5,15.5 14,15.5 Z",
        [NoticeGlyph.Code] = "M7,6 L3,10 L7,14 M13,6 L17,10 L13,14 M11.25,4.5 L8.75,15.5",
    };

    // The bare marks drawn inside a small round badge, where an outline around them would only crowd it. Stroked MarkStroke wide.
    private static readonly Dictionary<NoticeGlyph, string> Marks = new()
    {
        [NoticeGlyph.Success] = "M5.5,10.25 L8.5,13.25 L14.5,6.75",
        [NoticeGlyph.Error] = "M6.5,6.5 L13.5,13.5 M13.5,6.5 L6.5,13.5",
        [NoticeGlyph.Warning] = "M10,4.75 V11 M10,15 V15.01",
        [NoticeGlyph.Info] = "M10,9 V15 M10,5.25 V5.26",
        [NoticeGlyph.Progress] = "M10,4.5 A5.5,5.5 0 1 1 4.5,10",
    };

    public static IReadOnlyCollection<NoticeGlyph> All { get; } = Enum.GetValues<NoticeGlyph>();
    public static string PathData(NoticeGlyph glyph) => Paths[glyph];
    // The data a badge draws: the bare mark where there is one, else the glyph itself.
    public static string BadgeData(NoticeGlyph glyph, out bool mark) => (mark = Marks.TryGetValue(glyph, out var data)) ? data! : Paths[glyph];

    // Kinds by name, as a source or a sender may name them ("brim-notify --kind timer", a time notice, a category). New kinds
    // plug in here; an unknown name has no glyph and leaves the notice's category to decide.
    public static IReadOnlyDictionary<string, NoticeGlyph> Kinds { get; } = new Dictionary<string, NoticeGlyph>(StringComparer.OrdinalIgnoreCase)
    {
        ["message"] = NoticeGlyph.Message, ["chat"] = NoticeGlyph.Chat, ["mail"] = NoticeGlyph.Mail, ["email"] = NoticeGlyph.Mail,
        ["alarm"] = NoticeGlyph.Alarm, ["reminder"] = NoticeGlyph.Bell, ["notice"] = NoticeGlyph.Bell,
        ["timer"] = NoticeGlyph.Timer, ["stopwatch"] = NoticeGlyph.Timer, ["countdown"] = NoticeGlyph.Countdown,
        ["calendar"] = NoticeGlyph.Calendar, ["event"] = NoticeGlyph.Calendar, ["meeting"] = NoticeGlyph.Meeting, ["call"] = NoticeGlyph.Call,
        ["task"] = NoticeGlyph.Task, ["progress"] = NoticeGlyph.Progress,
        ["success"] = NoticeGlyph.Success, ["done"] = NoticeGlyph.Success, ["warning"] = NoticeGlyph.Warning,
        ["error"] = NoticeGlyph.Error, ["failed"] = NoticeGlyph.Error, ["info"] = NoticeGlyph.Info,
        ["download"] = NoticeGlyph.Download, ["upload"] = NoticeGlyph.Upload, ["update"] = NoticeGlyph.Update,
        ["security"] = NoticeGlyph.Security, ["shield"] = NoticeGlyph.Security, ["system"] = NoticeGlyph.System, ["windows"] = NoticeGlyph.Desktop,
        ["battery"] = NoticeGlyph.Battery, ["power"] = NoticeGlyph.Power, ["network"] = NoticeGlyph.Network, ["media"] = NoticeGlyph.Media,
        ["music"] = NoticeGlyph.Music, ["video"] = NoticeGlyph.Video, ["terminal"] = NoticeGlyph.Terminal, ["wsl"] = NoticeGlyph.Linux,
        ["linux"] = NoticeGlyph.Linux, ["phone"] = NoticeGlyph.Phone, ["browser"] = NoticeGlyph.Browser, ["web"] = NoticeGlyph.Browser,
        ["assistant"] = NoticeGlyph.Assistant, ["agent"] = NoticeGlyph.Assistant, ["ai"] = NoticeGlyph.Assistant,
        ["build"] = NoticeGlyph.Build, ["ci"] = NoticeGlyph.Build, ["game"] = NoticeGlyph.Game, ["cloud"] = NoticeGlyph.Cloud, ["code"] = NoticeGlyph.Code,
    };
    public static NoticeGlyph? Kind(string? name) => name is { Length: > 0 } && Kinds.TryGetValue(name.Trim(), out var glyph) ? glyph : null;

    // The state mark of a notice: its kind when it names one, else its category. A plain message has none.
    public static NoticeGlyph? Status(NoticeCategory category, string? kind = null) => Kind(kind) ?? category switch
    {
        NoticeCategory.Success => NoticeGlyph.Success, NoticeCategory.Warning => NoticeGlyph.Warning, NoticeCategory.Error => NoticeGlyph.Error,
        NoticeCategory.Info => NoticeGlyph.Info, NoticeCategory.Task => NoticeGlyph.Progress, _ => null
    };

    public static NoticeGlyphTone Tone(NoticeGlyph glyph) => glyph switch
    {
        NoticeGlyph.Success => NoticeGlyphTone.Success, NoticeGlyph.Warning => NoticeGlyphTone.Warning, NoticeGlyph.Error => NoticeGlyphTone.Danger,
        NoticeGlyph.Info => NoticeGlyphTone.Info, NoticeGlyph.Progress or NoticeGlyph.Task or NoticeGlyph.Download or NoticeGlyph.Upload or NoticeGlyph.Update
            or NoticeGlyph.Alarm or NoticeGlyph.Timer or NoticeGlyph.Countdown or NoticeGlyph.Calendar or NoticeGlyph.Meeting or NoticeGlyph.Call => NoticeGlyphTone.Accent,
        _ => NoticeGlyphTone.Primary
    };

    // Where a notice came from, when that is not the Windows application itself: WSL, brim-notify, or a phone or a web app that
    // Windows relays under another application's name.
    public static NoticeGlyph? Source(NoticeOrigin origin, string appKey) => origin switch
    {
        NoticeOrigin.Wsl => NoticeGlyph.Linux,
        NoticeOrigin.Cli => NoticeGlyph.Terminal,
        NoticeOrigin.Windows when Contains(appKey.ToLowerInvariant(), "yourphone", "phonelink", "crossdevice") => NoticeGlyph.Phone,
        NoticeOrigin.Windows when IsWebApp(appKey) => NoticeGlyph.Browser,
        _ => null
    };

    // A web app installed from a browser (its notices come through the browser under the app's own name).
    public static bool IsWebApp(string appKey) => appKey.Contains("_crx_", StringComparison.OrdinalIgnoreCase);

    // The kind of application, guessed from its key and name, for an application without an icon: a chat glyph for a chat
    // application rather than only its first letter. Null when nothing fits; the initial is then shown. Rules are checked in
    // order, so "qqmusic" is music before "qq" is chat. A word with "=" must be a whole word ("line" is not "pipeline");
    // the others may appear anywhere in the key or name, which also catches "WindowsTerminal" and Chinese names.
    private static readonly (NoticeGlyph Glyph, string[] Words)[] AppRules =
    [
        (NoticeGlyph.Phone, ["yourphone", "phonelink", "phone link", "crossdevice", "手机连接", "=phone"]),
        (NoticeGlyph.Assistant, ["claude", "chatgpt", "openai", "copilot", "gemini", "codex", "deepseek", "assistant", "助手", "doubao", "豆包", "通义", "qwen", "kimi", "grok", "=ai", "=agent"]),
        (NoticeGlyph.Build, ["github", "gitlab", "jenkins", "devops", "pipeline", "buildkite", "teamcity", "=ci", "=build", "构建"]),
        (NoticeGlyph.Code, ["vscode", "visual studio", "visualstudio", "jetbrains", "intellij", "pycharm", "webstorm", "=rider", "sublime", "notepad++", "cursor", "=code", "=idea", "=vim", "=nvim", "=emacs", "=zed"]),
        (NoticeGlyph.Terminal, ["terminal", "powershell", "pwsh", "console", "终端", "命令提示符", "=cmd", "=bash", "=zsh", "=shell", "=ssh", "=wsl", "brim-notify"]),
        (NoticeGlyph.Mail, ["outlook", "thunderbird", "foxmail", "gmail", "邮件", "邮箱", "=mail", "=email"]),
        (NoticeGlyph.Calendar, ["calendar", "日历", "日程"]),
        (NoticeGlyph.Music, ["spotify", "music", "网易云", "音乐", "foobar", "kugou", "酷狗", "kuwo", "酷我", "itunes"]),
        (NoticeGlyph.Video, ["video", "bilibili", "哔哩", "potplayer", "youtube", "视频", "netflix", "movies", "影音", "iqiyi", "爱奇艺", "youku", "优酷", "=vlc", "=obs"]),
        (NoticeGlyph.Chat, ["wechat", "weixin", "微信", "telegram", "slack", "teams", "discord", "whatsapp", "dingtalk", "钉钉", "飞书", "feishu", "lark", "messenger", "skype", "wecom", "企业微信", "zoom", "腾讯会议", "wemeet", "chat", "消息", "=qq", "=tim", "=signal", "=line"]),
        (NoticeGlyph.Game, ["steam", "epicgames", "epic games", "xbox", "game", "游戏", "battle.net", "ubisoft", "=riot", "=gog"]),
        (NoticeGlyph.Cloud, ["onedrive", "dropbox", "icloud", "网盘", "netdisk", "nutstore", "坚果云", "cloud", "=drive"]),
        (NoticeGlyph.Browser, ["chrome", "msedge", "firefox", "browser", "浏览器", "=opera", "brave", "vivaldi", "safari", "_crx_", "=edge"]),
        (NoticeGlyph.Security, ["defender", "security", "安全", "antivirus", "huorong", "火绒", "=360"]),
        (NoticeGlyph.Download, ["download", "下载", "迅雷", "torrent", "=idm"]),
        (NoticeGlyph.Update, ["windowsupdate", "updater", "更新"]),
        (NoticeGlyph.Network, ["network", "wifi", "wi-fi", "网络", "=vpn", "clash"]),
        (NoticeGlyph.Battery, ["battery", "电池", "电源"]),
        (NoticeGlyph.System, ["explorer", "settings", "设置", "系统", "systemtoast"]),
    ];

    public static NoticeGlyph? App(string appKey, string appName, NoticeOrigin origin = NoticeOrigin.Windows)
    {
        // The origin's prefix ("windows:", "wsl:", "cli:") says nothing about the application.
        int colon = appKey.IndexOf(':');
        string key = colon > 0 && appKey[..colon] is "windows" or "wsl" or "cli" ? appKey[(colon + 1)..] : appKey;
        string text = (appName + " " + key).ToLowerInvariant();
        var words = Words(appName + " " + key);
        foreach (var (glyph, list) in AppRules)
            foreach (var word in list)
                if (word[0] == '=' ? words.Contains(word[1..]) : text.Contains(word, StringComparison.Ordinal)) return glyph;
        return origin switch { NoticeOrigin.Cli => NoticeGlyph.Terminal, NoticeOrigin.Wsl => NoticeGlyph.Linux, _ => null };
    }

    // The words of a key or name: split at anything but letters and digits and between a lower and an upper case letter
    // ("WindowsTerminal" is "windows" and "terminal").
    private static HashSet<string> Words(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var current = new System.Text.StringBuilder();
        void Flush() { if (current.Length > 0) { words.Add(current.ToString().ToLowerInvariant()); current.Clear(); } }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
            if (char.IsUpper(c) && i > 0 && char.IsLower(text[i - 1])) Flush();
            current.Append(c);
        }
        Flush();
        return words;
    }

    private static bool Contains(string text, params string[] words) => words.Any(word => text.Contains(word, StringComparison.Ordinal));

    // The glyph's name for screen readers.
    public static string Name(NoticeGlyph glyph) => glyph switch
    {
        NoticeGlyph.Bell => Loc.T("通知", "Notice"), NoticeGlyph.Message => Loc.T("消息", "Message"), NoticeGlyph.Chat => Loc.T("聊天", "Chat"),
        NoticeGlyph.Mail => Loc.T("邮件", "Mail"), NoticeGlyph.Alarm => Loc.T("闹钟", "Alarm"), NoticeGlyph.Timer => Loc.T("计时", "Timer"),
        NoticeGlyph.Countdown => Loc.T("倒计时", "Countdown"), NoticeGlyph.Calendar => Loc.T("日程", "Event"), NoticeGlyph.Meeting => Loc.T("会议", "Meeting"),
        NoticeGlyph.Call => Loc.T("来电", "Call"), NoticeGlyph.Task => Loc.T("任务", "Task"), NoticeGlyph.Progress => Loc.T("进行中", "In progress"),
        NoticeGlyph.Success => Loc.T("成功", "Success"), NoticeGlyph.Warning => Loc.T("警告", "Warning"), NoticeGlyph.Error => Loc.T("错误", "Error"),
        NoticeGlyph.Info => Loc.T("信息", "Information"), NoticeGlyph.Download => Loc.T("下载", "Download"), NoticeGlyph.Upload => Loc.T("上传", "Upload"),
        NoticeGlyph.Update => Loc.T("更新", "Update"), NoticeGlyph.Security => Loc.T("安全", "Security"), NoticeGlyph.System => Loc.T("系统", "System"),
        NoticeGlyph.Battery => Loc.T("电池", "Battery"), NoticeGlyph.Power => Loc.T("电源", "Power"), NoticeGlyph.Network => Loc.T("网络", "Network"),
        NoticeGlyph.Media => Loc.T("媒体", "Media"), NoticeGlyph.Desktop => Loc.T("Windows 应用", "Windows app"), NoticeGlyph.Terminal => Loc.T("终端", "Terminal"),
        NoticeGlyph.Linux => "WSL", NoticeGlyph.Phone => Loc.T("手机", "Phone"), NoticeGlyph.Browser => Loc.T("网页应用", "Web app"),
        NoticeGlyph.Assistant => Loc.T("AI 助手", "AI assistant"), NoticeGlyph.Build => Loc.T("构建", "Build"), NoticeGlyph.Game => Loc.T("游戏", "Game"),
        NoticeGlyph.Music => Loc.T("音乐", "Music"), NoticeGlyph.Video => Loc.T("视频", "Video"), NoticeGlyph.Cloud => Loc.T("云存储", "Cloud storage"),
        NoticeGlyph.Code => Loc.T("代码", "Code"), _ => glyph.ToString()
    };
}

// The colors the glyphs are drawn in on one theme. Every color is opaque and keeps at least 3:1 (WCAG's minimum for graphics)
// against each color it is drawn on: the state colors and the badge on the surface and its gradient stops, the badge's mark
// against the badge (it is drawn in the surface color), an application glyph on its plate. High contrast takes the system
// colors only and tells states apart by shape.
public sealed record NoticeGlyphColors(
    ThemeColor Surface, ThemeColor Plate, IReadOnlyList<ThemeColor> Backdrops, ThemeColor Primary, ThemeColor Secondary, ThemeColor Accent,
    ThemeColor Success, ThemeColor Warning, ThemeColor Danger, ThemeColor Info, ThemeColor BadgeInk, bool HighContrast)
{
    public const double Minimum = PanelThemes.GraphicContrast;
    public const string SuccessDefault = "#34C759";

    public static NoticeGlyphColors FromPalette(PanelPalette palette)
    {
        var surface = palette.Surface.Over(ThemeColor.Black);
        var plate = palette["fill"].Over(surface);
        IReadOnlyList<ThemeColor> backdrops = [.. palette.SurfaceStops.Select(stop => stop.Over(ThemeColor.Black)), plate];
        ThemeColor On(ThemeColor color) => color.Over(surface).EnsureContrast(backdrops, Minimum);
        var accent = On(palette["accent"]);
        return new(surface, plate, backdrops, On(palette["textPrimary"]), On(palette["textSecondary"]), accent,
            On(ThemeColor.Parse(SuccessDefault)), On(palette.WarningColor), On(palette["danger"]), accent, surface, false);
    }

    // High contrast: text on the window color, states in the highlight with the highlight's text inside. A scheme whose
    // highlight does not stand out from the window draws them in the window text instead.
    public static NoticeGlyphColors FromSystem(ThemeColor window, ThemeColor windowText, ThemeColor control, ThemeColor highlight, ThemeColor highlightText)
    {
        window = window.WithAlpha(255); control = control.Over(window);
        IReadOnlyList<ThemeColor> backdrops = [window, control];
        bool highlightReads = highlight.MinimumContrast(backdrops) >= Minimum && ThemeColor.Contrast(highlightText, highlight) >= Minimum;
        var state = highlightReads ? highlight.WithAlpha(255) : windowText.WithAlpha(255);
        var ink = highlightReads ? highlightText.WithAlpha(255) : window;
        var text = windowText.WithAlpha(255);
        return new(window, control, backdrops, text, text, state, state, state, state, state, ink, true);
    }

    public ThemeColor Tone(NoticeGlyphTone tone) => tone switch
    {
        NoticeGlyphTone.Secondary => Secondary, NoticeGlyphTone.Accent => Accent, NoticeGlyphTone.Success => Success,
        NoticeGlyphTone.Warning => Warning, NoticeGlyphTone.Danger => Danger, NoticeGlyphTone.Info => Info, _ => Primary
    };

    // An application's color for its glyph on the plate; in high contrast the text color.
    public ThemeColor OnPlate(ThemeColor appColor) => HighContrast ? Primary : appColor.Over(Plate).EnsureContrast(Backdrops, Minimum);
}
