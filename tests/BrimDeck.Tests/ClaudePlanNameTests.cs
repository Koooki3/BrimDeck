using System.Text.Json;
using BrimDeck.Core;

// How a Claude plan is named. Anthropic documents none of the tier codes, and no Ultra account was available: the Ultra
// strings below are the shape the existing codes follow ("default_claude_max_5x"), not identifiers read from a real account.
// What matters is that a name Anthropic adds later is shown as itself, that nothing is invented from a code that names no
// plan, and that every identifier seen on live accounts gives the name it gave before.
static class ClaudePlanNameTests
{
    private static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }

    public static async Task Run(string root, Action<string, bool> check)
    {
        Tiers(check);
        Organizations(check);
        Display(check);
        Refine(check);
        await WeeklyOnly(root, check);
    }

    static void Tiers(Action<string, bool> check)
    {
        // Identifiers seen on live accounts: the result is the same as before the names were generalised.
        check("Claude tier: Max 5x and Max 20x keep their names", PlanParser.ClaudeTier("default_claude_max_5x", "max") == "Max 5x" &&
            PlanParser.ClaudeTier("default_claude_max_20x", "max") == "Max 20x" && PlanParser.ClaudeTier("default_claude_max_20x") == "Max 20x");
        check("Claude tier: Team and Enterprise seats keep their names", PlanParser.ClaudeTier("default_raven", "team") == "Team" &&
            PlanParser.ClaudeTier("default_claude_max_5x", "team") == "Team Premium" && PlanParser.ClaudeTier("default_raven", "enterprise") == "Enterprise");
        check("Claude tier: a Pro tier and an unnamed tier fall back to the subscription type as written", PlanParser.ClaudeTier("default_claude_ai", "pro") == "pro" &&
            PlanParser.ClaudeTier("", "max") == "max" && PlanParser.ClaudeTier("default_raven", " Max ") == "Max");
        check("Claude tier: a code that names no plan stays empty", PlanParser.ClaudeTier("default_claude_ai") == "" && PlanParser.ClaudeTier("unknown") == "" &&
            PlanParser.ClaudeTier("") == "" && PlanParser.ClaudeTier("default_raven") == "");

        // New names, by the same shape.
        check("Claude tier: default_claude_max_10x is Max 10x", PlanParser.ClaudeTier("default_claude_max_10x", "max") == "Max 10x");
        check("Claude tier: default_claude_ultra is Ultra", PlanParser.ClaudeTier("default_claude_ultra", "ultra") == "Ultra" && PlanParser.ClaudeTier("default_claude_ultra") == "Ultra");
        check("Claude tier: a bare claude_ultra is Ultra and a multiplier is kept", PlanParser.ClaudeTier("claude_ultra") == "Ultra" && PlanParser.ClaudeTier("default_claude_ultra_10x") == "Ultra 10x");
        check("Claude tier: the tier names the plan even when the subscription type says another word", PlanParser.ClaudeTier("default_claude_ultra", "max") == "Ultra");
        check("Claude tier: letter case does not matter", PlanParser.ClaudeTier("DEFAULT_CLAUDE_ULTRA") == "Ultra" && PlanParser.ClaudeTier("Default_Claude_Max_5X") == "Max 5x");
        check("Claude tier: an unknown subscription type is kept as written", PlanParser.ClaudeTier("default_raven_v2", "ultra") == "ultra");
        check("Claude tier: codes of another shape are not turned into a name",
            PlanParser.ClaudeTier("default_claude_5x") == "" && PlanParser.ClaudeTier("default_claude_ultra_x") == "" && PlanParser.ClaudeTier("default_claude_max_5x_beta") == "" &&
            PlanParser.ClaudeTier("claude_") == "" && PlanParser.ClaudeTier("my_claude-ultra") == "" && PlanParser.ClaudeTier("default_claude_ai_5x") == "");
    }

    static void Organizations(Action<string, bool> check)
    {
        check("Claude organization: known types and tiers give the names they gave before",
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_pro"}}""")) == "Pro" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_pro","rate_limit_tier":"default_claude_ai"}}""")) == "Pro" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_max"}}""")) == "Max" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_max","rate_limit_tier":"default_claude_max_5x"}}""")) == "Max 5x" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_max","rate_limit_tier":"default_claude_max_20x"}}""")) == "Max 20x" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_team","rate_limit_tier":"default_raven"}}""")) == "Team" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_enterprise"}}""")) == "Enterprise");
        check("Claude organization: an unknown claude_<name> type is shown by its name",
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_ultra"}}""")) == "Ultra" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_ultra","rate_limit_tier":"default_claude_ultra"}}""")) == "Ultra" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"Claude_Ultra"}}""")) == "Ultra");
        check("Claude organization: a type that names no plan stays empty",
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"api"}}""")) == "" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_ai"}}""")) == "" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"organization_type":"claude_max_ultra"}}""")) == "" &&
            PlanParser.ClaudeOrganization(Json("""{"organization":{"rate_limit_tier":"unknown"}}""")) == "" &&
            PlanParser.ClaudeOrganization(Json("""{}""")) == "");
        check("Claude local record: Ultra by tier or by type, and the known records are unchanged",
            PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationRateLimitTier":"default_claude_ultra"}}""")) == "Ultra" &&
            PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationType":"claude_ultra"}}""")) == "Ultra" &&
            PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationType":"claude_pro","organizationRateLimitTier":"default_claude_ai"}}""")) == "Pro" &&
            PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationType":"claude_max","organizationRateLimitTier":"default_claude_max_20x"}}""")) == "Max 20x" &&
            PlanParser.ClaudeProfile(Json("""{"oauthAccount":{"organizationRateLimitTier":"unknown"}}""")) == "" &&
            PlanParser.ClaudeProfile(Json("""{}""")) == "");
    }

    static void Display(Action<string, bool> check)
    {
        check("Plan display: the known names keep their spelling", new[] { ("pro", "Pro"), ("PRO", "Pro"), ("prolite", "Pro Lite"), ("google ai pro", "Pro"), ("google ai ultra", "Ultra"),
            ("plus", "Plus"), ("free", "Free"), ("max", "Max"), ("team", "Team"), ("business", "Business"), ("enterprise", "Enterprise") }
            .All(item => PlanParser.DisplayName(item.Item1) == item.Item2));
        check("Plan display: ultra, in any case and with spaces around it, is Ultra", new[] { "ultra", "ULTRA", "  ultra ", "Ultra" }.All(plan => PlanParser.DisplayName(plan) == "Ultra"));
        check("Plan display: another lowercase single word gets a capital", PlanParser.DisplayName("hobby") == "Hobby" && PlanParser.DisplayName("k2") == "K2");
        check("Plan display: names with several words, a multiplier or other characters are shown as given",
            PlanParser.DisplayName("Max 5x") == "Max 5x" && PlanParser.DisplayName("Ultra 10x") == "Ultra 10x" && PlanParser.DisplayName("Team Premium") == "Team Premium" &&
            PlanParser.DisplayName("Pro + Team Premium") == "Pro + Team Premium" && PlanParser.DisplayName("free_trial") == "free_trial" && PlanParser.DisplayName("Moderato") == "Moderato" &&
            PlanParser.DisplayName("  标准版 ") == "标准版" && PlanParser.DisplayName("5x") == "5x" && PlanParser.DisplayName("") == "");
    }

    static void Refine(Action<string, bool> check)
    {
        check("Sign-in plan: a bare name takes the multiplier the local record names", PlanParser.RefineSignInPlan("max", "Max 5x") == "Max 5x" &&
            PlanParser.RefineSignInPlan("Max", "Max 20x") == "Max 20x" && PlanParser.RefineSignInPlan("ultra", "Ultra 10x") == "Ultra 10x");
        check("Sign-in plan: anything else is left as it is", PlanParser.RefineSignInPlan("Team", "Team Premium") == "Team" && PlanParser.RefineSignInPlan("pro", "Pro") == "pro" &&
            PlanParser.RefineSignInPlan("max", "") == "max" && PlanParser.RefineSignInPlan("", "Max 5x") == "" && PlanParser.RefineSignInPlan("Max 5x", "Max 20x") == "Max 5x" &&
            PlanParser.RefineSignInPlan("max", "Max Premium") == "max" && PlanParser.RefineSignInPlan("ultra", "Max 5x") == "ultra");
    }

    // An Ultra reading by the real code path: the quota interface answers with the weekly window only (the five-hour window is
    // absent or null), and the sign-in names the plan. Nothing is made up for the missing window.
    static async Task WeeklyOnly(string root, Action<string, bool> check)
    {
        var quotas = QuotaParser.Claude(Json("""{"five_hour":null,"seven_day":{"utilization":12,"resets_at":"2026-11-07T10:00:00Z"}}"""));
        check("Claude quotas: a null five-hour window leaves only the weekly one, with no zero in its place", quotas.Count == 1 && quotas[0] is { Label: "每周额度", UsedPercent: 12, Minutes: 10080, ResetAt: not null });
        quotas = QuotaParser.Claude(Json("""{"seven_day":{"utilization":0},"limits":[{"kind":"weekly_all","group":"weekly","percent":0}]}"""));
        check("Claude quotas: a weekly window with nothing used stays a real zero and the missing window is not added", quotas.Count == 1 && quotas[0] is { Minutes: 10080, UsedPercent: 0 });
        quotas = QuotaParser.Claude(Json("""{"limits":[{"kind":"weekly_all","group":"weekly","percent":31},{"kind":"weekly_scoped","group":"weekly","percent":5,"scope":{"model":{"display_name":"Fable"}}}]}"""));
        check("Claude quotas: server-driven limits without a session meter give the weekly and the scoped ones only", quotas.Count == 2 && quotas.All(q => q.Minutes == 10080) &&
            quotas[0].UsedPercent == 31 && quotas[1].Label == "Fable 每周");
        check("Claude quotas: an answer without any window is empty, so it is rejected rather than shown", QuotaParser.Claude(Json("""{"five_hour":null,"seven_day":null}""")).Count == 0);

        var home = Path.Combine(root, "claude-plan-names");
        var locations = DataLocations.Resolve(Path.Combine(home, "home"), Path.Combine(home, "roaming"), Path.Combine(home, "local"), null, "");
        Directory.CreateDirectory(locations.ClaudeHome);
        File.WriteAllText(Path.Combine(locations.ClaudeHome, ".credentials.json"),
            """{"claudeAiOauth":{"accessToken":"test-ultra","subscriptionType":"ultra","rateLimitTier":"default_claude_ultra"}}""");
        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = now;
        using var handler = new ClaudeQuotaHandler
        {
            Override = (_, _) => new(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("""{"five_hour":null,"seven_day":{"utilization":12,"resets_at":"2026-11-07T10:00:00Z"},"limits":[{"kind":"weekly_all","group":"weekly","percent":12,"resets_at":"2026-11-07T10:00:00Z"}]}""") }
        };
        using var client = new HttpClient(handler);
        using var service = new UsageService(new ClaudeFixtureSources(), locations, client, () => clock);
        var settings = new DeckSettings { Apps = [new AppEntry { Id = ProviderId.Claude }] };
        clock = clock.AddSeconds(60);
        var snapshot = (await service.RefreshAsync(settings)).Single();
        check("Claude read: an Ultra sign-in with only a weekly window connects and shows the weekly quota alone",
            snapshot.LiveQuota && snapshot.Quotas.Count == 1 && snapshot.Quotas[0] is { Minutes: 10080, UsedPercent: 12 } && snapshot.Metrics.Count == 1);
        check("Claude read: the plan of that sign-in is Ultra, and the panel writes Ultra", snapshot.Plan == "Ultra" && PlanParser.DisplayName(snapshot.Plan) == "Ultra");

        // The account endpoint names the organization and wins over the sign-in's copy.
        handler.Profile = """{"organization":{"organization_type":"claude_ultra","rate_limit_tier":"default_claude_ultra_10x"}}""";
        clock = clock.AddSeconds(60);
        snapshot = (await service.RefreshAsync(settings)).Single();
        check("Claude read: the account endpoint's unknown type still gives a name", snapshot.Plan == "Ultra 10x" && snapshot.Quotas.Count == 1);

        // A sign-in that names the plan without a multiplier takes the local record's.
        handler.Profile = null;
        File.WriteAllText(Path.Combine(locations.ClaudeHome, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"test-ultra","subscriptionType":"ultra"}}""");
        File.WriteAllText(Path.Combine(locations.ClaudeHome, ".claude.json"), """{"oauthAccount":{"organizationRateLimitTier":"default_claude_ultra_10x"}}""");
        clock = clock.AddSeconds(60);
        snapshot = (await service.RefreshAsync(settings)).Single();
        check("Claude read: a bare sign-in plan takes the multiplier of the local record", snapshot.Plan == "Ultra 10x");
    }
}
