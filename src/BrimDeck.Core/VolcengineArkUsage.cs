using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

// Volcengine Ark (火山方舟, ProviderId.VolcengineArk) and its international edition BytePlus ModelArk (ProviderId.BytePlusArk).
// Inference API keys cannot query quotas or balances; a row reads with either credential, or both (ScriptSession):
//   1. Static AccessKey ID + Secret Access Key entered by hand in the row, tried first.
//   2. Browser sign-in from settings, one per edition: OAuth 2.0 Device Authorization Grant matching the official
//      volcengine-cli (`ve login`, https://signin.volcengine.com) and byteplus-cli (https://signin.byteplus.com).
//      Issues auto-refreshable temporary STS credentials (AccessKeyId + SecretAccessKey + SessionToken). Used when the row
//      has no AccessKey or the AccessKey is refused.
//
// Both paths share the exact same Volcengine / BytePlus V4 HMAC-SHA256 OpenAPI signer (STS adds X-Security-Token) and probe
// every plan kind an account may hold at once:
//   - Personal Agent Plan: ark 2024-01-01 GetAFPUsage (5-hour, weekly, monthly AFP quotas; AFPDaily is hidden by console).
//   - Personal Coding Plan: ark 2024-01-01 GetCodingPlanUsage (session 5-hour, weekly, monthly percent).
//   - Team / Enterprise Coding Plan seats: ListSeatInfos (Scene=coding_plan_enterprise) -> GetSeatInfoUsage / ListSeatInfoUsages.
//   - Team / Enterprise Agent Plan seats: ListSeatInfos (Scene=agent_plan_enterprise) -> GetSeatAFPUsage.
//   - Account balance: billing 2022-01-01 QueryBalanceAcct.
// Parts the account does not subscribe to or lacks permission for (e.g., 403 AccessDenied on personal GetAFPUsage for a team
// account) are skipped without blocking the others; an invalid or expired credential throws RejectedException immediately.
public static class VolcengineArkUsage
{
    // ArkHost serves the ark actions and BillingHost the billing one, as the API references name them: Volcengine's Ark pages
    // use ark.cn-beijing.volcengineapi.com and its QueryBalanceAcct page open.volcengineapi.com; BytePlus's ModelArk pages use
    // ark.ap-southeast-1.byteplusapi.com, and its SDK addresses billing, a global service, at billing.byteplusapi.com.
    public sealed record Edition(ProviderId Id, string SignInOrigin, string SignInHost,
        string ArkHost, string BillingHost, string Region, string Currency, Guid SessionKey, bool China);

    public static readonly Edition China = new(
        ProviderId.VolcengineArk,
        "https://signin.volcengine.com", "volcengine.com",
        "ark.cn-beijing.volcengineapi.com", "open.volcengineapi.com", "cn-beijing", "CNY",
        new("8b4d7c1e-3f62-4a9b-91d4-2e7f5a0c8b13"), true);

    public static readonly Edition International = new(
        ProviderId.BytePlusArk,
        "https://signin.byteplus.com", "byteplus.com",
        "ark.ap-southeast-1.byteplusapi.com", "billing.byteplusapi.com", "ap-southeast-1", "USD",
        new("4f9a2d7c-1e85-4c3a-b6e9-7d0c3f1a5e28"), false);

    public static Edition For(ProviderId id) => id == ProviderId.BytePlusArk ? International : China;

    public const string ClientId = "trn:signin:::devtools/cross-device";
    public const string Scope = "Console:All:All";
    public const string DeviceAuthPath = "/authorize/oauth/device_authorization";
    public const string TokenPath = "/authorize/oauth/token";
    public const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private const string ArkService = "ark", ArkVersion = "2024-01-01";
    private const string BillingService = "billing", BillingVersion = "2022-01-01";
    // ListSeatInfos takes at most 100 seats a page; the seat usage actions take at most 1000 SeatIDs a request.
    private const int SeatPageSize = 100, MaxSeatPages = 50, SeatBatchSize = 1000, BillingRunning = 2;
    private const string UserAgent = "BrimDeck/1.0";

    // Signing credentials; never shown or logged.
    public sealed record Credentials(string AccessKeyId, string SecretAccessKey, string SessionToken = "")
    {
        public override string ToString() => nameof(Credentials);
    }

