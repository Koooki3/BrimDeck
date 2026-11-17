using System.Buffers.Binary;
using System.Security.Authentication;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using Org.BouncyCastle.Math;

namespace BrimDeck.Core.ClipboardSync;

public sealed record LanPairingConfirmation(string DeviceName, string Verification);

internal static class LanPairingHandshake
{
    private const int MaxFrame = 4096;
    private const int NonceSize = 16;

    internal static bool IsCode(string code) => code.Length == 6 && code.All(char.IsAsciiDigit);

    internal static string CheckName(string name)
    {
        if (name.Length is < 1 or > 64 || name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            throw new InvalidDataException("Invalid pairing device name.");
        return name;
    }

    internal static async Task<byte[]> ConfirmAsync(Stream stream, bool host, Guid discoveryId, byte[] certificatePin,
        string code, string name, Func<LanPairingConfirmation, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        if (!IsCode(code)) throw new ArgumentException("A six-digit pairing code is required.", nameof(code));
        if (certificatePin.Length != 32 || discoveryId == Guid.Empty) throw new ArgumentException("Invalid pairing context.");
        using var time = CancellationTokenSource.CreateLinkedTokenSource(token);
        time.CancelAfter(TimeSpan.FromSeconds(12));
        var exchange = time.Token;
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        await SendAsync(stream, 1, nonce, exchange).ConfigureAwait(false);
        byte[] remoteNonce = await ReceiveAsync(stream, 1, NonceSize, exchange).ConfigureAwait(false);
        if (remoteNonce.Length != NonceSize || nonce.AsSpan().SequenceEqual(remoteNonce))
            throw new InvalidDataException("Invalid pairing nonce.");

        // Both proof identities bind the fresh exchange to the selected discovery session and
        // the TLS endpoint. A relay terminating TLS under another certificate cannot reuse them.
        byte[] context = SHA256.HashData([
            .. "BrimDeck LAN pairing v2/text+png"u8, .. discoveryId.ToByteArray(), .. certificatePin,
            .. (host ? nonce : remoteNonce), .. (host ? remoteNonce : nonce)
        ]);
        string binding = Convert.ToHexString(context);
        string identity = (host ? "host/" : "guest/") + binding;
        string peerIdentity = (host ? "guest/" : "host/") + binding;
        char[] password = code.ToCharArray();
        var participant = new JPakeParticipant(identity, password);
        Array.Clear(password);

        BigInteger key;
        try
        {
            var first = participant.CreateRound1PayloadToSend();
            await SendAsync(stream, 2, Pack([first.Gx1, first.Gx2, .. first.KnowledgeProofForX1, .. first.KnowledgeProofForX2]), exchange).ConfigureAwait(false);
            var round1 = Unpack(await ReceiveAsync(stream, 2, MaxFrame, exchange).ConfigureAwait(false), 6);
            participant.ValidateRound1PayloadReceived(new(peerIdentity, round1[0], round1[1], round1[2..4], round1[4..6]));

            var second = participant.CreateRound2PayloadToSend();
            await SendAsync(stream, 3, Pack([second.A, .. second.KnowledgeProofForX2s]), exchange).ConfigureAwait(false);
            var round2 = Unpack(await ReceiveAsync(stream, 3, MaxFrame, exchange).ConfigureAwait(false), 3);
            participant.ValidateRound2PayloadReceived(new(peerIdentity, round2[0], round2[1..3]));

            key = participant.CalculateKeyingMaterial();
            var third = participant.CreateRound3PayloadToSend(key);
            await SendAsync(stream, 4, third.MacTag.ToByteArray(), exchange).ConfigureAwait(false);
            var mac = await ReceiveAsync(stream, 4, 33, exchange).ConfigureAwait(false);
            participant.ValidateRound3PayloadReceived(new(peerIdentity, new BigInteger(mac)), key);
        }
        catch (CryptoException ex) { throw new AuthenticationException("The pairing code or TLS endpoint could not be verified.", ex); }

        // The verification string is derived from the high-entropy PAKE result, not the code.
        byte[] material = key.ToByteArrayUnsigned();
        byte[] verification = HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 4, context, "BrimDeck LAN compare v2"u8.ToArray());
        string compare = Convert.ToHexString(verification);
        try
        {
            byte[] confirmationKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, context, "BrimDeck LAN binding v2"u8.ToArray());
            try
            {
                byte[] ownTag = HMACSHA256.HashData(confirmationKey, SyncTransport.Utf8.GetBytes(identity));
                byte[] expectedTag = HMACSHA256.HashData(confirmationKey, SyncTransport.Utf8.GetBytes(peerIdentity));
                await SendAsync(stream, 5, ownTag, exchange).ConfigureAwait(false);
                byte[] remoteTag = await ReceiveAsync(stream, 5, 32, exchange).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(remoteTag, expectedTag))
                    throw new AuthenticationException("Pairing binding did not match.");
            }
            finally { CryptographicOperations.ZeroMemory(confirmationKey); }
            await SendAsync(stream, 6, SyncTransport.Utf8.GetBytes(CheckName(name)), exchange).ConfigureAwait(false);
            string peerName;
            try { peerName = CheckName(SyncTransport.Utf8.GetString(await ReceiveAsync(stream, 6, 256, exchange).ConfigureAwait(false))); }
            catch (System.Text.DecoderFallbackException ex) { throw new InvalidDataException("Invalid device name encoding.", ex); }

