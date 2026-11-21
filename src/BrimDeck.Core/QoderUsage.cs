using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrimDeck.Core;

// Qoder (qoder.com) and Qoder CN (qoder.com.cn) have separate accounts. Within an edition the CLI, the desktop app and the IDE
// sign in to the same account and spend the same credits. The service encrypts its answers, which only Qoder's own programs
// can read, so BrimDeck asks one of them, in this order:
//
// 1. Qoder CLI (%USERPROFILE%\.qoder\bin\qodercli\qodercli.exe; Qoder CN: .qoder-cn\bin\qoderclicn\qoderclicn.exe), started as the
//    daemon of the Qoder Agent SDK (`--sdk`, with QODER_AGENT_SDK_DAEMON=1). It is asked to use the CLI's own sign-in
//    (.auth\user), which it only reads: the daemon never renews it, so an expired sign-in waits for the CLI's next use.
// 2. The desktop app's copy of the same daemon (resources\app.asar.unpacked\node_modules\@qoder-ai\<sdk>\dist\_worker\
//    qoder-worker-runtime.obf.mjs, run by the app's own executable as Node). The desktop app keeps its sign-in apart, in
//    auth.v1.dat in its data folder (%APPDATA%\com.qoder.app.stable; Qoder CN: com.qodercn.app.stable), and hands its token to the
//    daemon when asked (daemon.auth.fetch_job_token); BrimDeck does the same with the token it decrypts. It is never renewed.
// 3. The IDE's language server, while the IDE runs: SharedClientCache\.info.json in %APPDATA%\Qoder (Qoder CN: QoderCN) names its
//    local WebSocket port, which answers JSON-RPC requests framed like LSP messages: credit/usage and user/plan.
//
// The daemon and the language server answer credit/usage alike: {userQuota, addOnQuota, orgResourcePackage (cap in place of total),
// dedicatedResourcePackages [{id, total, used, expiresAt, available, displayLabels [{dimension: "title", valueI18n}]}], expiresAt,
// isQuotaExceeded}. The percentages differ (the daemon's are whole, the server's are shares), so they are worked out here.
public static class QoderUsage
{
    // An edition's folders and programs: Home under %USERPROFILE%, Cli under Home\bin, DesktopData and IdeData under %APPDATA%.
    public sealed record Edition(ProviderId Id, string Name, string Cli, string DesktopData, string IdeData, string DesktopExe)
    {
        public bool China => Id == ProviderId.QoderCn;
    }
    public static readonly Edition Global = new(ProviderId.Qoder, "Qoder", "qodercli", "com.qoder.app.stable", "Qoder", "Qoder.exe");
    public static readonly Edition China = new(ProviderId.QoderCn, "Qoder CN", "qoderclicn", "com.qodercn.app.stable", "QoderCN", "Qoder CN.exe");
    public static Edition For(ProviderId id) => id == ProviderId.QoderCn ? China : Global;

    public enum Route { Cli, Desktop, Ide }
    public static string RouteName(Edition edition, Route route) => route switch
    {
        Route.Cli => edition.Name + " CLI",
        Route.Desktop => Loc.T($"{edition.Name} 桌面版", $"{edition.Name} desktop app"),
        _ => edition.Name + " IDE"
    };

    public sealed record Reading(List<UsageMetric> Metrics, string Plan, bool Exceeded);

    // The daemon refused a request; SignIn when the reason is the sign-in (missing, expired or refused).
    public sealed class DaemonException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
        public bool SignIn => Code.Contains("AUTH", StringComparison.OrdinalIgnoreCase)
            || new[] { "credential", "login", "expired", "unauthorized", "token" }.Any(word => Message.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public static string CliPath(string home, Edition edition) => Path.Combine(home, "bin", edition.Cli, edition.Cli + ".exe");
    public static string CliSignIn(string home) => Path.Combine(home, ".auth", "user");
    public static string DesktopData(string roaming, Edition edition) => Path.Combine(roaming, edition.DesktopData);
    public static string IdeInfo(string roaming, Edition edition) => Path.Combine(roaming, edition.IdeData, "SharedClientCache", ".info.json");

    // The daemon the desktop app installed in folder carries: the package name differs by edition (qoder-agent-sdk,
    // qoder-cn-agent-sdk), so every @qoder-ai package is looked in.
    public static string? FindRuntime(string folder)
    {
        var packages = Path.Combine(folder, "resources", "app.asar.unpacked", "node_modules", "@qoder-ai");
        if (!Directory.Exists(packages)) return null;
        foreach (var package in Directory.EnumerateDirectories(packages).Order(StringComparer.OrdinalIgnoreCase))
        {
            var runtime = Path.Combine(package, "dist", "_worker", "qoder-worker-runtime.obf.mjs");
            if (File.Exists(runtime)) return runtime;
        }
        return null;
    }

    // The desktop app's sign-in: {token, refreshToken, expiresAt (ISO 8601), refreshTokenExpiresAt, user}. Its token lasts 30 days.
    public sealed record DesktopSignIn(string Token, DateTimeOffset? ExpiresAt)
    {
        public bool Expired(DateTimeOffset now) => ExpiresAt is { } at && at <= now.AddMinutes(1);
    }
    public static DesktopSignIn? ParseDesktopSignIn(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var token = doc.RootElement.Get("token").Text();
            return token.Length == 0 ? null : new(token, doc.RootElement.Get("expiresAt").Date());
        }
        catch (JsonException) { return null; }
    }

