using System.Net;
using BrimDeck.Core.ClipboardSync;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

internal static class WeTypeFileTests
{
    internal static void Run(Action<string, bool> check)
    {
        var range = new WeTypeWire().Message(1, new WeTypeWire().Number(1, 5).Message(2, new WeTypeWire().Text(1, "file")));
        check("WeType empty file needs an explicit range", !WeTypeFileStream.IsRangeAck(range, "file", 0, 0));
        range.Number(3, 0).Number(4, 0);
        check("WeType accepts an acknowledged empty file", WeTypeFileStream.IsRangeAck(range, "file", 0, 0));
        check("WeType rejects another file's acknowledgement", !WeTypeFileStream.IsRangeAck(range, "other", 0, 0));
        check("WeType rejects a short acknowledgement", !WeTypeFileStream.IsRangeAck(range, "file", 0, 1));

        var endpoint = new WeTypeWire().Text(1, "192.0.2.1").Number(2, 443);
        var connection = new WeTypeWire().Message(4, endpoint).Message(5, endpoint).Message(4, endpoint)
            .Message(4, new WeTypeWire().Text(1, "192.0.2.2").Number(2, 65536));
        var room = new WeTypeWire().Number(1, 77).Number(2, 88).Number(3, 2)
            .Message(4, new WeTypeWire().Message(15, connection));
        var candidates = WeTypeFileDispatch.Parse(room.Encode()).Relays;
        check("WeType distinguishes TCP and UDP relays and filters invalid or duplicate endpoints",
            candidates.Count == 2 && candidates.Select(e => e.Transport).Distinct().Count() == 2 &&
            candidates.All(e => e.Address.Port == 443 && e.Address.Address.Equals(IPAddress.Parse("192.0.2.1"))));
        bool rejected = false;
        try { WeTypeFileDispatch.Parse(new WeTypeWire().Message(4, connection).Encode()); }
        catch (InvalidDataException) { rejected = true; }
        check("WeType dispatch without room parameters is rejected", rejected);
        CheckNativeReceiverEof(check).GetAwaiter().GetResult();
        CheckIncomingFiles(check).GetAwaiter().GetResult();
        WeTypeWxp2pTests.Run(check);
    }

