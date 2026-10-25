using System.Buffers.Binary;
using System.Text;

namespace BrimDeck.Protocol.DBus;

// Reads a message larger than a connection holds (C-L6). Such a message is nearly always a notification with an image hint
// ("image-data": raw pixels, some 4 MB at 1024x1024), which the agent never uses. Each byte array longer than LargeArray is
// skipped on the wire except for its length's remainder modulo 8, so every later value keeps its alignment and what is left
// decodes as usual: the notice is kept, without the image. When even that is larger than the limit, or the body is malformed,
// only the header comes back, so that a call can still be answered with an error; the stream is past the message either way.
public static class DBusOversized
{
    public const int LargeArray = 64 * 1024;

    // start is the message's first 16 bytes, already read; total its whole length (DBusMessage.TotalLength).
    public static Task<(byte[]? Message, DBusMessage? Header)> ReadAsync(Stream stream, byte[] start, int total, int limit, CancellationToken cancellation = default)
        => new Slimmer(stream, start, total, limit, cancellation).ReadAsync();

    private sealed class Slimmer(Stream stream, byte[] start, int total, int limit, CancellationToken cancellation)
    {
        // The specification's nesting limit: 32 arrays and 32 structs.
        private const int MaxDepth = 64;
        private readonly bool _little = start[0] == (byte)'l';
        private readonly MemoryStream _out = new();
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _bufferStart, _bufferEnd, _depth;
        // How much of the message has been read from the stream; never more than total.
        private long _read = start.Length;
        // Where in the original message the next value starts.
        private long Position => _read - (_bufferEnd - _bufferStart);

