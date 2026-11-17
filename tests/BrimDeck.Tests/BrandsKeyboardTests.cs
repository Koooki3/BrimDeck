using BrimDeck.Core;
using static BrandFamilyTests;

// The keyboard brands declared as families: which collections they take (and the ones they must not), their requests, the
// answers from their sources, and what is not a reading (the request read back, zeros, other frames).
static class BrandsKeyboardTests
{
    private static string[] Names((BrandFamily Family, IReadOnlyList<BrandWay> Ways)? found) => found is { } f ? [.. f.Ways.Select(way => way.Name)] : [];
    private static byte[] Hex(string text) => [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(each => Convert.ToByte(each, 16))];
    private static byte[] Padded(int length, string head) { var frame = new byte[length]; Hex(head).CopyTo(frame, 0); return frame; }
    // A frame whose last byte is the low byte of the sum of the others.
    private static byte[] Summed(int length, string head)
    {
        var frame = Padded(length, head);
        int sum = 0; for (int i = 0; i < length - 1; i++) sum += frame[i];
        frame[^1] = (byte)sum;
        return frame;
    }

    public static void Run(Action<string, bool> check)
    {
        Alienware(check);
        Urx(check);
        WobRainy(check);
        NuPhy(check);
        Lofree(check);
        Rongyuan(check);
        Sonix(check);
    }

    private static void Alienware(Action<string, bool> check)
    {
        var vendor = Shape(0x413C, 0x211D, 0xFF00, 1, 1, input: 65, output: 0, feature: 65, featureIds: [0x00], name: "Alienware Pro KB Gaming Receiver");
        check("Alienware: the receiver's vendor collection takes the battery read, byte-swapped page too",
            Names(BrandFamilies.Find(vendor)).SequenceEqual(["alienware.battery"])
            && Names(BrandFamilies.Find(vendor with { UsagePage = 0x00FF })).SequenceEqual(["alienware.battery"]));
        check("Alienware: not Dell's other products on 413C, nor another interface or usage of the receiver",
            BrandFamilies.Find(vendor with { Product = 0x2113 }) is null && BrandFamilies.Find(vendor with { Interface = 0 }) is null
            && BrandFamilies.Find(vendor with { Usage = 2 }) is null && BrandFamilies.Find(Shape(0x413C, 0x211D, 0x0001, 6, 1)) is null);
        var request = BrimDeck.Core.Alienware.Request();
        check("Alienware: the request is feature report 0 set to 0E A1 (65 bytes)",
            request.Length == 65 && request[0] == 0x00 && request[1] == 0x0E && request[2] == 0xA1 && request.Skip(3).All(b => b == 0));
        check("Alienware: the captured answers (37 %, 92 %, 19 % charging); state reports, the request read back and over 100 are none",
            BrimDeck.Core.Alienware.Battery(Padded(64, "0E A1 25 00")) == new BatteryRead(37)
            && BrimDeck.Core.Alienware.Battery(Padded(64, "0E A1 5C 00")) == new BatteryRead(92)
            && BrimDeck.Core.Alienware.Battery(Padded(64, "0E A1 13 04")) == new BatteryRead(19, true)
            && BrimDeck.Core.Alienware.Battery(Padded(64, "0E 05 00 AC")) is null && BrimDeck.Core.Alienware.Battery(Padded(64, "0E A1 00 00")) is null
            && BrimDeck.Core.Alienware.Battery(Padded(64, "0E A1 65 00")) is null);
    }

