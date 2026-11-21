using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class VolcengineArkUsageTests
{
    private const string AfpResponse = """
    {
      "ResponseMetadata": { "Action": "GetAFPUsage", "Version": "2024-01-01" },
      "Result": {
        "PlanType": "pro",
        "AFPDaily": { "Quota": 500, "Used": 100 },
        "AFPFiveHour": { "Quota": 200, "Used": 49, "ResetTime": 1796000000 },
        "AFPWeekly": { "Quota": 1000, "Used": 512, "ResetTime": 1796500000 },
        "AFPMonthly": { "Quota": 3000, "Used": 990, "ResetTime": 1798000000 }
      }
    }
    """;

    private const string CodingPlanResponse = """
    {
      "ResponseMetadata": { "Action": "GetCodingPlanUsage", "Version": "2024-01-01" },
      "Result": {
        "QuotaUsage": [
          { "Level": "session", "Percent": 18, "ResetTimestamp": 1796001000 },
          { "Level": "weekly", "Percent": 40, "ResetTimestamp": 1796501000 },
          { "Level": "monthly", "Percent": 22, "ResetTimestamp": 1798001000 }
        ]
      }
    }
    """;

    private const string SeatListCodingResponse = """
    {
      "Result": { "Data": [ { "SeatID": "seat-c-1", "SeatStatus": "Active" }, { "SeatID": "seat-c-2", "SeatStatus": "Active" }, { "SeatID": "seat-old", "SeatStatus": "Released" } ] }
    }
    """;

    private const string SeatListAgentResponse = """
    {
      "Result": { "Data": [ { "SeatID": "seat-a-1", "SeatStatus": "Active" } ] }
    }
    """;

    private const string CodingSeatUsagesResponse = """
    {
      "Result": {
        "Data": [
          { "SeatID": "seat-c-1", "ShortTermUsage": 30, "WeeklyUsage": 50, "MonthlyUsage": 60, "ShortTermResetMilestone": 1796001500 },
          { "SeatID": "seat-c-2", "ShortTermUsage": 10, "WeeklyUsage": 20, "MonthlyUsage": 40, "ShortTermResetMilestone": 1796001200 }
        ]
      }
    }
    """;

    private const string SeatAfpUsageResponse = """
    {
      "Result": {
        "SeatAFPUsages": [
          {
            "SeatID": "seat-a-1",
            "PlanType": "enterprise",
            "AFPFiveHour": { "Quota": 400, "Used": 50, "ResetTime": 1796002000 },
            "AFPMonthly": { "Quota": 5000, "Used": 2250, "ResetTime": 1798002000 }
          }
        ]
      }
    }
    """;

    private const string BalanceResponse = """
    {
      "Result": { "AvailableBalance": "128.50", "CashBalance": "100.00" }
    }
    """;

    public static async Task Run(string root, Action<string, bool> check)
    {
        var previousLanguage = Loc.Language;
        Loc.Use(Loc.Chinese);
        try
        {
            var now = new DateTimeOffset(2026, 11, 21, 18, 0, 0, TimeSpan.Zero);

            // 1. Static AK/SK packing & unpacking
            var packed = VolcengineArkUsage.PackStaticSecret("  AKLT12345  ", "  SKSECRET67890  ");
            var creds = VolcengineArkUsage.ParseStaticSecret(packed);
            check("volcengine pack/parse json AK/SK", creds is { AccessKeyId: "AKLT12345", SecretAccessKey: "SKSECRET67890" });
            check("volcengine parse colon AK:SK", VolcengineArkUsage.ParseStaticSecret("AK1:SK1") is { AccessKeyId: "AK1", SecretAccessKey: "SK1" });
            check("volcengine parse two-line AK/SK", VolcengineArkUsage.ParseStaticSecret("AK2\nSK2") is { AccessKeyId: "AK2", SecretAccessKey: "SK2" });
            check("volcengine parse incomplete secret returns null", VolcengineArkUsage.ParseStaticSecret("ONLYONE") is null);
            check("volcengine credentials ToString hides secrets", !creds!.ToString().Contains("SKSECRET67890", StringComparison.Ordinal));

            // 2. Host pinning for OAuth login URLs
            check("volcengine login url allows signin.volcengine.com", VolcengineArkUsage.LoginUrlMatches(VolcengineArkUsage.China, "https://signin.volcengine.com/device?code=ABCD"));
            check("volcengine login url rejects http", !VolcengineArkUsage.LoginUrlMatches(VolcengineArkUsage.China, "http://signin.volcengine.com/device"));
            check("volcengine login url rejects foreign host", !VolcengineArkUsage.LoginUrlMatches(VolcengineArkUsage.China, "https://evil.example.com/device"));
            check("byteplus login url allows signin.byteplus.com", VolcengineArkUsage.LoginUrlMatches(VolcengineArkUsage.International, "https://signin.byteplus.com/device?code=EFGH"));
            check("byteplus login url rejects volcengine host", !VolcengineArkUsage.LoginUrlMatches(VolcengineArkUsage.International, "https://signin.volcengine.com/device"));

            // 3. V4 HMAC-SHA256 signature verification (no AWS4 prefix, X-Security-Token only for STS)
            var stsCreds = new VolcengineArkUsage.Credentials("AK_TEST", "SK_TEST", "STS_TOK_999");
            using (var signed = VolcengineArkUsage.SignRequest(VolcengineArkUsage.China, stsCreds, "ark", "2024-01-01", "GetAFPUsage", "{}", now))
            {
                var auth = signed.Headers.GetValues("Authorization").First();
                var xDate = signed.Headers.GetValues("X-Date").First();
                var xSha = signed.Headers.GetValues("X-Content-Sha256").First();
                var xTok = signed.Headers.GetValues("X-Security-Token").First();
                check("volcengine sign x-date UTC format", xDate == "20261121T180000Z");
                check("volcengine sign x-security-token present for STS", xTok == "STS_TOK_999");
                check("volcengine sign auth header scope", auth.StartsWith("HMAC-SHA256 Credential=AK_TEST/20261121/cn-beijing/ark/request, SignedHeaders=", StringComparison.Ordinal));
                check("volcengine sign x-content-sha256 matches empty json object", xSha == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("{}"))).ToLowerInvariant());
                var sigPart = auth.Split("Signature=")[1];
                check("volcengine sign signature is 64 hex chars", sigPart.Length == 64 && sigPart.All(char.IsAsciiHexDigitLower));
            }
            using (var staticSigned = VolcengineArkUsage.SignRequest(VolcengineArkUsage.International, creds, "billing", "2022-01-01", "QueryBalanceAcct", "{}", now))
            {
                check("byteplus sign omits X-Security-Token for static AK/SK", !staticSigned.Headers.Contains("X-Security-Token"));
                check("byteplus sign uses open.byteplusapi.com and ap-southeast-1",
                    staticSigned.RequestUri!.Host == "open.byteplusapi.com" &&
                    staticSigned.Headers.GetValues("Authorization").First().Contains("/ap-southeast-1/billing/request"));
            }

            // 4. Individual response parsers
            using (var doc = JsonDocument.Parse(AfpResponse))
            {
                var (metrics, plan) = VolcengineArkUsage.ParseAfpUsage(doc.RootElement.Get("Result"), "afp-personal");
                check("volcengine afp skips AFPDaily and keeps 3 windows", metrics.Count == 3 && plan == "Pro");
                check("volcengine afp 5h count and window", metrics[0].Kind == MetricKind.Count && metrics[0].Used == 49 && metrics[0].Total == 200 && metrics[0].Window == 300);
            }
            using (var doc = JsonDocument.Parse(CodingPlanResponse))
            {
                var metrics = VolcengineArkUsage.ParseCodingPlanUsage(doc.RootElement.Get("Result"), "coding-personal");
                check("volcengine coding plan parses 3 windows", metrics.Count == 3 && metrics[0].Percent == 18 && metrics[1].Percent == 40 && metrics[2].Percent == 22);
            }
            using (var doc = JsonDocument.Parse(SeatListCodingResponse))
            {
                var seatIds = VolcengineArkUsage.ExtractSeatIds(doc.RootElement.Get("Result"));
                check("volcengine extract seat ids skips Released seats", seatIds.Count == 2 && seatIds[0] == "seat-c-1" && seatIds[1] == "seat-c-2");
            }
            using (var doc = JsonDocument.Parse(BalanceResponse))
            {
                var balance = VolcengineArkUsage.ParseBalance(doc.RootElement.Get("Result"), "CNY");
                check("volcengine balance parses CNY amount", balance is { Kind: MetricKind.Balance, Amount: 128.50m, Currency: "CNY" });
            }

            // 5. ReadAsync simultaneous multi-plan (personal Agent + personal Coding + team Coding seats + team Agent seat + balance)
            using (var multiHttp = new HttpClient(new StubHandler(req =>
            {
                var query = req.RequestUri!.Query;
                if (query.Contains("Action=GetAFPUsage")) return JsonResponse(AfpResponse);
                if (query.Contains("Action=GetCodingPlanUsage")) return JsonResponse(CodingPlanResponse);
                if (query.Contains("Action=ListSeatInfos"))
                {
                    var reqBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    return JsonResponse(reqBody.Contains("agent_plan_enterprise") ? SeatListAgentResponse : SeatListCodingResponse);
                }
                if (query.Contains("Action=ListSeatInfoUsages")) return JsonResponse(CodingSeatUsagesResponse);
                if (query.Contains("Action=GetSeatAFPUsage")) return JsonResponse(SeatAfpUsageResponse);
                if (query.Contains("Action=QueryBalanceAcct")) return JsonResponse(BalanceResponse);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })))
            {
                var reading = await VolcengineArkUsage.ReadAsync(multiHttp, VolcengineArkUsage.China, creds, now, CancellationToken.None);
                check("volcengine multi-plan combines all plans and balance",
                    reading.Metrics.Any(m => m.Id.StartsWith("afp-personal")) &&
                    reading.Metrics.Any(m => m.Id.StartsWith("coding-personal")) &&
                    reading.Metrics.Any(m => m.Id.StartsWith("coding-seat")) &&
                    reading.Metrics.Any(m => m.Id.StartsWith("afp-seat")) &&
                    reading.Metrics.Any(m => m.Kind == MetricKind.Balance && m.Amount == 128.50m));
                check("volcengine multi-plan label names personal and team plans",
                    reading.Plan.Contains("Coding Plan") && reading.Plan.Contains("Agent Plan") && reading.Plan.Contains("团队"));
            }

            // 6. ReadAsync 403 AccessDenied on one endpoint does NOT block other plans; 401 throws RejectedException immediately
            using (var partialHttp = new HttpClient(new StubHandler(req =>
            {
                var query = req.RequestUri!.Query;
                if (query.Contains("Action=GetAFPUsage")) return JsonResponse("""{"ResponseMetadata":{"Error":{"Code":"AccessDenied"}}}""", HttpStatusCode.Forbidden);
                if (query.Contains("Action=GetCodingPlanUsage")) return JsonResponse(CodingPlanResponse);
                if (query.Contains("Action=ListSeatInfos")) return JsonResponse("""{"Result":{"Data":[]}}""");
                if (query.Contains("Action=QueryBalanceAcct")) return JsonResponse(BalanceResponse);
                return JsonResponse("""{"Result":{}}""");
            })))
            {
                var reading = await VolcengineArkUsage.ReadAsync(partialHttp, VolcengineArkUsage.China, creds, now, CancellationToken.None);
                check("volcengine 403 AccessDenied skipped without blocking coding plan or balance",
                    reading.Metrics.Count == 4 && reading.Metrics.Any(m => m.Kind == MetricKind.Balance));
            }
            bool rejected = false;
            using (var badCredHttp = new HttpClient(new StubHandler(_ => JsonResponse("""{"ResponseMetadata":{"Error":{"Code":"InvalidAccessKey"}}}""", HttpStatusCode.Unauthorized))))
            {
                try { await VolcengineArkUsage.ReadAsync(badCredHttp, VolcengineArkUsage.China, creds, now, CancellationToken.None); }
                catch (VolcengineArkUsage.RejectedException) { rejected = true; }
            }
            check("volcengine invalid credentials throw RejectedException immediately", rejected);

            // 7. OAuth Device Code Login + UsageService auto-refresh on expired STS
            int tokenCalls = 0;
            bool refreshedCalled = false;
            var secrets = new MemorySecrets();
            using (var oauthHttp = new HttpClient(new StubHandler(req =>
            {
                var uri = req.RequestUri!.AbsoluteUri;
                if (uri.EndsWith(VolcengineArkUsage.DeviceAuthPath))
                    return JsonResponse("""{"device_code":"dev-1","user_code":"ABCD-1234","verification_uri_complete":"https://signin.volcengine.com/device?user_code=ABCD-1234","expires_in":600,"interval":1}""");
                if (uri.EndsWith(VolcengineArkUsage.TokenPath))
                {
                    var form = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (form.Contains("grant_type=refresh_token"))
                    {
                        refreshedCalled = true;
                        var newAccess = JsonSerializer.Serialize(new { access_key_id = "AK_NEW", secret_access_key = "SK_NEW", session_token = "STS_NEW" });
                        return JsonResponse(JsonSerializer.Serialize(new { access_token = newAccess, refresh_token = "RT_NEW", expires_in = 3600 }));
                    }
                    tokenCalls++;
                    if (tokenCalls == 1) return JsonResponse("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest);
                    var firstAccess = JsonSerializer.Serialize(new { access_key_id = "AK_INIT", secret_access_key = "SK_INIT", session_token = "STS_INIT" });
                    return JsonResponse(JsonSerializer.Serialize(new { access_token = firstAccess, refresh_token = "RT_INIT", expires_in = 3600 }));
                }
                if (req.RequestUri.Query.Contains("Action=GetCodingPlanUsage")) return JsonResponse(CodingPlanResponse);
                if (req.RequestUri.Query.Contains("Action=QueryBalanceAcct")) return JsonResponse(BalanceResponse);
                return JsonResponse("""{"Result":{"Data":[]}}""");
            })))
            {
                using var service = new UsageService(new FakeDesktop(), DataLocations.Resolve(root, root, root, null, null), oauthHttp, () => now, secrets);
                string openedUrl = "";
                bool connected = await service.ConnectVolcengineAsync(ProviderId.VolcengineArk, url => { openedUrl = url; return true; }, CancellationToken.None);
                check("volcengine oauth device connect succeeds", connected && openedUrl.StartsWith("https://signin.volcengine.com/") && service.VolcengineConnected(ProviderId.VolcengineArk));

                // Expire the stored STS token to verify automatic refresh
                var expiring = new VolcengineArkUsage.Session("AK_OLD", "SK_OLD", "STS_OLD", "RT_INIT", now.AddSeconds(30));
                secrets.Write(VolcengineArkUsage.China.SessionKey, expiring.Serialize());
                var snaps = await service.RefreshAsync(new DeckSettings { Apps = [new AppEntry { Id = ProviderId.VolcengineArk }] });
                var snap = snaps.Single(s => s.Id == ProviderId.VolcengineArk);
                check("volcengine auto-refreshes expiring STS and reads live quota", refreshedCalled && snap.LiveQuota && snap.Metrics.Count >= 4);
                var savedAfter = VolcengineArkUsage.Session.Parse(secrets.Read(VolcengineArkUsage.China.SessionKey));
                check("volcengine persists renewed STS session", savedAfter is { AccessKeyId: "AK_NEW", RefreshToken: "RT_NEW" });

                service.DisconnectVolcengine(ProviderId.VolcengineArk);
                check("volcengine disconnect removes session", !service.VolcengineConnected(ProviderId.VolcengineArk));
            }

            // 8. ConfiguredProviders (manual AK/SK for VolcengineArkApi & BytePlusArkApi) + secret redaction
            var manualSecrets = new MemorySecrets();
            var entry = new AppEntry { Id = ProviderId.BytePlusArkApi };
            using (var manualHttp = new HttpClient(new StubHandler(req =>
            {
                if (req.RequestUri!.Query.Contains("Action=GetCodingPlanUsage")) return JsonResponse(CodingPlanResponse);
                if (req.RequestUri.Query.Contains("Action=QueryBalanceAcct")) return JsonResponse(BalanceResponse);
                return JsonResponse("""{"Result":{"Data":[]}}""");
            })))
            {
                using var configured = new ConfiguredProviders(manualSecrets, manualHttp, () => now);
                var missing = await configured.TestAsync(entry);
                check("byteplus manual missing AK/SK reports not configured", missing.Snapshot.StatusLabel == "未配置");

                manualSecrets.Write(entry.InstanceId, VolcengineArkUsage.PackStaticSecret("AK_BP_99", "SK_BP_SECRET_99"));
                var ok = await configured.TestAsync(entry);
                check("byteplus manual AK/SK reads live coding plan and USD balance",
                    ok.Snapshot.LiveQuota && ok.Snapshot.Metrics.Any(m => m.Kind == MetricKind.Balance && m.Currency == "USD" && m.Amount == 128.50m));
            }
            using (var rejectHttp = new HttpClient(new StubHandler(_ => JsonResponse("""{"ResponseMetadata":{"Error":{"Code":"SignatureDoesNotMatch"}}}""", HttpStatusCode.Unauthorized))))
            {
                using var configured = new ConfiguredProviders(manualSecrets, rejectHttp, () => now);
                var bad = await configured.TestAsync(entry);
                check("byteplus manual rejected AK/SK reports invalid key and never leaks secret",
                    bad.Snapshot.StatusLabel == "密钥无效" && !bad.Snapshot.Status.Contains("SK_BP_SECRET_99") && !bad.Snapshot.Status.Contains("AK_BP_99"));
            }
        }
        finally { Loc.Use(previousLanguage); }
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeDesktop : IDesktopSources
    {
        public string? ReadCursorToken(string database) => null;
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
    }

    private sealed class MemorySecrets : IProviderSecrets
    {
        private readonly Dictionary<Guid, string> _items = [];
        public string Read(Guid instanceId) => _items.GetValueOrDefault(instanceId, "");
        public void Write(Guid instanceId, string secret) { if (secret.Length == 0) _items.Remove(instanceId); else _items[instanceId] = secret; }
        public void Delete(Guid instanceId) => _items.Remove(instanceId);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
