using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace BrimDeck.Core.ClipboardSync;

// Platform as the device list reports it: 1 for the phone seen so far, 6 for the official Windows client.
public sealed record WeTypeFileDevice(ulong Id, string Name, ulong Platform = 0);

public sealed partial class WeTypeSyncClient
{
    public Action<string>? FileTrace { get; set; }

    public async Task<IReadOnlyList<WeTypeFileDevice>> FileDevicesAsync(CancellationToken ct)
    {
        if (_key is null) await InitializeAsync(ct);
        var group = await GroupAsync(ct);
        return group.Messages(2).Where(device => device.GetNumber(1) != identity.Uin &&
                device.GetNumber(1) != 0 && device.GetMessage(2).GetNumber(5) != 0)
            .Select(device => new WeTypeFileDevice(device.GetNumber(1), device.GetMessage(2).GetText(1), device.GetMessage(2).GetNumber(2))).ToArray();
    }

    // A null target requests a one-time QR invitation. A bound-device invitation is addressed by UIN.
    // After a relay transfer the connection is returned open (the phone may send its next files through it); the caller
    // owns it. A direct transfer returns null.
    public async Task<WeTypeRelayLink?> SendFilesAsync(ulong? target, IReadOnlyList<string> paths, Action<string> qrReady,
        IProgress<WeTypeSendProgress> progress, CancellationToken ct)
    {
        if (paths.Count == 0 || paths.Any(path => !File.Exists(path))) throw new FileNotFoundException();
        var devices = await FileDevicesAsync(ct);
        if (target.HasValue && !devices.Any(device => device.Id == target))
            throw new InvalidOperationException("The device is no longer available for file transfer.");
        using var credentials = new WeTypeTransferCertificate();
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .Where(a => !IPAddress.IsLoopback(a) && (a.AddressFamily == AddressFamily.InterNetwork ||
                a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6Multicast))
            .Select(a => a.ToString()).Distinct().Take(32).ToArray();
        var link = JsonSerializer.Serialize(new { lan = addresses, device_name = Name, preferred_client = true });
        var request = target is ulong peer
            ? new WeTypeWire().Number(1, peer).Text(2, credentials.AuthorityPem).Text(3, link).Text(4, Name).Number(5, 2)
            : new WeTypeWire().Text(1, credentials.AuthorityPem).Text(2, link).Text(3, Name).Number(4, 2);
        var offer = await ApiAsync(target.HasValue ? 77u : 73u, request, ct);
        Success(offer);
        var code = offer.GetText(2);
        var lifetime = offer.GetNumber(4);
        if (code.Length is 0 or > 256 || code.Any(c => c is < '!' or > '~') || lifetime == 0)
            throw new InvalidDataException("Invalid file invitation.");
        bool channelOpened = false;
        try
        {
            if (!target.HasValue)
            {
                var url = offer.GetText(5);
                if (string.IsNullOrEmpty(url) || url.Length > 2048) throw new InvalidDataException("Missing QR address.");
                qrReady(url);
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(lifetime, 300_000UL)));
            WeTypeWire remote;
            DateTimeOffset? relayAvailable = null;
            WeTypeFileDispatch? relayDispatch = null;
            bool requestedDispatch = false;
            bool useRelayDirectly = false;
            var pollingSince = System.Diagnostics.Stopwatch.StartNew();
            void CaptureRelay(WeTypeWire response)
            {
                if (relayDispatch != null) return;
                var parsed = TryParseRelay(response);
                if (parsed == null) return;
                relayDispatch = parsed;
                relayAvailable ??= DateTimeOffset.UtcNow;
            }
            async Task CheckRelayAsync()
            {
                if (requestedDispatch || !target.HasValue) return;
                requestedDispatch = true;
                var join = new WeTypeWire().Text(1, code).Text(2, credentials.AuthorityPem).Text(3, link).Number(7, 2);
                var joined = await ApiAsync(74, join, wait.Token);
                Success(joined);
                CaptureRelay(joined);
                FileTrace?.Invoke(relayDispatch != null ? "Received relay candidates for this invitation." : "Joined the invitation; waiting for the peer.");
            }
            while (true)
            {
                var reply = await ApiAsync(75, new WeTypeWire().Text(1, code), wait.Token);
                Success(reply);
                remote = reply.GetMessage(2);
                if (!requestedDispatch && pollingSince.Elapsed >= TimeSpan.FromSeconds(5)) await CheckRelayAsync();
                CaptureRelay(reply);
                if (remote.GetNumber(1) != 0)
                {
                    if (target.HasValue && remote.GetNumber(1) != target || remote.GetNumber(1) == identity.Uin)
                        throw new InvalidDataException("Unexpected transfer peer.");
                    if (remote.GetNumber(11) is 1 or 2) throw new IOException("The other device declined or canceled.");
                    if (remote.GetBytes(2).Length > 0 && remote.GetBytes(6).Length > 0) break;
                    await CheckRelayAsync();
                }
                if (remote.GetNumber(1) != 0 && relayAvailable is { } since && DateTimeOffset.UtcNow - since > TimeSpan.FromSeconds(3))
                {
                    if (relayDispatch?.CanRelay == true) { useRelayDirectly = true; break; }
                    throw new WeTypeRelayUnavailableException();
                }
                await Task.Delay(700, wait.Token);
            }
            Grpc.Net.Client.GrpcChannel? channel = null;
            if (!useRelayDirectly)
            {
                FileTrace?.Invoke("Matched the receiving device. Trying its direct connection candidates.");
                try { channel = await WeTypeFileStream.ConnectAsync(credentials, remote, ct, FileTrace); }
                catch (IOException)
                {
                    await CheckRelayAsync();
                    for (int poll = 0; poll < 4 && relayDispatch is not { CanRelay: true }; poll++)
                    {
                        var latest = await ApiAsync(75, new WeTypeWire().Text(1, code), ct);
                        Success(latest);
                        CaptureRelay(latest);
                        if (relayDispatch?.CanRelay == true) break;
                        await Task.Delay(700, ct);
                    }
                    if (relayDispatch == null) throw;
                    if (!relayDispatch.CanRelay) throw new WeTypeRelayUnavailableException();
                    FileTrace?.Invoke("Direct connection failed; switching to WXP2P relay.");
                }
            }
            if (channel != null)
            {
                channelOpened = true;
                using (channel)
                    await Task.Run(() => WeTypeFileStream.SendAsync(channel, paths, progress, ct, FileTrace), ct);
            }
            else
            {
                FileTrace?.Invoke("Connecting to the WXP2P relay for file transfer.");
                var relay = await WeTypeWxp2pSession.ConnectAsync(relayDispatch!, ct, FileTrace);
                channelOpened = true;
                try { await WeTypeFileStream.SendOverWxp2pAsync(relay, paths, progress, ct, FileTrace, hangUp: false); }
                catch
                {
                    await relay.DisposeAsync();
                    throw;
                }
                return new WeTypeRelayLink(relay, remote.GetNumber(1), FileTrace);
            }
            return null;
        }
        catch (Exception) when (ct.IsCancellationRequested && !channelOpened)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await ApiAsync(74, new WeTypeWire().Text(1, code).Number(6, 1), cleanup.Token); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException) { }
            throw new OperationCanceledException(ct);
        }
    }

    private static WeTypeFileDispatch? TryParseRelay(WeTypeWire response)
    {
        var dispatch = response.GetBytes(5);
        if (dispatch.Length == 0) return null;
        try
        {
            var parsed = WeTypeFileDispatch.Parse(dispatch);
            return parsed.Relays.Count > 0 ? parsed : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or System.Text.DecoderFallbackException)
        {
            // A newer dispatch format must not prevent a direct connection.
            return null;
        }
    }
}
