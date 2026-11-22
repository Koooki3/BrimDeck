using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BrimDeck.Core.ClipboardSync;

public sealed class WeTypeFileInvite(string code, ulong sender, string name)
{
    internal string Code { get; } = code;
    public ulong Sender { get; } = sender;
    public string Name { get; } = name;
    public bool SameOffer(WeTypeFileInvite? other) => other != null && Sender == other.Sender && Code == other.Code;
}

public sealed partial class WeTypeSyncClient
{
    public async Task<WeTypeFileInvite?> IncomingFileAsync(CancellationToken ct)
    {
        if (_key is null) await InitializeAsync(ct);
        var group = await GroupAsync(ct, fileInvites: true);
        return ReadIncomingFile(group);
    }

    public Action<WeTypeFileInvite?>? FileInviteChanged { get; set; }

    private string _invitesListed = "";

    private WeTypeFileInvite? ReadIncomingFile(WeTypeWire group)
    {
        if (Trace is { } trace)
        {
            string listed = string.Join("; ", group.Messages(7).Select(invite =>
                $"code#{Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(invite.GetText(1))))[..6]} " +
                $"self={invite.GetNumber(3) == identity.Uin} time={invite.GetNumber(4)} platform={invite.GetNumber(5)} caps={invite.GetNumber(6)}"));
            if (listed != _invitesListed)
            {
                _invitesListed = listed;
                trace($"wetype pending invitations ({group.Messages(7).Count()}): {(listed.Length > 0 ? listed : "none")}");
            }
        }
        return LatestIncomingFile(group, identity.Uin);
    }

    internal static WeTypeFileInvite? LatestIncomingFile(WeTypeWire group, ulong self)
    {
        foreach (var invite in group.Messages(7).Reverse().OrderByDescending(item => item.GetNumber(4)))
        {
            string code = invite.GetText(1);
            ulong sender = invite.GetNumber(3);
            if (sender == 0 || sender == self || code.Length is 0 or > 256 || code.Any(c => c is < '!' or > '~')) continue;
            var member = group.Messages(2).FirstOrDefault(d => d.GetNumber(1) == sender);
            if (member != null) return new(code, sender, member.GetMessage(2).GetText(1));
        }
        return null;
    }

    // The service keeps the phone's last invitation in the list after it ends (canceled, declined or finished), so an
    // invitation seen for the first time, at startup above all, is asked about before anything is shown for it.
    public async Task<bool> FileInviteLiveAsync(WeTypeFileInvite invite, CancellationToken ct)
    {
        if (_key is null) await InitializeAsync(ct);
        var current = await ApiAsync(75, new WeTypeWire().Text(1, invite.Code), ct);
        Success(current);
        var issuer = current.GetMessage(2);
        ulong id = issuer.GetNumber(1);
        return (id == 0 || id == invite.Sender) && issuer.GetNumber(11) is not (1 or 2 or 4);
    }

    public async Task DeclineFileAsync(WeTypeFileInvite invite, CancellationToken ct)
    {
        if (_key == null) await InitializeAsync(ct);
        var current = await ApiAsync(75, new WeTypeWire().Text(1, invite.Code), ct);
        Success(current);
        if (current.GetMessage(2).GetNumber(1) != invite.Sender) return;
        Success(await ApiAsync(74, new WeTypeWire().Text(1, invite.Code).Number(6, 2), ct));
    }

