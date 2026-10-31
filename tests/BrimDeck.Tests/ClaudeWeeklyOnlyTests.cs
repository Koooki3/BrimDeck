using System.Text.Json;
using BrimDeck.Core;

// An account with a weekly limit alone (the plan Anthropic describes as having no five-hour limit): what the panel does with a
// Claude reading that has no five-hour window. No account like that was available; the readings here are made up in the shape of
// the API's own (a null or missing "five_hour", a weekly "seven_day" or a weekly_all limit). Each check names a place that used to
// assume two windows, or that must go on treating the five-hour window as before when an account has one.
static class ClaudeWeeklyOnlyTests
{
    private static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private static ProviderSnapshot Snapshot(params Quota[] quotas) => new(ProviderId.Claude) { Quotas = [.. quotas], LiveQuota = true, QuotaTime = Now };
    private static Quota Week(double used) => new("每周额度", used, Now.AddDays(4), 10080);
    private static Quota Session(double used) => new("5 小时额度", used, Now.AddHours(3), 300);

    public static async Task Run(string root, Action<string, bool> check)
    {
        Rows(check);
        Rings(check);
        Alerts(check);
        Cache(check);
        History(check);
        await LocalReading(root, check);
        await Fallbacks(root, check);
    }

    // The rows of a usage column (UsagePanel builds one block for each metric of a page).
    static void Rows(Action<string, bool> check)
    {
        var weekly = Snapshot(Week(40));
        var (pages, slots) = QuotaDisplay.Pages(ProviderId.Claude, weekly.Metrics, 2, awaiting: false);
        check("Weekly-only column: one page with one row, the weekly one, and the column is sized for one", pages.Count == 1 && pages[0].Count == 1 && pages[0][0].Window == 10080 && slots == 1);
        check("Weekly-only column: no row is made up for the missing five-hour window", !pages.SelectMany(p => p).Any(m => m.Window == 300 || m.Label.Contains("5 小时")));
        check("Weekly-only column: before its first reading the column keeps the places of a full page", QuotaDisplay.Pages(ProviderId.Claude, [], 2, awaiting: true) is ({ Count: 1 }, 2));
        var both = Snapshot(Week(40), Session(10));
        var full = QuotaDisplay.Pages(ProviderId.Claude, both.Metrics, 2, awaiting: false);
        check("Two-window column: the five-hour row comes first, both on one page", full.Pages.Count == 1 && full.Slots == 2 && full.Pages[0].Select(m => m.Window).SequenceEqual([300, 10080]));
        var scoped = Snapshot(Week(40), new Quota("Fable 每周", 12, Now.AddDays(4), 10080));
        var withFable = QuotaDisplay.Pages(ProviderId.Claude, scoped.Metrics, 2, awaiting: false);
        check("Weekly column with a per-model weekly quota: both on one page, in order", withFable.Pages.Count == 1 && withFable.Pages[0].Count == 2 && withFable.Slots == 2);
        var merged = Snapshot(new Quota("Pro · 5 小时额度", 5, null, 300), new Quota("Pro · 每周额度", 6, null, 10080), new Quota("Ultra · 每周额度", 7, null, 10080));
        var organizations = QuotaDisplay.Pages(ProviderId.Claude, merged.Metrics, 2, awaiting: false);
        check("An account with a Pro and a weekly-only organization: each organization has its page, the second with one row",
            organizations.Pages.Count == 2 && organizations.Pages[0].Count == 2 && organizations.Pages[1].Count == 1 && organizations.Pages[1][0].Label == "Ultra · 每周额度");
        var parsed = new ProviderSnapshot(ProviderId.Claude) { Quotas = QuotaParser.Claude(Json("""{"five_hour":null,"seven_day":{"utilization":9,"resets_at":"2026-11-07T10:00:00Z"}}""")) };
        check("The API's answer with a null five-hour window builds one row", QuotaDisplay.Pages(ProviderId.Claude, parsed.Metrics, 2, false).Pages.Single().Single().Window == 10080);
    }

