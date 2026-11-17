using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BrimDeck.Core.ClipboardSync;

public sealed record LanInterface(string Name, IPAddress Address, int PrefixLength)
{
    public string Label => $"{Name} · {Address}/{PrefixLength}";
    internal int Index { get; init; }
}

public sealed class LanHostAddressUnavailableException()
    : IOException("The local address used for pairing is unavailable.");

public sealed class LanPeerOffSubnetException()
    : IOException("The peer address is not on any local subnet.");

public sealed class LanNetworkChangedException()
    : IOException("The local network changed during pairing.");

public static class LanNetwork
{
    public static IReadOnlyList<LanInterface> Interfaces() => Eligible(NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType is
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => new LanInterface(adapter.Name, address.Address, address.PrefixLength)
            { Index = adapter.GetIPProperties().GetIPv4Properties()?.Index ?? 0 })));

    public static IReadOnlyList<LanInterface> Eligible(IEnumerable<LanInterface> interfaces) => interfaces
        .Where(adapter => IsPrivate(adapter.Address) && adapter.PrefixLength is > 0 and <= 32)
        .DistinctBy(adapter => (adapter.Name, adapter.Address, adapter.PrefixLength))
        .ToArray();

    public static LanInterface? DefaultHost(IReadOnlyList<LanInterface> interfaces) =>
        interfaces.Count == 1 ? interfaces[0] : null;

    public static IReadOnlyList<IPAddress> RoutesTo(IPAddress peer, IEnumerable<LanInterface> interfaces) =>
        Eligible(interfaces)
            .Where(adapter => IsOnSubnet(peer, adapter))
            .OrderByDescending(adapter => adapter.PrefixLength)
            .Select(adapter => adapter.Address)
            .Distinct()
            .ToArray();

    public static bool IsOnSubnet(IPAddress peer, LanInterface adapter)
    {
        if (!IsPrivate(peer) || !IsPrivate(adapter.Address) || adapter.PrefixLength is < 1 or > 32) return false;
        uint local = BinaryPrimitives.ReadUInt32BigEndian(adapter.Address.GetAddressBytes());
        uint remote = BinaryPrimitives.ReadUInt32BigEndian(peer.GetAddressBytes());
        int hostBits = 32 - adapter.PrefixLength;
        return local >> hostBits == remote >> hostBits;
    }

    internal static IPAddress? BroadcastAddress(LanInterface adapter)
    {
        if (!IsPrivate(adapter.Address) || adapter.PrefixLength is < 1 or > 30) return null;
        var bytes = adapter.Address.GetAddressBytes();
        uint address = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        uint host = uint.MaxValue >> adapter.PrefixLength;
        BinaryPrimitives.WriteUInt32BigEndian(bytes, address | host);
        return new IPAddress(bytes);
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
    }
}
