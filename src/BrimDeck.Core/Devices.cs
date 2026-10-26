namespace BrimDeck.Core;

// The devices page: the battery of the wireless keyboards, mice, headphones, controllers and pens connected to the PC, from
// whatever reports it (DeviceSource). Every source gives readings in this one shape; the roster merges them, one entry per
// device, in a steady order, so the page never reorders itself as readings come and go.

// What a device is, for its icon and its place in the order. Not saved.
public enum DeviceKind { Mouse, Keyboard, Headphones, Earbuds, Controller, Pen, Speaker, Other }

// Where a reading comes from, roughly from the most to the least exact. A device seen by more than one source keeps the
// reading of the first in this order that has a percentage (DeviceRoster.Merge).
public enum DeviceSource { Logitech, Razer, SteelSeries, HyperX, Sony, Apple, Gatt, Gaming, Hid, Bluetooth }

// The parts of earbuds that report their own charge.
public enum BatteryPart { Left, Right, Case }
public readonly record struct PartBattery(BatteryPart Part, int? Percent, bool Charging);

// One device's reading. Key is the device's own: "bt:<address>" for anything Bluetooth (12 hex digits, upper case), so the
// several Bluetooth sources meet on one entry; other sources key by what identifies the device on its link. Percent is null
// while the device is connected but reports no battery ("not available"); Coarse marks a level the device reports in a few
// steps (a voltage or a four-step level), shown as such. Address is the Bluetooth address when the source knows it, which
// lets a vendor source's entry replace the plain Bluetooth one.
public sealed record DeviceBattery(string Key, string Name, DeviceKind Kind, DeviceSource Source, int? Percent)
{
    public bool Charging { get; init; }
    public bool Coarse { get; init; }
    public string? Address { get; init; }
    // "VVVV:PPPP", the USB or Bluetooth vendor and product, when the source knows them: a controller seen both as a game
    // controller and as a Bluetooth device is one device.
    public string? Hardware { get; init; }
    public IReadOnlyList<PartBattery> Parts { get; init; } = [];
    // The lowest of the parts with a reading (the earbuds, not the case), or the device's own.
    public int? Lowest => Parts.Where(part => part.Part != BatteryPart.Case && part.Percent is not null).Select(part => part.Percent).Min() ?? Percent;
    public bool Same(DeviceBattery other) => Key == other.Key && Name == other.Name && Kind == other.Kind && Source == other.Source && Percent == other.Percent
        && Charging == other.Charging && Coarse == other.Coarse && Parts.SequenceEqual(other.Parts);
}

public static class Devices
{
    public const int DefaultLowPercent = 15;
    // The low battery threshold the settings offer.
    public static readonly IReadOnlyList<int> LowChoices = [5, 10, 15, 20, 25, 30];

    // A reading's level: critical at or under the low threshold, elevated within ten points above it, as the collapsed readings'
    // levels color it. Charging is never low.
    public static MemoryLevel Level(int? percent, bool charging, int low)
        => percent is not { } p || charging ? MemoryLevel.Normal : p <= low ? MemoryLevel.Critical : p <= low + 10 ? MemoryLevel.Elevated : MemoryLevel.Normal;

    public static int Clamp(int percent) => Math.Clamp(percent, 0, 100);

