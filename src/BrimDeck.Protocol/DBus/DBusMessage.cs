using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace BrimDeck.Protocol.DBus;

// The parts of the D-Bus wire format the WSL agent needs: messages with header fields, and bodies made of the basic types,
// arrays, structs, dictionaries and variants. Unix file descriptors are not supported. See the D-Bus specification,
// "Message Protocol".
public enum DBusMessageType : byte { Invalid = 0, MethodCall = 1, MethodReturn = 2, Error = 3, Signal = 4 }

public readonly record struct ObjectPath(string Value) { public override string ToString() => Value; }
public readonly record struct DBusSignature(string Value) { public override string ToString() => Value; }
public sealed record DBusVariant(string Signature, object Value);

public sealed class DBusMessage
{
    public const byte NoReplyExpected = 1, NoAutoStart = 2;
    public DBusMessageType Type { get; set; }
    public byte Flags { get; set; }
    public uint Serial { get; set; }
    public string? Path { get; set; }
    public string? Interface { get; set; }
    public string? Member { get; set; }
    public string? ErrorName { get; set; }
    public uint? ReplySerial { get; set; }
    public string? Destination { get; set; }
    public string? Sender { get; set; }
    public string Signature { get; set; } = "";
    public object[] Body { get; set; } = [];
    // Received too large to hold even without its large byte arrays: only the header was read, and Body is empty (C-L6).
    public bool Oversized { get; set; }

    public static DBusMessage Call(string destination, string path, string @interface, string member, string signature = "", params object[] body)
        => new() { Type = DBusMessageType.MethodCall, Destination = destination, Path = path, Interface = @interface, Member = member, Signature = signature, Body = body };
    public static DBusMessage Signal(string path, string @interface, string member, string signature = "", params object[] body)
        => new() { Type = DBusMessageType.Signal, Path = path, Interface = @interface, Member = member, Signature = signature, Body = body };
    public DBusMessage ReturnFor(string signature = "", params object[] body)
        => new() { Type = DBusMessageType.MethodReturn, ReplySerial = Serial, Destination = Sender, Signature = signature, Body = body };
    public DBusMessage ErrorFor(string name, string text)
        => new() { Type = DBusMessageType.Error, ReplySerial = Serial, Destination = Sender, ErrorName = name, Signature = "s", Body = [text] };

    public byte[] Encode()
    {
        var body = new DBusWriter();
        body.WriteValues(Signature, Body);
        var bodyBytes = body.ToArray();
        var header = new DBusWriter();
        header.WriteByte((byte)'l'); header.WriteByte((byte)Type); header.WriteByte(Flags); header.WriteByte(1);
        header.WriteUInt32((uint)bodyBytes.Length); header.WriteUInt32(Serial);
        var fields = new List<object>();
        void Field(byte code, string signature, object? value) { if (value is not null) fields.Add(new object[] { code, new DBusVariant(signature, value) }); }
        Field(1, "o", Path is null ? null : new ObjectPath(Path));
        Field(2, "s", Interface); Field(3, "s", Member); Field(4, "s", ErrorName);
        Field(5, "u", ReplySerial); Field(6, "s", Destination); Field(7, "s", Sender);
        if (Signature.Length > 0) Field(8, "g", new DBusSignature(Signature));
        header.WriteValue("a(yv)", fields);
        header.Pad(8);
        var result = new byte[header.Length + bodyBytes.Length];
        header.ToArray().CopyTo(result, 0);
        bodyBytes.CopyTo(result, header.Length);
        return result;
    }

    // The length of the whole message whose first 16 bytes are given: the fixed header, the header fields padded to 8, and the body.
    public static int TotalLength(ReadOnlySpan<byte> start)
    {
        bool little = start[0] == (byte)'l';
        if (!little && start[0] != (byte)'B') throw new InvalidDataException("Unknown byte order.");
        uint body = little ? BinaryPrimitives.ReadUInt32LittleEndian(start[4..]) : BinaryPrimitives.ReadUInt32BigEndian(start[4..]);
        uint fields = little ? BinaryPrimitives.ReadUInt32LittleEndian(start[12..]) : BinaryPrimitives.ReadUInt32BigEndian(start[12..]);
        long total = 16 + fields;
        total = (total + 7) & ~7L;
        total += body;
        if (total > 128L * 1024 * 1024) throw new InvalidDataException("The message is larger than D-Bus allows.");
        return (int)total;
    }

