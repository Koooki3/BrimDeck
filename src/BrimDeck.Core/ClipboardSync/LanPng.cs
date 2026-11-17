using System.Buffers.Binary;
using System.IO.Compression;

namespace BrimDeck.Core.ClipboardSync;

public sealed record LanPng(byte[] Bytes, int Width, int Height)
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public const int MaxSide = 8192;
    public const int MaxPixels = 16_777_216;
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = MakeCrcTable();

    public static bool Fits(int width, int height, int bytes) =>
        width is > 0 and <= MaxSide && height is > 0 and <= MaxSide &&
        (long)width * height <= MaxPixels && bytes is > 0 and <= MaxBytes;

    public static LanPng Validate(byte[] bytes, CancellationToken cancellation = default)
    {
        if (bytes.Length > MaxBytes || !bytes.AsSpan().StartsWith(Signature))
            throw new InvalidDataException("Invalid PNG signature or size.");
        using var sanitized = new MemoryStream();
        using var compressed = new MemoryStream();
        sanitized.Write(Signature);
        int position = Signature.Length, width = 0, height = 0, rowBytes = 0;
        int color = -1, bitDepth = 0, palette = 0, chunkCount = 0;
        bool hasTransparency = false, afterData = false;
        while (position < bytes.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            var remaining = bytes.AsSpan(position);
            if (++chunkCount > 4096 || remaining.Length < 12)
                throw new InvalidDataException("Invalid PNG chunk.");
            uint size = BinaryPrimitives.ReadUInt32BigEndian(remaining);
            if (size > remaining.Length - 12)
                throw new InvalidDataException("Truncated PNG chunk.");
            int length = (int)size;
            var type = remaining.Slice(4, 4);
            var data = remaining.Slice(8, length);
            var chunk = remaining[..(length + 12)];
            if (Crc(remaining.Slice(4, 4 + length)) != BinaryPrimitives.ReadUInt32BigEndian(chunk[^4..]))
                throw new InvalidDataException("Invalid PNG checksum.");
            if (width == 0 && !type.SequenceEqual("IHDR"u8))
                throw new InvalidDataException("PNG is missing its header.");
            bool keep = true;

            if (type.SequenceEqual("IHDR"u8))
            {
                if (width != 0 || length != 13) throw new InvalidDataException("Invalid PNG header.");
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                bitDepth = data[8];
                color = data[9];
                if (!Fits(width, height, bytes.Length) || data[10] != 0 || data[11] != 0 || data[12] != 0)
                    throw new InvalidDataException("Unsupported PNG size or encoding.");
                int channels = color switch
                {
                    0 or 3 when bitDepth is 1 or 2 or 4 or 8 => 1,
                    2 when bitDepth == 8 => 3,
                    4 when bitDepth == 8 => 2,
                    6 when bitDepth == 8 => 4,
                    _ => throw new InvalidDataException("Unsupported PNG pixel format.")
                };
                rowBytes = 1 + (width * channels * bitDepth + 7) / 8;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (compressed.Length != 0 || palette != 0 || color is 0 or 4 || length is < 3 or > 768 ||
                    length % 3 != 0 || color == 3 && length / 3 > 1 << bitDepth)
                    throw new InvalidDataException("Invalid PNG palette.");
                palette = length / 3;
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                bool valid = color switch { 0 => length == 2, 2 => length == 6, 3 => length > 0 && length <= palette, _ => false };
                if (compressed.Length != 0 || hasTransparency || !valid) throw new InvalidDataException("Invalid PNG transparency.");
                hasTransparency = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (afterData || color == 3 && palette == 0) throw new InvalidDataException("Invalid PNG image data.");
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || position + chunk.Length != bytes.Length || compressed.Length == 0)
                    throw new InvalidDataException("Invalid PNG ending.");
                ValidateRows(compressed, height, rowBytes, cancellation);
                sanitized.Write(chunk);
                return new(sanitized.ToArray(), width, height);
            }
            else
            {
                // Exclude other image formats, animation and metadata (including compressed profiles and text).
                if ((type[0] & 32) == 0 || type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8))
                    throw new InvalidDataException("Unsupported PNG chunk.");
                keep = false;
            }
            position += chunk.Length;
            if (keep) sanitized.Write(chunk);
            if (compressed.Length != 0 && !type.SequenceEqual("IDAT"u8)) afterData = true;
        }
        throw new InvalidDataException("Truncated PNG.");
    }

    private static void ValidateRows(MemoryStream compressed, int height, int rowBytes, CancellationToken cancellation)
    {
        compressed.Position = 0;
        using var decoded = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        var row = new byte[rowBytes];
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            decoded.ReadExactly(row);
            if (row[0] > 4) throw new InvalidDataException("Invalid PNG row filter.");
        }
        if (decoded.ReadByte() != -1) throw new InvalidDataException("PNG expands beyond its declared dimensions.");
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) crc = CrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            uint crc = value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320U;
            table[value] = crc;
        }
        return table;
    }
}
