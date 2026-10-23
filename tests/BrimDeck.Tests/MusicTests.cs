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
        PageLayout(check);
        CompactLayout(check);
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
        async Task<(Lyrics Lyrics, List<Uri> Requested)> Served(Func<Uri, HttpResponseMessage> reply)
        {
            using var served = new ReplyHandler(reply); using var servedHttp = new HttpClient(served); using var servedService = new LyricsService(servedHttp);
            return (await servedService.FindAsync(track, true, default), served.Requested);
        }
        const string LrclibReply = "{\"trackName\":\"测试曲目\",\"artistName\":\"测试歌手\",\"duration\":200,\"syncedLyrics\":\"[00:01]歌词\"}";
        HttpResponseMessage Moved(string location) { var moved = new HttpResponseMessage(HttpStatusCode.Found); moved.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute); return moved; }
        var sameHost = await Served(uri => uri.AbsolutePath == "/moved" ? new(HttpStatusCode.OK) { Content = new StringContent(LrclibReply) } : Moved("/moved"));
        check("A lyrics redirect to HTTPS on the same host is followed", sameHost.Lyrics.Lines.Count == 1 && sameHost.Requested[1].ToString() == "https://lrclib.net/moved");
        var elsewhere = await Served(uri => uri.Host == "lrclib.net" ? Moved("https://lyrics.example/get") : new(HttpStatusCode.OK) { Content = new StringContent(LrclibReply) });
        check("A lyrics redirect to another host or to HTTP is not followed",
            elsewhere.Lyrics.Lines.Count == 0 && elsewhere.Requested.All(uri => uri.Host != "lyrics.example")
            && (await Served(uri => uri.Scheme == "https" ? Moved("http://lrclib.net/get") : new(HttpStatusCode.OK) { Content = new StringContent(LrclibReply) })).Requested.All(uri => uri.Scheme == "https"));
        var loop = await Served(uri => Moved("/again"));
        check("Lyrics redirects stop after a few hops", loop.Lyrics.Lines.Count == 0 && loop.Requested.Count(uri => uri.Host == "lrclib.net") <= 3 * 2);
        string padded = LrclibReply.Insert(1, "\"pad\":\"" + new string('x', LyricsService.MaxResponseBytes) + "\",");
        var large = await Served(_ => new(HttpStatusCode.OK) { Content = new StringContent(padded) });
        check("A lyrics reply over the size limit is given up", large.Lyrics.Lines.Count == 0);
        var undeclared = await Served(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(padded))) });
        check("A lyrics reply without a declared length is cut off at the limit", undeclared.Lyrics.Lines.Count == 0);
        var bom = await Served(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(LrclibReply)]) });
        check("A lyrics reply under the limit is still read, byte order mark included", bom.Lyrics.Lines.Count == 1);
        await QqLyricsTests.Run(check);
    }
    private sealed class ReplyHandler(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public readonly List<Uri> Requested = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requested) Requested.Add(request.RequestUri!);
            return Task.FromResult(reply(request.RequestUri!));
        }
    }
    private static void PageLayout(Action<string, bool> check)
    {
        MusicLayout At(PanelSize size) { var (w, h) = MusicSizes.For(size); return MusicLayout.Calculate(w, h, true, true); }
        var compact = At(PanelSize.Compact); var standard = At(PanelSize.Standard); var spacious = At(PanelSize.Spacious);
        check("The presets are densities 0, 1 and 2, the largest panel 3", compact.Density == 0 && standard.Density == 1 && spacious.Density == 2
            && MusicLayout.Calculate(1200, 400, true, true).Density == 3);
        check("The compact preset puts the artist on the title line to keep a lyric line", compact.MergeTitle && compact.LyricLines == 1);
        check("The standard preset gives the artist its own line above one lyric line", !standard.MergeTitle && standard.LyricLines == 1);
        check("The spacious preset shows the lines before and after the current lyric", !spacious.MergeTitle && spacious.LyricLines == 3);
        check("Without lyrics the artist keeps its own line at the compact preset", !MusicLayout.Calculate(440, 140, false, true).MergeTitle);
        check("A tall, narrow panel stays one step above the narrow preset", MusicLayout.DensityFor(440, 400) == 1);
        check("Without a timeline the cover takes the progress row's room",
            MusicLayout.Calculate(440, 200, true, false).Cover > standard.Cover && MusicLayout.Calculate(440, 200, true, false).ProgressHeight == 0);
        check("The standard music preset is 440 wide, as wide as the compact one", MusicSizes.For(PanelSize.Standard) == (440, 200)
            && new DeckSettings().MusicWidth == 440 && new DeckSettings().MusicHeight == 200);
        check("The bottom margin below the transport equals the side margin", standard.Metrics.Bottom == standard.Metrics.Side && spacious.Metrics.Bottom == spacious.Metrics.Side);
        var failures = new List<string>(); double title = 0;
        for (double h = 140; h <= 400; h += 2)
        {
            var growing = MusicLayout.Calculate(1200, h, true, true).Metrics;
            if (growing.Title < title) failures.Add($"title shrinks at {h}");
            title = growing.Title;
            for (double w = 440; w <= 1200; w += 20)
                foreach (bool timeline in new[] { true, false })
                foreach (bool lyrics in new[] { true, false })
                {
                    var l = MusicLayout.Calculate(w, h, lyrics, timeline, logMode: true); var c = l.Metrics;
                    string at = $"{w}x{h} timeline={timeline} lyrics={lyrics}";
                    double barTop = l.ProgressY + (c.SeekRow - c.Bar) / 2, inkTop = l.ControlsY + c.Play / 8;
                    double upperBottom = (timeline ? barTop : inkTop) - c.SectionGap;
                    if (l.Cover < 40 || l.CoverY < 0 || l.CoverY + l.Cover > upperBottom + .5) failures.Add("cover " + at);
                    if (l.TextX + l.TextWidth != l.BodyWidth || l.TextWidth < c.MinColumn) failures.Add("text column " + at);
                    if (l.HeaderHeight + (l.LyricLines > 0 ? c.MiddleGap + l.LyricLines * c.LyricLine : 0) > l.Cover) failures.Add("text height " + at);
                    if (lyrics && l.LyricLines == 0) failures.Add("no lyric line " + at);
                    if (Math.Abs(l.ControlsY + c.Play * 7 / 8 - l.BodyHeight) > .5) failures.Add("transport bottom " + at);
                    if (timeline && (l.TimesY < l.ProgressY + (c.SeekRow + c.BarHover) / 2 || l.TimesY + c.TimeLine + c.ControlsGap > inkTop + .5)) failures.Add("times " + at);
                    if (l.ModeX < 0 || l.ModeX + c.Mode + 16 > l.TripletX || l.TripletX + l.TripletWidth > l.BodyWidth || Math.Abs(l.TripletX + l.TripletWidth / 2 - l.BodyWidth / 2) > .5) failures.Add("transport " + at);
                    if (!l.ShowFullControl) failures.Add("full control entry " + at);
                }
        }
        check("The music page fits every size from 440x140 to 1200x400" + (failures.Count > 0 ? ": " + string.Join("; ", failures.Take(5)) : ""), failures.Count == 0);
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
    // The collapsed row and the quota notice: see CompactRowTests.
    private static void CompactLayout(Action<string, bool> check) => CompactRowTests.Run(check);
}
