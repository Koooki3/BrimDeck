using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

// Qianwen AI platform (platform.qianwenai.com, ProviderId.Qianwen) and its international edition QwenCloud (www.qwencloud.com,
// ProviderId.QwenCloud). An API key only calls models: neither edition answers a balance or quota request made with one. BrimDeck
// signs in to the account the way the editions' own CLIs (qianwen, qwencloud) do, and reads with that sign-in what they read.
//
// Sign-in (device flow with PKCE), at the edition's auth host, both requests bare POSTs:
//   /cli/device/code?client_id&code_challenge&code_challenge_method=S256 -> {Success, Data: {Token, VerificationUrl, ExpiresIn, Interval}}
//   /cli/device/token?client_id&token&code_verifier -> {Data: {Status: authorization_pending | complete | not_found | expired_token |
//     access_denied, Credentials: {AccessToken, ExpireTime, User}}}
// client_id is a random id of BrimDeck's own. The sign-in cannot be renewed: once it expires, the user signs in again.
//
// Account requests ("gateway"): POST <api>/data/v2/api.json with the access token as a bearer token and the JSON body {product,
// action, region, params, version?}, every param value a string. The reply is {code, data, successResponse}; code "200" is success,
// "ConsoleNeedLogin" a sign-in that is no longer accepted.
//
// Token Plan, product BssOpenAPI-V3:
//   GetSeatSubscriptionSummary {productCode: <team commodity>} -> {Data: {PlanName, PeriodEnd, SubscriptionGroupList: [{SpecType,
//     SubscriptionTotalNumber, EquityList: [{TotalValue, SurplusValue}], NextCycleFlushTime}]}}: the team edition by seats;
//     SurplusValue is what is left, NextCycleFlushTime (milliseconds) when the seats' credits renew. QwenCloud asks QuerySubscriptionGray
//     -> {IsGray} first and reads seats only when it is true.
//   DescribeFrInstances {Group: tokenPlan, CommodityCode, PageNum, PageSize} -> {Data: [{Status, TemplateName, CommodityName,
//     InitCapacityBaseValue, CurrCapacityBaseValue, CapacityTypeCode, periodCapacityBaseValue, EndTime}]}: plans bought before seats,
//     and the team add-on packs; CurrCapacityBaseValue is what is left.
//   The China edition's newer personal edition is read through cs-data (CsDataAsync): .../tokenplan/personal/api/v2/subscription
//     {commodityCode} -> {data: {status, specCode, endTime}}, absent when there is none; quota-config -> {data: {<specCode>: {monthly,
//     weekly}}}, the credits; usage -> {data: {per1MonthPercentage, per1MonthResetTime}} or the per1Week pair, the used share 0..1
//     and the reset in milliseconds.
// Pay-as-you-go: GetFundAccountAvailableAmount -> {AvailableAmount, Currency}, the China edition only; QwenCloud has no balance.
//   The month's charges: MaasDescribeCostAnalysis (China, the sum of GroupByTotal[].Amount) and MaasListConsumeSummary (QwenCloud,
//   daily rows). Both CLIs take the month as the calendar month in UTC.
public static class QianwenUsage
{
    public sealed record Edition(ProviderId Id, string Auth, string Api, string AccountHost, string Region, string Currency, Guid SessionKey,
        string TeamCode, string PersonalCode, string AddonCode, bool China);
    public static readonly Edition China = new(ProviderId.Qianwen, "https://t.qianwenai.com", "https://cli.qianwenai.com", "account.qianwenai.com", "cn-beijing", "CNY",
        new("5d0c7b4e-2a91-4f3c-8e6d-1b7a9c3f0e52"), "sfm_tokenplanteams_dp_cn", "sfm_tokenplanpersonal_dp_cn", "sfm_tokenplanteamsaddon_dp_cn", true);
    public static readonly Edition International = new(ProviderId.QwenCloud, "https://t.qwencloud.com", "https://cli.qwencloud.com", "account.qwencloud.com", "ap-southeast-1", "USD",
        new("b8e1f4a2-6c37-4d95-a0b8-3e2f7d1c9a64"), "sfm_tokenplanteams_dp_intl", "sfm_tokenplanpersonal_dp_intl", "sfm_tokenplanteamsaddon_dp_intl", false);
    public static Edition For(ProviderId id) => id == ProviderId.QwenCloud ? International : China;
    public const string CsData = "https://cs-data.qianwenai.com/cli/api.json";
    public const string SoloCode = "sfm_tokenplansolo_public_cn";
    private const string UserAgent = "BrimDeck/1.0";

