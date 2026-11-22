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

    // Tokens is the highest daily total; Cost is the highest known amount only. Unpriced is how many
    // records in the range could not be priced, so a cost peak may be incomplete or unknown.
    public static (long Tokens, decimal Cost, int Unpriced) Peak(IReadOnlyList<UsageTrendDay> days)
    {
        long tokens = 0;
        decimal cost = 0;
        int unpriced = 0;
        foreach (var day in days)
        {
            if (day.Tokens > tokens) tokens = day.Tokens;
            if (day.Cost > cost) cost = day.Cost;
            unpriced += day.Unpriced;
        }
        return (tokens, cost, unpriced);
    }

    public const double GraphPad = 2;

    // Gap shrinks first so a 1 DIP bar still fits; if even that overflows, bars go below 1 DIP so the last day stays in view.
    public static (double Gap, double Bar) Layout(int days, double width)
    {
        if (days <= 0 || width <= 0) return (0, 0);
        double available = Math.Max(0, width - GraphPad * 2);
        if (days == 1) return (0, available);
        double gap = Math.Min(6, available / days * .18);
        double bar = (available - gap * (days - 1)) / days;
        if (bar < 1)
        {
            gap = Math.Max(0, (available - days) / (days - 1));
            bar = (available - gap * (days - 1)) / days;
        }
        return (gap, bar);
    }
}