            time.CancelAfter(TimeSpan.FromSeconds(45));
            bool approved = await confirm(new(peerName, compare[..4] + " " + compare[4..]), exchange).ConfigureAwait(false);
            await SendAsync(stream, 7, [approved ? (byte)1 : (byte)0], exchange).ConfigureAwait(false);
            if (!approved) throw new LanPairingDeclinedException();
            byte[] decision = await ReceiveAsync(stream, 7, 1, exchange).ConfigureAwait(false);
            if (decision.Length != 1 || decision[0] != 1) throw new LanPairingDeclinedException();
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, context, "BrimDeck LAN credential v2"u8.ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(material); }
    }

    private static byte[] Pack(params BigInteger[] values)
    {
        using var output = new MemoryStream();
        foreach (var value in values)
        {
            var bytes = value.ToByteArrayUnsigned();
            ushort size = checked((ushort)bytes.Length);
            output.WriteByte((byte)(size >> 8));
            output.WriteByte((byte)size);
            output.Write(bytes);
        }
        return output.ToArray();
    }

    private static BigInteger[] Unpack(byte[] payload, int count)
    {
        ReadOnlySpan<byte> remaining = payload;
        var result = new BigInteger[count];
        for (int i = 0; i < count; i++)
        {
            if (remaining.Length < 2) throw new InvalidDataException("Missing pairing proof.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(remaining);
            bool exponent = count == 6 ? i is 3 or 5 : i == 2;
            if (length > (exponent ? 32 : 384) || remaining.Length < 2 + length)
                throw new InvalidDataException("Invalid pairing proof length.");
            result[i] = length == 0 ? BigInteger.Zero : new BigInteger(1, remaining.Slice(2, length).ToArray());
            remaining = remaining[(2 + length)..];
        }
        if (!remaining.IsEmpty) throw new InvalidDataException("Unexpected pairing proof bytes.");
        return result;
    }

    internal static async Task SendAsync(Stream stream, byte step, byte[] payload, CancellationToken token)
    {
        if (payload.Length is < 1 or > MaxFrame) throw new InvalidDataException("Invalid outgoing pairing frame.");
        byte[] header = [step, (byte)(payload.Length >> 8), (byte)payload.Length];
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReceiveAsync(Stream stream, byte step, int limit, CancellationToken token)
    {
        var header = new byte[3];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
        if (header[0] != step || length is < 1 || length > limit) throw new InvalidDataException("Invalid pairing frame.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return payload;
    }
}

public sealed class LanPairingDeclinedException() : IOException("Pairing was declined.");
