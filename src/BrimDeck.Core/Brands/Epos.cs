namespace BrimDeck.Core;

// EPOS's gaming headsets (formerly Sennheiser's; vendor 0x1395 is Sennheiser's id, which its other products carry too, so only
// the products EPOS Gaming Suite 1.8's device table lists). Two ways, by product:
// - HID, the products the suite gives its "Capricorn" adapter: the H3PRO Hybrid (dongle 0303, cable 0101), the GSP 670's GSA 70
//   dongles (0089 Sennheiser, 0300 EPOS) and its cable (008A), the GSA 70S dongle (0301), the GTW 270 Hybrid (009E) and the
//   H3 Hybrid (0102). On the vendor collection (the suite takes usage page 0xFFFF or 0xFFCD with usage 1, or any vendor page
//   with usage 1; the H3PRO's is 0xFFFF on interface 3), report 1 both ways, exactly 107 bytes with the id (a write of another
//   length makes the GSA 70 enumerate again): 01 EB FE, then little-endian 16-bit words: version 0001, type 0004, target,
//   payload length, CRC-8 of the payload (polynomial 0x07, initial 0xFF), then the payload's words: group, command, values.
//   The battery is group 0x12 command 0x03, answered with one value, the percent (the H3PRO's in steps of 20). The target is
//   0x0001 for the device on the cable (a dongle, or a headset on its cable) and 0x0005 for the headset behind a dongle; the
//   answer comes back to the target minus one. While the headset is off, the dongle hands the request back from 0x0001. A
//   dongle is asked for its headset, a cable for itself; the GTW 270 and the H3 Hybrid, whose targets no source gives, both,
//   the headset first: on a cable with no headset behind it, "the headset off" may only be the request handed back, so it
//   does not settle the read (BrandWay.ProbeOnAsleep) and the device itself is asked next.
// - The GSP 370's dongle (009A Sennheiser, 00AB EPOS) carries only media keys on HID: the battery is read on its USB serial
//   port (921600 baud, 8N1, DTR and RTS raised as the suite and HeadsetControl open it), in packets framed like Bluetooth HCI
//   commands: 01 9C FC 00 asks the battery; the answer 04 0E <length> 01 9C FC <status> <percent>, status 0 for a reading and
//   0x0C for the headset off or out of range. The dongle also sends events of its own (04 E9 00, 04 E8 00) in between, so the
//   answer is looked for in the bytes as they come. Stands aside while EPOS Gaming Suite runs (VendorSoftware), which opens
//   the same port. The reader is HidBatteries.EposSerial.
// From EPOS Gaming Suite 1.8.0.767 (its device table and frame code, read statically) and HeadsetControl's
// epos_h3pro_hybrid.hpp and sennheiser_gsp370.hpp (GPL; facts only: the targets, the offline answers, the H3PRO's and GSP
// 370's commands measured on the devices). Not measured by us. Never sent: settings (sidetone 0A/0B and the GSP 370's A0-A2,
// noise cancelling 0A/11, auto sleep 01/1F, LEDs 14/01), the event switches (12/0F, 08/F0, 06/1A), anything for firmware,
// reset or serial numbers (01/06, 01/0A, 01/13, 01/1A, 13/03, 19/xx, 11/02), and nothing to the firmware-update products
// (8089, 808A, 8300, 8303), which are not taken.
[BrandFamilies]
public static class Epos
{
    public const int Vendor = 0x1395, ReportLength = 107;
    public const ushort Device = 0x0001, Headset = 0x0005;

