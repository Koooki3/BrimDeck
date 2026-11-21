using System.Collections.Concurrent;

namespace BrimDeck.Core;

// GravaStar (重力星球) keyboards and mice, from the brand's own web driver GS HUB (support.gravastar.com: its K98 Pro app, its
// mouse app and its device table); not measured by us. Its Compx mice (Mercury M1, M2, X, X Air, X Pro) are Dongles.CompxOfficial's.
// - Mercury K98 Pro on its cable (372E:10E5 and 10FE): VIA's raw collection (usage page 0xFF60, usage 0x61) with the Bytech
//   library ATK and VGN keyboards use (AtkOfficial): 0x87 (getBatteryStatus) in a 63-byte packet with the check at 62, the
//   report id the first nonzero output report the collection declares, the answer 87, the level at 7 and a state at 8 whose
//   high nibble is charging. 372E is GravaStar's id here; EWEADN's beiyin boards use it on other products and page 0xFF00.
// - Mercury K98 Pro's 8K receiver (372E:106C), the same collection with 20-byte reports (the driver takes 20 whatever the
//   descriptor says): the same Bytech request, the report id in front (64 bytes), split into 14-byte pieces, each sent as
//   66 <frame count | sync bit 2 << 5> <frame number | sync bit 1 << 6> <piece length | sync bit 0 << 7> <piece> <sum of
//   all before, low byte>, zeros to 19 bytes. The sync counts every frame sent (mod 8, from 0, the first frame 1) and goes
//   on from one message to the next; each frame waits for the receiver's acknowledgement (a frame of length 0, its fifth
//   byte the sum of the four before, keyed by sync, count and number; the driver waits 100 ms, up to 10 times). The answer
//   comes the same way, each frame acknowledged with 66 <b1> <b2> <b3 & 0x80> <sum>; its pieces joined are the Bytech
//   answer. The receiver also sends 0A .. .. .. 02 <linked> (19 bytes): the keyboard's link to it. Only the
//   acknowledgements are sent besides the request; the sync is kept per collection for the run, as the driver keeps it.
// - Mercury V75 Ultra (19F5:FB8E, FB96, FB97, FB98), on 19F5 (which NuPhy shares, NuPhy's reader leaves these): the G-COME
//   frame on 0x0001 / 0x0000 with 64-byte reports (report 0): GS HUB reads the function area (55 05 00 38 38 00 00 00, 56
//   bytes from 0) and takes the level from its byte 0x20, low 7 bits, bit 7 charging; NuPhy's one-byte read of 0x0020 is
//   that byte alone (NuPhy.Request).
// - S1 (362D:FF1E), S1 Pro (362D:FF08, FF1A, its receiver FF1C) and R1 (362D:FF1B, its receiver FF1D): Keychron Launcher's
//   status command (KeychronMice: output report B3 06, answered B4 06 with the level at 20, low 7 bits, bit 7 charging),
//   on the collection that declares the reports B3 and B5 (GS HUB looks at no page; a standard collection is left). A
//   receiver is asked first which mice are linked: B5 03, answered B6 03 with a mouse at 7, 12 or 17 when one is. GS HUB
//   sends the FF1C receiver a B3 04 first, which no source says the meaning of; it is not sent (that receiver may then not
//   answer). The S1 Pro's 391D:1A23 receiver is KeychronMice's "VGN mouse".
// - Left out: the S1 Lite (19F5:B016), which GS HUB reads in two steps whose offsets could not be checked against the
//   obfuscated code (NuPhy's reader leaves it as well); the V75 Ultra's update device 19F5:AF78.
[BrandFamilies]
public static class GravaStar
{
    public const int Vendor = 0x372E, K98ProReceiver = 0x106C, GcomeVendor = 0x19F5, KcVendor = 0x362D;
    public static readonly int[] K98ProCables = [0x10E5, 0x10FE];
    public static readonly int[] V75Ultra = [0xFB8E, 0xFB96, 0xFB97, 0xFB98];
    // NuPhy's reader must not take these (19F5 is shared): the V75 Ultra (read here), the S1 Lite and the V75 Ultra's update device.
    public static readonly int[] OnGcomeVendor = [.. V75Ultra, 0xB016, 0xAF78];
    public static readonly (int Product, string Name, bool Receiver)[] KcMice =
        [(0xFF1E, "GravaStar S1", false), (0xFF08, "GravaStar S1 Pro", false), (0xFF1A, "GravaStar S1 Pro", false), (0xFF1C, "GravaStar S1 Pro", true),
         (0xFF1B, "GravaStar R1", false), (0xFF1D, "GravaStar R1", true)];