    private static void Urx(Action<string, bool> check)
    {
        var via = Shape(0x36B0, 0x3002, 0xFF60, 0x61, 1, input: 33, output: 33, outputIds: [0x00], name: "URX Core75 Wireless");
        check("URX: the Core75's VIA collection takes the read",
            Names(BrandFamilies.Find(via)).SequenceEqual(["urx.via"]));
        check("URX: the shared receiver id under another name (PMO, a gamepad dongle) and other collections are not asked by URX, and non-keyboard dongles stay untaken",
            !BrimDeck.Core.Urx.Takes(via with { Name = "PMO 2.4G Receiver" })
            && BrandFamilies.Find(via with { Name = "PMO 2.4G Receiver" }) is { Family.Brand: "PMO / WOBKEY" }
            && BrandFamilies.Find(via with { Name = "RDMCTMZT Wireless 2.4G Dongle" }) is null
            && BrandFamilies.Find(via with { Name = "2.4G Wireless Receiver" }) is null
            && BrandFamilies.Find(via with { Interface = 2 }) is null && BrandFamilies.Find(via with { OutputLength = 65 }) is null
            && BrandFamilies.Find(via with { Product = 0x3009 }) is null);
        check("URX: the request is VIA's custom get, channel 0, value 7",
            BrimDeck.Core.Urx.Request() is { Length: 33 } r && r.Take(4).SequenceEqual(new byte[] { 0x00, 0x08, 0x00, 0x07 }) && r.Skip(4).All(b => b == 0));
        check("URX: the captured answer (76 %); the refusal, another value, zero and over 100 are none",
            BrimDeck.Core.Urx.Battery(Padded(32, "08 00 07 4C")) == new BatteryRead(76)
            && BrimDeck.Core.Urx.Battery(Padded(32, "FF 07")) is null && BrimDeck.Core.Urx.Battery(Padded(32, "08 00 06 4C")) is null
            && BrimDeck.Core.Urx.Battery(Padded(32, "08 00 07 00")) is null && BrimDeck.Core.Urx.Battery(Padded(32, "08 00 07 65")) is null);
    }

    private static void Lofree(Action<string, bool> check)
    {
        var control = Shape(0x05AC, 0x024F, 0xFF02, 2, 1, input: 17, output: 17, outputIds: [0x08], name: "2.4G Wireless Receiver");
        var found = BrandFamilies.Find(control);
        check("Lofree: the Flow Lite84 receiver's control collection takes the read, the online query asked first as its gate",
            found is { Family.Brand: "Lofree" } && Names(found).SequenceEqual(["lofree.flow"]) && found.Value.Ways[0].Before.Count == 0
            && found.Value.Ways[0].Gate is { } gate && gate.SequenceEqual(BrimDeck.Core.Lofree.Request(0x03)) && found.Value.Ways[0].Gated is not null);
        // Apple's wired Aluminium Keyboard on the same ids: its keyboard, consumer and vendor collections.
        bool apple = new[] { Shape(0x05AC, 0x024F, 0x0001, 6, 0, name: "Apple Keyboard"), Shape(0x05AC, 0x024F, 0x000C, 1, 1, name: "Apple Keyboard"),
            Shape(0x05AC, 0x024F, 0xFF00, 1, 1, input: 17, output: 17, outputIds: [0x08], name: "Apple Keyboard"),
            control with { Name = "Apple Keyboard" } }.All(shape => BrandFamilies.Find(shape) is null);
        check("Lofree: Apple's own keyboard on 05AC:024F is never asked", apple);
        check("Lofree: not the receiver's other collections nor one whose output report cannot take the frame",
            BrandFamilies.Find(control with { UsagePage = 0xFF04 }) is null && BrandFamilies.Find(control with { OutputLength = 20 }) is null
            && BrandFamilies.Find(control with { Interface = 0 }) is null);
        var battery = BrimDeck.Core.Lofree.Request(0x04);
        check("Lofree: the requests carry the keyboard flag and sum to 0x55",
            battery.Length == 17 && battery[0] == 0x08 && battery[1] == 0x04 && battery[5] == 0x80 && battery[16] == 0xC9
            && BrimDeck.Core.Lofree.Request(0x03)[16] == 0xCA && battery.Sum(b => b) % 256 == 0x55);
        static byte[] Reply(byte command, byte level, byte charging, byte status = 0)
        {
            var frame = Padded(17, $"08 {command:X2} {status:X2} 00 00 00 {level:X2} {charging:X2} 0E CE");
            int sum = 0; for (int i = 0; i < 16; i++) sum += frame[i];
            frame[16] = (byte)(0x55 - sum);
            return frame;
        }
        var bad = Reply(0x04, 57, 1); bad[16] ^= 0xFF;
        check("Lofree: a battery answer (57 %, charging); the online answer, a bad checksum, a status, zero and the request read back are none",
            BrimDeck.Core.Lofree.Battery(Reply(0x04, 57, 1)) == new BatteryRead(57, true) && BrimDeck.Core.Lofree.Battery(Reply(0x04, 80, 0)) == new BatteryRead(80)
            && BrimDeck.Core.Lofree.Battery(Reply(0x03, 1, 0)) is null && BrimDeck.Core.Lofree.Battery(bad) is null
            && BrimDeck.Core.Lofree.Battery(Reply(0x04, 57, 0, status: 1)) is null && BrimDeck.Core.Lofree.Battery(Reply(0x04, 0, 0)) is null
            && BrimDeck.Core.Lofree.Battery(battery) is null);
        var badOnline = Reply(0x03, 1, 0); badOnline[16] ^= 0xFF;
        check("Lofree: the online answer lets the read go on with 1, says asleep with 0; a bad checksum, a status, the battery answer and the request read back are none",
            BrimDeck.Core.Lofree.Online(Reply(0x03, 1, 0)) == true && BrimDeck.Core.Lofree.Online(Reply(0x03, 0, 0)) == false
            && BrimDeck.Core.Lofree.Online(Reply(0x03, 2, 0)) is null && BrimDeck.Core.Lofree.Online(badOnline) is null
            && BrimDeck.Core.Lofree.Online(Reply(0x03, 1, 0, status: 1)) is null && BrimDeck.Core.Lofree.Online(Reply(0x04, 1, 0)) is null
            && BrimDeck.Core.Lofree.Online(BrimDeck.Core.Lofree.Request(0x03)) is null);
    }

