using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

// Which provider song a track takes (LyricsService.FitOf), and the lyrics KuGou keeps (KugouLocalLyrics).
internal static class LyricMatchTests
{
    public static async Task Run(Action<string, bool> check)
    {
        await Matching(check);
        await KugouLocal(check);
    }

    private static object Row(string title, string artist, double duration, string lyric) => new { trackName = title, artistName = artist, duration, syncedLyrics = lyric };

    // LRCLIB's search answers with the given songs; its exact lookup and the other providers with nothing.
    private static async Task<(Lyrics Lyrics, List<Uri> Requested)> Searched(MediaTrack track, params object[] rows)
    {
        using var handler = new Handler(uri => uri.Host == "lrclib.net" && uri.AbsolutePath == "/api/search"
            ? new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(rows)) }
            : uri.Host == "lrclib.net" ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var http = new HttpClient(handler); using var service = new LyricsService(http);
        return (await service.FindAsync(track, true, default), handler.Requested);
    }

    private static async Task Matching(Action<string, bool> check)
    {
        var track = new MediaTrack { Source = "Spotify", Title = "Song", Artist = "Artist", End = TimeSpan.FromSeconds(200) };
        string Current(Lyrics lyrics) => lyrics.At(1.5).Current;

        var original = await Searched(track, Row("Song (Live)", "Artist", 200, "[00:01]live"), Row("Song", "Artist", 201, "[00:01]original"));
        check("The song's own title is taken before another version of it", Current(original.Lyrics) == "original");
        var version = await Searched(track with { Title = "Song - Remastered 2011 (feat. Guest)" }, Row("Song (Live)", "Artist", 200, "[00:01]live"));
        check("A title differing only in a note at its end is the same song", Current(version.Lyrics) == "live");
        check("Another title, or a dash that is part of the name, does not match",
            (await Searched(track, Row("Song Two", "Artist", 200, "[00:01]x"))).Lyrics.Lines.Count == 0
            && (await Searched(track with { Title = "Back - Home" }, Row("Back", "Artist", 200, "[00:01]x"))).Lyrics.Lines.Count == 0);
        check("An accompaniment as long as the song is not taken for it",
            (await Searched(track, Row("Song (伴奏)", "Artist", 200, "[00:01]x"), Row("Song (Instrumental)", "Artist", 200, "[00:01]x"))).Lyrics.Lines.Count == 0
            && Current((await Searched(track with { Title = "Song (伴奏)" }, Row("Song (伴奏)", "Artist", 200, "[00:01]accompaniment"))).Lyrics) == "accompaniment");
        check("Another song of the same length by the same artist is not taken", (await Searched(track, Row("Other", "Artist", 200, "[00:01]x"))).Lyrics.Lines.Count == 0);
        check("Lengths more than two seconds apart are another recording", (await Searched(track, Row("Song", "Artist", 203, "[00:01]x"))).Lyrics.Lines.Count == 0);

        var chinese = new MediaTrack { Source = "Spotify", Title = "晴天", Artist = "周杰伦", End = TimeSpan.FromSeconds(269) };
        check("Traditional and simplified names are the same name", Current((await Searched(chinese, Row("晴天", "周杰倫", 270, "[00:01]故事的小黄花"))).Lyrics) == "故事的小黄花");
        var duet = track with { Artist = "Artist / Guest" };
        var shared = await Searched(duet, Row("Song", "Artist", 200, "[00:01]lead"), Row("Song", "Guest & Artist", 200, "[00:01]both"));
        check("One artist in common is enough, and the song with all of them is taken first", Current(shared.Lyrics) == "both"
            && Current((await Searched(duet, Row("Song", "Artist", 200, "[00:01]lead"))).Lyrics) == "lead"
            && (await Searched(duet, Row("Song", "Someone", 200, "[00:01]x"))).Lyrics.Lines.Count == 0);
        check("A shorter or longer way of writing an artist is the same artist",
            Current((await Searched(track with { Artist = "G.E.M. 邓紫棋" }, Row("Song", "邓紫棋", 200, "[00:01]gem"))).Lyrics) == "gem");

        var anonymous = track with { Artist = "" };
        var found = await Searched(anonymous, Row("Song", "Whoever", 200, "[00:01]anonymous"));
        check("A track without an artist is found by its title and length, by a search only", Current(found.Lyrics) == "anonymous"
            && found.Requested.All(uri => uri.AbsolutePath != "/api/get") && found.Requested.Any(uri => uri.AbsolutePath == "/api/search"));
        var unknown = await Searched(anonymous with { End = TimeSpan.Zero }, Row("Song", "Whoever", 200, "[00:01]x"));
        check("A track without an artist or a length is not looked up", unknown.Lyrics.Lines.Count == 0 && unknown.Requested.Count == 0);
    }

    private const string Hash = "0123456789abcdef0123456789abcdef";
    private const string Krc = "[ti:测试歌]\n[ar:歌手]\n[total:0]\n[offset:0]\n[1000,2000]<0,1000,0>第一<1000,1000,0>句\n[3500,1500]<0,1500,0>第二句\n[12000,1000]<0,1000,0>第三句";

    internal static byte[] KrcFile(string text)
    {
        byte[] key = [0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69];
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(Encoding.UTF8.GetBytes("﻿" + text));
        return [.. "krc1"u8, .. packed.ToArray().Select((b, i) => (byte)(b ^ key[i % key.Length]))];
    }

    private static async Task KugouLocal(Action<string, bool> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "BrimDeck.Tests.Kugou." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string online = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01.00]在线歌词"));
            using var handler = new Handler(uri => new(HttpStatusCode.OK)
            {
                Content = new StringContent(uri.Host switch
                {
                    "mobiles.kugou.com" => "{\"data\":{\"info\":[{\"hash\":\"" + Hash.ToUpperInvariant() + "\",\"songname\":\"测试歌\",\"singername\":\"歌手\",\"duration\":200}]}}",
                    "krcs.kugou.com" => "{\"candidates\":[{\"id\":\"1\",\"accesskey\":\"0123456789ABCDEF\",\"song\":\"测试歌\",\"singer\":\"歌手\",\"duration\":200000}]}",
                    "lyrics.kugou.com" => "{\"content\":\"" + online + "\"}",
                    _ => "{}"
                })
            });
            using var http = new HttpClient(handler);
            var hashed = new MediaTrack { Source = "酷狗音乐", Title = "名字写法不同", Artist = "别名", End = TimeSpan.FromSeconds(200), SongId = "kugou:" + Hash };
            var named = new MediaTrack { Source = "酷狗音乐", Title = "测试歌", Artist = "歌手", End = TimeSpan.FromSeconds(200) };

            check("A KRC file decodes, and anything else is not one", LyricsService.DecodeKrc(KrcFile(Krc)) == Krc
                && LyricsService.DecodeKrc("krc1broken"u8.ToArray()) is null && LyricsService.DecodeKrc([]) is null);
            var parsed = LyricsService.ParseKrc(Krc, 200);
            check("KRC lines become whole lines at their start, with a pause shown only when it is long",
                parsed.At(1.5).Current == "第一句" && parsed.At(3.2).Current == "第一句" && parsed.At(4).Current == "第二句" && parsed.At(6).Current == "♪" && parsed.At(12.5).Current == "第三句");

            using (var service = new LyricsService(http, kugouLyricFolder: () => folder))
            {
                var first = await service.FindAsync(hashed, true, default);
                var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                service.KugouLyricsWritten += () => written.TrySetResult();
                await File.WriteAllBytesAsync(Path.Combine(folder, $"歌手 - 测试歌-{Hash}-1-0000000A.krc"), KrcFile(Krc));
                bool raised = await Task.WhenAny(written.Task, Task.Delay(5000)) == written.Task;
                int requests = handler.Requested.Count;
                var local = await service.FindAsync(hashed, true, default);
                check("Without KuGou's file the lyrics come from KuGou online; the file is noticed and then taken instead",
                    first.Source == "酷狗音乐" && raised && local.Source == LyricsService.KugouLocalSource && local.At(1.5).Current == "第一句" && handler.Requested.Count == requests);
            }
            using (var service = new LyricsService(http, kugouLyricFolder: () => folder))
            {
                handler.Requested.Clear();
                check("A song without its hash takes the file of its title and artist, offline",
                    (await service.FindAsync(named, true, default)).Source == LyricsService.KugouLocalSource && handler.Requested.Count == 0);
                check("A song with another hash, or another version of the title, does not take the file",
                    (await service.FindAsync(hashed with { SongId = "kugou:" + new string('f', 32) }, true, default)).Source == "酷狗音乐"
                    && (await service.FindAsync(named with { Title = "测试歌 (Live)" }, true, default)).Source != LyricsService.KugouLocalSource);
            }
            await File.WriteAllBytesAsync(Path.Combine(folder, $"歌手 - 测试歌-{Hash}-1-0000000A.krc"), KrcFile(Krc.Replace("[total:0]", "[total:150000]")));
            using (var service = new LyricsService(http, kugouLyricFolder: () => folder))
                check("A file recording another length is not taken by name, but is by its hash",
                    (await service.FindAsync(named, true, default)).Source == "酷狗音乐" && (await service.FindAsync(hashed, true, default)).Source == LyricsService.KugouLocalSource);
            using (var service = new LyricsService(http))
                check("Without KuGou's folder no file is read", (await service.FindAsync(hashed, true, default)).Source == "酷狗音乐");
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Handler(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public readonly List<Uri> Requested = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requested) Requested.Add(request.RequestUri!);
            return Task.FromResult(reply(request.RequestUri!));
        }
    }
}
