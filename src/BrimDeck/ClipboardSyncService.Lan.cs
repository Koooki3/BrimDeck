using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;
using BrimDeck.Native;

namespace BrimDeck;

internal sealed partial class ClipboardSyncService
{
    private CancellationTokenSource? _lanStop;
    private Channel<ClipCopy>? _lanOutgoing;
    private bool _lanImages;
    public string LanStatus { get; private set; } = "";
    public SyncLevel LanLevel { get; private set; }
    // Put in the outgoing queue to tell the other computer that this one ends the pairing.
    private static readonly ClipCopy LanGoodbye = new("", "", "", null, 0, 0, "");
    private TaskCompletionSource? _lanGoodbyeSent;

    // Ends the pairing. The other computer, when it is connected, is told, and ends it on its side too; one that is away
    // keeps waiting until it is unpaired there.
    public async Task UnpairLanAsync()
    {
        if (_lanImages && _lanOutgoing is { } outgoing)
        {
            var sent = _lanGoodbyeSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            outgoing.Writer.TryWrite(LanGoodbye);
            await Task.WhenAny(sent.Task, Task.Delay(1500));
        }
        SetLanPeer(null);
    }

    public void SetLanPeer(LanClipboardPeer? peer)
    {
        StopLan();
        var previous = Accounts.Lan;
        Accounts.Lan = peer;
        try { Save(); }
        catch
        {
            Accounts.Lan = previous;
            Apply();
            throw;
        }
        Apply();
        Changed?.Invoke();
    }

    private void ApplyLan()
    {
        if (!_app.Settings.LanClipboardSync || Accounts.Lan is not { } peer)
        {
            StopLan();
            return;
        }
        if (_lanStop is not null) return;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        _lanStop = stop;
        _ = RunLanAsync(peer, stop);
    }

    private void StopLan()
    {
        var stop = _lanStop;
        _lanStop = null;
        _lanOutgoing = null;
        _lanImages = false;
        LanStatus = ""; LanLevel = SyncLevel.Idle;
        stop?.Cancel();
    }

    private async Task RunLanAsync(LanClipboardPeer peer, CancellationTokenSource stop)
    {
        void Status(string message, SyncLevel level)
        {
            if (_disposed || !ReferenceEquals(_lanStop, stop)) return;
            if (LanStatus != message) Log("lan status [" + level + "] " + message);
            LanStatus = message; LanLevel = level;
            Changed?.Invoke();
        }

        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Status(Loc.T("正在等待另一台电脑…", "Waiting for the other PC…"), SyncLevel.Idle);
                    await LanClipboardLink.RunWithImagesAsync(peer, async (stream, images, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        Status(images ? Loc.T("已连接", "Connected")
                            : Loc.T("已连接 · 另一台电脑的版本只同步文字", "Connected · The other PC's version syncs text only"), SyncLevel.Active);
                        try { await ExchangeLanAsync(stream, images, token); }
                        finally { Status(Loc.T("正在等待另一台电脑…", "Waiting for the other PC…"), SyncLevel.Idle); }
                    }, stop.Token, (address, port) =>
                    {
                        // The other computer came back with another address: it is tried first from now on.
                        peer = peer with { Address = address.ToString(), Port = port };
                        if (!ReferenceEquals(_lanStop, stop)) return;
                        Accounts.Lan = peer;
                        try { Save(); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or AuthenticationException
                    or OperationCanceledException or CryptographicException or FormatException
                    or System.Net.NetworkInformation.NetworkInformationException)
                {
                    if (stop.IsCancellationRequested) break;
                    Status(Loc.T("正在等待另一台电脑…", "Waiting for the other PC…"), SyncLevel.Idle);
                }
                await Task.Delay(TimeSpan.FromSeconds(3), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_lanStop, stop))
            {
                _lanStop = null;
                ReleaseIdleWatcher();
            }
            stop.Dispose();
        }
    }

