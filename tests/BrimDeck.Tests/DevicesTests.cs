using System.Text.Json;
using BrimDeck.Core;

// The devices page's core: what a device is by its Bluetooth category, appearance or name; its address and its vendor and
// product from Windows' ids; one entry per device across sources; the levels and the low battery notice, once per discharge;
// the page's layout steps; the settings; and the collapsed battery reading's place among the readings.
static class DevicesTests
{
    public static void Run(Action<string, bool> check)
    {
        Kinds(check);
        Ids(check);
        Roster(check);
        Levels(check);
        LowNotice(check);
        Layout(check);
        Settings(check);
        Strip(check);
    }

    private static void Kinds(Action<string, bool> check)
    {
        check("Devices: Windows' Bluetooth categories give the kind", Devices.Kind("Input.Mouse", null, "MCHOSE A7 V2 Ultra+") == DeviceKind.Mouse
            && Devices.Kind("Input.Gaming.Gamepad", 964, "Xbox Wireless Controller") == DeviceKind.Controller
            && Devices.Kind("Input.Keyboard", null, "F99") == DeviceKind.Keyboard
            && Devices.Kind("Communication.Headset.Bluetooth", null, "WH-1000XM4") == DeviceKind.Headphones);
        check("Devices: a headset whose name says earbuds is earbuds", Devices.Kind("Communication.Headset.Bluetooth", null, "OPPO Enco X3") == DeviceKind.Earbuds
            && Devices.Kind("Communication.Headset.Bluetooth", null, "AirPods Pro") == DeviceKind.Earbuds);
        check("Devices: the LE appearance gives the kind", Devices.Kind(null, 0x03C2, "x") == DeviceKind.Mouse && Devices.Kind(null, 0x03C1, "x") == DeviceKind.Keyboard
            && Devices.Kind(null, 0x03C4, "x") == DeviceKind.Controller && Devices.Kind(null, 0x03C7, "x") == DeviceKind.Pen && Devices.Kind(null, 0x0941, "x") == DeviceKind.Earbuds
            && Devices.Kind(null, 0x0943, "x") == DeviceKind.Headphones);
        check("Devices: names give the kind in Chinese and English", Devices.Named("罗技无线鼠标") == DeviceKind.Mouse && Devices.Named("Logitech MX Keys") == DeviceKind.Keyboard
            && Devices.Named("Surface Pen") == DeviceKind.Pen && Devices.Named("DualSense Wireless Controller") == DeviceKind.Controller && Devices.Named("Bose QC45") == DeviceKind.Headphones);
        check("Devices: a word inside another word is not a kind", Devices.Named("OpenRGB hub") is null && Devices.Named("联想笔记本") is null && Devices.Kind(null, null, "F99 MAX Dongle") == DeviceKind.Other);
    }

    private static void Ids(Action<string, bool> check)
    {
        check("Devices: the address from a BTHLE node, a hands-free node and an endpoint",
            Devices.AddressIn(@"BTHLE\Dev_cd5994078968\7&2dd3ebc8&0&cd5994078968") == "CD5994078968"
            && Devices.AddressIn(@"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0d58\7&8ee6b29&0&8099E7C236A8_C00000000") == "8099E7C236A8"
            && Devices.AddressIn("Bluetooth#Bluetooth3c:91:80:12:34:56-cd:59:94:07:89:68") == "CD5994078968");
        check("Devices: no address where there is none", Devices.AddressIn(@"BTH\MS_BTHLE\6&19f74fd8&0&3") is null && Devices.AddressIn(null) is null);
        check("Devices: vendor and product from LE, classic and USB ids",
            Devices.HardwareIn(@"BTHLEDevice\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02045e_PID&0b13_REV&0517_0c3526eaff88\8&1") == "045E:0B13"
            && Devices.HardwareIn(@"BTHENUM\{0000111e}_VID&0002054c_PID&0d58\7&8") == "054C:0D58"
            && Devices.HardwareIn(@"HID\VID_046D&PID_C52B&MI_02&Col01\8&1") == "046D:C52B" && Devices.Hardware(0x45E, 0xB13) == "045E:0B13");
    }

