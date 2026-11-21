namespace BrimDeck.Core;

// Keyboards on Bytech's "916" and "8815" platforms over their cable: the battery is asked with a feature report of 520 bytes
// (the report id and 519 of data): <report> 87 00 00 01 00 02 00 and zeros, then the same report is got back:
// <report> 87 00 00 01 00 02 00 <percent> <state>, the level at 8 and the state's high nibble 1 while charging (its low
// nibble 1 full). Measured on a VGN V98Pro V3's cable (258A:024E, pull request #327): 09 87 00 00 01 00 02 00 64 11, 100 %
// charging, on its vendor collection 0xFF02 / 1. Not measured by us on the others. Sources:
// - VGN HUB (hub.vgnlab.com), its "beiying" cables (isBeiyingWired): VGN V98Pro V3 258A:024E and 027E, VGN V87 V2 258A:026A,
//   VXE V87pro 258A:010C (its feature report 6, not 9), VGN N75 V2 391D:2003 and 2004, VGN V108 391D:2005 and 2006. It reads
//   the collection whose first feature report is the table's (getBattery: 135 0 0 1 0 2 0, the level at 8).
// - GravaStar's 1khub (support.gravastar.com/1khub), Bytech's multi-brand driver (GravaStar's Mercury K1, K1 Lite, K1 Pro,
//   NP, NP Pro, K98, Kinesis, Redragon, Hellyberry and others, told apart only by what they answer later): 916 cables
//   258A:010C, 019D, 0293, 0294, 0295, 0296, 0297, 02A5, 02DD, 02E9 and 379A:A000, A001, A004, A005, A008, 8815 cable
//   258A:01B9, on a collection with feature and input reports on a vendor page (the 8815's page is not checked by it), the
//   collection's first feature report the one asked.
// 258A is Sinowealth's id, shared by many products (AULA's SC380 Pro, 258A:0138, has a 520-byte report 9 with another
// protocol), and 391D and 379A by other makers, so only these pairs; on a vendor collection, its first feature report, with
// 520 bytes; a reply counts with the query echoed.
// Their 2.4 GHz receivers take VGN's 13 frame (VgnV108: output report 0x13, 13 4A 01, the low byte of the sum at 19; the
// answer 13 4A 01 00 02 <percent> <state>, the same sum): VGN HUB's beiying receivers are VgnV108's, 3554:FA09 is AULA's
// reader's, 258A:0233 is RK's (its R98Pro V2 alone); 1khub's other 916 receivers, 258A:0150 and 379A:A002, A006, A009, are
// read here on a vendor collection that declares report 0x13 with 20-byte reports (1khub sends the frame without the sum;
// VGN HUB and the readers here send it with it, which these receivers are not known to refuse; their answer is taken
// without its sum, as 1khub takes it).
[BrandFamilies]
public static class Bytech916
{
    public const int FeatureLength = 520;
    public const byte Command = 0x87;

    // 258A:010C is not VXE's alone (LEOBOG's and LANGTU's cables carry it too, their drivers' tables say), so it keeps its own
    // product string.
    public static readonly (int Vendor, int Product, byte Report, string? Name)[] Vgn =
    [
        (0x258A, 0x024E, 0x09, "VGN V98Pro V3"), (0x258A, 0x027E, 0x09, "VGN V98Pro V3"), (0x258A, 0x026A, 0x09, "VGN V87 V2"),
        (0x258A, 0x010C, 0x06, null), (0x391D, 0x2003, 0x09, "VGN N75 V2"), (0x391D, 0x2004, 0x09, "VGN N75 V2"),
        (0x391D, 0x2005, 0x09, "VGN V108"), (0x391D, 0x2006, 0x09, "VGN V108"),
    ];
    public static readonly (int Vendor, int Product)[] OneKHub =
    [
        (0x258A, 0x019D), (0x258A, 0x0293), (0x258A, 0x0294), (0x258A, 0x0295), (0x258A, 0x0296), (0x258A, 0x0297), (0x258A, 0x02A5),
        (0x258A, 0x02DD), (0x258A, 0x02E9), (0x379A, 0xA000), (0x379A, 0xA001), (0x379A, 0xA004), (0x379A, 0xA005), (0x379A, 0xA008),
        (0x258A, 0x01B9),
    ];

