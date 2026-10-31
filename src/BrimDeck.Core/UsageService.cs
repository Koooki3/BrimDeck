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

// quotaCache is the file that keeps the last Claude quota read online across restarts (ClaudeQuotaCache); null keeps none.
// kimiQuotaCache does the same for Kimi Code (KimiQuotaCache). jitter (0 to 1) is the share of a pause after a 429 that is added to it
// (RateLimitBackoff); a test passes a fixed one.
public sealed class UsageService(IDesktopSources desktop, DataLocations? locations = null, HttpClient? http = null, Func<DateTimeOffset>? clock = null, IProviderSecrets? secrets = null, Func<IReadOnlyList<WslHome>>? wsl = null, TimeSpan? wslTimeout = null, string? quotaCache = null, string? kimiQuotaCache = null, Func<double>? jitter = null) : IDisposable
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
    // WSL is read away from the refresh itself: its file system is reached over the network share, with synchronous calls that
    // cannot be cancelled and that stall while a distribution starts, stops or hangs. A read that does not finish in time
    // is skipped for that round, and no new one starts until it has ended.
    private readonly Dictionary<ProviderId, LogReader> _wslLogs = new() { [ProviderId.Claude] = new(), [ProviderId.Codex] = new() };
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ProviderId, WslRead> _wslReads = [];
    private readonly object _wslGate = new();
    // Home folders are searched again every few minutes: listing them touches the distributions' file systems.
    private (DateTimeOffset Time, IReadOnlyList<WslHome> Homes)? _wsl;
    private bool _includeWsl;
    private readonly AntigravityUsage _antigravity = new();
    private readonly DshSessions _dsh = new();
    private readonly KimiSessions _kimi = new();
    // The Kimi Code membership level, read from /me at most every few hours, for the hash of the token it was read with.
    private (string Account, string Plan, DateTimeOffset Time)? _kimiPlan;
    // The last Kimi Code quota read online and the hash of its account; kimiQuotaCache keeps it across restarts.
    private KimiQuotaReading? _kimiRetained;
    private string _kimiAccount = "", _kimiSaved = "";
    private bool _kimiCacheRead;
    private DateTimeOffset _kimiSavedTime;
    // Cursor token details by local day, for the account they were read for (the sub claim of its token). A day is read again
    // until a full read ends an hour after the day did, so records the service adds late still count; older days are kept.
    private readonly Dictionary<DateTime, (List<TokenEntry> Entries, DateTimeOffset ReadAt)> _cursorDays = [];
    private string _cursorAccount = "";
    private readonly SemaphoreSlim _gate = new(1);
    private ClaudeQuotaSnapshot? _grokBotRetained;
    // The last Claude quota read online; until this run reads its own, the previous run's (ClaudeQuotaCache), which is read on
    // the first refresh that needs it, off the UI thread, rather than while BrimDeck starts.
    // _claudeAccount is the hash of the account it was read for ("" when the sign-in did not name one). A reading is shown only
    // for the account signed in now: the file needs the same known account, while a reading of this run is set aside only
    // when both accounts are known and differ, as before for a sign-in whose records name no account.
    private ClaudeQuotaSnapshot? _claudeRetained;
    private string _claudeAccount = "";
    private bool _claudeCacheRead;
    private ClaudeQuotaSnapshot? ClaudeRetained(string account)
    {
        var hash = ClaudeQuotaCache.AccountHash(account);
        if (_claudeRetained is not null && hash.Length > 0 && _claudeAccount.Length > 0 && hash != _claudeAccount) { _claudeRetained = null; _claudeAccount = hash; }
        // The file is read once the account is known; a sign-in that names no account leaves it for a later refresh.
        if (_claudeRetained is null && !_claudeCacheRead && quotaCache is not null && hash.Length > 0)
        {
            _claudeCacheRead = true;
            _claudeRetained = ClaudeQuotaCache.Load(quotaCache, _clock(), account);
            _claudeAccount = hash;
        }
        return _claudeRetained;
    }
    // The file is written when the quotas, the plan or the account changed, and otherwise once its reading is
    // ClaudeQuotaCache.RewriteAfter old, not on every refresh (about one a minute) that read the same values. The time it
    // keeps, which the status names after a restart, is so never more than RewriteAfter behind the last successful reading.
    // A reading whose account is not known is kept for this run only: the file could not tell whose it is.
    private string _claudeSaved = "";
    private DateTimeOffset _claudeSavedTime;
    private void KeepOnline(ClaudeQuotaSnapshot snapshot, ClaudeCredential credential, ClaudePlanOrigin planFrom)
    {
        _claudeRetained = snapshot; _claudeAccount = ClaudeQuotaCache.AccountHash(credential.Account); _claudeCacheRead = true;
        if (quotaCache is null || _claudeAccount.Length == 0) return;
        var key = string.Join("|", _claudeAccount, snapshot.Plan, planFrom, credential.SignIn, string.Join(";", snapshot.Quotas));
        if (key == _claudeSaved && snapshot.Time - _claudeSavedTime < ClaudeQuotaCache.RewriteAfter) return;
        _claudeSaved = key; _claudeSavedTime = snapshot.Time;
        ClaudeQuotaCache.Save(quotaCache, snapshot, credential.Account, credential.SignIn, planFrom);
    }
    // A sign-in that is gone or rejected keeps no quota, on disk either. A sign-in that could not be read just now only drops
    // the reading of this run; the file is read again the next time a reading is needed, so once the sign-in can be read and
    // the API is still rate limited, the kept quotas show again.
    private void ForgetClaude(bool signedOut = true)
    {
        _claudeRetained = null; _claudeAccount = "";
        if (!signedOut) { _claudeCacheRead = false; return; }
        if (quotaCache is null) return;
        _claudeCacheRead = true;
        _claudeSaved = "";
        ClaudeQuotaCache.Delete(quotaCache);
    }
    // The pause after a 429 from the Claude quota API, for each token (with its sign-in and account): a new sign-in starts without
    // one, and the several tokens of one Desktop account, tried in turn, do not replace each other's. Only used while _gate is held.
    // It is not kept on disk: a start asks once, and a 429 starts the pause again.
    private readonly Dictionary<string, RateLimitBackoff> _claudeLimits = [];
    private RateLimitBackoff ClaudeLimit(ClaudeCredential credential, DateTimeOffset now)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(credential.Token)));
        var key = string.Join("|", credential.SignIn, ClaudeQuotaCache.AccountHash(credential.Account), token);
        if (_claudeLimits.TryGetValue(key, out var limit)) return limit;
        // Tokens are renewed now and then; the ones of the past have no pause left to keep.
        if (_claudeLimits.Count >= 16)
            foreach (var old in _claudeLimits.Where(pair => pair.Value.RetryAt(now) is null).Select(pair => pair.Key).ToList()) _claudeLimits.Remove(old);
        return _claudeLimits[key] = new();
    }
    private static string Clock(DateTimeOffset time, DateTimeOffset now) => time.LocalDateTime.ToString(time.LocalDateTime.Date == now.LocalDateTime.Date ? "HH:mm" : "MM-dd HH:mm");
    private readonly ConfiguredProviders _configured = new(secrets, http, clock);
    public Task<ProviderTestResult> TestProviderAsync(AppEntry entry, CancellationToken ct = default) => _configured.TestAsync(entry, ct);

    public async Task<List<ProviderSnapshot>> RefreshAsync(DeckSettings settings, CancellationToken cancellation = default, DateTime? usageStart = null)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            // Each underlying source is read once, even when several columns reuse it.
            var required = settings.RequiredProviders;
            _includeWsl = settings.IncludeWsl;
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

    private IReadOnlyList<WslHome> WslHomes(DateTimeOffset now)
    {
        lock (_wslGate)
        {
            if (_wsl is not { } found || now - found.Time > TimeSpan.FromMinutes(3) || now < found.Time) _wsl = found = (now, wsl is null ? WslLocations.Discover() : wsl());
            return found.Homes;
        }
    }

    private sealed class WslRead(Task<ProviderSnapshot?> task, string localNote, bool localAvailable)
    {
        public readonly Task<ProviderSnapshot?> Task = task;
        public readonly System.Diagnostics.Stopwatch Elapsed = System.Diagnostics.Stopwatch.StartNew();
        // The snapshot's note and availability before WSL joins it, so the WSL part is placed right after them.
        public readonly string LocalNote = localNote;
        public readonly bool LocalAvailable = localAvailable;
        public bool TimedOut;
    }

    // Starts reading the WSL statistics in the background, right after the local ones, so that the wait overlaps the network
    // requests of the same refresh instead of adding to them. A read that has not ended yet (it ran out of time, or its
    // round was cancelled) is never started a second time: the reader is not meant for two reads at once.
    private WslRead StartWsl(ProviderSnapshot snapshot, DateTimeOffset now, DateTime start)
    {
        var id = snapshot.Id;
        var read = _wslReads.GetOrAdd(id, _ => new(Task.FromResult<ProviderSnapshot?>(null), "", false));
        if (!read.Task.IsCompleted) return _wslReads[id] = new(read.Task, snapshot.UsageNote, snapshot.UsageAvailable) { TimedOut = read.TimedOut };
        return _wslReads[id] = new(Task.Run(() =>
        {
            var roots = WslHomes(now).Select(home => id == ProviderId.Codex ? home.Codex : home.Claude).Where(root => root.Length > 0).ToList();
            return roots.Count == 0 ? null : _wslLogs[id].Read(id, roots, now, start, "WSL");
        }, CancellationToken.None), snapshot.UsageNote, snapshot.UsageAvailable);
    }

    // Adds the WSL token statistics to the local ones once the rest of the refresh is done. Entries seen on both sides count once.
    // Only the time the read has not yet had is waited for; after that the round shows this PC only.
    private async Task MergeWslAsync(ProviderSnapshot snapshot, WslRead read, CancellationToken ct)
    {
        void Skipped() { snapshot.UsageNote += Loc.T(" · WSL 读取超时，本轮仅显示本机数据", " · Reading WSL timed out; this round shows this PC only"); snapshot.UsageComplete = false; }
        if (read.TimedOut && !read.Task.IsCompleted) { Skipped(); return; }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = (wslTimeout ?? TimeSpan.FromSeconds(8)) - read.Elapsed.Elapsed;
        var finished = await Task.WhenAny(read.Task, Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, wait.Token));
        wait.Cancel();
        ct.ThrowIfCancellationRequested();
        if (finished != read.Task) { read.TimedOut = true; Skipped(); return; }
        ProviderSnapshot? result;
        try { result = await read.Task; }
        catch (Exception ex) when (ex is not OperationCanceledException) { snapshot.UsageComplete = false; return; }
        if (result is null) return;
        var merged = new Dictionary<string, TokenEntry>();
        foreach (var entry in snapshot.Entries.Concat(result.Entries))
            if (!merged.TryGetValue(entry.Key, out var old) || entry.Total >= old.Total) merged[entry.Key] = entry;
        snapshot.Entries = merged.Values.OrderBy(entry => entry.Time).ToList();
        snapshot.UsageAvailable |= result.UsageAvailable;
        snapshot.UsageComplete &= result.UsageComplete;
        if (!result.UsageAvailable) return;
        var tail = snapshot.UsageNote.StartsWith(read.LocalNote, StringComparison.Ordinal) ? snapshot.UsageNote[read.LocalNote.Length..] : "";
        snapshot.UsageNote = (read.LocalAvailable ? read.LocalNote + " · " : "") + result.UsageNote + tail;
    }

    private async Task<ProviderSnapshot> FetchCoreAsync(ProviderId id, DateTime? usageStart, bool claudeOnline, CancellationToken ct)
    {
        var now = _clock();
        var start = new DateTimeOffset(usageStart?.Date ?? now.LocalDateTime.Date.AddDays(-29));
        // The reader owns a cache and is accessed serially before the first asynchronous request.
        var snapshot = id is ProviderId.Claude or ProviderId.Codex or ProviderId.Pi
            ? _logs.Read(id, id == ProviderId.Pi ? _locations.PiSessions : id == ProviderId.Codex ? _locations.CodexHome : _locations.ClaudeHome, now, start.LocalDateTime) : new ProviderSnapshot(id) { UsageStart = start.LocalDateTime.Date };
        var claudeAccount = id == ProviderId.Claude ? ReadClaudeProfile(snapshot) : "";
        var wslRead = _includeWsl && id is ProviderId.Claude or ProviderId.Codex ? StartWsl(snapshot, now, start.LocalDateTime) : null;
        if (id == ProviderId.ZCode) ZCodeUsage.Read(snapshot, desktop, _locations.ZCodeHome, start, now);
        if (id == ProviderId.Antigravity) _antigravity.Read(snapshot, desktop, _locations.AntigravityConversations, start, now);
        if (id == ProviderId.Dsh) _dsh.Read(snapshot, _locations.DshHome, start, now);
        if (id == ProviderId.Kimi) _kimi.Read(snapshot, _locations.KimiHome, start, now);
        if (id == ProviderId.OpenCode) OpenCodeUsage.Read(snapshot, desktop, _locations.OpenCodeDatabase, start, now);
        try
        {
            switch (id)
            {
                case ProviderId.Pi:
                    snapshot.UsageNote += Loc.T(" · 费用取自 Pi 记录，非订阅账单", " · Costs recorded by Pi, not subscription charges");
                    snapshot.StatusLabel = Loc.T("仅本地统计", "Local usage only");
                    snapshot.Status = Loc.T("Pi 仅统计本机会话的 Token 与费用，无在线额度", "Pi tracks local session tokens and costs; no online quota.");
                    break;
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
                    if (claudeOnline) await ReadClaudeAsync(snapshot, claudeAccount, now, ct);
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
                case ProviderId.Kimi:
                    await ReadKimiAsync(snapshot, now, ct);
                    break;
                case ProviderId.OpenCode:
                    await ReadOpenCodeAsync(snapshot, now, ct);
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
                HttpStatusCode.TooManyRequests => ex is RateLimitedException { RetryAt: { } at }
                    ? Loc.T($"请求频繁，{Clock(at, now)} 后自动重试", $"Too many requests. Retrying automatically after {Clock(at, now)}.")
                    : Loc.T("请求频繁，稍后自动重试", "Too many requests. Retrying automatically later."),
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
        if (wslRead is not null) await MergeWslAsync(snapshot, wslRead, ct);
        if (!snapshot.LiveQuota && snapshot.Quotas.Count > 0) snapshot.Source = Loc.T("本地配额快照 · 可能已过时", "Local quota snapshot · may be outdated");
        return snapshot;
    }

    // Returns the id of the account Claude Code is signed in to (oauthAccount.accountUuid, written by Claude Code at sign-in),
    // or "" when the record is missing or names none. It ties a kept quota to its account without a request.
    private string ReadClaudeProfile(ProviderSnapshot snapshot)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var profile = Path.Combine(_locations.ClaudeHome, ".claude.json");
            if (!File.Exists(profile) && Path.GetFullPath(_locations.ClaudeHome).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.Combine(home, ".claude"), StringComparison.OrdinalIgnoreCase)) profile = Path.Combine(home, ".claude.json");
            using var doc = ReadJson(profile);
            snapshot.Plan = PlanParser.ClaudeProfile(doc?.RootElement ?? default);
            if (snapshot.Plan.Length > 0) snapshot.PlanSource = ClaudeSources.Plan(ClaudePlanOrigin.LocalRecord, ClaudeSignIn.Code);
            return doc?.RootElement.Get("oauthAccount").Get("accountUuid").Text() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return ""; }
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

    // codeAccount is the account Claude Code's local record names (ReadClaudeProfile); the desktop app's sign-ins carry their own.
    private async Task ReadClaudeAsync(ProviderSnapshot snapshot, string codeAccount, DateTimeOffset now, CancellationToken ct)
    {
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        Exception? failure = null;
        Exception? lastFailure = null;
        ClaudeCredential? login = null;
        Task<string>? loginPlan = null;
        async Task<bool> TryCredential(ClaudeCredential credential)
        {
            ct.ThrowIfCancellationRequested();
            lastFailure = null;
            if (string.IsNullOrWhiteSpace(credential.Token) || !attempted.Add(credential.Token)) return false;
            login = credential;
            // The plan is asked at the same time as the quota, so a slow or unreachable network costs one wait, not two.
            var planRequest = loginPlan = ReadClaudePlanAsync(credential.Token, ct);
            var limit = ClaudeLimit(credential, now);
            try
            {
                // While the quota API is asking for time, it is not asked again (a manual refresh included); the failure below is
                // the same one a 429 gives, so the reading kept from before is shown with the time the next request goes out.
                if (limit.RetryAt(_clock()) is { } wait) throw RateLimitedException.Held(wait);
                using var request = Bearer(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage", credential.Token);
                request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                using var doc = await SendAsync(_http, request, ct);
                limit.Reset();
                var quotas = QuotaParser.Claude(doc.RootElement);
                if (quotas.Count == 0) throw new InvalidDataException();
                // Publish only a complete successful result, all from the same login.
                snapshot.Quotas = quotas;
                var live = await planRequest;
                ClaudePlanOrigin planFrom;
                if (live.Length > 0) { snapshot.Plan = live; planFrom = ClaudePlanOrigin.Account; }
                else
                {
                    var plan = PlanParser.ReconcileClaude(credential.Plan, snapshot.Plan);
                    // The local account record already filled Plan and PlanSource; a sign-in record only describes its own plan.
                    planFrom = plan == credential.Plan || snapshot.PlanSource.Length == 0
                        ? plan.Length > 0 ? ClaudePlanOrigin.SignIn : ClaudePlanOrigin.None : ClaudePlanOrigin.LocalRecord;
                    snapshot.Plan = plan;
                }
                snapshot.PlanSource = ClaudeSources.Plan(planFrom, credential.SignIn);
                Connected(snapshot, ClaudeSources.Quota(credential.SignIn), now);
                snapshot.UsageNote += ClaudeUsageNote;
                KeepOnline(new(now, snapshot.Plan, snapshot.PlanSource, snapshot.Source, quotas.ToList()), credential, planFrom);
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is
                HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException or FormatException)
            {
                lastFailure = ex;
                // The pause counts from the moment the answer arrived, not from the start of the round: another token may have been
                // tried first, and the request itself takes time, which a Retry-After of a minute must not be shortened by.
                if (ex is RateLimitedException { Sent: true } limited) { var received = _clock(); limited.RetryAt = limit.Fail(received, limited.RetryAfter(received), (jitter ?? Random.Shared.NextDouble)()); }
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
            if (token.Length > 0) codeCredential = new(token, plan, ClaudeSignIn.Code, codeAccount);
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
            if (failure is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }) ForgetClaude();
            else
            {
                // The reading shown in place of the failed one must belong to the account of the sign-in that was asked.
                if (Freshest(now, login?.Account ?? "") is { } local)
                {
                    // The plan stays known while the quota interface is unavailable (rate limited, for one): it is asked from the
                    // account endpoint, which a rate-limited quota request does not block, and otherwise taken from the local records.
                    if (login is not null)
                    {
                        var live = loginPlan is null ? "" : await loginPlan;
                        var plan = live.Length > 0 ? live : PlanParser.ReconcileClaude(login.Plan, snapshot.Plan);
                        var source = live.Length > 0 ? ClaudeSources.Plan(ClaudePlanOrigin.Account, login.SignIn)
                            : plan == snapshot.Plan && snapshot.PlanSource.Length > 0 ? snapshot.PlanSource : ClaudeSources.Plan(ClaudePlanOrigin.SignIn, login.SignIn);
                        if (plan.Length > 0) _claudeRetained = local = local with { Plan = plan, PlanSource = source };
                    }
                    Retain(snapshot, local, failure, now); return;
                }
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        ForgetClaude(signedOut: !desktopState.ReadFailed && !codeReadFailed);
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
    private ClaudeQuotaSnapshot? Freshest(DateTimeOffset now, string account)
    {
        var kept = ClaudeRetained(account);
        var sample = ClaudeUsageHistory.Latest(_locations);
        if (sample is not null && sample.Time <= now.AddMinutes(5) && (kept is null || sample.Time > kept.Time))
        {
            DateTimeOffset? Reset(int minutes) => kept?.Quotas.FirstOrDefault(quota => quota.Minutes == minutes)?.ResetAt is { } reset && reset > sample.Time ? reset : null;
            var scoped = kept?.Quotas.Where(q => q.Label is not ("5 小时额度" or "每周额度")).ToList() ?? [];
            var scopedTime = scoped.Count == 0 ? null : kept!.Local ? kept.ScopedTime : kept.Time;
            kept = new(sample.Time, kept?.Plan ?? "", kept?.PlanSource ?? "", Loc.T("Claude 桌面版 · 本地用量记录", "Claude desktop app · local usage records"),
                [new Quota("5 小时额度", sample.FiveHour, Reset(300), 300), new Quota("每周额度", sample.SevenDay, Reset(10080), 10080), .. scoped], true, scopedTime);
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
        var time = Clock(kept.Time, now);
        var shown = kept.Local ? Loc.T($"显示 Claude 桌面版 {time} 记录的配额", $"showing the quota the Claude desktop app recorded at {time}")
            : Loc.T($"显示 {time} 读取的配额", $"showing the quota read at {time}");
        // Per-model quotas carried over from an earlier online reading say when they were read.
        if (kept.Local && kept.ScopedTime is { } scoped && kept.Time - scoped > TimeSpan.FromMinutes(1))
        {
            var at = Clock(scoped, now);
            shown += Loc.T($"（按模型的额度为 {at} 读取）", $" (per-model quotas read at {at})");
        }
        snapshot.Status = failure switch
        {
            null => kept.Local ? Loc.T($"已连接，{shown}", $"Connected, {shown}") : Loc.T("已连接", "Connected"),
            RateLimitedException { RetryAt: { } retry } => Loc.T($"请求频繁，{shown}，{Clock(retry, now)} 后自动重试", $"Too many requests; {shown}. Retrying automatically after {Clock(retry, now)}."),
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
                var subject = CursorAccount(token);
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
        await ReadCursorEvents(snapshot, token, now, start, ct);
    }

    // The account's records can run to thousands a day, more than one refresh can read, so they are read a day at a time,
    // newest first, and every day read in full is kept. The days a refresh did not reach are read by the next ones.
    private async Task ReadCursorEvents(ProviderSnapshot snapshot, string token, DateTimeOffset now, DateTimeOffset start, CancellationToken ct)
    {
        var account = CursorAccount(token);
        // Details are shown only for the account signed in now; a token that names no account keeps nothing between refreshes.
        if (account.Length == 0 || account != _cursorAccount) { _cursorDays.Clear(); _cursorAccount = account; }
        DateTime first = start.LocalDateTime.Date, last = now.LocalDateTime.Date;
        foreach (var day in _cursorDays.Keys.Where(day => day < first || day > last).ToList()) _cursorDays.Remove(day);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            for (var day = last; day >= first; day = day.AddDays(-1))
            {
                var end = new DateTimeOffset(day.AddDays(1));
                if (_cursorDays.TryGetValue(day, out var kept) && kept.ReadAt >= end.AddHours(1)) continue;
                _cursorDays[day] = (await ReadCursorDay(token, new DateTimeOffset(day), end, budget.Token), now);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidDataException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested) { }
        int days = (int)(last - first).TotalDays + 1, missing = days - _cursorDays.Count;
        if (missing == days)
        { snapshot.UsageAvailable = false; snapshot.UsageNote = Loc.T("配额已读取，账户 Token 明细暂时不可用或尚未读取完整。", "The quota was read, but the account's token details are unavailable or not fully read yet."); return; }
        snapshot.Entries = _cursorDays.Values.SelectMany(read => read.Entries).Where(entry => entry.Time >= start && entry.Time <= now).OrderBy(entry => entry.Time).ToList();
        snapshot.UsageAvailable = true;
        snapshot.UsageComplete = missing == 0;
        if (missing > 0) snapshot.UsageNote += Loc.T($"其中 {missing} 天的明细尚未读取，将在之后的刷新中补全。", " " + Loc.Count(missing, "day", "days") + " not read yet; later refreshes fill them in.");
    }

    // One local day's records, [from, to). An empty day, or a page past the last, comes back without the list.
    private async Task<List<TokenEntry>> ReadCursorDay(string token, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var entries = new Dictionary<string, TokenEntry>();
        long rowsRead = 0;
        for (int page = 1; page <= 100; page++)
        {
            using var request = Bearer(HttpMethod.Post, "https://api2.cursor.sh/aiserver.v1.DashboardService/GetFilteredUsageEvents", token);
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Content = JsonBody(new { startDate = from.ToUnixTimeMilliseconds().ToString(), endDate = (to.ToUnixTimeMilliseconds() - 1).ToString(), page, pageSize = 1000 });
            using var response = await SendAsync(_http, request, ct);
            var events = response.RootElement.Get("usageEventsDisplay");
            if (events.ValueKind is not (JsonValueKind.Array or JsonValueKind.Undefined)) throw new InvalidDataException();
            int rows = events.ValueKind == JsonValueKind.Array ? events.GetArrayLength() : 0;
            if (rows > 0)
                foreach (var item in events.EnumerateArray())
                    if (ParseCursorEvent(item) is { } entry && entry.Time >= from && entry.Time < to) entries.TryAdd(entry.Key, entry);
            rowsRead += rows;
            var total = response.RootElement.Get("totalUsageEventsCount").Number();
            if (rows == 0 || (total is { } expected && rowsRead >= expected) || (total is null && rows < 1000)) return entries.Values.ToList();
        }
        throw new InvalidDataException();
    }

    // The account a Cursor sign-in belongs to: the sub claim of its token, read without checking the signature, since it
    // only tells accounts apart. "" when the token names none.
    private static string CursorAccount(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return "";
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        try { using var jwt = JsonDocument.Parse(Convert.FromBase64String(payload)); return jwt.RootElement.Get("sub").Text(); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return ""; }
    }

    public static TokenEntry? ParseCursorEvent(JsonElement item)
    {
        var usage = item.Get("tokenUsage");
        if (usage.ValueKind != JsonValueKind.Object || item.Get("timestamp").Date() is not { } time) return null;
        var model = item.Get("model").Text();
        var identity = item.Get("usageEventId").Text();
        if (identity.Length == 0) identity = item.Get("id").Text();
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

    // OpenCode's own sign-in to OpenCode Console reads the Go allowance of its workspace (OpenCodeUsage). The token is used as
    // it is and never renewed; an expired one is not sent. A workspace without Go is refused with HTTP 403.
    private async Task ReadOpenCodeAsync(ProviderSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        var credential = OpenCodeUsage.ReadCredential(desktop, _locations.OpenCodeDatabase, _locations.OpenCodeData);
        if (credential is null)
        {
            snapshot.StatusLabel = Loc.T("未连接", "Not connected");
            snapshot.Status = Loc.T("请先在 OpenCode 中登录 OpenCode Console", "Sign in to OpenCode Console in OpenCode first.");
            return;
        }
        if (credential.Expired(now))
        {
            snapshot.StatusLabel = Loc.T("登录已过期", "Sign-in expired");
            snapshot.Status = Loc.T("OpenCode 的登录已过期，在 OpenCode 中使用一次即可续期", "The OpenCode sign-in has expired. Using OpenCode once renews it.");
            return;
        }
        if (OpenCodeUsage.UsageAddress(credential.Server) is not { } address) throw new InvalidDataException();
        snapshot.Scope = "account";
        using var request = Bearer(HttpMethod.Get, address.AbsoluteUri, credential.Token);
        if (credential.Workspace.Length > 0) request.Headers.TryAddWithoutValidation("x-opencode-org-id", credential.Workspace);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // Cloudflare in front of opencode.ai answers 403 as well when it blocks a request; only OpenCode's own 403 means no Go.
            if ((await response.Content.ReadAsStringAsync(ct)).Contains("\"cloudflare_error\"", StringComparison.Ordinal)) throw new HttpRequestException(null, null, HttpStatusCode.ServiceUnavailable);
            snapshot.StatusLabel = Loc.T("未订阅 Go", "No Go plan");
            snapshot.Status = Loc.T("OpenCode 登录的工作区没有订阅 OpenCode Go", "The workspace OpenCode signed in to has no OpenCode Go plan.");
            return;
        }
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var quotas = OpenCodeUsage.Allowance(doc.RootElement, out bool limited);
        if (quotas.Count == 0) throw new InvalidDataException();
        snapshot.Quotas = quotas;
        Connected(snapshot, "OpenCode Go", now);
        if (limited) snapshot.Status = Loc.T("已达到 OpenCode Go 的用量上限，重置前请求会被拒绝", "The OpenCode Go limit is reached; requests are refused until it resets.");
    }

    // Kimi Code's own sign-in reads the plan quota. The token is used as it is: an expired one is not sent and never renewed,
    // since renewing would replace the sign-in Kimi Code holds. Kimi Code renews it only while in use, so most refreshes find
    // it expired; the last reading (KimiQuotaCache) then stands in, marked as old, until Kimi Code is used again. A rejected
    // token is treated the same way, since Kimi Code renews it on its next use.
    private async Task ReadKimiAsync(ProviderSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        var service = KimiUsage.Service(_locations.KimiHome, _locations.KimiEnvironment);
        var source = KimiAccount(service.Global);
        var credential = KimiUsage.ReadCredential(_locations.KimiHome, service.CredentialName);
        if (credential is null)
        {
            ForgetKimi();
            snapshot.StatusLabel = Loc.T("未连接", "Not connected"); snapshot.Status = Loc.T("请先登录 Kimi Code", "Sign in to Kimi Code first");
            return;
        }
        snapshot.Scope = "account";
        var baseUrl = service.BaseUrl;
        // The hash covers the service address, so a reading kept for one region is never shown for the other.
        var accountHash = KimiQuotaCache.AccountHash(baseUrl, credential.Account);
        if (credential.Expired(now)) { ShowKimiRetained(snapshot, accountHash, source, null, now); return; }
        var planKey = accountHash.Length > 0 ? accountHash
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(baseUrl + "\n" + credential.AccessToken)));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var cached = _kimiPlan is { } plan && plan.Account == planKey && now - plan.Time < TimeSpan.FromHours(6) ? plan.Plan : null;
        var planTask = cached is null ? ReadKimiPlanAsync(baseUrl, credential.AccessToken, deadline.Token) : Task.FromResult<string?>(cached);
        // Leaving early stops the membership request still under way.
        using var stop = new CancelOnExit(deadline);
        List<UsageMetric> metrics;
        try
        {
            using var request = Bearer(HttpMethod.Get, baseUrl + "/usages", credential.AccessToken);
            using var response = await _http.SendAsync(request, deadline.Token);
            if (response.StatusCode == HttpStatusCode.PaymentRequired) { NoKimiPlan(snapshot); return; }
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
            metrics = KimiUsage.Usages(doc.RootElement, service.Global ? "USD" : "CNY");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException or IOException or FormatException)
        {
            if (ShowKimiRetained(snapshot, accountHash, source, ex, now)) return;
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }) return;
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
            {
                snapshot.StatusLabel = Loc.T("暂不可用", "Unavailable");
                snapshot.Status = Loc.T("未找到 Kimi Code 用量接口，请检查 Kimi Code 的服务地址", "The Kimi Code usage API was not found. Check the service address Kimi Code uses.");
                return;
            }
            throw;
        }
        if (metrics.Count == 0) { NoKimiPlan(snapshot); return; }
        var name = await planTask;
        if (name is not null && cached is null) _kimiPlan = (planKey, name, now);
        snapshot.Metrics = metrics;
        snapshot.Plan = name ?? "";
        if (snapshot.Plan.Length > 0) snapshot.PlanSource = source;
        Connected(snapshot, source, now);
        KeepKimi(new(now, snapshot.Plan, metrics), accountHash);
    }

    // As with ZCode's two regions, the account names its service: kimi.com in mainland China, kimi.ai elsewhere.
    private static string KimiAccount(bool global) => KimiUsage.Source("Kimi Code", global);

    // An account without a Kimi Code plan has no quota to keep.
    private void NoKimiPlan(ProviderSnapshot snapshot)
    {
        ForgetKimi();
        snapshot.StatusLabel = Loc.T("未订阅", "No plan");
        snapshot.Status = Loc.T("未订阅 Kimi Code 套餐", "No Kimi Code plan");
    }

    // The last reading, for the account signed in now: read from the file once that account is known. A reading of another
    // known account is set aside.
    private KimiQuotaReading? KimiRetained(string accountHash, DateTimeOffset now)
    {
        if (accountHash.Length > 0 && _kimiAccount.Length > 0 && accountHash != _kimiAccount) { _kimiRetained = null; _kimiCacheRead = false; }
        if (_kimiRetained is null && !_kimiCacheRead && kimiQuotaCache is not null && accountHash.Length > 0)
        {
            _kimiCacheRead = true;
            _kimiRetained = KimiQuotaCache.Load(kimiQuotaCache, now, accountHash);
            _kimiAccount = accountHash;
        }
        return _kimiRetained is null ? null : KimiQuotaCache.Usable(_kimiRetained, now);
    }

    // The file is written when the reading changed, and otherwise once it is RewriteAfter old. A reading whose account is not
    // known is kept for this run only.
    private void KeepKimi(KimiQuotaReading reading, string accountHash)
    {
        _kimiRetained = reading; _kimiAccount = accountHash; _kimiCacheRead = true;
        if (kimiQuotaCache is null || accountHash.Length == 0) return;
        var key = KimiQuotaCache.Serialize(reading with { Time = default }, accountHash);
        if (key == _kimiSaved && reading.Time - _kimiSavedTime < KimiQuotaCache.RewriteAfter) return;
        _kimiSaved = key; _kimiSavedTime = reading.Time;
        KimiQuotaCache.Save(kimiQuotaCache, reading, accountHash);
    }

    private void ForgetKimi()
    {
        _kimiRetained = null; _kimiAccount = ""; _kimiSaved = "";
        if (kimiQuotaCache is null) return;
        _kimiCacheRead = true;
        KimiQuotaCache.Delete(kimiQuotaCache);
    }

    // Shows the last reading in place of one that could not be taken: failure is null when the token had expired and no request
    // was sent. Without a reading, an expired or rejected sign-in waits for Kimi Code to renew it; other failures return false.
    private bool ShowKimiRetained(ProviderSnapshot snapshot, string accountHash, string source, Exception? failure, DateTimeOffset now)
    {
        bool renew = failure is null or HttpRequestException { StatusCode: HttpStatusCode.Unauthorized };
        if (KimiRetained(accountHash, now) is not { } kept)
        {
            if (!renew) return false;
            snapshot.StatusLabel = Loc.T("等待更新", "Waiting for update");
            snapshot.Status = Loc.T("使用 Kimi Code 后自动更新", "Updates automatically after Kimi Code is used");
            return true;
        }
        snapshot.IsStale = true;
        snapshot.Metrics = kept.Metrics.ToList();
        if (kept.Plan.Length > 0) { snapshot.Plan = kept.Plan; snapshot.PlanSource = source; }
        snapshot.LiveQuota = true; snapshot.QuotaTime = kept.Time; snapshot.Source = source;
        var time = kept.Time.LocalDateTime.ToString(kept.Time.LocalDateTime.Date == now.LocalDateTime.Date ? "HH:mm" : "MM-dd HH:mm");
        var shown = Loc.T($"显示 {time} 读取的额度", $"showing the allowance read at {time}");
        (snapshot.StatusLabel, snapshot.Status) = failure switch
        {
            _ when renew => (Loc.T("等待更新", "Waiting for update"), Loc.T($"使用 Kimi Code 后自动更新；{shown}", $"Updates automatically after Kimi Code is used; {shown}")),
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => (Loc.T("稍后重试", "Retrying later"), Loc.T($"请求频繁，{shown}，稍后自动重试", $"Too many requests; {shown}. Retrying automatically later.")),
            OperationCanceledException => (Loc.T("稍后重试", "Retrying later"), Loc.T($"读取超时，{shown}，稍后自动重试", $"Reading timed out; {shown}. Retrying automatically later.")),
            InvalidDataException or JsonException or FormatException => (Loc.T("稍后重试", "Retrying later"), Loc.T($"数据格式发生变化，{shown}", $"The data format has changed; {shown}.")),
            IOException => (Loc.T("稍后重试", "Retrying later"), Loc.T($"暂时无法读取数据，{shown}，稍后自动重试", $"The data cannot be read right now; {shown}. Retrying automatically later.")),
            _ => (Loc.T("稍后重试", "Retrying later"), Loc.T($"账户接口暂时不可用，{shown}，稍后自动重试", $"The account API is temporarily unavailable; {shown}. Retrying automatically later."))
        };
        return true;
    }

    private sealed class CancelOnExit(CancellationTokenSource source) : IDisposable { public void Dispose() => source.Cancel(); }

    // The membership level only labels the column, so a failed read leaves the label out instead of failing the quota.
    private async Task<string?> ReadKimiPlanAsync(string baseUrl, string token, CancellationToken ct)
    {
        try
        {
            using var request = Bearer(HttpMethod.Get, baseUrl + "/me", token);
            using var doc = await SendAsync(_http, request, ct);
            return KimiUsage.Plan(doc.RootElement) is { Length: > 0 } plan ? plan : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException) { return null; }
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
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw RateLimitedException.From(response);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
    private static JsonDocument? ReadJson(string path) => SharedFile.ReadJson(path);
    private static void Connected(ProviderSnapshot snapshot, string source, DateTimeOffset now)
    { snapshot.LiveQuota = true; snapshot.QuotaTime = now; snapshot.Source = source; snapshot.Status = Loc.T("已连接", "Connected"); snapshot.StatusLabel = Loc.T("已连接", "Connected"); }
    public void Dispose() { _configured.Dispose(); if (http is null) { _http.Dispose(); _zcodeHttp.Dispose(); } _gate.Dispose(); }
}
