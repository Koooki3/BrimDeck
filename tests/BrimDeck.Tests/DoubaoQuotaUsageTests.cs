using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class DoubaoQuotaUsageTests
{
    private const string CombinedSummaryJson = """
    {
      "code": 0,
      "data": {
        "member_info": {
          "hasActiveSubscription": true,
          "hasEnterpriseSubscription": true,
          "enterprise_type": 2,
          "plan_name": "Pro"
        },
        "window_limit_section": {
          "window_limit_groups": [
            {
              "feature_group": "chat",
              "feature_group_name": "对话与深度思考",
              "window_limits": [
                { "window_type": 1, "used_percent": 0, "less_than_one_percent": true, "end_time": 1796000000 },
                { "window_type": 2, "used_percent": 42.5, "end_time": 1796500000 }
              ]
            }
          ]
        },
        "enterprise_window_limit_section": {
          "window_limit_groups": [
            {
              "feature_group": "ent_pool",
              "feature_group_name": "团队算力池",
              "window_limits": [
                { "window_type": 4, "item_type": 1, "used_percent": 65.0, "end_time": 1798000000 }
              ]
            }
          ]
        },
        "enterprise_package_entitlement_groups": [
          {
            "feature_group": "ent_pool",
            "entitlements": [
              { "display": { "title": "高级推理" }, "total_used_percent": 24, "used_amount": 120, "total_amount": 500 },
              { "display": { "title": "文档协作" }, "total_used_percent": 50, "used_amount": 50, "total_amount": 100 }
            ]
          }
        ],
        "quota_package_section": {
          "window_limit_groups": [
            {
              "feature_group_name": "生图加油包",
              "window_limits": [
                { "window_type": 3, "used_amount": 30, "total_amount": 100, "end_time": 1799000000 }
              ]
            }
          ]
        },
        "incentive_quota_section": {
          "window_limit_groups": [
            {
              "feature_group_name": "邀请福利",
              "window_limits": [
                { "window_type": 3, "used_percent": 15, "end_time": 1799500000 }
              ]
            }
          ]
        }
      }
    }
    """;

    private const string ExemptionSummaryJson = """
    {
      "code": 0,
      "data": {
        "member_info": {
          "hasActiveSubscription": false,
          "hasEnterpriseSubscription": true,
          "enterprise_type": 1
        },
        "enterprise_window_limit_section": {
          "window_limit_groups": [
            {
              "window_limits": [
                { "window_type": 1, "used_percent": 10, "exemption": { "active": true, "end_time": 1796000000 } }
              ]
            }
          ]
        }
      }
    }
    """;

    private const string FreeSummaryJson = """
    {
      "code": 0,
      "data": {
        "member_info": {
          "hasActiveSubscription": false,
          "hasEnterpriseSubscription": false
        }
      }
    }
    """;

    public static async Task Run(string root, Action<string, bool> check)
    {
        var previousLanguage = Loc.Language;
        Loc.Use(Loc.Chinese);
        try
        {
            // 1. Session serialization, auth cookie detection, header building, secret safety
            var session = new DoubaoQuotaUsage.Session(new()
            {
                ["sessionid"] = "sess-secret-123",
                ["passport_csrf_token_default"] = "csrf-fallback-1",
                ["bad"] = "line1\r\nEvil: 1"
            }, "user-42");
            check("doubao session has auth cookie", DoubaoQuotaUsage.Session.HasAuthCookie(session.Cookies));
            check("doubao session hides secrets in ToString", !session.ToString().Contains("sess-secret-123", StringComparison.Ordinal));
            var parsed = DoubaoQuotaUsage.Session.Parse(session.Serialize());
            check("doubao session roundtrip", parsed is not null && parsed.Cookies["sessionid"] == "sess-secret-123" && parsed.Account == "user-42");
            check("doubao session cookie header skips invalid CRLF value", parsed!.CookieHeader().Contains("sessionid=sess-secret-123") && !parsed.CookieHeader().Contains("Evil"));
            check("doubao session csrf fallback", parsed.CsrfToken() == "csrf-fallback-1");
            check("doubao session rejects missing auth cookie", DoubaoQuotaUsage.Session.Parse("""{"Cookies":{"ttwid":"abc"}}""") is null);

            using (var cnReq = DoubaoQuotaUsage.BuildRequest(DoubaoQuotaUsage.China, DoubaoQuotaUsage.SummaryPath, parsed))
            {
                check("doubao cn request uri", cnReq.RequestUri!.AbsoluteUri == "https://www.doubao.com/alice/commerce/sale/subscription/quota/summary/");
                check("doubao cn request csrf header", cnReq.Headers.GetValues("x-csrf-token").First() == "csrf-fallback-1");
            }
            // Dola has no quota summary (404); its quota is in the subscription overview.
            using (var intlReq = DoubaoQuotaUsage.BuildRequest(DoubaoQuotaUsage.International, DoubaoQuotaUsage.International.QuotaPath, parsed))
            {
                check("dola reads the subscription overview, doubao the quota summary",
                    intlReq.RequestUri!.AbsoluteUri == "https://www.dola.com/alice/commerce/sale/subscription/overview/"
                    && DoubaoQuotaUsage.China.QuotaPath == DoubaoQuotaUsage.SummaryPath);
            }
            // The overview of a signed-in Dola account without a subscription, as www.dola.com answers it (2026-11-21, trimmed).
            using (var doc = JsonDocument.Parse("""
                {"data":{"current_subscription":{"display":{"product_name":"Dola Membership"},"status":0,"subscription_id":"0"},
                 "enterprise_package_entitlement_groups":[],"enterprise_subscription":{"sku_key":""},
                 "enterprise_window_limit_section":{"entitlement_count":0,"usage_exhausted":false,"window_limit_groups":[]},
                 "entitlements":[],"incentive_quota_entitlements":[],
                 "member_info":{"hasActiveSubscription":false,"hasEnterpriseSubscription":false,"has_business_subscription":false,"has_team_subscription":false,"usr_type":1},
                 "package_entitlement_groups":[],"quota_package_entitlement_groups":[],"window_limit_section":{}},"code":0,"msg":"","message":""}
                """))
            {
                var reading = DoubaoQuotaUsage.ParseSummary(doc.RootElement);
                check("dola overview of a free account reads as no quota", reading.FreeWithoutQuota && reading.Metrics.Count == 0);
            }
            foreach (var expiredAnswer in new[]
            {
                """{"data":null,"code":710012001,"msg":"登录已过期，请重新登录","message":"login invalid"}""",
                """{"data":null,"code":710012001,"msg":"Session expired. Log in again.","message":"login invalid"}"""
            })
            {
                bool expiredRejected = false;
                try { using var doc = JsonDocument.Parse(expiredAnswer); DoubaoQuotaUsage.ParseSummary(doc.RootElement); }
                catch (DoubaoQuotaUsage.RejectedException) { expiredRejected = true; }
                check("doubao and dola expired sign-in (710012001) is a refused sign-in", expiredRejected);
            }

            // 2. ParseSummary: simultaneous personal + team drill-down + creation pack + incentive reward
            using (var doc = JsonDocument.Parse(CombinedSummaryJson))
            {
                var reading = DoubaoQuotaUsage.ParseSummary(doc.RootElement);
                check("doubao combined not free", !reading.FreeWithoutQuota);
                check("doubao combined plan names both personal and team", reading.Plan.Contains("Pro") && reading.Plan.Contains("团队版"));
                check("doubao combined metric count includes all 6 active items", reading.Metrics.Count == 6);
                var first = reading.Metrics[0];
                check("doubao less_than_one_percent clamps 0 to 0.5", first.Percent == 0.5 && first.Window == 300);
                var second = reading.Metrics[1];
                check("doubao personal 7d window", second.Percent == 42.5 && second.Window == 10080);
                check("doubao enterprise drill-down absolute amounts included",
                    reading.Metrics.Any(m => m.Kind == MetricKind.Count && m.Used == 120 && m.Total == 500) &&
                    reading.Metrics.Any(m => m.Kind == MetricKind.Count && m.Used == 50 && m.Total == 100));
                check("doubao creation quota pack included", reading.Metrics.Any(m => m.Kind == MetricKind.Count && m.Used == 30 && m.Total == 100));
                check("doubao activity reward included", reading.Metrics.Any(m => m.Percent == 15));
            }

            // 3. ParseSummary: enterprise exemption (unlimited)
            using (var doc = JsonDocument.Parse(ExemptionSummaryJson))
            {
                var reading = DoubaoQuotaUsage.ParseSummary(doc.RootElement);
                check("doubao enterprise label", reading.Plan == "企业版");
                check("doubao exemption sets Unlimited flag", reading.Metrics.Count == 1 && reading.Metrics[0].Unlimited && reading.Metrics[0].Percent == 0);
            }

            // 4. ParseSummary: signed-in free account without subscriptions
            using (var doc = JsonDocument.Parse(FreeSummaryJson))
            {
                var reading = DoubaoQuotaUsage.ParseSummary(doc.RootElement);
                check("doubao free account detected", reading.FreeWithoutQuota && reading.Metrics.Count == 0);
            }

            // 5. ParseSummary: unauthenticated business error throws RejectedException
            bool rejected = false;
            try
            {
                using var doc = JsonDocument.Parse("""{"code":10001,"msg":"not login"}""");
                DoubaoQuotaUsage.ParseSummary(doc.RootElement);
            }
            catch (DoubaoQuotaUsage.RejectedException) { rejected = true; }
            check("doubao unauthenticated code throws RejectedException", rejected);

            // 6. UsageService integration (Doubao & Dola, plus stale retention on 401)
            var now = new DateTimeOffset(2026, 11, 21, 18, 0, 0, TimeSpan.FromHours(8));
            var status = HttpStatusCode.OK;
            var body = CombinedSummaryJson;
            string lastHost = "", lastPath = "";
            using var http = new HttpClient(new StubHandler(req =>
            {
                lastHost = req.RequestUri!.Host; lastPath = req.RequestUri.AbsolutePath;
                return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }));
            var desktop = new FakeDesktop();
            using var service = new UsageService(desktop, DataLocations.Resolve(root, root, root, null, null), http, () => now);
            var settings = new DeckSettings { Apps = [new AppEntry { Id = ProviderId.Doubao }, new AppEntry { Id = ProviderId.Dola }] };

            // Not signed in
            var initial = await service.RefreshAsync(settings);
            check("doubao unsigned reports not connected", initial.Single(s => s.Id == ProviderId.Doubao).StatusLabel == "未连接");
            check("dola unsigned reports not connected", initial.Single(s => s.Id == ProviderId.Dola).StatusLabel == "未连接");

            // Signed in to Doubao + Dola
            desktop.Sessions[ProviderId.Doubao] = parsed.Serialize();
            desktop.Sessions[ProviderId.Dola] = parsed.Serialize();
            var live = await service.RefreshAsync(settings);
            var doubaoSnap = live.Single(s => s.Id == ProviderId.Doubao);
            check("doubao live refresh succeeds", doubaoSnap.LiveQuota && doubaoSnap.Metrics.Count == 6 && !doubaoSnap.IsStale);

            // 401 keeps stale reading
            status = HttpStatusCode.Unauthorized;
            var expired = await service.RefreshAsync(new DeckSettings { Apps = [new AppEntry { Id = ProviderId.Doubao }] });
            var staleSnap = expired.Single(s => s.Id == ProviderId.Doubao);
            check("doubao 401 reports sign-in expired with stale reading", staleSnap.StatusLabel == "登录已失效" && staleSnap.IsStale && staleSnap.Metrics.Count == 6);

            // Free account on Dola
            status = HttpStatusCode.OK;
            body = FreeSummaryJson;
            var freeDola = await service.RefreshAsync(new DeckSettings { Apps = [new AppEntry { Id = ProviderId.Dola }] });
            check("dola hits www.dola.com's subscription overview and reports no quota on free tier", lastHost == "www.dola.com" && lastPath == DoubaoQuotaUsage.OverviewPath
                && freeDola.Single(s => s.Id == ProviderId.Dola).StatusLabel == "无额度");
        }
        finally { Loc.Use(previousLanguage); }
    }

    private sealed class FakeDesktop : IDesktopSources
    {
        public Dictionary<ProviderId, string> Sessions { get; } = [];
        public string? ReadCursorToken(string database) => null;
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
        public string? ReadDoubaoQuotaSession(ProviderId id) => Sessions.GetValueOrDefault(id);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