    private static DeviceBattery Bt(string address, int? percent, string name = "Mouse", DeviceKind kind = DeviceKind.Mouse) =>
        new("bt:" + address, name, kind, DeviceSource.Bluetooth, percent) { Address = address };

    private static void Roster(Action<string, bool> check)
    {
        var roster = new DeviceRoster();
        check("Devices: a source's list fills the roster", roster.Set(DeviceSource.Bluetooth, [Bt("AA", 50), Bt("BB", null, "Buds", DeviceKind.Earbuds)]) && roster.All.Count == 2);
        check("Devices: the same list again changes nothing", !roster.Set(DeviceSource.Bluetooth, [Bt("AA", 50), Bt("BB", null, "Buds", DeviceKind.Earbuds)]));
        // GATT reads the earbuds Windows has no level for: one entry, the reading's, under the Bluetooth key.
        roster.Set(DeviceSource.Gatt, [new DeviceBattery("bt:BB", "Buds", DeviceKind.Earbuds, DeviceSource.Gatt, 70) { Address = "BB" }]);
        check("Devices: two sources of one device make one entry with the reading", roster.All.Count == 2 && roster.All.Single(d => d.Key == "bt:BB").Percent == 70);
        // A vendor source (Logitech) keys by its own id but knows the address: it replaces the plain Bluetooth reading.
        roster.Set(DeviceSource.Logitech, [new DeviceBattery("hidpp:1", "MX Master 3", DeviceKind.Mouse, DeviceSource.Logitech, 85) { Address = "AA", Charging = true }]);
        var aa = roster.All.Single(d => d.Key == "bt:AA");
        check("Devices: the more exact source wins and the Bluetooth key stays", roster.All.Count == 2 && aa.Source == DeviceSource.Logitech && aa.Percent == 85 && aa.Charging && aa.Name == "MX Master 3");
        roster.Set(DeviceSource.Gaming, [new DeviceBattery("gip:x", "Xbox", DeviceKind.Controller, DeviceSource.Gaming, 40) { Hardware = "045E:0B13" }]);
        roster.Set(DeviceSource.Bluetooth, [Bt("AA", 50), Bt("BB", null, "Buds", DeviceKind.Earbuds), Bt("CC", 100, "Xbox Wireless Controller", DeviceKind.Controller) with { Hardware = "045E:0B13" }]);
        check("Devices: a controller seen by Windows.Gaming.Input and Bluetooth is one", roster.All.Count(d => d.Kind == DeviceKind.Controller) == 1);
        check("Devices: the order is by kind, then name", roster.All.Select(d => d.Kind).SequenceEqual([DeviceKind.Mouse, DeviceKind.Earbuds, DeviceKind.Controller]));
        roster.Set(DeviceSource.Logitech, []);
        var two = DeviceRoster.Merge([
            new DeviceBattery("ps:1", "DualSense", DeviceKind.Controller, DeviceSource.Sony, 40) { Hardware = "054C:0CE6" },
            new DeviceBattery("ps:2", "DualSense", DeviceKind.Controller, DeviceSource.Sony, 90) { Hardware = "054C:0CE6" },
            Bt("X1", null, "DualSense Wireless Controller", DeviceKind.Controller) with { Hardware = "054C:0CE6" },
            Bt("X2", null, "DualSense Wireless Controller", DeviceKind.Controller) with { Hardware = "054C:0CE6" }]);
        check("Devices: two controllers of one model stay two each, not one", two.Count == 4);
        var chain = DeviceRoster.Merge([Bt("AA", null), new DeviceBattery("logitech:1", "M", DeviceKind.Mouse, DeviceSource.Logitech, 50) { Address = "AA" },
            new DeviceBattery("logitech:1", "M", DeviceKind.Mouse, DeviceSource.Hid, 49)]);
        check("Devices: entries linked through another are one, under the Bluetooth key", chain.Count == 1 && chain[0].Key == "bt:AA" && chain[0].Percent == 50);
        check("Devices: a source that empties leaves the others", roster.All.Single(d => d.Key == "bt:AA").Source == DeviceSource.Bluetooth);
        var parts = new DeviceBattery("bt:DD", "AirPods", DeviceKind.Earbuds, DeviceSource.Apple, 90)
        { Parts = [new(BatteryPart.Left, 80, false), new(BatteryPart.Right, 60, false), new(BatteryPart.Case, 10, false)] };
        check("Devices: earbuds' lowest is the lower bud, not the case", parts.Lowest == 60);
        check("Devices: a device that reports nothing has no lowest", Bt("EE", null).Lowest is null);
    }

