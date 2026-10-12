using System.Net;
using BrimDeck.Core;

internal static class MusicTests
{
    public static async Task Run(Action<string, bool> check)
    {
        await MediaRoutingTests.Run(check);
        MediaSeekPreviewTests.Run(check);
        MediaPlaybackStackTests.Run(check);
        var modes = new MediaTrack { CanRepeat = true, CanShuffle = true, Repeat = MediaRepeat.List };
        check("Modes cycle list, single, shuffle, order, list", MusicPlaybackModes.Next(modes) == MusicPlaybackMode.Track
            && MusicPlaybackModes.Next(modes with { Repeat = MediaRepeat.Track }) == MusicPlaybackMode.Shuffle
            && MusicPlaybackModes.Next(modes with { Shuffle = true }) == MusicPlaybackMode.Order
            && MusicPlaybackModes.Next(modes with { Repeat = MediaRepeat.None }) == MusicPlaybackMode.List);
        check("Repeat-only sources omit shuffle", MusicPlaybackModes.Supported(modes with { CanShuffle = false }).SequenceEqual([MusicPlaybackMode.List, MusicPlaybackMode.Track, MusicPlaybackMode.Order]));
        check("Sources without modes omit the mode control", MusicPlaybackModes.Supported(new MediaTrack()).Count == 0);
        var narrow = CompactMusicLayout.Calculate(115, true, 4, true);
        check("A narrow compact row keeps one rotating ring beside a scrolling lyric", narrow.VisibleRings == 1 && narrow.ScrollRings && narrow.RingSize == 10 && narrow.TextWidth == 43);
        var capsule = CompactMusicLayout.Calculate(91.3, true, 4, true);
        check("The capsule beside the widest clock still shows a ring and the lyric", capsule.VisibleRings == 1 && capsule.TextWidth > 19);
        var tight = CompactMusicLayout.Calculate(84, true, 4, true);
        check("Rings give way to the lyric once it would fall below its minimum", tight.VisibleRings == 0 && tight.TextWidth == 30);
        check("Without the clock the notch shows four rings and the capsule three beside the lyric", CompactMusicLayout.Calculate(178, true, 4, true).VisibleRings == 4
            && CompactMusicLayout.Calculate(164, true, 4, true).VisibleRings == 3);
        var now = DateTimeOffset.UtcNow;
        var track = new MediaTrack { Title = "测试曲目", Artist = "测试歌手", Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(210), ReportedPosition = TimeSpan.FromSeconds(20), PositionAt = now, State = MediaState.Playing, Rate = 2 };
        check("Playback position applies rate and timeline origin", track.Position(now.AddSeconds(3)).TotalSeconds == 16);
        check("Paused position does not advance", (track with { State = MediaState.Paused }).Position(now.AddSeconds(30)).TotalSeconds == 10);
        check("Position never passes the media duration", track.Position(now.AddDays(1)) == track.Duration);
        check("Known duration without playback position is not a timeline", !(track with { PositionKnown = false }).HasTimeline && (track with { PositionKnown = false }).Position(now.AddMinutes(2)) == TimeSpan.Zero);
        check("Seeking respects advertised bounds and tick units", (track with { MinSeek = TimeSpan.FromSeconds(30), MaxSeek = TimeSpan.FromSeconds(150) }).SeekTicks(1) == TimeSpan.FromSeconds(150).Ticks);
        var lrc = Lyrics.Parse("[offset:-500]\n[00:02.00][00:06.25]同一行\n[00:20]后一行", null, 210, "test");
        check("LRC multiple timestamps and offsets are parsed", lrc.Synced && lrc.Lines.Count == 3 && lrc.Lines[0].Seconds == 1.5 && lrc.Lines[1].Seconds == 5.75);
        check("Lyrics show an instrumental introduction before the first timestamp", lrc.At(0).Current == "♪");
        check("Long lyric lines stay visible until the next timestamp", lrc.At(10.75).Current == "同一行" && lrc.At(12).Current == "同一行" && lrc.At(19.499).Current == "同一行" && lrc.At(19.5).Current == "后一行");
        var gap = Lyrics.Parse("[00:02]长句\n[00:14]\n[00:20]下一句", null, 40, "网易云音乐");
        check("Only timestamped blank lines start an instrumental gap", gap.At(13.999).Current == "长句" && gap.At(14).Current == "♪" && gap.At(19.999).Current == "♪" && gap.At(20).Current == "下一句");
        check("Seeking backwards from an explicit gap restores the lyric", gap.At(16).Current == "♪" && gap.At(8).Current == "长句");
        check("Timestamped lyrics require a real playback position to synchronize", lrc.CanSynchronize(track) && !lrc.CanSynchronize(track with { PositionKnown = false }) && !lrc.CanSynchronize(track with { End = TimeSpan.Zero }));
        var plain = Lyrics.Parse(null, "甲\n乙", 100, "test");
        check("Plain lyrics retain text without fabricating timestamps", !plain.Synced && plain.Lines.Select(l => l.Text).SequenceEqual(["甲", "乙"]) && plain.Lines.All(l => l.Seconds == 0));
        check("Plain lyrics never select a timed line", !plain.CanSynchronize(track) && plain.At(0).Current == "" && plain.At(60).Current == "");
        using var handler = new LyricsHandler(); using var http = new HttpClient(handler); using var service = new LyricsService(http);
        check("Lyrics disabled sends no request", (await service.FindAsync(track, false, default)).Lines.Count == 0 && handler.Calls == 0);
        var result = await service.FindAsync(track, true, default);
        check("Lyrics provider uses identified requests without credentials", result.Lines.Count == 1 && handler.Calls == 1 && handler.Identified && !handler.Authorization);
        await service.FindAsync(track, true, default);
        check("Repeated tracks are served from the lyrics cache", handler.Calls == 1);
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); bool cancelled = false;
        try { await service.FindAsync(track with { Title = "另一首" }, true, canceled.Token); } catch (OperationCanceledException) { cancelled = true; }
        check("Cancelled lyrics queries do not issue requests", cancelled && handler.Calls == 1);
        var nativeLyrics = await service.FindAsync(track with { EmbeddedLyrics = "[00:02]播放器提供的完整歌词" }, true, default);
        check("Native full lyrics bypass online lookup", nativeLyrics.At(2).Current == "播放器提供的完整歌词" && handler.Calls == 1);
        using var versioned = new NeteaseLyricHandler(); using var versionedHttp = new HttpClient(versioned); using var versionedService = new LyricsService(versionedHttp);
        var firstVersion = await versionedService.FindAsync(track with { Source = "NetEase", SongId = "netease:2692035029" }, true, default);
        check("A NetEase lyric still at its first version is requested in full", firstVersion.At(1.5).Current == "first" && versioned.Requested.All(u => !u.Contains("lv=1")));
        await QqLyricsTests.Run(check);
    }
    // NetEase's lyric endpoint answers lv=<held version> with an empty lyric unless its own version is newer.
    private sealed class NeteaseLyricHandler : HttpMessageHandler
    {
        public readonly List<string> Requested = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString(); Requested.Add(url);
            if (!url.StartsWith("https://music.163.com/api/song/lyric?", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var held = System.Text.RegularExpressions.Regex.Match(url, @"[?&]lv=(-?\d+)");
            string lyric = held.Success && int.Parse(held.Groups[1].Value) >= 1 ? "" : "[00:01]first";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"lrc\":{\"version\":1,\"lyric\":\"" + lyric + "\"},\"code\":200}") });
        }
    }
    private sealed class LyricsHandler : HttpMessageHandler
    {
        public int Calls; public bool Identified, Authorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Identified = request.Headers.UserAgent.ToString().Contains("BrimDeck"); Authorization |= request.Headers.Authorization is not null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"trackName\":\"测试曲目\",\"artistName\":\"测试歌手\",\"duration\":200,\"syncedLyrics\":\"[00:01]歌词\"}") });
        }
    }
}
