using System.Net;
using System.Text.Json;
using BrimDeck.Core;

internal static class ConfiguredProviderTests
{
    private static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    public static async Task Run(Action<string, bool> check)
    {
        var metrics = MetricParser.Parse(Json("""{"metrics":[{"kind":"balance","label":"余额","amount":75,"total":100,"currency":"CNY"},{"kind":"balance","label":"钱包","amount":0,"currency":"USD"},{"kind":"spend","label":"消费","amount":0,"unlimited":true},{"kind":"count","label":"MCP","used":1860,"total":2000,"unit":"次"}]}""")).Metrics;
        check("Balance, unlimited spend and counts keep distinct semantics", metrics[0].UsedPercent == 25 && metrics[1].UsedPercent == 100 && metrics[2].Unlimited && metrics[2].UsedPercent is null && metrics[3].UsedPercent == 93);
        check("Balance above total clamps only when drawn and zero balance is fully used", (metrics[0] with { Amount = 150 }).UsedPercent == -50 && (metrics[0] with { Amount = 0 }).UsedPercent == 100);
        var partial = MetricParser.Parse(Json("""{"metrics":[{"kind":"percent","label":"有效","percent":0},{"kind":"percent","label":"缺失"}]}"""));
        var texts = MetricParser.Parse(Json("""[{"title":"5 小时","percent":35,"value":"$12.50","suffix":"已用","note":"无上限","resetAt":"2 时 13 分后"},{"title":"余额","percent":null,"value":"","suffix":"可用"},{"percent":10},{"title":"每周","value":12},{"title":"每月","percent":101}]"""));
        check("Custom array items keep their display strings in order", texts.Metrics.Count == 2 && texts.Metrics[0] is { Kind: MetricKind.Text, Label: "5 小时", UsedPercent: 35, ValueText: "$12.50", ValueSuffix: "已用", Note: "无上限", ResetText: "2 时 13 分后" });
        check("Custom items without percent or value draw a full bar and no number or suffix", texts.Metrics[1] is { UsedPercent: null, ValueText: "", ValueSuffix: "" });
        check("Invalid custom items are reported by position and field", texts.Warnings.Count == 3 && texts.Warnings.Zip(new[] { ("3", "title"), ("4", "value"), ("5", "percent") }).All(w => w.First.Contains(w.Second.Item1) && w.First.Contains(w.Second.Item2)));
        static string Failure(string json) { try { MetricParser.Parse(Json(json)); return ""; } catch (InvalidDataException ex) { return ex.Message; } }
        check("Empty arrays and objects without metrics explain the expected return value",
            Failure("[]") != "" && Failure("""{"title":"x"}""").Contains("fetchUsage") && Failure("""[{"title":" "}]""").Contains("title"));
        check("Missing values are rejected while genuine zero is retained", partial.Metrics.Count == 1 && partial.Metrics[0].Percent == 0 && partial.Warnings.Single().Contains("metrics[1].percent"));
        var claude = QuotaParser.Claude(Json("""{"five_hour":{"utilization":45},"seven_day":{"utilization":9},"seven_day_opus":null,"limits":[{"kind":"weekly_all","group":"weekly","percent":9},{"kind":"weekly_scoped","group":"weekly","percent":18,"resets_at":"2026-09-25T10:00:00Z","scope":{"model":{"display_name":"Fable"}}}]}"""));
        check("Claude uses the server-provided Fable meter without duplicating the overall week", claude.Count == 3 && claude[2] is { UsedPercent: 18, Minutes: 10080, ResetAt: not null });
        check("Claude plans with no scoped allowance do not show Fable as zero", QuotaParser.Claude(Json("""{"five_hour":{"utilization":0},"seven_day":{"utilization":0},"limits":[]} """)).Count == 2);
        var legacy = JsonSerializer.Deserialize<DeckSettings>("""{"Apps":[{"QuotaSource":0,"UsageSource":1,"DisplayName":"原名"}]}""")!;
        legacy.Normalize();
        check("Quota source identifiers migrate existing numeric settings", JsonSerializer.Serialize(legacy).Contains("\"QuotaSource\":\"claude\"") && legacy.Apps[0].UsageSource == ProviderId.Codex && legacy.Apps[0].Name == "原名");
        var blank = new AppEntry { QuotaSource = ProviderId.Custom, Script = " " }; blank.Normalize();
        var written = new AppEntry { QuotaSource = ProviderId.Custom, Script = "function fetchUsage() {}" }; written.Normalize();
        check("Empty custom rows receive the template while written scripts are kept", blank.Script == ProviderScripts.Example && written.Script == "function fetchUsage() {}");
        check("Gateway address normalization preserves deployment paths", ProviderCatalog.NormalizeSite(" https://example.test/deploy/v1/// ") == "https://example.test/deploy");
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var entry = new AppEntry { QuotaSource = ProviderId.NewApi, Site = "https://example.test/deploy", SecretRevision = Guid.NewGuid() };
        var secrets = new MemorySecrets(); secrets.Write(entry.InstanceId, "fixture-key-a");
        var copy = entry.Copy(); copy.InstanceId = Guid.NewGuid(); secrets.Write(copy.InstanceId, "fixture-key-a");
        using var handler = new Handler(); using var http = new HttpClient(handler);
        using var providers = new ConfiguredProviders(secrets, http, () => now);
        handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/status")
            ? Reply("""{"success":true,"data":{"quota_per_unit":500000,"display_in_currency":true,"quota_display_type":"USD"}}""")
            : Reply("""{"code":true,"data":{"total_granted":9000000,"total_used":2800000,"total_available":6200000,"unlimited_quota":false,"expires_at":1800000000}}""");
        var snapshots = await providers.RefreshAsync([entry, copy], default);
        check("Identical connections issue one set of requests and bind both rows", snapshots.Count == 1 && handler.Calls.Count == 5 && snapshots[0].Configurations.Contains(copy.ConfigurationKey));
        check("New API reads station currency settings and amounts", snapshots[0].LiveQuota && snapshots[0].Metrics.Single() is { Amount: 12.4m, Total: 18m, Currency: "USD", ExpiresAt: not null });
        check("Public status request never carries the API credential", handler.Calls.Single(c => c.Path.EndsWith("/status")).Authorization is null && handler.Calls.Single(c => c.Path.EndsWith("/usage/token/")).Authorization == "Bearer fixture-key-a");
        static bool Accepted(string url) { try { ProviderCatalog.ValidateUrl(url); return true; } catch (InvalidDataException) { return false; } }
        check("Site addresses use HTTPS; plain HTTP only reaches this PC",
            Accepted("https://example.test/deploy") && Accepted("http://localhost:3000") && Accepted("http://127.0.0.1:8080/api") && Accepted("http://[::1]:8080")
            && !Accepted("http://example.test") && !Accepted("http://192.168.1.20:3000") && !Accepted("http://localhost.example.test"));
        var plainEntry = new AppEntry { QuotaSource = ProviderId.NewApi, Site = "http://example.test/deploy", SecretRevision = Guid.NewGuid() };
        secrets.Write(plainEntry.InstanceId, "fixture-key-a");
        int before = handler.Calls.Count;
        var plain = await providers.TestAsync(plainEntry);
        check("A saved HTTP site is not read and says HTTPS is required",
            handler.Calls.Count == before && plain.Snapshot.StatusLabel == "需要 HTTPS" && plain.Snapshot.Status.Contains("HTTPS") && !plain.Snapshot.LiveQuota);
        var plainScript = new AppEntry { QuotaSource = ProviderId.Custom, Script = """async function fetchUsage(ctx) { await ctx.http.get("http://example.test/usage"); return [{ title: "x", percent: 1 }]; }""" };
        var scripted = await providers.TestAsync(plainScript);
        check("A script's request to an HTTP address is refused before it is sent", handler.Calls.Count == before && scripted.Snapshot.Status.Contains("HTTPS"));
        static bool AcceptedWithHttp(string url) { try { ProviderCatalog.ValidateUrl(url, allowHttp: true); return true; } catch (InvalidDataException) { return false; } }
        check("A row that allows HTTP accepts addresses on the local network, still without a user name or #",
            AcceptedWithHttp("http://192.168.1.20:3000") && AcceptedWithHttp("http://nas.tailnet.test") && !AcceptedWithHttp("http://user:pass@192.168.1.20") && !AcceptedWithHttp("http://192.168.1.20/#x"));
        var lanEntry = new AppEntry { QuotaSource = ProviderId.NewApi, Site = "http://192.168.1.20:3000", SecretRevision = Guid.NewGuid(), AllowHttp = true };
        secrets.Write(lanEntry.InstanceId, "fixture-key-a");
        var lan = await providers.TestAsync(lanEntry);
        check("A saved HTTP site is read once the row allows HTTP", handler.Calls.Count > before && lan.Snapshot.LiveQuota);
        int afterLan = handler.Calls.Count;
        var lanScript = new AppEntry { QuotaSource = ProviderId.Custom, AllowHttp = true, Script = plainScript.Script };
        var lanScripted = await providers.TestAsync(lanScript);
        check("A script's HTTP request is sent once the row allows HTTP", handler.Calls.Count == afterLan + 1 && lanScripted.Snapshot.LiveQuota);
        var strict = lanEntry.Copy(); strict.AllowHttp = false;
        check("Rows that differ only in allowing HTTP are read apart, and other rows keep their keys",
            strict.ConfigurationKey != lanEntry.ConfigurationKey && ProviderCatalog.RequestKey(strict, "k") != ProviderCatalog.RequestKey(lanEntry, "k")
            && !strict.ConfigurationKey.EndsWith(":http") && lanEntry.Copy().AllowHttp);
        var savedAllow = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { Apps = [lanEntry] }))!;
        check("Allowing HTTP is saved per row and off for rows saved before it existed",
            savedAllow.Apps.Single().AllowHttp && !JsonSerializer.Deserialize<DeckSettings>("""{"Apps":[{"QuotaSource":"newapi","Site":"http://192.168.1.20"}]}""")!.Apps.Single().AllowHttp);
        var settings = new DeckSettings { Apps = [entry, copy] };
        var local = new ProviderSnapshot(ProviderId.Claude) { UsageAvailable = true };
        check("Remote quota columns keep local statistics separate and deduplicated", DashboardUsage.Columns(settings, [.. snapshots, local]).All(c => ReferenceEquals(c.Usage, local)) && DashboardUsage.Statistics(settings, [.. snapshots, local]).Count == 1);
        copy.SecretRevision = Guid.NewGuid(); secrets.Write(copy.InstanceId, "fixture-key-b");
        check("Replacing credentials immediately hides old quota snapshots", DashboardUsage.Quota(copy, snapshots).Metrics.Count == 0);
        handler.Calls.Clear();
        await providers.RefreshAsync([entry, copy], default);
        check("Different credentials on the same provider remain independent", handler.Calls.Count == 10);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}"), Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(180)) } };
        var stale = await providers.TestAsync(entry);
        check("Transient errors preserve amounts as stale data without live alerts", stale.Snapshot.IsStale && !stale.Snapshot.LiveQuota && stale.Snapshot.Metrics[0].Amount == 12.4m);
        int calls = handler.Calls.Count; now = now.AddSeconds(60); await providers.TestAsync(entry);
        check("Retry-After suppresses premature manual and scheduled requests", handler.Calls.Count == calls);
        now = now.AddSeconds(121);
        handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/status")
            ? Reply("""{"success":true,"data":{"quota_per_unit":500000,"quota_display_type":"CNY","usd_exchange_rate":7}}""")
            : Reply("""{"code":true,"data":{"total_used":500000,"unlimited_quota":true}}""");
        var unlimited = await providers.TestAsync(entry);
        check("Unlimited New API keys display spend in the station currency", unlimited.Snapshot.Metrics.Single() is { Kind: MetricKind.Spend, Amount: 7, Currency: "CNY", Unlimited: true, UsedPercent: null });
        handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/status")
            ? Reply("""{"success":true,"data":{"display_in_currency":false}}""")
            : Reply("""{"code":true,"data":{"total_used":0,"total_available":0,"total_granted":0,"unlimited_quota":false}}""");
        var zero = await providers.TestAsync(entry);
        check("New API token-display sites keep raw quota units and valid zero balances", zero.Snapshot.Metrics.Single() is { Amount: 0, Total: 0, Currency: "", UsedPercent: 100 });
        entry.QuotaSource = ProviderId.Sub2Api;
        handler.Respond = _ => Reply("""{"mode":"quota_limited","isValid":true,"quota":{"limit":20,"remaining":12.4,"used":7.6,"unit":"USD"},"rate_limits":[{"window":"5h","limit":10,"used":2,"reset_at":"2026-09-20T20:00:00Z"},{"window":"7d","limit":100,"used":40}],"expires_at":"2026-10-20T00:00:00Z"}""");
        var sub = await providers.TestAsync(entry);
        check("Sub2API returns all windows and key balance with separate expiry", sub.Snapshot.Metrics.Count == 3 && sub.Snapshot.Metrics[0] is { Percent: 20, Window: 300 } && sub.Snapshot.Metrics[2] is { Amount: 12.4m, Total: 20, ResetAt: null, ExpiresAt: not null });
        handler.Respond = _ => Reply("""{"mode":"unrestricted","isValid":true,"balance":0,"unit":"USD","planName":"钱包余额"}""");
        var wallet = await providers.TestAsync(entry);
        check("Sub2API wallet has an account scope, no artificial total, and an empty wallet counts as used up", wallet.Snapshot.Scope == "account" && wallet.Snapshot.Metrics[0] is { Amount: 0, Total: null, UsedPercent: 100 });
        handler.Respond = _ => Reply("""{"mode":"unrestricted","isValid":true,"planName":"套餐A","subscription":{"daily_limit_usd":10,"daily_usage_usd":3,"weekly_limit_usd":100,"weekly_usage_usd":25,"weekly_window_start":"2026-09-19T00:00:00Z","monthly_limit_usd":null,"expires_at":"2026-10-20T00:00:00Z"}}""");
        var plan = await providers.TestAsync(entry);
        check("Sub2API subscription windows omit absent limits and never substitute expiry for reset", plan.Snapshot.Scope == "plan" && plan.Snapshot.Metrics.Count == 2 && plan.Snapshot.Metrics[0].ResetAt is null && plan.Snapshot.Metrics[1].ResetAt == DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        entry.QuotaSource = ProviderId.GlmChina; entry.Site = "";
        handler.Respond = _ => Reply("""{"success":true,"code":200,"data":{"limits":[{"type":"TOKENS_LIMIT","percentage":15},{"type":"TIME_LIMIT","currentValue":1860,"usage":2000}]}}""");
        var glm = await providers.TestAsync(entry);
        check("GLM uses raw Authorization and treats MCP as calls rather than tokens", glm.Snapshot.Metrics.Count == 2 && glm.Snapshot.Metrics[1] is { Kind: MetricKind.Count, Used: 1860, Total: 2000 } && handler.Calls.Last().Authorization == "fixture-key-a");
        long At(double hours) => now.AddHours(hours).ToUnixTimeMilliseconds();
        string host = "";
        HttpResponseMessage Glm(string limits, string level = "lite") => Reply($$$"""{"code":200,"msg":"操作成功","success":true,"data":{"limits":[{{{limits}}}],"level":"{{{level}}}"}}""");
        // Legacy plan without a weekly limit.
        handler.Respond = request => { host = request.RequestUri!.Host; return Glm($$$"""{"type":"TIME_LIMIT","unit":5,"number":1,"usage":100,"currentValue":6,"remaining":94,"percentage":6,"nextResetTime":{{{At(200)}}},"usageDetails":[{"modelCode":"search-prime","usage":6}]},{"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":1,"nextResetTime":{{{At(3)}}}}"""); };
        var v1 = (await providers.TestAsync(entry)).Snapshot;
        check("GLM legacy plans show the 5-hour window and tool calls only", host == "open.bigmodel.cn" && v1.Plan == "Lite" && v1.Metrics.Count == 2 &&
            v1.Metrics.Any(m => m is { Window: 300, Percent: 1, ResetAt: not null }) && v1.Metrics.Any(m => m is { Id: "tools", Used: 6, Total: 100, Window: null }));
        handler.Respond = _ => Glm($$$"""{"type":"TOKENS_LIMIT","unit":6,"number":7,"percentage":40,"nextResetTime":{{{At(2)}}}},{"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":15,"nextResetTime":{{{At(4)}}}},{"type":"TIME_LIMIT","unit":5,"number":1,"usage":1000,"currentValue":10}""", "pro");
        var weekly = (await providers.TestAsync(entry)).Snapshot;
        check("GLM weekly limits are identified by unit even when they reset first", weekly.Plan == "Pro" && weekly.Metrics.Single(m => m.Window == 10080).Percent == 40 && weekly.Metrics.Single(m => m.Window == 300).Percent == 15);
        handler.Respond = _ => Glm($$$"""{"type":"CREDIT_LIMIT","unit":3,"number":5,"usage":2000,"currentValue":71,"remaining":1929,"percentage":3,"nextResetTime":{{{At(9)}}}},{"type":"CREDIT_LIMIT","unit":6,"number":1,"usage":10000,"currentValue":500,"percentage":5,"nextResetTime":{{{At(100)}}}}""");
        var credits = (await providers.TestAsync(entry)).Snapshot;
        check("GLM credit plans report exact credits and drop resets beyond the window", credits.Metrics.Count == 2 &&
            credits.Metrics[0] is { Kind: MetricKind.Count, Window: 300, Used: 71, Total: 2000, ResetAt: null } && credits.Metrics[1] is { Window: 10080, Used: 500, ResetAt: not null });
        handler.Respond = _ => Glm("", "pro");
        var team = (await providers.TestAsync(entry)).Snapshot;
        check("GLM team plans with no limits explain the omission", !team.LiveQuota && !string.IsNullOrWhiteSpace(team.Status) && !team.Status.StartsWith("HTTP"));
        entry.QuotaSource = ProviderId.GlmGlobal;
        handler.Respond = request => { host = request.RequestUri!.Host; return Glm("""{"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":20}""", "max"); };
        var global = (await providers.TestAsync(entry)).Snapshot;
        check("Z.ai GLM uses the international host with the same parser", host == "api.z.ai" && global.Plan == "Max" && global.Metrics.Single() is { Window: 300, Percent: 20, ResetAt: null });
        entry.QuotaSource = ProviderId.DeepSeek;
        handler.Respond = request => { host = request.RequestUri!.Host; return Reply("""{"is_available":false,"balance_infos":[{"currency":"USD","total_balance":"0.00","granted_balance":"0.00","topped_up_balance":"0.00"}]}"""); };
        var deepseek = (await providers.TestAsync(entry)).Snapshot;
        check("DeepSeek reads the key's balance and warns when calls are no longer possible", host == "api.deepseek.com" && handler.Calls.Last().Authorization == "Bearer fixture-key-a" &&
            deepseek.Scope == "account" && deepseek.Metrics.Count == 2 && deepseek.Metrics[0] is { Kind: MetricKind.Balance, Currency: "USD", UsedPercent: 100 } && deepseek.Status.Contains("余额不足"));
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"message":"Authentication Fails"}}""") };
        check("A rejected DeepSeek key explains what to check", (await providers.TestAsync(entry)).Snapshot is { LiveQuota: false, StatusLabel: "密钥无效" } rejected && rejected.Status.Contains("DeepSeek"));
        // Kimi Code API: a key from the Kimi Code console reads the plan quota the Kimi Code sign-in reads.
        var manual = ProviderCatalog.Sources.Where(id => !ProviderCatalog.IsBuiltIn(id)).ToList();
        check("Kimi Code API is entered by hand, right after DeepSeek and before New API",
            manual.IndexOf(ProviderId.KimiApi) == manual.IndexOf(ProviderId.DeepSeek) + 1 && manual.IndexOf(ProviderId.NewApi) == manual.IndexOf(ProviderId.KimiApi) + 1 &&
            ProviderCatalog.Group(ProviderId.KimiApi) == "手动填写" && ProviderCatalog.Name(ProviderId.KimiApi) == "Kimi Code API" && !ProviderCatalog.NeedsSite(ProviderId.KimiApi) &&
            !ProviderCatalog.IsUsageSource(ProviderId.KimiApi) && (int)ProviderId.KimiApi == 16);
        var kimiSaved = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.KimiApi }] }))!;
        check("Kimi Code API is saved as kimi-api and shares the color of sources entered by hand",
            ProviderCatalog.Key(ProviderId.KimiApi) == "kimi-api" && kimiSaved.Apps.Single().QuotaSource == ProviderId.KimiApi &&
            AppPresets.ThemeColor(ProviderId.KimiApi) == AppPresets.ManualColor && PanelThemes.AppKey(ProviderId.KimiApi) == "manual");
        entry.QuotaSource = ProviderId.KimiApi;
        var hosts = new List<string>();
        HttpResponseMessage Rejected() => new(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"unauthorized"}""") };
        HttpResponseMessage KimiReply(HttpRequestMessage request, string me) => request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal) ? Reply(me)
            : Reply("""{"usages":{"limit_5h":{"used_ratio":0.25,"reset_time":"2026-09-20T15:00:00Z"},"limit_7d":{"used_ratio":0.5},"limit_month_total":{"used_ratio":0.1}},"boosterWallet":{"balance":{"type":"BOOSTER","amount":"500000000","amountLeft":"200000000"}}}""");
        handler.Respond = request => { lock (hosts) hosts.Add(request.RequestUri!.Host); return KimiReply(request, """{"user_level_name":"Allegretto","region":"REGION_CN"}"""); };
        handler.Calls.Clear();
        var kimi = (await providers.TestAsync(entry)).Snapshot;
        check("A Kimi Code API key reads kimi.com's quota and membership with the Bearer key, parsed as the sign-in's",
            hosts.All(h => h == "api.kimi.com") && handler.Calls.Select(c => c.Path).SequenceEqual(["/coding/v1/usages", "/coding/v1/me"]) && handler.Calls.All(c => c.Authorization == "Bearer fixture-key-a") &&
            kimi.LiveQuota && kimi.Plan == "Allegretto" && kimi.Scope == "plan" && kimi.Source == "Kimi Code API · kimi.com 账户" &&
            kimi.Metrics.Select(m => m.Window).Take(3).SequenceEqual(new int?[] { 300, 10080, 43200 }) && kimi.Metrics[3] is { Kind: MetricKind.Balance, Currency: "CNY" });
        hosts.Clear();
        handler.Respond = request => { lock (hosts) hosts.Add(request.RequestUri!.Host); return request.RequestUri!.Host == "api.kimi.com" ? Rejected() : KimiReply(request, """{"user_level_name":"Vivace","region":"REGION_GLOBAL"}"""); };
        kimi = (await providers.TestAsync(entry)).Snapshot;
        check("A key kimi.com rejects is read on kimi.ai, shown as a kimi.ai account in dollars",
            hosts.SequenceEqual(["api.kimi.com", "api.kimi.ai", "api.kimi.ai"]) && kimi.LiveQuota && kimi.Plan == "Vivace" && kimi.Source == "Kimi Code API · kimi.ai 账户" &&
            kimi.Metrics.Last() is { Kind: MetricKind.Balance, Currency: "USD" } && kimi.StatusLabel == "已连接");
        handler.Respond = request => KimiReply(request, """{"user_level_name":"Plus","region":"REGION_GLOBAL"}""");
        check("The region shown follows /me", (await providers.TestAsync(entry)).Snapshot is { Source: "Kimi Code API · kimi.ai 账户", Plan: "Plus" });
        handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") } : KimiReply(request, "");
        check("Without the membership the quota still shows, named by the service that answered", (await providers.TestAsync(entry)).Snapshot is { LiveQuota: true, Plan: "", Source: "Kimi Code API · kimi.com 账户" });
        handler.Respond = _ => Reply("""{"usages":{}}""");
        check("An account without a Kimi Code plan says so", (await providers.TestAsync(entry)).Snapshot is { LiveQuota: false } none && none.Status.Contains("没有 Kimi Code"));
        handler.Respond = request => KimiReply(request, """{"user_level_name":"Allegretto","region":"REGION_CN"}""");
        await providers.TestAsync(entry);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("") };
        check("A passing failure keeps the last quota, marked stale", (await providers.TestAsync(entry)).Snapshot is { LiveQuota: false, IsStale: true, StatusLabel: "读取失败" } kimiStale && kimiStale.Metrics.Count == 4);
        hosts.Clear();
        handler.Respond = request => { lock (hosts) hosts.Add(request.RequestUri!.Host); return Rejected(); };
        var invalid = (await providers.TestAsync(entry)).Snapshot;
        check("A key both services reject is reported invalid without showing the key", hosts.SequenceEqual(["api.kimi.com", "api.kimi.ai"]) && invalid is { LiveQuota: false, StatusLabel: "密钥无效" } &&
            invalid.Status.Contains("sk-kimi-") && !invalid.Status.Contains("fixture-key"));
        check("Kimi's region comes from /me", KimiUsage.GlobalRegion(Json("""{"region":"REGION_CN"}""")) == false && KimiUsage.GlobalRegion(Json("""{"region":"REGION_GLOBAL"}""")) == true &&
            KimiUsage.GlobalRegion(Json("{}")) is null && KimiUsage.GlobalRegion(default) is null && KimiUsage.GlobalRegion(Json("""{"region":"cn"}""")) is null);
        entry.QuotaSource = ProviderId.Custom;
        entry.Script = """async function fetchUsage(ctx) { const [a,b] = await Promise.all([ctx.http.get("https://example.test/a"),ctx.http.post("https://example.test/b",{body:{value:1}})]); ctx.log(ctx.secrets.key); return {metrics:[{kind:"balance",label:"余额",amount:a.json().balance+b.json().balance,currency:"USD"}]}; }""";
        handler.Respond = _ => Reply("""{"balance":2}""");
        var custom = await providers.TestAsync(entry);
        check("Custom JavaScript supports async await, parallel HTTP, POST bodies and redacted logs", custom.Snapshot.LiveQuota && custom.Snapshot.Metrics[0].Amount == 4 && !string.Join("", custom.Logs).Contains("fixture-key"));
        entry.Script = """function fetchUsage(ctx) { if (typeof require !== "undefined" || typeof System !== "undefined" || typeof importNamespace !== "undefined" || typeof ctx.http.get.GetType !== "undefined") throw new Error("CLR exposed"); return {metrics:[{kind:"percent",label:"safe",percent:1}]}; }""";
        check("Custom scripts have no file, process or CLR namespace access", (await providers.TestAsync(entry)).Snapshot.LiveQuota);
        entry.Script = ProviderScripts.Example;
        var example = (await providers.TestAsync(entry)).Snapshot;
        check("The inserted custom template runs as written", example.LiveQuota && example.Metrics.Count > 0);
        secrets.Write(entry.InstanceId, "SCRIPT\"\\CONTEXT");
        entry.Script = """function fetchUsage(ctx) { return {metrics:[{kind:"percent",label:"context",percent:ctx.secrets.key === 'SCRIPT"\\CONTEXT' ? 1 : 0}]}; }""";
        check("Script context preserves template words and escaped credential characters", (await providers.TestAsync(entry)).Snapshot.Metrics.Single().Percent == 1);
        secrets.Write(entry.InstanceId, "fixture-key-a");
        entry.Script = """function fetchUsage(ctx) { throw new Error(ctx.secrets.key); }""";
        var error = await providers.TestAsync(entry);
        check("Script exceptions redact the configured key", !error.Snapshot.LiveQuota && !error.Snapshot.Status.Contains("fixture-key-a"));
        entry.Script = """function fetchUsage() { while (true) {} }""";
        var loop = await providers.TestAsync(entry);
        check("Unbounded scripts stop within their execution budget", !loop.Snapshot.LiveQuota && loop.Seconds < 12);
        entry.Script = """async function fetchUsage(ctx) { await ctx.http.get("https://example.test/redirect"); return {metrics:[{kind:"percent",label:"bad",percent:0}]}; }""";
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://other.test") } };
        check("Redirects are rejected without forwarding credentials", !(await providers.TestAsync(entry)).Snapshot.LiveQuota);
        await RelayStations(check);
    }

    // New API and Sub2API: account balance, today's charge, units and wrong-type hints.
    private static async Task RelayStations(Action<string, bool> check)
    {
        check("Console page paths are removed from station addresses", ProviderCatalog.NormalizeSite("https://relay.example/console/token") == "https://relay.example" &&
            ProviderCatalog.NormalizeSite("https://example.test/deploy/dashboard/models?x=1") == "https://example.test/deploy" && ProviderCatalog.NormalizeSite("https://example.test/deploy/v1") == "https://example.test/deploy");
        check("Negative amounts put the sign before the currency", UsageMetric.Money(-0.05m, "USD") == "−$0.05");
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00+08:00");
        long today = now.AddHours(-1).ToUnixTimeSeconds(), yesterday = now.AddDays(-1).ToUnixTimeSeconds();
        var entry = new AppEntry { QuotaSource = ProviderId.NewApi, Site = "https://relay.test", SecretRevision = Guid.NewGuid() };
        var secrets = new MemorySecrets(); secrets.Write(entry.InstanceId, "relay-key");
        using var handler = new Handler(); using var http = new HttpClient(handler);
        using var providers = new ConfiguredProviders(secrets, http, () => now);
        long used = 1_000_000; string status = """{"success":true,"data":{"quota_per_unit":500000,"quota_display_type":"USD"}}""";
        string hardLimit = "10", logs = $$$"""{"success":true,"data":[{"type":2,"quota":250000,"created_at":{{{today}}},"other":"x"},{"type":6,"quota":50000,"created_at":{{{today}}}},{"type":2,"quota":100000,"created_at":{{{yesterday}}}}]}""";
        HttpStatusCode statusCode = HttpStatusCode.OK;
        handler.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/api/usage/token/" => Reply($$$"""{"code":true,"data":{"object":"token_usage","total_granted":0,"total_used":{{{used}}},"total_available":{{{-used}}},"unlimited_quota":true,"expires_at":0}}"""),
            "/api/status" => new HttpResponseMessage(statusCode) { Content = new StringContent(status) },
            "/v1/dashboard/billing/subscription" => Reply($$$"""{"object":"billing_subscription","hard_limit_usd":{{{hardLimit}}}}"""),
            "/v1/dashboard/billing/usage" => Reply("""{"object":"list","total_usage":250}"""),
            "/api/log/token" => Reply(logs),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") }
        };
        var first = (await providers.TestAsync(entry)).Snapshot;
        check("New API shows the account balance, today's charge and the key total in order", first.Scope == "account" && first.Metrics.Select(m => m.Id).SequenceEqual(["account", "today", "total"]) &&
            first.Metrics[0] is { Amount: { } balance, Total: null, UsedPercent: null } && balance == 7.5m && first.Metrics[1] is { Kind: MetricKind.Spend, Amount: 0.4m, AtLeast: false } && first.Metrics[2] is { Amount: 2m, Unlimited: true });
        handler.Calls.Clear(); used += 500_000;
        var second = (await providers.TestAsync(entry)).Snapshot;
        check("Later refreshes on the same day derive today's charge without downloading the log", !handler.Calls.Any(c => c.Path == "/api/log/token") && second.Metrics[1].Amount == 1.4m);
        now = now.AddDays(1); handler.Calls.Clear();
        await providers.TestAsync(entry);
        check("A new day downloads the log again", handler.Calls.Count(c => c.Path == "/api/log/token") == 1);
        hardLimit = "100000000"; logs = """{"success":true,"data":[]}"""; now = now.AddDays(1);
        var keyOnly = (await providers.TestAsync(entry)).Snapshot;
        check("Per-key billing and an empty log show only the key total", keyOnly.Scope == "key" && keyOnly.Metrics.Single() is { Id: "total", Amount: 3m });
        now = now.AddDays(1);
        var rows = string.Join(",", Enumerable.Range(0, 1200).Select(i => $$$"""{"type":2,"quota":5000,"created_at":{{{now.AddHours(-1).ToUnixTimeSeconds()}}},"other":"{{{new string('x', 900)}}}"}"""));
        logs = $$$"""{"success":true,"data":[{{{rows}}}]}""";
        var capped = (await providers.TestAsync(entry)).Snapshot;
        check("A log larger than 1 MB is reduced by the host and a full page from today is a lower bound", capped.Metrics.Single(m => m.Id == "today") is { Amount: 12m, AtLeast: true, ValueText: "≥$12.00" });
        statusCode = HttpStatusCode.Forbidden; status = "<!DOCTYPE html><title>Attention Required! | Cloudflare</title>"; hardLimit = "10";
        var blocked = (await providers.TestAsync(entry)).Snapshot;
        check("A blocked status page falls back to dollars with a visible note", blocked.LiveQuota && !string.IsNullOrWhiteSpace(blocked.Status) && blocked.Metrics.Last() is { Currency: "USD", Amount: 3m });
        statusCode = HttpStatusCode.OK; status = """{"success":true,"data":{"quota_per_unit":500000,"quota_display_type":"CNY","usd_exchange_rate":7}}""";
        var yuan = (await providers.TestAsync(entry)).Snapshot;
        check("A CNY station with its own exchange rate omits the ambiguous account balance", yuan.Metrics.All(m => m.Id != "account") && yuan.Metrics.Last() is { Currency: "CNY", Amount: 21m });

        entry.QuotaSource = ProviderId.Sub2Api;
        handler.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/usage" => Reply("""{"mode":"unrestricted","isValid":true,"planName":"钱包余额","balance":50,"unit":"USD","usage":{"today":{"cost":1,"actual_cost":1.5}}}"""),
            "/v1/sub2api/billing" => Reply("""{"object":"sub2api.key_billing","effective_rate_multiplier":1.5}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") }
        };
        var wallet = (await providers.TestAsync(entry)).Snapshot;
        check("Sub2API wallets show balance, today's actual charge and the multiplier tag", wallet.Plan == "1.5×" && wallet.Metrics.Select(m => m.Id).SequenceEqual(["wallet", "today"]) && wallet.Metrics[1].Amount == 1.5m);
        entry.QuotaSource = ProviderId.NewApi;
        var wrong = (await providers.TestAsync(entry)).Snapshot;
        check("A Sub2API station entered as New API names the right source", !wrong.LiveQuota && wrong.Status.Contains("Sub2API"));
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") };
        var neither = (await providers.TestAsync(entry)).Snapshot;
        check("A station of neither kind reports the plain HTTP error", neither.Status.StartsWith("HTTP 404"));
    }
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class MemorySecrets : IProviderSecrets
    {
        private readonly Dictionary<Guid, string> _keys = [];
        public string Read(Guid id) => _keys.GetValueOrDefault(id, "");
        public void Write(Guid id, string key) => _keys[id] = key;
        public void Delete(Guid id) => _keys.Remove(id);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Reply("{}");
        public List<(string Path, string? Authorization)> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add((request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null));
            return Task.FromResult(Respond(request));
        }
    }
}
