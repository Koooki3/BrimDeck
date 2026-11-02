using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

static class CursorPricesTests
{
    // Prices in USD per token: input, output, cache read, cache write.
    private static readonly decimal[] Truth = [0.000003m, 0.000015m, 0.0000003m, 0.00000375m];

    public static async Task Run(string root, Action<string, bool> check)
    {
        var start = new DateTimeOffset(2026, 11, 2, 3, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, 40).Select(i => Record(i, start.AddMinutes(7 * i + 1))).ToList();
        // A reading 4 minutes after each record, in whole cents rounded down, as usage-status gives them.
        var rows = records.Select((_, i) => (Sum(records.Take(i + 1)), Math.Floor((double)records.Take(i + 1).Sum(Cost) * 100))).ToList();
        var fitted = CursorPrices.Fit(rows);
        decimal truth = records.Sum(Cost), worked = fitted is null ? 0 : records.Sum(r => Price(r, fitted));
        check("Readings of the weekly amount over time give prices that price the week within 2%", fitted is not null && Math.Abs(worked - truth) <= truth * 0.02m);
        check("Readings that stay below 20 cents give no prices", CursorPrices.Fit(rows.Where(r => r.Item2 < 20).ToList()) is null);
        check("Readings that no fixed prices reproduce give no prices", CursorPrices.Fit(rows.Select((r, i) => (r.Item1, i % 2 == 0 ? r.Item2 : r.Item2 * 1.5)).ToList()) is null);

        var path = Path.Combine(root, "cursor-prices", CursorPrices.FileName);
        var kept = new CursorPrices(path);
        kept.Use("auth0|A");
        kept.Add(start, 10, start.AddHours(1));
        kept.Add(start, 10, start.AddHours(2));
        check("A reading of the same amount in the same week extends the last one", kept.Samples.Count == 1 && kept.Samples[0].Last == start.AddHours(2));
        var near = new TokenEntry("near", start.AddHours(2).AddMinutes(-1), "Grok Bot", 1, 0, 0, 0, 1);
        kept.Add(start, 12, start.AddHours(3));
        kept.Update([near], (_, _) => true, start.AddHours(4));
        check("A reading with a record just before it is left out of the fit", kept.Samples[0] is { Checked: true, Tokens: null } && kept.Samples[1].Tokens is not null);
        kept.Save();
        var again = new CursorPrices(path);
        again.Use("auth0|A");
        check("Readings are kept on disk for the same account", again.Samples.Count == 2 && again.Samples[1].Tokens is not null);
        again.Use("auth0|B");
        check("Another account's readings are not used", again.Samples.Count == 0 && again.Prices is null);
        check("The file names no account", !File.ReadAllText(path).Contains("auth0|A", StringComparison.Ordinal));

        await Refreshes(root, check);
    }

