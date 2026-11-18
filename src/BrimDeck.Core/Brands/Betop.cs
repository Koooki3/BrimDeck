namespace BrimDeck.Core;

// BETOP (北通) controllers on their 2.4 GHz receivers and cables, vendor 0x20BC (BETOP's own: every 20BC receiver is asked the
// status read rather than a list of models) and 0x20DD, the second vendor id BETOP's client registers each model under (only
// the products its scripts list are taken there). The receiver has four collections, told apart by their usage: 3 the vendor
// one, 2 the mouse, 6 the keyboard and 5 the gamepad the controller is to Windows. Only the vendor one is asked; its page is
// written 0xFF00 in the HID specification's order, but Windows reports it as 0x00FF (both sources measured that), so either
// is taken. It takes output report 0x02 with command 0x15, zeros after (the report's own length); the answer is input report
// 02 15 <percent> 00 <state>, the state 0x50 on battery and 0x51 charging (its low nibble set while charging). A state whose
// high nibble is not 5 is no answer (the request read back is 02 15 00 00 00), and neither is a level of 0 (vigil-watt's BETOP
// notes, MIT: the receiver's placeholder while not ready). From oitsukiii/betop-battery (MIT; Kunpeng 20, checked against
// BETOP's own client on Windows 11) and HaloBattery's beitong.py (MIT; KP40D, from a capture of the official client).
//
// Some models scramble every byte after the report id, both ways (BETOP's client 2.6.2, its model scripts calling
// SetHidWriteEnc 1, the table in its native JoypadConnHelper): a byte's high nibble goes through Nibbles and its low nibble is
// XORed with 8, so the status command 0x15 goes out as 0xAD and the answer comes back scrambled the same way. The zeros that pad
// the report are sent as they are, as the client's own writes do. The plain 02 15 reaches such a model as command 0x4D, which
// it never answers (#348, the Kunpeng 20 BTP-KP20A on 20BC:5128). The KP20's script reads its state unmasked and a public
// implementation takes 1 as charging, so a scrambled answer's state is only read for its low nibble; the read-back request
// unscrambles to 02 15 08 08 08, which the 00 at [3] turns away. vigil-watt's log of 20DD:5128 sending 02 CD 09 0A 08 by
// itself unscrambles with the same table to the firmware report 02 55 01 02 00.
//
// The models, from the client's scripts (cable / receiver): scrambled Kunpeng 20 5126/5128, Kunpeng 40 5125/5127, 5145/5146,
// 5154/5155, 515D/515E, 5169/516A; plain Kunpeng 20 5158/5159, 516F/5170, Kunpeng 40 515B/515C, 5163/5164, 5165/5166, Kunpeng
// 50 5133/5134, 5149/514A, 5150/5151, 5152/5153, Kunpeng 70 512F/5130, 515F/5160, 516D/516E. 5125/5127 also has an older script
// that does not scramble, so it is asked scrambled first and plain after. Only reads are sent: the client's other commands
// set modes and profiles (0x11, 0x31), write or save the configuration (0x22, 0x62, 0xA2, 0xE2, 0x13, 0x23), vibrate (0x16),
// reset (0x36), enter a test mode (0x17), pair (0x27, 0x37) or update the firmware, and none of them is ever sent.
[BrandFamilies]
public static class Betop
{
    public const int Vendor = 0x20BC, SecondVendor = 0x20DD;

    private static readonly byte[] Nibbles = [0x0, 0xA, 0x6, 0xF, 0x1, 0xC, 0x2, 0x9, 0xE, 0x3, 0x7, 0xD, 0x4, 0xB, 0x5, 0x8];
    private static readonly byte[] Unscrambled = Inverse();

    private static readonly HashSet<int> ScrambledProducts = [0x5126, 0x5128, 0x5125, 0x5127, 0x5145, 0x5146, 0x5154, 0x5155, 0x515D, 0x515E, 0x5169, 0x516A];
    private static readonly HashSet<int> BothProducts = [0x5125, 0x5127];
    private static readonly HashSet<int> PlainProducts = [0x5158, 0x5159, 0x516F, 0x5170, 0x515B, 0x515C, 0x5163, 0x5164, 0x5165, 0x5166,
        0x5133, 0x5134, 0x5149, 0x514A, 0x5150, 0x5151, 0x5152, 0x5153, 0x512F, 0x5130, 0x515F, 0x5160, 0x516D, 0x516E];

    public static byte Scramble(byte value) => (byte)((Nibbles[value >> 4] << 4) | ((value & 0x0F) ^ 0x08));
    public static byte Unscramble(byte value) => Unscrambled[value];

    private static byte[] Inverse()
    {
        var inverse = new byte[256];
        for (int value = 0; value < 256; value++) inverse[Scramble((byte)value)] = (byte)value;
        return inverse;
    }

    public static bool Scrambles(int product) => ScrambledProducts.Contains(product);

    public static byte[] Request() => BrandFamilies.Frame(2, 0x02, 0x15);
    public static BatteryRead? Battery(byte[] r)
        => r.Length >= 5 && r[0] == 0x02 && r[1] == 0x15 && r[3] == 0x00 && (r[4] & 0xF0) == 0x50 && r[2] is > 0 and <= 100 ? new(r[2], (r[4] & 0x0F) != 0) : null;

    public static byte[] ScrambledRequest() => BrandFamilies.Frame(2, 0x02, Scramble(0x15));
    public static BatteryRead? ScrambledBattery(byte[] r)
    {
        if (r.Length < 5 || r[0] != 0x02 || Unscramble(r[1]) != 0x15 || Unscramble(r[3]) != 0x00) return null;
        int level = Unscramble(r[2]);
        return level is > 0 and <= 100 ? new(level, (Unscramble(r[4]) & 0x0F) != 0) : null;
    }

    private static readonly BrandWay Plain = new("betop.status", WayTransport.Output, Request(), Battery) { TimeoutMs = 800 };
    private static readonly BrandWay Scrambled = new("betop.status.scrambled", WayTransport.Output, ScrambledRequest(), ScrambledBattery) { TimeoutMs = 800 };

    public static IEnumerable<BrandFamily> Families =>
    [
        new("BETOP", shape => (shape.Vendor == Vendor || shape.Vendor == SecondVendor && (ScrambledProducts.Contains(shape.Product) || PlainProducts.Contains(shape.Product)))
                && shape.UsagePage is 0x00FF or 0xFF00 && shape.Usage == 0x0003 && shape.InputLength >= 5,
            shape => BothProducts.Contains(shape.Product) ? [Scrambled, Plain] : Scrambles(shape.Product) ? [Scrambled] : [Plain])
        { Kind = _ => DeviceKind.Controller, Prefix = "betop" },
    ];
}
