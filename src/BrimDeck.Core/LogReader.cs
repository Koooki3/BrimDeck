using System.Buffers;
using System.Text.Json;

namespace BrimDeck.Core;

public sealed class LogReader
{
    // Session logs only grow at the end. Each file remembers where its last complete line ended, together with the
    // Codex parser's running state, so a refresh parses only the lines written since. A shorter or rewritten file,
    // or an earlier start date, is parsed again from the beginning.
    private sealed class CacheItem(DateTimeOffset cutoff)
    {
        public readonly DateTimeOffset Cutoff = cutoff;
        public long Length, Offset;
        public DateTime Modified;
        public readonly List<TokenEntry> Entries = [];
        public JsonElement Quota;
        public DateTimeOffset? QuotaTime;
        public int Errors;
        public string Model = "unknown", LastTotal = "";
    }
    // Claude and Codex read different folders; each keeps only the files its latest read visited.
    private readonly Dictionary<ProviderId, Dictionary<string, CacheItem>> _caches = [];
    // A line longer than this (a huge tool result) is skipped and counted as unreadable instead of growing the buffer without end.
    public const int DefaultMaxLine = 32 << 20;
    private const int Chunk = 1 << 20;
    private readonly int _maxLine;

    public LogReader(int maxLine = DefaultMaxLine) => _maxLine = Math.Max(1024, maxLine);

    public ProviderSnapshot Read(ProviderId id, string root, DateTimeOffset now, DateTime? startDate = null) => Read(id, [root], now, startDate);

