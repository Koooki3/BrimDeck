namespace BrimDeck.Core;

// Keyboards on Shinetek's tri-mode platform (vendor 0x1A2C, "device_type 301" in the vendor's DeviceDriver.exe): the LANGTU
// (狼途) T98 RGB on its cable 1A2C:A047, the LANGTU T75 on 1A2C:A071, the AJAZZ AK820 Max Plus on 1A2C:A036, and their shared
// 2.4 GHz receiver 1A2C:8FFF (the T98 RGB's and T75's driver lists it for both, and AJAZZ's for the AK820 Max Plus). 1A2C is
// shared by many makers, so only these pairs. The collection is the configuration one: usage page 0xFF02, usage 2, 64-byte
// input and output reports with no report id (65 with Windows' report id 0); the cable's interface 0, the receiver's as well.
// The status command 0x0E: output report AA 55 CC 33 0E and zeros, answered by AA 55 CC 33 0E <percent> <state>, the state 0
// with no keyboard behind the receiver (the driver then keeps no level), 1 charging, 2 on battery, 3 full on the cable (the
// driver takes 1 and 3 as on the cable). A level over 100 is passed over, as the driver does. The keyboard also sends the
// same frame by itself every 5 seconds. Sources: LANGTU's T98 tri-mode driver 1.0.4.8 (langtucn.com; its 5-second poll and
// first read send exactly this frame), prasoondev/ajazz-linux's PROTOCOL.md (MIT; the AK820 Max Plus on 1A2C:A036, measured
// on the cable: 64 03, 100 % full) and shrey715/openajazz (MIT). Every other command of the protocol writes (macros, key
// assignments, lighting, a factory reset at 0x08, sleep time, response time, the Fn switch) and none is ever sent.
[BrandFamilies]
public static class Shinetek
{
    public const int Vendor = 0x1A2C, Receiver = 0x8FFF;

    private static readonly (int Product, string? Name)[] Products =
    [
        (0xA047, "LANGTU T98 RGB"), (0xA071, "LANGTU T75"), (0xA036, "AJAZZ AK820 Max Plus"), (Receiver, null),
    ];

    public static byte[] Request() => BrandFamilies.Frame(65, 0x00, 0xAA, 0x55, 0xCC, 0x33, 0x0E);

    // The answer without its report id 0 (StripZero). On the receiver a state of 0 is the keyboard off or out of range.
    public static BatteryRead? Battery(byte[] r, bool receiver)
    {
        if (r.Length < 7 || r[0] != 0xAA || r[1] != 0x55 || r[2] != 0xCC || r[3] != 0x33 || r[4] != 0x0E) return null;
        if (r[6] == 0) return receiver ? new(null, Asleep: true) : null;
        return r[6] <= 3 && r[5] is > 0 and <= 100 ? new(r[5], r[6] is 1 or 3) : null;
    }

    public static bool Takes(HidShape s) => s.Vendor == Vendor && Products.Any(p => p.Product == s.Product)
        && MouseShapes.Collection(s, 0xFF02, 0x0002) && s.InputLength == 65 && s.OutputLength == 65;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Shinetek keyboard", Takes,
            s => [new BrandWay("shinetek.status", WayTransport.Output, Request(), r => Battery(r, s.Product == Receiver)) { TimeoutMs = 800, StripZero = true }])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "shinetek", DisplayName = s => Products.FirstOrDefault(p => p.Product == s.Product).Name },
    ];
}
