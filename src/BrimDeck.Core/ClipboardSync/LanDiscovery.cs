using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace BrimDeck.Core.ClipboardSync;

public sealed record LanPairingDevice(Guid Session, string Name, IPAddress Address, IPAddress LocalAddress, int Port)
{
    public string Label => $"{Name} · {Address}";
}

internal static class LanDiscovery
{
    internal const int Port = 49837;
    private const int QuerySize = 300, ResponseHeader = 38, MaxResponse = 294;
    private static ReadOnlySpan<byte> QueryMagic => "BDQ2"u8;
    private static ReadOnlySpan<byte> ResponseMagic => "BDR2"u8;
    // A paired computer asking where its partner is now: the query names the pair (LanClipboardLink.LinkId), and only that
    // partner answers. A computer showing a pairing code does not answer these, nor a paired one the pairing queries.
    private static ReadOnlySpan<byte> LinkQueryMagic => "BDQL"u8;
    private static ReadOnlySpan<byte> LinkResponseMagic => "BDRL"u8;

    internal static Task<IReadOnlyList<LanPairingDevice>> FindAsync(IEnumerable<LanInterface> interfaces, CancellationToken cancellation) =>
        FindAsync(interfaces, null, cancellation);

    // Where the paired computer listens now, on whichever local network it answers from.
    internal static Task<IReadOnlyList<LanPairingDevice>> FindLinkAsync(IEnumerable<LanInterface> interfaces, Guid link, CancellationToken cancellation) =>
        FindAsync(interfaces, link, cancellation);

