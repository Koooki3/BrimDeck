using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

// Prices are USD per token, exactly as returned by the remote catalog.
public sealed record TokenPrices(decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite, decimal? CacheWriteHour);
public sealed record PriceTier(long MinimumPromptTokens, TokenPrices Prices);
public sealed record ModelPrice(string Model, TokenPrices Prices, IReadOnlyList<PriceTier> Tiers);
public enum RecoveryStatus { Recovered, TooFewRecords, Undetermined, Inconsistent }
// Records is the number of records with an API amount; Needed is how many the fit requires.
public sealed record PriceRecovery(RecoveryStatus Status, TokenPrices? Prices, int Records, int Needed);
public sealed record CostSummary(decimal Amount, int Reported, int Estimated, int Unpriced)
{
    public bool HasValue => Reported + Estimated > 0;
    public bool IsEstimate => Estimated > 0;
}

public sealed class Pricing : IDisposable
{
    public const string Source = "https://openrouter.ai/api/v1/models";
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cacheFile;
    private readonly string _manualFile;
    private readonly string _officialFile;
    private readonly object _sync = new();
    private readonly Dictionary<string, ModelPrice> _remote = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ModelPrice> _manual = new(StringComparer.OrdinalIgnoreCase);
    // Kimi Code models priced at Moonshot AI's own endpoint, by catalog ID (see Moonshot below).
    private readonly Dictionary<string, ModelPrice> _official = new(StringComparer.OrdinalIgnoreCase);
    // Catalog entries for those models that still carry the catalog's own price, which Kimi Code records do not use.
    private HashSet<ModelPrice> _unofficial = new(ReferenceEqualityComparer.Instance);
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1);
    private DateTimeOffset? _lastAttempt;
    private Dictionary<string, ModelPrice> _lookup = new(StringComparer.OrdinalIgnoreCase);
    // Per model family ("gemini", "claude-opus", "gpt-oss"), the words that follow the version in catalog IDs, such as "lite" in
    // "gemini-3.5-flash-lite". They name distinct priced models, so the Antigravity fallback never drops them.
    private Dictionary<string, HashSet<string>> _tiers = new(StringComparer.Ordinal);
    // Each refresh prices thousands of records that share a few model names; results are kept until the catalog changes.
    private System.Collections.Concurrent.ConcurrentDictionary<string, ModelPrice?> _found = new(StringComparer.Ordinal);
    public int ModelCount { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public string? LastError { get; private set; }
    public string? ManualLoadWarning { get; private set; }
    public event Action? Changed;
    public IReadOnlyList<ModelPrice> Models
    {
        get { lock (_sync) return MergedModels().Values.OrderBy(x => x.Model, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }
    public bool IsManual(string model) { lock (_sync) return _manual.ContainsKey(Normalize(model)); }
    public bool IsStale => UpdatedAt is null || _now() - UpdatedAt >= RefreshInterval;

    public Pricing(string directory, HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        _cacheFile = Path.Combine(directory, "model-prices.json");
        _manualFile = Path.Combine(directory, "manual-model-prices.json");
        _officialFile = Path.Combine(directory, OfficialFile);
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttp = http is null; _now = now ?? (() => DateTimeOffset.UtcNow);
        try
        {
            if (File.Exists(_cacheFile))
            {
                using var cache = JsonDocument.Parse(File.ReadAllText(_cacheFile));
                var timestamp = cache.RootElement.Get("updatedAt").Date();
                if (timestamp is null || timestamp > _now().AddMinutes(5)) throw new InvalidDataException();
                var stored = cache.RootElement.Get("models");
                var models = stored.ValueKind == JsonValueKind.Array
                    ? stored.Deserialize<List<ModelPrice>>()?.Where(ValidModel).ToArray() ?? []
                    : Parse(cache.RootElement.Get("catalog"));
                if (models.Count == 0) throw new InvalidDataException();
                SetCatalog(models, timestamp.Value);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        { LastError = Loc.T("价格缓存不可用", "The price cache is unavailable"); }
        try
        {
            if (File.Exists(_manualFile))
            {
                var models = JsonSerializer.Deserialize<List<ModelPrice>>(File.ReadAllText(_manualFile));
                if (models is null || models.Any(m => !ValidModel(m)) || models.Select(m => Normalize(m.Model)).Distinct().Count() != models.Count)
                    throw new InvalidDataException();
                _manual = models.ToDictionary(m => Normalize(m.Model), StringComparer.OrdinalIgnoreCase);
                RebuildLookup();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        { ManualLoadWarning = Loc.T("无法读取手动模型价格，原文件已保留。", "The prices added by hand could not be read; the original file is kept. "); }
        try
        {
            if (File.Exists(_officialFile))
            {
                using var saved = JsonDocument.Parse(File.ReadAllText(_officialFile));
                var models = saved.RootElement.Get("models").Deserialize<List<ModelPrice>>() ?? [];
                lock (_sync)
                {
                    foreach (var model in models.Where(m => ValidModel(m) && OfficialIds.Contains(Normalize(m.Model)))) _official[Normalize(model.Model)] = model;
                    RebuildLookup();
                }
            }
        }
        // Without saved prices, those models stay unpriced until the next update.
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { }
    }

    // The first call in each process fetches online even when a disk cache exists.
    public async Task RefreshAsync(bool force = false, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var now = _now();
            if (!force && _lastAttempt is { } previous && now - previous < RefreshInterval) return;
            _lastAttempt = now;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Source);
                request.Headers.UserAgent.ParseAdd("BrimDeck/0.1.0");
                using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
                using var catalog = JsonDocument.Parse(json);
                var models = Parse(catalog.RootElement);
                var updated = _now();
                SetCatalog(models, updated); LastError = null;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                    var temporary = _cacheFile + ".tmp";
                    string payload;
                    lock (_sync) payload = JsonSerializer.Serialize(new { updatedAt = updated, models = _remote.Values.ToArray() });
                    await File.WriteAllTextAsync(temporary, payload, cancellation).ConfigureAwait(false);
                    File.Move(temporary, _cacheFile, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = Loc.T("价格已更新，缓存保存失败", "Prices were updated, but the cache could not be saved"); }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidDataException)
            { LastError = ModelCount > 0 ? Loc.T("更新失败，使用缓存价格", "Update failed; cached prices are used") : Loc.T("价格暂不可用", "Prices are not available yet"); }
            await RefreshOfficialAsync(cancellation).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // Kimi Code bills its models at Moonshot AI's list prices, but a catalog entry carries the price of whichever provider is
    // cheapest (for Kimi K3, a quantized third-party host). So Kimi K3 and K3.1 Pro take the prices of the Moonshot AI endpoint
    // from https://openrouter.ai/api/v1/models/{id}/endpoints, which also lists K3.1 Pro Highspeed as a Moonshot AI endpoint
    // of K3.1 Pro tagged "highspeed". These prices replace the catalog entry, are added when the catalog has no entry, and are
    // kept on disk. A failed or empty answer keeps the prices saved before; without any, Kimi Code records of these models stay
    // unpriced rather than taking the catalog price. Prices added by hand still win.
    public const string OfficialFile = "moonshot-prices.json";
    public const string OfficialProvider = "Moonshot AI";
    public static readonly IReadOnlyList<string> OfficialModels = ["moonshotai/kimi-k3", "moonshotai/kimi-k3.1-pro"];
    private static readonly HashSet<string> OfficialIds = OfficialModels.SelectMany(m => new[] { m, m + "-highspeed" }).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public static string EndpointsSource(string model) => $"https://openrouter.ai/api/v1/models/{model}/endpoints";

    private async Task RefreshOfficialAsync(CancellationToken cancellation)
    {
        var found = new List<ModelPrice>();
        foreach (var model in OfficialModels)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, EndpointsSource(model));
                request.Headers.UserAgent.ParseAdd("BrimDeck/0.1.0");
                using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
                found.AddRange(ParseOfficial(doc.RootElement, model));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException) { }
        }
        if (found.Count == 0) return;
        string payload;
        lock (_sync)
        {
            // A model whose answer named no Moonshot AI endpoint keeps its earlier price.
            foreach (var group in found.GroupBy(m => OfficialBase(m.Model)))
            {
                foreach (var key in _official.Keys.Where(k => OfficialBase(k) == group.Key).ToList()) _official.Remove(key);
                foreach (var model in group) _official[Normalize(model.Model)] = model;
            }
            RebuildLookup();
            payload = JsonSerializer.Serialize(new { updatedAt = _now(), models = _official.Values.ToArray() });
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_officialFile)!);
            var temporary = _officialFile + ".tmp";
            await File.WriteAllTextAsync(temporary, payload, cancellation).ConfigureAwait(false);
            File.Move(temporary, _officialFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static string OfficialBase(string model)
    {
        var key = Normalize(model);
        return key.EndsWith("-highspeed", StringComparison.Ordinal) ? key[..^"-highspeed".Length] : key;
    }

    // The Moonshot AI endpoints of one model: the one tagged "highspeed" prices "<model>-highspeed"; one whose tag names only a
    // quantization ("moonshotai/mxfp4") prices the model itself. Other tags (a batch tier, say) are left out. The first wins.
    public static IReadOnlyList<ModelPrice> ParseOfficial(JsonElement root, string model)
    {
        var prices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in root.Get("data").Get("endpoints").Items())
        {
            if (!string.Equals(endpoint.Get("provider_name").Text().Trim(), OfficialProvider, StringComparison.OrdinalIgnoreCase)) continue;
            var tag = endpoint.Get("tag").Text().ToLowerInvariant();
            var words = tag[(tag.IndexOf('/') + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (!tag.Contains('/')) words = [];
            string? id = words.Contains("highspeed") ? model + "-highspeed"
                : words.All(word => Regex.IsMatch(word, @"^(mx)?(fp|int|bf)\d+$|^unknown$")) ? model : null;
            if (id is null || prices.ContainsKey(id) || ReadModel(id, endpoint.Get("pricing")) is not { } price) continue;
            prices[id] = price;
        }
        return prices.Values.ToArray();
    }

    public static IReadOnlyList<ModelPrice> Parse(JsonElement catalog)
    {
        var models = new List<ModelPrice>();
        foreach (var item in catalog.Get("data").Items())
        {
            var id = item.Get("id").Text();
            if (string.IsNullOrWhiteSpace(id) || !id.Contains('/') || ReadModel(id, item.Get("pricing")) is not { } model) continue;
            models.Add(model);
        }
        if (models.Count == 0) throw new InvalidDataException("No usable model prices.");
        return models;
    }
    private static ModelPrice? ReadModel(string id, JsonElement pricing)
    {
        if (ReadPrice(pricing, "prompt") is not { } input || ReadPrice(pricing, "completion") is not { } output) return null;
        var prices = new TokenPrices(input, output, ReadPrice(pricing, "input_cache_read"), ReadPrice(pricing, "input_cache_write"), ReadPrice(pricing, "input_cache_write_1h"));
        var tiers = new List<PriceTier>();
        foreach (var tier in pricing.Get("overrides").Items())
        {
            if (tier.Get("min_prompt_tokens").Number() is not { } threshold || threshold < 0 || threshold > 1e15) continue;
            tiers.Add(new((long)threshold, new(ReadPrice(tier, "prompt") ?? input, ReadPrice(tier, "completion") ?? output,
                ReadPrice(tier, "input_cache_read") ?? prices.CacheRead, ReadPrice(tier, "input_cache_write") ?? prices.CacheWrite,
                ReadPrice(tier, "input_cache_write_1h") ?? prices.CacheWriteHour)));
        }
        return new(id, prices, tiers.OrderBy(x => x.MinimumPromptTokens).ToArray());
    }
    private static decimal? ReadPrice(JsonElement pricing, string name)
    {
        var field = pricing.Get(name);
        string? value = field.ValueKind == JsonValueKind.Number ? field.GetRawText() : field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var price) && price is >= 0 and <= 1_000 ? price : null;
    }
    private static string Normalize(string model)
    {
        var value = model.Trim().ToLowerInvariant();
        return value.StartsWith("claude-", StringComparison.Ordinal) || value.StartsWith("anthropic/claude-", StringComparison.Ordinal)
            ? Regex.Replace(value, @"(?<=\d)\.(?=\d)", "-") : value;
    }
    private void SetCatalog(IReadOnlyList<ModelPrice> models, DateTimeOffset updated)
    {
        lock (_sync)
        {
            // Remote updates are incremental; manually edited entries live separately and always win.
            foreach (var model in models) _remote[Normalize(model.Model)] = model;
            UpdatedAt = updated; RebuildLookup();
        }
    }
    private void RebuildLookup()
    {
        var merged = MergedModels();
        var models = merged.Values.ToArray();
        var lookup = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models) lookup[Normalize(model.Model)] = model;
        foreach (var alias in models.GroupBy(x => Normalize(x.Model[(x.Model.IndexOf('/') + 1)..])))
            if (alias.Count() == 1) lookup.TryAdd(alias.Key, alias.Single());
        foreach (var remote in _remote.Values)
            if (_manual.TryGetValue(Normalize(remote.Model[(remote.Model.IndexOf('/') + 1)..]), out var manual))
                lookup.TryAdd(Normalize(remote.Model), manual);
        var tiers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var id = Normalize(model.Model); id = id[(id.IndexOf('/') + 1)..];
            if (id.IndexOf(':') is var colon and >= 0) id = id[..colon];
            var segments = id.Split('-');
            if (Shape(segments) is not { } shape) continue;
            if (!tiers.TryGetValue(shape.Family, out var words)) tiers[shape.Family] = words = new(StringComparer.Ordinal);
            foreach (var word in segments[(shape.Version + 1)..]) if (word.Length > 0 && !char.IsDigit(word[0])) words.Add(word);
        }
        // The table is replaced before the cache, so a concurrent Find never stores an old answer in the new cache.
        var unofficial = new HashSet<ModelPrice>(ReferenceEqualityComparer.Instance);
        foreach (var model in models)
            if (OfficialIds.Contains(Normalize(model.Model)) && _remote.TryGetValue(Normalize(model.Model), out var remote) && ReferenceEquals(remote, model)) unofficial.Add(model);
        _unofficial = unofficial;
        _lookup = lookup; _tiers = tiers; _found = new(StringComparer.Ordinal); ModelCount = models.Length;
        Changed?.Invoke();
    }
    private Dictionary<string, ModelPrice> MergedModels()
    {
        var merged = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in _remote)
        {
            var bare = Normalize(model.Value.Model[(model.Value.Model.IndexOf('/') + 1)..]);
            // A manually added bare model ID also protects the same model when it later arrives online.
            if (!_manual.ContainsKey(model.Key) && !_manual.ContainsKey(bare)) merged[model.Key] = model.Value;
        }
        foreach (var model in _official)
            if (!_manual.ContainsKey(model.Key) && !_manual.ContainsKey(Normalize(model.Value.Model[(model.Value.Model.IndexOf('/') + 1)..]))) merged[model.Key] = model.Value;
        foreach (var model in _manual) merged[model.Key] = model.Value;
        return merged;
    }
    private static bool ValidPrices(TokenPrices? prices) => prices is not null &&
        prices.Input is >= 0 and <= 1000 && prices.Output is >= 0 and <= 1000 &&
        (prices.CacheRead is null or >= 0 and <= 1000) && (prices.CacheWrite is null or >= 0 and <= 1000) &&
        (prices.CacheWriteHour is null or >= 0 and <= 1000);
    private static bool ValidModel(ModelPrice? model) => model is not null && !string.IsNullOrWhiteSpace(model.Model) &&
        ValidPrices(model.Prices) && model.Tiers is not null && model.Tiers.All(t => t is not null && t.MinimumPromptTokens >= 0 && ValidPrices(t.Prices));

    // Prices remain per-token internally; conversion from per-million happens only at the settings boundary.
    public bool TrySetManual(string model, TokenPrices prices, bool replaceExisting, out string? error)
    {
        error = null; model = model.Trim();
        if (!Regex.IsMatch(model, @"^[A-Za-z0-9][A-Za-z0-9._:/+\-]{0,199}$"))
        { error = Loc.T("请输入有效的模型 ID（仅限字母、数字及 . _ : / + -）。", "Enter a valid model ID (letters, digits and . _ : / + - only)."); return false; }
        if (!ValidPrices(prices)) { error = Loc.T("单价必须为零或正数，且不能超出支持范围。", "Prices must be zero or positive and within the supported range."); return false; }
        lock (_sync)
        {
            if (ManualLoadWarning is not null) { error = ManualLoadWarning + Loc.T("请先修复该文件。", "Repair that file first."); return false; }
            var key = Normalize(model);
            var match = Find(model) ?? _manual.GetValueOrDefault(Normalize(model[(model.IndexOf('/') + 1)..]));
            if (!replaceExisting && match is not null) { error = Loc.T("该模型已存在，请编辑现有模型。", "This model already exists. Edit the existing model instead."); return false; }
            if (replaceExisting && (match is null || Normalize(match.Model) != key))
            { error = Loc.T("模型已发生变化，请重新打开编辑窗口。", "The model has changed. Open the editor again."); return false; }
            // A manual price is a flat price and is not silently superseded by remote context tiers.
            var next = new Dictionary<string, ModelPrice>(_manual, StringComparer.OrdinalIgnoreCase)
            { [key] = new(model, prices, []) };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_manualFile)!);
                var temporary = _manualFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(next.Values.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, _manualFile, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { error = Loc.T("无法写入手动价格，请检查设置目录权限。", "The prices added by hand could not be written. Check the permissions of the settings folder."); return false; }
            _manual = next; RebuildLookup(); return true;
        }
    }
    public ModelPrice? Find(string model)
    {
        var found = _found; var lookup = _lookup;
        if (found.TryGetValue(model, out var known)) return known;
        string key = Normalize(model);
        // Only a dated snapshot can fall back to its exact base model. Variants remain distinct.
        string undated = Regex.Replace(key, @"-\d{8}$", "");
        var price = lookup.TryGetValue(key, out var exact) ? exact : undated != key && lookup.TryGetValue(undated, out var dated) ? dated : null;
        found[model] = price;
        return price;
    }
    // The catalog or manual model whose prices apply to a record.
    public ModelPrice? Match(TokenEntry entry)
    {
        var model = Find(entry.Model);
        if (model is null && entry.Key.StartsWith("ag:", StringComparison.Ordinal))
            model = FindAntigravityBase(entry.Model);
        else if (entry.Key.StartsWith("kimi:", StringComparison.Ordinal))
        {
            if (model is null && KimiUsage.PriceModel(entry.Model) is { } priced) model = Find(priced);
            if (model is not null && _unofficial.Contains(model)) model = null;
        }
        return model;
    }
    // Blank cache prices in a manual entry: a cache read costs nothing, a cache write costs the input price, and a
    // one-hour cache write costs the cache write price. Remote entries keep a missing cache price as unknown, so
    // their records stay unpriced rather than guessed.
    public TokenPrices Effective(ModelPrice model, TokenPrices prices)
    {
        if (!_manual.TryGetValue(Normalize(model.Model), out var manual) || !ReferenceEquals(manual, model)) return prices;
        var write = prices.CacheWrite ?? prices.Input;
        return prices with { CacheRead = prices.CacheRead ?? 0, CacheWrite = write, CacheWriteHour = prices.CacheWriteHour ?? write };
    }
    public decimal? Estimate(TokenEntry entry)
    {
        if (entry.ReportedCostUsd is { } reported) return reported;
        if (Match(entry) is not { } model) return null;
        long prompt = entry.Input + entry.CacheRead + entry.CacheWrite + entry.CacheWriteHour;
        var prices = Effective(model, model.Tiers.LastOrDefault(t => prompt >= t.MinimumPromptTokens)?.Prices ?? model.Prices);
        if ((entry.CacheRead > 0 && prices.CacheRead is null) || (entry.CacheWrite > 0 && prices.CacheWrite is null) || (entry.CacheWriteHour > 0 && prices.CacheWriteHour is null)) return null;
        return entry.Input * prices.Input + entry.Output * prices.Output + entry.CacheRead * (prices.CacheRead ?? 0) +
            entry.CacheWrite * (prices.CacheWrite ?? 0) + entry.CacheWriteHour * (prices.CacheWriteHour ?? 0);
    }
    // Cursor reports the amount of each record but not the unit prices behind it. The amount is the token counts
    // times fixed per-category prices, so a least-squares fit over a model's records recovers those prices. The fit is
    // kept only when it reproduces every record's amount, which rejects tiered or otherwise non-linear pricing and
    // sets with too few records to fix each price. A category without tokens has no recoverable price and stays null.
    public static PriceRecovery FromReported(IEnumerable<TokenEntry> source)
    {
        var entries = source.Where(t => t.ReportedCostUsd is not null && t.Total > 0).ToArray();
        Func<TokenEntry, long>[] counts = [t => t.Input, t => t.Output, t => t.CacheRead, t => t.CacheWrite, t => t.CacheWriteHour];
        var used = Enumerable.Range(0, counts.Length).Where(c => entries.Any(t => counts[c](t) > 0)).ToArray();
        int k = used.Length, needed = k + 3;
        PriceRecovery Failed(RecoveryStatus status) => new(status, null, entries.Length, needed);
        if (entries.Length < needed) return Failed(RecoveryStatus.TooFewRecords);
        if (!used.Contains(0) || !used.Contains(1)) return Failed(RecoveryStatus.Undetermined);
        // Each column is scaled to at most 1, so the normal equations stay well conditioned.
        var scale = used.Select(c => (double)entries.Max(t => counts[c](t))).ToArray();
        var a = new double[k, k + 1];
        foreach (var entry in entries)
        {
            var x = used.Select((c, i) => counts[c](entry) / scale[i]).ToArray();
            double y = (double)entry.ReportedCostUsd!.Value;
            for (int i = 0; i < k; i++)
            {
                for (int j = 0; j < k; j++) a[i, j] += x[i] * x[j];
                a[i, k] += x[i] * y;
            }
        }
        double largest = Enumerable.Range(0, k).Max(i => a[i, i]);
        for (int p = 0; p < k; p++)
        {
            int best = p;
            for (int r = p + 1; r < k; r++) if (Math.Abs(a[r, p]) > Math.Abs(a[best, p])) best = r;
            for (int c = 0; c <= k; c++) (a[p, c], a[best, c]) = (a[best, c], a[p, c]);
            if (Math.Abs(a[p, p]) < 1e-10 * largest) return Failed(RecoveryStatus.Undetermined);
            for (int r = 0; r < k; r++)
                if (r != p) { double f = a[r, p] / a[p, p]; for (int c = p; c <= k; c++) a[r, c] -= f * a[p, c]; }
        }
        var prices = new double[k];
        for (int i = 0; i < k; i++)
        {
            prices[i] = a[i, k] / a[i, i] / scale[i];
            if (prices[i] < -1e-12) return Failed(RecoveryStatus.Inconsistent);
        }
        // Cursor gives amounts to 1e-7 USD; a millionth of a dollar leaves room for that rounding and nothing more.
        foreach (var entry in entries)
        {
            double predicted = used.Select((c, i) => counts[c](entry) * prices[i]).Sum(), actual = (double)entry.ReportedCostUsd!.Value;
            if (Math.Abs(predicted - actual) > 1e-6 + 1e-6 * actual) return Failed(RecoveryStatus.Inconsistent);
        }
        decimal? Price(int category) => Array.IndexOf(used, category) is var i and >= 0 ? (decimal)Math.Round(Math.Max(0, prices[i]) * 1e6, 6) / 1_000_000m : null;
        return new(RecoveryStatus.Recovered, new(Price(0)!.Value, Price(1)!.Value, Price(2), Price(3), Price(4)), entries.Length, needed);
    }
    public CostSummary Summarize(IEnumerable<TokenEntry> entries)
    {
        decimal amount = 0; int reported = 0, estimated = 0, unpriced = 0;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Model) && entry.Total == 0 && entry.ReportedCostUsd is null) continue;
            if (Estimate(entry) is not { } price) { unpriced++; continue; }
            amount += price;
            if (entry.ReportedCostUsd is not null) reported++; else estimated++;
        }
        return new(amount, reported, estimated, unpriced);
    }
    // Antigravity adds its own words to a model name: reasoning levels and other qualifiers, such as "claude-opus-5-5-high",
    // "claude-opus-4-6-thinking", "gpt-oss-120b-medium" (names seen in Antigravity records, confirmed by the user) and
    // "gemini-3.8-flash-control". The catalog lists only base models, so a name without its own price takes the price of the
    // catalog model whose ID is its longest prefix ending at a "-": "gemini-3.8-flash-control" → "google/gemini-3.8-flash".
    // Exact catalog and manual entries are tried first by the caller, and the fallback applies to Antigravity records only.
    //   - The prefix keeps the family and its version: "claude-opus" or "gemini" alone never matches.
    //   - A cut directly before a number is skipped, so "claude-opus-4" never prices "claude-opus-4-7-thinking" or "claude-opus-4-6x".
    //   - The longest prefix wins, so "gemini-3.5-flash-lite-low" keeps the Flash-Lite price.
    //   - If a dropped word names a distinct model of the same family in the catalog (a tier word, such as "lite" in
    //     "gemini-3.5-flash-lite" or "mini" in "gpt-5-mini"), the name stays unpriced instead of taking a cheaper or dearer base.
    //   - A Gemini Pro model that the catalog lists only as a preview: "gemini-3.1-pro-low" → "google/gemini-3.1-pro-preview".
    // Antigravity writes Claude versions as "4-6" and the catalog as "4.6"; Normalize turns both into "4-6".
    // Models offered: https://antigravity.google/docs/models; IDs: https://antigravity.google/docs/enterprise
    private ModelPrice? FindAntigravityBase(string model)
    {
        var key = Normalize(model);
        var vendor = key[..(key.IndexOf('/') + 1)];
        var segments = key[vendor.Length..].Split('-');
        if (Shape(segments) is not { } shape) return null;
        var tiers = _tiers;
        for (int length = segments.Length; length > shape.Version; length--)
        {
            if (length < segments.Length && (segments[length].Length == 0 || char.IsDigit(segments[length][0]))) continue;
            var candidate = vendor + string.Join('-', segments[..length]);
            var price = length < segments.Length ? Find(candidate) : null;
            if (price is null && Regex.IsMatch(candidate, @"^(google/)?gemini-\d[\d.]*-pro$")) price = Find(candidate + "-preview");
            if (price is null) continue;
            return tiers.TryGetValue(shape.Family, out var words) && segments[length..].Any(words.Contains) ? null : price;
        }
        return null;
    }
    // A model family is the words before the version, the first later word that starts with a digit:
    // "gemini-3.5-flash-lite" → ("gemini", 1), "claude-opus-4-6" → ("claude-opus", 2), "gpt-oss-120b" → ("gpt-oss", 2).
    private static (string Family, int Version)? Shape(string[] segments)
    {
        for (int i = 1; i < segments.Length; i++)
            if (segments[i].Length > 0 && char.IsDigit(segments[i][0])) return (string.Join('-', segments[..i]), i);
        return null;
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
