using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using BrimDeck.Core.ClipboardSync;

static class LanPairingTests
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        var session = Guid.NewGuid();
        byte[] pin = SHA256.HashData("synthetic TLS certificate"u8);
        var paired = await ExchangeAsync("503197", "503197", session, session, pin, pin);
        check("LAN PAKE produces the same independent key and verification string on both devices",
            paired.Host is { } host && paired.Guest is { } guest && host.SequenceEqual(guest) &&
            paired.Confirmations is { Length: 2 } both && both[0].Verification == both[1].Verification &&
            both.Select(value => value.DeviceName).Order().SequenceEqual(["guest", "host"]));
        var replay = await ExchangeAsync("503197", "503197", session, session, pin, pin);
        check("LAN PAKE keys differ across sessions even for the same six digits", replay.Host is { } fresh &&
            paired.Host is { } before && !fresh.AsSpan().SequenceEqual(before));
        var wrongCode = await ExchangeAsync("503197", "503198", session, session, pin, pin);
        check("LAN PAKE blocks wrong codes before any device confirmation", FailedBeforeConfirmation(wrongCode));
        var changedCert = await ExchangeAsync("503197", "503197", session, session, pin, SHA256.HashData("substituted certificate"u8));
        check("LAN PAKE blocks a relayed proof under a different TLS certificate", FailedBeforeConfirmation(changedCert));
        var staleId = await ExchangeAsync("503197", "503197", session, Guid.NewGuid(), pin, pin);
        check("LAN PAKE blocks proofs from a different discovery session", FailedBeforeConfirmation(staleId));
        var wrongRole = await ExchangeAsync("503197", "503197", session, session, pin, pin, guestClaimsHost: true);
        check("LAN PAKE blocks role reflection", FailedBeforeConfirmation(wrongRole));
        var declined = await ExchangeAsync("503197", "503197", session, session, pin, pin, guestAccepts: false);
        check("LAN PAKE requires both confirmations before returning the enrollment key",
            declined.Host is null && declined.Guest is null && declined.Confirmations.Length == 2);
        var cancelled = await ExchangeAsync("503197", "503197", session, session, pin, pin, cancelWhenConfirmed: true);
        check("LAN PAKE cancellation during confirmation returns no enrollment key", cancelled.Host is null && cancelled.Guest is null);

        using var malformed = new MemoryStream([2, 255, 255]);
        bool bounded = false;
        try { await LanPairingHandshake.ReceiveAsync(malformed, 2, 4096, default); }
        catch (InvalidDataException) { bounded = true; }
        check("LAN PAKE rejects a forged frame length before allocation", bounded);

        var source = new LanInterface("Wi-Fi", IPAddress.Parse("192.168.42.3"), 24);
        var peer = IPAddress.Parse("192.168.42.8");
        byte[] nonce = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];
        byte[] response = LanDiscovery.Response(nonce, session, 4312, "test peer");
        var discovered = LanDiscovery.ParseResponse(response, nonce, peer, source);
        check("LAN discovery identifies an on-link peer without a code", discovered is { Name: "test peer", Port: 4312 }
            && discovered.Session == session && discovered.LocalAddress.Equals(source.Address) &&
            response.AsSpan().IndexOf("503197"u8) < 0);
        check("LAN discovery ignores off-subnet and unsolicited responses",
            LanDiscovery.ParseResponse(response, new byte[16], peer, source) is null &&
            LanDiscovery.ParseResponse(response, nonce, IPAddress.Parse("192.168.40.8"), source) is null);
        check("LAN discovery preserves distinct devices with the same name",
            LanDiscovery.ParseResponse(LanDiscovery.Response(nonce, Guid.NewGuid(), 4312, "test peer"), nonce, peer, source) is { } other &&
            other.Session != discovered?.Session);
        // A paired computer that comes back with another address is found again by the pair's id.
        byte[] secret = [.. Enumerable.Range(0, 32).Select(i => (byte)(i * 7))];
        Guid link = LanClipboardLink.LinkId(secret);
        byte[] moved = LanDiscovery.Response(nonce, link, 50123, "BrimDeck", link: true);
        var movedPeer = IPAddress.Parse("192.168.42.77");
        check("LAN finds its paired computer at a new address",
            LanDiscovery.ParseResponse(moved, nonce, movedPeer, source, link) is { Port: 50123 } found && found.Address.Equals(movedPeer));
        check("LAN pair id is stable for a pair and differs between pairs",
            link == LanClipboardLink.LinkId((byte[])secret.Clone()) && link != LanClipboardLink.LinkId(new byte[32]) && link != Guid.Empty);
        check("LAN ignores another pair's answer and answers of the other kind",
            LanDiscovery.ParseResponse(LanDiscovery.Response(nonce, Guid.NewGuid(), 50123, "BrimDeck", link: true), nonce, movedPeer, source, link) is null &&
            LanDiscovery.ParseResponse(moved, nonce, movedPeer, source) is null &&
            LanDiscovery.ParseResponse(response, nonce, peer, source, session) is null);
        check("LAN discovery calculates the broadcast address from the subnet prefix",
            LanNetwork.BroadcastAddress(source with { PrefixLength = 26 })?.Equals(IPAddress.Parse("192.168.42.63")) == true);

        var clock = new TestClock();
        var limiter = new LanPairingAttempts(4, 2, TimeSpan.FromMinutes(5), clock);
        var alternate = IPAddress.Parse("192.168.42.9");
        check("LAN pairing limits attempts by source and across different sources",
            limiter.TryBegin(peer) && limiter.TryBegin(peer) && !limiter.TryBegin(peer) &&
            limiter.TryBegin(alternate) && limiter.TryBegin(alternate) && !limiter.TryBegin(IPAddress.Parse("192.168.42.10")));
        clock.Ticks = TimeSpan.FromMinutes(5).Ticks;
        check("LAN pairing limits expire using monotonic time", limiter.TryBegin(peer));
        var parallel = new LanPairingAttempts(5, 5, TimeSpan.FromMinutes(1));
        int passed = 0;
        Parallel.For(0, 16, _ => { if (parallel.TryBegin(peer)) Interlocked.Increment(ref passed); });
        check("LAN pairing attempt limit remains bounded under concurrent requests", passed == 5);
    }

    private static bool FailedBeforeConfirmation((byte[]? Host, byte[]? Guest, LanPairingConfirmation[] Confirmations) result) =>
        result.Host is null && result.Guest is null && result.Confirmations.Length == 0;

    private static async Task<(byte[]? Host, byte[]? Guest, LanPairingConfirmation[] Confirmations)> ExchangeAsync(
        string hostCode, string guestCode, Guid hostId, Guid guestId, byte[] hostPin, byte[] guestPin,
        bool guestClaimsHost = false, bool guestAccepts = true, bool cancelWhenConfirmed = false)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guest = new TcpClient();
        await guest.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop.Token);
        using var host = await listener.AcceptTcpClientAsync(stop.Token);
        listener.Stop();
        var confirmations = new System.Collections.Concurrent.ConcurrentQueue<LanPairingConfirmation>();
        var hostTask = Run(host.GetStream(), true, hostId, hostPin, hostCode, "host", true);
        var guestTask = Run(guest.GetStream(), guestClaimsHost, guestId, guestPin, guestCode, "guest", guestAccepts);
        await Task.WhenAll(hostTask, guestTask);
        return (await hostTask, await guestTask, confirmations.ToArray());

        async Task<byte[]?> Run(Stream stream, bool role, Guid id, byte[] pin, string code, string name, bool accept)
        {
            try
            {
                return await LanPairingHandshake.ConfirmAsync(stream, role, id, pin, code, name, (confirmation, _) =>
                {
                    confirmations.Enqueue(confirmation);
                    if (cancelWhenConfirmed) stop.Cancel();
                    return Task.FromResult(accept);
                }, stop.Token);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or InvalidDataException or OperationCanceledException)
            { return null; }
            finally { stream.Dispose(); }
        }
    }

    private sealed class TestClock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
}
