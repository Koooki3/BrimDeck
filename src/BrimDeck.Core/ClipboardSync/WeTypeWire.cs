using System.Buffers.Binary;
using System.Text;

namespace BrimDeck.Core.ClipboardSync;

// Only the protobuf wire types used by the WeType control protocol. Unknown fields are retained,
// lengths are bounded before slicing, and absent status fields are never treated as success.
public sealed class WeTypeWire
{
    private readonly List<(int Number, object Value)> _fields = [];
    public WeTypeWire Number(int field, ulong value) { _fields.Add((field, value)); return this; }
    public WeTypeWire Bytes(int field, byte[] value) { _fields.Add((field, value)); return this; }
    public WeTypeWire Text(int field, string value) => Bytes(field, SyncTransport.Utf8.GetBytes(value));
    public WeTypeWire Message(int field, WeTypeWire value) => Bytes(field, value.Encode());
    public bool Has(int field) => _fields.Any(f => f.Number == field);
    internal IEnumerable<int> FieldNumbers() => _fields.Select(f => f.Number).Distinct().Order();

    // The layout of a message for diagnostics: field numbers, numbers, nested messages and JSON keys. Text is shown only
    // by its length, and a 64-digit hex string as "id", so no file names or contents reach the log.
    internal static string Describe(byte[] bytes, int depth = 0)
    {
        if (bytes.Length > 0 && bytes[0] == (byte)'{') return DescribeJson(bytes);
        WeTypeWire wire;
        try { wire = Parse(bytes); }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { return $"bytes({bytes.Length})"; }
        if (wire._fields.Count == 0 || !wire.Encode().AsSpan().SequenceEqual(bytes)) return $"bytes({bytes.Length})";
        var parts = wire._fields.Select(f => f.Value switch
        {
            ulong n => $"{f.Number}={n}",
            byte[] b when b.Length == 64 && b.All(c => Uri.IsHexDigit((char)c)) => $"{f.Number}=id",
            byte[] b when depth < 4 && b.Length > 0 && Describe(b, depth + 1) is var inner && !inner.StartsWith("bytes(") => $"{f.Number}={inner}",
            byte[] b => $"{f.Number}=bytes({b.Length})",
            _ => $"{f.Number}=?"
        });
        string text = "{" + string.Join(" ", parts) + "}";
        return text.Length > 600 ? text[..600] + "…" : text;
    }

