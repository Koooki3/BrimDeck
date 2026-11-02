using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

// One reading of a merged Cursor plan's weekly included usage: the amount used (UsedCents) held from First to Last within
// the week that began at PeriodStart. Tokens is the included tokens of that week up to Last, by kind (input, output, cache
// read, cache write), filled in once the records up to Last are known; Checked marks a reading that was looked at for it,
// whether or not it could be used.
public sealed record CursorSpendSample(DateTimeOffset PeriodStart, long UsedCents, DateTimeOffset First, DateTimeOffset Last)
{
    public long[]? Tokens { get; init; }
    public bool Checked { get; init; }
}

// A merged Cursor plan prices nothing per record: its billing items charge included usage at zero, and only the week's
// included usage as a whole has an amount (usage-status, whole cents). Each reading of that amount is the sum of the
// week's included tokens up to then, times a fixed price per token kind, so readings over time, across weeks, fix those
// prices by least squares, as Pricing.FromReported does with per-record amounts. Readings and prices are kept on disk,
// because the amount starts again from zero every week and the readings of a week cannot be had later.
public sealed class CursorPrices
{
    public const string FileName = "cursor-prices.json";
    // Readings older than this are dropped, so prices that changed are soon worked out again.
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(28);
    // The amount trails the records: a reading counts only when no record came within this time before it.
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(3);
    // A reading's records are taken as complete once a day read this long after the reading has them.
    public static readonly TimeSpan RecordsLate = TimeSpan.FromMinutes(10);
    // Below this amount, whole-cent rounding fits any prices.
    public const long MinimumCents = 20;
    private const int MaxSamples = 5000;

    private sealed record Stored(string? Account, List<StoredSample?>? Samples, StoredPrices? Prices);
    private sealed record StoredSample(DateTimeOffset PeriodStart, long UsedCents, DateTimeOffset First, DateTimeOffset Last, long[]? Tokens, bool Checked);
    private sealed record StoredPrices(decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite);

    private readonly string? _path;
    private string _account = "";
    private bool _loaded, _changed;
    public List<CursorSpendSample> Samples { get; private set; } = [];
    public TokenPrices? Prices { get; private set; }

    public CursorPrices(string? path) => _path = path;

