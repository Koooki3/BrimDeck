using System.Diagnostics;
using System.Text;
using BrimDeck.Protocol;
using BrimDeck.Protocol.DBus;

// The second round of audit fixes for brim-notify, the WSL agent and the D-Bus reader (group "wsl2").
internal static class AuditWsl2Tests
{
    public static async Task Run(Action<string, bool> check)
    {
        CmdEscaping(check);
        CmdThroughCmd(check);
        ProgramSearch(check);
        await NotFoundIsReported(check);
        AgentRequests(check);
        await OversizedMessages(check);
        await BoundedErrorLines(check);
    }

    // C-L5: BrimDeck's actions and dismissals name the session, and an agent acts only on its own run's.
    private static void AgentRequests(Action<string, bool> check)
    {
        var action = NoticeProtocol.ToAgent(NoticeTypes.Action, "wsl:Ubuntu-24.04:server-1a2b3c:12", "default");
        check("An action carries the notice's session and id", action is { Type: NoticeTypes.Action, Session: "server-1a2b3c", NativeId: "12", ActionKey: "default" });
        check("A dismissal does too", NoticeProtocol.ToAgent(NoticeTypes.Dismissed, "wsl:Debian:monitor-ff:3") is { Session: "monitor-ff", NativeId: "3", ActionKey: null });
        check("A key without a session sends none", NoticeProtocol.ToAgent(NoticeTypes.Dismissed, "wsl:Debian::3") is { Session: null, NativeId: "3" });
        check("A key without a D-Bus id gives no request",
            NoticeProtocol.ToAgent(NoticeTypes.Action, "wsl:Debian:server-a:abc") is null && NoticeProtocol.ToAgent(NoticeTypes.Action, "windows:App") is null
            && NoticeProtocol.ToAgent(NoticeTypes.Action, "wsl:7") is null && NoticeProtocol.ToAgent(NoticeTypes.Action, "cli:x:y:1") is null);
        var json = NoticeProtocol.Deserialize(NoticeProtocol.Serialize(action!));
        check("The session travels on the wire", json is { Session: "server-1a2b3c", NativeId: "12" });
        var server = new NotificationServer("1", session: "server-new");
        uint id = (uint)server.Handle(Notify()).Bus[0].Body[0];
        check("The current run's request is taken", server.Addresses(new NoticeMessage { Type = NoticeTypes.Action, Session = "server-new", NativeId = "1" }));
        check("An earlier run's request for the same id is not", !server.Addresses(new NoticeMessage { Type = NoticeTypes.Action, Session = "server-old", NativeId = id.ToString() }));
        check("A request from a BrimDeck that sends no session is taken as before", server.Addresses(new NoticeMessage { Type = NoticeTypes.Dismissed, NativeId = "1" }));
    }

    private static DBusMessage Notify(object[]? actions = null, Dictionary<object, object>? hints = null, string body = "Body", uint serial = 4) => new()
    {
        Type = DBusMessageType.MethodCall, Serial = serial, Sender = ":1.7", Destination = FreedesktopNotifications.Name, Path = FreedesktopNotifications.Path,
        Interface = FreedesktopNotifications.Interface, Member = "Notify", Signature = FreedesktopNotifications.NotifySignature,
        Body = ["app", 0u, "", "Title", body, (actions ?? []).ToList(), hints ?? new Dictionary<object, object>(), 5000]
    };

    private static object Image(int bytes) => new DBusVariant("(iiibiiay)", new object[] { 512, 512, 2048, true, 8, 4, new byte[bytes] });

    // Reads one message the way the connection does when it is larger than MaxMessage; the stream holds a second message after it.
    private static async Task<(byte[]? Slim, DBusMessage? Header, bool NextIntact)> ReadOversized(DBusMessage message, int limit)
    {
        var bytes = message.Encode();
        var next = DBusMessage.Signal("/x", "x.y", "After", "s", "next").Encode();
        using var stream = new MemoryStream([.. bytes, .. next]);
        var start = new byte[16];
        stream.ReadExactly(start);
        var (slim, header) = await DBusOversized.ReadAsync(stream, start, DBusMessage.TotalLength(start), limit);
        var rest = new byte[next.Length];
        stream.ReadExactly(rest);
        return (slim, header, DBusMessage.Decode(rest) is { Member: "After", Body: ["next"] } && stream.Position == stream.Length);
    }