    private static string DescribeJson(byte[] bytes)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(bytes);
            static string Element(System.Text.Json.JsonElement e, int depth) => e.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Object when depth < 4 =>
                    "{" + string.Join(" ", e.EnumerateObject().Select(p => p.Name + "=" + Element(p.Value, depth + 1))) + "}",
                System.Text.Json.JsonValueKind.Array when depth < 4 =>
                    "[" + string.Join(" ", e.EnumerateArray().Take(8).Select(v => Element(v, depth + 1))) + (e.GetArrayLength() > 8 ? " …" : "") + "]",
                System.Text.Json.JsonValueKind.String => $"s({e.GetString()!.Length})",
                System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => e.GetRawText(),
                _ => e.ValueKind.ToString()
            };
            string text = "json" + Element(json.RootElement, 0);
            return text.Length > 600 ? text[..600] + "…" : text;
        }
        catch (System.Text.Json.JsonException) { return $"bytes({bytes.Length})"; }
    }
    internal bool TryGetNumber(int field, out ulong value)
    {
        if (_fields.FirstOrDefault(f => f.Number == field).Value is ulong number)
        {
            value = number;
            return true;
        }
        value = 0;
        return false;
    }
    public ulong GetNumber(int field) => TryGetNumber(field, out var value) ? value : 0;
    public byte[] GetBytes(int field) => _fields.FirstOrDefault(f => f.Number == field).Value as byte[] ?? [];
    public string GetText(int field) => SyncTransport.Utf8.GetString(GetBytes(field));
    internal IEnumerable<string> Texts(int field) => _fields.Where(f => f.Number == field && f.Value is byte[]).Select(f => SyncTransport.Utf8.GetString((byte[])f.Value));
    public WeTypeWire GetMessage(int field) => Parse(GetBytes(field));
    public IEnumerable<WeTypeWire> Messages(int field) => _fields.Where(f => f.Number == field && f.Value is byte[]).Select(f => Parse((byte[])f.Value));
    private static void WriteVar(Stream output, ulong n)
    {
        while (n >= 128) { output.WriteByte((byte)(n | 128)); n >>= 7; }
        output.WriteByte((byte)n);
    }
    private static ulong ReadVar(ReadOnlySpan<byte> input, ref int at)
    {
        ulong value = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (at >= input.Length) throw new InvalidDataException("Truncated varint.");
            byte b = input[at++];
            if (shift == 63 && b > 1) throw new InvalidDataException("Varint overflow.");
            value |= (ulong)(b & 127) << shift;
            if (b < 128) return value;
        }
        throw new InvalidDataException("Invalid varint.");
    }
    public byte[] Encode()
    {
        using var output = new MemoryStream();
        foreach (var (number, value) in _fields)
        {
            WriteVar(output, (ulong)(number * 8 + (value is byte[] ? 2 : 0)));
            if (value is byte[] bytes) { WriteVar(output, (ulong)bytes.Length); output.Write(bytes); }
            else WriteVar(output, (ulong)value);
        }
        return output.ToArray();
    }
    public static WeTypeWire Parse(ReadOnlySpan<byte> input) => Parse(input, 2 * 1024 * 1024);

    internal static WeTypeWire Parse(ReadOnlySpan<byte> input, int maxBytes)
    {
        if (input.Length > maxBytes) throw new InvalidDataException("Packet too large.");
        var result = new WeTypeWire(); int at = 0;
        while (at < input.Length)
        {
            ulong tag = ReadVar(input, ref at); int number = checked((int)(tag >> 3));
            if (number == 0) throw new InvalidDataException("Invalid field number.");
            int type = (int)(tag & 7);
            if (type == 0) { result.Number(number, ReadVar(input, ref at)); continue; }
            int length = type switch { 1 => 8, 5 => 4, 2 => checked((int)ReadVar(input, ref at)), _ => throw new InvalidDataException("Invalid wire type.") };
            if (length > input.Length - at) throw new InvalidDataException("Truncated field.");
            if (type == 2) result.Bytes(number, input.Slice(at, length).ToArray());
            at += length;
        }
        return result;
    }
    // Raw Snappy blocks (not the framed stream). Used only when the server declares compression=2.
    public static byte[] Unsnappy(ReadOnlySpan<byte> input)
    {
        int at = 0, size = checked((int)ReadVar(input, ref at)), written = 0;
        if (size > 2 * 1024 * 1024) throw new InvalidDataException("Snappy output too large.");
        var output = new byte[size];
        while (at < input.Length && written < size)
        {
            byte tag = input[at++]; int kind = tag & 3, length, offset;
            if (kind == 0)
            {
                length = tag >> 2;
                if (length >= 60)
                {
                    int n = length - 59; if (n > input.Length - at) throw new InvalidDataException();
                    uint v = 0; for (int i = 0; i < n; i++) v |= (uint)input[at++] << (8 * i);
                    length = checked((int)v);
                }
                length = checked(length + 1);
                if (length > size - written || length > input.Length - at) throw new InvalidDataException();
                input.Slice(at, length).CopyTo(output.AsSpan(written)); at += length; written += length;
                continue;
            }
            int bytes = kind == 1 ? 1 : kind == 2 ? 2 : 4;
            if (bytes > input.Length - at) throw new InvalidDataException();
            offset = kind switch { 1 => ((tag & 224) << 3) | input[at], 2 => BinaryPrimitives.ReadUInt16LittleEndian(input[at..]), _ => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(input[at..])) };
            at += bytes; length = kind == 1 ? 4 + ((tag >> 2) & 7) : 1 + (tag >> 2);
            if (offset <= 0 || offset > written || length > size - written) throw new InvalidDataException();
            for (int i = 0; i < length; i++) { output[written] = output[written - offset]; written++; }
        }
        if (at != input.Length || written != size) throw new InvalidDataException("Truncated Snappy block.");
        return output;
    }
}
