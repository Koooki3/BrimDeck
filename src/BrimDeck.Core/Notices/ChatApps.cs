using System.Text.RegularExpressions;

namespace BrimDeck.Core.Notices;

// Chat and messaging applications, whose notices carry a person's name and the words they wrote. For these the notice shows
// only the application and that there is a new message unless the user chose otherwise (NoticeChatAppsFollowGlobal).
public static class ChatApps
{
    // One row per application; extend the table by adding rows. Words are matched, case-insensitively, against the
    // application's name and its key (the Windows application id, "windows:" prefix removed):
    //   plain     a part of the name or id ("wechat" is in "WeChat" and "Tencent.WeChat")
    //   =word     a whole word only ("=lark" is "Lark" but not "Larkspur")
    //   ==exact   the whole name or the whole id, for names too short or too common to match as a part ("==qq" is not "QQ Music")
    // Left out on purpose: Teams and Skype (their notices mix chat with meetings and calls under one id), Zoom, Tencent Meeting
    // and other meeting apps, mail apps, and "chat" as a word (ChatGPT and other assistants are not conversations with people).
    private static readonly (string App, string[] Words)[] Table =
    [
        ("WeChat", ["wechat", "weixin", "微信"]),
        ("QQ", ["==qq", "==qq nt", "==腾讯qq", "==tim", "tencent.qq"]),
        ("WeCom", ["wecom", "wxwork", "企业微信"]),
        ("Feishu / Lark", ["feishu", "飞书", "=lark", "bytedance.lark"]),
        ("DingTalk", ["dingtalk", "钉钉"]),
        ("Telegram", ["telegram"]),
        ("Discord", ["discord"]),
        ("WhatsApp", ["whatsapp"]),
        ("Signal", ["==signal", "org.whispersystems.signal"]),
        ("Slack", ["==slack", "com.squirrel.slack", "slacktechnologies"]),
        ("LINE", ["==line", "naver.line", "linecorp"]),
        ("KakaoTalk", ["kakaotalk", "카카오톡"]),
        ("Messenger", ["==messenger", "facebook.messenger"]),
        ("Viber", ["viber"]),
        ("Zalo", ["==zalo", "vng.zalo"]),
        ("Element / Matrix", ["==element", "im.riot", "mattermost", "rocket.chat"]),
    ];

    public static bool IsChat(string appKey, string appName)
    {
        string id = Id(appKey ?? "").ToLowerInvariant();
        string name = (appName ?? "").Trim().ToLowerInvariant();
        foreach (var (_, words) in Table)
            foreach (var word in words)
            {
                if (word.StartsWith("==", StringComparison.Ordinal)) { if (name == word[2..] || id == word[2..]) return true; }
                else if (word[0] == '=') { if (Words(name).Contains(word[1..]) || Words(id).Contains(word[1..])) return true; }
                else if (name.Contains(word, StringComparison.Ordinal) || id.Contains(word, StringComparison.Ordinal)) return true;
            }
        return false;
    }

    // The privacy a notice is shown with. Precedence: the application's own rule; then the user's own choice of the global
    // setting (chatAppsFollowGlobal: the global setting was chosen, or the switch is on); then, for a chat application, the
    // application and nothing else; then the global setting.
    public static NoticePrivacy EffectivePrivacy(NoticePrivacy global, bool chatAppsFollowGlobal, NoticePrivacy? appRule, bool isChatApp)
        => appRule ?? (isChatApp && !chatAppsFollowGlobal ? NoticePrivacy.AppOnly : global);

    private static string Id(string appKey)
    {
        int colon = appKey.IndexOf(':');
        return colon > 0 && appKey[..colon] is "windows" or "wsl" or "cli" ? appKey[(colon + 1)..] : appKey;
    }

    private static HashSet<string> Words(string text) => [.. Regex.Split(text, @"[^\p{L}\p{N}]+").Where(word => word.Length > 0)];
}
