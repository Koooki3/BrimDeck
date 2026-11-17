using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BrimDeck.Core.ClipboardSync;

public sealed record LanClipboardPeer(string Address, int Port, byte[] Fingerprint, byte[] Key, byte[]? HostCertificate)
{
    public bool IsHost => HostCertificate is not null;
    public string? DeviceName { get; init; }
}

public abstract record LanClipboardContent
{
    public sealed record Text(string Value) : LanClipboardContent;
    public sealed record Image(LanPng Png) : LanClipboardContent;
    // The other computer ended the pairing.
    public sealed record Unpaired : LanClipboardContent;
}

public static partial class LanClipboardLink
{
    private const string InvitePrefix = "BD-LAN-1.";
    private const int KeySize = 32;
    private const byte Sync = 2, SyncWithImages = 3;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);

    public static IReadOnlyList<IPAddress> LocalAddresses() => LanNetwork.Interfaces().Select(n => n.Address).Distinct().ToArray();
    public static bool ContainsInvitation(string text) => text.Contains(InvitePrefix, StringComparison.Ordinal);

    // Names a pair of computers to each other when one asks where the other is now. Derived from the pair's secret, it
    // tells nothing about it.
    internal static Guid LinkId(byte[] key) => new(HMACSHA256.HashData(key, "BrimDeck LAN link"u8)[..16]);

    public static async Task RunAsync(LanClipboardPeer config, Func<SslStream, CancellationToken, Task> session,
        CancellationToken cancellation)
    {
        using var network = new NetworkLifetime(cancellation);
        await RunOnCurrentNetworkAsync(config, false, (stream, _, token) => session(stream, token), network.Token);
    }

    // located: the host answered from another address than the one known; the caller keeps it for the next connection.
    public static async Task RunWithImagesAsync(LanClipboardPeer config,
        Func<SslStream, bool, CancellationToken, Task> session, CancellationToken cancellation, Action<IPAddress, int>? located = null)
    {
        using var network = new NetworkLifetime(cancellation);
        await RunOnCurrentNetworkAsync(config, true, session, network.Token, located);
    }

    // A computer that leaves and comes back, a laptop above all, may return with another address. So the host listens on
    // every local network it is on, at the port chosen when the two were paired, and answers its partner's question about
    // where it is; the other computer tries the address it knows and asks on its local networks when that fails. Each run
    // lasts until the addresses of this computer change (NetworkLifetime), and the caller starts the next.
    private static async Task RunOnCurrentNetworkAsync(LanClipboardPeer config, bool allowImages,
        Func<SslStream, bool, CancellationToken, Task> session,
        CancellationToken cancellation, Action<IPAddress, int>? located = null)
    {
        if (config.IsHost)
        {
            using var cert = X509CertificateLoader.LoadPkcs12(config.HostCertificate!, null);
            var listeners = new List<(LanInterface Adapter, TcpListener Listener)>();
            using var answering = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            Task answers = Task.CompletedTask;
            Task<TcpClient>[] pending = [];
            try
            {
                foreach (var adapter in LanNetwork.Interfaces().DistinctBy(a => a.Address).Take(16))
                {
                    try { listeners.Add((adapter, Listen(adapter.Address, config.Port))); }
                    catch (SocketException) { }
                }
                if (listeners.Count == 0) throw new LanHostAddressUnavailableException();
                var advertised = listeners.Where(entry => LanNetwork.BroadcastAddress(entry.Adapter) is not null).Select(entry => (entry.Adapter, config.Port)).ToArray();
                if (advertised.Length > 0) answers = AnswerAsync(advertised, LinkId(config.Key), answering.Token);
                pending = listeners.Select(entry => entry.Listener.AcceptTcpClientAsync(cancellation).AsTask()).ToArray();
                while (!cancellation.IsCancellationRequested)
                {
                    var ready = await Task.WhenAny(pending);
                    int index = Array.IndexOf(pending, ready);
                    using var peer = await ready;
                    pending[index] = listeners[index].Listener.AcceptTcpClientAsync(cancellation).AsTask();
                    using var timeout = Timed(cancellation);
                    try
                    {
                        ValidateRemote(peer, listeners[index].Adapter);
                        using var stream = await SecureServerAsync(peer, cert, timeout.Token);
                        byte mode = await ReadProofAsync(stream, Sync, config.Key, timeout.Token, allowImages);
                        await stream.WriteAsync(new[] { mode }, timeout.Token);
                        await session(stream, mode == SyncWithImages, cancellation);
                    }
                    catch (Exception ex) when (RejectedConnection(ex) && !cancellation.IsCancellationRequested) { }
                }
            }
            finally
            {
                answering.Cancel();
                foreach (var entry in listeners) entry.Listener.Stop();
                foreach (var task in pending)
                {
                    try { (await task).Dispose(); }
                    catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
                }
                await answers;
            }
        }
        else
        {
            (ConnectedPeer Peer, byte Mode) connection;
            try { connection = await DialAsync(IPAddress.Parse(config.Address), config.Port); }
            catch (IOException known) when (!cancellation.IsCancellationRequested)
            {
                // The host is not where it was: it is asked for on this computer's local networks.
                connection = default;
                foreach (var device in await LanDiscovery.FindLinkAsync(LanNetwork.Interfaces(), LinkId(config.Key), cancellation))
                {
                    try { connection = await DialAsync(device.Address, device.Port); }
                    catch (IOException) when (!cancellation.IsCancellationRequested) { continue; }
                    located?.Invoke(device.Address, device.Port);
                    break;
                }
                if (connection.Peer is null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(known);
            }
            using (connection.Peer) await session(connection.Peer.Stream, connection.Mode == SyncWithImages, cancellation);

            // A host of a version that syncs text only refuses the request for pictures; it is then asked for text alone.
            // A host that did not answer at all is not asked twice.
            async Task<(ConnectedPeer Peer, byte Mode)> DialAsync(IPAddress address, int port)
            {
                byte mode = allowImages ? SyncWithImages : Sync;
                try { return (await ConnectSyncAsync(address, port, mode), mode); }
                catch (IOException ex) when (allowImages && ex is not LanPeerOffSubnetException && ex.InnerException is not (SocketException or OperationCanceledException)
                    && !cancellation.IsCancellationRequested)
                { return (await ConnectSyncAsync(address, port, Sync), Sync); }
            }
            Task<ConnectedPeer> ConnectSyncAsync(IPAddress address, int port, byte requestMode) =>
                ConnectAsync(address, port, config.Fingerprint, async (stream, token) =>
                {
                    await WriteProofAsync(stream, requestMode, config.Key, token);
                    byte[] accepted = new byte[1];
                    await stream.ReadExactlyAsync(accepted, token);
                    if (accepted[0] != requestMode) throw new AuthenticationException("LAN authentication failed.");
                }, cancellation);
        }
    }

    // Without the answers the partner still finds this computer at the address it knows, so a port taken by another
    // program only costs the search.
    private static async Task AnswerAsync(IReadOnlyList<(LanInterface Adapter, int Port)> listeners, Guid link, CancellationToken cancellation)
    {
        try { await LanDiscovery.ReplyAsync(listeners, link, "BrimDeck", cancellation, link: true); }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private static bool RejectedConnection(Exception ex) =>
        ex is IOException or InvalidDataException or SocketException or AuthenticationException or OperationCanceledException;

    private static CancellationTokenSource Timed(CancellationToken parent)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(parent);
        result.CancelAfter(HandshakeTimeout);
        return result;
    }

    private static TcpListener Listen(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
        try { listener.Start(4); return listener; }
        catch { listener.Stop(); throw; }
    }

    private static void ValidateRemote(TcpClient peer, LanInterface local)
    {
        if (!LanNetwork.IsOnSubnet(((IPEndPoint)peer.Client.RemoteEndPoint!).Address, local)) throw new LanPeerOffSubnetException();
    }

    private static async Task<ConnectedPeer> ConnectAsync(IPAddress address, int port, byte[] pin,
        Func<SslStream, CancellationToken, Task> authenticate, CancellationToken cancellation)
    {
        var routes = LanNetwork.RoutesTo(address, LanNetwork.Interfaces());
        if (routes.Count == 0) throw new LanPeerOffSubnetException();
        Exception? failure = null;
        foreach (var local in routes)
        {
            cancellation.ThrowIfCancellationRequested();
            using var timeout = Timed(cancellation);
            TcpClient? socket = null;
            SslStream? stream = null;
            try
            {
                socket = new TcpClient(new IPEndPoint(local, 0)) { NoDelay = true };
                await socket.ConnectAsync(address, port, timeout.Token);
                stream = await SecureClientAsync(socket, pin, timeout.Token);
                await authenticate(stream, timeout.Token);
                var result = new ConnectedPeer(socket, stream);
                socket = null;
                stream = null;
                return result;
            }
            catch (Exception ex) when (RejectedConnection(ex) && !cancellation.IsCancellationRequested)
            {
                failure = ex;
            }
            finally
            {
                stream?.Dispose();
                socket?.Dispose();
            }
        }
        throw new IOException("Could not connect over a local interface.", failure);
    }

    private sealed class ConnectedPeer(TcpClient socket, SslStream stream) : IDisposable
    {
        public SslStream Stream => stream;

        public void Dispose()
        {
            stream.Dispose();
            socket.Dispose();
        }
    }

    private sealed class NetworkLifetime : IDisposable
    {
        private readonly CancellationTokenSource _stop;

        public NetworkLifetime(CancellationToken parent)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(parent);
            NetworkChange.NetworkAddressChanged += AddressChanged;
        }

        public CancellationToken Token => _stop.Token;

        private void AddressChanged(object? sender, EventArgs args)
        {
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= AddressChanged;
            _stop.Dispose();
        }
    }

    private static async Task<SslStream> SecureServerAsync(TcpClient peer, X509Certificate2 cert, CancellationToken ct)
    {
        var stream = new SslStream(peer.GetStream());
        try
        {
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, ct);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static async Task<SslStream> SecureClientAsync(TcpClient peer, byte[] pin, CancellationToken ct)
    {
        var stream = new SslStream(peer.GetStream(), false, (_, cert, _, _) =>
            cert is not null && CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), pin));
        try
        {
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "BrimDeck LAN",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, ct);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static async Task WriteProofAsync(Stream stream, byte mode, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        await stream.WriteAsync(new[] { mode }, ct);
        await stream.WriteAsync(secret, ct);
    }

    private static async Task<byte> ReadProofAsync(Stream stream, byte mode, byte[] expected, CancellationToken ct, bool allowImages = false)
    {
        byte[] proof = new byte[1 + KeySize];
        await stream.ReadExactlyAsync(proof, ct);
        byte requestedMode = proof[0];
        bool accepted = (requestedMode == mode || allowImages && requestedMode == SyncWithImages) &&
            CryptographicOperations.FixedTimeEquals(proof.AsSpan(1), expected);
        CryptographicOperations.ZeroMemory(proof);
        if (!accepted) throw new AuthenticationException("LAN authentication failed.");
        return requestedMode;
    }

    public static async Task WriteTextAsync(Stream stream, string text, CancellationToken ct)
    {
        var bytes = SyncTransport.TextBytes(text);
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(bytes, ct);
    }

    public static async Task WriteImageAsync(Stream stream, LanPng image, CancellationToken ct)
    {
        if (!LanPng.Fits(image.Width, image.Height, image.Bytes.Length))
            throw new InvalidDataException("Invalid LAN image size.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, -image.Bytes.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(image.Bytes, ct);
    }

    public static async Task<string?> ReadTextAsync(Stream stream, CancellationToken ct) =>
        (await ReadContentAsync(stream, false, ct) as LanClipboardContent.Text)?.Value;

    public static async Task<LanClipboardContent?> ReadContentAsync(Stream stream, bool allowImages, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length == 0) return null; // Keepalive
        if (length == int.MinValue && allowImages) return new LanClipboardContent.Unpaired();
        if (length < 0 && allowImages)
        {
            if (length < -LanPng.MaxBytes) throw new InvalidDataException("Invalid LAN image frame length.");
            byte[] png = new byte[-length];
            await stream.ReadExactlyAsync(png, ct);
            return new LanClipboardContent.Image(await Task.Run(() => LanPng.Validate(png, ct), ct));
        }
        if (length is < 0 or > SyncTransport.MaxTextBytes) throw new InvalidDataException("Invalid LAN clipboard frame length.");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        try { return new LanClipboardContent.Text(SyncTransport.Utf8.GetString(bytes)); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid UTF-8 in LAN clipboard frame.", ex); }
    }

    public static ValueTask KeepAliveAsync(Stream stream, CancellationToken ct) => stream.WriteAsync(new byte[4], ct);
    // Sent only on a connection that carries pictures: a version without them would take it for a broken frame.
    public static ValueTask WriteUnpairedAsync(Stream stream, CancellationToken ct) => stream.WriteAsync(new byte[] { 0x80, 0, 0, 0 }, ct);
}
