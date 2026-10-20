using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

public sealed record LocalEndpoint(int Port, string Scheme, string Token);
public interface IDesktopSources
{
    ClaudeDesktopState ReadClaudeDesktop(DataLocations locations) => new(false, []);
    GrokBotSignIn ReadGrokBotDesktop(DataLocations locations) => new(false, "");
    string? ReadCursorToken(string database);
    Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation);
    // Runs a read-only query against a local SQLite database; null means the database could not be opened or queried.
    IReadOnlyList<string?[]>? QueryDatabase(string database, string sql) => null;
}

public sealed class UsageService(IDesktopSources desktop, DataLocations? locations = null, HttpClient? http = null, Func<DateTimeOffset>? clock = null, IProviderSecrets? secrets = null, Func<IReadOnlyList<WslHome>>? wsl = null) : IDisposable
{
    // Every provider uses the panel's shared refresh cycle. Claude may display local history
    // after an unsuccessful request, but it does not delay the next scheduled refresh.
    private static string ClaudeUsageNote => Loc.T(" Token 统计来自本机 Claude Code 会话，不包含桌面版普通聊天。",
        " Token statistics come from local Claude Code sessions and do not include regular chats in the desktop app.");
    private readonly HttpClient _http = http ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    // ZCode's own service (Start Plan and MCP allowances) can take tens of seconds; those requests finish in the background.
    private readonly HttpClient _zcodeHttp = http ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private readonly LatestResponse _zcodeStart = new(), _zcodeMcp = new();
    private (string Account, ProviderResult Result, DateTimeOffset Time)? _zcodeCoding;
    private readonly DataLocations _locations = locations ?? DataLocations.Detect();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private readonly LogReader _logs = new();
    // WSL home folders are searched again every few minutes: listing them touches the distributions' file systems.
    private (DateTimeOffset Time, IReadOnlyList<WslHome> Homes)? _wsl;
    private IReadOnlyList<string> _wslClaude = [], _wslCodex = [];
    private readonly AntigravityUsage _antigravity = new();
    private readonly DshSessions _dsh = new();
    private readonly SemaphoreSlim _gate = new(1);
    private ClaudeQuotaSnapshot? _claudeRetained, _grokBotRetained;
    private readonly ConfiguredProviders _configured = new(secrets, http, clock);
    public Task<ProviderTestResult> TestProviderAsync(AppEntry entry, CancellationToken ct = default) => _configured.TestAsync(entry, ct);

