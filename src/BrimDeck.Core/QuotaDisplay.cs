namespace BrimDeck.Core;

// What the panel decides from a snapshot's quotas, kept apart from the drawing so it can be tested: the pages of a usage column,
// the share a collapsed ring shows, and which quota has just risen into a warning. Every window an account has is one quota, and
// none is assumed: a plan with a weekly limit alone (no five-hour window) is a column of one quota, a ring at the weekly share,
// and a weekly alert, with no five-hour row, zero or alert made up beside it.
public static class QuotaDisplay
{
    // "Gemini · 5 小时" → group "Gemini", which orders the quota pages. The group stays in the data's words.
    public static string Group(string label) => label.Contains('·') ? label.Split('·')[0].Trim() : "";

    // The quotas of one column in pages of at most capacity, and the number of places the column is sized for (the fullest page it
    // can switch to; capacity while its first reading is awaited). Shorter windows come first. Antigravity labels its quotas
    // "Gemini · 5 小时", "Claude · 每周" and so on, and Claude labels them by organization when an account reads several
    // ("Pro · 5 小时额度", "Team · 每周额度"): each group starts its own page, Gemini first, a Claude organization in the order
    // the service gave them. Labels without a group come last.
    public static (List<List<UsageMetric>> Pages, int Slots) Pages(ProviderId provider, IReadOnlyList<UsageMetric> metrics, int capacity, bool awaiting)
    {
        var quotas = metrics.OrderBy(x => x.Window is > 0 ? x.Window.Value : int.MaxValue).ToList();
        var order = metrics.Select(x => Group(x.Label)).Distinct().ToList();
        var sets = provider switch
        {
            ProviderId.Antigravity => quotas.GroupBy(x => Group(x.Label)).OrderBy(x => x.Key == "Gemini" ? 0 : x.Key.Length > 0 ? 1 : 2).Select(x => x.ToList()).ToList(),
            ProviderId.Claude => quotas.GroupBy(x => Group(x.Label)).OrderBy(x => order.IndexOf(x.Key)).Select(x => x.ToList()).ToList(),
            _ => [quotas]
        };
        var pages = sets.SelectMany(set => set.Chunk(capacity)).Select(x => x.ToList()).ToList();
        if (pages.Count == 0) pages.Add([]);
        return (pages, awaiting ? capacity : Math.Clamp(pages.Max(x => x.Count), 1, capacity));
    }

    // One ring per application: the highest used share among its quotas, so a plan with one window shows that window's share.
    // A quota with no share counts as full when it is all there is.
    public static double RingShare(IReadOnlyList<UsageMetric> metrics)
    {
        var percentages = metrics.Where(m => m.UsedPercent is not null).Select(m => Math.Clamp(m.UsedPercent!.Value, 0, 100)).ToArray();
        return percentages.Length > 0 ? percentages.Max() : metrics.Count > 0 ? 100 : 0;
    }
}

// Warning and critical levels (AppPresets.Level) per quota, and which quota has just risen into a higher one. A quota is told
// by its row, its configuration and its own id, so only the windows an account really has are watched; the first scan only
// remembers the levels, so nothing already high at the start is announced.
public sealed class QuotaLevels
{
    private readonly Dictionary<string, int> _levels = [];
    private bool _seeded;

    // rows: each application's key and its metrics (stale or not yet read ones are left out by the caller). Returns the quota that
    // rose into a higher level with the highest share, with the owner it belongs to.
    public (object Owner, UsageMetric Metric, double Used)? Scan(IEnumerable<(object Owner, string Key, IEnumerable<UsageMetric> Metrics)> rows)
    {
        (object Owner, UsageMetric Metric, double Used)? risen = null;
        foreach (var (owner, key, metrics) in rows)
            foreach (var metric in metrics)
            {
                if (metric.UsedPercent is not { } used) continue;
                var id = $"{key}:{metric.Id}";
                int level = AppPresets.Level(used);
                bool rose = _seeded && _levels.TryGetValue(id, out var previous) && level > previous;
                _levels[id] = level;
                if (rose && level > 0 && (risen is null || used > risen.Value.Used)) risen = (owner, metric, used);
            }
        _seeded = true;
        return risen;
    }
}
