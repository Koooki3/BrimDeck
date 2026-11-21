namespace BrimDeck.Core;

// Keyboards and mice on Shinetek's tri-mode platform (vendor 0x1A2C, the vendor's DeviceDriver.exe; "device_type 301" for the
// keyboards, 601 for the mice). Keyboards: the LANGTU (狼途) T98 RGB on its cable 1A2C:A047, the LANGTU T75 on 1A2C:A071, the
// AJAZZ AK820 Max Plus on 1A2C:A036. Mice: the Machenike (机械师) M7 Pro on 1A2C:9015 (its PAW3311 model, driver "015") and
// 1A2C:9016 (its 8925 model, "016"), the Machenike M721 on 1A2C:9018. All of them share the 2.4 GHz receiver 1A2C:8FFF (the
// T98 RGB's, T75's, AK820 Max Plus's, M7 Pro's and M721's drivers list it). 1A2C is shared by many makers, so only these
// pairs. The collection is the configuration one: usage page 0xFF02, usage 2, 64-byte input and output reports with no report
// id (65 with Windows' report id 0); the cable's interface 0, the receiver's as well.
// The keyboards' status command 0x0E: output report AA 55 CC 33 0E and zeros, answered by AA 55 CC 33 0E <percent> <state>,
// the state 0 with no keyboard behind the receiver (the driver then keeps no level), 1 charging, 2 on battery, 3 full on the
// cable (the driver takes 1 and 3 as on the cable). A level over 100 is passed over, as the driver does. The keyboard also
// sends the same frame by itself every 5 seconds. Sources: LANGTU's T98 tri-mode driver 1.0.4.8 (langtucn.com; its 5-second
// poll and first read send exactly this frame), prasoondev/ajazz-linux's PROTOCOL.md (MIT; the AK820 Max Plus on 1A2C:A036,
// measured on the cable: 64 03, 100 % full) and shrey715/openajazz (MIT). Every other command of the protocol writes (macros,
// key assignments, lighting, a factory reset at 0x08, sleep time, response time, the Fn switch) and none is ever sent.
// The mice's status command 0xFD has no AA 55 CC 33 head: on the cable the output report is 08 FD and zeros, through the
// receiver BB AA 99 88 09 FD and zeros (BB AA 99 88 09 heads every command the mouse driver sends through the receiver). The
// answer is the keyboards' frame, AA 55 CC 33 <any> <percent> <state>, with the same states (0: no level; 1 and 3 charging);
// the mouse also sends it by itself. The driver checks only the head, the state and the level (at most 100), so the fifth byte
// is not checked here either. Source: Machenike's M7 Pro drivers 1.0.4.3 ("015") and 1.0.4.6 ("016") and M721 driver 1.0.4.1
// (machenike.com, the same DeviceDriver.exe code): for the mouse device types it sends FD every 5 seconds (and once on opening
// the receiver), reads the answer back into the battery display, and reads the same frame off the input reports between;
// FD is the only mouse command whose answer it reads. The other commands it sends through the receiver (F6, F7, F8, F9, FB, FC
// and FE) write settings or restore them and are never sent. Not measured on a device.
// On the shared receiver both reads go out, so a keyboard's receiver may also be sent the mouse frame (and a mouse's the
// keyboards'), each tried only through the probing (DeviceReadBook) and kept only if answered. The keyboard sends its 0E frame
// by itself every 5 seconds, so the mouse read there passes over a fifth byte of 0x0E (a keyboard's frame landing in its
// wait must not make the keyboard a mouse); and the receiver's own "nothing behind me" answer to the keyboard read does not
// settle a first try (BrandWay.ProbeOnAsleep), so a mouse behind it is still asked.
[BrandFamilies]
public static class Shinetek
{
    public const int Vendor = 0x1A2C, Receiver = 0x8FFF;

    private static readonly (int Product, string? Name, DeviceKind Kind)[] Products =
    [
        (0xA047, "LANGTU T98 RGB", DeviceKind.Keyboard), (0xA071, "LANGTU T75", DeviceKind.Keyboard), (0xA036, "AJAZZ AK820 Max Plus", DeviceKind.Keyboard),
        (0x9015, "Machenike M7 Pro", DeviceKind.Mouse), (0x9016, "Machenike M7 Pro", DeviceKind.Mouse), (0x9018, "Machenike M721", DeviceKind.Mouse),
        (Receiver, null, DeviceKind.Keyboard),
    ];

    public static byte[] Request() => BrandFamilies.Frame(65, 0x00, 0xAA, 0x55, 0xCC, 0x33, 0x0E);
    public static byte[] MouseRequest(bool receiver) => receiver
        ? BrandFamilies.Frame(65, 0x00, 0xBB, 0xAA, 0x99, 0x88, 0x09, 0xFD)
        : BrandFamilies.Frame(65, 0x00, 0x08, 0xFD);

    // The answer without its report id 0 (StripZero). On the receiver a state of 0 is the device off or out of range. The
    // keyboards' answer carries their command 0x0E; the mice's is not checked (see above).
    public static BatteryRead? Battery(byte[] r, bool receiver) => r.Length >= 5 && r[4] == 0x0E ? Status(r, receiver) : null;
    public static BatteryRead? MouseBattery(byte[] r, bool receiver) => receiver && r.Length >= 5 && r[4] == 0x0E ? null : Status(r, receiver);

    private static BatteryRead? Status(byte[] r, bool receiver)
    {
        if (r.Length < 7 || r[0] != 0xAA || r[1] != 0x55 || r[2] != 0xCC || r[3] != 0x33) return null;
        if (r[6] == 0) return receiver ? new(null, Asleep: true) : null;
        return r[6] <= 3 && r[5] is > 0 and <= 100 ? new(r[5], r[6] is 1 or 3) : null;
    }

    public static bool Takes(HidShape s) => s.Vendor == Vendor && Products.Any(p => p.Product == s.Product)
        && MouseShapes.Collection(s, 0xFF02, 0x0002) && s.InputLength == 65 && s.OutputLength == 65;

    private static BrandWay KeyboardWay(bool receiver) => new("shinetek.status", WayTransport.Output, Request(), r => Battery(r, receiver))
        { TimeoutMs = 800, StripZero = true, ProbeOnAsleep = receiver };
    private static BrandWay MouseWay(bool receiver) => new("shinetek.mouse", WayTransport.Output, MouseRequest(receiver), r => MouseBattery(r, receiver)) { TimeoutMs = 800, StripZero = true };

    // The receiver does not say whether a keyboard or a mouse is behind it: both reads are tried and the one answered is kept
    // (the keyboards' first, as before the mice were added).
    public static IReadOnlyList<BrandWay> Ways(HidShape s) => s.Product == Receiver ? [KeyboardWay(true), MouseWay(true)]
        : Products.First(p => p.Product == s.Product).Kind == DeviceKind.Mouse ? [MouseWay(false)] : [KeyboardWay(false)];

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Shinetek", Takes, Ways)
        {
            Kind = s => Products.FirstOrDefault(p => p.Product == s.Product).Kind, WayKind = way => way == "shinetek.mouse" ? DeviceKind.Mouse : null,
            Prefix = "shinetek", DisplayName = s => Products.FirstOrDefault(p => p.Product == s.Product).Name,
        },
    ];
}