    // roots are data folders of the same tool; place names where they are, for the note shown with the statistics.
    public ProviderSnapshot Read(ProviderId id, IReadOnlyList<string> roots, DateTimeOffset now, DateTime? startDate = null, string? place = null)
    {
        var result = new ProviderSnapshot(id);
        var cutoff = new DateTimeOffset(startDate?.Date ?? now.LocalDateTime.Date.AddDays(-29));
        result.UsageStart = cutoff.LocalDateTime.Date;
        var folders = id == ProviderId.Codex ? new[] { "sessions", "archived_sessions" } : new[] { "projects" };
        if (!_caches.TryGetValue(id, out var cache)) _caches[id] = cache = new(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, TokenEntry>();
        int count = 0, errors = 0;
        JsonElement latestQuota = default;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in roots.SelectMany(dataRoot => folders.Select(name => Path.Combine(dataRoot, name))))
        {
            var path = folder;
            if (!Directory.Exists(path)) continue;
            result.UsageAvailable = true;
            foreach (var file in Directory.EnumerateFiles(path, "*.jsonl", options))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff.UtcDateTime) continue;
                    visited.Add(file);
                    if (!cache.TryGetValue(file, out var cached) || cutoff < cached.Cutoff || info.Length < cached.Length ||
                        info.Length == cached.Length && info.LastWriteTimeUtc != cached.Modified)
                        cache[file] = cached = new CacheItem(cutoff);
                    if (info.Length != cached.Length || info.LastWriteTimeUtc != cached.Modified) Parse(file, id, cached, info);
                    count++; errors += cached.Errors;
                    foreach (var entry in cached.Entries)
                    {
                        if (entry.Time < cutoff || entry.Time > now || entry.Total <= 0) continue;
                        // Streaming Claude records can repeat an id; keep its most complete usage.
                        if (!entries.TryGetValue(entry.Key, out var old) || entry.Total >= old.Total) entries[entry.Key] = entry;
                    }
                    if (cached.QuotaTime is { } time && (result.QuotaTime is null || time > result.QuotaTime))
                    { result.QuotaTime = time; latestQuota = cached.Quota; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; cache.Remove(file); }
            }
        }
        foreach (var gone in cache.Keys.Where(file => !visited.Contains(file)).ToList()) cache.Remove(gone);
        result.Entries = entries.Values.OrderBy(x => x.Time).ToList();
        var where = place ?? Loc.T("本机", "This PC");
        result.UsageNote = result.UsageAvailable ? Loc.T($"{where} {cutoff.LocalDateTime:yyyy-MM-dd} 起 · {count} 个记录文件", $"{where} since {cutoff.LocalDateTime:yyyy-MM-dd} · " + Loc.Count(count, "record file", "record files"))
            : Loc.T("未找到本机会话记录。", "No local session records were found.");
        result.UsageComplete = errors == 0;
        if (errors > 0) result.UsageNote += Loc.T($" · {errors} 处记录未能读取", " · " + Loc.Count(errors, "record", "records") + " could not be read");
        if (latestQuota.ValueKind == JsonValueKind.Object)
        {
            result.Quotas = QuotaParser.Codex(latestQuota, result.QuotaTime ?? now, false);
            result.Plan = latestQuota.Get("plan_type").Text();
            result.Source = Loc.T("本地配额快照", "Local quota snapshot");
            result.Status = Loc.T("本地记录", "Local records");
        }
        return result;
    }

    // Reads complete lines after the remembered offset. A line still being written is left for the next refresh. The buffer
    // comes from the shared pool, so a refresh that finds many changed files does not allocate a megabyte for each of them.
    private void Parse(string file, ProviderId id, CacheItem item, FileInfo info)
    {
        using var stream = SharedFile.Open(file);
        stream.Seek(item.Offset, SeekOrigin.Begin);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(Chunk, _maxLine));
        // The bytes dropped so far of a line over the limit. The offset moves past them only once the line's end is found, so a
        // long line still being written is looked at again on the next refresh and counted once, when it is complete.
        long skipped = 0;
        bool skipping = false;
        try
        {
            int filled = 0, read;
            while ((read = stream.Read(buffer, filled, buffer.Length - filled)) > 0)
            {
                filled += read;
                int start = 0, newline;
                while ((newline = Array.IndexOf(buffer, (byte)'\n', start, filled - start)) >= 0)
                {
                    if (skipping) { skipping = false; item.Offset += skipped; skipped = 0; item.Errors++; }
                    else ParseLine(buffer.AsMemory(start, newline - start), id, item);
                    start = newline + 1;
                }
                item.Offset += start;
                Buffer.BlockCopy(buffer, start, buffer, 0, filled - start);
                filled -= start;
                if (filled < buffer.Length) continue;
                // A single line fills the buffer (a large tool result): it grows up to the limit, beyond which the line is dropped.
                if (buffer.Length < _maxLine)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(Math.Min(_maxLine, buffer.Length * 2));
                    Buffer.BlockCopy(buffer, 0, larger, 0, filled);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
                else { skipping = true; skipped += filled; filled = 0; }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        item.Length = info.Length; item.Modified = info.LastWriteTimeUtc;
    }

    private static void ParseLine(ReadOnlyMemory<byte> line, ProviderId id, CacheItem item)
    {
        if (line.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) line = line[3..]; // UTF-8 byte order mark
        var span = line.Span;
        if (span.IndexOf("\"token_count\""u8) < 0 && span.IndexOf("\"turn_context\""u8) < 0 && span.IndexOf("\"usage\""u8) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var obj = doc.RootElement;
            var time = obj.Get("timestamp").Date();
            var type = obj.Get("type").Text();
            if (id == ProviderId.Codex)
            {
                var payload = obj.Get("payload");
                if (type == "turn_context") { item.Model = payload.Get("model").Text(); return; }
                if (type != "event_msg" || payload.Get("type").Text() != "token_count" || time is null) return;
                var limits = payload.Get("rate_limits");
                if (limits.ValueKind == JsonValueKind.Object && (limits.Get("limit_id").Text() is "" or "codex") && (item.QuotaTime is null || time >= item.QuotaTime))
                { item.Quota = limits.Clone(); item.QuotaTime = time; }
                var usageInfo = payload.Get("info");
                var cumulative = usageInfo.Get("total_token_usage");
                var identity = cumulative.ValueKind == JsonValueKind.Object ? cumulative.GetRawText() : "";
                if (identity.Length > 0)
                {
                    if (identity == item.LastTotal) return;
                    item.LastTotal = identity;
                }
                var usage = usageInfo.Get("last_token_usage");
                if (usage.ValueKind != JsonValueKind.Object || time < item.Cutoff) return;
                long input = usage.Get("input_tokens").Count(), cached = Math.Min(input, usage.Get("cached_input_tokens").Count());
                var write = usage.Get("cache_write_input_tokens").Count();
                var output = usage.Get("output_tokens").Count();
                // Timestamp + cumulative counters deduplicate history copied into a fork.
                item.Entries.Add(new($"codex:{time:O}:{identity}:{usage.GetRawText()}", time.Value, item.Model, input - cached, cached, write, 0, output));
            }
            else
            {
                if (type != "assistant" || time is null || time < item.Cutoff) return;
                var message = obj.Get("message");
                var usage = message.Get("usage");
                if (usage.ValueKind != JsonValueKind.Object) return;
                var creation = usage.Get("cache_creation_input_tokens").Count();
                var hour = Math.Min(creation, usage.Get("cache_creation").Get("ephemeral_1h_input_tokens").Count());
                var key = message.Get("id").Text();
                if (key.Length == 0) key = obj.Get("uuid").Text();
                if (key.Length == 0) key = $"{time:O}:{usage.GetRawText()}";
                item.Entries.Add(new("claude:" + key, time.Value, message.Get("model").Text(), usage.Get("input_tokens").Count(),
                    usage.Get("cache_read_input_tokens").Count(), creation - hour, hour, usage.Get("output_tokens").Count()));
            }
        }
        catch (JsonException) { item.Errors++; }
    }
}