    private static void Rongyuan(Action<string, bool> check)
    {
        var config = Shape(0x3151, 0x5026, 0xFFFF, 2, 2, input: 0, output: 0, feature: 65, featureIds: [0x00]);
        var named = new (int Vendor, int Product, string Brand, string Way, string Model)[]
        {
            (0x3151, 0x5026, "MonsGeek", "monsgeek.status", "MonsGeek FUN60 Pro"), (0x3151, 0x5038, "MonsGeek", "monsgeek.status", "MonsGeek M2 V5 HE"),
            (0x3151, 0x4011, "AJAZZ", "ajazz.status", "AJAZZ AK870"), (0x391D, 0x2A05, "VGN", "vgn.status", "VGN Neon75 Air"),
            (0x3151, 0x502B, "CAROTMAS", "carotmas.status", "CAROTMAS Mars68"),
        };
        check("Rongyuan: each listed receiver's configuration collection takes its brand's status read",
            named.All(each => BrandFamilies.Find(config with { Vendor = each.Vendor, Product = each.Product }) is { } found
                && found.Family.Brand == each.Brand && Names(found).SequenceEqual([each.Way])
                && found.Family.DisplayName(config with { Vendor = each.Vendor, Product = each.Product }) == each.Model));
        check("Rongyuan: not LINGBAO's 3151:402D, the cables (502F, 5030), another 3151 product, nor the receiver's other collections",
            BrandFamilies.Find(config with { Product = 0x402D }) is null && BrandFamilies.Find(config with { Product = 0x502F }) is null
            && BrandFamilies.Find(config with { Product = 0x5030 }) is null && BrandFamilies.Find(config with { Product = 0x4012 }) is null
            && BrandFamilies.Find(config with { Usage = 1 }) is null && BrandFamilies.Find(config with { FeatureLength = 64 }) is null);
        var bare = RongyuanStatus.Request(false);
        var summed = RongyuanStatus.Request(true);
        check("Rongyuan: MonsGeek's bare F7, and F7 with Rongyuan's checksum (00 F7 00 00 00 00 00 00 08)",
            bare.Length == 65 && bare[1] == 0xF7 && bare.Where((b, i) => i != 1).All(b => b == 0)
            && summed.Length == 65 && summed.Take(9).SequenceEqual(Hex("00 F7 00 00 00 00 00 00 08")) && summed.Skip(9).All(b => b == 0)
            && BrandFamilies.Find(config) is { } monsgeek && monsgeek.Ways[0].Request.SequenceEqual(bare)
            && BrandFamilies.Find(config with { Product = 0x4011 }) is { } ajazz && ajazz.Ways[0].Request.SequenceEqual(summed));
        check("Rongyuan: the measured statuses (AK870 41 %, Neon75 Air 82 %, Mars68 72 %), a keyboard away is asleep",
            RongyuanStatus.Battery(Padded(65, "00 00 29 00 00 01 01 01 60")) == new BatteryRead(41)
            && RongyuanStatus.Battery(Padded(65, "00 00 52 00 00 01 01 01")) == new BatteryRead(82)
            && RongyuanStatus.Battery(Padded(65, "00 00 48 00 00 01 01 01")) == new BatteryRead(72)
            && RongyuanStatus.Battery(Padded(65, "00 00 3D 00 01 01 01 01")) == new BatteryRead(null, Asleep: true));
        check("Rongyuan: the request read back, zeros, a mouse alone without a level, a zero or too high level are none; a mouse alone away is asleep",
            RongyuanStatus.Battery(bare) is null && RongyuanStatus.Battery(summed) is null && RongyuanStatus.Battery(new byte[65]) is null
            && RongyuanStatus.Battery(Padded(65, "00 00 29 00 00 00 01 02")) is null
            && RongyuanStatus.Battery(Padded(65, "00 00 29 00 00 01 01 02")) == new BatteryRead(null, Asleep: true)
            && RongyuanStatus.Battery(Padded(65, "00 01 00 00 00 01 01 01")) is null
            && RongyuanStatus.Battery(Padded(65, "00 00 65 00 00 01 01 01")) is null);
    }

