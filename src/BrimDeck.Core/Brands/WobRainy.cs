namespace BrimDeck.Core;

// WOBKEY and PMO wireless keyboards (VID 0x36B0, from WOB HUB Driver 2.0 at wobwxe.com and WOB Legacy):
// 1. 0xBE .. 0xED 257-byte protocol (wob.be): Rainy75 V2 (3071 rx / 3075 cable) and Rainy98 (3091 rx / 3098 cable) on
//    0xFF60:0x0061. Bootloaders (3070, 3078, 3079, 3090, 3099), wired RT models (343F, 352F..3531), Rainy S1 (3151:5043)
//    and GL68 (1CA6:3Dxx) are excluded; Rainy75 V1 (320F:5088) stays with Dongles.Vgn. Never send firmware-entry BE FC.
//    A receiver drops the battery query BE BB 04 ED unless a 2.4 GHz session is open: WOBHub sends BE 93 05 01 ED
//    (beginWireless, echoed) before readBattery and BE 93 05 00 ED (endWireless) after, and so does this read, 300 ms
//    apart as a user's measurement on a Rainy75 V2 receiver (36B0:3071, 2026-11-17: no answer in 4 s without the session,
//    2 to 9 ms with it; the session lapses within 15 s) waited. The receiver also pushes BE BB 06 <level> <state> ED every
//    5 s with or without a session (the same measurement), which a second read only listens for. State 0 on battery, 1 full,
//    2 charging.
// 2. 0xAA RDR / Zen65 protocol (wob.rdr): PMO Aurora80 (3004), PMO Wave75 (3009), WOBKEY Zen 65 (3025), and the shared
//    36B0:3002 2.4 GHz receiver (when not named URX, so Urx keeps URX Core75 Wireless untouched). Uses read-only session
//    wrappers CMD_START_COMM (0x10) / CMD_STOP_COMM (0x11) around CMD_GET_BATTERY_STATUS (0x30) and CMD_GET_DONGLE (0xD0).
[BrandFamilies]
public static class WobRainy
{
    public const int Vendor = 0x36B0, Receiver = 0x3071, Cable = 0x3075, RdrReceiver = 0x3002;

    public static readonly IReadOnlyDictionary<int, (string Name, bool Receiver)> BeModels = new Dictionary<int, (string, bool)>
    {
        [Receiver] = ("WOBKEY Rainy75 V2", true),
        [Cable] = ("WOBKEY Rainy75 V2", false),
        [0x3091] = ("WOBKEY Rainy98", true),
        [0x3098] = ("WOBKEY Rainy98", false),
    };

    public static readonly IReadOnlyDictionary<int, string> RdrCables = new Dictionary<int, string>
    {
        [0x3004] = "PMO Aurora80",
        [0x3009] = "PMO Wave75",
        [0x3025] = "WOBKEY Zen 65",
    };

    public static byte[] OnlineRequest() => BrandFamilies.Frame(257, 0x00, 0xBE, 0x65, 0x04, 0xED);
    public static byte[] Request() => BrandFamilies.Frame(257, 0x00, 0xBE, 0xBB, 0x04, 0xED);
    public static byte[] BeginWireless() => BrandFamilies.Frame(257, 0x00, 0xBE, 0x93, 0x05, 0x01, 0xED);
    public static byte[] EndWireless() => BrandFamilies.Frame(257, 0x00, 0xBE, 0x93, 0x05, 0x00, 0xED);

    public static bool? Online(byte[] r)
    {
        int o = r.Length > 5 && r[0] == 0x00 && r[1] == 0xBE ? 1 : 0;
        if (r.Length < o + 5 || r[o] != 0xBE || r[o + 1] != 0x65 || r[o + 2] != 0x05 || r[o + 4] != 0xED) return null;
        return r[o + 3] switch { 1 => true, 0 => false, _ => null };
    }

    public static BatteryRead? Battery(byte[] r)
    {
        int o = r.Length > 6 && r[0] == 0x00 && r[1] == 0xBE ? 1 : 0;
        if (r.Length < o + 6 || r[o] != 0xBE || r[o + 1] != 0xBB) return null;
        int len = r[o + 2];
        bool validFrame = (len == 6 && r[o + 5] == 0xED)
            || (len == 0 && r[o + 5] == 0xED && r.Skip(o + 6).All(b => b == 0))
            || (r.Length >= o + 10 && len == 10 && r[o + 9] == 0xED)
            || (r.Length >= o + 14 && len == 14 && r[o + 13] == 0xED);
        if (!validFrame) return null;
        int level = r[o + 3], charge = r[o + 4];
        return level is > 0 and <= 100 && charge <= 2 ? new(level, charge == 2) : null;
    }

