using BrimDeck.Core.Notices;

// Which existing window opening a notice's application brings forward (NoticeAppWindows), with a fake window system.
internal static class NoticeFocusTests
{
    private const int Self = 100;

    private sealed class Fake : IAppWindowSystem
    {
        public readonly List<nint> Order = [];
        public readonly Dictionary<nint, AppWindowInfo> Windows = [];
        public readonly Dictionary<int, string> Paths = [];
        public readonly List<(nint Handle, bool Restore)> Raised = [];
        public bool Accept = true;
        // Runs when a window is read for the second time, to model it changing between the list and the action.
        public Action<nint>? OnSecondLook;
        private readonly HashSet<nint> _seen = [];

        public Fake Add(nint handle, int pid, string? path, Func<AppWindowInfo, AppWindowInfo>? change = null)
        {
            var info = new AppWindowInfo(pid, true, false, false, false, false, true, false, false);
            Order.Add(handle); Windows[handle] = change?.Invoke(info) ?? info;
            if (path is not null) Paths[pid] = path;
            return this;
        }

        public IReadOnlyList<nint> TopLevelWindows() => Order.ToList();
        public AppWindowInfo? Inspect(nint handle)
        {
            if (!_seen.Add(handle)) OnSecondLook?.Invoke(handle);
            return Windows.TryGetValue(handle, out var info) ? info : null;
        }
        public string? ImagePath(int pid) => Paths.GetValueOrDefault(pid);
        public bool Foreground(nint handle, bool restore) { Raised.Add((handle, restore)); return Accept; }
    }

    private static readonly AppProcessHint WeChat = NoticeAppWindows.Hint("Weixin", null);
    private const string WeChatPath = @"C:\Program Files\Tencent\Weixin\Weixin.exe";

    public static void Run(Action<string, bool> check)
    {
        Hints(check);
        Selection(check);
        Stale(check);
    }