    // The kind from what the device says of itself, best first: its Bluetooth category or appearance, then its name.
    public static DeviceKind Kind(string? category, int? appearance, string? name)
    {
        if (category is { Length: > 0 })
        {
            if (category.Contains("Mouse", StringComparison.OrdinalIgnoreCase) || category.Contains("Pointing", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Mouse;
            if (category.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Keyboard;
            if (category.Contains("Gaming", StringComparison.OrdinalIgnoreCase) || category.Contains("Gamepad", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Controller;
            if (category.Contains("Pen", StringComparison.OrdinalIgnoreCase) || category.Contains("Digitizer", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Pen;
            if (category.Contains("Speaker", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Speaker;
            if (category.Contains("Head", StringComparison.OrdinalIgnoreCase) || category.Contains("Audio", StringComparison.OrdinalIgnoreCase))
                return Named(name) is DeviceKind.Earbuds ? DeviceKind.Earbuds : DeviceKind.Headphones;
        }
        if (appearance is { } a && Appearance(a) is { } byAppearance) return byAppearance == DeviceKind.Headphones && Named(name) == DeviceKind.Earbuds ? DeviceKind.Earbuds : byAppearance;
        return Named(name) ?? DeviceKind.Other;
    }

    // The Bluetooth appearance's category (its upper ten bits) and the HID subcategories (Bluetooth Assigned Numbers, 2.6).
    public static DeviceKind? Appearance(int appearance) => (appearance >> 6) switch
    {
        0x00F => appearance switch { 0x03C1 => DeviceKind.Keyboard, 0x03C2 => DeviceKind.Mouse, 0x03C3 or 0x03C4 => DeviceKind.Controller, 0x03C5 or 0x03C7 => DeviceKind.Pen, _ => null },
        // Wearable audio: earbud, then headset, headphones and neck band.
        0x025 => appearance == 0x0941 ? DeviceKind.Earbuds : DeviceKind.Headphones,
        // Audio sink: speakers and sound bars.
        0x021 => DeviceKind.Speaker,
        _ => null
    };

    private static readonly (string Word, DeviceKind Kind)[] Words =
    [
        ("airpods", DeviceKind.Earbuds), ("buds", DeviceKind.Earbuds), ("earbud", DeviceKind.Earbuds), ("enco", DeviceKind.Earbuds), ("freebuds", DeviceKind.Earbuds),
        ("耳塞", DeviceKind.Earbuds), ("真无线", DeviceKind.Earbuds), ("tws", DeviceKind.Earbuds), ("powerbeats", DeviceKind.Earbuds), ("wf-", DeviceKind.Earbuds),
        ("headset", DeviceKind.Headphones), ("headphone", DeviceKind.Headphones), ("耳机", DeviceKind.Headphones), ("wh-", DeviceKind.Headphones), ("arctis", DeviceKind.Headphones),
        ("cloud", DeviceKind.Headphones), ("bose", DeviceKind.Headphones), (" qc", DeviceKind.Headphones), ("void", DeviceKind.Headphones), ("kraken", DeviceKind.Headphones),
        ("keyboard", DeviceKind.Keyboard), ("键盘", DeviceKind.Keyboard), ("kbd", DeviceKind.Keyboard), (" keys", DeviceKind.Keyboard),
        ("mouse", DeviceKind.Mouse), ("鼠标", DeviceKind.Mouse), ("trackball", DeviceKind.Mouse), ("mx master", DeviceKind.Mouse), ("mx anywhere", DeviceKind.Mouse),
        ("controller", DeviceKind.Controller), ("gamepad", DeviceKind.Controller), ("手柄", DeviceKind.Controller), ("dualsense", DeviceKind.Controller),
        ("dualshock", DeviceKind.Controller), ("xbox", DeviceKind.Controller), ("joy-con", DeviceKind.Controller), ("pro controller", DeviceKind.Controller),
        (" pen ", DeviceKind.Pen), ("手写笔", DeviceKind.Pen), ("触控笔", DeviceKind.Pen), ("stylus", DeviceKind.Pen), ("pencil", DeviceKind.Pen),
        ("speaker", DeviceKind.Speaker), ("音箱", DeviceKind.Speaker), ("soundlink", DeviceKind.Speaker), ("jbl flip", DeviceKind.Speaker), ("jbl charge", DeviceKind.Speaker),
    ];

    // The kind a name suggests, if any.
    public static DeviceKind? Named(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        // Padded, so a word given with a space (" pen ") matches only as a whole word.
        var lower = " " + name.ToLowerInvariant() + " ";
        foreach (var (word, kind) in Words) if (lower.Contains(word, StringComparison.Ordinal)) return kind;
        return null;
    }

    // The order of the kinds on the page: what is held in the hand first.
    public static int Rank(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => 0, DeviceKind.Keyboard => 1, DeviceKind.Headphones => 2, DeviceKind.Earbuds => 3, DeviceKind.Controller => 4, DeviceKind.Pen => 5,
        DeviceKind.Speaker => 6, _ => 7
    };

    public static string KindName(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => Loc.T("鼠标", "Mouse"), DeviceKind.Keyboard => Loc.T("键盘", "Keyboard"), DeviceKind.Headphones => Loc.T("头戴耳机", "Headphones"),
        DeviceKind.Earbuds => Loc.T("入耳耳机", "Earbuds"), DeviceKind.Controller => Loc.T("手柄", "Controller"), DeviceKind.Pen => Loc.T("笔", "Pen"),
        DeviceKind.Speaker => Loc.T("音箱", "Speaker"), _ => Loc.T("设备", "Device")
    };

    public static string SourceName(DeviceSource source) => source switch
    {
        DeviceSource.Logitech => Loc.T("罗技接收器", "Logitech receiver"), DeviceSource.Razer => "Razer", DeviceSource.SteelSeries => "SteelSeries",
        DeviceSource.HyperX => "HyperX", DeviceSource.Sony => "PlayStation", DeviceSource.Gatt => Loc.T("蓝牙电池服务", "Bluetooth battery service"),
        DeviceSource.Gaming => Loc.T("游戏控制器", "Game controller"), DeviceSource.Hid => Loc.T("HID 电量", "HID battery"), DeviceSource.Apple => Loc.T("Apple 广播", "Apple broadcast"),
        _ => Loc.T("蓝牙", "Bluetooth")
    };

    public static string PartName(BatteryPart part) => part switch { BatteryPart.Left => Loc.T("左", "L"), BatteryPart.Right => Loc.T("右", "R"), _ => Loc.T("盒", "Case") };

    // The figure as shown: "85%", or "—" for a device that reports no battery.
    public static string Figure(int? percent) => percent is { } p ? Clamp(p).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" : "—";

    // A line for the tooltip and the settings: "左 80% · 右 75% · 盒 60% · 充电中 · 蓝牙".
    public static string Describe(DeviceBattery device)
    {
        var parts = new List<string>();
        if (device.Parts.Count > 0) parts.AddRange(device.Parts.Select(part => PartName(part.Part) + " " + Figure(part.Percent) + (part.Charging ? Loc.T("（充电）", " (charging)") : "")));
        else parts.Add(device.Percent is null ? Loc.T("未报告电量", "No battery reported") : Figure(device.Percent) + (device.Coarse ? Loc.T("（约）", " (approx.)") : ""));
        if (device.Charging) parts.Add(Loc.T("充电中", "Charging"));
        parts.Add(SourceName(device.Source));
        return device.Name + "\n" + string.Join(" · ", parts);
    }

    // The steady order: by kind, then by name, then by key.
    public static IComparer<DeviceBattery> Order { get; } = Comparer<DeviceBattery>.Create((a, b) =>
    {
        int c = Rank(a.Kind).CompareTo(Rank(b.Kind));
        if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
    });

    // "045E:0B13" from an id carrying "VID&02045e_PID&0b13" (Bluetooth LE), "VID&0002054c_PID&0d58" (classic) or "VID_046D&PID_C52B" (USB).
    public static string? HardwareIn(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(id, @"VID[&_][0-9A-Fa-f]*?([0-9A-Fa-f]{4})[_&]PID[&_]([0-9A-Fa-f]{4})");
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() + ":" + match.Groups[2].Value.ToUpperInvariant() : null;
    }
    public static string Hardware(int vendor, int product) => $"{vendor & 0xFFFF:X4}:{product & 0xFFFF:X4}";

    // A Bluetooth address from a device instance or interface path ("..._cd5994078968\..." or "Dev_CD5994078968"); null when
    // there is none.
    public static string? AddressIn(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        // The last run of exactly twelve hex digits that stands alone (after "_", "&", "Dev_" or ":"-separated in an AEP id).
        // An endpoint id names the radio first and the device last ("Bluetooth#Bluetooth<radio>-<device>").
        var colon = System.Text.RegularExpressions.Regex.Matches(id, @"([0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5})(?![0-9A-Fa-f:])");
        if (colon.Count > 0) return colon[^1].Groups[1].Value.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant();
        var runs = System.Text.RegularExpressions.Regex.Matches(id, @"(?<=[_&\\])([0-9A-Fa-f]{12})(?![0-9A-Fa-f])");
        return runs.Count > 0 ? runs[^1].Groups[1].Value.ToUpperInvariant() : null;
    }
}

// The devices of every source as one list. Each source hands over its whole current list (Set); entries of several sources
// that are one device (the same key, or the same Bluetooth address) become one, which takes its reading from the most exact
// source with a percentage, its name from that source too, and charging from any. Hidden devices (the settings) are left out
// of what a page shows but stay in All, so the settings can list them.
public sealed class DeviceRoster
{
    private readonly Dictionary<DeviceSource, IReadOnlyList<DeviceBattery>> _sources = [];
    public IReadOnlyList<DeviceBattery> All { get; private set; } = [];

    // Returns whether the merged list changed.
    public bool Set(DeviceSource source, IReadOnlyList<DeviceBattery> devices)
    {
        if (devices.Count == 0) _sources.Remove(source); else _sources[source] = devices;
        var merged = Merge(_sources.Values.SelectMany(list => list));
        bool changed = merged.Count != All.Count || merged.Where((device, i) => !device.Same(All[i])).Any();
        All = merged;
        return changed;
    }

    public void Clear() { _sources.Clear(); All = []; }

    // One source's own list, before merging.
    public IReadOnlyList<DeviceBattery> Of(DeviceSource source) => _sources.TryGetValue(source, out var list) ? list : [];

    // Entries are one device when they share a key or a Bluetooth address (as many links as it takes: A with B and B with C
    // make one). Then a controller without an address (Windows.Gaming.Input, a PlayStation controller's HID) joins one with an
    // address by its vendor and product, but only where that vendor and product is one entry on either side, so two controllers
    // of one model stay two.
    public static List<DeviceBattery> Merge(IEnumerable<DeviceBattery> devices)
    {
        var list = devices.ToList();
        var parent = Enumerable.Range(0, list.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        void Join(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }
        static string? AddressOf(DeviceBattery device) => device.Address ?? (device.Key.StartsWith("bt:", StringComparison.Ordinal) ? device.Key[3..] : null);
        var byIdentity = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < list.Count; i++)
            foreach (var identity in new[] { "key:" + list[i].Key, AddressOf(list[i]) is { } address ? "bt:" + address : null })
                if (identity is not null) { if (byIdentity.TryGetValue(identity, out var other)) Join(other, i); else byIdentity[identity] = i; }
        var withAddress = list.Select((device, i) => (device, i)).Where(entry => AddressOf(entry.device) is not null && entry.device.Hardware is not null && entry.device.Kind == DeviceKind.Controller).ToList();
        var without = list.Select((device, i) => (device, i)).Where(entry => AddressOf(entry.device) is null && entry.device.Hardware is not null && entry.device.Kind == DeviceKind.Controller).ToList();
        foreach (var hardware in without.Select(entry => entry.device.Hardware!).Distinct())
        {
            var ours = without.Where(entry => entry.device.Hardware == hardware).Select(entry => Find(entry.i)).Distinct().ToList();
            var theirs = withAddress.Where(entry => entry.device.Hardware == hardware).Select(entry => Find(entry.i)).Distinct().ToList();
            if (ours.Count == 1 && theirs.Count == 1) Join(theirs[0], ours[0]);
        }
        var result = new List<DeviceBattery>();
        foreach (var group in list.Select((device, i) => (device, i)).GroupBy(entry => Find(entry.i), entry => entry.device))
        {
            var ranked = group.OrderBy(device => device.Percent is null && device.Parts.Count == 0 ? 1 : 0).ThenBy(device => (int)device.Source).ToList();
            var best = ranked[0];
            // A device with a Bluetooth address keeps "bt:<address>" as its key, whichever source reads it and whether or not the
            // Bluetooth source lists it now, so a device switched off in the settings stays off.
            string key = group.Select(AddressOf).FirstOrDefault(address => address is not null) is { } address ? "bt:" + address : best.Key;
            var kind = best.Kind != DeviceKind.Other ? best.Kind : group.Select(device => device.Kind).FirstOrDefault(kind => kind != DeviceKind.Other, DeviceKind.Other);
            result.Add(best with { Key = key, Kind = kind, Charging = group.Any(device => device.Charging && device.Percent is not null || device == best && best.Charging) });
        }
        result.Sort(Devices.Order);
        return result;
    }
}
