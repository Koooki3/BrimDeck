using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

static class OpenCodeUsageTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var profile = Path.Combine(root, "opencode-profile");
        var xdg = Path.Combine(root, "opencode-xdg");
        var defaultData = Path.Combine(profile, ".local", "share", "opencode");
        check("OpenCode keeps its data in .local\\share\\opencode unless XDG_DATA_HOME names an absolute folder",
            DataLocations.Resolve(profile, root, root, null, null).OpenCodeData == defaultData &&
            DataLocations.Resolve(profile, root, root, null, null).OpenCodeDatabase == Path.Combine(defaultData, "opencode.db") &&
            OpenCodeUsage.DataFolder(profile, xdg) == Path.Combine(xdg, "opencode") && OpenCodeUsage.DataFolder(profile, "relative") == defaultData &&
            OpenCodeUsage.DataFolder(profile, " ") == defaultData);
        check("OPENCODE_DB names the database, relative to the data folder or absolute; :memory: is ignored",
            OpenCodeUsage.Database(defaultData, null) == Path.Combine(defaultData, "opencode.db") &&
            OpenCodeUsage.Database(defaultData, "other.db") == Path.Combine(defaultData, "other.db") &&
            OpenCodeUsage.Database(defaultData, Path.Combine(root, "x.db")) == Path.Combine(root, "x.db") &&
            OpenCodeUsage.Database(defaultData, ":memory:") == Path.Combine(defaultData, "opencode.db"));
        check("OpenCode is an automatic quota and statistics source keyed opencode",
            ProviderCatalog.IsBuiltIn(ProviderId.OpenCode) && ProviderCatalog.IsUsageSource(ProviderId.OpenCode) && ProviderCatalog.Key(ProviderId.OpenCode) == "opencode" &&
            ProviderCatalog.Name(ProviderId.OpenCode) == "OpenCode" && (int)ProviderId.OpenCode == 17 &&
            PanelThemes.AppKey(ProviderId.OpenCode) == "opencode" && PanelThemes.AppKeys.Contains("opencode"));
        check("OpenCode Go is entered by hand, saved as opencode-go, and shares the color of sources entered by hand",
            !ProviderCatalog.IsBuiltIn(ProviderId.OpenCodeGo) && !ProviderCatalog.IsUsageSource(ProviderId.OpenCodeGo) && !ProviderCatalog.NeedsSite(ProviderId.OpenCodeGo) &&
            ProviderCatalog.Key(ProviderId.OpenCodeGo) == "opencode-go" && (int)ProviderId.OpenCodeGo == 18 &&
            AppPresets.ThemeColor(ProviderId.OpenCodeGo) == AppPresets.ManualColor && PanelThemes.AppKey(ProviderId.OpenCodeGo) == "manual" &&
            JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.OpenCodeGo }] }))!.Apps.Single().QuotaSource == ProviderId.OpenCodeGo);

        check("OpenCode defaults to a gray that stays apart from Cursor's in every built-in theme",
            AppPresets.ThemeColor(ProviderId.OpenCode) == "#8E8E93" &&
            PanelThemes.BuiltIns.SelectMany(theme => theme.File.Night is null ? [PanelThemes.Resolve(theme)] : new[] { PanelThemes.Resolve(theme), PanelThemes.Resolve(theme, true) })
                .All(palette => palette.Apps["opencode"] is var gray && Math.Max(Math.Abs(gray.R - gray.G), Math.Max(Math.Abs(gray.G - gray.B), Math.Abs(gray.R - gray.B))) <= 24 &&
                    ThemeColor.Contrast(gray, palette.Apps["cursor"]) >= 1.75));

        Credentials(root, check);
        Allowance(check);
        Records(root, check);
        await Service(root, check);
    }

    private static string OAuth(string access, DateTimeOffset expires, string org = "org_fixture", string server = "https://opencode.ai/console") =>
        $$$"""{"type":"oauth","access":"{{{access}}}","refresh":"fixture-refresh","expires":{{{expires.ToUnixTimeMilliseconds()}}},"metadata":{"server":"{{{server}}}","orgID":"{{{org}}}","email":"someone@example.test"}}""";

    private static void Credentials(string root, Action<string, bool> check)
    {
        var expires = DateTimeOffset.Parse("2026-11-25T00:00:00Z");
        check("A Console sign-in gives its token, expiry, workspace and address",
            OpenCodeUsage.Parse(OAuth("fixture-access", expires)) is { Token: "fixture-access", Workspace: "org_fixture", Server: "https://opencode.ai/console" } signedIn &&
            signedIn.ExpiresAt == expires && signedIn.Expired(expires) && !signedIn.Expired(expires.AddMinutes(-1)));
        check("An API key connection has no expiry; other kinds and broken JSON are passed over",
            OpenCodeUsage.Parse("""{"type":"key","key":"oc_sk_fixture"}""") is { Token: "oc_sk_fixture", ExpiresAt: null, Workspace: "", Server: OpenCodeUsage.DefaultServer } &&
            OpenCodeUsage.Parse("""{"type":"wellknown","key":"x"}""") is null && OpenCodeUsage.Parse("""{"type":"oauth","access":""}""") is null &&
            OpenCodeUsage.Parse("{bad") is null && OpenCodeUsage.Parse((string?)null) is null);
        check("The sign-in never shows its token as text", !OpenCodeUsage.Parse(OAuth("fixture-access", expires))!.ToString().Contains("fixture"));

        var data = Path.Combine(root, "opencode-credentials"); Directory.CreateDirectory(data);
        var desktop = new Database();
        check("Without a credential row or auth.json there is no sign-in", OpenCodeUsage.ReadCredential(desktop, "db", data) is null &&
            desktop.Queries.Any(q => q.Contains("FROM credential") && q.Contains("active = 1") && q.Contains("integration_id = 'opencode'")));
        desktop.Credentials = [["""{"type":"key","key":"oc_sk_row"}"""], [OAuth("fixture-access", expires)]];
        check("A Console sign-in is used before an API key", OpenCodeUsage.ReadCredential(desktop, "db", data) is { Token: "fixture-access" });
        desktop.Credentials = [["{bad"], ["""{"type":"key","key":"oc_sk_row"}"""]];
        check("An unreadable row is skipped for the next", OpenCodeUsage.ReadCredential(desktop, "db", data) is { Token: "oc_sk_row" });
        desktop.Credentials = null;
        File.WriteAllText(Path.Combine(data, "auth.json"), """{"anthropic":{"type":"api","key":"sk-ant"},"opencode-go":{"type":"api","key":" oc_sk_go "},"opencode":{"type":"api","key":"oc_sk_zen"}}""");
        check("1.x keys in auth.json are read when the database has none, OpenCode Go first", OpenCodeUsage.ReadCredential(desktop, "db", data) is { Token: "oc_sk_go", Workspace: "" });
        File.WriteAllText(Path.Combine(data, "auth.json"), """{"opencode":{"type":"oauth","access":"x"}}""");
        check("Only API keys are taken from auth.json", OpenCodeUsage.ReadCredential(desktop, "db", data) is null);
        File.WriteAllText(Path.Combine(data, "auth.json"), "{bad");
        check("A broken auth.json is passed over", OpenCodeUsage.ReadCredential(desktop, "db", data) is null);

        check("The Go allowance is read at the Console's origin; the retired console.opencode.ai maps onto opencode.ai",
            OpenCodeUsage.UsageAddress(OpenCodeUsage.DefaultServer)?.AbsoluteUri == "https://opencode.ai/zen/go/v1/usage" &&
            OpenCodeUsage.UsageAddress("https://console.opencode.ai")?.AbsoluteUri == "https://opencode.ai/zen/go/v1/usage" &&
            OpenCodeUsage.UsageAddress("https://opencode.example:8443/console")?.AbsoluteUri == "https://opencode.example:8443/zen/go/v1/usage");
        check("A sign-in address that is not HTTPS is never used", OpenCodeUsage.UsageAddress("http://opencode.ai/console") is null && OpenCodeUsage.UsageAddress("not a url") is null);
    }

    private static void Allowance(Action<string, bool> check)
    {
        using var doc = JsonDocument.Parse("""
            {"usage":{"rolling":{"status":"ok","percent":4,"resetsAt":"2026-10-26T19:30:00.000Z"},
                      "weekly":{"status":"ok","percent":1,"resetsAt":"2026-11-02T00:00:00.000Z"},
                      "monthly":{"status":"ok","percent":0,"resetsAt":"2026-11-26T00:00:00.000Z"}}}
            """);
        var quotas = OpenCodeUsage.Allowance(doc.RootElement, out bool limited);
        check("The Go usage gives the 5-hour, weekly and monthly shares with their reset times", !limited && quotas.Count == 3 &&
            quotas[0] is { Label: "5 小时额度", UsedPercent: 4, Minutes: 300 } && quotas[0].ResetAt == DateTimeOffset.Parse("2026-10-26T19:30:00Z") &&
            quotas[1] is { Label: "每周额度", UsedPercent: 1, Minutes: 10080 } && quotas[2] is { Label: "每月额度", UsedPercent: 0, Minutes: null } &&
            quotas[2].ResetAt == DateTimeOffset.Parse("2026-11-26T00:00:00Z"));
        using var full = JsonDocument.Parse("""{"usage":{"rolling":{"status":"rate-limited","percent":104},"weekly":{"status":"ok"},"monthly":{"percent":-3}}}""");
        quotas = OpenCodeUsage.Allowance(full.RootElement, out limited);
        check("A rate-limited window is reported, shares are kept within 0 to 100, and a window without a share is left out", limited && quotas.Count == 2 &&
            quotas[0] is { UsedPercent: 100, ResetAt: null } && quotas[1] is { Label: "每月额度", UsedPercent: 0 });
        check("An answer without usage has no windows", OpenCodeUsage.Allowance(default, out _).Count == 0 && OpenCodeUsage.Allowance(JsonDocument.Parse("[]").RootElement, out _).Count == 0);
    }

    private static void Records(string root, Action<string, bool> check)
    {
        var start = new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Local));
        string At(double hours) => start.AddHours(hours).ToUnixTimeMilliseconds().ToString();
        // id, session, time, model (2.x), model (1.x), input, cache read, cache write, output, reasoning, cost
        var entries = OpenCodeUsage.Parse([
            ["m1", "s1", At(1), "glm-5.3", null, "7942", "1408", "0", "116", "20", "0.01199528"],
            ["m2", "s2", At(2), null, "claude-sonnet-4", "10", "0", "300", "5", "0", "1.5e-3"],
            ["m3", "s2", "not a time", "x", null, "1", "1", "1", "1", "1", "0"],
            ["m4", "s2", At(3), "flint-40b-free", null, "100", null, null, "7", null, "0"],
            ["m5", "s2", At(4), "grok-4.7", null, "-5", "1e30", "NaN", "2", "0", "-1"]]).ToList();
        check("2.x records give the model, tokens and the cost OpenCode recorded; reasoning counts as output",
            entries.Count == 4 && entries[0] is { Key: "opencode:m1", Model: "glm-5.3", Input: 7942, CacheRead: 1408, CacheWrite: 0, CacheWriteHour: 0, Output: 136, ReportedCostUsd: 0.01199528m } &&
            entries[0].Time == start.AddHours(1));
        check("1.x records give the model from modelID", entries[1] is { Key: "opencode:m2", Model: "claude-sonnet-4", Input: 10, CacheWrite: 300, Output: 5, ReportedCostUsd: 0.0015m });
        check("A free model costs 0 and missing counts are 0", entries[2] is { Model: "flint-40b-free", Input: 100, CacheRead: 0, Output: 7, ReportedCostUsd: 0m });
        check("Counts are kept within range and a negative cost is left for an estimate",
            entries[3] is { Input: 0, CacheRead: 1_000_000_000_000_000, CacheWrite: 0, Output: 2, ReportedCostUsd: null });

        var database = Path.Combine(root, "opencode-records.db");
        var desktop = new Database();
        var snapshot = new ProviderSnapshot(ProviderId.OpenCode);
        OpenCodeUsage.Read(snapshot, desktop, database, start, start.AddDays(1));
        check("Without the database the statistics are unavailable and nothing is queried", !snapshot.UsageAvailable && snapshot.Entries.Count == 0 && desktop.Queries.Count == 0);

        File.WriteAllText(database, "");
        desktop.Tables = [["session_message"]];
        desktop.Current = [
            ["m1", "s1", At(1), "glm-5.3", null, "100", "0", "0", "10", "0", "0.001"],
            ["m2", "s1", At(2), "iceman-contributor-free", null, null, null, null, null, null, null],
            ["m3", "s2", At(30), "gemini-4-argon", null, "200", "0", "0", "20", "0", "0.002"],
            ["m4", "s3", At(-2), "gemini-4-argon", null, "200", "0", "0", "20", "0", "0.002"]];
        snapshot = new ProviderSnapshot(ProviderId.OpenCode);
        OpenCodeUsage.Read(snapshot, desktop, database, start, start.AddDays(1));
        var current = desktop.Queries.Single(q => q.Contains("FROM session_message"));
        check("2.x records are read from session_message: model responses since the start only",
            current.Contains("type = 'assistant'") && current.Contains("time_created >= " + start.ToUnixTimeMilliseconds()) &&
            current.Contains("$.model.id") && current.Contains("$.tokens.cache.read") && current.Contains("$.cost") && !desktop.Queries.Any(q => q.Contains("FROM message ")));
        check("Failed responses without tokens and records outside the period are left out", snapshot.UsageAvailable && snapshot.UsageComplete &&
            snapshot.Entries.Select(e => e.Key).SequenceEqual(["opencode:m1"]));
        check("The note counts the sessions and model requests", snapshot.UsageNote.Contains("OpenCode") && snapshot.UsageNote.Contains("1 次模型请求"));

        desktop.Queries.Clear();
        desktop.Tables = [["session_message"], ["message"]];
        desktop.Legacy = [["v1", "old", At(5), null, "kimi-k2", "50", "0", "0", "5", "0", "0.0004"]];
        snapshot = new ProviderSnapshot(ProviderId.OpenCode);
        OpenCodeUsage.Read(snapshot, desktop, database, start, start.AddDays(2));
        var legacy = desktop.Queries.Single(q => q.Contains("FROM message "));
        check("1.x records left in the message table are read as well", legacy.Contains("json_extract(data, '$.role') = 'assistant'") && legacy.Contains("$.modelID") &&
            snapshot.Entries.Select(e => e.Key).SequenceEqual(["opencode:m1", "opencode:v1", "opencode:m3"]) && snapshot.Entries[1].Model == "kimi-k2");

        desktop.Legacy = null;
        snapshot = new ProviderSnapshot(ProviderId.OpenCode);
        OpenCodeUsage.Read(snapshot, desktop, database, start, start.AddDays(2));
        check("A table that cannot be read marks the statistics incomplete", !snapshot.UsageComplete && snapshot.Entries.Count == 2);
        desktop.Tables = null;
        snapshot = new ProviderSnapshot(ProviderId.OpenCode);
        OpenCodeUsage.Read(snapshot, desktop, database, start, start.AddDays(2));
        check("A database that cannot be opened is reported, not shown as empty", snapshot.UsageAvailable && !snapshot.UsageComplete && snapshot.Entries.Count == 0);
    }

    private static async Task Service(string root, Action<string, bool> check)
    {
        var xdg = Path.Combine(root, "opencode-service");
        var locations = DataLocations.Resolve(Path.Combine(root, "opencode-profile"), root, root, null, null).WithOpenCode(Path.Combine(root, "opencode-profile"), xdg, null);
        Directory.CreateDirectory(locations.OpenCodeData);
        File.WriteAllText(locations.OpenCodeDatabase, "");
        var now = DateTimeOffset.Parse("2026-10-26T12:00:00+08:00");
        var desktop = new Database { Tables = [["session_message"]], Current = [["m1", "s1", now.AddHours(-1).ToUnixTimeMilliseconds().ToString(), "glm-5.3", null, "100", "0", "0", "10", "0", "0.001"]] };
        using var handler = new Handler(); using var http = new HttpClient(handler);
        using var service = new UsageService(desktop, locations, http, () => now);
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.OpenCode }] };
        settings.Normalize();
        check("An OpenCode row reads its own statistics", settings.Apps[0].UsageSource == ProviderId.OpenCode);
        async Task<ProviderSnapshot> Refresh() => (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.OpenCode);

        var snapshot = await Refresh();
        check("Without a sign-in OpenCode asks to sign in to the Console and sends nothing, but still counts tokens",
            handler.Calls.Count == 0 && snapshot.StatusLabel == Loc.T("未连接", "Not connected") && snapshot.Status.Contains("OpenCode Console") &&
            snapshot.Entries.Count == 1 && snapshot.Entries[0].ReportedCostUsd == 0.001m);

        desktop.Credentials = [[OAuth("fixture-access", now.AddMinutes(-1))]];
        snapshot = await Refresh();
        check("An expired sign-in sends nothing and is never renewed", handler.Calls.Count == 0 && snapshot.StatusLabel == Loc.T("登录已过期", "Sign-in expired"));

        desktop.Credentials = [[OAuth("fixture-access", now.AddDays(20))]];
        handler.Respond = _ => Reply("""{"usage":{"rolling":{"status":"ok","percent":4,"resetsAt":"2026-10-26T19:30:00.000Z"},"weekly":{"status":"ok","percent":1,"resetsAt":"2026-11-02T00:00:00.000Z"},"monthly":{"status":"ok","percent":0,"resetsAt":"2026-11-26T00:00:00.000Z"}}}""");
        snapshot = await Refresh();
        var call = handler.Calls.Single();
        check("The Go allowance is read with the Console sign-in, its workspace and a user agent Cloudflare accepts",
            call.Uri == "https://opencode.ai/zen/go/v1/usage" && call.Authorization == "Bearer fixture-access" && call.Workspace == "org_fixture" && call.UserAgent.StartsWith("BrimDeck/", StringComparison.Ordinal));
        check("The Go allowance shows as three windows from the account", snapshot.LiveQuota && snapshot.Scope == "account" && snapshot.Source == "OpenCode Go" &&
            snapshot.Quotas.Select(q => q.Minutes).SequenceEqual(new int?[] { 300, 10080, null }) && snapshot.StatusLabel == Loc.T("已连接", "Connected"));
        check("The token is not written to the snapshot", new[] { snapshot.Status, snapshot.StatusLabel, snapshot.Source, snapshot.Plan, snapshot.UsageNote }.All(text => !text.Contains("fixture")));

        handler.Respond = _ => Reply("""{"usage":{"rolling":{"status":"rate-limited","percent":100},"weekly":{"status":"ok","percent":40},"monthly":{"status":"ok","percent":20}}}""");
        snapshot = await Refresh();
        check("A reached limit is told in the status", snapshot.LiveQuota && snapshot.Status.Contains("上限"));

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"type":"error","error":{"type":"AuthError","message":"No subscription"}}""") };
        snapshot = await Refresh();
        check("OpenCode's own 403 means the workspace has no Go plan", !snapshot.LiveQuota && snapshot.StatusLabel == Loc.T("未订阅 Go", "No Go plan") && snapshot.Quotas.Count == 0);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"title":"Error 1010","cloudflare_error":true,"error_code":1010}""") };
        snapshot = await Refresh();
        check("A Cloudflare refusal is a passing failure, not a missing plan", !snapshot.LiveQuota && snapshot.StatusLabel == Loc.T("暂不可用", "Unavailable"));
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
        snapshot = await Refresh();
        check("A rejected sign-in asks to sign in again", snapshot.StatusLabel == Loc.T("登录已失效", "Sign-in expired"));
        handler.Respond = _ => Reply("""{"usage":{}}""");
        snapshot = await Refresh();
        check("An answer without windows is a format change", snapshot.StatusLabel == Loc.T("数据不可用", "Data unavailable"));

        desktop.Credentials = [[OAuth("fixture-access", now.AddDays(20), server: "http://opencode.ai/console")]];
        handler.Calls.Clear();
        snapshot = await Refresh();
        check("A sign-in to a plain HTTP address is never sent", handler.Calls.Count == 0 && !snapshot.LiveQuota);

        desktop.Credentials = null;
        File.WriteAllText(Path.Combine(locations.OpenCodeData, "auth.json"), """{"opencode-go":{"type":"api","key":"oc_sk_fixture"}}""");
        handler.Calls.Clear();
        handler.Respond = _ => Reply("""{"usage":{"rolling":{"percent":10},"weekly":{"percent":5},"monthly":{"percent":1}}}""");
        snapshot = await Refresh();
        check("A 1.x key is sent without a workspace", snapshot.LiveQuota && handler.Calls.Single() is { Authorization: "Bearer oc_sk_fixture", Workspace: null });
    }

    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Database : IDesktopSources
    {
        public IReadOnlyList<string?[]>? Credentials { get; set; } = [];
        public IReadOnlyList<string?[]>? Tables { get; set; } = [];
        public IReadOnlyList<string?[]>? Current { get; set; } = [];
        public IReadOnlyList<string?[]>? Legacy { get; set; } = [];
        public List<string> Queries { get; } = [];
        public IReadOnlyList<string?[]>? QueryDatabase(string database, string sql)
        {
            lock (Queries) Queries.Add(sql);
            return sql.Contains("FROM credential") ? Credentials : sql.Contains("sqlite_master") ? Tables : sql.Contains("FROM session_message") ? Current : sql.Contains("FROM message ") ? Legacy : null;
        }
        public string? ReadCursorToken(string database) => null;
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Reply("{}");
        public List<(string Uri, string? Authorization, string? Workspace, string UserAgent)> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-opencode-org-id", out var org) ? org.Single() : null, request.Headers.UserAgent.ToString()));
            return Task.FromResult(Respond(request));
        }
    }
}
