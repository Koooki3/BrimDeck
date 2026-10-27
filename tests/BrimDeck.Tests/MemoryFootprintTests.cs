using System.Text.Json;
using BrimDeck.Core;

// What the usage statistics keep and redo between refreshes: a month of records stays cached in a compact form, and a refresh
// that finds no file changed hands back the statistics it already has instead of gathering and sorting them again.
internal static class MemoryFootprintTests
{
    public static void Run(Action<string, bool> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "BrimDeck-Tests-memory-" + Guid.NewGuid().ToString("N"));
        try { Statistics(check, root); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static void Statistics(Action<string, bool> check, string root)
    {
        var now = DateTimeOffset.Now;
        string Codex(DateTimeOffset time, long total, long output) => JsonSerializer.Serialize(new
        {
            timestamp = time, type = "event_msg", payload = new { type = "token_count", info = new
            { total_token_usage = new { total_tokens = total, padding = new string('x', 400) }, last_token_usage = new { input_tokens = 100, cached_input_tokens = 50, output_tokens = output } } }
        });
        const string Context = """{"type":"turn_context","payload":{"model":"gpt-6-astra"}}""";
        var codexRoot = Path.Combine(root, "codex"); var sessions = Path.Combine(codexRoot, "sessions"); Directory.CreateDirectory(sessions);
        var file = Path.Combine(sessions, "session.jsonl");
        File.WriteAllLines(file, [Context, Codex(now.AddMinutes(-5), 120, 20), Context, Codex(now.AddMinutes(-3), 250, 30)]);
        File.WriteAllLines(Path.Combine(sessions, "fork.jsonl"), [Context, Codex(now.AddMinutes(-5), 120, 20)]);
        var logs = new LogReader();
        var first = logs.Read(ProviderId.Codex, codexRoot, now);
        check("Codex records are known by a short key, whatever the size of their counters",
            first.Entries.Count == 2 && first.Entries.All(e => e.Key.StartsWith("codex:", StringComparison.Ordinal) && e.Key.Length == "codex:".Length + 32));
        check("A fork's copy of a record still counts once", first.Entries.Count == 2 && first.Entries.Sum(e => e.Output) == 50);
        check("Records share one string per model", ReferenceEquals(first.Entries[0].Model, first.Entries[1].Model));

        var again = logs.Read(ProviderId.Codex, codexRoot, now.AddMinutes(1));
        check("A refresh with no file changed hands back the same statistics", ReferenceEquals(again.Entries, first.Entries));
        File.AppendAllText(file, Codex(now.AddMinutes(-1), 370, 40) + Environment.NewLine);
        var appended = logs.Read(ProviderId.Codex, codexRoot, now.AddMinutes(1));
        check("A changed file gathers the statistics again", !ReferenceEquals(appended.Entries, first.Entries) && appended.Entries.Count == 3);
        File.Delete(Path.Combine(sessions, "fork.jsonl"));
        var removed = logs.Read(ProviderId.Codex, codexRoot, now.AddMinutes(1));
        check("A removed file gathers them again too", !ReferenceEquals(removed.Entries, appended.Entries) && removed.Entries.Count == 3);
        check("An earlier start date gathers them again", logs.Read(ProviderId.Codex, codexRoot, now.AddMinutes(1), now.LocalDateTime.Date.AddDays(-60)).Entries != removed.Entries);

        // A record stamped ahead of the clock is left out until its time, then joins without any file changing.
        var claudeRoot = Path.Combine(root, "claude"); var projects = Path.Combine(claudeRoot, "projects"); Directory.CreateDirectory(projects);
        string Claude(string id, DateTimeOffset time) => JsonSerializer.Serialize(new { timestamp = time, type = "assistant",
            message = new { id, model = "claude-sonnet-4-6", usage = new { input_tokens = 10, output_tokens = 5 } } });
        File.WriteAllLines(Path.Combine(projects, "session.jsonl"), [Claude("now", now.AddMinutes(-1)), Claude("ahead", now.AddMinutes(2))]);
        var claude = new LogReader();
        check("A record ahead of the clock is left out", claude.Read(ProviderId.Claude, claudeRoot, now).Entries.Count == 1);
        check("It joins once its time has come, with no file changed", claude.Read(ProviderId.Claude, claudeRoot, now.AddMinutes(3)).Entries.Count == 2);
    }
}