    private static void Sonix(Action<string, bool> check)
    {
        var raw = Shape(0x05AC, 0x024F, 0xFF60, 0x61, 3, input: 33, output: 33, outputIds: [0x00], name: "2.4G Wireless Receiver");
        check("Sonix: the borrowed 05AC:024F receiver's raw collection on interface 3, or named by the keyboard, takes the query",
            Names(BrandFamilies.Find(raw)).SequenceEqual(["sonix.query", "huafenda.info"])
            && Names(BrandFamilies.Find(raw with { Interface = 2, Name = "F108Pro Dongle" })).SequenceEqual(["sonix.query", "huafenda.info"])
            && BrandFamilies.Find(raw with { Name = "F108Pro Dongle" })?.Family.DisplayName(raw with { Name = "F108Pro Dongle" }) == "AULA F108 Pro"
            && BrandFamilies.Find(raw with { Interface = 2, Name = "GANSS GS3104T-Pro" })?.Family.DisplayName(raw with { Name = "GANSS" }) == "GANSS GS3104T-Pro");
        check("Sonix: not Apple's keyboard, another interface without a name, nor the 65-byte collection beside it",
            BrandFamilies.Find(Shape(0x05AC, 0x024F, 0x0001, 6, 0, name: "Apple Keyboard")) is null
            && BrandFamilies.Find(raw with { Interface = 4 }) is null && BrandFamilies.Find(raw with { InputLength = 65, OutputLength = 65 }) is null
            && BrandFamilies.Find(raw with { Usage = 0x62 }) is null);
        var x87 = raw with { Vendor = 0x0C45, Product = 0xFEFE, Name = "" };
        check("Sonix: EWEADN X87's receiver on interface 3 only, named by its product string (AULA's F87 Pro V2D shares 0C45:FEFE)",
            BrandFamilies.Find(x87) is { Family.Brand: "EWEADN" } found && Names(found).SequenceEqual(["eweadn.query", "huafenda.info"]) && found.Family.DisplayName(x87) is null
            && BrandFamilies.Find(x87 with { Interface = 4, InputLength = 65, OutputLength = 65 }) is null && BrandFamilies.Find(x87 with { Interface = 4 }) is null
            && BrandFamilies.Find(x87 with { Product = 0xFEFD }) is { Family.Brand: "Huafenda keyboard" });
        check("Sonix: the query is 00 20 01 ... 21",
            SonixQuery.Request() is { Length: 33 } q && q[1] == 0x20 && q[2] == 0x01 && q[32] == 0x21 && q.Skip(3).Take(29).All(b => b == 0));
        var bad = Summed(32, "20 01 00 61"); bad[31] ^= 0x01;
        check("Sonix: the captured answers (GANSS 97 %, X87 100 %); zero, a bad checksum, another command and the request read back are none",
            SonixQuery.Battery(Summed(32, "20 01 00 61")) == new BatteryRead(97) && SonixQuery.Battery(Summed(32, "20 01 00 64")) == new BatteryRead(100)
            && Summed(32, "20 01 00 61")[31] == 0x82 && Summed(32, "20 01 00 64")[31] == 0x85
            && SonixQuery.Battery(Summed(32, "20 01 00 00")) is null && SonixQuery.Battery(bad) is null && SonixQuery.Battery(Summed(32, "20 02 00 61")) is null
            && SonixQuery.Battery(SonixQuery.Request()[1..]) is null && SonixQuery.Battery(Summed(32, "20 01 00 65")) is null);
    }

