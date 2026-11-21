using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Blake3.Managed;
using Grpc.Core;
using Grpc.Net.Client;

namespace BrimDeck.Core.ClipboardSync;

// Index and Count: which of the files in this transfer it is (from 1), and how many there are.
public readonly record struct WeTypeSendProgress(string FileName, long Sent, long Total, int Index = 1, int Count = 1);

internal sealed class WeTypeTransferCertificate : IDisposable
{
    public string AuthorityPem { get; }
    public X509Certificate2 Client { get; }
    public X509Certificate2? Server { get; }
    public string ServerName { get; } = "brimdeck-" + Guid.NewGuid().ToString("N") + ".local";

    public WeTypeTransferCertificate(bool server = false)
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=BrimDeck transfer CA", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        var end = start.AddDays(1);
        using var root = rootRequest.CreateSelfSigned(start, end);
        AuthorityPem = root.ExportCertificatePem();

        using var clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=BrimDeck", clientKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var leaf = request.Create(root, start, end, RandomNumberGenerator.GetBytes(16));
        using var withKey = leaf.CopyWithPrivateKey(clientKey);
        Client = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        if (server)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var csr = new CertificateRequest("CN=" + ServerName, key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(ServerName);
            csr.CertificateExtensions.Add(names.Build());
            csr.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using var signed = csr.Create(root, start, end, RandomNumberGenerator.GetBytes(16));
            using var pair = signed.CopyWithPrivateKey(key);
            Server = X509CertificateLoader.LoadPkcs12(pair.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        }
    }

    public void Dispose() { Client.Dispose(); Server?.Dispose(); }
}

