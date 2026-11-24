using System.Net;
using System.Net.Http;
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

        // An orchestrated conversation: the response model names only the role, the display name the model that ran. The
        // checkpoint step has no generation record and is named by its usage's model number.
        var orchestratedDb = Path.Combine(desktopFolder, "orchestrated.db");
        File.WriteAllText(orchestratedDb, "");
        fake.Steps[orchestratedDb] = [Step(now.AddMinutes(-9), "o1", 100, 10, 0, 1667), Step(now.AddMinutes(-8), "w1", 50, 5, 0, 1669),
            Step(now.AddMinutes(-7), "k1", 70, 7, 0, 1667), Step(now.AddMinutes(-6), "x1", 30, 3, 0, 1999), Step(now.AddMinutes(-5), "p1", 20, 2, 0, 1402)];
        fake.Models[orchestratedDb] = [Generation("o1", "gemini-pro-orchestrator", "Orchestrator (Carbon)", 1667),
            Generation("w1", "gemini-flash-worker-agy", "Gemini 4 Hydrogen (Orchestrator)", 1669), Generation("p1", "claude-opus-5-5-high", "", 1402)];
        var orchestrated = new ProviderSnapshot(ProviderId.Antigravity);
        new AntigravityUsage().Read(orchestrated, fake, [desktopFolder], now.AddDays(-29), now);
        TokenEntry Orchestrated(string id) => orchestrated.Entries.Single(e => e.Key == "ag:" + id);
        check("Antigravity orchestrated records show the model menu's name and are priced by the model that ran",
            Orchestrated("o1") is { Model: "Orchestrator (Carbon)", PriceModel: "carbon" }
            && Orchestrated("w1") is { Model: "Gemini 4 Hydrogen (Orchestrator)", PriceModel: "gemini-4-hydrogen" });
        check("Antigravity records without a display name keep their response model",
            Orchestrated("p1") is { Model: "claude-opus-5-5-high", PriceModel: null } && desktop.PriceModel is null);
        check("An Antigravity checkpoint step is named by the model number of the conversation's generations",
            Orchestrated("k1") is { Model: "Orchestrator (Carbon)", PriceModel: "carbon" } && Orchestrated("x1") is { Model: "", PriceModel: null });

        var missing = new ProviderSnapshot(ProviderId.Antigravity);
        new AntigravityUsage().Read(missing, fake, [Path.Combine(root, "none")], now.AddDays(-29), now);
        check("Missing Antigravity conversation folders are unavailable, not zero", !missing.UsageAvailable);

        var daemon = Path.Combine(root, "antigravity", "daemon");
        Directory.CreateDirectory(daemon);
        File.WriteAllText(Path.Combine(daemon, "hub_64335.json"), """{"pid":29156,"httpsPort":64334,"httpPort":64335,"lsVersion":"2.32.1","csrfToken":"token-a"}""");
        File.WriteAllText(Path.Combine(daemon, "hub_1.json"), """{"pid":1,"httpsPort":0,"httpPort":0,"csrfToken":"no-port"}""");
        File.WriteAllText(Path.Combine(daemon, "hub_2.json"), """{"pid":2,"httpsPort":10,"httpPort":11}""");
        File.WriteAllText(Path.Combine(daemon, "hub_3.json"), "{not json");
        File.WriteAllText(Path.Combine(daemon, "other.json"), """{"pid":4,"httpsPort":10,"httpPort":11,"csrfToken":"other"}""");
        var servers = AntigravityUsage.ReadLanguageServers(daemon);
        check("Antigravity service files give each language server's ports and token, skipping incomplete and malformed files",
            servers.SequenceEqual([new AntigravityUsage.LanguageServer(29156, 64334, 64335, "token-a")]));
        check("A missing Antigravity service folder gives no language servers", AntigravityUsage.ReadLanguageServers(Path.Combine(root, "none")).Count == 0);
        check("The Antigravity service folder is under .gemini", AntigravityUsage.DaemonFolder(@"C:\Users\A") == @"C:\Users\A\.gemini\antigravity\daemon");

        CliFallback(root, check);
        CliSwitch(root, now, check);
        RetainedReading(root, check);
        ProbeAndClassify(check);
        PrefixPricing(root, now, check);
    }

    private static void CliSwitch(string root, DateTimeOffset now, Action<string, bool> check)
    {
        check("Reading Antigravity via the CLI is off by default", !new AppEntry().AntigravityCli);
        var saved = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Antigravity }] }))!;
        foreach (var app in saved["Apps"]!.AsArray()) app!.AsObject().Remove("AntigravityCli");
        check("A settings file without the CLI switch does not read via the CLI", !SettingsMigrations.Read(saved.ToJsonString()).Apps.Single().AntigravityCli);

        var reset = now.AddDays(2).ToUniversalTime().ToString("O");
        var fake = new Databases { Report = $"Gemini Models\tWeekly Limit Remaining\t96%\t{reset}" };
        using var service = new UsageService(fake, DataLocations.Resolve(root, root, root, null, null), clock: () => now);
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Antigravity, AntigravityCli = true }] };
        settings.Normalize();
        ProviderSnapshot Read() => service.RefreshAsync(settings).GetAwaiter().GetResult().Single(x => x.Id == ProviderId.Antigravity);
        check("The CLI is read while the switch is on", Read().Source == "Antigravity CLI" && fake.Calls == 1);
        settings.Apps[0].AntigravityCli = false;
        var off = Read();
        check("With the switch off the CLI is not run and its reading is not shown", fake.Calls == 1 && off.Quotas.Count == 0 && !off.LiveQuota
            && off.Status == "未检测到 Antigravity 应用或 IDE 的本地用量服务");
        settings.Apps[0].AntigravityCli = true;
        now = now.AddMinutes(1);
        check("Turning the switch back on runs the CLI at once", Read().Source == "Antigravity CLI" && fake.Calls == 2);
        settings.Apps = [new AppEntry { QuotaSource = ProviderId.Antigravity, AntigravityCli = false }, new AppEntry { QuotaSource = ProviderId.Antigravity, AntigravityCli = true }];
        settings.Normalize();
        now = now.AddMinutes(5);
        check("The CLI is run while any Antigravity row has the switch on", Read().Source == "Antigravity CLI" && fake.Calls == 3);
    }

    private static void CliFallback(string root, Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.FromHours(8));
        var fake = new Databases { Report = "Gemini Models\tWeekly Limit Remaining\t96%\t2026-11-25T02:00:10Z\n"
            + "bad row\nClaude and GPT models\tFive Hour Limit Remaining\t0%\t2026-11-23T18:00:00Z" };
        var locations = DataLocations.Resolve(root, root, root, null, null);
        using var service = new UsageService(fake, locations, clock: () => now)
        {
            Deadline = TimeSpan.FromSeconds(5),
            AntigravityCliWait = TimeSpan.FromSeconds(2),
            AntigravityCliTimeout = TimeSpan.FromSeconds(30)
        };
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Antigravity, AntigravityCli = true }] };
        settings.Normalize();
        ProviderSnapshot Read() => service.RefreshAsync(settings).GetAwaiter().GetResult().Single(x => x.Id == ProviderId.Antigravity);

        var first = Read();
        check("Without a local endpoint the CLI supplies grouped quota and reset times", first.LiveQuota && first.Source == "Antigravity CLI"
            && first.Quotas is [{ Label: "Gemini · 每周", UsedPercent: 4, Minutes: 10080 }, { UsedPercent: 100, Minutes: 300 }]
            && first.Quotas[0].ResetAt == DateTimeOffset.Parse("2026-11-25T02:00:10Z"));
        foreach (var failure in new[] { AntigravityCliStatus.NetworkUnavailable, AntigravityCliStatus.AuthRequired, AntigravityCliStatus.TimedOut })
        {
            fake.CliResult = new(failure);
            var stale = Read();
            check($"A {failure} poll keeps the last valid quota", stale.IsStale && stale.QuotaTime == first.QuotaTime);
            fake.CliResult = null;
            check($"The refresh immediately following {failure} reads again", !Read().IsStale);
        }

        // Layer 4: when a local endpoint exists and fails, CLI is NOT called even with the switch on.
        now = now.AddMinutes(10);
        using var reserved = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        fake.Endpoints = [new LocalEndpoint(((IPEndPoint)reserved.LocalEndPoint!).Port, "http", "t")];
        var localDown = Read();
        check("Running local endpoint that fails does not spawn CLI and preserves retained quota",
            fake.Calls == 7 && localDown.IsStale && localDown.StatusLabel == "服务暂不可用" && localDown.Quotas.Count > 0);
        fake.Endpoints = [];

        // Layer 4: slow CLI runs in the background without blocking RefreshAsync, then raises BackgroundReadFinished.
        now = now.AddMinutes(10);
        var release = new TaskCompletionSource();
        var finished = new TaskCompletionSource<bool>();
        service.BackgroundReadFinished += firstRead => finished.TrySetResult(firstRead);
        var entered = new TaskCompletionSource();
        fake.OnCliRead = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };

        var pending = Read();
        entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        check("Slow CLI read does not block the refresh round and shows retained quota while refreshing",
            pending.IsStale && pending.Quotas.Count > 0
            && pending.Status.Contains("正在通过 Antigravity CLI 刷新", StringComparison.Ordinal) && fake.Calls == 8);
        release.SetResult();
        finished.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        fake.OnCliRead = null;
        now = now.AddSeconds(5);
        var afterBg = service.RefreshAsync(settings, backgroundCompletion: true).GetAwaiter().GetResult().Single(x => x.Id == ProviderId.Antigravity);
        check("After background CLI finishes, the next refresh displays it without starting another CLI call",
            !afterBg.IsStale && fake.Calls == 8);

        // Lifetime cancellation propagates even with cached quota.
        now = now.AddMinutes(5);
        using var lifetime = new CancellationTokenSource();
        fake.OnCliRead = ct => { lifetime.Cancel(); return Task.FromCanceled(ct); };
        bool cancelled = false;
        try { service.RefreshAsync(settings, lifetime.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelled = true; }
        check("Lifetime cancellation during a CLI poll propagates even with cached quota", cancelled && fake.Calls == 9);
    }

    private static void RetainedReading(string root, Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.FromHours(8));
        var fake = new Databases();
        var locations = DataLocations.Resolve(root, root, root, null, null);
        using var service = new UsageService(fake, locations, clock: () => now);
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Antigravity, AntigravityCli = false }] };
        settings.Normalize();

        var restored = new ProviderSnapshot(ProviderId.Antigravity)
        {
            Plan = "Google AI Pro",
            QuotaTime = now.AddMinutes(-15),

            IsStale = true,
            Metrics =
            [
                UsageMetric.FromQuota(new("Gemini · 5 小时", 35, now.AddHours(1), 300)),
                UsageMetric.FromQuota(new("Gemini · 每周", 12, now.AddDays(3), 10080))
            ]
        };
        var store = new UsageLastSeen(Path.Combine(root, "antigravity-retained.json"), () => now);
        settings.Apps[0].AntigravityCli = true;
        restored.Source = "Antigravity CLI";
        store.Save([restored], settings);
        check("Stored CLI quota retains its origin", store.Load(settings).Single().Source == "Antigravity CLI");
        settings.Apps[0].AntigravityCli = false;
        check("Disabling CLI hides stored CLI quota after restart", store.Load(settings).All(x => x.Metrics.Count == 0));
        restored.Source = "Antigravity";
        store.Save([restored], settings);
        service.RestoreLastReadings(store.Load(settings));

        ProviderSnapshot Read() => service.RefreshAsync(settings).GetAwaiter().GetResult().Single(x => x.Id == ProviderId.Antigravity);
        var closed = Read();
        check("Restored Antigravity reading stays visible when desktop app is closed and CLI is off",
            closed.LiveQuota && closed.IsStale && closed.Plan == "Google AI Pro" && closed.QuotaTime == restored.QuotaTime
            && closed.StatusLabel == "上次读取" && closed.Quotas.Count == 2 && fake.Calls == 0);

        // After the 5-hour window resets, only the weekly window that still holds remains visible.
        now = now.AddHours(2);
        var partial = Read();
        check("Expired Antigravity windows drop from the retained reading while active windows remain",
            partial.LiveQuota && partial.IsStale && partial.Quotas is [{ Label: "Gemini · 每周", UsedPercent: 12 }]);

        // Past UsageLastSeen.MaxAge (24h), the retained reading expires cleanly.
        now = now.AddHours(24);
        var expired = Read();
        check("Antigravity retained reading expires after MaxAge instead of showing stale numbers",
            !expired.LiveQuota && expired.Quotas.Count == 0 && expired.StatusLabel == "服务未就绪");
    }

    private static void ProbeAndClassify(Action<string, bool> check)
    {
        var valid = "Gemini Models\tWeekly Limit Remaining\t80%\t2026-11-25T02:00:10Z";
        check("CLI classifier accepts valid usage report even with benign stderr",
            AntigravityUsage.ClassifyCliOutput(0, valid, "info: cached").Status == AntigravityCliStatus.Ok);
        check("CLI classifier detects OAuth URL as AuthRequired",
            AntigravityUsage.ClassifyCliOutput(1, "Open this URL: https://accounts.google.com/o/oauth2/v2/auth?client_id=x", "").Status == AntigravityCliStatus.AuthRequired);
        check("CLI classifier detects sign-in prompt on stderr as AuthRequired",
            AntigravityUsage.ClassifyCliOutput(1, "", "Error: not signed in, please login").Status == AntigravityCliStatus.AuthRequired);
        check("CLI classifier detects dial tcp timeout as NetworkUnavailable",
            AntigravityUsage.ClassifyCliOutput(1, "", "Post https://accounts.google.com/oauth2: dial tcp 142.250.0.1:443: i/o timeout").Status == AntigravityCliStatus.NetworkUnavailable);
        check("CLI classifier falls back to Failed for generic non-zero exit",
            AntigravityUsage.ClassifyCliOutput(1, "", "unexpected internal error").Status == AntigravityCliStatus.Failed);
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
            ("openai/gpt-oss-120b", 11), ("openai/gpt-oss-20b", 12), ("openai/gpt-5", 15), ("openai/gpt-5-mini", 16), ("openai/gpt-5.1-codex-max", 17),
            ("google/gemini-3-carbon", 23), ("google/gemini-4-carbon", 24), ("google/gemini-4-carbon:batch", 25), ("google/gemini-4.5-carbon-preview", 26),
            ("google/gemini-4-hydrogen", 27)
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
        bool Priced(int n, string model, string price) => pricing.Estimate(entry with { Model = model, PriceModel = price }) == Cost(n);
        check("An Antigravity orchestrator is priced as the newest catalog Gemini of its family, variants aside",
            Priced(24, "Orchestrator (Carbon)", "carbon") && Priced(27, "Gemini 4 Hydrogen (Orchestrator)", "gemini-4-hydrogen")
            && pricing.Estimate(entry with { Model = "Orchestrator (Neon)", PriceModel = "neon" }) is null
            && pricing.Estimate(entry with { Key = "claude:prefix", Model = "Orchestrator (Carbon)", PriceModel = "carbon" }) is null);
        check("An exact manual price wins over the prefix match",
            pricing.TrySetManual("gemini-3.8-flash-control", Price(20), false, out _) && All(20, "gemini-3.8-flash-control") && All(2, "gemini-3.8-flash-high"));
    }

    // CortexStepMetadata { 1: Timestamp { 1: seconds }, 9: ModelUsageStats { 1 model, 2 input, 3 output, 5 cache read, 7 message id } }
    private static string?[] Step(DateTimeOffset time, string id, long input, long output, long cacheRead, ulong model = 0) =>
        [Convert.ToHexString([.. Message(1, Varint(1, (ulong)time.ToUnixTimeSeconds())),
            .. Message(9, [.. model > 0 ? Varint(1, model) : [], .. Varint(2, (ulong)input), .. Varint(3, (ulong)output), .. Varint(5, (ulong)cacheRead), .. Message(7, Utf8(id))])])];

    // gen_metadata { 1: ChatModelMetadata { 4: ModelUsageStats { 1 model, 7 message id }, 19: response_model, 21: model_display_name } }
    private static string?[] Generation(string id, string model, string display = "", ulong number = 0) =>
        [Convert.ToHexString(Message(1, [.. Message(4, [.. number > 0 ? Varint(1, number) : [], .. Message(7, Utf8(id))]), .. Message(19, Utf8(model)),
            .. display.Length > 0 ? Message(21, Utf8(display)) : []]))];
    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

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
        public List<LocalEndpoint> Endpoints { get; set; } = [];
        public string? Report { get; set; }
        public AntigravityCliResult? CliResult { get; set; }
        public int Calls { get; private set; }
        public Func<CancellationToken, Task>? OnCliRead { get; set; }
        public async Task<AntigravityCliResult> ReadAntigravityCliResultAsync(CancellationToken cancellation)
        {
            Calls++;
            if (OnCliRead is not null) await OnCliRead(cancellation);
            if (CliResult is { } explicitResult) return explicitResult;
            return string.IsNullOrWhiteSpace(Report) ? new(AntigravityCliStatus.Failed) : new(AntigravityCliStatus.Ok, Report);
        }
        public string? ReadCursorToken(string database) => null;
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation)
            => Task.FromResult<IReadOnlyList<LocalEndpoint>>(Endpoints);
        public IReadOnlyList<string?[]>? QueryDatabase(string database, string sql) =>
            (sql.Contains("FROM steps") ? Steps : Models).GetValueOrDefault(database) ?? [];
    }
}
