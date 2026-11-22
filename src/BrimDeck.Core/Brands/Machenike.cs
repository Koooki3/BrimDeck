namespace BrimDeck.Core;

// Machenike (机械师) mice whose platform another brand's reader already speaks, on chip makers' shared vendor ids, so only
// the pairs Machenike's own drivers list (machenike.com, 2026-11-21; not measured by us). The rest of Machenike's mice are
// read where their platform lives: the M7 Pro and M721 on Shinetek (Shinetek), on 248A:5B2E and 5B2F (Dongles' Inphic read, with the
// M720 and M840), the M7, M721 and M722 on 258A:002E and 002F (RedragonMice), the L7 Air 4K on Compx (Dongles.CompxOfficial),
// the L7 Mix 8K on Realtek's 0xFFC1 (KeychronMice.Partners), the L9 X1 on EWEADN's ids (EweadnMice), and the NearLink models
// on 369B (MetapandaMice's other products).
// - Telink, Keychron Launcher's "1k" identity read (DarmosharkMice.Identity): feature report 0x51 set as 51 06 (21 bytes) and
//   got back, the percent at 11 and the power state at 12. The L7 Mix on its cable 248A:FF4E (interface 2) and receiver FF4C
//   (interface 1) on usage page 0x8C usage 1, and the L8 Pro 2K on its cable FFE1 and receiver FFE2 (interface 3) on usage
//   page 0xFF01 usage 1: the drivers MKM_App_L7Mix and MKM_App_L8PRO.2K check those pages and send 51 06. On the receivers
//   the bond read 51 03 goes first, as for Darmoshark's on the same platform (DarmosharkMice: measured there, the receiver
//   keeps its last answer in the buffer and never gets a fresh identity while the mouse is off, so without it a mouse that
//   is off would show its last level for good); the bond answer still there with link 0 is the mouse asleep.
// - Evision's 320F, VGN's keyboard frame (Dongles.VgnRequest): the G7 on 320F:22A4, usage page 0xFF1C usage 0x92, output
//   report 4 (64 bytes) 00 00 1A 06, the percent at 8 of the answer (report id counted), from the G7's web driver
//   (machenike.com/G7). The driver reads no charging state there, so none is taken. It sends 00 00 19 18 before each read,
//   whose purpose is not known; it is not sent. The G7's 320F:226F needs a slot found by another query first and is not read.
[BrandFamilies]
public static class MachenikeMice
{
    public const int TelinkVendor = 0x248A, EvisionVendor = 0x320F;
    public const int L7MixCable = 0xFF4E, L7MixReceiver = 0xFF4C, L8ProCable = 0xFFE1, L8ProReceiver = 0xFFE2, G7 = 0x22A4;

    private static bool Telink(HidShape shape) => shape.Vendor == TelinkVendor && shape.FeatureLength >= 21 && shape.Product switch
    {
        L7MixCable or L7MixReceiver => MouseShapes.Collection(shape, 0x008C, 0x01),
        L8ProCable or L8ProReceiver => MouseShapes.Collection(shape, 0xFF01, 0x01),
        _ => false
    };
    private static bool Evision(HidShape shape) => shape.Vendor == EvisionVendor && shape.Product == G7
        && MouseShapes.Collection(shape, 0xFF1C, 0x92) && shape.OutputLength >= 64;

    // The G7's answer: the level alone.
    public static BatteryRead? G7Battery(byte[] r) => Dongles.VgnBattery(r) is { Percent: > 0 and var level } ? new(level) : null;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Machenike mouse", shape => Telink(shape) || Evision(shape), shape => Telink(shape)
            ? [shape.Product is L7MixReceiver or L8ProReceiver
                ? new BrandWay("machenike.identity", WayTransport.Feature, DarmosharkMice.IdentityRequest(), DarmosharkMice.ReceiverIdentity)
                    { Before = [DarmosharkMice.BondRequest()], GapMs = 120, FeatureLength = 21 }
                : new BrandWay("machenike.identity", WayTransport.Feature, DarmosharkMice.IdentityRequest(), DarmosharkMice.Identity) { GapMs = 120, FeatureLength = 21 }]
            : [new BrandWay("machenike.g7", WayTransport.Output, Dongles.VgnRequest(), G7Battery) { TimeoutMs = 500 }])
        {
            Kind = _ => DeviceKind.Mouse, Prefix = "machenike",
            DisplayName = shape => shape.Product switch
            {
                L7MixCable or L7MixReceiver => "Machenike L7 Mix", L8ProCable or L8ProReceiver => "Machenike L8 Pro", G7 => "Machenike G7", _ => null
            },
        },
    ];
}
