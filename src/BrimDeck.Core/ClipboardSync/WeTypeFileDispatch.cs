using System.Net;
using System.Net.Sockets;

namespace BrimDeck.Core.ClipboardSync;

// TransferDispatchResult contains room credentials. Keep its raw bytes, room key, and encryption key out of logs and settings.
internal sealed class WeTypeFileDispatch
{
    internal sealed record Endpoint(IPEndPoint Address, ProtocolType Transport);
    internal sealed record ConnectionCandidate(int ConnId, IReadOnlyList<IPEndPoint> UdpRelays, IReadOnlyList<IPEndPoint> TcpRelays);

    public ulong RoomId { get; }
    public ulong RoomKey { get; }
    public uint MemberId { get; }
    public ulong Uin { get; }
    public uint EncryptStrategy { get; }
    public byte[] EncryptKey { get; }
    public uint HelloFreqSec { get; }
    public ulong SwitchFlag { get; }
    public IReadOnlyList<ConnectionCandidate> Connections { get; }
    public IReadOnlyList<Endpoint> Relays { get; }

    public bool CanRelay => RoomId != 0 && EncryptKey.Length == 32 && Relays.Count > 0;
    public uint PeerMemberId => MemberId ^ 1u;

    private WeTypeFileDispatch(ulong roomId, ulong roomKey, uint memberId, ulong uin, uint encryptStrategy,
        byte[] encryptKey, uint helloFreqSec, ulong switchFlag, List<ConnectionCandidate> connections, List<Endpoint> relays)
    {
        RoomId = roomId;
        RoomKey = roomKey;
        MemberId = memberId;
        Uin = uin;
        EncryptStrategy = encryptStrategy;
        EncryptKey = encryptKey;
        HelloFreqSec = helloFreqSec is >= 2 and <= 30 ? helloFreqSec : 5;
        SwitchFlag = switchFlag;
        Connections = connections;
        Relays = relays;
    }

    internal static WeTypeFileDispatch Parse(byte[] bytes)
    {
        if (bytes.Length is 0 or > 128 * 1024) throw new InvalidDataException("Invalid WXP2P dispatch size.");
        var room = WeTypeWire.Parse(bytes);
        ulong roomId = room.GetNumber(1);
        if (roomId == 0 || !room.Has(2) || !room.Has(3) || !room.Has(4))
            throw new InvalidDataException("Missing WXP2P room parameters.");
        ulong roomKey = room.GetNumber(2);
        ulong memberRaw = room.GetNumber(3);
        if (memberRaw > ushort.MaxValue) throw new InvalidDataException("Invalid WXP2P member id.");
        ulong uin = room.GetNumber(5);

        var strategy = room.GetMessage(4);
        uint encryptStrategy = (uint)strategy.GetNumber(9);
        byte[] encryptKey = strategy.GetBytes(10);
        uint helloFreq = (uint)strategy.GetNumber(5);
        ulong switchFlag = strategy.GetNumber(14);

        List<Endpoint> endpoints = [];
        List<ConnectionCandidate> connections = [];
        foreach (var connection in strategy.Messages(15))
        {
            int connId = unchecked((int)connection.GetNumber(1));
            var udp = ReadAddresses(connection, 4, ProtocolType.Udp, endpoints);
            var tcp = ReadAddresses(connection, 5, ProtocolType.Tcp, endpoints);
            if (udp.Count > 0 || tcp.Count > 0)
                connections.Add(new ConnectionCandidate(connId, udp, tcp));
        }

        if (connections.Count == 0)
        {
            var udp = ReadAddresses(strategy, 1, ProtocolType.Udp, endpoints);
            var tcp = ReadAddresses(strategy, 2, ProtocolType.Tcp, endpoints);
            if (udp.Count > 0 || tcp.Count > 0)
                connections.Add(new ConnectionCandidate(0, udp, tcp));
        }

        return new WeTypeFileDispatch(roomId, roomKey, (uint)memberRaw, uin, encryptStrategy, encryptKey,
            helloFreq, switchFlag, connections, endpoints);
    }

    private static List<IPEndPoint> ReadAddresses(WeTypeWire container, int field, ProtocolType protocol, List<Endpoint> all)
    {
        List<IPEndPoint> list = [];
        foreach (var entry in container.Messages(field))
        {
            ulong port = entry.GetNumber(2);
            if (port is 0 or > 65535 || !IPAddress.TryParse(entry.GetText(1), out var address)) continue;
            var ipEndPoint = new IPEndPoint(address, (int)port);
            if (!list.Contains(ipEndPoint) && list.Count < 32) list.Add(ipEndPoint);
            var endpoint = new Endpoint(ipEndPoint, protocol);
            if (!all.Contains(endpoint) && all.Count < 64) all.Add(endpoint);
        }
        return list;
    }
}

public sealed class WeTypeRelayUnavailableException() : IOException("A WXP2P relay is required for this file transfer.");

// The phone is still sending an earlier transfer into this room, which another BrimDeck process had joined (it was
// restarted part-way). That data is gone, so the transfer cannot continue.
public sealed class WeTypeStaleTransferException() : IOException("The peer is continuing a transfer that this process did not start.");
