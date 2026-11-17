using BrimDeck.Core;
using BrimDeck.Core.Notices;

// Bringing WeChat forward from its taskbar flash notice (ChatRestore): the decision, the fixed address and its allow-list, and
// the switch, all with fake registry and launcher; nothing is started.
internal static class NoticeChatRestoreTests
{
    private sealed class FakeRegistry(bool registered, bool throws = false) : IProtocolRegistry
    {
        public readonly List<string> Asked = [];
        public bool IsRegistered(string scheme) { Asked.Add(scheme); if (throws) throw new InvalidOperationException(); return registered; }
    }

    private sealed class FakeLauncher(bool result = true, bool throws = false) : IProtocolLauncher
    {
        public readonly List<string> Launched = [];
        public bool Launch(string uri) { Launched.Add(uri); if (throws) throw new InvalidOperationException(); return result; }
    }

    private static Notice Flash(string id = "wechat", NoticeOrigin origin = NoticeOrigin.Windows) => new() { AppKey = "flash:" + id, Origin = origin, InstanceKey = $"flash:{id}:1" };

    public static void Run(Action<string, bool> check)
    {
        Allow(check); Decisions(check); Calls(check); Setting(check);
    }

    private static void Allow(Action<string, bool> check)
    {
        check("Restore: exactly weixin:// is allowed", ChatRestore.IsAllowed("weixin://") && ChatRestore.WeChatUri == "weixin://");
        string[] refused =
        [
            "weixin:///", "weixin://x", "weixin://?a=1", "weixin://#a", "weixin:", "weixin:/", "weixin", "WEIXIN://", "Weixin://", "weixin://WeChat",
            " weixin://", "weixin:// ", "weixin://\n", "\tweixin://", "weixin://\0", "weixin://%20", "weixin://@evil.exe", "weixin://\"x\"",
            "wechat://", "weixins://", "http://weixin://", "https://weixin.qq.com", "file:///C:/Windows/notepad.exe", @"C:\Windows\notepad.exe",
            "ms-settings:", "javascript:alert(1)", "", " ", "x", new string('a', 100_000), "weixin://" + new string('a', 100_000)
        ];
        check($"Restore: {refused.Length} other strings are refused", refused.Length >= 25 && refused.All(uri => !ChatRestore.IsAllowed(uri)));
        check("Restore: null is refused", !ChatRestore.IsAllowed(null));
        // A row naming any other address never restores, whatever else is true.
        bool any = refused.Append(null!).Any(uri => ChatRestore.Decide(Flash(), true, true, [new("wechat", "微信", "WeChat", ["Weixin.exe"], true, uri)]).Kind == ChatRestoreKind.Restore);
        check("Restore: a row with any other address never restores", !any);
    }

    private static void Decisions(Action<string, bool> check)
    {
        var decision = ChatRestore.Decide(Flash(), true, true);
        check("Restore: the WeChat flash notice with the switch on and the scheme registered restores with the constant", decision is { Kind: ChatRestoreKind.Restore, Uri: "weixin://" });
        check("Restore: the WeChat row is the only one with an address", ChatFlashApps.All.Where(app => app.RestoreUri is not null).Select(app => app.Id).SequenceEqual(["wechat"]));
        check("Restore: the switch off is the ordinary click", ChatRestore.Decide(Flash(), false, true) == ChatRestoreDecision.Fallback);
        check("Restore: a scheme that is not registered is the ordinary click", ChatRestore.Decide(Flash(), true, false) == ChatRestoreDecision.Fallback);
        check("Restore: another chat application's flash notice is the ordinary click", ChatRestore.Decide(Flash("qq"), true, true).Kind == ChatRestoreKind.Fallback
            && ChatRestore.Decide(Flash("feishu"), true, true).Kind == ChatRestoreKind.Fallback && ChatRestore.Decide(Flash("dingtalk"), true, true).Kind == ChatRestoreKind.Fallback);
        check("Restore: an unknown flash id is the ordinary click", ChatRestore.Decide(Flash("nobody"), true, true).Kind == ChatRestoreKind.Fallback
            && ChatRestore.Decide(Flash("WECHAT"), true, true).Kind == ChatRestoreKind.Fallback && ChatRestore.Decide(Flash("wechat "), true, true).Kind == ChatRestoreKind.Fallback);
        check("Restore: a row that is not enabled is the ordinary click", ChatRestore.Decide(Flash(), true, true, [new("wechat", "微信", "WeChat", ["Weixin.exe"], false, ChatRestore.WeChatUri)]).Kind == ChatRestoreKind.Fallback);
        check("Restore: a notice that is not a flash notice is the ordinary click",
            ChatRestore.Decide(new Notice { AppKey = "windows:Weixin", Origin = NoticeOrigin.Windows }, true, true).Kind == ChatRestoreKind.Fallback
            && ChatRestore.Decide(new Notice { AppKey = "flashwechat", Origin = NoticeOrigin.Windows }, true, true).Kind == ChatRestoreKind.Fallback
            && ChatRestore.Decide(Flash(origin: NoticeOrigin.Wsl), true, true).Kind == ChatRestoreKind.Fallback
            && ChatRestore.Decide(Flash(origin: NoticeOrigin.Cli), true, true).Kind == ChatRestoreKind.Fallback
            && ChatRestore.Decide(null, true, true).Kind == ChatRestoreKind.Fallback);
        check("Restore: the notice's own text never reaches the address", ChatRestore.Decide(new Notice { AppKey = "flash:wechat", Origin = NoticeOrigin.Windows, Title = "weixin://evil", Body = "?x=1", AppId = "a b" }, true, true).Uri == "weixin://");
        var input = new ChatFlashFilter(_ => "Weixin.exe", _ => false, () => DateTimeOffset.UnixEpoch).OnFlash(1);
        check("Restore: the notice the flash source really makes qualifies", input is not null
            && ChatRestore.Decide(new Notice { AppKey = input.AppKey, Origin = input.Origin }, true, true).Kind == ChatRestoreKind.Restore);
    }

