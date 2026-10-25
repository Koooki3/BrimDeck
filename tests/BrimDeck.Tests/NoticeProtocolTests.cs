using System.Buffers.Binary;
using BrimDeck.Protocol;

internal static class NoticeProtocolTests
{
    public static async Task Run(Action<string, bool> check)
    {
        await Frames(check);
        Validation(check);
        CommandLine(check);
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

    private static async Task<bool> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; } catch (T) { return true; }
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; } catch (T) { return true; }
    }
}