    // C-L6: a notification too large because of its image keeps everything but the image; one too large without it is
    // answered with an error at once.
    private static async Task OversizedMessages(Action<string, bool> check)
    {
        var hints = new Dictionary<object, object> { ["image-data"] = Image(4 * 1024 * 1024 + 3), ["urgency"] = new DBusVariant("y", (byte)2), ["category"] = new DBusVariant("s", "im") };
        var big = Notify(["default", "Open"], hints);
        check("A notice with a 4 MB image is larger than a connection holds", big.Encode().Length > DBusConnection.MaxMessage);
        var (slim, header, intact) = await ReadOversized(big, DBusConnection.MaxMessage);
        check("It is read without the image", slim is not null && slim.Length < 64 * 1024 && header is { Member: "Notify", Serial: 4, Sender: ":1.7" } && intact);
        var message = DBusMessage.Decode(slim!);
        var kept = (Dictionary<object, object>)message.Body[6];
        check("Every other value is kept, aligned as before",
            message.Body is ["app", 0u, "", "Title", "Body", List<object> { Count: 2 }, _, 5000] && kept["urgency"] is DBusVariant { Value: (byte)2 } && kept["category"] is DBusVariant { Value: "im" });
        check("The image keeps only its length's remainder modulo 8", kept["image-data"] is DBusVariant { Value: object[] { Length: 7 } fields } && fields[6] is byte[] { Length: 3 });
        var notice = new NotificationServer("1").Handle(message);
        check("The notice still reaches BrimDeck and the sender gets its id", notice.ToBrimDeck is [{ Title: "Title" }] && notice.Bus is [{ Type: DBusMessageType.MethodReturn, ReplySerial: 4 }]);

        // Several large arrays nested in arrays and variants: each enclosing length is corrected.
        var nested = new Dictionary<object, object>
        {
            ["a"] = new DBusVariant("aay", new List<object> { new byte[100_001], new byte[5], new byte[200_002] }),
            ["b"] = new DBusVariant("(yay)", new object[] { (byte)1, new byte[70_000] }), ["c"] = new DBusVariant("x", 42L)
        };
        var (nestedSlim, _, nestedIntact) = await ReadOversized(Notify(hints: nested), 64 * 1024);
        var nestedKept = nestedSlim is null ? null : (Dictionary<object, object>)DBusMessage.Decode(nestedSlim).Body[6];
        var arrays = (nestedKept?["a"] as DBusVariant)?.Value as List<object>;
        var pair = (nestedKept?["b"] as DBusVariant)?.Value as object[];
        check("Nested large arrays are cut and their containers stay valid",
            nestedIntact && arrays is { Count: 3 } && arrays.Cast<byte[]>().Select(bytes => bytes.Length).SequenceEqual([1, 5, 2])
            && pair is { Length: 2 } && pair[0] is (byte)1 && pair[1] is byte[] { Length: 0 } && nestedKept!["c"] is DBusVariant { Value: 42L });

        var huge = Notify(body: new string('x', 3 * 1024 * 1024), serial: 9);
        var (none, hugeHeader, hugeIntact) = await ReadOversized(huge, DBusConnection.MaxMessage);
        check("A message too large without its byte arrays comes back as its header", none is null && hugeHeader is { Serial: 9, Sender: ":1.7", Body: [] } && hugeIntact);
        hugeHeader!.Oversized = true;
        var refused = new NotificationServer("1").Handle(hugeHeader);
        check("The service answers it with an error at once and passes nothing on",
            refused.ToBrimDeck.Count == 0 && refused.Bus is [{ Type: DBusMessageType.Error, ErrorName: DBusConnection.LimitsExceeded, ReplySerial: 9, Destination: ":1.7" }]);
        hugeHeader.Flags = DBusMessage.NoReplyExpected;
        check("A call that wants no reply gets none", new NotificationServer("1").Handle(hugeHeader).Bus.Count == 0);
        var monitor = new NotificationMonitor { Owner = ":1.3" };
        check("A monitor ignores a header it could not read past", monitor.Observe(hugeHeader, DateTimeOffset.UnixEpoch).Count == 0 && monitor.NextDue is null);

        var truncated = big.Encode()[..(DBusConnection.MaxMessage + 100)];
        var cut = new MemoryStream(truncated);
        var first = new byte[16];
        cut.ReadExactly(first);
        bool ended = false;
        try { await DBusOversized.ReadAsync(cut, first, DBusMessage.TotalLength(first), DBusConnection.MaxMessage); }
        catch (EndOfStreamException) { ended = true; }
        check("A stream that ends inside the message ends the reading", ended);
    }

