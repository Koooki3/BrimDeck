using System.Diagnostics;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;
using BrimDeck.Protocol.DBus;

// The review of the WSL link and brim-notify (review-wsl-cli, W1 to W14).
internal static class NoticeWslOpsTests
{
    public static async Task Run(Action<string, bool> check)
    {
        OutputVariables(check);
        await NoSecondSend(check);
        await SocketFileWatch(check);
        AgentIntake(check);
        await LongMessages(check);
        StopNotFound(check);
        RelativeIcons(check);
        ExpireTimeout(check);
        Backoff(check);
    }

    // W1: WSL_UTF8 is never passed on to wsl.exe, whose "--list" would then not be UTF-16.
    private static void OutputVariables(Action<string, bool> check)
    {
        var environment = new Dictionary<string, string?> { ["WSL_UTF8"] = "1", ["wsl_utf8"] = "1", ["PATH"] = @"C:\Windows", ["WSLENV"] = "X" };
        WslLink.ClearOutputVariables(environment);
        check("WSL_UTF8 is removed whatever its case", !environment.Keys.Any(key => key.Equals("WSL_UTF8", StringComparison.OrdinalIgnoreCase)));
        check("Other variables stay", environment.ContainsKey("PATH") && environment.ContainsKey("WSLENV"));
        var start = new ProcessStartInfo("wsl.exe");
        start.Environment["WSL_UTF8"] = "1";
        WslLink.ClearOutputVariables(start.Environment);
        check("A process's own environment loses it too", !start.Environment.ContainsKey("WSL_UTF8") && start.Environment.Count > 0);
    }

    // W3: a message that reached the other side is never sent again another way; brim-notify waits longer than the agent.
    private static async Task NoSecondSend(Action<string, bool> check)
    {
        var message = new NoticeMessage { Title = "Build", EventId = "e1" };
        var refused = await NoticeFrames.ExchangeAsync(new Duplex([], failWrite: true), message, TimeSpan.FromSeconds(2));
        check("A message that could not be written may go another way", refused is (null, false));
        var watch = Stopwatch.StartNew();
        var silent = await NoticeFrames.ExchangeAsync(new Duplex([], blockRead: true), message, TimeSpan.FromMilliseconds(200));
        check("One written but not answered in time counts as written", silent is (null, true) && watch.Elapsed < TimeSpan.FromSeconds(5));
        using var reply = new MemoryStream();
        await NoticeFrames.WriteAsync(reply, NoticeProtocol.Reply(NoticeResults.Accepted, null, "e1"));
        var answered = await NoticeFrames.ExchangeAsync(new Duplex(reply.ToArray()), message, TimeSpan.FromSeconds(2));
        check("An answered one gives its reply", answered is ({ Result: NoticeResults.Accepted, EventId: "e1" }, true));
        check("brim-notify waits longer than the agent waits for BrimDeck", WslLink.CliReplyWait > WslLink.AgentReplyWait + TimeSpan.FromSeconds(2));
        check("A late answer is reported as unreachable, not as refused", NotifyCli.Report(NoticeProtocol.Reply(NoticeResults.Rejected, "no-reply"), "x") == NotifyCli.Unreachable);
    }

