using System.Text.Json;
using BrimDeck.Core;

// The devices page: its layout steps, its settings, and the collapsed battery reading's place among the readings.
static class DevicesPageTests
{
    public static void Run(Action<string, bool> check)
    {
        Layout(check);
        Settings(check);
        Strip(check);
    }

    private static void Layout(Action<string, bool> check)
    {
        static DevicesLayout At(double width, double height, int devices, DevicesLayout? previous = null) => DevicesLayout.For(width, height, devices, previous);
        // The design's sizes (the panel less its header, sides and bottom): 440×140, 460×200, 600×220, 900×320 and 440×400.
        check("Devices layout: the smallest panel shows four devices at the small step", At(400, 92, 4) == new DevicesLayout(DevicesForm.Tiles, 3, 1));
        check("Devices layout: the default panel shows two to four devices at the middle step", Enumerable.Range(2, 3).All(n => At(420, 152, n) == new DevicesLayout(DevicesForm.Tiles, 2, 1)));
        check("Devices layout: a larger panel takes the large step", At(560, 172, 4) == new DevicesLayout(DevicesForm.Tiles, 1, 1));
        check("Devices layout: a large panel takes the largest step", At(860, 272, 4) == new DevicesLayout(DevicesForm.Tiles, 0, 1));
        check("Devices layout: a narrow tall panel puts four devices on two rows", At(400, 352, 4) == new DevicesLayout(DevicesForm.Tiles, 1, 2)
            && At(400, 352, 2) == new DevicesLayout(DevicesForm.Tiles, 0, 1) && At(400, 352, 3) == new DevicesLayout(DevicesForm.Tiles, 1, 1));
        check("Devices layout: one device is set out wide", At(420, 152, 1).Form == DevicesForm.Single && At(400, 92, 1) == new DevicesLayout(DevicesForm.Single, 2, 1));
        var many = At(420, 152, 9);
        check("Devices layout: devices the smallest step cannot hold are listed in two columns", many.Form == DevicesForm.List && many.Columns(9, 420) == 2
            && many.ListHeight(9, 420) == 5 * DevicesLayout.ListRow);
        check("Devices layout: the steps' type grows with the step", DevicesLayout.Tiles.Zip(DevicesLayout.Tiles.Skip(1)).All(pair => pair.First.Ring > pair.Second.Ring && pair.First.Figure > pair.Second.Figure));
        var middle = At(420, 140, 4);
        check("Devices layout: a resize within the band keeps the step", middle.Step == 2 && At(420, 135, 4, middle) == middle && At(420, 120, 4, middle).Step == 3);
        var (x, y, w, h) = At(420, 152, 4).Place(3, 4, 420, 152);
        check("Devices layout: tiles lie within the page", x >= 0 && y >= 0 && x + w <= 420.5 && y + h <= 152.5);
        var two = At(400, 352, 4);
        var (last, _, tile, _) = two.Place(2, 3, 400, 352);
        check("Devices layout: a last row that is not full is centred", Math.Abs(last + tile / 2 - 200) <= 1);
        var (alone, _, whole, _) = At(420, 152, 1).Place(0, 1, 420, 152);
        check("Devices layout: the single device sits in the middle", Math.Abs(alone + whole / 2 - 210) <= 1);
        check("Devices layout: a size that is not a number is taken as nothing", At(double.NaN, double.PositiveInfinity, 3).Form == DevicesForm.List);
    }

