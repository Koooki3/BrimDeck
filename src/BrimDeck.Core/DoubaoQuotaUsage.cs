using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

// Doubao (豆包 & 豆包工作, www.doubao.com, ProviderId.Doubao) and its international edition Dola (formerly Cici, www.dola.com,
// ProviderId.Dola). Doubao and DoubaoWork share the same account, subscription and quota backend; Dola has a separate account
// and the same commerce gateway under www.dola.com, but not its quota summary (404, checked 2026-11-21): Dola's quota is read from
// the subscription overview, whose data carries the same member_info and window sections (Edition.QuotaPath). Neither desktop
// app exposes local or cloud token counts, so both are quota-only sources.
//
// Sign-in. The desktop apps lock their Chromium Cookies database while running, so BrimDeck signs in through an isolated browser
// profile (Native.DoubaoQuotaAccount, like MiMoAccount) and stores the captured session cookies encrypted with DPAPI on this PC.
// Cookies are sent only to the edition's own origin over HTTPS.
//
// Quota. POST <origin>/alice/commerce/sale/subscription/quota/summary/ {"product_line":"membership"}.
// The response's data holds:
//   - member_info: { hasActiveSubscription, hasEnterpriseSubscription, enterprise_type (1=BUSINESS, 2=TEAM), ... }
//   - window_limit_section: personal subscription windows (window_limit_groups[].window_limits[])
//   - enterprise_window_limit_section + enterprise_package_entitlement_groups: team/enterprise subscription windows & drill-down
//   - quota_package_section: add-on / creation quota packages (window_type 3 = SubscriptionTotal)
//   - incentive_quota_section: activity reward quota (window_type 3 = SubscriptionTotal)
// Window types (OHI.ZI): 1 = H5 (5-hour / current window), 2 = D7 (7-day), 3 = SubscriptionTotal, 4 = M1 (monthly).
// Unlike the app's compact popover (which picks only enterprise or personal), BrimDeck displays every active subscription the
// account holds—personal, team/enterprise, add-on packages and activity rewards—together so nothing is hidden when an account
// holds both personal and team/enterprise plans.
public static class DoubaoQuotaUsage
{
    public sealed record Edition(ProviderId Id, string Origin, string Host, string LoginUrl, string SessionFile, IReadOnlyList<string> CookieDomains, bool China)
    {
        public string QuotaPath => China ? SummaryPath : OverviewPath;
    }

    public static readonly Edition China = new(
        ProviderId.Doubao, "https://www.doubao.com", "www.doubao.com", "https://www.doubao.com/chat/",
        "doubao-quota-session.dat", [".doubao.com", "www.doubao.com", "doubao.com"], true);

    public static readonly Edition International = new(
        ProviderId.Dola, "https://www.dola.com", "www.dola.com", "https://www.dola.com/chat/",
        "dola-quota-session.dat", [".dola.com", "www.dola.com", "dola.com"], false);

    public static Edition For(ProviderId id) => id == ProviderId.Dola ? International : China;

    public const string SummaryPath = "/alice/commerce/sale/subscription/quota/summary/";
    public const string OverviewPath = "/alice/commerce/sale/subscription/overview/";
    private const string MembershipBody = """{"product_line":"membership"}""";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36";

    private const int WindowH5 = 1, WindowD7 = 2, WindowSubscriptionTotal = 3, WindowM1 = 4;
    private const int EnterpriseBusiness = 1, EnterpriseTeam = 2;

