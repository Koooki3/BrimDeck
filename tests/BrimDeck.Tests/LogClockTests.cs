using System.Text.Json;
using BrimDeck.Core;

// A record can carry a time later than the read that finds it: written while the read was under way, or by a clock that is ahead.
static class LogClockTests
{
    private delegate string Line(DateTimeOffset time, string id, int output = 5);
    private delegate List<TokenEntry> Reading(string home, DateTimeOffset now);

    public static void Run(string root, Action<string, bool> check)
    {
        // Noon, so no near record falls on another day.
        var now = new DateTimeOffset(DateTime.Today.AddHours(12));
        var start = new DateTimeOffset(now.Date.AddDays(-29));
        Line claude = (time, id, output) => JsonSerializer.Serialize(new { type = "assistant", timestamp = time, requestId = id,
            message = new { id, model = "claude-opus-5-5", usage = new { input_tokens = 10, output_tokens = output } } }) + "\n";
        Line kimi = (time, id, output) => JsonSerializer.Serialize(new { type = "usage.record", agentId = id, model = "kimi-code/k3.1-pro", usageScope = "turn",
            usage = new { inputOther = 10, output, inputCacheRead = 0, inputCacheCreation = 0 }, time = time.ToUnixTimeMilliseconds() }) + "\n";
        Line dsh = (time, id, output) => JsonSerializer.Serialize(new { type = "assistant/message", time = time.ToUnixTimeMilliseconds(), data = new {
            message = new { id, source = new { model = "deepseek-flash" } }, usage = new { inputTokens = 10, outputTokens = output, cacheReadTokens = 0, cacheWriteTokens = 0 } } }) + "\n";
        var logs = new LogReader();
        var kimiLogs = new KimiSessions();
        var dshLogs = new DshSessions();
        Reading readClaude = (home, at) => logs.Read(ProviderId.Claude, home, at).Entries;
        (string Name, string File, Line Line, Reading Read)[] sources =
        [
            ("Claude", Path.Combine("projects", "p", "session.jsonl"), claude, readClaude),
            ("Kimi", Path.Combine("sessions", "w", "s", "agents", "main", "wire.jsonl"), kimi,
                (home, at) => { var snapshot = new ProviderSnapshot(ProviderId.Kimi); kimiLogs.Read(snapshot, home, start, at); return snapshot.Entries; }),
            ("DSH", Path.Combine("sessions", "p", "s", "session.v1.jsonl"), dsh,
                (home, at) => { var snapshot = new ProviderSnapshot(ProviderId.Dsh); dshLogs.Read(snapshot, home, start, at); return snapshot.Entries; }),
        ];
        foreach (var (name, path, line, read) in sources)
        {
            var home = Path.Combine(root, "log-clock-" + name);
            var file = Path.Combine(home, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, line(now.AddMinutes(-9), "a") + line(now.AddSeconds(2), "b") + line(now.AddSeconds(60), "c") + line(now.AddYears(50), "d"));
            var first = read(home, now);
            check($"{name}: records ahead of the read wait, and the merged list is kept meanwhile", first.Count == 1 && ReferenceEquals(first, read(home, now.AddSeconds(1))));
            check($"{name}: each record joins once its time has come, with no file changed", read(home, now.AddSeconds(2)).Count == 2 && read(home, now.AddSeconds(61)).Count == 3);
            check($"{name}: a record decades ahead does not make every read merge again",
                read(home, now.AddSeconds(120)) is { Count: > 0 } kept && !kept.Exists(entry => entry.Time > now.AddSeconds(120)) && ReferenceEquals(kept, read(home, now.AddSeconds(180))));
        }

        var replyHome = Path.Combine(root, "log-clock-reply");
        var reply = Path.Combine(replyHome, "projects", "p", "session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(reply)!);
        File.WriteAllText(reply, claude(now.AddMinutes(-1), "r", 5));
        bool counted = readClaude(replyHome, now) is [{ Output: 5 }];
        File.AppendAllText(reply, claude(now.AddSeconds(2), "r", 50));
        readClaude(replyHome, now);
        check("Claude: a reply whose fuller line is ahead of the read is counted in full once that time has come",
            counted && readClaude(replyHome, now.AddSeconds(3)) is [{ Output: 50 }]);
    }
}
