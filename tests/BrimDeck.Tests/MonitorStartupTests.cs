using BrimDeck.Core;

// The pieces that keep the monitor's start light: swallowed failures noted once, and the cache of program names kept bounded.
static class MonitorStartupTests
{
    public static void Run(Action<string, bool> check)
    {
        double clock = 0;
        var lines = new List<string>();
        var notes = new FailureNotes(() => clock, lines.Add, 600);
        var error = new InvalidOperationException("boom");
        check("Failure notes: the first failure of a kind is written", notes.Note("battery", error) && lines.Count == 1 && lines[0].StartsWith("battery: InvalidOperationException: boom"));
        clock = 5;
        check("Failure notes: the same kind from the same source is not written again within the quiet time", !notes.Note("battery", new InvalidOperationException("again")) && lines.Count == 1);
        check("Failure notes: another kind, or another source, is written", notes.Note("battery", new System.IO.IOException("io")) && notes.Note("storage", error) && lines.Count == 3);
        clock = 700;
        check("Failure notes: after the quiet time the kind is written again", notes.Note("battery", error) && lines.Count == 4);
        for (int i = 0; i < 200; i++) notes.Note("flood", "kind " + i, "x");
        check("Failure notes: the kinds kept are bounded", lines.Count(line => line.StartsWith("flood")) <= 64);

        var cache = Enumerable.Range(0, 300).ToDictionary(i => "app" + i + ".exe", i => i, StringComparer.OrdinalIgnoreCase);
        check("Name cache: under the limit nothing is dropped", NameCache.Trim(cache, ["APP1.EXE"], 300) == 0 && cache.Count == 300);
        int dropped = NameCache.Trim(cache, ["APP1.EXE", "app2.exe"], 256);
        check("Name cache: past the limit only the programs running are kept", dropped == 298 && cache.Count == 2 && cache.ContainsKey("app1.exe") && cache.ContainsKey("app2.exe"));
    }
}
