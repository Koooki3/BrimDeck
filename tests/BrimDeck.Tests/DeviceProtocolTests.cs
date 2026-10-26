using BrimDeck.Core;

// The vendor protocols against the bytes they are documented with: requests byte for byte (with their checksums), and replies
// read as the devices send them, other reports passed over.
static class DeviceProtocolTests
{
    public static void Run(Action<string, bool> check)
    {
        Logitech(check);
        RazerReports(check);
        DongleFrames(check);
        HeadsetReplies(check);
        Controllers(check);
        Usages(check);
        Apple(check);
    }

    private static byte[] Long(byte device, byte feature, int function, params byte[] parameters)
    {
        var report = new byte[20];
        report[0] = 0x11; report[1] = device; report[2] = feature; report[3] = (byte)((function << 4) | Hidpp.SoftwareId);
        parameters.CopyTo(report, 4);
        return report;
    }

    private static void Logitech(Action<string, bool> check)
    {
        check("HID++: a long request, padded", Hidpp.Request(1, 0x00, 1, 0, 0, 0x5A).SequenceEqual(new byte[] { 0x11, 1, 0, 0x1D, 0, 0, 0x5A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        check("HID++: the reply to its request", Hidpp.Match(Long(1, 0x08, 1, 55, 4, 1), 1, 0x08, 1).Reply is [55, 4, 1, ..]);
        check("HID++: another device's or another function's report is not the reply", Hidpp.Match(Long(2, 0x08, 1, 55), 1, 0x08, 1) == (null, null)
            && Hidpp.Match(Long(1, 0x08, 0, 55), 1, 0x08, 1) == (null, null));
        check("HID++: a 2.0 error", Hidpp.Match([0x11, 1, 0xFF, 0x08, 0x1D, 0x05, 0, 0], 1, 0x08, 1).Error == 0x05);
        check("HID++: a 1.0 error (the device speaks only 1.0)", Hidpp.Match([0x10, 1, 0x8F, 0x00, 0x1D, 0x01, 0], 1, 0x00, 1).Error == Hidpp.InvalidSubId);
        check("HID++: an event (software id 0) is not a reply", Hidpp.Match([0x11, 1, 0x08, 0x00, 50, 0, 0], 1, 0x08, 0) == (null, null));
        check("HID++: receivers hold six devices, a cable one", Hidpp.Indices(Hidpp.IsReceiver(0xC52B, "USB Receiver")).Count == 6 && Hidpp.Indices(Hidpp.IsReceiver(0xC08B, "G502 HERO")).SequenceEqual(new byte[] { 0xFF })
            && Hidpp.IsReceiver(0xC548, "") && Hidpp.IsReceiver(0x1234, "LIGHTSPEED Receiver"));
        check("HID++: 0x1004 with a state of charge, charging", Hidpp.Unified([55, 4, 1, 0], [0x0F, 0x03]) == new BatteryRead(55, true, false));
        check("HID++: 0x1004 without one maps its steps", Hidpp.Unified([0, 2, 0, 0], [0x0F, 0x01]) == new BatteryRead(20, false, true)
            && Hidpp.Unified([0, 8, 3, 0], [0x0F, 0x01]) == new BatteryRead(100, true, true));
        check("HID++: 0x1000 level and status", Hidpp.Status([70, 50, 0]) == new BatteryRead(70) && Hidpp.Status([90, 50, 1]).Charging && Hidpp.Status([0, 0, 3]).Percent == 100
            && Hidpp.Status([0, 0, 0]).Percent is null);
        check("HID++: 0x1001 millivolts big endian on the curve", Hidpp.Voltage([0x0F, 0x50, 0x00]) is { Percent: 70, Charging: false, Coarse: true } && Hidpp.Voltage([0x10, 0x5A, 0x80]) is { Percent: 100, Charging: true });
        check("HID++: the voltage curve's ends and a point between", Hidpp.VoltageToPercent(4300) == 100 && Hidpp.VoltageToPercent(3400) == 0 && Hidpp.VoltageToPercent(3811) == 50);
        check("HID++: 0x1F20 headsets, full and charging by bit", Hidpp.Adc([0x0F, 0xA0, 0x07]) is { Percent: 100, Charging: true } && Hidpp.Adc([0x0F, 0xA0, 0x01]) is { Charging: false, Coarse: true }
            && Hidpp.Adc([0x0F, 0x50, 0x03]) is { Percent: 70, Charging: true });
        check("HID++: 0x1F20 without a valid measurement (the headset off) is no reading, not 0 %", Hidpp.Adc([0, 0, 0]) is null && Hidpp.Adc([0x0F, 0xA0, 0x02]) is null);
        check("HID++ 1.0: register 0x0D and 0x07", Hidpp.Mileage([80, 0, 0x40]) == new BatteryRead(80, true) && Hidpp.Level([5, 0x00]) is { Percent: 50, Coarse: true } && Hidpp.Level([3, 0x22]).Percent == 100
            && Hidpp.RegisterRequest(2, 0x0D).SequenceEqual(new byte[] { 0x10, 2, 0x81, 0x0D, 0, 0, 0 }));
        check("HID++: device types", Hidpp.Kind(0) == DeviceKind.Keyboard && Hidpp.Kind(3) == DeviceKind.Mouse && Hidpp.Kind(5) == DeviceKind.Mouse && Hidpp.Kind(8) == DeviceKind.Headphones && Hidpp.Kind(7) == DeviceKind.Other);
    }

    private static void RazerReports(Action<string, bool> check)
    {
        var request = Razer.Request(0x1F, Razer.Battery);
        check("Razer: the battery request as razer-battery-report encodes it", request.Length == 90 && request.Take(8).SequenceEqual(new byte[] { 0x00, 0x1F, 0x00, 0x00, 0x00, 0x02, 0x07, 0x80 })
            && request[88] == (0x02 ^ 0x07 ^ 0x80) && request[89] == 0 && request.Skip(8).Take(80).All(b => b == 0));
        var reply = Razer.Request(0x1F, Razer.Battery); reply[0] = Razer.Success; reply[9] = 0xFF; reply[88] = Razer.Checksum(reply);
        check("Razer: a successful reply and its level", Razer.Reply(reply, Razer.Battery) == (Razer.Success, 0xFF) && Razer.Percent(0xFF) == 100 && Razer.Percent(0) == 0 && Razer.Percent(128) == 50);
        var garbled = (byte[])reply.Clone(); garbled[10] ^= 1;
        check("Razer: a bad checksum or another command is no reply", Razer.Reply(garbled, Razer.Battery) is null && Razer.Reply(reply, Razer.ChargingState) is null && Razer.Reply(reply[..89], Razer.Battery) is null);
        check("Razer: the catalog's models and transaction ids", Razer.Find(0x00C1) is { Transaction: 0x1F, Name: "Razer Viper V3 Pro" } && Razer.Find(0x007D) is { Transaction: 0x3F }
            && Razer.Find(0x007B) is { Transaction: 0xFF } && Razer.Find(0x00E6) is { Interface: 3 } && Razer.Find(0x9999) is null
            && Razer.Catalog.Select(model => model.Product).Distinct().Count() == Razer.Catalog.Count);
    }

    private static void DongleFrames(Action<string, bool> check)
    {
        check("ATK/VXE: the battery request with its checksum", Dongles.BitMouseRequest(0x07).Take(8).SequenceEqual(new byte[] { 0x08, 0x7D, 0x72, 0x02, 0x00, 0x01, 0x07, 0x01 }));
        check("ATK/VXE: a reply's level; an asleep mouse or a cabled placeholder is none",
            Dongles.BitMouseBattery([0x08, 0x72, 0x00, 0x00, 0x01, 0x07, 0x01, 87, 0x10, 0x0F]) == new BatteryRead(87)
            && Dongles.BitMouseBattery([0x08, 0x72, 0x01, 0x00, 0x01, 0x07, 0x01, 87, 0x10, 0x0F]) is null
            && Dongles.BitMouseBattery([0x08, 0x72, 0x00, 0x00, 0x00, 0x07, 0x01, 0, 0, 0]) is null
            && Dongles.BitMouseCharging([0x08, 0x72, 0x00, 0x00, 0x01, 0x0F, 0x01, 1]) == true);
        var compx = Dongles.CompxRequest(0x04);
        check("Compx: the battery request, its checksum 0x49", compx.Length == 17 && compx[0] == 0x08 && compx[1] == 0x04 && compx[16] == 0x49 && compx.Sum(b => b) % 256 == 0x55);
        check("Compx: a reply; charging only at 1", Dongles.CompxBattery([0x08, 0x04, 0, 0, 0, 1, 63, 1, 0x0F, 0xA0, 0, 0, 0, 0, 0, 0, 0]) == new BatteryRead(63, true)
            && Dongles.CompxBattery([0x08, 0x04, 0, 0, 0, 1, 63, 2]) == new BatteryRead(63) && Dongles.CompxBattery([0x08, 0x03, 0, 0, 0, 0, 63, 0]) is null);
        var vgn = new byte[64]; vgn[0] = 0x04; vgn[3] = 0x1A; vgn[8] = 41; vgn[9] = 0;
        check("VGN keyboards: the request and a reply", Dongles.VgnRequest().Take(5).SequenceEqual(new byte[] { 0x04, 0, 0, 0x1A, 0x06 }) && Dongles.VgnBattery(vgn) == new BatteryRead(41));
        var endgame = new byte[32]; endgame[0] = 0xA1; endgame[1] = 0x01; endgame[16] = 77;
        check("Endgame Gear: the request and a reply; a busy one is none", Dongles.EndgameRequest(32)[..2].SequenceEqual(new byte[] { 0xA1, 0xB4 }) && Dongles.EndgameBattery(endgame) == new BatteryRead(77)
            && Dongles.EndgameBattery([0xA1, 0x03, .. new byte[30]]) is null);
        check("VAXEE: five points a step", Dongles.VaxeeRequest(8)[..5].SequenceEqual(new byte[] { 0x0E, 0xA5, 0x0B, 0x01, 0x01 }) && Dongles.VaxeeBattery([0x0E, 0, 0, 0, 0, 17]) == new BatteryRead(85, Coarse: true));
        var nordic = new byte[16]; nordic[0] = 0x05; nordic[9] = 52;
        check("Nordic receivers", Dongles.NordicRequest(16)[..5].SequenceEqual(new byte[] { 0x05, 0x15, 0, 0, 0x01 }) && Dongles.NordicBattery(nordic) == new BatteryRead(52));
        var inphic = new byte[33]; inphic[0] = 0x0C; inphic[2] = 0x20; inphic[6] = 1; inphic[0x12] = 66; inphic[0x13] = 2;
        check("Inphic IN9: the handshake with its payload, the query, a reply", Dongles.InphicRequest(0xF0, 3, Dongles.InphicHandshake)[..9].SequenceEqual(new byte[] { 0x0C, 0x01, 0xF0, 0x00, 0x03, 0x01, 0x01, 0xFF, 0 })
            && Dongles.InphicRequest(0x20, 3)[..6].SequenceEqual(new byte[] { 0x0C, 0x01, 0x20, 0x00, 0x03, 0 }) && Dongles.InphicRequest(0x20, 3).Length == 33
            && Dongles.InphicBattery(inphic) == new BatteryRead(66, true));
        check("MCHOSE: the device info request with its checksum", Dongles.MchoseRequest()[..9].SequenceEqual(new byte[] { 0x4D, 0x01, 0x01, 0x00, 0x00, 0x09, 0x00, 0x00, 0x08 }));
        var mchose = new byte[64]; mchose[0] = 0x4D; mchose[5] = 0x09; mchose[8 + 10] = 1; mchose[8 + 11] = 0; mchose[8 + 12] = 91;
        var off = (byte[])mchose.Clone(); off[8 + 12] = 0;
        check("MCHOSE: a reply's level; a disconnected mouse is none", Dongles.MchoseBattery(mchose) == new BatteryRead(91) && Dongles.MchoseBattery(off) is null);
        check("MCHOSE: the mouse protocol only for its mice, never the MagDock or the X9", Dongles.IsMchoseMouse(0x5011, "MCHOSE G7 Mouse") && Dongles.IsMchoseMouse(0x5011, "迈从无线鼠标")
            && !Dongles.IsMchoseMouse(0x1012, "MCHOSE MagDock Mouse") && !Dongles.IsMchoseMouse(0x6045, "X9") && !Dongles.IsMchoseMouse(0x4001, "MCHOSE K99"));
    }

    private static void HeadsetReplies(Action<string, bool> check)
    {
        check("Headsets: the HyperX requests go to the vendor collections only", Headsets.Channel(Headsets.Kind.CloudAlpha).UsagePage == Headsets.VendorPages);
        check("Headsets: models found by vendor and product", Headsets.Find(0x1038, 0x12AD) == Headsets.Kind.Arctis7 && Headsets.Find(0x1038, 0x2202) == Headsets.Kind.Nova7Steps
            && Headsets.Find(0x03F0, 0x098D) == Headsets.Kind.CloudAlpha && Headsets.Find(0x3837, 0x6045) == Headsets.Kind.MchoseX9 && Headsets.Find(0x3837, 0x1012) is null);
        check("Headsets: requests", Headsets.Request(Headsets.Kind.Arctis7).SequenceEqual(new byte[] { 0x06, 0x18 }) && Headsets.Request(Headsets.Kind.CloudII).Length == 52
            && Headsets.Request(Headsets.Kind.NovaPro)[..2].SequenceEqual(new byte[] { 0x06, 0xB0 }));
        check("Headsets: Arctis 7 and Arctis 9", Headsets.Battery(Headsets.Kind.Arctis7, [0x06, 0x18, 72]) == new BatteryRead(72)
            && Headsets.Battery(Headsets.Kind.Arctis9, [0, 0, 0, 0x9A, 1]) == new BatteryRead(100, true) && Headsets.Battery(Headsets.Kind.Arctis9, [0, 0, 0, 0x64, 0]) == new BatteryRead(0));
        check("Headsets: a value out of its range is no level, never an empty battery",
            Headsets.Battery(Headsets.Kind.Arctis7, [0x06, 0x18, 200]) is null && Headsets.Battery(Headsets.Kind.Arctis7, [0x01, 0, 0]) is null && Headsets.Battery(Headsets.Kind.Arctis9, [0x02, 0, 0, 0x10, 0]) is null
            && Headsets.Battery(Headsets.Kind.Nova7Steps, [0xB0, 0, 9, 3]) is null);
        check("Headsets: Nova 7 in percent and in steps, off is none", Headsets.Battery(Headsets.Kind.Nova7Percent, [0xB0, 0, 64, 3]) == new BatteryRead(64)
            && Headsets.Battery(Headsets.Kind.Nova7Steps, [0xB0, 0, 3, 1]) == new BatteryRead(75, true, true) && Headsets.Battery(Headsets.Kind.Nova7Percent, [0xB0, 0, 64, 0]) is null);
        var pro = new byte[16]; pro[0] = 0x06; pro[1] = 0xB0; pro[6] = 4; pro[15] = 0x02;
        check("Headsets: Nova Pro in eighths", Headsets.Battery(Headsets.Kind.NovaPro, pro) == new BatteryRead(50, true, true));
        check("Headsets: HyperX Cloud Alpha level and charging", Headsets.Battery(Headsets.Kind.CloudAlpha, [0x21, 0xBB, 0x0B, 58]) == new BatteryRead(58)
            && Headsets.Charging(Headsets.Kind.CloudAlpha, [0x21, 0xBB, 0x0C, 1]) == true && Headsets.Battery(Headsets.Kind.CloudAlpha, [0x21, 0xBB, 0x0C, 1]) is null);
        check("Headsets: HyperX Cloud Alpha asked whether its headset is connected first", Headsets.ConnectionRequest(Headsets.Kind.CloudAlpha) is [0x21, 0xBB, 0x03, ..] and { Length: 31 }
            && Headsets.Connected(Headsets.Kind.CloudAlpha, [0x21, 0xBB, 0x03, 0x01]) == false && Headsets.Connected(Headsets.Kind.CloudAlpha, [0x21, 0xBB, 0x03, 0x02]) == true
            && Headsets.Connected(Headsets.Kind.CloudAlpha, [0x21, 0xBB, 0x0B, 0x01]) is null && Headsets.ConnectionRequest(Headsets.Kind.CloudII) is null);
        check("Headsets: HyperX Cloud II", Headsets.Battery(Headsets.Kind.CloudII, [0x06, 0xFF, 0xBB, 0x02, 0, 0x0F, 0xA0, 81]) == new BatteryRead(81));
        check("Headsets: HyperX Cloud Flight by voltage, on its cable", Headsets.Battery(Headsets.Kind.CloudFlight, [0x21, 0xFF, 0x05, 0x0E, 0xA0]) is { Coarse: true, Percent: 29 }
            && Headsets.Battery(Headsets.Kind.CloudFlight, [0x21, 0xFF, 0x05, 0x10, 0x40]) is { Charging: true, Percent: null });
        check("Headsets: MCHOSE X9, the battery field only; other fields are none", Headsets.Battery(Headsets.Kind.MchoseX9, [0x55, 0x65, 40, 0x02]) == new BatteryRead(40)
            && Headsets.Battery(Headsets.Kind.MchoseX9, [0x55, 0x65, 40, 0x03]) is null && Headsets.Battery(Headsets.Kind.MchoseX9, [0x55, 0x65, 40, 0x04]) is null
            && Headsets.Battery(Headsets.Kind.MchoseX9, [0x55, 0x65, 40, 26]) is null);
    }

    private static void Controllers(Action<string, bool> check)
    {
        var usb = new byte[64]; usb[0] = 0x01; usb[53] = 0x07;
        var bt = new byte[78]; bt[0] = 0x31; bt[54] = 0x15;
        var full = new byte[64]; full[0] = 0x01; full[53] = 0x2A;
        check("DualSense: USB and Bluetooth reports", PlayStation.Battery(0x0CE6, usb, bluetooth: false) == new BatteryRead(75) && PlayStation.Battery(0x0CE6, bt, bluetooth: true) == new BatteryRead(55, true)
            && PlayStation.Battery(0x0CE6, full, bluetooth: false) == new BatteryRead(100));
        // Windows pads every report to the collection's longest: a short Bluetooth 0x01 report comes 78 bytes long, all zeros after it.
        var padded = new byte[78]; padded[0] = 0x01;
        check("DualSense: a short Bluetooth report, padded, carries none", PlayStation.Battery(0x0CE6, padded, bluetooth: true) is null
            && PlayStation.Battery(0x0CE6, new byte[10] { 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, bluetooth: true) is null);
        var ds4 = new byte[64]; ds4[0] = 0x01; ds4[30] = 0x08;
        var ds4Cable = new byte[78]; ds4Cable[0] = 0x11; ds4Cable[32] = 0x1B;
        var ds4Charging = (byte[])ds4Cable.Clone(); ds4Charging[32] = 0x15;
        var ds4Short = new byte[547]; ds4Short[0] = 0x01;
        check("DualShock 4: on battery, charging and full on its cable; a padded short Bluetooth report carries none", PlayStation.Battery(0x09CC, ds4, bluetooth: false) == new BatteryRead(85)
            && PlayStation.Battery(0x09CC, ds4Cable, bluetooth: true) == new BatteryRead(100) && PlayStation.Battery(0x09CC, ds4Charging, bluetooth: true) == new BatteryRead(55, true)
            && PlayStation.Battery(0x09CC, ds4Short, bluetooth: true) is null);
    }

    private static void Usages(Action<string, bool> check)
    {
        check("HID battery usages", HidBattery.IsBattery(0x06, 0x20) && HidBattery.IsBattery(0x0D, 0x3B) && HidBattery.IsBattery(0x85, 0x66) && !HidBattery.IsBattery(0x01, 0x02));
        check("HID battery: scaled by the logical range", HidBattery.Percent(50, 0, 100) == 50 && HidBattery.Percent(255, 0, 255) == 100 && HidBattery.Percent(3, 0, 4) == 75
            && HidBattery.Percent(300, 0, 255) is null);
    }

    private static void Apple(Action<string, bool> check)
    {
        // AirPods Pro: byte 6 0x86 with byte 5's 0x20 set (not flipped): left the low nibble 6 (60 %), right the high 8 (80 %);
        // case 3 (30 %) charging, as OpenPods and AirPodsDesktop read it.
        byte[] advert = [0x07, 0x19, 0x01, 0x0E, 0x20, 0x2B, 0x86, 0x43, 0, 0];
        var parts = AppleAdvert.Parts(advert)!;
        check("AirPods: the model and each part's level", AppleAdvert.Model(advert) == "AirPods Pro" && parts.Count == 3 && parts[0] == new PartBattery(BatteryPart.Left, 60, false)
            && parts[1] == new PartBattery(BatteryPart.Right, 80, false) && parts[2] == new PartBattery(BatteryPart.Case, 30, true) && AppleAdvert.Single(advert) is null);
        // Flipped (0x20 clear): the left is the high nibble, and its charging bit is 0x02; the right charges here.
        byte[] flipped = [0x07, 0x19, 0x01, 0x0E, 0x20, 0x0B, 0x86, 0x1F, 0, 0];
        var swapped = AppleAdvert.Parts(flipped)!;
        check("AirPods: flipped buds swap with their charging, a missing case is left out", swapped.Count == 2 && swapped[0] == new PartBattery(BatteryPart.Left, 80, false)
            && swapped[1] == new PartBattery(BatteryPart.Right, 60, true));
        // AirPods Max: one level (byte 6's low nibble, 7 = 70 %), charging, no parts.
        byte[] max = [0x07, 0x19, 0x01, 0x0A, 0x20, 0x2B, 0x07, 0x1F, 0, 0];
        check("AirPods Max and the one-piece Beats: one level, no left and right", AppleAdvert.Model(max) == "AirPods Max" && AppleAdvert.Parts(max) is { Count: 0 }
            && AppleAdvert.Single(max) == new BatteryRead(70, true, true) && AppleAdvert.Single([0x07, 0x19, 0x01, 0x10, 0x20, 0x2B, 0x0F, 0x0F]) is null);
        check("AirPods: another advertisement is none", AppleAdvert.Parts([0x10, 0x05, 0, 0, 0, 0, 0, 0]) is null && AppleAdvert.Model([0x07, 0x19, 0x01, 0x99, 0x99]) is null);
    }
}
