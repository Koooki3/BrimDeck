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
        Machenike(check);
    }

    // Machenike's mice on other brands' platforms: only the pairs its drivers list, each on its own collection.
    private static void Machenike(Action<string, bool> check)
    {
        HidShape Telink(int product, int page) => Shape(0x248A, product, page, 1, 1, 21, 21, 21, featureIds: [0x51]);
        check("Machenike: the L7 Mix on 0x8C and the L8 Pro 2K on 0xFF01 take the identity read, the receivers after the bond read, not on the other page",
            new[] { (0xFF4E, 0x008C, false), (0xFF4C, 0x008C, true), (0xFFE1, 0xFF01, false), (0xFFE2, 0xFF01, true) }.All(p => BrandFamilies.Find(Telink(p.Item1, p.Item2)) is { Family.Brand: "Machenike mouse" } f
                && f.Ways.Select(way => way.Name).SequenceEqual(["machenike.identity"]) && f.Ways[0].FeatureLength == 21
                && f.Ways[0].Request.SequenceEqual(DarmosharkMice.IdentityRequest())
                && (p.Item3 ? f.Ways[0].Before.Single().SequenceEqual(DarmosharkMice.BondRequest()) : f.Ways[0].Before.Count == 0))
            && BrandFamilies.Find(Telink(0xFF4C, 0xFF01)) is null && BrandFamilies.Find(Telink(0xFFE2, 0x008C)) is null && BrandFamilies.Find(Telink(0xFF4D, 0x008C)) is null
            && BrandFamilies.Find(Telink(0xFF4C, 0x008C))!.Value.Family.DisplayName(Telink(0xFF4C, 0x008C)) == "Machenike L7 Mix"
            && BrandFamilies.Find(Telink(0xFFE1, 0xFF01))!.Value.Family.DisplayName(Telink(0xFFE1, 0xFF01)) == "Machenike L8 Pro");
        var g7 = Shape(0x320F, 0x22A4, 0xFF1C, 0x92, -1, 64, 64, outputIds: [0x04]);
        var answer = new byte[64]; answer[0] = 0x04; answer[3] = 0x1A; answer[8] = 57; answer[9] = 1;
        check("Machenike G7: VGN's 1A 06 on report 4 of 0xFF1C / 0x92, the level alone; 0 or another frame is none; 226F is not asked",
            BrandFamilies.Find(g7) is { Family.Brand: "Machenike mouse" } g && g.Ways.Select(way => way.Name).SequenceEqual(["machenike.g7"])
            && g.Ways[0].Request.SequenceEqual(Dongles.VgnRequest()) && MachenikeMice.G7Battery(answer) == new BatteryRead(57)
            && MachenikeMice.G7Battery(answer[..3]) is null && MachenikeMice.G7Battery(Dongles.VgnRequest()) is null
            && BrandFamilies.Find(g7 with { Product = 0x226F }) is null && BrandFamilies.Find(g7 with { UsagePage = 0xFF1D }) is null);
        var l7Mix8K = Shape(0x0BDA, 0xFFE6, 0xFFC1, 1, 1, 64, 64, outputIds: [0xB3]);
        check("Machenike L7 Mix 8K: its receiver takes Keychron's B3 06, its cable does not",
            BrandFamilies.Find(l7Mix8K) is { Family.Brand: "Machenike 8K mouse" } m && m.Ways.Select(way => way.Name).SequenceEqual(["machenike.status"])
            && m.Family.DisplayName(l7Mix8K) == "Machenike L7 Mix 8K" && BrandFamilies.Find(l7Mix8K with { Product = 0xFFD4 }) is null);
        var hub = Shape(0x089D, 0x062F, 0xFF01, 0x10, -1, 33, 33, outputIds: [0x00]);
        var info10 = new byte[32]; info10[0] = 0x10; "M603"u8.ToArray().CopyTo(info10, 4); info10[12] = 1; info10[13] = 64; info10[14] = 1;
        var off = (byte[])info10.Clone(); off[14] = 0;
        check("EWEADN's ids: the 0x30 info first, Machenike's 0x10 after; the 0x10 answer read from 4 on, offline asleep, other frames none",
            BrandFamilies.Find(hub) is { } e && e.Ways.Select(way => way.Name).SequenceEqual(["eweadn.info", "eweadn.info10"])
            && EweadnMice.Info10Request().Length == 33 && EweadnMice.Info10Request()[..2].SequenceEqual(new byte[] { 0x00, 0x10 }) && EweadnMice.Info10Request()[2..].All(b => b == 0)
            && EweadnMice.Info10(info10) == new BatteryRead(100 - 36, true) && EweadnMice.Info10(off) == new BatteryRead(null, Asleep: true)
            && EweadnMice.CableInfo10(off) == new BatteryRead(64, true) && EweadnMice.CableInfo10(info10) == new BatteryRead(64, true)
            && EweadnMice.Info10(info10[..14]) is null && EweadnMice.Info(info10) is null && EweadnMice.Info10([0x30, .. info10[1..]]) is null);
        var beiyin = Shape(0x372E, 0x101F, 0xFF00, 1, -1, 0, 0, 64, featureIds: [0x03]);
        check("EWEADN's beiyin read: Machenike's L9 X1 8K receiver 101F and cables 1020 and 104F",
            new[] { 0x101F, 0x1020, 0x104F }.All(p => BrandFamilies.Find(beiyin with { Product = p }) is { } b && b.Ways.Select(way => way.Name).SequenceEqual(["eweadn.beiyin"])));
        var nearlink = Shape(0x369B, 0x0043, 0xFFA0, 2, -1, 64, 64, outputIds: [0xA0]);
        check("NearLink: Machenike's 369B products are asked as other products, shown as mice; another product's kind is left to its name",
            BrandFamilies.Find(nearlink) is { Family.Brand: "NearLink receiver" } n && n.Family.Kind(nearlink) == DeviceKind.Mouse
            && n.Family.Kind(nearlink with { Product = 0x0999 }) is null);
        check("Compx: Machenike's L7 Air 4K and its receivers on the official list",
            Dongles.IsCompxOfficial(0x3554, 0xF54D) && Dongles.IsCompxOfficial(0x3554, 0xF54C) && Dongles.IsCompxOfficial(0x3554, 0xF54F));
    }

    // #349: LANGTU's T98 RGB on Shinetek's platform, LEOBOG's and LANGTU's models behind 3554:FA09, LANGTU's Huafenda cables.
    private static void LangtuLeobog(Action<string, bool> check)
    {
        HidShape Shinetek(int product, int page = 0xFF02, int usage = 2, int length = 65) => Shape(0x1A2C, product, page, usage, 0, length, length, outputIds: [0x00]);
        check("Shinetek: the T98 RGB's and T75's cables, the AK820 Max Plus's and their receiver, on the 65-byte 0xFF02 collection alone, named but the receiver",
            new[] { 0xA047, 0xA071, 0xA036, 0x8FFF }.All(p => BrandFamilies.Find(Shinetek(p)) is { Family.Brand: "Shinetek" } f
                && f.Ways[0].Name == "shinetek.status" && f.Ways[0].Request.Length == 65
                && f.Ways[0].Request[..6].SequenceEqual(new byte[] { 0x00, 0xAA, 0x55, 0xCC, 0x33, 0x0E }) && f.Ways[0].Request[6..].All(b => b == 0))
            && BrandFamilies.Find(Shinetek(0xA047)) is { } t98 && t98.Family.DisplayName(Shinetek(0xA047)) == "LANGTU T98 RGB"
            && t98.Ways.Count == 1 && t98.Family.Kind(Shinetek(0xA047)) == DeviceKind.Keyboard
            && t98.Family.DisplayName(Shinetek(0x8FFF)) is null
            && BrandFamilies.Find(Shinetek(0xAFFF)) is null && BrandFamilies.Find(Shinetek(0xA047, 0xFF01, 1, 4)) is null
            && BrandFamilies.Find(Shinetek(0xA047, length: 33)) is null && BrandFamilies.Find(Shinetek(0xA047, 0x0001, 6)) is null);
        // Machenike's M7 Pro (9015, 9016) and M721 (9018) on the same platform: 08 FD on the cable, BB AA 99 88 09 FD through the
        // shared receiver, which tries the keyboards' read first; a mouse read answering makes the receiver a mouse.
        static bool Zeros(byte[] frame, int from) => frame[from..].All(b => b == 0);
        check("Shinetek: Machenike's mice on their cables take the mouse status read alone, named, as mice; the receiver takes both reads",
            new[] { 0x9015, 0x9016, 0x9018 }.All(p => BrandFamilies.Find(Shinetek(p)) is { Family.Brand: "Shinetek" } f
                && f.Ways.Select(way => way.Name).SequenceEqual(["shinetek.mouse"]) && f.Ways[0].Request.Length == 65
                && f.Ways[0].Request[..3].SequenceEqual(new byte[] { 0x00, 0x08, 0xFD }) && Zeros(f.Ways[0].Request, 3)
                && f.Family.Kind(Shinetek(p)) == DeviceKind.Mouse)
            && BrandFamilies.Find(Shinetek(0x9015)) is { } m7 && m7.Family.DisplayName(Shinetek(0x9015)) == "Machenike M7 Pro"
            && m7.Family.DisplayName(Shinetek(0x9016)) == "Machenike M7 Pro" && m7.Family.DisplayName(Shinetek(0x9018)) == "Machenike M721"
            && BrandFamilies.Find(Shinetek(0x8FFF)) is { } receiver && receiver.Ways.Select(way => way.Name).SequenceEqual(["shinetek.status", "shinetek.mouse"])
            && receiver.Ways[1].Request[..7].SequenceEqual(new byte[] { 0x00, 0xBB, 0xAA, 0x99, 0x88, 0x09, 0xFD }) && Zeros(receiver.Ways[1].Request, 7)
            && receiver.Family.WayKind("shinetek.mouse") == DeviceKind.Mouse && receiver.Family.WayKind("shinetek.status") is null
            && receiver.Ways[0].ProbeOnAsleep && !receiver.Ways[1].ProbeOnAsleep && !BrandFamilies.Find(Shinetek(0xA047))!.Value.Ways[0].ProbeOnAsleep
            && receiver.Family.Kind(Shinetek(0x8FFF)) == DeviceKind.Keyboard
            && BrandFamilies.Find(Shinetek(0x9017)) is null && BrandFamilies.Find(Shinetek(0x9015, length: 33)) is null);
        check("Shinetek: the mice's status answer with any fifth byte (but a keyboard's 0E through the receiver), charging on the cable and through the receiver, the mouse gone from the receiver, and other frames",
            BrimDeck.Core.Shinetek.MouseBattery([0xAA, 0x55, 0xCC, 0x33, 0xFD, 0x50, 0x02, 0x00], true) == new BatteryRead(80)
            && BrimDeck.Core.Shinetek.MouseBattery([0xAA, 0x55, 0xCC, 0x33, 0x0E, 0x64, 0x03], false) == new BatteryRead(100, true)
            && BrimDeck.Core.Shinetek.MouseBattery([0xAA, 0x55, 0xCC, 0x33, 0x0E, 0x64, 0x02], true) is null
            && BrimDeck.Core.Shinetek.MouseBattery([0xAA, 0x55, 0xCC, 0x33, 0x00, 0x21, 0x01], true) == new BatteryRead(33, true)
            && BrimDeck.Core.Shinetek.MouseBattery([0xAA, 0x55, 0xCC, 0x33, 0xFD, 0x00, 0x00], true) == new BatteryRead(null, Asleep: true)
            && BrimDeck.Core.Shinetek.MouseBattery([0xAA, 0x55, 0xCC, 0x33, 0xFD, 0x00, 0x00], false) is null
            && BrimDeck.Core.Shinetek.MouseBattery([0xAA, 0x55, 0xCC, 0x33, 0xFD, 0x65, 0x02], true) is null
            && BrimDeck.Core.Shinetek.MouseBattery([0xAA, 0x55, 0xCC, 0x33, 0xFD, 0x50, 0x04], true) is null
            && BrimDeck.Core.Shinetek.MouseBattery([0xBB, 0xAA, 0x99, 0x88, 0x09, 0xFD, 0x00], true) is null
            && BrimDeck.Core.Shinetek.MouseBattery([0xAA, 0x55, 0xCC, 0x33, 0xFD, 0x50], true) is null);
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
