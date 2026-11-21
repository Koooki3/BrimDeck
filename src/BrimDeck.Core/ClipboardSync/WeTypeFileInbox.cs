using System.Security.Cryptography;
using Blake3.Managed;
using Grpc.Core;

namespace BrimDeck.Core.ClipboardSync;

internal sealed class WeTypeFileInbox(Task<string> destination, IProgress<WeTypeSendProgress> progress, Action<string> saved,
    Action<string>? trace = null, Action? fileOffered = null)
{
    internal WeTypeFileInbox(string directory, IProgress<WeTypeSendProgress> progress, Action<string> saved)
        : this(Task.FromResult(directory), progress, saved) { }

    internal Task ReceiveAsync(IAsyncStreamReader<WeTypeWire> input, IAsyncStreamWriter<WeTypeWire> output,
        CancellationToken ct, bool helloSent = false) =>
        ReceiveChunksAsync(input.ReadAllAsync(ct), output.WriteAsync, requireHello: !helloSent, ct);

    // The session owns transport acknowledgements and passes complete, ordered application messages here.
    // pending: bytes that have arrived for the message still being assembled, so the progress moves before it is complete.
    internal Task ReceiveFileCommandsAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> commands,
        Func<WeTypeWire, CancellationToken, Task> sendReceipt, CancellationToken ct, Func<long>? pending = null)
    {
        async IAsyncEnumerable<WeTypeWire> ReadChunks()
        {
            await foreach (var bytes in commands.WithCancellation(ct))
                yield return WeTypeFileDataCommand.Decode(bytes.Span).FileChunk;
        }
        return ReceiveChunksAsync(ReadChunks(), sendReceipt, requireHello: false, ct, pending);
    }

    // One file being received. Over the relay the phone sends blocks on more than one stream, so blocks, and the
    // start of the next file, can arrive out of order: each block is written at its own offset, several files can be
    // open at once, and a file is checked once every byte is there.
    // Files with the same content share one id: the phone sends the content once and lists every name in the file
    // description, and each name is saved.
    private sealed class IncomingFile(string id, List<string> names, long size, ulong sequence, string temporary, FileStream stream)
    {
        public string Id { get; } = id;
        public List<string> Names { get; } = names;
        public string Name => Names[0];
        public long Size { get; } = size;
        public ulong Sequence { get; } = sequence;
        public string Temporary { get; } = temporary;
        public FileStream Stream { get; } = stream;
        public List<(long Start, long End)> Written { get; } = [];
        public long Received { get; set; }
        // Its place among the files announced in this transfer, from 1.
        public int Ordinal { get; init; }
    }

    private async Task ReceiveChunksAsync(IAsyncEnumerable<WeTypeWire> chunks,
        Func<WeTypeWire, CancellationToken, Task> sendReceipt, bool requireHello, CancellationToken ct, Func<long>? pending = null)
    {
        Dictionary<string, IncomingFile> open = [];
        string? directory = null;
        bool firstFrame = true;
        int controlsLogged = 0;
        static WeTypeWire Reply(ulong kind) => new WeTypeWire().Message(1, new WeTypeWire().Number(1, kind));

        // The progress is for the whole transfer: bytes of every file announced so far, how many are saved, and the next
        // file. It is read by the timer below as well, so it changes only under the lock.
        var gate = new object();
        int announced = 0, savedCount = 0;
        long totalBytes = 0, receivedBytes = 0, shownBytes = 0;
        string? nextName = null;

        void Report(long arriving)
        {
            WeTypeSendProgress value;
            lock (gate)
            {
                if (nextName is null) return;
                long sent = receivedBytes + arriving;
                // An estimate never reaches the end; the bar never moves back.
                if (arriving > 0) sent = Math.Min(sent, totalBytes - 1);
                shownBytes = sent = Math.Max(sent, Math.Min(shownBytes, totalBytes));
                value = new(nextName, sent, totalBytes, savedCount + 1, announced);
            }
            progress.Report(value);
        }

        void ShowNext()
        {
            string? name = open.Values.MinBy(f => f.Ordinal)?.Name;
            lock (gate) nextName = name;
            Report(0);
        }

        async Task DiscardAsync(IncomingFile file)
        {
            open.Remove(file.Id);
            await file.Stream.DisposeAsync();
            File.Delete(file.Temporary);
        }

        // Over the relay a whole file can arrive as one message; meanwhile the bytes already here move the progress.
        using var ticking = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task TickAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                while (await timer.WaitForNextTickAsync(ticking.Token))
                    if (pending!() is > 0 and var arriving) Report(arriving);
            }
            catch (OperationCanceledException) { }
        }
        Task ticker = pending is null ? Task.CompletedTask : TickAsync();

        // A data chunk names its file. When it does not, the only open file, or the one whose data sequence (its start
        // sequence + 1) matches, is used.
        IncomingFile? Find(WeTypeWire chunk, string fileId)
        {
            if (fileId.Length > 0) return open.GetValueOrDefault(fileId);
            if (open.Count == 1) return open.Values.First();
            if (!chunk.Has(6)) return null;
            ulong sequence = chunk.GetNumber(6);
            var matches = open.Values.Where(f => f.Sequence + 1 == sequence).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        try
        {
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                var action = chunk.GetMessage(1);
                var info = action.GetMessage(2);
                ulong kind = action.GetNumber(1);
                if (kind == 200)
                {
                    var control = chunk.GetMessage(5);
                    trace?.Invoke(controlsLogged++ < 40
                        ? $"Incoming file control: {control.GetNumber(2)} data={WeTypeWire.Describe(control.GetBytes(3))} chunk={WeTypeWire.Describe(chunk.Encode())}."
                        : $"Incoming file control: {control.GetNumber(2)}.");
                }
                else if (kind != 2) trace?.Invoke($"Incoming file action: {kind} {WeTypeWire.Describe(chunk.Encode())}.");
                if (firstFrame)
                {
                    firstFrame = false;
                    if (kind == 100) continue;
                    if (requireHello) throw new InvalidDataException("Missing file stream initialization.");
                }
                switch (kind)
                {
                    case 1:
                    {
                        // One start can describe several files, one description each (files with the same content share
                        // a description that lists every name).
                        var described = action.Messages(2).ToList();
                        if (described.Count is 0 or > 1000) throw new InvalidDataException("Invalid incoming file description.");
                        if (described.Count > 1) trace?.Invoke($"One start describes {described.Count} files.");
                        foreach (var file in described)
                        {
                            var names = file.Texts(2).ToList();
                            string id = file.GetText(1);
                            if (names.Count is 0 or > 1000 || !names.All(SafeName) || id.Length != 64 || !id.All(Uri.IsHexDigit) || !file.Has(3))
                                throw new InvalidDataException("Invalid incoming file description.");
                            if (names.Count > 1) trace?.Invoke($"One incoming content is saved under {names.Count} names.");
                            long size = checked((long)file.GetNumber(3));
                            if (open.TryGetValue(id, out var same))
                            {
                                // The same content described again: save it under these names too.
                                if (same.Size != size) throw new InvalidDataException("Overlapping files.");
                                same.Names.AddRange(names);
                                lock (gate) announced += names.Count;
                                continue;
                            }
                            fileOffered?.Invoke();
                            directory ??= await destination.WaitAsync(ct);
                            ct.ThrowIfCancellationRequested();
                            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException();
                            string staging = Path.Combine(directory, ".brimdeck-" + Guid.NewGuid().ToString("N") + ".part");
                            var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true);
                            lock (gate)
                            {
                                open[id] = new IncomingFile(id, names, size, chunk.GetNumber(6), staging, stream) { Ordinal = announced + 1 };
                                announced += names.Count;
                                totalBytes += size;
                            }
                            if (open.Count > 1) trace?.Invoke($"Receiving {open.Count} files at once.");
                        }
                        ShowNext();
                        break;
                    }
                    case 2:
                    {
                        byte[] data = chunk.GetBytes(2);
                        string dataId = info.GetText(1);
                        var file = Find(chunk, dataId);
                        ulong rangeStart = chunk.GetNumber(3), rangeEnd = chunk.GetNumber(4);
                        if (file is null || !chunk.Has(2) || !chunk.Has(3) || !chunk.Has(4) ||
                            data.Length > 2 * 1024 * 1024 || rangeEnd > (ulong)file.Size || rangeStart > rangeEnd ||
                            rangeEnd - rangeStart != (ulong)data.Length || (data.Length == 0 && file.Size != 0))
                        {
                            trace?.Invoke($"Invalid incoming range: openFiles={open.Count}, found={file != null}, idPresent={dataId.Length > 0}, " +
                                $"fields=[{string.Join(",", chunk.FieldNumbers())}], start={rangeStart}, end={rangeEnd}, " +
                                $"dataLen={data.Length}, size={file?.Size}.");
                            throw new InvalidDataException("Invalid file range.");
                        }
                        long start = (long)rangeStart, end = (long)rangeEnd;
                        await RandomAccess.WriteAsync(file.Stream.SafeFileHandle, data, start, ct);
                        long added = AddRange(file.Written, start, end);
                        file.Received += added;
                        lock (gate) receivedBytes += added;
                        bool complete = file.Received == file.Size;
                        if (complete)
                        {
                            file.Stream.Flush(true);
                            if (!CryptographicOperations.FixedTimeEquals(await DigestAsync(file.Stream, file.Size, ct), Convert.FromHexString(file.Id)))
                                throw new InvalidDataException("Incoming file digest mismatch.");
                            open.Remove(file.Id);
                            await file.Stream.DisposeAsync();
                            if (OperatingSystem.IsWindows())
                                File.WriteAllText(file.Temporary + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
                            string first = Commit(directory!, file.Temporary, file.Name);
                            saved(first);
                            foreach (string other in file.Names.Skip(1))
                                saved(CommitCopy(directory!, first, other));
                            lock (gate) savedCount += file.Names.Count;
                        }
                        var receipt = new WeTypeWire().Number(1, 5).Message(2, new WeTypeWire().Text(1, file.Id));
                        await sendReceipt(new WeTypeWire().Message(1, receipt).Number(3, rangeStart).Number(4, rangeEnd), ct);
                        if (complete) ShowNext(); else Report(0);
                        break;
                    }
                    case 100:
                    case 5:
                        break;
                    case 200:
                        if (chunk.Has(8))
                            await sendReceipt(Reply(5).Bytes(8, chunk.GetBytes(8)), ct);
                        if (chunk.Has(5) && chunk.GetMessage(5).GetNumber(2) == 1001)
                        {
                            if (open.Count > 0) throw new EndOfStreamException();
                            return;
                        }
                        break;
                    case 3:
                    {
                        // The sender dropped one file; the others continue.
                        string canceledId = info.GetText(1);
                        if (canceledId.Length == 0 || !open.TryGetValue(canceledId, out var canceled))
                            throw new IOException("Sender canceled the file.");
                        trace?.Invoke("Sender canceled one incoming file.");
                        lock (gate)
                        {
                            announced -= canceled.Names.Count;
                            totalBytes -= canceled.Size;
                            receivedBytes -= canceled.Received;
                        }
                        await DiscardAsync(canceled);
                        ShowNext();
                        break;
                    }
                    default: throw new InvalidDataException("Unsupported file action.");
                }
            }
            if (firstFrame || open.Count > 0) throw new EndOfStreamException();
        }
        finally
        {
            ticking.Cancel();
            await ticker;
            foreach (var file in open.Values.ToArray()) await DiscardAsync(file);
        }
    }

    // Adds [start, end) to the sorted, non-overlapping ranges and returns how many bytes were new.
    internal static long AddRange(List<(long Start, long End)> ranges, long start, long end)
    {
        if (end <= start) return 0;
        long before = ranges.Sum(r => r.End - r.Start);
        int i = 0;
        while (i < ranges.Count && ranges[i].End < start) i++;
        while (i < ranges.Count && ranges[i].Start <= end)
        {
            start = Math.Min(start, ranges[i].Start);
            end = Math.Max(end, ranges[i].End);
            ranges.RemoveAt(i);
        }
        ranges.Insert(i, (start, end));
        return ranges.Sum(r => r.End - r.Start) - before;
    }

    private static async Task<byte[]> DigestAsync(FileStream file, long size, CancellationToken ct)
    {
        using var hash = Hasher.New();
        byte[] buffer = new byte[1024 * 1024];
        for (long at = 0; at < size;)
        {
            int read = await RandomAccess.ReadAsync(file.SafeFileHandle, buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - at)), at, ct);
            if (read == 0) throw new EndOfStreamException();
            hash.Update(buffer.AsSpan(0, read));
            at += read;
        }
        return hash.Finalize().AsSpan().ToArray();
    }

    internal static bool SafeName(string name)
    {
        if (name.Length is 0 or > 255 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))) return false;
        string stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") &&
            !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "0123456789¹²³".Contains(stem[3]));
    }

    // Another name for content already saved. The copy keeps the downloaded-file mark.
    private static string CommitCopy(string directory, string savedPath, string name)
    {
        string staging = Path.Combine(directory, ".brimdeck-" + Guid.NewGuid().ToString("N") + ".part");
        File.Copy(savedPath, staging);
        try
        {
            if (OperatingSystem.IsWindows())
                File.WriteAllText(staging + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            return Commit(directory, staging, name);
        }
        catch
        {
            File.Delete(staging);
            throw;
        }
    }

    private static string Commit(string directory, string source, string name)
    {
        for (int n = 0; ; n++)
        {
            string candidate = n == 0 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
            string path = Path.Combine(directory, candidate);
            try { File.Move(source, path, overwrite: false); return path; }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
    }
}
