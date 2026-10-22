using System.Text.Json;

namespace BrimDeck.Core;

// The last Claude quota read online, kept on disk. Only the account API reports the per-model quotas (Fable and the like) and
// the reset times; the Claude desktop app's local records carry the 5-hour and weekly shares alone. A start while the API is
// rate limited therefore showed two quotas and no Fable page until a request got through. With this file the reading from
// before the restart stands in, as the last reading of the same run already did (UsageService.Freshest).
// It holds the quotas, the plan and when they were read, never a token. A reading older than MaxAge is not used, and a quota
// whose window has reset since is left out: its share no longer says anything.
public static class ClaudeQuotaCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public const string FileName = "claude-quota.json";

    public static string Serialize(ClaudeQuotaSnapshot snapshot) => JsonSerializer.Serialize(snapshot with { Local = false });

    public static ClaudeQuotaSnapshot? Parse(string json, DateTimeOffset now)
    {
        ClaudeQuotaSnapshot? stored;
        try { stored = JsonSerializer.Deserialize<ClaudeQuotaSnapshot>(json); }
        catch (JsonException) { return null; }
        // A reading from the future (a clock set back since) or too old is not shown as the account's quota.
        if (stored?.Quotas is null || stored.Time > now.AddMinutes(5) || now - stored.Time > MaxAge) return null;
        var quotas = stored.Quotas.Where(quota => quota is not null && !string.IsNullOrWhiteSpace(quota.Label)
            && double.IsFinite(quota.UsedPercent) && (quota.ResetAt is not { } reset || reset > now)).ToList();
        return quotas.Count == 0 ? null : stored with { Quotas = quotas, Local = false, Plan = stored.Plan ?? "", PlanSource = stored.PlanSource ?? "", Source = stored.Source ?? "" };
    }

    public static ClaudeQuotaSnapshot? Load(string path, DateTimeOffset now)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path), now) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Written whole and then moved into place, so a reading cut short by an exit never leaves half a file. A failed write keeps
    // the previous file; the reading is still kept in memory for this run.
    public static void Save(string path, ClaudeQuotaSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Serialize(snapshot));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // A signed-out or rejected account keeps no quota of its own, on disk either.
    public static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
