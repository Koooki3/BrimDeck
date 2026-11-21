namespace BrimDeck.Core.ClipboardSync;

// A relay connection kept open after sending. The official clients keep the connection after a transfer, and the phone
// sends its next files through it instead of a new invitation; it waits for this device while the connection is gone.
public sealed class WeTypeRelayLink : IAsyncDisposable
{
    private const ulong PeerDisconnected = 1001;
    private readonly WeTypeWxp2pSession _session;
    private readonly Action<string>? _trace;
    private readonly CancellationTokenSource _life = new();
    private readonly CancellationTokenSource _idle = new();
    private readonly CancellationTokenSource _reading;
    private IAsyncEnumerator<ReadOnlyMemory<byte>>? _commands;
    private ReadOnlyMemory<byte> _first;
    private Task _current = Task.CompletedTask;
    private bool _peerLeft;
    private int _disposed;

    public ulong Peer { get; }

    internal WeTypeRelayLink(WeTypeWxp2pSession session, ulong peer, Action<string>? trace)
    {
        _session = session;
        _trace = trace;
        Peer = peer;
        _reading = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, _idle.Token);
    }

    // Waits for the phone to start sending files, acknowledging its control messages meanwhile. False when the phone
    // leaves, the connection ends, or nothing happens for the idle time.
    public Task<bool> WaitForFilesAsync(TimeSpan idle)
    {
        var wait = WaitCoreAsync(idle);
        _current = wait;
        return wait;
    }

    private async Task<bool> WaitCoreAsync(TimeSpan idle)
    {
        _commands ??= _session.IncomingCommands(_reading.Token).GetAsyncEnumerator(_reading.Token);
        _idle.CancelAfter(idle);
        try
        {
            while (await _commands.MoveNextAsync())
            {
                var bytes = _commands.Current;
                var chunk = WeTypeFileDataCommand.Decode(bytes.Span).FileChunk;
                ulong kind = chunk.GetMessage(1).GetNumber(1);
                if (kind == 200)
                {
                    if (chunk.Has(8))
                    {
                        var ack = new WeTypeWire().Message(1, new WeTypeWire().Number(1, 5)).Bytes(8, chunk.GetBytes(8));
                        await _session.SendFileCommandAsync(new WeTypeFileDataCommand(_session.NextCommandId(), ack).Encode(), _reading.Token);
                    }
                    if (chunk.GetMessage(5).GetNumber(2) == PeerDisconnected)
                    {
                        _peerLeft = true;
                        _trace?.Invoke("WXP2P kept connection: the phone left.");
                        return false;
                    }
                    _idle.CancelAfter(idle);
                    continue;
                }
                if (kind is 100 or 5) continue;
                _idle.CancelAfter(Timeout.InfiniteTimeSpan);
                _first = bytes;
                _trace?.Invoke($"WXP2P kept connection: the phone started sending (action {kind}).");
                return true;
            }
            _trace?.Invoke("WXP2P kept connection ended by the phone.");
            return false;
        }
        catch (OperationCanceledException)
        {
            if (_idle.IsCancellationRequested) _trace?.Invoke("WXP2P kept connection idle; closing it.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Sockets.SocketException)
        {
            _trace?.Invoke($"WXP2P kept connection failed: {ex.GetType().Name}.");
            return false;
        }
    }

    // Receives the files the phone started sending, after WaitForFilesAsync returned true.
    public Task ReceiveAsync(Task<string> destination, IProgress<WeTypeSendProgress> progress, Action<string> saved,
        Action fileOffered, CancellationToken ct)
    {
        if (_commands == null || _first.IsEmpty) throw new InvalidOperationException("The phone has not started sending.");
        var receive = ReceiveCoreAsync(destination, progress, saved, fileOffered, ct);
        _current = receive;
        return receive;
    }

    private async Task ReceiveCoreAsync(Task<string> destination, IProgress<WeTypeSendProgress> progress, Action<string> saved,
        Action fileOffered, CancellationToken ct)
    {
        // Closing the window ends the connection: a read cannot be abandoned while it is pending.
        using var stop = ct.Register(() => _life.Cancel());
        var call = new WeTypeIncomingCall(new WeTypeFileInbox(destination, progress, saved, _trace, fileOffered));
        try { await call.ReceiveWxp2pAsync(_session, _first, _commands!, _reading.Token); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Tell the phone this device left, so its next transfer starts with a new invitation instead of waiting.
        if (!_peerLeft && !_session.RemoteHangup)
        {
            using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                var envelope = new WeTypeWire().Number(1, 1).Number(2, PeerDisconnected).Bytes(3, "{}"u8.ToArray());
                var chunk = new WeTypeWire().Message(1, new WeTypeWire().Number(1, 200)).Message(5, envelope)
                    .Number(7, 0).Bytes(8, [1]);
                await _session.SendFileCommandAsync(new WeTypeFileDataCommand(_session.NextCommandId(), chunk).Encode(), bye.Token);
                await _session.SendHangupAsync(bye.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
        }
        _life.Cancel();
        try { await _current; } catch (Exception) { }
        if (_commands != null) await _commands.DisposeAsync();
        await _session.DisposeAsync();
        _reading.Dispose();
        _idle.Dispose();
        _life.Dispose();
        _trace?.Invoke("WXP2P kept connection closed.");
    }
}