        public async Task<(byte[]?, DBusMessage?)> ReadAsync()
        {
            DBusMessage? header = null;
            try
            {
                Emit(start);
                long headerEnd = (16L + U32(start.AsSpan(12)) + 7) & ~7L;
                await CopyAsync(headerEnd - start.Length).ConfigureAwait(false);
                header = DBusMessage.DecodeHeader(_out.ToArray());
                foreach (var type in DBusSignatures.Split(header.Signature)) await WalkAsync(type).ConfigureAwait(false);
                if (Position != total) throw new InvalidDataException("The body does not fill its length.");
                var data = _out.ToArray();
                Write32(data, 4, data.Length - headerEnd);
                return (data, header);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            {
                await SkipAsync(total - Position).ConfigureAwait(false);
                return (null, header);
            }
        }

        private async ValueTask WalkAsync(string type)
        {
            if (++_depth > MaxDepth) throw new InvalidDataException("The message nests too deeply.");
            try
            {
                switch (type[0])
                {
                    case 'y': await CopyAsync(1).ConfigureAwait(false); break;
                    case 'n' or 'q': await AlignAsync(2).ConfigureAwait(false); await CopyAsync(2).ConfigureAwait(false); break;
                    case 'b' or 'i' or 'u' or 'h': await AlignAsync(4).ConfigureAwait(false); await CopyAsync(4).ConfigureAwait(false); break;
                    case 'x' or 't' or 'd': await AlignAsync(8).ConfigureAwait(false); await CopyAsync(8).ConfigureAwait(false); break;
                    case 's' or 'o':
                        await AlignAsync(4).ConfigureAwait(false);
                        await CopyAsync(U32(await TakeAsync(4).ConfigureAwait(false)) + 1L).ConfigureAwait(false);
                        break;
                    case 'g': await CopyAsync((await TakeAsync(1).ConfigureAwait(false))[0] + 1L).ConfigureAwait(false); break;
                    case 'v':
                    {
                        int length = (await TakeAsync(1).ConfigureAwait(false))[0];
                        var signature = Encoding.ASCII.GetString(await TakeAsync(length + 1).ConfigureAwait(false), 0, length);
                        if (signature.Length == 0 || DBusSignatures.CompleteTypeEnd(signature, 0) != signature.Length) throw new InvalidDataException("A variant holds other than one type.");
                        await WalkAsync(signature).ConfigureAwait(false);
                        break;
                    }
                    case '(':
                        if (type.Length < 3 || type[^1] != ')') throw new InvalidDataException("An empty or open struct.");
                        await AlignAsync(8).ConfigureAwait(false);
                        foreach (var field in DBusSignatures.Split(type[1..^1])) await WalkAsync(field).ConfigureAwait(false);
                        break;
                    case 'a': await ArrayAsync(type[1..]).ConfigureAwait(false); break;
                    default: throw new InvalidDataException("Unsupported type " + type + ".");
                }
            }
            finally { _depth--; }
        }

        private async ValueTask ArrayAsync(string element)
        {
            await AlignAsync(4).ConfigureAwait(false);
            long lengthAt = _out.Length;
            long length = U32(await TakeAsync(4).ConfigureAwait(false));
            await AlignAsync(DBusSignatures.Alignment(element[0])).ConfigureAwait(false);
            long end = Position + length;
            if (end > total) throw new InvalidDataException("An array runs past the message.");
            if (element == "y")
            {
                if (length <= LargeArray) { await CopyAsync(length).ConfigureAwait(false); return; }
                long kept = length % 8;
                await CopyAsync(kept).ConfigureAwait(false);
                await SkipAsync(length - kept).ConfigureAwait(false);
                Write32(_out.GetBuffer(), lengthAt, kept);
                return;
            }
            long contentAt = _out.Length;
            if (element[0] == '{')
            {
                if (element.Length < 4 || element[^1] != '}') throw new InvalidDataException("A malformed dictionary entry.");
                var parts = DBusSignatures.Split(element[1..^1]).ToList();
                if (parts.Count != 2) throw new InvalidDataException("A malformed dictionary entry.");
                while (Position < end)
                {
                    await AlignAsync(8).ConfigureAwait(false);
                    await WalkAsync(parts[0]).ConfigureAwait(false);
                    await WalkAsync(parts[1]).ConfigureAwait(false);
                }
            }
            else while (Position < end) await WalkAsync(element).ConfigureAwait(false);
            if (Position != end) throw new InvalidDataException("An array's elements do not fill its length.");
            // A large array skipped inside shortened this one by a multiple of 8.
            if (_out.Length - contentAt != length) Write32(_out.GetBuffer(), lengthAt, _out.Length - contentAt);
        }

        private ValueTask AlignAsync(int alignment) => CopyAsync((alignment - Position % alignment) % alignment);

        // The next count bytes, also kept in the output.
        private async ValueTask<byte[]> TakeAsync(int count)
        {
            if (Position + count > total) throw new InvalidDataException("The message is cut off.");
            var bytes = new byte[count];
            for (int at = 0; at < count;)
            {
                await FillAsync().ConfigureAwait(false);
                int n = Math.Min(count - at, _bufferEnd - _bufferStart);
                Array.Copy(_buffer, _bufferStart, bytes, at, n);
                _bufferStart += n; at += n;
            }
            Emit(bytes);
            return bytes;
        }

        private async ValueTask CopyAsync(long count)
        {
            if (Position + count > total) throw new InvalidDataException("The message is cut off.");
            if (_out.Length + count > limit) throw new InvalidDataException("The message is too large even without its large byte arrays.");
            while (count > 0)
            {
                await FillAsync().ConfigureAwait(false);
                int n = (int)Math.Min(count, _bufferEnd - _bufferStart);
                _out.Write(_buffer, _bufferStart, n);
                _bufferStart += n; count -= n;
            }
        }

        private async ValueTask SkipAsync(long count)
        {
            while (count > 0)
            {
                await FillAsync().ConfigureAwait(false);
                int n = (int)Math.Min(count, _bufferEnd - _bufferStart);
                _bufferStart += n; count -= n;
            }
        }

        private async ValueTask FillAsync()
        {
            if (_bufferStart < _bufferEnd) return;
            int want = (int)Math.Min(_buffer.Length, total - _read);
            if (want <= 0) throw new InvalidDataException("The message is cut off.");
            int got = await stream.ReadAsync(_buffer.AsMemory(0, want), cancellation).ConfigureAwait(false);
            if (got == 0) throw new EndOfStreamException();
            _bufferStart = 0; _bufferEnd = got; _read += got;
        }

        private void Emit(ReadOnlySpan<byte> bytes)
        {
            if (_out.Length + bytes.Length > limit) throw new InvalidDataException("The message is too large even without its large byte arrays.");
            _out.Write(bytes);
        }

        private uint U32(ReadOnlySpan<byte> bytes) => _little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

        private void Write32(byte[] data, long at, long value)
        {
            var span = data.AsSpan((int)at, 4);
            if (_little) BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)value);
            else BinaryPrimitives.WriteUInt32BigEndian(span, (uint)value);
        }
    }
}
