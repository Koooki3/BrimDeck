namespace BrimDeck.Core;

// One local calendar day in a usage trend: tokens and priced cost for that day.
// Unpriced is how many records that day could not be matched to a price; Cost is the known amount only.
public sealed record UsageTrendDay(DateTime Day, long Tokens, decimal Cost, int Unpriced = 0);

// Buckets TokenEntry records into one point per day of a UsageDateRange (zeros for days with no records).
public static class UsageTrend
{
    public static IReadOnlyList<UsageTrendDay> Days(IEnumerable<TokenEntry> entries, UsageDateRange range,
        Func<IReadOnlyList<TokenEntry>, decimal>? costOf = null)
        => Days(entries, range, list =>
        {
            decimal cost = costOf is null ? 0 : costOf(list);
            return new CostSummary(cost, 0, 0, 0);
        });

    public static IReadOnlyList<UsageTrendDay> Days(IEnumerable<TokenEntry> entries, UsageDateRange range,
        Func<IReadOnlyList<TokenEntry>, CostSummary> costOf)
    {
        var byDay = new Dictionary<DateTime, List<TokenEntry>>();
        foreach (var entry in entries)
        {
            var day = entry.Time.LocalDateTime.Date;
            if (day < range.Start || day > range.End) continue;
            if (!byDay.TryGetValue(day, out var list)) byDay[day] = list = [];
            list.Add(entry);
        }
        var result = new List<UsageTrendDay>();
        for (var day = range.Start; day <= range.End; day = day.AddDays(1))
        {
            if (!byDay.TryGetValue(day, out var list)) list = [];
            long tokens = 0;
            foreach (var entry in list) tokens += entry.Total;
            var cost = costOf(list);
            result.Add(new(day, tokens, cost.Amount, cost.Unpriced));
        }
        return result;
    }
}