    // Messages from other processes are untrusted: anything malformed in them is reported as InvalidDataException, never as
    // another exception that could end the reading loop.
    // A span is copied first; an array is read in place, so a message is not held twice (sec-M3).
    public static DBusMessage Decode(ReadOnlySpan<byte> data) => Decode(data.ToArray());

    public static DBusMessage Decode(byte[] data) => Decode(data, body: true);

    // The fixed header and the header fields alone (data ends after them), with an empty Body.
    public static DBusMessage DecodeHeader(byte[] data) => Decode(data, body: false);

    private static DBusMessage Decode(byte[] data, bool body)
    {
        try { return DecodeCore(data, body); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidCastException or NullReferenceException or FormatException)
        {
            throw new InvalidDataException("Malformed message.", ex);
        }
    }

    private static DBusMessage DecodeCore(byte[] data, bool body)
    {
        var reader = new DBusReader(data, data[0] == (byte)'l');
        reader.ReadByte();
        var message = new DBusMessage { Type = (DBusMessageType)reader.ReadByte(), Flags = reader.ReadByte() };
        if (reader.ReadByte() != 1) throw new InvalidDataException("Unsupported protocol version.");
        uint bodyLength = reader.ReadUInt32();
        message.Serial = reader.ReadUInt32();
        var fields = (IList)reader.ReadValue("a(yv)");
        foreach (object[] field in fields)
        {
            var value = ((DBusVariant)field[1]).Value;
            switch ((byte)field[0])
            {
                case 1: message.Path = value.ToString(); break;
                case 2: message.Interface = value as string; break;
                case 3: message.Member = value as string; break;
                case 4: message.ErrorName = value as string; break;
                case 5: message.ReplySerial = value as uint?; break;
                case 6: message.Destination = value as string; break;
                case 7: message.Sender = value as string; break;
                case 8: message.Signature = value.ToString() ?? ""; break;
            }
        }
        if (!body) return message;
        reader.Align(8);
        if (reader.Position + bodyLength > data.Length) throw new InvalidDataException("The body is cut off.");
        message.Body = message.Signature.Length == 0 ? [] : reader.ReadValues(message.Signature);
        return message;
    }
}

public static class DBusSignatures
{
    // The end of the single complete type that starts at index.
    public static int CompleteTypeEnd(string signature, int index)
    {
        if (index >= signature.Length) throw new InvalidDataException("The signature ends early.");
        char c = signature[index];
        switch (c)
        {
            case 'a': return CompleteTypeEnd(signature, index + 1);
            case '(':
            case '{':
                char close = c == '(' ? ')' : '}';
                int i = index + 1;
                while (i < signature.Length && signature[i] != close) i = CompleteTypeEnd(signature, i);
                if (i >= signature.Length) throw new InvalidDataException("Unbalanced signature.");
                return i + 1;
            case 'y' or 'b' or 'n' or 'q' or 'i' or 'u' or 'x' or 't' or 'd' or 's' or 'o' or 'g' or 'v' or 'h':
                return index + 1;
            default: throw new InvalidDataException("Unknown type code " + c + ".");
        }
    }
    public static IEnumerable<string> Split(string signature)
    {
        for (int i = 0; i < signature.Length;)
        {
            int end = CompleteTypeEnd(signature, i);
            yield return signature[i..end];
            i = end;
        }
    }
    public static int Alignment(char code) => code switch
    {
        'y' or 'g' or 'v' => 1, 'n' or 'q' => 2, 'b' or 'i' or 'u' or 's' or 'o' or 'a' or 'h' => 4, _ => 8
    };
}

