using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

// The last Claude quota read online, kept on disk. Only the account API reports the per-model quotas (Fable and the like) and
// the reset times; the Claude desktop app's local records carry the 5-hour and weekly shares alone. A start while the API is
// rate limited therefore showed two quotas and no Fable page until a request got through. With this file the reading from
// before the restart stands in, as the last reading of the same run already did (UsageService.Freshest).
// It holds the quotas, the plan and when they were read, never a token. A reading older than MaxAge is not used, and a quota
// whose window has reset since is left out: its share no longer says anything.
// It also says which account it was read for, as a hash of the account id (AccountHash), never the id itself. It is used only
// for the same account: after a switch of account while BrimDeck was closed, the other account's quotas and plan would
// otherwise stand in for the new one's. A file that names no account (one written before the hash was kept) or a sign-in
// whose account cannot be told is not used either.
public static class ClaudeQuotaCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public const string FileName = "claude-quota.json";

    // The sources are kept as which sign-in and which plan origin, not as text: a text written in the language of the day would
    // still show in that language after a change of language and a restart. They are named again when the file is read.
    // Quota labels are already language-neutral (Loc.Label translates them where they are shown).
    private sealed record Stored(string? Account, DateTimeOffset Time, string? Plan, ClaudePlanOrigin PlanFrom, ClaudeSignIn SignIn, List<Quota>? Quotas);
    private static readonly JsonSerializerOptions Options = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    // A one-way hash of the account id, enough to tell two accounts apart without naming either. "" when the id is unknown.
    public static string AccountHash(string account) => string.IsNullOrWhiteSpace(account) ? ""
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("BrimDeck Claude account " + account.Trim().ToLowerInvariant())))[..32];

    public static string Serialize(ClaudeQuotaSnapshot snapshot, string account, ClaudeSignIn signIn, ClaudePlanOrigin planFrom) =>
        JsonSerializer.Serialize(new Stored(AccountHash(account), snapshot.Time, snapshot.Plan, planFrom, signIn, snapshot.Quotas), Options);

    // account is the id of the account signed in now.
    public static ClaudeQuotaSnapshot? Parse(string json, DateTimeOffset now, string account)
    {
        Stored? stored;
        try { stored = JsonSerializer.Deserialize<Stored>(json, Options); }
        catch (JsonException) { return null; }
        var expected = AccountHash(account);
        if (stored is null || expected.Length == 0 || stored.Account != expected) return null;
        // A reading from the future (a clock set back since) or too old is not shown as the account's quota.
        if (stored.Quotas is null || stored.Time > now.AddMinutes(5) || now - stored.Time > MaxAge) return null;
        var quotas = stored.Quotas.Where(quota => quota is not null && !string.IsNullOrWhiteSpace(quota.Label)
            && double.IsFinite(quota.UsedPercent) && (quota.ResetAt is not { } reset || reset > now)).ToList();
        if (quotas.Count == 0) return null;
        var plan = stored.Plan ?? "";
        return new(stored.Time, plan, plan.Length > 0 ? ClaudeSources.Plan(stored.PlanFrom, stored.SignIn) : "", ClaudeSources.Quota(stored.SignIn), quotas);
    }

    public static ClaudeQuotaSnapshot? Load(string path, DateTimeOffset now, string account)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path), now, account) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Written whole and then moved into place, so a reading cut short by an exit never leaves half a file. A failed write keeps
    // the previous file; the reading is still kept in memory for this run.
    public static void Save(string path, ClaudeQuotaSnapshot snapshot, string account, ClaudeSignIn signIn, ClaudePlanOrigin planFrom)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Serialize(snapshot, account, signIn, planFrom));
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