    private static bool K98ProCable(HidShape s) => s.Vendor == Vendor && K98ProCables.Contains(s.Product) && MouseShapes.Collection(s, 0xFF60, 0x61)
        && s.OutputLength >= 64;
    private static bool K98ProDongle(HidShape s) => s.Vendor == Vendor && s.Product == K98ProReceiver && MouseShapes.Collection(s, 0xFF60, 0x61)
        && s.OutputLength == 20 && s.InputLength >= 20;
    private static bool V75UltraShape(HidShape s) => s.Vendor == GcomeVendor && V75Ultra.Contains(s.Product) && s.UsagePage == 0x0001 && s.Usage == 0x0000
        && s.InputLength == 65 && s.OutputLength == 65;
    private static bool Standard(HidShape s) => s.UsagePage == 0x0001 && s.Usage != 0 || s.UsagePage == 0x000C;
    private static bool KcMouse(HidShape s) => s.Vendor == KcVendor && KcMice.Any(m => m.Product == s.Product) && !Standard(s)
        && s.OutputIds.Contains(0xB3) && s.InputLength >= 21;

    // ----- The 8K receiver's frames -----

    public const int FrameLength = 19, PieceLength = 14;
    private const byte Header = 0x66;
    public static byte Sum(ReadOnlySpan<byte> bytes) { int sum = 0; foreach (byte b in bytes) sum += b; return (byte)sum; }

