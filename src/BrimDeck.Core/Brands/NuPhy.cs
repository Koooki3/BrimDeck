namespace BrimDeck.Core;

// NuPhy 2.4 GHz receivers and NuPhyIO wireless keyboards (VID 0x19F5, which GravaStar's G-COME devices share; from drive.nuphy.io and
// documented protocol facts): matches any 65-byte configuration collection on 0x0001:0x0000 with report id 0, excluding
// known wired-only keyboards and all bootloader/upgrader PIDs (0x07xx, 0x1020, 0x6Fxx, 0x8A04, 0xAF0F, 0xFE00) or names.
// Frame 55 05 00 <sum> 01 <off lo> <off hi> 00 reads 1 byte from the profile function table (profile 1 at 0x0060, profile 0
// at 0x0020; sum = low byte of bytes after index 3); the keyboard answers AA 05 00 <sum> 01 <off lo> <off hi> 00 <value>
// (low 7 bits percent, bit 7 charging) or pushes FA FB 07 00 <status> <percent>.
[BrandFamilies]
public static class NuPhy
{
    public const int Vendor = 0x19F5, U1Dongle = 0x2620;
    public const ushort Profile1BatteryOffset = 0x0060, Profile0BatteryOffset = 0x0020;

    private static readonly HashSet<int> WiredOnly =
    [
        0x1021, 0x1022, 0x1023, 0x1024, 0x1025,
        0x103D, 0x103E, 0x1041, 0x1045,
        0x32F5, 0x6112,
    ];

    // Known 2.4 GHz receivers and wireless-capable keyboards; U1 (0x2620) and unlisted wireless IDs fall back to the USB name.
    public static readonly IReadOnlyDictionary<int, string> KnownNames = new Dictionary<int, string>
    {
        [0x8F01] = "NuPhy WH80",
        [0x1026] = "NuPhy Kick75",
        [0x1027] = "NuPhy Kick75",
        [0x1028] = "NuPhy Air75 V3",
        [0x1029] = "NuPhy Air75 V3",
        [0x102A] = "NuPhy Air75 V3",
        [0x102B] = "NuPhy Air65 V3",
        [0x102C] = "NuPhy Halo75 Lite",
        [0x102D] = "NuPhy Air100 V3",
        [0x102E] = "NuPhy Halo96 Lite",
        [0x102F] = "NuPhy Halo65 Lite",
        [0x1030] = "NuPhy Node75",
        [0x1031] = "NuPhy Node75",
        [0x1032] = "NuPhy Node75",
        [0x1033] = "NuPhy Node75",
        [0x1034] = "NuPhy Node75",
        [0x1035] = "NuPhy Node75",
        [0x1036] = "NuPhy Node100",
        [0x1037] = "NuPhy Node100",
        [0x1038] = "NuPhy Node100",
        [0x1039] = "NuPhy Air100 V3",
        [0x103A] = "NuPhy Node100",
        [0x103B] = "NuPhy Node100",
        [0x103C] = "NuPhy Node100",
        [0x103F] = "NuPhy Air100 V3",
        [0x1040] = "NuPhy Air65 V3",
        [0x1042] = "NuPhy Air65 V3",
        [0x1043] = "NuPhy Halo75 V2",
        [0x1044] = "NuPhy Halo96 V2",
        [0xA011] = "NuPhy WH80",
    };

    // 19F5 is not NuPhy's alone: GravaStar's G-COME devices are on it too (GravaStar.OnGcomeVendor), and are not read here.
    public static bool IsExcluded(int product, string? name = "") => WiredOnly.Contains(product) || GravaStar.OnGcomeVendor.Contains(product)
        || product is (>= 0x0700 and <= 0x07FF) or 0x1020 or (>= 0x6F00 and <= 0x6FFF) or 0x8A04 or 0xAF0F or 0xFE00
        || (!string.IsNullOrEmpty(name) && (name.Contains("Upgrader", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Boot", StringComparison.OrdinalIgnoreCase)
            || name.Contains("DFU", StringComparison.OrdinalIgnoreCase)));

    public static byte[] Request(ushort offset)
    {
        byte lo = (byte)offset, hi = (byte)(offset >> 8);
        return BrandFamilies.Frame(65, 0x00, 0x55, 0x05, 0x00, (byte)(0x01 + lo + hi), 0x01, lo, hi, 0x00);
    }

    public static BatteryRead? Battery(byte[] r, ushort offset)
    {
        int o = r.Length > 9 && r[0] == 0x00 && r[1] == 0xAA ? 1 : 0;
        if (r.Length >= o + 9 && r[o] == 0xAA && r[o + 1] == 0x05 && r[o + 2] == 0x00
            && r[o + 4] == 0x01 && r[o + 5] == (byte)offset && r[o + 6] == (byte)(offset >> 8) && r[o + 7] == 0x00)
        {
            int sum = 0;
            for (int i = 4; i < 9; i++) sum += r[o + i];
            if (r[o + 3] != (byte)sum) return null;
            int level = r[o + 8] & 0x7F;
            return level is > 0 and <= 100 ? new(level, (r[o + 8] & 0x80) != 0) : null;
        }
        int p = r.Length > 6 && r[0] == 0x00 && r[1] == 0xFA ? 1 : 0;
        if (r.Length >= p + 6 && r[p] == 0xFA && r[p + 1] == 0xFB && r[p + 2] == 0x07 && r[p + 4] <= 4 && r[p + 5] is > 0 and <= 100)
            return new(r[p + 5], r[p + 4] is 1 or 2 or 3);
        return null;
    }

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && !IsExcluded(shape.Product, shape.Name)
        && shape.UsagePage == 0x0001 && shape.Usage == 0x0000 && shape.InputLength == 65 && shape.OutputLength == 65;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("NuPhy", Takes, _ =>
            [
                new BrandWay("nuphy.func1", WayTransport.Output, Request(Profile1BatteryOffset), r => Battery(r, Profile1BatteryOffset))
                {
                    TimeoutMs = 400, Attempts = 1, StripZero = true,
                },
                new BrandWay("nuphy.func0", WayTransport.Output, Request(Profile0BatteryOffset), r => Battery(r, Profile0BatteryOffset))
                {
                    TimeoutMs = 400, Attempts = 1, StripZero = true,
                },
            ])
        {
            Kind = _ => DeviceKind.Keyboard,
            Prefix = "nuphy",
            DisplayName = shape => KnownNames.GetValueOrDefault(shape.Product),
        },
    ];
}
