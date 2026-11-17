namespace BrimDeck.Core;

// The 2.4 GHz dongle of ZhuAudio's (竹林鸟) Z HUB headsets, 3654:5055 (the 烛龙 ZG4; a Jieli design, the same vendor id as the
// DARKRIM SHADOW's 6901, so only this product). From the brand's own web driver, Z HUB (drive.zhuaudio.cn/web, a Flutter
// build that also carries DAILING, PulseFire and DEZE headsets' names and asks WebHID for this pair alone); not measured by us.
// - The driver talks on a vendor collection's output report 0xCD (it tries every collection with an output report, vendor
//   pages first, and report 0xCD first), 63 bytes after the id; so only a vendor collection declaring 0xCD is asked.
// - To the dongle: <length of what follows> AA 5A CC <CRC low> <CRC high> <n> <command> <data>, n counting the command and
//   its data, the CRC-16/ARC (reflected 0xA001, from 0) over n, the command and the data. Command 2, no data, is the probe the
//   driver sends to every candidate report on connecting, and asks whether the headset is linked: the answer's first data
//   byte is 1 when it is, anything else when not (the driver then shows the headset gone). The gate.
// - To the headset, through the dongle: the frame FF <sequence> <n> <command> <data> AA, sent in packets EE <index, 0x80 on
//   the last> 00 <frame length, low byte first> <up to 58 bytes of it>, nothing before the EE. The headset's battery is its
//   property 7, which the driver reads only through the property query, command 0xFE with the property ids as its data (it
//   asks 5, 7, 4, 6, 36, 27, 34, 44, 28, 45, 38, 39, 31 at once; here 7 alone). The commands 2 to 7 without data the driver
//   sends on connecting are the dongle's (7 its name), not the headset's, and the headset's commands with data, whose
//   numbers are its property ids, set them: neither is sent. The answer FE 07 <value> <two more bytes at least>, or the
//   headset's own notice of command 7 with the same data: the percent in the low 7 bits of the value, bit 7 while charging.
// - The driver takes the report id off every input report; then the dongle's answers start AA CC (or one byte later):
//   AA CC _ <p> <CRC low> <CRC high> <command> <p − 1 bytes of data>, the CRC over the command and the data (the driver also
//   takes it over one byte more); the headset's start EE (or EA): the packet as sent, holding frames FF <sequence> <n>
//   <command> <data> AA, a frame of command 0xFC an acknowledgement. Neither the dongle's leading byte nor the sequence is
//   checked here: a frame counts by its command, its length and its closing AA (the dongle's by its CRC).
// The installed Z HUB's process name is not known (its installer was not read), so the brand does not stand aside for it.
//
// ZhuAudio's C-Media headsets (the 朱雀 ZG3's driver, "ZHUAUDIO HUB (CM002X1)" 1.1.15, C-Media's XearONE rebranded; read from
// its installer without running it, not measured by us). Its INF takes the audio interface of 0D8C:0334, 20F8:A962, A963,
// A964, 046A:01ED, 0E6A:0110 and 4C4A:4255 (which of them is the ZG3's dongle it does not say), so only these pairs. XearONE
// (BatteryManager, DriverInstance) writes 55 65 01 in a 64-byte output report to the device's collection that declares report
// 0x55, and takes 55 65 <percent> <state> from its input reports, the state 2 on battery, 3 charging, 4 full, 0x1A the headset
// not linked to the dongle, 0x20 a device without the battery; the same frame as the ATK N9 V2's and the MCHOSE V9 Pro's
// C-Media headsets. Only a vendor collection is asked (XearONE does not look at the page; the C-Media headsets read so far
// carry the report on 0xFF90). ZG2 2.0's driver (Xii's XiiSound Kit) reads no battery, so it is not read.
[BrandFamilies]
public static class Zhuaudio
{
    public const int Vendor = 0x3654, ZhubDongle = 0x5055;
    public const byte Report = 0xCD, LinkCommand = 0x02, PropertyQuery = 0xFE, BatteryProperty = 0x07;