    // The frames of one message (report id in front of each), the sync going on from the one given.
    public static List<byte[]> Frames(byte reportId, byte[] message, ref int sync)
    {
        int count = (message.Length + PieceLength - 1) / PieceLength;
        var frames = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            sync = (sync + 1) & 7;
            var piece = message.AsSpan(i * PieceLength, Math.Min(PieceLength, message.Length - i * PieceLength));
            var frame = new byte[1 + FrameLength];
            frame[0] = reportId;
            frame[1] = Header;
            frame[2] = (byte)(count & 0x7F | (sync & 4) << 5);
            frame[3] = (byte)((i + 1) & 0x7F | (sync & 2) << 6);
            frame[4] = (byte)(piece.Length & 0x7F | (sync & 1) << 7);
            piece.CopyTo(frame.AsSpan(5));
            frame[5 + piece.Length] = Sum(frame.AsSpan(1, 4 + piece.Length));
            frames.Add(frame);
        }
        return frames;
    }

    public readonly record struct Frame8K(int Count, int Number, int Sync, byte[] Piece, bool Ack);

    // One report (report id first) as a frame of this transport, or null.
    public static Frame8K? ParseFrame(byte[] r)
    {
        if (r.Length < 6 || r[1] != Header) return null;
        int count = r[2] & 0x7F, number = r[3] & 0x7F, length = r[4] & 0x7F, sync = (r[2] & 0x80) >> 5 | (r[3] & 0x80) >> 6 | (r[4] & 0x80) >> 7;
        if (length == 0) return r[5] == Sum(r.AsSpan(1, 4)) ? new(count, number, sync, [], true) : null;
        if (5 + length >= r.Length || length > PieceLength || number < 1 || number > count || r[5 + length] != Sum(r.AsSpan(1, 4 + length))) return null;
        return new(count, number, sync, r.AsSpan(5, length).ToArray(), false);
    }

    // The acknowledgement of a frame received (report id first).
    public static byte[] AckOf(byte reportId, byte[] r)
    {
        var ack = new byte[1 + FrameLength];
        ack[0] = reportId; ack[1] = r[1]; ack[2] = r[2]; ack[3] = r[3]; ack[4] = (byte)(r[4] & 0x80);
        ack[5] = Sum(ack.AsSpan(1, 4));
        return ack;
    }

    // The receiver's notice of its keyboard's link (19 bytes 0A .. .. .. 02 <linked>), report id first; null for anything else.
    public static bool? LinkNotice(byte[] r) => r.Length >= 7 && r[1] == 0x0A && r[5] == 0x02 ? r[6] != 0 : null;

    private static readonly ConcurrentDictionary<string, int> Syncs = new(StringComparer.OrdinalIgnoreCase);
    public const int AckWaitMs = 150, SendTries = 3, ExchangeMs = 2500, ReplyMs = 1200;

    // The Bytech battery request through the 8K receiver and its answer.
    public static BatteryRead? ReceiverBattery(IHidExchange link, byte reportId) => ReceiverBattery(link, reportId, () => Environment.TickCount64);
    public static BatteryRead? ReceiverBattery(IHidExchange link, byte reportId, Func<long> clock)
    {
        long deadline = clock() + ExchangeMs;
        int sync = Syncs.GetOrAdd(link.Key, 0);
        var frames = Frames(reportId, AtkOfficial.BytechRequest(reportId, reportId), ref sync);
        Syncs[link.Key] = sync;
        var pieces = new List<byte[]>();
        int expectedCount = 0;
        (int Sync, int Number)? last = null;
        BatteryRead? answer = null;
        bool? linked = null;

        // Takes one report: acknowledges a frame, keeps its piece, and sets answer once a whole message parses. True for an
        // acknowledgement matching the frame awaited.
        bool Take(byte[] r, Frame8K? awaited)
        {
            if (LinkNotice(r) is { } notice) { linked = notice; return false; }
            if (r.Length > 0 && r[0] != reportId || ParseFrame(r) is not { } frame) return false;
            if (frame.Ack) return awaited is { } a && frame.Sync == a.Sync && frame.Count == a.Count && frame.Number == a.Number;
            link.Write(AckOf(reportId, r));
            if (last == (frame.Sync, frame.Number) && frame.Number != 1) return false;
            last = (frame.Sync, frame.Number);
            if (frame.Number == 1) { pieces.Clear(); expectedCount = frame.Count; }
            if (frame.Count != expectedCount || frame.Number != pieces.Count + 1) return false;
            pieces.Add(frame.Piece);
            if (pieces.Count == expectedCount)
            {
                var message = pieces.SelectMany(piece => piece).ToArray();
                if (message.Length > 0 && message[0] == 0x87) message = [reportId, .. message];
                answer ??= AtkOfficial.BytechBattery(message);
                pieces.Clear();
            }
            return false;
        }

        foreach (var frame in frames)
        {
            var awaited = ParseFrame(frame)!.Value;
            bool acked = false;
            for (int attempt = 0; attempt < SendTries && !acked; attempt++)
            {
                if (clock() >= deadline || linked == false) return linked == false ? new(null, Asleep: true) : null;
                link.Write(frame);
                long ackBy = Math.Min(deadline, clock() + AckWaitMs);
                while (!acked && clock() < ackBy)
                    if (link.Read((int)(ackBy - clock())) is { } r) acked = Take(r, awaited);
            }
            if (!acked) return linked == false ? new(null, Asleep: true) : null;
        }
        // The answer within ReplyMs of the last acknowledgement: a keyboard off or out of reach does not answer (the receiver
        // still acknowledges), and the receiver does not say so unless its link changes.
        long replyBy = Math.Min(deadline, clock() + ReplyMs);
        while (answer is null && linked != false && clock() < replyBy)
            if (link.Read((int)(replyBy - clock())) is { } r) Take(r, null);
        return answer ?? (linked == false ? new(null, Asleep: true) : null);
    }

    // ----- The KC mice -----

    public static byte[] KcLinkRequest() => BrandFamilies.Frame(21, 0xB5, 0x03);
    public static bool? KcLinked(byte[] r) => r.Length >= 18 && r[0] == 0xB6 && r[1] == 0x03 ? (r[7] | r[12] | r[17]) != 0 : null;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("GravaStar keyboard", K98ProCable, s => s.OutputIds.Where(id => id != 0).Order().Take(1)
            .Select(id => new BrandWay("gravastar.bytech", WayTransport.Output, AtkOfficial.BytechRequest(id, id), AtkOfficial.BytechBattery) { TimeoutMs = 600 })
            .ToList())
        { Kind = _ => DeviceKind.Keyboard, Prefix = "gravastar", DisplayName = _ => "GravaStar Mercury K98 Pro" },
        new("GravaStar 8K receiver", K98ProDongle, s => s.OutputIds.Where(id => id != 0).Order().Take(1)
            .Select(id => new BrandWay("gravastar.bytech-8k", WayTransport.Converse, [id], _ => null) { Converse = link => ReceiverBattery(link, id) })
            .ToList())
        { Kind = _ => DeviceKind.Keyboard, Prefix = "gravastar", DisplayName = _ => "GravaStar Mercury K98 Pro" },
        new("GravaStar V75 Ultra", V75UltraShape, _ =>
        [
            new BrandWay("gravastar.gcome", WayTransport.Output, NuPhy.Request(NuPhy.Profile0BatteryOffset), r => NuPhy.Battery(r, NuPhy.Profile0BatteryOffset))
            {
                TimeoutMs = 400, Attempts = 1, StripZero = true,
            },
        ])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "gravastar", DisplayName = _ => "GravaStar Mercury V75 Ultra" },
        new("GravaStar mouse", KcMouse, s =>
        {
            bool receiver = KcMice.First(m => m.Product == s.Product).Receiver && s.OutputIds.Contains(0xB5);
            return [new BrandWay("gravastar.kc", WayTransport.Output, KeychronMice.Request(), KeychronMice.Battery)
            {
                TimeoutMs = 500, Gate = receiver ? KcLinkRequest() : null, Gated = receiver ? KcLinked : null,
            }];
        })
        { Kind = _ => DeviceKind.Mouse, Prefix = "gravastar", DisplayName = s => KcMice.First(m => m.Product == s.Product).Name },
    ];
}
