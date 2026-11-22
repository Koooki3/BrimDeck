using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace BrimDeck.Core.ClipboardSync;

internal interface IWeTypeRelayTransport : IAsyncDisposable
{
    IPEndPoint LocalEndPoint { get; }
    bool IsTcp { get; }
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint target, CancellationToken ct);
    ValueTask<(byte[] Data, IPEndPoint Remote)> ReceiveAsync(CancellationToken ct);
}

internal sealed class WeTypeUdpRelayTransport : IWeTypeRelayTransport
{
    private readonly UdpClient _udp;

    public WeTypeUdpRelayTransport(AddressFamily family)
    {
        _udp = new UdpClient(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        _udp.Client.SendBufferSize = 4 * 1024 * 1024;
        if (OperatingSystem.IsWindows())
        {
            // Prevent ICMP Port Unreachable from throwing SocketException 10054 on unrelated probes.
            const int SioUdpConnReset = unchecked((int)0x9800000C);
            try { _udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null); }
            catch (SocketException) { }
        }
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;
    public bool IsTcp => false;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint target, CancellationToken ct) =>
        await _udp.SendAsync(packet, target, ct);

    public async ValueTask<(byte[] Data, IPEndPoint Remote)> ReceiveAsync(CancellationToken ct)
    {
        var result = await _udp.ReceiveAsync(ct);
        return (result.Buffer, result.RemoteEndPoint);
    }

    public ValueTask DisposeAsync()
    {
        _udp.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class WeTypeTcpRelayTransport : IWeTypeRelayTransport
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly IPEndPoint _remote;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private WeTypeTcpRelayTransport(TcpClient tcp, IPEndPoint remote)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _remote = remote;
    }

    public static async Task<WeTypeTcpRelayTransport> ConnectAsync(IPEndPoint remote, CancellationToken ct)
    {
        var tcp = new TcpClient(remote.AddressFamily) { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(remote.Address, remote.Port, ct);
            return new WeTypeTcpRelayTransport(tcp, remote);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_tcp.Client.LocalEndPoint!;
    public bool IsTcp => true;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint target, CancellationToken ct)
    {
        byte[] framed = WeTypeRelayCrypto.PackTcpFrame(packet.Span);
        await _sendLock.WaitAsync(ct);
        try { await _stream.WriteAsync(framed, ct); }
        finally { _sendLock.Release(); }
    }

    public async ValueTask<(byte[] Data, IPEndPoint Remote)> ReceiveAsync(CancellationToken ct)
    {
        byte[] header = new byte[9];
        await _stream.ReadExactlyAsync(header, ct);
        if (header[0] != WeTypeRelayCrypto.TcpFrameMagic)
            throw new InvalidDataException("Invalid WXP2P TCP frame magic.");
        uint totalMinusOne = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));
        if (totalMinusOne < 8 || totalMinusOne > 65536)
            throw new InvalidDataException("Invalid WXP2P TCP frame length.");
        int innerLen = (int)(totalMinusOne - 8);
        byte[] body = new byte[innerLen];
        if (innerLen > 0) await _stream.ReadExactlyAsync(body, ct);
        return (body, _remote);
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _tcp.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

// Implements the WeType WXP2P relay control plane, MultiPathRUDP reliable layer, and BigRudp fragmentation.
internal sealed class WeTypeWxp2pSession : IAsyncDisposable
{
    private const uint BusinessId = 10;
    private const uint CmdCheckInReq = 2;
    private const uint CmdStatReq = 4;
    private const uint CmdVerifyIpPortResp = 7;
    private const uint CmdCheckInResp = 0x1002;
    private const uint CmdNotifyReady = 0x1004;
    private const uint CmdErrorNotify = 0x1009;
    private const uint CmdStatResp = 0x100A;
    private const uint CmdRedirectReq = 0x100C;
    private const uint CmdVerifyIpPortReq = 0x100D;

    private const uint RudpCmdData = 1200;
    private const uint RudpCmdHangup = 1201;
    private const int MaxRudpPacketSize = 1300;
    private const int UnfragmentedLimit = MaxRudpPacketSize - 0x32; // 1250
    private const int FragmentPayloadSize = MaxRudpPacketSize - 0x80; // 1172
    private const int MaxInFlightPackets = 160;
    private const int MaxReassemblyBytes = WeTypeFileStream.MaxFrameSize + 65536;

    private sealed class OutgoingPacket
    {
        public uint StreamId { get; init; }
        public uint PktNum { get; init; }
        public byte[] Payload { get; init; } = [];
        public long FirstSentTicks { get; init; }
        public long LastSentTicks { get; set; }
        public int SendCount { get; set; }
    }

    private sealed class ReceiveStreamState
    {
        public uint NextExpected { get; set; } = 1;
        public uint MaxReceived { get; set; }
        public SortedDictionary<uint, byte[]> Reorder { get; } = [];
        public MemoryStream? FragmentBuffer { get; set; }
        public uint FirstFragmentCmdType { get; set; }
        public uint FirstFragmentSequence { get; set; }
        public long FirstPacketTicks { get; } = Stopwatch.GetTimestamp();
    }

    private readonly WeTypeFileDispatch _dispatch;
    private readonly IWeTypeRelayTransport _transport;
    private readonly byte[] _controlKey;
    private readonly Action<string>? _trace;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly object _sync = new();
    private readonly Dictionary<uint, OutgoingPacket> _unacked = [];
    private readonly Dictionary<uint, ReceiveStreamState> _recvStreams = [];
    private readonly Dictionary<IPEndPoint, int> _endpointConnIds = [];
    private readonly TaskCompletionSource _checkedIn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _peerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _windowSignal = new(0, int.MaxValue);

