namespace BrimDeck.Core;

// Corsair's older Void headsets (the Void Elite Wireless, 1B1C:0A55): output report C9 64 on the receiver's vendor collection
// (usage page 0xFFC5, usage 1), answered by input report 64 00 <level> B1 <status>: bit 7 of the level is the microphone
// lifted, the status 0 no headset on the receiver (off), 1 and 2 on battery (normal, low), 4 and 5 charging; any other status
// is no reading. From HaloBattery's corsair.py (MIT) and HeadsetControl's corsair_void_rich.hpp (GPL; facts only). 1B1C is
// Corsair's own id but carries other protocols (Corsair, the bragi exchange), so only this product.
[BrandFamilies]
public static class CorsairVoid
{
    public const int Vendor = 0x1B1C, Product = 0x0A55;

    public static byte[] Request() => [0xC9, 0x64];
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 5 || r[0] != 0x64 || r[3] != 0xB1) return null;
        if (r[4] == 0) return new(null, Asleep: true);
        if (r[4] is not (1 or 2 or 4 or 5)) return null;
        int level = r[2] & 0x7F;
        return level <= 100 ? new(level, r[4] is 4 or 5) : null;
    }

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Corsair Void", shape => shape.Vendor == Vendor && shape.Product == Product && UsagePages.Is(shape.UsagePage, 0xFFC5) && shape.Usage == 0x0001,
            _ => [new BrandWay("corsair.void", WayTransport.Output, Request(), Battery) { TimeoutMs = 500 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "corsair" },
    ];
}
