using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

// Replaying a toast's own activation when the user opens an application from a notice: which schemes are opened, which
// activation types are replayed, and the switch (off by default, absent from old settings files).
internal static class ToastActivationTests
{
    private const string App = "TelegramMessengerLLP.TelegramDesktop";

    internal static void Run(Action<string, bool> check)
    {
        Schemes(check);
        Plans(check);
        Parsing(check);
        Ids(check);
        Setting(check);
    }

    private static void Schemes(Action<string, bool> check)
    {
        (string Text, bool Opens)[] table =
        [
            ("https://example.com/a?b=c", true), ("http://example.com", true), ("HTTPS://EXAMPLE.COM/x", true), ("  https://example.com/x  ", true),
            ("tg://resolve?domain=durov", true), ("discord://-/channels/1/2", true), ("TG://resolve?domain=x", true), ("mailto:a@example.com", true),
            ("whatsapp://send?phone=1", true), ("https://example.com/%E4%B8%AD%20x", true),
            ("file:///C:/Windows/System32/calc.exe", false), ("FILE:///C:/x", false), ("File://host/share/x.exe", false),
            ("javascript:alert(1)", false), ("JavaScript:alert(1)", false), ("vbscript:msgbox(1)", false), ("data:text/html,<b>x</b>", false),
            ("ms-msdt:/id PCWDiagnostic /skip force", false), ("ms-settings:privacy", false), ("MS-CXH:x", false), ("ms-officecmd:{}", false),
            ("search-ms:query=x", false), ("shell:startup", false), ("microsoft-edge:https://example.com", false), ("ftp://example.com/x", false),
            ("", false), ("   ", false), ("not a uri", false), ("C:\\Windows\\System32\\calc.exe", false), ("\\\\host\\share\\x.exe", false),
            ("/just/a/path", false), ("tg:", false), (":x", false), ("1http://x", false), ("t://x", false), ("https:/example.com", false),
            ("https://", false), ("tg://a b", false), ("tg://a\tb", false), ("tg://a\nb", false), ("tg://a\"b", false), ("tg://a|b", false),
            ("tg://a^b", false), ("tg://a\\b", false), ("tg://a<b>", false), ("tg://x\u0000y", false), ("scheme_with_underscore://x", false),
            ("tg://" + new string('a', 3000), false), ("tg://" + new string('a', 2000), true)
        ];
        foreach (var (text, opens) in table)
            check($"Toast link {Short(text)} is {(opens ? "opened" : "refused")}", (ToastActivation.ProtocolUri(text) is not null) == opens);
        check("A link is returned trimmed and otherwise as the toast wrote it", ToastActivation.ProtocolUri("  tg://resolve?domain=x  ") == "tg://resolve?domain=x");
        check("No link text is refused", ToastActivation.ProtocolUri(null) is null);
    }

    private static string Short(string text) => text.Length > 40 ? $"({text.Length} chars)" : "'" + text.Replace("\n", "\\n").Replace("\t", "\\t").Replace("\0", "\\0") + "'";

