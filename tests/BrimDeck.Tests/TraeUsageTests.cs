using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class TraeUsageTests
{
    private const string GlobalAuthJson = """
    {"token":"jwt-global-1","expiredAt":"2026-12-05T09:26:01Z","host":"https://growsg-normal.trae.ai","userId":"u-global-1","account":{"scope":"marscode","storeRegion":"SG"}}
    """;
    private const string CnAuthJson = """
    {"token":"jwt-cn-1","expiredAt":"2026-12-05T09:29:13Z","host":"https://api.trae.cn","userId":"u-cn-1","account":{"scope":"marscode","storeRegion":"CN"}}
    """;
    private const string SaasAuthJson = """
    {"token":"jwt-saas-1","expiredAt":"2026-12-05T09:29:13Z","host":"https://api.trae.cn","userId":"u-saas-1","account":{"scope":"saas"}}
    """;

    private const string GlobalDollarResponse = """
    {
      "billing_version": 3,
      "is_dollar_usage_billing": true,
      "user_entitlement_pack_list": [
        {
          "display_desc": "Pro plan",
          "entitlement_base_info": {
            "product_type": 1,
            "end_time": 1796083199,
            "quota": { "basic_usage_limit": 20, "bonus_usage_limit": 5, "no_bonus_quota": false }
          },
          "usage": { "basic_usage_amount": 4.25, "bonus_usage_amount": 1.5 }
        },
        {
          "display_desc": "Extra pack",
          "entitlement_base_info": {
            "product_type": 2,
            "end_time": 1797931749,
            "quota": { "basic_usage_limit": 10 }
          },
          "expire_time": 1797931749,
          "usage": { "basic_usage_amount": 2.0 }
        },
        {
          "display_desc": "On-Demand",
          "entitlement_base_info": { "product_type": 7, "quota": {} },
          "usage": { "pay_go_amount": 3.75 }
        },
        {
          "display_desc": "Promo",
          "entitlement_base_info": { "product_type": 3, "quota": { "basic_usage_limit": 99 } },
          "usage": { "basic_usage_amount": 0 }
        }
      ]
    }
    """;

    private const string CnCreditsResponse = """
    {
      "is_credits_billing": true,
      "is_dollar_usage_billing": false,
      "total_balance": 2094.37,
      "usage_summary": { "consumed_amount": 5.63, "total_amount": 2100 },
      "user_entitlement_pack_list": [
        {
          "display_desc": "免费",
          "entitlement_base_info": { "product_type": 0, "end_time": 1796054399, "quota": {} },
          "expire_time": 1796054399,
          "usage": {}
        },
        {
          "display_desc": "新用户福利",
          "group_name": "用户福利",
          "entitlement_base_info": { "product_type": 2, "end_time": 1797931749, "quota": { "credits_limit": 1500 } },
          "expire_time": 1797931749,
          "usage": {}
        },
        {
          "display_desc": "每月登录赠送",
          "group_name": "每月登录积分",
          "entitlement_base_info": { "product_type": 2, "end_time": 1796054399, "quota": { "credits_limit": 500 } },
          "expire_time": 1796054399,
          "usage": { "credits_amount": 5.6288 }
        },
        {
          "display_desc": "签到奖励",
          "group_name": "每日签到",
          "entitlement_base_info": { "product_type": 2, "end_time": 1797931816, "quota": { "credits_limit": 100 } },
          "expire_time": 1797931816,
          "usage": {}
        }
      ]
    }
    """;

    private const string CnSinglePackResponse = """
    {
      "is_credits_billing": true,
      "user_entitlement_pack_list": [
        {
          "display_desc": "Pro",
          "entitlement_base_info": { "product_type": 1, "end_time": 1796054399, "quota": { "credits_limit": 4000 } },
          "usage": { "credits_amount": 120.5 }
        }
      ]
    }
    """;

    public static async Task Run(string root, Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 11, 21, 19, 0, 0, TimeSpan.FromHours(8));
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(8));
        static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }

        // 1. byteCrypto round-trip, tamper rejection, plain JSON fallback.
        var encrypted = TraeUsage.EncryptAuth(GlobalAuthJson);
        check("TRAE byteCrypto encrypt/decrypt round-trip succeeds", TraeUsage.DecryptAuth(encrypted) == GlobalAuthJson);
        var tampered = Convert.FromBase64String(encrypted);
        tampered[^1] ^= 0x55;
        check("TRAE byteCrypto rejects tampered ciphertext", TraeUsage.DecryptAuth(Convert.ToBase64String(tampered)) is null);
        check("TRAE byteCrypto accepts plain legacy JSON", TraeUsage.DecryptAuth(GlobalAuthJson) == GlobalAuthJson);

        // 2. SignIn parsing and host pinning.
        var globalSignIn = TraeUsage.ParseSignIn(GlobalAuthJson, TraeUsage.International);
        check("TRAE international sign-in parses host, expiry and non-enterprise scope",
            globalSignIn is { Token: "jwt-global-1", Api: "https://growsg-normal.trae.ai", AccountId: "u-global-1", Enterprise: false } && !globalSignIn.Expired(now));
        var cnSignIn = TraeUsage.ParseSignIn(CnAuthJson, TraeUsage.China);
        check("TRAE CN sign-in parses host and non-enterprise scope",
            cnSignIn is { Token: "jwt-cn-1", Api: "https://api.trae.cn", AccountId: "u-cn-1", Enterprise: false });
        var saasSignIn = TraeUsage.ParseSignIn(SaasAuthJson, TraeUsage.China);
        check("TRAE SaaS scope is detected as enterprise", saasSignIn is { Enterprise: true });

        check("TRAE international host pinning accepts trae.ai subdomains and rejects others",
            TraeUsage.Api("https://api-sg-central.trae.ai", TraeUsage.International) == "https://api-sg-central.trae.ai"
            && TraeUsage.Api("https://api.trae.cn", TraeUsage.International) == TraeUsage.International.DefaultApi
            && TraeUsage.Api("http://api.trae.ai", TraeUsage.International) == TraeUsage.International.DefaultApi
            && TraeUsage.Api("https://evil.example", TraeUsage.International) == TraeUsage.International.DefaultApi);
        check("TRAE CN host pinning accepts trae.cn and rejects trae.ai or non-HTTPS",
            TraeUsage.Api("https://api.trae.cn", TraeUsage.China) == "https://api.trae.cn"
            && TraeUsage.Api("https://growsg-normal.trae.ai", TraeUsage.China) == TraeUsage.China.DefaultApi
            && TraeUsage.Api("https://api.trae.cn/extra", TraeUsage.China) == TraeUsage.China.DefaultApi);

        // 3. Quota parsing: international USD, CN credits (multi-pack and single-pack), legacy fast requests.
        var globalReading = TraeUsage.Parse(Json(GlobalDollarResponse), TraeUsage.International);
        check("TRAE international USD reading parses plan, bonus, add-on package, and PayGo while ignoring promo code",
            globalReading.Plan == "Pro"
            && globalReading.Metrics.Count == 4
            && globalReading.Metrics[0] is { Id: "plan", Kind: MetricKind.Spend, Currency: "USD", Total: 20m, Amount: 4.25m, ResetAt: not null }
            && globalReading.Metrics[1] is { Id: "bonus", Kind: MetricKind.Spend, Currency: "USD", Total: 5m, Amount: 1.5m }
            && globalReading.Metrics[2] is { Id: "package", Kind: MetricKind.Spend, Currency: "USD", Total: 10m, Amount: 2.0m, ExpiresAt: not null }
            && globalReading.Metrics[3] is { Id: "paygo", Kind: MetricKind.Spend, Currency: "USD", Amount: 3.75m, Unlimited: true });

        var cnReading = TraeUsage.Parse(Json(CnCreditsResponse), TraeUsage.China);
        check("TRAE CN multi-pack credits reading sums total credits and lists per-group breakdowns",
            cnReading.Plan == "Free"
            && cnReading.Metrics.Count == 4
            && cnReading.Metrics[0] is { Id: "credits", Kind: MetricKind.Count, Unit: "积分", Total: 2100m, Used: 5.63m }
            && cnReading.Metrics[1] is { Label: "用户福利", Total: 1500m, Used: 0m, ExpiresAt: not null }
            && cnReading.Metrics[2] is { Label: "每月登录积分", Total: 500m, Used: 5.63m }
            && cnReading.Metrics[3] is { Label: "每日签到", Total: 100m, Used: 0m });

        var cnSingle = TraeUsage.Parse(Json(CnSinglePackResponse), TraeUsage.China);
        check("TRAE CN single-pack credits reading shows only the total credits row",
            cnSingle.Plan == "Pro" && cnSingle.Metrics.Count == 1
            && cnSingle.Metrics[0] is { Id: "credits", Total: 4000m, Used: 120.5m, ResetAt: not null });

        // 4. SQLCipher 4 in-memory encryption/decryption round-trip and HMAC check.
        var fakeSqlite = new byte[4096 * 2];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(fakeSqlite, 0);
        fakeSqlite[16] = 0x10; fakeSqlite[17] = 0x00; // 4096-byte page size
        fakeSqlite[20] = 80; // reserved bytes per page
        Encoding.UTF8.GetBytes("hello-trae-page-1").CopyTo(fakeSqlite, 100);
        Encoding.UTF8.GetBytes("hello-trae-page-2").CopyTo(fakeSqlite, 4096 + 64);
        var cipherDb = TraeUsage.EncryptBytes(fakeSqlite);
        var plainDb = TraeUsage.DecryptBytes(cipherDb, null);
        check("TRAE SQLCipher 4 decrypts multi-page image faithfully",
            plainDb is not null && plainDb.SequenceEqual(fakeSqlite));
        var walSqlite = (byte[])fakeSqlite.Clone();
        walSqlite[18] = 2; walSqlite[19] = 2; // WAL mode, as Trae keeps its databases
        var walPlain = TraeUsage.DecryptBytes(TraeUsage.EncryptBytes(walSqlite), null);
        check("TRAE SQLCipher 4 marks a decrypted WAL-mode image as a rollback-journal database so SQLite can open it in memory",
            walPlain is not null && walPlain[18] == 1 && walPlain[19] == 1 && walPlain.AsSpan(20).SequenceEqual(walSqlite.AsSpan(20)));
        cipherDb[500] ^= 0x01;
        check("TRAE SQLCipher 4 rejects corrupted ciphertext page via HMAC",
            TraeUsage.DecryptBytes(cipherDb, null) is null);

        // 5. Token parsing and model name cleanup.
        var roaming = Path.Combine(root, "trae-roaming");
        var traeDb = Path.Combine(roaming, "Trae", "ModularData", "ai-agent", "database.db");
        var traeCnDb = Path.Combine(roaming, "Trae CN", "ModularData", "ai-agent", "database.db");
        Directory.CreateDirectory(Path.GetDirectoryName(traeDb)!);
        Directory.CreateDirectory(Path.GetDirectoryName(traeCnDb)!);
        File.WriteAllBytes(traeDb, []);
        File.WriteAllBytes(traeCnDb, []);

        var tokenDesktop = new Desktop
        {
            Rows =
            [
                // prompt=19317, completion=99 (includes reasoning=16), total=19416; model has __dev suffix
                ["13", "ses-1", "1795256142", "deepseek-v4.1-flash__dev", "19317", "0", null, "99", "0", null, "0", "0", "16", null, "19416"],
                // prompt=18505 includes cache_read=14136, completion=51, total=18556; the model called, with its __dollar__dev suffix
                ["4", "ses-1", "1795253197", "Dola-Seed-2.0-Code__dollar__dev", "18505", "0", null, "51", "0", null, "14136", "0", "31", null, "18556"],
                // duplicate row with same id + session must be deduplicated
                ["4", "ses-1", "1795253197", "Dola-Seed-2.0-Code__dollar__dev", "18505", "0", null, "51", "0", null, "14136", "0", "31", null, "18556"]
            ]
        };
        var tokenSnap = new ProviderSnapshot(ProviderId.Trae);
        TraeUsage.ReadTokens(tokenSnap, tokenDesktop, roaming, TraeUsage.International, start, now);
        check("TRAE token reader reconciles cached input, reasoning-inclusive output, cleans __dev suffix and deduplicates",
            tokenSnap.UsageAvailable && tokenSnap.UsageComplete && tokenSnap.Entries.Count == 2
            && tokenSnap.Entries[0] is { Key: "trae:ses-1:4", Model: "Dola-Seed-2.0-Code", Input: 4369, CacheRead: 14136, Output: 51, Total: 18556 }
            && tokenSnap.Entries[1] is { Key: "trae:ses-1:13", Model: "deepseek-v4.1-flash", Input: 19317, CacheRead: 0, Output: 99, Total: 19416 });
        // TRAE shows Dola-Seed-2.0-Code as "Seed-2.1-Turbo"; the records name the model called.
        int Column(string field) => TraeUsage.TokenQuery.IndexOf($"model_info.{field}'", StringComparison.Ordinal);
        check("TRAE token records name the model called before the name TRAE shows for it",
            Column("model_name") >= 0 && Column("model_name") < Column("config_name") && Column("config_name") < Column("display_model_name"));

        var unreadable = new ProviderSnapshot(ProviderId.TraeCn);
        TraeUsage.ReadTokens(unreadable, new Desktop { Rows = null }, roaming, TraeUsage.China, start, now);
        check("An unreadable TRAE CN database marks token usage incomplete", unreadable.UsageAvailable && !unreadable.UsageComplete);

        // 6. End-to-end UsageService refresh for TRAE and TRAE CN.
        var requests = new List<(string Url, string? Auth, string Body)>();
        bool rejected = false;
        using var http = new HttpClient(new Site(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            requests.Add((request.RequestUri!.ToString(), request.Headers.TryGetValues("Authorization", out var vals) ? vals.FirstOrDefault() : null, body));
            if (rejected) return (HttpStatusCode.Unauthorized, """{"code":401,"message":"unauthorized"}""");
            return request.RequestUri.Host switch
            {
                "growsg-normal.trae.ai" => (HttpStatusCode.OK, GlobalDollarResponse),
                "api.trae.cn" => (HttpStatusCode.OK, CnCreditsResponse),
                _ => (HttpStatusCode.NotFound, "{}")
            };
        }));

        using var service = new UsageService(tokenDesktop, new DataLocations("", "", "", roaming, ""), http, () => now);
        var globalSettings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Trae }] };
        globalSettings.Normalize();
        var cnSettings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.TraeCn }] };
        cnSettings.Normalize();

        var none = (await service.RefreshAsync(globalSettings)).Single(s => s.Id == ProviderId.Trae);
        check("Without storage.json nothing is sent and TRAE asks to sign in",
            none.StatusLabel == Loc.T("未连接", "Not connected") && requests.Count == 0);

        void WriteStorage(string folder, string authJson)
        {
            var path = TraeUsage.StorageFile(Path.Combine(roaming, folder));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = JsonSerializer.Serialize(new Dictionary<string, string> { [TraeUsage.AuthKey] = TraeUsage.EncryptAuth(authJson) });
            File.WriteAllText(path, payload);
        }

        WriteStorage("Trae", GlobalAuthJson);
        WriteStorage("Trae CN", CnAuthJson);

        var liveGlobal = (await service.RefreshAsync(globalSettings)).Single(s => s.Id == ProviderId.Trae);
        check("TRAE reads international USD quota with Cloud-IDE-JWT header",
            liveGlobal.LiveQuota && liveGlobal.Plan == "Pro" && liveGlobal.Metrics.Count == 4
            && requests.Count == 1
            && requests[0].Url == "https://growsg-normal.trae.ai/trae/api/v1/pay/ide_user_ent_usage"
            && requests[0].Auth == "Cloud-IDE-JWT jwt-global-1");

        requests.Clear();
        var liveCn = (await service.RefreshAsync(cnSettings)).Single(s => s.Id == ProviderId.TraeCn);
        check("TRAE CN reads credits quota via v2 endpoint with Cloud-IDE-JWT header",
            liveCn.LiveQuota && liveCn.Plan == "Free" && liveCn.Metrics.Count == 4
            && requests.Count == 1
            && requests[0].Url == "https://api.trae.cn/trae/api/v2/pay/ide_user_ent_usage"
            && requests[0].Auth == "Cloud-IDE-JWT jwt-cn-1"
            && requests[0].Body.Contains("\"req_source\":1"));

        requests.Clear();
        rejected = true;
        var refused = (await service.RefreshAsync(cnSettings)).Single(s => s.Id == ProviderId.TraeCn);
        check("A 401 from TRAE CN reports sign-in expired and keeps the previous reading as stale",
            refused.StatusLabel == Loc.T("登录已失效", "Sign-in expired") && refused.IsStale && refused.Metrics.Count == 4);

        requests.Clear();
        rejected = false;
        WriteStorage("Trae CN", SaasAuthJson);
        var enterprise = (await service.RefreshAsync(cnSettings)).Single(s => s.Id == ProviderId.TraeCn);
        check("A SaaS team/enterprise TRAE account reports not supported without sending a personal quota request",
            enterprise.StatusLabel == Loc.T("暂不支持", "Not supported") && requests.Count == 0);
    }

    private sealed class Site(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Desktop : IDesktopSources
    {
        public IReadOnlyList<string?[]>? Rows { get; init; } = [];
        public string? ReadCursorToken(string database) => throw new InvalidOperationException("Unexpected credential read");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => throw new InvalidOperationException("Unexpected service discovery");
        public IReadOnlyList<string?[]>? QueryTraeDatabase(string database, string sql) => Rows;
    }
}
