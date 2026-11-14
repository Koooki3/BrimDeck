namespace BrimDeck.Core;

// Keyboards on Rongyuan's (Royuan's) 2.4 GHz receivers, which answer the battery themselves: MonsGeek, AJAZZ, VGN and
// CAROTMAS. The receivers carry the chip maker's vendor ids (Yichip's 0x3151, and 0x391D), shared with other products, so
// only the listed vendor and product pairs are asked, and only on their configuration collection: usage page 0xFFFF, usage 2,
// a 65-byte feature report with no report id. 3151:402D (LINGBAO's mouse receiver) is read elsewhere and is not listed.
//
// The read is the receivers' status query 0xF7, a feature report set to F7 and zeros, then read back. Rongyuan's checksum
// (0xFF less the sum of the seven bytes before it, 0x08 here) follows where the vendor's driver sends it; MonsGeek's driver
// sends the bare F7. The status, after the zero report id: [0] 1 when a relayed answer waits, [1] the keyboard's percent,
// [2] the mouse's, [3] 0 while the keyboard is online, [4] 0 while the mouse is, [5] 1 when the receiver takes a command,
// [6] what is paired (1 keyboard, 2 mouse, 3 both). A reading needs a keyboard paired, online, and a percent of 1 to 100; a
// keyboard paired but offline is asleep. The request read back (F7 at [0]), the zeros of a receiver just plugged in and the
// cable's echo are none. The receiver keeps its last value while the keyboard is away: [3] tells. No charging state is read:
// VGN V HUB reads one at [9], but on the Neon75 Air it stayed 0 with the cable in, while [1] jumped to 100 (mousedock's
// VGN-NEON75-AIR.md), so a 100 there may be the cable's and not a measured level.
// Facts from: HaloBattery's monsgeek.py (MIT; MonsGeek's driver, 3151:5026 measured at 54 % and 52 %); mousedock (MIT;
// VGN V HUB's RyServe.setStatus for 391D:2A05, measured at 82 %; MonsGeek's V4 driver for 3151:5038, user-tested);
// vigil-watt (MIT; AK870 3151:4011 measured at 41 %); YesCharge (MIT; CAROTMAS Mars68 3151:502B, its receiver status);
// sharkfin's protocol notes (GPL, facts only: the field layout and the checksum).
[BrandFamilies]
public static class RongyuanStatus
{
    public const byte Status = 0xF7;

    // F7 and zeros (MonsGeek), or with Rongyuan's checksum at the eighth byte after the report id.
    public static byte[] Request(bool checksum) => checksum ? BrandFamilies.Frame(65, 0x00, Status, 0, 0, 0, 0, 0, 0, 0xFF - Status) : BrandFamilies.Frame(65, 0x00, Status);

    // The feature report as read back, report id 0 first.
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 9 || r[0] != 0x00 || r[1] > 1 || r[7] is not (1 or 3)) return null;
        if (r[4] == 1) return new(null, Asleep: true);
        return r[4] == 0 && r[2] is >= 1 and <= 100 ? new(r[2]) : null;
    }

    public static bool Collection(HidShape shape) => shape.UsagePage == 0xFFFF && shape.Usage == 0x0002 && shape.FeatureLength == 65;

    private static BrandFamily Family(string brand, string prefix, bool checksum, params (int Vendor, int Product, string Name)[] models)
        => new(brand, shape => Collection(shape) && models.Any(model => model.Vendor == shape.Vendor && model.Product == shape.Product),
            _ => [new BrandWay(prefix + ".status", WayTransport.Feature, Request(checksum), Battery) { GapMs = 100, FeatureLength = 65 }])
        {
            Kind = _ => DeviceKind.Keyboard, Prefix = prefix,
            DisplayName = shape => models.FirstOrDefault(model => model.Vendor == shape.Vendor && model.Product == shape.Product).Name,
        };

    public static IEnumerable<BrandFamily> Families =>
    [
        Family("AJAZZ", "ajazz", true, (0x3151, 0x4011, "AJAZZ AK870")),
        Family("CAROTMAS", "carotmas", true, (0x3151, 0x502B, "CAROTMAS Mars68")),
        Family("MonsGeek", "monsgeek", false, (0x3151, 0x5026, "MonsGeek FUN60 Pro"), (0x3151, 0x5038, "MonsGeek M2 V5 HE")),
        Family("VGN", "vgn", true, (0x391D, 0x2A05, "VGN Neon75 Air")),
    ];
}
