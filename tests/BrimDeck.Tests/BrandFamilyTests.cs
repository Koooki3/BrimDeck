using BrimDeck.Core;

// The brand families (BrandFamilies): every brand's matching and reads, and the rules all of them keep.
static class BrandFamilyTests
{
    public static HidShape Shape(int vendor, int product, int page, int usage = 1, int iface = -1, int input = 64, int output = 64, int feature = 0,
        byte[]? outputIds = null, byte[]? featureIds = null, string name = "")
        => new(vendor, product, page, usage, iface, input, output, feature, (outputIds ?? []).ToHashSet(), (featureIds ?? []).ToHashSet(), name);

    public static void Run(Action<string, bool> check)
    {
        Rules(check);
        Betop(check);
        VgnV108(check);
        VgnV98ProV3(check);
        LangtuLeobog(check);
    }

    // #349: LANGTU's T98 RGB on Shinetek's platform, LEOBOG's and LANGTU's models behind 3554:FA09, LANGTU's Huafenda cables.
    private static void LangtuLeobog(Action<string, bool> check)
    {
        HidShape Shinetek(int product, int page = 0xFF02, int usage = 2, int length = 65) => Shape(0x1A2C, product, page, usage, 0, length, length, outputIds: [0x00]);
        check("Shinetek: the T98 RGB's and T75's cables, the AK820 Max Plus's and their receiver, on the 65-byte 0xFF02 collection alone, named but the receiver",
            new[] { 0xA047, 0xA071, 0xA036, 0x8FFF }.All(p => BrandFamilies.Find(Shinetek(p)) is { Family.Brand: "Shinetek keyboard" } f
                && f.Ways.Select(way => way.Name).SequenceEqual(["shinetek.status"]) && f.Ways[0].Request.Length == 65
                && f.Ways[0].Request[..6].SequenceEqual(new byte[] { 0x00, 0xAA, 0x55, 0xCC, 0x33, 0x0E }) && f.Ways[0].Request[6..].All(b => b == 0))
            && BrandFamilies.Find(Shinetek(0xA047)) is { } t98 && t98.Family.DisplayName(Shinetek(0xA047)) == "LANGTU T98 RGB"
            && t98.Family.DisplayName(Shinetek(0x8FFF)) is null
            && BrandFamilies.Find(Shinetek(0xAFFF)) is null && BrandFamilies.Find(Shinetek(0xA047, 0xFF01, 1, 4)) is null
            && BrandFamilies.Find(Shinetek(0xA047, length: 33)) is null && BrandFamilies.Find(Shinetek(0xA047, 0x0001, 6)) is null);
        check("Shinetek: the status answer (ajazz-linux's measured 64 03, full on the cable), charging and on battery, the keyboard gone from the receiver, and other frames",
            BrimDeck.Core.Shinetek.Battery([0xAA, 0x55, 0xCC, 0x33, 0x0E, 0x64, 0x03, 0x00], false) == new BatteryRead(100, true)
            && BrimDeck.Core.Shinetek.Battery([0xAA, 0x55, 0xCC, 0x33, 0x0E, 0x61, 0x01], true) == new BatteryRead(97, true)
            && BrimDeck.Core.Shinetek.Battery([0xAA, 0x55, 0xCC, 0x33, 0x0E, 0x2A, 0x02], true) == new BatteryRead(42)
            && BrimDeck.Core.Shinetek.Battery([0xAA, 0x55, 0xCC, 0x33, 0x0E, 0x00, 0x00], true) == new BatteryRead(null, Asleep: true)
            && BrimDeck.Core.Shinetek.Battery([0xAA, 0x55, 0xCC, 0x33, 0x0E, 0x00, 0x00], false) is null
            && BrimDeck.Core.Shinetek.Battery([0xAA, 0x55, 0xCC, 0x33, 0x0E, 0x65, 0x02], true) is null
            && BrimDeck.Core.Shinetek.Battery([0xAA, 0x55, 0xCC, 0x33, 0x0E, 0x50, 0x04], true) is null
            && BrimDeck.Core.Shinetek.Battery([0xAA, 0x55, 0xCC, 0x33, 0x0C, 0x50, 0x02], true) is null
            && BrimDeck.Core.Shinetek.Battery([0xAA, 0x55, 0xCC, 0x33, 0x0E, 0x50], true) is null);
        static byte[] Fa09(params byte[] head) { var frame = new byte[20]; head.CopyTo(frame, 0); frame[19] = (byte)frame.Take(19).Sum(b => b); return frame; }
        check("3554:FA09: LEOBOG's keyboards and LANGTU's L18 named by their drivers' codes",
            Dongles.AulaModel(Fa09(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x00, 0xA2)) == "LEOBOG Hi75"
            && Dongles.AulaModel(Fa09(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x01, 0x21)) == "LEOBOG Hi98"
            && Dongles.AulaModel(Fa09(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x01, 0xAD)) == "LANGTU L18"
            && Dongles.AulaModels.Select(model => Convert.ToHexString(model.Code)).Distinct().Count() == Dongles.AulaModels.Length);
        HidShape Sonix(int product, int page = 0xFF68) => Shape(0x0C45, product, page, 0x61, 2, 65, 65, outputIds: [0x00]);
        check("Huafenda: LANGTU's cables (T87 8008, T88 8501, X102Air 880C) on their 0xFF68 collection take the device-info read, not on another page",
            new[] { 0x8008, 0x8501, 0x880C, 0x80BB }.All(p => BrandFamilies.Find(Sonix(p)) is { Family.Brand: "Huafenda keyboard" } f
                && f.Ways.Select(way => way.Name).SequenceEqual(["huafenda.info"]))
            && BrandFamilies.Find(Sonix(0x8008, 0xFF60)) is null && BrandFamilies.Find(Sonix(0x8005)) is null);
    }

