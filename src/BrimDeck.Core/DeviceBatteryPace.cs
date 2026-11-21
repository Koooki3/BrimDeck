using System.Text.Json;

namespace BrimDeck.Core;

// Time left and time to full for a wireless device, worked out from how its percent moved while BrimDeck was reading it
// (DeviceBatteryPace). Null where there is not yet enough of a slope, or the reading is too coarse to trust.
public readonly record struct DeviceBatteryEstimate(TimeSpan? TimeLeft, TimeSpan? TimeToFull);

// Remembers recent charge readings per device while the setting is on, and turns a same-mode slope into an estimate. Fed from
// the devices' existing reports only: it never asks a device for anything further. Kept across runs in device-battery-pace.json
// (DeviceBatteryPaceStore) while the setting stays on; cleared when it is turned off.
public sealed class DeviceBatteryPace
{
    public const int MaxSamples = 48;
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
    // A discharge that has barely moved is noise on a mouse that reports in whole percents; charging usually moves faster.
    public static readonly TimeSpan MinDischargeSpan = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinChargeSpan = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Longest = BatteryModel.LongestTime;

    private sealed class SampleRow
    {
        public DateTimeOffset At { get; set; }
        public int Percent { get; set; }
        public bool Charging { get; set; }
    }
    private sealed class DeviceRow
    {
        public string Key { get; set; } = "";
        public List<SampleRow> Samples { get; set; } = [];
    }

    private readonly record struct Sample(DateTimeOffset At, int Percent, bool Charging);

    private readonly Dictionary<string, List<Sample>> _series = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    // Set when the kept samples changed; cleared by Serialize.
    public bool Dirty { get; private set; }

    // Drops every kept reading. The file is erased by DeviceBatteryPaceStore when the setting is turned off.
    public void Clear()
    {
        lock (_gate)
        {
            _series.Clear();
            Dirty = false;
        }
    }

    // Replaces what is kept with a file's contents (Start), trimmed to what is still worth keeping.
    public void Load(string? json, DateTimeOffset now)
    {
        lock (_gate)
        {
            _series.Clear();
            foreach (var (key, list) in Parse(json, now)) _series[key] = list;
            Dirty = false;
        }
    }

    // Records a sample when a device's percent or charging state changed. Earlier / asleep / stepped readings are skipped.
    public void Observe(IReadOnlyList<DeviceBattery> devices, DateTimeOffset now)
    {
        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var device in devices)
            {
                if (!Eligible(device)) continue;
                string key = device.Key;
                seen.Add(key);
                int percent = Devices.Clamp(device.Lowest!.Value);
                bool charging = device.Charging;
                if (!_series.TryGetValue(key, out var list)) _series[key] = list = [];
                if (Trim(list, now)) Dirty = true;
                if (list.Count > 0)
                {
                    var last = list[^1];
                    if (last.Percent == percent && last.Charging == charging) continue;
                }
                list.Add(new Sample(now, percent, charging));
                if (list.Count > MaxSamples) list.RemoveRange(0, list.Count - MaxSamples);
                Dirty = true;
            }
            foreach (var key in _series.Keys.Where(key => !seen.Contains(key)).ToList())
            {
                if (Trim(_series[key], now)) Dirty = true;
                if (_series[key].Count == 0) { _series.Remove(key); Dirty = true; }
            }
        }
    }

    public DeviceBatteryEstimate Of(DeviceBattery device, DateTimeOffset now)
    {
        if (!Eligible(device)) return default;
        lock (_gate)
        {
            if (!_series.TryGetValue(device.Key, out var list)) return default;
            Trim(list, now);
            bool charging = device.Charging;
            // The latest run of samples in the device's present mode (charging or not); a plug or unplug starts a new slope.
            var points = new List<Sample>();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Charging != charging) break;
                points.Add(list[i]);
            }
            if (points.Count < 2) return default;
            points.Reverse();
            var last = points[^1];
            Sample? first = null;
            foreach (var sample in points)
            {
                if (sample.Percent != last.Percent) { first = sample; break; }
            }
            if (first is not { } start) return default;
            var span = last.At - start.At;
            if (span < (charging ? MinChargeSpan : MinDischargeSpan) || span.TotalHours <= 0) return default;
            double rate = (last.Percent - start.Percent) / span.TotalHours;
            int percent = Devices.Clamp(device.Lowest!.Value);
            if (charging)
            {
                if (rate < 0.5 || percent >= 100) return default;
                return new(null, Bounded(TimeSpan.FromHours((100 - percent) / rate)));
            }
            if (rate > -0.05 || percent <= 0) return default;
            return new(Bounded(TimeSpan.FromHours(percent / -rate)), null);
        }
    }

    // The detail line's estimate, or that readings are still being collected while the setting is on; null when this device
    // cannot be estimated (no level, asleep, earlier, or stepped).
    public string? Status(DeviceBattery device, DateTimeOffset now)
    {
        if (!Eligible(device)) return null;
        var estimate = Of(device, now);
        if (estimate.TimeToFull is { } full)
            return Loc.T("约 " + PowerFormat.Duration(full) + " 充满", "~" + PowerFormat.Duration(full) + " to full");
        if (estimate.TimeLeft is { } left)
            return Loc.T("约 " + PowerFormat.Duration(left) + " 可用", "~" + PowerFormat.Duration(left) + " left");
        return Loc.T("正在收集电量数据…", "Collecting battery data…");
    }

    public string Serialize()
    {
        lock (_gate)
        {
            Dirty = false;
            var rows = _series.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new DeviceRow
                {
                    Key = pair.Key,
                    Samples = [.. pair.Value.Select(sample => new SampleRow { At = sample.At, Percent = sample.Percent, Charging = sample.Charging })]
                }).ToList();
            return JsonSerializer.Serialize(rows, Options);
        }
    }

    public static DeviceBatteryPace FromFile(string? json, DateTimeOffset now)
    {
        var pace = new DeviceBatteryPace();
        pace.Load(json, now);
        return pace;
    }

    private static Dictionary<string, List<Sample>> Parse(string? json, DateTimeOffset now)
    {
        var series = new Dictionary<string, List<Sample>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return series;
        try
        {
            var rows = JsonSerializer.Deserialize<List<DeviceRow>>(json, Options) ?? [];
            foreach (var row in rows.Where(row => row is { Key.Length: > 0 }).Take(256))
            {
                var list = new List<Sample>();
                foreach (var sample in (row.Samples ?? []).Where(sample => sample.Percent is >= 0 and <= 100).Take(MaxSamples))
                    list.Add(new Sample(sample.At, sample.Percent, sample.Charging));
                list.Sort((a, b) => a.At.CompareTo(b.At));
                Trim(list, now);
                if (list.Count > MaxSamples) list.RemoveRange(0, list.Count - MaxSamples);
                if (list.Count > 0) series[row.Key] = list;
            }
        }
        catch (JsonException) { }
        return series;
    }

    private static bool Eligible(DeviceBattery device)
        => device.Lowest is not null && !device.Earlier && !device.Asleep && device.Accuracy != BatteryAccuracy.Steps;

    private static bool Trim(List<Sample> list, DateTimeOffset now)
    {
        var keep = now - KeepFor;
        int drop = 0;
        while (drop < list.Count && list[drop].At < keep) drop++;
        if (drop == 0) return false;
        list.RemoveRange(0, drop);
        return true;
    }

    private static TimeSpan? Bounded(TimeSpan time) => time > TimeSpan.Zero && time <= Longest ? time : null;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