    // Persisted OAuth + STS session for browser sign-in; never shown or logged.
    public sealed record Session(string AccessKeyId, string SecretAccessKey, string SessionToken,
        string RefreshToken, DateTimeOffset? ExpiresAt, string ClientId = VolcengineArkUsage.ClientId)
    {
        public string Serialize() => JsonSerializer.Serialize(this);
        public static Session? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                var s = JsonSerializer.Deserialize<Session>(text);
                return s is { AccessKeyId.Length: > 0, SecretAccessKey.Length: > 0 } ? s : null;
            }
            catch (JsonException) { return null; }
        }
        public bool NeedsRefresh(DateTimeOffset now) => ExpiresAt is { } at && at <= now.AddMinutes(2);
        public Credentials ToCredentials() => new(AccessKeyId, SecretAccessKey, SessionToken);
        public override string ToString() => nameof(Session);
    }

    public sealed record Login(string DeviceCode, string UserCode, string Url, TimeSpan Interval, DateTimeOffset Until);

    public sealed class RejectedException(string message = "Credentials rejected") : Exception(message);

    public sealed record Reading(List<UsageMetric> Metrics, string Plan, List<string> Warnings);

    // Packs static AK + SK into a single encrypted secret string stored in secrets.dat.
    public static string PackStaticSecret(string accessKeyId, string secretAccessKey) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["ak"] = accessKeyId.Trim(), ["sk"] = secretAccessKey.Trim() });

    // Unpacks static AK + SK from JSON {"ak","sk"}, "AK:SK", or two lines.
    public static Credentials? ParseStaticSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return null;
        var trimmed = secret.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var ak = doc.RootElement.Get("ak").Text().Trim();
                var sk = doc.RootElement.Get("sk").Text().Trim();
                return ak.Length > 0 && sk.Length > 0 ? new(ak, sk) : null;
            }
            catch (JsonException) { return null; }
        }
        var lines = trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 2 && lines[0].Length > 0 && lines[1].Length > 0)
            return new(lines[0], lines[1]);
        int colon = trimmed.IndexOf(':');
        if (colon > 0 && colon < trimmed.Length - 1)
        {
            var ak = trimmed[..colon].Trim();
            var sk = trimmed[(colon + 1)..].Trim();
            if (ak.Length > 0 && sk.Length > 0) return new(ak, sk);
        }
        return null;
    }

    // --- OAuth 2.0 Device Code Flow (Volcengine / BytePlus Console Login) ---

    public static bool LoginUrlMatches(Edition edition, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
        && (uri.Host.Equals(edition.SignInHost, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + edition.SignInHost, StringComparison.OrdinalIgnoreCase));

    public static async Task<Login> StartLoginAsync(HttpClient http, Edition edition, DateTimeOffset now, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, edition.SignInOrigin + DeviceAuthPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = Scope,
                ["device_info"] = "BrimDeck"
            })
        };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        string deviceCode = root.Get("device_code").Text();
        string userCode = root.Get("user_code").Text();
        string url = root.Get("verification_uri_complete").Text();
        if (url.Length == 0) url = root.Get("verification_uri").Text();
        if (deviceCode.Length == 0 || url.Length == 0)
            throw new InvalidDataException(Loc.T("暂时无法创建登录请求", "A sign-in cannot be started right now"));
        if (!LoginUrlMatches(edition, url))
            throw new InvalidDataException(Loc.T("返回的登录地址无效", "The returned sign-in address is not valid"));
        var interval = TimeSpan.FromSeconds(Math.Clamp(root.Get("interval").Number() ?? 5, 2, 30));
        var lifetime = TimeSpan.FromSeconds(Math.Clamp(root.Get("expires_in").Number() ?? 300, 30, 1800));
        return new(deviceCode, userCode, url, interval, now + lifetime);
    }

    public static async Task<Session?> WaitForLoginAsync(HttpClient http, Edition edition, Login login, Func<DateTimeOffset> clock, CancellationToken ct)
    {
        var interval = login.Interval;
        while (clock() < login.Until)
        {
            await Task.Delay(interval, ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, edition.SignInOrigin + TokenPath)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = DeviceGrantType,
                    ["client_id"] = ClientId,
                    ["device_code"] = login.DeviceCode,
                    ["scope"] = Scope
                })
            };
            request.Headers.UserAgent.ParseAdd(UserAgent);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, ct); }
            catch (HttpRequestException) { continue; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            using (response)
            {
                string body = await response.Content.ReadAsStringAsync(ct);
                JsonDocument doc;
                try { doc = JsonDocument.Parse(body); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (!response.IsSuccessStatusCode || root.Get("error").Text().Length > 0)
                    {
                        var err = root.Get("error").Text().ToLowerInvariant();
                        if (err == "authorization_pending") continue;
                        if (err == "slow_down") { interval = TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds + 5, 30)); continue; }
                        if (err == "access_denied") throw new InvalidDataException(Loc.T("登录已被拒绝", "The sign-in was refused"));
                        if (err is "expired_token" or "invalid_device_code") return null;
                        if ((int)response.StatusCode >= 500 || err is "server_error" or "temporarily_unavailable") continue;
                        throw new InvalidDataException(Loc.T("登录失败，请重试", "Sign-in failed. Try again."));
                    }
                    return ParseTokenResponse(root, clock(), ClientId)
                        ?? throw new InvalidDataException(Loc.T("没有返回有效的登录凭据", "No valid sign-in credentials were returned"));
                }
            }
        }
        return null;
    }

    public static async Task<Session?> RefreshSessionAsync(HttpClient http, Edition edition, Session session, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(session.RefreshToken)) return null;
        string clientId = string.IsNullOrWhiteSpace(session.ClientId) ? ClientId : session.ClientId;
        using var request = new HttpRequestMessage(HttpMethod.Post, edition.SignInOrigin + TokenPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = session.RefreshToken,
                ["scope"] = Scope
            })
        };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            try
            {
                using var errDoc = JsonDocument.Parse(body);
                var err = errDoc.RootElement.Get("error").Text();
                if (err is "invalid_grant" or "invalid_token" or "expired_token" or "access_denied")
                    throw new RejectedException(err);
            }
            catch (JsonException) { }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new RejectedException();
        }
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        var refreshed = ParseTokenResponse(doc.RootElement, now, clientId);
        if (refreshed is null) return null;
        return refreshed.RefreshToken.Length > 0 ? refreshed : refreshed with { RefreshToken = session.RefreshToken };
    }

    // Reads with the browser sign-in, renewing it when it is about to expire or is refused; a renewed sign-in is passed to save.
    // Null when the sign-in has expired and cannot be renewed.
    public static async Task<Reading?> ReadSignedInAsync(HttpClient http, Edition edition, Session session, Action<Session> save, DateTimeOffset now, CancellationToken ct)
    {
        async Task<bool> Renew()
        {
            if (await RefreshSessionAsync(http, edition, session, now, ct) is not { } renewed) return false;
            session = renewed;
            save(renewed);
            return true;
        }
        try
        {
            if (session.NeedsRefresh(now) && session.RefreshToken.Length > 0 && !await Renew()) return null;
            try { return await ReadAsync(http, edition, session.ToCredentials(), now, ct); }
            catch (RejectedException) when (session.RefreshToken.Length > 0)
            {
                return await Renew() ? await ReadAsync(http, edition, session.ToCredentials(), now, ct) : null;
            }
        }
        catch (RejectedException) { return null; }
    }

    public static Session? ParseTokenResponse(JsonElement root, DateTimeOffset now, string clientId = ClientId)
    {
        var accessTokenRaw = root.Get("access_token").Text();
        if (accessTokenRaw.Length == 0) return null;
        try
        {
            using var stsDoc = JsonDocument.Parse(accessTokenRaw);
            var sts = stsDoc.RootElement;
            string ak = sts.Get("access_key_id").Text();
            string sk = sts.Get("secret_access_key").Text();
            string token = sts.Get("session_token").Text();
            if (ak.Length == 0 || sk.Length == 0) return null;
            DateTimeOffset? expires = root.Get("expires_in").Number() is > 0 and var secs ? now.AddSeconds(secs) : null;
            return new(ak, sk, token, root.Get("refresh_token").Text(), expires, clientId);
        }
        catch (JsonException) { return null; }
    }

    // --- Volcengine / BytePlus V4 HMAC-SHA256 OpenAPI Signer ---

    public static HttpRequestMessage SignRequest(Edition edition, Credentials creds, string service, string version,
        string action, string bodyJson, DateTimeOffset utcNow, string? regionOverride = null)
    {
        string region = string.IsNullOrWhiteSpace(regionOverride) ? edition.Region : regionOverride;
        string host = service == BillingService ? edition.BillingHost : edition.ArkHost;
        string query = $"Action={Rfc3986(action)}&Version={Rfc3986(version)}";
        string xDate = utcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string shortDate = utcNow.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        byte[] payload = Encoding.UTF8.GetBytes(bodyJson);
        string payloadHash = LowerHex(SHA256.HashData(payload));
        const string contentType = "application/json";

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["content-type"] = contentType,
            ["host"] = host,
            ["x-content-sha256"] = payloadHash,
            ["x-date"] = xDate
        };
        if (!string.IsNullOrEmpty(creds.SessionToken))
            headers["x-security-token"] = creds.SessionToken;

        var canonicalHeaders = new StringBuilder();
        foreach (var (k, v) in headers)
            canonicalHeaders.Append(k).Append(':').Append(v.Trim()).Append('\n');
        string signedHeaders = string.Join(";", headers.Keys);

        string canonicalRequest = $"POST\n/\n{query}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        string canonicalHash = LowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));
        string credentialScope = $"{shortDate}/{region}/{service}/request";
        string stringToSign = $"HMAC-SHA256\n{xDate}\n{credentialScope}\n{canonicalHash}";

        // Volcengine / BytePlus V4 key derivation: raw SecretAccessKey (no "AWS4" prefix).
        byte[] kDate = HmacSha256(Encoding.UTF8.GetBytes(creds.SecretAccessKey), shortDate);
        byte[] kRegion = HmacSha256(kDate, region);
        byte[] kService = HmacSha256(kRegion, service);
        byte[] kSigning = HmacSha256(kService, "request");
        string signature = LowerHex(HmacSha256(kSigning, stringToSign));

        string auth = $"HMAC-SHA256 Credential={creds.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/?{query}")
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("X-Date", xDate);
        request.Headers.TryAddWithoutValidation("X-Content-Sha256", payloadHash);
        if (!string.IsNullOrEmpty(creds.SessionToken))
            request.Headers.TryAddWithoutValidation("X-Security-Token", creds.SessionToken);
        request.Headers.TryAddWithoutValidation("Authorization", auth);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return request;
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string LowerHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Rfc3986(string value) =>
        Uri.EscapeDataString(value).Replace("!", "%21").Replace("'", "%27").Replace("(", "%28").Replace(")", "%29").Replace("*", "%2A");

    // --- Unified Multi-Plan Reading (Personal + Team/Enterprise + Balance) ---

    public static async Task<Reading> ReadAsync(HttpClient http, Edition edition, Credentials creds, DateTimeOffset now, CancellationToken ct)
    {
        var metrics = new List<UsageMetric>();
        var plans = new List<string>();
        var warnings = new List<string>();

        async Task<JsonDocument?> CallOptionalAsync(string service, string version, string action, string bodyJson, string partChinese, string partEnglish)
        {
            using var request = SignRequest(edition, creds, service, version, action, bodyJson, now);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, ct); }
            catch (HttpRequestException)
            {
                warnings.Add(Loc.T(partChinese + "暂时无法读取", partEnglish + " cannot be read right now"));
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                warnings.Add(Loc.T(partChinese + "暂时无法读取", partEnglish + " cannot be read right now"));
                return null;
            }

            using (response)
            {
                string raw = await response.Content.ReadAsStringAsync(ct);
                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(raw); } catch (JsonException) { }
                if (doc is not null)
                {
                    var err = doc.RootElement.Get("ResponseMetadata").Get("Error");
                    if (err.ValueKind == JsonValueKind.Object)
                    {
                        string code = err.Get("Code").Text();
                        string msg = err.Get("Message").Text();
                        doc.Dispose();
                        if (IsAuthError(response.StatusCode, code))
                            throw new RejectedException(msg.Length > 0 ? msg : code);
                        if (IsUnavailableOrUnentitled(response.StatusCode, code))
                            return null;
                        warnings.Add(Loc.T(partChinese + "暂时无法读取", partEnglish + " cannot be read right now"));
                        return null;
                    }
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                { doc?.Dispose(); throw new RejectedException(); }
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                { doc?.Dispose(); return null; }
                if (!response.IsSuccessStatusCode || doc is null)
                {
                    doc?.Dispose();
                    warnings.Add(Loc.T(partChinese + "暂时无法读取", partEnglish + " cannot be read right now"));
                    return null;
                }
                return doc;
            }
        }

        // Probe every plan type independently so accounts holding multiple plans show all of them.
        var afpPersonal = new List<UsageMetric>();
        string afpPersonalPlan = "";
        using (var doc = await CallOptionalAsync(ArkService, ArkVersion, "GetAFPUsage", "{}", "个人 Agent Plan ", "Personal Agent Plan"))
        {
            if (doc is not null) (afpPersonal, afpPersonalPlan) = ParseAfpUsage(doc.RootElement.Get("Result"), "afp-personal");
        }

        var codingPersonal = new List<UsageMetric>();
        using (var doc = await CallOptionalAsync(ArkService, ArkVersion, "GetCodingPlanUsage", "{}", "个人 Coding Plan ", "Personal Coding Plan"))
        {
            if (doc is not null) codingPersonal = ParseCodingPlanUsage(doc.RootElement.Get("Result"), "coding-personal");
        }

        // The seats of a team plan, page by page (ListSeatInfos: PageSize at most 100, Result.Total), only those being billed
        // (Filter.BillingStatus 2 = running; 1 pending, 3 expired and 4 reclaimed seats are left out).
        async Task<List<string>> SeatIdsAsync(string scene, string partChinese, string partEnglish)
        {
            var ids = new List<string>();
            for (int page = 1; page <= MaxSeatPages; page++)
            {
                var body = JsonSerializer.Serialize(new { Scene = scene, ProjectName = "default", PageNum = page, PageSize = SeatPageSize, Filter = new { BillingStatus = new[] { BillingRunning } } });
                using var listDoc = await CallOptionalAsync(ArkService, ArkVersion, "ListSeatInfos", body, partChinese, partEnglish);
                if (listDoc is null) break;
                var result = listDoc.RootElement.Get("Result");
                ids.AddRange(ExtractSeatIds(result));
                int listed = result.Get("Data").Items().Count();
                if (listed < SeatPageSize || Decimal(result.Get("Total")) is { } total && page * SeatPageSize >= total) break;
            }
            return ids.Distinct(StringComparer.Ordinal).ToList();
        }
        // The usage of many seats, asked for in batches of at most 1000 seats (SeatIDs), with the items of every answer kept together.
        async Task<JsonDocument?> SeatUsagesAsync(string action, List<string> seatIds, string listKey, bool project, string partChinese, string partEnglish)
        {
            var items = new List<string>();
            foreach (var batch in seatIds.Chunk(SeatBatchSize))
            {
                var body = project ? JsonSerializer.Serialize(new { SeatIDs = batch, ProjectName = "default" }) : JsonSerializer.Serialize(new { SeatIDs = batch });
                using var doc = await CallOptionalAsync(ArkService, ArkVersion, action, body, partChinese, partEnglish);
                if (doc is null) return null;
                items.AddRange(doc.RootElement.Get("Result").Get(listKey).Items().Select(item => item.GetRawText()));
            }
            return JsonDocument.Parse($"{{\"{listKey}\":[{string.Join(",", items)}]}}");
        }

        var codingSeats = new List<UsageMetric>();
        int codingSeatCount = 0;
        var codingSeatIds = await SeatIdsAsync("coding_plan_enterprise", "团队/企业 Coding Plan ", "Team/Enterprise Coding Plan");
        if (codingSeatIds.Count == 1)
        {
            var body = JsonSerializer.Serialize(new { SeatID = codingSeatIds[0], ProjectName = "default" });
            using var usageDoc = await CallOptionalAsync(ArkService, ArkVersion, "GetSeatInfoUsage", body, "团队/企业 Coding Plan ", "Team/Enterprise Coding Plan");
            // The seat's usage is wrapped in Result.SeatInfoUsage.
            if (usageDoc is not null) { codingSeats = ParseCodingSeatUsage(usageDoc.RootElement.Get("Result").Get("SeatInfoUsage")); codingSeatCount = codingSeats.Count > 0 ? 1 : 0; }
        }
        else if (codingSeatIds.Count > 1)
        {
            using var usageDoc = await SeatUsagesAsync("ListSeatInfoUsages", codingSeatIds, "Data", true, "团队/企业 Coding Plan ", "Team/Enterprise Coding Plan");
            if (usageDoc is not null) (codingSeats, codingSeatCount) = ParseCodingSeatUsages(usageDoc.RootElement);
        }

        var afpSeats = new List<UsageMetric>();
        string afpSeatPlan = "";
        int afpSeatCount = 0;
        var afpSeatIds = await SeatIdsAsync("agent_plan_enterprise", "团队/企业 Agent Plan ", "Team/Enterprise Agent Plan");
        if (afpSeatIds.Count > 0)
        {
            using var usageDoc = await SeatUsagesAsync("GetSeatAFPUsage", afpSeatIds, "SeatAFPUsages", false, "团队/企业 Agent Plan ", "Team/Enterprise Agent Plan");
            if (usageDoc is not null) (afpSeats, afpSeatPlan, afpSeatCount) = ParseSeatAfpUsages(usageDoc.RootElement);
        }

        UsageMetric? balance = null;
        using (var balDoc = await CallOptionalAsync(BillingService, BillingVersion, "QueryBalanceAcct", "{}", "账户余额", "Account balance"))
        {
            if (balDoc is not null) balance = ParseBalance(balDoc.RootElement.Get("Result"), edition.Currency);
        }

        int activePlanGroups = (afpPersonal.Count > 0 ? 1 : 0) + (codingPersonal.Count > 0 ? 1 : 0)
            + (codingSeats.Count > 0 ? 1 : 0) + (afpSeats.Count > 0 ? 1 : 0);

        if (afpPersonal.Count > 0)
        {
            plans.Add(afpPersonalPlan.Length > 0 ? $"Agent Plan {afpPersonalPlan}" : "Agent Plan");
            string prefix = activePlanGroups > 1 ? "个人 Agent" : "";
            metrics.AddRange(WithPrefix(afpPersonal, prefix));
        }
        if (codingPersonal.Count > 0)
        {
            plans.Add("Coding Plan");
            string prefix = activePlanGroups > 1 ? "个人 Coding" : "";
            metrics.AddRange(WithPrefix(codingPersonal, prefix));
        }
        if (afpSeats.Count > 0)
        {
            var seatTag = afpSeatCount > 1 ? $" ({afpSeatCount})" : "";
            plans.Add((afpSeatPlan.Length > 0 ? $"团队/企业 Agent {afpSeatPlan}" : "团队/企业 Agent Plan") + seatTag);
            string prefix = activePlanGroups > 1 ? "团队 Agent" : "团队版";
            metrics.AddRange(WithPrefix(afpSeats, prefix));
        }
        if (codingSeats.Count > 0)
        {
            var seatTag = codingSeatCount > 1 ? $" ({codingSeatCount})" : "";
            plans.Add("团队/企业 Coding Plan" + seatTag);
            string prefix = activePlanGroups > 1 ? "团队 Coding" : "团队版";
            metrics.AddRange(WithPrefix(codingSeats, prefix));
        }
        if (balance is not null)
            metrics.Add(balance);

        if (metrics.Count == 0 && warnings.Count > 0)
            throw new InvalidDataException(string.Join("；", warnings));

        return new(metrics, string.Join(" · ", plans), warnings);
    }

    private static IEnumerable<UsageMetric> WithPrefix(IEnumerable<UsageMetric> items, string prefix) =>
        prefix.Length == 0 ? items : items.Select(m => m with { Label = $"{prefix} · {m.Label}" });

    private static bool IsAuthError(HttpStatusCode status, string code) =>
        status == HttpStatusCode.Unauthorized
        || code.Contains("InvalidAccessKey", StringComparison.OrdinalIgnoreCase)
        || code.Contains("SignatureDoesNotMatch", StringComparison.OrdinalIgnoreCase)
        || code.Contains("ExpiredToken", StringComparison.OrdinalIgnoreCase)
        || code.Contains("InvalidSecurityToken", StringComparison.OrdinalIgnoreCase)
        || code.Contains("InvalidCredential", StringComparison.OrdinalIgnoreCase)
        || code.Equals("Unauthenticated", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnavailableOrUnentitled(HttpStatusCode status, string code) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
        || code.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase)
        || code.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
        || code.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
        || code.Contains("InvalidAction", StringComparison.OrdinalIgnoreCase)
        || code.Contains("NotSubscribed", StringComparison.OrdinalIgnoreCase)
        || code.Contains("NoSubscription", StringComparison.OrdinalIgnoreCase);

    // --- Response Parsers ---

    public static (List<UsageMetric> Metrics, string Plan) ParseAfpUsage(JsonElement result, string idPrefix)
    {
        var metrics = new List<UsageMetric>();
        if (result.ValueKind != JsonValueKind.Object) return (metrics, "");
        string plan = result.Get("PlanType").Text().Trim();
        if (plan.Length == 0) plan = result.Get("Tier").Text().Trim();
        if (plan.Length > 0) plan = char.ToUpperInvariant(plan[0]) + plan[1..];

        // AFPDaily is intentionally skipped because Volcengine console hides it (default ceiling higher than weekly cap).
        foreach (var (field, id, label, window) in new[]
        {
            ("AFPFiveHour", "5h", "5 小时额度", (int?)300),
            ("AFPWeekly", "weekly", "每周额度", (int?)10080),
            ("AFPMonthly", "monthly", "每月额度", (int?)null)
        })
        {
            var bucket = result.Get(field);
            if (bucket.ValueKind != JsonValueKind.Object) continue;
            var quota = Decimal(bucket.Get("Quota")) ?? Decimal(bucket.Get("Total"));
            if (quota is null or <= 0) continue;
            var used = Math.Max(0m, Decimal(bucket.Get("Used")) ?? 0m);
            var reset = Epoch(bucket.Get("ResetTime"));
            metrics.Add(new UsageMetric(MetricKind.Count, label)
            {
                Id = $"{idPrefix}-{id}", Unit = "AFP", Total = Math.Max(quota.Value, used), Used = used, ResetAt = reset, Window = window
            });
        }
        return (metrics, plan);
    }

    public static List<UsageMetric> ParseCodingPlanUsage(JsonElement result, string idPrefix)
    {
        var metrics = new List<UsageMetric>();
        if (result.ValueKind != JsonValueKind.Object) return metrics;
        foreach (var item in result.Get("QuotaUsage").Items())
        {
            var level = item.Get("Level").Text().ToLowerInvariant();
            if (Double(item.Get("Percent")) is not { } pct) continue;
            var (id, label, window) = level switch
            {
                "session" => ("5h", "5 小时额度", (int?)300),
                "weekly" => ("weekly", "每周额度", (int?)10080),
                "monthly" => ("monthly", "每月额度", (int?)null),
                _ => (level, level.Length > 0 ? level : "套餐额度", (int?)null)
            };
            metrics.Add(new UsageMetric(MetricKind.Percent, label)
            {
                Id = $"{idPrefix}-{id}", Percent = Math.Clamp(pct, 0, 100), ResetAt = Epoch(item.Get("ResetTimestamp")), Window = window
            });
        }
        return metrics;
    }

    // ListSeatInfos: Result.Data[] with SeatID and BillingStatus (1 pending, 2 running, 3 expired, 4 reclaimed). The request asks
    // for running seats only; a seat answered with another billing status is still left out.
    public static List<string> ExtractSeatIds(JsonElement result)
    {
        var ids = new List<string>();
        foreach (var item in result.Get("Data").Items())
        {
            if (Decimal(item.Get("BillingStatus")) is { } billing && billing != BillingRunning) continue;
            var id = item.Get("SeatID").Text().Trim();
            if (id.Length > 0) ids.Add(id);
        }
        return ids.Distinct(StringComparer.Ordinal).ToList();
    }

    public static List<UsageMetric> ParseCodingSeatUsage(JsonElement item)
    {
        var metrics = new List<UsageMetric>();
        if (item.ValueKind != JsonValueKind.Object) return metrics;
        if (Double(item.Get("ShortTermUsage")) is { } st)
            metrics.Add(new UsageMetric(MetricKind.Percent, "5 小时额度") { Id = "coding-seat-5h", Percent = Math.Clamp(st, 0, 100), ResetAt = Epoch(item.Get("ShortTermResetMilestone")), Window = 300 });
        if (Double(item.Get("WeeklyUsage")) is { } wk)
            metrics.Add(new UsageMetric(MetricKind.Percent, "每周额度") { Id = "coding-seat-weekly", Percent = Math.Clamp(wk, 0, 100), Window = 10080 });
        if (Double(item.Get("MonthlyUsage")) is { } mo)
            metrics.Add(new UsageMetric(MetricKind.Percent, "每月额度") { Id = "coding-seat-monthly", Percent = Math.Clamp(mo, 0, 100), ResetAt = Epoch(item.Get("MonthlyResetMilestone")) });
        return metrics;
    }

    public static (List<UsageMetric> Metrics, int Seats) ParseCodingSeatUsages(JsonElement result)
    {
        var items = result.Get("Data").Items().ToList();
        if (items.Count == 0) items = result.Get("Items").Items().ToList();
        if (items.Count == 0) return ([], 0);
        if (items.Count == 1) return (ParseCodingSeatUsage(items[0]), 1);

        double? Avg(string key)
        {
            var vals = items.Select(i => Double(i.Get(key))).Where(v => v is not null).Select(v => v!.Value).ToList();
            return vals.Count > 0 ? Math.Round(vals.Average(), 2) : null;
        }
        DateTimeOffset? Earliest(string key) => items.Select(i => Epoch(i.Get(key))).Where(d => d is not null).Min();

        var metrics = new List<UsageMetric>();
        if (Avg("ShortTermUsage") is { } st)
            metrics.Add(new UsageMetric(MetricKind.Percent, "5 小时额度") { Id = "coding-seat-5h", Percent = Math.Clamp(st, 0, 100), ResetAt = Earliest("ShortTermResetMilestone"), Window = 300 });
        if (Avg("WeeklyUsage") is { } wk)
            metrics.Add(new UsageMetric(MetricKind.Percent, "每周额度") { Id = "coding-seat-weekly", Percent = Math.Clamp(wk, 0, 100), Window = 10080 });
        if (Avg("MonthlyUsage") is { } mo)
            metrics.Add(new UsageMetric(MetricKind.Percent, "每月额度") { Id = "coding-seat-monthly", Percent = Math.Clamp(mo, 0, 100), ResetAt = Earliest("MonthlyResetMilestone") });
        return (metrics, items.Count);
    }

    public static (List<UsageMetric> Metrics, string Plan, int Seats) ParseSeatAfpUsages(JsonElement result)
    {
        var items = result.Get("SeatAFPUsages").Items().ToList();
        if (items.Count == 0) items = result.Get("Items").Items().ToList();
        if (items.Count == 0) return ([], "", 0);

        var planNames = items.Select(i => i.Get("PlanType").Text().Trim()).Where(s => s.Length > 0)
            .Select(s => char.ToUpperInvariant(s[0]) + s[1..]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string plan = string.Join("/", planNames);

        var metrics = new List<UsageMetric>();
        foreach (var (field, id, label, window) in new[]
        {
            ("AFPFiveHour", "5h", "5 小时额度", (int?)300),
            ("AFPWeekly", "weekly", "每周额度", (int?)10080),
            ("AFPMonthly", "monthly", "每月额度", (int?)null)
        })
        {
            decimal totalQuota = 0, totalUsed = 0;
            DateTimeOffset? earliestReset = null;
            foreach (var item in items)
            {
                var bucket = item.Get(field);
                if (bucket.ValueKind != JsonValueKind.Object) continue;
                var q = Decimal(bucket.Get("Quota")) ?? Decimal(bucket.Get("Total"));
                if (q is null or <= 0) continue;
                totalQuota += q.Value;
                totalUsed += Math.Max(0m, Decimal(bucket.Get("Used")) ?? 0m);
                var reset = Epoch(bucket.Get("ResetTime"));
                if (reset is not null && (earliestReset is null || reset < earliestReset)) earliestReset = reset;
            }
            if (totalQuota > 0)
                metrics.Add(new UsageMetric(MetricKind.Count, label)
                {
                    Id = $"afp-seat-{id}", Unit = "AFP", Total = Math.Max(totalQuota, totalUsed), Used = totalUsed, ResetAt = earliestReset, Window = window
                });
        }
        return (metrics, plan, items.Count);
    }

    public static UsageMetric? ParseBalance(JsonElement result, string currency)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;
        decimal? amount = Decimal(result.Get("AvailableBalance"))
            ?? Decimal(result.Get("CashBalance"))
            ?? Decimal(result.Get("TotalBalance"));
        if (amount is null) return null;
        return new UsageMetric(MetricKind.Balance, "账户余额")
        {
            Id = "balance", Amount = Math.Round(amount.Value, 2), Currency = currency
        };
    }

    private static DateTimeOffset? Epoch(JsonElement value)
    {
        if (Double(value) is not { } raw || raw <= 0) return null;
        try
        {
            return raw >= 1_000_000_000_000d
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)raw)
                : DateTimeOffset.FromUnixTimeSeconds((long)raw);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static double? Double(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n :
        value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && double.IsFinite(n) ? n : null;

    private static decimal? Decimal(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n) ? Math.Round(n, 4) :
        value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n) ? Math.Round(n, 4) : null;
}
