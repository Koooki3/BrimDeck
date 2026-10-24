using System.Buffers.Binary;
using BrimDeck.Protocol;
using BrimDeck.Protocol.DBus;

internal static class NoticeProtocolTests
{
    public static async Task Run(Action<string, bool> check)
    {
        await Frames(check);
        Validation(check);
        CommandLine(check);
        DBusWire(check);
        Server(check);
        Monitor(check);
        Markup(check);
    }

    private static async Task Frames(Action<string, bool> check)
    {
        var stream = new MemoryStream();
        await NoticeFrames.WriteAsync(stream, new NoticeMessage { Title = "构建完成", Body = "测试已通过 ✓", Progress = .5 });
        await NoticeFrames.WriteAsync(stream, new NoticeMessage { Type = NoticeTypes.Status });
        stream.Position = 0;
        var first = await NoticeFrames.ReadMessageAsync(stream);
        var second = await NoticeFrames.ReadMessageAsync(stream);
        var end = await NoticeFrames.ReadMessageAsync(stream);
        check("Notice frames round-trip UTF-8 text in order", first?.Title == "构建完成" && first.Body == "测试已通过 ✓" && first.Progress == .5 && second?.Type == NoticeTypes.Status && end is null);

        var json = System.Text.Encoding.UTF8.GetString(NoticeProtocol.Serialize(new NoticeMessage { Title = "x" }));
        check("Null fields are left out of a frame", !json.Contains("body", StringComparison.Ordinal) && json.Contains("\"title\":\"x\"", StringComparison.Ordinal));

        var oversize = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(oversize, NoticeProtocol.MaxFrame + 1);
        check("A frame over 64 KiB is refused", await Throws<InvalidDataException>(() => NoticeFrames.ReadAsync(new MemoryStream(oversize))));
        var negative = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(negative, -1);
        check("A negative frame length is refused", await Throws<InvalidDataException>(() => NoticeFrames.ReadAsync(new MemoryStream(negative))));
        var cut = new byte[6]; BinaryPrimitives.WriteInt32LittleEndian(cut, 10);
        check("A stream ending inside a frame is refused", await Throws<InvalidDataException>(() => NoticeFrames.ReadAsync(new MemoryStream(cut))));
        check("A stream ending inside the length is refused", await Throws<InvalidDataException>(() => NoticeFrames.ReadAsync(new MemoryStream([1, 0]))));
        var garbage = new MemoryStream(); garbage.Write([3, 0, 0, 0]); garbage.Write("abc"u8); garbage.Position = 0;
        check("A frame that is not JSON is refused", await Throws<InvalidDataException>(() => NoticeFrames.ReadMessageAsync(garbage)));
        check("Writing a message larger than a frame fails before sending", await Throws<InvalidDataException>(() => NoticeFrames.WriteAsync(new MemoryStream(), new NoticeMessage { Actions = Enumerable.Range(0, 3000).Select(i => new NoticeActionInfo { Key = "k" + i, Label = new string('x', 30) }).ToList() })));

        string a = NoticeProtocol.PipeName(1, "S-1-5-21-1"), b = NoticeProtocol.PipeName(1, "S-1-5-21-1"), c = NoticeProtocol.PipeName(2, "S-1-5-21-1"), d = NoticeProtocol.PipeName(1, "S-1-5-21-2");
        check("Pipe names are stable per user and session and differ otherwise", a == b && a != c && a != d && !a.Contains('\\') && a.Length < 64);
    }