public static class QuotaParser
{
    public static List<Quota> Codex(JsonElement root, DateTimeOffset now, bool online)
    {
        var limits = online ? root.Get("rate_limit") : root;
        var result = new List<Quota>();
        foreach (var field in online ? new[] { "primary_window", "secondary_window" } : new[] { "primary", "secondary" })
        {
            var window = limits.Get(field);
            if (window.Get("used_percent").Number() is not { } used) continue;
            int? minutes = online ? (window.Get("limit_window_seconds").Number() is { } seconds ? (int)(seconds / 60) : null) :
                (window.Get("window_minutes").Number() is { } mins ? (int)mins : null);
            var reset = window.Get(online ? "reset_at" : "resets_at").Date();
            if (reset is null && window.Get("reset_after_seconds").Number() is { } after && after is >= 0 and < 31536000) reset = now.AddSeconds(after);
            string label = minutes switch { 300 => "5 小时额度", 10080 => "每周额度", > 0 => $"{minutes} 分钟额度", _ => field.StartsWith("primary") ? "主要额度" : "次要额度" };
            result.Add(new(label, Math.Clamp(used, 0, 100), reset, minutes));
        }
        return result;
    }
    public static List<Quota> Claude(JsonElement root)
    {
        var result = new List<Quota>();
        void Add(string label, double? percent, DateTimeOffset? reset, int? window)
        {
            if (percent is not { } value || value < 0 || result.Any(q => q.Label == label)) return;
            result.Add(new(label, value, reset, window));
        }
        foreach (var (key, label, window) in new[] { ("five_hour", "5 小时额度", 300), ("seven_day", "每周额度", 10080) })
            Add(label, root.Get(key).Get("utilization").Number(), root.Get(key).Get("resets_at").Date(), window);
        // Current Claude clients read server-driven limits, including Fable, from scope.model.display_name.
        // The server decides entitlement; missing/null meters never become an invented zero for a plan.
        foreach (var limit in root.Get("limits").Items())
        {
            string kind = limit.Get("kind").Text(), group = limit.Get("group").Text();
            string model = limit.Get("scope").Get("model").Get("display_name").Text();
            string surface = limit.Get("scope").Get("surface").Get("display_name").Text();
            if (kind == "session") Add("5 小时额度", limit.Get("percent").Number(), limit.Get("resets_at").Date(), 300);
            else if (kind == "weekly_all") Add("每周额度", limit.Get("percent").Number(), limit.Get("resets_at").Date(), 10080);
            else if (!string.IsNullOrWhiteSpace(model.Length > 0 ? model : surface))
            {
                var name = model.Length > 0 ? model : surface;
                bool weekly = group == "weekly" || kind == "weekly_scoped";
                Add(name + (weekly ? " 每周" : ""), limit.Get("percent").Number(), limit.Get("resets_at").Date(), weekly ? 10080 : null);
            }
        }
        foreach (var (key, name) in new[] { ("seven_day_opus", "Opus"), ("seven_day_sonnet", "Sonnet") })
            if (!result.Any(q => q.Label.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                Add(name + " 每周", root.Get(key).Get("utilization").Number(), root.Get(key).Get("resets_at").Date(), 10080);
        return result;
    }
    public static List<Quota> Cursor(JsonElement root)
    {
        var plan = root.Get("planUsage");
        if (plan.ValueKind != JsonValueKind.Object) plan = root.Get("individualUsage").Get("plan");
        var reset = root.Get("billingCycleEnd").Date();
        var result = new List<Quota>();
        foreach (var (key, label) in new[] { ("autoPercentUsed", "Cursor 模型"), ("apiPercentUsed", "其他模型") })
            if (plan.Get(key).Number() is { } percent) result.Add(new(label, Math.Clamp(percent, 0, 100), reset));
        if (result.Count == 0 && plan.Get("totalPercentUsed").Number() is { } total) result.Add(new("账期额度", Math.Clamp(total, 0, 100), reset));
        return result;
    }
    // GetSandUsageStatus reports one weekly figure for the Grok Bot / Cursor account: how much of the included
    // allowance is used (0-100) and when it resets. usagePercent shares the same scale as the Cursor quotas above.
    public static Quota? SandUsage(JsonElement root)
    {
        if (root.Get("usagePercent").Number() is not { } percent) return null;
        return new("Grok Bot", Math.Clamp(percent, 0, 100), root.Get("nextResetTimestampUtc").Date());
    }
    public static List<Quota> Antigravity(JsonElement root)
    {
        var result = new List<Quota>();
        var groups = root.Get("response").Get("groups");
        if (groups.ValueKind != JsonValueKind.Array) groups = root.Get("groups");
        foreach (var group in groups.Items())
            foreach (var bucket in group.Get("buckets").Items())
                if (bucket.Get("remainingFraction").Number() is { } fraction)
                {
                    var id = bucket.Get("bucketId").Text();
                    var label = id switch { "gemini-5h" => "Gemini · 5 小时", "gemini-weekly" => "Gemini · 每周", "3p-5h" => "Claude · 5 小时", "3p-weekly" => "Claude · 每周", _ => id };
                    int? minutes = id switch { "gemini-5h" or "3p-5h" => 300, "gemini-weekly" or "3p-weekly" => 10080, _ => null };
                    result.Add(new(label, (1 - Math.Clamp(fraction, 0, 1)) * 100, bucket.Get("resetTime").Date(), minutes));
                }
        if (result.Count > 0) return result;
        var configs = root.Get("userStatus").Get("cascadeModelConfigData").Get("clientModelConfigs");
        foreach (var model in configs.Items())
            if (model.Get("quotaInfo").Get("remainingFraction").Number() is { } fraction)
                result.Add(new(model.Get("label").Text(), (1 - Math.Clamp(fraction, 0, 1)) * 100, model.Get("quotaInfo").Get("resetTime").Date()));
        return result;
    }
}