    private static async Task CheckIncomingFiles(Action<string, bool> check)
    {
        check("WeType accepts a plain Unicode file name", WeTypeFileInbox.SafeName("测试.txt"));
        check("WeType takes the client role when the sender prefers to listen",
            WeTypeSyncClient.ShouldDialSender(new WeTypeWire().Text(3, "{\"preferred_client\":false}")));
        check("WeType listens when the sender prefers to connect or omits its preference",
            !WeTypeSyncClient.ShouldDialSender(new WeTypeWire().Text(3, "{\"preferred_client\":true}")) &&
            !WeTypeSyncClient.ShouldDialSender(new WeTypeWire()));
        var group = new WeTypeWire().Message(2, new WeTypeWire().Number(1, 8).Message(2, new WeTypeWire().Text(1, "phone")));
        WeTypeWire Offer(string code, ulong peer, ulong time) => new WeTypeWire().Text(1, code).Number(3, peer).Number(4, time);
        group.Message(7, Offer("file-selected", 8, 30)).Message(7, Offer("device-selected", 8, 20)).Message(7, Offer("unknown", 44, 40));
        check("WeType uses the latest paired invitation when an older device-selection offer is still listed",
            WeTypeSyncClient.LatestIncomingFile(group, 7)?.SameOffer(new("file-selected", 8, "phone")) == true);
        check("WeType rejects paths, streams, and device names",
            new[] { "../bad", @"a\b", "x:stream", "LPT1.txt", "CON ", "COM².txt" }.All(n => !WeTypeFileInbox.SafeName(n)));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var destination = dir.CreateSubdirectory("received");
            var source = Path.Combine(dir.FullName, "sample.bin");
            var empty = Path.Combine(dir.FullName, "empty.txt");
            var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 19);
            await File.WriteAllBytesAsync(source, bytes);
            await File.WriteAllBytesAsync(empty, []);
            await File.WriteAllTextAsync(Path.Combine(destination.FullName, "sample.bin"), "original");
            using var sender = new WeTypeTransferCertificate();
            using var receiver = new WeTypeTransferCertificate(server: true);
            using var ca = X509Certificate2.CreateFromPem(sender.AuthorityPem);
            var saved = new List<string>();
            var progress = new Progress<WeTypeSendProgress>();
            var folderChosen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var call = new WeTypeIncomingCall(new(folderChosen.Task, progress, saved.Add));
            await using var server = WeTypeSyncClient.CreateFileListener(receiver, call, Task.FromResult(ca.RawData), IPAddress.Loopback);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await server.StartAsync(deadline.Token);
            try
            {
                var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
                {
                    DisposeHttpClient = true,
                    HttpHandler = new SocketsHttpHandler { UseProxy = false, SslOptions = new()
                    {
                        ClientCertificates = new() { sender.Client },
                        LocalCertificateSelectionCallback = (_, _, _, _, _) => sender.Client,
                        RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null && cert.GetRawCertData().SequenceEqual(receiver.Server!.RawData)
                    }}
                });
                var sending = WeTypeFileStream.SendAsync(channel, [source, empty], progress, deadline.Token);
                await call.Connected.Task.WaitAsync(deadline.Token);
                check("WeType can connect before the recipient chooses a folder without saving or acknowledging the file",
                    !sending.IsCompleted && saved.Count == 0 && destination.GetFiles().Length == 1);
                folderChosen.SetResult(destination.FullName);
                await sending;
                await call.Finished.Task.WaitAsync(deadline.Token);
                check("WeType receives and verifies all bytes over mutual TLS", saved.Count == 2 &&
                    (await File.ReadAllBytesAsync(saved[0])).SequenceEqual(bytes) && new FileInfo(saved[1]).Length == 0);
                check("WeType preserves existing files and removes partials", Path.GetFileName(saved[0]) == "sample (1).bin" &&
                    await File.ReadAllTextAsync(Path.Combine(destination.FullName, "sample.bin")) == "original" &&
                    !destination.GetFiles("*.part").Any());
            }
            finally { await server.StopAsync(); }
            await CheckDecodedChunksAsync(check, destination.FullName, progress, deadline.Token);
            await CheckOutOfOrderChunksAsync(check, destination.FullName, progress, deadline.Token);
            var badFile = new WeTypeWire().Text(1, new string('0', 64)).Text(2, "bad.bin").Number(3, 1);
            static WeTypeWire Frame(ulong kind, WeTypeWire file) =>
                new WeTypeWire().Message(1, new WeTypeWire().Number(1, kind).Message(2, file));
            var outgoingConnection = new IncomingFrames();
            var emptyInfo = new WeTypeWire().Text(1, "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262")
                .Text(2, "from-server.txt").Number(3, 0);
            outgoingConnection.Frames.Enqueue(Frame(1, emptyInfo));
            outgoingConnection.Frames.Enqueue(Frame(2, emptyInfo).Bytes(2, []).Number(3, 0).Number(4, 0));
            await new WeTypeFileInbox(destination.FullName, progress, _ => {}).ReceiveAsync(outgoingConnection, outgoingConnection, deadline.Token, helloSent: true);
            check("WeType can choose the folder first and receive metadata immediately on the sender's server stream",
                outgoingConnection.AcknowledgedData && File.Exists(Path.Combine(destination.FullName, "from-server.txt")));
            using var cancel = new CancellationTokenSource();
            var waiting = new IncomingFrames();
            waiting.Frames.Enqueue(Frame(100, new()));
            waiting.Frames.Enqueue(Frame(1, badFile));
            var undecided = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var receiving = new WeTypeFileInbox(undecided.Task, progress, saved.Add).ReceiveAsync(waiting, waiting, cancel.Token);
            cancel.Cancel();
            try { await receiving; }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            check("Closing the folder prompt leaves no file or receipt", receiving.IsCanceled && !waiting.AcknowledgedData && !destination.GetFiles("*.part").Any());
            foreach (bool truncated in new[] { true, false })
            {
                var wire = new IncomingFrames();
                wire.Frames.Enqueue(Frame(100, new()));
                wire.Frames.Enqueue(Frame(1, badFile));
                if (!truncated) wire.Frames.Enqueue(Frame(2, badFile).Bytes(2, [12]).Number(3, 0).Number(4, 1));
                bool rejected = false;
                try { await new WeTypeFileInbox(destination.FullName, progress, saved.Add).ReceiveAsync(wire, wire, deadline.Token); }
                catch (Exception ex) when (truncated ? ex is EndOfStreamException : ex is InvalidDataException) { rejected = true; }
                check("WeType cleans up and withholds the final receipt for " + (truncated ? "a truncated file" : "a digest mismatch"),
                    rejected && !wire.AcknowledgedData && saved.Count == 2 && !destination.GetFiles("*.part").Any() &&
                    !File.Exists(Path.Combine(destination.FullName, "bad.bin")));
            }
        }
        finally { dir.Delete(recursive: true); }
    }

    private static async Task CheckDecodedChunksAsync(Action<string, bool> check, string directory,
        IProgress<WeTypeSendProgress> progress, CancellationToken ct)
    {
        const string digest = "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85";
        // Synthetic "abc" file. The WeType 3.0.2.44 application-message encoder produced these envelopes offline.
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "wetype-file-commands.json");
        using var samples = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(fixture, ct));
        async IAsyncEnumerable<ReadOnlyMemory<byte>> Input()
        {
            await Task.Yield();
            foreach (var sample in samples.RootElement.EnumerateArray())
            {
                var bytes = sample.GetProperty("command").GetBytesFromBase64();
                var command = WeTypeFileDataCommand.Decode(bytes);
                check("WXP2P file command matches native bytes for chunk " + command.ChunkId,
                    command.ChunkId == sample.GetProperty("id").GetUInt32() && command.Encode().SequenceEqual(bytes));
                yield return bytes;
            }
        }
        string? savedPath = null;
        var receipts = new List<(WeTypeWire Frame, bool Saved)>();
        var inbox = new WeTypeFileInbox(directory, progress, path => savedPath = path);
        await inbox.ReceiveFileCommandsAsync(Input(), (frame, _) =>
        {
            receipts.Add((frame, File.Exists(savedPath)));
            return Task.CompletedTask;
        }, ct);
        var expectedReceipts = samples.RootElement.EnumerateArray()
            .Where(sample => sample.TryGetProperty("replyCommand", out _)).ToArray();
        check("File receipts use the same WXP2P envelopes as the native encoder",
            expectedReceipts.Length == receipts.Count && expectedReceipts.Zip(receipts).All(pair =>
                new WeTypeFileDataCommand(pair.First.GetProperty("replyId").GetUInt32(), pair.Second.Frame)
                    .Encode().SequenceEqual(pair.First.GetProperty("replyCommand").GetBytesFromBase64())));
        check("Decoded file chunks acknowledge exact ranges and commit before the final receipt",
            savedPath != null && await File.ReadAllTextAsync(savedPath, ct) == "abc" && receipts.Count == 2 &&
            !receipts[0].Saved && WeTypeFileStream.IsRangeAck(receipts[0].Frame, digest, 0, 2) &&
            receipts[1].Saved && WeTypeFileStream.IsRangeAck(receipts[1].Frame, digest, 2, 3));
    }

    // Over the relay the phone sends blocks on more than one stream, so a later block can arrive first.
    private static async Task CheckOutOfOrderChunksAsync(Action<string, bool> check, string directory,
        IProgress<WeTypeSendProgress> progress, CancellationToken ct)
    {
        List<(long Start, long End)> ranges = [];
        long added = WeTypeFileInbox.AddRange(ranges, 200, 300) + WeTypeFileInbox.AddRange(ranges, 0, 100) +
            WeTypeFileInbox.AddRange(ranges, 50, 150) + WeTypeFileInbox.AddRange(ranges, 150, 200);
        check("WeType file ranges merge and count overlapping bytes once", added == 300 && ranges.Count == 1);

        byte[] content = RandomNumberGenerator.GetBytes(300);
        using var hasher = Blake3.Managed.Hasher.New();
        hasher.Update(content);
        string id = Convert.ToHexStringLower(hasher.Finalize().AsSpan());
        WeTypeWire Action(ulong kind, WeTypeWire file) => new WeTypeWire().Message(1, new WeTypeWire().Number(1, kind).Message(2, file));
        var commands = new List<WeTypeWire>
        {
            Action(1, new WeTypeWire().Text(1, id).Text(2, "out-of-order.bin").Number(3, 300)),
            Action(2, new WeTypeWire().Text(1, id)).Bytes(2, content[200..300]).Number(3, 200).Number(4, 300),
            Action(2, new WeTypeWire()).Bytes(2, content[0..100]).Number(3, 0).Number(4, 100),
            Action(2, new WeTypeWire().Text(1, id)).Bytes(2, content[100..200]).Number(3, 100).Number(4, 200),
        };
        async IAsyncEnumerable<ReadOnlyMemory<byte>> Input()
        {
            await Task.Yield();
            uint chunk = 1;
            foreach (var command in commands) yield return new WeTypeFileDataCommand(chunk++, command).Encode();
        }
        string? savedPath = null;
        int receipts = 0;
        await new WeTypeFileInbox(directory, progress, path => savedPath = path).ReceiveFileCommandsAsync(Input(), (_, _) =>
        {
            receipts++;
            return Task.CompletedTask;
        }, ct);
        check("WeType saves a file whose blocks arrive out of order",
            savedPath != null && (await File.ReadAllBytesAsync(savedPath, ct)).SequenceEqual(content) && receipts == 3);

        // The second file starts before the first one ends; a block without a file name is matched by its sequence.
        byte[] other = RandomNumberGenerator.GetBytes(250);
        using var otherHasher = Blake3.Managed.Hasher.New();
        otherHasher.Update(other);
        string otherId = Convert.ToHexStringLower(otherHasher.Finalize().AsSpan());
        commands =
        [
            Action(1, new WeTypeWire().Text(1, id).Text(2, "first.bin").Number(3, 300)).Number(6, 0),
            Action(2, new WeTypeWire().Text(1, id)).Bytes(2, content[0..200]).Number(3, 0).Number(4, 200).Number(6, 1),
            Action(1, new WeTypeWire().Text(1, otherId).Text(2, "second.bin").Number(3, 250)).Number(6, 2),
            Action(2, new WeTypeWire().Text(1, otherId)).Bytes(2, other[100..250]).Number(3, 100).Number(4, 250).Number(6, 3),
            Action(2, new WeTypeWire()).Bytes(2, content[200..300]).Number(3, 200).Number(4, 300).Number(6, 1),
            Action(2, new WeTypeWire().Text(1, otherId)).Bytes(2, other[0..100]).Number(3, 0).Number(4, 100).Number(6, 3),
        ];
        var savedPaths = new List<string>();
        await new WeTypeFileInbox(directory, progress, savedPaths.Add).ReceiveFileCommandsAsync(Input(), (_, _) => Task.CompletedTask, ct);
        check("WeType saves two files whose blocks interleave",
            savedPaths.Count == 2 && Path.GetFileName(savedPaths[0]) == "first.bin" && Path.GetFileName(savedPaths[1]) == "second.bin" &&
            (await File.ReadAllBytesAsync(savedPaths[0], ct)).SequenceEqual(content) &&
            (await File.ReadAllBytesAsync(savedPaths[1], ct)).SequenceEqual(other));

        // Several files in one start, one description each, then the data of each.
        commands =
        [
            new WeTypeWire().Message(1, new WeTypeWire().Number(1, 1)
                .Message(2, new WeTypeWire().Text(1, id).Text(2, "listed-a.bin").Number(3, 300))
                .Message(2, new WeTypeWire().Text(1, otherId).Text(2, "listed-b.bin").Number(3, 250))).Number(6, 0),
            Action(2, new WeTypeWire().Text(1, id)).Bytes(2, content).Number(3, 0).Number(4, 300).Number(6, 1),
            Action(2, new WeTypeWire().Text(1, otherId)).Bytes(2, other).Number(3, 0).Number(4, 250).Number(6, 1),
        ];
        savedPaths.Clear();
        var reports = new Reports();
        await new WeTypeFileInbox(directory, reports, savedPaths.Add).ReceiveFileCommandsAsync(Input(), (_, _) => Task.CompletedTask, ct);
        check("WeType saves every file one start describes",
            savedPaths.Select(Path.GetFileName).SequenceEqual(["listed-a.bin", "listed-b.bin"]) &&
            (await File.ReadAllBytesAsync(savedPaths[0], ct)).SequenceEqual(content) &&
            (await File.ReadAllBytesAsync(savedPaths[1], ct)).SequenceEqual(other));
        // The progress is for both files: the first file next, then the second after the first is saved.
        check("WeType reports the progress of every file in one transfer",
            reports.Values.SequenceEqual([new WeTypeSendProgress("listed-a.bin", 0, 550, 1, 2), new WeTypeSendProgress("listed-b.bin", 300, 550, 2, 2)]));

        // Files with the same content: the phone sends the content once and lists each name.
        commands =
        [
            Action(1, new WeTypeWire().Text(1, id).Text(2, "copy-a.bin").Text(2, "copy-b.bin").Number(3, 300)).Number(6, 0),
            Action(2, new WeTypeWire().Text(1, id)).Bytes(2, content).Number(3, 0).Number(4, 300).Number(6, 1),
        ];
        savedPaths.Clear();
        await new WeTypeFileInbox(directory, progress, savedPaths.Add).ReceiveFileCommandsAsync(Input(), (_, _) => Task.CompletedTask, ct);
        check("WeType saves one content under every name the phone lists",
            savedPaths.Select(Path.GetFileName).SequenceEqual(["copy-a.bin", "copy-b.bin"]) &&
            savedPaths.All(path => File.ReadAllBytes(path).SequenceEqual(content)) &&
            !Directory.EnumerateFiles(directory, ".brimdeck-*").Any());
    }

    private sealed class Reports : IProgress<WeTypeSendProgress>
    {
        public List<WeTypeSendProgress> Values { get; } = [];
        public void Report(WeTypeSendProgress value) => Values.Add(value);
    }

    private sealed class IncomingFrames : IAsyncStreamReader<WeTypeWire>, IServerStreamWriter<WeTypeWire>
    {
        public Queue<WeTypeWire> Frames { get; } = new();
        public bool AcknowledgedData { get; private set; }
        public WeTypeWire Current { get; private set; } = new();
        public WriteOptions? WriteOptions { get; set; }
        public Task<bool> MoveNext(CancellationToken ct)
        {
            if (!Frames.TryDequeue(out var frame)) return Task.FromResult(false);
            Current = frame;
            return Task.FromResult(true);
        }
        public Task WriteAsync(WeTypeWire message)
        {
            if (message.GetMessage(1).GetNumber(1) == 5) AcknowledgedData = true;
            return Task.CompletedTask;
        }
        public Task WriteAsync(WeTypeWire message, CancellationToken ct) => WriteAsync(message);
    }

    private static async Task CheckNativeReceiverEof(Action<string, bool> check)
    {
        var folder = Directory.CreateTempSubdirectory();
        var path = Path.Combine(folder.FullName, "payload.bin");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[2 * 1024 * 1024 + 1]);
            var peer = new Receiver();
            await WeTypeFileStream.SendOnStreamAsync(peer, peer, [path], peer, CancellationToken.None);
            check("WeType closes normally after receiver confirms all bytes and returns CANCELLED at EOF",
                peer.Closed && peer.Sent == new FileInfo(path).Length && peer.Actions.SequenceEqual([100UL, 1UL, 2UL, 2UL]));
            check("WeType uses one sequence for all chunks of a file", peer.DataSequences.SequenceEqual([1UL, 1UL]));
            var interrupted = new Receiver { CancelOnData = true };
            bool failed = false;
            try { await WeTypeFileStream.SendOnStreamAsync(interrupted, interrupted, [path], interrupted, CancellationToken.None); }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { failed = true; }
            check("WeType propagates receiver cancellation before the final range receipt",
                failed && !interrupted.Closed && interrupted.Sent == 0);
        }
        finally { File.Delete(path); folder.Delete(); }
    }

    private sealed class Receiver : IClientStreamWriter<WeTypeWire>, IAsyncStreamReader<WeTypeWire>, IProgress<WeTypeSendProgress>
    {
        public bool CancelOnData { get; init; }
        public bool Closed { get; private set; }
        public long Sent { get; private set; }
        public List<ulong> Actions { get; } = [];
        public List<ulong> DataSequences { get; } = [];
        public WeTypeWire Current { get; private set; } = new();
        public WriteOptions? WriteOptions { get; set; }
        public void Report(WeTypeSendProgress value) => Sent = value.Sent;
        public Task WriteAsync(WeTypeWire value)
        {
            var action = value.GetMessage(1);
            ulong kind = action.GetNumber(1);
            Actions.Add(kind);
            if (kind == 2)
            {
                DataSequences.Add(value.GetNumber(6));
                Current = new WeTypeWire().Message(1, new WeTypeWire().Number(1, 5).Message(2, action.GetMessage(2)))
                    .Number(3, value.GetNumber(3)).Number(4, value.GetNumber(4));
            }
            return Task.CompletedTask;
        }
        public Task WriteAsync(WeTypeWire value, CancellationToken ct) => WriteAsync(value);
        public Task CompleteAsync() { Closed = true; return Task.CompletedTask; }
        public Task<bool> MoveNext(CancellationToken ct) => Closed || CancelOnData
            ? Task.FromException<bool>(new RpcException(new Status(StatusCode.Cancelled, ""))) : Task.FromResult(true);
    }
}