    public static byte[] RdrFrame(int outputLength, byte command, byte size = 0)
        => BrandFamilies.Frame(outputLength, 0x00, 0xAA, command, 0x00, 0x00, size);

    public static byte[] RdrOnlineRequest(int outputLength) => RdrFrame(outputLength, 0xD0, 0x18);

    public static bool? RdrOnline(byte[] r)
    {
        int o = r.Length > 14 && r[0] == 0x00 && r[1] == 0xAA ? 1 : 0;
        if (r.Length < o + 14 || r[o] != 0xAA || r[o + 1] != 0xD0) return null;
        return r[o + 13] == 4 ? true : r[o + 13] < 4 ? false : null;
    }

    public static BatteryRead? RdrBattery(byte[] r)
    {
        int o = r.Length > 10 && r[0] == 0x00 && r[1] == 0xAA ? 1 : 0;
        if (r.Length < o + 10 || r[o] != 0xAA || r[o + 1] != 0x30 || r[o + 2] != 0x00 || r[o + 3] != 0x00 || r[o + 4] != 0x18 || r[o + 7] != 0x55) return null;
        return r[o + 8] is > 0 and <= 100 && r[o + 9] <= 2 ? new(r[o + 8], r[o + 9] == 1) : null;
    }

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && BeModels.ContainsKey(shape.Product)
        && UsagePages.Is(shape.UsagePage, 0xFF60) && shape.Usage == 0x0061 && shape.InputLength == 257 && shape.OutputLength == 257;

    private static readonly string[] RdrReceiverNames = ["PMO", "WOB", "Aurora", "Wave", "Zen"];
    public static bool IsRdrReceiverName(string? name) => !string.IsNullOrEmpty(name)
        && !name.Contains("URX", StringComparison.OrdinalIgnoreCase)
        && RdrReceiverNames.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    public static bool TakesRdr(HidShape shape) => shape.Vendor == Vendor
        && UsagePages.Is(shape.UsagePage, 0xFF60) && shape.Usage == 0x0061
        && ((shape.Product == RdrReceiver && IsRdrReceiverName(shape.Name)
                && shape.InputLength is 33 or 65 && shape.OutputLength == shape.InputLength)
            || (RdrCables.ContainsKey(shape.Product) && shape.InputLength == 65 && shape.OutputLength == 65));

    public static IEnumerable<BrandFamily> Families =>
    [
        new("WOBKEY", Takes, shape => BeModels[shape.Product].Receiver
            ?
            [
                new BrandWay("wob.be", WayTransport.Output, Request(), Battery)
                {
                    TimeoutMs = 1000, Attempts = 2, GapMs = 300, StripZero = true,
                    Before = [BeginWireless()], After = [EndWireless()],
                    Gate = OnlineRequest(), Gated = Online,
                },
                new BrandWay("wob.be-push", WayTransport.Listen, [], Battery) { TimeoutMs = 5500, Attempts = 1, StripZero = true },
            ]
            : [new BrandWay("wob.be", WayTransport.Output, Request(), Battery) { TimeoutMs = 1000, Attempts = 2, StripZero = true }])
        {
            Kind = _ => DeviceKind.Keyboard,
            Prefix = "wob",
            DisplayName = shape => BeModels[shape.Product].Name,
            StandAsideFor = ["WOBHub.exe"],
        },
        new("PMO / WOBKEY", TakesRdr, shape =>
            {
                byte chunk = shape.OutputLength >= 65 ? (byte)0x38 : (byte)0x18;
                return
                [
                    new BrandWay("wob.rdr", WayTransport.Output, RdrFrame(shape.OutputLength, 0x30, 0x18), RdrBattery)
                    {
                        TimeoutMs = 800, Attempts = 2, GapMs = 30, StripZero = true,
                        Gate = shape.Product == RdrReceiver ? RdrOnlineRequest(shape.OutputLength) : null,
                        Gated = shape.Product == RdrReceiver ? RdrOnline : null,
                        Before = [RdrFrame(shape.OutputLength, 0x10, chunk)],
                        After = [RdrFrame(shape.OutputLength, 0x11, chunk)],
                    }
                ];
            })
        {
            Kind = _ => DeviceKind.Keyboard,
            Prefix = "wob",
            DisplayName = shape => RdrCables.GetValueOrDefault(shape.Product),
            StandAsideFor = ["WOBHub.exe"],
        },
    ];
}