    // The V98Pro V3's cable: 520-byte feature report 9, the query echoed, the level at 8 and the charging nibble at 9.
    private static void VgnV98ProV3(Action<string, bool> check)
    {
        var request = Bytech916.Request(0x09);
        check("VGN V98Pro V3 wired: the 520-byte feature query, the measured 100 % charging answer, a battery one, and a zero level, a bad echo and a short answer are none",
            request.Length == 520 && request[..8].SequenceEqual(Bytes("09 87 00 00 01 00 02 00")) && request[8..].All(value => value == 0)
            && Bytech916.Battery(0x09, Bytes("09 87 00 00 01 00 02 00 64 11")) == new BatteryRead(100, true)
            && Bytech916.Battery(0x09, Bytes("09 87 00 00 01 00 02 00 64 01")) == new BatteryRead(100)
            && Bytech916.Battery(0x09, Bytes("09 87 00 00 01 00 02 00 00 11")) is null
            && Bytech916.Battery(0x09, Bytes("09 87 00 00 01 00 02 00 65 11")) is null
            && Bytech916.Battery(0x09, Bytes("09 86 00 00 01 00 02 00 64 11")) is null
            && Bytech916.Battery(0x09, Bytes("09 87 00 00 01 00 02 00 64")) is null
            && Bytech916.Battery(0x09, request) is null);
        check("VGN V98Pro V3 wired: 258A:024E's 520-byte feature 9 collection, its page either way; the 2.4G receivers and other report shapes are not taken",
            BrandFamilies.Find(Shape(0x258A, 0x024E, 0xFF02, 0x0001, 1, feature: 520, featureIds: [0x09])) is { Family.Brand: "VGN keyboard", Ways.Count: 1 }
            && BrandFamilies.Find(Shape(0x258A, 0x024E, 0x02FF, 0x0100, 1, feature: 520, featureIds: [0x09])) is { Family.Brand: "VGN keyboard" }
            && BrandFamilies.Find(Shape(0x258A, 0x024E, 0xFF02, 0x0001, 1, feature: 519, featureIds: [0x09])) is null
            && BrandFamilies.Find(Shape(0x258A, 0x024E, 0xFF02, 0x0001, 1, feature: 520, featureIds: [0x05])) is null
            && BrandFamilies.Find(Shape(0x258A, 0x024E, 0x0001, 0x0001, 1, feature: 520, featureIds: [0x09])) is null
            && BrandFamilies.Find(Shape(0x258A, 0x024F, 0xFF02, 0x0001, 1, feature: 520, featureIds: [0x09])) is not { Family.Brand: "VGN keyboard" }
            && BrandFamilies.Find(Shape(0x258A, 0x024E, 0xFF02, 0x0002, 1, input: 20, output: 20, outputIds: [0x13])) is not { Family.Brand: "VGN keyboard" });
    }