    // The collapsed row's ring shows the highest share among the quotas there are.
    static void Rings(Action<string, bool> check)
    {
        check("Ring: a weekly-only account shows the weekly share", QuotaDisplay.RingShare(Snapshot(Week(61)).Metrics) == 61);
        check("Ring: a weekly share of zero is zero, not the full ring of a missing reading", QuotaDisplay.RingShare(Snapshot(Week(0)).Metrics) == 0);
        check("Ring: two windows show the higher one", QuotaDisplay.RingShare(Snapshot(Session(80), Week(30)).Metrics) == 80 && QuotaDisplay.RingShare(Snapshot(Session(10), Week(30)).Metrics) == 30);
        check("Ring: no quota at all is empty", QuotaDisplay.RingShare([]) == 0);
        check("Ring: shares past the ends are held to 0 to 100", QuotaDisplay.RingShare(Snapshot(Week(140)).Metrics) == 100);
    }

    // The warning and critical alerts (MainWindow.DetectThresholds asks QuotaLevels).
    static void Alerts(Action<string, bool> check)
    {
        object row = "claude-row";
        (object, string, IEnumerable<UsageMetric>)[] Rows(params Quota[] quotas) => [(row, "claude:default", Snapshot(quotas).Metrics)];
        var levels = new QuotaLevels();
        check("Alerts: the first scan only remembers the levels, even of a weekly quota already high", levels.Scan(Rows(Week(95))) is null);
        var warned = new QuotaLevels(); warned.Scan(Rows(Week(10)));
        check("Alerts: the weekly quota rising into warning alerts, by its own name and owner", warned.Scan(Rows(Week(75))) is { Owner: var owner, Metric.Window: 10080, Used: 75 } && ReferenceEquals(owner, row));
        var weekly = new QuotaLevels(); weekly.Scan(Rows(Week(10)));
        check("Alerts: the same level is announced once", weekly.Scan(Rows(Week(75))) is not null && weekly.Scan(Rows(Week(78))) is null);
        check("Alerts: the critical level is announced after the warning", weekly.Scan(Rows(Week(92))) is { Used: 92 });
        check("Alerts: falling back and rising again alerts again", weekly.Scan(Rows(Week(20))) is null && weekly.Scan(Rows(Week(72))) is { Used: 72 });
        var pro = new QuotaLevels(); pro.Scan(Rows(Session(10), Week(10)));
        check("Alerts: an account with two windows still alerts for the five-hour one, and the higher rise wins",
            pro.Scan(Rows(Session(72), Week(10))) is { Metric.Window: 300 } && pro.Scan(Rows(Session(72), Week(95))) is { Metric.Window: 10080, Used: 95 });
        // A plan change from two windows to one: the vanished window is not alerted for, and the weekly one goes on being watched.
        var changed = new QuotaLevels(); changed.Scan(Rows(Session(95), Week(10)));
        check("Alerts: when the five-hour window disappears from the account nothing is alerted for it", changed.Scan(Rows(Week(10))) is null && changed.Scan(Rows(Week(80))) is { Metric.Window: 10080 });
        check("Alerts: a quota with no share is skipped", new QuotaLevels().Scan([(row, "k", new[] { UsageMetric.FromQuota(Week(1)) with { Percent = null } })]) is null);
    }

    // The reading kept on disk (ClaudeQuotaCache): one from before weekly-only readings, with two windows or with one.
    static void Cache(Action<string, bool> check)
    {
        var weeklyOnly = new ClaudeQuotaSnapshot(Now, "Ultra", "", "", [Week(33)]);
        var json = ClaudeQuotaCache.Serialize(weeklyOnly, "account-a", ClaudeSignIn.Code, ClaudePlanOrigin.Account);
        var back = ClaudeQuotaCache.Parse(json, Now.AddMinutes(10), "account-a");
        check("Cache: a weekly-only reading comes back as the weekly quota alone", back is { Plan: "Ultra" } && back.Quotas.Count == 1 && back.Quotas[0] is { Minutes: 10080, UsedPercent: 33 });
        var older = new ClaudeQuotaSnapshot(Now, "Max 5x", "", "", [Session(50), Week(20)]);
        var olderBack = ClaudeQuotaCache.Parse(ClaudeQuotaCache.Serialize(older, "account-a", ClaudeSignIn.Code, ClaudePlanOrigin.Account), Now.AddHours(4), "account-a");
        check("Cache: a two-window reading whose five-hour window has reset since keeps the weekly quota alone, with no zero in its place",
            olderBack is not null && olderBack.Quotas.Count == 1 && olderBack.Quotas[0].Minutes == 10080);
        check("Cache: a reading that only held a window which has reset is not used", ClaudeQuotaCache.Parse(
            ClaudeQuotaCache.Serialize(new(Now, "Pro", "", "", [Session(50)]), "account-a", ClaudeSignIn.Code, ClaudePlanOrigin.Account), Now.AddHours(4), "account-a") is null);
    }

