using System.Text.Json;

namespace BrimDeck.Core;

// Deliberately a class: generated record formatting must never include an access token.
// Account is the id of the account the token belongs to, read from the same local records without a request; "" when they
// do not name it.
public sealed class ClaudeCredential(string token, string plan, string source, string account = "")
{
    public string Token { get; } = token;
    public string Plan { get; } = plan;
    public string Source { get; } = source;
    public string Account { get; } = account;
}

public sealed record ClaudeDesktopState(bool Detected, IReadOnlyList<ClaudeCredential> Credentials, bool ReadFailed = false);

public static class ClaudeDesktopCache
{
    public static IReadOnlyList<ClaudeCredential> Parse(JsonElement cache, string account, DateTimeOffset now)
    {
        if (cache.ValueKind != JsonValueKind.Object) return [];
        var credentials = new List<(ClaudeCredential Credential, int ScopeCount)>();
        foreach (var item in cache.EnumerateObject())
        {
            if (item.Name.StartsWith("acct:", StringComparison.Ordinal) &&
                (account.Length == 0 || !item.Name.StartsWith("acct:" + account + "|", StringComparison.Ordinal))) continue;
            const string host = ":https://api.anthropic.com:";
            var index = item.Name.IndexOf(host, StringComparison.Ordinal);
            if (index < 0) continue;
            var scopes = item.Name[(index + host.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!scopes.Contains("user:profile", StringComparer.Ordinal)) continue;
            var token = item.Value.Get("token").Text();
            var expires = item.Value.Get("expiresAt").Number();
            if (token.Length == 0 || expires is null || expires <= now.ToUnixTimeMilliseconds()) continue;
            credentials.Add((new(token, PlanParser.ClaudeTier(item.Value.Get("rateLimitTier").Text(),
                item.Value.Get("subscriptionType").Text()), Loc.T("Claude 桌面版", "Claude desktop app"), account), scopes.Length));
        }
        // Prefer the existing profile-only credential when the desktop has several scopes cached.
        return credentials.OrderBy(item => item.ScopeCount).Select(item => item.Credential)
            .DistinctBy(item => item.Token).ToList();
    }
}