    public static ushort Crc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (ushort)(crc >> 1 ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    // A question to the dongle (no data).
    public static byte[] DongleRequest(byte command)
    {
        ushort crc = Crc([0x01, command]);
        return BrandFamilies.Frame(64, Report, 0x07, 0xAA, 0x5A, 0xCC, (byte)crc, (byte)(crc >> 8), 0x01, command);
    }

    // The headset's property query for one property, in one packet.
    public static byte[] PropertyRequest(byte property)
        => BrandFamilies.Frame(64, Report, 0xEE, 0x80, 0x00, 0x06, 0x00, 0xFF, 0x01, 0x02, PropertyQuery, property, 0xAA);

    // The dongle's answer to command, its data; null for any other report. r has its report id first.
    public static byte[]? DongleAnswer(byte[] r, byte command)
    {
        for (int at = 1; at <= 3 && at + 7 <= r.Length; at++)
        {
            if (r[at] != 0xAA || r[at + 1] != 0xCC) continue;
            int p = r[at + 3];
            if (p is < 1 or > 57 || at + 6 + p > r.Length || r[at + 6] != command) continue;
            ushort crc = (ushort)(r[at + 4] | r[at + 5] << 8);
            bool sound = Crc(r.AsSpan(at + 6, p)) == crc || at + 7 + p <= r.Length && Crc(r.AsSpan(at + 6, p + 1)) == crc;
            if (sound) return r.AsSpan(at + 7, p - 1).ToArray();
        }
        return null;
    }

    public static bool? Linked(byte[] r) => DongleAnswer(r, LinkCommand) is { Length: > 0 } data ? data[0] == 1 : null;

    // The headset's answer to command in a single-packet report, its data; null for any other report.
    public static byte[]? HeadsetAnswer(byte[] r, byte command, int? first = null)
    {
        for (int at = 1; at <= 3 && at + 5 <= r.Length; at++)
        {
            if (r[at] is not (0xEE or 0xEA) || r[at + 1] != 0x80) continue;
            int length = r[at + 3] | r[at + 4] << 8, start = at + 5, end = start + length;
            if (length < 5 || length > 58 || end > r.Length) continue;
            for (int f = start; f + 4 < end;)
            {
                if (r[f] != 0xFF) { f++; continue; }
                int n = r[f + 2], close = f + 3 + n;
                if (n < 1 || close >= end || r[close] != 0xAA) { f++; continue; }
                if (r[f + 3] == command && (first is null || n >= 2 && r[f + 4] == first)) return r.AsSpan(f + 4, n - 1).ToArray();
                f = close + 1;
            }
        }
        return null;
    }

    public static BatteryRead? Battery(byte[] r)
    {
        // The property query's answer (FE 07 <data>), or the headset's own battery notice (command 7, the same data).
        var data = HeadsetAnswer(r, PropertyQuery, BatteryProperty) is { } property ? property[1..] : HeadsetAnswer(r, BatteryProperty);
        if (data is not { Length: >= 3 }) return null;
        int percent = data[0] & 0x7F;
        // The driver shows 0 as 0 %; it is not taken here (no source says when a headset reports it).
        return percent is > 0 and <= 100 ? new(percent, (data[0] & 0x80) != 0) : null;
    }

    public static readonly (int Vendor, int Product)[] CmediaHeadsets =
        [(0x0D8C, 0x0334), (0x20F8, 0xA962), (0x20F8, 0xA963), (0x20F8, 0xA964), (0x046A, 0x01ED), (0x0E6A, 0x0110), (0x4C4A, 0x4255)];
    public static byte[] CmediaRequest() => BrandFamilies.Frame(64, 0x55, 0x65, 0x01);
    public static BatteryRead? CmediaBattery(byte[] r)
    {
        if (r.Length < 4 || r[0] != 0x55 || r[1] != 0x65) return null;
        int level = r[2], state = r[3];
        if (state == 0x1A) return new(null, Asleep: true);
        if (state is not (0x02 or 0x03 or 0x04) || level is 0 or > 100) return null;
        return new(level, state == 0x03);
    }

    private static bool TakesCmedia(HidShape s) => CmediaHeadsets.Contains((s.Vendor, s.Product)) && HeadsetPages.Vendor(s) && s.OutputIds.Contains(0x55)
        && s.OutputLength >= 64 && s.InputLength >= 4;

    private static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product == ZhubDongle && HeadsetPages.Vendor(s) && s.OutputIds.Contains(Report)
        && s.OutputLength >= 64 && s.InputLength >= 16;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("ZhuAudio", Takes, _ =>
        [
            new BrandWay("zhuaudio.battery", WayTransport.Output, PropertyRequest(BatteryProperty), Battery)
            {
                Gate = DongleRequest(LinkCommand), Gated = Linked, TimeoutMs = 1000,
            },
        ])
        { Kind = _ => DeviceKind.Headphones, Prefix = "zhuaudio" },
        new("ZhuAudio C-Media", TakesCmedia, _ => [new BrandWay("zhuaudio.cmedia", WayTransport.Output, CmediaRequest(), CmediaBattery) { TimeoutMs = 800 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "zhuaudio" },
    ];
}
