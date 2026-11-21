using System.Globalization;
using System.Text.Json;

namespace BrimDeck.Core;

// QwenWork is a desktop app published by DingTalk and built on the Qoder desktop app, in two editions with separate accounts: QwenWork
// CN (千问办公, qwenwork.cn) and the international QwenWork (qwenwork.ai). They keep their data apart (Edition) and are read alike.
// Where an edition is installed does not matter here: everything read is in the current user's folders.
//
// Credits. The app keeps its sign-in in %APPDATA%\QwenWorkCN\auth-v2.dat (QwenWork: %APPDATA%\QwenWork), encrypted with Chromium's os_crypt like the Qoder desktop
// app's (IDesktopSources.ReadQwenWorkSignIn): { schemaVersion, authRealm, endpoints: { apiOrigin, … }, token, refreshToken, expiresAt
// (ISO 8601, about 7 days), user }. The app renews the token while it runs; BrimDeck only reads it, so an expired one waits for the
// app's next start. Its account API (endpoints.apiOrigin: https://gateway.qwenwork.cn, or https://gateway.qwenwork.ai) takes the token as a bearer token and answers
// { code: "ok", data }:
//   GET /api/v1/adapter/user/account-context?include=user,plan,quota: data { user { is_biz }, plan { name, is_personal_version,
//     is_subscribed, next_refresh_date }, quota { total, used, remaining, exceeded } }. A personal account's quota has only
//     remaining (total and used are null, with or without a plan), as a decimal number of credits; the app itself then shows only
//     what is left.
//   GET /api/v1/adapter/user/wallets: data { daily_credits, monthly_credits, longterm_credits: { total_balance }, expiring_soon:
//     { count, total_balance, wallets [{ balance, valid_to }] }, active_wallets { wallets [{ balance, valid_to }] } }. The monthly
//     credits hold the plan's credits, which the plan renews on next_refresh_date, and the free monthly grant. A wallet carries no
//     amount granted, so no kind has a total. The international edition has no wallets (HTTP 404); its credits left show alone.
// A team account (is_personal_version false) reads GET /api/v2/quota/usage instead: { user_quota { total, used, remaining }, org
// { remaining }, exceeded }, and the next reset of its members' credits from GET /api/v1/adapter/biz/settings/enterprise/quota
// ({ next_used_reset_at }). These two are read as the desktop app reads them; no team account was at hand to see their answers.
// A sign-in to a dedicated enterprise deployment (authRealm "premiumIam") uses other paths and is not read.
//
// Tokens. The app's engine is a build of OpenCode (flash-cli) that keeps its data in %USERPROFILE%\.qwenworkcn\.harness\<engine>\
// qwenwork\qwenwork.db (QwenWork: .qwenwork), laid out as OpenCode 1.x's: each model response is a row of message whose data is { role: "assistant",
// providerID, modelID, tokens: { input, output, reasoning, cache: { read, write } }, cost }. input excludes the cache, and reasoning
// is billed as output but not counted in output. Models picked by level are recorded by the level's id ("flash", "pro"), named in the
// app's texts (%APPDATA%\QwenWorkCN\data\dynamic-text\qwenwork.json: data.<zh|en>.model.<id>.label; the
// international edition's texts name none, and it records its models by their own names). cost is OpenCode's own estimate
// at list prices (0 for the levels); the account is charged in credits.
public static class QwenWorkUsage
{
    // Folder is the data folder under %APPDATA%, Home the engine's folder under %USERPROFILE%, Host the domain the token may be sent to.
    public sealed record Edition(ProviderId Id, string Folder, string Home, string Host)
    {
        public bool China => Id == ProviderId.QwenWorkCn;
        public string DefaultApi => "https://gateway." + Host;
    }
    public static readonly Edition China = new(ProviderId.QwenWorkCn, "QwenWorkCN", ".qwenworkcn", "qwenwork.cn");
    public static readonly Edition International = new(ProviderId.QwenWork, "QwenWork", ".qwenwork", "qwenwork.ai");
    public static Edition For(ProviderId id) => id == ProviderId.QwenWork ? International : China;

    public const string SignInFile = "auth-v2.dat";
    public const string ContextPath = "/api/v1/adapter/user/account-context?include=user,plan,quota";
    public const string WalletsPath = "/api/v1/adapter/user/wallets";
    public const string TeamUsagePath = "/api/v2/quota/usage";
    public const string TeamResetPath = "/api/v1/adapter/biz/settings/enterprise/quota";
    public static string DataFolder(string roaming, Edition edition) => Path.Combine(roaming, edition.Folder);

    // The app's sign-in; never shown or logged.
    public sealed record SignIn(string Token, DateTimeOffset? ExpiresAt, string Api, bool Enterprise)
    {
        public bool Expired(DateTimeOffset now) => ExpiresAt is { } at && at <= now.AddMinutes(1);
        public override string ToString() => nameof(SignIn);
    }

