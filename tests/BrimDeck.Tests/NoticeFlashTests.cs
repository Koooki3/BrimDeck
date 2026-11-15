using BrimDeck.Core;
using BrimDeck.Core.Notices;

// The taskbar flash source's decisions (ChatFlashFilter) with a fake clock and fake process lookups, and its setting.
internal static class NoticeFlashTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    public static void Run(Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try { Decisions(check); Coalescing(check); Table(check); Setting(check); }
        finally { Loc.Use(language); }
    }

    private sealed class World
    {
        public DateTimeOffset Now = T0;
        public readonly Dictionary<nint, string?> Images = new() { [10] = "Weixin.exe", [11] = "WeChat.exe", [20] = "notepad.exe", [30] = "QQ.exe" };
        public readonly HashSet<nint> Front = [];
        public ChatFlashFilter Filter(IReadOnlyList<ChatFlashApp>? apps = null) => new(handle => Images.GetValueOrDefault(handle), Front.Contains, () => Now, null, apps);
    }

    private static void Decisions(Action<string, bool> check)
    {
        var world = new World();
        var notice = world.Filter().OnFlash(10);
        check("Flash: a flashing Weixin.exe window is a new-message notice naming WeChat", notice is { Title: "微信", Body: "有新消息", AppName: "微信", Origin: NoticeOrigin.Windows, Category: NoticeCategory.Message });
        check("Flash: the notice carries no sender, text or icon", notice!.Picture is null && notice.IconRef is null && notice.Actions.Count == 0 && notice.Capabilities == NoticeCapabilities.None);
        check("Flash: WeChat.exe is WeChat too, and the file name is matched without case", world.Filter().OnFlash(11)?.AppKey == "flash:wechat"
            && new ChatFlashFilter(_ => @"C:\Program Files\Tencent\WEIXIN.EXE", _ => false, () => T0).OnFlash(1)?.AppKey == "flash:wechat");
        check("Flash: an unknown program gives nothing", world.Filter().OnFlash(20) is null);
        check("Flash: a window that cannot be looked up gives nothing", world.Filter().OnFlash(99) is null && world.Filter().OnFlash(0) is null);
        check("Flash: a row that is not enabled gives nothing (QQ is listed but off)", world.Filter().OnFlash(30) is null);
        var enabledQq = world.Filter([new ChatFlashApp("qq", "QQ", "QQ", ["QQ.exe"], true)]);
        check("Flash: another application is added by one row", enabledQq.OnFlash(30) is { AppKey: "flash:qq", Title: "QQ" });
        Loc.Use(Loc.English);
        var english = world.Filter().OnFlash(11);
        Loc.Use(Loc.Chinese);
        check("Flash: the words follow the language", english is { Title: "WeChat", Body: "New message" });
        world.Front.Add(10);
        check("Flash: nothing while the user is already in the application", world.Filter().OnFlash(10) is null);
    }

    private static void Coalescing(Action<string, bool> check)
    {
        var world = new World();
        var filter = world.Filter();
        var first = filter.OnFlash(10)!;
        world.Now = T0.AddSeconds(5);
        var second = filter.OnFlash(10)!;
        world.Now = T0.AddMinutes(2.9);
        var third = filter.OnFlash(11)!;
        check("Flash: repeated flashes within the window are the same notice (the hub updates it)", first.InstanceKey == second.InstanceKey && second.InstanceKey == third.InstanceKey && first.AppKey == second.AppKey);
        world.Now = T0.AddMinutes(3).AddSeconds(1);
        var later = filter.OnFlash(10)!;
        check("Flash: a flash after the window is a new notice", later.InstanceKey != first.InstanceKey);
        world.Now = T0.AddMinutes(3).AddSeconds(30);
        check("Flash: the window counts from the first flash of a notice, so steady flashing alerts again", filter.OnFlash(10)!.InstanceKey == later.InstanceKey);
        world.Now = T0.AddMinutes(-1);
        check("Flash: a clock that went back starts a new notice", filter.OnFlash(10)!.InstanceKey != later.InstanceKey);

        // The hub with the same inputs: a burst is listed once and shown once; the next burst is listed beside it.
        var hub = new NoticeHub();
        var arrived = hub.Receive(first, T0);
        var merged = hub.Receive(second, T0.AddSeconds(5));
        check("Flash: the hub lists a burst once and shows it once", arrived is { Receipt: NoticeReceipt.Accepted, Present: true } && merged is { Receipt: NoticeReceipt.Updated, Present: false } && hub.Notices.Count == 1);
        check("Flash: the next burst is shown and listed beside it", hub.Receive(later, T0.AddMinutes(4)).Present && hub.Notices.Count == 2);

        var two = new World();
        two.Images[40] = "Lark.exe";
        var both = two.Filter([new ChatFlashApp("wechat", "微信", "WeChat", ["Weixin.exe"], true), new ChatFlashApp("feishu", "飞书", "Feishu", ["Lark.exe"], true)]);
        check("Flash: two applications flashing are two notices", both.OnFlash(10)!.InstanceKey != both.OnFlash(40)!.InstanceKey);

        var front = new World();
        front.Front.Add(10);
        var ignoring = front.Filter();
        ignoring.OnFlash(10);
        front.Front.Clear();
        front.Now = T0.AddSeconds(10);
        check("Flash: a flash ignored in front does not use up the window", ignoring.OnFlash(10) is not null);
    }

    private static void Table(Action<string, bool> check)
    {
        check("Flash: only WeChat is enabled", ChatFlashApps.All.Where(app => app.Enabled).Select(app => app.Id).SequenceEqual(["wechat"]));
        check("Flash: ids are unique and every row names executables", ChatFlashApps.All.Select(app => app.Id).Distinct().Count() == ChatFlashApps.All.Count
            && ChatFlashApps.All.All(app => app.Executables.Count > 0 && app.Executables.All(e => e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))));
        check("Flash: lookups by name and by path", ChatFlashApps.Find(@"D:\x\Weixin.exe")?.Id == "wechat" && ChatFlashApps.Find("") is null && ChatFlashApps.Find(null) is null && ChatFlashApps.Find("weixin") is null);
    }

    private static void Setting(Action<string, bool> check)
    {
        check("Flash: the switch is off by default", !new DeckSettings().NoticeChatFlash);
        string folder = Path.Combine(Path.GetTempPath(), "BrimDeck-FlashTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(folder);
            store.Save(new DeckSettings { NoticesEnabled = true, NoticeChatFlash = true });
            var loaded = store.Load();
            check("Flash: the switch survives saving, loading and copying", loaded.NoticeChatFlash && loaded.Copy().NoticeChatFlash);
            File.WriteAllText(store.FilePath, $$"""{"AppVersion":"{{ApplicationVersion.Parse(ApplicationVersion.Current).ToString(3)}}","NoticesEnabled":true}""");
            check("Flash: an older settings file reads with the switch off", !store.Load().NoticeChatFlash);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
