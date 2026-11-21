namespace BrimDeck.Core;

// The devices page: the battery of the wireless keyboards, mice, headphones, controllers, pens and phones connected to the PC, from
// whatever reports it (DeviceSource). Every source gives readings in this one shape; the roster merges them, one entry per
// device, in a steady order, so the page never reorders itself as readings come and go.

// What a device is, for its icon and its place in the order. Saved by number with a device's choices (DeviceChoice.Kind), so a
// new kind goes last.
public enum DeviceKind { Mouse, Keyboard, Headphones, Earbuds, Controller, Pen, Speaker, Other, Phone }

// Where a reading comes from, roughly from the most to the least exact. A device seen by more than one source keeps the
// reading of the first in this order that has a percentage (DeviceRoster.Merge).
public enum DeviceSource { PhoneLink, Logitech, Razer, SteelSeries, HyperX, Sony, Apple, EncoDeck, Gatt, Gaming, Hid, Bluetooth }

// The parts of earbuds that report their own charge.
public enum BatteryPart { Left, Right, Case }
public readonly record struct PartBattery(BatteryPart Part, int? Percent, bool Charging);

// One device's reading. Key is the device's own: "bt:<address>" for anything Bluetooth (12 hex digits, upper case), so the
// several Bluetooth sources meet on one entry; other sources key by what identifies the device on its link. Percent is null
// while the device is connected but reports no battery ("not available"); Accuracy says whether the level is the device's
// own figure, one of a few steps, or worked out from the voltage, shown as such. Address is the Bluetooth address when the source knows it, which
// lets a vendor source's entry replace the plain Bluetooth one.
public sealed record DeviceBattery(string Key, string Name, DeviceKind Kind, DeviceSource Source, int? Percent)
{
    public bool Charging { get; init; }
    public BatteryAccuracy Accuracy { get; init; }
    // The wireless mouse is asleep / out of range while the receiver still reports the last battery: show that percent, never 0 %.
    public bool Asleep { get; init; }
    public string? Address { get; init; }
    // "VVVV:PPPP", the USB or Bluetooth vendor and product, when the source knows them: a controller seen both as a game
    // controller and as a Bluetooth device is one device.
    public string? Hardware { get; init; }
    public IReadOnlyList<PartBattery> Parts { get; init; } = [];
    // The reading of the last time the devices were read, shown faded while they are being read again (DeviceRoster.WithEarlier).
    public bool Earlier { get; init; }
    // The lowest of the parts with a reading (the earbuds, not the case), or the device's own.
    public int? Lowest => Parts.Where(part => part.Part != BatteryPart.Case && part.Percent is not null).Select(part => part.Percent).Min() ?? Percent;
    public bool Same(DeviceBattery other) => Key == other.Key && Name == other.Name && Kind == other.Kind && Source == other.Source && Percent == other.Percent
        && Charging == other.Charging && Accuracy == other.Accuracy && Asleep == other.Asleep && Earlier == other.Earlier && Parts.SequenceEqual(other.Parts);
}

// A device's own choices (DeckSettings.DeviceChoices): a name of the user's (Label, null for the device's own), and whether it
// shows in the collapsed row and on the page. Their order is the list's. Remembered once the user changes anything in the list,
// with the name and kind the device had, so one that is not connected is still listed and keeps them.
public sealed class DeviceChoice
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; } = DeviceKind.Other;
    public string? Label { get; set; }
    public bool Collapsed { get; set; } = true;
    public bool Expanded { get; set; } = true;
    // The device's own colors, "#RRGGBB" each, or null for the default: its ring while normal (the text color), while charging
    // (green), when getting low and when low (the collapsed readings' warning and critical colors).
    public string? Color { get; set; }
    public string? ChargeColor { get; set; }
    public string? WarningColor { get; set; }
    public string? CriticalColor { get; set; }
    // The device's vendor and product ("VVVV:PPPP", DeviceBattery.Hardware), and whether its battery is read: turned off, nothing
    // is sent to it any more (DeviceReadSwitches) and it is listed without a reading.
    public string? Hardware { get; set; }
    public bool Read { get; set; } = true;
    public DeviceChoice Copy() => (DeviceChoice)MemberwiseClone();
}


// The devices as the settings arrange them.
public static class DeviceLists
{
    public const int MaximumLabel = 60;

