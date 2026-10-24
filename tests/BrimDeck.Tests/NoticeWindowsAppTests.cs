using BrimDeck.Core;
using BrimDeck.Core.Notices;

// How Windows notifications are attributed (WindowsNoticeApps) and how a replaced notification is recognized
// (NoticeReplacements), with application ids as the senders on a real machine use them.
internal static class NoticeWindowsAppTests
{
    private static readonly DateTimeOffset WinT0 = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    public static void Run(Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try
        {
            Names(check);
            WebApps(check);
            Categories(check);
            Replacements(check);
        }
        finally { Loc.Use(language); }
    }

    private static WindowsNoticeApps.Identity Who(string appId, string? display, params string[] texts) => WindowsNoticeApps.Identify(new(appId, display, null, texts));

    private static void Names(Action<string, bool> check)
    {
        check("A Windows notice keeps the display name Windows gives", Who("QQ", "QQ", "Alice", "hello").AppName == "QQ");
        check("A registered desktop program is named from the registry when Windows has no name",
            WindowsNoticeApps.Identify(new("Google.Play.Games", null, "Google Play Games", ["t"])).AppName == "Google Play Games");
        check("A display name that only repeats the id falls back to the registered name",
            WindowsNoticeApps.Identify(new("Google.Play.Games", "Google.Play.Games", "Google Play Games", ["t"])).AppName == "Google Play Games");
        check("A tray balloon source is not named by its generated number", Who("NotifyIconGeneratedAumid_14992917314978067803", null, "t").AppName == "托盘程序");
        check("A tray balloon source with a display name keeps it", Who("NotifyIconGeneratedAumid_1", "Tailscale", "t").AppName == "Tailscale");
        check("An unregistered desktop program is named by its file", Who(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe", null, "t").AppName == "powershell");
        check("An application without any name keeps its id", Who("Some.Unknown.App", "", "t").AppName == "Some.Unknown.App");
        check("Desktop programs Windows gives no name for are named from BrimDeck's list", Who("BiliBiliPC", "", "t").AppName == "哔哩哔哩"
            && Who("ali.dingtalk.release", null, "t").AppName == "钉钉" && Who("com.google.gemini.windows", null, "t").AppName == "Gemini"
            && Who("Microsoft.Office.OUTLOOK.EXE.15", null, "t").AppName == "Outlook");
        check("A known id still takes the name Windows gives", Who("BiliBiliPC", "哔哩哔哩 PC", "t").AppName == "哔哩哔哩 PC");
        check("A Squirrel-installed program is named by its program", Who("com.squirrel.Discord.Discord", null, "t").AppName == "Discord"
            && Who("com.squirrel.slack.slack", null, "t").AppName == "Slack" && Who("com.squirrel.", null, "t").AppName == "com.squirrel."
            && Who("com.squirrel.x.a b", null, "t").AppName == "com.squirrel.x.a b");
        var hostile = Who("Evil", "Bad‮name\u0007" + new string('x', 400), "t");
        check("A name from the sender is clipped and loses direction overrides and control characters",
            hostile.AppName.Length == 256 && !hostile.AppName.Contains('‮') && !hostile.AppName.Contains('\u0007'));
        var lines = Who("QQ", "QQ", "群聊", "第一行", "第二行");
        check("The first text is the title and the rest form a multi-line body", lines.Title == "群聊" && lines.Body == "第一行\n第二行" && lines.AppKey == "windows:QQ");
    }

    private static void WebApps(Action<string, bool> check)
    {
        check("An Edge web app's site is read from its id", WindowsNoticeApps.WebAppSite("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!https://web.whatsapp.com/") == "web.whatsapp.com");
        check("An Edge web app id with a profile token is read too", WindowsNoticeApps.WebAppSite("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!CgdEZWZhdWx0EgAQ https://web.whatsapp.com/") == "web.whatsapp.com");
        check("An ordinary packaged app is no web app", WindowsNoticeApps.WebAppSite("Microsoft.ScreenSketch_8wekyb3d8bbwe!App") is null
            && WindowsNoticeApps.WebAppSite("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!App") is null);
        check("A non-browser id with an address is no web app", WindowsNoticeApps.WebAppSite("Other.App_123!https://web.whatsapp.com/") is null);
        check("An address glued to other text is no web app", WindowsNoticeApps.WebAppSite("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!xhttps://a.com/") is null);

        var whatsapp = Who("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!https://web.whatsapp.com/", "Microsoft Edge", "Bob", "See you", "web.whatsapp.com");
        check("An Edge web app is named after its site and loses the site line",
            whatsapp.AppName == "WhatsApp" && whatsapp.Title == "Bob" && whatsapp.Body == "See you" && whatsapp.AppKey == "windows:Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!https://web.whatsapp.com/");
        var facebook = Who("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!https://www.facebook.com/", "Microsoft Edge", "Carol", "liked");
        check("Different web apps stay different applications", facebook.AppName == "Facebook" && facebook.AppKey != whatsapp.AppKey && facebook.Body == "liked");
        check("An unknown site is named by its host", WindowsNoticeApps.SiteName("www.example.org") == "example.org" && WindowsNoticeApps.SiteName("chat.example.org") == "chat.example.org");
        check("A subdomain of a known site takes its name", WindowsNoticeApps.SiteName("app.slack.com") == "Slack" && WindowsNoticeApps.SiteName("notslack.com") == "notslack.com");

        var tab = Who("Chrome", "Google Chrome", "New mail", "From Dan", "mail.google.com");
        check("A site's notice in Chrome gets the site's name and its own key", tab.AppName == "Gmail · Google Chrome" && tab.AppKey == "windows:Chrome|mail.google.com"
            && tab.Title == "New mail" && tab.Body == "From Dan");
        var shortChrome = Who("Chrome", "Google Chrome", "Done", "example.com");
        check("A two-line Chrome notice keeps its last line as body", shortChrome.AppKey == "windows:Chrome" && shortChrome.Body == "example.com");
        var sentence = Who("Chrome", "Google Chrome", "Title", "Body", "not a host");
        check("A last line that is not a host stays in the body", sentence.AppKey == "windows:Chrome" && sentence.Body == "Body\nnot a host");
        var other = Who("QQ", "QQ", "Title", "Body", "example.com");
        check("Only browsers have their last line taken as a site", other.Body == "Body\nexample.com" && other.AppKey == "windows:QQ");
        check("Host names are recognized strictly", WindowsNoticeApps.Host("Web.WhatsApp.com") == "web.whatsapp.com" && WindowsNoticeApps.Host("127.0.0.1") is null
            && WindowsNoticeApps.Host("localhost") is null && WindowsNoticeApps.Host("a..b") is null && WindowsNoticeApps.Host("http://a.com") is null
            && WindowsNoticeApps.Host("例子.com") is null && WindowsNoticeApps.Host("-a.com") is null);
    }

    private static void Categories(Action<string, bool> check)
    {
        check("Chat, mail and browsers stay messages", WindowsNoticeApps.Category("QQ") == NoticeCategory.Message
            && WindowsNoticeApps.Category("MSTeams_8wekyb3d8bbwe!MSTeams") == NoticeCategory.Message && WindowsNoticeApps.Category("Chrome") == NoticeCategory.Message
            && WindowsNoticeApps.Category("Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows") == NoticeCategory.Message);
        check("Coding agents' notices are tasks", WindowsNoticeApps.Category("Claude_pzs8sxrjxfjjc!Claude") == NoticeCategory.Task
            && WindowsNoticeApps.Category("OpenAI.Codex_2p2nqsd0c76g0!App") == NoticeCategory.Task);
        check("System and utility notices are information", WindowsNoticeApps.Category("Windows.SystemToast.BthQuickPair") == NoticeCategory.Info
            && WindowsNoticeApps.Category("Windows.Defender.SecurityCenter") == NoticeCategory.Info && WindowsNoticeApps.Category("com.nvidia.nvapp") == NoticeCategory.Info
            && WindowsNoticeApps.Category("party.mihomo.app") == NoticeCategory.Info && WindowsNoticeApps.Category("Microsoft.ScreenSketch_8wekyb3d8bbwe!App") == NoticeCategory.Info);
        check("The category is part of the identity", Who("com.nvidia.nvapp", "NVIDIA App", "Driver").Category == NoticeCategory.Info);
        check("Other assistants' notices are tasks", WindowsNoticeApps.Category("com.google.gemini.windows") == NoticeCategory.Task
            && WindowsNoticeApps.Category("OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT") == NoticeCategory.Task && WindowsNoticeApps.Category("Anysphere.Cursor") == NoticeCategory.Task);
        check("Security, phone setup and vendor utilities are information", WindowsNoticeApps.Category("Microsoft.SecHealthUI_8wekyb3d8bbwe!SecHealthUI") == NoticeCategory.Info
            && WindowsNoticeApps.Category("Microsoft.YourPhone_8wekyb3d8bbwe!YourPhoneSetup") == NoticeCategory.Info
            && WindowsNoticeApps.Category("Microsoft.YourPhone_8wekyb3d8bbwe!App") == NoticeCategory.Message && WindowsNoticeApps.Category("Lenovo.PcManager.MainWnd") == NoticeCategory.Info);
        Calls(check);
        Sites(check);
    }

    private static void Calls(Action<string, bool> check)
    {
        check("QQ's voice and video call invitations are calls", Who("QQ", "QQ", "Alice", "邀请你进行语音通话").Call && Who("QQ", "QQ", "Alice", "视频通话邀请").Call);
        check("Teams and WhatsApp calls are calls", Who("MSTeams_8wekyb3d8bbwe!MSTeams", "Microsoft Teams", "Bob", "is calling you").Call
            && Who("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", "WhatsApp", "Incoming voice call", "Carol").Call
            && Who("com.squirrel.Discord.Discord", null, "Dan", "Incoming Call").Call);
        check("A phone call relayed by Phone Link is a call", Who("Microsoft.YourPhone_8wekyb3d8bbwe!App", "手机连接", "来电", "138 0000 0000").Call);
        check("A missed call or a chat about calls is no ringing call", !Who("QQ", "QQ", "Alice", "未接来电").Call && !Who("QQ", "QQ", "Alice", "我们晚点打电话").Call
            && !Who("MSTeams_8wekyb3d8bbwe!MSTeams", "Microsoft Teams", "Bob", "Missed call").Call && !Who("QQ", "QQ", "Alice", "recalling yours").Call);
        check("Chat about power or later calls is no call", !Who("QQ", "QQ", "家人群", "来电了").Call && !Who("QQ", "QQ", "Alice", "来电话了吗").Call
            && !Who("QQ", "QQ", "来电显示怎么设置", "x").Call && !Who("MSTeams_8wekyb3d8bbwe!MSTeams", "Microsoft Teams", "Bob", "I'll be calling you later").Call
            && !Who("MSTeams_8wekyb3d8bbwe!MSTeams", "Microsoft Teams", "Bob", "Missed incoming call").Call);
        check("A call said only further down the body is no call", !Who("QQ", "QQ", "Alice", "看看这个\n邀请你进行语音通话").Call);
        check("A caller line after 来电 is a call", Who("Microsoft.YourPhone_8wekyb3d8bbwe!App", "手机连接", "来电：妈妈", "").Call && Who("QQ", "QQ", "Alice", "语音来电").Call);
        check("Only chat and phone apps have calls", !Who("Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows", "Outlook", "Incoming call", "x").Call
            && !Who("Chrome", "Google Chrome", "Incoming call", "x").Call && !Who("Claude_pzs8sxrjxfjjc!Claude", "Claude", "Task", "is calling you").Call);

        var hub = new NoticeHub();
        var call = hub.Receive(WinInput("c1", "windows:QQ", "Alice", "邀请你进行语音通话") with { Call = true }, WinT0);
        check("A call is high priority and keeps its mark", call.Notice!.Priority == NoticePriority.High && call.Notice.Call && call.Present);
        var ended = hub.Receive(WinInput("c2", "windows:QQ", "Alice", "通话已结束") with { Replaces = "c1" }, WinT0.AddSeconds(20));
        check("A call that ends loses its mark", ended.Receipt == NoticeReceipt.Updated && !ended.Notice!.Call && ended.Notice.Priority == NoticePriority.Normal);
        var low = new NoticeHub(key => key == "windows:QQ" ? new NoticeAppRule { Key = key, Priority = NoticePriority.Low } : null);
        check("A call from an application set to low priority stays low", low.Receive(WinInput("c", "windows:QQ", "A") with { Call = true }, WinT0).Notice!.Priority == NoticePriority.Low);
        var copy = System.Text.Json.JsonSerializer.Deserialize<Notice>(System.Text.Json.JsonSerializer.Serialize(new Notice { Call = true }));
        check("The call mark is saved with the notice", copy!.Call);
    }

    private static void Sites(Action<string, bool> check)
    {
        check("A site is read from a browser notice's key or a web app's id", WindowsNoticeApps.SiteOf("windows:Chrome|mail.google.com", "Chrome") == "mail.google.com"
            && WindowsNoticeApps.SiteOf("windows:Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!https://web.whatsapp.com/", "Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!https://web.whatsapp.com/") == "web.whatsapp.com"
            && WindowsNoticeApps.SiteOf("windows:QQ", "QQ") is null && WindowsNoticeApps.SiteOf("windows:QQ|not a host", "QQ") is null);
        check("Mail and calendar sites are known", WindowsNoticeApps.IsMailSite("mail.google.com") && WindowsNoticeApps.IsMailSite("mail.qq.com")
            && WindowsNoticeApps.IsCalendarSite("calendar.google.com") && !WindowsNoticeApps.IsMailSite("google.com") && !WindowsNoticeApps.IsMailSite("evilmail.google.com.example"));
        check("More sites have names", WindowsNoticeApps.SiteName("wx.qq.com") == "微信" && WindowsNoticeApps.SiteName("mail.qq.com") == "QQ 邮箱"
            && WindowsNoticeApps.SiteName("chat.deepseek.com") == "DeepSeek" && WindowsNoticeApps.SiteName("qq.com") == "qq.com");
    }

    private static NoticeInput WinInput(string instance, string app, string title, string body = "b") => new()
    {
        Origin = NoticeOrigin.Windows, AppKey = app, AppName = app, InstanceKey = instance, Title = title, Body = body
    };

    private static void Replacements(Action<string, bool> check)
    {
        var (added, removed) = NoticeReplacements.Pair(
            [WinInput("new1", "windows:QQ", "Alice", "3 条新消息"), WinInput("new2", "windows:QQ", "Bob")],
            [new("old1", "windows:QQ", "Alice"), new("old2", "windows:Teams", "Bob")]);
        check("A notice arriving as one from the same app with the same title leaves is its replacement",
            added[0].Replaces == "old1" && added[1].Replaces is null && removed.SequenceEqual(["old2"]));
        var (untitled, kept) = NoticeReplacements.Pair([WinInput("n", "windows:A", "")], [new("o", "windows:A", "")]);
        check("Notices without a title are never paired", untitled[0].Replaces is null && kept.SequenceEqual(["o"]));
        var (twice, _) = NoticeReplacements.Pair([WinInput("n1", "windows:A", "T"), WinInput("n2", "windows:A", "T")], [new("o", "windows:A", "T")]);
        check("One leaving notice is replaced at most once", twice[0].Replaces == "o" && twice[1].Replaces is null);

        var hub = new NoticeHub();
        var first = hub.Receive(WinInput("old", "windows:QQ", "Alice", "hi"), WinT0);
        var merged = hub.Receive(WinInput("new", "windows:QQ", "Alice", "2 条新消息") with { Replaces = "old" }, WinT0.AddSeconds(3));
        check("The hub updates the notice a replacement takes the place of", merged.Receipt == NoticeReceipt.Updated && merged.Notice == first.Notice
            && hub.Notices.Count == 1 && first.Notice!.InstanceKey == "new" && first.Notice.Body == "2 条新消息" && merged.Present);
        var foreign = hub.Receive(WinInput("x", "windows:Other", "Alice") with { Replaces = "new" }, WinT0.AddSeconds(4));
        check("A replacement never takes over another application's notice", foreign.Receipt == NoticeReceipt.Accepted && hub.Notices.Count == 2);
        var orphan = hub.Receive(WinInput("y", "windows:QQ", "Zed") with { Replaces = "deleted" }, WinT0.AddSeconds(5));
        check("A replacement for a notice no longer kept is added", orphan.Receipt == NoticeReceipt.Accepted && hub.Notices.Count == 3);
    }
}
