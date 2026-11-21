using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

public sealed partial class LyricsService
{
    private static readonly Regex KugouHash = new(@"\A[0-9a-fA-F]{32}\z");

    // The lyrics KuGou keeps for the recording of a song KuGou itself plays, found by its hash.
    private async Task<Lyrics> FindKugou(MediaTrack track, CancellationToken token)
    {
        string hash = track.SongId.StartsWith("kugou:", StringComparison.Ordinal) ? track.SongId[6..] : "";
        return KugouHash.IsMatch(hash) ? await FindKugouHash(hash, track, token) : Lyrics.Empty;
    }

    // Any song: KuGou's song search lists recordings with their hashes, and those with the same title, artists and length are
    // looked up by hash, the best fit first.
    private async Task<Lyrics> FindKugouSearch(MediaTrack track, CancellationToken token)
    {
        var search = await Get("https://mobiles.kugou.com/api/v3/search/song?format=json&page=1&pagesize=10&showtype=1&keyword=" +
            Uri.EscapeDataString(SearchWords(track)), token);
        if (search is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("info", out var songs) || songs.ValueKind != JsonValueKind.Array) return Lyrics.Empty;
        var hashes = Fitting(track, songs.EnumerateArray().Where(song => song.ValueKind == JsonValueKind.Object && KugouHash.IsMatch(Str(song, "hash"))),
            song => (Str(song, "songname"), Str(song, "singername"), Num(song, "duration"))).Select(song => Str(song, "hash").ToLowerInvariant()).Distinct();
        foreach (string hash in hashes)
        {
            var lyrics = await FindKugouHash(hash, track, token);
            if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics;
        }
        return Lyrics.Empty;
    }

    // KuGou's lyric search answers a recording's hash with candidates and the key to download each.
    private async Task<Lyrics> FindKugouHash(string hash, MediaTrack track, CancellationToken token)
    {
        string duration = track.Duration > TimeSpan.Zero ? ((long)track.Duration.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        var search = await Get($"https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=&duration={duration}&hash={hash}", token);
        if (search is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array) return Lyrics.Empty;
        foreach (var candidate in candidates.EnumerateArray().Where(candidate => track.Duration.TotalSeconds <= 0 || Math.Abs(Num(candidate, "duration") / 1000 - track.Duration.TotalSeconds) <= 2))
        {
            string id = Str(candidate, "id"), key = Str(candidate, "accesskey");
            if (!Regex.IsMatch(id, @"\A[0-9]{1,16}\z") || !Regex.IsMatch(key, @"\A[0-9A-Fa-f]{16,64}\z")) continue;
            var lyrics = await DownloadKugou(id, key, track.Duration.TotalSeconds, token);
            if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics;
        }
        return Lyrics.Empty;
    }

    private async Task<Lyrics> DownloadKugou(string id, string key, double duration, CancellationToken token)
    {
        var reply = await Get($"https://lyrics.kugou.com/download?ver=1&client=pc&id={id}&accesskey={key}&fmt=lrc&charset=utf8", token);
        if (reply is not { ValueKind: JsonValueKind.Object } value || Str(value, "content") is not { Length: > 0 } content) return Lyrics.Empty;
        try { return Lyrics.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(content)), null, duration, "酷狗音乐"); }
        catch (FormatException) { return Lyrics.Empty; }
    }
}