    private IPEndPoint? _activeEndPoint;
    private int _activeConnId;
    private uint _peerMemberId;
    private uint _checkinSn;
    private uint _nextPktNum;
    private ushort _relaySn;
    private ushort _dataTypeSeq;
    private ushort _helloSeq;
    private bool _helloReplied;
    private int _tracedHelloCount;
    private bool _secondCheckInSent;
    private long _lastRecvStatsTicks;
    private uint _recvDataPackets;
    private uint _recvDuplicatePackets;
    private ulong _pathPn;
    private ulong _largestRecvPathPn;
    private int _statIntervalSec;
    private int _rttMs = 100;
    private long _lastPeerActivityTicks;
    private long _lastFeedbackSentTicks;
    private int _packetsSinceFeedback;
    private bool _feedbackDirty;
    private bool _remoteHangup;
    private int _tracedDataCount;
    private int _tracedFeedbackCount;
    private bool _tracedDecryptFailure;
    private uint _sendCount;
    private ulong _sendBytes;
    private uint _recvCount;
    private ulong _recvBytes;

    private Task? _recvLoop;
    private Task? _timerLoop;
    private bool _disposed;

    internal Task PeerReady => _peerReady.Task;
    internal int UnackedCount { get { lock (_sync) return _unacked.Count; } }
    // Application messages are numbered per connection; a connection kept after a transfer continues the numbering.
    private int _commandId;
    internal uint NextCommandId() => (uint)Interlocked.Increment(ref _commandId);
    internal bool RemoteHangup => Volatile.Read(ref _remoteHangup);
    // Bytes of messages that have arrived but are not complete yet: fragments joined so far, and packets held behind a gap.
    private long _assemblingBytes;
    internal long PendingBytes()
    {
        long held;
        lock (_sync) held = _recvStreams.Values.Sum(s => s.Reorder.Values.Sum(p => (long)p.Length));
        return Interlocked.Read(ref _assemblingBytes) + held;
    }
    internal IAsyncEnumerable<ReadOnlyMemory<byte>> IncomingCommands(CancellationToken ct) => _incoming.Reader.ReadAllAsync(ct);

    private WeTypeWxp2pSession(WeTypeFileDispatch dispatch, IWeTypeRelayTransport transport, Action<string>? trace)
    {
        _dispatch = dispatch;
        _transport = transport;
        _controlKey = WeTypeRelayCrypto.DeriveControlKey(dispatch.RoomKey);
        _trace = trace;
        _peerMemberId = dispatch.PeerMemberId;
        _statIntervalSec = (int)dispatch.HelloFreqSec;
        _lastPeerActivityTicks = Stopwatch.GetTimestamp();
    }

    internal static async Task<WeTypeWxp2pSession> ConnectAsync(WeTypeFileDispatch dispatch, CancellationToken ct, Action<string>? trace = null)
    {
        if (!dispatch.CanRelay) throw new WeTypeRelayUnavailableException();

        var udpTargets = dispatch.Connections
            .SelectMany(c => c.UdpRelays.Select(ep => (EndPoint: ep, c.ConnId)))
            .DistinctBy(x => x.EndPoint)
            .ToList();

        // Prefer IPv4 UDP relays first if available, else IPv6 UDP relays.
        var ipv4Udp = udpTargets.Where(t => t.EndPoint.AddressFamily == AddressFamily.InterNetwork).ToList();
        var chosenUdp = ipv4Udp.Count > 0 ? ipv4Udp : udpTargets;

        if (chosenUdp.Count > 0)
        {
            var udpTransport = new WeTypeUdpRelayTransport(chosenUdp[0].EndPoint.AddressFamily);
            var session = new WeTypeWxp2pSession(dispatch, udpTransport, trace);
            try
            {
                using var udpTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                udpTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                await session.StartAndCheckInAsync(chosenUdp, udpTimeout.Token);
                trace?.Invoke("Connected to WXP2P UDP relay.");
                return session;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is OperationCanceledException or SocketException or IOException)
            {
                await session.DisposeAsync();
                trace?.Invoke("WXP2P UDP relay check-in did not complete; trying TCP relay.");
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        var tcpTargets = dispatch.Connections
            .SelectMany(c => c.TcpRelays.Select(ep => (EndPoint: ep, c.ConnId)))
            .DistinctBy(x => x.EndPoint)
            .ToList();

        foreach (var target in tcpTargets.Take(8))
        {
            WeTypeTcpRelayTransport? tcpTransport = null;
            WeTypeWxp2pSession? session = null;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(4));
                tcpTransport = await WeTypeTcpRelayTransport.ConnectAsync(target.EndPoint, attempt.Token);
                session = new WeTypeWxp2pSession(dispatch, tcpTransport, trace);
                await session.StartAndCheckInAsync([target], attempt.Token);
                trace?.Invoke("Connected to WXP2P TCP relay.");
                return session;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is OperationCanceledException or SocketException or IOException)
            {
                if (session != null) await session.DisposeAsync();
                else if (tcpTransport != null) await tcpTransport.DisposeAsync();
            }
            catch
            {
                if (session != null) await session.DisposeAsync();
                else if (tcpTransport != null) await tcpTransport.DisposeAsync();
                throw;
            }
        }

        throw new IOException("Could not check in to any WXP2P relay server.");
    }

