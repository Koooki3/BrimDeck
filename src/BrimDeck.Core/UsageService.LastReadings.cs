using System.Collections.Concurrent;

namespace BrimDeck.Core;

// The last quota of the sources below, shown in place of a reading that failed (the service did not answer or timed out, refused the
// sign-in, or answered in a form that is not read), marked as old with the time it was read, until a new reading replaces it. The
// sources with a last reading of their own (Claude, Grok Bot, Kimi Code, Qoder, Antigravity's CLI) are not among them; Qoder still
// takes the previous run's reading from here (RestoreLastReadings).
// A source that is not signed in, or whose account has no quota, keeps nothing; neither does one signed in again in settings. A
// reading of another account is set aside once the sign-in names its account (ReadingFor).
public sealed partial class UsageService
{
    private static bool KeepsLastReading(ProviderId id) => id is ProviderId.WorkBuddy or ProviderId.WorkBuddyAi or ProviderId.Qianwen or ProviderId.QwenCloud
        or ProviderId.QwenWorkCn or ProviderId.QwenWork or ProviderId.MiMo;

    // Account: the account it was read for; null when that is not known (a reading of the previous run, or a sign-in that names no
    // account), which is shown for any account.
    private sealed record LastReading(DateTimeOffset Time, string? Account, List<UsageMetric> Metrics, string Plan, string PlanSource, string Source, string Scope);
    private readonly ConcurrentDictionary<ProviderId, LastReading> _lastReadings = new();

    // Keeps a successful reading; a reading without quotas keeps nothing.
    private void KeepReading(ProviderSnapshot snapshot, string? account, DateTimeOffset now)
    {
        var metrics = snapshot.Metrics.ToList();
        if (metrics.Count == 0) { ForgetReading(snapshot.Id); return; }
        _lastReadings[snapshot.Id] = new(now, account, metrics, snapshot.Plan, snapshot.PlanSource, snapshot.Source, snapshot.Scope);
    }

    private void ForgetReading(ProviderId id) { _lastReadings.TryRemove(id, out _); _qianwenParts.TryRemove(id, out _); }

    // A quota that has reset or expired since it was read says nothing about now.
    private static bool StillHolds(UsageMetric metric, DateTimeOffset now) =>
        (metric.ResetAt is not { } reset || reset > now) && (metric.ExpiresAt is not { } end || end > now);
    private static bool Recent(DateTimeOffset time, DateTimeOffset now) => time <= now.AddMinutes(5) && now - time <= UsageLastSeen.MaxAge;

    // Called once the sign-in is read: a reading of another account is set aside.
    private void ReadingFor(ProviderId id, string account)
    {
        if (account.Length > 0 && _lastReadings.TryGetValue(id, out var kept) && kept.Account is { Length: > 0 } other && other != account) ForgetReading(id);
    }

    // The previous run's readings (UsageLastSeen.Load) stand in until this run reads its own, so a first read that fails still
    // shows them. A reading of this run is never replaced.
    public void RestoreLastReadings(IEnumerable<ProviderSnapshot> restored)
    {
        foreach (var snapshot in restored)
        {
            // Qoder keeps its last reading itself (QoderKept); the previous run's stands in until a read of this run ends.
            if (snapshot.Id is ProviderId.Qoder or ProviderId.QoderCn && !snapshot.Configurations.Contains(ProviderCatalog.LocalQuota)
                && snapshot.Metrics.Count > 0 && snapshot.QuotaTime is { } readAt)
            {
                var qoder = _qoder.GetOrAdd(snapshot.Id, _ => new());
                lock (qoder)
                    if (qoder.Reading is null) { qoder.Reading = new(snapshot.Metrics.ToList(), snapshot.Plan, false); qoder.Time = readAt; qoder.Restored = true; }
                continue;
            }
            if (KeepsLastReading(snapshot.Id) && !snapshot.Configurations.Contains(ProviderCatalog.LocalQuota) && snapshot.Metrics.Count > 0 && snapshot.QuotaTime is { } time)
            {
                var source = ProviderCatalog.Name(snapshot.Id) + Loc.T(" 账户", " account");
                _lastReadings.TryAdd(snapshot.Id, new(time, null, snapshot.Metrics.ToList(), snapshot.Plan, snapshot.Plan.Length > 0 ? source : "", source, "account"));
                // The previous run kept no plan names by part, so a part filled in from it shows its figures without its plan's name.
                if (snapshot.Id is ProviderId.Qianwen or ProviderId.QwenCloud)
                    _qianwenParts.TryAdd(snapshot.Id, (null, snapshot.Metrics.GroupBy(QianwenUsage.PartOf).ToDictionary(part => part.Key, part => new QianwenPart(time, [.. part], []))));
            }
        }
    }