    public async Task ReceiveFilesAsync(WeTypeFileInvite invite, Task<string> destination, IProgress<WeTypeSendProgress> progress,
        Action<string> saved, Action connected, Action fileOffered, CancellationToken ct)
    {
        if (!(await FileDevicesAsync(ct)).Any(device => device.Id == invite.Sender))
            throw new InvalidOperationException("Sender is no longer paired.");
        var offer = await ApiAsync(75, new WeTypeWire().Text(1, invite.Code), ct);
        Success(offer);
        var issuer = offer.GetMessage(2);
        ulong issuerId = issuer.GetNumber(1);
        ulong issuerState = issuer.GetNumber(11);
        if ((issuerId != 0 && issuerId != invite.Sender) || issuerState is 1 or 2)
        {
            FileTrace?.Invoke($"Rejected incoming offer: issuerId={issuerId}, expected={invite.Sender}, state={issuerState}.");
            throw new InvalidDataException("Sender changed or withdrew the invitation.");
        }
        bool preferClient = ShouldDialSender(issuer);
        var ips = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .ThenByDescending(n => n.GetIPProperties().GatewayAddresses.Count != 0)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address)
            .Where(a => !IPAddress.IsLoopback(a) && (a.AddressFamily == AddressFamily.InterNetwork ||
                Socket.OSSupportsIPv6 && a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal))
            .Select(a => a.ToString()).Distinct().Take(32).ToArray();
        if (ips.Length == 0) throw new IOException("No active network interface.");
        using var credentials = new WeTypeTransferCertificate(server: true);
        var state = new WeTypeIncomingCall(new(destination, progress, saved, FileTrace, fileOffered), connected);
        var authority = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateFileListener(credentials, state, authority.Task, Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any);
        await server.StartAsync(ct);
        try
        {
            int port = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
            var grpc = new { platform = 1, server_name = credentials.ServerName, device_name = Name,
                available_nets = new { lan = ips.Select(ip => new { ip, port }) } };
            WeTypeWire Join(bool clientRole) => new WeTypeWire().Text(1, invite.Code).Text(2, credentials.AuthorityPem)
                .Text(3, JsonSerializer.Serialize(new { lan = ips, device_name = Name, preferred_client = clientRole }))
                .Text(4, JsonSerializer.Serialize(grpc)).Number(7, 2);
            var reply = await ApiAsync(74, Join(preferClient), ct);
            Success(reply);
            WeTypeFileDispatch? relayDispatch = TryParseRelay(reply) ?? TryParseRelay(offer);
            FileTrace?.Invoke($"Joined the paired invitation; advertised {ips.Length} addresses; preferred client: {preferClient}; offerDispatch={offer.GetBytes(5).Length}, replyDispatch={reply.GetBytes(5).Length}, replyPeerDispatch={reply.GetBytes(6).Length}, issuerState={issuer.GetNumber(11)}, issuerCaps={issuer.GetNumber(13)}, peerCaps={reply.GetMessage(2).GetNumber(13)}.");
            if (relayDispatch != null)
                FileTrace?.Invoke("Received WXP2P relay candidates for the incoming invitation.");
            var started = System.Diagnostics.Stopwatch.StartNew();
            bool reversed = false;
            string? lastOffer = null;
            byte[]? trusted = null;
            bool certificateChangeLogged = false;
            Task? relayTask = null;
            WeTypeStaleTransferException? staleTransfer = null;
            CancellationTokenSource? relayCts = null;
            void StartRelayIfReady()
            {
                if (relayTask != null || relayDispatch is not { CanRelay: true } || state.Connected.Task.IsCompleted) return;
                relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var dispatch = relayDispatch;
                var relayToken = relayCts.Token;
                FileTrace?.Invoke("Starting WXP2P relay listener for incoming files.");
                relayTask = Task.Run(async () =>
                {
                    try
                    {
                        await using var session = await WeTypeWxp2pSession.ConnectAsync(dispatch, relayToken, FileTrace);
                        await using var commands = session.IncomingCommands(relayToken).GetAsyncEnumerator(relayToken);
                        if (!await commands.MoveNextAsync()) return;
                        var first = commands.Current;
                        await state.ReceiveWxp2pAsync(session, first, commands, ct);
                    }
                    catch (OperationCanceledException) when (relayToken.IsCancellationRequested || state.Connected.Task.IsCompleted) { }
                    catch (WeTypeStaleTransferException ex) { staleTransfer = ex; }
                    catch (Exception ex) when (ex is IOException or SocketException)
                    {
                        FileTrace?.Invoke($"WXP2P relay receive stopped: {ex.GetType().Name}: {ex.Message}");
                    }
                }, relayToken);
            }
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            setup.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                try
                {
                    while (!state.Connected.Task.IsCompleted)
                    {
                        if (relayDispatch == null)
                        {
                            relayDispatch = TryParseRelay(reply);
                            if (relayDispatch != null)
                                FileTrace?.Invoke("Received WXP2P relay candidates for the incoming invitation.");
                        }
                        var peer = reply.GetMessage(2);
                        if (peer.GetNumber(1) != 0)
                        {
                            if (peer.GetNumber(1) != invite.Sender || peer.GetNumber(11) is 1 or 2)
                            {
                                FileTrace?.Invoke($"Incoming offer ended during poll: peerId={peer.GetNumber(1)}, state={peer.GetNumber(11)}.");
                                throw new InvalidDataException("Sender changed or withdrew the invitation.");
                            }
                            if (peer.GetBytes(2).Length is > 0 and <= 16384)
                            {
                                using var ca = X509Certificate2.CreateFromPem(peer.GetText(2));
                                // The phone renews its certificate while it waits for the relay. Each one arrives in the same
                                // authenticated service response; the listener keeps the first, and the relay uses neither.
                                if (trusted != null && !trusted.AsSpan().SequenceEqual(ca.RawData) && !certificateChangeLogged)
                                {
                                    certificateChangeLogged = true;
                                    FileTrace?.Invoke("Sender renewed its certificate; the listener keeps the first one.");
                                }
                                if (trusted == null)
                                {
                                    trusted = ca.RawData;
                                    authority.SetResult(trusted);
                                    FileTrace?.Invoke("Matched sender certificate; listening for its file stream.");
                                }
                                var endpoint = peer.GetText(6);
                                if (endpoint.Length > 0 && endpoint != lastOffer)
                                {
                                    lastOffer = endpoint;
                                    Grpc.Net.Client.GrpcChannel? channel = null;
                                    try { channel = await WeTypeFileStream.ConnectAsync(credentials, peer, setup.Token, FileTrace); }
                                    catch (IOException)
                                    {
                                        FileTrace?.Invoke("Sender endpoints unreachable; continuing to listen.");
                                        StartRelayIfReady();
                                    }
                                    using (channel)
                                    {
                                        if (channel != null && !state.Connected.Task.IsCompleted)
                                        {
                                            FileTrace?.Invoke("Opened the sender's file channel.");
                                            await WeTypeFileStream.ReceiveAsClientAsync(channel, state, ct);
                                        }
                                    }
                                }
                            }
                        }
                        if (Volatile.Read(ref staleTransfer) is { } stale)
                        {
                            // The phone keeps sending until it is told: reject the invitation so it ends, then ask for a resend.
                            try { await ApiAsync(74, new WeTypeWire().Text(1, invite.Code).Number(6, 2), ct); }
                            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException) { }
                            throw stale;
                        }
                        if (started.Elapsed >= TimeSpan.FromSeconds(3)) StartRelayIfReady();
                        if (await Task.WhenAny(state.Connected.Task, Task.Delay(800, setup.Token)) == state.Connected.Task) break;
                        setup.Token.ThrowIfCancellationRequested();
                        if (preferClient && !reversed && started.Elapsed >= TimeSpan.FromSeconds(8))
                        {
                            // If the peer never opened a usable listener it can still dial this same session.
                            reversed = true;
                            FileTrace?.Invoke("Sender did not open a file channel; offering the existing listener.");
                            reply = await ApiAsync(74, Join(false), setup.Token);
                        }
                        else reply = await ApiAsync(75, new WeTypeWire().Text(1, invite.Code), setup.Token);
                        Success(reply);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && setup.IsCancellationRequested)
                {
                    if (!state.Connected.Task.IsCompleted)
                    {
                        if (relayDispatch is { CanRelay: false }) throw new WeTypeRelayUnavailableException();
                        throw new TimeoutException("The sender did not establish a connection.");
                    }
                }
                if (!state.UsingWxp2p) relayCts?.Cancel();
                await state.Finished.Task.WaitAsync(ct);
            }
            finally
            {
                if (relayCts != null)
                {
                    relayCts.Cancel();
                    if (relayTask != null) { try { await relayTask; } catch { } }
                    relayCts.Dispose();
                }
            }
        }
        finally
        {
            authority.TrySetCanceled();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(shutdown.Token);
        }
    }

    internal static bool ShouldDialSender(WeTypeWire issuer)
    {
        var link = issuer.GetBytes(3);
        if (link.Length == 0) return false;
        using var json = JsonDocument.Parse(link);
        return json.RootElement.TryGetProperty("preferred_client", out var preferred) && preferred.ValueKind == JsonValueKind.False;
    }

    internal static WebApplication CreateFileListener(WeTypeTransferCertificate credentials, WeTypeIncomingCall state,
        Task<byte[]> trustedAuthority, IPAddress address)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(WeTypeIncomingCall).Assembly.GetName().Name });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxConcurrentConnections = 8;
            options.Listen(address, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(new TlsHandshakeCallbackOptions { OnConnection = async hello =>
                {
                    byte[] root = await trustedAuthority.WaitAsync(TimeSpan.FromSeconds(8), hello.CancellationToken);
                    return new SslServerAuthenticationOptions
                    {
                        ServerCertificate = credentials.Server,
                        ApplicationProtocols = [SslApplicationProtocol.Http2],
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = true,
                        RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                        {
                            if (certificate is not X509Certificate2 leaf) return false;
                            using var ca = X509CertificateLoader.LoadCertificate(root);
                            using var chain = new X509Chain();
                            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                            chain.ChainPolicy.CustomTrustStore.Add(ca);
                            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
                            chain.ChainPolicy.DisableCertificateDownloads = true;
                            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                            return chain.Build(leaf);
                        }
                    };
                }});
            });
        });
        builder.Services.AddSingleton(state);
        builder.Services.AddGrpc(options => options.MaxReceiveMessageSize = WeTypeFileStream.MaxFrameSize);
        var server = builder.Build();
        server.MapGrpcService<WeTypeIncomingCall>();
        return server;
    }
}

