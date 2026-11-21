namespace BrimDeck.Core;

// IQUNIX on its own vendor id 0x3869, as its web driver (ez.iqunix.com, the same code on ev and ec) reads it. The keyboards
// there (0xFF60 usage 0x61) are never asked: the driver reads no keyboard battery. IQUNIX's Evision-platform keyboards (Magi,
// FOX, MQ80, N65) share VGN's receiver id 320F:5088: a 0xFF1C collection there is sent VGN's frame (Dongles.Vgn) and named by
// its product string; whether IQUNIX's receiver has that collection and answers is not known. Their cable (320F:5055) has an
// 8-byte input report on 0xFF1C, too short for VGN's answer, and is not asked.
// - The ITX Pro headset (60EA on its cable, 771A on its 2.4 GHz dongle), usage page 0xFF90 usage 1: output report 0x55,
//   65 01 (padded to the report's length); the answer 55 65 <percent> <state> ..., at least 7 bytes after the report id. The
//   state 03 is charging, 04 full, 1A unknown (the driver shows no level then); a level of FE is the headset disconnected,
//   FF the dongle's own timeout (no level). The same frame as ATK's and FIFINE's headsets.
// - The Q101 mouse (6CBE on its cable, 7D68 on its receiver), usage page 0xFF10 usage 1: feature report 0 set to 82 and seven
//   zeros; the answer comes as an input report 82 ..., byte 8 the level in its low seven bits and charging in bit 7. The
//   driver sets the level whatever byte 9 (1 for the mouse linked) says, and so does this read, but a level of 0 is no reading.
// The vendor id is IQUNIX's own, so any 0x3869 collection of those two shapes is asked (the driver also knows mouse products
// C793, C501 and C502, outside its device table). From the web driver's code (deobfuscated; not measured by us). Never sent:
// the headset's 55 21/23/68/69/09 settings, the mouse's 3D writes and anything else the driver writes.
[BrandFamilies]
public static class Iqunix
{
    public const int Vendor = 0x3869;
    public static readonly (int Product, string Name)[] Models =
        [(0x60EA, "IQUNIX ITX Pro"), (0x771A, "IQUNIX ITX Pro"), (0x6CBE, "IQUNIX Q101"), (0x7D68, "IQUNIX Q101")];

    public static byte[] HeadsetRequest() => BrandFamilies.Frame(3, 0x55, 0x65, 0x01);
    public static BatteryRead? HeadsetBattery(byte[] r)
    {
        if (r.Length < 8 || r[0] != 0x55 || r[1] != 0x65) return null;
        if (r[2] == 0xFE) return new(null, Asleep: true);
        // The request read back (65 01 and zeros) is not an answer.
        if (r[2] == 0x01 && r.AsSpan(3).IndexOfAnyExcept((byte)0) < 0) return null;
        if (r[3] == 0x1A) return new(null, Asleep: true);
        return r[2] is > 0 and <= 100 ? new(r[2], r[3] == 0x03) : null;
    }

    public static byte[] MouseRequest() => BrandFamilies.Frame(9, 0x00, 0x82);
    // The input report without its zero report id.
    public static BatteryRead? MouseBattery(byte[] r)
    {
        if (r.Length < 9 || r[0] != 0x82) return null;
        int level = Math.Min(100, r[8] & 0x7F);
        return level > 0 ? new(level, (r[8] & 0x80) != 0) : null;
    }

    private static bool Headset(HidShape s) => s.Vendor == Vendor && HeadsetPages.Is(s, 0xFF90) && s.Usage == 0x0001 && s.InputLength >= 8;
    private static bool Mouse(HidShape s) => s.Vendor == Vendor && HeadsetPages.Is(s, 0xFF10) && s.Usage == 0x0001 && s.InputLength >= 10;
    private static string? Name(HidShape s) => Models.FirstOrDefault(m => m.Product == s.Product).Name;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("IQUNIX Headset", Headset, _ => [new BrandWay("iqunix.headset", WayTransport.Output, HeadsetRequest(), HeadsetBattery) { TimeoutMs = 1000 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "iqunix", DisplayName = Name },
        new("IQUNIX Mouse", Mouse, _ => [new BrandWay("iqunix.mouse", WayTransport.FeatureListen, MouseRequest(), MouseBattery) { TimeoutMs = 800, StripZero = true }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "iqunix", DisplayName = Name },
    ];
}
