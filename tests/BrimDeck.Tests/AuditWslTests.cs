using BrimDeck.Protocol;
using BrimDeck.Protocol.DBus;

// The audit fixes of the WSL agent, the D-Bus parser and brim-notify (group "wsl").
internal static class AuditWslTests
{
    public static void Run(Action<string, bool> check)
    {
        ValueBudget(check);
        Sessions(check);
        MonitorWake(check);
        LongNotices(check);
        WindowsProgramSearch(check);
        CmdQuoting(check);
        ExitCodes(check);
    }

    // sec-M3: a message of many small values is refused instead of turned into many objects.
    private static void ValueBudget(Action<string, bool> check)
    {
        DBusMessage Shorts(int count) => DBusMessage.Signal("/x", "x.y", "Z", "aq", Enumerable.Range(0, count).Select(i => (object)(ushort)i).ToList());
        check("A message of 70,000 values is refused", Throws<InvalidDataException>(() => DBusMessage.Decode(Shorts(70_000).Encode())));
        check("A message within the value budget still decodes", DBusMessage.Decode(Shorts(1000).Encode()).Body is [List<object> { Count: 1000 }]);
        check("The budget counts per message, not per reader's lifetime",
            DBusMessage.Decode(Shorts(40_000).Encode()).Body is [List<object> { Count: 40_000 }] && DBusMessage.Decode(Shorts(40_000).Encode()).Body is [List<object> { Count: 40_000 }]);
        check("The largest message held is a few MiB, not 16", DBusConnection.MaxMessage <= 2 * 1024 * 1024);
    }

    private static DBusMessage Notify(uint replaces = 0, string sender = ":1.7", uint serial = 4) => new()
    {
        Type = DBusMessageType.MethodCall, Serial = serial, Sender = sender, Destination = FreedesktopNotifications.Name, Path = FreedesktopNotifications.Path,
        Interface = FreedesktopNotifications.Interface, Member = "Notify", Signature = FreedesktopNotifications.NotifySignature,
        Body = ["app", replaces, "", "Title", "Body", new List<object>(), new Dictionary<object, object>(), -1]
    };

    // core-H2: a new run of the agent hands out id 1 again, under a session of its own.
    private static void Sessions(Action<string, bool> check)
    {
        var earlier = new NotificationServer("1", session: "server-aaaa").Handle(Notify()).ToBrimDeck.Single();
        var later = new NotificationServer("1", session: "server-bbbb").Handle(Notify()).ToBrimDeck.Single();
        check("Two runs of the service give the same first id under different sessions",
            earlier.NativeId == later.NativeId && earlier.Session == "server-aaaa" && later.Session == "server-bbbb");
        var server = new NotificationServer("1", session: "server-cccc");
        uint id = (uint)server.Handle(Notify()).Bus[0].Body[0];
        var close = new DBusMessage { Type = DBusMessageType.MethodCall, Serial = 9, Sender = ":1.7", Interface = FreedesktopNotifications.Interface, Member = "CloseNotification", Signature = "u", Body = [id] };
        check("A close carries the same session as the notice it closes", server.Handle(close).ToBrimDeck.Single() is { Type: NoticeTypes.Close, Session: "server-cccc" });
        check("Without a session the service keeps the earlier one", new NotificationServer("1").Handle(Notify()).ToBrimDeck.Single().Session == "server");
        var monitor = new NotificationMonitor { Owner = ":1.3", Session = "monitor-dddd" };
        check("A watched service's notices carry the session the agent gave it", monitor.Observe(Notify(replaces: 5), DateTimeOffset.UnixEpoch).Single().Session == "monitor-dddd");
    }

    // perf-12: the monitor says when it next needs to pass on a held call, and nothing while none is held.
    private static void MonitorWake(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var monitor = new NotificationMonitor { Owner = ":1.3" };
        check("A monitor holding nothing needs no wake-up", monitor.NextDue is null);
        monitor.Observe(Notify(), now);
        check("A held call is due after the monitor's patience", monitor.NextDue == now + NotificationMonitor.Patience);
        var reply = new DBusMessage { Type = DBusMessageType.MethodReturn, Sender = ":1.3", Destination = ":1.7", ReplySerial = 4, Signature = "u", Body = [12u] };
        check("The service's reply passes the call on and leaves nothing due", monitor.Observe(reply, now).Single().NativeId == "12" && monitor.NextDue is null);
        monitor.Observe(Notify(serial: 5), now);
        check("An unanswered call is passed on when due, with no id", monitor.Flush(now + NotificationMonitor.Patience) is [{ NativeId: null }] && monitor.NextDue is null);
    }

