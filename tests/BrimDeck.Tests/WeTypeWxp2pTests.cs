using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using BrimDeck.Core.ClipboardSync;

internal static class WeTypeWxp2pTests
{
    internal static void Run(Action<string, bool> check)
    {
        CheckCryptoAndFraming(check);
        CheckLoopbackFileTransferAsync(check, dropRatePercent: 0).GetAwaiter().GetResult();
        CheckLoopbackFileTransferAsync(check, dropRatePercent: 12).GetAwaiter().GetResult();
        CheckKeptConnectionAsync(check).GetAwaiter().GetResult();
    }

    private static void CheckCryptoAndFraming(Action<string, bool> check)
    {
        check("WXP2P CRC-32 matches standard IEEE 802.3 vector",
            WeTypeRelayCrypto.Crc32("123456789"u8) == 0xCBF43926u);
        check("WXP2P Adler-32 matches standard vector",
            WeTypeRelayCrypto.Adler32("Wikipedia"u8) == 0x11E60398u);

        byte[] teaKey = WeTypeRelayCrypto.DeriveControlKey(0x0123456789ABCDEFUL);
        bool teaOk = true;
        foreach (int len in new[] { 0, 1, 2, 7, 8, 9, 15, 16, 63, 128 })
        {
            byte[] plain = RandomNumberGenerator.GetBytes(len);
            byte[] cipher = WeTypeRelayCrypto.TeaEncrypt(plain, teaKey);
            teaOk &= (cipher.Length & 7) == 0 &&
                WeTypeRelayCrypto.TeaDecrypt(cipher, teaKey, out var roundtrip) &&
                roundtrip.SequenceEqual(plain);
            if (cipher.Length > 0)
            {
                cipher[cipher.Length - 1] ^= 0xFF;
                teaOk &= !WeTypeRelayCrypto.TeaDecrypt(cipher, teaKey, out _);
            }
        }
        check("WXP2P 16-round OICQ TEA round-trips all lengths and rejects tampering", teaOk);

        // Original ChaCha20-Poly1305 (8-byte nonce, 64-bit counter, unpadded MAC): draft-agl-tls-chacha20poly1305-04 vector.
        byte[] vecKey = Convert.FromHexString("4290bcb154173531f314af57f3be3b5006da371ece272afa1b5dbdd1100a1007");
        byte[] vecNonceBytes = Convert.FromHexString("cd7cf67be39c794a");
        ulong vecNonce = BinaryPrimitives.ReadUInt64LittleEndian(vecNonceBytes);
        byte[] vecAad = Convert.FromHexString("87e229d4500845a079c0");
        byte[] vecPlain = Convert.FromHexString("86d09974840bded2a5ca");
        byte[] vecExpected = Convert.FromHexString("e3e446f7ede9a19b62a4677dabf4e3d24b876bb284753896e1d6");
        byte[] vecActual = WeTypeRelayCrypto.ChaCha20Poly1305OriginalEncrypt(vecKey, vecNonce, vecPlain, vecAad);
        check("WXP2P original ChaCha20-Poly1305 matches libsodium test vector",
            vecActual.SequenceEqual(vecExpected) &&
            WeTypeRelayCrypto.ChaCha20Poly1305OriginalDecrypt(vecKey, vecNonce, vecActual, out var vecDec, vecAad) &&
            vecDec.SequenceEqual(vecPlain));

        byte[] tampered = (byte[])vecActual.Clone();
        tampered[^1] ^= 1;
        check("WXP2P original ChaCha20-Poly1305 rejects tampered tags",
            !WeTypeRelayCrypto.ChaCha20Poly1305OriginalDecrypt(vecKey, vecNonce, tampered, out _, vecAad));

        // 0x96 LiveCloud data packet pack/unpack
        const ulong roomId = 0x1122334455667788UL;
        const ulong roomKey = 0x8877665544332211UL;
        byte[] dataKey = RandomNumberGenerator.GetBytes(32);
        byte[] payload = "wxp2p-mp-rudp-payload"u8.ToArray();
        byte[] pkt96 = WeTypeRelayCrypto.PackLiveCloud96(roomId, roomKey, dataKey, memberId: 0, sn: 7, dataTypeSeq: 12, connId: 3, payload);
        check("WXP2P 0x96 packet encodes 19-byte LE header and unpacks for peer while dropping self loopback",
            pkt96[0] == 0x96 && pkt96[1] == 19 && pkt96[14] == 0x50 && pkt96[18] == 3 &&
            WeTypeRelayCrypto.TryUnpackRelayData(pkt96, roomKey, dataKey, selfMemberId: 1, out byte dt, out ushort sender, out var unpacked) &&
            dt == WeTypeRelayCrypto.DataTypeMpRudp && sender == 0 && unpacked.SequenceEqual(payload) &&
            !WeTypeRelayCrypto.TryUnpackRelayData(pkt96, roomKey, dataKey, selfMemberId: 0, out _, out _, out _));

        byte[] tcpFramed = WeTypeRelayCrypto.PackTcpFrame(pkt96);
        check("WXP2P TCP frame prefixes 0xD8 header with big-endian length",
            tcpFramed.Length == pkt96.Length + 9 && tcpFramed[0] == 0xD8 &&
            BinaryPrimitives.ReadUInt32BigEndian(tcpFramed.AsSpan(1, 4)) == (uint)(pkt96.Length + 8) &&
            BinaryPrimitives.ReadUInt16BigEndian(tcpFramed.AsSpan(5, 2)) == 8 &&
            BinaryPrimitives.ReadUInt16BigEndian(tcpFramed.AsSpan(7, 2)) == 1);

        // Full dispatch parsing
        var ep = new WeTypeWire().Text(1, "198.51.100.10").Number(2, 16285);
        var conn = new WeTypeWire().Number(1, 3).Message(4, ep).Message(5, ep);
        var strat = new WeTypeWire().Number(5, 5).Number(9, 1).Bytes(10, dataKey).Message(15, conn);
        var dispatchWire = new WeTypeWire().Number(1, roomId).Number(2, roomKey).Number(3, 1).Message(4, strat).Number(5, 999);
        var dispatch = WeTypeFileDispatch.Parse(dispatchWire.Encode());
        check("WXP2P dispatch parser reads room credentials, encryption key, and conn_set",
            dispatch.CanRelay && dispatch.RoomId == roomId && dispatch.RoomKey == roomKey &&
            dispatch.MemberId == 1 && dispatch.PeerMemberId == 0 && dispatch.EncryptKey.SequenceEqual(dataKey) &&
            dispatch.Connections.Count == 1 && dispatch.Connections[0].ConnId == 3);
    }