    public static readonly (int Vendor, int Product)[] OneKHubReceivers = [(0x258A, 0x0150), (0x379A, 0xA002), (0x379A, 0xA006), (0x379A, 0xA009)];

    // 1khub's receivers' answer, which it takes without a sum: 13 4A 01 00 02 <percent> <state>, the state's high nibble 1
    // charging. The request read back has 00 at 4.
    public static BatteryRead? ReceiverBattery(byte[] r)
        => r.Length >= 7 && r[0] == 0x13 && r[1] == 0x4A && r[2] == 0x01 && r[3] == 0x00 && r[4] == 0x02 && r[5] is > 0 and <= 100
            ? new(r[5], (r[6] & 0xF0) != 0) : null;

    public static byte[] Request(byte reportId)
    {
        var frame = new byte[FeatureLength];
        frame[0] = reportId; frame[1] = Command; frame[4] = 0x01; frame[6] = 0x02;
        return frame;
    }

    // The report got back, its id first: the query echoed to 6, the level at 8, 1 in the high nibble of 9 while charging. A
    // level of 0 (the request's own echo) or over 100 is none.
    public static BatteryRead? Battery(byte reportId, byte[] r)
        => r.Length >= 10 && r[0] == reportId && r[1] == Command && r[2] == 0x00 && r[3] == 0x00 && r[4] == 0x01 && r[5] == 0x00 && r[6] == 0x02
            && r[8] is >= 1 and <= 100
            ? new(r[8], (r[9] >> 4 & 0xF) == 1) : null;

    private static bool Shape(HidShape s) => MouseShapes.VendorPage(s) && s.FeatureLength >= FeatureLength && s.FeatureIds.Count > 0;
    private static (int Vendor, int Product, byte Report, string? Name)? VgnOf(HidShape s)
        => Vgn.Any(p => p.Vendor == s.Vendor && p.Product == s.Product) ? Vgn.First(p => p.Vendor == s.Vendor && p.Product == s.Product) : null;
    // VGN HUB: the collection that declares the table's feature report (it looks at no input report); 1khub: the collection's
    // first feature report where it has input reports too, which here is taken only where the collection declares one feature
    // report (the order they are declared in is not known here, and the query must not go to another).
    private static bool IsVgn(HidShape s) => VgnOf(s) is { } p && Shape(s) && s.FeatureIds.Contains(p.Report);
    private static bool IsOneKHub(HidShape s) => OneKHub.Contains((s.Vendor, s.Product)) && Shape(s) && s.FeatureIds.Count == 1 && s.InputLength > 0;

    private static bool IsOneKHubReceiver(HidShape s) => OneKHubReceivers.Contains((s.Vendor, s.Product)) && MouseShapes.VendorPage(s)
        && s.InputLength >= 20 && s.OutputLength >= 20 && s.OutputIds.Contains(0x13);

    private static IReadOnlyList<BrandWay> Ways(byte id, string name)
        => [new BrandWay(name, WayTransport.Feature, Request(id), r => Battery(id, r)) { GapMs = 25, FeatureLength = FeatureLength, TimeoutMs = 600 }];

    public static IEnumerable<BrandFamily> Families =>
    [
        new("VGN keyboard", IsVgn, s => Ways(VgnOf(s)!.Value.Report, "vgn-v98pro-v3-wired.battery"))
        { Kind = _ => DeviceKind.Keyboard, Prefix = "vgn", DisplayName = s => VgnOf(s)?.Name },
        new("Bytech 916 keyboard", IsOneKHub, s => Ways(s.FeatureIds.Order().First(), "bytech916.battery")) { Kind = _ => DeviceKind.Keyboard, Prefix = "bytech" },
        new("Bytech 916 receiver", IsOneKHubReceiver, _ => [new BrandWay("bytech916.13", WayTransport.Output, VgnV108.Request(), ReceiverBattery) { TimeoutMs = 600 }])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "bytech" },
    ];
}