    private static void WobRainy(Action<string, bool> check)
    {
        var receiver = Shape(0x36B0, 0x3071, 0xFF60, 0x61, 1, input: 257, output: 257, outputIds: [0x00], name: "Rainy75 V2 Receiver");
        var cable = receiver with { Product = 0x3075, Name = "Rainy75 V2" };
        var r98Rx = receiver with { Product = 0x3091, Name = "Rainy98 Receiver" };
        var r98Cable = receiver with { Product = 0x3098, Name = "Rainy98" };
        var foundRx = BrandFamilies.Find(receiver);
        var foundCable = BrandFamilies.Find(cable);
        var foundR98Rx = BrandFamilies.Find(r98Rx);
        var foundR98Cab = BrandFamilies.Find(r98Cable);
        check("WOB BE: Rainy75 V2 (3071/3075) and Rainy98 (3091/3098) take wob.be on 257-byte 0xFF60:0x61; bootloaders, RT and wrong sizes are not taken",
            foundRx is { Family.Brand: "WOBKEY" } rx && Names(foundRx).SequenceEqual(["wob.be", "wob.be-push"])
            && rx.Family.Kind(receiver) == DeviceKind.Keyboard && rx.Family.DisplayName(receiver) == "WOBKEY Rainy75 V2"
            && rx.Family.StandAsideFor.Contains("WOBHub.exe")
            && rx.Ways[0] is { StripZero: true, Gate: { } gate, Gated: not null } && gate.Length == 257 && gate[..5].SequenceEqual(Hex("00 BE 65 04 ED"))
            && foundCable is { Family.Brand: "WOBKEY" } cab && Names(foundCable).SequenceEqual(["wob.be"]) && cab.Ways[0].Gate is null
            && foundR98Rx is { Family.Brand: "WOBKEY" } r98 && Names(foundR98Rx).SequenceEqual(["wob.be", "wob.be-push"])
            && r98.Family.DisplayName(r98Rx) == "WOBKEY Rainy98" && r98.Ways[0].Gate is not null
            && foundR98Cab is { Family.Brand: "WOBKEY" } r98c && r98c.Family.DisplayName(r98Cable) == "WOBKEY Rainy98" && r98c.Ways[0].Gate is null
            && BrandFamilies.Find(receiver with { UsagePage = 0x60FF }) is { Family.Brand: "WOBKEY" }
            && new[] { 0x3070, 0x3078, 0x3079, 0x3090, 0x3099, 0x343F, 0x352F, 0x3530, 0x3531 }
                .All(pid => BrandFamilies.Find(receiver with { Product = pid }) is null)
            && BrandFamilies.Find(receiver with { InputLength = 33, OutputLength = 33 }) is null
            && BrandFamilies.Find(Shape(0x36B0, 0x3071, 0x0001, 0x0006, 0, outputIds: [0x00])) is null
            && BrandFamilies.Find(receiver with { OutputIds = new HashSet<byte> { 0x01 } }) is { } noZero && Names(noZero).SequenceEqual(["wob.be-push"])
            && BrandFamilies.Find(cable with { OutputIds = new HashSet<byte> { 0x01 } }) is { Ways.Count: 0 });
        // A user's measurement on a Rainy75 V2 receiver: the query is answered only inside a 2.4 GHz session (BE 93 05 01 ED),
        // which the read opens before and closes (BE 93 05 00 ED) after; the cable has none. The push needs nothing sent.
        check("WOB BE: a receiver's read opens the 2.4 GHz session first and closes it after, 300 ms apart; the cable's does not; the push read only listens",
            foundRx!.Value.Ways[0] is { GapMs: 300 } withSession && withSession.Before.Count == 1 && withSession.Before[0].Length == 257
            && withSession.Before[0][..6].SequenceEqual(Hex("00 BE 93 05 01 ED")) && withSession.After.Count == 1 && withSession.After[0][..6].SequenceEqual(Hex("00 BE 93 05 00 ED"))
            && foundRx.Value.Ways[1] is { Transport: WayTransport.Listen, Gate: null, StripZero: true, TimeoutMs: > 5000 and < 6000, Attempts: 1 }
            && foundCable!.Value.Ways[0].Before.Count == 0 && foundCable.Value.Ways[0].After.Count == 0
            && BrimDeck.Core.WobRainy.Battery(BrimDeck.Core.WobRainy.BeginWireless()) is null && BrimDeck.Core.WobRainy.Battery(Padded(257, "00 BE BB 06 53 00 ED")) == new BatteryRead(83));
        var request = BrimDeck.Core.WobRainy.Request();
        check("WOB BE: BE BB 04 ED request and BE 65 05 online gate replies",
            request.Length == 257 && request[..5].SequenceEqual(Hex("00 BE BB 04 ED")) && request.Skip(5).All(b => b == 0)
            && BrimDeck.Core.WobRainy.Online(Hex("BE 65 05 01 ED")) == true
            && BrimDeck.Core.WobRainy.Online(Padded(257, "00 BE 65 05 00 ED")) == false
            && BrimDeck.Core.WobRainy.Online(BrimDeck.Core.WobRainy.OnlineRequest()) is null
            && BrimDeck.Core.WobRainy.Online(Hex("BE 65 05 02 ED")) is null
            && BrimDeck.Core.WobRainy.Online(Hex("BE 65 05 01 EE")) is null);
        var zeroTailBad = Padded(257, "00 BE BB 00 50 02 ED");
        zeroTailBad[20] = 0x01;
        check("WOB BE: 6-, 10- and 14-byte BE BB replies (discharging, full, charging); zero, over 100, bad trailer, dirty len-0 tail and request echo are none",
            BrimDeck.Core.WobRainy.Battery(Padded(256, "BE BB 06 4E 00 ED")) == new BatteryRead(78)
            && BrimDeck.Core.WobRainy.Battery(Padded(257, "00 BE BB 00 50 02 ED")) == new BatteryRead(80, true)
            && BrimDeck.Core.WobRainy.Battery(Padded(256, "BE BB 0A 64 01 00 00 00 00 ED")) == new BatteryRead(100)
            && BrimDeck.Core.WobRainy.Battery(Padded(256, "BE BB 0E 2D 02 00 00 00 00 00 00 00 00 ED")) == new BatteryRead(45, true)
            && BrimDeck.Core.WobRainy.Battery(zeroTailBad) is null
            && BrimDeck.Core.WobRainy.Battery(Padded(256, "BE BB 06 00 00 ED")) is null
            && BrimDeck.Core.WobRainy.Battery(Padded(256, "BE BB 06 65 00 ED")) is null
            && BrimDeck.Core.WobRainy.Battery(Padded(256, "BE BB 06 50 03 ED")) is null
            && BrimDeck.Core.WobRainy.Battery(Padded(256, "BE BB 06 50 00 EE")) is null
            && BrimDeck.Core.WobRainy.Battery(request) is null);

        var rdrRx33 = Shape(0x36B0, 0x3002, 0xFF60, 0x61, 1, input: 33, output: 33, outputIds: [0x00], name: "PMO Wave75 2.4G Receiver");
        var rdrRx65 = rdrRx33 with { InputLength = 65, OutputLength = 65 };
        var aurora = Shape(0x36B0, 0x3004, 0xFF60, 0x61, 1, input: 65, output: 65, outputIds: [0x00], name: "Aurora80");
        var wave = aurora with { Product = 0x3009, Name = "Wave75" };
        var zen = aurora with { Product = 0x3025, Name = "Zen 65" };
        var foundRdr33 = BrandFamilies.Find(rdrRx33);
        var foundRdr65 = BrandFamilies.Find(rdrRx65);
        var foundZen = BrandFamilies.Find(zen);
        check("PMO / WOBKEY RDR: 3002 non-URX (33B/65B) with online gate and Aurora80/Wave75/Zen65 cables take wob.rdr; URX keeps 3002 URX Core75 Wireless",
            foundRdr33 is { Family.Brand: "PMO / WOBKEY" } r33 && Names(foundRdr33).SequenceEqual(["wob.rdr"])
            && r33.Family.Kind(rdrRx33) == DeviceKind.Keyboard && r33.Family.DisplayName(rdrRx33) is null
            && r33.Family.StandAsideFor.Contains("WOBHub.exe")
            && r33.Ways[0] is { StripZero: true, Gate: { } rdrGate, Gated: not null } && rdrGate.Length == 33 && rdrGate[..6].SequenceEqual(Hex("00 AA D0 00 00 18"))
            && r33.Ways[0].Before.Single().SequenceEqual(Padded(33, "00 AA 10 00 00 18"))
            && r33.Ways[0].Request.SequenceEqual(Padded(33, "00 AA 30 00 00 18"))
            && r33.Ways[0].After.Single().SequenceEqual(Padded(33, "00 AA 11 00 00 18"))
            && foundRdr65 is { Family.Brand: "PMO / WOBKEY" } r65 && r65.Ways[0].Before.Single().SequenceEqual(Padded(65, "00 AA 10 00 00 38"))
            && r65.Ways[0].After.Single().SequenceEqual(Padded(65, "00 AA 11 00 00 38"))
            && BrandFamilies.Find(aurora) is { Family.Brand: "PMO / WOBKEY" } fa && fa.Family.DisplayName(aurora) == "PMO Aurora80" && fa.Ways[0].Gate is null
            && BrandFamilies.Find(wave) is { Family.Brand: "PMO / WOBKEY" } fw && fw.Family.DisplayName(wave) == "PMO Wave75"
            && foundZen is { Family.Brand: "PMO / WOBKEY" } fz && fz.Family.DisplayName(zen) == "WOBKEY Zen 65"
            && BrandFamilies.Find(rdrRx33 with { Name = "URX Core75 Wireless" }) is { Family.Brand: "URX" }
            && BrandFamilies.Find(rdrRx33 with { Name = "RDMCTMZT Wireless 2.4G Dongle" }) is null
            && BrandFamilies.Find(rdrRx33 with { Name = "2.4G Wireless Receiver" }) is null
            && BrandFamilies.Find(zen with { InputLength = 33, OutputLength = 33 }) is null);
        check("PMO / WOBKEY RDR: AA D0 online status == 4 and AA 30 battery status 0x55",
            BrimDeck.Core.WobRainy.RdrOnline(Padded(32, "AA D0 00 00 18 00 00 55 00 00 00 00 00 04")) == true
            && BrimDeck.Core.WobRainy.RdrOnline(Padded(33, "00 AA D0 00 00 18 00 00 55 00 00 00 00 00 01")) == false
            && BrimDeck.Core.WobRainy.RdrOnline(Padded(32, "AA D0 00 00 18 00 00 55 00 00 00 00 00 05")) is null
            && BrimDeck.Core.WobRainy.RdrBattery(Padded(32, "AA 30 00 00 18 00 00 55 4E 00")) == new BatteryRead(78)
            && BrimDeck.Core.WobRainy.RdrBattery(Padded(65, "00 AA 30 00 00 18 00 00 55 50 01")) == new BatteryRead(80, true)
            && BrimDeck.Core.WobRainy.RdrBattery(Padded(32, "AA 30 00 00 18 00 00 0F 50 00")) is null
            && BrimDeck.Core.WobRainy.RdrBattery(Padded(32, "AA 30 00 00 18 00 00 55 00 00")) is null
            && BrimDeck.Core.WobRainy.RdrBattery(Padded(32, "AA 30 00 00 18 00 00 55 65 00")) is null
            && BrimDeck.Core.WobRainy.RdrBattery(BrimDeck.Core.WobRainy.RdrFrame(65, 0x30, 0x18)) is null);
    }

