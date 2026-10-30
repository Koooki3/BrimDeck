using BrimDeck.Core.Notices;

// Windows notices, the local pipe and the path checks (audit fix group "win").
internal static class AuditWinTests
{
    internal static void Run(Action<string, bool> check)
    {
        NoticeList(check);
        UnknownWait(check);
        AppIds(check);
        Intake(check);
        Paths(check);
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private static NoticeInput Input(uint id, string title = "", int minute = 0) => new()
    {
        Origin = NoticeOrigin.Windows, AppKey = "windows:App", AppName = "App", InstanceKey = "windows:App:" + id,
        Title = title.Length > 0 ? title : "t" + id, CreatedAt = T0.AddMinutes(minute == 0 ? id : minute)
    };

    private static WindowsNoticeList.Changes Read(WindowsNoticeList list, params uint[] ids) => list.Compare(ids, id => Input(id));

    private static void NoticeList(Action<string, bool> check)
    {
        var list = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        var first = Read(list, 1, 2);
        check("The first Windows reading is a baseline", first.Arrived.Count == 0 && first.Missed.Count == 0 && first.Removed.Count == 0
            && list.Newest == 2 && !list.Confirmed && list.Baseline == WindowsNoticeBaseline.None);
        var next = Read(list, 1, 2, 3);
        check("A new id after the baseline arrives and confirms the newest id", next.Arrived.Count == 1 && next.Arrived[0].InstanceKey == "windows:App:3"
            && next.Missed.Count == 0 && list.Confirmed && list.Newest == 3);
        var gone = Read(list, 1, 3);
        check("A listed notice that disappears is removed by its key", gone.Removed.SequenceEqual(["windows:App:2"]) && gone.Arrived.Count == 0);
        var replaced = list.Compare([1, 4], id => Input(id, id == 4 ? "t3" : ""));
        check("A notice that took the place of a removed one with the same title names it", replaced.Arrived.Count == 1
            && replaced.Arrived[0].Replaces == "windows:App:3" && replaced.Removed.Count == 0);

        // core-M5: what arrived while asleep is handed over as missed, oldest first, and nothing is shown.
        list.ExpectBaseline(WindowsNoticeBaseline.Resume);
        var resumed = list.Compare([1, 4, 6, 5], id => Input(id));
        check("After waking, notices that arrived meanwhile are missed, not dropped", resumed.Arrived.Count == 0
            && resumed.Missed.Select(i => i.InstanceKey).SequenceEqual(["windows:App:5", "windows:App:6"]) && list.Baseline == WindowsNoticeBaseline.None);
        var after = Read(list, 1, 4, 5, 6, 7);
        check("After the waking reading, new notices arrive as usual", after.Arrived.Count == 1 && after.Missed.Count == 0);

        var fresh = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        fresh.ExpectBaseline(WindowsNoticeBaseline.Resume);
        var unread = Read(fresh, 1, 2);
        check("Waking before any reading is a plain baseline", unread.Arrived.Count == 0 && unread.Missed.Count == 0);
        fresh.ExpectBaseline(WindowsNoticeBaseline.Start);
        fresh.ExpectBaseline(WindowsNoticeBaseline.Resume);
        check("A later waking does not weaken a pending baseline", fresh.Baseline == WindowsNoticeBaseline.Start
            && Read(fresh, 1, 2, 3) is { Arrived.Count: 0, Missed.Count: 0 });

        fresh.Reset();
        var empty = Read(fresh);
        check("Revoked access forgets the list without reporting removals", empty.Removed.Count == 0 && fresh.Newest == 0 && !fresh.Confirmed);

        var calls = 0;
        var odd = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        Read(odd, 1);
        var skipped = odd.Compare([1, 2, 2, 3], id => { calls++; return id == 2 ? null : Input(id, minute: 10 - (int)id); });
        var again = odd.Compare([1, 2, 3], id => { calls++; return Input(id); });
        check("An unreadable notice is skipped once, repeated ids count once, and arrivals come oldest first",
            skipped.Arrived.Select(i => i.InstanceKey).SequenceEqual(["windows:App:3"]) && calls == 2 && again.Arrived.Count == 0);
        var order = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        Read(order);
        var sorted = order.Compare([8, 9], id => Input(id, minute: id == 8 ? 30 : 20));
        check("Arrivals are handed over oldest first", sorted.Arrived.Select(i => i.InstanceKey).SequenceEqual(["windows:App:9", "windows:App:8"]));
    }

    // perf-6: while the newest id is unconfirmed, readings that find nothing come less often, up to the full interval.
    private static void UnknownWait(Action<string, bool> check)
    {
        var list = new WindowsNoticeList(TimeSpan.FromSeconds(15));
        Read(list, 1);
        bool startsShort = list.UnknownWait == WindowsNoticeList.FirstUnknownWait;
        var waits = new List<double>();
        for (int i = 0; i < 4; i++) { Read(list, 1); waits.Add(list.UnknownWait.TotalSeconds); }
        check("Unconfirmed readings back off from 3 s to the full interval", startsShort && waits.SequenceEqual([6d, 12d, 15d, 15d]));
        list.ExpectBaseline(WindowsNoticeBaseline.Resume);
        check("Waking checks often again", list.UnknownWait == WindowsNoticeList.FirstUnknownWait);
        Read(list, 1);
        Read(list, 1, 2);
        check("A notice arriving confirms the newest id", list.Confirmed);
    }

    private static void AppIds(Action<string, bool> check)
    {
        check("Application ids the shell may open are accepted",
            WindowsNoticeList.IsAppId("Microsoft.WindowsAlarms_8wekyb3d8bbwe!App")
            && WindowsNoticeList.IsAppId(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Vendor\app.exe")
            && WindowsNoticeList.IsAppId(new string('a', 512)));
        check("Application ids that read as switches, paths or shell names are refused",
            new[] { "-x", "/x", @"\x", ".x", "a..b", "a,b", "a:b", "shell:x", "a\"b", "'a'", new string('a', 513), "", "a/b", "a\tb" }.All(id => !WindowsNoticeList.IsAppId(id)));
    }

    private static void Intake(Action<string, bool> check)
    {
        var gate = new IntakeGate(3, TimeSpan.FromSeconds(10));
        bool filled = gate.Admit(0) && gate.Admit(1) && gate.Admit(2) && !gate.Admit(3) && !gate.Admit(9_999);
        check("The pipe's gate lets the limit through within the window, then again once it slides", filled && gate.Admit(10_000) && !gate.Admit(10_000));
        var other = new IntakeGate(1, TimeSpan.FromSeconds(10));
        check("Gates count apart", other.Admit(5) && !other.Admit(6) && new IntakeGate(1, TimeSpan.FromSeconds(10)).Admit(6));
    }

    // life-T3: the checks that keep icon paths local refuse network, device and climbing paths.
    private static void Paths(Action<string, bool> check)
    {
        check("Local Windows icon paths are accepted", NoticeIntake.IsLocalWindowsPath(@"C:\icons\a.png") && NoticeIntake.IsLocalWindowsPath("D:/icons/a.png"));
        string[] windows =
        [
            @"\\server\share\a.png", @"\\?\C:\a.png", @"\\.\pipe\x", @"\\?\UNC\server\share\a.png", "//server/share/a.png", @"C:a.png", @"C:\a\..\b.png",
            @"C:\a.png:stream", @"C:\a\\b.png", @"C:\a//b.png", @"C:\a*.png", @"C:\a?.png", "C:\\a\".png", @"C:\a<.png", @"C:\a>.png", @"C:\a|.png", "1:\\a.png", "C:\\"
        ];
        check("Network, device, relative and climbing Windows paths are refused", windows.All(path => !NoticeIntake.IsLocalWindowsPath(path)));
        check("Icon references refuse UNC file URLs, web addresses and over-long paths",
            NoticeIntake.IconRef("file://server/share/a.png", NoticeOrigin.Cli, null) is null
            && NoticeIntake.IconRef("https://example.com/a.png", NoticeOrigin.Cli, null) is null
            && NoticeIntake.IconRef(@"C:\" + new string('a', 1022), NoticeOrigin.Cli, null) is null
            && NoticeIntake.IconRef(@"C:\" + new string('a', 1021), NoticeOrigin.Cli, null) is not null
            && NoticeIntake.IconRef("C:\\a\u0001.png", NoticeOrigin.Cli, null) is null);
        check("Linux icon paths and names are accepted", NoticeIntake.IsLinuxIcon("/usr/share/icons/a.png") && NoticeIntake.IsLinuxIcon("firefox"));
        check("Linux icons that climb, reach Windows or another root are refused",
            new[] { "../x", "/a/../b", "..", "//x", "a/b", "C:/x", @"a\b", "a*", "a?b", "a|b" }.All(icon => !NoticeIntake.IsLinuxIcon(icon))
            && NoticeIntake.IconRef("../x", NoticeOrigin.Wsl, "Ubuntu") is null);
    }
}
