using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace BrimDeck.Core.ClipboardSync;

// Clean-room wire crypto and packet framing for the WeType WXP2P relay protocol.
internal static class WeTypeRelayCrypto
{
    internal const byte ControlMagicCrc = 0xD5;
    internal const byte TcpFrameMagic = 0xD8;
    internal const byte DataMagicLiveCloud96 = 0x96;
    internal const byte DataMagicLiveCloud98 = 0x98;
    internal const byte DataMagicChaChaNew97 = 0x97;
    internal const byte DataMagicChaChaV3 = 0x75;
    internal const byte DataTypeMpRudp = 0xD9;
    internal const byte LiveCloudNibbleMpRudp = 5;
    internal const byte DataTypeHello = 0x84;
    internal const byte LiveCloudNibbleHello = 7;

    internal static ReadOnlySpan<byte> ErrorNotifyKey => "xep@624!Ja?g3145"u8;

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    internal static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint s1 = 1;
        uint s2 = 0;
        const uint Mod = 65521;
        foreach (byte b in data)
        {
            s1 += b;
            if (s1 >= Mod) s1 -= Mod;
            s2 += s1;
            if (s2 >= Mod) s2 -= Mod;
        }
        return (s2 << 16) | s1;
    }

    internal static byte[] DeriveControlKey(ulong roomKey)
    {
        Span<byte> raw = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(raw, roomKey);
        return MD5.HashData(raw);
    }

    // 16-round OICQ TEA with bi-CBC chaining (big-endian 32-bit words).
    internal static byte[] TeaEncrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key)
    {
        if (key.Length != 16) throw new ArgumentException("TEA key must be 16 bytes.", nameof(key));
        int pad = (plain.Length + 10) % 8;
        if (pad != 0) pad = 8 - pad;
        int totalLen = 1 + pad + 2 + plain.Length + 7;
        byte[] padded = new byte[totalLen];
        Span<byte> rand = stackalloc byte[1 + pad + 2];
        RandomNumberGenerator.Fill(rand);
        padded[0] = (byte)((rand[0] & 0xF8) | (pad & 0x07));
        rand[1..].CopyTo(padded.AsSpan(1));
        plain.CopyTo(padded.AsSpan(1 + pad + 2));
        // Trailing 7 bytes remain zero.

        uint k0 = BinaryPrimitives.ReadUInt32BigEndian(key[0..4]);
        uint k1 = BinaryPrimitives.ReadUInt32BigEndian(key[4..8]);
        uint k2 = BinaryPrimitives.ReadUInt32BigEndian(key[8..12]);
        uint k3 = BinaryPrimitives.ReadUInt32BigEndian(key[12..16]);

        byte[] output = new byte[totalLen];
        ulong prevCipher = 0;
        ulong prevPlainXor = 0;
        for (int offset = 0; offset < totalLen; offset += 8)
        {
            ulong block = BinaryPrimitives.ReadUInt64BigEndian(padded.AsSpan(offset, 8));
            ulong plainXor = block ^ prevCipher;
            ulong enc = TeaEncryptBlock(plainXor, k0, k1, k2, k3);
            ulong cipher = enc ^ prevPlainXor;
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(offset, 8), cipher);
            prevPlainXor = plainXor;
            prevCipher = cipher;
        }
        return output;
    }

    internal static bool TeaDecrypt(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> key, out byte[] plain)
    {
        plain = [];
        if (key.Length != 16 || cipher.Length < 16 || (cipher.Length & 7) != 0) return false;

        uint k0 = BinaryPrimitives.ReadUInt32BigEndian(key[0..4]);
        uint k1 = BinaryPrimitives.ReadUInt32BigEndian(key[4..8]);
        uint k2 = BinaryPrimitives.ReadUInt32BigEndian(key[8..12]);
        uint k3 = BinaryPrimitives.ReadUInt32BigEndian(key[12..16]);

        byte[] padded = new byte[cipher.Length];
        ulong prevCipher = 0;
        ulong prevPlainXor = 0;
        for (int offset = 0; offset < cipher.Length; offset += 8)
        {
            ulong block = BinaryPrimitives.ReadUInt64BigEndian(cipher.Slice(offset, 8));
            ulong plainXor = TeaDecryptBlock(block ^ prevPlainXor, k0, k1, k2, k3);
            ulong decrypted = plainXor ^ prevCipher;
            BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(offset, 8), decrypted);
            prevPlainXor = plainXor;
            prevCipher = block;
        }

        int pad = padded[0] & 0x07;
        int start = 1 + pad + 2;
        int end = padded.Length - 7;
        if (start > end) return false;
        for (int i = end; i < padded.Length; i++)
            if (padded[i] != 0) return false;

        plain = padded[start..end];
        return true;
    }

    private static ulong TeaEncryptBlock(ulong value, uint k0, uint k1, uint k2, uint k3)
    {
        uint y = (uint)(value >> 32);
        uint z = (uint)value;
        uint sum = 0;
        const uint Delta = 0x9E3779B9u;
        for (int i = 0; i < 16; i++)
        {
            sum += Delta;
            y += ((z << 4) + k0) ^ (z + sum) ^ ((z >> 5) + k1);
            z += ((y << 4) + k2) ^ (y + sum) ^ ((y >> 5) + k3);
        }
        return ((ulong)y << 32) | z;
    }

    private static ulong TeaDecryptBlock(ulong value, uint k0, uint k1, uint k2, uint k3)
    {
        uint y = (uint)(value >> 32);
        uint z = (uint)value;
        const uint Delta = 0x9E3779B9u;
        uint sum = unchecked(Delta * 16u);
        for (int i = 0; i < 16; i++)
        {
            z -= ((y << 4) + k2) ^ (y + sum) ^ ((y >> 5) + k3);
            y -= ((z << 4) + k0) ^ (z + sum) ^ ((z >> 5) + k1);
            sum -= Delta;
        }
        return ((ulong)y << 32) | z;
    }

    // Original libsodium ChaCha20-Poly1305 (8-byte nonce, 64-bit LE block counter, Poly1305 over aad || le64(aadLen) || ct || le64(ctLen)).
    internal static byte[] ChaCha20Poly1305OriginalEncrypt(ReadOnlySpan<byte> key32, ulong nonceLe64, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> aad = default)
    {
        if (key32.Length != 32) throw new ArgumentException("ChaCha20 key must be 32 bytes.", nameof(key32));
        byte[] output = new byte[plain.Length + 16];
        Span<byte> otkBlock = stackalloc byte[64];
        ChaCha20Block(key32, 0UL, nonceLe64, otkBlock);

        ChaCha20Xor(key32, 1UL, nonceLe64, plain, output.AsSpan(0, plain.Length));
        ComputeOriginalPoly1305Tag(otkBlock[..32], aad, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length, 16));
        CryptographicOperations.ZeroMemory(otkBlock);
        return output;
    }

    internal static bool ChaCha20Poly1305OriginalDecrypt(ReadOnlySpan<byte> key32, ulong nonceLe64, ReadOnlySpan<byte> cipherAndTag, out byte[] plain, ReadOnlySpan<byte> aad = default)
    {
        plain = [];
        if (key32.Length != 32 || cipherAndTag.Length < 16) return false;
        int ctLen = cipherAndTag.Length - 16;
        ReadOnlySpan<byte> ct = cipherAndTag[..ctLen];
        ReadOnlySpan<byte> tag = cipherAndTag[ctLen..];

        Span<byte> otkBlock = stackalloc byte[64];
        ChaCha20Block(key32, 0UL, nonceLe64, otkBlock);
        Span<byte> expected = stackalloc byte[16];
        ComputeOriginalPoly1305Tag(otkBlock[..32], aad, ct, expected);
        CryptographicOperations.ZeroMemory(otkBlock);

        if (!CryptographicOperations.FixedTimeEquals(expected, tag)) return false;
        plain = new byte[ctLen];
        ChaCha20Xor(key32, 1UL, nonceLe64, ct, plain);
        return true;
    }

    private static void ChaCha20Xor(ReadOnlySpan<byte> key32, ulong initialCounter, ulong nonceLe64, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> block = stackalloc byte[64];
        ulong counter = initialCounter;
        int offset = 0;
        while (offset < input.Length)
        {
            ChaCha20Block(key32, counter++, nonceLe64, block);
            int chunk = Math.Min(64, input.Length - offset);
            for (int i = 0; i < chunk; i++)
                output[offset + i] = (byte)(input[offset + i] ^ block[i]);
            offset += chunk;
        }
        CryptographicOperations.ZeroMemory(block);
    }

    private static void ChaCha20Block(ReadOnlySpan<byte> key32, ulong counter, ulong nonceLe64, Span<byte> output64)
    {
        uint x0 = 0x61707865u, x1 = 0x3320646Eu, x2 = 0x79622D32u, x3 = 0x6B206574u;
        uint x4 = BinaryPrimitives.ReadUInt32LittleEndian(key32[0..4]);
        uint x5 = BinaryPrimitives.ReadUInt32LittleEndian(key32[4..8]);
        uint x6 = BinaryPrimitives.ReadUInt32LittleEndian(key32[8..12]);
        uint x7 = BinaryPrimitives.ReadUInt32LittleEndian(key32[12..16]);
        uint x8 = BinaryPrimitives.ReadUInt32LittleEndian(key32[16..20]);
        uint x9 = BinaryPrimitives.ReadUInt32LittleEndian(key32[20..24]);
        uint x10 = BinaryPrimitives.ReadUInt32LittleEndian(key32[24..28]);
        uint x11 = BinaryPrimitives.ReadUInt32LittleEndian(key32[28..32]);
        uint x12 = (uint)counter;
        uint x13 = (uint)(counter >> 32);
        uint x14 = (uint)nonceLe64;
        uint x15 = (uint)(nonceLe64 >> 32);

        uint s0 = x0, s1 = x1, s2 = x2, s3 = x3, s4 = x4, s5 = x5, s6 = x6, s7 = x7;
        uint s8 = x8, s9 = x9, s10 = x10, s11 = x11, s12 = x12, s13 = x13, s14 = x14, s15 = x15;

        for (int i = 0; i < 10; i++)
        {
            QuarterRound(ref x0, ref x4, ref x8, ref x12);
            QuarterRound(ref x1, ref x5, ref x9, ref x13);
            QuarterRound(ref x2, ref x6, ref x10, ref x14);
            QuarterRound(ref x3, ref x7, ref x11, ref x15);
            QuarterRound(ref x0, ref x5, ref x10, ref x15);
            QuarterRound(ref x1, ref x6, ref x11, ref x12);
            QuarterRound(ref x2, ref x7, ref x8, ref x13);
            QuarterRound(ref x3, ref x4, ref x9, ref x14);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output64[0..4], x0 + s0);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[4..8], x1 + s1);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[8..12], x2 + s2);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[12..16], x3 + s3);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[16..20], x4 + s4);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[20..24], x5 + s5);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[24..28], x6 + s6);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[28..32], x7 + s7);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[32..36], x8 + s8);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[36..40], x9 + s9);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[40..44], x10 + s10);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[44..48], x11 + s11);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[48..52], x12 + s12);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[52..56], x13 + s13);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[56..60], x14 + s14);
        BinaryPrimitives.WriteUInt32LittleEndian(output64[60..64], x15 + s15);
    }

    private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b; d = BitOperations.RotateLeft(d ^ a, 16);
        c += d; b = BitOperations.RotateLeft(b ^ c, 12);
        a += b; d = BitOperations.RotateLeft(d ^ a, 8);
        c += d; b = BitOperations.RotateLeft(b ^ c, 7);
    }

    private static readonly BigInteger Poly1305Prime = (BigInteger.One << 130) - 5;

    private static void ComputeOriginalPoly1305Tag(ReadOnlySpan<byte> otk32, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ct, Span<byte> tag16)
    {
        // Build MAC input: aad || le64(aad.Length) || ct || le64(ct.Length) with NO 16-byte padding.
        byte[] macInput = new byte[aad.Length + 8 + ct.Length + 8];
        int pos = 0;
        if (!aad.IsEmpty) { aad.CopyTo(macInput.AsSpan(pos)); pos += aad.Length; }
        BinaryPrimitives.WriteUInt64LittleEndian(macInput.AsSpan(pos, 8), (ulong)aad.Length); pos += 8;
        if (!ct.IsEmpty) { ct.CopyTo(macInput.AsSpan(pos)); pos += ct.Length; }
        BinaryPrimitives.WriteUInt64LittleEndian(macInput.AsSpan(pos, 8), (ulong)ct.Length);

        Span<byte> rBytes = stackalloc byte[16];
        otk32[..16].CopyTo(rBytes);
        rBytes[3] &= 15; rBytes[7] &= 15; rBytes[11] &= 15; rBytes[15] &= 15;
        rBytes[4] &= 252; rBytes[8] &= 252; rBytes[12] &= 252;

        var r = new BigInteger(rBytes, isUnsigned: true, isBigEndian: false);
        var s = new BigInteger(otk32[16..32], isUnsigned: true, isBigEndian: false);
        var acc = BigInteger.Zero;

        Span<byte> chunkBuf = stackalloc byte[17];
        for (int offset = 0; offset < macInput.Length; offset += 16)
        {
            int len = Math.Min(16, macInput.Length - offset);
            chunkBuf[..len].Clear();
            macInput.AsSpan(offset, len).CopyTo(chunkBuf);
            chunkBuf[len] = 1;
            var n = new BigInteger(chunkBuf[..(len + 1)], isUnsigned: true, isBigEndian: false);
            acc = ((acc + n) * r) % Poly1305Prime;
        }

        acc += s;
        tag16.Clear();
        Span<byte> outBytes = stackalloc byte[20];
        if (acc.TryWriteBytes(outBytes, out int written, isUnsigned: true, isBigEndian: false))
            outBytes[..Math.Min(16, written)].CopyTo(tag16);
        else
            acc.ToByteArray(isUnsigned: true, isBigEndian: false).AsSpan(0, 16).CopyTo(tag16);
    }

    internal static byte[] PackRelayControlPacket(WeTypeWire c2sPkt)
    {
        byte[] proto = c2sPkt.Encode();
        byte[] framed = new byte[5 + proto.Length];
        framed[0] = ControlMagicCrc;
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(1, 4), Crc32(proto));
        proto.CopyTo(framed.AsSpan(5));
        return framed;
    }

    internal static bool TryUnpackRelayControlPacket(ReadOnlySpan<byte> datagram, out WeTypeWire packet)
    {
        packet = new WeTypeWire();
        if (datagram.Length < 2) return false;
        ReadOnlySpan<byte> body;
        if (datagram[0] == ControlMagicCrc)
        {
            if (datagram.Length < 6) return false;
            uint expected = BinaryPrimitives.ReadUInt32BigEndian(datagram.Slice(1, 4));
            body = datagram[5..];
            if (Crc32(body) != expected) return false;
        }
        else if (datagram[0] is 0xD0 or 0xD1)
        {
            body = datagram[1..];
        }
        else return false;

        try
        {
            packet = WeTypeWire.Parse(body);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            return false;
        }
    }

    internal static byte[] PackTcpFrame(ReadOnlySpan<byte> inner)
    {
        byte[] framed = new byte[9 + inner.Length];
        framed[0] = TcpFrameMagic;
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(1, 4), (uint)(inner.Length + 8));
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(5, 2), 8);
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(7, 2), 1);
        inner.CopyTo(framed.AsSpan(9));
        return framed;
    }

    internal static byte[] PackLiveCloud96(ulong roomId, ulong roomKey, ReadOnlySpan<byte> encryptKey,
        ushort memberId, ushort sn, ushort dataTypeSeq, byte connId, ReadOnlySpan<byte> payload,
        byte liveTypeNibble = LiveCloudNibbleMpRudp, byte flags = 0, byte subFlag = 0)
    {
        byte typeByte = (byte)((liveTypeNibble << 4) | (flags & 0x0F));
        byte nonceTypeByte = (typeByte & 0xF0) == 0x60 ? (byte)((typeByte & 0x0F) | 0x20) : typeByte;
        ulong nonce = unchecked(roomKey + ((ulong)nonceTypeByte << 32) + dataTypeSeq);

        byte[] innerPlain = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(innerPlain.AsSpan(0, 2), memberId);
        payload.CopyTo(innerPlain.AsSpan(2));
        byte[] cipher = ChaCha20Poly1305OriginalEncrypt(encryptKey, nonce, innerPlain);

        byte[] packet = new byte[19 + cipher.Length];
        packet[0] = DataMagicLiveCloud96;
        packet[1] = 19;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(2, 8), roomId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10, 2), memberId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), sn);
        packet[14] = typeByte;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(15, 2), dataTypeSeq);
        packet[17] = subFlag;
        packet[18] = connId;
        cipher.CopyTo(packet.AsSpan(19));
        return packet;
    }

    internal static bool TryUnpackRelayData(ReadOnlySpan<byte> packet, ulong roomKey, ReadOnlySpan<byte> encryptKey,
        uint selfMemberId, out byte dataType, out ushort senderMemberId, out byte[] payload)
    {
        dataType = 0;
        senderMemberId = 0;
        payload = [];
        if (packet.Length < 16 || encryptKey.Length != 32) return false;

        byte magic = packet[0];
        if (magic == DataMagicLiveCloud96)
        {
            if (packet.Length < 19 + 18) return false;
            int headerLen = packet[1];
            if (headerLen < 19 || headerLen + 18 > packet.Length) return false;
            ushort headerMember = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(10, 2));
            byte typeByte = packet[14];
            ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(15, 2));
            ulong nonce = unchecked(roomKey + ((ulong)typeByte << 32) + seq);
            if (!ChaCha20Poly1305OriginalDecrypt(encryptKey, nonce, packet[headerLen..], out var plain) || plain.Length < 3)
                return false;
            ushort innerMember = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(0, 2));
            if (innerMember != headerMember || innerMember == selfMemberId) return false;
            dataType = MapLiveCloudNibbleToDataType((byte)(typeByte >> 4));
            senderMemberId = innerMember;
            payload = plain[2..];
            return dataType != 0;
        }

        if (magic == DataMagicLiveCloud98)
        {
            if (packet.Length < 21 + 18) return false;
            int headerLen = packet[1];
            if (headerLen < 21 || headerLen + 18 > packet.Length) return false;
            ushort headerMember = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(10, 2));
            byte typeByte = packet[14];
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(15, 4));
            ulong nonce = unchecked(roomKey + ((ulong)typeByte << 32) + seq);
            if (!ChaCha20Poly1305OriginalDecrypt(encryptKey, nonce, packet[headerLen..], out var plain) || plain.Length < 3)
                return false;
            ushort innerMember = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(0, 2));
            if (innerMember != headerMember || innerMember == selfMemberId) return false;
            dataType = MapLiveCloudNibbleToDataType((byte)(typeByte >> 4));
            senderMemberId = innerMember;
            payload = plain[2..];
            return dataType != 0;
        }

        if (magic == DataMagicChaChaNew97)
        {
            if (packet.Length < 17 + 17) return false;
            int headerLen = packet[1];
            if (headerLen < 17 || headerLen + 17 > packet.Length) return false;
            byte member = (byte)(packet[10] & 0x7F);
            if (member == selfMemberId) return false;
            byte dt = packet[14];
            ushort seq = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(15, 2));
            ulong nonce = unchecked(roomKey + ((ulong)dt << 32) + seq);
            if (!ChaCha20Poly1305OriginalDecrypt(encryptKey, nonce, packet[headerLen..], out var plain) || plain.Length == 0)
                return false;
            dataType = dt;
            senderMemberId = member;
            payload = plain;
            return true;
        }

        if (magic == DataMagicChaChaV3)
        {
            if (packet.Length < 15 + 17) return false;
            byte member = (byte)(packet[11] & 0x7F);
            if (member == selfMemberId) return false;
            byte dt = packet[12];
            ushort seq = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(13, 2));
            ulong nonce = unchecked(roomKey + ((ulong)dt << 32) + seq);
            if (!ChaCha20Poly1305OriginalDecrypt(encryptKey, nonce, packet[15..], out var plain) || plain.Length == 0)
                return false;
            dataType = dt;
            senderMemberId = member;
            payload = plain;
            return true;
        }

        return false;
    }

    private static byte MapLiveCloudNibbleToDataType(byte nibble) => nibble switch
    {
        1 => 0x76,
        2 => 0x77,
        3 => 0x93,
        4 => 0x83,
        5 => DataTypeMpRudp,
        6 => 0x79,
        7 => 0x84,
        _ => 0
    };
}
