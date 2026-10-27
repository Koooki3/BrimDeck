using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class QqLyricsTests
{
    private const string Mid = "000000000000AA";
    private const string Lrc = "[00:02.00]前一句\n[00:08.00]当前 &amp; 歌词\n[00:15.00]后一句\n[02:15.75]最后一句";
    private static string Rpc(string text = Lrc) => JsonSerializer.Serialize(new { code = 0, lyric = new { code = 0, data = new { lyric = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) } } });
    private static object Song(string mid = Mid, string title = "Song", string artist = "Guest", string album = "Album", int duration = 140)
        => new { songmid = mid, songname = title, singer = new[] { new { name = "Artist" }, new { name = artist } }, albumname = album, interval = duration };
    private static string Search(params object[] songs) => JsonSerializer.Serialize(new { code = 0, data = new { song = new { list = songs } } });
    private static string PreviewRpc(string mid = Mid, string text = Lrc) => JsonSerializer.Serialize(new
    {
        code = 0,
        lyric = new { code = 0, data = new { lyric = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) } },
        info = new { code = 0, data = new { track_info = new { mid, id = 1, interval = 140,
            file = new { b_30s = 15000, e_30s = 45000, try_begin = 40000, try_end = 100000 } } } }
    });

    public static async Task Run(Action<string, bool> check)
    {
        var track = new MediaTrack { Source = "QQ 音乐", Title = "Song", Artist = "Artist/Guest", Album = "Album", End = TimeSpan.FromSeconds(140) };
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.Contains("search_for_qq_cp")
            ? Search(Song("wrongTitle01", title: "Song (Live)"), Song("wrongSinger1", artist: "Someone Else"),
                Song("wrongAlbum01", album: "Live Album"), Song("wrongLength1", duration: 180), Song()) : Rpc());
        using var http = new HttpClient(handler); using var service = new LyricsService(http);
        check("QQ metadata without duration waits before matching recordings", (await service.FindAsync(track with { End = TimeSpan.Zero }, true, default)).Lines.Count == 0 && handler.Urls.Count == 0);
        var lyrics = await service.FindAsync(track with { EmbeddedLyrics = "[00:01]其他来源", CurrentLyric = "管道单句" }, true, default);
        check("QQ search selects the matching title, artists, album and duration", handler.Urls.Count == 2 && handler.Parameters.Single().GetProperty("songMID").GetString() == Mid);
        check("QQ full LRC decodes entities and supplies both neighboring lines", lyrics.Source == "QQ 音乐" && lyrics.Lines.Count == 4 && lyrics.At(10) == ("前一句", "当前 & 歌词", "后一句") && lyrics.Lines[^1].Seconds == 135.75);
        check("QQ requests identify the app and use the QQ referer without credentials", handler.HeadersCorrect);
        await service.FindAsync(track with { CurrentLyric = "新管道单句", ReportedPosition = TimeSpan.FromSeconds(20) }, true, default);
        check("QQ line and position changes reuse the complete lyric cache", handler.Urls.Count == 2);

        var previewTrack = track with { End = TimeSpan.FromSeconds(60), State = MediaState.Paused,
            ReportedPosition = TimeSpan.FromSeconds(10), CurrentLyric = "当前 & 歌词", CurrentLyricStart = TimeSpan.FromSeconds(7.96) };
        using (var preview = new Handler(request => request.RequestUri!.Host == "c.y.qq.com"
            ? Search(Song("shortWrong01", album: "", duration: 59), Song()) : PreviewRpc()))
        using (var client = new HttpClient(preview))
        using (var lookup = new LyricsService(client))
        {
            var result = await lookup.FindAsync(previewTrack, true, default);
            check("QQ VIP preview matches the full recording instead of a same-title short version", result.QqPreview && result.Lines.Count == 4 &&
                preview.Parameters.Single().GetProperty("songMID").GetString() == Mid);
            check("QQ preview retains lyrics beyond its playable minute", result.Lines[^1].Seconds == 135.75);
            check("QQ preview uses the player's actual line without adding a suggested clip offset", result.At(previewTrack, DateTimeOffset.UtcNow) == ("前一句", "当前 & 歌词", "后一句"));
        }
        foreach (string response in new[] { Rpc(), PreviewRpc(mid: "wrongSongId1") })
        {
            using var invalid = new Handler(request => request.RequestUri!.Host == "c.y.qq.com" ? Search(Song()) : response);
            using var client = new HttpClient(invalid); using var lookup = new LyricsService(client);
            check("Mismatched duration needs matching QQ preview metadata", (await lookup.FindAsync(previewTrack, true, default)).Lines.Count == 0 && invalid.Urls.Count == 2);
        }
        foreach (var sample in new[]
        {
            (Track: previewTrack with { Album = "" }, Songs: Search(Song())),
            (Track: previewTrack, Songs: Search(Song(), Song("otherVersion1", duration: 180)))
        })
        {
            using var ambiguous = new Handler(_ => sample.Songs);
            using var client = new HttpClient(ambiguous); using var lookup = new LyricsService(client);
            check("QQ preview refuses incomplete or ambiguous recording identity", (await lookup.FindAsync(sample.Track, true, default)).Lines.Count == 0 && ambiguous.Urls.Count == 1);
        }
        using (var directPreview = new Handler(_ => PreviewRpc()))
        using (var client = new HttpClient(directPreview))
        using (var lookup = new LyricsService(client))
            check("Known QQ preview identities retain clip synchronization",
                (await lookup.FindAsync(previewTrack with { SongId = "qqid:1" }, true, default)).QqPreview && directPreview.Urls.Count == 1);
        using (var legacyPreview = new Handler(request => request.RequestUri!.AbsolutePath.Contains("search_for_qq_cp") ? Search(Song()) :
            request.RequestUri.Host == "u.y.qq.com" ? PreviewRpc(text: "") : JsonSerializer.Serialize(new { code = 0, lyric = Lrc })))
        using (var client = new HttpClient(legacyPreview))
        using (var lookup = new LyricsService(client))
        {
            var result = await lookup.FindAsync(previewTrack, true, default);
            check("QQ legacy lyrics preserve a verified preview identity", result.QqPreview && result.Lines.Count == 4 && legacyPreview.Urls.Count == 3);
        }
        var repeated = Lyrics.Parse("[00:08]第一段之前\n[00:15]重复句\n[00:22]第一段之后\n[01:38]第二段之前\n[01:45]重复句\n[01:52]第二段之后", null, 140, "QQ 音乐") with { QqPreview = true };
        var chorus = previewTrack with { CurrentLyric = "重复句", CurrentLyricStart = TimeSpan.FromSeconds(104.95) };
        check("QQ full-song cue disambiguates repeated choruses within a short preview", repeated.At(chorus, DateTimeOffset.UtcNow) == ("第二段之前", "重复句", "第二段之后"));
        check("QQ paused preview does not drift as wall time advances", repeated.At(chorus, DateTimeOffset.UtcNow.AddMinutes(5)) == ("第二段之前", "重复句", "第二段之后"));
        check("QQ backward seek follows the new native lyric cue", repeated.At(chorus with { CurrentLyricStart = TimeSpan.FromSeconds(15) }, DateTimeOffset.UtcNow) == ("第一段之前", "重复句", "第一段之后"));
        check("Ambiguous preview lyrics never guess a chorus from the local clip clock", repeated.At(chorus with { CurrentLyricStart = null }, DateTimeOffset.UtcNow) == ("", "重复句", ""));
        check("QQ preview gaps and disconnects clear old neighboring lines", repeated.At(chorus with { CurrentLyric = "", CurrentLyricStart = null }, DateTimeOffset.UtcNow) == ("", "♪", "") &&
            repeated.At(chorus with { CurrentLyric = null, CurrentLyricStart = null }, DateTimeOffset.UtcNow) == ("", "", ""));

        foreach (string identity in new[] { "qq:" + Mid, "qqid:1" })
        {
            using var direct = new Handler(_ => Rpc()); using var client = new HttpClient(direct); using var lookup = new LyricsService(client);
            var result = await lookup.FindAsync(track with { Source = "后台播放器", SongId = identity }, true, default);
            check("Known QQ identities bypass search: " + identity, result.Lines.Count == 4 && direct.Urls.Count == 1 && direct.Urls[0].Host == "u.y.qq.com" &&
                (identity.StartsWith("qqid:") ? direct.Parameters[0].GetProperty("songID").GetInt64() == 1 : direct.Parameters[0].GetProperty("songMID").GetString() == Mid));
        }
        foreach (string response in new[] { "{\"code\":0,\"lyric\":{\"code\":0,\"data\":{\"lyric\":\"not-base64\"}}}", "{\"code\":1000,\"lyric\":{\"code\":0}}" })
        {
            using var legacy = new Handler(request => request.RequestUri!.Host == "u.y.qq.com" ? response : JsonSerializer.Serialize(new { code = 0, lyric = Lrc }));
            using var client = new HttpClient(legacy); using var lookup = new LyricsService(client);
            var result = await lookup.FindAsync(track with { SongId = "qq:" + Mid }, true, default);
            check("Invalid QQ RPC results fall back only to QQ's legacy lyric endpoint", result.At(10).Next == "后一句" && legacy.Urls.Count == 2 && legacy.Urls[1].AbsolutePath.Contains("fcg_query_lyric_new"));
        }
        using (var none = new Handler(_ => "{\"code\":0,\"data\":{\"song\":{\"list\":[]}}}"))
        using (var client = new HttpClient(none))
        using (var lookup = new LyricsService(client))
        {
            var result = await lookup.FindAsync(track, true, default);
            check("No QQ match never searches LRCLIB or Netease", result.Lines.Count == 0 && none.Urls.Count == 1 && none.Urls[0].Host == "c.y.qq.com");
        }
        using (var offline = new Handler(_ => "{}", HttpStatusCode.ServiceUnavailable))
        using (var client = new HttpClient(offline))
        using (var lookup = new LyricsService(client))
        {
            var result = await lookup.FindAsync(track with { SongId = "qq:" + Mid }, true, default);
            check("Failed QQ endpoints never fall through to another lyrics provider", result.Lines.Count == 0 && offline.Urls.Count == 4 && offline.Urls.All(u => u.Host is "u.y.qq.com" or "c.y.qq.com"));
        }
        using (var empty = new Handler(_ => "{\"code\":0,\"lyric\":{\"code\":0,\"data\":{\"lyric\":\"\"}}}"))
        using (var client = new HttpClient(empty))
        using (var lookup = new LyricsService(client))
        {
            await lookup.FindAsync(track with { SongId = "qq:" + Mid }, true, default);
            int first = empty.Urls.Count;
            await lookup.FindAsync(track with { SongId = "qq:" + Mid }, true, default);
            check("Empty QQ results do not poison the lyrics cache", first == 2 && empty.Urls.Count == first * 2);
            int before = empty.Urls.Count;
            var invalid = await lookup.FindAsync(track with { SongId = "qq:bad&mid" }, true, default);
            check("Invalid QQ identities are rejected without cross-provider fallback", invalid.Lines.Count == 0 && empty.Urls.Count == before);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, string> response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Urls { get; } = [];
        public List<JsonElement> Parameters { get; } = [];
        public bool HeadersCorrect { get; private set; } = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!; Urls.Add(uri);
            if (uri.Host is not ("u.y.qq.com" or "c.y.qq.com")) throw new InvalidOperationException("QQ requested another provider: " + uri.Host);
            HeadersCorrect &= request.Headers.Referrer?.AbsoluteUri == "https://y.qq.com/" && request.Headers.UserAgent.ToString().Contains("BrimDeck") && request.Headers.Authorization is null && !request.Headers.Contains("Cookie");
            if (uri.Host == "u.y.qq.com")
            {
                using var json = JsonDocument.Parse(Uri.UnescapeDataString(uri.Query[6..]));
                Parameters.Add(json.RootElement.GetProperty("lyric").GetProperty("param").Clone());
            }
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(response(request), Encoding.UTF8, "application/json") });
        }
    }
}