    private static void Settings(Action<string, bool> check)
    {
        var defaults = new DeckSettings();
        check("Devices settings: the page is on, low at 15 % and getting low at 25 %, every device everywhere with its figure, time estimates off, the part off",
            defaults.DevicesPage && defaults.DeviceLowPercent == 15 && defaults.DeviceWarningPercent == 25 && defaults.DeviceChoices.Count == 0 && defaults.DeviceFigures
            && !defaults.DeviceBatteryEstimates && !defaults.NotchDevices && !defaults.CapsuleDevices && defaults.NotchOrder.Contains(CompactPart.Devices));
        var saved = new DeckSettings { DevicesWidth = 9999, DevicesHeight = double.NaN, DeviceLowPercent = 7, DeviceWarningPercent = 33,
            DeviceChoices = [new() { Key = "bt:AA", Name = "Mouse", Label = "  ", Color = "#12345G" }, new() { Key = "bt:AA" }, new() { Key = " " },
                new() { Key = "bt:BB", Label = new string('x', 80), Kind = (DeviceKind)40, ChargeColor = "#00ff00" }] };
        saved.Normalize();
        check("Devices settings: normalized", saved.DevicesWidth == 1200 && saved.DevicesHeight == 200 && saved.DeviceLowPercent == Devices.DefaultLowPercent
            && saved.DeviceWarningPercent == Devices.DefaultWarningPercent && saved.DeviceChoices.Count == 2 && saved.DeviceChoices[0] is { Label: null, Color: null }
            && saved.DeviceChoices[1].Label!.Length == DeviceLists.MaximumLabel && saved.DeviceChoices[1].Kind == DeviceKind.Other && saved.DeviceChoices[1].ChargeColor == "#00FF00");
        var crossed = new DeckSettings { DeviceLowPercent = 30, DeviceWarningPercent = 20 };
        crossed.Normalize();
        check("Devices settings: getting low stays above low", crossed.DeviceWarningPercent == 35);
        var copy = saved.Copy(); copy.DeviceChoices[0].Collapsed = false;
        check("Devices settings: a copy has its own choices", saved.DeviceChoices[0].Collapsed);
        var back = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings
        {
            DeviceChoices = [new() { Key = "bt:AA", Name = "Mouse", Kind = DeviceKind.Mouse, Label = "Left", Expanded = false, WarningColor = "#FFAA00" }], DeviceFigures = false,
            CapsuleDevices = true, LastPage = DeckPage.Devices, DefaultPage = DefaultDeckPage.Devices
        }))!;
        check("Devices settings: survive saving", back.DeviceChoices[0] is { Key: "bt:AA", Name: "Mouse", Kind: DeviceKind.Mouse, Label: "Left", Collapsed: true, Expanded: false, WarningColor: "#FFAA00" }
            && !back.DeviceFigures && back.CapsuleDevices && back.ShowsDevices(CompactStyle.Capsule) && !back.ShowsDevices(CompactStyle.Notch)
            && back.LastPage == DeckPage.Devices && back.DefaultPage == DefaultDeckPage.Devices);
        check("Devices settings: the page is saved as number 4, its opening choice as 5 (3 and 4 are kept free for a page in review)",
            (int)DeckPage.Devices == 4 && (int)DefaultDeckPage.Devices == 5 && DeckPages.Opening(DefaultDeckPage.Devices) == DeckPage.Devices);
        var sized = new DeckSettings();
        DeckPages.SetSize(sized, DeckPage.Devices, 600, 250);
        check("Devices settings: a drag sizes the devices page alone", DeckPages.Size(sized, DeckPage.Devices) == (600, 250) && sized.Width == new DeckSettings().Width);
        // A file saved before the devices page has none of its members and needs no migration step: the page takes its defaults, joins
        // its default place in the page order and the scale and the other settings read as they were.
        var older = SettingsMigrations.Read("""{"AppVersion":"1.2.2","MonitorPage":true,"PanelScale":150,"PageOrder":[2,0,1]}""");
        check("Devices settings: a file from before the page reads with the page's defaults, in its default place",
            older.DevicesPage && older.DevicesWidth == 460 && older.DevicesHeight == 200 && older.DeviceChoices.Count == 0 && older.DeviceLowPercent == Devices.DefaultLowPercent
            && older.PageOrder.SequenceEqual([DeckPage.Monitor, DeckPage.Usage, DeckPage.Music, DeckPage.Clipboard, DeckPage.Devices]) && older.PanelScale == 150);
        // 1.3.0: a device switched off was off on the page and in the collapsed row; the battery reading among the system readings
        // becomes the devices' own part where the readings showed it; getting low was ten points above low.
        var hidden = SettingsMigrations.Read("""
            {"AppVersion":"1.2.4","HiddenDevices":[{"Key":"bt:AA","Name":"Mouse"}],"DeviceLowPercent":20,"NotchMonitor":false,"CapsuleMonitor":true,
             "CompactReadings":[{"Item":2,"Visible":true},{"Item":256,"Visible":true}],"CapsuleOrder":[2,0,1]}
            """, new Version(1, 3, 0), out _);
        check("Devices settings: a device switched off before 1.3.0 stays off everywhere",
            hidden.DeviceChoices.Single() is { Key: "bt:AA", Name: "Mouse", Collapsed: false, Expanded: false, Label: null });
        check("Devices settings: the battery reading becomes the devices' part where the readings showed",
            hidden.CapsuleDevices && !hidden.NotchDevices && hidden.CompactReadings.All(r => r.Item != CompactMonitorItems.Battery)
            && hidden.CapsuleOrder.Last() == CompactPart.Devices && hidden.DeviceWarningPercent == 30);
        var unshown = SettingsMigrations.Read("""{"AppVersion":"1.2.4","CapsuleMonitor":true,"CompactReadings":[{"Item":256,"Visible":false}]}""", new Version(1, 3, 0), out _);
        check("Devices settings: a battery reading that was hidden leaves the part off", !unshown.CapsuleDevices && !unshown.NotchDevices);

        static DeviceBattery Device(string key, string name, int? percent, DeviceKind kind = DeviceKind.Mouse) => new(key, name, kind, DeviceSource.Bluetooth, percent);
        IReadOnlyList<DeviceBattery> all = [Device("a", "A", 80), Device("b", "B", 20), Device("c", "C", null, DeviceKind.Pen), Device("d", "D", 50)];
        List<DeviceChoice> choices = [new() { Key = "c" }, new() { Key = "b", Label = "Mine", Collapsed = false }, new() { Key = "gone", Name = "Gone", Expanded = false }];
        check("Device lists: remembered devices first in their order, the others after in the roster's, under the user's names",
            DeviceLists.Ordered(all, choices).Select(d => d.Name).SequenceEqual(["C", "Mine", "A", "D"]));
        check("Device lists: the page leaves out the devices switched off for it", DeviceLists.Expanded(all, [new() { Key = "a", Expanded = false }]).Select(d => d.Key).SequenceEqual(["b", "c", "d"]));
        check("Device lists: the collapsed row leaves out its own switched off, in the list's order",
            DeviceLists.Collapsed(all, choices).Select(d => d.Key).SequenceEqual(["c", "a", "d"]));
        var remembered = DeviceLists.Remember(all, choices, ["d", "a"]);
        check("Device lists: remembering keeps every device and every one not connected, in the new order, with their current names",
            remembered.Select(c => c.Key).SequenceEqual(["d", "a", "c", "b", "gone"]) && remembered[3] is { Name: "B", Label: "Mine", Collapsed: false }
            && remembered[4] is { Name: "Gone", Expanded: false } && remembered[2].Kind == DeviceKind.Pen && !ReferenceEquals(remembered[3], choices[1]));
        Adoption(check);
    }

    private static void Adoption(Action<string, bool> check)
    {
        // 1. A Bluetooth mouse re-recognized under a new address keeps its label, colors, switches and order, without leaving the old entry offline.
        IReadOnlyList<DeviceBattery> btNew =
        [
            new("bt:OTHER", "Keyboard", DeviceKind.Keyboard, DeviceSource.Bluetooth, 90),
            new("bt:AA12", "HUAWEI Mouse WYN", DeviceKind.Mouse, DeviceSource.Gatt, 80) { Address = "AA12" }
        ];
        List<DeviceChoice> btChoices =
        [
            new()
            {
                Key = "bt:AA11", Name = "HUAWEI Mouse WYN", Kind = DeviceKind.Mouse, Label = "Work Mouse",
                Collapsed = false, Expanded = false, Color = "#112233", ChargeColor = "#00FF00", WarningColor = "#FFAA00", CriticalColor = "#FF0000"
            },
            new() { Key = "bt:OTHER", Name = "Keyboard", Kind = DeviceKind.Keyboard }
        ];
        var btAdopted = DeviceLists.Adopt(btNew, btChoices);
        check("Device adoption: a Bluetooth mouse under a new address keeps its choices and position",
            btAdopted is { Count: 2 }
            && btAdopted[0] is { Key: "bt:AA12", Name: "HUAWEI Mouse WYN", Kind: DeviceKind.Mouse, Label: "Work Mouse", Collapsed: false, Expanded: false, Color: "#112233", ChargeColor: "#00FF00", WarningColor: "#FFAA00", CriticalColor: "#FF0000" }
            && DeviceLists.Ordered(btNew, btChoices).Select(d => d.Name).SequenceEqual(["Work Mouse", "Keyboard"])
            && DeviceLists.Collapsed(btNew, btChoices).Select(d => d.Key).SequenceEqual(["bt:OTHER"])
            && DeviceLists.Expanded(btNew, btChoices).Select(d => d.Key).SequenceEqual(["bt:OTHER"])
            && DeviceLists.Remember(btNew, btChoices).Select(c => c.Key).SequenceEqual(["bt:AA12", "bt:OTHER"]));

        // Also check Collapsed = true stays true when re-recognized.
        List<DeviceChoice> btShownChoices = [new() { Key = "bt:AA11", Name = "HUAWEI Mouse WYN", Kind = DeviceKind.Mouse, Label = "Desk", Collapsed = true, Expanded = true, Color = "#224466" }];
        check("Device adoption: a Bluetooth mouse with Collapsed on stays shown under its label",
            DeviceLists.Collapsed(btNew, btShownChoices).Single(d => d.Key == "bt:AA12").Name == "Desk"
            && DeviceLists.Adopt(btNew, btShownChoices)?[0] is { Key: "bt:AA12", Collapsed: true, Color: "#224466" });

        // 2. Switching between 2.4 GHz dongle, USB cable, and Bluetooth keeps choices across all links.
        IReadOnlyList<DeviceBattery> dongle = [new("razer:1532:00C1", "Razer Viper V3 Pro", DeviceKind.Mouse, DeviceSource.Razer, 75) { Hardware = "1532:00C1" }];
        IReadOnlyList<DeviceBattery> cable = [new("razer:1532:00C0", "Razer Viper V3 Pro", DeviceKind.Mouse, DeviceSource.Razer, 78) { Hardware = "1532:00C0", Charging = true }];
        IReadOnlyList<DeviceBattery> bluetooth = [new("bt:CCDD", "Razer Viper V3 Pro", DeviceKind.Mouse, DeviceSource.Bluetooth, 76) { Address = "CCDD" }];
        List<DeviceChoice> triChoices = [new() { Key = "razer:1532:00C1", Name = "Razer Viper V3 Pro", Kind = DeviceKind.Mouse, Hardware = "1532:00C1", Label = "Viper", Color = "#00FF88", Collapsed = false }];
        var onCable = DeviceLists.Adopt(cable, triChoices);
        var onBt = DeviceLists.Adopt(bluetooth, onCable ?? []);
        var backOnDongle = DeviceLists.Adopt(dongle, onBt ?? []);
        check("Device adoption: switching between dongle, cable and Bluetooth keeps choices and updates Hardware",
            onCable?.Single() is { Key: "razer:1532:00C0", Hardware: "1532:00C0", Label: "Viper", Color: "#00FF88", Collapsed: false }
            && onBt?.Single() is { Key: "bt:CCDD", Hardware: null, Read: true, Label: "Viper", Color: "#00FF88", Collapsed: false }
            && backOnDongle?.Single() is { Key: "razer:1532:00C1", Hardware: "1532:00C1", Label: "Viper", Color: "#00FF88", Collapsed: false });

        // 3. An unread USB device ("hid:VVVV:PPPP") later answering under its brand reader adopts by Hardware even when name and kind were upgraded.
        IReadOnlyList<DeviceBattery> mchose = [new("mchose:3837:1015", "MCHOSE G75 Pro", DeviceKind.Keyboard, DeviceSource.Hid, 64) { Hardware = "3837:1015" }];
        List<DeviceChoice> unreadChoices = [new() { Key = "hid:3837:1015", Name = "3837:1015", Kind = DeviceKind.Other, Hardware = "3837:1015", Label = "G75", Color = "#AABBCC", Collapsed = false }];
        check("Device adoption: an unread USB entry upgrades its Key, Name and Kind by Hardware",
            DeviceLists.Adopt(mchose, unreadChoices)?.Single() is { Key: "mchose:3837:1015", Name: "MCHOSE G75 Pro", Kind: DeviceKind.Keyboard, Hardware: "3837:1015", Label: "G75", Color: "#AABBCC", Collapsed: false });

        // 4a. Non-adoption guard: two controllers of one model connected at once or two offline choices of one model are never guessed.
        IReadOnlyList<DeviceBattery> twoPads =
        [
            new("ps:1", "DualSense", DeviceKind.Controller, DeviceSource.Sony, 40) { Hardware = "054C:0CE6" },
            new("ps:2", "DualSense", DeviceKind.Controller, DeviceSource.Sony, 90) { Hardware = "054C:0CE6" }
        ];
        List<DeviceChoice> oneOfflinePad = [new() { Key = "ps:old", Name = "DualSense", Kind = DeviceKind.Controller, Hardware = "054C:0CE6", Label = "P1" }];
        IReadOnlyList<DeviceBattery> onePad = [new("ps:3", "DualSense", DeviceKind.Controller, DeviceSource.Sony, 55) { Hardware = "054C:0CE6" }];
        List<DeviceChoice> twoOfflinePads =
        [
            new() { Key = "ps:old1", Name = "DualSense", Kind = DeviceKind.Controller, Hardware = "054C:0CE6", Label = "P1" },
            new() { Key = "ps:old2", Name = "DualSense", Kind = DeviceKind.Controller, Hardware = "054C:0CE6", Label = "P2" }
        ];
        check("Device adoption: ambiguous identical models are never guessed",
            DeviceLists.Adopt(twoPads, oneOfflinePad) is null && DeviceLists.Adopt(onePad, twoOfflinePads) is null);

        // 4b. Non-adoption guard: an already-connected choice is never stolen by a second connected device of the same name.
        IReadOnlyList<DeviceBattery> secondPad =
        [
            new("ps:1", "DualSense", DeviceKind.Controller, DeviceSource.Sony, 40) { Hardware = "054C:0CE6" },
            new("ps:2", "DualSense", DeviceKind.Controller, DeviceSource.Sony, 90) { Hardware = "054C:0CE6" }
        ];
        List<DeviceChoice> activePlusOffline =
        [
            new() { Key = "ps:1", Name = "DualSense", Kind = DeviceKind.Controller, Hardware = "054C:0CE6", Label = "Mine" },
            new() { Key = "ps:old", Name = "DualSense", Kind = DeviceKind.Controller, Hardware = "054C:0CE6", Label = "Spare" }
        ];
        check("Device adoption: a second connected device of the same model does not steal an offline choice",
            DeviceLists.Adopt(secondPad, activePlusOffline) is null);

        // 4c. Non-adoption guard: Read = false on USB Hardware is not erased by Bluetooth, while USB with the same Hardware keeps Read = false.
        IReadOnlyList<DeviceBattery> btSameName = [new("bt:1122", "MCHOSE G75 Pro", DeviceKind.Keyboard, DeviceSource.Bluetooth, 70) { Address = "1122" }];
        IReadOnlyList<DeviceBattery> usbSameHw = [new("mchose:3837:1015", "MCHOSE G75 Pro", DeviceKind.Keyboard, DeviceSource.Hid, null) { Hardware = "3837:1015" }];
        List<DeviceChoice> readOffChoice = [new() { Key = "hid:3837:1015", Name = "MCHOSE G75 Pro", Kind = DeviceKind.Keyboard, Hardware = "3837:1015", Read = false, Label = "KB" }];
        check("Device adoption: Read = false on USB hardware is kept for USB and not erased by Bluetooth",
            DeviceLists.Adopt(btSameName, readOffChoice) is null
            && DeviceLists.Adopt(usbSameHw, readOffChoice)?.Single() is { Key: "mchose:3837:1015", Hardware: "3837:1015", Read: false, Label: "KB" });
    }

    private static void Strip(Action<string, bool> check)
    {
        var older = CompactMonitor.Normalize([new CompactReading { Item = CompactMonitorItems.Cpu }, new CompactReading { Item = CompactMonitorItems.Battery }]);
        check("Battery reading: retired, a saved table drops it", older.All(r => r.Item != CompactMonitorItems.Battery)
            && !CompactMonitor.Order.Contains(CompactMonitorItems.Battery) && (CompactMonitor.Every & CompactMonitorItems.Battery) == 0);
        check("Devices part: a saved order without it takes it at the end", CompactParts.Normalize([CompactPart.Clock, CompactPart.Rings]).Last() == CompactPart.Devices);
    }
}