    private static void NuPhy(Action<string, bool> check)
    {
        var u1 = Shape(0x19F5, 0x2620, 0x0001, 0x0000, 1, input: 65, output: 65, outputIds: [0x00], name: "NuPhy Halo75 V2 Dongle");
        var v2 = u1 with { Product = 0x8F05, Name = "NuPhy Dongle" };
        var wh80 = u1 with { Product = 0x8F01, Name = "WH80" };
        var lite = u1 with { Product = 0x102C, Name = "Halo75 Lite" };
        var isoCable = u1 with { Product = 0x1043, Name = "Halo75 V2" };
        var futureDongle = u1 with { Product = 0x8F08, Name = "NuPhy Future Dongle" };
        var futureCable = u1 with { Product = 0x1050, Name = "NuPhy Future Keyboard" };
        check("NuPhy: unified 0x19F5 takes known/unlisted wireless dongles & cables on 0x0001:0x0000 65B; keyboard/consumer collections, wired-only HE and upgraders are not taken",
            BrandFamilies.Find(u1) is { Family.Brand: "NuPhy" } fu1 && Names(fu1).SequenceEqual(["nuphy.func1", "nuphy.func0"]) && fu1.Family.DisplayName(u1) is null
            && BrandFamilies.Find(v2) is { Family.Brand: "NuPhy" } fv2 && fv2.Family.DisplayName(v2) == "NuPhy Halo75 V2"
            && BrandFamilies.Find(wh80)?.Family.DisplayName(wh80) == "NuPhy WH80"
            && BrandFamilies.Find(lite)?.Family.DisplayName(lite) == "NuPhy Halo75 Lite"
            && BrandFamilies.Find(isoCable)?.Family.DisplayName(isoCable) == "NuPhy Halo75 V2"
            && BrandFamilies.Find(futureDongle) is { Family.Brand: "NuPhy" } fd && Names(fd).SequenceEqual(["nuphy.func1", "nuphy.func0"]) && fd.Family.DisplayName(futureDongle) is null
            && BrandFamilies.Find(futureCable) is { Family.Brand: "NuPhy" } fc && Names(fc).SequenceEqual(["nuphy.func1", "nuphy.func0"]) && fc.Family.DisplayName(futureCable) is null
            && BrandFamilies.Find(u1 with { Usage = 0x0006 }) is null
            && BrandFamilies.Find(u1 with { UsagePage = 0x000C, Usage = 0x0001 }) is null
            && BrandFamilies.Find(u1 with { InputLength = 33, OutputLength = 33 }) is null
            && new[] { 0x1021, 0x1025, 0x103D, 0x103E, 0x1041, 0x1045, 0x32F5, 0x6112, 0x1020, 0x072C, 0x6F01, 0x8A04, 0xAF0F, 0xFE00 }
                .All(pid => BrandFamilies.Find(u1 with { Product = pid }) is null)
            && BrandFamilies.Find(futureCable with { Name = "NuPhy Upgrader" }) is null
            && BrandFamilies.Find(futureCable with { Name = "NuPhy Boot" }) is null
            && BrandFamilies.Find(futureCable with { Name = "NuPhy DFU" }) is null);
        var req1 = BrimDeck.Core.NuPhy.Request(BrimDeck.Core.NuPhy.Profile1BatteryOffset);
        var req0 = BrimDeck.Core.NuPhy.Request(BrimDeck.Core.NuPhy.Profile0BatteryOffset);
        check("NuPhy: 55 05 func read requests with checksum and AA 05 / FA FB 07 replies; bad sum, wrong offset, 0, >100 and request echo are none",
            req1.Length == 65 && req1[..9].SequenceEqual(Hex("00 55 05 00 61 01 60 00 00")) && req1.Skip(9).All(b => b == 0)
            && req0.Length == 65 && req0[..9].SequenceEqual(Hex("00 55 05 00 21 01 20 00 00"))
            && BrimDeck.Core.NuPhy.Battery(Padded(65, "00 AA 05 00 96 01 60 00 00 35"), 0x0060) == new BatteryRead(53)
            && BrimDeck.Core.NuPhy.Battery(Padded(64, "AA 05 00 16 01 60 00 00 B5"), 0x0060) == new BatteryRead(53, true)
            && BrimDeck.Core.NuPhy.Battery(Padded(64, "AA 05 00 71 01 20 00 00 50"), 0x0020) == new BatteryRead(80)
            && BrimDeck.Core.NuPhy.Battery(Padded(64, "FA FB 07 00 00 4B"), 0x0060) == new BatteryRead(75)
            && BrimDeck.Core.NuPhy.Battery(Padded(65, "00 FA FB 07 00 01 32"), 0x0060) == new BatteryRead(50, true)
            && BrimDeck.Core.NuPhy.Battery(Padded(64, "FA FB 07 00 04 64"), 0x0060) == new BatteryRead(100)
            && BrimDeck.Core.NuPhy.Battery(Padded(64, "AA 05 00 97 01 60 00 00 35"), 0x0060) is null
            && BrimDeck.Core.NuPhy.Battery(Padded(64, "AA 05 00 71 01 20 00 00 50"), 0x0060) is null
            && BrimDeck.Core.NuPhy.Battery(Padded(64, "AA 05 00 61 01 60 00 00 00"), 0x0060) is null
            && BrimDeck.Core.NuPhy.Battery(Padded(64, "AA 05 00 C6 01 60 00 00 65"), 0x0060) is null
            && BrimDeck.Core.NuPhy.Battery(Padded(64, "FA FB 07 00 00 00"), 0x0060) is null
            && BrimDeck.Core.NuPhy.Battery(Padded(64, "FA FB 07 00 05 50"), 0x0060) is null
            && BrimDeck.Core.NuPhy.Battery(req1, 0x0060) is null);
    }
}