    // S8: a line without end is held to the bound; lines are passed on as they end.
    private static async Task BoundedErrorLines(Action<string, bool> check)
    {
        var lines = new List<string>();
        var text = "first\r\n\n  \nsecond 中文\n" + new string('x', 5_000_000);
        await BoundedLines.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), 200, lines.Add);
        check("Lines are passed on without blanks, and an endless one is cut to the bound",
            lines.Count == 3 && lines[0] == "first" && lines[1] == "second 中文" && lines[2] == new string('x', 200));
        var failing = new FailingStream();
        await BoundedLines.ReadAsync(failing, 200, lines.Add);
        check("A stream that fails ends the reading quietly", true);
    }

    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("gone");
    }

    private const string Prefix = "/e:on /d /v:off /s /c \"";

    // C-M1: an argument is quoted only when cmd needs it to be, as Rust's standard library does for batch files.
    private static void CmdEscaping(Action<string, bool> check)
    {
        string? Line(params string[] arguments) => WindowsPrograms.CmdArguments(@"C:\s\build.bat", arguments, false);
        string Expected(string arguments) => Prefix + "\"C:\\s\\build.bat\"" + arguments + "\"";
        check("A plain argument reaches the batch file unquoted", Line("release") == Expected(" release"));
        check("Plain paths, options and numbers stay unquoted", Line(@"C:\x\y.txt", "--flag=1", "-v", "#$*+./:?@_") == Expected(@" C:\x\y.txt ""--flag=1"" -v #$*+./:?@_"));
        check("An argument with a space is quoted", Line("a b") == Expected(" \"a b\""));
        check("cmd's special characters are quoted", Line("&|^<>()", "a,b;c") == Expected(" \"&|^<>()\" \"a,b;c\""));
        check("A quote inside is doubled", Line("say \"hi\"") == Expected(" \"say \"\"hi\"\"\""));
        check("Backslashes before a quote are doubled with it", Line("a\\\"b") == Expected(" \"a\\\\\"\"b\""));
        check("% cannot start a variable", Line("%PATH%") == Expected(" \"%%cd:~,%PATH%%cd:~,%\""));
        check("! is quoted and left to /v:off", Line("!x!") == Expected(" \"!x!\""));
        check("An empty argument is kept", Line("") == Expected(" \"\""));
        check("A trailing backslash is quoted and doubled", Line(@"C:\dir\") == Expected(@" ""C:\dir\\"""));
        check("Letters beyond ASCII stay as they are", Line("中文", "café") == Expected(" 中文 café"));
        check("A line break or NUL is still refused", Line("a\nb") is null && Line("a\rb") is null && Line("a\0b") is null);
        check("A program path with a quote or a trailing backslash is refused",
            WindowsPrograms.CmdArguments("C:\\a\"b.bat", [], false) is null && WindowsPrograms.CmdArguments(@"C:\a\", [], false) is null);
        check("A builtin's name stays bare, and so does a plain argument", WindowsPrograms.CmdArguments("echo", ["hello"], true) == Prefix + "echo hello\"");
    }

    // C-M1, end to end: what cmd actually hands a batch file and its own echo (Windows only).
    private static void CmdThroughCmd(Action<string, bool> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeck-wsl2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // Prints its arguments without running anything in them: %1 inside brackets, and %~1 through a variable read
            // with delayed expansion, which does not expand what it reads.
            var batch = Path.Combine(folder, "args.bat");
            File.WriteAllText(batch, string.Join("\r\n",
                "@echo off", "chcp 65001 >nul", "echo 1=[%1]", "echo 2=[%2]", "set \"v=%~1\"", "setlocal enabledelayedexpansion", "echo t=[!v!]", "endlocal", ""), new UTF8Encoding(false));
            // The usual way a build script reads its first argument.
            var release = Path.Combine(folder, "release.bat");
            File.WriteAllText(release, string.Join("\r\n", "@echo off", "if \"%1\"==\"release\" echo matched", "set NAME=%1", "echo name=%NAME%", "exit /b 7", ""));
            var lines = Run(release, false, "release");
            check("if \"%1\"==\"release\" matches again, and set takes no quotes", lines.SequenceEqual(["matched", "name=release"]));
            check("A batch file's own exit code comes back", Run(release, false, out int code, "x") is not null && code == 7);
            lines = Run(batch, false, "release");
            check("A plain argument arrives unquoted", lines.Contains("1=[release]") && lines.Contains("2=[]"));
            lines = Run(batch, false, "a b", "c");
            check("An argument with a space is one argument", lines.Contains("1=[\"a b\"]") && lines.Contains("t=[a b]") && lines.Contains("2=[c]"));
            lines = Run(batch, false, "Release %s");
            check("npm's %s is passed as it is", lines.Contains("t=[Release %s]"));
            lines = Run(batch, false, "%PATH%", "%~1%");
            check("A variable is not expanded", lines.Contains("t=[%PATH%]") && lines.Contains("2=[\"%~1%\"]"));
            lines = Run(batch, false, "!OS!");
            check("! is passed as it is", lines.Contains("t=[!OS!]"));
            lines = Run(batch, false, "&|^<>()");
            check("cmd's special characters arrive literally", lines.Contains("t=[&|^<>()]"));
            lines = Run(batch, false, "x\" & echo INJECTED & \"", "a & echo INJECTED", "(echo INJECTED)", "^& echo INJECTED");
            check("Nothing in an argument runs as a command", !lines.Any(line => line.StartsWith("INJECTED", StringComparison.Ordinal)) && lines.Contains("2=[\"a & echo INJECTED\"]"));
            lines = Run(batch, false, "中文 ✓");
            check("Letters beyond ASCII arrive intact", lines.Contains("t=[中文 ✓]"));
            lines = Run("echo", true, "hello");
            check("echo hello prints hello, not \"hello\"", lines.SequenceEqual(["hello"]));
            lines = Run("echo", true, "a & echo INJECTED");
            check("A builtin's arguments do not run either", lines.SequenceEqual(["\"a & echo INJECTED\""]));
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    private static List<string> Run(string program, bool builtin, params string[] arguments) => Run(program, builtin, out _, arguments) ?? [];

    private static List<string>? Run(string program, bool builtin, out int code, params string[] arguments)
    {
        code = -1;
        var line = WindowsPrograms.CmdArguments(program, arguments, builtin);
        if (line is null) return null;
        // Its own hidden console: chcp must not change the test runner's.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), line)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        code = process.ExitCode;
        return output.Split('\n').Select(text => text.TrimEnd('\r', ' ')).Where(text => text.Length > 0).ToList();
    }

    // S5 and C-M1: BrimDeck's folder is never searched; a script with its own extension is found by that name.
    private static void ProgramSearch(Action<string, bool> check)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\BrimDeck\git.exe", @"C:\tools\git.exe", @"C:\scripts\tool.py", @"C:\scripts\a.ps1" };
        const string pathExt = ".COM;.EXE;.BAT;.CMD";
        // Found with the extension as PATHEXT spells it; Windows paths compare without case.
        string? Find(string name, string path, string? skip = null) => WindowsPrograms.Find(name, path, pathExt, files.Contains, skip) is { } found ? files.First(file => file.Equals(found, StringComparison.OrdinalIgnoreCase)) : null;
        check("BrimDeck's folder in PATH is skipped", Find("git", @"C:\BrimDeck;C:\tools", @"C:\BrimDeck\") == @"C:\tools\git.exe"
            && Find("git", @"""C:\BrimDeck\"";C:\tools", @"C:\brimdeck") == @"C:\tools\git.exe");
        check("Without it, PATH's first match wins", Find("git", @"C:\BrimDeck;C:\tools") == @"C:\BrimDeck\git.exe");
        check("A script with its own extension is found in PATH", Find("tool.py", @"C:\scripts") == @"C:\scripts\tool.py" && Find("a.ps1", @"C:\scripts") == @"C:\scripts\a.ps1");
        check("A script is never run directly", !WindowsPrograms.RunsDirectly(@"C:\scripts\tool.py"));
        check("A name without an extension is not found as a bare file", WindowsPrograms.Find("tool", @"C:\scripts", pathExt, name => name == @"C:\scripts\tool") is null);
    }

    // C-M1: a program that is not found exits with 9009 and is still reported to BrimDeck as a finished run.
    private static async Task NotFoundIsReported(Action<string, bool> check)
    {
        var sent = new List<NoticeMessage>();
        var sender = new NotifySender("1",
            message => { sent.Add(message); return Task.FromResult<NoticeMessage?>(NoticeProtocol.Reply(NoticeResults.Accepted)); },
            _ => (null, WindowsPrograms.NotFound, true), "x", "x");
        int code = await NotifyCli.RunAsync(NotifyCommandLine.Parse(["run", "--", "nope", "a"]), sender);
        check("A program not found exits with 9009 and reports its run", code == 9009 && sent is [{ ExitCode: 9009 }]);
        sent.Clear();
        var quiet = sender with { Start = _ => (null, NotifyCli.Usage, false) };
        check("A refused start reports nothing", await NotifyCli.RunAsync(NotifyCommandLine.Parse(["run", "--", "x"]), quiet) == NotifyCli.Usage && sent.Count == 0);
    }
}