    // The last reading of each part of a Qianwen or QwenCloud account (QianwenUsage.Parts) and when it was read, with the account it
    // was read for (null when not known): a part that cannot be read shows its last reading in its place while the others show their
    // own. A part read again keeps the new reading, even an empty one (the account no longer has it).
    private sealed record QianwenPart(DateTimeOffset Time, List<UsageMetric> Metrics, List<string> Plans);
    private readonly ConcurrentDictionary<ProviderId, (string? Account, Dictionary<string, QianwenPart> Parts)> _qianwenParts = new();

    // The quotas and plan to show for a reading, the failed parts filled in from their last reading; Old gives the time of each part
    // that was.
    private (List<UsageMetric> Metrics, string Plan, Dictionary<string, DateTimeOffset> Old) FillQianwenParts(ProviderId id, string account, QianwenUsage.Reading reading, DateTimeOffset now)
    {
        var kept = _qianwenParts.TryGetValue(id, out var last) && (last.Account is null || account.Length == 0 || last.Account == account) ? last.Parts : [];
        var parts = new Dictionary<string, QianwenPart>();
        var old = new Dictionary<string, DateTimeOffset>();
        var metrics = new List<UsageMetric>();
        var plans = new List<string>();
        foreach (var part in QianwenUsage.Parts)
        {
            var shown = new QianwenPart(now, [.. reading.Metrics.Where(metric => QianwenUsage.PartOf(metric) == part)], [.. reading.Plans.Where(plan => plan.Part == part).Select(plan => plan.Name)]);
            if (!reading.Failed.Contains(part)) parts[part] = shown;
            else if (kept.TryGetValue(part, out var earlier) && Recent(earlier.Time, now) && earlier.Metrics.Where(metric => StillHolds(metric, now)).ToList() is { Count: > 0 } live)
            {
                parts[part] = earlier;
                old[part] = earlier.Time;
                shown = earlier with { Metrics = live };
            }
            metrics.AddRange(shown.Metrics);
            plans.AddRange(shown.Plans);
        }
        metrics.AddRange(reading.Metrics.Where(metric => !QianwenUsage.Parts.Contains(QianwenUsage.PartOf(metric))));
        _qianwenParts[id] = (account.Length > 0 ? account : null, parts);
        return (metrics, string.Join(" · ", plans), old);
    }

    // Shows the last reading in place of one that failed. The snapshot's status says why it failed and is followed by when the
    // reading was taken; retrying replaces the status label with "Retrying later", while a label that asks the user to act (an
    // expired sign-in) stays. False when there is nothing to show: no reading was kept, it is older than UsageLastSeen.MaxAge or
    // from the future (a clock set back since), or every quota of it has reset or expired since.
    private bool ShowLastReading(ProviderSnapshot snapshot, DateTimeOffset now, bool retrying)
    {
        if (!_lastReadings.TryGetValue(snapshot.Id, out var kept)) return false;
        if (!Recent(kept.Time, now)) return false;
        var metrics = kept.Metrics.Where(metric => StillHolds(metric, now)).ToList();
        if (metrics.Count == 0) return false;
        snapshot.Quotas = [];
        snapshot.Metrics = metrics;
        if (kept.Plan.Length > 0) { snapshot.Plan = kept.Plan; snapshot.PlanSource = kept.PlanSource; }
        if (kept.Source.Length > 0) snapshot.Source = kept.Source;
        if (kept.Scope.Length > 0) snapshot.Scope = kept.Scope;
        snapshot.IsStale = true;
        snapshot.LiveQuota = true;
        snapshot.QuotaTime = kept.Time;
        var reason = snapshot.Status.TrimEnd('。', '.', ' ');
        var time = Clock(kept.Time, now);
        snapshot.Status = reason.Length > 0 ? Loc.T($"{reason}；显示 {time} 读取的额度", $"{reason}; showing the quota read at {time}")
            : Loc.T($"显示 {time} 读取的额度", $"Showing the quota read at {time}");
        if (retrying) snapshot.StatusLabel = Loc.T("稍后重试", "Retrying later");
        return true;
    }
}
