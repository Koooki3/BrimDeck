using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

static class GrokBotUsageTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
        const string slice = "sand.client.slice.account.auth0%7Cuser_FIXTURE.weekly-usage.cache";
        check("A Grok Bot slice file name decodes from lowercase unpadded base32", GrokBotUsage.SliceName(Base32(slice)) == slice);
        check("A file name that is not base32 is not a slice", GrokBotUsage.SliceName("not-base32!") is null && GrokBotUsage.SliceName("ABC") is null);

        string Cache(double percent, long readAt) => $$$"""
            {"schemaVersion":2,"value":{"kind":"present","selectedTeamId":null,"reading":{"usage":{"percentUsed":{{{percent.ToString(System.Globalization.CultureInfo.InvariantCulture)}}},"nextResetMs":1792308098721,"isSandTrial":false,"hasNonZeroIncludedLimit":true,"isTeamSeat":false,"onDemand":null,"grokPlanLabel":"Grok Bot Plan"},"readAtMs":{{{readAt}}}},"expiresAtMs":1792308098721}}
            """;
        var cached = GrokBotUsage.Cache(Json(Cache(1.509722, 1791721201139)));
        check("The cached weekly allowance keeps its percent, reset time, plan and read time", cached is { UsedPercent: 1.509722, Plan: "Grok Bot Plan" } &&
            cached.ResetAt == DateTimeOffset.FromUnixTimeMilliseconds(1792308098721) && cached.ReadAt == DateTimeOffset.FromUnixTimeMilliseconds(1791721201139) &&
            cached.Quotas is [{ Label: "每周额度", UsedPercent: 1.509722, Minutes: 10080 }]);
        check("An absent or unknown cache value is no reading", GrokBotUsage.Cache(Json("""{"schemaVersion":2,"value":{"kind":"absent"}}""")) is null &&
            GrokBotUsage.Cache(Json("""{"schemaVersion":9,"value":{"kind":"present","reading":{}}}""")) is null && GrokBotUsage.Cache(Json("[]")) is null);

        var status = GrokBotUsage.Status(Json("""{"usagePercent":2.565521,"nextResetTimestampUtc":"2026-10-18T07:21:38.721Z","grokPlanLabel":"Grok Bot Plan","onDemand":{"spendCents":120}}"""));
        check("The online status gives only the weekly allowance", status is { UsedPercent: 2.565521, Plan: "Grok Bot Plan" } && status.Quotas.Count == 1 &&
            status.ResetAt == DateTimeOffset.Parse("2026-10-18T07:21:38.721Z"));
        var pooled = GrokBotUsage.Status(Json("""{"usagePercent":40,"usesPooledEnterpriseAllowance":true,"grokPlanLabel":"Enterprise"}"""));
        check("A pooled enterprise allowance hides the percent", pooled is { UsedPercent: null, Plan: "Enterprise" } && pooled.Quotas.Count == 0);
        check("A status without a percent is no reading", GrokBotUsage.Status(Json("""{"nextResetTimestampUtc":"2026-10-18T07:21:38.721Z"}""")) is null);

        var roaming = Path.Combine(root, "grok-roaming");
        var folder = GrokBotUsage.Folder(roaming);
        var persistence = Path.Combine(folder, "sand-client-persistence");
        check("Without the desktop app's folder there is no cached reading", GrokBotUsage.ReadCache(folder) is null);
        Directory.CreateDirectory(persistence);
        var now = DateTimeOffset.Parse("2026-10-11T20:30:00+08:00");
        void Write(string name, string text) => File.WriteAllText(Path.Combine(persistence, Base32(name) + ".blob"), text);
        Write("sand.client.slice.account.auth0%7Cuser_OLD.weekly-usage.cache", Cache(9, now.AddDays(-2).ToUnixTimeMilliseconds()));
        Write(slice, Cache(1.5, now.AddMinutes(-10).ToUnixTimeMilliseconds()));
        Write("sand.client.slice.account.auth0%7Cuser_BROKEN.weekly-usage.cache", "{\"schemaVersion\":2,\"value\":");
        Write("sand.client.slice.account.auth0%7Cuser_FIXTURE.roster.last-roster", Cache(77, now.ToUnixTimeMilliseconds()));
        File.WriteAllText(Path.Combine(persistence, "notbase32!.blob"), Cache(55, now.ToUnixTimeMilliseconds()));
        check("The newest cached weekly allowance is read; other slices and damaged files are skipped", GrokBotUsage.ReadCache(folder) is { UsedPercent: 1.5 });

        var locations = DataLocations.Resolve(Path.Combine(root, "grok-home"), roaming, root, null, null);
        var desktop = new Desktop();
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var clock = now;
        using var service = new UsageService(desktop, locations, http, () => clock);
        DeckSettings Rows(params bool[] online) { var s = new DeckSettings { Apps = online.Select(o => new AppEntry { QuotaSource = ProviderId.GrokBot, QuotaOnline = o }).ToList() }; s.Normalize(); return s; }
        async Task<IReadOnlyList<DashboardColumn>> Columns(DeckSettings s) => DashboardUsage.Columns(s, await service.RefreshAsync(s));

        var one = Rows(true);
        check("Grok Bot is read automatically, is a quota source only and keeps its key",
            ProviderCatalog.IsBuiltIn(ProviderId.GrokBot) && !ProviderCatalog.IsUsageSource(ProviderId.GrokBot) && ProviderCatalog.HasOnlineSwitch(ProviderId.GrokBot) &&
            one.Apps[0].UsageSource == ProviderId.Claude && ProviderCatalog.Key(ProviderId.GrokBot) == "grokbot" &&
            SettingsMigrations.Read(JsonSerializer.Serialize(one)).Apps.Single().QuotaSource == ProviderId.GrokBot);

        var column = (await Columns(one)).Single().Quota;
        check("An online Grok Bot row uses the desktop app's own sign-in and shows only the weekly allowance",
            handler.Calls.SequenceEqual(["fixture-grok"]) && column.LiveQuota && !column.IsStale && column.Plan == "Grok Bot Plan" &&
            column.Quotas is [{ Label: "每周额度", UsedPercent: 2.5, Minutes: 10080 }] && column.StatusLabel == "已连接");
        handler.Calls.Clear();
        await Columns(Rows(true, true));
        check("Two online Grok Bot rows share one request", handler.Calls.Count == 1);

        handler.Calls.Clear(); clock = now.AddMinutes(1);
        handler.Status = HttpStatusCode.ServiceUnavailable;
        column = (await Columns(one)).Single().Quota;
        check("A failed request keeps the last reading, newer than the cache, and marks it as old",
            handler.Calls.Count == 1 && column.IsStale && column.Quotas.Single().UsedPercent == 2.5 && column.QuotaTime == now && column.StatusLabel == "稍后重试");
        handler.Status = HttpStatusCode.Unauthorized;
        column = (await Columns(one)).Single().Quota;
        check("A rejected sign-in drops the last reading and shows the desktop app's cache",
            column.IsStale && column.Quotas.Single().UsedPercent == 1.5 && column.StatusLabel == "登录已失效" && column.Status.Contains("缓存", StringComparison.Ordinal));
        handler.Status = HttpStatusCode.OK;

        handler.Calls.Clear(); var reads = desktop.Reads;
        var mixed = Rows(true, false);
        var columns = await Columns(mixed);
        check("An offline Grok Bot row sends no request and reads no sign-in of its own", handler.Calls.Count == 1 && desktop.Reads == reads + 1 &&
            columns[1].Quota is { IsStale: true, LiveQuota: true, StatusLabel: "本地记录" } local && local.Quotas.Single().UsedPercent == 1.5 &&
            local.QuotaTime == clock && local.Status.StartsWith("来自 Grok Bot 桌面版，最新记录于 ", StringComparison.Ordinal) && !columns[0].Quota.IsStale);
        handler.Calls.Clear(); reads = desktop.Reads;
        await Columns(Rows(false, false));
        check("Offline Grok Bot rows alone read nothing online", handler.Calls.Count == 0 && desktop.Reads == reads);

        desktop.SignIn = new(true, "");
        column = (await Columns(one)).Single().Quota;
        check("A signed-out desktop app asks for a sign-in instead of showing old figures", !column.LiveQuota && column.Quotas.Count == 0 && column.StatusLabel == "等待登录");
        desktop.SignIn = new(true, "", true);
        column = (await Columns(one)).Single().Quota;
        check("An unreadable sign-in falls back to the desktop app's cache", column.IsStale && column.Quotas.Single().UsedPercent == 1.5 && column.StatusLabel == "读取失败");
        Directory.Delete(folder, true);
        column = (await Columns(Rows(false))).Single().Quota;
        check("Without a cache an offline row says so instead of inventing a value", !column.LiveQuota && column.Quotas.Count == 0 && column.StatusLabel == "无本地记录");
    }

    private static string Base32(string text)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var result = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { bits -= 5; result.Append(alphabet[(buffer >> bits) & 31]); }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) result.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }

    private sealed class Desktop : IDesktopSources
    {
        public GrokBotSignIn SignIn { get; set; } = new(true, "fixture-grok");
        public int Reads { get; private set; }
        public GrokBotSignIn ReadGrokBotDesktop(DataLocations locations) { Reads++; return SignIn; }
        public string? ReadCursorToken(string database) => "fixture-cursor";
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri != GrokBotUsage.StatusUri || request.Method != HttpMethod.Post)
                throw new InvalidOperationException("Unexpected Grok Bot destination");
            lock (Calls) Calls.Add(request.Headers.Authorization?.Parameter ?? "");
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Status == HttpStatusCode.OK
                ? """{"usagePercent":2.5,"nextResetTimestampUtc":"2026-10-18T07:21:38.721Z","grokPlanLabel":"Grok Bot Plan","onDemand":{"spendCents":120}}""" : "") });
        }
    }
}