    // The desktop application's usage history (ClaudeUsageHistory).
    static void History(Action<string, bool> check)
    {
        static string Samples(params (int MinutesAgo, int? FiveHour, int SevenDay)[] samples) => JsonSerializer.Serialize(new
        {
            version = 2,
            samples = samples.Select(s => new { t = Now.AddMinutes(-s.MinutesAgo).ToUnixTimeMilliseconds(), org = "org",
                u = s.FiveHour is { } fh ? (object)new { fh, sd = s.SevenDay } : new { sd = s.SevenDay } }).ToArray()
        });
        var weekly = ClaudeUsageHistory.Parse(Json(Samples((0, null, 22), (15, null, 21), (30, null, 20))));
        check("History: samples with a weekly share alone give a sample with no five-hour share", weekly is { FiveHour: null, SevenDay: 22 });
        check("History: such a sample's quotas are the weekly one alone", ClaudeUsageHistory.Quotas(weekly!, null, null) is [{ Minutes: 10080, UsedPercent: 22 }]);
        var full = ClaudeUsageHistory.Parse(Json(Samples((0, 37, 7), (30, 60, 6))));
        check("History: a newest sample with both shares is used as before", full is { FiveHour: 37, SevenDay: 7 } && ClaudeUsageHistory.Quotas(full, null, null).Select(q => q.Minutes).SequenceEqual([300, 10080]));
        var gap = ClaudeUsageHistory.Parse(Json(Samples((0, null, 8), (15, 41, 7), (30, 40, 7))));
        check("History: a newer sample that skips the five-hour share does not replace the five-hour quota while the account has sent one lately",
            gap is { FiveHour: 41 });
        check("History: the newest sample keeps its own time and weekly share, the five-hour share comes with its own time",
            gap is { SevenDay: 8 } && gap.Time == Now && gap.FiveHourTime == Now.AddMinutes(-15));
        check("History: a sample with its own five-hour share has no separate five-hour time", full is { FiveHourTime: null });
        // The reset known from an earlier reading against the five-hour share carried over from the older sample.
        var carried = new ClaudeUsageSample(Now, 41, 8, "", Now.AddMinutes(-15));
        check("History quotas: a reset ahead of the newest sample keeps the carried five-hour share and its reset",
            ClaudeUsageHistory.Quotas(carried, Now.AddHours(2), Now.AddDays(3)) is [{ Minutes: 300, UsedPercent: 41, ResetAt: var ahead }, { Minutes: 10080, UsedPercent: 8 }] && ahead == Now.AddHours(2));
        check("History quotas: a reset between the two samples drops the carried five-hour share, not the weekly one",
            ClaudeUsageHistory.Quotas(carried, Now.AddMinutes(-10), Now.AddDays(3)) is [{ Minutes: 10080, UsedPercent: 8 }]);
        check("History quotas: a reset at the newest sample's time has ended too", ClaudeUsageHistory.Quotas(carried, Now, null) is [{ Minutes: 10080 }]);
        check("History quotas: a reset from before the older sample is of an earlier window and does not end it",
            ClaudeUsageHistory.Quotas(carried, Now.AddMinutes(-40), null) is [{ Minutes: 300, UsedPercent: 41, ResetAt: null }, { Minutes: 10080 }]);
        check("History quotas: a five-hour share from the newest sample itself is shown whatever reset was known",
            ClaudeUsageHistory.Quotas(carried with { FiveHourTime = null }, Now.AddMinutes(-10), null) is [{ Minutes: 300, UsedPercent: 41, ResetAt: null }, { Minutes: 10080 }]);
        var edge = ClaudeUsageHistory.Parse(Json(Samples((0, null, 8), (6 * 60, 41, 7))));
        var past = ClaudeUsageHistory.Parse(Json(Samples((0, null, 8), (6 * 60 + 1, 41, 7))));
        check("History: the gap is six hours; past it the account is taken to have no five-hour window", edge is { FiveHour: 41 } && past is { FiveHour: null, SevenDay: 8 });
        check("History: samples without any share, or without a time, are skipped", ClaudeUsageHistory.Parse(Json("""{"samples":[{"t":1,"u":{}},{"u":{"sd":5}}]}""")) is null);
        check("History: a share of the five-hour window of zero is a real zero", ClaudeUsageHistory.Parse(Json(Samples((0, 0, 3)))) is { FiveHour: 0 });
    }

