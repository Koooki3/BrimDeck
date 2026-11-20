using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

// Qoder and Qoder CN are read from the first source that answers: Qoder's API with the desktop app's sign-in, the CLI's daemon,
// the IDE's language server. The fixtures follow what Qoder CLI 1.1.91, Qoder IDE 1.106.3 and the API (asked as the Qoder 0.5.14
// desktop app asks it) answered on 2026-11-18 and 2026-11-19, a personal and a Teams account, with the account details replaced.
static class QoderUsageTests
{
    private const string TeamsUsage = """
        {"displayMode":"qoder","qoderUsage":{"userId":"user-2","userType":"teams","usageType":"credits","totalUsagePercentage":0.8,"isQuotaExceeded":false,
         "expiresAt":1795332718000,"refreshAt":1795332718000,"upgradeUrl":"https://qoder.com/pricing?client=qoder",
         "userQuota":{"total":3000,"used":2409,"remaining":591,"percentage":0.8,"unit":"credits"},
         "orgResourcePackage":{"used":0,"remaining":8054,"percentage":0,"unit":"credits","cap":-1,"available":true},"isPlanQuotaProrated":false}}
        """;
    private const string TeamsPlan = """
        {"user_type":"teams","plan_tier_name":"Teams","is_personal_version":false,"is_paid_plan":true,"is_highest_tier":false,
         "organization":{"org_id":"org-1","org_name":"Team","role_name":"Member","is_suspended":false},"start_date":1792654318000,"end_date":1795332718000}
        """;
    private const string DaemonUsage = """
        {"userId":"user-1","userType":"personal_professional_trial","totalUsagePercentage":3,"isHighestTier":false,"expiresAt":1796180759888,
         "isQuotaExceeded":false,"usageType":"credits","userQuota":{"total":300,"used":30,"remaining":270,"percentage":10,"unit":"credits"},
         "isPlanQuotaProrated":false,"addOnQuota":{"total":100,"used":0,"remaining":100,"percentage":0,"unit":"credits"},
         "dedicatedResourcePackages":[
          {"id":"pkg-sota","name":"sota","total":2000,"used":16,"remaining":1984,"percentage":1,"unit":"credits","expiresAt":1795230424931,"available":true,
           "status":"QUOTA_DETAIL_STATUS_ACTIVE","displayLabels":[{"dimension":"description","value":"sota model series description","valueI18n":{"zh-CN":"SOTA 专属积分：……"}},
           {"dimension":"title","value":"sota model series","valueI18n":{"en-US":"SOTA Exclusive Credits","zh-CN":"SOTA 专属积分"}}]},
          {"id":"pkg-ultimate","name":"ultimate","total":500,"used":27,"remaining":473,"percentage":5,"unit":"credits","expiresAt":1795230426343,"available":true,
           "displayLabels":[{"dimension":"title","valueI18n":{"en-US":"Ultimate Exclusive Credits","zh-CN":"极致专属积分"}}]},
          {"id":"pkg-gone","name":"gone","total":100,"used":0,"remaining":100,"available":false}]}
        """;
    private static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }

    public static async Task Run(string root, Action<string, bool> check)
    {
        Catalog(check);
        Parse(check);
        Files(root, check);
        Tokens(root, check);
        await Daemon(check);
        await Service(root, check);
    }

    private static void Catalog(Action<string, bool> check)
    {
        check("Qoder and Qoder CN are automatic quota and statistics sources saved as qoder and qoder-cn, sharing the qoder color",
            ProviderCatalog.IsBuiltIn(ProviderId.Qoder) && ProviderCatalog.IsBuiltIn(ProviderId.QoderCn) && ProviderCatalog.IsUsageSource(ProviderId.Qoder) && ProviderCatalog.IsUsageSource(ProviderId.QoderCn) &&
            ProviderCatalog.Key(ProviderId.Qoder) == "qoder" && ProviderCatalog.Key(ProviderId.QoderCn) == "qoder-cn" && (int)ProviderId.Qoder == 31 && (int)ProviderId.QoderCn == 32 &&
            ProviderCatalog.Name(ProviderId.Qoder) == "Qoder" && ProviderCatalog.Name(ProviderId.QoderCn) == "Qoder CN" && !ProviderCatalog.BuiltIns.Contains(ProviderId.Qoder) &&
            PanelThemes.AppKey(ProviderId.QoderCn) == "qoder" && PanelThemes.AppKeys.Contains("qoder") &&
            JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.QoderCn }] }))!.Apps.Single().QuotaSource == ProviderId.QoderCn);
    }

    private static void Parse(Action<string, bool> check)
    {
        var reading = QoderUsage.Parse(Json(DaemonUsage), "Pro Trial");
        check("Qoder shows the plan's credits, add-on credits and each package kept for a model, leaving out packages no longer available",
            reading.Metrics.Select(m => m.Id).SequenceEqual(["plan", "addon", "package:pkg-sota", "package:pkg-ultimate"]) && reading.Plan == "Pro Trial" && !reading.Exceeded);
        var plan = reading.Metrics[0];
        check("The plan's credits are counted from the amounts and end with the plan",
            plan is { Kind: MetricKind.Count, Label: "套餐积分", Unit: "积分", Total: 300m, Used: 30m } && plan.UsedPercent == 10 &&
            plan.ExpiresAt == DateTimeOffset.FromUnixTimeMilliseconds(1796180759888));
        check("A package is named by the service in the display language, with its own end",
            reading.Metrics[2] is { Label: "SOTA 专属积分", Total: 2000m, Used: 16m } && reading.Metrics[2].ExpiresAt == DateTimeOffset.FromUnixTimeMilliseconds(1795230424931));
        // The IDE gives shares where the daemon gives whole percentages; neither is used.
        var ide = QoderUsage.Parse(Json("""{"userQuota":{"total":300,"used":300,"remaining":0,"percentage":1},"orgResourcePackage":{"cap":1000,"used":250,"remaining":750},"isQuotaExceeded":true}"""), "");
        check("The organization's package is read from its cap, and used-up credits are marked",
            ide.Metrics.Select(m => (m.Id, m.UsedPercent)).SequenceEqual([("plan", 100.0), ("organization", 25.0)]) && ide.Exceeded);
        check("An empty bucket is left out", QoderUsage.Parse(Json("""{"addOnQuota":{"total":0,"used":0,"remaining":0}}"""), "").Metrics.Count == 0);

        var teams = QoderUsage.ParsePresentation(Json(TeamsUsage), QoderUsage.PlanName(Json(TeamsPlan)));
        check("A Teams member's credits come from the API's qoderUsage, named by the plan's tier",
            teams is { Plan: "Teams", Exceeded: false } && teams.Metrics.Select(m => m.Id).SequenceEqual(["plan", "organization"]) &&
            teams.Metrics[0] is { Total: 3000m, Used: 2409m } && teams.Metrics[0].ExpiresAt == DateTimeOffset.FromUnixTimeMilliseconds(1795332718000));
        check("The shared package of a member without a limit counts what was used against that and what is left",
            teams!.Metrics[1] is { Label: "组织资源包", Total: 8054m, Used: 0m } &&
            QoderUsage.Parse(Json("""{"orgResourcePackage":{"used":100,"remaining":7954,"cap":-1,"available":true}}"""), "").Metrics.Single() is { Total: 8054m, Used: 100m });
        check("A personal account reads alike through the API",
            QoderUsage.ParsePresentation(Json("""{"displayMode":"qoder","qoderUsage":""" + DaemonUsage + "}"), "Pro Trial") is { Plan: "Pro Trial", Metrics.Count: 4 });
        bool Rejected(string json) { try { QoderUsage.ParsePresentation(Json(json), ""); return false; } catch (InvalidDataException) { return true; } }
        check("An enterprise layout is not read, and an answer without credits is a changed format",
            QoderUsage.ParsePresentation(Json("""{"displayMode":"enterprise","enterpriseUsage":{}}"""), "") is null &&
            Rejected("""{"displayMode":"qoder"}""") && Rejected("[]"));
        check("LSP framing is written and read", QoderUsage.Frame("{\"id\":1}") == "Content-Length: 8\r\n\r\n{\"id\":1}" &&
            QoderUsage.Unframe("Content-Length: 8\r\n\r\n{\"id\":1}") == "{\"id\":1}" && QoderUsage.Unframe("Content-Length: 0\r\n\r\n") is null);
    }

    private static void Files(string root, Action<string, bool> check)
    {
        var signIn = QoderUsage.ParseDesktopSignIn("""
            {"schemaVersion":1,"authenticationContext":"[\"https://qoder.com\",\"https://openapi.qoder.sh\",\"machine-1\",\"prod\"]",
             "token":"dt-fixture","refreshToken":"rt","expiresAt":"2026-12-18T03:06:40Z","user":{"id":"u"}}
            """, QoderUsage.Global);
        check("The desktop app's sign-in gives its token, when it expires and the API it signed in at, not the site",
            signIn is { Token: "dt-fixture", Api: "https://openapi.qoder.sh" } && signIn.ExpiresAt == new DateTimeOffset(2026, 12, 18, 3, 6, 40, TimeSpan.Zero) &&
            !signIn.Expired(new DateTimeOffset(2026, 12, 18, 3, 0, 0, TimeSpan.Zero)) && signIn.Expired(new DateTimeOffset(2026, 12, 18, 3, 6, 0, TimeSpan.Zero)) &&
            !signIn.ToString().Contains("dt-fixture"));
        check("Qoder CN's sign-in uses its own API, and a context kept as an array reads the same",
            QoderUsage.ParseDesktopSignIn("""{"authenticationContext":["https://qoder.cn","https://openapi.qoder.com.cn","m","prod"],"token":"t"}""", QoderUsage.China)?.Api == "https://openapi.qoder.com.cn");
        check("The token goes only to the edition's API over HTTPS; otherwise the edition's own API is used",
            QoderUsage.ParseDesktopSignIn("""{"authenticationContext":"[\"https://openapi.example.com\",\"http://openapi.qoder.sh\"]","token":"t"}""", QoderUsage.Global)?.Api == "https://openapi.qoder.sh" &&
            QoderUsage.ParseDesktopSignIn("""{"authenticationContext":"not json","token":"t"}""", QoderUsage.China)?.Api == "https://openapi.qoder.com.cn" &&
            QoderUsage.Api("https://openapi.qoder.com.cn", QoderUsage.Global) is null && QoderUsage.Api("https://evilqoder.sh", QoderUsage.Global) is null &&
            QoderUsage.Api("https://center.qoder.sh/", QoderUsage.Global) == "https://center.qoder.sh");
        check("A sign-in without a token is none", QoderUsage.ParseDesktopSignIn("""{"token":""}""", QoderUsage.Global) is null && QoderUsage.ParseDesktopSignIn("not json", QoderUsage.Global) is null);
        var info = Path.Combine(root, "qoder-info.json");
        File.WriteAllText(info, """{"websocketPort":56510,"pid":54440,"ipcServerPath":"\\\\.\\pipe\\qoder-854e04","isDev":false}""");
        check("The IDE's port is read from .info.json", QoderUsage.ReadIdePort(info) == 56510 && QoderUsage.ReadIdePort(Path.Combine(root, "none.json")) is null);
        check("Each edition keeps its own folders", QoderUsage.CliPath(@"C:\u\.qoder-cn", QoderUsage.China) == @"C:\u\.qoder-cn\bin\qoderclicn\qoderclicn.exe" &&
            QoderUsage.IdeInfo(@"C:\r", QoderUsage.Global) == @"C:\r\Qoder\SharedClientCache\.info.json" && QoderUsage.DesktopData(@"C:\r", QoderUsage.China) == @"C:\r\com.qodercn.app.stable");
    }

    private const string ModelList = """{"assistant":[{"name":"auto","displayName":"Auto","priceFactor":0.3},{"name":"smodel","displayName":"Sonus"},{"name":"gfmodel","displayName":"GLM-5.3-Flash"}]}""";
    private static readonly string?[][] Replies =
    [
        ["m1", "s1", "1794971436710", """{"prompt_tokens":19795,"completion_tokens":8,"cached_tokens":2304,"max_input_tokens":200000}""", """{"model_key":"auto"}"""],
        ["m2", "s1", "1794971542321", """{"prompt_tokens":19459,"completion_tokens":93,"cached_tokens":19200}""", """{"model_key":"gfmodel"}"""],
        ["m3", "s2", "1794971600000", """{"prompt_tokens":100,"completion_tokens":5,"cached_tokens":0}""", """{"model_key":"newmodel"}"""],
        ["m4", "s2", "1794971600001", "not json", """{"model_key":"auto"}"""],
    ];

    private static void Tokens(string root, Action<string, bool> check)
    {
        var names = QoderUsage.ModelNames([[ModelList], [null], ["not json"]]);
        check("The IDE's model lists name each model key", names["smodel"] == "Sonus" && names["gfmodel"] == "GLM-5.3-Flash" && names.Count == 3);
        var entries = QoderUsage.ParseTokens(Replies, names, QoderUsage.Global).ToList();
        check("An IDE reply counts its cached tokens apart from the rest of the prompt, under the name the IDE shows",
            entries.Count == 3 && entries[0] is { Key: "qoder:m1", Model: "Auto", Input: 17491, CacheRead: 2304, Output: 8, CacheWrite: 0 } &&
            entries[1] is { Model: "GLM-5.3-Flash", Input: 259, CacheRead: 19200, Output: 93 } && entries[0].Time == DateTimeOffset.FromUnixTimeMilliseconds(1794971436710));
        check("A key no list names keeps the key, and a reply that cannot be read is left out", entries[2].Model == "newmodel" && entries.All(e => e.Key != "qoder:m4"));
        check("Qoder CN's records are kept apart", QoderUsage.ParseTokens(Replies, names, QoderUsage.China).First().Key == "qodercn:m1");

        var roaming = Path.Combine(root, "qoder-tokens");
        var home = Path.Combine(root, "qoder-tokens-home", ".qoder");
        var desktop = new Desktop { Rows = Replies, Models = ModelList };
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var now = new DateTimeOffset(2026, 11, 18, 18, 0, 0, TimeSpan.FromHours(8));
        ProviderSnapshot Read(bool? switchOn)
        {
            var snapshot = new LogReader().Read(ProviderId.Qoder, home, now, start.LocalDateTime);
            QoderUsage.ReadTokens(snapshot, desktop, roaming, home, QoderUsage.Global, start, now, switchOn);
            return snapshot;
        }
        var snapshot = Read(true);
        check("Without any Qoder records there is nothing to count", !snapshot.UsageAvailable && snapshot.Entries.Count == 0);

        // A session the CLI wrote with the switch on (Qoder CLI 1.1.91): the reply names "auto", the prompt the model chosen; the
        // first line of a reply has no usage, the last one does. Lines written with the switch off carry no usage at all.
        var session = Path.Combine(home, "projects", "C--work");
        Directory.CreateDirectory(session);
        File.WriteAllLines(Path.Combine(session, "92b5de98.jsonl"), [
            """{"type":"runtime-config","sessionId":"92b5de98","model":"auto","timestamp":1794974018702}""",
            """{"type":"user","timestamp":"2026-11-18T03:53:39.600Z","message":{"role":"user","content":"hi"},"businessModelKey":"dfmodel","sessionId":"92b5de98"}""",
            """{"type":"assistant","timestamp":"2026-11-18T03:53:46.727Z","message":{"id":"m-1","model":"auto","content":[{"type":"thinking","thinking":"…"}]},"requestTokenAnchor":{"requestId":"req-1"},"sessionId":"92b5de98"}""",
            """{"type":"assistant","timestamp":"2026-11-18T03:53:46.727Z","message":{"id":"m-1","model":"auto","stop_reason":"end_turn","usage":{"input_tokens":3713,"cache_creation_input_tokens":0,"cache_read_input_tokens":2304,"output_tokens":8,"cache_creation":{"ephemeral_1h_input_tokens":0,"ephemeral_5m_input_tokens":0}}},"requestTokenAnchor":{"requestId":"req-1"},"sessionId":"92b5de98"}""",
            """{"type":"user","timestamp":"2026-11-18T03:55:00.000Z","message":{"role":"user","content":"again"},"businessModelKey":"mystery","sessionId":"92b5de98"}""",
            """{"type":"assistant","timestamp":"2026-11-18T03:55:05.000Z","message":{"id":"m-2","model":"auto","stop_reason":"end_turn","usage":{"input_tokens":100,"cache_read_input_tokens":0,"output_tokens":5}},"requestTokenAnchor":{"requestId":"req-2"},"sessionId":"92b5de98"}""",
            """{"type":"assistant","timestamp":"2026-11-18T03:56:00.000Z","message":{"id":"m-3","model":"auto","stop_reason":"end_turn"},"requestTokenAnchor":{"requestId":"req-3"},"sessionId":"92b5de98"}"""]);
        Directory.CreateDirectory(Path.Combine(home, ".auth"));
        File.WriteAllText(Path.Combine(home, ".auth", "dynamic-texts.json"),
            """{"locales":{"en":{"modelSelector.item.dfmodel":"DeepSeek-Flash","modelSelector.item.dfmodel.description":"…","modelSelector.item.smodel":"Sonus (texts)"},"zh":{"modelSelector.item.dfmodel":"深度"}}}""");
        snapshot = Read(true);
        var cli = snapshot.Entries.Where(e => e.Key.StartsWith("qoder:92b5de98:", StringComparison.Ordinal)).ToList();
        check("A CLI or desktop reply counts once, under the model its prompt chose, named from the CLI's texts",
            snapshot.UsageAvailable && cli.Count == 2 && cli[0] is { Key: "qoder:92b5de98:req-1", Model: "DeepSeek-Flash", Input: 3713, CacheRead: 2304, Output: 8 } && cli[1].Model == "mystery");
        check("Without IDE records the CLI's are still counted", snapshot.Entries.Count == 2 && snapshot.UsageNote.Contains("IDE 0"));

        var database = QoderUsage.IdeDatabase(roaming, QoderUsage.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!); File.WriteAllText(database, "");
        snapshot = Read(true);
        check("The IDE's replies since the start are added to the CLI's, the IDE's model list naming before the CLI's texts",
            snapshot is { UsageAvailable: true, UsageComplete: true } && snapshot.Entries.Count == 5 && desktop.Queries.Any(q => q.Contains("gmt_create >= " + start.ToUnixTimeMilliseconds())) &&
            snapshot.Entries.Select(e => e.Model).Order(StringComparer.Ordinal).SequenceEqual(["Auto", "DeepSeek-Flash", "GLM-5.3-Flash", "mystery", "newmodel"]) &&
            snapshot.UsageNote.Contains("IDE 3") && snapshot.UsageNote.Contains(Loc.T("CLI 与桌面版 2 次", "CLI and desktop app 2 ")));
        snapshot = Read(false);
        check("With the switch off the note says so", snapshot.UsageNote.Contains(Loc.T("未开启 Token 记录", "Token recording is off")));
        desktop.Rows = null;
        snapshot = Read(true);
        check("An IDE database that cannot be read marks the statistics incomplete and keeps the CLI's", snapshot is { UsageAvailable: true, UsageComplete: false } && snapshot.Entries.Count == 2);
        desktop.Rows = Replies;
        check("The switch is the edition's own variable, and only a true value is on",
            QoderUsage.TokenSwitch(QoderUsage.Global) == "QODER_EXPOSE_TOKEN_USAGE" && QoderUsage.TokenSwitch(QoderUsage.China) == "QODERCN_EXPOSE_TOKEN_USAGE" &&
            QoderUsage.SwitchOn(" 1 ") && QoderUsage.SwitchOn("True") && !QoderUsage.SwitchOn("0") && !QoderUsage.SwitchOn(null));
    }

    // A daemon on the far side of a pipe pair, answering as Qoder's does.
    private static async Task Daemon(Action<string, bool> check)
    {
        async Task<(QoderUsage.Reading? Reading, Exception? Error, List<JsonElement> Sent)> Talk(Func<JsonElement, string?, IEnumerable<string>> answer)
        {
            using var toDaemon = new AnonymousPipeServerStream(PipeDirection.Out);
            using var fromBrimDeck = new AnonymousPipeClientStream(PipeDirection.In, toDaemon.ClientSafePipeHandle);
            using var toBrimDeck = new AnonymousPipeServerStream(PipeDirection.Out);
            using var fromDaemon = new AnonymousPipeClientStream(PipeDirection.In, toBrimDeck.ClientSafePipeHandle);
            var input = new StreamWriter(toDaemon, new UTF8Encoding(false));
            var output = new StreamReader(fromDaemon, Encoding.UTF8);
            var sent = new List<JsonElement>();
            var daemon = Task.Run(async () =>
            {
                using var reader = new StreamReader(fromBrimDeck, Encoding.UTF8);
                using var writer = new StreamWriter(toBrimDeck, new UTF8Encoding(false)) { AutoFlush = true };
                await writer.WriteLineAsync("daemon log line that is not JSON");
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    var frame = Json(line); sent.Add(frame);
                    foreach (var reply in answer(frame, frame.Get("request").Get("type").Text())) await writer.WriteLineAsync(reply);
                    if (frame.Get("request").Get("type").Text() == "daemon.shutdown") return;
                }
            });
            try { return (await QoderUsage.ConverseAsync(input, output, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token), null, sent); }
            catch (Exception ex) when (ex is QoderUsage.DaemonException or IOException) { return (null, ex, sent); }
            finally { input.Dispose(); await daemon.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        static string Ok(JsonElement frame, string response) =>
            $$$"""{"type":"control_response","response":{"subtype":"success","request_id":"{{{frame.Get("request_id").Text()}}}","response":{{{response}}}}}""";
        IEnumerable<string> Cli(JsonElement frame, string? method) => method switch
        {
            "daemon.initialize" => [Ok(frame, """{"daemon_protocol_version":2,"daemon_version":"1.1.91","capabilities":["multi_session_v2"]}""")],
            "daemon.get_usage_info" => [Ok(frame, """{"usage":""" + JsonSerializer.Serialize(Json(DaemonUsage)) + ""","session":{"total_credits":0,"model_usage":{}}}""")],
            "daemon.account_info" => [Ok(frame, """{"account":{"userId":"user-1","name":"n","subscriptionType":"Pro Trial"}}""")],
            "daemon.shutdown" => [Ok(frame, "{}")],
            _ => []
        };
        var (reading, error, sent) = await Talk(Cli);
        check("The CLI's daemon is asked to use the CLI's own sign-in, then for the credits and the plan, then to end",
            error is null && reading is { Plan: "Pro Trial", Metrics.Count: 4 } &&
            sent.Select(f => f.Get("request").Get("type").Text()).SequenceEqual(["daemon.initialize", "daemon.get_usage_info", "daemon.account_info", "daemon.shutdown"]) &&
            sent[0].Get("request").Get("auth").Get("type").Text() == "qodercli" && sent[0].Get("request").Get("daemon_protocol_version").Number() == 2);

        (reading, error, sent) = await Talk((frame, method) => method == "daemon.initialize"
            ? [Ok(frame, """{"daemon_protocol_version":2,"daemon_version":"1.1.86","capabilities":[]}"""),
               """{"type":"control_request","request_id":"cb-1","request":{"type":"daemon.auth.fetch_job_token","reason":"initial"}}"""]
            : method == "daemon.account_info" ? ["""{"type":"control_response","response":{"subtype":"error","request_id":"brimdeck-3","error":"no account","code":"DAEMON_COMMAND_FAILED"}}"""]
            : Cli(frame, method));
        var refusal = sent.FirstOrDefault(f => f.Get("type").Text() == "control_response").Get("response");
        check("A request from the daemon is refused, and a missing plan leaves the credits",
            error is null && reading is { Plan: "", Metrics.Count: 4 } && refusal.Get("request_id").Text() == "cb-1" &&
            refusal.Get("subtype").Text() == "error" && refusal.Get("response").ValueKind == JsonValueKind.Undefined);

        (reading, error, _) = await Talk((frame, method) => method == "daemon.get_usage_info"
            ? ["""{"type":"control_response","response":{"subtype":"error","request_id":"brimdeck-2","error":"Local login has expired. Run \"qodercli login\" to refresh.","code":"DAEMON_COMMAND_FAILED"}}"""]
            : Cli(frame, method));
        check("An expired sign-in is told apart from other failures", error is QoderUsage.DaemonException { SignIn: true });
        (reading, error, _) = await Talk((frame, method) => method == "daemon.get_usage_info"
            ? [Ok(frame, """{"usage":null,"session":{"total_credits":0,"model_usage":{}},"usage_error":"quota endpoint unavailable"}""")]
            : Cli(frame, method));
        check("A daemon that has no credits to give fails the reading", error is QoderUsage.DaemonException { SignIn: false, Code: "USAGE_UNAVAILABLE" });
    }

    private static async Task Service(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "qoder-home", ".qoder");
        var roaming = Path.Combine(root, "qoder-roaming");
        Directory.CreateDirectory(roaming);
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(8));
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Qoder }] };
        settings.Normalize();
        var calls = new List<string>();
        Exception? cliFailure = null;
        var desktop = new Desktop();
        var api = new Api(asked => { lock (calls) calls.Add(asked); });
        Func<Task>? gate = null;
        UsageService Service(TimeSpan? wait = null) => new(desktop, new DataLocations("", "", "", roaming, "") { QoderHome = home }, new HttpClient(api), () => now)
        {
            QoderWait = wait ?? TimeSpan.FromSeconds(3),
            QoderDaemon = async (command, arguments, _) =>
            {
                lock (calls) calls.Add("cli");
                if (gate is not null) await gate();
                if (cliFailure is not null) throw cliFailure;
                return QoderUsage.Parse(Json(DaemonUsage), "Pro Trial");
            },
            QoderIde = (port, _) => { lock (calls) calls.Add("ide:" + port); return Task.FromResult(QoderUsage.Parse(Json("""{"userQuota":{"total":300,"used":45}}"""), "Pro Trial")); }
        };
        async Task<ProviderSnapshot> Refresh(UsageService service) => (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Qoder);
        const string Usage = "https://openapi.qoder.sh/sash/api/v2/me/usage", Plan = "https://openapi.qoder.sh/api/v2/user/plan";
        var desktopName = Loc.T("Qoder 桌面版", "Qoder desktop app");

        using (var service = Service())
        {
            var snapshot = await Refresh(service);
            check("Without any Qoder program signed in nothing is asked", calls.Count == 0 && !snapshot.LiveQuota && snapshot.StatusLabel == Loc.T("未连接", "Not connected"));
        }

        // The desktop app's sign-in comes first, ahead of a signed-in CLI and a running IDE.
        Directory.CreateDirectory(Path.Combine(home, "bin", "qodercli")); File.WriteAllText(Path.Combine(home, "bin", "qodercli", "qodercli.exe"), "");
        Directory.CreateDirectory(Path.Combine(home, ".auth")); File.WriteAllText(Path.Combine(home, ".auth", "user"), "encrypted");
        desktop.SignIn = """{"authenticationContext":"[\"https://qoder.com\",\"https://openapi.qoder.sh\",\"m\",\"prod\"]","token":"dt-fixture","expiresAt":"2026-12-18T03:06:40Z"}""";
        Directory.CreateDirectory(Path.Combine(roaming, "Qoder", "SharedClientCache"));
        File.WriteAllText(Path.Combine(roaming, "Qoder", "SharedClientCache", ".info.json"), """{"websocketPort":56510,"pid":1}""");
        using (var service = Service())
        {
            calls.Clear();
            var snapshot = await Refresh(service);
            check("With the desktop app signed in, Qoder's API is asked for the credits and the plan with its token, and named the desktop app",
                calls.SequenceEqual([Usage, Plan]) && api.Tokens.All(token => token == "dt-fixture") &&
                snapshot is { LiveQuota: true, Plan: "Teams", Scope: "account", Source: var source } && source == desktopName &&
                snapshot.Metrics.Select(m => m.Id).SequenceEqual(["plan", "organization"]));
            now = now.AddMinutes(1);
            calls.Clear();
            snapshot = await Refresh(service);
            check("Every refresh reads again", calls.SequenceEqual([Usage, Plan]) && snapshot.QuotaTime == now);
            now = now.AddSeconds(10);
            calls.Clear();
            snapshot = await Refresh(service);
            check("A refresh right after a read shows it without asking again", calls.Count == 0 && snapshot.LiveQuota && snapshot.QuotaTime == now.AddSeconds(-10));

            now = now.AddMinutes(1);
            api.PlanStatus = HttpStatusCode.InternalServerError;
            calls.Clear();
            snapshot = await Refresh(service);
            check("Without the plan the credits still show", calls.SequenceEqual([Usage, Plan]) && snapshot is { LiveQuota: true, Plan: "" } && snapshot.Metrics.Count == 2);
            api.PlanStatus = HttpStatusCode.OK;

            now = now.AddMinutes(1);
            api.Status = HttpStatusCode.Unauthorized;
            calls.Clear();
            snapshot = await Refresh(service);
            check("When the API refuses the desktop app's token the CLI is read", calls.SequenceEqual([Usage, "cli"]) && snapshot is { LiveQuota: true, Source: "Qoder CLI" });
            now = now.AddMinutes(1);
            cliFailure = new QoderUsage.DaemonException("DAEMON_COMMAND_FAILED", "Local login has expired. Run \"qodercli login\" to refresh.");
            calls.Clear();
            snapshot = await Refresh(service);
            check("With both sign-ins refused the running IDE is asked", calls.SequenceEqual([Usage, "cli", "ide:56510"]) && snapshot is { LiveQuota: true, Source: "Qoder IDE" } &&
                snapshot.Metrics.Single().Used == 45m);
            now = now.AddMinutes(1);
            calls.Clear();
            await Refresh(service);
            check("The API is asked again on every read, while a CLI that failed is not started again for a while", calls.SequenceEqual([Usage, "ide:56510"]));

            api.Status = HttpStatusCode.OK;
            now = now.AddMinutes(1);
            calls.Clear();
            snapshot = await Refresh(service);
            check("A token the desktop app renewed is read at once", calls.SequenceEqual([Usage, Plan]) && snapshot.Source == desktopName);

            desktop.SignIn = """{"token":"dt-old","expiresAt":"2026-11-01T00:00:00Z"}""";
            File.Delete(Path.Combine(roaming, "Qoder", "SharedClientCache", ".info.json"));
            now = now.AddMinutes(1);
            calls.Clear();
            snapshot = await Refresh(service);
            check("An expired desktop sign-in is not sent; without any source that answers the last reading stays, marked as old, naming the expired sign-ins",
                calls.Count == 0 && snapshot.IsStale && snapshot.Metrics.Count == 2 && snapshot.Status.Contains(desktopName) && snapshot.Status.Contains("Qoder CLI") &&
                snapshot.StatusLabel == Loc.T("稍后重试", "Retrying later"));
        }

        // An enterprise account's layout is not read: the status says so.
        cliFailure = null;
        File.Delete(Path.Combine(home, ".auth", "user"));
        desktop.SignIn = """{"token":"dt-fixture","expiresAt":"2026-12-18T03:06:40Z"}""";
        api.Usage = """{"displayMode":"enterprise","enterpriseUsage":{}}""";
        using (var service = Service())
        {
            calls.Clear();
            var snapshot = await Refresh(service);
            check("An enterprise account is said to be not supported yet", calls.SequenceEqual([Usage, Plan]) && !snapshot.LiveQuota &&
                snapshot.StatusLabel == Loc.T("暂不支持", "Not supported") && snapshot.Status.Contains(Loc.T("企业版", "enterprise")));
        }

        // A slow daemon is waited for only briefly: the round ends with the other sources, and the reading follows in a round of its own.
        api.Usage = TeamsUsage;
        desktop.SignIn = null;
        File.WriteAllText(Path.Combine(home, ".auth", "user"), "encrypted");
        var release = new TaskCompletionSource();
        gate = () => release.Task;
        using (var service = Service(TimeSpan.FromMilliseconds(200)))
        {
            var finished = new TaskCompletionSource<bool>();
            service.BackgroundReadFinished += first => finished.TrySetResult(first);
            calls.Clear();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = await Refresh(service);
            check("A round does not wait for a slow Qoder read", timer.Elapsed < TimeSpan.FromSeconds(2) && !snapshot.LiveQuota &&
                snapshot.StatusLabel == Loc.T("等待更新", "Waiting for update") && calls.SequenceEqual(["cli"]));
            release.SetResult();
            check("The read that ends later asks for a round, as the first reading", await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            now = now.AddSeconds(5);
            calls.Clear();
            snapshot = await Refresh(service);
            check("That round shows the reading without starting another read", calls.Count == 0 && snapshot is { LiveQuota: true, Source: "Qoder CLI" });
        }
        gate = null;

        // After a restart the previous run's reading stands in until a read of this run ends: while the read is under way, and when it fails.
        var earlier = now.AddHours(-1);
        var restored = new ProviderSnapshot(ProviderId.Qoder)
        {
            Metrics = [new UsageMetric(MetricKind.Count, "积分") { Id = "plan", Unit = "积分", Total = 300, Used = 30 }], Plan = "Pro", QuotaTime = earlier, LastSeenAt = earlier, IsStale = true
        };
        release = new TaskCompletionSource();
        gate = () => release.Task;
        using (var service = Service(TimeSpan.FromMilliseconds(200)))
        {
            service.RestoreLastReadings([restored]);
            var finished = new TaskCompletionSource<bool>();
            service.BackgroundReadFinished += first => finished.TrySetResult(first);
            var snapshot = await Refresh(service);
            check("While the first Qoder read of a run is under way, the previous run's reading shows, marked as old",
                snapshot is { LiveQuota: true, IsStale: true, Plan: "Pro" } && snapshot.QuotaTime == earlier && snapshot.Metrics.Single().Used == 30m
                && snapshot.Status.Contains(Loc.T("正在读取", "Reading")));
            release.SetResult();
            check("The first reading of the run still asks for a round of its own", await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            now = now.AddSeconds(5);
            snapshot = await Refresh(service);
            check("It then replaces the previous run's reading", snapshot is { LiveQuota: true, IsStale: false, Source: "Qoder CLI" } && snapshot.QuotaTime != earlier);
        }
        gate = null;
        cliFailure = new QoderUsage.DaemonException("DAEMON_COMMAND_FAILED", "Local login has expired. Run \"qodercli login\" to refresh.");
        using (var service = Service())
        {
            service.RestoreLastReadings([restored]);
            var snapshot = await Refresh(service);
            check("A first Qoder read that fails shows the previous run's reading, marked as old",
                snapshot is { LiveQuota: true, IsStale: true } && snapshot.QuotaTime == earlier && snapshot.StatusLabel == Loc.T("稍后重试", "Retrying later"));
        }
        cliFailure = null;
    }

    // Qoder's API: the credits and the plan, refusing every request with Status.
    private sealed class Api(Action<string> asked) : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK, PlanStatus = HttpStatusCode.OK;
        public string Usage = TeamsUsage;
        public readonly List<string?> Tokens = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            asked(uri.GetLeftPart(UriPartial.Path));
            lock (Tokens) Tokens.Add(request.Headers.Authorization is { Scheme: "Bearer" } bearer ? bearer.Parameter : null);
            bool plan = uri.AbsolutePath == QoderUsage.PlanPath;
            var status = Status != HttpStatusCode.OK ? Status : plan ? PlanStatus : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(plan ? TeamsPlan : Usage, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Desktop : IDesktopSources
    {
        public string? SignIn;
        public string? ReadCursorToken(string database) => throw new InvalidOperationException("Unexpected credential read");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => throw new InvalidOperationException("Unexpected service discovery");
        public string? ReadQoderDesktopSignIn(string folder) => folder.EndsWith("com.qoder.app.stable", StringComparison.Ordinal) ? SignIn : null;
        public string?[][]? Rows = [];
        public string? Models;
        public readonly List<string> Queries = [];
        public IReadOnlyList<string?[]>? QueryDatabase(string database, string sql)
        {
            Queries.Add(sql);
            return database.EndsWith("state.vscdb", StringComparison.Ordinal) ? Models is null ? null : [[Models]] : Rows;
        }
    }
}