    // The devices whose reading the user turned off.
    public static IEnumerable<string> ReadOff(IReadOnlyList<DeviceChoice> choices)
        => choices.Where(choice => !choice.Read && !string.IsNullOrEmpty(choice.Hardware)).Select(choice => choice.Hardware!).Distinct(StringComparer.OrdinalIgnoreCase);
    // The sources whose readers send to or listen on the device's USB collections, which the read switch stops; the others
    // (Windows' Bluetooth and controller levels) have no switch, and their choices keep no hardware.
    public static bool ReadSwitchable(DeviceSource source)
        => source is DeviceSource.Hid or DeviceSource.Logitech or DeviceSource.Razer or DeviceSource.SteelSeries or DeviceSource.HyperX or DeviceSource.Sony;
    // The read switch is the hardware's: every choice of that hardware takes it (two keys of one device, such as the one it
    // was read under and the one it was listed under before it answered, never disagree).
    public static void SetRead(List<DeviceChoice> choices, string hardware, bool on)
    {
        foreach (var choice in choices)
            if (string.Equals(choice.Hardware, hardware, StringComparison.OrdinalIgnoreCase)) choice.Read = on;
    }

    public static DeviceChoice? Find(IReadOnlyList<DeviceChoice> choices, string key)
    {
        foreach (var choice in choices) if (choice.Key == key) return choice;
        return null;
    }

    // Moves a remembered choice onto a device that came back under another key (such as a new Bluetooth address or switching
    // between its receiver, cable and Bluetooth) while its old entry is offline, when only one offline choice matches it.
    // Returns the updated list, or null when nothing changed.
    public static List<DeviceChoice>? Adopt(IReadOnlyList<DeviceBattery> all, IReadOnlyList<DeviceChoice> choices)
    {
        if (all.Count == 0 || choices.Count == 0) return null;
        var knownKeys = choices.Select(choice => choice.Key).ToHashSet(StringComparer.Ordinal);
        var activeKeys = all.Select(device => device.Key).ToHashSet(StringComparer.Ordinal);
        var unmatched = Enumerable.Range(0, all.Count).Where(i => !knownKeys.Contains(all[i].Key)).ToList();
        var offline = Enumerable.Range(0, choices.Count).Where(i => !activeKeys.Contains(choices[i].Key)).ToList();
        if (unmatched.Count == 0 || offline.Count == 0) return null;

        static bool CompatibleKind(DeviceKind a, DeviceKind b) => a == b || a == DeviceKind.Other || b == DeviceKind.Other;
        static bool SameName(string a, DeviceKind ak, string b, DeviceKind bk)
            => ak == DeviceKind.Phone && bk == DeviceKind.Phone
                ? Devices.SamePhone(a, b)
                : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        static bool CanTake(DeviceChoice choice, DeviceBattery device)
            => choice.Read || string.IsNullOrEmpty(choice.Hardware)
                || ReadSwitchable(device.Source) && string.Equals(choice.Hardware, device.Hardware, StringComparison.OrdinalIgnoreCase);

        var pairs = new List<(int ChoiceIndex, int DeviceIndex)>();
        var takenChoices = new HashSet<int>();
        var takenDevices = new HashSet<int>();

        // Pass 1: unambiguous 1-to-1 match by Hardware ("VVVV:PPPP").
        foreach (int di in unmatched)
        {
            var device = all[di];
            if (string.IsNullOrEmpty(device.Hardware)) continue;
            if (all.Count(other => string.Equals(other.Hardware, device.Hardware, StringComparison.OrdinalIgnoreCase)) != 1) continue;
            int match = -1, count = 0;
            foreach (int ci in offline)
            {
                if (takenChoices.Contains(ci)) continue;
                var choice = choices[ci];
                if (!string.Equals(choice.Hardware, device.Hardware, StringComparison.OrdinalIgnoreCase)) continue;
                if (!CompatibleKind(choice.Kind, device.Kind) || !CanTake(choice, device)) continue;
                match = ci; count++;
            }
            if (count == 1)
            {
                pairs.Add((match, di));
                takenChoices.Add(match);
                takenDevices.Add(di);
            }
        }

        // Pass 2: unambiguous 1-to-1 match by trimmed raw Name + compatible Kind.
        foreach (int di in unmatched)
        {
            if (takenDevices.Contains(di)) continue;
            var device = all[di];
            var name = device.Name.Trim();
            if (name.Length == 0) continue;
            if (all.Count(other => SameName(other.Name, other.Kind, name, device.Kind) && CompatibleKind(other.Kind, device.Kind)) != 1) continue;
            int match = -1, count = 0;
            foreach (int ci in offline)
            {
                if (takenChoices.Contains(ci)) continue;
                var choice = choices[ci];
                if (!SameName(choice.Name, choice.Kind, name, device.Kind)) continue;
                if (!CompatibleKind(choice.Kind, device.Kind) || !CanTake(choice, device)) continue;
                match = ci; count++;
            }
            if (count == 1)
            {
                pairs.Add((match, di));
                takenChoices.Add(match);
                takenDevices.Add(di);
            }
        }

        if (pairs.Count == 0) return null;

        var result = choices.Select(choice => choice.Copy()).ToList();
        foreach (var (ci, di) in pairs)
        {
            var device = all[di];
            var updated = result[ci];
            updated.Key = device.Key;
            updated.Name = device.Name;
            if (device.Kind != DeviceKind.Other) updated.Kind = device.Kind;
            if (ReadSwitchable(device.Source)) updated.Hardware = device.Hardware ?? updated.Hardware;
            else { updated.Hardware = null; updated.Read = true; }
        }
        return [.. result.DistinctBy(choice => choice.Key, StringComparer.Ordinal)];
    }