    private static void Hints(Action<string, bool> check)
    {
        check("focus: a known application id is told by its program's file names",
            WeChat.Matches(WeChatPath) && WeChat.Matches(@"D:\x\WeChat.exe") && !WeChat.Matches(@"C:\x\Weixin2.exe"));
        check("focus: a registered desktop program is told by the file in its id",
            NoticeAppWindows.Hint(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\Tencent\QQ\qq.exe", null).Matches(@"C:\Apps\QQ.EXE"));
        check("focus: an Office id is told by its program", NoticeAppWindows.Hint("Microsoft.Office.OUTLOOK.EXE.16", null).Matches(@"C:\O\OUTLOOK.EXE"));
        check("focus: a packaged app is told by its install folder, not by its entry name",
            NoticeAppWindows.Hint("Fam_8we!App", @"C:\Program Files\WindowsApps\Fam_1.0_x64__8we").Matches(@"C:\Program Files\WindowsApps\Fam_1.0_x64__8we\App.exe")
            && !NoticeAppWindows.Hint("Fam_8we!App", @"C:\Program Files\WindowsApps\Fam_1.0_x64__8we").Matches(@"C:\Program Files\WindowsApps\Fam_1.0_x64__8we2\App.exe")
            && !NoticeAppWindows.Hint("Fam_8we!App", null).Matches(@"C:\x\App.exe"));
        check("focus: ids that name no program give an empty hint",
            NoticeAppWindows.Hint("NotifyIconGeneratedAumid_123", null).IsEmpty && NoticeAppWindows.Hint("Some.Dotted.Id", null).IsEmpty && NoticeAppWindows.Hint("", null).IsEmpty);
        check("focus: an unreadable process path never matches", !WeChat.Matches(null) && !WeChat.Matches(""));
    }

    private static void Selection(Action<string, bool> check)
    {
        var none = new Fake().Add(1, 5, @"C:\Windows\notepad.exe");
        check("focus: no window of the application gives false and raises nothing", !NoticeAppWindows.TryFocus(WeChat, none, Self) && none.Raised.Count == 0);

        var empty = new Fake().Add(1, 5, WeChatPath);
        check("focus: an empty hint raises nothing", !NoticeAppWindows.TryFocus(new AppProcessHint([], null), empty, Self) && empty.Raised.Count == 0);

        var one = new Fake().Add(1, 5, @"C:\Windows\notepad.exe").Add(2, 6, WeChatPath);
        check("focus: the application's window is raised, not restored when it is not minimised",
            NoticeAppWindows.TryFocus(WeChat, one, Self) && one.Raised is [(2, false)]);

        var minimised = new Fake().Add(3, 6, WeChatPath, i => i with { Minimized = true });
        check("focus: a minimised window is restored when it is raised", NoticeAppWindows.TryFocus(WeChat, minimised, Self) && minimised.Raised is [(3, true)]);

        var several = new Fake().Add(1, 7, @"C:\x\other.exe").Add(2, 6, WeChatPath).Add(3, 8, @"D:\more\WeChat.exe");
        check("focus: with several processes the topmost matching window wins", NoticeAppWindows.TryFocus(WeChat, several, Self) && several.Raised is [(2, false)]);

        var refused = new Fake { Accept = false }.Add(2, 6, WeChatPath).Add(3, 8, WeChatPath);
        check("focus: a refusal is reported at once, without trying more windows", !NoticeAppWindows.TryFocus(WeChat, refused, Self) && refused.Raised.Count == 1);

        var skipped = new Fake()
            .Add(1, 6, WeChatPath, i => i with { Visible = false })
            .Add(2, 6, WeChatPath, i => i with { Cloaked = true })
            .Add(3, 6, WeChatPath, i => i with { ToolWindow = true })
            .Add(4, 6, WeChatPath, i => i with { HasOwner = true })
            .Add(5, 6, WeChatPath, i => i with { HasTitle = false })
            .Add(6, 6, WeChatPath, i => i with { IsShell = true })
            .Add(7, Self, WeChatPath);
        check("focus: hidden, cloaked, tool, owned, untitled, shell and BrimDeck's own windows are never raised",
            !NoticeAppWindows.TryFocus(WeChat, skipped, Self) && skipped.Raised.Count == 0);

        var listed = new Fake().Add(1, 6, WeChatPath, i => i with { ToolWindow = true, AppWindowStyle = true });
        check("focus: a tool-styled window that asks for a taskbar button counts", NoticeAppWindows.TryFocus(WeChat, listed, Self) && listed.Raised is [(1, false)]);

        var behind = new Fake().Add(1, 6, WeChatPath, i => i with { Cloaked = true }).Add(2, 6, WeChatPath);
        check("focus: an unusable window is passed over for a usable one of the same process", NoticeAppWindows.TryFocus(WeChat, behind, Self) && behind.Raised is [(2, false)]);
    }

    private static void Stale(Action<string, bool> check)
    {
        var closed = new Fake().Add(1, 6, WeChatPath);
        closed.OnSecondLook = h => closed.Windows.Remove(h);
        check("focus: a window closed since the list is not acted on", !NoticeAppWindows.TryFocus(WeChat, closed, Self) && closed.Raised.Count == 0);

        var reused = new Fake().Add(1, 6, WeChatPath);
        reused.OnSecondLook = h => reused.Windows[h] = reused.Windows[h] with { Pid = 9 };
        check("focus: a handle that now belongs to another process is not acted on", !NoticeAppWindows.TryFocus(WeChat, reused, Self) && reused.Raised.Count == 0);

        var pid = new Fake().Add(1, 6, WeChatPath);
        pid.OnSecondLook = _ => pid.Paths[6] = @"C:\Windows\notepad.exe";
        check("focus: a process id taken by another program is not acted on", !NoticeAppWindows.TryFocus(WeChat, pid, Self) && pid.Raised.Count == 0);

        var hidden = new Fake().Add(1, 6, WeChatPath);
        hidden.OnSecondLook = h => hidden.Windows[h] = hidden.Windows[h] with { Visible = false };
        check("focus: a window hidden since the list is not forced to show", !NoticeAppWindows.TryFocus(WeChat, hidden, Self) && hidden.Raised.Count == 0);
    }
}