    // The fallback after a rate limit when the last reading had a five-hour window (UsageService.Freshest): an online reading at t0
    // with the five-hour window resetting at t0 + fiveHourReset, then the desktop history, then a 429.
    static async Task<IReadOnlyList<Quota>> Fallback(string root, string name, Action<string, bool> check, double fiveHourResetMinutes, (int Minute, int? FiveHour, int SevenDay)[] history, int minutesLater, DateTimeOffset[] times)
    {
        var home = Path.Combine(root, "claude-fallback-" + name);
        var locations = DataLocations.Resolve(Path.Combine(home, "home"), Path.Combine(home, "roaming"), Path.Combine(home, "local"), null, "");
        var desktop = Path.Combine(locations.Roaming, "Claude");
        Directory.CreateDirectory(desktop); Directory.CreateDirectory(locations.ClaudeHome);
        File.WriteAllText(Path.Combine(locations.ClaudeHome, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"test-fallback","subscriptionType":"pro"}}""");
        var clock = Now;
        bool limited = false;
        using var handler = new ClaudeQuotaHandler
        {
            Override = (_, _) => limited ? new(System.Net.HttpStatusCode.TooManyRequests) : new(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { five_hour = new { utilization = 30, resets_at = Now.AddMinutes(fiveHourResetMinutes) }, seven_day = new { utilization = 10, resets_at = Now.AddDays(3) } })) }
        };
        using var client = new HttpClient(handler);
        using var service = new UsageService(new ClaudeFixtureSources(), locations, client, () => clock);
        var settings = new DeckSettings { Apps = [new AppEntry { Id = ProviderId.Claude }] };
        clock = Now.AddSeconds(30);
        var online = (await service.RefreshAsync(settings)).Single(s => !s.Configurations.Contains(ProviderCatalog.LocalQuota));
        check($"Fallback ({name}): the online reading has both windows", online.Quotas.Count == 2 && online.Quotas.Any(q => q.Minutes == 300 && q.UsedPercent == 30));
        limited = true;
        File.WriteAllText(Path.Combine(desktop, "plan-usage-history.json"), JsonSerializer.Serialize(new
        {
            version = 2,
            samples = history.Select(s => new { t = Now.AddMinutes(s.Minute).ToUnixTimeMilliseconds(), org = "org", u = s.FiveHour is { } fh ? (object)new { fh, sd = s.SevenDay } : new { sd = s.SevenDay } }).ToArray()
        }));
        clock = Now.AddMinutes(minutesLater);
        var result = (await service.RefreshAsync(settings)).Single(s => !s.Configurations.Contains(ProviderCatalog.LocalQuota));
        times[0] = result.QuotaTime ?? default;
        check($"Fallback ({name}): the quota interface was limited and the history stands in", result.IsStale && result.LiveQuota);
        return result.Quotas;
    }

    static async Task Fallbacks(string root, Action<string, bool> check)
    {
        var time = new DateTimeOffset[1];
        // No five-hour share in the newest sample, none for hours before it: a five-hour window the last reading still has ahead stays.
        var kept = await Fallback(root, "kept", check, 180, [(10, null, 11)], 11, time);
        check("Fallback: a newest sample without a five-hour share keeps the five-hour quota the last reading still has ahead, and takes the new weekly share",
            kept.Count == 2 && kept[0] is { Minutes: 300, UsedPercent: 30 } && kept[1] is { Minutes: 10080, UsedPercent: 11 } && time[0] == Now.AddMinutes(10));
        // The same, but the window has ended by the newest sample: none is made up.
        var ended = await Fallback(root, "ended", check, 60, [(10, null, 11)], 90, time);
        check("Fallback: a five-hour window that has ended is not kept, and the account then has the weekly quota alone", ended.Count == 1 && ended[0] is { Minutes: 10080, UsedPercent: 11 });
        // A skipped five-hour share within hours: the share of the older sample is carried, the newest sample's time and weekly share stand.
        var carried = await Fallback(root, "carried", check, 180, [(30, 60, 10), (90, null, 12)], 95, time);
        check("Fallback: a skipped five-hour share carries the older share, with the newest sample's weekly share and time",
            carried.Count == 2 && carried[0] is { Minutes: 300, UsedPercent: 60 } && carried[1] is { Minutes: 10080, UsedPercent: 12 } && time[0] == Now.AddMinutes(90));
        // The window reset between the two samples: the older share belongs to the ended window and is not shown as the present one.
        var between = await Fallback(root, "between", check, 60, [(30, 60, 10), (90, null, 12)], 95, time);
        check("Fallback: a five-hour reset between the two samples drops the older five-hour share and keeps the new weekly share and time",
            between.Count == 1 && between[0] is { Minutes: 10080, UsedPercent: 12 } && time[0] == Now.AddMinutes(90));
    }

    // The service's offline reading and its fallback after the quota interface failed, by the real code path.
    static async Task LocalReading(string root, Action<string, bool> check)
    {
        var home = Path.Combine(root, "claude-weekly-only");
        var locations = DataLocations.Resolve(Path.Combine(home, "home"), Path.Combine(home, "roaming"), Path.Combine(home, "local"), null, "");
        var desktop = Path.Combine(locations.Roaming, "Claude");
        Directory.CreateDirectory(desktop);
        var clock = Now;
        void Write(int? fiveHour, int sevenDay) => File.WriteAllText(Path.Combine(desktop, "plan-usage-history.json"), JsonSerializer.Serialize(new
        {
            version = 2,
            samples = new[] { new { t = clock.AddMinutes(-1).ToUnixTimeMilliseconds(), org = "org", u = fiveHour is { } fh ? (object)new { fh, sd = sevenDay } : new { sd = sevenDay } } }
        }));
        using var handler = new ClaudeQuotaHandler { Override = (_, _) => new(System.Net.HttpStatusCode.TooManyRequests) };
        using var client = new HttpClient(handler);
        using var service = new UsageService(new ClaudeFixtureSources(), locations, client, () => clock);
        DeckSettings Row(bool online) => new() { Apps = [new AppEntry { Id = ProviderId.Claude, QuotaOnline = online }] };

        Write(null, 18);
        clock = clock.AddSeconds(60);
        var offline = (await service.RefreshAsync(Row(false))).Single(s => s.Configurations.Contains(ProviderCatalog.LocalQuota));
        check("Offline reading: a history with a weekly share alone gives one weekly quota and no five-hour one",
            offline.LiveQuota && offline.Quotas.Count == 1 && offline.Quotas[0] is { Minutes: 10080, UsedPercent: 18 } && offline.Metrics.Count == 1);
        Write(30, 18);
        clock = clock.AddSeconds(60);
        offline = (await service.RefreshAsync(Row(false))).Single(s => s.Configurations.Contains(ProviderCatalog.LocalQuota));
        check("Offline reading: a history with both shares gives both, five-hour first", offline.Quotas.Select(q => q.Minutes).SequenceEqual([300, 10080]) && offline.Quotas[0].UsedPercent == 30);

        // The quota interface limits the request and there is no earlier reading: the history stands in (UsageService.Freshest).
        Directory.CreateDirectory(locations.ClaudeHome);
        File.WriteAllText(Path.Combine(locations.ClaudeHome, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"test-limited","subscriptionType":"ultra"}}""");
        Write(null, 19);
        clock = clock.AddSeconds(60);
        var limited = (await service.RefreshAsync(Row(true))).Single(s => !s.Configurations.Contains(ProviderCatalog.LocalQuota));
        check("Fallback after a rate limit: a weekly-only history stands in with the weekly quota alone",
            limited.LiveQuota && limited.IsStale && limited.Quotas.Count == 1 && limited.Quotas[0] is { Minutes: 10080, UsedPercent: 19 });
    }

}
