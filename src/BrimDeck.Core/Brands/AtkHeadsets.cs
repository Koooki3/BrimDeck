namespace BrimDeck.Core;

// ATK's N9 V2 Ultimate headset on its dongle (373B:1262, "ATK N9V2 ULTIMATE dongle"). 0x373B is shared with ATK's mice and
// keyboards (their BitMouse collection is usage page 0xFF05, which Dongles takes), so only this product, and only its 0xFF90
// collection, usage 1 (64-byte input and output; the 0xFF82 twin refuses the report; the usage as
// wushuai0802/myfinder-device-battery's atk_headset.py, AGPL, facts only, matches it): output report 0x55, 55 65 01 00 (the
// headset as the target), written as an output report; the answer 55 65 <percent> <state>, the state 02 on battery, 03
// charging, 04 full, 1A the headset gone; 55 0A ... is the headset disconnected or asleep. From Voltide's ATKN9V2UltimateProvider.cs
// (MIT; checked on the dongle against ATK HUB: 55 65 64 02 02 00 00 04 ..., 100 %). FIFINE's HT6 answers the same frame.
[BrandFamilies]
public static class AtkHeadsets
{
    public const int Vendor = 0x373B, N9V2Ultimate = 0x1262;

    public static byte[] Request() => BrandFamilies.Frame(4, 0x55, 0x65, 0x01, 0x00);

    // 55 65 <percent> <state>: a state of 0 is the query read back; 1A the link lost (asleep). 55 0A: the headset is off.
    public static BatteryRead? StateBattery(byte[] r)
    {
        if (r.Length < 4 || r[0] != 0x55) return null;
        if (r[1] == 0x0A) return new(null, Asleep: true);
        if (r[1] != 0x65 || r[3] == 0x00) return null;
        if (r[3] == 0x1A) return new(null, Asleep: true);
        if (r[3] == 0x04 && r[2] <= 100) return new(100);
        return r[2] is > 0 and <= 100 ? new(r[2], r[3] == 0x03) : null;
    }

    private static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product == N9V2Ultimate && HeadsetPages.Is(s, 0xFF90) && s.Usage == 0x0001
        && s.InputLength >= 64 && s.OutputLength >= 64;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("ATK Headset", Takes, _ => [new BrandWay("atk-headset.state", WayTransport.Output, Request(), StateBattery) { TimeoutMs = 1000 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "atk-headset", DisplayName = _ => "ATK N9 V2 Ultimate" },
    ];
}
