using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BrimDeck.Core.ClipboardSync;

public sealed class LanPairingRateLimitedException()
    : IOException("Too many pairing attempts.");

public static partial class LanClipboardLink
{
    // These limits span all codes in this process. Issuing another code does not reset them.
    private static readonly LanPairingAttempts HostAttempts = new(10, 3, TimeSpan.FromMinutes(5));
    private static readonly LanPairingAttempts GuestAttempts = new(10, 3, TimeSpan.FromMinutes(5));

    public static bool IsPairingCode(string code) => LanPairingHandshake.IsCode(code);

    public static Task<IReadOnlyList<LanPairingDevice>> DiscoverPairingDevicesAsync(CancellationToken cancellation) =>
        LanDiscovery.FindAsync(LanNetwork.Interfaces(), cancellation);

    public static async Task<LanClipboardPeer> JoinPairingAsync(LanPairingDevice device, string code, string deviceName,
        Func<LanPairingConfirmation, CancellationToken, Task<bool>> confirm, CancellationToken cancellation)
    {
        if (!IsPairingCode(code)) throw new ArgumentException("Expected a six-digit pairing code.", nameof(code));
        if (device.Session == Guid.Empty || device.Port is < 1 or > 65535 ||
            !LanNetwork.Interfaces().Any(adapter => adapter.Address.Equals(device.LocalAddress) && LanNetwork.IsOnSubnet(device.Address, adapter)))
            throw new LanPeerOffSubnetException();
        if (!GuestAttempts.TryBegin(device.Address)) throw new LanPairingRateLimitedException();

        using var network = new NetworkLifetime(cancellation);
        using var socket = new TcpClient(new IPEndPoint(device.LocalAddress, 0)) { NoDelay = true };
        using var connect = Timed(network.Token);
        await socket.ConnectAsync(device.Address, device.Port, connect.Token).ConfigureAwait(false);
        // Trust is provisional until both PAKE binding tags have authenticated this exact certificate.
        using var stream = new SslStream(socket.GetStream(), false, (_, certificate, _, _) => certificate is not null);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "BrimDeck LAN",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, connect.Token).ConfigureAwait(false);
        byte[] pin = stream.RemoteCertificate?.GetCertHash(HashAlgorithmName.SHA256)
            ?? throw new AuthenticationException("Missing pairing certificate.");
        string? verifiedName = null;
        byte[] key = await LanPairingHandshake.ConfirmAsync(stream, false, device.Session, pin, code, deviceName, (details, token) =>
        {
            verifiedName = details.DeviceName;
            return confirm(details, token);
        }, network.Token).ConfigureAwait(false);
        try
        {
            using var complete = Timed(network.Token);
            byte[] packet = await LanPairingHandshake.ReceiveAsync(stream, 8, 60, complete.Token).ConfigureAwait(false);
            byte[] secret = OpenCredential(packet, key, pin);
            await LanPairingHandshake.SendAsync(stream, 9, [1], complete.Token).ConfigureAwait(false);
            if (!(await LanPairingHandshake.ReceiveAsync(stream, 10, 1, complete.Token).ConfigureAwait(false)).AsSpan().SequenceEqual(new byte[] { 1 }))
                throw new AuthenticationException("Pairing did not finish.");
            complete.Token.ThrowIfCancellationRequested();
            return new(device.Address.ToString(), device.Port, pin, secret, null) { DeviceName = verifiedName };
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public sealed class PairingHost : IDisposable
    {
        private readonly X509Certificate2 _certificate;
        private readonly List<(LanInterface Adapter, TcpListener Listener)> _listeners = [];
        private readonly string _deviceName;
        private readonly Guid _session = Guid.NewGuid();
        private bool _used;
        public string Code { get; } = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);

        public PairingHost(string deviceName)
        {
            _deviceName = LanPairingHandshake.CheckName(deviceName);
            _certificate = CreatePairingCertificate();
            try
            {
                foreach (var adapter in LanNetwork.Interfaces().Where(a => LanNetwork.BroadcastAddress(a) is not null)
                    .DistinctBy(a => a.Address).Take(16))
                {
                    try { _listeners.Add((adapter, Listen(adapter.Address, 0))); }
                    catch (SocketException) { }
                }
                if (_listeners.Count == 0) throw new LanHostAddressUnavailableException();
            }
            catch { Dispose(); throw; }
        }

        public async Task<LanClipboardPeer> AcceptAsync(Func<LanPairingConfirmation, CancellationToken, Task<bool>> confirm,
            CancellationToken cancellation)
        {
            if (_used) throw new InvalidOperationException("This pairing code has already been used.");
            _used = true;
            using var network = new NetworkLifetime(cancellation);
            using var expiry = CancellationTokenSource.CreateLinkedTokenSource(network.Token);
            expiry.CancelAfter(TimeSpan.FromMinutes(10));
            var token = expiry.Token;
            var advertised = _listeners.Select(pair => (pair.Adapter, ((IPEndPoint)pair.Listener.LocalEndpoint).Port)).ToArray();
            Task discovery = LanDiscovery.ReplyAsync(advertised, _session, _deviceName, token);
            var pending = new Task<TcpClient>?[_listeners.Count];
            int failures = 0;
            try
            {
                for (int i = 0; i < pending.Length; i++)
                    pending[i] = _listeners[i].Listener.AcceptTcpClientAsync(token).AsTask();
                while (failures < 5)
                {
                    var ready = await Task.WhenAny(pending.OfType<Task>().Append(discovery)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (ready == discovery)
                    {
                        await discovery.ConfigureAwait(false);
                        throw new IOException("Pairing discovery stopped.");
                    }
                    var accepting = (Task<TcpClient>)ready;
                    int index = Array.IndexOf(pending, accepting);
                    var (adapter, listener) = _listeners[index];
                    pending[index] = null;
                    using var socket = await accepting.ConfigureAwait(false);
                    pending[index] = listener.AcceptTcpClientAsync(token).AsTask();
                    var address = ((IPEndPoint)socket.Client.RemoteEndPoint!).Address;
                    if (!LanNetwork.IsOnSubnet(address, adapter) || !HostAttempts.TryBegin(address)) continue;
                    bool verifying = false;
                    try
                    {
                        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                        using var handshake = Timed(token);
                        using var stream = await SecureServerAsync(socket, _certificate, handshake.Token).ConfigureAwait(false);
                        byte[] pin = stream.LocalCertificate?.GetCertHash(HashAlgorithmName.SHA256)
                            ?? throw new AuthenticationException("Missing local pairing certificate.");
                        string? verifiedName = null;
                        byte[] key = await LanPairingHandshake.ConfirmAsync(stream, true, _session, pin, Code, _deviceName, (details, ct) =>
                        {
                            verifying = true;
                            verifiedName = details.DeviceName;
                            return confirm(details, ct);
                        }, token).ConfigureAwait(false);
                        try
                        {
                            using var complete = Timed(token);
                            byte[] secret = RandomNumberGenerator.GetBytes(KeySize);
                            await LanPairingHandshake.SendAsync(stream, 8, SealCredential(secret, key, pin), complete.Token).ConfigureAwait(false);
                            if (!(await LanPairingHandshake.ReceiveAsync(stream, 9, 1, complete.Token).ConfigureAwait(false)).AsSpan().SequenceEqual(new byte[] { 1 }))
                                throw new AuthenticationException("Missing pairing receipt.");
                            await LanPairingHandshake.SendAsync(stream, 10, [1], complete.Token).ConfigureAwait(false);
                            complete.Token.ThrowIfCancellationRequested();
                            return new(adapter.Address.ToString(), port, pin, secret, _certificate.Export(X509ContentType.Pkcs12))
                            { DeviceName = verifiedName };
                        }
                        finally { CryptographicOperations.ZeroMemory(key); }
                    }
                    catch (Exception ex) when (!verifying && RejectedConnection(ex) && !token.IsCancellationRequested)
                    {
                        failures++;
                        await Task.Delay(TimeSpan.FromMilliseconds(300 * failures), token).ConfigureAwait(false);
                    }
                }
                throw new LanPairingRateLimitedException();
            }
            catch (OperationCanceledException) when (network.Token.IsCancellationRequested && !cancellation.IsCancellationRequested)
            { throw new LanNetworkChangedException(); }
            finally
            {
                expiry.Cancel();
                foreach (var entry in _listeners) entry.Listener.Stop();
                foreach (var task in pending.OfType<Task<TcpClient>>())
                {
                    try { (await task.ConfigureAwait(false)).Dispose(); }
                    catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
                }
                try { await discovery.ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            foreach (var entry in _listeners) entry.Listener.Stop();
            _certificate.Dispose();
        }
    }

    private static X509Certificate2 CreatePairingCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=BrimDeck LAN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private static byte[] SealCredential(byte[] secret, byte[] key, byte[] pin)
    {
        byte[] packet = new byte[60];
        RandomNumberGenerator.Fill(packet.AsSpan(0, 12));
        using var cipher = new AesGcm(key, 16);
        cipher.Encrypt(packet.AsSpan(0, 12), secret, packet.AsSpan(12, 32), packet.AsSpan(44, 16), pin);
        return packet;
    }

    private static byte[] OpenCredential(byte[] packet, byte[] key, byte[] pin)
    {
        if (packet.Length != 60) throw new InvalidDataException("Invalid pairing credential.");
        byte[] secret = new byte[32];
        using var cipher = new AesGcm(key, 16);
        cipher.Decrypt(packet.AsSpan(0, 12), packet.AsSpan(12, 32), packet.AsSpan(44, 16), secret, pin);
        return secret;
    }
}
