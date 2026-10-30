using System.Net;
using System.Text;
using BrimDeck.Core;

internal static class KugouTests
{
    internal static async Task Run(Action<string, bool> check)
    {
        Patch(check);
        await LyricsLookup(check);
        check("KuGou's session is shown under its name and found by its process",
            MediaAppTarget.DisplayName("kugou", _ => null) == "酷狗音乐" && new MediaTrack { Source = "酷狗音乐" }.IsKugou && new MediaTrack { SongId = "kugou:" + new string('a', 32) }.IsKugou
            && MediaAppTarget.Plays(new MediaAppOwner(null, MediaAppTarget.ProcessName("kugou"), "kugou"), new MediaAppProcess(1, "KuGou", null, "kugou")));
    }

    // The nine places, as KugouPatch writes them, in a file as long as KuGou 20.1's libcef.dll.
    private static readonly (long Offset, string Original, string Patched)[] Places =
    [
        (0x58C63EB, "C706AAAAAAAA", "C706C92F0000"), (0x58C6415, "E896A9E9FC", "9090909090"), (0x58C6420, "0F8796010000", "909090909090"),
        (0x58C6428, "0F848E010000", "909090909090"), (0x4BC180E, "8B957C010000", "BAC92F000090"), (0x4BEDE41, "0F8470010000", "909090909090"),
        (0x4BEDE4C, "41C707AAAAAAAA", "41C707C92F0000"), (0x4BEDEB5, "E8F62EB7FD", "9090909090"), (0x4BEDED0, "0F44DA", "909090"),
    ];
    private static void Patch(Action<string, bool> check)
    {
        SparseStream Library(bool patched)
        {
            var file = new SparseStream(145_902_560);
            foreach (var (offset, original, written) in Places) { file.Position = offset; file.Write(Convert.FromHexString(patched ? written : original)); }
            file.Position = 0x100; file.Write([1, 2, 3]);
            file.Writes = 0; return file;
        }
        var library = Library(false);
        check("An unchanged KuGou libcef.dll is recognized", KugouPatch.Classify(library) == KugouPatchState.Original);
        KugouPatch.Write(library, true);
        check("Patching changes the nine places and nothing else", KugouPatch.Classify(library) == KugouPatchState.Patched && library.Writes == Places.Length
            && library.Equivalent(Library(true)) && library.ReadAt(0x100, 3).SequenceEqual(new byte[] { 1, 2, 3 }));
        KugouPatch.Write(library, true);
        check("Patching a patched file writes nothing", library.Writes == Places.Length);
        KugouPatch.Write(library, false);
        check("Restoring writes the original bytes back", KugouPatch.Classify(library) == KugouPatchState.Original && library.Equivalent(Library(false)));

        var partial = Library(false);
        foreach (var (offset, _, written) in Places.Take(4)) { partial.Position = offset; partial.Write(Convert.FromHexString(written)); }
        partial.Writes = 0;
        check("A file another tool patched in part is recognized", KugouPatch.Classify(partial) == KugouPatchState.Partial);
        KugouPatch.Write(partial, true);
        check("Patching a part-patched file writes only the places still original", KugouPatch.Classify(partial) == KugouPatchState.Patched
            && partial.Writes == Places.Length - 4 && partial.Equivalent(Library(true)));

        var mixed = Library(false); mixed.Position = Places[3].Offset; mixed.Write([0x90]); mixed.Writes = 0;
        bool refused = false;
        try { KugouPatch.Write(mixed, true); } catch (InvalidDataException) { refused = true; }
        check("A file another build or tool changed is left alone", KugouPatch.Classify(mixed) == KugouPatchState.Unsupported && refused && mixed.Writes == 0);
        check("A file too short for the places is not a known build", KugouPatch.Classify(new SparseStream(0x4BC1000)) == KugouPatchState.Unsupported);
    }

    private static async Task LyricsLookup(Action<string, bool> check)
    {
        const string Hash = "d62a437719aba2b0da0a5d2ad5e14e51";
        string lyric = Convert.ToBase64String(Encoding.UTF8.GetBytes("[id:$00000000]\n[00:01.47]我们之间的回忆\n[00:03.60]全部都小心地收集"));
        var requested = new List<Uri>();
        HttpResponseMessage Reply(Uri uri)
        {
            lock (requested) requested.Add(uri);
            string body = uri.Host switch
            {
                "krcs.kugou.com" => "{\"status\":200,\"candidates\":[{\"id\":\"1\",\"accesskey\":\"0123456789ABCDEF0123456789ABCDEF\",\"song\":\"同名歌\",\"singer\":\"别人\",\"duration\":181000},"
                    + "{\"id\":\"511461352\",\"accesskey\":\"68AE8550EA30C9BDA452384B1C5B8E0C\",\"song\":\"把回忆拼好给你\",\"singer\":\"张妙格\",\"duration\":181000}]}",
                "lyrics.kugou.com" => uri.Query.Contains("id=511461352") ? "{\"status\":200,\"content\":\"" + lyric + "\"}" : "{\"status\":200,\"content\":\"\"}",
                _ => "{}"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
        var song = new MediaTrack { Title = "把回忆拼好给你", Artist = "张妙格", End = TimeSpan.FromSeconds(181), Source = "酷狗音乐", SongId = "kugou:" + Hash };
        using (var http = new HttpClient(new Handler(Reply))) using (var service = new LyricsService(http))
        {
            var lyrics = await service.FindAsync(song, true, default);
            check("A song KuGou plays is looked up by its hash on KuGou first", lyrics.Source == "酷狗音乐" && lyrics.At(2).Current == "我们之间的回忆"
                && requested[0].Host == "krcs.kugou.com" && requested[0].Query.Contains("hash=" + Hash) && requested[0].Query.Contains("duration=181000")
                && requested.All(u => u.Host.EndsWith(".kugou.com", StringComparison.Ordinal) && u.Scheme == "https"));
        }
        requested.Clear();
        using (var http = new HttpClient(new Handler(Reply))) using (var service = new LyricsService(http))
        {
            var lyrics = await service.FindAsync(song with { Source = "Spotify", SongId = "" }, true, default);
            int kugou = requested.FindIndex(u => u.Host == "krcs.kugou.com");
            check("Another player's song asks KuGou last, by name, and takes only the candidate with the same title and artist",
                lyrics.Source == "酷狗音乐" && kugou > 0 && requested.Take(kugou).All(u => u.Host != "lyrics.kugou.com")
                && requested[kugou].Query.Contains("keyword=" + Uri.EscapeDataString("张妙格 - 把回忆拼好给你"))
                && requested.Where(u => u.Host == "lyrics.kugou.com").All(u => u.Query.Contains("id=511461352")));
        }
    }

    private sealed class Handler(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request.RequestUri!));
    }

    // A file of the given length holding only what was written to it, so a 140 MB library costs a few bytes.
    private sealed class SparseStream(long length) : Stream
    {
        private readonly Dictionary<long, byte> _bytes = [];
        public int Writes;
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = (int)Math.Max(0, Math.Min(count, length - Position));
            for (int i = 0; i < read; i++) buffer[offset + i] = _bytes.GetValueOrDefault(Position + i);
            Position += read; return read;
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) _bytes[Position + i] = buffer[offset + i];
            Position += count; Writes++;
        }
        public byte[] ReadAt(long offset, int count) { Position = offset; var bytes = new byte[count]; ReadExactly(bytes); return bytes; }
        public bool Equivalent(SparseStream other) => _bytes.Keys.Union(other._bytes.Keys).All(k => _bytes.GetValueOrDefault(k) == other._bytes.GetValueOrDefault(k));
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
