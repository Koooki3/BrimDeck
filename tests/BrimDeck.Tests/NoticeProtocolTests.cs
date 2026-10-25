using BrimDeck.Protocol;

internal static class NoticeProtocolTests
{
    public static Task Run(Action<string, bool> check)
    {
        Validation(check);
        return Task.CompletedTask;
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

    private static async Task<bool> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; } catch (T) { return true; }
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; } catch (T) { return true; }
    }
}
