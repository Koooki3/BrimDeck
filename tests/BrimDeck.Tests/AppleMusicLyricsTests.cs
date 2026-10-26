using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class AppleMusicLyricsTests
{
    private const string Ttml = "<tt xmlns='http://www.w3.org/ns/ttml'><body dur='4:37.500'><div><p begin='1.500' end='3.000'><span>前</span><span>一句</span></p><p begin='00:05.000' end='7s'>下一句</p></div></body></tt>";
    private static readonly MediaTrack Track = new() { Source = "Apple Music", Title = "唱月", Artist = "李耀陽", Album = "唱月 - Single", End = TimeSpan.FromSeconds(277.5) };

    public static async Task Run(string root, Action<string, bool> check)
    {
        var parsed = LyricsService.ParseAppleTtml(Ttml);
        check("Apple TTML preserves line timing, word spans and explicit gaps", parsed.Synced && parsed.At(2).Current == "前一句" && parsed.At(4).Current == "♪" && parsed.At(6).Current == "下一句");
        var plain = LyricsService.ParseAppleTtml("<tt xmlns='http://www.w3.org/ns/ttml'><body><div><p>第一行</p><p>第二行</p></div></body></tt>");
        check("Apple untimed lyrics remain plain text", !plain.Synced && plain.Lines.Count == 2);
        bool rejected = false;
        try { LyricsService.ParseAppleTtml("<!DOCTYPE tt [<!ENTITY x SYSTEM 'file:///missing'>]><tt>&x;</tt>"); }
        catch (System.Xml.XmlException) { rejected = true; }
        check("Apple TTML refuses external entities", rejected);

        // Apple Music's cache folders are hidden system folders.
        string folder = Path.Combine(root, "apple-cache"), hidden = Path.Combine(folder, "15ZD3MVJ"); Directory.CreateDirectory(hidden);
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
        string file = Path.Combine(hidden, "ttmlLyrics[1].json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { lyricsId = "AP_1592065224-zh-Hans", status = "success", ttml = Ttml }));
        await File.WriteAllTextAsync(Path.Combine(hidden, "1434016317[1].json"), """{"href":"/v1/catalog/cn/albums/1","next":"/v1/catalog/cn/albums/2","us":"/v1/catalog/us/songs/3"}""");

        using (var apple = new Apple())
        using (var service = new LyricsService(apple.Http, null, () => [folder]))
        {
            var result = await service.FindAsync(Track, true, default);
            check("Without authorization Apple Music's cache is found by catalog ID", result.Source == "Apple Music" && result.At(2).Current == "前一句" &&
                apple.Urls.All(u => u.Host.EndsWith("apple.com")));
            check("Without authorization the storefront is the one Apple Music's cache names most", apple.Requests.Any(r => r.Path == "/v1/catalog/cn/search"));
            check("The web player's token is taken from the site's script", apple.Urls.Any(u => u.AbsolutePath.StartsWith("/assets/index")));
        }
        using (var apple = new Apple { Title = "唱月 (DJ版)" })
        using (var service = new LyricsService(apple.Http, null, () => [folder]))
            check("Another recording of the same length does not use the cached lyrics", (await service.FindAsync(Track, true, default)).Lines.Count == 0);
        using (var apple = new Apple { HasLyrics = false })
        using (var service = new LyricsService(apple.Http, null, () => [folder]))
            check("A catalog song without lyrics is not looked for in the cache", (await service.FindAsync(Track, true, default)).Lines.Count == 0);

        // A reissued song keeps an older lyric ID, so the cache misses it; Apple's catalog has its lyrics.
        using (var apple = new Apple { SongId = "1751787927" })
        {
            var account = new Account("user-token");
            using var service = new LyricsService(apple.Http, account, () => [folder]);
            var result = await service.FindAsync(Track, true, default);
            check("With authorization lyrics come from Apple's catalog", result.Source == "Apple Music" && result.At(6).Current == "下一句" &&
                apple.Requests.Any(r => r.Path.EndsWith("/songs/1751787927/lyrics") && r.User == "user-token"));
            check("A valid authorization stays", account.Token == "user-token");
        }
        using (var apple = new Apple { SongId = "1751787927", RejectUser = true })
        {
            var account = new Account("old-token");
            using var service = new LyricsService(apple.Http, account, () => [folder]);
            var result = await service.FindAsync(Track, true, default);
            check("A rejected token expires the authorization", account.Expired == "old-token" && account.Token == "");
            check("An expired authorization falls back to the lookup without it", result.Lines.Count == 0 && apple.Requests.All(r => !r.Path.EndsWith("/lyrics")));
        }
        using (var apple = new Apple { SongId = "1751787927", LyricsMissing = true, RejectUser = true, AcceptUserOnce = true })
        {
            var account = new Account("stale-token");
            using var service = new LyricsService(apple.Http, account, () => [folder]);
            await service.FindAsync(Track, true, default);
            check("A lyric reply of 404 checks whether the token is still accepted", account.Expired == "stale-token");
        }
        using (var apple = new Apple { RejectWebOnce = true })
        using (var service = new LyricsService(apple.Http, null, () => [folder]))
        {
            var result = await service.FindAsync(Track, true, default);
            check("A web player token Apple stopped accepting is fetched again", result.Source == "Apple Music" &&
                apple.Urls.Count(u => u.AbsolutePath.StartsWith("/assets/index")) == 2);
        }

        string Lrc(bool synced) => JsonSerializer.Serialize(new { trackName = Track.Title, artistName = "李耀阳", duration = 277.5,
            syncedLyrics = synced ? "[00:01]LRCLIB同步" : "", plainLyrics = "LRCLIB纯文本" });
        string QqSearch = JsonSerializer.Serialize(new { code = 0, data = new { song = new { list = new[] { new {
            songmid = "000000000000AA", songname = Track.Title, singer = new[] { new { name = "李耀阳" } }, albumname = "唱月", interval = 278 } } } } });
        string QqRpc = JsonSerializer.Serialize(new { code = 0, lyric = new { code = 0, data = new { lyric = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]QQ同步")) } } });
        foreach (bool qqWorks in new[] { true, false })
        {
            using var apple = new Apple { Others = u => u.Host switch {
                "lrclib.net" => Lrc(false),
                "c.y.qq.com" => qqWorks ? QqSearch : "{}",
                "u.y.qq.com" => QqRpc,
                "music.163.com" when u.AbsolutePath.Contains("search") => JsonSerializer.Serialize(new { result = new { songs = new[] { new { id = 1, name = Track.Title, artists = new[] { new { name = "李耀阳" } }, duration = 277500 } } } }),
                "music.163.com" => JsonSerializer.Serialize(new { lrc = new { lyric = "[00:01]网易同步" } }),
                _ => "{}" } };
            using var service = new LyricsService(apple.Http, null, () => []);
            var result = await service.FindAsync(Track, true, default);
            check("Apple plain LRCLIB continues to " + (qqWorks ? "QQ before NetEase" : "NetEase after QQ fails"), result.Source == (qqWorks ? "QQ 音乐" : "网易云音乐") && result.Synced &&
                (!qqWorks || apple.Urls.All(u => u.Host != "music.163.com")));
        }
        using (var apple = new Apple { Others = u => u.Host == "lrclib.net" ? Lrc(true) : "{}" })
        using (var service = new LyricsService(apple.Http, null, () => []))
        {
            var result = await service.FindAsync(Track, true, default);
            check("Apple synced LRCLIB stops before QQ", result.Synced && result.Source == "LRCLIB" && apple.Urls.All(u => !u.Host.EndsWith("qq.com") && u.Host != "music.163.com"));
        }
        using (var apple = new Apple { Others = u => u.Host == "lrclib.net" ? Lrc(false) : "{}" })
        using (var service = new LyricsService(apple.Http, null, () => []))
            check("Apple keeps plain lyrics when every synced provider fails", (await service.FindAsync(Track, true, default)) is { Synced: false, Lines.Count: > 0 });
        foreach (string source in new[] { "QQ 音乐", "网易云音乐", "Google Chrome" })
        {
            using var apple = new Apple();
            using var service = new LyricsService(apple.Http, new Account("user-token"), () => throw new InvalidOperationException("Non-Apple source touched Apple cache"));
            await service.FindAsync(Track with { Source = source }, true, default);
            check("Only Apple desktop gets the new lookup chain: " + source,
                apple.Urls.All(u => !u.Host.EndsWith("apple.com")) &&
                (source == "QQ 音乐" ? apple.Urls.All(u => u.Host.EndsWith("qq.com")) : apple.Urls.All(u => !u.Host.EndsWith("qq.com"))));
        }

        // A third-party result found first does not hide Apple's lyrics once Apple Music caches them.
        string late = Path.Combine(root, "apple-late"); Directory.CreateDirectory(late);
        using (var apple = new Apple { Others = u => u.Host == "lrclib.net" ? Lrc(true) : "{}" })
        using (var service = new LyricsService(apple.Http, null, () => [late]))
        {
            check("Before Apple Music caches the lyrics a third-party result is used", (await service.FindAsync(Track, true, default)).Source == "LRCLIB");
            File.Copy(file, Path.Combine(late, "ttmlLyrics.json"));
            check("Lyrics Apple Music caches later replace the third-party result", (await service.FindAsync(Track, true, default)).Source == "Apple Music");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            bool cancelled = false;
            try { await service.FindAsync(Track, true, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            check("Cancelled Apple lookup does not deliver an old song", cancelled);
        }
    }

    private sealed class Account(string token) : IAppleMusicAccount
    {
        public string Expired { get; private set; } = "";
        public string Token => Expired == token ? "" : token;
        public void Expire(string value) => Expired = value;
    }

    // music.apple.com and its catalog, as far as the lookup uses them; other hosts answer through Others.
    private sealed class Apple : HttpMessageHandler
    {
        private static string Part(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static readonly string WebToken = Part(new { alg = "ES256", kid = "WebPlayKid" }) + "." + Part(new { iss = "AMPWebPlay", exp = DateTimeOffset.UtcNow.AddDays(70).ToUnixTimeSeconds() }) + ".signature";
        private static readonly string OtherToken = Part(new { alg = "ES256", kid = "OTHER" }) + "." + Part(new { exp = 1 }) + ".signature";
        public string Title { get; init; } = "唱月";
        public string SongId { get; init; } = "1592065224";
        public bool HasLyrics { get; init; } = true;
        public bool LyricsMissing { get; init; }
        public bool RejectUser { get; init; }
        public bool AcceptUserOnce { get; init; }
        public bool RejectWebOnce { get; init; }
        public Func<Uri, string> Others { get; init; } = _ => "{}";
        public List<Uri> Urls { get; } = [];
        public List<(string Path, string User)> Requests { get; } = [];
        public HttpClient Http { get; }
        private bool _webRejected, _userAccepted;
        public Apple() => Http = new HttpClient(this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!; Urls.Add(uri);
            string user = request.Headers.TryGetValues("media-user-token", out var values) ? values.Single() : "";
            HttpResponseMessage Reply(HttpStatusCode status, string body = "{}") => new(status) { Content = new StringContent(body) };
            HttpResponseMessage Answer()
            {
                if (uri.Host == "music.apple.com")
                    return uri.AbsolutePath == "/us/new" ? Reply(HttpStatusCode.OK, "<script src=\"/assets/index~1a2b.js\"></script>")
                        : Reply(HttpStatusCode.OK, $"const a=\"{OtherToken}\",b=\"{WebToken}\";");
                if (uri.Host != "amp-api.music.apple.com") return Reply(HttpStatusCode.OK, Others(uri));
                Requests.Add((uri.AbsolutePath, user));
                if (request.Headers.Authorization?.Parameter != WebToken) return Reply(HttpStatusCode.Unauthorized);
                if (RejectWebOnce && !_webRejected) { _webRejected = true; return Reply(HttpStatusCode.Unauthorized); }
                if (uri.AbsolutePath == "/v1/me/storefront")
                {
                    if (RejectUser && !(AcceptUserOnce && !_userAccepted)) return Reply(HttpStatusCode.Forbidden);
                    _userAccepted = true;
                    return Reply(HttpStatusCode.OK, """{"data":[{"id":"cn","type":"storefronts"}]}""");
                }
                if (uri.AbsolutePath.EndsWith("/search"))
                    return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { results = new { songs = new { data = new[] { new { id = SongId, type = "songs",
                        attributes = new { name = Title, artistName = "李耀陽", albumName = "唱月 - Single", durationInMillis = 277500, hasLyrics = HasLyrics } } } } } }));
                if (uri.AbsolutePath.EndsWith("/lyrics"))
                    return LyricsMissing || user.Length == 0 ? Reply(HttpStatusCode.NotFound)
                        : Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = new[] { new { id = SongId, type = "lyrics", attributes = new { ttml = Ttml } } } }));
                return Reply(HttpStatusCode.NotFound);
            }
            return Task.FromResult(Answer());
        }
    }
}
