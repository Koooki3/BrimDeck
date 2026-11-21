using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class NeteaseSongDetailsTests
{
    internal static async Task Run(Action<string, bool> check)
    {
        Parse(check);
        Fill(check);
        await Lookup(check);
    }

    private static JsonElement Json(string value) { using var doc = System.Text.Json.JsonDocument.Parse(value); return doc.RootElement.Clone(); }

    private const string Detail = """
        {"id":26598946,"name":"山丘","duration":405800,
         "artists":[{"name":"李宗盛"},{"name":"某艺人"}],
         "album":{"name":"山丘","picUrl":"https://p1.music.126.net/cover.jpg"}}
        """;

    private static void Parse(Action<string, bool> check)
    {
        var details = NeteaseSongDetails.Parse(Json(Detail), "26598946");
        check("song/detail parses artwork, full artist list, album and duration in seconds",
            details is not null && details.Id == "26598946" && details.Name == "山丘" && details.Artist == "李宗盛 / 某艺人"
            && details.Album == "山丘" && details.CoverUrl == "https://p1.music.126.net/cover.jpg" && Math.Abs(details.DurationSeconds - 405.8) < .001);
        check("A detail for another id is rejected so it cannot relabel the playing song",
            NeteaseSongDetails.Parse(Json(Detail), "999") is null);
        var noCover = NeteaseSongDetails.Parse(Json("""{"id":1,"name":"a","artists":[],"album":{"name":"b","picUrl":""}}"""), "1");
        check("A song without artwork keeps a null cover instead of an empty url",
            noCover is not null && noCover.CoverUrl is null && noCover.Artist == "");
        check("Malformed details never throw",
            NeteaseSongDetails.Parse(Json("""{"id":"x"}"""), "1") is null
            && NeteaseSongDetails.Parse(Json("{}"), "1") is null);
    }

    private static void Fill(Action<string, bool> check)
    {
        var playing = new MediaTrack { Title = "山丘", Artist = "", Album = "", SongId = "netease:26598946", State = MediaState.Playing,
            ReportedPosition = TimeSpan.FromSeconds(12), PositionAt = DateTimeOffset.UtcNow, CanSeek = true };
        var details = NeteaseSongDetails.Parse(Json(Detail), "26598946")!;
        var filled = MediaRouting.FillDetails(playing, details);
        check("song/detail fills an empty artist and album but never the playing title, time, state or controls",
            filled.Title == "山丘" && filled.Artist == "李宗盛 / 某艺人" && filled.Album == "山丘"
            && filled.ReportedPosition == playing.ReportedPosition && filled.PositionAt == playing.PositionAt
            && filled.State == MediaState.Playing && filled.CanSeek && filled.SongId == playing.SongId);
        var kept = new MediaTrack { Title = "山丘", Artist = "李宗盛", Album = "原专辑", SongId = "netease:26598946" };
        var keptFilled = MediaRouting.FillDetails(kept, details);
        check("An artist or album already reported by the player is not overwritten",
            keptFilled.Artist == "李宗盛" && keptFilled.Album == "原专辑");
        check("Details of a different song, or no details, leave the track untouched",
            MediaRouting.FillDetails(playing, null) == playing
            && MediaRouting.FillDetails(playing with { SongId = "netease:999" }, details).Artist == "");
        check("Non-Netease tracks are never filled",
            MediaRouting.FillDetails(playing with { SongId = "qq:1", Artist = "" }, details).Artist == "");
        check("A track whose blank artist and album were filled is still the same song, so its lyrics are kept",
            MediaRouting.OnlyDetailsFilled(playing, filled) && MediaRouting.OnlyDetailsFilled(playing, playing));
        check("A changed title, song id, length, or an artist replaced rather than filled is another lookup",
            !MediaRouting.OnlyDetailsFilled(playing, filled with { Title = "别的歌" })
            && !MediaRouting.OnlyDetailsFilled(playing, filled with { SongId = "netease:1" })
            && !MediaRouting.OnlyDetailsFilled(playing, filled with { End = TimeSpan.FromSeconds(200) })
            && !MediaRouting.OnlyDetailsFilled(kept, kept with { Artist = "别人" })
            && !MediaRouting.OnlyDetailsFilled(playing with { SongId = "qq:1" }, filled with { SongId = "qq:1" }));
    }

    private sealed class Web(string body, HttpStatusCode status = HttpStatusCode.OK, bool fail = false) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (fail) throw new HttpRequestException("offline");
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
            return Task.FromResult(response);
        }
    }

    private static async Task Lookup(Action<string, bool> check)
    {
        var reply = "{\"songs\":[" + Detail + "],\"code\":200}";
        var web = new Web(reply);
        using var service = new NeteaseSongDetailService(new HttpClient(web));
        var first = await service.GetAsync("26598946");
        var second = await service.GetAsync("26598946");
        check("A lookup parses the detail, caches it by id and serves the next call from the cache",
            first is not null && first.CoverUrl == "https://p1.music.126.net/cover.jpg" && web.Calls == 1 && ReferenceEquals(first, second));
        check("Known exposes only what has already been fetched, synchronously",
            service.Known("26598946") is not null && service.Known("999") is null);

        var failing = new Web("", fail: true);
        using var offline = new NeteaseSongDetailService(new HttpClient(failing));
        check("A failed request degrades silently to no details and is not cached as known",
            await offline.GetAsync("1") is null && offline.Known("1") is null);
        check("A failure cools down so repeated refresh ticks do not reissue the request",
            await offline.GetAsync("1") is null && await offline.GetAsync("1") is null && failing.Calls == 1);

        var empty = new Web("{\"songs\":[],\"code\":200}");
        using var missing = new NeteaseSongDetailService(new HttpClient(empty));
        check("A reply without the requested song yields no details", await missing.GetAsync("1") is null);
        check("An empty reply cools down too, without caching a miss as known",
            await missing.GetAsync("1") is null && missing.Known("1") is null && empty.Calls == 1);
    }
}