    // W4: the CLI socket notices its file is gone instead of listening on, unreachable, for good.
    private static async Task SocketFileWatch(Action<string, bool> check)
    {
        static async Task<object> Never(CancellationToken token) { await Task.Delay(Timeout.Infinite, token); return new object(); }
        var gone = await WslLink.AcceptWhileThereAsync(Never, () => false, TimeSpan.FromMilliseconds(30), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        check("A socket whose file went stops waiting for connections", gone is null);
        int looks = 0;
        var connection = new TaskCompletionSource<object>();
        var waiting = WslLink.AcceptWhileThereAsync(_ => connection.Task, () => { if (++looks == 3) connection.TrySetResult("client"); return true; }, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        check("While the file is there it keeps waiting, and returns the connection", await waiting.WaitAsync(TimeSpan.FromSeconds(5)) is "client" && looks >= 3);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        bool cancelled = false;
        try { await WslLink.AcceptWhileThereAsync(Never, () => true, TimeSpan.FromMilliseconds(20), stop.Token).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { cancelled = true; }
        check("Stopping the agent ends the wait", cancelled);
        check("The file is looked for every half minute, not in a busy loop", WslLink.SocketCheck >= TimeSpan.FromSeconds(10));
    }

    // W5: a distribution's flood is counted on the bridge's thread, before the UI thread.
    private static void AgentIntake(Action<string, bool> check)
    {
        var gate = new IntakeGate(2, TimeSpan.FromSeconds(10));
        NoticeMessage Cli(string id) => new() { Type = NoticeTypes.Notify, Origin = "cli", EventId = id, Title = "x" };
        check("Notices within the limit go on", NoticeIntake.AdmitFromAgent(gate, Cli("a"), 0, out var none) && none is null && NoticeIntake.AdmitFromAgent(gate, Cli("b"), 1, out _));
        check("One over it stops, and a waiting brim-notify hears why",
            !NoticeIntake.AdmitFromAgent(gate, Cli("c"), 2, out var refusal) && refusal is { Type: NoticeTypes.Reply, Result: NoticeResults.Rejected, Detail: "rate-limited", EventId: "c" });
        check("A desktop notification over it stops without a reply",
            !NoticeIntake.AdmitFromAgent(gate, new NoticeMessage { Type = NoticeTypes.Notify, Origin = "dbus", Title = "x" }, 3, out var silent) && silent is null);
        check("Closes and status reports are not counted",
            NoticeIntake.AdmitFromAgent(gate, new NoticeMessage { Type = NoticeTypes.Close, NativeId = "1" }, 4, out _) && NoticeIntake.AdmitFromAgent(gate, new NoticeMessage { Type = NoticeTypes.Status }, 5, out _));
        check("The window passes", NoticeIntake.AdmitFromAgent(gate, Cli("d"), 10_000, out _));
    }

    // W7: a long body is cut before it is sent; a message that still does not fit is a usage error, not "not running".
    private static async Task LongMessages(Action<string, bool> check)
    {
        var sent = new List<NoticeMessage>();
        var sender = new NotifySender("1", message => { sent.Add(message); return Task.FromResult<NoticeMessage?>(NoticeProtocol.Reply(NoticeResults.Accepted)); }, _ => (null, 1, false), "unreachable", "x");
        int code = await NotifyCli.RunAsync(NotifyCommandLine.Parse(["Build log", new string('日', 80_000)]), sender);
        check("A body over the limit is cut as BrimDeck cuts it, and sent", code == 0 && sent is [{ Body.Length: NoticeProtocol.MaxBody, Title: "Build log" }] && NoticeProtocol.Fits(sent[0]));
        sent.Clear();
        var huge = new NotifyCommand(NotifyCommandKind.Send, new NoticeMessage { Origin = "cli", Title = "x", Kind = NoticeTimes.Countdown, At = new string('9', 70_000) });
        check("One that still does not fit is not sent, and is a usage error", await NotifyCli.RunAsync(huge, sender) == NotifyCli.Usage && sent.Count == 0);
        check("That reply is told apart from an unreachable BrimDeck", NotifyCli.Report(NoticeProtocol.Reply(NoticeResults.Rejected, NotifyCli.TooLarge), "x") == NotifyCli.Usage);
        var clipped = new NoticeMessage { Title = new string('t', 1000), Body = "a\u202Eb" };
        NoticeProtocol.ClipTexts(clipped);
        check("Cutting before sending is what BrimDeck does", clipped.Title!.Length == NoticeProtocol.MaxTitle && clipped.Body == "ab");
    }

    // W8: a stop that finds nothing says so and fails; other filtered notices still succeed.
    private static void StopNotFound(Action<string, bool> check)
    {
        var stop = NotifyCommandLine.Parse(["stop", "tea"]).Message;
        check("A stop for an unknown key is refused", NotifyCli.Report(NoticeProtocol.Reply(NoticeResults.Filtered, "no-timer"), "x", stop) == NotifyCli.Refused);
        check("A notice kept quiet by the rules still succeeds", NotifyCli.Report(NoticeProtocol.Reply(NoticeResults.Filtered, "muted"), "x") == 0);
    }

    // W9: a relative -i is made full from the current folder instead of being dropped by BrimDeck.
    private static void RelativeIcons(Action<string, bool> check)
    {
        string Linux(string path) => "/home/me/" + path.TrimStart('.', '/');
        string? InWsl(string icon, bool exists = false) => NotifyCommandLine.ResolveIcon(icon, false, Linux, _ => exists);
        check("WSL: ./logo.png and art/logo.png are made full", InWsl("./logo.png") == "/home/me/logo.png" && InWsl("art/logo.png") == "/home/me/art/logo.png");
        check("WSL: an absolute path stays", InWsl("/usr/share/pixmaps/a.png") == "/usr/share/pixmaps/a.png");
        check("WSL: a bare name is a file only when it is in the current folder",
            InWsl("logo.png", exists: true) == "/home/me/logo.png" && InWsl("dialog-information") == "dialog-information" && InWsl("org.gnome.Foo") == "org.gnome.Foo");
        string? OnWindows(string icon) => NotifyCommandLine.ResolveIcon(icon, true, path => Path.GetFullPath(path, @"C:\work"), _ => false);
        check("Windows: relative paths are made full", OnWindows("logo.png") == @"C:\work\logo.png" && OnWindows(@".\art\logo.png") == @"C:\work\art\logo.png");
        check("Windows: a full path stays", OnWindows(@"D:\icons\a.ico") == @"D:\icons\a.ico");
        check("Links and empty values are left alone", OnWindows("https://x/a.png") == "https://x/a.png" && InWsl("file:///tmp/a.png") == "file:///tmp/a.png"
            && NotifyCommandLine.ResolveIcon(null, true, Path.GetFullPath, File.Exists) is null);
        check("The result is what BrimDeck accepts", NoticeIntake.IconRef(OnWindows("logo.png"), NoticeOrigin.Cli, null) is not null
            && NoticeIntake.IconRef(InWsl("./logo.png"), NoticeOrigin.Cli, "Ubuntu") is not null);
    }

    // W12: an application's expire_timeout closes the notice for it as expired, so "notify-send --wait -t" returns.
    private static void ExpireTimeout(Action<string, bool> check)
    {
        var server = new NotificationServer("1");
        var t0 = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        uint Id(ServiceOutput output) => (uint)output.Bus[0].Body[0];
        uint timed = Id(server.Handle(Notify(5000), t0));
        uint never = Id(server.Handle(Notify(0), t0));
        uint byServer = Id(server.Handle(Notify(-1), t0));
        check("Only a positive timeout expires", server.NextExpiry == t0.AddSeconds(5));
        check("Nothing expires early", server.Expire(t0.AddSeconds(4.9)).Bus.Count == 0);
        var closed = server.Expire(t0.AddSeconds(5));
        check("It closes as expired when due", closed.Bus is [{ Member: "NotificationClosed", Body: [uint id, FreedesktopNotifications.Expired] }] && id == timed);
        check("Once", server.Expire(t0.AddSeconds(60)).Bus.Count == 0 && server.NextExpiry is null && server.Dismissed(timed).Count == 0);
        check("The others stay open", server.Dismissed(never).Count == 1 && server.Dismissed(byServer).Count == 1);

        uint replaced = Id(server.Handle(Notify(3000), t0));
        server.Handle(Notify(0, replaced), t0.AddSeconds(1));
        check("A replacement without a timeout keeps it open", server.NextExpiry is null && server.Dismissed(replaced).Count == 1);
        uint closedEarly = Id(server.Handle(Notify(3000), t0));
        server.Handle(Close(closedEarly), t0);
        check("A notice the application closed no longer expires", server.NextExpiry is null && server.Expire(t0.AddSeconds(10)).Bus.Count == 0);
    }

    // W14: the pause between starts grows and starts over only after an agent kept running a while.
    private static void Backoff(Action<string, bool> check)
    {
        var backoff = new RetryBackoff();
        var waits = Enumerable.Range(0, 7).Select(_ => backoff.Next().TotalSeconds).ToList();
        check("The pause grows to two minutes", waits.SequenceEqual([5d, 10, 20, 40, 80, 120, 120]));
        backoff.Ran(TimeSpan.FromSeconds(3));
        check("An agent that reported and ended at once does not start the pauses over", backoff.Next() == RetryBackoff.Longest);
        backoff.Ran(RetryBackoff.Stable);
        check("One that kept running does", backoff.Next() == RetryBackoff.Initial);
    }

    private static DBusMessage Notify(int timeout, uint replaces = 0) => new()
    {
        Type = DBusMessageType.MethodCall, Serial = 4, Sender = ":1.7", Destination = FreedesktopNotifications.Name, Path = FreedesktopNotifications.Path,
        Interface = FreedesktopNotifications.Interface, Member = "Notify", Signature = FreedesktopNotifications.NotifySignature,
        Body = ["app", replaces, "", "Title", "Body", new List<object>(), new Dictionary<object, object>(), timeout]
    };

    private static DBusMessage Close(uint id) => new()
    {
        Type = DBusMessageType.MethodCall, Serial = 5, Sender = ":1.7", Destination = FreedesktopNotifications.Name, Path = FreedesktopNotifications.Path,
        Interface = FreedesktopNotifications.Interface, Member = "CloseNotification", Signature = "u", Body = [id]
    };

    // Writes go nowhere (or fail); reads come from readable, or wait until cancelled.
    private sealed class Duplex(byte[] readable, bool failWrite = false, bool blockRead = false) : Stream
    {
        private readonly MemoryStream _in = new(readable);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (blockRead) await Task.Delay(Timeout.Infinite, token);
            return _in.Read(buffer.Span);
        }
        public override void Write(byte[] buffer, int offset, int count) { if (failWrite) throw new IOException("broken pipe"); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            => failWrite ? ValueTask.FromException(new IOException("broken pipe")) : ValueTask.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