    public async Task<List<ProviderSnapshot>> RefreshAsync(DeckSettings settings, CancellationToken cancellation = default, DateTime? usageStart = null)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            // Each underlying source is read once, even when several columns reuse it.
            var required = settings.RequiredProviders;
            UpdateWsl(settings.IncludeWsl);
            // The Claude and Grok Bot account APIs are asked only for rows that read online; rows that do not share one local snapshot.
            bool claudeOnline = settings.EnabledApps.Any(app => app.QuotaSource == ProviderId.Claude && app.QuotaOnline);
            bool claudeLocal = settings.EnabledApps.Any(app => app.QuotaSource == ProviderId.Claude && ProviderCatalog.ReadsLocalQuota(app));
            bool grokBotOnline = settings.EnabledApps.Any(app => app.QuotaSource == ProviderId.GrokBot && app.QuotaOnline);
            bool grokBotLocal = settings.EnabledApps.Any(app => app.QuotaSource == ProviderId.GrokBot && ProviderCatalog.ReadsLocalQuota(app));
            // A stalled source ends with a timeout status instead of holding every later refresh behind this gate.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            // Grok Bot has no usage records, so it is read only for its online quota rows.
            var sources = ProviderCatalog.UsageSources.Where(required.Contains);
            if (grokBotOnline) sources = sources.Append(ProviderId.GrokBot);
            var tasks = sources.Select(id => FetchAsync(id, usageStart, claudeOnline, deadline.Token, cancellation));
            var configured = _configured.RefreshAsync(settings.EnabledApps, deadline.Token);
            List<ProviderSnapshot> results = [.. await Task.WhenAll(tasks), .. await configured];
            if (claudeLocal) results.Add(ReadClaudeLocal(usageStart));
            if (grokBotLocal) results.Add(ReadGrokBotLocal(usageStart));
            return results;
        }
        finally { _gate.Release(); }
    }

    // One failing source must not discard the others' results; errors not anticipated below still end as a status.
    private async Task<ProviderSnapshot> FetchAsync(ProviderId id, DateTime? usageStart, bool claudeOnline, CancellationToken ct, CancellationToken lifetime)
    {
        try { return await FetchCoreAsync(id, usageStart, claudeOnline, ct); }
        catch (Exception ex) when (!lifetime.IsCancellationRequested)
        {
            bool timedOut = ex is OperationCanceledException;
            return new ProviderSnapshot(id)
            {
                UsageStart = usageStart?.Date ?? _clock().LocalDateTime.Date.AddDays(-29),
                StatusLabel = timedOut ? Loc.T("读取超时", "Timed out") : Loc.T("读取失败", "Read failed"),
                Status = timedOut ? Loc.T("读取超时，稍后重试", "Reading timed out. Retrying later.") : Loc.T("暂时无法读取数据，稍后重试", "The data cannot be read right now. Retrying later.")
            };
        }
    }

    private void UpdateWsl(bool include)
    {
        if (!include) { _wsl = null; _wslClaude = _wslCodex = []; return; }
        var now = _clock();
        if (_wsl is not { } found || now - found.Time > TimeSpan.FromMinutes(3) || now < found.Time) _wsl = found = (now, wsl is null ? WslLocations.Discover() : wsl());
        _wslClaude = found.Homes.Where(home => home.Claude.Length > 0).Select(home => home.Claude).ToList();
        _wslCodex = found.Homes.Where(home => home.Codex.Length > 0).Select(home => home.Codex).ToList();
    }

    private async Task<ProviderSnapshot> FetchCoreAsync(ProviderId id, DateTime? usageStart, bool claudeOnline, CancellationToken ct)
    {
        var now = _clock();
        var start = new DateTimeOffset(usageStart?.Date ?? now.LocalDateTime.Date.AddDays(-29));
        // The reader owns a cache and is accessed serially before the first asynchronous request.
        var snapshot = id is ProviderId.Claude or ProviderId.Codex
            ? _logs.Read(id, id == ProviderId.Codex ? _locations.CodexHome : _locations.ClaudeHome, now, start.LocalDateTime, id == ProviderId.Codex ? _wslCodex : _wslClaude) : new ProviderSnapshot(id) { UsageStart = start.LocalDateTime.Date };
        if (id == ProviderId.Claude) ReadClaudeProfile(snapshot);
        if (id == ProviderId.ZCode) ZCodeUsage.Read(snapshot, desktop, _locations.ZCodeHome, start, now);
        if (id == ProviderId.Antigravity) _antigravity.Read(snapshot, desktop, _locations.AntigravityConversations, start, now);
        if (id == ProviderId.Dsh) _dsh.Read(snapshot, _locations.DshHome, start, now);
        try
        {
            switch (id)
            {
                case ProviderId.Codex:
                {
                    using var auth = ReadJson(Path.Combine(_locations.CodexHome, "auth.json"));
                    var tokens = auth?.RootElement.Get("tokens") ?? default;
                    var token = tokens.Get("access_token").Text();
                    if (token.Length == 0) { snapshot.StatusLabel = Loc.T("未连接", "Not connected"); snapshot.Status = Loc.T("请先登录 Codex", "Sign in to Codex first"); break; }
                    using var request = Bearer(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage", token);
                    var account = tokens.Get("account_id").Text();
                    if (account.Length > 0) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
                    using var doc = await SendAsync(_http, request, ct);
                    var quotas = QuotaParser.Codex(doc.RootElement, now, true);
                    if (quotas.Count == 0) throw new InvalidDataException();
                    snapshot.Quotas = quotas;
                    snapshot.Plan = doc.RootElement.Get("plan_type").Text();
                    snapshot.PlanSource = Loc.T("Codex 账户", "Codex account");
                    Connected(snapshot, Loc.T("Codex 账户", "Codex account"), now);
                    break;
                }
                case ProviderId.Claude:
                    // Without an online Claude row this snapshot only serves token statistics, so no sign-in is read.
                    if (claudeOnline) await ReadClaudeAsync(snapshot, now, ct);
                    break;
                case ProviderId.Cursor:
                    await ReadCursorAsync(snapshot, now, start, ct);
                    break;
                case ProviderId.Antigravity:
                    await ReadAntigravityAsync(snapshot, now, start, ct);
                    break;
                case ProviderId.ZCode:
                    await ReadZCodeAsync(snapshot, now, ct);
                    break;
                case ProviderId.Dsh:
                    await ReadDshAsync(snapshot, now, ct);
                    break;
                case ProviderId.GrokBot:
                    await ReadGrokBotAsync(snapshot, now, ct);
                    break;
            }
        }
        catch (HttpRequestException ex)
        {
            snapshot.StatusLabel = ex.StatusCode switch
            { HttpStatusCode.Unauthorized => Loc.T("登录已失效", "Sign-in expired"), HttpStatusCode.Forbidden => Loc.T("访问受限", "Access denied"), _ => Loc.T("暂不可用", "Unavailable") };
            snapshot.Status = ex.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Loc.T("登录已失效或账户接口拒绝访问，请在原应用重新登录",
                    "The sign-in has expired or the account API refused access. Sign in again in the original app."),
                HttpStatusCode.TooManyRequests => Loc.T("请求频繁，稍后自动重试", "Too many requests. Retrying automatically later."),
                _ => Loc.T("账户接口暂时不可用", "The account API is temporarily unavailable.")
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { snapshot.StatusLabel = Loc.T("读取超时", "Timed out"); snapshot.Status = Loc.T("读取超时，稍后重试", "Reading timed out. Retrying later."); }
        catch (InvalidDataException)
        { snapshot.StatusLabel = Loc.T("数据不可用", "Data unavailable"); snapshot.Status = Loc.T("数据格式发生变化，暂时无法读取配额", "The data format has changed, so the quota cannot be read for now."); }
        catch (System.Runtime.InteropServices.COMException)
        { snapshot.StatusLabel = Loc.T("检测失败", "Detection failed"); snapshot.Status = Loc.T("本地服务检测失败，稍后重试", "Detecting the local service failed. Retrying later."); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or FormatException)
        { snapshot.StatusLabel = Loc.T("读取失败", "Read failed"); snapshot.Status = Loc.T("暂时无法读取数据，稍后重试", "The data cannot be read right now. Retrying later."); }
        if (!snapshot.LiveQuota && snapshot.Quotas.Count > 0) snapshot.Source = Loc.T("本地配额快照 · 可能已过时", "Local quota snapshot · may be outdated");
        return snapshot;
    }

    private void ReadClaudeProfile(ProviderSnapshot snapshot)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var profile = Path.Combine(_locations.ClaudeHome, ".claude.json");
            if (!File.Exists(profile) && Path.GetFullPath(_locations.ClaudeHome).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.Combine(home, ".claude"), StringComparison.OrdinalIgnoreCase)) profile = Path.Combine(home, ".claude.json");
            using var doc = ReadJson(profile);
            snapshot.Plan = PlanParser.ClaudeProfile(doc?.RootElement ?? default);
            if (snapshot.Plan.Length > 0) snapshot.PlanSource = Loc.T("Claude 本地账户记录", "Claude local account record");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    // Null-safe: the account endpoint needs the profile scope, which not every sign-in has, so a failure only means the plan
    // is taken from the local records instead.
    private async Task<string> ReadClaudePlanAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = Bearer(HttpMethod.Get, "https://api.anthropic.com/api/oauth/profile", token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            using var doc = await SendAsync(_http, request, ct);
            return PlanParser.ClaudeOrganization(doc.RootElement);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException or FormatException)
        { return ""; }
    }

    private async Task ReadClaudeAsync(ProviderSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        Exception? failure = null;
        Exception? lastFailure = null;
        ClaudeCredential? login = null;
        async Task<bool> TryCredential(ClaudeCredential credential)
        {
            ct.ThrowIfCancellationRequested();
            lastFailure = null;
            if (string.IsNullOrWhiteSpace(credential.Token) || !attempted.Add(credential.Token)) return false;
            login = credential;
            try
            {
                using var request = Bearer(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage", credential.Token);
                request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                using var doc = await SendAsync(_http, request, ct);
                var quotas = QuotaParser.Claude(doc.RootElement);
                if (quotas.Count == 0) throw new InvalidDataException();
                // Publish only a complete successful result, all from the same login.
                snapshot.Quotas = quotas;
                var live = await ReadClaudePlanAsync(credential.Token, ct);
                if (live.Length > 0) { snapshot.Plan = live; snapshot.PlanSource = Loc.T("Claude 账户", "Claude account"); }
                else
                {
                    var plan = PlanParser.ReconcileClaude(credential.Plan, snapshot.Plan);
                    // The local account record already filled Plan and PlanSource; a sign-in record only describes its own plan.
                    if (plan == credential.Plan || snapshot.PlanSource.Length == 0)
                        snapshot.PlanSource = plan.Length > 0 ? credential.Source + Loc.T(" 登录记录", " sign-in record") : "";
                    snapshot.Plan = plan;
                }
                Connected(snapshot, credential.Source + Loc.T(" · 账户配额", " · account quota"), now);
                snapshot.UsageNote += ClaudeUsageNote;
                _claudeRetained = new(now, snapshot.Plan, snapshot.PlanSource, snapshot.Source, quotas.ToList());
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is
                HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException or FormatException)
            {
                lastFailure = ex;
                // Keep an operational error instead of masking it with a later stale login.
                if (failure is null || ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } ||
                    failure is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }) failure = ex;
                return false;
            }
        }

        bool codeReadFailed = false;
        ClaudeCredential? codeCredential = null;
        try
        {
            using var auth = ReadJson(Path.Combine(_locations.ClaudeHome, ".credentials.json"));
            var oauth = auth?.RootElement.Get("claudeAiOauth") ?? default;
            var token = oauth.Get("accessToken").Text();
            var plan = PlanParser.ClaudeTier(oauth.Get("rateLimitTier").Text(), oauth.Get("subscriptionType").Text());
            if (plan.Equals("max", StringComparison.OrdinalIgnoreCase) && snapshot.Plan.StartsWith("Max ", StringComparison.Ordinal)) plan = snapshot.Plan;
            if (token.Length > 0) codeCredential = new(token, plan, "Claude Code");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { codeReadFailed = true; }
        if (codeCredential is not null && await TryCredential(codeCredential)) return;

        ct.ThrowIfCancellationRequested();
        ClaudeDesktopState desktopState;
        try { desktopState = desktop.ReadClaudeDesktop(_locations); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or FormatException or
            ArgumentException or System.Security.Cryptography.CryptographicException or System.Runtime.InteropServices.COMException)
        { desktopState = new(true, [], true); }
        foreach (var credential in desktopState.Credentials)
        {
            if (await TryCredential(credential)) return;
            // Only stale or insufficiently scoped tokens warrant trying another
            // Desktop credential. Network errors and rate limits affect this source.
            if (lastFailure is not null && lastFailure is not HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }) break;
        }
        if (failure is not null)
        {
            if (failure is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }) _claudeRetained = null;
            else
            {
                if (Freshest(now) is { } local)
                {
                    // The plan comes from the login record and stays known while the interface is unavailable.
                    if (local.Plan.Length == 0 && login is { Plan.Length: > 0 }) _claudeRetained = local = local with { Plan = login.Plan, PlanSource = login.Source + Loc.T(" 登录记录", " sign-in record") };
                    Retain(snapshot, local, failure, now); return;
                }
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        _claudeRetained = null;
        snapshot.StatusLabel = desktopState.ReadFailed || codeReadFailed ? Loc.T("读取失败", "Read failed")
            : desktopState.Detected ? Loc.T("等待登录", "Waiting for sign-in") : Loc.T("未连接", "Not connected");
        snapshot.Status = desktopState.ReadFailed ? Loc.T("已发现 Claude 桌面版，暂时无法读取登录信息", "The Claude desktop app was found, but its sign-in cannot be read right now.")
            : codeReadFailed ? Loc.T("暂时无法读取 Claude Code 登录信息", "The Claude Code sign-in cannot be read right now.")
            : Loc.T("请先登录 Claude Code 或 Claude 桌面版，然后刷新配额", "Sign in to Claude Code or the Claude desktop app, then refresh the quota.");
    }

    // Claude rows that do not read online share this snapshot. No sign-in is read and no request is sent: the quota is the
    // newest sample in the desktop application's own usage history, which carries no reset times. The plan comes from
    // the local account record.
    private ProviderSnapshot ReadClaudeLocal(DateTime? usageStart)
    {
        var now = _clock();
        var snapshot = new ProviderSnapshot(ProviderId.Claude) { Configurations = [ProviderCatalog.LocalQuota], UsageStart = usageStart?.Date ?? now.LocalDateTime.Date.AddDays(-29) };
        ReadClaudeProfile(snapshot);
        if (ClaudeUsageHistory.Latest(_locations) is not { } sample || sample.Time > now.AddMinutes(5))
        {
            snapshot.StatusLabel = Loc.T("无本地记录", "No local record");
            snapshot.Status = Loc.T("Claude 桌面版尚未在本机记录额度", "The Claude desktop app has not recorded a quota on this PC yet.");
            return snapshot;
        }
        snapshot.Quotas = [new Quota("5 小时额度", sample.FiveHour, null, 300), new Quota("每周额度", sample.SevenDay, null, 10080)];
        snapshot.IsStale = true;
        // The update time is when BrimDeck read the history; the status gives the time of the newest sample in it.
        snapshot.LiveQuota = true; snapshot.QuotaTime = now;
        snapshot.Source = Loc.T("Claude 桌面版 · 本地用量记录", "Claude desktop app · local usage records");
        var time = sample.Time.LocalDateTime.ToString(sample.Time.LocalDateTime.Date == now.LocalDateTime.Date ? "HH:mm" : "MM-dd HH:mm");
        snapshot.StatusLabel = Loc.T("本地记录", "Local record");
        snapshot.Status = Loc.T($"来自 Claude 桌面版，最新记录于 {time}", $"From the Claude desktop app, latest record at {time}.");
        return snapshot;
    }

    // The desktop application samples the same account about every fifteen minutes. A newer local
    // sample replaces the last interface reading; reset times are carried over while they still lie ahead.
    // Null when neither an earlier reading nor a usable sample exists.
    private ClaudeQuotaSnapshot? Freshest(DateTimeOffset now)
    {
        var kept = _claudeRetained;
        var sample = ClaudeUsageHistory.Latest(_locations);
        if (sample is not null && sample.Time <= now.AddMinutes(5) && (kept is null || sample.Time > kept.Time))
        {
            DateTimeOffset? Reset(int minutes) => kept?.Quotas.FirstOrDefault(quota => quota.Minutes == minutes)?.ResetAt is { } reset && reset > sample.Time ? reset : null;
            var scoped = kept?.Quotas.Where(q => q.Label is not ("5 小时额度" or "每周额度")).ToList() ?? [];
            kept = new(sample.Time, kept?.Plan ?? "", kept?.PlanSource ?? "", Loc.T("Claude 桌面版 · 本地用量记录", "Claude desktop app · local usage records"),
                [new Quota("5 小时额度", sample.FiveHour, Reset(300), 300), new Quota("每周额度", sample.SevenDay, Reset(10080), 10080), .. scoped], true);
            _claudeRetained = kept;
        }
        return kept;
    }

    private static void Retain(ProviderSnapshot snapshot, ClaudeQuotaSnapshot kept, Exception? failure, DateTimeOffset now)
    {
        snapshot.IsStale = true;
        snapshot.Quotas = kept.Quotas.ToList();
        if (kept.Plan.Length > 0) { snapshot.Plan = kept.Plan; snapshot.PlanSource = kept.PlanSource; }
        snapshot.LiveQuota = true; snapshot.QuotaTime = kept.Time; snapshot.Source = kept.Source;
        var time = kept.Time.LocalDateTime.ToString(kept.Time.LocalDateTime.Date == now.LocalDateTime.Date ? "HH:mm" : "MM-dd HH:mm");
        var shown = kept.Local ? Loc.T($"显示 Claude 桌面版 {time} 记录的配额", $"showing the quota the Claude desktop app recorded at {time}")
            : Loc.T($"显示 {time} 读取的配额", $"showing the quota read at {time}");
        snapshot.Status = failure switch
        {
            null => kept.Local ? Loc.T($"已连接，{shown}", $"Connected, {shown}") : Loc.T("已连接", "Connected"),
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => Loc.T($"请求频繁，{shown}，稍后自动重试", $"Too many requests; {shown}. Retrying automatically later."),
            OperationCanceledException => Loc.T($"读取超时，{shown}，稍后自动重试", $"Reading timed out; {shown}. Retrying automatically later."),
            _ => Loc.T($"账户接口暂时不可用，{shown}，稍后自动重试", $"The account API is temporarily unavailable; {shown}. Retrying automatically later.")
        };
        snapshot.StatusLabel = failure is null ? Loc.T("已连接", "Connected") : Loc.T("稍后重试", "Retrying later");
        snapshot.UsageNote += ClaudeUsageNote;
    }

    // Grok Bot rows that read online use the Grok Bot desktop app's own sign-in, never Cursor's, and ask only for the
    // weekly allowance. The token is never refreshed. When the request fails, the newer of the last reading and the
    // desktop app's own cache is shown; a rejected sign-in discards the last reading but still shows the cache.
    private async Task ReadGrokBotAsync(ProviderSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        GrokBotSignIn signIn;
        try { signIn = desktop.ReadGrokBotDesktop(_locations); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or FormatException or
            ArgumentException or System.Security.Cryptography.CryptographicException)
        { signIn = new(true, "", true); }
        Exception? failure = null;
        if (signIn.Token.Length > 0)
        {
            try
            {
                using var request = Bearer(HttpMethod.Post, GrokBotUsage.StatusUri, signIn.Token);
                request.Content = JsonBody(new { });
                using var doc = await SendAsync(_http, request, ct);
                var reading = GrokBotUsage.Status(doc.RootElement) ?? throw new InvalidDataException();
                snapshot.Quotas = reading.Quotas;
                snapshot.Plan = reading.Plan;
                snapshot.PlanSource = reading.Plan.Length > 0 ? Loc.T("Grok Bot 账户", "Grok Bot account") : "";
                Connected(snapshot, Loc.T("Grok Bot 账户 · 每周额度", "Grok Bot account · weekly allowance"), now);
                if (reading.UsedPercent is null) snapshot.Status += PooledNote;
                _grokBotRetained = new(now, snapshot.Plan, snapshot.PlanSource, snapshot.Source, reading.Quotas);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is
                HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException or FormatException)
            { failure = ex; }
            if (failure is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }) _grokBotRetained = null;
        }
        else if (!signIn.ReadFailed)
        {
            _grokBotRetained = null;
            snapshot.StatusLabel = signIn.Detected ? Loc.T("等待登录", "Waiting for sign-in") : Loc.T("未连接", "Not connected");
            snapshot.Status = Loc.T("请先登录 Grok Bot 桌面版，然后刷新配额", "Sign in to the Grok Bot desktop app, then refresh the quota.");
            return;
        }
        if (GrokBotFreshest(now) is { } kept) { RetainGrokBot(snapshot, kept, failure, now); return; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        snapshot.StatusLabel = Loc.T("读取失败", "Read failed");
        snapshot.Status = Loc.T("已发现 Grok Bot 桌面版，暂时无法读取登录信息", "The Grok Bot desktop app was found, but its sign-in cannot be read right now.");
    }

    // The desktop app's cached reading, ignored when it claims a time well in the future.
    private GrokBotReading? ReadGrokBotCache(DateTimeOffset now)
        => GrokBotUsage.ReadCache(GrokBotUsage.Folder(_locations.Roaming)) is { } reading && reading.ReadAt <= now.AddMinutes(5) ? reading : null;
    private static string PooledNote => Loc.T("；账户使用企业共享额度，没有单独的使用比例", "; the account uses a pooled enterprise allowance, which has no share of its own");
    private static string GrokBotCacheSource => Loc.T("Grok Bot 桌面版 · 本地缓存", "Grok Bot desktop app · local cache");
    private static string GrokBotCachePlanSource => Loc.T("Grok Bot 桌面版本地缓存", "Grok Bot desktop app local cache");

    // A cached reading newer than the last interface reading replaces it; the cache carries its own reset time.
    private ClaudeQuotaSnapshot? GrokBotFreshest(DateTimeOffset now)
    {
        var kept = _grokBotRetained;
        if (ReadGrokBotCache(now) is { ReadAt: { } time } cached && (kept is null || time > kept.Time))
            _grokBotRetained = kept = new(time, cached.Plan, cached.Plan.Length > 0 ? GrokBotCachePlanSource : "", GrokBotCacheSource, cached.Quotas, true);
        return kept;
    }

    private static void RetainGrokBot(ProviderSnapshot snapshot, ClaudeQuotaSnapshot kept, Exception? failure, DateTimeOffset now)
    {
        snapshot.IsStale = true;
        snapshot.Quotas = kept.Quotas.ToList();
        if (kept.Plan.Length > 0) { snapshot.Plan = kept.Plan; snapshot.PlanSource = kept.PlanSource; }
        snapshot.LiveQuota = true; snapshot.QuotaTime = kept.Time; snapshot.Source = kept.Source;
        var time = kept.Time.LocalDateTime.ToString(kept.Time.LocalDateTime.Date == now.LocalDateTime.Date ? "HH:mm" : "MM-dd HH:mm");
        var shown = kept.Local ? Loc.T($"显示 Grok Bot 桌面版 {time} 缓存的额度", $"showing the allowance the Grok Bot desktop app cached at {time}")
            : Loc.T($"显示 {time} 读取的额度", $"showing the allowance read at {time}");
        (snapshot.StatusLabel, snapshot.Status) = failure switch
        {
            null => (Loc.T("读取失败", "Read failed"), Loc.T($"暂时无法读取 Grok Bot 桌面版的登录信息，{shown}", $"The Grok Bot desktop app sign-in cannot be read right now; {shown}.")),
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => (Loc.T("登录已失效", "Sign-in expired"),
                Loc.T($"Grok Bot 桌面版的登录已失效，请打开 Grok Bot 桌面版；{shown}", $"The Grok Bot desktop app sign-in has expired. Open the Grok Bot desktop app; {shown}.")),
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => (Loc.T("稍后重试", "Retrying later"), Loc.T($"请求频繁，{shown}，稍后自动重试", $"Too many requests; {shown}. Retrying automatically later.")),
            OperationCanceledException => (Loc.T("稍后重试", "Retrying later"), Loc.T($"读取超时，{shown}，稍后自动重试", $"Reading timed out; {shown}. Retrying automatically later.")),
            InvalidDataException or JsonException or FormatException => (Loc.T("稍后重试", "Retrying later"), Loc.T($"数据格式发生变化，{shown}", $"The data format has changed; {shown}.")),
            _ => (Loc.T("稍后重试", "Retrying later"), Loc.T($"账户接口暂时不可用，{shown}，稍后自动重试", $"The account API is temporarily unavailable; {shown}. Retrying automatically later."))
        };
    }

    // Grok Bot rows that do not read online share this snapshot. No sign-in is read and no request is sent: the quota is
    // the weekly allowance the desktop app last cached on this PC.
    private ProviderSnapshot ReadGrokBotLocal(DateTime? usageStart)
    {
        var now = _clock();
        var snapshot = new ProviderSnapshot(ProviderId.GrokBot) { Configurations = [ProviderCatalog.LocalQuota], UsageStart = usageStart?.Date ?? now.LocalDateTime.Date.AddDays(-29) };
        if (ReadGrokBotCache(now) is not { ReadAt: { } readAt } reading)
        {
            snapshot.StatusLabel = Loc.T("无本地记录", "No local record");
            snapshot.Status = Loc.T("Grok Bot 桌面版尚未在本机记录每周额度", "The Grok Bot desktop app has not recorded a weekly allowance on this PC yet.");
            return snapshot;
        }
        snapshot.Quotas = reading.Quotas;
        snapshot.Plan = reading.Plan;
        if (reading.Plan.Length > 0) snapshot.PlanSource = GrokBotCachePlanSource;
        snapshot.IsStale = true;
        // The update time is when BrimDeck read the cache; the status gives the time the desktop app took the reading.
        snapshot.LiveQuota = true; snapshot.QuotaTime = now;
        snapshot.Source = GrokBotCacheSource;
        var time = readAt.LocalDateTime.ToString(readAt.LocalDateTime.Date == now.LocalDateTime.Date ? "HH:mm" : "MM-dd HH:mm");
        snapshot.StatusLabel = Loc.T("本地记录", "Local record");
        snapshot.Status = reading.UsedPercent is null ? Loc.T($"来自 Grok Bot 桌面版，最新记录于 {time}", $"From the Grok Bot desktop app, latest record at {time}") + PooledNote
            : Loc.T($"来自 Grok Bot 桌面版，最新记录于 {time}", $"From the Grok Bot desktop app, latest record at {time}.");
        return snapshot;
    }

    private async Task ReadCursorAsync(ProviderSnapshot snapshot, DateTimeOffset now, DateTimeOffset start, CancellationToken ct)
    {
        var token = desktop.ReadCursorToken(_locations.CursorDatabase);
        snapshot.UsageNote = Loc.T("Cursor Token 明细来自账户用量接口，包含其他设备上的用量。", "Cursor token details come from the account usage API and include usage on other devices.");
        if (string.IsNullOrWhiteSpace(token))
        { snapshot.StatusLabel = Loc.T("未连接", "Not connected"); snapshot.Status = Loc.T("请打开 Cursor 并确认已登录", "Open Cursor and make sure you are signed in."); return; }
        try
        {
            using var request = Bearer(HttpMethod.Post, "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage", token);
            request.Content = JsonBody(new { });
            using var doc = await SendAsync(_http, request, ct);
            snapshot.Quotas = QuotaParser.Cursor(doc.RootElement);
            snapshot.Plan = PlanParser.Cursor(doc.RootElement);
        }
        catch (HttpRequestException) { /* Older client sessions may use the dashboard endpoint. */ }
        if (snapshot.Quotas.Count == 0 || snapshot.Plan.Length == 0)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length < 2) throw new InvalidDataException();
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                using var jwt = JsonDocument.Parse(Convert.FromBase64String(payload));
                var subject = jwt.RootElement.Get("sub").Text();
                if (subject.Length == 0) throw new InvalidDataException();
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://cursor.com/api/usage-summary");
                request.Headers.TryAddWithoutValidation("Cookie", "WorkosCursorSessionToken=" + Uri.EscapeDataString(subject + "::" + token));
                using var doc = await SendAsync(_http, request, ct);
                if (snapshot.Quotas.Count == 0) snapshot.Quotas = QuotaParser.Cursor(doc.RootElement);
                snapshot.Plan = PlanParser.Cursor(doc.RootElement);
            }
            catch (Exception ex) when (snapshot.Quotas.Count > 0 && !ct.IsCancellationRequested &&
                ex is HttpRequestException or JsonException or InvalidDataException or FormatException or TaskCanceledException) { }
        }
        if (snapshot.Plan.Length > 0) snapshot.PlanSource = Loc.T("Cursor 账户", "Cursor account");
        if (snapshot.Quotas.Count == 0) throw new InvalidDataException();
        // The Grok Bot desktop app shares this Cursor account, and its weekly allowance is a separate figure from the
        // billing-period quota above. Add it as one more row; a failure here must not drop the Cursor quota.
        try
        {
            using var request = Bearer(HttpMethod.Post, "https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus", token);
            request.Content = JsonBody(new { });
            using var doc = await SendAsync(_http, request, ct);
            if (QuotaParser.SandUsage(doc.RootElement) is { } grokBot) snapshot.Quotas.Add(grokBot);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested) { }
        Connected(snapshot, Loc.T("Cursor 当前账期", "Cursor current billing period"), now);
        try { await ReadCursorEvents(snapshot, token, now, start, ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidDataException or TaskCanceledException)
        { snapshot.UsageAvailable = false; snapshot.Entries.Clear(); snapshot.UsageNote = Loc.T("配额已读取，账户 Token 明细暂时不可用或尚未读取完整。", "The quota was read, but the account's token details are unavailable or not fully read yet."); }
    }

    private async Task ReadCursorEvents(ProviderSnapshot snapshot, string token, DateTimeOffset now, DateTimeOffset start, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var entries = new Dictionary<string, TokenEntry>();
        long rowsRead = 0;
        for (int page = 1; page <= 100; page++)
        {
            using var request = Bearer(HttpMethod.Post, "https://api2.cursor.sh/aiserver.v1.DashboardService/GetFilteredUsageEvents", token);
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Content = JsonBody(new { startDate = start.ToUnixTimeMilliseconds().ToString(), endDate = now.ToUnixTimeMilliseconds().ToString(), page, pageSize = 1000 });
            using var response = await SendAsync(_http, request, budget.Token);
            var events = response.RootElement.Get("usageEventsDisplay");
            if (events.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
            foreach (var item in events.EnumerateArray())
            {
                var entry = ParseCursorEvent(item);
                if (entry is not null && entry.Time >= start && entry.Time <= now) entries.TryAdd(entry.Key, entry);
            }
            rowsRead += events.GetArrayLength();
            var total = response.RootElement.Get("totalUsageEventsCount").Number();
            if ((total is { } expected && rowsRead >= expected) || (total is null && events.GetArrayLength() < 1000))
            { snapshot.Entries = entries.Values.ToList(); snapshot.UsageAvailable = true; return; }
            if (events.GetArrayLength() == 0) throw new InvalidDataException();
        }
        throw new InvalidDataException();
    }
    public static TokenEntry? ParseCursorEvent(JsonElement item)
    {
        var usage = item.Get("tokenUsage");
        if (usage.ValueKind != JsonValueKind.Object || item.Get("timestamp").Date() is not { } time) return null;
        var model = item.Get("model").Text();
        var identity = item.Get("id").Text();
        if (identity.Length == 0) identity = time.ToString("O") + ":" + model + ":" + item.Get("conversationId").Text() + ":" + usage.GetRawText();
        decimal? cost = usage.Get("totalCents").Number() is { } cents && cents >= 0 ? (decimal)cents / 100m : null;
        return new("cursor:" + identity, time, model, usage.Get("inputTokens").Count(), usage.Get("cacheReadTokens").Count(),
            usage.Get("cacheWriteTokens").Count(), 0, usage.Get("outputTokens").Count(), cost);
    }

    private async Task ReadAntigravityAsync(ProviderSnapshot snapshot, DateTimeOffset now, DateTimeOffset start, CancellationToken ct)
    {
        var endpoints = await desktop.FindAntigravityAsync(ct);
        if (endpoints.Count == 0)
        { snapshot.StatusLabel = Loc.T("服务未就绪", "Service not ready"); snapshot.Status = Loc.T("未检测到 Antigravity 本地用量服务", "The Antigravity local usage service was not found."); return; }
        foreach (var endpoint in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            // Certificate exception is restricted to a loopback port owned by the detected Antigravity process.
            using var local = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false, UseProxy = false,
                ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                    request.RequestUri?.Host == "127.0.0.1" && request.RequestUri.Port == endpoint.Port
            }) { Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                using var status = await LocalCall(local, endpoint, "GetUserStatus", new { }, ct);
                snapshot.Plan = PlanParser.Antigravity(status.RootElement);
                if (snapshot.Plan.Length > 0) snapshot.PlanSource = Loc.T("Antigravity 本地账户服务", "Antigravity local account service");
                snapshot.Quotas = QuotaParser.Antigravity(status.RootElement);
                try
                {
                    using var summary = await LocalCall(local, endpoint, "RetrieveUserQuotaSummary", new { }, ct);
                    var authoritative = QuotaParser.Antigravity(summary.RootElement);
                    if (authoritative.Count > 0) snapshot.Quotas = authoritative;
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { }
                if (snapshot.Quotas.Count == 0) continue;
                Connected(snapshot, Loc.T("Antigravity 本地服务", "Antigravity local service"), now);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { }
        }
        snapshot.StatusLabel = Loc.T("服务暂不可用", "Service unavailable"); snapshot.Status = Loc.T("检测到 Antigravity，但本地用量服务暂不可用", "Antigravity was found, but its local usage service is unavailable.");
    }

    // A ZCode column combines the signed-in account's Coding Plan (the same template as the 智谱 GLM / Z.ai GLM
    // sources), its Start Plan event allowances and ZCode's daily MCP allowance. Each part is shown when available.
    private async Task ReadZCodeAsync(ProviderSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        var account = ZCodeUsage.ReadAccount(_locations.ZCodeHome);
        if (account is null || account.ApiKey.Length == 0 && account.Jwt.Length == 0) { snapshot.StatusLabel = Loc.T("未连接", "Not connected"); snapshot.Status = Loc.T("请先登录 ZCode", "Sign in to ZCode first."); return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        // Each part keeps its last good result for the same account, so a failed or slow read never hides it.
        static string Owner(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        var owner = Owner(account.Jwt);
        Task<JsonElement?> Latest(LatestResponse latest, Func<JsonElement, bool> accept, string uri, params (string Name, string Value)[] headers) => latest.GetAsync(owner, async timeout =>
        {
            using var request = Bearer(HttpMethod.Get, uri, account.Jwt);
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            using var doc = await SendAsync(_zcodeHttp, request, timeout);
            return doc.RootElement.Clone();
        }, accept, deadline.Token);
        // The MCP request goes first: the service answers one account's requests in order and Start Plan is often slow.
        var mcpTask = account.Jwt.Length > 0 && account.AccessToken.Length > 0
            ? Latest(_zcodeMcp, root => ZCodeUsage.McpMetric(root) is not null, "https://zcode.z.ai/api/v1/mcp/usage",
                ("X-Bigmodel-Authorization", "Bearer " + account.AccessToken), ("Bigmodel-Target-Type", "PERSONAL"))
            : Task.FromResult<JsonElement?>(null);
        bool startRequested = account.Jwt.Length > 0 && account.DeviceMid.Length > 0;
        var startTask = startRequested
            ? Latest(_zcodeStart, root => root.Get("code").Number() == 0 && root.Get("data").Get("plans").ValueKind == JsonValueKind.Array,
                "https://zcode.z.ai/api/v1/zcode-plan/billing/balance", ("X-Device-Mid", account.DeviceMid))
            : Task.FromResult<JsonElement?>(null);
        ScriptSession? session = null; ProviderResult? coding = null; Exception? failure = null;
        var codingOwner = Owner(account.Family + ":" + account.ApiKey);
        if (account.ApiKey.Length > 0)
        {
            session = new ScriptSession(_http, new AppEntry { QuotaSource = account.Family == "zai" ? ProviderId.GlmGlobal : ProviderId.GlmChina }, account.ApiKey, now, deadline.Token);
            try { coding = await Task.Run(session.RunAsync, deadline.Token); _zcodeCoding = (codingOwner, coding, now); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { failure = ex; }
        }
        (string Label, string Status)? codingFailure = failure is null || session is null ? null : session.FailureStatus switch
        {
            401 => (Loc.T("登录已失效", "Sign-in expired"), Loc.T("ZCode 的 Coding Plan 凭据已失效，请在 ZCode 中重新登录", "The Coding Plan credentials of ZCode have expired. Sign in again in ZCode.")),
            403 => (Loc.T("访问受限", "Access denied"), Loc.T("账户接口拒绝访问", "The account API refused access.")),
            429 => (Loc.T("请求频繁", "Too many requests"), Loc.T("请求频繁，稍后自动重试", "Too many requests. Retrying automatically later.")),
            _ => deadline.IsCancellationRequested ? (Loc.T("读取超时", "Timed out"), Loc.T("读取超时，稍后重试", "Reading timed out. Retrying later."))
                : (Loc.T("读取失败", "Read failed"), session.Redact(failure.Message))
        };
        DateTimeOffset? codingTime = coding is null ? null : now;
        if (coding is null && failure is not null && _zcodeCoding is { } kept && kept.Account == codingOwner) (coding, codingTime) = (kept.Result, kept.Time);
        var startRoot = await startTask; var mcpRoot = await mcpTask;
        ct.ThrowIfCancellationRequested();
        var start = startRoot is { } root ? ZCodeUsage.StartPlan(root, now) : (Metrics: new List<UsageMetric>(), Plan: "");
        var mcp = mcpRoot is { } mcpValue ? ZCodeUsage.McpMetric(mcpValue) : null;
        if (coding is null && start.Metrics.Count == 0 && mcp is null)
        {
            (snapshot.StatusLabel, snapshot.Status) = codingFailure ?? (startRequested && startRoot is null
                ? (Loc.T("读取超时", "Timed out"), Loc.T("ZCode 服务响应较慢，稍后自动重试", "The ZCode service is responding slowly. Retrying automatically later."))
                : (Loc.T("未连接", "Not connected"), Loc.T("ZCode 账户没有可用的 Coding Plan 或 Start Plan", "The ZCode account has no active Coding Plan or Start Plan.")));
            return;
        }
        // Rolling windows sort first by window length; the Start Plan grants precede the tool-call allowances.
        // A kept MCP result past its daily refresh stays visible and its reset time reads as pending.
        var codingMetrics = coding?.Metrics ?? [];
        List<UsageMetric> metrics = [.. codingMetrics.Where(m => m.Window is not null), .. start.Metrics, .. codingMetrics.Where(m => m.Window is null)];
        if (mcp is not null) metrics.Add(mcp);
        snapshot.Metrics = metrics;
        snapshot.Plan = coding?.Plan is { Length: > 0 } plan ? plan : start.Plan; snapshot.Scope = "plan";
        snapshot.PlanSource = Loc.T("ZCode 账户", "ZCode account");
        if (start.Plan.Length > 0) snapshot.Details["Start Plan"] = start.Plan;
        Connected(snapshot, account.Family == "zai" ? Loc.T("ZCode · Z.ai 账户", "ZCode · Z.ai account") : Loc.T("ZCode · 智谱账户", "ZCode · Zhipu account"), now);
        if (codingFailure is { } partial)
        {
            snapshot.IsStale = codingTime is not null;
            snapshot.Status = "Coding Plan " + partial.Status + (codingTime is { } time
                ? Loc.T($"，显示 {time.LocalDateTime:MM-dd HH:mm} 读取的数据", $" Showing the data read at {time.LocalDateTime:MM-dd HH:mm}.") : "");
        }
    }

    // DeepSeek Harness signs in to a DeepSeek platform account; its balance is read the way DeepSeek Harness reads it.
    // Without a signed-in account, the API key saved on its Models page is read with the DeepSeek source's script.
    private async Task ReadDshAsync(ProviderSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        var account = DshUsage.ReadAccount(_locations.DshHome);
        if (account is null || account.Token.Length == 0 && account.ApiKey.Length == 0)
        {
            snapshot.StatusLabel = Loc.T("未连接", "Not connected");
            snapshot.Status = Loc.T("请先在 DeepSeek Harness 中登录 DeepSeek 账户，或在其中保存 DeepSeek API 密钥", "Sign in to a DeepSeek account in DeepSeek Harness, or save a DeepSeek API key there.");
            return;
        }
        snapshot.Scope = "account";
        if (account.Token.Length == 0)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var session = new ScriptSession(_http, new AppEntry { QuotaSource = ProviderId.DeepSeek }, account.ApiKey, now, deadline.Token);
            ProviderResult result;
            try { result = await Task.Run(session.RunAsync, deadline.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                (snapshot.StatusLabel, snapshot.Status) = session.FailureStatus switch
                {
                    401 => (Loc.T("密钥无效", "Invalid key"), Loc.T("DeepSeek 拒绝了 DeepSeek Harness 中保存的 API 密钥，请在 DeepSeek Harness 中更新", "DeepSeek rejected the API key saved in DeepSeek Harness. Update it in DeepSeek Harness.")),
                    429 => (Loc.T("请求频繁", "Too many requests"), Loc.T("请求频繁，稍后自动重试", "Too many requests. Retrying automatically later.")),
                    _ => deadline.IsCancellationRequested ? (Loc.T("读取超时", "Timed out"), Loc.T("读取超时，稍后重试", "Reading timed out. Retrying later."))
                        : (Loc.T("读取失败", "Read failed"), session.Redact(ex.Message))
                };
                return;
            }
            snapshot.Metrics = result.Metrics;
            Connected(snapshot, Loc.T("DeepSeek Harness · DeepSeek API 密钥", "DeepSeek Harness · DeepSeek API key"), now);
            if (result.Warnings.Count > 0) snapshot.Status += Loc.T("；", "; ") + string.Join(Loc.T("；", "; "), result.Warnings);
            return;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, DshUsage.Platform + "/api/v0/users/get_user_summary");
        request.Headers.TryAddWithoutValidation("x-dsh-auth-token", account.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("BrimDeck/0.1");
        using var response = await _http.SendAsync(request, ct);
        // As in DeepSeek Harness, HTTP 401 or code 40003 means the stored sign-in is no longer accepted.
        void Expired()
        {
            snapshot.StatusLabel = Loc.T("登录已失效", "Sign-in expired");
            snapshot.Status = Loc.T("DeepSeek Harness 的登录已失效，请在 DeepSeek Harness 中重新登录", "The DeepSeek Harness sign-in has expired. Sign in again in DeepSeek Harness.");
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized) { Expired(); return; }
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.Get("code").Number() == 40003) { Expired(); return; }
        snapshot.Metrics = DshUsage.Summary(doc.RootElement) ?? throw new InvalidDataException();
        Connected(snapshot, Loc.T("DeepSeek Harness · DeepSeek 账户", "DeepSeek Harness · DeepSeek account"), now);
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("BrimDeck/0.1");
        return request;
    }
    private static StringContent JsonBody(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    private static async Task<JsonDocument> LocalCall(HttpClient client, LocalEndpoint endpoint, string method, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.Scheme}://127.0.0.1:{endpoint.Port}/exa.language_server_pb.LanguageServerService/{method}") { Content = JsonBody(body) };
        request.Headers.TryAddWithoutValidation("x-codeium-csrf-token", endpoint.Token);
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        return await SendAsync(client, request, ct);
    }
    private static async Task<JsonDocument> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
    private static JsonDocument? ReadJson(string path) => SharedFile.ReadJson(path);
    private static void Connected(ProviderSnapshot snapshot, string source, DateTimeOffset now)
    { snapshot.LiveQuota = true; snapshot.QuotaTime = now; snapshot.Source = source; snapshot.Status = Loc.T("已连接", "Connected"); snapshot.StatusLabel = Loc.T("已连接", "Connected"); }
    public void Dispose() { _configured.Dispose(); if (http is null) { _http.Dispose(); _zcodeHttp.Dispose(); } _gate.Dispose(); }
}
