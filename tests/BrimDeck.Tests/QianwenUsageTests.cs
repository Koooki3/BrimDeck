using System.Net;
using System.Text.Json;
using System.Web;
using BrimDeck.Core;

static class QianwenUsageTests
{
    private static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }

    public static async Task Run(string root, Action<string, bool> check)
    {
        CheckCatalog(check);
        CheckParsing(check);
        await CheckChinaAsync(check);
        await CheckInternationalAsync(check);
    }

    private static void CheckCatalog(Action<string, bool> check)
    {
        var menu = ProviderCatalog.Sources.ToList();
        var manual = ProviderCatalog.Sources.Where(id => !ProviderCatalog.IsBuiltIn(id)).ToList();
        check("Qianwen and QwenCloud sign in from settings and are listed after Xiaomi MiMo Token Plan, with the sources entered by hand",
            menu.IndexOf(ProviderId.Qianwen) == menu.IndexOf(ProviderId.MiMoTokenPlan) + 1 && menu.IndexOf(ProviderId.QwenCloud) == menu.IndexOf(ProviderId.Qianwen) + 1 &&
            menu.IndexOf(ProviderId.DeepSeek) == menu.IndexOf(ProviderId.QwenCloud) + 1 && menu.Count(id => id == ProviderId.Qianwen) == 1 &&
            ProviderCatalog.IsBuiltIn(ProviderId.Qianwen) && ProviderCatalog.IsBuiltIn(ProviderId.QwenCloud) && !manual.Contains(ProviderId.Qianwen) &&
            ProviderCatalog.Group(ProviderId.Qianwen) == ProviderCatalog.Group(ProviderId.DeepSeek) && ProviderCatalog.Group(ProviderId.QwenCloud) == ProviderCatalog.Group(ProviderId.DeepSeek));
        check("Qianwen and QwenCloud are quota sources only, saved as qianwen and qwencloud, in the color of sources entered by hand",
            !ProviderCatalog.IsUsageSource(ProviderId.Qianwen) && !ProviderCatalog.IsUsageSource(ProviderId.QwenCloud) &&
            ProviderCatalog.Key(ProviderId.Qianwen) == "qianwen" && ProviderCatalog.Key(ProviderId.QwenCloud) == "qwencloud" &&
            (int)ProviderId.Qianwen == 33 && (int)ProviderId.QwenCloud == 34 && ProviderCatalog.Name(ProviderId.QwenCloud) == "QwenCloud" &&
            PanelThemes.AppKey(ProviderId.Qianwen) == "manual" && AppPresets.ThemeColor(ProviderId.QwenCloud) == AppPresets.ManualColor &&
            ProviderCatalog.QuotaDescription(ProviderId.Qianwen).Length > 0 && ProviderCatalog.QuotaDescription(ProviderId.QwenCloud).Length > 0 &&
            JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.QwenCloud }] }))!.Apps.Single().QuotaSource == ProviderId.QwenCloud);
        check("Each edition signs in and reads at its own hosts and region, with a sign-in kept apart from the other's",
            QianwenUsage.For(ProviderId.Qianwen) == QianwenUsage.China && QianwenUsage.For(ProviderId.QwenCloud) == QianwenUsage.International &&
            QianwenUsage.China is { Region: "cn-beijing", Currency: "CNY", Auth: "https://t.qianwenai.com", Api: "https://cli.qianwenai.com" } &&
            QianwenUsage.International is { Region: "ap-southeast-1", Currency: "USD", Auth: "https://t.qwencloud.com", Api: "https://cli.qwencloud.com" } &&
            QianwenUsage.China.SessionKey != QianwenUsage.International.SessionKey);
        check("Only the edition's own account page is opened for a sign-in",
            QianwenUsage.LoginUrlMatches(QianwenUsage.China, "https://account.qianwenai.com/sso/OIDCAuth?app_type=qianwenai_cli&auth_token=x") &&
            !QianwenUsage.LoginUrlMatches(QianwenUsage.China, "http://account.qianwenai.com/sso") && !QianwenUsage.LoginUrlMatches(QianwenUsage.China, "https://example.com/sso") &&
            !QianwenUsage.LoginUrlMatches(QianwenUsage.China, "https://account.qwencloud.com/sso") && QianwenUsage.LoginUrlMatches(QianwenUsage.International, "https://account.qwencloud.com/sso"));
    }

    private static void CheckParsing(Action<string, bool> check)
    {
        var reset = DateTimeOffset.FromUnixTimeMilliseconds(1796832000000);
        var cycle = QianwenUsage.Cycle(Json("""{"per1MonthPercentage":0.1874060507,"per1MonthResetTime":1796832000000}"""));
        check("The personal usage is the used share of the month, with its reset", cycle.Label == "每月额度" && Math.Abs(cycle.Ratio - 0.1874060507) < 1e-9 && cycle.Reset == reset);
        var weekly = QianwenUsage.Cycle(Json("""{"per1WeekPercentage":0.5,"per1WeekResetTime":1796832000000}"""));
        check("A weekly cycle is read when there is no monthly one; no usage yet reads as nothing used",
            weekly.Label == "每周额度" && weekly.Ratio == 0.5 && QianwenUsage.Cycle(default) is { Ratio: 0, Reset: null });
        bool rejected; try { QianwenUsage.Cycle(Json("""{"per1MonthPercentage":1.5}""")); rejected = false; } catch (InvalidDataException) { rejected = true; }
        check("A used share above 1 is refused", rejected);

        var solo = QianwenUsage.Solo(Json("""{"status":"VALID","specCode":"lite","endTime":1799424000000}"""),
            Json("""{"lite":{"monthly":20000},"pro":{"monthly":200000}}"""), Json("""{"per1MonthPercentage":0.25,"per1MonthResetTime":1796832000000}"""));
        check("The personal edition shows its plan and its credits used against the month's total",
            solo is { Name: "Lite", Metric: { Kind: MetricKind.Count, Label: "个人版 · 每月额度", Total: 20000, Used: 5000, Unit: "积分" } } &&
            solo.Metric.ResetAt == reset && solo.Metric.ExpiresAt == DateTimeOffset.FromUnixTimeMilliseconds(1799424000000));
        var unknown = QianwenUsage.Solo(Json("""{"status":"VALID","specCode":"essential"}"""), default, Json("""{"per1MonthPercentage":0.4}"""));
        check("Without the plan's credits the personal edition shows the used share", unknown is { Name: "Essential", Metric: { Kind: MetricKind.Percent, Percent: 40 } });
        check("A personal edition that is not in force is not shown", QianwenUsage.Solo(Json("""{"status":"EXPIRED","specCode":"lite"}"""), default, default) is null);

        var seats = QianwenUsage.Seats(Json("""
            {"Data":{"PlanName":"Token Plan 团队版","PeriodEnd":1799424000000,"SubscriptionGroupList":[
              {"SpecType":"standard","SubscriptionTotalNumber":2,"EquityList":[{"TotalValue":"50000","SurplusValue":"30000.5"}],"NextCycleFlushTime":1796832000000},
              {"SpecType":"pro","SubscriptionTotalNumber":1,"TotalValue":"100000","SurplusValue":"100000"}]}}
            """));
        check("The team edition adds up its seats, names their tiers and shows when their credits renew",
            seats is { Name: "团队版 标准席位、高级席位", Metric: { Kind: MetricKind.Count, Label: "团队版 · 每月额度", Total: 150000, Used: 19999.5m } } &&
            seats.Metric.ResetAt == reset && seats.Metric.ExpiresAt == DateTimeOffset.FromUnixTimeMilliseconds(1799424000000));
        var ending = QianwenUsage.Seats(Json("""{"PeriodEnd":1796000000000,"SubscriptionGroupList":[{"SpecType":"max","TotalValue":"250000","SurplusValue":"0","NextCycleFlushTime":1796832000000}]}"""));
        check("A renewal after the team plan ends is not shown as a reset", ending is { Name: "团队版 尊享席位", Metric: { ResetAt: null, Used: 250000 } });
        check("No seats means no team edition", QianwenUsage.Seats(Json("""{"Data":{"SubscriptionGroupList":[]}}""")) is null);

        var legacy = QianwenUsage.Instances(Json("""
            {"Data":[{"Status":"expired","TemplateName":"Old","InitCapacityBaseValue":"10","CurrCapacityBaseValue":"0"},
              {"Status":{"Code":"valid"},"TemplateName":"Token Plan 个人版 Lite","InitCapacityBaseValue":"20000","CurrCapacityBaseValue":"15000","EndTime":1799424000000}]}
            """), "个人版");
        check("A plan bought before seats is the first valid one, with what is left of its credits",
            legacy is { Name: "Token Plan 个人版 Lite", Metric: { Label: "个人版 · 套餐额度", Total: 20000, Used: 5000 } });
        var monthly = QianwenUsage.Instances(Json("""{"Data":[{"Status":"valid","CommodityName":"Team","CapacityTypeCode":"periodMonthlyShift","InitCapacityBaseValue":100,"CurrCapacityBaseValue":90,"periodCapacityBaseValue":40}]}"""), "团队版");
        check("A monthly plan counts what is left of the month", monthly is { Name: "Team", Metric: { Id: "team", Used: 60 } });
        var addon = QianwenUsage.AddonPacks(Json("""
            {"Data":[{"Status":"valid","InitCapacityBaseValue":"625000","CurrCapacityBaseValue":"600000","EndTime":1799424000000},
              {"Status":"valid","InitCapacityBaseValue":"625000","CurrCapacityBaseValue":"625000","EndTime":1798000000000},{"Status":"invalid","InitCapacityBaseValue":"9","CurrCapacityBaseValue":"9"}]}
            """));
        check("The add-on packs in force are shown together, with the earliest expiry",
            addon is { Label: "团队版 · 加油包", Total: 1250000, Used: 25000 } && addon.ExpiresAt == DateTimeOffset.FromUnixTimeMilliseconds(1798000000000) &&
            QianwenUsage.AddonPacks(Json("""{"Data":[]}""")) is null);

        check("The balance is the available amount in its currency",
            QianwenUsage.Balance(Json("""{"AvailableAmount":"12.34","Currency":"cny"}"""), "CNY") is { Kind: MetricKind.Balance, Label: "账户余额", Amount: 12.34m, Currency: "CNY" } &&
            ProviderCatalog.IsAccountBalance(QianwenUsage.Balance(Json("""{"AvailableAmount":"-1"}"""), "CNY")));
        check("Qianwen rows can set a target balance; QwenCloud, which has no balance, cannot",
            ProviderCatalog.HasAccountBalance(ProviderId.Qianwen) && !ProviderCatalog.HasAccountBalance(ProviderId.QwenCloud));
        check("The China month's charges add up the charges by model",
            QianwenUsage.CostAnalysisTotal(Json("""{"GroupByTotal":[{"Key":"qwen","Amount":"1.5"},{"Key":"glm","Amount":2.25}],"CostTotals":{"Amount":"9"}}""")) == 3.75m &&
            QianwenUsage.CostAnalysisTotal(Json("""{"CostTotals":{"Amount":"9"}}""")) == 9m);
        check("QwenCloud's month leaves out rounding, refunds, credit adjustments and free usage",
            QianwenUsage.ConsumeTotal(Json("""
                {"Data":[{"LineItemCategory":"Usage","RequireAmount":"1.25"},{"LineItemCategory":"Refund","RequireAmount":"-5"},
                  {"LineItemCategory":"Free Tier","Amount":"3"},{"LineItemCategory":"Usage","Amount":0.75}]}
                """)) == 2m);
        var now = new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero);
        var charge = QianwenUsage.MonthCharge(2m, "USD", now);
        check("The month's charges reset when the next UTC month starts", charge is { Kind: MetricKind.Spend, Label: "本月扣费", Currency: "USD" } &&
            charge.ResetAt == new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero) &&
            QianwenUsage.CostAnalysisQuery(now)["TimePeriod"] == """{"Start":"202611","End":"202611"}""" &&
            QianwenUsage.ConsumeQuery(now, 2) is var query && query["StartBillingDate"] == "2026-11-01" && query["EndBillingDate"] == "2026-11-18" && query["CurrentPage"] == "2");

        var session = QianwenUsage.ParseCredentials(Json("""{"AccessToken":"at","ExpireTime":"2026-11-25T10:00:00Z","User":{"Id":1357924680123456,"Email":"a@b.c","AliyunId":"x"}}"""));
        check("The sign-in keeps its token, expiry and account, and expires at its time",
            session is { AccessToken: "at", Account: "a@b.c" } && session.ExpiresAt == new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero) &&
            !session.Expired(now) && session.Expired(now.AddDays(8)) && QianwenUsage.Session.Parse(session.Serialize()) == session &&
            QianwenUsage.Session.Parse("") is null && QianwenUsage.Session.Parse("{}") is null && QianwenUsage.ParseCredentials(Json("{}")) is null);
    }

    // The China edition end to end: the sign-in, then every part of the account.
    private static async Task CheckChinaAsync(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero);
        var calls = new List<string>();
        int polls = 0, consoleTokens = 0; bool consoleRefused = true; string? csAuthorization = null, region = null, challenge = null;
        bool signInRefused = false;
        // The kind of account the server answers for: "full" has the newer personal edition and team seats; "new" was just opened and
        // has nothing; "older" bought its personal and team plans before seats, and seats cannot be read; "both" has both personal editions.
        string account = "full";
        var site = new Site(request =>
        {
            var url = request.RequestUri!;
            var query = HttpUtility.ParseQueryString(url.Query);
            if (url.AbsolutePath == "/cli/device/code")
            {
                challenge = query["code_challenge"];
                return (HttpStatusCode.OK, """{"Success":true,"Data":{"Token":"device","VerificationUrl":"https://account.qianwenai.com/sso/OIDCAuth?auth_token=device","ExpiresIn":300,"Interval":1}}""");
            }
            if (url.AbsolutePath == "/cli/device/token")
                return ++polls < 2 ? (HttpStatusCode.OK, """{"Success":true,"Data":{"Status":"authorization_pending"}}""")
                    : (HttpStatusCode.OK, """{"Success":true,"Data":{"Status":"complete","Credentials":{"AccessToken":"access","ExpireTime":"2026-11-25T10:00:00Z","User":{"Id":1,"Email":"me@example.com"}}}}""");
            var body = request.Content!.ReadAsStringAsync().Result;
            if (url.Host == "cs-data.qianwenai.com")
            {
                csAuthorization = request.Headers.Authorization?.ToString();
                var api = query["api"]!;
                calls.Add("cs:" + api[(api.LastIndexOf('/') + 1)..]);
                if (consoleRefused) { consoleRefused = false; return (HttpStatusCode.OK, """{"code":"200","successResponse":true,"data":{"success":false,"errorCode":"BailianGateway.Login.NotLogined"}}"""); }
                string Wrap(string data) => """{"code":"200","successResponse":true,"data":{"success":true,"DataV2":{"ret":["SUCCESS::ok"],"data":{"code":"SUCCESS","success":true,"data":""" + data + "}}}}";
                // Without a subscription, cs-data leaves data out of its answer.
                if (account is "new" or "older" && api.EndsWith("subscription"))
                    return (HttpStatusCode.OK, """{"code":"200","successResponse":true,"data":{"success":true,"DataV2":{"ret":["SUCCESS::ok"],"data":{"code":"SUCCESS","success":true}}}}""");
                return (HttpStatusCode.OK, Wrap(api.EndsWith("subscription") ? """{"status":"VALID","specCode":"lite","endTime":1799424000000}"""
                    : api.EndsWith("quota-config") ? """{"lite":{"monthly":20000}}""" : """{"per1MonthPercentage":0.1,"per1MonthResetTime":1796832000000}"""));
            }
            using var doc = JsonDocument.Parse(body);
            var action = doc.RootElement.Get("action").Text();
            region = doc.RootElement.Get("region").Text();
            calls.Add(action);
            if (signInRefused) return (HttpStatusCode.OK, """{"code":"ConsoleNeedLogin","message":"You need to log in.","successResponse":false}""");
            if (request.Headers.Authorization?.ToString() != "Bearer access") return (HttpStatusCode.Unauthorized, "{}");
            string Ok(string data) => $$"""{"code":"200","successResponse":true,"data":{{data}}}""";
            var parameters = doc.RootElement.Get("params");
            return action switch
            {
                "GenerateCLIAccessToken" => (HttpStatusCode.OK, Ok($$"""{"cliAccessToken":"console{{++consoleTokens}}"}""")),
                "GetSeatSubscriptionSummary" when account == "older" => (HttpStatusCode.OK, """{"code":"InternalError","successResponse":false}"""),
                "GetSeatSubscriptionSummary" when account == "new" => (HttpStatusCode.OK, Ok("""{"Data":{"SubscriptionGroupList":[]}}""")),
                "DescribeFrInstances" when account is "older" or "both" && parameters.Get("CommodityCode").Text() == "sfm_tokenplanpersonal_dp_cn" =>
                    (HttpStatusCode.OK, Ok("""{"Data":[{"Status":"valid","TemplateName":"个人版 Pro","InitCapacityBaseValue":"200000","CurrCapacityBaseValue":"180000"}]}""")),
                "DescribeFrInstances" when account == "older" && parameters.Get("CommodityCode").Text() == "sfm_tokenplanteams_dp_cn" =>
                    (HttpStatusCode.OK, Ok("""{"Data":[{"Status":"valid","TemplateName":"团队版","InitCapacityBaseValue":"100000","CurrCapacityBaseValue":"40000"}]}""")),
                "GetSeatSubscriptionSummary" => (HttpStatusCode.OK, Ok("""{"Data":{"SubscriptionGroupList":[{"SpecType":"standard","TotalValue":"25000","SurplusValue":"20000","NextCycleFlushTime":1796832000000}]}}""")),
                "DescribeFrInstances" when parameters.Get("CommodityCode").Text().Contains("addon") => (HttpStatusCode.OK, Ok("""{"Data":[]}""")),
                "DescribeFrInstances" => (HttpStatusCode.OK, Ok("""{"Data":[]}""")),
                "GetFundAccountAvailableAmount" => (HttpStatusCode.OK, Ok("""{"AvailableAmount":"88.80","Currency":"CNY"}""")),
                "MaasDescribeCostAnalysis" => (HttpStatusCode.OK, Ok("""{"GroupByTotal":[{"Amount":"1.20"}]}""")),
                _ => (HttpStatusCode.OK, """{"code":"NotFound"}""")
            };
        });
        using var http = new HttpClient(site);
        var secrets = new Secrets();
        using var service = new UsageService(new Desktop(), new DataLocations("", "", "", "", ""), http, () => now, secrets);
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Qianwen }] };
        settings.Normalize();
        var before = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Qianwen);
        check("Qianwen without a sign-in asks for one and sends nothing", before is { LiveQuota: false } && before.StatusLabel == "未连接" && calls.Count == 0 && !service.QianwenConnected(ProviderId.Qianwen));

        string? opened = null;
        bool done = await service.ConnectQianwenAsync(ProviderId.Qianwen, url => { opened = url; return true; }, CancellationToken.None);
        check("Signing in opens the account page, waits for the approval and saves the sign-in",
            done && opened == "https://account.qianwenai.com/sso/OIDCAuth?auth_token=device" && polls == 2 && challenge is { Length: 43 } &&
            service.QianwenConnected(ProviderId.Qianwen) && !service.QianwenConnected(ProviderId.QwenCloud));

        var snapshot = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Qianwen);
        check("Qianwen shows the personal plan first, then the team seats, the balance and the month's charges",
            snapshot.LiveQuota && snapshot.Metrics.Select(m => m.Label).SequenceEqual(["个人版 · 每月额度", "团队版 · 每月额度", "账户余额", "本月扣费"]) &&
            snapshot.Metrics[0] is { Total: 20000, Used: 2000 } && snapshot.Metrics[1] is { Total: 25000, Used: 5000 } &&
            snapshot.Metrics[2] is { Amount: 88.80m, Currency: "CNY" } && snapshot.Metrics[3] is { Amount: 1.20m, Currency: "CNY" } &&
            snapshot.Plan == "Lite · 团队版 标准席位" && !snapshot.Status.Contains("无法读取"));
        check("Qianwen asks in the China region, and cs-data takes a console token issued again once it is refused",
            region == "cn-beijing" && consoleTokens == 2 && csAuthorization == "Bearer console2" && calls.Count(c => c == "cs:subscription") == 2 && !calls.Contains("MaasListConsumeSummary"));

        account = "new";
        var fresh = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Qianwen);
        check("A newly opened account with no Token Plan shows its balance and charges, with no warning",
            fresh.LiveQuota && fresh.Metrics.Select(m => m.Label).SequenceEqual(["账户余额", "本月扣费"]) && fresh.Plan == "" && fresh.StatusLabel == "已连接" && fresh.Status == "已连接");
        account = "older";
        var older = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Qianwen);
        check("Plans bought before seats are read when there is no newer edition and seats cannot be read",
            older.Metrics.Select(m => m.Label).SequenceEqual(["个人版 · 套餐额度", "团队版 · 套餐额度", "账户余额", "本月扣费"]) &&
            older.Metrics[0] is { Total: 200000, Used: 20000 } && older.Metrics[1] is { Total: 100000, Used: 60000 } && older.Plan == "个人版 Pro · 团队版" && older.Status == "已连接");
        account = "both";
        var both = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Qianwen);
        check("An account with both personal editions shows both, the newer first",
            both.Metrics.Select(m => m.Label).Take(3).SequenceEqual(["个人版 · 每月额度", "个人版 · 套餐额度", "团队版 · 每月额度"]) && both.Metrics[0].Id != both.Metrics[1].Id &&
            both.Plan == "Lite · 个人版 Pro · 团队版 标准席位");
        account = "full";

        signInRefused = true; calls.Clear();
        var expired = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Qianwen);
        check("A sign-in the account no longer accepts asks to sign in again", expired.StatusLabel == "登录已失效" && expired.Status.Contains("重新登录") && calls.Count == 1);
        service.DisconnectQianwen(ProviderId.Qianwen);
        check("Signing out removes the sign-in", !service.QianwenConnected(ProviderId.Qianwen));
    }

    // QwenCloud: no balance and no cs-data; the month's charges come from the daily consumption, page by page.
    private static async Task CheckInternationalAsync(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero);
        var actions = new List<string>(); var hosts = new HashSet<string>(); string? region = null;
        bool seatAccount = false, seatsFail = false;
        var site = new Site(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            var action = doc.RootElement.Get("action").Text();
            region = doc.RootElement.Get("region").Text();
            actions.Add(action);
            string Ok(string data) => $$"""{"code":"200","successResponse":true,"data":{{data}}}""";
            var page = doc.RootElement.Get("params").Get("CurrentPage").Text();
            return action switch
            {
                "QuerySubscriptionGray" => (HttpStatusCode.OK, Ok(seatAccount ? """{"IsGray":true}""" : """{"IsGray":false}""")),
                "GetSeatSubscriptionSummary" when seatsFail => (HttpStatusCode.OK, """{"code":"InternalError","successResponse":false}"""),
                "GetSeatSubscriptionSummary" => (HttpStatusCode.OK, Ok("""{"Data":{"SubscriptionGroupList":[{"SpecType":"standard","TotalValue":"25000","SurplusValue":"25000"}]}}""")),
                "DescribeFrInstances" when doc.RootElement.Get("params").Get("CommodityCode").Text() == "sfm_tokenplanpersonal_dp_intl" =>
                    (HttpStatusCode.OK, Ok("""{"Data":[{"Status":"valid","TemplateName":"Token Plan Personal Pro","InitCapacityBaseValue":"200000","CurrCapacityBaseValue":"150000"}]}""")),
                "DescribeFrInstances" => (HttpStatusCode.OK, Ok("""{"Data":[]}""")),
                "MaasListConsumeSummary" => (HttpStatusCode.OK, Ok(page == "1"
                    ? "{\"Data\":[" + string.Join(",", Enumerable.Repeat("""{"LineItemCategory":"Usage","RequireAmount":"0.01"}""", 100)) + "]}"
                    : """{"Data":[{"LineItemCategory":"Usage","RequireAmount":"0.5"}]}""")),
                _ => (HttpStatusCode.OK, """{"code":"NotFound"}""")
            };
        });
        using var http = new HttpClient(site);
        var secrets = new Secrets();
        secrets.Write(QianwenUsage.International.SessionKey, new QianwenUsage.Session("access", null, "").Serialize());
        using var service = new UsageService(new Desktop(), new DataLocations("", "", "", "", ""), http, () => now, secrets);
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.QwenCloud }] };
        settings.Normalize();
        var snapshot = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.QwenCloud);
        check("QwenCloud shows its personal plan and the month's charges in dollars, with no balance",
            snapshot.LiveQuota && snapshot.Metrics.Select(m => m.Label).SequenceEqual(["个人版 · 套餐额度", "本月扣费"]) && snapshot.Plan == "Token Plan Personal Pro" &&
            snapshot.Metrics[0] is { Total: 200000, Used: 50000 } && snapshot.Metrics[1] is { Amount: 1.5m, Currency: "USD" });
        check("QwenCloud is read only at its own host, in its region, and never asks for a balance or cs-data",
            hosts.SetEquals(["cli.qwencloud.com"]) && region == "ap-southeast-1" && !actions.Contains("GetFundAccountAvailableAmount") && !actions.Contains("GenerateCLIAccessToken") &&
            actions.Count(a => a == "MaasListConsumeSummary") == 2);
        check("QwenCloud asks for seats only for accounts it has moved to them", !actions.Contains("GetSeatSubscriptionSummary") && snapshot.Status == "已连接");
        seatAccount = true;
        var seats = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.QwenCloud);
        check("A QwenCloud seat account shows its seats; seats that cannot be read are named while the rest is shown",
            seats.Metrics.Any(m => m.Label == "团队版 · 每月额度" && m.Total == 25000) && seats.Plan == "Token Plan Personal Pro · 团队版 标准席位");
        seatsFail = true;
        var failing = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.QwenCloud);
        check("Seats that cannot be read, with no older team plan, are named while the rest is shown",
            failing.LiveQuota && failing.Status.Contains("团队版 Token Plan") && failing.Metrics.Any(m => m.Label == "本月扣费"));
        seatAccount = seatsFail = false;

        secrets.Write(QianwenUsage.International.SessionKey, new QianwenUsage.Session("access", now.AddMinutes(-1), "").Serialize());
        actions.Clear();
        var expired = (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.QwenCloud);
        check("An expired sign-in asks to sign in again without a request", expired.StatusLabel == "登录已失效" && actions.Count == 0);
    }

    private sealed class Site(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Secrets : IProviderSecrets
    {
        private readonly Dictionary<Guid, string> _values = [];
        public string Read(Guid instanceId) => _values.TryGetValue(instanceId, out var value) ? value : "";
        public void Write(Guid instanceId, string secret) => _values[instanceId] = secret;
        public void Delete(Guid instanceId) => _values.Remove(instanceId);
    }

    private sealed class Desktop : IDesktopSources
    {
        public string? ReadCursorToken(string database) => throw new InvalidOperationException("Unexpected credential read");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => throw new InvalidOperationException("Unexpected service discovery");
    }
}
