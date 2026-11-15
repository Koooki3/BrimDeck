using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

// The chat application privacy default (ChatApps): which applications count as chat, the precedence of an application's own
// rule, the user's choice of the global setting and the default, and how settings saved before it behave.
internal static class ChatAppsTests
{
    public static void Run(Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try { Matcher(check); Precedence(check); Display(check); Stored(check); }
        finally { Loc.Use(language); }
    }

    private static void Matcher(Action<string, bool> check)
    {
        (string Key, string Name)[] chat =
        [
            ("windows:Weixin", "微信"), ("windows:WeChat", "WeChat"), ("windows:Tencent.WeChat", ""), ("windows:QQ", "QQ"), ("windows:x", "QQ NT"), ("windows:x", "腾讯QQ"),
            ("windows:x", "TIM"), ("windows:WXWork", "企业微信"), ("windows:x", "WeCom"), ("windows:x", "飞书"), ("windows:x", "Feishu"), ("windows:x", "Lark"),
            ("windows:ali.dingtalk.release", ""), ("windows:x", "钉钉"), ("windows:TelegramMessengerLLP.TelegramDesktop", ""), ("windows:x", "Telegram Desktop"),
            ("windows:com.squirrel.Discord.Discord", "Discord"), ("windows:5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", "WhatsApp"), ("windows:x", "WhatsApp Beta"),
            ("windows:Signal", "Signal"), ("windows:com.squirrel.slack.slack", "Slack"), ("windows:x", "slack"), ("windows:NAVER.LINE", "LINE"), ("windows:x", "LINE"),
            ("windows:x", "KakaoTalk"), ("windows:x", "카카오톡"), ("windows:x", "Messenger"), ("windows:x", "Viber"), ("windows:x", "Zalo"), ("windows:x", "Element"),
            ("windows:x", "Mattermost"), ("wsl:Ubuntu:telegram-desktop", "telegram-desktop")
        ];
        foreach (var (key, name) in chat) check($"Chat app: {name} {key}", ChatApps.IsChat(key, name));
        (string Key, string Name)[] other =
        [
            ("windows:x", "QQ音乐"), ("windows:QQMusic", "QQ Music"), ("windows:x", "Command Line"), ("windows:x", "Pipeline"), ("windows:x", "Online Services"),
            ("windows:x", "Signal Strength"), ("windows:x", "Slackware"), ("windows:x", "Microsoft Teams"), ("windows:MSTeams_8wekyb3d8bbwe!MSTeams", "Microsoft Teams"),
            ("windows:x", "Skype"), ("windows:x", "Zoom"), ("windows:x", "腾讯会议"), ("windows:x", "ChatGPT"), ("windows:x", "Claude"), ("windows:x", "Outlook"),
            ("windows:x", "Gmail"), ("windows:Google.Chrome", "Google Chrome"), ("windows:x", "Windows Update"), ("windows:x", "Steam"),
            ("windows:x", "Elementary"), ("windows:x", "Larkspur"), ("windows:x", "Timer"), ("cli:claude", "claude"), ("", "")
        ];
        foreach (var (key, name) in other) check($"Not a chat app: {name} {key}", !ChatApps.IsChat(key, name));
        check("The chat flash notices of every flash application count as chat applications", ChatFlashApps.All.All(app => ChatApps.IsChat("flash:" + app.Id, app.NameZh) && ChatApps.IsChat("flash:" + app.Id, app.NameEn)));
    }

    private static void Precedence(Action<string, bool> check)
    {
        foreach (var global in Enum.GetValues<NoticePrivacy>())
            foreach (var follow in new[] { false, true })
                foreach (NoticePrivacy? rule in new NoticePrivacy?[] { null, NoticePrivacy.Full, NoticePrivacy.TitleOnly, NoticePrivacy.AppOnly })
                    foreach (var chat in new[] { false, true })
                    {
                        var expected = rule ?? (chat && !follow ? NoticePrivacy.AppOnly : global);
                        check($"Precedence global {global} follow {follow} rule {rule} chat {chat} is {expected}", ChatApps.EffectivePrivacy(global, follow, rule, chat) == expected);
                    }
        check("An application's own rule beats the chat default", ChatApps.EffectivePrivacy(NoticePrivacy.Full, false, NoticePrivacy.Full, true) == NoticePrivacy.Full);
        check("A chat application defaults to the application only", ChatApps.EffectivePrivacy(NoticePrivacy.Full, false, null, true) == NoticePrivacy.AppOnly);
        check("Choosing the global setting makes chat applications follow it", ChatApps.EffectivePrivacy(NoticePrivacy.Full, true, null, true) == NoticePrivacy.Full);
        check("Other applications follow the global setting", ChatApps.EffectivePrivacy(NoticePrivacy.TitleOnly, false, null, false) == NoticePrivacy.TitleOnly);
    }

    private static Notice Chat(string key, string name) => new() { AppKey = key, AppName = name, Title = "Alice", Body = "see you at 8" };