    private static void Levels(Action<string, bool> check)
    {
        check("Devices: levels at the threshold and ten above", Devices.Level(15, false, 15) == MemoryLevel.Critical && Devices.Level(16, false, 15) == MemoryLevel.Elevated
            && Devices.Level(25, false, 15) == MemoryLevel.Elevated && Devices.Level(26, false, 15) == MemoryLevel.Normal);
        check("Devices: a charging or unreported battery is never low", Devices.Level(3, true, 15) == MemoryLevel.Normal && Devices.Level(null, false, 15) == MemoryLevel.Normal);
        check("Devices: figures", Devices.Figure(42) == "42%" && Devices.Figure(null) == "—" && Devices.Figure(130) == "100%");
    }

    private static void LowNotice(Action<string, bool> check)
    {
        var watch = new LowBatteryWatch();
        check("Low battery: nothing above the threshold", watch.Update([Bt("AA", 40)], 15).Count == 0);
        check("Low battery: falling to it notices once", watch.Update([Bt("AA", 15)], 15).Count == 1 && watch.Update([Bt("AA", 14)], 15).Count == 0 && watch.Update([Bt("AA", 5)], 15).Count == 0);
        check("Low battery: a reading that wobbles back a point does not notice again", watch.Update([Bt("AA", 17)], 15).Count == 0 && watch.Update([Bt("AA", 15)], 15).Count == 0);
        watch.Update([Bt("AA", 15) with { Charging = true }], 15);
        check("Low battery: after charging it notices again", watch.Update([Bt("AA", 12)], 15).Count == 1);
        watch.Update([Bt("AA", 60)], 15);
        check("Low battery: after climbing well above it notices again", watch.Update([Bt("AA", 10)], 15).Count == 1);
        check("Low battery: a charging device or one without a reading never notices", watch.Update([Bt("BB", 5) with { Charging = true }, Bt("CC", null)], 15).Count == 0);
        var gone = new LowBatteryWatch();
        gone.Update([Bt("AA", 10)], 15); gone.Update([], 15);
        check("Low battery: a device that drops off and returns does not notice again", gone.Update([Bt("AA", 10)], 15).Count == 0);
    }

    private static void Layout(Action<string, bool> check)
    {
        // The default page's content: 420 × 148.
        var one = DevicesLayout.For(420, 148, 1, null);
        var two = DevicesLayout.For(420, 148, 2, one);
        check("Devices layout: one and two devices share the comfortable step (no rebuild as the second arrives)", one == two && !one.Dense && one.Columns == 1 && one.Room == 2);
        var three = DevicesLayout.For(420, 148, 3, two);
        check("Devices layout: a third device takes the dense step, which holds six", three.Dense && three.Room == 6 && three.Columns == 2);
        check("Devices layout: four to six devices stay in that step", DevicesLayout.For(420, 148, 6, three) == three && DevicesLayout.For(420, 148, 4, three) == three);
        check("Devices layout: back to two keeps the dense step; one returns to the comfortable one", DevicesLayout.For(420, 148, 2, three) == three && !DevicesLayout.For(420, 148, 1, three).Dense);
        var wide = DevicesLayout.For(660, 248, 6, null);
        check("Devices layout: a large page holds six comfortably", !wide.Dense && wide.Room >= 6);
        check("Devices layout: no card is wider than the maximum", DevicesLayout.For(1160, 348, 1, null).CardSize(1160, 348).Width <= DevicesLayout.MaximumCard);
        var held = DevicesLayout.For(420, 148, 3, null);
        check("Devices layout: a resize within the band keeps the step", DevicesLayout.For(420 + DevicesLayout.Hysteresis - 1, 148, 3, held) == held);
        var (x, y, w, h) = three.Card(4, 420, 148, 6);
        check("Devices layout: cards lie within the page", x >= 0 && y >= 0 && x + w <= 420 + .5 && y + h <= 148 + .5 && w > 0 && h <= DevicesLayout.MaximumRow);
        var (alone, _, _, _) = wide.Card(0, 660, 248, 1);
        var (single, _) = wide.CardSize(660, 248);
        check("Devices layout: a single device sits in the middle", Math.Abs(alone + single / 2 - 330) <= 1);
        check("Devices layout: a size that is not a number is taken as nothing", DevicesLayout.For(double.NaN, double.PositiveInfinity, 3, null).Columns == 1);
    }