    // The devices read now in the list's order: those the settings remember in theirs, then the others in the roster's own,
    // each under the name the user gave it.
    public static List<DeviceBattery> Ordered(IReadOnlyList<DeviceBattery> all, IReadOnlyList<DeviceChoice> choices)
    {
        choices = Adopt(all, choices) ?? choices;
        var place = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < choices.Count; i++) place.TryAdd(choices[i].Key, i);
        return [.. all.Select((device, i) => (device, rank: place.TryGetValue(device.Key, out var at) ? at : choices.Count + i)).OrderBy(entry => entry.rank)
            .Select(entry => Find(choices, entry.device.Key)?.Label is { Length: > 0 } label ? entry.device with { Name = label } : entry.device)];
    }

    // The devices the page shows.
    public static List<DeviceBattery> Expanded(IReadOnlyList<DeviceBattery> all, IReadOnlyList<DeviceChoice> choices)
    {
        choices = Adopt(all, choices) ?? choices;
        return [.. Ordered(all, choices).Where(device => Find(choices, device.Key)?.Expanded ?? true)];
    }

    // The devices the collapsed row shows, in the list's order.
    public static List<DeviceBattery> Collapsed(IReadOnlyList<DeviceBattery> all, IReadOnlyList<DeviceChoice> choices)
    {
        choices = Adopt(all, choices) ?? choices;
        return [.. Ordered(all, choices).Where(device => Find(choices, device.Key)?.Collapsed ?? true)];
    }

    // The list as the user changed it: every device read now and every one remembered, in this order, with the names and kinds
    // they have now. order: the keys in their new order (those missing keep their place after them).
    public static List<DeviceChoice> Remember(IReadOnlyList<DeviceBattery> all, IReadOnlyList<DeviceChoice> choices, IEnumerable<string>? order = null)
    {
        choices = Adopt(all, choices) ?? choices;
        var listed = Ordered(all, choices).Select(device => device.Key).Concat(choices.Select(choice => choice.Key)).Distinct().ToList();
        if (order is not null) { var first = order.Where(listed.Contains).ToList(); listed = [.. first, .. listed.Except(first)]; }
        return [.. listed.Select(key =>
        {
            var choice = Find(choices, key)?.Copy() ?? new DeviceChoice { Key = key };
            if (all.FirstOrDefault(device => device.Key == key) is { } device)
            {
                choice.Name = device.Name; choice.Kind = device.Kind;
                if (ReadSwitchable(device.Source)) choice.Hardware = device.Hardware ?? choice.Hardware;
                else { choice.Hardware = null; choice.Read = true; }
            }
            return choice;
        })];
    }
}

public static class Devices
{
    public const int DefaultLowPercent = 15, DefaultWarningPercent = 25;
    // The low battery threshold the settings offer.
    public static readonly IReadOnlyList<int> LowChoices = [5, 10, 15, 20, 25, 30];
    // The getting-low threshold the settings offer; it stays above the low one (DeckSettings.Normalize).
    public static readonly IReadOnlyList<int> WarningChoices = [10, 15, 20, 25, 30, 35, 40, 50];