[BindServiceMethod(typeof(WeTypeIncomingCall), nameof(BindService))]
internal class WeTypeIncomingCall(WeTypeFileInbox inbox, Action? connected = null)
{
    private int _transportMode; // 0 = none, 1 = gRPC, 2 = WXP2P
    internal bool UsingWxp2p => Volatile.Read(ref _transportMode) == 2;
    internal TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void BindService(ServiceBinderBase binder, WeTypeIncomingCall? service)
    {
        var codec = Marshallers.Create<WeTypeWire>(v => v.Encode(), bytes => WeTypeWire.Parse(bytes, WeTypeFileStream.MaxFrameSize));
        var method = new Method<WeTypeWire, WeTypeWire>(MethodType.DuplexStreaming, "flurry.FileService", "TransferFiles", codec, codec);
        binder.AddMethod(method, service == null ? null : new DuplexStreamingServerMethod<WeTypeWire, WeTypeWire>(service.TransferFiles));
    }

    public virtual Task TransferFiles(IAsyncStreamReader<WeTypeWire> input, IServerStreamWriter<WeTypeWire> output, ServerCallContext context) =>
        ReceiveAsync(input, output, context.CancellationToken);

    internal async Task ReceiveAsync(IAsyncStreamReader<WeTypeWire> input, IAsyncStreamWriter<WeTypeWire> output, CancellationToken ct, bool helloSent = false)
    {
        if (Interlocked.CompareExchange(ref _transportMode, 1, 0) != 0)
        {
            if (helloSent) return;
            throw new RpcException(new Status(StatusCode.AlreadyExists, "A transfer is already active."));
        }
        Connected.TrySetResult();
        try { connected?.Invoke(); await inbox.ReceiveAsync(input, output, ct, helloSent); Finished.TrySetResult(); }
        catch (Exception ex) { Fail(ex); throw; }
    }

    // The caller sees the failure directly; nobody may be waiting on Finished, and an unread failure is reported as a crash.
    private void Fail(Exception ex)
    {
        Finished.TrySetException(ex);
        _ = Finished.Task.Exception;
    }

    internal async Task ReceiveWxp2pAsync(WeTypeWxp2pSession session, ReadOnlyMemory<byte> firstCommand,
        IAsyncEnumerator<ReadOnlyMemory<byte>> restCommands, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _transportMode, 2, 0) != 0) return;
        Connected.TrySetResult();
        try
        {
            connected?.Invoke();
            async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamCommands()
            {
                yield return firstCommand;
                while (await restCommands.MoveNextAsync())
                    yield return restCommands.Current;
            }
            await inbox.ReceiveFileCommandsAsync(StreamCommands(), async (receipt, token) =>
            {
                var cmd = new WeTypeFileDataCommand(session.NextCommandId(), receipt);
                await session.SendFileCommandAsync(cmd.Encode(), token);
            }, ct, session.PendingBytes);
            await session.FlushOutgoingAsync(TimeSpan.FromSeconds(3), ct);
            Finished.TrySetResult();
        }
        catch (Exception ex) { Fail(ex); throw; }
    }
}
