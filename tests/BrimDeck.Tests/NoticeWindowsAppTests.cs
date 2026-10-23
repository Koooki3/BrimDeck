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
        check("An application without any name keeps its id", Who("BiliBiliPC", "", "t").AppName == "BiliBiliPC");
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
