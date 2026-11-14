namespace BrimDeck.Core;

// MCHOSE V9 Pro headset on its C-Media receiver or cable (291D:385D). 291D is C-Media's id, which MCHOSE's S9 PRO, G9 PRO and
// V9 share (wzk112/MchoseHub's catalog), so only a collection whose product string names the V9 PRO is asked, and only its
// vendor collection usage page 0xFF90 usage 1 with 64-byte reports (rafagfran/mchose-v9-pro-battery-tray and vigil-watt's
// mchose-v9-pro.md measured it so on Windows, aipno/mchose-linux's HEADSET.md has it in the descriptor with report 0x55).
// Output report 55 65 01, zeros to 64 bytes, answered 55 65 <percent> <state>. The states, from every source:
// - 0x02 on battery (HaloBattery's mchose_v9.py, rafagfran and vigil-watt measured it);
// - 0x00 on its cable, read as charging (HaloBattery measured it at 60 %, the level following M HUB while it charged;
//   wushuai0802/myfinder-device-battery, AGPL, facts only, holds the level frozen then and hides it, which HaloBattery's
//   measurement does not bear out, so the level is shown);
// - 0x03 charging, 0x04 full and 0x1A (26) asleep: M HUB's table for this frame (aipno/mchose-linux's HEADSET.md;
//   wushuai0802 reads 1A as the headset off). Asleep is no level.
// A level of 0 is what the receiver answers while the headset sleeps or is off (vigil-watt), so it is asleep too, never 0 %.
// The request read back (55 65 01 00) is no reading (vigil-watt's echo rule), nor is any other state. A receiver firmware
// below 0008 does not answer at all.
[BrandFamilies]
public static class MchoseV9Pro
{
    public const int Vendor = 0x291D, Product = 0x385D;

    public static byte[] Request() => BrandFamilies.Frame(64, 0x55, 0x65, 0x01);
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 4 || r[0] != 0x55 || r[1] != 0x65) return null;
        int level = r[2], state = r[3];
        if (state == 0x1A) return new(null, Asleep: true);
        if (state is not (0x00 or 0x02 or 0x03 or 0x04) || level > 100) return null;
        if (state == 0x00 && level == 0x01) return null;
        if (level == 0) return new(null, Asleep: true);
        return new(level, state is 0x00 or 0x03);
    }

    private static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product == Product && HeadsetPages.Is(s, 0xFF90) && s.Usage == 0x0001
        && s.InputLength >= 4 && s.OutputLength >= 64 && s.Name.Contains("V9 PRO", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<BrandFamily> Families =>
    [
        new("MCHOSE V9 Pro", Takes, _ => [new BrandWay("mchose.v9pro", WayTransport.Output, Request(), Battery) { TimeoutMs = 500 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "headset" },
    ];
}