    private static void Plans(Action<string, bool> check)
    {
        static ToastLaunch T(string launch, string type = "") => new(launch, type);
        check("Off: nothing is replayed", ToastActivation.Decide(false, T("tg://x", "protocol"), App, true).Kind == ToastActivationKind.Fallback);
        check("No toast read: start the application only", ToastActivation.Decide(true, null, App, true).Kind == ToastActivationKind.Fallback);
        check("An empty launch string: start the application only", ToastActivation.Decide(true, T("  ", "protocol"), App, true).Kind == ToastActivationKind.Fallback
            && ToastActivation.Decide(true, T(""), App, true).Kind == ToastActivationKind.Fallback);
        var protocol = ToastActivation.Decide(true, T("tg://resolve?domain=x", "protocol"), App, false);
        check("A protocol toast opens its link, packaged or not", protocol is { Kind: ToastActivationKind.Protocol, Uri: "tg://resolve?domain=x" }
            && ToastActivation.Decide(true, T("https://a.example/x", "Protocol"), App, true).Kind == ToastActivationKind.Protocol);
        check("A protocol toast with a refused scheme starts the application only", ToastActivation.Decide(true, T("file:///C:/x.exe", "protocol"), App, true).Kind == ToastActivationKind.Fallback
            && ToastActivation.Decide(true, T("ms-msdt:/id x", "protocol"), App, true).Kind == ToastActivationKind.Fallback);
        var package = ToastActivation.Decide(true, T("action=open&peer=1&msg=2"), App, true);
        check("A foreground toast of a packaged application launches it with the launch string", package is { Kind: ToastActivationKind.PackageLaunch, Arguments: "action=open&peer=1&msg=2" });
        check("An explicit foreground type is the same", ToastActivation.Decide(true, T("x=1", "foreground"), App, true).Kind == ToastActivationKind.PackageLaunch
            && ToastActivation.Decide(true, T("x=1", " FOREGROUND "), App, true).Kind == ToastActivationKind.PackageLaunch);
        check("A desktop application is started as before (COM activation is not used)", ToastActivation.Decide(true, T("x=1"), App, false).Kind == ToastActivationKind.Fallback);
        check("Background and system activations are not replayed", ToastActivation.Decide(true, T("x=1", "background"), App, true).Kind == ToastActivationKind.Fallback
            && ToastActivation.Decide(true, T("dismiss", "system"), App, true).Kind == ToastActivationKind.Fallback && ToastActivation.Decide(true, T("x", "bogus"), App, true).Kind == ToastActivationKind.Fallback);
        check("An application id that could carry switches is not launched with arguments", ToastActivation.Decide(true, T("x=1"), "-evil", true).Kind == ToastActivationKind.Fallback
            && ToastActivation.Decide(true, T("x=1"), "a/b", true).Kind == ToastActivationKind.Fallback && ToastActivation.Decide(true, T("x=1"), "", true).Kind == ToastActivationKind.Fallback);
        check("A very long launch string is not passed on", ToastActivation.Decide(true, T(new string('a', ToastActivation.MaxLaunch + 1)), App, true).Kind == ToastActivationKind.Fallback
            && ToastActivation.Decide(true, T(new string('a', ToastActivation.MaxLaunch)), App, true).Kind == ToastActivationKind.PackageLaunch);
        check("A launch string with a control character is not passed on", ToastActivation.Decide(true, T("a\u0000b"), App, true).Kind == ToastActivationKind.Fallback
            && ToastActivation.Decide(true, T("a\r\nb"), App, true).Kind == ToastActivationKind.Fallback);
        check("Arguments are passed as one string, whatever they contain", ToastActivation.Decide(true, T("a b \"c\" & d"), App, true).Arguments == "a b \"c\" & d");
    }

    private static void Parsing(Action<string, bool> check)
    {
        var toast = ToastActivation.ParseToast("""<toast launch="action=open&amp;peer=7" activationType="protocol"><visual><binding template="ToastGeneric"><text>hi</text></binding></visual></toast>""");
        check("The toast's launch and activation type are read", toast is { Launch: "action=open&peer=7", ActivationType: "protocol" });
        check("Missing attributes read as empty", ToastActivation.ParseToast("<toast><visual/></toast>") is { Launch: "", ActivationType: "" });
        check("Only a launch on the root element counts", ToastActivation.ParseToast("""<toast><actions><action launch="x" arguments="y"/></actions></toast>""") is { Launch: "" });
        check("Text that is not a toast is refused", ToastActivation.ParseToast("<notoast launch=\"x\"/>") is null && ToastActivation.ParseToast("not xml") is null
            && ToastActivation.ParseToast("") is null && ToastActivation.ParseToast(null) is null);
        check("A document type declaration is refused", ToastActivation.ParseToast("""<!DOCTYPE toast [<!ENTITY x "y">]><toast launch="&x;"/>""") is null);
    }

    private static void Ids(Action<string, bool> check)
    {
        check("The notification id is read from the notice's key", ToastActivation.NotificationId($"windows:{App}:42:638000000000000000", App) == 42);
        check("A key of another application or another shape gives none", ToastActivation.NotificationId($"windows:Other:42:1", App) is null
            && ToastActivation.NotificationId("wsl:x:42", App) is null && ToastActivation.NotificationId($"windows:{App}:x:1", App) is null
            && ToastActivation.NotificationId($"windows:{App}:-1:1", App) is null && ToastActivation.NotificationId($"windows:{App}:1", App) is null);
    }

    private static void Setting(Action<string, bool> check)
    {
        check("Replaying the launch string is off by default", !new DeckSettings().NoticeOpenReplay);
        var on = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { NoticeOpenReplay = true }));
        check("The switch survives a round trip", on is { NoticeOpenReplay: true });
        var old = JsonSerializer.Deserialize<DeckSettings>("""{"NoticeHistory":true,"NoticeDuration":6}""");
        check("An old settings file without the switch reads as off", old is { NoticeOpenReplay: false });
        var reread = SettingsMigrations.Read("""{"Width":890}""");
        reread.Normalize();
        check("An old file through the migrations reads as off", !reread.NoticeOpenReplay);
    }
}
