namespace BrimDeck.Core;

// The VGN V108 keyboard's 2.4 GHz receiver (391D:2A02; 0x391D is shared with other makers' receivers, so only this pair). VGN
// HUB asks it on interface 1 with output report 0x13, command 0x4A: 13 4A 01, zeros, and the low byte of the sum of bytes 0 to
// 18 at 19 (0x5E), 20 bytes. The answer is 13 4A 01 00 02 <percent> <state> … with the same sum at 19. The state byte is the
// one the 13-frame keyboards on 41E4:2001 and 3554:FA09 carry: its high nibble set while charging, 01 (its low nibble) full
// off the cable, as in the captured 100 % answer. From pull request #278 (nekox41, measured on a V108 receiver: 100 %). Only
// a collection of interface 1 that declares report 0x13 with 20-byte reports is asked, and never a standard one (a pointer,
// mouse, keyboard, gamepad or system control collection, or a consumer one): which page the report sits on was not reported.
// A level of 0 is not taken (no source says what the receiver means by it).
[BrandFamilies]
public static class VgnV108
{
    public const int Vendor = 0x391D, Receiver = 0x2A02;

    public static byte[] Request()
    {
        var frame = BrandFamilies.Frame(20, 0x13, 0x4A, 0x01);
        frame[19] = Sum(frame);
        return frame;
    }
    private static byte Sum(byte[] frame) { int sum = 0; for (int i = 0; i < 19; i++) sum += frame[i]; return (byte)sum; }

    public static BatteryRead? Battery(byte[] r)
        => r.Length >= 20 && r[0] == 0x13 && r[1] == 0x4A && r[2] == 0x01 && r[3] == 0x00 && r[4] == 0x02 && r[5] is > 0 and <= 100 && r[19] == Sum(r)
            ? new(r[5], (r[6] & 0xF0) != 0) : null;

    // A generic desktop collection with a usage (usage 0 is left: vendors put their own reports there), or a consumer one.
    private static bool Standard(HidShape shape) => shape.UsagePage == 0x0001 && shape.Usage != 0 || shape.UsagePage == 0x000C;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("VGN V108", shape => shape.Vendor == Vendor && shape.Product == Receiver && shape.Interface == 1 && !Standard(shape)
                && shape.InputLength >= 20 && shape.OutputLength >= 20 && shape.OutputIds.Contains(0x13),
            _ => [new BrandWay("vgn-v108.battery", WayTransport.Output, Request(), Battery) { TimeoutMs = 600 }])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "vgn", DisplayName = _ => "VGN V108" },
    ];
}