    private static void Validation(Action<string, bool> check)
    {
        check("An empty notice is refused", NoticeProtocol.Validate(new NoticeMessage { Title = " ", Body = "" }) == "empty");
        check("A run result without text is accepted", NoticeProtocol.Validate(new NoticeMessage { ExitCode = 1 }) is null);
        check("A newer protocol version is refused", NoticeProtocol.Validate(new NoticeMessage { Version = 99, Title = "x" }) == "unsupported-version");
        check("An unknown type is refused", NoticeProtocol.Validate(new NoticeMessage { Type = "exec", Title = "x" }) == "unknown-type");
        check("Progress outside 0..1 is refused", NoticeProtocol.Validate(new NoticeMessage { Title = "x", Progress = 1.5 }) == "bad-progress"
            && NoticeProtocol.Validate(new NoticeMessage { Title = "x", Progress = double.NaN }) == "bad-progress");
        check("A run duration that cannot be a time span is refused", NoticeProtocol.Validate(new NoticeMessage { ExitCode = 0, DurationMs = long.MaxValue }) == "bad-duration"
            && NoticeProtocol.Validate(new NoticeMessage { ExitCode = 0, DurationMs = -1 }) == "bad-duration"
            && NoticeProtocol.Validate(new NoticeMessage { ExitCode = 0, DurationMs = NoticeProtocol.MaxDurationMs }) is null
            && TimeSpan.FromMilliseconds(NoticeProtocol.MaxDurationMs) > TimeSpan.Zero);
        var long_ = new NoticeMessage { Title = new string('标', 1000), Body = "a\u0007b\r\nc\td", Actions = Enumerable.Range(0, 20).Select(i => new NoticeActionInfo { Key = "k" + i, Label = "L" }).Append(new NoticeActionInfo { Key = "", Label = "x" }).ToList() };
        check("Validation trims texts and actions to the limits", NoticeProtocol.Validate(long_) is null && long_.Title!.Length == NoticeProtocol.MaxTitle
            && long_.Body == "a b\nc\td" && long_.Actions!.Count == NoticeProtocol.MaxActions);
        var emoji = NoticeProtocol.Clip("ab😀", 3);
        check("Clipping never splits a surrogate pair", emoji == "ab");
    }

    private static void CommandLine(Action<string, bool> check)
    {
        var send = NotifyCommandLine.Parse(["构建完成", "测试已通过", "-s", "make", "--priority", "HIGH", "--progress=40", "-r", "build-1"]);
        check("brim-notify reads title, body and options", send.Kind == NotifyCommandKind.Send && send.Message!.Title == "构建完成" && send.Message.Body == "测试已通过"
            && send.Message.AppName == "make" && send.Message.Priority == "high" && send.Message.Progress == .4 && send.Message.ReplaceId == "build-1" && send.Message.Origin == "cli");
        var body = NotifyCommandLine.Parse(["Title", "--body", "Body text"]);
        check("brim-notify takes the body as an option", body.Message?.Body == "Body text" && body.Message.AppName == "brim-notify");
        check("A negative number is a body, not an option", NotifyCommandLine.Parse(["Temp", "-5"]).Message?.Body == "-5");
        check("Unknown options are refused", NotifyCommandLine.Parse(["x", "--nope", "1"]).Kind == NotifyCommandKind.Error);
        check("An option without its value is refused", NotifyCommandLine.Parse(["x", "--body"]).Kind == NotifyCommandKind.Error);
        check("A third positional argument is refused", NotifyCommandLine.Parse(["a", "b", "c"]).Kind == NotifyCommandKind.Error);
        check("Progress outside 0..100 is refused", NotifyCommandLine.Parse(["a", "--progress", "101"]).Kind == NotifyCommandKind.Error);
        check("An unknown category is refused", NotifyCommandLine.Parse(["a", "-c", "spam"]).Kind == NotifyCommandKind.Error);
        check("The title cannot be given twice", NotifyCommandLine.Parse(["a", "-t", "b"]).Kind == NotifyCommandKind.Error);
        check("No arguments show the help", NotifyCommandLine.Parse([]).Kind == NotifyCommandKind.Help && NotifyCommandLine.Parse(["x", "-h"]).Kind == NotifyCommandKind.Help);
        var run = NotifyCommandLine.Parse(["run", "--title", "测试", "--", "dotnet", "test", "--no-build"]);
        check("run takes options, then the program after --", run.Kind == NotifyCommandKind.Run && run.Program!.SequenceEqual(["dotnet", "test", "--no-build"])
            && run.Message!.Title == "测试" && run.Message.Command == "dotnet");
        var bare = NotifyCommandLine.Parse(["run", "make", "-j8"]);
        check("run without -- takes the first word as the program and keeps its options", bare.Kind == NotifyCommandKind.Run && bare.Program!.SequenceEqual(["make", "-j8"]));
        check("run without a program is refused", NotifyCommandLine.Parse(["run", "--"]).Kind == NotifyCommandKind.Error);
        check("status and --stdio are commands of their own", NotifyCommandLine.Parse(["status"]).Kind == NotifyCommandKind.Status && NotifyCommandLine.Parse(["--stdio"]).Kind == NotifyCommandKind.Stdio);
        var result = NotifyCommandLine.RunResult(run.Message!, 2, TimeSpan.FromSeconds(3));
        check("A run result carries the exit code, time and command", result.ExitCode == 2 && result.DurationMs == 3000 && result.Category == "error" && result.Command == "dotnet" && result.Title == "测试");
    }