    public static SignIn? ParseSignIn(string? json, Edition edition)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var token = root.Get("token").Text();
            if (token.Length == 0) return null;
            return new(token, root.Get("expiresAt").Date(), Api(root.Get("endpoints").Get("apiOrigin").Text(), edition), root.Get("authRealm").Text() == "premiumIam");
        }
        catch (JsonException) { return null; }
    }

    // The token is sent only to the edition's own hosts over HTTPS; any other origin falls back to its public gateway.
    public static string Api(string origin, Edition edition) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/"
        && (uri.Host.Equals(edition.Host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + edition.Host, StringComparison.OrdinalIgnoreCase))
            ? uri.GetLeftPart(UriPartial.Authority) : edition.DefaultApi;

    public sealed record Reading(List<UsageMetric> Metrics, string Plan, bool Team);

    // An answer that is not { code: "ok", data } (or a bare object, as /api/v2/quota/usage answers) is a changed format.
    public static JsonElement Data(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        if (root.Get("code").ValueKind != JsonValueKind.Undefined && root.Get("code").Text() != "ok") throw new InvalidDataException(root.Get("msg").Text());
        return root.Get("data").ValueKind == JsonValueKind.Object ? root.Get("data") : root;
    }

    public static bool IsTeam(JsonElement context)
    {
        var data = Data(context);
        return data.Get("plan").Get("is_personal_version").ValueKind == JsonValueKind.False || data.Get("user").Get("is_biz").ValueKind == JsonValueKind.True;
    }

    public static string PlanName(JsonElement context) => Data(context).Get("plan").Get("name").Text().Trim();

    // A personal account: the credits left, and what kinds they are of when the wallets say so (wallets is default when they could
    // not be read).
    public static Reading ParsePersonal(JsonElement context, JsonElement wallets)
    {
        var data = Data(context);
        var quota = data.Get("quota");
        if (quota.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var metrics = new List<UsageMetric>();
        if (Credits(quota, "credits") is not { } main) throw new InvalidDataException();
        metrics.Add(main);
        var plan = data.Get("plan");
        var renews = plan.Get("is_subscribed").ValueKind == JsonValueKind.True ? plan.Get("next_refresh_date").Date() : null;
        if (wallets.ValueKind == JsonValueKind.Object)
        {
            var kinds = new[] { ("daily_credits", "daily", "每日积分", (DateTimeOffset?)null), ("monthly_credits", "monthly", "每月积分", renews), ("longterm_credits", "longterm", "长期积分", null) }
                .Select(kind => (kind.Item2, kind.Item3, kind.Item4, Amount: Decimal(wallets.Get(kind.Item1).Get("total_balance")) ?? 0))
                .Where(kind => kind.Amount > 0).ToList();
            // One kind alone is the same number as the credits left.
            if (kinds.Count > 1)
                foreach (var (id, label, reset, amount) in kinds)
                    metrics.Add(new UsageMetric(MetricKind.Balance, label) { Id = id, Amount = amount, Unit = "积分", ResetAt = reset });
            var soon = wallets.Get("expiring_soon");
            if (Decimal(soon.Get("total_balance")) is > 0 and var expiring)
                metrics.Add(new UsageMetric(MetricKind.Balance, "即将到期积分")
                {
                    Id = "expiring", Amount = expiring, Unit = "积分",
                    ExpiresAt = soon.Get("wallets").Items().Select(wallet => wallet.Get("valid_to").Date()).Where(at => at is not null).Min()
                });
        }
        return new(metrics, plan.Get("name").Text().Trim(), false);
    }

    // A team account: the member's credits for the period, and what the organization has left.
    public static Reading ParseTeam(JsonElement usage, string plan, DateTimeOffset? reset)
    {
        var data = Data(usage);
        var metrics = new List<UsageMetric>();
        var member = data.Get("user_quota").ValueKind == JsonValueKind.Object ? data.Get("user_quota") : data.Get("userQuota");
        if (member.ValueKind == JsonValueKind.Object && Credits(member, "member", "成员积分", reset) is { } own) metrics.Add(own);
        if (Decimal(data.Get("org").Get("remaining")) is { } left)
            metrics.Add(new UsageMetric(MetricKind.Balance, "企业积分") { Id = "organization", Amount = left, Unit = "积分" });
        if (metrics.Count == 0) throw new InvalidDataException();
        return new(metrics, plan, true);
    }

    // { total, used, remaining }: used against the total when there is one, otherwise the credits left.
    private static UsageMetric? Credits(JsonElement bucket, string id, string label = "全部积分", DateTimeOffset? reset = null)
    {
        decimal? total = Decimal(bucket.Get("total")), used = Decimal(bucket.Get("used")), left = Decimal(bucket.Get("remaining"));
        if (total is > 0)
        {
            var spent = Math.Max(0, used ?? total.Value - (left ?? total.Value));
            return new UsageMetric(MetricKind.Count, label) { Id = id, Unit = "积分", Total = Math.Max(total.Value, spent), Used = spent, ResetAt = reset };
        }
        return left is { } amount ? new UsageMetric(MetricKind.Balance, label) { Id = id, Amount = amount, Unit = "积分", ResetAt = reset } : null;
    }

    public static decimal? Decimal(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? Math.Round(number, 4) :
        value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? Math.Round(number, 4) : null;

    // The engines' databases: one per engine folder under .harness.
    public static IReadOnlyList<string> Databases(string home)
    {
        var harness = Path.Combine(home, ".harness");
        try
        {
            return Directory.Exists(harness)
                ? Directory.EnumerateDirectories(harness).Select(engine => Path.Combine(engine, "qwenwork", "qwenwork.db")).Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static string TextsFile(string roaming, Edition edition) => Path.Combine(DataFolder(roaming, edition), "data", "dynamic-text", "qwenwork.json");

    // The names the app shows for its model levels, in the display language: data.<zh|en>.model.<id>.label.
    public static Dictionary<string, string> LevelNames(string file)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = SharedFile.ReadJson(file);
            if (doc is null) return names;
            foreach (var language in Loc.IsEnglish ? new[] { "en", "zh" } : ["zh", "en"])
            {
                var models = doc.RootElement.Get("data").Get(language).Get("model");
                if (models.ValueKind != JsonValueKind.Object) continue;
                foreach (var model in models.EnumerateObject())
                    if (model.Value.Get("label").Text() is { Length: > 0 } label) names.TryAdd(model.Name, label);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return names;
    }

    private const string Fields = "id, session_id, time_created, json_extract(data, '$.modelID'), json_extract(data, '$.tokens.input'), " +
        "json_extract(data, '$.tokens.cache.read'), json_extract(data, '$.tokens.cache.write'), json_extract(data, '$.tokens.output'), " +
        "json_extract(data, '$.tokens.reasoning'), json_extract(data, '$.cost')";

    // home is the edition's engine folder (DataLocations.QwenWorkCnHome or QwenWorkHome).
    public static void ReadTokens(ProviderSnapshot snapshot, IDesktopSources desktop, string home, string roaming, Edition edition, DateTimeOffset start, DateTimeOffset now)
    {
        var name = ProviderCatalog.Name(edition.Id);
        var databases = Databases(home);
        snapshot.UsageAvailable = databases.Count > 0;
        if (!snapshot.UsageAvailable) { snapshot.UsageNote = Loc.T(Loc.Joined($"未找到本机 {name} 记录。"), $"No local {name} records were found."); return; }
        var since = start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var rows = new List<string?[]>();
        bool complete = true;
        foreach (var database in databases)
        {
            var found = desktop.QueryDatabase(database, $"SELECT {Fields} FROM message WHERE json_extract(data, '$.role') = 'assistant' AND time_created >= {since}");
            if (found is null) complete = false; else rows.AddRange(found);
        }
        var names = LevelNames(TextsFile(roaming, edition));
        snapshot.Entries = Parse(rows, names, edition).Where(e => e.Time >= start && e.Time <= now && e.Total > 0).OrderBy(e => e.Time).ToList();
        snapshot.UsageComplete = complete;
        int sessions = rows.Where(row => row.Length > 1 && row[1] is not null).Select(row => row[1]).Distinct().Count();
        snapshot.UsageNote = Loc.T(Loc.Joined($"本机 {name} {start.LocalDateTime:yyyy-MM-dd} 起 · {sessions} 个会话 · {snapshot.Entries.Count} 次模型请求"),
            $"Local {name} since {start.LocalDateTime:yyyy-MM-dd} · " + Loc.Count(sessions, "session", "sessions") + " · " + Loc.Count(snapshot.Entries.Count, "model request", "model requests"));
        if (!complete) snapshot.UsageNote += Loc.T(" · 部分记录未能读取", " · Some records could not be read");
    }

    // Rows of: id, session, time (ms), model, input, cache read, cache write, output, reasoning, cost. A cost of 0 is OpenCode's for
    // a model it has no price for, so it is left for BrimDeck's own prices.
    public static IEnumerable<TokenEntry> Parse(IEnumerable<string?[]> rows, IReadOnlyDictionary<string, string> names, Edition edition)
    {
        foreach (var row in rows)
        {
            if (row.Length < 10 || string.IsNullOrEmpty(row[0]) || !long.TryParse(row[2], out var millis)) continue;
            long Count(int i) => double.TryParse(row[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? (long)Math.Clamp(value, 0, 1e15) : 0;
            DateTimeOffset time;
            try { time = DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { continue; }
            decimal? cost = decimal.TryParse(row[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount > 0 ? amount : null;
            var model = row[3] ?? "";
            yield return new TokenEntry(ProviderCatalog.Key(edition.Id) + ":" + row[0], time, names.GetValueOrDefault(model, model), Count(4), Count(5), Count(6), 0, Count(7) + Count(8), cost);
        }
    }
}