    private static void Calls(Action<string, bool> check)
    {
        var registry = new FakeRegistry(true);
        var launcher = new FakeLauncher();
        check("Restore: it launches once, with the constant, and reports it", ChatRestore.TryRestore(Flash(), true, registry, launcher)
            && launcher.Launched.SequenceEqual(["weixin://"]) && registry.Asked.SequenceEqual(["weixin"]));

        registry = new FakeRegistry(true); launcher = new FakeLauncher();
        check("Restore: the switch off reads nothing and launches nothing", !ChatRestore.TryRestore(Flash(), false, registry, launcher) && launcher.Launched.Count == 0 && registry.Asked.Count == 0);
        registry = new FakeRegistry(true); launcher = new FakeLauncher();
        check("Restore: a notice that is not a flash notice reads nothing and launches nothing",
            !ChatRestore.TryRestore(new Notice { AppKey = "windows:x", Origin = NoticeOrigin.Windows }, true, registry, launcher) && launcher.Launched.Count == 0 && registry.Asked.Count == 0);
        registry = new FakeRegistry(true); launcher = new FakeLauncher();
        check("Restore: another chat application reads nothing and launches nothing", !ChatRestore.TryRestore(Flash("qq"), true, registry, launcher) && launcher.Launched.Count == 0 && registry.Asked.Count == 0);
        registry = new FakeRegistry(false); launcher = new FakeLauncher();
        check("Restore: a scheme that is not registered launches nothing", !ChatRestore.TryRestore(Flash(), true, registry, launcher) && launcher.Launched.Count == 0 && registry.Asked.Count == 1);
        registry = new FakeRegistry(true, throws: true); launcher = new FakeLauncher();
        check("Restore: a registry that cannot be read launches nothing and does not throw", !ChatRestore.TryRestore(Flash(), true, registry, launcher) && launcher.Launched.Count == 0);
        registry = new FakeRegistry(true); launcher = new FakeLauncher(result: false);
        check("Restore: a launch that failed is the ordinary click, and was tried once", !ChatRestore.TryRestore(Flash(), true, registry, launcher) && launcher.Launched.Count == 1);
        registry = new FakeRegistry(true); launcher = new FakeLauncher(throws: true);
        check("Restore: a launcher that throws is the ordinary click", !ChatRestore.TryRestore(Flash(), true, registry, launcher) && launcher.Launched.Count == 1);
    }

    private static void Setting(Action<string, bool> check)
    {
        check("Restore: the switch is off by default", !new DeckSettings().NoticeChatFlashRestore);
        string folder = Path.Combine(Path.GetTempPath(), "BrimDeck-RestoreTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(folder);
            store.Save(new DeckSettings { NoticesEnabled = true, NoticeChatFlash = true, NoticeChatFlashRestore = true });
            var loaded = store.Load();
            check("Restore: the switch survives saving, loading and copying", loaded.NoticeChatFlashRestore && loaded.Copy().NoticeChatFlashRestore);
            File.WriteAllText(store.FilePath, $$"""{"AppVersion":"{{ApplicationVersion.Parse(ApplicationVersion.Current).ToString(3)}}","NoticesEnabled":true,"NoticeChatFlash":true}""");
            var old = store.Load();
            check("Restore: an older settings file reads with the switch off and the flash source as it was", !old.NoticeChatFlashRestore && old.NoticeChatFlash);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
