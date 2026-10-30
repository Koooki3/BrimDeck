using BrimDeck.Core.Notices;
using BrimDeck.Protocol;
using BrimDeck.Protocol.DBus;

// The second round of the WSL review (review-wsl-cli W2, W6, W10, W11, W13, I1) and review-batch1 items 2 and 6.
internal static class NoticeWsl2OpsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public static async Task Run(Action<string, bool> check)
    {
        IdleProcesses(check);
        IdleTiming(check);
        BusDiscovery(check);
        NotServing(check);
        BodyMarkup(check);
        StackTags(check);
        Transient(check);
        IconLinks(check);
        ExpiredForBrimDeck(check);
        await StatusNotConfirmed(check);
    }

    // W2: the user's own processes keep the agent; plumbing, WSL's init and relays, and the agent's own children do not.
    private static void IdleProcesses(Action<string, bool> check)
    {
        const uint User = 1000;
        const int Self = 500;
        List<LinuxProcess> plumbing =
        [
            new(1, 0, 0, "systemd"), new(2, 1, 0, "init-systemd(Ub"), new(9, 2, 0, "init"), new(40, 2, 0, "SessionLeader"), new(41, 40, 0, "Relay(500)"),
            new(100, 1, User, "systemd"), new(101, 100, User, "(sd-pam)"), new(102, 100, User, "dbus-daemon"), new(103, 100, User, "dbus-broker-lau"),
            new(104, 103, User, "dbus-broker"), new(Self, 41, User, "brimdeck-agent"), new(501, Self, User, "stat"), new(502, 501, User, "sh"),
            // Another user's and root's programs are theirs.
            new(600, 1, 0, "cron"), new(601, 1, 1001, "bash")
        ];
        check("W2: plumbing, relays and the agent's own children are idle", !WslIdle.Busy(plumbing, Self, User));
        check("W2: a terminal of the user's is use", WslIdle.Busy([.. plumbing, new(700, 41, User, "bash")], Self, User));
        check("W2: a background job left running is use too", WslIdle.Busy([.. plumbing, new(701, 1, User, "python3")], Self, User));
        check("W2: brim-notify running is use", WslIdle.Busy([.. plumbing, new(702, 700, User, "brimdeck-agent")], Self, User));
        check("W2: a parent loop does not hang the check", WslIdle.Busy([new(5, 6, User, "a"), new(6, 5, User, "b")], Self, User));

        var parsed = WslIdle.Parse(42, "Name:\tdbus-daemon\nUmask:\t0022\nState:\tS (sleeping)\nPid:\t42\nPPid:\t7\nUid:\t1000\t1001\t1000\t1000\n");
        check("W2: /proc status gives name, parent and the real user id", parsed == new LinuxProcess(42, 7, 1000, "dbus-daemon"));
        check("W2: a status without them is skipped", WslIdle.Parse(43, "Name:\tx\n") is null);
    }

    // W2: the agent ends only after the whole limit of idle checks; the bridge waits for the distribution to stop first.
    private static void IdleTiming(Action<string, bool> check)
    {
        var clock = new IdleClock(TimeSpan.FromMinutes(5));
        check("W2: not at the first idle check", !clock.Due(false, T0));
        check("W2: not before the limit", !clock.Due(false, T0.AddMinutes(4)));
        check("W2: use starts the count over", !clock.Due(true, T0.AddMinutes(4.5)) && !clock.Due(false, T0.AddMinutes(5)) && !clock.Due(false, T0.AddMinutes(9.9)));
        check("W2: once idle for the whole limit", clock.Due(false, T0.AddMinutes(10)));
        check("W2: a limit of zero never ends it", !new IdleClock(TimeSpan.Zero).Due(false, T0) && !new IdleClock(TimeSpan.Zero).Due(false, T0.AddDays(1)));
        check("W2: the idle code is none of the agent's others", !new[] { 0, NotifyCli.Usage, NotifyCli.Refused, NotifyCli.Unreachable, 127 }.Contains(WslLink.IdleExitCode));
        check("W2: no new start while the distribution stays up", !WslLink.StartAfterIdle(true, TimeSpan.FromMinutes(1)) && !WslLink.StartAfterIdle(true, WslLink.IdleRestart - TimeSpan.FromSeconds(1)));
        check("W2: nor while it is stopped", !WslLink.StartAfterIdle(false, TimeSpan.FromHours(2)));
        check("W2: at most once per IdleRestart, which is far longer than the agent's own limit", WslLink.StartAfterIdle(true, WslLink.IdleRestart) && WslLink.IdleRestart >= 4 * WslLink.IdleLimit);
    }

    // W6: the session bus is looked for where libdbus and GIO look, in their order.
    private static void BusDiscovery(Action<string, bool> check)
    {
        const uint User = 1000;
        BusLookup Look(Dictionary<string, string> variables, Dictionary<string, uint> sockets, Dictionary<string, string>? files = null) => new(
            name => variables.GetValueOrDefault(name), sockets.ContainsKey, path => sockets.TryGetValue(path, out var owner) ? owner : uint.MaxValue,
            folder => (files ?? []).Keys.Where(path => path.StartsWith(folder, StringComparison.Ordinal)), path => (files ?? []).GetValueOrDefault(path), "abc123");

        check("W6: DBUS_SESSION_BUS_ADDRESS comes first",
            DBusConnection.SessionAddress(User, Look(new() { ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/x", ["XDG_RUNTIME_DIR"] = "/run/user/1000" }, new() { ["/run/user/1000/bus"] = User })) == "unix:path=/x");
        check("W6: then $XDG_RUNTIME_DIR/bus",
            DBusConnection.SessionAddress(User, Look(new() { ["XDG_RUNTIME_DIR"] = "/run/user/1000/" }, new() { ["/run/user/1000/bus"] = User })) == "unix:path=/run/user/1000/bus");
        check("W6: WSLg's runtime folder without a bus falls back to /run/user",
            DBusConnection.SessionAddress(User, Look(new() { ["XDG_RUNTIME_DIR"] = "/mnt/wslg/runtime-dir" }, new() { ["/run/user/1000/bus"] = User })) == "unix:path=/run/user/1000/bus");
        check("W6: a bus socket of another user is not taken",
            DBusConnection.SessionAddress(User, Look(new() { ["XDG_RUNTIME_DIR"] = "/tmp/evil" }, new() { ["/tmp/evil/bus"] = 1001 })) is null);
        check("W6: an unknown user id finds no socket",
            DBusConnection.SessionAddress(uint.MaxValue, Look(new() { ["XDG_RUNTIME_DIR"] = "/run/user/x" }, new() { ["/run/user/x/bus"] = uint.MaxValue })) is null);

        var autolaunch = new Dictionary<string, string>
        {
            ["/home/u/.dbus/session-bus/other-0"] = "DBUS_SESSION_BUS_ADDRESS=unix:abstract=/tmp/other\n",
            ["/home/u/.dbus/session-bus/abc123-0"] = "# This file allows processes on the machine\nDBUS_SESSION_BUS_ADDRESS='unix:abstract=/tmp/dbus-Q,guid=1'\nDBUS_SESSION_BUS_PID=88\n",
            ["/home/u/.dbus/session-bus/abc123-1"] = "DBUS_SESSION_BUS_ADDRESS=unix:abstract=/tmp/display1\n"
        };
        check("W6: last, this machine's autolaunch file for the display, unquoted",
            DBusConnection.SessionAddress(User, Look(new() { ["HOME"] = "/home/u", ["DISPLAY"] = ":0.0" }, [], autolaunch)) == "unix:abstract=/tmp/dbus-Q,guid=1");
        check("W6: another display's file is not taken",
            DBusConnection.SessionAddress(User, Look(new() { ["HOME"] = "/home/u", ["DISPLAY"] = ":2" }, [], autolaunch)) is null);
        check("W6: without DISPLAY there is no autolaunch bus", DBusConnection.SessionAddress(User, Look(new() { ["HOME"] = "/home/u" }, [], autolaunch)) is null);
        check("W6: display numbers", DBusConnection.DisplayNumber(":0") == "0" && DBusConnection.DisplayNumber("host:10.0") == "10" && DBusConnection.DisplayNumber("x") is null && DBusConnection.DisplayNumber(":") is null);
        check("W6: the agent looks again within seconds, not half a minute", WslLink.BusCheck <= TimeSpan.FromSeconds(5));
    }

    // W10: not the service, the agent still answers a call at once.
    private static void NotServing(Action<string, bool> check)
    {
        var call = Notify([]);
        var output = NotificationServer.NotServing(call);
        check("W10: a call is answered with an error", output.Bus is [{ Type: DBusMessageType.Error, ErrorName: NotificationServer.UnknownMethod } error] && error.ReplySerial == call.Serial && output.ToBrimDeck.Count == 0);
        call.Flags |= DBusMessage.NoReplyExpected;
        check("W10: nothing for a call that expects no reply", NotificationServer.NotServing(call).Bus.Count == 0);
    }

    // W11: "body-markup" is said; only the body is read as markup.
    private static void BodyMarkup(Action<string, bool> check)
    {
        check("W11: body-markup is a capability", FreedesktopNotifications.Capabilities.Contains("body-markup"));
        var notice = FreedesktopNotifications.ToNotice(Body("Vec<u8> & co", "if a&lt;b and c&gt;d <b>bold</b>"), 1);
        check("W11: the summary is kept as it is", notice.Title == "Vec<u8> & co");
        check("W11: the body's escapes are decoded and its tags go", notice.Body == "if a<b and c>d bold");
    }

    // I1: an OSD's tag replaces the notice it sent before, within the same application only.
    private static void StackTags(Action<string, bool> check)
    {
        foreach (var hint in FreedesktopNotifications.StackHints)
            check("I1: " + hint + " gives the replace key", FreedesktopNotifications.ToNotice(Body("Volume", "", new() { [hint] = new DBusVariant("s", "volume") }), 1).ReplaceId == "stack:volume");
        check("I1: no tag, no key", FreedesktopNotifications.ToNotice(Body("x", ""), 1).ReplaceId is null);

        var hub = new NoticeHub();
        NoticeInput Input(uint id, string app, int level)
        {
            var message = FreedesktopNotifications.ToNotice(["", 0u, "", "Volume " + level, "", new List<object>(), new Dictionary<object, object> { ["synchronous"] = new DBusVariant("s", "volume") }, -1], id);
            message.AppName = app; message.Session = "server-1";
            return NoticeIntake.FromAgent(message, "Ubuntu", agentIsServer: true, T0);
        }
        var first = hub.Receive(Input(1, "osd", 10), T0);
        var second = hub.Receive(Input(2, "osd", 20), T0.AddSeconds(1));
        check("I1: the second step updates the first notice", first.Receipt == NoticeReceipt.Accepted && second.Receipt == NoticeReceipt.Updated && hub.Notices.Count == 1 && hub.Notices[0].Title == "Volume 20");
        check("I1: another application's same tag is its own notice", hub.Receive(Input(3, "other", 30), T0.AddSeconds(2)).Receipt == NoticeReceipt.Accepted && hub.Notices.Count == 2);
    }

    // I1: a transient notice shows and is listed, but is never written to the saved notices.
    private static void Transient(Action<string, bool> check)
    {
        check("I1: transient as a boolean", FreedesktopNotifications.ToNotice(Body("x", "", new() { ["transient"] = new DBusVariant("b", true) }), 1).Transient == true);
        check("I1: transient as a number", FreedesktopNotifications.ToNotice(Body("x", "", new() { ["transient"] = new DBusVariant("y", (byte)1) }), 1).Transient == true);
        check("I1: transient false is not transient", FreedesktopNotifications.ToNotice(Body("x", "", new() { ["transient"] = new DBusVariant("b", false) }), 1).Transient is null);

        var message = FreedesktopNotifications.ToNotice(Body("Copied", "", new() { ["transient"] = new DBusVariant("b", true) }), 7);
        message.Session = "s";
        var hub = new NoticeHub();
        var outcome = hub.Receive(NoticeIntake.FromAgent(message, "Ubuntu", agentIsServer: true, T0), T0);
        hub.Receive(NoticeIntake.FromAgent(new NoticeMessage { Origin = "dbus", Session = "s", NativeId = "8", Title = "Kept", AppName = "app" }, "Ubuntu", true, T0), T0);
        check("I1: a transient notice is received and shown", outcome.Receipt == NoticeReceipt.Accepted && outcome.Notice is { Transient: true } && hub.Notices.Count == 2);

        var folder = Path.Combine(Path.GetTempPath(), "brimdeck-wsl2-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new NoticeHistory(folder, new NoticeHistory.PlainProtector());
            history.Save(hub.Notices, []);
            check("I1: only the other notice is saved", history.Load(out var saved, out _) == NoticeHistoryState.Loaded && saved is [{ Title: "Kept", Transient: false }]);
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    // I1: icons that are links within the icon folders are sent as the files they lead to; nothing else changes.
    private static void IconLinks(Action<string, bool> check)
    {
        var links = new Dictionary<string, string>
        {
            ["/usr/share/icons/hicolor/48x48/apps/apport.png"] = "../mimetypes/apport-real.png",
            ["/usr/share/icons/hicolor/48x48/mimetypes/apport-real.png"] = "/usr/share/pixmaps/apport.png",
            ["/usr/share/icons/hicolor/48x48/apps/escape.png"] = "../../../../../../home/u/secret.png",
            ["/usr/share/icons/hicolor/48x48/apps/away.png"] = "/home/u/x.png",
            ["/usr/share/icons/hicolor/48x48/apps/loop.png"] = "loop.png",
            ["/home/u/link.png"] = "/usr/share/pixmaps/apport.png",
            ["/usr/share/icons/hicolor/48x48/apps/broken.png"] = "gone.png"
        };
        var files = new HashSet<string> { "/usr/share/pixmaps/apport.png", "/home/u/secret.png", "/home/u/x.png", "/usr/share/icons/hicolor/64x64/apps/away.png", "/usr/share/icons/hicolor/48x48/apps/plain.png" };
        string? Resolve(string? icon) => LinuxIcons.Resolve(icon, links.GetValueOrDefault, files.Contains);

        check("I1: a chain of links within the trees resolves", Resolve("apport") == "/usr/share/pixmaps/apport.png");
        check("I1: an absolute path that is such a link too", Resolve("/usr/share/icons/hicolor/48x48/apps/apport.png") == "/usr/share/pixmaps/apport.png");
        check("I1: a plain file keeps its name", Resolve("plain") == "plain");
        check("I1: a link leaving the trees is passed over, as BrimDeck passes it over", Resolve("away") == "away" && Resolve("escape") == "escape");
        check("I1: a link from outside the trees is not followed", Resolve("/home/u/link.png") == "/home/u/link.png");
        check("I1: a loop or a dangling link changes nothing", Resolve("loop") == "loop" && Resolve("broken") == "broken");
        check("I1: names BrimDeck refuses and addresses are left alone", Resolve("../apport") == "../apport" && Resolve("a/b") == "a/b" && Resolve("file:///x.png") == "file:///x.png" && Resolve(null) is null);
        check("I1: the follow stops climbing above /", LinuxIcons.Normalize("/a/../../b") is null && LinuxIcons.Normalize("/usr//share/./icons/x/../y.png") == "/usr/share/icons/y.png");
        check("I1: BrimDeck still looks in the same places", LinuxIcons.Candidates("apport").First() == "/usr/share/icons/hicolor/48x48/apps/apport.png" && LinuxIcons.Candidates("apport").Last() == "/usr/share/pixmaps/apport.png");
    }

    // review-batch1 item 2: an expired notice is closed for BrimDeck too, so that its buttons go.
    private static void ExpiredForBrimDeck(Action<string, bool> check)
    {
        var server = new NotificationServer("1", session: "server-x");
        var reply = server.Handle(new DBusMessage
        {
            Type = DBusMessageType.MethodCall, Serial = 4, Sender = ":1.7", Destination = FreedesktopNotifications.Name, Path = FreedesktopNotifications.Path,
            Interface = FreedesktopNotifications.Interface, Member = "Notify", Signature = FreedesktopNotifications.NotifySignature,
            Body = ["app", 0u, "", "T", "B", new List<object> { "open", "Open" }, new Dictionary<object, object>(), 3000]
        }, T0);
        uint id = (uint)reply.Bus[0].Body[0];
        var expired = server.Expire(T0.AddSeconds(3));
        check("Item 2: the application hears it expired", expired.Bus is [{ Member: "NotificationClosed" }]);
        check("Item 2: BrimDeck hears it closed, as expired, for this run's notice", expired.ToBrimDeck is [{ Type: NoticeTypes.Close, Origin: "dbus", Session: "server-x", Reason: 1 } close] && close.NativeId == id.ToString());
        check("Item 2: an action after that reaches nothing", server.ActionInvoked(id, "open").Count == 0);
    }

    // review-batch1 item 6: a status written but not answered is not reported as receiving.
    private static async Task StatusNotConfirmed(Action<string, bool> check)
    {
        NotifySender Sender(NoticeMessage? reply) => new("1", _ => Task.FromResult(reply), _ => (null, 127, false), "unreachable", "not receiving");
        var status = NotifyCommandLine.Parse(["status"]);
        var output = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            check("Item 6: no reply is not confirmed", await NotifyCli.RunAsync(status, Sender(NoticeProtocol.Reply(NoticeResults.Rejected, "no-reply"))) == NotifyCli.Unreachable);
            check("Item 6: nothing reached is unreachable as before", await NotifyCli.RunAsync(status, Sender(null)) == NotifyCli.Unreachable);
            check("Item 6: an accepted status is receiving", await NotifyCli.RunAsync(status, Sender(NoticeProtocol.Reply(NoticeResults.Accepted, "agent server"))) == 0);
        }
        finally { Console.SetOut(output); }
    }

    private static object[] Body(string summary, string body, Dictionary<object, object>? hints = null)
        => ["app", 0u, "", summary, body, new List<object>(), hints ?? new Dictionary<object, object>(), -1];

    private static DBusMessage Notify(object[] actions) => new()
    {
        Type = DBusMessageType.MethodCall, Serial = 9, Sender = ":1.7", Destination = ":1.3", Path = FreedesktopNotifications.Path,
        Interface = FreedesktopNotifications.Interface, Member = "Notify", Signature = FreedesktopNotifications.NotifySignature,
        Body = ["app", 0u, "", "T", "B", actions.ToList(), new Dictionary<object, object>(), -1]
    };
}
