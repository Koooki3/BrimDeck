namespace BrimDeck.Core;

// One local calendar day in a usage trend: tokens and priced cost for that day.
public sealed record UsageTrendDay(DateTime Day, long Tokens, decimal Cost);

// Buckets TokenEntry records into one point per day of a UsageDateRange (zeros for days with no records).
public static class UsageTrend
{
    public static IReadOnlyList<UsageTrendDay> Days(IEnumerable<TokenEntry> entries, UsageDateRange range,
        Func<IReadOnlyList<TokenEntry>, decimal>? costOf = null)
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
            decimal cost = costOf is null ? 0 : costOf(list);
            result.Add(new(day, tokens, cost));
        }
        return result;
    }
}
