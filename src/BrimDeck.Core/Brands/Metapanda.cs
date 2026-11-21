namespace BrimDeck.Core;

// Metaphyuni's (玄派) Metapanda (玄熊猫) P1 on its NearLink receiver 369B:00B3 (product string "MPMouseNearLink"), usage page
// 0xFFA0 usage 2, 64-byte input and output reports: the collection, report ids and vendor of Thunderobot's ML903
// (ThunderobotMice), with another command. Output report 0xA0 with the wireless-status command 0x50, answered on input
// report 0xA1:
//   A1 50 01 03 0D | 02 04 25 1F 44 03 | 03 02 06 04 | 00 01 1B
// [4] is the length of what follows, fields of a type, a length and a value. The field of type 02 starts with 25 (the tag
// the ML903's device info gives its battery under, 02 25) and the level after it: 1 to 100 the percent, 188 charging (the
// firmware gives no percent then) and 189 charged. A1 50 and zeros is what the receiver sends while the mouse is off or out
// of reach. Measured in #305 on a P1 (31 % and 32 % against the vendor's driver, four requests out of four answered within
// 0.3 s; the command and the two marks from the vendor's driver); the fields' layout is read off that one answer, so an
// answer that does not walk to its length, has no such field or has two is no reading.
//
// The vendor id is the NearLink receivers' maker's, not one brand's: another 369B product with the same collection and
// output report is asked both commands, this one (once a pass) and the ML903's device info, and keeps the one it answers
// (DeviceReadBook). Neither is measured on such a product, and what it is comes from its product string alone. There the
// zeros are no answer rather than a mouse asleep, so a product that sends them without speaking this command is not held
// to it; one whose mouse is off at every try stays unread until it is plugged in again or BrimDeck starts again.
[BrandFamilies]
public static class MetapandaMice
{
    public const int P1 = 0x00B3;
    public const byte Charging = 188, Charged = 189;

    public static byte[] Request() => BrandFamilies.Frame(2, 0xA0, 0x50);

    // The level in a status answer; null for any other report, the zeros included.
    public static BatteryRead? Status(byte[] r)
    {
        if (r.Length < 9 || r[0] != 0xA1 || r[1] != 0x50 || r[4] == 0) return null;
        int end = 5 + r[4];
        if (end > r.Length) return null;
        int? level = null;
        for (int at = 5; at < end;)
        {
            if (at + 2 > end) return null;
            int type = r[at], length = r[at + 1];
            if (at + 2 + length > end) return null;
            if (type == 0x02)
            {
                if (level is not null || length < 2 || r[at + 2] != 0x25) return null;
                level = r[at + 3];
            }
            at += 2 + length;
        }
        return level switch
        {
            Charging => new(null, Charging: true),
            Charged => new(100),
            > 0 and <= 100 => new(level.Value),
            _ => null
        };
    }
    // As Status, and the zeros the P1's receiver sends for a mouse that is off: asleep.
    public static BatteryRead? StatusOrAsleep(byte[] r)
        => r.Length >= 9 && r[0] == 0xA1 && r[1] == 0x50 && r.AsSpan(2).IndexOfAnyExcept((byte)0) < 0 ? new(null, Asleep: true) : Status(r);

    private static bool Collection(HidShape shape) => shape.Vendor == ThunderobotMice.Vendor && MouseShapes.Collection(shape, 0xFFA0, 0x02) && shape.InputLength >= 9;
    public static bool Takes(HidShape shape) => shape.Product == P1 && Collection(shape);
    // Another product of the vendor: only where the collection declares the output report both commands go in, so a
    // collection that could not be asked is not listed as a device without a reading.
    public static bool Others(HidShape shape) => shape.Product is not (P1 or ThunderobotMice.Ml903) && Collection(shape)
        && shape.InputLength >= 25 && shape.OutputIds.Contains(0xA0);

    // Machenike's NearLink mice (the L8 Max NearLink, L8 SE and L7 Air NearLink apps, machenike.com: their device tables on
    // 0xFFA0 / 2 with reports A0 and A1), which these reads reach as other products: listed only to show them as mice.
    public static readonly IReadOnlySet<int> MachenikeNearLink = new HashSet<int> { 0x0043, 0x0044, 0x0053, 0x0054, 0x0123, 0x0144, 0x0133, 0x0154, 0x01B3, 0x0204 };

    private static BrandWay Way(Func<byte[], BatteryRead?> parse) => new("metapanda.status", WayTransport.Output, Request(), parse) { TimeoutMs = 600 };

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Metapanda", Takes, _ => [Way(StatusOrAsleep)])
        { Kind = _ => DeviceKind.Mouse, Prefix = "nearlink", DisplayName = _ => "Metapanda NearLink Mouse" },
        new("NearLink receiver", Others, _ => [Way(Status) with { Attempts = 1 }, ThunderobotMice.Info()])
        { Prefix = "nearlink", Kind = shape => MachenikeNearLink.Contains(shape.Product) ? DeviceKind.Mouse : null },
    ];
}
