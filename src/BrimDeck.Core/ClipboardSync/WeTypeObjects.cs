using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BrimDeck.Core.ClipboardSync;

// Pictures and long text travel as objects on Tencent Cloud COS; the clipboard message carries only a reference to them.
// The picture's reference is JSON with the object's address, the MD5 of the picture and the key it is encrypted with
// (AES-128 in counter mode, the key also being the first counter block). Format checked against mkdir700/wx-ime-sdk.
public static class WeTypeObjects
{
    public static Uri CheckUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length != 0 || url.Port != 443 ||
            !url.Host.EndsWith(".myqcloud.com", StringComparison.OrdinalIgnoreCase) || url.Fragment.Length != 0)
            throw new InvalidDataException("Unexpected object address.");
        return url;
    }

    public static byte[] Crypt(byte[] key, byte[] bytes) => Counter(key, key, bytes);

    internal static byte[] Counter(byte[] key, byte[] first, byte[] bytes)
    {
        if (key.Length != 16 || first.Length != 16) throw new ArgumentException("A 16-byte key is required.");
        using var aes = Aes.Create(); aes.Key = key;
        var counter = (byte[])first.Clone();
        var output = new byte[bytes.Length];
        var blocks = new byte[Math.Min(bytes.Length + 15 & ~15, 64 * 1024)];
        for (int at = 0; at < bytes.Length; at += blocks.Length)
        {
            int count = Math.Min(blocks.Length, bytes.Length - at), used = count + 15 & ~15;
            for (int block = 0; block < used; block += 16)
            {
                counter.CopyTo(blocks, block);
                for (int i = 15; i >= 0 && ++counter[i] == 0; i--) { }
            }
            var stream = aes.EncryptEcb(blocks.AsSpan(0, used), PaddingMode.None);
            for (int i = 0; i < count; i++) output[at + i] = (byte)(bytes[at + i] ^ stream[i]);
        }
        return output;
    }

    public static byte[] ImageReference(string url, byte[] picture, byte[] key)
    {
        using var output = new MemoryStream();
        // The address keeps its "&" as written: the reference is read by the official applications.
        using (var json = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("version", 1);
            json.WriteString("path", url);
            json.WriteString("md5", Convert.ToHexStringLower(MD5.HashData(picture)));
            json.WriteString("key", Convert.ToBase64String(key));
            json.WriteEndObject();
        }
        return output.ToArray();
    }

    public static (Uri Url, string Md5, byte[] Key) ParseImageReference(byte[] reference)
    {
        try
        {
            using var json = JsonDocument.Parse(reference);
            var root = json.RootElement;
            string md5 = root.GetProperty("md5").GetString() ?? "";
            byte[] key = Convert.FromBase64String(root.GetProperty("key").GetString() ?? "");
            if (root.GetProperty("version").GetInt32() != 1 || md5.Length != 32 || !md5.All(char.IsAsciiHexDigit) || key.Length != 16)
                throw new InvalidDataException("Unsupported image reference.");
            return (CheckUrl(root.GetProperty("path").GetString() ?? ""), md5, key);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Invalid image reference.", ex); }
    }

    public static byte[] OpenImage(byte[] encrypted, string md5, byte[] key)
    {
        var picture = Crypt(key, encrypted);
        if (!Convert.ToHexString(MD5.HashData(picture)).Equals(md5, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The image does not match its checksum.");
        return picture;
    }
}