    private async Task ExchangeLanAsync(SslStream stream, bool images, CancellationToken cancellation)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var outgoing = Channel.CreateBounded<ClipCopy>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _lanOutgoing = outgoing;
        _lanImages = images;
        Task read = ReadAsync();
        Task write = WriteAsync();
        await Task.WhenAny(read, write);
        session.Cancel();
        if (ReferenceEquals(_lanOutgoing, outgoing))
        {
            _lanOutgoing = null;
            _lanImages = false;
        }
        await Task.WhenAll(read, write);

        async Task ReadAsync()
        {
            string? last = null;
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var content = await LanClipboardLink.ReadContentAsync(stream, images, timeout.Token);
                session.Token.ThrowIfCancellationRequested();
                if (content is null) continue;
                if (content is LanClipboardContent.Unpaired)
                {
                    // Once this session has unwound: the pairing here goes too.
                    var ended = Accounts.Lan;
                    Log("lan unpaired by the other computer");
                    _ = _app.Dispatcher.BeginInvoke(() =>
                    {
                        if (_disposed || !ReferenceEquals(Accounts.Lan, ended)) return;
                        try { SetLanPeer(null); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
                    });
                    return;
                }
                string source = Accounts.Lan?.DeviceName is { Length: > 0 } name ? name : Loc.T("局域网设备", "LAN device");
                if (content is LanClipboardContent.Text(var text))
                {
                    string hash = Hash(text);
                    if (LanClipboardLink.ContainsInvitation(text) || hash == last) continue;
                    if (!outgoing.Reader.TryPeek(out _))
                    {
                        try { AcceptText(text, source); }
                        catch (ExternalException) { /* The clipboard is currently locked by another application. */ }
                    }
                    last = hash;
                }
                else if (content is LanClipboardContent.Image(var png))
                {
                    string hash = HashImage(png.Bytes);
                    if (hash == last || outgoing.Reader.TryPeek(out _)) continue;
                    uint sequence = ClipboardWatch.GetClipboardSequenceNumber();
                    System.Windows.Media.Imaging.BitmapSource bitmap;
                    try { bitmap = await Task.Run(() => ShelfService.DecodeRemoteImage(png), timeout.Token); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or FileFormatException or ArgumentException or NotSupportedException or InvalidOperationException) { continue; }
                    timeout.Token.ThrowIfCancellationRequested();
                    if (sequence == ClipboardWatch.GetClipboardSequenceNumber() && !outgoing.Reader.TryPeek(out _)) AcceptImage(png, bitmap, source);
                    last = hash;
                }
            }
        }

        async Task WriteAsync()
        {
            string? last = null;
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(5));
                ClipCopy? copy = null;
                try { copy = await outgoing.Reader.ReadAsync(idle.Token); }
                catch (OperationCanceledException) when (!session.IsCancellationRequested) { }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                if (copy is null)
                {
                    await LanClipboardLink.KeepAliveAsync(stream, timeout.Token);
                    continue;
                }
                if (ReferenceEquals(copy, LanGoodbye))
                {
                    if (images)
                    {
                        await LanClipboardLink.WriteUnpairedAsync(stream, timeout.Token);
                        await stream.FlushAsync(timeout.Token);
                    }
                    _lanGoodbyeSent?.TrySetResult();
                    continue;
                }
                if (copy.Png is { } bytes)
                {
                    if (!images) continue;
                    string hash = HashImage(bytes);
                    if (hash == last) continue;
                    LanPng png;
                    try { png = await Task.Run(() => LanPng.Validate(bytes, timeout.Token), timeout.Token); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException) { continue; }
                    await LanClipboardLink.WriteImageAsync(stream, png, timeout.Token);
                    last = hash;
                    continue;
                }
                string textHash = Hash(copy.Text);
                if (textHash == last) continue;
                try { await LanClipboardLink.WriteTextAsync(stream, copy.Text, timeout.Token); }
                catch (EncoderFallbackException) { continue; }
                last = textHash;
            }
        }
    }
}