    // Pull request #278's capture: 100 % on the receiver (byte 6 = 01: full, off the cable).
    private static void VgnV108(Action<string, bool> check)
    {
        var reply = Bytes("13 4A 01 00 02 64 01 00 00 00 00 00 00 00 00 00 00 00 00 C5");
        var charging = Bytes("13 4A 01 00 02 32 10 00 00 00 00 00 00 00 00 00 00 00 00 A2");
        check("VGN V108: the request with its sum, the captured 100 % answer, a charging state, a wrong sum and a level over 100 are none",
            BrimDeck.Core.VgnV108.Request().SequenceEqual(Bytes("13 4A 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 5E"))
            && BrimDeck.Core.VgnV108.Battery(reply) == new BatteryRead(100) && BrimDeck.Core.VgnV108.Battery(charging) == new BatteryRead(50, true)
            && BrimDeck.Core.VgnV108.Battery([.. reply[..^1], 0]) is null
            && BrimDeck.Core.VgnV108.Battery(Bytes("13 4A 01 00 02 65 01 00 00 00 00 00 00 00 00 00 00 00 00 C6")) is null
            && BrimDeck.Core.VgnV108.Battery(Bytes("13 4A 01 00 02 00 01 00 00 00 00 00 00 00 00 00 00 00 00 61")) is null
            && BrimDeck.Core.VgnV108.Battery(BrimDeck.Core.VgnV108.Request()) is null);
        check("VGN V108: only interface 1 of 391D:2A02, with 20-byte reports and report 0x13 declared, never a standard collection",
            BrandFamilies.Find(Shape(0x391D, 0x2A02, 0xFF02, 0x0002, 1, 25, 20, outputIds: [0x13])) is { Family.Brand: "VGN V108", Ways.Count: 1 }
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0x0001, 0x0000, 1, 25, 20, outputIds: [0x13])) is { Family.Brand: "VGN V108", Ways.Count: 1 }
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0x0001, 0x0006, 1, 25, 20, outputIds: [0x13])) is null
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0x0001, 0x0080, 1, 25, 20, outputIds: [0x13])) is null
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0x000C, 0x0001, 1, 25, 20, outputIds: [0x13])) is null
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0xFF02, 0x0002, 0, 8, 1, outputIds: [0x00])) is null
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0xFF02, 0x0002, 1, 19, 20, outputIds: [0x13])) is null
            && BrandFamilies.Find(Shape(0x391D, 0x2A02, 0xFF02, 0x0002, 1, 25, 20, outputIds: [0x01])) is null
            && BrandFamilies.Find(Shape(0x391D, 0x2A05, 0xFF00, 0x0001, 1, 25, 20, outputIds: [0x13])) is not { Family.Brand: "VGN V108" });
    }

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    // What every family must keep: unique brands and read names (they are kept on disk), names under the brand's prefix, and
    // never a standard collection (mouse, keyboard, consumer, system control) asked.
    private static void Rules(Action<string, bool> check)
    {
        var all = BrandFamilies.All;
        check("Brand families: found by their attribute", all.Count > 0 && all.Any(family => family.Brand == "BETOP"));
        check("Brand families: each brand once", all.Select(family => family.Brand).Distinct().Count() == all.Count);
        int[] standard = [0x01, 0x0C];
        bool none = true;
        foreach (var family in all)
            foreach (var page in standard)
                for (int vendor = 0; vendor < 0x10000; vendor += 0x101)
                    if (family.Takes(Shape(vendor, 0x0001, page))) none = false;
        check("Brand families: none takes a generic desktop or consumer collection", none);
    }

    private static void Betop(Action<string, bool> check)
    {
        var receiver = Shape(0x20BC, 0x5073, 0x00FF, 3, 1, outputIds: [0x02]);
        var found = BrandFamilies.Find(receiver);
        check("BETOP: any 20BC receiver's vendor collection (the Kunpeng 70's id is not known) takes the status read, its page as Windows reports it or as written",
            found is { Family.Brand: "BETOP" } && found.Value.Ways.Select(way => way.Name).SequenceEqual(["betop.status"])
            && BrandFamilies.Find(Shape(0x20BC, 0x5191, 0xFF00, 3, 1, outputIds: [0x02])) is { Ways.Count: 1 }
            && BrandFamilies.Find(Shape(0x20BD, 0x5191, 0x00FF, 3, outputIds: [0x02])) is null);
        check("BETOP: never the receiver's mouse, keyboard or gamepad collections, nor another usage on its vendor page",
            BrandFamilies.Find(Shape(0x20BC, 0x5191, 0x0001, 2, outputIds: [0x02])) is null && BrandFamilies.Find(Shape(0x20BC, 0x5191, 0x0001, 6, outputIds: [0x02])) is null
            && BrandFamilies.Find(Shape(0x20BC, 0x5191, 0x0001, 5, outputIds: [0x02])) is null && BrandFamilies.Find(Shape(0x20BC, 0x5191, 0x00FF, 1, outputIds: [0x02])) is null);
        check("BETOP: not sent where the collection declares no report 0x02",
            BrandFamilies.Find(Shape(0x20BC, 0x5191, 0x00FF, 3, outputIds: [0x01])) is { Ways.Count: 0 });
        check("BETOP: the request, a reading on battery and one charging (the Kunpeng 20's capture, vigil-watt's 93 %), and the request read back and a level of 0 are none",
            BrimDeck.Core.Betop.Request().SequenceEqual(new byte[] { 0x02, 0x15 })
            && BrimDeck.Core.Betop.Battery([0x02, 0x15, 0x64, 0x00, 0x51, 0x01, 0x01, 0x00]) == new BatteryRead(100, true)
            && BrimDeck.Core.Betop.Battery([0x02, 0x15, 0x5D, 0x00, 0x50]) == new BatteryRead(93)
            && BrimDeck.Core.Betop.Battery([0x02, 0x15, 0x37, 0x00, 0x50]) == new BatteryRead(55)
            && BrimDeck.Core.Betop.Battery([0x02, 0x15, 0x00, 0x00, 0x50]) is null
            && BrimDeck.Core.Betop.Battery([0x02, 0x15, 0x00, 0x00, 0x00]) is null && BrimDeck.Core.Betop.Battery([0x02, 0x15, 0x99, 0x00, 0x50]) is null
            && BrimDeck.Core.Betop.Battery([0x02, 0x12, 0x40, 0x00, 0x50]) is null);

        // #348: the KP20's receiver 20BC:5128 scrambles every byte after the report id (BETOP's client 2.6.2).
        var kp20 = Shape(0x20BC, 0x5128, 0x00FF, 3, outputIds: [0x02]);
        check("BETOP: the scrambling models get only the scrambled status read, 5125/5127 scrambled then plain, the others only plain",
            BrandFamilies.Find(kp20) is { } scrambled && scrambled.Ways.Select(way => way.Name).SequenceEqual(["betop.status.scrambled"])
            && BrandFamilies.Find(Shape(0x20BC, 0x5126, 0x00FF, 3, outputIds: [0x02])) is { } cable && cable.Ways.Select(way => way.Name).SequenceEqual(["betop.status.scrambled"])
            && BrandFamilies.Find(Shape(0x20BC, 0x5127, 0x00FF, 3, outputIds: [0x02])) is { } both && both.Ways.Select(way => way.Name).SequenceEqual(["betop.status.scrambled", "betop.status"])
            && BrandFamilies.Find(Shape(0x20BC, 0x515C, 0x00FF, 3, outputIds: [0x02])) is { } plain && plain.Ways.Select(way => way.Name).SequenceEqual(["betop.status"]));
        check("BETOP: 0x20DD only for the products BETOP's client lists",
            BrandFamilies.Find(Shape(0x20DD, 0x5128, 0x00FF, 3, outputIds: [0x02])) is { Family.Brand: "BETOP", Ways.Count: 1 }
            && BrandFamilies.Find(Shape(0x20DD, 0x5130, 0x00FF, 3, outputIds: [0x02])) is { Family.Brand: "BETOP", Ways.Count: 1 }
            && BrandFamilies.Find(Shape(0x20DD, 0x1234, 0x00FF, 3, outputIds: [0x02])) is null);
        check("BETOP: the scrambling table, its inverse, and the KP20's scrambled status request",
            BrimDeck.Core.Betop.Scramble(0x15) == 0xAD && BrimDeck.Core.Betop.Scramble(0x00) == 0x08 && BrimDeck.Core.Betop.Scramble(0x55) == 0xCD
            && BrimDeck.Core.Betop.Scramble(0x64) == 0x2C && BrimDeck.Core.Betop.Scramble(0x51) == 0xC9
            && Enumerable.Range(0, 256).All(value => BrimDeck.Core.Betop.Unscramble(BrimDeck.Core.Betop.Scramble((byte)value)) == value)
            && BrimDeck.Core.Betop.ScrambledRequest().SequenceEqual(new byte[] { 0x02, 0xAD }));
        check("BETOP: a scrambled answer on battery and charging (state 0/1 or 0x50/0x51), and the request read back, the firmware report, a level of 0 and a plain answer are none",
            BrimDeck.Core.Betop.ScrambledBattery([0x02, 0xAD, 0x2C, 0x08, 0x08]) == new BatteryRead(100)
            && BrimDeck.Core.Betop.ScrambledBattery([0x02, 0xAD, 0x6D, 0x08, 0x09]) == new BatteryRead(37, true)
            && BrimDeck.Core.Betop.ScrambledBattery([0x02, 0xAD, 0x6D, 0x08, 0xC9]) == new BatteryRead(37, true)
            && BrimDeck.Core.Betop.ScrambledBattery([0x02, 0xAD, 0x6D, 0x08, 0xC8]) == new BatteryRead(37)
            && BrimDeck.Core.Betop.ScrambledBattery([0x02, 0xAD, 0x00, 0x00, 0x00]) is null
            && BrimDeck.Core.Betop.ScrambledBattery([0x02, 0xCD, 0x09, 0x0A, 0x08]) is null
            && BrimDeck.Core.Betop.ScrambledBattery([0x02, 0xAD, 0x08, 0x08, 0x08]) is null
            && BrimDeck.Core.Betop.ScrambledBattery([0x02, 0x15, 0x64, 0x00, 0x50]) is null);
    }
}