    // A merged plan read every 11 minutes, with a record 6 minutes before each refresh, works out prices that then price
    // last week's records after a restart, once the weekly amount has started again from zero.
    private static async Task Refreshes(string root, Action<string, bool> check)
    {
        var today = new DateTime(2026, 11, 2);
        var week = new DateTimeOffset(today.AddHours(3));
        var clock = new DateTimeOffset(today.AddHours(8));
        var cache = Path.Combine(root, "cursor-refresh", CursorPrices.FileName);
        using var handler = new Handler { Week = week, Now = clock };
        using var http = new HttpClient(handler);
        var locations = DataLocations.Resolve(Path.Combine(root, "cursor-prices-home"), Path.Combine(root, "cursor-prices-roaming"), root, null, null);
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Cursor }] };
        settings.Normalize();
        ProviderSnapshot last;
        using (var service = new UsageService(new Desktop(), locations, http, () => clock, cursorPriceCache: cache))
        {
            last = await Refresh(service);
            for (int i = 0; i < 14; i++)
            {
                clock = clock.AddMinutes(11);
                handler.Records.Add(Record(i, clock.AddMinutes(-6)));
                handler.Now = clock;
                last = await Refresh(service);
            }
        }
        decimal truth = handler.Records.Sum(Cost), shown = last.Entries.Sum(e => e.ReportedCostUsd ?? 0);
        check("A merged plan's records are priced from the weekly amount once enough readings are known",
            last.Entries.Count == 14 && last.Entries.All(e => e.FittedCost) && Math.Abs(shown - truth) <= truth * 0.03m && File.Exists(cache));

        // A week later the amount is zero again and nothing new was used; the kept prices still price last week.
        clock = clock.AddDays(7); handler.Week = week.AddDays(7); handler.Now = clock;
        using (var restarted = new UsageService(new Desktop(), locations, http, () => clock, cursorPriceCache: cache))
            last = await Refresh(restarted);
        check("After a restart and the weekly reset, last week's records keep their worked-out prices",
            last.Entries.Count == 14 && last.Entries.All(e => e.FittedCost) && Math.Abs(last.Entries.Sum(e => e.ReportedCostUsd ?? 0) - truth) <= truth * 0.03m);

        async Task<ProviderSnapshot> Refresh(UsageService service) => (await service.RefreshAsync(settings)).Single(s => s.Id == ProviderId.Cursor);
    }

    // Token mixes that vary from record to record, mostly cache reads as Grok Bot's are.
    private static TokenEntry Record(int i, DateTimeOffset time) => new($"cursor:r{i}", time, "Grok Bot",
        200 + i * 37 % 900, 30000 + i * 7919 % 60000, 500 + i * 3571 % 9000, 0, 100 + i * 211 % 2500);

    private static decimal Cost(TokenEntry e) => e.Input * Truth[0] + e.Output * Truth[1] + e.CacheRead * Truth[2] + e.CacheWrite * Truth[3];
    private static decimal Price(TokenEntry e, TokenPrices p) => e.Input * p.Input + e.Output * p.Output + e.CacheRead * (p.CacheRead ?? 0) + e.CacheWrite * (p.CacheWrite ?? 0);
    private static long[] Sum(IEnumerable<TokenEntry> entries)
    {
        var list = entries.ToList();
        return [list.Sum(e => e.Input), list.Sum(e => e.Output), list.Sum(e => e.CacheRead), list.Sum(e => e.CacheWrite)];
    }

    private static string Token(string subject) =>
        "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = subject }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    private sealed class Desktop : IDesktopSources
    {
        public string? ReadCursorToken(string database) => Token("auth0|A");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<TokenEntry> Records { get; } = [];
        public DateTimeOffset Week { get; set; }
        public DateTimeOffset Now { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string Ms(DateTimeOffset time) => time.ToUnixTimeMilliseconds().ToString();
            if (path.EndsWith("/GetSandUsageStatus", StringComparison.Ordinal)) return Reply("""{"usagePercent":1,"cursorPlanName":"Premium","isUnified":true}""");
            if (path.EndsWith("/GetCurrentPeriodUsage", StringComparison.Ordinal))
                return Reply($$$"""{"billingCycleStart":"{{{Ms(Week)}}}","billingCycleEnd":"{{{Ms(Week.AddDays(7))}}}","planUsage":{"totalPercentUsed":1}}""");
            if (path == "/api/dashboard/v2/usage-status")
            {
                // The amount trails the records by a minute and is rounded down to whole cents.
                var cents = Math.Floor(Records.Where(r => r.Time >= Week && r.Time <= Now.AddMinutes(-1)).Sum(Cost) * 100);
                return Reply($$$"""{"weeklyIncludedUsage":{"usedCents":{{{cents}}},"budgetCents":4615,"periodStart":"{{{Week.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}}"}}""");
            }
            if (path == "/api/dashboard/v2/query-billing-items")
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                DateTimeOffset from = DateTimeOffset.Parse(query["startTime"]!), to = DateTimeOffset.Parse(query["endTime"]!);
                var items = Records.Where(r => r.Time >= from && r.Time < to && r.Time <= Now).Select(r =>
                    $$$"""{"billingItemId":"{{{r.Key[7..]}}}","eventTime":"{{{r.Time.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}}","product":"grok-bot","chargeCategory":"usage","usageType":"included","usage":{"prompt-text-tokens":"{{{r.Input}}}","cached-prompt-text-tokens":"{{{r.CacheRead}}}","cache-write-text-tokens":"{{{r.CacheWrite}}}","completion-text-tokens":"{{{r.Output}}}"},"pricing":{"currency":"USD","contracted":"0.0000000000"}}""");
                return Reply("{\"data\":[" + string.Join(",", items) + "],\"pagination\":{\"hasMore\":false}}");
            }
            throw new InvalidOperationException("Unexpected Cursor destination " + path);
        }

        private static Task<HttpResponseMessage> Reply(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
