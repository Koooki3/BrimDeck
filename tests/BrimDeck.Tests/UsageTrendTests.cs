using BrimDeck.Core;

internal static class UsageTrendTests
{
    internal static void Run(Action<string, bool> check)
    {
        var today = new DateTime(2026, 11, 17);
        var range = new UsageDateRange(today.AddDays(-2), today);
        TokenEntry Entry(DateTime day, long tokens, decimal? cost = null) =>
            new("k", new DateTimeOffset(day.AddHours(12)), "model", tokens, 0, 0, 0, 0, cost);

        var entries = new[]
        {
            Entry(today.AddDays(-2), 100, 1.5m),
            Entry(today.AddDays(-2), 50, 0.5m),
            Entry(today, 200, 2m),
            // Outside the range — ignored.
            Entry(today.AddDays(-5), 999, 9m),
        };
        var days = UsageTrend.Days(entries, range, list => list.Sum(e => e.ReportedCostUsd ?? 0));
        check("Usage trend keeps every local day in the range, zeros included",
            days.Count == 3
            && days[0] is { Day: var d0, Tokens: 150, Cost: 2.0m } && d0 == today.AddDays(-2)
            && days[1] is { Day: var d1, Tokens: 0, Cost: 0m } && d1 == today.AddDays(-1)
            && days[2] is { Day: var d2, Tokens: 200, Cost: 2.0m } && d2 == today);

        check("Usage trend token totals match summing the filtered entries",
            days.Sum(d => d.Tokens) == entries.Where(e => range.Contains(e.Time)).Sum(e => e.Total));

        var week = UsageDateRange.Recent(7, today);
        var weekDays = UsageTrend.Days([], week);
        check("Usage trend fills a seven-day range with empty days when there are no records",
            weekDays.Count == 7 && weekDays.All(d => d.Tokens == 0 && d.Cost == 0) && weekDays[0].Day == today.AddDays(-6) && weekDays[^1].Day == today);

        var single = UsageDateRange.Recent(1, today);
        check("Usage trend for today alone is one day",
            UsageTrend.Days([Entry(today, 10)], single).Single() is { Tokens: 10 } &&
            UsageTrend.Days([Entry(today.AddDays(-1), 10)], single).Single() is { Tokens: 0 });
    }
}