    // The language server's port from .info.json ({"websocketPort": 56510, "pid": …}); null when the IDE never ran.
    public static int? ReadIdePort(string info)
    {
        try
        {
            using var doc = SharedFile.Parse(info);
            return doc.RootElement.Get("websocketPort").Number() is { } port && port is > 0 and < 65536 ? (int)port : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // One row for each kind of credits the account holds: the plan's, add-on credits, the organization's package, and each
    // package kept for a model (named by the service in the display language). The plan's credits end when the plan's period
    // does (expiresAt), as Qoder does not carry them over.
    public static Reading Parse(JsonElement usage, string plan)
    {
        var metrics = new List<UsageMetric>();
        void Add(string id, string label, JsonElement bucket, string totalField = "total", DateTimeOffset? expires = null)
        {
            if (bucket.ValueKind != JsonValueKind.Object) return;
            decimal total = (decimal)Math.Max(0, bucket.Get(totalField).Number() ?? 0), used = (decimal)Math.Max(0, bucket.Get("used").Number() ?? 0);
            if (bucket.Get("used").Number() is null && bucket.Get("remaining").Number() is { } left) used = Math.Max(0, total - (decimal)left);
            if (total <= 0 && used <= 0) return;
            metrics.Add(new UsageMetric(MetricKind.Count, label) { Id = id, Unit = "积分", Total = Math.Max(total, used), Used = used, ExpiresAt = expires });
        }
        Add("plan", "套餐积分", usage.Get("userQuota"), expires: usage.Get("expiresAt").Date());
        Add("addon", "加购积分", usage.Get("addOnQuota"));
        Add("organization", "组织资源包", usage.Get("orgResourcePackage"), "cap");
        foreach (var package in usage.Get("dedicatedResourcePackages").Items())
        {
            if (package.Get("available").ValueKind == JsonValueKind.False) continue;
            Add("package:" + package.Get("id").Text(), PackageName(package), package, expires: package.Get("expiresAt").Date());
        }
        return new(metrics, plan, usage.Get("isQuotaExceeded").ValueKind == JsonValueKind.True);
    }

    private static string PackageName(JsonElement package)
    {
        foreach (var label in package.Get("displayLabels").Items())
        {
            if (label.Get("dimension").Text() != "title") continue;
            var names = label.Get("valueI18n");
            var name = names.Get(Loc.IsEnglish ? "en-US" : "zh-CN").Text();
            if (name.Length == 0) name = names.Get(Loc.IsEnglish ? "zh-CN" : "en-US").Text();
            if (name.Length > 0) return name;
        }
        return package.Get("name").Text() is { Length: > 0 } own ? own : Loc.T("专属积分", "Dedicated credits");
    }

    // Starts the daemon at command (with arguments), asks it for the credits and the plan, and ends it. signIn is the desktop
    // app's token to hand over; without it the daemon reads the CLI's sign-in. electron runs the desktop app's executable as Node.
    public static async Task<Reading> ReadDaemonAsync(string command, IReadOnlyList<string> arguments, bool electron, DesktopSignIn? signIn, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["QODER_AGENT_SDK_DAEMON"] = "1";
        if (electron) start.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("The Qoder daemon did not start");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        _ = DrainAsync(process.StandardError);
        try { return await ConverseAsync(process.StandardInput, process.StandardOutput, signIn, deadline.Token); }
        finally
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
            // The daemon ends by itself after daemon.shutdown; it and what it started are ended if it has not after a moment.
            try { if (!process.WaitForExit(2000)) process.Kill(true); } catch (InvalidOperationException) { }
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer) > 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    // The daemon protocol (version 2): one JSON object per line each way. BrimDeck sends control_request frames
    // {type: "control_request", request_id, request: {type: "daemon.<method>", …}} and reads the control_response frame with the
    // same request_id, {response: {subtype: "success", response} | {subtype: "error", error, code}}. The daemon asks back the same
    // way for the desktop app's token.
    public static async Task<Reading> ConverseAsync(TextWriter input, TextReader output, DesktopSignIn? signIn, CancellationToken ct)
    {
        async Task Send(JsonObject frame) { await input.WriteLineAsync(frame.ToJsonString().AsMemory(), ct); await input.FlushAsync(ct); }
        Task Request(string id, string method, JsonObject? parameters = null)
        {
            var request = parameters ?? [];
            request["type"] = "daemon." + method;
            return Send(new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request });
        }
        await Request("brimdeck-1", "initialize", new JsonObject
        {
            ["daemon_protocol_version"] = 2, ["sdk_version"] = "1.0.59", ["capabilities"] = new JsonArray("multi_session_v2"),
            ["auth"] = signIn is null ? new JsonObject { ["type"] = "qodercli" } : new JsonObject { ["type"] = "jobToken", ["jobTokenProvider"] = "host" }
        });
        JsonElement usage = default;
        while (true)
        {
            var line = await output.ReadLineAsync(ct) ?? throw new IOException("The Qoder daemon ended before it answered");
            if (line.Length == 0 || line[0] != '{') continue;
            JsonDocument frame;
            // The daemon writes nothing else to its output, but a line that is not a frame is passed over rather than trusted.
            try { frame = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using var _ = frame;
            var root = frame.RootElement;
            if (root.Get("type").Text() == "control_request")
            {
                var id = root.Get("request_id").Text();
                JsonObject answer = root.Get("request").Get("type").Text() == "daemon.auth.fetch_job_token" && signIn is not null
                    ? new() { ["subtype"] = "success", ["request_id"] = id, ["response"] = JobToken(signIn) }
                    : new() { ["subtype"] = "error", ["request_id"] = id, ["error"] = "BrimDeck has no handler for this request", ["code"] = "METHOD_NOT_FOUND" };
                await Send(new JsonObject { ["type"] = "control_response", ["response"] = answer });
                continue;
            }
            if (root.Get("type").Text() != "control_response") continue;
            var response = root.Get("response");
            var requestId = response.Get("request_id").Text();
            if (response.Get("subtype").Text() != "success")
            {
                // The plan only names the column; without it the credits still show.
                if (requestId == "brimdeck-3") return Parse(usage, "");
                throw new DaemonException(response.Get("code").Text(), response.Get("error").Text());
            }
            var result = response.Get("response");
            switch (requestId)
            {
                case "brimdeck-1":
                    await Request("brimdeck-2", "get_usage_info");
                    break;
                case "brimdeck-2":
                    if (result.Get("usage").ValueKind != JsonValueKind.Object)
                        throw new DaemonException("USAGE_UNAVAILABLE", result.Get("usage_error").Text() is { Length: > 0 } error ? error : "Qoder returned no credits");
                    usage = result.Get("usage").Clone();
                    await Request("brimdeck-3", "account_info");
                    break;
                case "brimdeck-3":
                    await Request("brimdeck-4", "shutdown");
                    return Parse(usage, result.Get("account").Get("subscriptionType").Text());
            }
        }
    }

    private static JsonObject JobToken(DesktopSignIn signIn)
    {
        var token = new JsonObject { ["token"] = signIn.Token };
        if (signIn.ExpiresAt is { } at) token["expires_at"] = at.ToUnixTimeMilliseconds();
        return token;
    }

    // Asks the IDE's language server on port for the credits and the plan.
    public static async Task<Reading> ReadIdeAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), deadline.Token);
        foreach (var (id, method) in new[] { (1, "credit/usage"), (2, "user/plan") })
        {
            var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = new { } });
            await socket.SendAsync(Encoding.UTF8.GetBytes(Frame(body)), WebSocketMessageType.Text, true, deadline.Token);
        }
        JsonElement? usage = null; string? plan = null;
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (usage is null || plan is null)
        {
            var received = await socket.ReceiveAsync(buffer, deadline.Token);
            if (received.MessageType == WebSocketMessageType.Close) throw new IOException("The Qoder IDE closed the connection");
            if (message.Length + received.Count > 4 << 20) throw new InvalidDataException(Loc.T("Qoder IDE 返回的数据过大", "Qoder IDE returned too much data"));
            message.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            if (Unframe(text) is not { } json) continue;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // The server also sends notifications of its own (extension/register and others), which have no id.
            switch (root.Get("id").Number())
            {
                case 1:
                    if (root.Get("result").ValueKind != JsonValueKind.Object) throw new DaemonException("IDE_ERROR", root.Get("error").Get("message").Text());
                    usage = root.Get("result").Clone();
                    break;
                case 2:
                    plan = root.Get("result").Get("plan_tier_name").Text();
                    break;
            }
        }
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1), ct); }
        catch (Exception ex) when (ex is WebSocketException or TimeoutException or OperationCanceledException) { }
        return Parse(usage.Value, plan);
    }

    // Token records come from two places:
    // - The IDE's language server writes each model reply to chat_message in SharedClientCache\cache\db\local.db (role
    //   "assistant", token_info {prompt_tokens, completion_tokens, cached_tokens}, model_info {model_key}, gmt_create in ms), where
    //   prompt_tokens includes the cached ones.
    // - The CLI and the desktop app write sessions under projects in Home (LogReader.ParseQoder), with token counts only once
    //   TokenSwitch is set for the user (QODER_EXPOSE_TOKEN_USAGE=1; Qoder CN: QODERCN_EXPOSE_TOKEN_USAGE=1) before they start.
    // A model is recorded by its key ("auto", "smodel", "dfmodel"); the names Qoder shows for them are in the IDE's model lists,
    // which it keeps in User\globalStorage\state.vscdb (aicoding.modelConfigs.cache.<mode>: [{name, displayName}]), and in the
    // texts the CLI keeps in .auth\dynamic-texts.json (locales.en["modelSelector.item.<key>"]). A key neither names is shown as it is.
    public static string IdeDatabase(string roaming, Edition edition) => Path.Combine(roaming, edition.IdeData, "SharedClientCache", "cache", "db", "local.db");
    public static string IdeState(string roaming, Edition edition) => Path.Combine(roaming, edition.IdeData, "User", "globalStorage", "state.vscdb");
    public static string TokenSwitch(Edition edition) => edition.China ? "QODERCN_EXPOSE_TOKEN_USAGE" : "QODER_EXPOSE_TOKEN_USAGE";
    public static bool SwitchOn(string? value) => value?.Trim().ToLowerInvariant() is "1" or "true" or "yes";
    // Whether the current user's environment variables, which Qoder started from Explorer inherits, ask for token counts.
    public static bool? UserSwitch(Edition edition)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return SwitchOn(Environment.GetEnvironmentVariable(TokenSwitch(edition), EnvironmentVariableTarget.User)); }
        catch (System.Security.SecurityException) { return null; }
    }

    // Adds the IDE's replies to the snapshot LogReader read from the CLI's and the desktop app's sessions, and names the models.
    // switchOn: whether the user's environment asks Qoder to record token counts; null when it cannot be told.
    public static void ReadTokens(ProviderSnapshot snapshot, IDesktopSources desktop, string roaming, string home, Edition edition, DateTimeOffset start, DateTimeOffset now, bool? switchOn)
    {
        int cli = snapshot.Entries.Count;
        var entries = snapshot.Entries.ToList();
        var database = IdeDatabase(roaming, edition);
        int ide = 0;
        bool ideRead = true;
        if (File.Exists(database))
        {
            snapshot.UsageAvailable = true;
            var since = start.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var rows = desktop.QueryDatabase(database, $"SELECT id, session_id, gmt_create, token_info, model_info FROM chat_message WHERE role = 'assistant' AND token_info <> '' AND gmt_create >= {since}");
            if (rows is null) { ideRead = false; snapshot.UsageComplete = false; }
            else
            {
                var replies = ParseTokens(rows, new Dictionary<string, string>(), edition).Where(e => e.Time >= start && e.Time <= now && e.Total > 0).ToList();
                ide = replies.Count; entries.AddRange(replies);
            }
        }
        if (!snapshot.UsageAvailable) { snapshot.UsageNote = Loc.T($"未找到本机 {edition.Name} 记录。", $"No local {edition.Name} records were found."); return; }
        var names = ModelNames(desktop.QueryDatabase(IdeState(roaming, edition), "SELECT value FROM ItemTable WHERE key LIKE 'aicoding.modelConfigs.cache.%'") ?? []);
        foreach (var (key, name) in TextNames(Path.Combine(home, ".auth", "dynamic-texts.json"))) names.TryAdd(key, name);
        snapshot.Entries = entries.Select(e => names.TryGetValue(e.Model, out var name) ? e with { Model = name } : e).OrderBy(e => e.Time).ToList();
        var note = Loc.T($"本机 {edition.Name} {start.LocalDateTime:yyyy-MM-dd} 起 · IDE {ide} 次 · CLI 与桌面版 {cli} 次模型请求",
            $"Local {edition.Name} since {start.LocalDateTime:yyyy-MM-dd} · IDE {ide} · CLI and desktop app {cli} " + (cli == 1 ? "model request" : "model requests"));
        if (switchOn == false) note += Loc.T(" · CLI 与桌面版未开启 Token 记录", " · Token recording is off for the CLI and desktop app");
        if (!ideRead) note += Loc.T(" · IDE 记录暂时无法读取", " · The IDE records cannot be read right now");
        else if (!snapshot.UsageComplete) note += Loc.T(" · 部分记录未能读取", " · Some records could not be read");
        snapshot.UsageNote = note;
    }

    // The English model names in the CLI's texts: "modelSelector.item.<key>" under locales.en, leaving out the keys' other texts.
    public static IEnumerable<(string Key, string Name)> TextNames(string file)
    {
        const string Prefix = "modelSelector.item.";
        var found = new List<(string, string)>();
        try
        {
            using var doc = SharedFile.Parse(file);
            var texts = doc.RootElement.Get("locales").Get("en");
            if (texts.ValueKind != JsonValueKind.Object) return found;
            foreach (var text in texts.EnumerateObject())
                if (text.Name.StartsWith(Prefix, StringComparison.Ordinal) && !text.Name.AsSpan(Prefix.Length).Contains('.') && text.Value.Text() is { Length: > 0 } name)
                    found.Add((text.Name[Prefix.Length..], name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return found;
    }

    // Model keys to the names the IDE shows, from the values of its model lists.
    public static Dictionary<string, string> ModelNames(IEnumerable<string?[]> lists)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.Get("name").Text() is { Length: > 0 } key && node.Get("displayName").Text() is { Length: > 0 } name) names.TryAdd(key, name);
                foreach (var property in node.EnumerateObject()) Walk(property.Value);
            }
            else foreach (var item in node.Items()) Walk(item);
        }
        foreach (var row in lists)
        {
            if (row.FirstOrDefault() is not { Length: > 0 } value) continue;
            try { using var doc = JsonDocument.Parse(value); Walk(doc.RootElement); }
            catch (JsonException) { }
        }
        return names;
    }

    // Rows of: id, session, time (ms), token_info, model_info.
    public static IEnumerable<TokenEntry> ParseTokens(IEnumerable<string?[]> rows, IReadOnlyDictionary<string, string> names, Edition edition)
    {
        foreach (var row in rows)
        {
            if (row.Length < 5 || string.IsNullOrEmpty(row[0]) || !long.TryParse(row[2], out var millis) || string.IsNullOrEmpty(row[3])) continue;
            DateTimeOffset time;
            long prompt, cached, output;
            string key;
            try
            {
                time = DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime();
                using var tokens = JsonDocument.Parse(row[3]!);
                prompt = tokens.RootElement.Get("prompt_tokens").Count(); cached = tokens.RootElement.Get("cached_tokens").Count(); output = tokens.RootElement.Get("completion_tokens").Count();
                using var model = JsonDocument.Parse(string.IsNullOrEmpty(row[4]) ? "{}" : row[4]!);
                key = model.RootElement.Get("model_key").Text();
            }
            catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException) { continue; }
            cached = Math.Min(cached, prompt);
            yield return new TokenEntry((edition.China ? "qodercn:" : "qoder:") + row[0], time, names.GetValueOrDefault(key, key), prompt - cached, cached, 0, 0, output);
        }
    }

    // The language server frames each JSON-RPC message as LSP does: "Content-Length: n\r\n\r\n" and the body.
    public static string Frame(string body) => $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
    public static string? Unframe(string message)
    {
        int split = message.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var body = split >= 0 ? message[(split + 4)..] : message;
        return body.TrimStart().StartsWith('{') ? body : null;
    }
}