    private static object[] NotifyBody(uint replaces = 0, string summary = "Build", string body = "<b>All</b> tests &amp; checks passed", IList<object>? actions = null, Dictionary<object, object>? hints = null)
        => ["make", replaces, "dialog-information", summary, body, actions ?? new List<object> { "default", "Open", "retry", "Retry" },
            hints ?? new Dictionary<object, object>
            {
                ["urgency"] = new DBusVariant("y", (byte)2),
                ["category"] = new DBusVariant("s", "transfer.complete"),
                ["desktop-entry"] = new DBusVariant("s", "org.example.Make"),
                ["value"] = new DBusVariant("i", 40),
                ["image-data"] = new DBusVariant("(iiibiiay)", new object[] { 2, 1, 8, true, 8, 4, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } })
            }, -1];

    private static void DBusWire(Action<string, bool> check)
    {
        var call = DBusMessage.Call(FreedesktopNotifications.Name, FreedesktopNotifications.Path, FreedesktopNotifications.Interface, "Notify", FreedesktopNotifications.NotifySignature, NotifyBody());
        call.Serial = 7; call.Sender = ":1.42";
        var bytes = call.Encode();
        check("Encoded D-Bus messages announce their own length", DBusMessage.TotalLength(bytes) == bytes.Length);
        var decoded = DBusMessage.Decode(bytes);
        var hints = (IDictionary<object, object>)decoded.Body[6];
        var image = (object[])((DBusVariant)hints["image-data"]).Value;
        check("A Notify call survives encoding and decoding", decoded.Member == "Notify" && decoded.Serial == 7 && decoded.Sender == ":1.42"
            && decoded.Signature == FreedesktopNotifications.NotifySignature && (string)decoded.Body[3] == "Build" && (int)decoded.Body[7] == -1
            && ((IList<object>)decoded.Body[5]).Count == 4 && ((byte[])image[6]).Length == 8 && (bool)image[3]);
        check("Signatures split into complete types", DBusSignatures.Split("susssasa{sv}i").SequenceEqual(["s", "u", "s", "s", "s", "as", "a{sv}", "i"]));
        check("An unbalanced signature is refused", Throws<InvalidDataException>(() => DBusSignatures.Split("a{sv").ToList()));
        check("A cut-off message is refused", Throws<InvalidDataException>(() => DBusMessage.Decode(bytes.AsSpan(0, bytes.Length - 5))));
        // A big-endian method return carrying a uint, written by hand: the agent must read messages of either byte order.
        byte[] big =
        [
            (byte)'B', 2, 0, 1, 0, 0, 0, 4, 0, 0, 0, 9, 0, 0, 0, 15,
            5, 1, (byte)'u', 0, 0, 0, 0, 3, 8, 1, (byte)'g', 0, 1, (byte)'u', 0, 0,
            0, 0, 0, 42
        ];
        var reply = DBusMessage.Decode(big);
        check("Big-endian messages are read", reply.Type == DBusMessageType.MethodReturn && reply.ReplySerial == 3 && reply.Serial == 9 && (uint)reply.Body[0] == 42);
        // Nested variants deeper than the reader allows.
        var deep = new DBusVariant("i", 1);
        for (int i = 0; i < 40; i++) deep = new DBusVariant("v", deep);
        var nested = new DBusMessage { Type = DBusMessageType.Signal, Path = "/a", Interface = "a.b", Member = "C", Signature = "v", Body = [deep] }.Encode();
        check("Excessive nesting is refused", Throws<InvalidDataException>(() => DBusMessage.Decode(nested)));
        var endpoints = DBusConnection.Endpoints("unix:path=/run/user/1000/bus;tcp:host=x;unix:abstract=/tmp/dbus-a%2Cb,guid=1").ToList();
        check("Bus addresses yield their Unix endpoints, unescaped", endpoints.Count == 2 && endpoints[0].ToString() == "/run/user/1000/bus" && endpoints[1].ToString()!.Contains("/tmp/dbus-a,b", StringComparison.Ordinal));
    }

    private static DBusMessage Call(string member, string signature = "", object[]? body = null, string @interface = FreedesktopNotifications.Interface, byte flags = 0)
        => new() { Type = DBusMessageType.MethodCall, Path = FreedesktopNotifications.Path, Interface = @interface, Member = member, Signature = signature, Body = body ?? [], Serial = 5, Sender = ":1.9", Flags = flags };

    private static void Server(Action<string, bool> check)
    {
        var server = new NotificationServer("1.2.0", () => "abc");
        var caps = server.Handle(Call("GetCapabilities"));
        check("The service lists only what it implements", caps.Bus is [{ Type: DBusMessageType.MethodReturn } r] && ((IEnumerable<object>)r.Body[0]).Cast<string>().SequenceEqual(["body", "body-markup", "actions"]) && r.ReplySerial == 5 && r.Destination == ":1.9");
        var first = server.Handle(Call("Notify", FreedesktopNotifications.NotifySignature, NotifyBody()));
        var notice = first.ToBrimDeck.Single();
        check("Notify answers with a new id and passes the notice on", first.Bus.Single().Body[0] is 1u && notice.NativeId == "1" && notice.Title == "Build"
            && notice.Body == "All tests & checks passed" && notice.Priority == "high" && notice.Category == "success" && notice.AppId == "org.example.Make"
            && notice.Progress == .4 && notice.Actions!.Count == 2 && notice.Actions[1].Key == "retry" && notice.Session == "server" && notice.Origin == "dbus");
        var replaced = server.Handle(Call("Notify", FreedesktopNotifications.NotifySignature, NotifyBody(replaces: 1, summary: "Build 2")));
        check("A replacement keeps its id", replaced.Bus.Single().Body[0] is 1u && replaced.ToBrimDeck.Single().Title == "Build 2");
        var unknown = server.Handle(Call("Notify", FreedesktopNotifications.NotifySignature, NotifyBody(replaces: 77)));
        check("Replacing an unknown id gets a new one", unknown.Bus.Single().Body[0] is 2u);
        var close = server.Handle(Call("CloseNotification", "u", [1u]));
        check("CloseNotification signals the close and tells BrimDeck", close.Bus.Count == 2 && close.Bus[1].Member == "NotificationClosed" && (uint)close.Bus[1].Body[1] == FreedesktopNotifications.ClosedByCall
            && close.ToBrimDeck.Single() is { Type: NoticeTypes.Close, NativeId: "1" });
        check("Closing an id twice signals nothing more", server.Handle(Call("CloseNotification", "u", [1u])).Bus.Count == 1);
        var action = server.ActionInvoked(2, "retry");
        check("A chosen action is signalled, then the notice closes as dismissed", action.Count == 2 && action[0].Member == "ActionInvoked" && (string)action[0].Body[1] == "retry"
            && (uint)action[1].Body[1] == FreedesktopNotifications.DismissedByUser);
        check("Actions and dismissals for closed ids do nothing", server.ActionInvoked(2, "retry").Count == 0 && server.Dismissed(99).Count == 0);
        check("Unknown methods get UnknownMethod", server.Handle(Call("Explode")).Bus.Single().ErrorName == "org.freedesktop.DBus.Error.UnknownMethod");
        check("Notify with the wrong signature is not taken", server.Handle(Call("Notify", "s", ["x"])).Bus.Single().Type == DBusMessageType.Error);
        check("No reply is sent when none is expected", server.Handle(Call("GetCapabilities", flags: DBusMessage.NoReplyExpected)).Bus.Count == 0);
        check("Introspection and Ping are answered", server.Handle(Call("Introspect", @interface: "org.freedesktop.DBus.Introspectable")).Bus.Single().Body[0] is string xml && xml.Contains("Notify", StringComparison.Ordinal)
            && server.Handle(Call("Ping", @interface: "org.freedesktop.DBus.Peer")).Bus.Single().Type == DBusMessageType.MethodReturn);
        check("Signals are not handled as calls", server.Handle(new DBusMessage { Type = DBusMessageType.Signal, Member = "Notify" }) == ServiceOutput.None);
        var bad = NotifyBody(); bad[1] = "not a number";
        check("Malformed arguments get InvalidArgs", server.Handle(Call("Notify", FreedesktopNotifications.NotifySignature, bad)).Bus.Single().ErrorName == "org.freedesktop.DBus.Error.InvalidArgs");
    }

    private static void Monitor(Action<string, bool> check)
    {
        var monitor = new NotificationMonitor { Owner = ":1.5" };
        var now = DateTimeOffset.UtcNow;
        var call = Call("Notify", FreedesktopNotifications.NotifySignature, NotifyBody()); call.Sender = ":1.20"; call.Serial = 11;
        check("A watched Notify waits for the service's reply", monitor.Observe(call, now).Count == 0);
        var stranger = new DBusMessage { Type = DBusMessageType.MethodReturn, Sender = ":1.99", Destination = ":1.20", ReplySerial = 11, Signature = "u", Body = [5u] };
        check("Replies from anyone but the service are ignored", monitor.Observe(stranger, now).Count == 0);
        var answer = new DBusMessage { Type = DBusMessageType.MethodReturn, Sender = ":1.5", Destination = ":1.20", ReplySerial = 11, Signature = "u", Body = [5u] };
        var seen = monitor.Observe(answer, now);
        check("The service's reply gives the watched notice its id, without actions", seen is [{ NativeId: "5", Session: "monitor", Actions: null }]);
        var replacement = Call("Notify", FreedesktopNotifications.NotifySignature, NotifyBody(replaces: 5)); replacement.Sender = ":1.20";
        check("A watched replacement passes on at once", monitor.Observe(replacement, now) is [{ NativeId: "5" }]);
        var lost = Call("Notify", FreedesktopNotifications.NotifySignature, NotifyBody()); lost.Sender = ":1.21"; lost.Serial = 3;
        monitor.Observe(lost, now);
        check("An unanswered call passes on without an id after the wait", monitor.Flush(now + TimeSpan.FromSeconds(1)).Count == 0 && monitor.Flush(now + NotificationMonitor.Patience) is [{ NativeId: null }]);
        var closed = new DBusMessage { Type = DBusMessageType.Signal, Sender = ":1.5", Interface = FreedesktopNotifications.Interface, Member = "NotificationClosed", Signature = "uu", Body = [5u, 2u] };
        check("The service's close signal is passed on", monitor.Observe(closed, now) is [{ Type: NoticeTypes.Close, NativeId: "5", Reason: 2 }]);
        check("The reply rule names the service", NotificationMonitor.ReplyRule(":1.5") == "type='method_return',sender=':1.5'");
    }

    private static void Markup(Action<string, bool> check)
    {
        check("Markup is shown as plain text", FreedesktopNotifications.PlainText("<b>Hi</b> &lt;you&gt;<br/>next <a href=\"x\">link</a>") == "Hi <you>\nnext link");
        check("Plain text is left alone", FreedesktopNotifications.PlainText("a < b") == "a < b");
        check("Categories map to notice categories", FreedesktopNotifications.CategoryOf("im.received") == "message" && FreedesktopNotifications.CategoryOf("network.error") == "error"
            && FreedesktopNotifications.CategoryOf("transfer") == "task" && FreedesktopNotifications.CategoryOf("device.added") == "info" && FreedesktopNotifications.CategoryOf(null) is null);
        var low = FreedesktopNotifications.ToNotice(NotifyBody(hints: new() { ["urgency"] = new DBusVariant("y", (byte)0), ["value"] = new DBusVariant("i", 400) }, actions: new List<object> { "only-key" }), 3);
        check("Low urgency, an out-of-range value and an odd action list are handled", low.Priority == "low" && low.Progress is null && low.Actions is null);
    }

    private static async Task<bool> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; } catch (T) { return true; }
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; } catch (T) { return true; }
    }
}