    // Cookies captured from the sign-in window; never shown or logged.
    public sealed record Session(Dictionary<string, string> Cookies, string Account = "")
    {
        public string Serialize() => JsonSerializer.Serialize(this);
        public static Session? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                var session = JsonSerializer.Deserialize<Session>(text);
                return session is { Cookies.Count: > 0 } && HasAuthCookie(session.Cookies) ? session : null;
            }
            catch (JsonException) { return null; }
        }
        public static bool HasAuthCookie(IReadOnlyDictionary<string, string> cookies) =>
            cookies.ContainsKey("sessionid") || cookies.ContainsKey("sessionid_ss") || cookies.ContainsKey("sid_tt");
        public string CookieHeader() => string.Join("; ", Cookies.Where(kv => ValidCookiePart(kv.Key) && ValidCookiePart(kv.Value)).Select(kv => $"{kv.Key}={kv.Value}"));
        public string CsrfToken() => Cookies.TryGetValue("passport_csrf_token", out var token) && token.Length > 0 ? token
            : Cookies.TryGetValue("passport_csrf_token_default", out var fallback) ? fallback : "";
        public override string ToString() => nameof(Session);
    }

    private static bool ValidCookiePart(string s) => s.Length is > 0 and < 8192 && !s.Any(c => c is '\r' or '\n' or ';');

    public sealed class RejectedException() : Exception("Sign-in rejected");

    public sealed record Reading(List<UsageMetric> Metrics, string Plan, bool FreeWithoutQuota);

    public static HttpRequestMessage BuildRequest(Edition edition, string path, Session session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, edition.Origin + path);
        request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader());
        request.Headers.TryAddWithoutValidation("Origin", edition.Origin);
        request.Headers.TryAddWithoutValidation("Referer", edition.LoginUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (session.CsrfToken() is { Length: > 0 } csrf)
            request.Headers.TryAddWithoutValidation("x-csrf-token", csrf);
        request.Content = new StringContent(MembershipBody, Encoding.UTF8, "application/json");
        return request;
    }

    public static async Task<Reading> ReadAsync(HttpClient http, Edition edition, Session session, CancellationToken ct)
    {
        using var request = BuildRequest(edition, edition.QuotaPath, session);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new RejectedException();
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParseSummary(doc.RootElement);
    }

    // Parses the response of /alice/commerce/sale/subscription/quota/summary/.
    public static Reading ParseSummary(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        CheckCode(root);
        var data = root.Get("data");
        if (data.ValueKind != JsonValueKind.Object) data = root;

        var memberInfo = data.Get("member_info");
        var personalSection = data.Get("window_limit_section");
        var enterpriseSection = data.Get("enterprise_window_limit_section");
        var enterpriseGroups = data.Get("enterprise_package_entitlement_groups");
        var packageSection = data.Get("quota_package_section");
        var incentiveSection = data.Get("incentive_quota_section");

        var personalWindows = CollectWindows(personalSection);
        var enterpriseWindows = CollectEnterpriseWindows(enterpriseSection, enterpriseGroups);
        var addonPackages = CollectWindows(packageSection).Where(w => w.WindowType == WindowSubscriptionTotal).ToList();
        var incentivePackages = CollectWindows(incentiveSection).Where(w => w.WindowType == WindowSubscriptionTotal).ToList();

        bool hasPersonal = memberInfo.Get("hasActiveSubscription").ValueKind == JsonValueKind.True || personalWindows.Count > 0;
        bool hasEnterprise = memberInfo.Get("hasEnterpriseSubscription").ValueKind == JsonValueKind.True || enterpriseWindows.Count > 0;
        bool bothScopes = hasPersonal && hasEnterprise;

        string enterpriseLabel = EnterpriseLabel(memberInfo, enterpriseSection, enterpriseGroups);
        var plans = new List<string>();
        if (hasPersonal) plans.Add(PlanName(memberInfo, personalSection, "个人版"));
        if (hasEnterprise) plans.Add(enterpriseLabel);

        var metrics = new List<UsageMetric>();
        int seq = 0;

        void AddWindow(WindowItem w, string prefix, string? labelOverride = null, string? idPrefix = null)
        {
            var baseLabel = labelOverride is { Length: > 0 } ? labelOverride : WindowLabel(w.WindowType, w.Label);
            var fullLabel = prefix.Length > 0 ? $"{prefix} · {baseLabel}" : baseLabel;
            var id = (idPrefix ?? "m") + "-" + (++seq).ToString(CultureInfo.InvariantCulture);
            metrics.Add(ToMetric(w, id, fullLabel));
        }

        // 1. Personal subscription windows (5-hour, 7-day, monthly).
        if (personalWindows.Count > 0)
        {
            string prefix = bothScopes ? "个人版" : "";
            foreach (var w in OrderWindows(personalWindows))
                AddWindow(w, prefix, idPrefix: "personal");
        }

        // 2. Team / Enterprise subscription windows (including drill-down entitlement packages when present).
        if (enterpriseWindows.Count > 0)
        {
            string prefix = bothScopes ? enterpriseLabel : "";
            foreach (var w in OrderWindows(enterpriseWindows))
            {
                string? labelOverride = w.WindowType == WindowM1 && w.Label.Length == 0 ? enterpriseLabel : null;
                AddWindow(w, prefix, labelOverride, "enterprise");
            }
        }

        // 3. Add-on / creation quota packages.
        foreach (var w in addonPackages)
            AddWindow(w, "", w.Label.Length > 0 ? w.Label : "创作额度包", "addon");

        // 4. Activity reward packages.
        foreach (var w in incentivePackages)
            AddWindow(w, "", w.Label.Length > 0 ? w.Label : "活动奖励", "incentive");

        if (metrics.Count == 0)
        {
            // Signed-in free account without paid subscription or quota packages.
            return new([], plans.Count > 0 ? string.Join(" · ", plans) : Loc.T("免费版", "Free"), true);
        }

        return new(metrics, string.Join(" · ", plans.Distinct(StringComparer.Ordinal)), false);
    }

    private static void CheckCode(JsonElement root)
    {
        var code = root.Get("code");
        if (code.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
        double? num = code.Number();
        if (num == 0) return;
        string msg = root.Get("msg").Text();
        if (msg.Length == 0) msg = root.Get("message").Text();
        // Common Doubao / ByteDance AGW unauthenticated codes or messages. Both sites answer an expired or missing sign-in with
        // 710012001, message "login invalid" (msg "登录已过期，请重新登录" / "Session expired. Log in again.").
        if (num is 1000 or 10001 or 10004 or 401 or 403 or 70001 or 710012001
            || root.Get("message").Text().Contains("login", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("login", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("未登录", StringComparison.Ordinal))
            throw new RejectedException();
        throw new InvalidDataException(msg.Length > 0 ? msg : $"code {num}");
    }

    private sealed record WindowItem(int WindowType, string Label, double UsedPercent, bool LessThanOnePercent,
        bool Exhausted, bool Unlimited, decimal? UsedAmount, decimal? TotalAmount, DateTimeOffset? ResetAt);

    private static List<WindowItem> CollectWindows(JsonElement section)
    {
        var list = new List<WindowItem>();
        if (section.ValueKind != JsonValueKind.Object) return list;
        bool sectionExhausted = section.Get("usage_exhausted").ValueKind == JsonValueKind.True;
        foreach (var group in section.Get("window_limit_groups").Items())
        {
            var groupName = group.Get("feature_group_name").Text().Trim();
            foreach (var item in group.Get("window_limits").Items())
                if (ParseWindow(item, groupName, sectionExhausted) is { } parsed) list.Add(parsed);
        }
        return list;
    }

    private static List<WindowItem> CollectEnterpriseWindows(JsonElement section, JsonElement entitlementGroups)
    {
        var list = new List<WindowItem>();
        if (section.ValueKind != JsonValueKind.Object) return list;
        bool sectionExhausted = section.Get("usage_exhausted").ValueKind == JsonValueKind.True;

        var byGroup = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var eg in entitlementGroups.Items())
        {
            var key = eg.Get("feature_group").Text();
            if (key.Length == 0) continue;
            byGroup[key] = eg.Get("entitlements").Items().ToList();
        }

        foreach (var group in section.Get("window_limit_groups").Items())
        {
            var featureGroup = group.Get("feature_group").Text();
            var groupName = group.Get("feature_group_name").Text().Trim();
            var drill = byGroup.GetValueOrDefault(featureGroup);

            foreach (var item in group.Get("window_limits").Items())
            {
                int itemType = (int)(item.Get("item_type").Number() ?? 0);
                int windowType = (int)(item.Get("window_type").Number() ?? 0);
                // Enterprise M1 buckets with item_type == 1 and multiple drill-down entitlements expand each entitlement.
                if (windowType == WindowM1 && itemType == 1 && drill is { Count: > 1 })
                {
                    foreach (var ent in drill)
                    {
                        var title = ent.Get("display").Get("title").Text().Trim();
                        var label = title.Length > 0 ? title : groupName;
                        var percent = Math.Clamp(DecimalDouble(ent.Get("total_used_percent")) ?? 0, 0, 100);
                        bool lt1 = ent.Get("total_less_than_one_percent").ValueKind == JsonValueKind.True;
                        var used = Decimal(ent.Get("used_amount"));
                        var total = Decimal(ent.Get("total_amount"));
                        bool exhausted = sectionExhausted || percent >= 100;
                        list.Add(new(WindowM1, label, percent, lt1, exhausted, false, used, total, null));
                    }
                    continue;
                }
                if (ParseWindow(item, groupName, sectionExhausted) is { } parsed) list.Add(parsed);
            }
        }
        return list;
    }

    private static WindowItem? ParseWindow(JsonElement item, string groupName, bool sectionExhausted)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        int windowType = (int)(item.Get("window_type").Number() ?? 0);
        if (windowType is not (WindowH5 or WindowD7 or WindowSubscriptionTotal or WindowM1)) return null;

        bool exemptionActive = windowType == WindowH5 && item.Get("exemption").Get("active").ValueKind == JsonValueKind.True;
        bool unlimited = exemptionActive || item.Get("unlimited").ValueKind == JsonValueKind.True;
        double usedPercent = Math.Clamp(DecimalDouble(item.Get("used_percent")) ?? 0, 0, 100);
        bool lt1 = item.Get("less_than_one_percent").ValueKind == JsonValueKind.True;
        bool exhausted = !unlimited && (sectionExhausted || item.Get("exhausted").ValueKind == JsonValueKind.True || usedPercent >= 100);
        if (exhausted) usedPercent = 100;
        if (unlimited) usedPercent = 0;

        decimal? usedAmount = Decimal(item.Get("used_amount"));
        decimal? totalAmount = Decimal(item.Get("total_amount"));

        DateTimeOffset? resetAt = null;
        if (windowType == WindowH5)
        {
            var rawEnd = exemptionActive ? item.Get("exemption").Get("end_time") : item.Get("end_time");
            resetAt = Epoch(rawEnd);
        }
        else
        {
            resetAt = Epoch(item.Get("end_time"));
        }

        return new(windowType, groupName, usedPercent, lt1, exhausted, unlimited, usedAmount, totalAmount, resetAt);
    }

    private static List<WindowItem> OrderWindows(List<WindowItem> windows) =>
        windows.OrderBy(w => w.WindowType switch { WindowH5 => 0, WindowD7 => 1, WindowM1 => 2, _ => 3 }).ToList();

    private static UsageMetric ToMetric(WindowItem w, string id, string label)
    {
        if (w.Unlimited)
            return new UsageMetric(MetricKind.Percent, label) { Id = id, Percent = 0, Unlimited = true, ResetAt = w.ResetAt, Window = WindowMinutes(w.WindowType) };
        if (w.TotalAmount is > 0 and var total && w.UsedAmount is { } used)
        {
            var spent = Math.Max(0m, used);
            return new UsageMetric(MetricKind.Count, label)
            {
                Id = id, Total = Math.Max(total, spent), Used = spent, ResetAt = w.ResetAt, Window = WindowMinutes(w.WindowType)
            };
        }
        double pct = w.LessThanOnePercent && w.UsedPercent <= 0 ? 0.5 : w.UsedPercent;
        return new UsageMetric(MetricKind.Percent, label) { Id = id, Percent = pct, ResetAt = w.ResetAt, Window = WindowMinutes(w.WindowType) };
    }

    private static int? WindowMinutes(int windowType) => windowType switch
    {
        WindowH5 => 300,
        WindowD7 => 10080,
        _ => null
    };

    private static string WindowLabel(int windowType, string groupName) => windowType switch
    {
        WindowH5 => "5 小时额度",
        WindowD7 => "每周额度",
        WindowM1 => groupName.Length > 0 ? groupName : "每月额度",
        WindowSubscriptionTotal => groupName.Length > 0 ? groupName : "创作额度包",
        _ => groupName.Length > 0 ? groupName : "套餐额度"
    };

    private static string PlanName(JsonElement memberInfo, JsonElement section, string fallback)
    {
        foreach (var key in new[] { "plan_name", "planName", "subscription_name", "subscriptionName", "tier_name", "tierName" })
        {
            var name = memberInfo.Get(key).Text().Trim();
            if (name.Length > 0) return name;
            name = section.Get(key).Text().Trim();
            if (name.Length > 0) return name;
        }
        return fallback;
    }

    private static string EnterpriseLabel(JsonElement memberInfo, JsonElement enterpriseSection, JsonElement groups)
    {
        int type = (int)(memberInfo.Get("enterprise_type").Number()
            ?? memberInfo.Get("enterpriseType").Number()
            ?? enterpriseSection.Get("enterprise_type").Number()
            ?? 0);
        if (type == EnterpriseBusiness) return "企业版";
        if (type == EnterpriseTeam) return "团队版";
        foreach (var eg in groups.Items())
        {
            var title = eg.Get("feature_group_name").Text().Trim();
            if (title.Contains("企业", StringComparison.Ordinal)) return "企业版";
            if (title.Contains("团队", StringComparison.Ordinal)) return "团队版";
        }
        return "团队版";
    }

    private static DateTimeOffset? Epoch(JsonElement value)
    {
        if (DecimalDouble(value) is not { } raw || raw <= 0) return null;
        try
        {
            return raw >= 1_000_000_000_000d
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)raw)
                : DateTimeOffset.FromUnixTimeSeconds((long)raw);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static double? DecimalDouble(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n :
        value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && double.IsFinite(n) ? n : null;

    private static decimal? Decimal(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n) ? Math.Round(n, 4) :
        value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n) ? Math.Round(n, 4) : null;
}