    private enum Link { Dongle, Cable, Either }
    private static readonly (int Product, string? Name, Link Link)[] Products =
    [
        (0x0303, "EPOS H3PRO Hybrid", Link.Dongle), (0x0101, "EPOS H3PRO Hybrid", Link.Cable),
        (0x0089, "Sennheiser GSP 670", Link.Dongle), (0x0300, "EPOS GSP 670", Link.Dongle), (0x008A, "EPOS GSP 670", Link.Cable),
        (0x0301, null, Link.Dongle), (0x009E, "EPOS GTW 270 Hybrid", Link.Either), (0x0102, "EPOS H3 Hybrid", Link.Either),
    ];
    // The H3PRO's percent comes in steps of 20 (HeadsetControl).
    private static readonly int[] InSteps = [0x0303, 0x0101];

    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0xFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
        }
        return crc;
    }

    // 01 EB FE 01 00 04 00 <target> 04 00 <crc> 00 12 00 03 00, and zeros to the report's length.
    public static byte[] Request(ushort target)
    {
        byte[] payload = [0x12, 0x00, 0x03, 0x00];
        var frame = BrandFamilies.Frame(ReportLength, 0x01, 0xEB, 0xFE, 0x01, 0x00, 0x04, 0x00, (byte)target, (byte)(target >> 8),
            (byte)payload.Length, 0x00, Crc8(payload), 0x00);
        payload.CopyTo(frame, 13);
        return frame;
    }

    // The answer to Request(target): a whole frame with a good CRC, the battery's group and command, back to the target minus
    // one with one value of at most 100; from 0x0001 to a request for the headset, the headset off.
    public static Func<byte[], BatteryRead?> Battery(ushort target, bool steps = false) => r =>
    {
        if (r.Length < 13 || r[0] != 0x01 || r[1] != 0xEB || r[2] != 0xFE) return null;
        int Word(int at) => r[at] | r[at + 1] << 8;
        int length = Word(9);
        if (length < 4 || length % 2 != 0 || 13 + length > r.Length || Word(11) != Crc8(r.AsSpan(13, length))) return null;
        if (Word(13) != 0x12 || Word(15) != 0x03) return null;
        int from = Word(7);
        if (target == Headset && from == Device) return new(null, Asleep: true);
        if (from != target - 1 || length != 6 || Word(17) > 100) return null;
        return new(Word(17), Accuracy: steps ? BatteryAccuracy.Steps : BatteryAccuracy.Exact);
    };

    private static bool Takes(HidShape s) => s.Vendor == Vendor && Products.Any(p => p.Product == s.Product)
        && (HeadsetPages.Is(s, 0xFFFF) || HeadsetPages.Is(s, 0xFFCD) || HeadsetPages.Vendor(s)) && s.Usage == 0x0001
        && s.OutputLength == ReportLength && s.InputLength >= 19;

    private static IReadOnlyList<BrandWay> Ways(HidShape s)
    {
        bool steps = InSteps.Contains(s.Product);
        BrandWay headset = new("epos.headset", WayTransport.Output, Request(Headset), Battery(Headset, steps)) { TimeoutMs = 1000 },
            device = new("epos.device", WayTransport.Output, Request(Device), Battery(Device, steps)) { TimeoutMs = 1000 };
        return Products.First(p => p.Product == s.Product).Link switch
        {
            Link.Dongle => [headset],
            Link.Cable => [device],
            _ => [headset with { ProbeOnAsleep = true }, device],
        };
    }

    public static IEnumerable<BrandFamily> Families =>
    [
        new("EPOS", Takes, Ways)
        { Kind = _ => DeviceKind.Headphones, Prefix = "epos", DisplayName = s => Products.FirstOrDefault(p => p.Product == s.Product).Name },
    ];

    // The GSP 370 on its serial port.
    public static readonly (int Product, string Name)[] SerialProducts = [(0x009A, "Sennheiser GSP 370"), (0x00AB, "EPOS GSP 370")];
    public const int SerialBaud = 921600, SerialSettleMs = 500;
    public static byte[] SerialRequest() => [0x01, 0x9C, 0xFC, 0x00];

    // The answer in the bytes received so far: whole once it has come in full (then null unless it is a reading or the headset
    // off: a failure status or a value over 100), not whole until then (other commands' answers and events are passed over).
    public static BatteryRead? SerialBattery(ReadOnlySpan<byte> received, out bool whole)
    {
        whole = false;
        for (int i = 0; i + 6 < received.Length; i++)
        {
            if (received[i] != 0x04 || received[i + 1] != 0x0E || received[i + 3] != 0x01 || received[i + 4] != 0x9C || received[i + 5] != 0xFC) continue;
            int length = received[i + 2];
            if (length < 4 || i + 3 + length > received.Length) return null;
            whole = true;
            byte status = received[i + 6];
            if (status == 0x0C) return new(null, Asleep: true);
            return status == 0x00 && length == 5 && received[i + 7] <= 100 ? new(received[i + 7]) : null;
        }
        return null;
    }
}