    // Internal entry point for deterministic unit tests with an in-memory relay transport.
    internal static async Task<WeTypeWxp2pSession> ConnectWithTransportAsync(WeTypeFileDispatch dispatch,
        IWeTypeRelayTransport transport, IReadOnlyList<(IPEndPoint EndPoint, int ConnId)> targets,
        CancellationToken ct, Action<string>? trace = null)
    {
        var session = new WeTypeWxp2pSession(dispatch, transport, trace);
        try
        {
            await session.StartAndCheckInAsync(targets, ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task StartAndCheckInAsync(IReadOnlyList<(IPEndPoint EndPoint, int ConnId)> targets, CancellationToken ct)
    {
        lock (_sync)
        {
            foreach (var (ep, connId) in targets)
                _endpointConnIds[ep] = connId;
        }

        _recvLoop = Task.Run(ReceiveLoopAsync);
        _timerLoop = Task.Run(TimerLoopAsync);

        // The official client checks in to one relay and waits for its answer before trying another. Checking in to
        // every relay at once leaves the room with several connections for this member.
        for (int attempt = 0; attempt < 8 && !_checkedIn.Task.IsCompleted; attempt++)
        {
            foreach (var (ep, connId) in targets)
            {
                if (_checkedIn.Task.IsCompleted) break;
                byte[] checkIn = BuildCheckInPacket(ep, connId);
                try { await _transport.SendAsync(checkIn, ep, ct); }
                catch (SocketException) { continue; }
                var delay = Task.Delay(500, ct);
                if (await Task.WhenAny(_checkedIn.Task, delay) == _checkedIn.Task) break;
                ct.ThrowIfCancellationRequested();
            }
        }

        await _checkedIn.Task.WaitAsync(ct);
    }

    internal async Task WaitForPeerReadyAsync(TimeSpan maxWait, CancellationToken ct)
    {
        if (_peerReady.Task.IsCompleted) return;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(maxWait);
        try { await _peerReady.Task.WaitAsync(wait.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    internal async Task SendFileCommandAsync(byte[] rudpCmdBytes, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;

        if (rudpCmdBytes.Length <= UnfragmentedLimit)
        {
            uint pktNum = await EnqueueAndSendMpRudpAsync(0, rudpCmdBytes, token);
            _trace?.Invoke($"WXP2P sent RudpCmd unfragmented: len={rudpCmdBytes.Length}, pktNum={pktNum}.");
            return;
        }

        // Fragment large RudpCmd using BigRudp (proto 0x141c80): cmd_type(1), content(2), sequence(3), send_time(4)=Adler32, is_fragmented(5).
        var parsed = WeTypeWire.Parse(rudpCmdBytes, MaxReassemblyBytes);
        uint cmdType = (uint)parsed.GetNumber(1);
        byte[] content = parsed.GetBytes(2);
        uint baseSequence = (uint)parsed.GetNumber(3);
        uint adler = WeTypeRelayCrypto.Adler32(content);
        int totalFragments = (content.Length + FragmentPayloadSize - 1) / FragmentPayloadSize;
        if (totalFragments == 0) totalFragments = 1;

        uint firstPktNum = 0, lastPktNum = 0;
        for (int i = 0; i < totalFragments; i++)
        {
            int offset = i * FragmentPayloadSize;
            int count = Math.Min(FragmentPayloadSize, content.Length - offset);
            bool isFragmented = i < totalFragments - 1;
            var frag = new WeTypeWire()
                .Number(1, cmdType)
                .Bytes(2, content[offset..(offset + count)])
                .Number(3, unchecked(baseSequence + (uint)i))
                .Number(4, adler)
                .Number(5, isFragmented ? 1UL : 0UL);
            lastPktNum = await EnqueueAndSendMpRudpAsync(0, frag.Encode(), token);
            if (i == 0) firstPktNum = lastPktNum;
        }
        _trace?.Invoke($"WXP2P sent RudpCmd fragmented: cmd={cmdType}, contentLen={content.Length}, frags={totalFragments}, pkts={firstPktNum}..{lastPktNum}.");
    }

    internal async Task SendHangupAsync(CancellationToken ct)
    {
        byte[] hangup = new WeTypeWire().Number(1, RudpCmdHangup).Bytes(2, []).Number(3, 0).Encode();
        await SendFileCommandAsync(hangup, ct);
        await FlushOutgoingAsync(TimeSpan.FromSeconds(2), ct);
    }

    internal async Task FlushOutgoingAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        deadline.CancelAfter(timeout);
        while (!deadline.IsCancellationRequested)
        {
            lock (_sync)
            {
                if (_unacked.Count == 0) return;
            }
            try { await _windowSignal.WaitAsync(50, deadline.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<uint> EnqueueAndSendMpRudpAsync(uint streamId, byte[] payload, CancellationToken ct)
    {
        int burst = 0;
        while (true)
        {
            bool hasSpace;
            lock (_sync) hasSpace = _unacked.Count < MaxInFlightPackets;
            if (hasSpace) break;
            await _windowSignal.WaitAsync(50, ct);
        }

        OutgoingPacket entry;
        uint minQueuePktNum;
        lock (_sync)
        {
            uint pktNum = ++_nextPktNum;
            long now = Stopwatch.GetTimestamp();
            entry = new OutgoingPacket
            {
                StreamId = streamId,
                PktNum = pktNum,
                Payload = payload,
                FirstSentTicks = now,
                LastSentTicks = now,
                SendCount = 1
            };
            _unacked[pktNum] = entry;
            minQueuePktNum = _unacked.Keys.Min();
            burst = (int)(pktNum & 15);
        }

        await TransmitMpRudpDataAsync(entry, minQueuePktNum, ct);
        if (!_transport.IsTcp && burst == 0)
            await Task.Delay(1, ct);
        return entry.PktNum;
    }

    private async Task TransmitMpRudpDataAsync(OutgoingPacket entry, uint minQueuePktNum, CancellationToken ct)
    {
        ulong pathPn;
        uint peerMember;
        lock (_sync)
        {
            pathPn = ++_pathPn;
            peerMember = _peerMemberId;
        }

        var rudpData = new WeTypeWire()
            .Number(1, entry.StreamId)
            .Number(2, entry.PktNum)
            .Bytes(3, entry.Payload)
            .Number(5, 0)
            .Number(6, minQueuePktNum)
            .Number(7, pathPn);

        var mpPkt = new WeTypeWire()
            .Number(1, 1) // head_sign = MpRudpData
            .Number(2, 0) // version
            .Number(3, (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .Bytes(4, rudpData.Encode())
            .Number(5, peerMember);

        await SendRelayDataAsync(mpPkt.Encode(), ct);
    }

    private async Task SendRelayDataAsync(byte[] mpRudpBytes, CancellationToken ct)
    {
        ushort seq;
        lock (_sync) seq = _dataTypeSeq++;
        await SendRelayPacketAsync(mpRudpBytes, WeTypeRelayCrypto.LiveCloudNibbleMpRudp, seq, ct);
    }

    // Each data type keeps its own sequence (it is part of the nonce); the packet serial number is shared.
    private async Task SendRelayPacketAsync(byte[] payload, byte liveTypeNibble, ushort seq, CancellationToken ct)
    {
        IPEndPoint? target;
        int connId;
        ushort sn;
        lock (_sync)
        {
            target = _activeEndPoint;
            connId = _activeConnId;
            sn = _relaySn++;
            _sendCount++;
            _sendBytes += (ulong)payload.Length;
        }
        if (target == null) return;

        byte[] packet = WeTypeRelayCrypto.PackLiveCloud96(
            _dispatch.RoomId,
            _dispatch.RoomKey,
            _dispatch.EncryptKey,
            (ushort)_dispatch.MemberId,
            sn,
            seq,
            (byte)(connId & 0xFF),
            payload,
            liveTypeNibble);

        await _transport.SendAsync(packet, target, ct);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (data, remote) = await _transport.ReceiveAsync(_cts.Token);
                if (data.Length == 0) continue;

                lock (_sync)
                {
                    _recvCount++;
                    _recvBytes += (ulong)data.Length;
                }

                if (WeTypeRelayCrypto.TryUnpackRelayControlPacket(data, out var s2c))
                {
                    await HandleControlPacketAsync(s2c, remote);
                    continue;
                }

                if (WeTypeRelayCrypto.TryUnpackRelayData(data, _dispatch.RoomKey, _dispatch.EncryptKey,
                    _dispatch.MemberId, out byte dataType, out ushort senderMemberId, out byte[] payload))
                {
                    if (_tracedDataCount < 20)
                    {
                        _tracedDataCount++;
                        _trace?.Invoke($"WXP2P data pkt #{_tracedDataCount}: magic=0x{data[0]:X2}, len={data.Length}, dataType=0x{dataType:X2}, sender={senderMemberId}, payloadLen={payload.Length}.");
                    }
                    if (dataType == WeTypeRelayCrypto.DataTypeMpRudp)
                        await HandleMpRudpPacketAsync(payload, senderMemberId, remote);
                    else if (dataType == WeTypeRelayCrypto.DataTypeHello)
                        await HandleHelloAsync(payload, senderMemberId);
                }
                else if (!_tracedDecryptFailure)
                {
                    _tracedDecryptFailure = true;
                    byte m0 = data[0];
                    byte m1 = data.Length > 1 ? data[1] : (byte)0;
                    _trace?.Invoke($"WXP2P packet not unpacked: magic=0x{m0:X2}, b1=0x{m1:X2}, len={data.Length}, selfMember={_dispatch.MemberId}.");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            if (!_cts.IsCancellationRequested)
            {
                _trace?.Invoke("WXP2P relay receive stopped: " + ex.GetType().Name);
                _incoming.Writer.TryComplete(ex);
            }
        }
        finally
        {
            _incoming.Writer.TryComplete();
        }
    }

    private async Task HandleControlPacketAsync(WeTypeWire s2c, IPEndPoint remote)
    {
        var header = s2c.GetMessage(1);
        var pub = header.GetMessage(1);
        uint cmdType = (uint)pub.GetNumber(1);
        uint encryptType = (uint)pub.GetNumber(3);
        uint svrErrno = (uint)header.GetNumber(7);
        byte[] rawBody = s2c.GetBytes(3);

        byte[] plainBody;
        if (rawBody.Length == 0 || encryptType == 0xFF)
        {
            plainBody = rawBody;
        }
        else
        {
            ReadOnlySpan<byte> key = cmdType == CmdErrorNotify ? WeTypeRelayCrypto.ErrorNotifyKey : _controlKey;
            if (!WeTypeRelayCrypto.TeaDecrypt(rawBody, key, out plainBody))
                return;
        }

        WeTypeWire body;
        try { body = plainBody.Length > 0 ? WeTypeWire.Parse(plainBody) : new WeTypeWire(); }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { return; }

        switch (cmdType)
        {
            case CmdCheckInResp:
                if (svrErrno != 0)
                {
                    _trace?.Invoke($"WXP2P CheckInResp error: {svrErrno}.");
                    return;
                }
                int interval = (int)body.GetNumber(8);
                int available = (int)body.GetNumber(7);
                int respConnId = (int)header.GetNumber(6);
                int activeConnId;
                lock (_sync)
                {
                    if (_activeEndPoint == null)
                    {
                        _activeEndPoint = remote;
                        if (_endpointConnIds.TryGetValue(remote, out int cid)) _activeConnId = cid;
                        else if (respConnId != 0) _activeConnId = respConnId;
                    }
                    else if (Equals(_activeEndPoint, remote) && _endpointConnIds.TryGetValue(remote, out int cid))
                    {
                        _activeConnId = cid;
                    }
                    activeConnId = _activeConnId;
                    if (interval is >= 2 and <= 30) _statIntervalSec = interval;
                    _lastPeerActivityTicks = Stopwatch.GetTimestamp();
                }
                _trace?.Invoke($"WXP2P CheckInResp ok: available={available}, statInterval={_statIntervalSec}s, selfMember={_dispatch.MemberId}, peerMember={_peerMemberId}, respConn={respConnId}, activeConn={activeConnId}, room64={_dispatch.RoomId > uint.MaxValue}, fields=[{string.Join(",", body.FieldNumbers())}].");
                _checkedIn.TrySetResult();
                // The official client sends a second check-in to the relay that answered, before the relay reports the peer.
                bool sendSecond;
                lock (_sync)
                {
                    sendSecond = !_secondCheckInSent && Equals(_activeEndPoint, remote);
                    if (sendSecond) _secondCheckInSent = true;
                }
                if (sendSecond)
                    await _transport.SendAsync(BuildCheckInPacket(remote, activeConnId), remote, _cts.Token);
                break;

            case CmdNotifyReady:
                if (svrErrno != 0) return;
                uint readyPeer;
                int memberCount = (int)body.GetNumber(3);
                lock (_sync)
                {
                    if (_activeEndPoint == null)
                    {
                        _activeEndPoint = remote;
                        if (_endpointConnIds.TryGetValue(remote, out int cid)) _activeConnId = cid;
                    }
                    foreach (var member in body.Messages(4))
                    {
                        if (!member.Has(4) && !member.Has(2)) continue;
                        uint m = member.Has(4) ? (uint)member.GetNumber(4) : (uint)member.GetNumber(2);
                        if (m != _dispatch.MemberId) _peerMemberId = m;
                    }
                    readyPeer = _peerMemberId;
                    _lastPeerActivityTicks = Stopwatch.GetTimestamp();
                }
                _checkedIn.TrySetResult();
                _peerReady.TrySetResult();
                _trace?.Invoke($"WXP2P relay reported peer ready (members={memberCount}, peerMember={readyPeer}).");
                break;

            case CmdStatResp:
                ulong reqMs = body.GetNumber(10);
                if (reqMs > 0)
                {
                    long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    long rtt = nowMs - (long)reqMs;
                    if (rtt is >= 5 and <= 5000)
                        Volatile.Write(ref _rttMs, (int)rtt);
                }
                break;

            case CmdVerifyIpPortReq:
                byte[] verifyResp = BuildVerifyIpPortRespPacket(remote, body);
                await _transport.SendAsync(verifyResp, remote, _cts.Token);
                break;

            case CmdRedirectReq:
                IPEndPoint? redirected = ParseRedirectTarget(body);
                if (redirected != null && !_transport.IsTcp)
                {
                    int connId;
                    lock (_sync)
                    {
                        connId = (int)body.GetNumber(12);
                        _activeEndPoint = redirected;
                        _activeConnId = connId;
                        _endpointConnIds[redirected] = connId;
                    }
                    await _transport.SendAsync(BuildCheckInPacket(redirected, connId), redirected, _cts.Token);
                }
                break;

            case CmdErrorNotify:
                _trace?.Invoke($"WXP2P relay error notify: cmd={body.GetNumber(2)} err={body.GetNumber(4)}.");
                break;
        }
    }

    // Both official clients exchange short hello packets (data type 0x84) right after the relay reports the peer, and
    // the file service starts only after that. The payload format is not known yet: the peer's first hello is answered
    // with the same bytes, and the bytes are logged (they hold no keys) so the format can be confirmed.
    private async Task HandleHelloAsync(byte[] payload, ushort senderMemberId)
    {
        bool reply;
        lock (_sync)
        {
            _peerMemberId = senderMemberId;
            _lastPeerActivityTicks = Stopwatch.GetTimestamp();
            reply = !_helloReplied;
            _helloReplied = true;
            if (_tracedHelloCount < 8)
            {
                _tracedHelloCount++;
                _trace?.Invoke($"WXP2P hello from member {senderMemberId}: {Convert.ToHexStringLower(payload)}.");
            }
        }
        _peerReady.TrySetResult();
        if (reply)
        {
            await SendHelloAsync(payload, _cts.Token);
            _trace?.Invoke("WXP2P answered the peer's hello.");
        }
    }

    private async Task SendHelloAsync(byte[] payload, CancellationToken ct)
    {
        ushort seq;
        lock (_sync) seq = _helloSeq++;
        await SendRelayPacketAsync(payload, WeTypeRelayCrypto.LiveCloudNibbleHello, seq, ct);
    }

    private async Task HandleMpRudpPacketAsync(byte[] rawPayload, ushort senderMemberId, IPEndPoint remote)
    {
        WeTypeWire pkt;
        try { pkt = WeTypeWire.Parse(rawPayload, MaxReassemblyBytes); }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { return; }

        uint headSign = (uint)pkt.GetNumber(1);
        byte[] content = pkt.GetBytes(4);
        if (content.Length == 0) return;

        // If the peer's packet reached us before CheckInResp/NotifyReady, treat the channel as live.
        lock (_sync)
        {
            if (_activeEndPoint == null)
            {
                _activeEndPoint = remote;
                if (_endpointConnIds.TryGetValue(remote, out int cid)) _activeConnId = cid;
            }
            _peerMemberId = senderMemberId;
            _lastPeerActivityTicks = Stopwatch.GetTimestamp();
        }
        _checkedIn.TrySetResult();
        _peerReady.TrySetResult();

        if (headSign == 1)
        {
            await HandleMpRudpDataAsync(content);
        }
        else if (headSign == 2)
        {
            await HandleMpRudpFeedbackAsync(content);
        }
    }

    private async Task HandleMpRudpDataAsync(byte[] content)
    {
        WeTypeWire data;
        try { data = WeTypeWire.Parse(content, MaxReassemblyBytes); }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { return; }

        uint streamId = (uint)data.GetNumber(1);
        uint pktNum = (uint)data.GetNumber(2);
        byte[] payload = data.GetBytes(3);
        ulong pathPn = data.GetNumber(7);
        ulong peerOldest = data.GetNumber(6);
        if (pktNum == 0 || payload.Length == 0) return;

        List<(uint StreamId, byte[] Payload)> ready = [];
        bool sendFeedbackNow = false;
        bool stale = false;

        lock (_sync)
        {
            if (pathPn > _largestRecvPathPn) _largestRecvPathPn = pathPn;
            if (!_recvStreams.TryGetValue(streamId, out var state))
                _recvStreams[streamId] = state = new ReceiveStreamState();
            // Field 6 is the oldest packet the sender still holds. Above 1 before anything was delivered, or no packet 1
            // for 8 seconds (a lost one is resent well within that), means the sender began this stream with an earlier
            // receiver.
            if (state.NextExpected == 1 && !state.Reorder.ContainsKey(1) && pktNum != 1 &&
                (peerOldest > 1 || Stopwatch.GetElapsedTime(state.FirstPacketTicks).TotalSeconds >= 8))
                stale = true;

            // A packet that is not kept must not raise the highest number: feedback treats everything up to it as received.
            if (pktNum >= state.NextExpected && !state.Reorder.ContainsKey(pktNum) && state.Reorder.Count < 4096)
            {
                state.Reorder[pktNum] = payload;
                if (pktNum > state.MaxReceived) state.MaxReceived = pktNum;
            }

            while (state.Reorder.Remove(state.NextExpected, out var nextPayload))
            {
                ready.Add((streamId, nextPayload));
                state.NextExpected++;
            }

            _feedbackDirty = true;
            _packetsSinceFeedback++;
            bool hasGap = state.NextExpected <= state.MaxReceived;
            bool isDuplicate = pktNum < state.NextExpected;
            _recvDataPackets++;
            if (isDuplicate) _recvDuplicatePackets++;
            // Periodic receive state, to tell a slow sender from a hole that is never filled.
            if (Stopwatch.GetElapsedTime(_lastRecvStatsTicks).TotalSeconds >= 5)
            {
                _lastRecvStatsTicks = Stopwatch.GetTimestamp();
                _trace?.Invoke($"WXP2P receive state: stream={streamId}, packets={_recvDataPackets}, duplicates={_recvDuplicatePackets}, nextExpected={state.NextExpected}, maxReceived={state.MaxReceived}, held={state.Reorder.Count}.");
            }
            double sinceLastMs = Stopwatch.GetElapsedTime(_lastFeedbackSentTicks).TotalMilliseconds;
            if (((hasGap || isDuplicate) && sinceLastMs >= 15) || _packetsSinceFeedback >= 12)
                sendFeedbackNow = true;
        }

        if (stale)
        {
            _trace?.Invoke($"WXP2P peer continues stream {streamId} from packet {pktNum} (oldest {peerOldest}); it began with an earlier receiver.");
            _incoming.Writer.TryComplete(new WeTypeStaleTransferException());
            return;
        }

        foreach (var (sid, item) in ready)
            DeliverReassembledPayload(sid, item);

        if (sendFeedbackNow)
            await SendFeedbackAsync(_cts.Token);
    }

    private void DeliverReassembledPayload(uint streamId, byte[] payload)
    {
        WeTypeWire frag;
        try { frag = WeTypeWire.Parse(payload, MaxReassemblyBytes); }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { return; }

        uint cmdType = (uint)frag.GetNumber(1);
        // Ignore internal transport probes (Quality=0x401, Bottleneck=0x402, FakingData=0x403).
        if (cmdType is 0x401 or 0x402 or 0x403) return;

        bool isFragmented = frag.GetNumber(5) != 0;
        byte[] content = frag.GetBytes(2);
        uint sequence = (uint)frag.GetNumber(3);

        ReceiveStreamState state;
        lock (_sync)
        {
            if (!_recvStreams.TryGetValue(streamId, out state!))
                _recvStreams[streamId] = state = new ReceiveStreamState();
        }

        if (isFragmented)
        {
            if (state.FragmentBuffer == null)
            {
                state.FragmentBuffer = new MemoryStream(Math.Max(content.Length * 4, 8192));
                state.FirstFragmentCmdType = cmdType;
                state.FirstFragmentSequence = sequence;
            }
            if (state.FragmentBuffer.Length + content.Length > MaxReassemblyBytes)
            {
                DropFragments(state);
                throw new InvalidDataException("WXP2P fragmented payload exceeds maximum frame size.");
            }
            state.FragmentBuffer.Write(content);
            Interlocked.Add(ref _assemblingBytes, content.Length);
            return;
        }

        uint finalCmdType;
        byte[] completeCommand;
        if (state.FragmentBuffer != null)
        {
            if (state.FragmentBuffer.Length + content.Length > MaxReassemblyBytes)
            {
                DropFragments(state);
                throw new InvalidDataException("WXP2P fragmented payload exceeds maximum frame size.");
            }
            state.FragmentBuffer.Write(content);
            Interlocked.Add(ref _assemblingBytes, content.Length);
            byte[] fullContent = state.FragmentBuffer.ToArray();
            DropFragments(state);

            finalCmdType = state.FirstFragmentCmdType;
            completeCommand = new WeTypeWire()
                .Number(1, finalCmdType)
                .Bytes(2, fullContent)
                .Number(3, state.FirstFragmentSequence)
                .Encode();
        }
        else
        {
            finalCmdType = cmdType;
            completeCommand = payload;
        }

        if (finalCmdType == RudpCmdData)
        {
            if (_tracedDataCount <= 20)
                _trace?.Invoke($"WXP2P delivered RudpCmdData: stream={streamId}, len={completeCommand.Length}.");
            _incoming.Writer.TryWrite(completeCommand);
        }
        else if (finalCmdType == RudpCmdHangup)
        {
            _trace?.Invoke($"WXP2P received RudpCmdHangup (1201) on stream={streamId}.");
            Volatile.Write(ref _remoteHangup, true);
            _incoming.Writer.TryComplete();
        }
        else
        {
            _trace?.Invoke($"WXP2P received unknown RudpCmd cmdType={finalCmdType} on stream={streamId}, len={completeCommand.Length}.");
        }
    }

    private void DropFragments(ReceiveStreamState state)
    {
        if (state.FragmentBuffer is not { } buffer) return;
        Interlocked.Add(ref _assemblingBytes, -buffer.Length);
        buffer.Dispose();
        state.FragmentBuffer = null;
    }

    private async Task HandleMpRudpFeedbackAsync(byte[] content)
    {
        WeTypeWire fb;
        try { fb = WeTypeWire.Parse(content); }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { return; }

        List<(OutgoingPacket Packet, uint MinQ)> toRetransmit = [];
        bool freedAny = false;
        long now = Stopwatch.GetTimestamp();
        string? fbSummary = null;

        lock (_sync)
        {
            foreach (var sf in fb.Messages(1))
            {
                uint streamId = (uint)sf.GetNumber(1);
                uint maxPktNum = (uint)sf.GetNumber(3);
                HashSet<uint> lost = [];
                foreach (var block in sf.Messages(2))
                {
                    uint baseLost = (uint)block.GetNumber(1);
                    uint mask = (uint)block.GetNumber(2);
                    if (baseLost > 0) lost.Add(baseLost);
                    for (int bit = 0; bit < 16; bit++)
                    {
                        if ((mask & (1u << bit)) != 0)
                            lost.Add(baseLost + 1u + (uint)bit);
                    }
                }

                List<uint> acked = [];
                foreach (var (pktNum, entry) in _unacked)
                {
                    if (entry.StreamId != streamId) continue;
                    if (pktNum <= maxPktNum && !lost.Contains(pktNum))
                        acked.Add(pktNum);
                }

                foreach (uint pktNum in acked)
                {
                    _unacked.Remove(pktNum);
                    freedAny = true;
                }

                if (_tracedFeedbackCount < 15)
                {
                    _tracedFeedbackCount++;
                    fbSummary = $"WXP2P feedback #{_tracedFeedbackCount}: stream={streamId}, maxPkt={maxPktNum}, lost={lost.Count}, acked={acked.Count}, unackedLeft={_unacked.Count}.";
                }

                uint minQ = _unacked.Count > 0 ? _unacked.Keys.Min() : _nextPktNum;
                foreach (uint lostPkt in lost.Order())
                {
                    if (_unacked.TryGetValue(lostPkt, out var entry) && entry.StreamId == streamId &&
                        Stopwatch.GetElapsedTime(entry.LastSentTicks, now).TotalMilliseconds >= 35)
                    {
                        entry.LastSentTicks = now;
                        entry.SendCount++;
                        toRetransmit.Add((entry, minQ));
                    }
                }
            }
        }

        if (fbSummary != null) _trace?.Invoke(fbSummary);

        if (freedAny && _windowSignal.CurrentCount == 0) _windowSignal.Release();

        foreach (var (pkt, minQ) in toRetransmit)
            await TransmitMpRudpDataAsync(pkt, minQ, _cts.Token);
    }

    private async Task SendFeedbackAsync(CancellationToken ct)
    {
        WeTypeWire feedback = new();
        bool hasAny = false;
        uint peerMember;
        ulong largestPathPn;
        int connId;

        lock (_sync)
        {
            peerMember = _peerMemberId;
            largestPathPn = _largestRecvPathPn;
            connId = _activeConnId;
            _feedbackDirty = false;
            _packetsSinceFeedback = 0;
            _lastFeedbackSentTicks = Stopwatch.GetTimestamp();

            foreach (var (streamId, state) in _recvStreams)
            {
                if (state.MaxReceived == 0) continue;
                hasAny = true;
                var sf = new WeTypeWire().Number(1, streamId);

                // Encode missing packet numbers in [NextExpected .. MaxReceived] into BlockInfo { lost_pkt_num, mask }.
                // The sender takes every packet up to the reported highest number that no block lists as received, so
                // when the blocks run out the highest number stops before the first packet they no longer describe.
                uint p = state.NextExpected;
                int blocks = 0;
                uint reported = state.MaxReceived;
                while (p <= state.MaxReceived)
                {
                    if (blocks == 32)
                    {
                        if (!state.Reorder.ContainsKey(p)) { reported = p - 1; break; }
                        p++;
                        continue;
                    }
                    if (state.Reorder.ContainsKey(p)) { p++; continue; }
                    uint baseLost = p;
                    uint mask = 0;
                    for (int bit = 0; bit < 16; bit++)
                    {
                        uint q = baseLost + 1u + (uint)bit;
                        if (q > state.MaxReceived) break;
                        if (!state.Reorder.ContainsKey(q))
                            mask |= 1u << bit;
                    }
                    sf.Message(2, new WeTypeWire().Number(1, baseLost).Number(2, mask));
                    blocks++;
                    p = baseLost + 17u;
                }
                sf.Number(3, reported);
                feedback.Message(1, sf);
            }
        }

        if (!hasAny) return;

        if (largestPathPn > 0)
        {
            var pathAck = new WeTypeWire()
                .Number(1, (uint)connId)
                .Number(2, largestPathPn)
                .Number(3, 0)
                .Message(4, new WeTypeWire().Number(1, 1).Number(2, largestPathPn));
            feedback.Message(3, pathAck);
        }

        var mpPkt = new WeTypeWire()
            .Number(1, 2) // head_sign = MpRudpFeedback
            .Number(2, 0)
            .Number(3, (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .Bytes(4, feedback.Encode())
            .Number(5, peerMember);

        await SendRelayDataAsync(mpPkt.Encode(), ct);
    }

    private async Task TimerLoopAsync()
    {
        long lastStatTicks = Stopwatch.GetTimestamp();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(35, _cts.Token);
                long now = Stopwatch.GetTimestamp();

                // 1. Periodic StatReq heartbeat.
                IPEndPoint? activeEp;
                int connId;
                bool sendFeedback;
                List<(OutgoingPacket Packet, uint MinQ)> rtoList = [];

                lock (_sync)
                {
                    activeEp = _activeEndPoint;
                    connId = _activeConnId;
                    double sinceFeedback = Stopwatch.GetElapsedTime(_lastFeedbackSentTicks, now).TotalMilliseconds;
                    // A hole is reported again every 50 ms until it is filled, as the official receiver does: a single
                    // report that is lost would otherwise leave the sender waiting for its own timeout.
                    sendFeedback = _feedbackDirty && sinceFeedback >= 30 ||
                        sinceFeedback >= 50 && _recvStreams.Values.Any(state => state.NextExpected <= state.MaxReceived);

                    if (_unacked.Count > 0)
                    {
                        int rtoBase = Math.Clamp(Volatile.Read(ref _rttMs) * 2, 180, 1200);
                        uint minQ = _unacked.Keys.Min();
                        foreach (var entry in _unacked.Values.OrderBy(e => e.PktNum))
                        {
                            double rto = Math.Min(rtoBase * (1 + Math.Min(entry.SendCount - 1, 4) * 0.5), 2500);
                            if (Stopwatch.GetElapsedTime(entry.LastSentTicks, now).TotalMilliseconds >= rto)
                            {
                                entry.LastSentTicks = now;
                                entry.SendCount++;
                                rtoList.Add((entry, minQ));
                                if (rtoList.Count >= 48) break;
                            }
                        }
                    }
                }

                if (sendFeedback)
                    await SendFeedbackAsync(_cts.Token);

                foreach (var (pkt, minQ) in rtoList)
                    await TransmitMpRudpDataAsync(pkt, minQ, _cts.Token);

                if (activeEp != null && Stopwatch.GetElapsedTime(lastStatTicks, now).TotalSeconds >= _statIntervalSec)
                {
                    lastStatTicks = now;
                    await _transport.SendAsync(BuildStatPacket(connId), activeEp, _cts.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) when (ex is IOException or SocketException) { }
    }

    private WeTypeWire BuildC2SHeader(uint cmdType, int connId)
    {
        uint nowSec = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pub = new WeTypeWire().Number(1, cmdType).Number(2, 3).Number(3, 0);
        var header = new WeTypeWire()
            .Message(1, pub)
            .Number(2, _dispatch.RoomId > uint.MaxValue ? 0UL : _dispatch.RoomId)
            .Number(3, BusinessId)
            .Number(4, _dispatch.MemberId)
            .Bytes(5, [])
            .Number(6, _dispatch.MemberId)
            .Number(8, nowSec)
            .Number(9, _dispatch.RoomId > uint.MaxValue ? _dispatch.RoomId : 0UL)
            .Number(10, (uint)connId);
        return header;
    }

    private byte[] BuildCheckInPacket(IPEndPoint server, int connId)
    {
        uint sn = Interlocked.Increment(ref _checkinSn);
        var local = _transport.LocalEndPoint;
        uint clientIp = IpToUInt32(local.Address);
        uint serverIp = IpToUInt32(server.Address);

        var body = new WeTypeWire()
            .Number(1, 0)
            .Number(2, (uint)_dispatch.RoomId)
            .Number(3, clientIp)
            .Number(4, serverIp)
            .Number(5, (uint)local.Port)
            .Number(6, (uint)server.Port)
            .Number(7, sn)
            .Number(8, _dispatch.MemberId)
            .Number(9, _dispatch.MemberId)
            .Number(10, (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .Number(12, 0)
            .Number(13, 1)
            .Text(14, local.Address.ToString())
            .Text(15, server.Address.ToString())
            .Number(16, 1) // headerCapVersion = 1 for businessID 10
            .Number(17, 0)
            .Number(18, 0)
            .Number(19, 0)
            .Number(20, 1);

        byte[] encrypted = WeTypeRelayCrypto.TeaEncrypt(body.Encode(), _controlKey);
        var c2s = new WeTypeWire()
            .Message(1, BuildC2SHeader(CmdCheckInReq, connId))
            .Number(2, (ulong)encrypted.Length)
            .Bytes(3, encrypted);
        return WeTypeRelayCrypto.PackRelayControlPacket(c2s);
    }

    private byte[] BuildStatPacket(int connId)
    {
        var now = DateTimeOffset.UtcNow;
        uint sendCnt, recvCnt;
        ulong sendBytes, recvBytes;
        lock (_sync)
        {
            sendCnt = _sendCount;
            sendBytes = _sendBytes;
            recvCnt = _recvCount;
            recvBytes = _recvBytes;
        }
        var body = new WeTypeWire()
            .Number(1, _dispatch.RoomId > uint.MaxValue ? 0UL : _dispatch.RoomId)
            .Number(2, 0)
            .Number(3, (ulong)now.ToUnixTimeSeconds())
            .Number(4, (ulong)now.Millisecond)
            .Number(5, sendCnt)
            .Number(6, (ulong)Math.Max(0, Volatile.Read(ref _rttMs)))
            .Number(7, _dispatch.MemberId)
            .Number(8, _dispatch.MemberId)
            .Number(9, _dispatch.RoomId > uint.MaxValue ? _dispatch.RoomId : 0UL)
            .Number(10, 1)
            .Number(11, sendBytes)
            .Number(12, recvCnt)
            .Number(13, recvBytes)
            .Number(18, (ulong)now.ToUnixTimeMilliseconds());

        byte[] encrypted = WeTypeRelayCrypto.TeaEncrypt(body.Encode(), _controlKey);
        var c2s = new WeTypeWire()
            .Message(1, BuildC2SHeader(CmdStatReq, connId))
            .Number(2, (ulong)encrypted.Length)
            .Bytes(3, encrypted);
        return WeTypeRelayCrypto.PackRelayControlPacket(c2s);
    }

    private byte[] BuildVerifyIpPortRespPacket(IPEndPoint server, WeTypeWire req)
    {
        int connId;
        lock (_sync) connId = _activeConnId;
        var body = new WeTypeWire()
            .Number(1, 0)
            .Number(2, _dispatch.RoomId > uint.MaxValue ? 0UL : _dispatch.RoomId)
            .Number(3, req.GetNumber(3))
            .Number(4, IpToUInt32(server.Address))
            .Number(5, req.GetNumber(4))
            .Number(6, (uint)server.Port)
            .Number(7, _dispatch.MemberId)
            .Number(8, _dispatch.MemberId)
            .Number(9, _dispatch.RoomId > uint.MaxValue ? _dispatch.RoomId : 0UL)
            .Text(10, req.GetText(8));

        byte[] encrypted = WeTypeRelayCrypto.TeaEncrypt(body.Encode(), _controlKey);
        var c2s = new WeTypeWire()
            .Message(1, BuildC2SHeader(CmdVerifyIpPortResp, connId))
            .Number(2, (ulong)encrypted.Length)
            .Bytes(3, encrypted);
        return WeTypeRelayCrypto.PackRelayControlPacket(c2s);
    }

    private static IPEndPoint? ParseRedirectTarget(WeTypeWire body)
    {
        foreach (var svr in body.Messages(10))
        {
            ulong port = svr.GetNumber(2);
            if (port is 0 or > 65535) continue;
            string ipText = svr.GetText(3);
            if (IPAddress.TryParse(ipText, out var addr)) return new IPEndPoint(addr, (int)port);
            uint ipU32 = (uint)svr.GetNumber(1);
            if (ipU32 != 0) return new IPEndPoint(new IPAddress(ipU32), (int)port);
        }
        ulong redirectPort = body.GetNumber(7);
        uint redirectIp = (uint)body.GetNumber(4);
        if (redirectIp != 0 && redirectPort is > 0 and <= 65535)
            return new IPEndPoint(new IPAddress(redirectIp), (int)redirectPort);
        return null;
    }

    private static uint IpToUInt32(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return 0;
        byte[] bytes = address.GetAddressBytes();
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _incoming.Writer.TryComplete();
        await _transport.DisposeAsync();
        if (_recvLoop != null) { try { await _recvLoop; } catch { } }
        if (_timerLoop != null) { try { await _timerLoop; } catch { } }
        lock (_sync)
        {
            foreach (var state in _recvStreams.Values) DropFragments(state);
        }
        _windowSignal.Dispose();
        _cts.Dispose();
    }
}
