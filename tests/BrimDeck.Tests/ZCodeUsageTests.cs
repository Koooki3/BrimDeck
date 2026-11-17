using BrimDeck.Core;

static class ZCodeUsageTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        // Local midnight, like the start of a statistics period, so the query bound matches on every machine's time zone.
        var start = new DateTimeOffset(new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Local));
        long At(int hours) => start.AddHours(hours).ToUnixTimeMilliseconds();
        // input_tokens includes the cached part, as in ZCode's own records.
        var entries = ZCodeUsage.Parse([
            ["a", "GLM-5.3", At(1).ToString(), "25000", "200", "20000", "0"],
            ["b", "GLM-5.3", At(2).ToString(), "100", "5", "80", "50"],
            ["c", "GLM-5.3", "not a time", "1", "1", "0", "0"]]).ToList();
        check("ZCode input excludes cached reads", entries.Count == 2 && entries[0] is { Input: 5000, CacheRead: 20000, CacheWrite: 0, Output: 200, Total: 25200 });
        check("ZCode cache writes never make input negative", entries[1] is { Input: 0, CacheRead: 80, CacheWrite: 20, Output: 5 });
        check("ZCode keys are unique per request", entries[0].Key == "zcode:a" && entries[1].Key == "zcode:b");

        var home = Path.Combine(root, "zcode-home");
        Directory.CreateDirectory(Path.Combine(home, "cli", "db"));
        File.WriteAllText(ZCodeUsage.Database(home), "");
        var locations = DataLocations.Resolve(Path.Combine(root, "home"), root, root, null, null, home);
        check("ZCODE_HOME overrides the ZCode directory", locations.ZCodeHome == home &&
            DataLocations.Resolve(Path.Combine(root, "home"), root, root, null, null).ZCodeHome == Path.Combine(root, "home", ".zcode"));
        var desktop = new Rows([["a", "GLM-5.3", At(1).ToString(), "25000", "200", "20000", "0"]]);
        using var service = new UsageService(desktop, locations, clock: () => start.AddDays(1));
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Claude, UsageSource = ProviderId.ZCode }] };
        var snapshot = (await service.RefreshAsync(settings, usageStart: start.LocalDateTime)).Single(s => s.Id == ProviderId.ZCode);
        check("ZCode statistics come from its request table", snapshot.UsageAvailable && snapshot.UsageComplete && snapshot.Entries.Count == 1 &&
            desktop.Sql.Contains("FROM model_usage") && desktop.Sql.Contains(start.ToUnixTimeMilliseconds().ToString()));
        check("ZCode without a signed-in Coding Plan asks the user to sign in", snapshot.StatusLabel == "未连接");

        // Credentials written in ZCode's format decrypt with the same key derivation.
        var key = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("fixture-secret"));
        string B(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string Seal(string value, byte[] with)
        {
            var iv = new byte[12]; var tag = new byte[16]; var plain = System.Text.Encoding.UTF8.GetBytes(value); var data = new byte[plain.Length];
            using var aes = new System.Security.Cryptography.AesGcm(with, 16); aes.Encrypt(iv, plain, data, tag);
            return B(iv) + "." + B(tag) + "." + B(data);
        }
        string Encrypt(string value) => "enc:v1:" + Seal(value, key);
        // The newer form names where its key is kept and the key's fingerprint.
        string EncryptV2(string value, string source, byte[] with) =>
            $"enc:v2:{source}:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(with))[..24]}:{Seal(value, with)}";
        Directory.CreateDirectory(Path.Combine(home, "v2"));
        void Store(Dictionary<string, string> values) => File.WriteAllText(ZCodeUsage.Credentials(home), System.Text.Json.JsonSerializer.Serialize(values));
        // A plan key left over from the previously signed-in region must not be used for the active one.
        const string old = "account-provider:coding-plan:account:bigmodel-individual-coding-plan:account:2:api-key";
        Store(new()
        {
            ["oauth:active_provider"] = Encrypt("zai"), ["zcodejwttoken"] = Encrypt("jwt"), ["oauth:zai:access_token"] = Encrypt("access"),
            [old] = Encrypt("old-key"), ["account-provider:coding-plan:account:zai-individual-coding-plan:account:1:api-key"] = Encrypt("plan-key")
        });
        check("ZCode credentials decrypt to the active region, its plan key and MCP tokens",
            ZCodeUsage.ReadAccount(home, "fixture-secret") == new ZCodeAccount("zai", "plan-key", "jwt", "access"));
        Store(new() { ["oauth:active_provider"] = Encrypt("zai"), [old] = Encrypt("old-key") });
        check("ZCode never queries the active region with another region's leftover key", ZCodeUsage.ReadAccount(home, "fixture-secret") is { Family: "zai", ApiKey: "" });
        Store(new() { [old] = Encrypt("old-key") });
        check("Without an active region the stored key decides the region", ZCodeUsage.ReadAccount(home, "fixture-secret") is { Family: "bigmodel", ApiKey: "old-key" });

        // ZCode 3.18.2 keeps a random key in the system's credential store, named after the credentials file.
        var osKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var keyName = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(ZCodeUsage.Credentials(home))))) + ".ZCode Credential Encryption";
        var asked = new List<string>();
        string? Stored(string name) { asked.Add(name); return name == keyName ? B(osKey) : null; }
        Store(new() { ["oauth:active_provider"] = EncryptV2("bigmodel", "os", osKey), ["zcodejwttoken"] = EncryptV2("jwt-2", "os", osKey), ["oauth:bigmodel:access_token"] = EncryptV2("access-2", "os", osKey) });
        check("Credentials in the newer form decrypt with the key in the system's credential store",
            ZCodeUsage.ReadAccount(home, "fixture-secret", Stored) == new ZCodeAccount("bigmodel", "", "jwt-2", "access-2") && asked.SequenceEqual([keyName]));
        Store(new() { ["oauth:active_provider"] = Encrypt("zai"), ["zcodejwttoken"] = EncryptV2("jwt-2", "os", osKey), ["oauth:zai:access_token"] = Encrypt("access") });
        check("A file holding both forms is read with each value's own key", ZCodeUsage.ReadAccount(home, "fixture-secret", Stored) == new ZCodeAccount("zai", "", "jwt-2", "access"));
        Store(new() { ["zcodejwttoken"] = EncryptV2("jwt-env", "env", key) });
        check("The newer form with a managed secret uses that secret's hash", ZCodeUsage.ReadAccount(home, "fixture-secret", _ => null) is { Jwt: "jwt-env" });
        bool Refused(Func<string, string?> store)
        {
            try { ZCodeUsage.ReadAccount(home, "fixture-secret", store); return false; }
            catch (InvalidDataException) { return true; }
        }
        Store(new() { ["zcodejwttoken"] = EncryptV2("jwt-2", "os", osKey) });
        check("Without its key the newer form is refused", Refused(_ => null));
        check("A key of other credentials is refused by its fingerprint", Refused(_ => B(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        Store(new() { ["zcodejwttoken"] = "enc:v3:" + Seal("jwt", key) });
        check("An unknown encrypted form is refused instead of being used as the credential", Refused(Stored));
        Store(new() { ["zcodejwttoken"] = EncryptV2("jwt-2", "os", osKey).Replace("enc:v2:os:", "enc:v2:disk:") });
        check("An unknown key source is refused", Refused(Stored));
        Store(new() { ["zcodejwttoken"] = "plain-jwt" });
        check("A value that is not encrypted is read as it is", ZCodeUsage.ReadAccount(home, "fixture-secret", Stored) is { Jwt: "plain-jwt" });
        var mcp = ZCodeUsage.McpMetric(System.Text.Json.JsonDocument.Parse("""{"code":0,"data":{"server_time":1790200000,"next_refresh_at":1790208000,"level":"lite","total_usage":{"used":12,"limit":1000,"remaining":988}}}""").RootElement);
        check("ZCode MCP is a daily count with its refresh time", mcp is { Kind: MetricKind.Count, Label: "ZCode MCP", Used: 12, Total: 1000 } && mcp.ResetAt == DateTimeOffset.FromUnixTimeSeconds(1790208000));
        // A Start Plan with a one-time grant, a daily allowance and an ended plan.
        var startNow = DateTimeOffset.FromUnixTimeSeconds(1790200000);
        var startPlan = ZCodeUsage.StartPlan(System.Text.Json.JsonDocument.Parse("""
            {"code":0,"msg":"","data":{"server_time":1790200000,
              "plans":[
                {"user_plan_id":"upl_a","plan_id":"start-plan-example","name":"Example Plan","status":"active","starts_at":1790100000,"ends_at":1790500000,
                 "entitlements":[{"entitlement_id":"ent-a","show_name":"GLM-5.3-Flash","unit_type":"token","grant_units":1000000,"period":"one_time"}]},
                {"user_plan_id":"upl_b","name":"Daily","status":"active","ends_at":1791000000,"entitlements":[{"entitlement_id":"ent-b","period":"daily"}]},
                {"user_plan_id":"upl_c","name":"Old","status":"expired","ends_at":1790000000,"entitlements":[{"entitlement_id":"ent-c","period":"one_time"}]}],
              "balances":[
                {"user_plan_id":"upl_a","entitlement_id":"ent-a","show_name":"GLM-5.3-Flash","unit_type":"token","total_units":1000000,"used_units":25000,"remaining_units":975000,"period_end":1790500000,"expires_at":1790500000},
                {"user_plan_id":"upl_b","entitlement_id":"ent-b","show_name":"GLM-5.3","unit_type":"token","total_units":1000,"remaining_units":400,"period_end":1790280000,"expires_at":1791000000},
                {"user_plan_id":"upl_c","entitlement_id":"ent-c","show_name":"Old","unit_type":"token","total_units":10,"used_units":1,"expires_at":1790000000}]}}
            """).RootElement, startNow);
        check("ZCode Start Plan shows each active grant with its expiry and skips ended plans", startPlan.Plan == "Example Plan" && startPlan.Metrics.Count == 2 &&
            startPlan.Metrics[0] is { Kind: MetricKind.Count, Label: "GLM-5.3-Flash", Used: 25000, Total: 1000000, Unit: "令牌", ResetAt: null } &&
            startPlan.Metrics[0].ExpiresAt == DateTimeOffset.FromUnixTimeSeconds(1790500000));
        check("Recurring Start Plan allowances reset at the end of their period", startPlan.Metrics[1] is { Used: 600, Total: 1000 } &&
            startPlan.Metrics[1].ResetAt == DateTimeOffset.FromUnixTimeSeconds(1790280000));
        // A slow ZCode response is not awaited past the refresh, finishes in the background and is used afterwards.
        var latest = new LatestResponse(); int loads = 0;
        var slow = new TaskCompletionSource();
        bool Accept(System.Text.Json.JsonElement root) => root.GetProperty("code").GetInt32() == 0;
        System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        Func<CancellationToken, Task<System.Text.Json.JsonElement>> load = async _ => { loads++; await slow.Task; return Json("""{"code":0,"n":1}"""); };
        using (var shortWait = new CancellationTokenSource(50))
            check("A slow ZCode response does not hold up the first refresh", await latest.GetAsync("a", load, Accept, shortWait.Token) is null);
        slow.SetResult(); await Task.Delay(20);
        check("The finished response is used by the next refresh", await latest.GetAsync("a", load, Accept, new CancellationToken(true)) is not null && loads == 2 && latest.Error is null);
        check("A failed ZCode read keeps showing the last good result",
            await latest.GetAsync("a", _ => throw new HttpRequestException(null, null, System.Net.HttpStatusCode.Unauthorized), Accept, default) is { } failed && failed.GetProperty("n").GetInt32() == 1);
        check("The failed read's reason is kept", latest.Error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized });
        check("An error response never replaces the last good result",
            await latest.GetAsync("a", _ => Task.FromResult(Json("""{"code":3001}""")), Accept, default) is { } error && error.GetProperty("code").GetInt32() == 0 && latest.Error is InvalidDataException);
        var pending = new TaskCompletionSource<System.Text.Json.JsonElement>();
        check("A refresh with a kept result does not wait for a slow response",
            await latest.GetAsync("a", _ => pending.Task, Accept, default) is { } kept && kept.GetProperty("n").GetInt32() == 1);
        pending.SetResult(Json("""{"code":0,"n":2}""")); await Task.Delay(20);
        check("The newer response replaces the kept one once it arrives", await latest.GetAsync("a", _ => throw new HttpRequestException(), Accept, default) is { } newer && newer.GetProperty("n").GetInt32() == 2);
        check("Another ZCode account never sees the kept result", await latest.GetAsync("b", _ => throw new HttpRequestException(), Accept, default) is null);
        // ZCode 3.18.2 stores no plan key: the Coding Plan is read with the account's sign-in, and nothing is created for it.
        Store(new() { ["oauth:active_provider"] = "bigmodel", ["zcodejwttoken"] = "jwt", ["oauth:bigmodel:access_token"] = "sign-in" });
        var web = new Web(start.AddDays(1).AddHours(1).ToUnixTimeMilliseconds());
        using (var http = new HttpClient(web))
        using (var signedIn = new UsageService(desktop, locations, http, () => start.AddDays(1)))
        {
            var read = (await signedIn.RefreshAsync(settings, usageStart: start.LocalDateTime)).Single(s => s.Id == ProviderId.ZCode);
            check("Without a stored plan key the Coding Plan is read with the sign-in",
                read.Plan == "Lite" && read.Metrics.Any(m => m is { Window: 300, Percent: 14 }) && read.Metrics.Any(m => m is { Label: "工具调用", Used: 34, Total: 100 }));
            check("The sign-in is only used to ask, and goes bare to 智谱", web.Requests.All(r => r.Method == HttpMethod.Get) &&
                web.Requests.Any(r => r.Uri == "https://open.bigmodel.cn/api/monitor/usage/quota/limit" && r.Authorization == "sign-in"));
            web.NoPlan = true;
            read = (await signedIn.RefreshAsync(settings, usageStart: start.LocalDateTime)).Single(s => s.Id == ProviderId.ZCode);
            check("A sign-in without a Coding Plan is not shown as a failed read", read.Metrics.Count == 0 && read.StatusLabel == "未连接" && !read.IsStale);
            web.NoPlan = false; web.Down = true;
            read = (await signedIn.RefreshAsync(settings, usageStart: start.LocalDateTime)).Single(s => s.Id == ProviderId.ZCode);
            check("A failed read with the sign-in keeps showing the last Coding Plan reading", read.IsStale && read.Metrics.Any(m => m is { Window: 300, Percent: 14 }));
            // ZCode has no way to renew this sign-in, so a refused one can only be signed in again there; it is read anew on every refresh.
            web.Down = false; web.Expired = true;
            read = (await signedIn.RefreshAsync(settings, usageStart: start.LocalDateTime)).Single(s => s.Id == ProviderId.ZCode);
            check("A refused sign-in keeps the last reading and asks to sign in to ZCode again",
                read.IsStale && read.Metrics.Any(m => m is { Window: 300, Percent: 14 }) && read.Status.Contains("请在 ZCode 中重新登录"));
            Store(new() { ["oauth:active_provider"] = "bigmodel", ["zcodejwttoken"] = "jwt", ["oauth:bigmodel:access_token"] = "sign-in-again" });
            web.Expired = false;
            read = (await signedIn.RefreshAsync(settings, usageStart: start.LocalDateTime)).Single(s => s.Id == ProviderId.ZCode);
            check("After signing in to ZCode again the new sign-in is read without a restart", !read.IsStale && read.Metrics.Any(m => m is { Window: 300, Percent: 14 }) &&
                web.Requests.Last(r => r.Uri.EndsWith("/quota/limit")).Authorization == "sign-in-again");
        }
        Store(new() { ["oauth:active_provider"] = "zai", ["zcodejwttoken"] = "jwt", ["oauth:zai:access_token"] = "sign-in" });
        web = new Web(start.AddDays(1).AddHours(1).ToUnixTimeMilliseconds());
        using (var http = new HttpClient(web))
        using (var global = new UsageService(desktop, locations, http, () => start.AddDays(1)))
        {
            await global.RefreshAsync(settings, usageStart: start.LocalDateTime);
            check("The Z.ai sign-in is sent as a Bearer token", web.Requests.Any(r => r.Uri == "https://api.z.ai/api/monitor/usage/quota/limit" && r.Authorization == "Bearer sign-in"));
        }
        Store(new() { ["oauth:active_provider"] = "bigmodel", ["zcodejwttoken"] = "jwt", ["oauth:bigmodel:access_token"] = "sign-in",
            ["account-provider:coding-plan:account:bigmodel-individual-coding-plan:account:2:api-key"] = "plan-key" });
        web = new Web(start.AddDays(1).AddHours(1).ToUnixTimeMilliseconds());
        using (var http = new HttpClient(web))
        using (var keyed = new UsageService(desktop, locations, http, () => start.AddDays(1)))
        {
            await keyed.RefreshAsync(settings, usageStart: start.LocalDateTime);
            check("A stored plan key is still the one used", web.Requests.Where(r => r.Uri.EndsWith("/quota/limit")).Select(r => r.Authorization).SequenceEqual(["plan-key"]));
        }

        Store(new() { ["zcodejwttoken"] = "enc:v3:" + Seal("jwt", key) });
        snapshot = (await service.RefreshAsync(settings, usageStart: start.LocalDateTime)).Single(s => s.Id == ProviderId.ZCode);
        check("Credentials in an unknown form are reported as unreadable", snapshot.StatusLabel == "数据不可用" && snapshot.Status == "ZCode 凭据格式无法识别。");
        desktop.Result = null;
        snapshot = (await service.RefreshAsync(settings, usageStart: start.LocalDateTime)).Single(s => s.Id == ProviderId.ZCode);
        check("An unreadable ZCode database is reported as incomplete", !snapshot.UsageComplete && snapshot.Entries.Count == 0);
    }

    // Answers the Coding Plan quota request like 智谱 does for a Lite plan; every other address is not found.
    private sealed class Web(long reset) : HttpMessageHandler
    {
        public bool NoPlan { get; set; }
        public bool Down { get; set; }
        public bool Expired { get; set; }
        public List<(HttpMethod Method, string Uri, string Authorization)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            lock (Requests) Requests.Add((request.Method, uri, request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : ""));
            if (!uri.EndsWith("/api/monitor/usage/quota/limit")) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            if (Down) throw new HttpRequestException("offline");
            if (Expired) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
            var limits = NoPlan ? "" : $$$"""
                {"type":"TIME_LIMIT","unit":5,"number":1,"usage":100,"currentValue":34,"percentage":34,"nextResetTime":{{{reset}}}},
                {"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":14,"nextResetTime":{{{reset}}}}
                """;
            var body = """{"code":200,"msg":"操作成功","success":true,"data":{"level":"lite","limits":[""" + limits + "]}}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Rows(IReadOnlyList<string?[]> rows) : IDesktopSources
    {
        public IReadOnlyList<string?[]>? Result { get; set; } = rows;
        public string Sql { get; private set; } = "";
        public IReadOnlyList<string?[]>? QueryDatabase(string database, string sql) { Sql = sql; return Result; }
        public string? ReadCursorToken(string database) => null;
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
    }
}
