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

    // Claude Code's local account record. On a Team seat the organization's tier is the Team's default ("default_raven") and the
    // seat's own tier is userRateLimitTier.
    public static string ClaudeProfile(JsonElement root)
    {
        var account = root.Get("oauthAccount");
        var type = account.Get("organizationType").Text();
        if (ClaudeOrganizationType(type, account.Get("seatTier").Text(), account.Get("userRateLimitTier").Text()) is { Length: > 0 } organization) return organization;
        var plan = ClaudeTier(account.Get("organizationRateLimitTier").Text());
        return plan.Length > 0 ? plan : ClaudePersonalType(type);
    }

    // A Team or Enterprise seat can carry a Max tier ("default_claude_max_5x" on a Team Premium seat); the organization decides.
    // A sign-in record names no seat, so its tier alone tells the Team seat.
    //
    // A tier named "<prefix>claude_<name>[_<N>x]" gives "<Name>[ <N>x]" ("default_claude_max_5x" is "Max 5x", "default_claude_ultra" is
    // "Ultra"), so a plan Anthropic adds later is shown under its own name. Anthropic documents none of these codes: "claude_ai" is
    // what a Pro account carries and names no plan, and any other shape of tier names nothing and leaves the subscription type.
    public static string ClaudeTier(string tier, string subscription = "")
    {
        if (ClaudeOrganizationType("claude_" + subscription.Trim().ToLowerInvariant(), "", tier) is { Length: > 0 } organization) return organization;
        var named = Regex.Match(tier.Trim(), @"(?:^|_)claude_([a-z][a-z0-9]*)(?:_(\d+x))?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (named.Success && !NotAPlan(named.Groups[1].Value))
            return Capitalized(named.Groups[1].Value) + (named.Groups[2].Success ? " " + named.Groups[2].Value.ToLowerInvariant() : "");
        return subscription.Trim();
    }

    // Words that follow "claude_" in a code but are not plan names ("default_claude_ai" is the tier a Pro account carries).
    private static bool NotAPlan(string name) => name.Equals("ai", StringComparison.OrdinalIgnoreCase);

    private static string Capitalized(string word) => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();

    // The plan name a sign-in token's tier gives when the local account record says more about the same plan: "Max" and the
    // record's "Max 5x" are one plan, so the record's wording is used. Only a plain name followed by a multiplier counts; a
    // Team seat's "Team Premium" or an unrelated plan is left alone.
    public static string RefineSignInPlan(string plan, string local)
    {
        if (plan.Length == 0 || plan.Contains(' ') || !local.StartsWith(plan + " ", StringComparison.OrdinalIgnoreCase)) return plan;
        return Regex.IsMatch(local[(plan.Length + 1)..], @"^\d+x$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) ? local : plan;
    }

    // What the panel writes beside an app's name for a plan: the known names in their usual spelling, any other single lowercase
    // word with a capital ("ultra" is "Ultra"), and everything else as given.
    public static string DisplayName(string plan)
    {
        var trimmed = plan.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "prolite" => "Pro Lite", "pro" or "google ai pro" => "Pro", "google ai ultra" => "Ultra", "plus" => "Plus", "free" => "Free",
            "max" => "Max", "ultra" => "Ultra", "team" => "Team", "business" => "Business", "enterprise" => "Enterprise",
            _ => Regex.IsMatch(trimmed, @"^[a-z][a-z0-9]*$", RegexOptions.CultureInvariant) ? Capitalized(trimmed) : trimmed
        };
    }

    // Team seats come as Standard and Premium. Anthropic documents no names for the codes; these were seen on live accounts:
    // seat "team_standard" for Standard, "team_tier_1" with the tier "default_claude_max_5x" for Premium (five times the usage
    // of a Standard seat). A seat that is not named counts as Premium on that tier, and otherwise stays "Team".
    private static string ClaudeOrganizationType(string type, string seat, string tier) => type switch
    {
        "claude_team" => seat switch
        {
            "team_standard" => "Team Standard",
            "team_tier_1" => "Team Premium",
            _ => Regex.IsMatch(tier, @"(?:^|_)claude_max_5x$", RegexOptions.CultureInvariant) ? "Team Premium" : "Team"
        },
        "claude_enterprise" => "Enterprise",
        _ => ""
    };

    // The account endpoint (/api/oauth/profile) names the organization's tier and type. It follows plan changes at once,
    // while the tier cached beside a sign-in token only changes when the token is issued again.
    public static string ClaudeOrganization(JsonElement root)
    {
        var organization = root.Get("organization");
        var type = organization.Get("organization_type").Text();
        if (ClaudeOrganizationType(type, organization.Get("seat_tier").Text(), organization.Get("rate_limit_tier").Text()) is { Length: > 0 } shared) return shared;
        var plan = ClaudeTier(organization.Get("rate_limit_tier").Text());
        return plan.Length > 0 ? plan : ClaudePersonalType(type);
    }

    // A personal subscription whose tier names no multiplier. A "claude_<name>" type that is not known here is shown by its name
    // ("claude_ultra" is "Ultra"); anything of another shape names nothing.
    private static string ClaudePersonalType(string type) => type switch
    {
        "claude_max" => "Max",
        "claude_pro" => "Pro",
        _ => Regex.Match(type.Trim(), @"^claude_([a-z][a-z0-9]*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) is { Success: true } named
            && !NotAPlan(named.Groups[1].Value) ? Capitalized(named.Groups[1].Value) : ""
    };

    // Combines the plan a sign-in token carries with the one in the local account record. Claude Code refreshes the local
    // record on every start, while the token's copy only changes when the token is issued again, so the local record wins.
    public static string ReconcileClaude(string signIn, string local)
    {
        signIn = signIn.Trim(); local = local.Trim();
        if (local.Length == 0) return signIn;
        return local;
    }
}
