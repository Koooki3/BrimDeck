using BrimDeck.Core;

// Phone Link first, Bluetooth as the fallback: what the merged list shows while Phone Link reports the phone, while it
// stops (the app closed, the phone away), and which Bluetooth entries survive.
static class PhoneLinkMergeTests
{
    private static DeviceBattery Link(int percent, string name = "Xiaomi 13 Ultra", bool charging = false)
        => new("phonelink:" + name.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant(), name, DeviceKind.Phone, DeviceSource.PhoneLink, percent) { Charging = charging };
    private static DeviceBattery Bt(string address, string name, int? percent, DeviceKind kind = DeviceKind.Phone)
        => new("bt:" + address, name, kind, DeviceSource.Bluetooth, percent) { Address = address };

    public static void Run(Action<string, bool> check)
    {
        var linked = Link(87, charging: true);
        var bluetooth = Bt("AABBCCDDEEFF", "Xiaomi 13 Ultra", 80);
        var merged = DeviceRoster.Merge([linked, bluetooth]);
        check("Phone Link: the primary reading replaces the same phone on Bluetooth",
            merged.Count == 1 && merged[0].Source == DeviceSource.PhoneLink && merged[0].Percent == 87 && merged[0].Charging);
        check("Phone Link: the merged phone keeps its Bluetooth key",
            merged.Count == 1 && merged[0].Key == "bt:AABBCCDDEEFF" && merged[0].Address == "AABBCCDDEEFF");

        check("Phone Link: without it the Bluetooth phone stands in",
            DeviceRoster.Merge([bluetooth]) is { Count: 1 } only && only[0].Source == DeviceSource.Bluetooth && only[0].Percent == 80);
        check("Phone Link: a phone it does not name keeps its Bluetooth entry",
            DeviceRoster.Merge([linked, Bt("112233445566", "Pixel 8", 66)]).Count == 2);
        check("Phone Link: a model suffix is the same phone",
            DeviceRoster.Merge([linked, Bt("AABBCCDDEEFF", "Xiaomi 13 Ultra 5G", 79)]).Count == 1);
        check("Phone Link: a model of its own is another phone",
            DeviceRoster.Merge([Link(70, "Pixel 8"), Bt("112233445566", "Pixel 8 Pro", 66)]).Count == 2);
        check("Phone Link: other Bluetooth devices are untouched",
            DeviceRoster.Merge([linked, Bt("001122334455", "MX Master 3S", 55, DeviceKind.Mouse)]).Count == 2);

        // The whole way: the Bluetooth source lists the phone, Phone Link takes over, and gives it back when it stops.
        var roster = new DeviceRoster();
        roster.Set(DeviceSource.Bluetooth, [bluetooth]);
        check("Phone Link: Bluetooth alone lists the phone", roster.All is { Count: 1 } one && one[0].Source == DeviceSource.Bluetooth);
        roster.Set(DeviceSource.PhoneLink, [linked]);
        check("Phone Link: its reading takes the page over", roster.All is { Count: 1 } two && two[0].Source == DeviceSource.PhoneLink);
        // Bluetooth stops listing the phone while Phone Link still reports it (the app reaches it over Wi-Fi as well): the
        // key the settings remember stays.
        roster.Set(DeviceSource.Bluetooth, []);
        check("Phone Link: the key holds after Bluetooth stops listing the phone",
            roster.All is { Count: 1 } kept && kept[0].Key == "bt:AABBCCDDEEFF" && kept[0].Source == DeviceSource.PhoneLink);
        roster.Set(DeviceSource.Bluetooth, [bluetooth]);
        roster.Set(DeviceSource.PhoneLink, []);
        check("Phone Link: closed, the Bluetooth entry returns", roster.All is { Count: 1 } three && three[0].Source == DeviceSource.Bluetooth);

        // Phone Link adds a model suffix to the name it shows, and the phone is paired again with another address: the
        // newer address is the one kept, even after Bluetooth stops listing the phone.
        var renamed = new DeviceRoster();
        renamed.Set(DeviceSource.PhoneLink, [Link(70)]);
        renamed.Set(DeviceSource.Bluetooth, [Bt("AABBCCDDEEFF", "Xiaomi 13 Ultra", 70)]);
        renamed.Set(DeviceSource.PhoneLink, [Link(70, "Xiaomi 13 Ultra 5G")]);
        renamed.Set(DeviceSource.Bluetooth, [Bt("112233445566", "Xiaomi 13 Ultra", 65)]);
        renamed.Set(DeviceSource.Bluetooth, []);
        check("Phone Link: the newer address wins over the older name",
            renamed.All is { Count: 1 } latest && latest[0].Key == "bt:112233445566");

        // The suffix Phone Link shows can change while Bluetooth stays away: one identity, one address, one key.
        var suffixed = new DeviceRoster();
        suffixed.Set(DeviceSource.PhoneLink, [Link(70)]);
        suffixed.Set(DeviceSource.Bluetooth, [Bt("AABBCCDDEEFF", "Xiaomi 13 Ultra", 70)]);
        suffixed.Set(DeviceSource.Bluetooth, []);
        suffixed.Set(DeviceSource.PhoneLink, [Link(70, "Xiaomi 13 Ultra 5G")]);
        suffixed.Set(DeviceSource.PhoneLink, [Link(70, "Xiaomi 13 Ultra LTE")]);
        check("Phone Link: a changing suffix keeps the key while Bluetooth is away",
            suffixed.All is { Count: 1 } same && same[0].Key == "bt:AABBCCDDEEFF");

        check("Phone Link: names match across case, spaces and a suffix",
            Devices.SamePhone("Xiaomi 13 Ultra", "XIAOMI 13 ultra") && Devices.SamePhone("Xiaomi 13 Ultra", "Xiaomi 13 Ultra 5G")
            && !Devices.SamePhone("Pixel 8", "iPhone 15") && !Devices.SamePhone("", "Pixel 8") && !Devices.SamePhone(null, null));
        check("Phone Link: a longer model is not the same phone",
            !Devices.SamePhone("Pixel 8", "Pixel 8 Pro") && !Devices.SamePhone("iPhone 15", "iPhone 15 Pro")
            && !Devices.SamePhone("Galaxy S23", "Galaxy S23 Ultra"));
        check("Phone Link: its source is named and has no read switch",
            Devices.SourceName(DeviceSource.PhoneLink).Length > 0 && !DeviceLists.ReadSwitchable(DeviceSource.PhoneLink));

        roster.Set(DeviceSource.PhoneLink, [linked]);
        roster.Clear();
        roster.Set(DeviceSource.PhoneLink, [linked]);
        check("Phone Link: stopping and restarting readers retains the known address",
            roster.All is { Count: 1 } resumed && resumed[0].Key == bluetooth.Key);

        // Settings survive a new process even when only Wi-Fi is available and the card adds a model suffix.
        var choices = new List<DeviceChoice>
        {
            new() { Key = bluetooth.Key, Name = bluetooth.Name, Kind = DeviceKind.Phone, Label = "My phone",
                Expanded = false, Collapsed = false, Color = "#123456", ChargeColor = "#654321" }
        };
        var saved = System.Text.Json.JsonSerializer.Serialize(choices);
        choices = System.Text.Json.JsonSerializer.Deserialize<List<DeviceChoice>>(saved)!;
        var restarted = new DeviceRoster();
        restarted.Set(DeviceSource.PhoneLink, [Link(70, "Xiaomi 13 Ultra 5G")]);
        choices = DeviceLists.Adopt(restarted.All, choices) ?? choices;
        check("Phone Link: persisted choices follow a Wi-Fi phone after restart",
            choices.Count == 1 && choices[0].Key == restarted.All[0].Key && choices[0].Label == "My phone"
            && choices[0].Color == "#123456" && choices[0].ChargeColor == "#654321"
            && DeviceLists.Expanded(restarted.All, choices).Count == 0 && DeviceLists.Collapsed(restarted.All, choices).Count == 0);
        restarted.Set(DeviceSource.Bluetooth, [bluetooth]);
        choices = DeviceLists.Adopt(restarted.All, choices) ?? choices;
        check("Phone Link: reconnecting Bluetooth preserves the restored choices",
            choices.Count == 1 && choices[0].Key == bluetooth.Key && !choices[0].Expanded && choices[0].Label == "My phone");

        var twin = Bt("112233445566", bluetooth.Name, 23);
        var ambiguous = new DeviceRoster();
        ambiguous.Set(DeviceSource.Bluetooth, [bluetooth]);
        ambiguous.Set(DeviceSource.PhoneLink, [linked]);
        ambiguous.Set(DeviceSource.Bluetooth, [bluetooth, twin]);
        check("Phone Link: two same-name addresses keep their own readings and discard the earlier association",
            ambiguous.All.Count == 3 && ambiguous.All.Any(d => d.Key == bluetooth.Key && d.Percent == 80)
            && ambiguous.All.Any(d => d.Key == twin.Key && d.Percent == 23)
            && ambiguous.All.Any(d => d.Key == linked.Key && d.Percent == 87));
        ambiguous.Set(DeviceSource.Bluetooth, []);
        check("Phone Link: an ambiguous address is not reused after Bluetooth disappears",
            ambiguous.All is { Count: 1 } unbound && unbound[0].Key == linked.Key);
        check("Phone Link: duplicate endpoints at one address still merge",
            DeviceRoster.Merge([linked, bluetooth, bluetooth with { Percent = 79 }]) is { Count: 1 } endpoints
            && endpoints[0].Key == bluetooth.Key && endpoints[0].Percent == 87);
        var offlineTwins = new List<DeviceChoice>
        {
            new() { Key = bluetooth.Key, Name = bluetooth.Name, Kind = DeviceKind.Phone, Expanded = false },
            new() { Key = twin.Key, Name = twin.Name, Kind = DeviceKind.Phone, Expanded = true }
        };
        check("Phone Link: ambiguous saved phones do not donate their settings",
            DeviceLists.Adopt([Link(70, "Xiaomi 13 Ultra 5G")], offlineTwins) is null);
    }
}