    private static async Task<IReadOnlyList<LanPairingDevice>> FindAsync(IEnumerable<LanInterface> interfaces, Guid? link, CancellationToken cancellation)
    {
        var jobs = interfaces.DistinctBy(adapter => adapter.Address).Take(16)
            .Select(adapter => FindOnInterfaceAsync(adapter, link, cancellation));
        var results = await Task.WhenAll(jobs).ConfigureAwait(false);
        return results.SelectMany(devices => devices).DistinctBy(device => (device.Session, device.Address, device.Port))
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).Take(32).ToArray();
    }

    private static async Task<List<LanPairingDevice>> FindOnInterfaceAsync(LanInterface adapter, Guid? link, CancellationToken cancellation)
    {
        var results = new List<LanPairingDevice>();
        if (LanNetwork.BroadcastAddress(adapter) is not { } broadcast) return results;
        using var time = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        time.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var client = new UdpClient(new IPEndPoint(adapter.Address, 0)) { EnableBroadcast = true };
            var query = new byte[QuerySize];
            (link is null ? QueryMagic : LinkQueryMagic).CopyTo(query);
            RandomNumberGenerator.Fill(query.AsSpan(4, 16));
            link?.TryWriteBytes(query.AsSpan(20, 16));
            await client.SendAsync(query, new IPEndPoint(broadcast, Port), time.Token).ConfigureAwait(false);
            byte[] buffer = new byte[MaxResponse + 1];
            var any = new IPEndPoint(IPAddress.Any, 0);
            for (int received = 0; received < 64 && !time.IsCancellationRequested && results.Count < 16; received++)
            {
                SocketReceiveFromResult response;
                try { response = await client.Client.ReceiveFromAsync(buffer, SocketFlags.None, any, time.Token).ConfigureAwait(false); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize) { continue; }
                if (response.RemoteEndPoint is IPEndPoint sender &&
                    ParseResponse(buffer.AsSpan(0, response.ReceivedBytes), query.AsSpan(4, 16), sender.Address, adapter, link) is { } device &&
                    !results.Contains(device))
                {
                    results.Add(device);
                    // The partner is one computer: its answer ends the wait.
                    if (link is not null) break;
                }
            }
        }
        catch (SocketException) when (!cancellation.IsCancellationRequested) { }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        return results;
    }

    internal static LanPairingDevice? ParseResponse(ReadOnlySpan<byte> response, ReadOnlySpan<byte> nonce,
        IPAddress sender, LanInterface adapter, Guid? link = null)
    {
        if (response.Length is <= ResponseHeader or > MaxResponse || !response.StartsWith(link is null ? ResponseMagic : LinkResponseMagic) ||
            !response.Slice(4, 16).SequenceEqual(nonce) || sender.Equals(adapter.Address) || !LanNetwork.IsOnSubnet(sender, adapter))
            return null;
        Guid session = new(response.Slice(20, 16));
        int port = BinaryPrimitives.ReadUInt16BigEndian(response[36..]);
        if (session == Guid.Empty || port == 0 || link is { } expected && session != expected) return null;
        try { return new(session, LanPairingHandshake.CheckName(SyncTransport.Utf8.GetString(response[ResponseHeader..])), sender, adapter.Address, port); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.DecoderFallbackException) { return null; }
    }

    internal static byte[] Response(ReadOnlySpan<byte> nonce, Guid session, int port, string name, bool link = false)
    {
        byte[] nameBytes = SyncTransport.Utf8.GetBytes(LanPairingHandshake.CheckName(name));
        byte[] response = new byte[ResponseHeader + nameBytes.Length];
        (link ? LinkResponseMagic : ResponseMagic).CopyTo(response);
        nonce.CopyTo(response.AsSpan(4, 16));
        session.TryWriteBytes(response.AsSpan(20, 16));
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(36), checked((ushort)port));
        nameBytes.CopyTo(response, ResponseHeader);
        return response;
    }

    // link: answers only the paired computer's question about this pair (session is the pair's id), not pairing queries.
    internal static async Task ReplyAsync(IReadOnlyList<(LanInterface Adapter, int Port)> listeners, Guid session,
        string name, CancellationToken cancellation, bool link = false)
    {
        byte[] pair = session.ToByteArray();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ExclusiveAddressUse = true;
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, Port));
        var sources = new Dictionary<IPAddress, Socket>();
        var replyLimit = new LanPairingAttempts(20, 2, TimeSpan.FromSeconds(1));
        try
        {
            foreach (var (adapter, _) in listeners)
            {
                var source = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sources.Add(adapter.Address, source);
                source.Bind(new IPEndPoint(adapter.Address, 0));
            }
            byte[] buffer = new byte[QuerySize + 1];
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (!cancellation.IsCancellationRequested)
            {
                SocketReceiveMessageFromResult message;
                try { message = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, any, cancellation).ConfigureAwait(false); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize) { continue; }
                if (message.ReceivedBytes != QuerySize || !buffer.AsSpan().StartsWith(link ? LinkQueryMagic : QueryMagic) ||
                    link && !buffer.AsSpan(20, 16).SequenceEqual(pair) || message.RemoteEndPoint is not IPEndPoint sender) continue;
                var selected = listeners.Where(entry => entry.Adapter.Index == message.PacketInformation.Interface &&
                        LanNetwork.BroadcastAddress(entry.Adapter)?.Equals(message.PacketInformation.Address) == true &&
                        !sender.Address.Equals(entry.Adapter.Address) && LanNetwork.IsOnSubnet(sender.Address, entry.Adapter))
                    .OrderByDescending(entry => entry.Adapter.PrefixLength).FirstOrDefault();
                if (selected.Adapter is null || !replyLimit.TryBegin(sender.Address)) continue;
                var response = Response(buffer.AsSpan(4, 16), session, selected.Port, name, link);
                try { await sources[selected.Adapter.Address].SendToAsync(response, SocketFlags.None, sender, cancellation).ConfigureAwait(false); }
                catch (SocketException) when (!cancellation.IsCancellationRequested) { }
            }
        }
        finally { foreach (var source in sources.Values) source.Dispose(); }
    }
}

internal sealed class LanPairingAttempts(int total, int perSource, TimeSpan window, TimeProvider? time = null)
{
    private readonly Queue<(IPAddress Source, long Time)> _used = new();

    internal bool TryBegin(IPAddress source)
    {
        var clock = time ?? TimeProvider.System;
        lock (_used)
        {
            long now = clock.GetTimestamp();
            while (_used.TryPeek(out var oldest) && clock.GetElapsedTime(oldest.Time, now) >= window) _used.Dequeue();
            if (_used.Count >= total || _used.Count(attempt => attempt.Source.Equals(source)) >= perSource) return false;
            _used.Enqueue((source, now));
            return true;
        }
    }
}
