using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

public static class PlanParser
{
    public static string Cursor(JsonElement root) => root.Get("membershipType").Text().Trim();

    public static string Antigravity(JsonElement root)
    {
        var status = root.Get("userStatus");
        var tier = status.Get("userTier").Get("name").Text().Trim();
        return tier.Length > 0 ? tier : status.Get("planStatus").Get("planInfo").Get("planName").Text().Trim();
    }

    public static string ClaudeProfile(JsonElement root)
    {
        var account = root.Get("oauthAccount");
        var tier = account.Get("organizationRateLimitTier").Text();
        return ClaudeTier(tier);
    }

    public static string ClaudeTier(string tier, string subscription = "")
    {
        var max = Regex.Match(tier, @"(?:^|_)claude_max_(\d+x)$", RegexOptions.CultureInvariant);
        return max.Success ? "Max " + max.Groups[1].Value : subscription.Trim();
    }

    // The account endpoint (/api/oauth/profile) names the organization's tier and type. It follows plan changes at once,
    // while the tier cached beside a sign-in token only changes when the token is issued again.
    public static string ClaudeOrganization(JsonElement root)
    {
        var organization = root.Get("organization");
        var plan = ClaudeTier(organization.Get("rate_limit_tier").Text());
        if (plan.Length > 0) return plan;
        return organization.Get("organization_type").Text() switch
        {
            "claude_max" => "Max",
            "claude_pro" => "Pro",
            "claude_team" => "Team",
            "claude_enterprise" => "Enterprise",
            _ => ""
        };
    }

    // Combines the plan a sign-in token carries with the one in the local account record. Claude Code refreshes the local
    // record on every start, while the token's copy only changes when the token is issued again, so the local record wins.
    public static string ReconcileClaude(string signIn, string local)
    {
        signIn = signIn.Trim(); local = local.Trim();
        if (local.Length == 0) return signIn;
        return local;
    }
}