    private static void Display(Action<string, bool> check)
    {
        var notice = Chat("windows:Weixin", "微信");
        check("A chat notice shown by application only says there is a new message", NoticeText.Presented(notice, NoticePrivacy.AppOnly) == ("微信", "有新消息"));
        check("The sender and text do not appear", NoticeText.Presented(notice, NoticePrivacy.AppOnly) is var (title, summary) && !title.Contains("Alice") && !summary.Contains("see you"));
        check("Another application shown by application only keeps the plain wording", NoticeText.Presented(Chat("windows:x", "Outlook"), NoticePrivacy.AppOnly) == ("Outlook", "新通知"));
        check("The full privacy still shows sender and text", NoticeText.Presented(notice, NoticePrivacy.Full) == ("Alice", "see you at 8"));
        Loc.Use(Loc.English);
        check("The chat wording is translated", NoticeText.Presented(notice, NoticePrivacy.AppOnly).Summary == "New message");
        Loc.Use(Loc.Chinese);
        var settings = new DeckSettings();
        check("By default a chat notice is shown by application only and another one in full",
            settings.NoticePrivacyFor("windows:Weixin", "微信") == NoticePrivacy.AppOnly && settings.NoticePrivacyFor("windows:x", "Outlook") == NoticePrivacy.Full);
        settings.NoticeApps.Add(new() { Key = "windows:Weixin", Privacy = NoticePrivacy.Full });
        check("An application rule makes a chat application show in full", settings.NoticePrivacyFor("windows:Weixin", "微信") == NoticePrivacy.Full
            && settings.NoticePrivacyFor("windows:QQ", "QQ") == NoticePrivacy.AppOnly);
        settings.NoticeChatAppsFollowGlobal = true;
        check("The switch makes the other chat applications follow the global setting", settings.NoticePrivacyFor("windows:QQ", "QQ") == NoticePrivacy.Full);
    }

    private static DeckSettings Read(string json) { var s = JsonSerializer.Deserialize<DeckSettings>(json)!; s.Normalize(); return s; }

    private static void Stored(Action<string, bool> check)
    {
        var oldDefault = Read("{\"NoticesEnabled\":true}");
        check("A file saved before the setting, with the default global value, gets the chat default", !oldDefault.ChatAppsFollowGlobal && oldDefault.NoticeChatAppsFollowGlobal is null
            && oldDefault.NoticePrivacyFor("windows:Weixin", "微信") == NoticePrivacy.AppOnly);
        var oldChanged = Read("{\"NoticePrivacy\":1}");
        check("A file saved before the setting, with the global value changed, keeps it for chat applications", oldChanged.ChatAppsFollowGlobal
            && oldChanged.NoticePrivacyFor("windows:Weixin", "微信") == NoticePrivacy.TitleOnly);
        var oldRules = Read("{\"NoticeApps\":[{\"Key\":\"windows:Weixin\",\"Name\":\"微信\",\"Privacy\":0},{\"Key\":\"windows:QQ\",\"Name\":\"QQ\"}]}");
        check("A file with application rules keeps them: a rule decides, an application without a privacy rule gets the chat default",
            oldRules.NoticePrivacyFor("windows:Weixin", "微信") == NoticePrivacy.Full && oldRules.NoticePrivacyFor("windows:QQ", "QQ") == NoticePrivacy.AppOnly);
        var oldBoth = Read("{\"NoticePrivacy\":2,\"NoticeApps\":[{\"Key\":\"windows:Weixin\",\"Privacy\":1}]}");
        check("A file with both keeps the rule and the global value", oldBoth.NoticePrivacyFor("windows:Weixin", "微信") == NoticePrivacy.TitleOnly
            && oldBoth.NoticePrivacyFor("windows:QQ", "QQ") == NoticePrivacy.AppOnly && oldBoth.NoticePrivacyFor("windows:x", "Outlook") == NoticePrivacy.AppOnly);
        var explicitFull = Read("{\"NoticePrivacy\":0,\"NoticeChatAppsFollowGlobal\":true}");
        check("An explicit choice of Full for chat applications is kept", explicitFull.NoticePrivacyFor("windows:Weixin", "微信") == NoticePrivacy.Full);
        var explicitOff = Read("{\"NoticePrivacy\":1,\"NoticeChatAppsFollowGlobal\":false}");
        check("Turning the switch off wins over a changed global value", explicitOff.NoticePrivacyFor("windows:Weixin", "微信") == NoticePrivacy.AppOnly
            && explicitOff.NoticePrivacyFor("windows:x", "Outlook") == NoticePrivacy.TitleOnly);
        string root = Path.Combine(Path.GetTempPath(), "brimdeck-chatapps-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(root, "1.2.0");
            store.Save(new DeckSettings { NoticePrivacy = NoticePrivacy.Full, NoticeChatAppsFollowGlobal = true });
            var back = store.Load();
            check("The choice survives saving", back.NoticeChatAppsFollowGlobal == true && back.NoticePrivacyFor("windows:Weixin", "微信") == NoticePrivacy.Full);
            store.Save(new DeckSettings());
            check("An untouched setting stays unset", store.Load().NoticeChatAppsFollowGlobal is null);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