    private static async Task CheckLoopbackFileTransferAsync(Action<string, bool> check, int dropRatePercent)
    {
        const ulong roomId = 0x2026112100000042UL;
        const ulong roomKey = 0x5A5A12349876CDEFUL;
        byte[] encryptKey = RandomNumberGenerator.GetBytes(32);
        var relayEp = new IPEndPoint(IPAddress.Loopback, 16285);

        WeTypeFileDispatch BuildDispatch(uint memberId)
        {
            var ep = new WeTypeWire().Text(1, relayEp.Address.ToString()).Number(2, (ulong)relayEp.Port);
            var conn = new WeTypeWire().Number(1, 1).Message(4, ep);
            var strat = new WeTypeWire().Number(5, 5).Number(9, 1).Bytes(10, encryptKey).Message(15, conn);
            var wire = new WeTypeWire().Number(1, roomId).Number(2, roomKey).Number(3, memberId).Message(4, strat);
            return WeTypeFileDispatch.Parse(wire.Encode());
        }

        var hub = new SimulatedRelayHub(roomId, roomKey, relayEp, dropRatePercent);
        var senderTransport = hub.CreateClient(memberId: 0, port: 41000);
        var receiverTransport = hub.CreateClient(memberId: 1, port: 41001);

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var outDir = dir.CreateSubdirectory("out");
            var bigPath = Path.Combine(dir.FullName, "big-relay.bin");
            var emptyPath = Path.Combine(dir.FullName, "zero.txt");
            // 650 KiB exercises multi-chunk FileChunk + BigRudp fragmentation (~570 UDP packets).
            byte[] bigBytes = RandomNumberGenerator.GetBytes(650 * 1024 + 37);
            await File.WriteAllBytesAsync(bigPath, bigBytes);
            await File.WriteAllBytesAsync(emptyPath, []);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await using var sender = await WeTypeWxp2pSession.ConnectWithTransportAsync(
                BuildDispatch(0), senderTransport, [(relayEp, 1)], deadline.Token);
            await using var receiver = await WeTypeWxp2pSession.ConnectWithTransportAsync(
                BuildDispatch(1), receiverTransport, [(relayEp, 1)], deadline.Token);

            var saved = new List<string>();
            var progress = new Progress<WeTypeSendProgress>();
            var call = new WeTypeIncomingCall(new WeTypeFileInbox(outDir.FullName, progress, saved.Add));

            var recvTask = Task.Run(async () =>
            {
                await using var commands = receiver.IncomingCommands(deadline.Token).GetAsyncEnumerator(deadline.Token);
                if (!await commands.MoveNextAsync()) throw new EndOfStreamException();
                await call.ReceiveWxp2pAsync(receiver, commands.Current, commands, deadline.Token);
            }, deadline.Token);

            await WeTypeFileStream.SendOverWxp2pAsync(sender, [bigPath, emptyPath], progress, deadline.Token);
            await recvTask;
            await call.Finished.Task.WaitAsync(deadline.Token);

            bool ok = saved.Count == 2 &&
                (await File.ReadAllBytesAsync(saved[0])).SequenceEqual(bigBytes) &&
                new FileInfo(saved[1]).Length == 0 &&
                (dropRatePercent == 0 || hub.DroppedDataPackets > 0);
            check($"WXP2P relay end-to-end file transfer succeeds ({dropRatePercent}% packet loss)", ok);
        }
        finally { dir.Delete(recursive: true); }
    }

    // After sending, the phone sends its next files through the same connection instead of a new invitation.
    private static async Task CheckKeptConnectionAsync(Action<string, bool> check)
    {
        const ulong roomId = 0x2026112100000077UL;
        const ulong roomKey = 0x1122334455667788UL;
        byte[] encryptKey = RandomNumberGenerator.GetBytes(32);
        var relayEp = new IPEndPoint(IPAddress.Loopback, 16285);
        WeTypeFileDispatch BuildDispatch(uint memberId)
        {
            var ep = new WeTypeWire().Text(1, relayEp.Address.ToString()).Number(2, (ulong)relayEp.Port);
            var conn = new WeTypeWire().Number(1, 1).Message(4, ep);
            var strat = new WeTypeWire().Number(5, 5).Number(9, 1).Bytes(10, encryptKey).Message(15, conn);
            var wire = new WeTypeWire().Number(1, roomId).Number(2, roomKey).Number(3, memberId).Message(4, strat);
            return WeTypeFileDispatch.Parse(wire.Encode());
        }
        var hub = new SimulatedRelayHub(roomId, roomKey, relayEp, 0);
        var pcTransport = hub.CreateClient(memberId: 0, port: 42000);
        var phoneTransport = hub.CreateClient(memberId: 1, port: 42001);
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var pc = await WeTypeWxp2pSession.ConnectWithTransportAsync(BuildDispatch(0), pcTransport, [(relayEp, 1)], deadline.Token);
            await using var phone = await WeTypeWxp2pSession.ConnectWithTransportAsync(BuildDispatch(1), phoneTransport, [(relayEp, 1)], deadline.Token);
            await using var link = new WeTypeRelayLink(pc, 8, null);

            byte[] content = RandomNumberGenerator.GetBytes(3000);
            using var hasher = Blake3.Managed.Hasher.New();
            hasher.Update(content);
            string id = Convert.ToHexStringLower(hasher.Finalize().AsSpan());
            uint trunk = 0;
            async Task PhoneSends(WeTypeWire chunk) =>
                await phone.SendFileCommandAsync(new WeTypeFileDataCommand(trunk++, chunk).Encode(), deadline.Token);
            WeTypeWire Control(ulong command) => new WeTypeWire().Message(1, new WeTypeWire().Number(1, 200))
                .Message(5, new WeTypeWire().Number(1, trunk + 1).Number(2, command).Bytes(3, "{}"u8.ToArray())).Number(7, trunk).Bytes(8, [(byte)(trunk + 1)]);
            WeTypeWire FileChunk(ulong kind) => new WeTypeWire().Message(1, new WeTypeWire().Number(1, kind)
                .Message(2, new WeTypeWire().Text(1, id).Text(2, "kept.bin").Number(3, (ulong)content.Length)));

            var waiting = link.WaitForFilesAsync(TimeSpan.FromSeconds(15));
            await PhoneSends(Control(1101));
            await PhoneSends(FileChunk(1).Number(6, 0));
            bool started = await waiting;
            await PhoneSends(FileChunk(2).Bytes(2, content).Number(3, 0).Number(4, (ulong)content.Length).Number(6, 1));
            await PhoneSends(Control(1001));

            var saved = new List<string>();
            if (started)
                await link.ReceiveAsync(Task.FromResult(dir.FullName), new Progress<WeTypeSendProgress>(), saved.Add, () => { }, deadline.Token)
                    .WaitAsync(deadline.Token);

            bool heartbeatAcknowledged = false;
            await using (var replies = phone.IncomingCommands(deadline.Token).GetAsyncEnumerator(deadline.Token))
            {
                while (!heartbeatAcknowledged && await replies.MoveNextAsync())
                {
                    var reply = WeTypeFileDataCommand.Decode(replies.Current.Span).FileChunk;
                    heartbeatAcknowledged = reply.GetMessage(1).GetNumber(1) == 5 && reply.Has(8);
                }
            }
            check("A kept relay connection answers the phone's heartbeat and saves files it sends later",
                started && heartbeatAcknowledged && saved.Count == 1 && (await File.ReadAllBytesAsync(saved[0])).SequenceEqual(content));
        }
        finally { dir.Delete(recursive: true); }
    }

    // Minimal in-memory Tencent relay simulator speaking real 0xD5 CRC+TEA CheckIn/NotifyReady/Stat and forwarding 0x96 packets.
    private sealed class SimulatedRelayHub(ulong roomId, ulong roomKey, IPEndPoint relayEp, int dropRatePercent)
    {
        private readonly byte[] _controlKey = WeTypeRelayCrypto.DeriveControlKey(roomKey);
        private readonly Dictionary<uint, SimulatedClientTransport> _clients = [];
        private readonly HashSet<uint> _checkedIn = [];
        private readonly Random _rng = new(20261121);
        private readonly object _lock = new();
        private int _droppedDataPackets;

        public int DroppedDataPackets => Volatile.Read(ref _droppedDataPackets);

        public SimulatedClientTransport CreateClient(uint memberId, int port)
        {
            var client = new SimulatedClientTransport(this, memberId, new IPEndPoint(IPAddress.Loopback, port));
            lock (_lock) _clients[memberId] = client;
            return client;
        }

        internal void OnPacketFromClient(uint senderMemberId, byte[] packet)
        {
            if (WeTypeRelayCrypto.TryUnpackRelayControlPacket(packet, out var c2s))
            {
                var header = c2s.GetMessage(1);
                var pub = header.GetMessage(1);
                uint cmd = (uint)pub.GetNumber(1);
                if (header.GetNumber(3) != 10) return; // businessID must be 10
                if (!WeTypeRelayCrypto.TeaDecrypt(c2s.GetBytes(3), _controlKey, out var plain)) return;
                var body = WeTypeWire.Parse(plain);

                if (cmd == 2) // CheckInReq
                {
                    if (body.GetNumber(16) != 1) return; // headerCapVersion must be 1 for businessID 10
                    var respBody = new WeTypeWire()
                        .Number(1, (uint)roomId).Number(2, 0).Number(3, 0).Number(4, 0).Number(5, 0)
                        .Number(6, body.GetNumber(7)).Number(7, 1).Number(8, 5).Number(9, senderMemberId)
                        .Number(11, roomId);
                    SendS2C(senderMemberId, 0x1002, respBody);

                    bool bothReady;
                    lock (_lock)
                    {
                        _checkedIn.Add(senderMemberId);
                        bothReady = _checkedIn.Count >= 2;
                    }
                    if (bothReady)
                    {
                        foreach (uint member in _clients.Keys)
                        {
                            var ready = new WeTypeWire().Number(1, 0).Number(2, (uint)roomId).Number(3, 2)
                                .Message(4, new WeTypeWire().Number(1, 0).Number(2, 0).Number(4, 0))
                                .Message(4, new WeTypeWire().Number(1, 0).Number(2, 1).Number(4, 1))
                                .Number(5, member).Number(6, 1).Number(7, member).Number(8, roomId);
                            SendS2C(member, 0x1004, ready);
                        }
                    }
                }
                else if (cmd == 4) // StatReq
                {
                    var statResp = new WeTypeWire()
                        .Number(1, (uint)roomId).Number(2, 1).Number(3, body.GetNumber(3)).Number(4, body.GetNumber(4))
                        .Number(5, 1).Number(6, senderMemberId).Number(7, senderMemberId).Number(9, roomId)
                        .Number(10, body.GetNumber(18));
                    SendS2C(senderMemberId, 0x100A, statResp);
                }
                return;
            }

            if (packet.Length > 0 && packet[0] == WeTypeRelayCrypto.DataMagicLiveCloud96)
            {
                bool drop = false;
                lock (_lock)
                {
                    if (dropRatePercent > 0 && _rng.Next(100) < dropRatePercent)
                    {
                        drop = true;
                        _droppedDataPackets++;
                    }
                }
                if (drop) return;

                foreach (var (member, client) in _clients)
                {
                    if (member != senderMemberId)
                        client.EnqueueFromRelay((byte[])packet.Clone(), relayEp);
                }
            }
        }

        private void SendS2C(uint memberId, uint cmdType, WeTypeWire body)
        {
            byte[] encrypted = WeTypeRelayCrypto.TeaEncrypt(body.Encode(), _controlKey);
            var s2cHeader = new WeTypeWire()
                .Message(1, new WeTypeWire().Number(1, cmdType).Number(2, 3).Number(3, 0))
                .Number(2, roomId > uint.MaxValue ? 0UL : roomId)
                .Number(3, 10)
                .Number(5, roomId > uint.MaxValue ? roomId : 0UL)
                .Number(6, 1)
                .Number(7, 0);
            var s2c = new WeTypeWire().Message(1, s2cHeader).Number(2, (ulong)encrypted.Length).Bytes(3, encrypted);
            byte[] framed = WeTypeRelayCrypto.PackRelayControlPacket(s2c);
            if (_clients.TryGetValue(memberId, out var client))
                client.EnqueueFromRelay(framed, relayEp);
        }

        internal sealed class SimulatedClientTransport(SimulatedRelayHub hub, uint memberId, IPEndPoint localEp) : IWeTypeRelayTransport
        {
            private readonly Channel<(byte[] Data, IPEndPoint Remote)> _inbox = Channel.CreateUnbounded<(byte[], IPEndPoint)>();

            public IPEndPoint LocalEndPoint => localEp;
            public bool IsTcp => false;

            public ValueTask SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint target, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                hub.OnPacketFromClient(memberId, packet.ToArray());
                return ValueTask.CompletedTask;
            }

            public ValueTask<(byte[] Data, IPEndPoint Remote)> ReceiveAsync(CancellationToken ct) =>
                _inbox.Reader.ReadAsync(ct);

            internal void EnqueueFromRelay(byte[] packet, IPEndPoint from) =>
                _inbox.Writer.TryWrite((packet, from));

            public ValueTask DisposeAsync()
            {
                _inbox.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }
}