internal static class WeTypeFileStream
{
    // The peer advertises an IP address for TCP and a DNS name for its TLS identity.
    private sealed class PeerAuthorityHandler(string authority, HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.Host = authority;
            return base.SendAsync(request, ct);
        }
    }

    internal const int MaxFrameSize = BlockSize + 4096;
    private static readonly Marshaller<WeTypeWire> Codec = Marshallers.Create<WeTypeWire>(wire => wire.Encode(), bytes => WeTypeWire.Parse(bytes, MaxFrameSize));
    private static readonly Method<WeTypeWire, WeTypeWire> Transfer = new(MethodType.DuplexStreaming, "flurry.FileService", "TransferFiles", Codec, Codec);
    private const int BlockSize = 2 * 1024 * 1024;
    private const int Wxp2pBlockSize = 256 * 1024;

    internal static async Task<GrpcChannel> ConnectAsync(WeTypeTransferCertificate identity, WeTypeWire peer, CancellationToken ct, Action<string>? trace = null)
    {
        using var authority = X509Certificate2.CreateFromPem(peer.GetText(2));
        using var json = JsonDocument.Parse(peer.GetBytes(6));
        var root = json.RootElement;
        if (!root.TryGetProperty("available_nets", out var nets) || !nets.TryGetProperty("lan", out var lan) ||
            lan.ValueKind != JsonValueKind.Array) throw new IOException("No direct file connection.");
        var serverName = root.TryGetProperty("server_name", out var name) ? name.GetString() : null;
        byte[] authorityDer = authority.RawData;
        foreach (var entry in lan.EnumerateArray().Take(16))
        {
            if (!entry.TryGetProperty("ip", out var ip) || !IPAddress.TryParse(ip.GetString(), out var address) ||
                IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
                !entry.TryGetProperty("port", out var number) || !number.TryGetInt32(out int port) || port is <= 0 or > 65535) continue;
            trace?.Invoke($"Trying {address.AddressFamily} endpoint; peer supplied a TLS name: {!string.IsNullOrEmpty(serverName)}.");
            var handler = new SocketsHttpHandler { UseProxy = false, SslOptions = new SslClientAuthenticationOptions
            {
                LocalCertificateSelectionCallback = (_, _, _, _, _) => identity.Client,
                ClientCertificates = new X509CertificateCollection { identity.Client },
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is not X509Certificate2 leaf) return false;
                    using var ca = X509CertificateLoader.LoadCertificate(authorityDer);
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(ca);
                    chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.DisableCertificateDownloads = true;
                    bool trusted = chain.Build(leaf);
                    bool matches = string.IsNullOrEmpty(serverName) || leaf.MatchesHostname(serverName);
                    trace?.Invoke($"Peer certificate: trusted={trusted}, name matches={matches}.");
                    return trusted && matches;
                }
            }};
            var channel = GrpcChannel.ForAddress(new UriBuilder("https", address.ToString(), port).Uri,
                new GrpcChannelOptions { HttpHandler = string.IsNullOrEmpty(serverName) ? handler : new PeerAuthorityHandler(serverName, handler), DisposeHttpClient = true, MaxReceiveMessageSize = MaxFrameSize });
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(4));
                await channel.ConnectAsync(attempt.Token);
                return channel;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is OperationCanceledException or HttpRequestException or RpcException)
            { channel.Dispose(); }
            catch { channel.Dispose(); throw; }
        }
        throw new IOException("No direct file connection.");
    }

    private static WeTypeWire Action(uint kind, WeTypeWire? file = null)
    {
        var action = new WeTypeWire().Number(1, kind);
        if (file != null) action.Message(2, file);
        return new WeTypeWire().Message(1, action);
    }

    internal static async Task SendAsync(GrpcChannel channel, IReadOnlyList<string> paths,
        IProgress<WeTypeSendProgress> progress, CancellationToken ct, Action<string>? trace = null)
    {
        using var stream = channel.CreateCallInvoker().AsyncDuplexStreamingCall(Transfer, null, new CallOptions(cancellationToken: ct));
        await SendOnStreamAsync(stream.RequestStream, stream.ResponseStream, paths, progress, ct, trace);
    }

    internal static async Task ReceiveAsClientAsync(GrpcChannel channel, WeTypeIncomingCall state, CancellationToken ct)
    {
        using var stream = channel.CreateCallInvoker().AsyncDuplexStreamingCall(Transfer, null, new CallOptions(cancellationToken: ct));
        await stream.RequestStream.WriteAsync(Action(100), ct);
        await state.ReceiveAsync(stream.ResponseStream, stream.RequestStream, ct, helloSent: true);
        await stream.RequestStream.CompleteAsync().WaitAsync(ct);
    }

    internal static async Task SendOnStreamAsync(IClientStreamWriter<WeTypeWire> request, IAsyncStreamReader<WeTypeWire> response,
        IReadOnlyList<string> paths, IProgress<WeTypeSendProgress> progress, CancellationToken ct, Action<string>? trace = null)
    {
        await request.WriteAsync(Action(100), ct);
        byte[] buffer = new byte[BlockSize];
        ulong sequence = 0;
        foreach (var path in paths)
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, true);
            string name = Path.GetFileName(path);
            long length = source.Length;
            progress.Report(new(name, 0, length));
            using var hash = Hasher.New();
            int count;
            while ((count = await source.ReadAsync(buffer, ct)) > 0) hash.Update(buffer.AsSpan(0, count));
            string id = Convert.ToHexStringLower(hash.Finalize().AsSpan());
            source.Position = 0;
            var file = new WeTypeWire().Text(1, id).Text(2, name).Number(3, (ulong)length);
            await request.WriteAsync(Action(1, file).Number(6, sequence++), ct);
            // Each file has one data sequence. Offsets distinguish its chunks.
            ulong dataSequence = sequence++;
            do
            {
                long start = source.Position;
                count = (int)Math.Min(length - start, buffer.Length);
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                long end = source.Position;
                await request.WriteAsync(Action(2, file).Bytes(2, buffer[..count]).Number(3, (ulong)start)
                    .Number(4, (ulong)end).Number(6, dataSequence), ct);
                using var ack = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ack.CancelAfter(TimeSpan.FromSeconds(60));
                while (true)
                {
                    if (!await response.MoveNext(ack.Token)) throw new EndOfStreamException();
                    var message = response.Current;
                    var action = message.GetMessage(1);
                    if (action.GetNumber(1) == 100) continue;
                    if (action.GetNumber(1) == 200 && message.Has(8))
                    {
                        await request.WriteAsync(Action(5).Bytes(8, message.GetBytes(8)), ack.Token);
                        if (WeTypeWire.Parse(message.GetBytes(5)).GetNumber(2) == 1001) throw new EndOfStreamException();
                        continue;
                    }
                    if (action.GetNumber(1) == 4 && action.GetMessage(2).GetText(1) == id)
                        throw new IOException("The receiving device canceled the file.");
                    if (!IsRangeAck(message, id, start, end)) throw new IOException("The peer did not acknowledge the file range.");
                    break;
                }
                progress.Report(new(name, end, length));
            } while (source.Position < length);
        }
        trace?.Invoke("All file ranges acknowledged.");
        await request.CompleteAsync().WaitAsync(ct);
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closing.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (await response.MoveNext(closing.Token))
            {
                if (response.Current.GetMessage(1).GetNumber(1) == 4)
                    throw new IOException("The receiving device canceled the file.");
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && !ct.IsCancellationRequested)
        {
            // The native receiver can return CANCELLED at EOF after acknowledging and saving the file.
            trace?.Invoke("Peer closed the acknowledged transfer after request EOF.");
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            trace?.Invoke("Peer kept the acknowledged transfer open after request EOF.");
        }
    }

    internal static async Task SendOverWxp2pAsync(WeTypeWxp2pSession session, IReadOnlyList<string> paths,
        IProgress<WeTypeSendProgress> progress, CancellationToken ct, Action<string>? trace = null, bool hangUp = true)
    {
        await session.WaitForPeerReadyAsync(TimeSpan.FromSeconds(15), ct);
        byte[] buffer = new byte[Wxp2pBlockSize];
        ulong sequence = 0;
        // Cancelled before the reader is disposed: a reader cannot be disposed while a read is pending.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var responses = session.IncomingCommands(stop.Token).GetAsyncEnumerator(stop.Token);
        Task<bool>? pendingMove = null;
        Task<bool>? lastMove = null;
        Task? reader = null;
        // One command at a time: a large command is sent as consecutive fragments and must not be split by another.
        using var sendLock = new SemaphoreSlim(1, 1);

        async Task SendChunkAsync(WeTypeWire chunk, CancellationToken token)
        {
            await sendLock.WaitAsync(token);
            try
            {
                var command = new WeTypeFileDataCommand(session.NextCommandId(), chunk);
                await session.SendFileCommandAsync(command.Encode(), token);
            }
            finally { sendLock.Release(); }
        }

        // Kind 100 opens the stream and kind 200 carries the peer's control messages; each 200 is acknowledged with
        // kind 5 and the same field 8. Returns false for any other message.
        async Task<bool> HandleControlAsync(uint trunk, WeTypeWire message, CancellationToken token)
        {
            ulong kind = message.GetMessage(1).GetNumber(1);
            if (kind == 100) return true;
            if (kind != 200) return false;
            var envelope = message.Has(5) ? WeTypeWire.Parse(message.GetBytes(5)) : new WeTypeWire();
            trace?.Invoke($"WXP2P peer control: trunk={trunk}, command={envelope.GetNumber(2)}, data={WeTypeWire.Describe(envelope.GetBytes(3))}.");
            if (message.Has(8))
                await SendChunkAsync(Action(5).Bytes(8, message.GetBytes(8)), token);
            if (envelope.GetNumber(2) == 1001) throw new EndOfStreamException();
            return true;
        }

        // The phone sends its opening message after the hello exchange; the official client waits for it before the
        // first file. A peer that sends nothing is given the file after the wait.
        using (var opening = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            opening.CancelAfter(TimeSpan.FromSeconds(5));
            var move = responses.MoveNextAsync().AsTask();
            try
            {
                if (!await move.WaitAsync(opening.Token)) throw new EndOfStreamException();
                var first = WeTypeFileDataCommand.Decode(responses.Current.Span);
                var action = first.FileChunk.GetMessage(1);
                trace?.Invoke($"WXP2P peer opening message: trunk={first.ChunkId}, action={action.GetNumber(1)}, fields=[{string.Join(",", first.FileChunk.FieldNumbers())}], actionFields=[{string.Join(",", action.FieldNumbers())}].");
                if (!await HandleControlAsync(first.ChunkId, first.FileChunk, ct))
                    trace?.Invoke("WXP2P peer opening message is not a control message; it is ignored.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                trace?.Invoke("WXP2P peer sent no opening message within 5 s; sending the file anyway.");
                pendingMove = move;
            }
        }

        // Over the relay the phone does not acknowledge each range before the next one (it only sends control
        // messages), and the official client streams the whole file. Replies are read in the background; the
        // reliable layer's acknowledgements pace the data and show that it arrived.
        string currentId = "";
        long rangeAcked = -1;
        try
        {
            reader = Task.Run(async () =>
            {
                while (true)
                {
                    var moveTask = pendingMove ?? responses.MoveNextAsync().AsTask();
                    pendingMove = null;
                    Volatile.Write(ref lastMove, moveTask);
                    if (!await moveTask.WaitAsync(stop.Token)) throw new EndOfStreamException();
                    var decoded = WeTypeFileDataCommand.Decode(responses.Current.Span);
                    var message = decoded.FileChunk;
                    var action = message.GetMessage(1);
                    ulong kind = action.GetNumber(1);
                    if (await HandleControlAsync(decoded.ChunkId, message, stop.Token)) continue;
                    trace?.Invoke($"WXP2P recv FileChunk: trunk={decoded.ChunkId}, action={kind}, start={message.GetNumber(3)}, end={message.GetNumber(4)}.");
                    string fileId = action.GetMessage(2).GetText(1);
                    if (kind == 4 && fileId == Volatile.Read(ref currentId))
                        throw new IOException("The receiving device canceled the file.");
                    if (kind == 5 && fileId == Volatile.Read(ref currentId) && message.Has(4))
                        Interlocked.Exchange(ref rangeAcked, (long)message.GetNumber(4));
                }
            }, stop.Token);

            void ThrowIfReaderFailed()
            {
                if (reader!.IsFaulted) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(reader.Exception!.InnerException!).Throw();
            }

            // Waits until the peer's reliable layer has acknowledged every packet sent so far.
            async Task DrainAsync(TimeSpan limit)
            {
                var stalled = System.Diagnostics.Stopwatch.StartNew();
                int last = session.UnackedCount;
                while (session.UnackedCount > 0)
                {
                    ThrowIfReaderFailed();
                    int now = session.UnackedCount;
                    if (now < last) { last = now; stalled.Restart(); }
                    if (stalled.Elapsed > limit) throw new IOException("The peer stopped acknowledging data.");
                    await Task.Delay(50, ct);
                }
            }

            try
            {
                foreach (var path in paths)
                {
                    await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Wxp2pBlockSize, true);
                    string name = Path.GetFileName(path);
                    long length = source.Length;
                    progress.Report(new(name, 0, length));
                    using var hash = Hasher.New();
                    int count;
                    while ((count = await source.ReadAsync(buffer, ct)) > 0) hash.Update(buffer.AsSpan(0, count));
                    string id = Convert.ToHexStringLower(hash.Finalize().AsSpan());
                    source.Position = 0;
                    Volatile.Write(ref currentId, id);
                    Interlocked.Exchange(ref rangeAcked, -1);

                    var file = new WeTypeWire().Text(1, id).Text(2, name).Number(3, (ulong)length);
                    trace?.Invoke($"WXP2P sending file START (len={length}).");
                    await SendChunkAsync(Action(1, file).Number(6, sequence++), ct);
                    ulong dataSequence = sequence++;
                    int traced = 0;
                    do
                    {
                        ThrowIfReaderFailed();
                        long start = source.Position;
                        count = (int)Math.Min(length - start, buffer.Length);
                        await source.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                        long end = source.Position;
                        if (traced++ < 3) trace?.Invoke($"WXP2P sending file DATA range {start}..{end}.");
                        // The send waits while the window is full; a peer that stops acknowledging ends the transfer.
                        using (var window = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            window.CancelAfter(TimeSpan.FromSeconds(60));
                            try
                            {
                                await SendChunkAsync(Action(2, file).Bytes(2, buffer[..count]).Number(3, (ulong)start)
                                    .Number(4, (ulong)end).Number(6, dataSequence), window.Token);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                throw new IOException("The peer stopped acknowledging data.");
                            }
                        }
                        // When a block is handed over, at most one window of it is still in flight, so the previous block
                        // has arrived.
                        progress.Report(new(name, Math.Max(start, Interlocked.Read(ref rangeAcked)), length));
                    } while (source.Position < length);

                    await DrainAsync(TimeSpan.FromSeconds(60));
                    trace?.Invoke($"WXP2P file data acknowledged by the peer's reliable layer; range ACK end={Interlocked.Read(ref rangeAcked)}.");
                    progress.Report(new(name, length, length));
                }

                // Give the phone a moment for any closing message, and log it, before hanging up.
                var settle = System.Diagnostics.Stopwatch.StartNew();
                while (settle.Elapsed < TimeSpan.FromSeconds(3) && !reader!.IsCompleted) await Task.Delay(100, ct);
                ThrowIfReaderFailed();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Closing the window cancels the file on the phone; otherwise it keeps showing the transfer.
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    string id = Volatile.Read(ref currentId);
                    if (id.Length > 0)
                        await SendChunkAsync(Action(3, new WeTypeWire().Text(1, id)).Number(6, sequence++), bye.Token);
                    await session.SendHangupAsync(bye.Token);
                    trace?.Invoke("WXP2P transfer canceled; told the peer.");
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
                throw;
            }
            trace?.Invoke("All file data delivered over WXP2P.");
            if (hangUp) await session.SendHangupAsync(ct);
        }
        finally
        {
            stop.Cancel();
            try { if (reader != null) await reader; }
            catch (Exception) when (stop.IsCancellationRequested) { }
            // The read itself must finish too, not only the wait for it.
            if ((Volatile.Read(ref lastMove) ?? pendingMove) is { } read)
            {
                try { await read; }
                catch (Exception) when (stop.IsCancellationRequested) { }
            }
        }
    }

    internal static bool IsRangeAck(WeTypeWire message, string id, long start, long end)
    {
        var action = message.GetMessage(1);
        return action.GetNumber(1) == 5 && action.GetMessage(2).GetText(1) == id && message.Has(3) && message.Has(4) &&
            message.GetNumber(3) == (ulong)start && message.GetNumber(4) == (ulong)end;
    }
}