    private static void Settings(Action<string, bool> check)
    {
        var defaults = new DeckSettings();
        check("Devices settings: the page is on, the notice at 15 %", defaults.DevicesPage && defaults.DeviceLowNotice && defaults.DeviceLowPercent == 15 && defaults.HiddenDevices.Count == 0);
        var saved = new DeckSettings { DevicesWidth = 9999, DevicesHeight = double.NaN, DeviceLowPercent = 7, HiddenDevices = [new("bt:AA", "Mouse"), new("bt:AA", "Mouse"), new(" ", "x")] };
        saved.Normalize();
        check("Devices settings: normalized", saved.DevicesWidth == 1200 && saved.DevicesHeight == 200 && saved.DeviceLowPercent == Devices.DefaultLowPercent && saved.HiddenDevices.Count == 1);
        var back = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { HiddenDevices = [new("bt:AA", "Mouse")], LastPage = DeckPage.Devices, DefaultPage = DefaultDeckPage.Devices }))!;
        check("Devices settings: survive saving", back.Hides("bt:AA") && back.HiddenDevices[0].Name == "Mouse" && back.LastPage == DeckPage.Devices && back.DefaultPage == DefaultDeckPage.Devices);
        check("Devices settings: the page is saved as number 4, its opening choice as 5", (int)DeckPage.Devices == 4 && (int)DefaultDeckPage.Devices == 5 && DeckPages.Opening(DefaultDeckPage.Devices) == DeckPage.Devices);
        var sized = new DeckSettings();
        DeckPages.SetSize(sized, DeckPage.Devices, 600, 250);
        check("Devices settings: a drag sizes the devices page alone", DeckPages.Size(sized, DeckPage.Devices) == (600, 250) && sized.Width == new DeckSettings().Width);
    }

    private static void Strip(Action<string, bool> check)
    {
        var older = CompactMonitor.Normalize([new CompactReading { Item = CompactMonitorItems.Cpu }]);
        check("Battery reading: an older table gains it hidden at the end", older[^1].Item == CompactMonitorItems.Battery && !older[^1].Visible);
        check("Battery reading: it leaves first when the row is short", CompactMonitor.LeaveOrder[0] == CompactMonitorItems.Battery && (CompactMonitor.Every & CompactMonitorItems.Battery) != 0);
        var readings = CompactMonitor.Normalize(null);
        foreach (var reading in readings) reading.Visible = reading.Item == CompactMonitorItems.Battery;
        CompactMonitor.Apply(readings, CompactMonitorMode.Full);
        check("Battery reading: the Full preset leaves it as it is", readings.Single(r => r.Item == CompactMonitorItems.Battery).Visible);
        CompactMonitor.Apply(readings, CompactMonitorMode.Brief);
        check("Battery reading: the Brief preset hides it", !readings.Single(r => r.Item == CompactMonitorItems.Battery).Visible);
        var admission = new StripAdmission();
        var table = CompactMonitor.Normalize(null);
        var (hidden, _) = admission.Update(null, table, SensorStates.Unknown, SensorState.Unknown);
        check("Battery reading: left out until a device reports", (hidden & CompactMonitorItems.Battery) != 0);
        var (found, arriving) = admission.Update(null, table, SensorStates.Unknown, SensorState.Found);
        check("Battery reading: joins with the first reading", (found & CompactMonitorItems.Battery) == 0 && (arriving & CompactMonitorItems.Battery) != 0);
        var (held, _) = admission.Update(null, table, SensorStates.Unknown, SensorState.Unknown);
        check("Battery reading: stays while the reading is away a moment", (held & CompactMonitorItems.Battery) == 0);
        var (none, _) = admission.Update(null, table, SensorStates.Unknown, SensorState.None);
        check("Battery reading: leaves once no device reports", (none & CompactMonitorItems.Battery) != 0);
    }
}
