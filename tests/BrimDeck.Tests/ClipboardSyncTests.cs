using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrimDeck.Core.ClipboardSync;

static class ClipboardSyncTests
{
    public static void Run(Action<string, bool> check)
    {
        // A captured protocol timestamp, independent from this encoder.
        check("WeType timestamp protobuf fixture", WeTypeWire.Parse(Convert.FromHexString("0880d095ffbc31")).GetNumber(1) == 1700000000000);
        byte[] repeated = [9, 8, (byte)'a', (byte)'b', (byte)'c', 22, 3, 0];
        check("Snappy overlapping copies", Encoding.ASCII.GetString(WeTypeWire.Unsnappy(repeated)) == "abcabcabc");
        foreach (byte[] invalid in new byte[][] { [9, 22, 0, 0], [255, 255, 255, 127], [1, 0], [1, 0, 65, 0] })
        {
            bool failed = false;
            try { WeTypeWire.Unsnappy(invalid); } catch (Exception e) when (e is InvalidDataException or OverflowException) { failed = true; }
            check("Reject malformed Snappy " + Convert.ToHexString(invalid), failed);
        }
        foreach (byte[] invalid in new byte[][] { [10, 255, 127], [8, 128], [0], [15], [8, 255, 255, 255, 255, 255, 255, 255, 255, 255, 2] })
        {
            bool failed = false;
            try { WeTypeWire.Parse(invalid); } catch (Exception e) when (e is InvalidDataException or OverflowException) { failed = true; }
            check("Reject malformed protobuf " + Convert.ToHexString(invalid), failed);
        }
        // AES-128 ECB first block from FIPS-197; the protocol also adds a full PKCS7 block.
        var key = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        var plain = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var encrypted = WeTypeSyncClient.Crypt(key, plain, true);
        check("AES protocol matches FIPS first block", Convert.ToHexString(encrypted[..16]) == "69C4E0D86A7B0430D8CDB78070B4C55A" && encrypted.Length == 32);
        check("AES protocol removes padding", WeTypeSyncClient.Crypt(key, encrypted, false).SequenceEqual(plain));
        string device = WeTypeIdentity.CreateDeviceCode();
        check("WeType device code checksum", device.Length == 64 && device.StartsWith("AND") && device[32..] == Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(device[..32] + "aI0wC6qT1nB3nN5r"))));
        check("Sync text uses UTF8 byte count", SyncTransport.TextBytes("测试😀").Length == 10);
        bool oversized = false;
        try { SyncTransport.TextBytes(new string('中', 100000)); } catch (InvalidDataException) { oversized = true; }
        check("Reject oversized Unicode text", oversized);
        // Real response shapes: account registration can succeed while clipboard use is denied.
        using var unavailable = JsonDocument.Parse("""[{"is_current_device":true,"sync_enabled":2,"is_support_copy":false},{"is_current_device":false,"sync_enabled":1,"is_support_copy":true}]""");
        check("Doubao login does not imply sync readiness", !DoubaoDeviceState.ParseCurrent(unavailable.RootElement).CanSync);
        using var ready = JsonDocument.Parse("""[{"is_current_device":true,"sync_enabled":1,"is_support_copy":true}]""");
        check("Doubao supported enabled current device is ready", DoubaoDeviceState.ParseCurrent(ready.RootElement).CanSync);
        foreach (string json in new[] { "[]", "{}", "[{\"is_current_device\":true}]", "[{\"is_current_device\":false,\"sync_enabled\":1,\"is_support_copy\":true}]", "[{\"is_current_device\":true},{\"is_current_device\":true}]" })
        {
            using var data = JsonDocument.Parse(json);
            bool rejected = false;
            try { DoubaoDeviceState.ParseCurrent(data.RootElement); } catch (InvalidDataException) { rejected = true; }
            check("Reject missing or ambiguous Doubao capability " + json, rejected);
        }
        check("Doubao server errors retain only numeric code", new DoubaoRequestException(900102010).Message.Contains("900102010"));
        WeTypeFileTests.Run(check);
        CloudContent(check);
        LanRoutes(check);
        LanFramesAsync(check).GetAwaiter().GetResult();
        LanImagesAsync(check).GetAwaiter().GetResult();
        LanPairingTests.RunAsync(check).GetAwaiter().GetResult();
    }

    // Pictures and rich text through the input methods' services.
    private static void CloudContent(Action<string, bool> check)
    {
        // NIST SP 800-38A F.5.1 (CTR-AES128), two blocks: the second needs the counter's carry into its last byte but one.
        var key = Convert.FromHexString("2B7E151628AED2A6ABF7158809CF4F3C");
        var counter = Convert.FromHexString("F0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF");
        var plain = Convert.FromHexString("6BC1BEE22E409F96E93D7E117393172AAE2D8A571E03AC9C9EB76FAC45AF8E51");
        check("WeType image cipher matches the AES counter-mode vector",
            Convert.ToHexString(WeTypeObjects.Counter(key, counter, plain)) == "874D6191B620E3261BEF6864990DB6CE9806F66B7970FDFF8617187BB9FFFDFF");
        byte[] picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];
        byte[] imageKey = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var sealedPicture = WeTypeObjects.Crypt(imageKey, picture);
        check("WeType image cipher keeps the length and changes the bytes", sealedPicture.Length == picture.Length && !sealedPicture.SequenceEqual(picture));
        const string address = "https://example-1250000000.cos.ap-guangzhou.myqcloud.com/a/b?sign=x&t=1";
        var reference = WeTypeObjects.ImageReference(address, picture, imageKey);
        string referenceText = Encoding.UTF8.GetString(reference);
        check("WeType image reference keeps the address as written", referenceText.Contains(address) && referenceText.StartsWith("{\"version\":1,\"path\":"));
        var (url, md5, readKey) = WeTypeObjects.ParseImageReference(reference);
        check("WeType image reference round trip", url.AbsoluteUri == address && readKey.SequenceEqual(imageKey) && WeTypeObjects.OpenImage(sealedPicture, md5, readKey).SequenceEqual(picture));
        bool mismatch = false;
        try { WeTypeObjects.OpenImage(picture, md5, readKey); } catch (InvalidDataException) { mismatch = true; }
        check("WeType rejects an image that does not match its checksum", mismatch);
        foreach (string invalid in new[] { "http://a.myqcloud.com/x", "https://myqcloud.com.example.org/x", "https://user@a.myqcloud.com/x", "https://a.myqcloud.com:8443/x", "https://a.myqcloud.com/x#y", "/x" })
        {
            bool rejected = false;
            try { WeTypeObjects.CheckUrl(invalid); } catch (InvalidDataException) { rejected = true; }
            check("WeType rejects object address " + invalid, rejected);
        }
        foreach (string invalid in new[] { "{}", "[]", "{\"version\":2,\"path\":\"" + address + "\",\"md5\":\"" + md5 + "\",\"key\":\"AAAAAAAAAAAAAAAAAAAAAA==\"}",
            "{\"version\":1,\"path\":\"" + address + "\",\"md5\":\"zz\",\"key\":\"AAAAAAAAAAAAAAAAAAAAAA==\"}", "{\"version\":1,\"path\":\"" + address + "\",\"md5\":\"" + md5 + "\",\"key\":\"AAAA\"}" })
        {
            bool rejected = false;
            try { WeTypeObjects.ParseImageReference(Encoding.UTF8.GetBytes(invalid)); } catch (InvalidDataException) { rejected = true; }
            check("WeType rejects image reference " + invalid[..Math.Min(24, invalid.Length)], rejected);
        }
        check("Sync pictures are told by their first bytes", SyncTransport.IsPicture(picture) && SyncTransport.IsPicture([0xFF, 0xD8, 0xFF, 0xE0]) && !SyncTransport.IsPicture("GIF89a"u8));

        // A phone's copy arrives as rich text: its plain form is the text itself, not compressed.
        using var rich = JsonDocument.Parse("""{"has_content":true,"payload_type":2,"rich_text_payload":{"plain_content":"手机文字\n🙂","formatted_content":"<b>x</b>","embedded_images":[]}}""");
        check("Doubao rich text gives its plain text", DoubaoSyncClient.ReadText(rich.RootElement) == "手机文字\n🙂");
        check("Doubao rich text gives its formatting", DoubaoSyncClient.ReadHtml(rich.RootElement) == "<b>x</b>");
        // The clipboard's HTML form counts its offsets in UTF-8 bytes.
        const string fragment = "<ol><li>一是</li><li>二是 😀</li></ol>";
        string wrapped = ClipboardHtml.Wrap(fragment);
        var wrappedBytes = Encoding.UTF8.GetBytes(wrapped);
        int Offset(string name) => int.Parse(wrapped.AsSpan(wrapped.IndexOf(name) + name.Length, 10));
        check("Clipboard HTML offsets point at the fragment and the document",
            Encoding.UTF8.GetString(wrappedBytes, Offset("StartFragment:"), Offset("EndFragment:") - Offset("StartFragment:")) == fragment &&
            Encoding.UTF8.GetString(wrappedBytes, Offset("StartHTML:"), Offset("EndHTML:") - Offset("StartHTML:")).StartsWith("<html>") && Offset("EndHTML:") == wrappedBytes.Length);
        check("Clipboard HTML fragment round trip", ClipboardHtml.Fragment(wrapped) == fragment);
        string unmarked = wrapped.Replace("<!--StartFragment-->", "<!--XXXXXXXXXXXXX-->").Replace("<!--EndFragment-->", "<!--XXXXXXXXXXX-->");
        check("Clipboard HTML fragment by offsets when the marks are missing", ClipboardHtml.Fragment(unmarked) == fragment && ClipboardHtml.Fragment("plain") == "");
        using var empty = JsonDocument.Parse("""{"has_content":true,"payload_type":2,"rich_text_payload":{"plain_content":""}}""");
        check("Doubao rich text without text gives nothing", DoubaoSyncClient.ReadText(empty.RootElement) is null);
        string inline = Convert.ToBase64String(picture);
        using var inlined = JsonDocument.Parse("{\"payload_type\":3,\"image_payload\":[{\"original_image_payload\":\"data:image/png;base64," + inline + "\",\"original_url\":\"https://example.org/unused\"}]}");
        check("Doubao picture inside the message", DoubaoSyncClient.ReadImage(inlined.RootElement) is { Inline: { } bytes, Url: null } && bytes.SequenceEqual(picture));
        using var fallback = JsonDocument.Parse("{\"payload_type\":3,\"image_payload\":[{\"original_image_payload\":null,\"payload\":\"" + inline + "\"}]}");
        check("Doubao picture in the fallback field", DoubaoSyncClient.ReadImage(fallback.RootElement) is { Inline: { } other } && other.SequenceEqual(picture));
        using var linked = JsonDocument.Parse("""{"payload_type":3,"image_payload":[{"original_url":"https://p0-ime-clip-private.byteimg.com/a/b.png?x=1","thumbnail_url":"https://example.org/t"}]}""");
        check("Doubao picture at an address", DoubaoSyncClient.ReadImage(linked.RootElement) is { Inline: null, Url: { Host: "p0-ime-clip-private.byteimg.com" } });
        foreach (string json in new[] { """{"payload_type":3}""", """{"image_payload":[]}""", """{"image_payload":[{"original_url":"http://example.org/a.png"}]}""", """{"image_payload":[{"uri":"tos-cn-i-x/abc"}]}""", """{"image_payload":{"original_url":"https://example.org/a"}}""" })
        {
            using var data = JsonDocument.Parse(json);
            check("Doubao picture not readable from " + json, DoubaoSyncClient.ReadImage(data.RootElement) is null);
        }
        using var message = JsonDocument.Parse(DoubaoSyncClient.ImageMessage(picture, "image/png", 4, 3));
        var sent = message.RootElement.GetProperty("content").GetProperty("images")[0];
        check("Doubao picture message", message.RootElement.GetProperty("payload_type").GetInt32() == 3 && message.RootElement.GetProperty("content").GetProperty("plain_content").GetString() == ""
            && sent.GetProperty("payload").GetBytesFromBase64().SequenceEqual(picture) && sent.GetProperty("uri").GetString() == "" && sent.GetProperty("place_holder").GetString() == ""
            && sent.GetProperty("mime_type").GetString() == "image/png" && sent.GetProperty("width").GetInt32() == 4 && sent.GetProperty("height").GetInt32() == 3
            && sent.GetProperty("file_size").GetInt32() == picture.Length);
    }

    private static async Task LanImagesAsync(Action<string, bool> check)
    {
        byte[] bytes = MakePng(2, 1, [0, 255, 0, 0, 128, 0, 255, 0, 255], metadata: true);
        var png = LanPng.Validate(bytes);
        check("LAN validates PNG dimensions and strips metadata", png.Width == 2 && png.Height == 1 && png.Bytes.Length < bytes.Length);
        using var stream = new MemoryStream();
        await LanClipboardLink.WriteTextAsync(stream, "before image", default);
        await LanClipboardLink.WriteImageAsync(stream, png, default);
        await LanClipboardLink.WriteTextAsync(stream, "after image", default);
        stream.Position = 0;
        check("LAN mixed link preserves text before an image", await LanClipboardLink.ReadContentAsync(stream, true, default) is LanClipboardContent.Text("before image"));
        long imagePosition = stream.Position;
        check("LAN mixed link preserves PNG bytes", await LanClipboardLink.ReadContentAsync(stream, true, default) is LanClipboardContent.Image(var image)
            && image.Bytes.SequenceEqual(png.Bytes));
        check("LAN mixed link still receives text after an image", await LanClipboardLink.ReadContentAsync(stream, true, default) is LanClipboardContent.Text("after image"));
        stream.Position = imagePosition;
        bool legacyRejected = false;
        try { await LanClipboardLink.ReadTextAsync(stream, default); }
        catch (InvalidDataException) { legacyRejected = true; }
        check("LAN text-only connection rejects an image frame", legacyRejected);

        byte[] badChecksum = (byte[])bytes.Clone();
        badChecksum[^1] ^= 1;
        foreach (var (name, invalid) in new[]
        {
            ("dimensions", MakePng(LanPng.MaxSide + 1, 1, [0])),
            ("pixel count", MakePng(8192, 8192, [0])),
            ("expands beyond dimensions", MakePng(1, 1, [0, 1, 2, 3, 4, 5])),
            ("invalid row filter", MakePng(1, 1, [5, 1, 2, 3, 4])),
            ("corrupt checksum", badChecksum),
            ("truncated chunk", bytes[..^1]),
            ("compressed size", new byte[LanPng.MaxBytes + 1])
        })
        {
            bool rejected = false;
            try { LanPng.Validate(invalid); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { rejected = true; }
            check("LAN rejects invalid PNG " + name, rejected);
        }
        using var oversized = new MemoryStream([0x80, 0, 0, 1]);
        bool tooLarge = false;
        try { await LanClipboardLink.ReadContentAsync(oversized, true, default); }
        catch (InvalidDataException) { tooLarge = true; }
        check("LAN rejects image length overflow before allocating", tooLarge);
        using var goodbye = new MemoryStream();
        await LanClipboardLink.WriteUnpairedAsync(goodbye, default);
        goodbye.Position = 0;
        check("LAN tells the other computer that the pairing ended", await LanClipboardLink.ReadContentAsync(goodbye, true, default) is LanClipboardContent.Unpaired);
        goodbye.Position = 0;
        bool notForText = false;
        try { await LanClipboardLink.ReadContentAsync(goodbye, false, default); }
        catch (InvalidDataException) { notForText = true; }
        check("LAN text-only connection takes the end of pairing for a broken frame", notForText);
    }

    private static byte[] MakePng(int width, int height, byte[] rows, bool metadata = false)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk("IHDR"u8, header);
        if (metadata) WriteChunk("tEXt"u8, "Comment\0synthetic"u8);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Compress, leaveOpen: true)) zlib.Write(rows);
        WriteChunk("IDAT"u8, compressed.ToArray());
        WriteChunk("IEND"u8, []);
        return output.ToArray();

        void WriteChunk(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
            output.Write(number);
            output.Write(type);
            output.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in type.ToArray().Concat(data.ToArray()))
            {
                crc ^= value;
                for (int i = 0; i < 8; i++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320;
            }
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
            output.Write(number);
        }
    }

    private static void LanRoutes(Action<string, bool> check)
    {
        var wifi = new LanInterface("Wi-Fi", IPAddress.Parse("192.168.42.6"), 24);
        var ethernet = new LanInterface("Ethernet", IPAddress.Parse("192.168.12.7"), 24);
        var peer = IPAddress.Parse("192.168.42.19");
        check("LAN selects the sole available host interface", LanNetwork.DefaultHost([wifi]) == wifi);
        check("LAN leaves the host interface unselected when several are available", LanNetwork.DefaultHost([ethernet, wifi]) is null);
        check("LAN joins on the matching interface rather than the first one", LanNetwork.RoutesTo(peer, [ethernet, wifi]).SequenceEqual([wifi.Address]));
        var wide = ethernet with { PrefixLength = 16 };
        var otherMatch = wifi with { Name = "Another adapter", Address = IPAddress.Parse("192.168.42.23") };
        check("LAN prefers the most specific subnet and retains other matching routes",
            LanNetwork.RoutesTo(peer, [wide, wifi, otherMatch]).SequenceEqual([wifi.Address, otherMatch.Address, wide.Address]));
        check("LAN does not connect across disjoint subnets", LanNetwork.RoutesTo(peer, [ethernet]).Count == 0);
        check("LAN reevaluates routes after a local address changes",
            LanNetwork.RoutesTo(peer, [wifi with { Address = IPAddress.Parse("192.168.55.6") }, otherMatch]).SequenceEqual([otherMatch.Address]));
        check("LAN rejects a zero prefix, public IPv4, IPv6 and loopback",
            LanNetwork.Eligible([wifi with { PrefixLength = 0 }, wifi with { Address = IPAddress.Parse("8.8.8.8") },
                wifi with { Address = IPAddress.IPv6Loopback }, wifi with { Address = IPAddress.Loopback }]).Count == 0);
        var narrow = wifi with { Address = IPAddress.Parse("10.0.0.130"), PrefixLength = 25 };
        check("LAN respects non-octet prefix boundaries",
            LanNetwork.IsOnSubnet(IPAddress.Parse("10.0.0.200"), narrow) && !LanNetwork.IsOnSubnet(IPAddress.Parse("10.0.0.100"), narrow));
    }

    private static async Task LanFramesAsync(Action<string, bool> check)
    {
        using var stream = new MemoryStream();
        await LanClipboardLink.KeepAliveAsync(stream, default);
        await LanClipboardLink.WriteTextAsync(stream, "文字\r\n😀", default);
        stream.Position = 0;
        check("LAN keepalive carries no clipboard text", await LanClipboardLink.ReadTextAsync(stream, default) is null);
        check("LAN preserves Unicode and line breaks", await LanClipboardLink.ReadTextAsync(stream, default) == "文字\r\n😀");
        foreach (byte[] invalid in new byte[][] { [0, 4, 0, 1], [255, 255, 255, 255], [0, 0, 0, 1, 255], [0, 0, 0, 2, 65] })
        {
            using var malformed = new MemoryStream(invalid);
            bool rejected = false;
            try { await LanClipboardLink.ReadTextAsync(malformed, default); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { rejected = true; }
            check("LAN rejects oversized, invalid UTF-8 or truncated frame " + Convert.ToHexString(invalid), rejected);
        }
        bool oversized = false;
        try { await LanClipboardLink.WriteTextAsync(stream, new string('中', 100000), default); }
        catch (InvalidDataException) { oversized = true; }
        check("LAN refuses oversized outbound text", oversized);

        check("LAN expects exactly six ASCII digits", LanClipboardLink.IsPairingCode("000024")
            && !LanClipboardLink.IsPairingCode("BD-LAN-1.invalid") && !LanClipboardLink.IsPairingCode("１２３４５６"));
    }
}
