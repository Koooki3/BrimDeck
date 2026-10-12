using BrimDeck.Core;

internal static class AntigravityUsageTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        var now = DateTimeOffset.Now;
        var desktopFolder = Path.Combine(root, "antigravity", "conversations");
        var cliFolder = Path.Combine(root, "antigravity-cli", "conversations");
        Directory.CreateDirectory(desktopFolder); Directory.CreateDirectory(cliFolder);
        var desktopDb = Path.Combine(desktopFolder, "desktop.db"); var cliDb = Path.Combine(cliFolder, "cli.db");
        File.WriteAllText(desktopDb, ""); File.WriteAllText(cliDb, "");
        var fake = new Databases();
        // A request whose cache read exceeds its input, repeated in a second step, plus one CLI request.
        fake.Steps[desktopDb] = [Step(now.AddHours(-1), "m1", input: 5000, output: 60, cacheRead: 12000), Step(now.AddHours(-1), "m1", 5000, 60, 12000), Step(now.AddDays(-40), "old", 5, 5, 0)];
        fake.Models[desktopDb] = [Generation("m1", "gemini-3.8-flash")];
        fake.Steps[cliDb] = [Step(now.AddMinutes(-5), "c1", 12000, 150, 0)];
        fake.Models[cliDb] = [Generation("c1", "gemini-3.8-flash")];

        var snapshot = new ProviderSnapshot(ProviderId.Antigravity);
        new AntigravityUsage().Read(snapshot, fake, [desktopFolder, cliFolder], now.AddDays(-29), now);
        check("Antigravity desktop and CLI conversations are both counted, each message once",
            snapshot.UsageAvailable && snapshot.Entries.Select(e => e.Key).OrderBy(k => k).SequenceEqual(["ag:c1", "ag:m1"]));
        var desktop = snapshot.Entries.Single(e => e.Key == "ag:m1");
        check("Antigravity cache reads are counted in addition to input and use the generation's model name",
            desktop is { Input: 5000, CacheRead: 12000, Output: 60, Model: "gemini-3.8-flash" });

        var missing = new ProviderSnapshot(ProviderId.Antigravity);
        new AntigravityUsage().Read(missing, fake, [Path.Combine(root, "none")], now.AddDays(-29), now);
        check("Missing Antigravity conversation folders are unavailable, not zero", !missing.UsageAvailable);

        PrefixPricing(root, now, check);
    }

    // Antigravity adds words to model names ("claude-opus-5-5-high", "gemini-3.8-flash-control"), while the online catalog lists
    // only base models with dotted Claude versions. Such a name takes the price of its longest catalog prefix, for Antigravity only.
    private static void PrefixPricing(string root, DateTimeOffset now, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "antigravity-prefix-prices");
        Directory.CreateDirectory(folder);
        // Distinct prices for every model, so a wrong match changes the result.
        TokenPrices Price(int n) => new(n * .000001m, n * .000005m, n * .0000001m, null, null);
        var catalog = new (string Id, int N)[]
        {
            ("anthropic/claude-opus-5.5", 5), ("anthropic/claude-opus-5.5:batch", 1), ("anthropic/claude-sonnet-5.5", 3),
            ("anthropic/claude-opus-4.6", 6), ("anthropic/claude-sonnet-4.6", 4), ("anthropic/claude-opus-4", 13), ("anthropic/claude-opus-4.1", 14),
            ("anthropic/claude-opus-5", 21), ("anthropic/claude-sonnet-4", 22),
            ("google/gemini-3.8-flash", 2), ("google/gemini-3.5-flash", 7), ("google/gemini-3.5-flash-lite", 8),
            ("google/gemini-3.1-pro-preview", 9), ("google/gemini-3.1-pro-preview-customtools", 10),
            ("openai/gpt-oss-120b", 11), ("openai/gpt-oss-20b", 12), ("openai/gpt-5", 15), ("openai/gpt-5-mini", 16), ("openai/gpt-5.1-codex-max", 17)
        };
        File.WriteAllText(Path.Combine(folder, "model-prices.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            updatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            models = catalog.Select(x => new ModelPrice(x.Id, Price(x.N), [])).ToArray()
        }));
        using var pricing = new Pricing(folder);
        var entry = new TokenEntry("ag:prefix", now, "", 1000, 2000, 0, 0, 100);
        decimal Cost(int n) { var p = Price(n); return 1000 * p.Input + 100 * p.Output + 2000 * p.CacheRead!.Value; }
        bool All(int n, params string[] names) => names.All(name => pricing.Estimate(entry with { Model = name }) == Cost(n));
        bool None(params string[] names) => names.All(name => pricing.Estimate(entry with { Model = name }) is null);

        check("Antigravity Claude Opus 5.5 names use the base model's price",
            All(5, "claude-opus-5-5", "claude-opus-5-5-low", "claude-opus-5-5-medium", "claude-opus-5-5-high", "claude-opus-5-5-max", "claude-opus-5-5-thinking",
                "Claude-Opus-5-5-HIGH", "claude-opus-5-5-thinking-high", "claude-opus-5-5-max-thinking", "claude-opus-5.5-high", "anthropic/claude-opus-5.5-thinking"));
        check("Antigravity Claude Sonnet 5.5 names use the base model's price",
            All(3, "claude-sonnet-5-5-low", "claude-sonnet-5-5-medium", "claude-sonnet-5-5-high", "claude-sonnet-5-5-max", "claude-sonnet-5-5-medium-thinking"));
        check("Antigravity Claude Opus 4.6 names match dotted and dashed versions",
            All(6, "claude-opus-4-6", "claude-opus-4-6-thinking", "claude-opus-4-6-high", "claude-opus-4-6-thinking-high", "claude-opus-4-6-high-thinking",
                "claude-opus-4-6-thinking-max", "claude-opus-4.6-thinking", "anthropic/claude-opus-4.6-thinking"));
        check("Antigravity Claude Sonnet 4.6 names use the base model's price",
            All(4, "claude-sonnet-4-6", "claude-sonnet-4-6-thinking", "claude-sonnet-4-6-high", "claude-sonnet-4-6-high-thinking", "claude-sonnet-4-6-thinking-low"));
        check("A Claude version is never cut short", All(13, "claude-opus-4-thinking") && All(14, "claude-opus-4-1-high")
            && None("claude-opus-4-7-thinking", "claude-opus-4-6x", "claude-opus-4-6x-high", "claude-opus-9-high"));
        // Antigravity writes "5-5" for 5.5, so "claude-opus-5" is a dash-boundary prefix of "claude-opus-5-5-high"; the whole
        // version must match, and a version missing from the catalog never falls back to the major version's price.
        check("A dashed Claude version matches as a whole, never the major version alone",
            All(5, "claude-opus-5-5-high", "claude-opus-5-5") && All(21, "claude-opus-5", "claude-opus-5-high", "claude-opus-5-thinking-max")
            && All(6, "claude-opus-4-6", "claude-opus-4-6-high") && All(13, "claude-opus-4", "claude-opus-4-high")
            && All(4, "claude-sonnet-4-6", "claude-sonnet-4-6-thinking") && All(22, "claude-sonnet-4", "claude-sonnet-4-thinking")
            && None("claude-opus-5-7-high", "claude-opus-5-7", "claude-sonnet-4-7-thinking", "claude-sonnet-4-5-high"));
        check("Antigravity Gemini Flash names use the base model's price",
            All(2, "gemini-3.8-flash-control", "gemini-3.8-flash-low", "gemini-3.8-flash-medium", "gemini-3.8-flash-high") && All(7, "gemini-3.5-flash-low"));
        check("The longest catalog prefix wins", All(8, "gemini-3.5-flash-lite", "gemini-3.5-flash-lite-low", "gemini-3.5-flash-lite-control")
            && All(10, "gemini-3.1-pro-preview-customtools", "gemini-3.1-pro-preview-customtools-high") && All(16, "gpt-5-mini-high"));
        check("Antigravity Gemini 3.1 Pro uses the preview catalog price", All(9, "gemini-3.1-pro", "gemini-3.1-pro-low", "gemini-3.1-pro-high", "gemini-3.1-pro-preview-high"));
        check("Antigravity GPT-OSS names use the base model's price",
            All(11, "gpt-oss-120b-low", "gpt-oss-120b-medium", "gpt-oss-120b-high") && All(12, "gpt-oss-20b-medium"));
        check("Words that name no distinct catalog model are ignored",
            All(5, "claude-opus-5-5-thinking-ultra", "claude-opus-5-5-highlight") && All(2, "gemini-3.8-flash-max") && All(15, "gpt-5-high"));
        // Tier words come from the catalog: "lite", "pro", "preview" and "customtools" for Gemini, "mini", "codex" and "max"
        // for GPT; none for Claude or GPT-OSS, so "max" still drops from "gpt-oss-120b-max".
        check("A tier word of the same family in the catalog keeps a name unpriced",
            None("gemini-3.8-flash-lite", "gemini-3.8-flash-lite-high", "gemini-3.8-flash-preview", "gemini-3.8-flash-high-lite", "gpt-5-max", "gpt-5-codex-high")
            && All(11, "gpt-oss-120b-max"));
        check("Too generic or unknown names stay unpriced",
            None("claude-opus-thinking", "claude-opus-high", "gemini-control", "gpt-oss-medium", "opus-5-5-high", "gemini-3.8-pro-low", "gemini-3.1-flash-high",
                "gemini-3.8-flash:free-high", "other/gemini-3.8-flash-high", "unknown-model-high"));
        check("Prefix matching is confined to Antigravity",
            new[] { "claude-opus-5-5-high", "claude-opus-4-6-thinking", "gemini-3.8-flash-control", "gemini-3.1-pro-low", "gpt-oss-120b-medium" }
                .All(name => pricing.Estimate(entry with { Key = "claude:prefix", Model = name }) is null));
        check("Prefix matching does not change catalog lookup",
            new[] { "claude-opus-5-5-high", "gemini-3.8-flash-control", "gemini-3.1-pro", "gpt-oss-120b-medium" }.All(name => pricing.Find(name) is null));
        check("An exact manual price wins over the prefix match",
            pricing.TrySetManual("gemini-3.8-flash-control", Price(20), false, out _) && All(20, "gemini-3.8-flash-control") && All(2, "gemini-3.8-flash-high"));
    }

    // CortexStepMetadata { 1: Timestamp { 1: seconds }, 9: ModelUsageStats { 2 input, 3 output, 5 cache read, 7 message id } }
    private static string?[] Step(DateTimeOffset time, string id, long input, long output, long cacheRead) =>
        [Convert.ToHexString([.. Message(1, Varint(1, (ulong)time.ToUnixTimeSeconds())),
            .. Message(9, [.. Varint(2, (ulong)input), .. Varint(3, (ulong)output), .. Varint(5, (ulong)cacheRead), .. Message(7, System.Text.Encoding.UTF8.GetBytes(id))])])];

    // gen_metadata { 1: ChatModelMetadata { 4: ModelUsageStats { 7 message id }, 19: response_model } }
    private static string?[] Generation(string id, string model) =>
        [Convert.ToHexString(Message(1, [.. Message(4, Message(7, System.Text.Encoding.UTF8.GetBytes(id))), .. Message(19, System.Text.Encoding.UTF8.GetBytes(model))]))];

    private static byte[] Varint(int field, ulong value) => [.. Raw((ulong)(field << 3)), .. Raw(value)];
    private static byte[] Message(int field, byte[] data) => [.. Raw((ulong)(field << 3 | 2)), .. Raw((ulong)data.Length), .. data];
    private static byte[] Raw(ulong value)
    {
        var bytes = new List<byte>();
        do { var b = (byte)(value & 0x7F); value >>= 7; bytes.Add(value == 0 ? b : (byte)(b | 0x80)); } while (value != 0);
        return [.. bytes];
    }

    private sealed class Databases : IDesktopSources
    {
        public Dictionary<string, List<string?[]>> Steps { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string?[]>> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ReadCursorToken(string database) => null;
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
        public IReadOnlyList<string?[]>? QueryDatabase(string database, string sql) =>
            (sql.Contains("FROM steps") ? Steps : Models).GetValueOrDefault(database) ?? [];
    }
}