    // sec-L3: a desktop notice too long for a frame is cut, not lost.
    private static void LongNotices(Action<string, bool> check)
    {
        var actions = Enumerable.Range(0, 40).SelectMany(i => new object[] { "key" + i, new string('l', 500) }).ToList();
        var body = new object[] { new string('a', 5000), 0u, new string('/', 5000), new string('t', 100_000), new string('中', 100_000), actions, new Dictionary<object, object>(), -1 };
        var notice = FreedesktopNotifications.ToNotice(body, 1);
        check("A long desktop notice is cut to the protocol's limits",
            notice.Title!.Length == NoticeProtocol.MaxTitle && notice.Body!.Length == NoticeProtocol.MaxBody && notice.AppName!.Length == NoticeProtocol.MaxField
            && notice.Icon!.Length == 1024 && notice.Actions!.Count == NoticeProtocol.MaxActions && notice.Actions.All(a => a.Label.Length == 64));
        check("A cut desktop notice fits in one frame", NoticeProtocol.Serialize(notice).Length <= NoticeProtocol.MaxFrame);
    }

    // sec-L1: a bare name is found in PATH only, never in the current folder or BrimDeck's.
    private static void WindowsProgramSearch(Action<string, bool> check)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\tools\git.exe", @"C:\scripts\build.bat", @"C:\work\make.exe", @"C:\work\run.cmd" };
        const string path = @"C:\tools;relative\dir;""C:\scripts"";;", pathExt = ".COM;.EXE;.BAT;.CMD";
        // Found with the extension as PATHEXT spells it; Windows paths compare without case.
        string? Find(string name) => WindowsPrograms.Find(name, path, pathExt, files.Contains)?.ToLowerInvariant();
        check("A bare name is found in PATH with PATHEXT", Find("git") == @"c:\tools\git.exe" && Find("build") == @"c:\scripts\build.bat");
        check("A name with its extension is found as it is", Find("build.bat") == @"c:\scripts\build.bat" && Find("git.exe") == @"c:\tools\git.exe");
        check("A program only in some other folder is not found by its bare name", Find("make") is null && Find("run") is null);
        check("A relative PATH entry is skipped", WindowsPrograms.Find("x", @"relative", pathExt, name => name.EndsWith(@"relative\x.exe", StringComparison.OrdinalIgnoreCase)) is null);
        check("A name with a folder is taken as written", Find(@"C:\work\make") == @"c:\work\make.exe" && Find(@"C:\work\run.cmd") == @"c:\work\run.cmd");
        check("Wildcards and empty names find nothing", Find("") is null && Find("g*t") is null);
        check("Executables start directly; batch files and scripts go through cmd",
            WindowsPrograms.RunsDirectly(@"C:\tools\git.exe") && WindowsPrograms.RunsDirectly(@"C:\x\a.COM") && !WindowsPrograms.RunsDirectly(@"C:\scripts\build.bat"));
        check("cmd's own commands are known", WindowsPrograms.IsBuiltin("echo") && WindowsPrograms.IsBuiltin("DIR") && !WindowsPrograms.IsBuiltin("git"));
    }

    // sec-L1: what goes through cmd keeps cmd's special characters literal (escaping in full: AuditWsl2Tests, C-M1).
    private static void CmdQuoting(Action<string, bool> check)
    {
        check("An argument with cmd's special characters is quoted, keeping them literal",
            WindowsPrograms.CmdArguments(@"C:\scripts\build.bat", ["a & calc", "x|y", "(z)^"], false) == "/e:on /d /v:off /s /c \"\"C:\\scripts\\build.bat\" \"a & calc\" \"x|y\" \"(z)^\"\"");
        check("Line breaks are refused", WindowsPrograms.CmdArguments(@"C:\b.bat", ["a\nb"], false) is null);
        check("A builtin's name stays bare", WindowsPrograms.CmdArguments("echo", ["hi there"], true) == "/e:on /d /v:off /s /c \"echo \"hi there\"\"");
    }

    private static void ExitCodes(Action<string, bool> check)
    {
        var error = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            check("brim-notify's exit codes: unreachable, refused, delivered, filtered",
                NotifyCli.Report(null, "x") == NotifyCli.Unreachable && NotifyCli.Report(NoticeProtocol.Reply(NoticeResults.Rejected, "bad-time"), "x") == NotifyCli.Refused
                && NotifyCli.Report(NoticeProtocol.Reply(NoticeResults.Accepted), "x") == 0 && NotifyCli.Report(NoticeProtocol.Reply(NoticeResults.Filtered), "x") == 0);
        }
        finally { Console.SetError(error); }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