public sealed class DBusWriter
{
    private readonly MemoryStream _buffer = new();
    public int Length => (int)_buffer.Length;
    public byte[] ToArray() => _buffer.ToArray();
    public void Pad(int alignment) { while (_buffer.Length % alignment != 0) _buffer.WriteByte(0); }
    public void WriteByte(byte value) => _buffer.WriteByte(value);
    public void WriteUInt32(uint value) { Pad(4); Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); _buffer.Write(b); }
    private void WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32((uint)bytes.Length); _buffer.Write(bytes); _buffer.WriteByte(0);
    }
    private void WriteSignature(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length > 255) throw new InvalidDataException("Signature too long.");
        _buffer.WriteByte((byte)bytes.Length); _buffer.Write(bytes); _buffer.WriteByte(0);
    }

    public void WriteValues(string signature, object[] values)
    {
        var types = DBusSignatures.Split(signature).ToList();
        if (types.Count != values.Length) throw new ArgumentException($"Signature {signature} has {types.Count} values; {values.Length} given.");
        for (int i = 0; i < types.Count; i++) WriteValue(types[i], values[i]);
    }

    public void WriteValue(string type, object value)
    {
        Span<byte> b = stackalloc byte[8];
        switch (type[0])
        {
            case 'y': _buffer.WriteByte(Convert.ToByte(value)); break;
            case 'b': WriteUInt32(value is true ? 1u : 0u); break;
            case 'n': Pad(2); BinaryPrimitives.WriteInt16LittleEndian(b, Convert.ToInt16(value)); _buffer.Write(b[..2]); break;
            case 'q': Pad(2); BinaryPrimitives.WriteUInt16LittleEndian(b, Convert.ToUInt16(value)); _buffer.Write(b[..2]); break;
            case 'i': Pad(4); BinaryPrimitives.WriteInt32LittleEndian(b, Convert.ToInt32(value)); _buffer.Write(b[..4]); break;
            case 'u' or 'h': WriteUInt32(Convert.ToUInt32(value)); break;
            case 'x': Pad(8); BinaryPrimitives.WriteInt64LittleEndian(b, Convert.ToInt64(value)); _buffer.Write(b); break;
            case 't': Pad(8); BinaryPrimitives.WriteUInt64LittleEndian(b, Convert.ToUInt64(value)); _buffer.Write(b); break;
            case 'd': Pad(8); BinaryPrimitives.WriteDoubleLittleEndian(b, Convert.ToDouble(value)); _buffer.Write(b); break;
            case 's': WriteString((string)value); break;
            case 'o': WriteString(value.ToString()!); break;
            case 'g': WriteSignature(value.ToString()!); break;
            case 'v':
                var variant = (DBusVariant)value;
                WriteSignature(variant.Signature); WriteValue(variant.Signature, variant.Value); break;
            case '(':
                Pad(8);
                var fields = DBusSignatures.Split(type[1..^1]).ToList();
                var items = (object[])value;
                if (items.Length != fields.Count) throw new ArgumentException("Struct " + type + " needs " + fields.Count + " fields.");
                for (int i = 0; i < fields.Count; i++) WriteValue(fields[i], items[i]);
                break;
            case 'a':
                string element = type[1..];
                WriteUInt32(0);
                long lengthAt = _buffer.Position - 4;
                Pad(DBusSignatures.Alignment(element[0]));
                long start = _buffer.Position;
                if (element[0] == '{')
                {
                    var parts = DBusSignatures.Split(element[1..^1]).ToList();
                    foreach (var (key, item) in Entries(value)) { Pad(8); WriteValue(parts[0], key); WriteValue(parts[1], item); }
                }
                else if (element == "y" && value is byte[] bytes) _buffer.Write(bytes);
                else foreach (var item in (IEnumerable)value) WriteValue(element, item!);
                long end = _buffer.Position;
                _buffer.Position = lengthAt;
                BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)(end - start)); _buffer.Write(b[..4]);
                _buffer.Position = end;
                break;
            default: throw new ArgumentException("Unsupported type " + type + ".");
        }
    }

    private static IEnumerable<(object Key, object Value)> Entries(object value)
    {
        if (value is IDictionary dictionary) { foreach (DictionaryEntry entry in dictionary) yield return (entry.Key, entry.Value!); yield break; }
        foreach (var item in (IEnumerable)value)
        {
            var pair = (object[])item!;
            yield return (pair[0], pair[1]);
        }
    }
}

public sealed class DBusReader(byte[] data, bool littleEndian)
{
    // Bodies from other processes are untrusted: nesting and the number of values in one message are bounded. Without the
    // budget, a message of small array elements ("aq", "av") would become many times its size in objects (sec-M3).
    private const int MaxDepth = 32;
    public const int MaxValues = 65536;
    public int Position { get; private set; }
    private int _depth, _values;