    public sealed record Session(string AccessToken, DateTimeOffset? ExpiresAt, string Account)
    {
        public string Serialize() => JsonSerializer.Serialize(this);
        public static Session? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonSerializer.Deserialize<Session>(text) is { AccessToken.Length: > 0 } session ? session : null; }
            catch (JsonException) { return null; }
        }
        public bool Expired(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;
    }

    public sealed record Login(string ClientId, string Token, string Verifier, string Url, TimeSpan Interval, DateTimeOffset Until);

    // The sign-in is no longer accepted.
    public sealed class RejectedException() : Exception("Sign-in rejected");

    public sealed record Reading(List<UsageMetric> Metrics, string Plan, List<string> Warnings);

    // Starts a sign-in. The address to open must be the edition's own account page, so a changed answer never sends the browser elsewhere.
    public static async Task<Login> StartLoginAsync(HttpClient http, Edition edition, DateTimeOffset now, CancellationToken ct)
    {
        string clientId = Guid.NewGuid().ToString(), verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{edition.Auth}/cli/device/code?client_id={clientId}&code_challenge={challenge}&code_challenge_method=S256");
        using var doc = await SendAsync(http, request, ct);
        var data = doc.RootElement.Get("Data");
        string token = data.Get("Token").Text(), url = data.Get("VerificationUrl").Text();
        if (doc.RootElement.Get("Success").ValueKind != JsonValueKind.True || token.Length == 0)
            throw new InvalidDataException(Loc.T("暂时无法创建登录请求", "A sign-in cannot be started right now"));
        if (!LoginUrlMatches(edition, url)) throw new InvalidDataException(Loc.T("返回的登录地址无效", "The returned sign-in address is not valid"));
        var interval = TimeSpan.FromSeconds(Math.Clamp(data.Get("Interval").Number() ?? 2, 1, 30));
        var lifetime = TimeSpan.FromSeconds(Math.Clamp(data.Get("ExpiresIn").Number() ?? 300, 30, 1800));
        return new(clientId, token, verifier, url, interval, now + lifetime);
    }

    public static bool LoginUrlMatches(Edition edition, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals(edition.AccountHost, StringComparison.OrdinalIgnoreCase);

    // Waits until the user has allowed the sign-in in the browser. Null when the request ran out before that.
    public static async Task<Session?> WaitForLoginAsync(HttpClient http, Edition edition, Login login, Func<DateTimeOffset> clock, CancellationToken ct)
    {
        while (clock() < login.Until)
        {
            await Task.Delay(login.Interval, ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{edition.Auth}/cli/device/token?client_id={login.ClientId}&token={Uri.EscapeDataString(login.Token)}&code_verifier={login.Verifier}");
            JsonDocument doc;
            try { doc = await SendAsync(http, request, ct); }
            // A poll that could not get through, or took too long, is simply tried again.
            catch (HttpRequestException ex) when (ex.StatusCode is null || (int)ex.StatusCode >= 500) { continue; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            using (doc)
            {
                var data = doc.RootElement.Get("Data");
                var status = data.Get("Status").Text().ToLowerInvariant();
                if (status is "" or "authorization_pending" or "slow_down") continue;
                if (status == "access_denied") throw new InvalidDataException(Loc.T("登录已被拒绝", "The sign-in was refused"));
                if (status != "complete") return null;
                return ParseCredentials(data.Get("Credentials"))
                    ?? throw new InvalidDataException(Loc.T("没有返回登录凭据", "No sign-in was returned"));
            }
        }
        return null;
    }

    public static Session? ParseCredentials(JsonElement credentials)
    {
        var token = credentials.Get("AccessToken").Text();
        if (token.Length == 0) return null;
        DateTimeOffset? expires = DateTimeOffset.TryParse(credentials.Get("ExpireTime").Text(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
        var user = credentials.Get("User");
        var account = user.Get("Email").Text() is { Length: > 0 } email ? email : user.Get("AliyunId").Text();
        return new(token, expires, account);
    }

    // Reads every part the account has. A part that fails is named in the warnings while the others are shown; a refused sign-in
    // stops the reading.
    public static async Task<Reading> ReadAsync(HttpClient http, Edition edition, Session session, DateTimeOffset now, CancellationToken ct)
    {
        if (session.Expired(now)) throw new RejectedException();
        var client = new Gateway(http, edition, session.AccessToken, ct);
        var metrics = new List<UsageMetric>();
        var warnings = new List<string>();
        var plans = new List<string>();
        bool Failed(Exception ex) => ex is InvalidDataException or JsonException or HttpRequestException or FormatException or InvalidOperationException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested;
        async Task Part(Func<Task> read, string chinese, string english)
        {
            try { await read(); }
            catch (Exception ex) when (Failed(ex)) { warnings.Add(Loc.T(chinese + "暂时无法读取", english + " cannot be read right now")); }
        }

        await Part(async () =>
        {
            // The newer personal edition (China only) and the older one, each shown when the account has it. One that cannot be read
            // does not keep the other from being shown.
            Exception? solo = null;
            if (edition.China)
                try { if (await SoloAsync(client) is { } plan) { plans.Add(plan.Name); metrics.Add(plan.Metric); } }
                catch (Exception ex) when (Failed(ex)) { solo = ex; }
            if (Instances(await client.CallAsync("DescribeFrInstances", PlanQuery(edition.PersonalCode, 10)), "个人版") is { } older)
            { plans.Add(older.Name); metrics.Add(older.Metric with { Id = "personal-older" }); }
            if (solo is not null) throw new InvalidDataException(solo.Message, solo);
        }, "个人版 Token Plan ", "The personal Token Plan");

        await Part(async () =>
        {
            // Both requests describe the same team plan, as the CLIs read them: the seats when the account has them, otherwise the
            // plan as it was bought before seats. Seats that cannot be read still leave the older reading.
            // QwenCloud asks for seats only for accounts it has moved to them (QuerySubscriptionGray), as its CLI does; an account it
            // cannot tell about is read the older way.
            Plan? team = null; Exception? seats = null;
            if (edition.China || await SeatAccountAsync(client))
                try { team = Seats(await client.CallAsync("GetSeatSubscriptionSummary", new() { ["productCode"] = edition.TeamCode })); }
                catch (Exception ex) when (Failed(ex)) { seats = ex; }
            team ??= Instances(await client.CallAsync("DescribeFrInstances", PlanQuery(edition.TeamCode, 10)), "团队版");
            if (team is not null) { plans.Add(team.Name); metrics.Add(team.Metric); }
            var addon = AddonPacks(await client.CallAsync("DescribeFrInstances", PlanQuery(edition.AddonCode, 100)));
            if (addon is not null) metrics.Add(addon);
            if (team is null && seats is not null) throw new InvalidDataException(seats.Message, seats);
        }, "团队版 Token Plan ", "The team Token Plan");

        if (edition.China)
            await Part(async () => metrics.Add(Balance(await client.CallAsync("GetFundAccountAvailableAmount", []), edition.Currency)), "账户余额", "The balance");

        await Part(async () => metrics.Add(edition.China
            ? MonthCharge(CostAnalysisTotal(await client.CallAsync("MaasDescribeCostAnalysis", CostAnalysisQuery(now))), edition.Currency, now)
            : MonthCharge(await ConsumeTotalAsync(client, now), edition.Currency, now)), "本月扣费", "This month's charges");

        return new(metrics, string.Join(" · ", plans), warnings);
    }

    // A plan in force: its name for the plan line, and its quota.
    public sealed record Plan(string Name, UsageMetric Metric);

    private static Dictionary<string, string> PlanQuery(string code, int size) =>
        new() { ["Group"] = "tokenPlan", ["CommodityCode"] = code, ["PageNum"] = "1", ["PageSize"] = size.ToString(CultureInfo.InvariantCulture) };

    private static async Task<bool> SeatAccountAsync(Gateway client)
    {
        try { return (await client.CallAsync("QuerySubscriptionGray", [])).Get("IsGray").ValueKind == JsonValueKind.True; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or HttpRequestException or InvalidOperationException) { return false; }
    }

    // The newer personal edition: what was bought, its credits, and the share used in the current cycle.
    private static async Task<Plan?> SoloAsync(Gateway client)
    {
        var subscription = await client.CsDataAsync("zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/subscription", new() { ["commodityCode"] = SoloCode });
        if (subscription.ValueKind != JsonValueKind.Object) return null;
        var config = await client.CsDataAsync("zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/quota-config", []);
        var usage = await client.CsDataAsync("zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/usage", []);
        return Solo(subscription, config, usage);
    }

    public static Plan? Solo(JsonElement subscription, JsonElement config, JsonElement usage)
    {
        if (subscription.ValueKind != JsonValueKind.Object || subscription.Get("status").Text() is { Length: > 0 } status && !status.Equals("VALID", StringComparison.OrdinalIgnoreCase)) return null;
        var spec = subscription.Get("specCode").Text();
        var name = spec.ToLowerInvariant() switch { "lite" => "Lite", "essential" => "Essential", "standard" => "Standard", "pro" => "Pro", _ => spec };
        var expires = Millis(subscription.Get("endTime"));
        // No usage yet reads as nothing used in a monthly cycle.
        var (label, ratio, reset) = Cycle(usage);
        var credits = config.Get(spec);
        decimal? total = Amount(label == "每周额度" ? credits.Get("weekly") : credits.Get("monthly")) ?? Amount(credits.Get("weekly")) ?? Amount(credits.Get("monthly"));
        var full = "个人版 · " + label;
        var metric = total is > 0
            ? new UsageMetric(MetricKind.Count, full) { Id = "personal", Unit = "积分", Total = total, Used = Math.Round(total.Value * (decimal)ratio, 0), ResetAt = reset, ExpiresAt = expires }
            : new UsageMetric(MetricKind.Percent, full) { Id = "personal", Percent = ratio * 100, ResetAt = reset, ExpiresAt = expires };
        return new(name, metric);
    }

    // The used share of the current cycle (0 to 1), monthly first; data null means nothing used yet.
    public static (string Label, double Ratio, DateTimeOffset? Reset) Cycle(JsonElement usage)
    {
        foreach (var (label, share, reset) in new[] { ("每月额度", "per1MonthPercentage", "per1MonthResetTime"), ("每周额度", "per1WeekPercentage", "per1WeekResetTime") })
        {
            if (usage.Get(share).Number() is not { } ratio) continue;
            if (ratio is < 0 or > 1) throw new InvalidDataException();
            return (label, ratio, Millis(usage.Get(reset)));
        }
        if (usage.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return ("每月额度", 0, null);
        throw new InvalidDataException();
    }

    // The team edition by seats: the credits of every seat together, and when they renew. Null without seats.
    public static Plan? Seats(JsonElement data)
    {
        var root = data.Get("Data").ValueKind == JsonValueKind.Object ? data.Get("Data") : data;
        decimal total = 0, left = 0; DateTimeOffset? reset = null;
        var tiers = new List<string>();
        foreach (var group in root.Get("SubscriptionGroupList").Items())
        {
            var equity = group.Get("EquityList").Items().FirstOrDefault();
            total += Amount(equity.Get("TotalValue")) ?? Amount(group.Get("TotalValue")) ?? 0;
            left += Amount(equity.Get("SurplusValue")) ?? Amount(group.Get("SurplusValue")) ?? 0;
            reset ??= Millis(group.Get("NextCycleFlushTime")) ?? Date(group.Get("NextCycleFlushTime"));
            if (Tier(group.Get("SpecType").Text()) is { Length: > 0 } tier && !tiers.Contains(tier)) tiers.Add(tier);
        }
        if (total <= 0) return null;
        var end = Millis(root.Get("PeriodEnd")) ?? Date(root.Get("PeriodEnd")) ?? Millis(root.Get("EndTime")) ?? Date(root.Get("EndTime"));
        // The credits renew only while the plan lasts; a renewal after its end is not shown as a reset.
        if (reset is { } r && end is { } e && r > e) reset = null;
        var name = Loc.T("团队版", "Team") + (tiers.Count > 0 ? " " + string.Join(Loc.T("、", ", "), tiers) : root.Get("PlanName").Text() is { Length: > 0 } plan ? " " + plan : "");
        return new(name, new UsageMetric(MetricKind.Count, "团队版 · 每月额度") { Id = "team", Unit = "积分", Total = total, Used = Math.Max(0, total - left), ResetAt = reset, ExpiresAt = end });
    }

    // The seat tiers the team overview names: standard, advanced and premium seats.
    public static string Tier(string spec) => spec.ToLowerInvariant() switch
    {
        "standard" => Loc.T("标准席位", "Standard seats"), "pro" => Loc.T("高级席位", "Advanced seats"), "max" => Loc.T("尊享席位", "Premium seats"), _ => ""
    };

    // A plan bought before seats: the first valid one, its total and what is left.
    public static Plan? Instances(JsonElement data, string edition)
    {
        var items = data.Get("Data").Items().ToList();
        var item = items.FirstOrDefault(Valid);
        if (item.ValueKind != JsonValueKind.Object) return null;
        var (total, left) = Capacity(item);
        if (total is not > 0) return null;
        var name = item.Get("TemplateName").Text() is { Length: > 0 } template ? template : item.Get("CommodityName").Text();
        var label = edition + " · " + "套餐额度";
        return new(name.Length > 0 ? name : edition == "团队版" ? Loc.T("团队版", "Team") : "Token Plan",
            new UsageMetric(MetricKind.Count, label) { Id = edition == "团队版" ? "team" : "personal", Unit = "积分", Total = total, Used = Math.Max(0, total.Value - (left ?? 0)),
                ExpiresAt = Millis(item.Get("EndTime")) ?? Date(item.Get("EndTime")) });
    }

    // The team add-on packs in force, together; null without any.
    public static UsageMetric? AddonPacks(JsonElement data)
    {
        decimal total = 0, left = 0; DateTimeOffset? expires = null;
        foreach (var item in data.Get("Data").Items().Where(Valid))
        {
            var (all, rest) = Capacity(item);
            total += all ?? 0; left += rest ?? 0;
            var end = Millis(item.Get("EndTime")) ?? Date(item.Get("EndTime"));
            if (end is { } e && (expires is null || e < expires)) expires = e;
        }
        if (total <= 0) return null;
        return new UsageMetric(MetricKind.Count, "团队版 · 加油包") { Id = "addon", Unit = "积分", Total = total, Used = Math.Max(0, total - left), ExpiresAt = expires };
    }

    private static bool Valid(JsonElement item)
    {
        var status = item.Get("Status");
        var text = status.ValueKind == JsonValueKind.Object ? status.Get("Code").Text() : status.Text();
        return text.Equals("valid", StringComparison.OrdinalIgnoreCase);
    }

    private static (decimal? Total, decimal? Left) Capacity(JsonElement item)
    {
        var left = item.Get("CapacityTypeCode").Text() == "periodMonthlyShift" ? Amount(item.Get("periodCapacityBaseValue")) ?? Amount(item.Get("CurrCapacityBaseValue")) : Amount(item.Get("CurrCapacityBaseValue"));
        return (Amount(item.Get("InitCapacityBaseValue")), left);
    }

    public static UsageMetric Balance(JsonElement data, string currency)
    {
        if (Amount(data.Get("AvailableAmount")) is not { } amount) throw new InvalidDataException();
        var unit = data.Get("Currency").Text() is { Length: > 0 } c ? c.ToUpperInvariant() : currency;
        return new(MetricKind.Balance, "账户余额") { Id = "balance", Amount = amount, Currency = unit };
    }

    public static UsageMetric MonthCharge(decimal amount, string currency, DateTimeOffset now)
    {
        var month = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return new(MetricKind.Spend, "本月扣费") { Id = "month", Amount = amount, Currency = currency, ResetAt = month.AddMonths(1) };
    }

    public static Dictionary<string, string> CostAnalysisQuery(DateTimeOffset now)
    {
        var month = now.UtcDateTime.ToString("yyyyMM", CultureInfo.InvariantCulture);
        return new()
        {
            ["BizType"] = "MAAS_CONSUME_ANALYSIS", ["ChargeTypes"] = """["postpaid"]""", ["Granularity"] = "MONTH",
            ["TimePeriod"] = $$"""{"Start":"{{month}}","End":"{{month}}"}""", ["TopNum"] = "20",
            ["GroupBy"] = """[{"Code":"BASE_MODEL","Type":"Dimensions"}]""",
            ["Filter"] = """{"Dimensions":[{"Code":"LINE_ITEM_CATEGORY","Values":["TaxFee"],"SelectType":"NOT"}]}"""
        };
    }

    // The month's charges by model, without tax, added up as the China CLI adds them.
    public static decimal CostAnalysisTotal(JsonElement data)
    {
        var groups = data.Get("GroupByTotal");
        if (groups.ValueKind != JsonValueKind.Array) return Amount(data.Get("CostTotals").Get("Amount")) ?? throw new InvalidDataException();
        return groups.Items().Sum(group => Amount(group.Get("Amount")) ?? 0);
    }

    // QwenCloud's charges of the month, day by day, a page of 100 rows at a time.
    private static async Task<decimal> ConsumeTotalAsync(Gateway client, DateTimeOffset now)
    {
        decimal total = 0;
        for (int page = 1; page <= 10; page++)
        {
            var data = await client.CallAsync("MaasListConsumeSummary", ConsumeQuery(now, page));
            var rows = data.Get("Data");
            if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
            total += ConsumeTotal(data);
            if (rows.GetArrayLength() < 100) break;
        }
        return total;
    }

    public static Dictionary<string, string> ConsumeQuery(DateTimeOffset now, int page) => new()
    {
        ["Console"] = "true", ["Granularity"] = "DAILY", ["ChargeTypes"] = """["postpaid"]""",
        ["StartBillingDate"] = now.UtcDateTime.ToString("yyyy-MM-01", CultureInfo.InvariantCulture), ["EndBillingDate"] = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["MaxResults"] = "100", ["CurrentPage"] = page.ToString(CultureInfo.InvariantCulture), ["SortName"] = "BillingDate", ["SortOrder"] = "DESC"
    };

    // Rounding, refunds, credit adjustments and free usage are not charges.
    public static decimal ConsumeTotal(JsonElement data) => data.Get("Data").Items()
        .Where(row => row.Get("LineItemCategory").Text() is var category && category is not ("Rounding Adjustment" or "Refund" or "Credit Adjustment")
            && !category.Contains("free", StringComparison.OrdinalIgnoreCase))
        .Sum(row => Amount(row.Get("RequireAmount")) ?? Amount(row.Get("Amount")) ?? Amount(row.Get("Cost")) ?? Amount(row.Get("ListPrice")) ?? 0);

    public static decimal? Amount(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number :
        value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;

    private static DateTimeOffset? Millis(JsonElement value)
    {
        long? millis = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n
            : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : null;
        if (millis is not > 0) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(millis.Value); } catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateTimeOffset? Date(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && !long.TryParse(value.GetString(), out _) &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    // A reply without the field (cs-data leaves data out when there is no subscription) stays missing; Clone would throw on it.
    private static JsonElement Copy(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? default : value.Clone();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // The account requests of one reading. cs-data takes a console token of its own, issued with the sign-in and issued again once
    // when cs-data no longer accepts it.
    private sealed class Gateway(HttpClient http, Edition edition, string token, CancellationToken ct)
    {
        private string? _console;

        public async Task<JsonElement> CallAsync(string action, Dictionary<string, string> parameters, string product = "BssOpenAPI-V3", string? version = null)
        {
            var body = new Dictionary<string, object> { ["product"] = product, ["action"] = action, ["region"] = edition.Region, ["params"] = parameters };
            if (version is not null) body["version"] = version;
            using var request = new HttpRequestMessage(HttpMethod.Post, edition.Api + "/data/v2/api.json") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var doc = await SendAsync(http, request, ct);
            var code = doc.RootElement.Get("code").Text();
            if (code is "ConsoleNeedLogin" or "NotLogin" or "TokenExpired") throw new RejectedException();
            if (code != "200") throw new InvalidDataException(code);
            return Copy(doc.RootElement.Get("data"));
        }

        public async Task<JsonElement> CsDataAsync(string api, Dictionary<string, string> parameters)
        {
            for (int attempt = 0; ; attempt++)
            {
                _console ??= await ConsoleTokenAsync();
                var data = new Dictionary<string, object>();
                foreach (var (key, value) in parameters) data[key] = value;
                data["cornerstoneParam"] = new Dictionary<string, object> { ["protocol"] = "V2", ["console"] = "ONE_CONSOLE", ["productCode"] = "p_efm", ["switchUserType"] = 3, ["consoleSite"] = "QIANWENAI" };
                var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["Api"] = api, ["V"] = "1.0", ["Data"] = data });
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{CsData}?action=BroadScopeAspnGateway&product=sfm_bailian&api={Uri.EscapeDataString(api)}")
                { Content = new FormUrlEncodedContent([new("params", json), new("region", edition.Region)]) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _console);
                using var doc = await SendAsync(http, request, ct);
                var root = doc.RootElement;
                if (root.Get("code").Text() != "200" || root.Get("successResponse").ValueKind != JsonValueKind.True) throw new InvalidDataException(root.Get("code").Text());
                var envelope = root.Get("data");
                if (envelope.Get("errorCode").Text() == "BailianGateway.Login.NotLogined")
                {
                    if (attempt > 0) throw new RejectedException();
                    _console = null;
                    continue;
                }
                var result = envelope.Get("DataV2");
                if (envelope.Get("success").ValueKind != JsonValueKind.True || result.Get("ret").Items().FirstOrDefault().Text() is not { } ret || !ret.StartsWith("SUCCESS::", StringComparison.Ordinal))
                    throw new InvalidDataException();
                var business = result.Get("data");
                if (business.Get("success").ValueKind != JsonValueKind.True) throw new InvalidDataException(business.Get("code").Text());
                return Copy(business.Get("data"));
            }
        }

        private async Task<string> ConsoleTokenAsync()
        {
            var data = await CallAsync("GenerateCLIAccessToken", [], "ModelStudioRegional", "2026-02-10");
            var console = data.Get("cliAccessToken").Text();
            if (console.Length == 0 || console == token) throw new InvalidDataException();
            return console;
        }
    }

    private static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.SendAsync(request, deadline.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new RejectedException();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
        if (bytes.Length > 4 << 20) throw new InvalidDataException(Loc.T("返回的数据过大", "Too much data was returned"));
        return JsonDocument.Parse(bytes);
    }
}
