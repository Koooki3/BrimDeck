using BrimDeck.Core;

// ZhuAudio's Z HUB dongle and GravaStar's keyboards and mice (2026-11-17, from the brands' web drivers; nothing measured): what
// each takes, the requests byte for byte, and the answers laid out as the drivers parse them.
static class BrandsZhuaudioGravaStarTests
{
    private static HidShape Shape(int vendor, int product, int page, int usage = 1, int input = 64, int output = 64, byte[]? outputIds = null)
        => BrandFamilyTests.Shape(vendor, product, page, usage, -1, input, output, 0, outputIds, null);
    private static byte[] Hex(string hex, int length = 0)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        return bytes.Length >= length ? bytes : BrandFamilies.Frame(length, bytes);
    }
    private static string? Brand(HidShape shape) => BrandFamilies.Find(shape)?.Family.Brand;

    public static void Run(Action<string, bool> check)
    {
        Zhuaudio(check);
        GravaStarKeyboard(check);
        GravaStarMice(check);
        GravaStar8K(check);
        GravaStarGcomeAndKc(check);
        Bytech(check);
    }

    private static void Bytech(Action<string, bool> check)
    {
        HidShape Wired(int vendor, int product, byte report) => BrandFamilyTests.Shape(vendor, product, 0xFF02, 0x0001, -1, 64, 0, 520, null, [report]);
        (int V, int P, byte R)[] vgn = [(0x258A, 0x024E, 9), (0x258A, 0x027E, 9), (0x258A, 0x026A, 9), (0x258A, 0x010C, 6), (0x391D, 0x2003, 9), (0x391D, 0x2004, 9), (0x391D, 0x2005, 9), (0x391D, 0x2006, 9)];
        check("Bytech 916 cables, VGN HUB's: each on its table's feature report (010C's 6, the rest 9) with 520 bytes on a vendor page, the request on that report; another report, a short one or a standard page is not taken",
            vgn.All(p => BrandFamilies.Find(Wired(p.V, p.P, p.R)) is { Family.Brand: "VGN keyboard" } f && f.Ways.Count == 1 && f.Ways[0].Request.SequenceEqual(Bytech916.Request(p.R))
                && f.Ways[0].Transport == WayTransport.Feature && Dongles.Find(p.V, p.P, 0xFF02, "", 520, -1, 1, 64, 0) is null
                && BrandFamilies.Find(Wired(p.V, p.P, p.R == 9 ? (byte)6 : (byte)9)) is null)
            && BrandFamilies.Find(Wired(0x258A, 0x010C, 6)) is { } vxe && vxe.Family.DisplayName(Wired(0x258A, 0x010C, 6)) == "VXE V87pro"
            && BrandFamilies.Find(BrandFamilyTests.Shape(0x258A, 0x026A, 0xFF02, 1, -1, 64, 0, 519, null, [9])) is null
            && BrandFamilies.Find(BrandFamilyTests.Shape(0x258A, 0x026A, 0x0001, 6, -1, 64, 0, 520, null, [9])) is null);
        int[] gravaStar = [0x019D, 0x0293, 0x0294, 0x0295, 0x0296, 0x0297, 0x02A5, 0x02DD, 0x02E9, 0x01B9];
        check("Bytech 916 cables, 1khub's: the collection's first feature report, any id; AULA's SC380 Pro (258A:0138, a 520-byte report 9 of its own) is not taken",
            gravaStar.All(p => BrandFamilies.Find(Wired(0x258A, p, 5)) is { Family.Brand: "Bytech 916 keyboard" } f && f.Ways[0].Request.SequenceEqual(Bytech916.Request(5)))
            && new[] { 0xA000, 0xA001, 0xA004, 0xA005, 0xA008 }.All(p => BrandFamilies.Find(Wired(0x379A, p, 9)) is { Family.Brand: "Bytech 916 keyboard" })
            && BrandFamilies.Find(BrandFamilyTests.Shape(0x258A, 0x0138, 0xFF02, 0x0002, -1, 64, 64, 520, null, [9])) is not { Family.Brand: "Bytech 916 keyboard" or "VGN keyboard" }
            && BrandFamilies.Find(BrandFamilyTests.Shape(0x258A, 0x019D, 0xFF02, 1, -1, 64, 0, 520, null, [5, 6])) is null
            && BrandFamilies.Find(BrandFamilyTests.Shape(0x258A, 0x019D, 0xFF02, 1, -1, 0, 0, 520, null, [5])) is null
            && BrandFamilies.Find(BrandFamilyTests.Shape(0x258A, 0x024E, 0xFF02, 1, -1, 0, 0, 520, null, [9])) is { Family.Brand: "VGN keyboard" });
        check("Bytech 916: a report 6 answer counts on report 6 only; the receivers' answer is taken without its sum, the request read back is none",
            Bytech916.Battery(6, Hex("06 87 00 00 01 00 02 00 50 01")) == new BatteryRead(80) && Bytech916.Battery(9, Hex("06 87 00 00 01 00 02 00 50 01")) is null
            && Bytech916.ReceiverBattery(Hex("13 4A 01 00 02 50 10", 20)) == new BatteryRead(80, true) && Bytech916.ReceiverBattery(Hex("13 4A 01 00 02 64 01", 20)) == new BatteryRead(100)
            && Bytech916.ReceiverBattery(VgnV108.Request()) is null && Bytech916.ReceiverBattery(Hex("13 4A 01 00 02 00 10", 20)) is null);
        var receiver = Shape(0x379A, 0xA002, 0xFF02, 0x0002, 20, 20, [0x13]);
        check("Bytech 916 receivers, 1khub's: 258A:0150 and 379A:A002, A006, A009 take VGN's 13 frame on a vendor collection declaring 0x13 with 20 bytes; 3554:FA09 stays AULA's and 258A:0233 RK's",
            new[] { (0x258A, 0x0150), (0x379A, 0xA002), (0x379A, 0xA006), (0x379A, 0xA009) }.All(p => BrandFamilies.Find(receiver with { Vendor = p.Item1, Product = p.Item2 }) is { Family.Brand: "Bytech 916 receiver" } f
                && f.Ways[0].Request.SequenceEqual(VgnV108.Request()))
            && BrandFamilies.Find(receiver with { OutputIds = new HashSet<byte> { 0x05 } }) is null && BrandFamilies.Find(receiver with { UsagePage = 0x000C }) is null
            && BrandFamilies.Find(receiver with { Vendor = 0x258A, Product = 0x0233 }) is not { Family.Brand: "Bytech 916 receiver" }
            && Dongles.Find(0x3554, 0xFA09, 0xFF02, "", 0, -1, 2, 20, 20) == Dongles.Protocol.Aula);
        check("Bytech keyboards: VGN's bykeyboard cables (391D:200B, 200C, 201C, 2015, 2018) take the Bytech 0x87 read on VIA's raw collection",
            new[] { 0x200B, 0x200C, 0x201C, 0x2015, 0x2018 }.All(p => BrandFamilies.Find(Shape(0x391D, p, 0xFF60, 0x61, 64, 64, [0x09])) is { Family.Brand: "Bytech keyboard" } f
                && f.Ways[0].Request.SequenceEqual(AtkOfficial.BytechRequest(9, 9))));
    }

    private static void Zhuaudio(Action<string, bool> check)
    {
        var dongle = Shape(0x3654, 0x5055, 0xFF00, 1, 64, 64, [0xCD]);
        check("ZhuAudio: the Z HUB dongle's vendor collection with report 0xCD is taken, not by the older readers; other reports, standard pages and DARKRIM's 6901 are not",
            Brand(dongle) == "ZhuAudio" && BrandFamilies.Find(dongle)!.Value.Ways.Select(way => way.Name).SequenceEqual(["zhuaudio.battery"])
            && Dongles.Find(0x3654, 0x5055, 0xFF00, "", 0, -1, 1, 64, 64, -1, new HashSet<byte> { 0xCD }) is null
            && Brand(Shape(0x3654, 0x5055, 0xFF00, 1, 64, 64, [0x01])) is null && Brand(Shape(0x3654, 0x5055, 0x000C, 1, 64, 64, [0xCD])) is null
            && Brand(Shape(0x3654, 0x5055, 0xFF00, 1, 64, 32, [0xCD])) is null && Brand(Shape(0x3654, 0x6901, 0xFF00, 1, 64, 64, [0xCD])) != "ZhuAudio");
        check("ZhuAudio: CRC-16/ARC (check value 0xBB3D) and the probe and battery requests as the driver frames them",
            BrimDeck.Core.Zhuaudio.Crc("123456789"u8) == 0xBB3D
            && BrimDeck.Core.Zhuaudio.DongleRequest(0x02).SequenceEqual(Hex("CD 07 AA 5A CC 80 51 01 02", 64))
            && BrimDeck.Core.Zhuaudio.PropertyRequest(0x07).SequenceEqual(Hex("CD EE 80 00 06 00 FF 01 02 FE 07 AA", 64)));
        // The link answer: AA CC _ 02 <CRC of 02 01, A0C0> 02 01, after the report id and the byte the driver drops.
        check("ZhuAudio: the link answer, linked (1) and not (0), right after the report id or one byte later; a bad CRC, another command and the request read back are none",
            BrimDeck.Core.Zhuaudio.Linked(Hex("05 08 AA CC 00 02 C0 A0 02 01", 64)) == true
            && BrimDeck.Core.Zhuaudio.Linked(Hex("05 AA CC 00 02 01 60 02 00", 64)) == false
            && BrimDeck.Core.Zhuaudio.Linked(Hex("05 08 AA CC 00 02 C0 A1 02 01", 64)) is null
            && BrimDeck.Core.Zhuaudio.Linked(Hex("05 08 AA CC 00 02 C0 A0 03 01", 64)) is null
            && BrimDeck.Core.Zhuaudio.Linked(BrimDeck.Core.Zhuaudio.DongleRequest(0x02)) is null);
        // The battery answer: an acknowledgement frame FF 01 01 FC AA, then FF 01 04 07 <level> <x> <y> AA, in one packet.
        check("ZhuAudio: property 7's answer (low 7 bits the level, bit 7 charging) right after the report id or a byte later, after an acknowledgement or another property, and the headset's own notice (command 7); fewer than 3 data bytes, 0, over 100, a frame without its AA, another property alone and the request read back are none",
            BrimDeck.Core.Zhuaudio.Battery(Hex("05 EE 80 00 0E 00 FF 01 01 FC AA FF 01 05 FE 07 4B 00 00 AA", 64)) == new BatteryRead(75)
            && BrimDeck.Core.Zhuaudio.Battery(Hex("05 0B EE 80 00 09 00 FF 01 05 FE 07 CB 00 00 AA", 64)) == new BatteryRead(75, true)
            && BrimDeck.Core.Zhuaudio.Battery(Hex("05 EE 80 00 12 00 FF 01 05 FE 05 11 22 33 AA FF 01 05 FE 07 4B 00 00 AA", 64)) == new BatteryRead(75)
            && BrimDeck.Core.Zhuaudio.Battery(Hex("05 EE 80 00 08 00 FF 01 04 07 4B 00 00 AA", 64)) == new BatteryRead(75)
            && BrimDeck.Core.Zhuaudio.Battery(Hex("05 EE 80 00 09 00 FF 01 05 FE 05 4B 00 00 AA", 64)) is null
            && BrimDeck.Core.Zhuaudio.Battery(Hex("05 EE 80 00 08 00 FF 01 04 FE 07 4B 00 AA", 64)) is null
            && BrimDeck.Core.Zhuaudio.Battery(Hex("05 EE 80 00 09 00 FF 01 05 FE 07 80 00 00 AA", 64)) is null
            && BrimDeck.Core.Zhuaudio.Battery(Hex("05 EE 80 00 09 00 FF 01 05 FE 07 65 00 00 AA", 64)) is null
            && BrimDeck.Core.Zhuaudio.Battery(Hex("05 EE 80 00 09 00 FF 01 05 FE 07 4B 00 00 00", 64)) is null
            && BrimDeck.Core.Zhuaudio.Battery(BrimDeck.Core.Zhuaudio.PropertyRequest(0x07)) is null);
        (int Vendor, int Product)[] cmedia = [(0x0D8C, 0x0334), (0x20F8, 0xA962), (0x20F8, 0xA963), (0x20F8, 0xA964), (0x046A, 0x01ED), (0x0E6A, 0x0110), (0x4C4A, 0x4255)];
        check("ZhuAudio C-Media: the seven pairs of the ZG3 driver's INF take 55 65 01 on a vendor collection declaring report 0x55 with 64 bytes, and no older reader does; other reports, pages, lengths and pairs are not taken",
            cmedia.All(p => Brand(Shape(p.Vendor, p.Product, 0xFF90, 1, 64, 64, [0x55])) == "ZhuAudio C-Media"
                && Dongles.Find(p.Vendor, p.Product, 0xFF90, "", 0, -1, 1, 64, 64, -1, new HashSet<byte> { 0x55 }) is null && Headsets.Find(p.Vendor, p.Product) is null
                && Brand(Shape(p.Vendor, p.Product, 0xFF90, 1, 64, 64, [0x01])) is null && Brand(Shape(p.Vendor, p.Product, 0x000C, 1, 64, 64, [0x55])) is null
                && Brand(Shape(p.Vendor, p.Product, 0xFF90, 1, 64, 32, [0x55])) is null)
            && Brand(Shape(0x0D8C, 0x0352, 0xFF90, 1, 64, 64, [0x55])) is null
            && BrandFamilies.Find(Shape(0x0D8C, 0x0334, 0xFF90, 1, 64, 64, [0x55]))!.Value.Ways[0].Request.SequenceEqual(Hex("55 65 01", 64)));
        check("ZhuAudio C-Media: 55 65 <level> <state> as XearONE reads it: 2 on battery, 3 charging, 4 full, 1A not linked (asleep); 0x20 (no battery), the request read back, 0 and over 100 are none",
            BrimDeck.Core.Zhuaudio.CmediaBattery(Hex("55 65 50 02", 64)) == new BatteryRead(80)
            && BrimDeck.Core.Zhuaudio.CmediaBattery(Hex("55 65 50 03", 64)) == new BatteryRead(80, true)
            && BrimDeck.Core.Zhuaudio.CmediaBattery(Hex("55 65 64 04", 64)) == new BatteryRead(100)
            && BrimDeck.Core.Zhuaudio.CmediaBattery(Hex("55 65 00 1A", 64)) == new BatteryRead(null, Asleep: true)
            && BrimDeck.Core.Zhuaudio.CmediaBattery(Hex("55 65 50 20", 64)) is null
            && BrimDeck.Core.Zhuaudio.CmediaBattery(BrimDeck.Core.Zhuaudio.CmediaRequest()) is null
            && BrimDeck.Core.Zhuaudio.CmediaBattery(Hex("55 65 00 02", 64)) is null && BrimDeck.Core.Zhuaudio.CmediaBattery(Hex("55 65 65 02", 64)) is null
            && BrimDeck.Core.Zhuaudio.CmediaBattery(Hex("55 6F 50 02", 64)) is null);
        var way = BrandFamilies.Find(dongle)!.Value.Ways[0];
        check("ZhuAudio: the read asks the link first (asleep when not linked) and is a headset",
            way.Gate is { } gate && gate.SequenceEqual(BrimDeck.Core.Zhuaudio.DongleRequest(0x02)) && way.Request.SequenceEqual(BrimDeck.Core.Zhuaudio.PropertyRequest(0x07)) && way.Gated is not null
            && BrandFamilies.Find(dongle)!.Value.Family.Kind(dongle) == DeviceKind.Headphones);
    }

    private static void GravaStarKeyboard(Action<string, bool> check)
    {
        var cable = Shape(0x372E, 0x10E5, 0xFF60, 0x61, 64, 64, [0x01]);
        check("GravaStar: the K98 Pro's cables (10E5, 10FE) on VIA's raw collection with a nonzero output report take the Bytech read with that id; report 0 alone, other pages and EWEADN's 1026 do not",
            Brand(cable) == "GravaStar keyboard" && Brand(Shape(0x372E, 0x10FE, 0xFF60, 0x61, 64, 64, [0x02])) == "GravaStar keyboard"
            && BrandFamilies.Find(cable)!.Value.Ways[0].Request.SequenceEqual(AtkOfficial.BytechRequest(0x01, 0x01))
            && BrandFamilies.Find(Shape(0x372E, 0x10E5, 0xFF60, 0x61, 64, 64, [0x00])) is { Ways.Count: 0 }
            && Brand(Shape(0x372E, 0x10E5, 0xFF00, 0x01, 64, 64, [0x01])) is null && Brand(Shape(0x372E, 0x1026, 0xFF60, 0x61, 64, 64, [0x01])) != "GravaStar keyboard");
        // GS HUB: 255 − (report id + sum) mod 256 at 62 of the packet; 0x76 for report 1.
        check("GravaStar: the K98 Pro's check byte for report 1 is 0x76, and the answer gives the level at 7 and charging in the high nibble of 8",
            AtkOfficial.BytechRequest(0x01, 0x01)[63] == 0x76
            && AtkOfficial.BytechBattery(Hex("01 87 00 00 01 00 02 50 10", 64)) == new BatteryRead(80, true)
            && AtkOfficial.BytechBattery(Hex("01 87 00 00 01 00 02 50 01", 64)) == new BatteryRead(80));
    }

    // A K98 Pro 8K receiver as GS HUB's transport describes it: it acknowledges each frame it gets, and once the last frame
    // of a message is in, answers with reply split the same way. linked: a link notice sent first (null none); ack: whether
    // it acknowledges at all.
    private sealed class Receiver8K(string key, byte reportId, byte[] reply, bool? linked = null, bool ack = true) : IHidExchange
    {
        public readonly List<byte[]> Written = [];
        private readonly Queue<byte[]> _inbox = new();
        private int _sync = 3;
        private bool _noticed;
        public string Key => key;
        public void Write(byte[] report)
        {
            Written.Add(report);
            if (GravaStar.ParseFrame(report) is not { Ack: false } frame) return;
            if (!_noticed && linked is { } l) { _noticed = true; _inbox.Enqueue(BrandFamilies.Frame(20, reportId, 0x0A, 0x01, 0x00, 0x04, 0x02, (byte)(l ? 1 : 0))); }
            if (!ack) return;
            _inbox.Enqueue(GravaStar.AckOf(reportId, report));
            if (frame.Number == frame.Count && linked != false) foreach (var f in GravaStar.Frames(reportId, reply, ref _sync)) _inbox.Enqueue(f);
        }
        public byte[]? Read(int timeoutMs) => _inbox.Count > 0 ? _inbox.Dequeue() : null;
    }

    private static void GravaStar8K(Action<string, bool> check)
    {
        int sync = 0;
        var frames = GravaStar.Frames(0x01, AtkOfficial.BytechRequest(0x01, 0x01), ref sync);
        check("GravaStar 8K: the request split into five 14-byte pieces, the first 66 05 01 8E 01 87 00 00 01 00 00 … with its sum, the sync 1 to 5, each frame 20 bytes",
            frames.Count == 5 && frames.All(f => f.Length == 20) && sync == 5
            && frames[0].AsSpan(0, 11).SequenceEqual(Hex("01 66 05 01 8E 01 87 00 00 01 00 00")[..11])
            && frames[0][19] == GravaStar.Sum(frames[0].AsSpan(1, 18))
            && frames.Select(f => GravaStar.ParseFrame(f)!.Value).Select((f, i) => f.Sync == i + 1 && f.Number == i + 1 && f.Count == 5 && !f.Ack).All(ok => ok)
            && (frames[4][4] & 0x7F) == 0x08 &&GravaStar.ParseFrame(frames[4])!.Value.Piece.Length == 8);
        var ackOf = GravaStar.AckOf(0x01, frames[0]);
        check("GravaStar 8K: an acknowledgement keeps the frame's sync, count and number with length 0 and its sum; a bad sum is no frame",
            GravaStar.ParseFrame(ackOf) is { Ack: true, Sync: 1, Count: 5, Number: 1 } && ackOf[5] == GravaStar.Sum(ackOf.AsSpan(1, 4))
            && GravaStar.ParseFrame([.. frames[0][..19], (byte)(frames[0][19] + 1)]) is null);

        var reply = Hex("01 87 00 00 01 00 02 50 10", 64);
        string key = Guid.NewGuid().ToString();
        var receiver = new Receiver8K(key, 0x01, reply);
        var read = GravaStar.ReceiverBattery(receiver, 0x01);
        var acks = receiver.Written.Where(w => GravaStar.ParseFrame(w) is { Ack: true }).ToList();
        check("GravaStar 8K: the answer comes back joined (80 %, charging), every frame of it acknowledged, and only the request's frames and those acknowledgements sent",
            read == new BatteryRead(80, true) && acks.Count == 5 && receiver.Written.Count == 10);
        var again = new Receiver8K(key, 0x01, reply);
        GravaStar.ReceiverBattery(again, 0x01);
        check("GravaStar 8K: the sync goes on from one read to the next on the same collection, and starts afresh on another",
            GravaStar.ParseFrame(again.Written[0]) is { Sync: 6 }
            && GravaStar.ParseFrame(new Func<byte[]>(() => { var other = new Receiver8K(Guid.NewGuid().ToString(), 0x01, reply); GravaStar.ReceiverBattery(other, 0x01); return other.Written[0]; })()) is { Sync: 1 });
        check("GravaStar 8K: an answer without the report id in front parses too; a keyboard not linked is asleep; a receiver that acknowledges nothing gives no reading after three tries of the first frame",
            GravaStar.ReceiverBattery(new Receiver8K(Guid.NewGuid().ToString(), 0x01, Hex("87 00 00 01 00 02 50 00", 63)), 0x01) == new BatteryRead(80)
            && GravaStar.ReceiverBattery(new Receiver8K(Guid.NewGuid().ToString(), 0x01, reply, linked: false), 0x01) == new BatteryRead(null, Asleep: true)
            && new Receiver8K(Guid.NewGuid().ToString(), 0x01, reply, ack: false) is var silent && GravaStar.ReceiverBattery(silent, 0x01) is null && silent.Written.Count == 3);
        var dongle = Shape(0x372E, 0x106C, 0xFF60, 0x61, 20, 20, [0x01]);
        check("GravaStar 8K: the receiver's 20-byte VIA raw collection with a nonzero report takes the converse read; 64-byte or report 0 collections do not",
            BrandFamilies.Find(dongle) is { Family.Brand: "GravaStar 8K receiver" } d8k && d8k.Ways is [{ Transport: WayTransport.Converse, Converse: not null }]
            && BrandFamilies.Find(Shape(0x372E, 0x106C, 0xFF60, 0x61, 64, 64, [0x01])) is null
            && BrandFamilies.Find(Shape(0x372E, 0x106C, 0xFF60, 0x61, 20, 20, [0x00])) is { Ways.Count: 0 });
    }

    private static void GravaStarGcomeAndKc(Action<string, bool> check)
    {
        var v75 = Shape(0x19F5, 0xFB8E, 0x0001, 0x0000, 65, 65, [0x00]);
        check("GravaStar V75 Ultra: its four ids read the G-COME byte at 0x20 only (not NuPhy's 0x60 first); NuPhy takes none of them, nor the S1 Lite or the update device, and still takes its own",
            new[] { 0xFB8E, 0xFB96, 0xFB97, 0xFB98 }.All(p => BrandFamilies.Find(v75 with { Product = p }) is { Family.Brand: "GravaStar V75 Ultra" } f
                && f.Ways.Count == 1 && f.Ways[0].Request.SequenceEqual(NuPhy.Request(NuPhy.Profile0BatteryOffset)) && !NuPhy.Takes(v75 with { Product = p }))
            && !NuPhy.Takes(v75 with { Product = 0xB016 }) && !NuPhy.Takes(v75 with { Product = 0xAF78 })
            && BrandFamilies.Find(v75 with { Product = 0xB016 }) is null && BrandFamilies.Find(v75 with { Product = 0xAF78 }) is null
            && NuPhy.Takes(v75 with { Product = 0x8F02 }));
        var s1Pro = Shape(0x362D, 0xFF1C, 0xFFC1, 0x01, 64, 64, [0xB3, 0xB5]);
        check("GravaStar KC mice: S1, S1 Pro and R1 ids take Keychron's status read where B3 is declared; the receivers ask B5 03 first; a cable does not; RIDLAG's FF09 stays RIDLAG's; standard collections are not taken",
            BrandFamilies.Find(s1Pro) is { Family.Brand: "GravaStar mouse" } rx && rx.Ways[0].Request.SequenceEqual(KeychronMice.Request())
            && rx.Ways[0].Gate is { } gate && gate.Length == 21 && gate[0] == 0xB5 && gate[1] == 0x03 && rx.Family.DisplayName(s1Pro) == "GravaStar S1 Pro"
            && BrandFamilies.Find(s1Pro with { Product = 0xFF1A }) is { Family.Brand: "GravaStar mouse" } cable && cable.Ways[0].Gate is null
            && BrandFamilies.Find(s1Pro with { Product = 0xFF1D }) is { } r1 && r1.Ways[0].Gate is not null && r1.Family.DisplayName(s1Pro with { Product = 0xFF1D }) == "GravaStar R1"
            && BrandFamilies.Find(s1Pro with { OutputIds = new HashSet<byte> { 0xB3 } }) is { } noB5 && noB5.Ways[0].Gate is null
            && new[] { 0xFF1E, 0xFF08, 0xFF1B, 0xFF1D }.All(p => BrandFamilies.Find(s1Pro with { Product = p }) is { Family.Brand: "GravaStar mouse" })
            && BrandFamilies.Find(s1Pro with { Product = 0xFF09 }) is { Family.Brand: "RIDLAG mouse" }
            && BrandFamilies.Find(s1Pro with { UsagePage = 0x0001, Usage = 0x0002 }) is null
            && BrandFamilies.Find(s1Pro with { OutputIds = new HashSet<byte> { 0x01 } }) is null);
        check("GravaStar KC mice: the link answer B6 03 with a mouse at 7, 12 or 17 is linked, all zero is not, another report is none",
            GravaStar.KcLinked(Hex("B6 03 00 00 00 00 00 01", 21)) == true && GravaStar.KcLinked(Hex("B6 03 00 00 00 00 00 00 00 00 00 00 01", 21)) == true
            && GravaStar.KcLinked(Hex("B6 03", 21)) == false && GravaStar.KcLinked(Hex("B4 06", 21)) is null);
    }

    private static void GravaStarMice(Action<string, bool> check)
    {
        int[] mice = [0xF54B, 0xF575, 0xF548, 0xF577, 0xF576, 0xF549];
        check("GravaStar: the six Mercury mouse ids (Control HUB's cfg.json) take the Compx frame on their 17-byte 0xFF02 / 2 collection only, as mice",
            mice.All(product => Dongles.Find(0x3554, product, 0xFF02, "", 0, 1, 0x0002, 17, 17) == Dongles.Protocol.CompxFramed
                && Dongles.Find(0x3554, product, 0xFF02, "", 0, 1, 0x0002, 20, 20) is null && Dongles.Find(0x3554, product, 0xFF05, "", 0, 1, 0x0002, 17, 17) is null
                && Dongles.CompxKind(0x3554, product) == DeviceKind.Mouse)
            && Dongles.CompxRequest(0x04).SequenceEqual(Hex("08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49")));
    }
}
