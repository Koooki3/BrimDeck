namespace BrimDeck.Core;

// Keyboards on Sonix-platform 2.4 GHz receivers that answer the battery query 20 01 on their raw HID collection (usage page
// 0xFF60, usage 0x61, 33-byte input and output reports with report id 0): [0, 0x20, 0x01, zeros, checksum], the checksum the
// low byte of the sum of the 32 bytes before it (0x21). The answer, after the zero report id: [0x20, 0x01, 0x00, percent,
// zeros, checksum], the checksum the low byte of the sum of the 31 bytes before it. No charging state is sent. Zero is the
// keyboard asleep (and the request read back), not a level.
//
// - AULA F108 Pro and GANSS GS3104T-Pro (and the AULA F75 Max on the same platform) on 05AC:024F: Apple's ids, borrowed by
//   the receiver, which Apple's own wired Aluminium Keyboard carries too (it has no 0xFF60 collection) and Lofree's Flow
//   Lite84 receiver as well. The battery is on interface 3 only (interfaces 0 and 4 give none), so the collection is taken on
//   interface 3, or where the product string names the keyboard ("F108Pro Dongle" measured; GANSS, GS3104) when the
//   interface number moved. vigil-watt (MIT; GANSS capture 97 %, F108 Pro checked on the keyboard), aula-keyboard-battery-tray
//   and Aula-F75-Max-Driver (MIT; the same query and answer for the F75 Max).
// - EWEADN X87 on 0C45:FEFE, Sonix's id: EWEADN's driver takes only interface 3 (its interface 4 is the same page with 65-byte
//   reports and must not be written). vigil-watt (MIT; from the driver, measured at 100 %). The AULA F87 Pro V2D's receiver
//   carries 0C45:FEFE as well (aula-keyboard-battery-tray, MIT), so the receiver's own product string names the keyboard,
//   not "EWEADN X87".
[BrandFamilies]
public static class SonixQuery
{
    public const int AppleVendor = 0x05AC, BorrowedReceiver = 0x024F, SonixVendor = 0x0C45, EweadnReceiver = 0xFEFE;

    public static byte[] Request()
    {
        var frame = BrandFamilies.Frame(33, 0x00, 0x20, 0x01);
        int sum = 0; for (int i = 0; i < 32; i++) sum += frame[i];
        frame[32] = (byte)sum;
        return frame;
    }

    // The input report without its zero report id.
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 32 || r[0] != 0x20 || r[1] != 0x01 || r[2] != 0x00 || r[3] is 0 or > 100) return null;
        int sum = 0; for (int i = 0; i < 31; i++) sum += r[i];
        return (byte)sum == r[31] ? new(r[3]) : null;
    }

    private static bool Page(int actual, int page) => actual == page || actual == ((page & 0xFF) << 8 | page >> 8);
    public static bool Collection(HidShape shape) => Page(shape.UsagePage, 0xFF60) && shape.Usage == 0x0061 && shape.InputLength == 33 && shape.OutputLength == 33;

    // The model a 05AC:024F receiver names, if it names one.
    public static string? Named(string name)
        => name.Contains("F108", StringComparison.OrdinalIgnoreCase) ? "AULA F108 Pro"
            : name.Contains("GANSS", StringComparison.OrdinalIgnoreCase) || name.Contains("GS3104", StringComparison.OrdinalIgnoreCase) ? "GANSS GS3104T-Pro"
            : null;

    public static bool TakesBorrowed(HidShape shape) => shape.Vendor == AppleVendor && shape.Product == BorrowedReceiver && Collection(shape)
        && (shape.Interface == 3 || Named(shape.Name) is not null);
    public static bool TakesEweadn(HidShape shape) => shape.Vendor == SonixVendor && shape.Product == EweadnReceiver && Collection(shape) && shape.Interface == 3;

    private static IReadOnlyList<BrandWay> Ways(string prefix) => [new BrandWay(prefix + ".query", WayTransport.Output, Request(), Battery) { TimeoutMs = 800, StripZero = true }];

    public static IEnumerable<BrandFamily> Families =>
    [
        new("AULA / GANSS", TakesBorrowed, _ => Ways("sonix"))
        { Kind = _ => DeviceKind.Keyboard, Prefix = "sonix", DisplayName = shape => Named(shape.Name) },
        new("EWEADN", TakesEweadn, _ => Ways("eweadn"))
        { Kind = _ => DeviceKind.Keyboard, Prefix = "eweadn" },
    ];
}