    public void Align(int alignment) => Position = (Position + alignment - 1) / alignment * alignment;
    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Position + count > data.Length) throw new InvalidDataException("The message is cut off.");
        var span = data.AsSpan(Position, count); Position += count; return span;
    }
    public byte ReadByte() => Take(1)[0];
    public uint ReadUInt32() { Align(4); var s = Take(4); return littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s); }
    private string ReadString()
    {
        int length = checked((int)ReadUInt32());
        var text = Encoding.UTF8.GetString(Take(length));
        Take(1);
        return text;
    }
    private string ReadSignature() { int length = ReadByte(); var text = Encoding.ASCII.GetString(Take(length)); Take(1); return text; }

    public object[] ReadValues(string signature) => DBusSignatures.Split(signature).Select(ReadValue).ToArray();

    public object ReadValue(string type)
    {
        if (++_depth > MaxDepth) throw new InvalidDataException("The message nests too deeply.");
        if (++_values > MaxValues) throw new InvalidDataException("The message holds too many values.");
        try { return ReadValueCore(type); }
        finally { _depth--; }
    }

    private object ReadValueCore(string type)
    {
        switch (type[0])
        {
            case 'y': return ReadByte();
            case 'b': return ReadUInt32() != 0;
            case 'n': { Align(2); var s = Take(2); return littleEndian ? BinaryPrimitives.ReadInt16LittleEndian(s) : BinaryPrimitives.ReadInt16BigEndian(s); }
            case 'q': { Align(2); var s = Take(2); return littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s); }
            case 'i': { Align(4); var s = Take(4); return littleEndian ? BinaryPrimitives.ReadInt32LittleEndian(s) : BinaryPrimitives.ReadInt32BigEndian(s); }
            case 'u' or 'h': return ReadUInt32();
            case 'x': { Align(8); var s = Take(8); return littleEndian ? BinaryPrimitives.ReadInt64LittleEndian(s) : BinaryPrimitives.ReadInt64BigEndian(s); }
            case 't': { Align(8); var s = Take(8); return littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(s) : BinaryPrimitives.ReadUInt64BigEndian(s); }
            case 'd': { Align(8); var s = Take(8); return littleEndian ? BinaryPrimitives.ReadDoubleLittleEndian(s) : BinaryPrimitives.ReadDoubleBigEndian(s); }
            case 's': return ReadString();
            case 'o': return new ObjectPath(ReadString());
            case 'g': return new DBusSignature(ReadSignature());
            case 'v':
                var signature = ReadSignature();
                if (DBusSignatures.CompleteTypeEnd(signature, 0) != signature.Length) throw new InvalidDataException("A variant holds more than one type.");
                return new DBusVariant(signature, ReadValue(signature));
            case '(':
            {
                if (type.Length < 3 || type[^1] != ')') throw new InvalidDataException("An empty or open struct.");
                Align(8);
                return DBusSignatures.Split(type[1..^1]).Select(ReadValue).ToArray();
            }
            case 'a':
            {
                int length = checked((int)ReadUInt32());
                string element = type[1..];
                Align(DBusSignatures.Alignment(element[0]));
                int end = Position + length;
                if (length < 0 || end > data.Length) throw new InvalidDataException("An array runs past the message.");
                if (element == "y") return Take(length).ToArray();
                if (element[0] == '{')
                {
                    var parts = DBusSignatures.Split(element[1..^1]).ToList();
                    // A dictionary entry is a basic key and one value.
                    if (parts.Count != 2 || parts[0].Length != 1 || parts[0][0] is 'a' or '(' or '{' or 'v') throw new InvalidDataException("A malformed dictionary entry.");
                    var dictionary = new Dictionary<object, object>();
                    while (Position < end) { Align(8); var key = ReadValue(parts[0]); dictionary[key] = ReadValue(parts[1]); }
                    return dictionary;
                }
                var list = new List<object>();
                while (Position < end) list.Add(ReadValue(element));
                if (Position != end) throw new InvalidDataException("An array's elements do not fill its length.");
                return list;
            }
            default: throw new InvalidDataException("Unsupported type " + type + ".");
        }
    }
}