    // A reading's level: critical at or under the low threshold, elevated at or under the warning one. Charging is never low.
    public static MemoryLevel Level(int? percent, bool charging, int warning, int low)
        => percent is not { } p || charging ? MemoryLevel.Normal : p <= low ? MemoryLevel.Critical : p <= warning ? MemoryLevel.Elevated : MemoryLevel.Normal;

    public static int Clamp(int percent) => Math.Clamp(percent, 0, 100);

    // The kind from what the device says of itself, best first: its Bluetooth category or appearance, then its name.
    public static DeviceKind Kind(string? category, int? appearance, string? name)
    {
        if (category is { Length: > 0 })
        {
            // Not "Phone" alone, which a headphone category carries too.
            if (category.Contains("Communication.Phone", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Phone;
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
        0x001 => DeviceKind.Phone,
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
        DeviceKind.Speaker => 6, DeviceKind.Phone => 7, _ => 8
    };

    public static string KindName(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => Loc.T("鼠标", "Mouse"), DeviceKind.Keyboard => Loc.T("键盘", "Keyboard"), DeviceKind.Headphones => Loc.T("头戴耳机", "Headphones"),
        DeviceKind.Earbuds => Loc.T("入耳耳机", "Earbuds"), DeviceKind.Controller => Loc.T("手柄", "Controller"), DeviceKind.Pen => Loc.T("笔", "Pen"),
        DeviceKind.Speaker => Loc.T("音箱", "Speaker"), DeviceKind.Phone => Loc.T("手机", "Phone"), _ => Loc.T("设备", "Device")
    };

    public static string SourceName(DeviceSource source) => source switch
    {
        DeviceSource.PhoneLink => Loc.T("手机连接", "Phone Link"),
        DeviceSource.Logitech => Loc.T("罗技接收器", "Logitech receiver"), DeviceSource.Razer => "Razer", DeviceSource.SteelSeries => "SteelSeries",
        DeviceSource.HyperX => "HyperX", DeviceSource.Sony => "PlayStation", DeviceSource.Gatt => Loc.T("蓝牙电池服务", "Bluetooth battery service"),
        DeviceSource.Gaming => Loc.T("游戏控制器", "Game controller"), DeviceSource.Hid => Loc.T("HID 电量", "HID battery"), DeviceSource.Apple => Loc.T("Apple 广播", "Apple broadcast"),
        DeviceSource.EncoDeck => "EncoDeck",
        _ => Loc.T("蓝牙", "Bluetooth")
    };

    public static string PartName(BatteryPart part) => part switch { BatteryPart.Left => Loc.T("左", "L"), BatteryPart.Right => Loc.T("右", "R"), _ => Loc.T("盒", "Case") };

    // The figure as shown: "85%", or "—" for a device that reports no battery.
    public static string Figure(int? percent) => percent is { } p ? Clamp(p).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" : "—";

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

    // Suspensions of a phone's Bluetooth name that still name the same phone: "Xiaomi 13 Ultra 5G" is the "Xiaomi 13
    // Ultra" Phone Link shows. Anything else after the shorter name is a different model ("Pixel 8" and "Pixel 8 Pro").
    private static readonly string[] PhoneSuffixes = ["5g", "4g", "lte", "5ge", "nr", "dualsim", "双卡", "双卡双待"];

    // Whether two names are the same phone: letters and digits only, in lower case, then equal, or one the start of the
    // other followed only by the way a phone names its link (Phone Link and Bluetooth may differ by that).
    public static bool SamePhone(string? a, string? b)
    {
        var left = PhoneName(a); var right = PhoneName(b);
        if (left.Length == 0 || right.Length == 0) return false;
        if (left == right) return true;
        var (shorter, longer) = left.Length <= right.Length ? (left, right) : (right, left);
        return shorter.Length >= 6 && longer.StartsWith(shorter, StringComparison.Ordinal)
            && PhoneSuffixes.Contains(longer[shorter.Length..], StringComparer.Ordinal);
    }
    private static string PhoneName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    // The name a phone is known by, without the suffixes that only name its link: a phone seen as "Xiaomi 13 Ultra",
    // "Xiaomi 13 Ultra 5G" and "Xiaomi 13 Ultra LTE" keeps one identity, so the address it was seen with does too.
    internal static string PhoneIdentity(string? name)
    {
        var key = PhoneName(name);
        foreach (var suffix in PhoneSuffixes)
            if (key.Length > suffix.Length && key.EndsWith(suffix, StringComparison.Ordinal)) return key[..^suffix.Length];
        return key;
    }
}

// The devices of every source as one list. Each source hands over its whole current list (Set); entries of several sources
// that are one device (the same key, or the same Bluetooth address) become one, which takes its reading from the most exact
// source with a percentage, its name from that source too, and charging from any. Hidden devices (the settings) are left out
// of what a page shows but stay in All, so the settings can list them.
public sealed class DeviceRoster
{
    private readonly Dictionary<DeviceSource, IReadOnlyList<DeviceBattery>> _sources = [];
    // The address each phone was last seen with, by name: Phone Link may keep a phone while Bluetooth stops listing it (the
    // app reaches it over Wi-Fi), and the entry keeps the key the settings remember.
    private readonly Dictionary<string, string> _addresses = new(StringComparer.Ordinal);
    public IReadOnlyList<DeviceBattery> All { get; private set; } = [];

    // Returns whether the merged list changed.
    public bool Set(DeviceSource source, IReadOnlyList<DeviceBattery> devices)
    {
        if (devices.Count == 0) _sources.Remove(source); else _sources[source] = devices;
        var merged = Merge(_sources.Values.SelectMany(list => list), _addresses);
        bool changed = merged.Count != All.Count || merged.Where((device, i) => !device.Same(All[i])).Any();
        All = merged;
        return changed;
    }

    // Stopping readers drops readings, not the identities already established in this session.
    public void Clear() { _sources.Clear(); All = []; }

    // One source's own list, before merging.
    public IReadOnlyList<DeviceBattery> Of(DeviceSource source) => _sources.TryGetValue(source, out var list) ? list : [];

    // Entries are one device when they share a key or a Bluetooth address (as many links as it takes: A with B and B with C
    // make one). Then a controller without an address (Windows.Gaming.Input, a PlayStation controller's HID) joins one with an
    // address by its vendor and product, but only where that vendor and product is one entry on either side, so two controllers
    // of one model stay two.
    public static List<DeviceBattery> Merge(IEnumerable<DeviceBattery> devices, IDictionary<string, string>? seen = null)
    {
        var list = devices.ToList();
        // Phone Link is the primary reading for a phone: while it reports one, the Bluetooth entry of that phone is left
        // out, so the page shows one phone with the fresher reading and its charging state. Its address goes to the Phone
        // Link entry first, so the merged key stays "bt:<address>" and the name, colors and hidden state the settings
        // remember still find the phone; once Bluetooth has stopped listing the phone, the address it was last seen with
        // stands in, since Phone Link reaches it over Wi-Fi as well. With no Phone Link reading (the app closed, the phone
        // away) the Bluetooth entry stands in unchanged; a phone it does not name is kept.
        var linked = list.Where(device => device.Source == DeviceSource.PhoneLink && device.Kind == DeviceKind.Phone).ToList();
        if (linked.Count > 0)
        {
            var dropped = new HashSet<DeviceBattery>(ReferenceEqualityComparer.Instance);
            var carries = new Dictionary<DeviceBattery, string>(ReferenceEqualityComparer.Instance);
            foreach (var phone in linked)
            {
                var candidates = list.Where(device => device.Source == DeviceSource.Bluetooth && device.Kind == DeviceKind.Phone
                    && device.Address is not null && Devices.SamePhone(phone.Name, device.Name)).ToList();
                // Multiple endpoints of one address are one phone; multiple addresses with the same name are ambiguous.
                // Discard an earlier guess too, so it cannot merge the phones again after Bluetooth goes away.
                if (candidates.Select(device => device.Address).Distinct(StringComparer.Ordinal).Count() > 1
                    || linked.Count(other => Devices.SamePhone(phone.Name, other.Name)) > 1)
                {
                    seen?.Remove(Devices.PhoneIdentity(phone.Name));
                    continue;
                }
                var bluetooth = candidates.FirstOrDefault();
                foreach (var candidate in candidates) dropped.Add(candidate);
                if ((bluetooth?.Address ?? Seen(seen, Devices.PhoneIdentity(phone.Name))) is not { } address) continue;
                carries[phone] = address;
                // One address per phone, whatever suffix the name carries this time: the identity, not the name, is the key.
                if (seen is not null) seen[Devices.PhoneIdentity(phone.Name)] = address;
            }
            if (dropped.Count > 0)
                list = list.Where(device => !dropped.Contains(device)).ToList();
            if (carries.Count > 0)
                list = list.Select(device => carries.TryGetValue(device, out var address) ? device with { Address = address } : device).ToList();
        }
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

    // What shows while the devices are read again, until every source has listed what is connected: the readings now, and for a
    // device without one yet its reading from the last time, marked Earlier. Once listed, only the readings now show, so a
    // device switched off since, or one that gives no reading this time, never keeps the old one.
    public static IReadOnlyList<DeviceBattery> WithEarlier(IReadOnlyList<DeviceBattery> now, IReadOnlyList<DeviceBattery> earlier)
    {
        var read = now.Where(device => device.Lowest is not null).Select(device => device.Key).ToHashSet(StringComparer.Ordinal);
        var standIns = new Dictionary<string, DeviceBattery>(StringComparer.Ordinal);
        foreach (var device in earlier)
            if (device.Lowest is not null && !read.Contains(device.Key)) standIns.TryAdd(device.Key, device with { Earlier = true });
        if (standIns.Count == 0) return now;
        var result = now.Where(device => !standIns.ContainsKey(device.Key)).Concat(standIns.Values).ToList();
        result.Sort(Devices.Order);
        return result;
    }

    // The address a phone was last seen with, by the identity its names share ("Xiaomi 13 Ultra 5G" is "Xiaomi 13 Ultra").
    private static string? Seen(IDictionary<string, string>? seen, string identity)
        => seen is not null && seen.TryGetValue(identity, out var address) ? address : null;
}

// The devices page's layout, in steps as the monitor page's: the type and the rings change only from one step to the next, never
// with each pixel of a resize, and the room left over goes between and around the devices. One device is set out wide (Single):
// its ring, then its figure, name and kind beside it. More take a ring each side by side (Tiles), on one row or, where that does
// not fit, two, at the largest step that fits; where even the smallest does not, they are listed a line each in columns (List),
// the list scrolling. A resize within Hysteresis of the step the page has keeps it, so dragging an edge does not flip between two.
public enum DevicesForm { Tiles, Single, List }

// A step of the tiles: the ring, its stroke and the icon in it, the gap to the figure, the figure, its line and its unit, the name
// and the line under it (0: none) with their line heights; the tile's width, from MinWidth to MaxWidth; the gap between tiles.
public sealed record DeviceTileStep(double Ring, double Stroke, double Icon, double Gap, double Figure, double FigureLine, double Unit,
    double Name, double NameLine, double Detail, double DetailLine, double MinWidth, double MaxWidth, double Spacing)
{
    public const double NameGap = 2;
    public double Height => Ring + Gap + FigureLine + NameGap + NameLine + DetailLine;
}

// A step of the single device: the ring, its stroke and icon, the gap to the text beside it, the figure, the name and the kind.
public sealed record DeviceSingleStep(double Ring, double Stroke, double Icon, double Gap, double Figure, double FigureLine, double Unit,
    double Name, double NameLine, double Detail, double DetailLine)
{
    public const double TextWidth = 180, MinimumText = 110;
}

public sealed record DevicesLayout(DevicesForm Form, int Step, int Rows)
{
    // Margin: the room kept above and below the tiles; the list's lines, the gap between its columns and their narrowest width.
    public const double Margin = 12, Hysteresis = 12, ListRow = 38, ListGap = 24, ListColumn = 180;
    public static readonly DeviceTileStep[] Tiles =
    [
        new(88, 6, 36, 12, 26, 30, 13, 13.5, 18, 11.5, 16, 132, 176, 20),
        new(72, 5, 30, 10, 22, 26, 11.5, 12.5, 17, 11, 15, 112, 150, 16),
        new(60, 4.5, 26, 9, 20, 24, 11, 12, 16, 10.5, 14, 96, 128, 12),
        // The smallest has no room for the line under the name; the tooltip carries it.
        new(40, 3.5, 17, 5, 16, 18, 10, 11.5, 14, 0, 0, 80, 96, 8),
    ];
    public static readonly DeviceSingleStep[] Singles =
    [
        new(132, 10, 52, 28, 44, 50, 16, 15, 20, 12, 17),
        new(104, 8, 40, 24, 36, 42, 14, 13.5, 18, 11, 16),
        new(72, 6, 28, 20, 28, 34, 12, 12.5, 17, 11, 15),
    ];
    public DeviceTileStep Tile => Tiles[Math.Clamp(Step, 0, Tiles.Length - 1)];
    public DeviceSingleStep Single => Singles[Math.Clamp(Step, 0, Singles.Length - 1)];

    // The page's content: width × height, without the header and the margins.
    public static DevicesLayout For(double width, double height, int devices, DevicesLayout? previous)
    {
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        height = double.IsFinite(height) ? Math.Max(0, height) : 0;
        var next = Fit(width, height, devices);
        if (previous is null || next == previous) return next;
        foreach (double dw in new[] { 0, -Hysteresis, Hysteresis })
            foreach (double dh in new[] { 0, -Hysteresis, Hysteresis })
                if (Fit(width + dw, height + dh, devices) == previous) return previous;
        return next;
    }

    private static DevicesLayout Fit(double width, double height, int devices)
    {
        // The single device's ring keeps about a sixth of the page's height clear above and below it (the smallest only its margin),
        // so it never fills the page.
        if (devices == 1)
            for (int i = 0; i < Singles.Length; i++)
                if ((i == Singles.Length - 1 ? Singles[i].Ring + Margin : Singles[i].Ring * 1.36) <= height
                    && Singles[i].Ring + Singles[i].Gap + DeviceSingleStep.MinimumText <= width) return new(DevicesForm.Single, i, 1);
        int count = Math.Max(1, devices);
        for (int i = 0; i < Tiles.Length; i++)
            for (int rows = 1; rows <= Math.Min(2, count); rows++)
                if (Fits(Tiles[i], rows, count, width, height)) return new(DevicesForm.Tiles, i, rows);
        return new(DevicesForm.List, 0, 1);
    }

    private static bool Fits(DeviceTileStep step, int rows, int devices, double width, double height)
    {
        int columns = (devices + rows - 1) / rows;
        double tile = Math.Min(step.MaxWidth, (width - (columns - 1) * step.Spacing) / columns);
        return tile >= step.MinWidth && rows * step.Height + (rows - 1) * step.Spacing + Margin <= height;
    }

    // The columns the devices take: the tiles' on a row, or the list's.
    public int Columns(int devices, double width) => Form switch
    {
        DevicesForm.Tiles => (Math.Max(1, devices) + Rows - 1) / Rows,
        DevicesForm.List => Math.Clamp((int)Math.Floor((width + ListGap) / (ListColumn + ListGap)), 1, 4),
        _ => 1
    };

    // The height of the list's lines all together, for its scrolling.
    public double ListHeight(int devices, double width) => Math.Ceiling(devices / (double)Columns(devices, width)) * ListRow;

    // Each device's place, in whole DIPs: the tiles in the middle of the page, a last row that is not full centred on its own; the
    // single device and its text in the middle; the list's lines row by row from the top, before scrolling.
    public (double X, double Y, double Width, double Height) Place(int index, int devices, double width, double height)
    {
        devices = Math.Max(1, devices);
        switch (Form)
        {
            case DevicesForm.Single:
            {
                var s = Single;
                double text = Math.Min(DeviceSingleStep.TextWidth, Math.Max(0, width - s.Ring - s.Gap)), whole = s.Ring + s.Gap + text;
                return (Math.Round((width - whole) / 2), Math.Round((height - s.Ring) / 2), whole, s.Ring);
            }
            case DevicesForm.List:
            {
                int columns = Columns(devices, width);
                double column = Math.Floor((width - (columns - 1) * ListGap) / columns);
                return (Math.Round(index % columns * (column + ListGap)), index / columns * ListRow, column, ListRow);
            }
            default:
            {
                var t = Tile;
                int columns = Columns(devices, width), rows = (devices + columns - 1) / columns, row = index / columns;
                double tile = Math.Floor(Math.Min(t.MaxWidth, (width - (columns - 1) * t.Spacing) / columns));
                int inRow = row == rows - 1 ? devices - row * columns : columns;
                double left = (width - inRow * tile - (inRow - 1) * t.Spacing) / 2, top = (height - rows * t.Height - (rows - 1) * t.Spacing) / 2;
                return (Math.Round(left + index % columns * (tile + t.Spacing)), Math.Round(top + row * (t.Height + t.Spacing)), tile, t.Height);
            }
        }
    }
}