    // A one-way hash of the account id, enough to tell two accounts apart without naming either.
    public static string AccountHash(string account) => string.IsNullOrWhiteSpace(account) ? ""
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("BrimDeck Cursor account\n" + account.Trim())))[..32];

    // Switches to the account, reading what the file keeps for it; another account's readings are dropped.
    public void Use(string account)
    {
        var hash = AccountHash(account);
        if (_loaded && hash == _account) return;
        _loaded = true; _account = hash; Samples = []; Prices = null;
        if (hash.Length == 0 || _path is null) return;
        try
        {
            if (!File.Exists(_path)) return;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
            if (stored?.Account != hash) return;
            Samples = (stored.Samples ?? []).Where(s => s is not null && s.UsedCents >= 0 && s.Last >= s.First && (s.Tokens is null || s.Tokens.Length == 4))
                .Select(s => new CursorSpendSample(s!.PeriodStart, s.UsedCents, s.First, s.Last) { Tokens = s.Tokens, Checked = s.Checked }).OrderBy(s => s.First).ToList();
            if (stored.Prices is { } p && p.Input >= 0 && p.Output >= 0) Prices = new(p.Input, p.Output, p.CacheRead, p.CacheWrite, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    // A new reading extends the last one while the week and the amount stay the same.
    public void Add(DateTimeOffset periodStart, long usedCents, DateTimeOffset time)
    {
        if (_account.Length == 0) return;
        var last = Samples.Count > 0 ? Samples[^1] : null;
        if (last is not null && last.PeriodStart == periodStart && last.UsedCents == usedCents && time >= last.Last && !last.Checked)
            Samples[^1] = last with { Last = time };
        else Samples.Add(new(periodStart, usedCents, time, time));
        _changed = true;
    }

    // Fills in the tokens of each reading whose records are known, then works out the prices again. entries are the
    // included records (no amount of their own) of the days in covered, each with the time that day was last read.
    public void Update(IReadOnlyCollection<TokenEntry> entries, Func<DateTimeOffset, DateTimeOffset, bool> covered, DateTimeOffset now)
    {
        if (_account.Length == 0) return;
        int before = Samples.Count;
        Samples.RemoveAll(s => now - s.Last > KeepFor);
        if (Samples.Count > MaxSamples) Samples.RemoveRange(0, Samples.Count - MaxSamples);
        _changed |= Samples.Count != before;
        for (int i = 0; i < Samples.Count; i++)
        {
            var sample = Samples[i];
            // The newest reading may still be extended; it is used once a later one has begun or it has stood for a while.
            if (sample.Checked || (i == Samples.Count - 1 && now - sample.Last < RecordsLate) || !covered(sample.PeriodStart, sample.Last)) continue;
            var week = entries.Where(e => e.Time >= sample.PeriodStart && e.Time <= sample.Last).ToList();
            bool settled = !week.Any(e => e.Time > sample.Last - Settle);
            Samples[i] = sample with { Checked = true, Tokens = settled ? [week.Sum(e => e.Input), week.Sum(e => e.Output), week.Sum(e => e.CacheRead), week.Sum(e => e.CacheWrite + e.CacheWriteHour)] : null };
            _changed = true;
        }
        if (Fit(Samples.Where(s => s.Tokens is not null).Select(s => (s.Tokens!, (double)s.UsedCents)).ToList()) is { } prices && prices != Prices)
        { Prices = prices; _changed = true; }
    }

    // The amount of an included record at the worked-out prices; null until there are prices.
    public decimal? Cost(TokenEntry entry) => Prices is not { } p ? null
        : entry.Input * p.Input + entry.Output * p.Output + entry.CacheRead * (p.CacheRead ?? 0) + (entry.CacheWrite + entry.CacheWriteHour) * (p.CacheWrite ?? 0);

    // Written whole and then moved into place, so an exit midway never leaves half a file. A failed write keeps the previous file.
    public void Save()
    {
        if (!_changed || _path is null || _account.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var stored = new Stored(_account, Samples.Select(s => (StoredSample?)new StoredSample(s.PeriodStart, s.UsedCents, s.First, s.Last, s.Tokens, s.Checked)).ToList(),
                Prices is { } p ? new StoredPrices(p.Input, p.Output, p.CacheRead, p.CacheWrite) : null);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored));
            File.Move(temporary, _path, true);
            _changed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Prices in USD per token from readings of (tokens by kind, amount in cents). Kinds that share their records in nearly
    // the same proportions cannot be told apart; a small ridge term then splits them evenly rather than failing, which
    // still prices each record close to its share. Each kind's price must be zero or more, so every subset of kinds is
    // tried and the best fit with none below zero is kept. The fit must reproduce every reading to within its rounding
    // to whole cents and a small share; otherwise the prices are not fixed and none are given.
    public static TokenPrices? Fit(IReadOnlyList<(long[] Tokens, double Cents)> rows)
    {
        if (rows.Count == 0 || rows.Max(r => r.Cents) < MinimumCents) return null;
        var used = Enumerable.Range(0, 4).Where(c => rows.Any(r => r.Tokens[c] > 0)).ToArray();
        if (used.Length == 0 || rows.Select(r => string.Join(",", r.Tokens)).Distinct().Count() < used.Length + 2) return null;
        var scale = used.Select(c => (double)rows.Max(r => r.Tokens[c])).ToArray();
        double[]? best = null; double bestError = double.MaxValue;
        for (int mask = 1; mask < 1 << used.Length; mask++)
        {
            var active = Enumerable.Range(0, used.Length).Where(i => (mask & 1 << i) != 0).ToArray();
            if (Solve(rows, used, scale, active) is not { } solved || solved.Any(p => p < 0)) continue;
            var prices = new double[used.Length];
            for (int i = 0; i < active.Length; i++) prices[active[i]] = solved[i];
            double error = rows.Sum(r => Math.Pow(Predict(r.Tokens, used, scale, prices) - r.Cents, 2));
            if (error < bestError) { bestError = error; best = prices; }
        }
        if (best is null || rows.Any(r => Math.Abs(Predict(r.Tokens, used, scale, best) - r.Cents) > 1 + 0.03 * r.Cents)) return null;
        decimal? Price(int kind) => Array.IndexOf(used, kind) is var i and >= 0 ? (decimal)Math.Round(best[i] / scale[i] / 100 * 1e12) / 1e12m : null;
        return new(Price(0) ?? 0, Price(1) ?? 0, Price(2), Price(3), null);
    }

    private static double Predict(long[] tokens, int[] used, double[] scale, double[] prices) => used.Select((c, i) => tokens[c] / scale[i] * prices[i]).Sum();

    // Least squares over the active kinds, with each column scaled to at most 1 and a ridge term of a millionth of the
    // largest diagonal entry.
    private static double[]? Solve(IReadOnlyList<(long[] Tokens, double Cents)> rows, int[] used, double[] scale, int[] active)
    {
        int k = active.Length;
        var a = new double[k, k + 1];
        foreach (var (tokens, cents) in rows)
        {
            var x = active.Select(i => tokens[used[i]] / scale[i]).ToArray();
            for (int i = 0; i < k; i++)
            {
                for (int j = 0; j < k; j++) a[i, j] += x[i] * x[j];
                a[i, k] += x[i] * cents;
            }
        }
        double ridge = 1e-6 * Enumerable.Range(0, k).Max(i => a[i, i]);
        if (ridge <= 0) return null;
        for (int i = 0; i < k; i++) a[i, i] += ridge;
        for (int p = 0; p < k; p++)
        {
            int pivot = p;
            for (int r = p + 1; r < k; r++) if (Math.Abs(a[r, p]) > Math.Abs(a[pivot, p])) pivot = r;
            for (int c = 0; c <= k; c++) (a[p, c], a[pivot, c]) = (a[pivot, c], a[p, c]);
            for (int r = 0; r < k; r++)
                if (r != p) { double f = a[r, p] / a[p, p]; for (int c = p; c <= k; c++) a[r, c] -= f * a[p, c]; }
        }
        return Enumerable.Range(0, k).Select(i => a[i, k] / a[i, i]).ToArray();
    }
}
