using System.Net;
using System.Text.Json;
using BrimDeck.Core;

// QwenWork CN (千问办公) and the international QwenWork. The fixtures follow what the QwenWork CN 2.1.0 and QwenWork 2.0.0 desktop
// apps' account APIs and engine databases held on 2026-11-18, with the account details replaced: a free QwenWork CN account, then the
// same account after subscribing to 个人基础版, and a free QwenWork account.
static class QwenWorkUsageTests
{
    private const string FreeContext = """
        {"code":"ok","data":{"user":{"id":"user-1","is_biz":false},"plan":{"pid":"subscription-cn-free","name":"个人免费版","user_type":"personal",
         "is_personal_version":true,"next_refresh_date":null,"is_subscribed":false},"quota":{"total":null,"used":null,"remaining":400,"exceeded":false}}}
        """;
    private const string PaidContext = """
        {"code":"ok","data":{"user":{"id":"user-1","is_biz":false},"plan":{"pid":"qwen-office-basic","name":"个人基础版","user_type":"personal",
         "is_personal_version":true,"next_due_date":"2026-12-18T08:19:54Z","next_refresh_date":"2026-12-18T16:19:54+08:00","is_subscribed":true,"period":"monthly"},
         "quota":{"total":null,"used":null,"remaining":880.9101,"exceeded":false}}}
        """;
    private const string PaidWallets = """
        {"code":"ok","data":{"active_wallets":{"wallets":[{"balance":80.9101,"valid_to":"2026-12-01T00:00:00+08:00"},{"balance":500,"valid_to":"2026-12-18T16:19:54+08:00"},
         {"balance":300,"valid_to":"2027-02-18T15:24:14.123+08:00"}],"total":3,"page_size":20,"page_number":1},"daily_credits":{"total_balance":0},
         "expiring_soon":{"count":0,"total_balance":0,"wallets":[]},"longterm_credits":{"total_balance":300},"monthly_credits":{"total_balance":580.9101}}}
        """;
    private const string InternationalContext = """
        {"code":"ok","data":{"user":{"id":"user-2","is_biz":false,"language":"en-US","nation":"US"},"plan":{"pid":"subscription-sgp-free","name":"Free",
         "user_type":"personal","is_personal_version":true,"next_refresh_date":null,"is_subscribed":false},"quota":{"total":null,"used":null,"remaining":685.8297,"exceeded":false}}}
        """;
    private const string SignIn = """
        {"schemaVersion":3,"authRealm":"standard","endpoints":{"authOrigin":"https://gateway.qwenwork.cn","apiOrigin":"https://gateway.qwenwork.cn",
         "webIdentityApiOrigin":"https://qwenwork.cn"},"token":"token-1","refreshToken":"refresh-1","expiresAt":"2026-11-25T07:24:48Z","user":{"id":"user-1"}}
        """;
    private const string InternationalSignIn = """
        {"schemaVersion":3,"authRealm":"standard","endpoints":{"authOrigin":"https://gateway.qwenwork.ai","apiOrigin":"https://gateway.qwenwork.ai"},
         "token":"token-2","refreshToken":"refresh-2","expiresAt":"2026-11-25T08:45:02Z","user":{"id":"user-2"}}
        """;
    private static readonly QwenWorkUsage.Edition China = QwenWorkUsage.China, International = QwenWorkUsage.International;
    private static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }

    public static async Task Run(string root, Action<string, bool> check)
    {
        Catalog(root, check);
        Parse(check);
        Tokens(root, check);
        await Service(root, check);
    }

    private static void Catalog(string root, Action<string, bool> check)
    {
        check("QwenWork CN and QwenWork are automatic quota and statistics sources saved as qwenwork-cn and qwenwork, sharing the qwenwork color",
            new[] { ProviderId.QwenWorkCn, ProviderId.QwenWork }.All(id => ProviderCatalog.IsBuiltIn(id) && ProviderCatalog.IsUsageSource(id) &&
                ProviderCatalog.Group(id) == Loc.T("自动读取", "Read automatically") && PanelThemes.AppKey(id) == "qwenwork" && AppPresets.ThemeColor(id) == "#1DCA63" &&
                JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { Apps = [new AppEntry { QuotaSource = id }] }))!.Apps.Single().QuotaSource == id) &&
            (int)ProviderId.QwenWorkCn == 35 && (int)ProviderId.QwenWork == 36 && ProviderCatalog.Key(ProviderId.QwenWorkCn) == "qwenwork-cn" && ProviderCatalog.Key(ProviderId.QwenWork) == "qwenwork" &&
            ProviderCatalog.Name(ProviderId.QwenWork) == "QwenWork" && PanelThemes.AppKeys.Contains("qwenwork") &&
            QwenWorkUsage.For(ProviderId.QwenWork) == International && QwenWorkUsage.For(ProviderId.QwenWorkCn) == China);
        var profile = Path.Combine(root, "qwenwork-profile");
        var locations = DataLocations.Resolve(profile, root, root, null, null);
        check("Each edition's records are looked for under the user's profile, whatever folder the app is installed in",
            locations.QwenWorkCnHome == Path.Combine(profile, ".qwenworkcn") && locations.QwenWorkHome == Path.Combine(profile, ".qwenwork") &&
            QwenWorkUsage.DataFolder(@"C:\Users\someone\AppData\Roaming", China) == @"C:\Users\someone\AppData\Roaming\QwenWorkCN" &&
            QwenWorkUsage.DataFolder(@"C:\Users\someone\AppData\Roaming", International) == @"C:\Users\someone\AppData\Roaming\QwenWork");
        check("Chinese text drops the spaces beside a Chinese name and keeps them beside a Latin one",
            Loc.Joined("请先在 千问办公 中登录") == "请先在千问办公中登录" && Loc.Joined("请先在 QwenWork 中登录") == "请先在 QwenWork 中登录" &&
            Loc.Joined("本机 千问办公 2026-11-18 起") == "本机千问办公 2026-11-18 起");
    }

    private static void Parse(Action<string, bool> check)
    {
        var signIn = QwenWorkUsage.ParseSignIn(SignIn, China)!;
        check("The desktop app's sign-in gives the token, its expiry and the account API",
            signIn is { Token: "token-1", Api: "https://gateway.qwenwork.cn", Enterprise: false } && signIn.ExpiresAt == DateTimeOffset.Parse("2026-11-25T07:24:48Z") &&
            !signIn.Expired(DateTimeOffset.Parse("2026-11-24T00:00:00Z")) && signIn.Expired(DateTimeOffset.Parse("2026-11-25T07:24:00Z")) && signIn.ToString() == "SignIn");
        check("The international sign-in goes to its own gateway", QwenWorkUsage.ParseSignIn(InternationalSignIn, International)!.Api == "https://gateway.qwenwork.ai");
        check("The token goes only to the edition's own hosts over HTTPS",
            QwenWorkUsage.Api("https://evil.example.com", China) == China.DefaultApi && QwenWorkUsage.Api("http://gateway.qwenwork.cn", China) == China.DefaultApi &&
            QwenWorkUsage.Api("https://gateway.qwenwork.cn.evil.com", China) == China.DefaultApi && QwenWorkUsage.Api("https://staging-gateway.qwenwork.cn", China) == "https://staging-gateway.qwenwork.cn" &&
            QwenWorkUsage.Api("https://gateway.qwenwork.cn", International) == "https://gateway.qwenwork.ai" && QwenWorkUsage.Api("https://gateway.qwenwork.ai", China) == "https://gateway.qwenwork.cn");
        check("A sign-in to a dedicated deployment, or one without a token, is told apart",
            QwenWorkUsage.ParseSignIn(SignIn.Replace("\"standard\"", "\"premiumIam\""), China)!.Enterprise && QwenWorkUsage.ParseSignIn("""{"token":""}""", China) is null &&
            QwenWorkUsage.ParseSignIn("{", China) is null);

        var free = QwenWorkUsage.ParsePersonal(Json(FreeContext), default);
        check("A free account shows all its credits as a number left, its label naming the unit",
            free.Plan == "个人免费版" && !free.Team && free.Metrics.Single() is { Kind: MetricKind.Balance, Label: "全部积分", Amount: 400m, Unit: "积分", Currency: "" } &&
            free.Metrics[0].ValueText == "400" && free.Metrics[0].ValueSuffix == Loc.T("可用", "left") && free.Metrics[0].AmountText(1000) == Loc.T("1,000 积分", "1,000 credits"));

        var paid = QwenWorkUsage.ParsePersonal(Json(PaidContext), QwenWorkUsage.Data(Json(PaidWallets)));
        check("A subscribed account shows all its credits, then the monthly credits renewed with the plan and the long-term ones",
            paid.Plan == "个人基础版" && paid.Metrics.Select(m => (m.Id, m.Label, m.Amount)).SequenceEqual([("credits", "全部积分", 880.9101m), ("monthly", "每月积分", 580.9101m), ("longterm", "长期积分", 300m)]) &&
            paid.Metrics[1].ResetAt == DateTimeOffset.Parse("2026-12-18T16:19:54+08:00") && paid.Metrics[2].ResetAt is null &&
            paid.Metrics[0].ValueText == "880.91" && paid.Metrics.All(m => m.Total is null));
        var abroad = QwenWorkUsage.ParsePersonal(Json(InternationalContext), default);
        check("An international account, which has no wallets, shows all its credits alone", abroad.Plan == "Free" && abroad.Metrics.Single() is { Id: "credits", Amount: 685.8297m });
        var single = QwenWorkUsage.ParsePersonal(Json(FreeContext), Json("""{"monthly_credits":{"total_balance":400},"longterm_credits":{"total_balance":0}}"""));
        check("Credits of one kind only are not shown twice", single.Metrics.Count == 1);
        var soon = QwenWorkUsage.ParsePersonal(Json(FreeContext), Json("""{"expiring_soon":{"count":2,"total_balance":50,"wallets":[{"balance":30,"valid_to":"2026-11-30T00:00:00+08:00"},{"balance":20,"valid_to":"2026-11-20T00:00:00+08:00"}]}}"""));
        check("Credits that expire soon are shown with the first expiry",
            soon.Metrics[^1] is { Id: "expiring", Amount: 50m } && soon.Metrics[^1].ExpiresAt == DateTimeOffset.Parse("2026-11-20T00:00:00+08:00"));
        var counted = QwenWorkUsage.ParsePersonal(Json("""{"code":"ok","data":{"quota":{"total":1000,"used":250,"remaining":750}}}"""), default);
        check("Credits with a total are counted against it", counted.Metrics.Single() is { Kind: MetricKind.Count, Total: 1000m, Used: 250m } && counted.Metrics[0].UsedPercent == 25);
        bool threw = false;
        try { QwenWorkUsage.ParsePersonal(Json("""{"code":"bad-request","msg":"include is required"}"""), default); } catch (InvalidDataException) { threw = true; }
        check("An answer other than ok is a changed format", threw);

        check("A team account is told apart by its plan or its user",
            QwenWorkUsage.IsTeam(Json("""{"code":"ok","data":{"plan":{"is_personal_version":false}}}""")) && QwenWorkUsage.IsTeam(Json("""{"code":"ok","data":{"user":{"is_biz":true}}}""")) &&
            !QwenWorkUsage.IsTeam(Json(PaidContext)));
        var reset = DateTimeOffset.Parse("2026-12-01T00:00:00+08:00");
        var team = QwenWorkUsage.ParseTeam(Json("""{"user_id":"user-1","user_type":"enterprise","user_quota":{"total":2000,"used":500,"remaining":1500},"org":{"remaining":90000},"exceeded":false}"""), "企业版", reset);
        check("A team account shows the member's credits with their reset and what the organization has left",
            team.Team && team.Plan == "企业版" && team.Metrics.Select(m => (m.Id, m.Label)).SequenceEqual([("member", "成员积分"), ("organization", "企业积分")]) &&
            team.Metrics[0] is { Kind: MetricKind.Count, Total: 2000m, Used: 500m } && team.Metrics[0].ResetAt == reset && team.Metrics[1] is { Kind: MetricKind.Balance, Amount: 90000m });
    }

    private static void Tokens(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "qwenwork-home");
        var roaming = Path.Combine(root, "qwenwork-roaming");
        var database = Path.Combine(home, ".harness", "flash-cli", "qwenwork", "qwenwork.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        File.WriteAllText(database, "");
        Directory.CreateDirectory(Path.GetDirectoryName(QwenWorkUsage.TextsFile(roaming, China))!);
        File.WriteAllText(QwenWorkUsage.TextsFile(roaming, China), """
            {"data":{"zh":{"model":{"flash":{"label":"标准"},"pro":{"label":"高级"}}},"en":{"model":{"flash":{"label":"Standard"},"pro":{"label":"Advanced"}}}}}
            """);
        check("The engine databases are found under .harness", QwenWorkUsage.Databases(home).SequenceEqual([database]) && QwenWorkUsage.Databases(Path.Combine(root, "none")).Count == 0);

        var start = DateTimeOffset.FromUnixTimeMilliseconds(1794989900000);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1794992000000);
        var desktop = new Desktop
        {
            Rows =
            [
                ["msg_1", "ses_1", "1794989944826", "flash", "40646", "0", "0", "166", "38", "0"],
                ["msg_2", "ses_2", "1794989974863", "deepseek-flash", "37022", "3840", "0", "29", "30", "0.00560022"],
                ["msg_3", "ses_3", "1794989979408", "glm-5.3", "0", "0", "0", "0", "0", "0"]
            ]
        };
        var snapshot = new ProviderSnapshot(ProviderId.QwenWorkCn);
        QwenWorkUsage.ReadTokens(snapshot, desktop, home, roaming, China, start, now);
        var levels = QwenWorkUsage.LevelNames(QwenWorkUsage.TextsFile(roaming, China));
        check("Each model response counts its input apart from the cache, and its reasoning as output",
            snapshot.UsageAvailable && snapshot.UsageComplete && snapshot.Entries.Count == 2 &&
            snapshot.Entries[0] is { Key: "qwenwork-cn:msg_1", Input: 40646, CacheRead: 0, Output: 204, ReportedCostUsd: null } &&
            snapshot.Entries[1] is { Model: "deepseek-flash", Input: 37022, CacheRead: 3840, Output: 59, ReportedCostUsd: 0.00560022m });
        check("A level is named as the app names it, and a model by its own name", snapshot.Entries[0].Model == levels["flash"] && levels["flash"] == (Loc.IsEnglish ? "Standard" : "标准"));
        check("Only assistant rows since the start are asked for", desktop.Queries.Single().Contains("'assistant'") && desktop.Queries.Single().Contains("time_created >= 1794989900000"));
        check("The note counts sessions and model requests", snapshot.UsageNote.Contains(Loc.T("3 个会话", "3 sessions")) && snapshot.UsageNote.Contains(Loc.T("2 次模型请求", "2 model requests")) &&
            (Loc.IsEnglish || snapshot.UsageNote.StartsWith("本机千问办公 ")));

        // The international texts name no levels, so its models keep their own names; its keys stay apart from the China edition's.
        var abroad = new ProviderSnapshot(ProviderId.QwenWork);
        QwenWorkUsage.ReadTokens(abroad, new Desktop { Rows = [["msg_1", "ses_1", "1794991536751", "qwen3.8-flash", "735", "38912", "0", "95", "97", "0"]] },
            home, roaming, International, start, now);
        check("The international edition reads the same records under its own keys",
            abroad.Entries.Single() is { Key: "qwenwork:msg_1", Model: "qwen3.8-flash", Input: 735, CacheRead: 38912, Output: 192 } && (Loc.IsEnglish || abroad.UsageNote.StartsWith("本机 QwenWork ")));

        var unreadable = new ProviderSnapshot(ProviderId.QwenWorkCn);
        QwenWorkUsage.ReadTokens(unreadable, new Desktop { Rows = null }, home, roaming, China, start, now);
        check("A database that cannot be read marks the statistics incomplete", unreadable.UsageAvailable && !unreadable.UsageComplete);
        var missing = new ProviderSnapshot(ProviderId.QwenWorkCn);
        QwenWorkUsage.ReadTokens(missing, new Desktop(), Path.Combine(root, "none"), roaming, China, start, now);
        check("Without the app's records the statistics say so", !missing.UsageAvailable && missing.UsageNote.Length > 0);
    }

    private static async Task Service(string root, Action<string, bool> check)
    {
        var now = DateTimeOffset.Parse("2026-11-18T09:00:00Z");
        var roaming = Path.Combine(root, "qwenwork-service");
        Directory.CreateDirectory(QwenWorkUsage.DataFolder(roaming, China));
        var requests = new List<string>();
        string? authorization = null;
        bool rejected = false;
        using var http = new HttpClient(new Site(request =>
        {
            requests.Add(request.RequestUri!.Host + request.RequestUri.PathAndQuery);
            authorization = request.Headers.Authorization?.ToString();
            if (rejected) return (HttpStatusCode.Unauthorized, """{"code":"unauthorized"}""");
            return (request.RequestUri.Host, request.RequestUri.AbsolutePath) switch
            {
                ("gateway.qwenwork.cn", "/api/v1/adapter/user/account-context") => (HttpStatusCode.OK, PaidContext),
                ("gateway.qwenwork.cn", "/api/v1/adapter/user/wallets") => (HttpStatusCode.OK, PaidWallets),
                ("gateway.qwenwork.ai", "/api/v1/adapter/user/account-context") => (HttpStatusCode.OK, InternationalContext),
                _ => (HttpStatusCode.NotFound, "{}")
            };
        }));
        var desktop = new Desktop();
        using var service = new UsageService(desktop, new DataLocations("", "", "", roaming, "") { QwenWorkCnHome = Path.Combine(root, "none"), QwenWorkHome = Path.Combine(root, "none") }, http, () => now);
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.QwenWorkCn }] };
        settings.Normalize();

        var none = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.QwenWorkCn);
        check("Without the app's sign-in nothing is sent and the row asks for one",
            none.StatusLabel == Loc.T("未连接", "Not connected") && requests.Count == 0 && (Loc.IsEnglish || none.Status == "请先在千问办公中登录"));

        desktop.SignIns[QwenWorkUsage.DataFolder(roaming, China)] = SignIn;
        var snapshot = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.QwenWorkCn);
        check("The credits are read with the app's sign-in from its account API",
            snapshot.LiveQuota && snapshot.Plan == "个人基础版" && snapshot.Metrics.Select(m => m.Id).SequenceEqual(["credits", "monthly", "longterm"]) &&
            requests.SequenceEqual(["gateway.qwenwork.cn" + QwenWorkUsage.ContextPath, "gateway.qwenwork.cn" + QwenWorkUsage.WalletsPath]) && authorization == "Bearer token-1");

        requests.Clear();
        desktop.SignIns[QwenWorkUsage.DataFolder(roaming, International)] = InternationalSignIn;
        var both = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.QwenWork }] };
        both.Normalize();
        var abroad = (await service.RefreshAsync(both)).Single(s => s.Id == ProviderId.QwenWork);
        check("The international edition is read with its own sign-in from its own gateway, its missing wallets left out",
            abroad.LiveQuota && abroad.Plan == "Free" && abroad.Metrics.Single() is { Id: "credits", Amount: 685.8297m } && authorization == "Bearer token-2" &&
            requests.SequenceEqual(["gateway.qwenwork.ai" + QwenWorkUsage.ContextPath, "gateway.qwenwork.ai" + QwenWorkUsage.WalletsPath]));

        requests.Clear();
        desktop.SignIns[QwenWorkUsage.DataFolder(roaming, China)] = SignIn.Replace("2026-11-25T07:24:48Z", "2026-11-18T08:00:00Z");
        var expired = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.QwenWorkCn);
        check("An expired sign-in asks to open the app without a request", expired.StatusLabel == Loc.T("登录已失效", "Sign-in expired") && requests.Count == 0);

        desktop.SignIns[QwenWorkUsage.DataFolder(roaming, China)] = SignIn;
        rejected = true;
        var refused = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.QwenWorkCn);
        check("A refused sign-in is reported as expired, with the last reading kept and marked as old", refused.StatusLabel == Loc.T("登录已失效", "Sign-in expired")
            && refused.IsStale && refused.Metrics.Select(m => m.Id).SequenceEqual(["credits", "monthly", "longterm"]) && refused.Status.Contains(Loc.T("读取的额度", "showing the quota read at")));
    }

    private sealed class Site(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Desktop : IDesktopSources
    {
        public IReadOnlyList<string?[]>? Rows { get; init; } = [];
        public List<string> Queries { get; } = [];
        public Dictionary<string, string> SignIns { get; } = [];
        public string? ReadCursorToken(string database) => throw new InvalidOperationException("Unexpected credential read");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => throw new InvalidOperationException("Unexpected service discovery");
        public IReadOnlyList<string?[]>? QueryDatabase(string database, string sql) { Queries.Add(sql); return Rows; }
        public string? ReadQwenWorkSignIn(string folder) => SignIns.GetValueOrDefault(folder);
    }
}
