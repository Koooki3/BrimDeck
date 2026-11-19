using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

public sealed record LyricLine(double Seconds, string Text);
// The lines shown around the one being sung: Before ends with the line just sung, After starts with the next one.
public sealed record LyricWindow(IReadOnlyList<string> Before, string Current, IReadOnlyList<string> After)
{
    public static LyricWindow Empty { get; } = new([], "", []);
    public static LyricWindow Only(string current) => new([], current, []);
}
public sealed record Lyrics(IReadOnlyList<LyricLine> Lines, bool Synced, string Source, bool Instrumental = false)
{
    public static Lyrics Empty { get; } = new([], true, "");
    // Apple Music's lines are shown this many seconds before they are sung, so that a line can be read as it starts.
    public const double AppleLead = 0.2;
    // A pause between lines shorter than this many seconds keeps the line before it shown; a longer one is shown as "♪".
    public const double Gap = 5;
    public bool QqPreview { get; init; }
    public bool CanSynchronize(MediaTrack track) => Synced && track.HasTimeline;
    public (string Previous, string Current, string Next) At(MediaTrack track, DateTimeOffset now, LyricHold? hold = null) => Nearest(Around(track, now, 1, 1, hold));
    public (string Previous, string Current, string Next) At(double seconds, LyricHold? hold = null) => Nearest(Around(seconds, 1, 1, hold));
    private static (string Previous, string Current, string Next) Nearest(LyricWindow window)
        => (window.Before.Count > 0 ? window.Before[^1] : "", window.Current, window.After.Count > 0 ? window.After[0] : "");
    // The current line with up to before lines above it and after lines below it, in the order they are sung.
    // hold: keeps the line on show through a small step back of the position (LyricHold).
    public LyricWindow Around(MediaTrack track, DateTimeOffset now, int before, int after, LyricHold? hold = null)
    {
        if (!CanSynchronize(track)) return LyricWindow.Empty;
        if (!QqPreview || !track.IsQqMusic) return Around(track.Position(now).TotalSeconds + (track.IsAppleMusic ? AppleLead : 0), before, after, hold);
        // A preview can start at zero or inside the song. QQ's actual current line and
        // full-song karaoke timestamp locate it without guessing from the clip metadata.
        string text = track.CurrentLyric ?? "";
        string key = NormalizeLine(text);
        if (key.Length == 0) return LyricWindow.Only(text.Length > 0 ? text : track.CurrentLyric is null ? "" : "♪");
        var matches = Lines.Select((line, index) => (Line: line, Index: index))
            .Where(row => NormalizeLine(row.Line.Text) == key).ToArray();
        if (matches.Length == 1) return Around(matches[0].Line.Seconds, before, after);
        if (matches.Length > 1 && track.CurrentLyricStart is { } start)
        {
            var nearest = matches.OrderBy(row => Math.Abs(row.Line.Seconds - start.TotalSeconds)).First();
            if (Math.Abs(nearest.Line.Seconds - start.TotalSeconds) <= 2) return Around(nearest.Line.Seconds, before, after);
        }
        // An unmatched line or ambiguous repeated chorus still has QQ's single-line fallback.
        return LyricWindow.Only(text);
    }
    private static string NormalizeLine(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    public LyricWindow Around(double seconds, int before, int after, LyricHold? hold = null)
    {
        before = Math.Max(0, before); after = Math.Max(0, after);
        if (Lines.Count == 0) return LyricWindow.Only(Instrumental ? "♪" : "");
        if (!Synced) return LyricWindow.Empty;
        int index = -1;
        for (int i = 0; i < Lines.Count && Lines[i].Seconds <= seconds; i++) index = i;
        if (hold is not null) index = hold.Index(this, index, seconds);
        string[] Texts(int from, int count) => [.. Lines.Skip(from).Take(count).Select(line => line.Text)];
        if (index < 0) return new([], "♪", Texts(0, after));
        // LRC timestamps start a line; they do not specify how long it is sung.
        // Only an explicit blank line marks a gap before the next timestamp (Parse keeps only those of Gap or longer).
        string text = string.IsNullOrWhiteSpace(Lines[index].Text) ? "♪" : Lines[index].Text;
        int first = Math.Max(0, index - before);
        return new(Texts(first, index - first), text, Texts(index + 1, after));
    }
    public static Lyrics Parse(string? lrc, string? plain, double duration, string source, bool instrumental = false)
    {
        var result = new List<LyricLine>();
        double offset = 0;
        var offsetMatch = Regex.Match(lrc ?? "", @"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase);
        if (offsetMatch.Success && double.TryParse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var ms)) offset = ms / 1000;
        foreach (var row in (lrc ?? "").Split('\n'))
        {
            var stamps = Regex.Matches(row, @"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]");
            if (stamps.Count == 0) continue;
            // Word-timed LRC puts a <mm:ss.xx> tag before each word; the panel shows whole lines, so the tags are dropped.
            string text = Regex.Replace(row[(stamps[^1].Index + stamps[^1].Length)..], @"<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>", "").Trim();
            foreach (Match stamp in stamps)
            {
                double time = int.Parse(stamp.Groups[1].Value) * 60 + int.Parse(stamp.Groups[2].Value);
                if (stamp.Groups[3].Success) time += double.Parse("0." + stamp.Groups[3].Value, CultureInfo.InvariantCulture);
                result.Add(new(Math.Max(0, time + offset), text));
            }
        }
        if (result.Count > 0)
            return InstrumentalNotice(result.Select(l => l.Text)) ? new([], true, source, true) : new(WithoutShortGaps([.. result.OrderBy(l => l.Seconds).Distinct()]), true, source, instrumental);
        var lines = (plain ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) return new([], true, source, instrumental);
        if (InstrumentalNotice(lines)) return new([], true, source, true);
        // Plain lyrics have no timing information; retain their order for manual scrolling.
        return new(lines.Select(line => new LyricLine(0, line)).ToArray(), false, source, instrumental);
    }
    // Some LRC files put a blank line wherever a line ends before the next one starts, often less than a second ahead of it.
    // A blank line the next line follows within Gap seconds is dropped, so the line before it stays shown through the pause.
    private static List<LyricLine> WithoutShortGaps(List<LyricLine> lines)
    {
        var kept = new List<LyricLine>(lines.Count);
        double? next = null;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            bool blank = string.IsNullOrWhiteSpace(lines[i].Text);
            if (blank && next is { } start && start - lines[i].Seconds < Gap) continue;
            kept.Add(lines[i]);
            if (!blank) next = lines[i].Seconds;
        }
        kept.Reverse();
        return kept;
    }
    // QQ Music, NetEase and KuGou give a song without words a lyric of only credits ("作曲 : Yiruma") and a notice such as
    // "纯音乐，请欣赏" or "此歌曲为没有填词的纯音乐，请您欣赏"; such a lyric is the song's mark as instrumental. A notice worded
    // otherwise is not recognised, and its lines are shown as a lyric.
    private static bool InstrumentalNotice(IEnumerable<string> lines)
    {
        var texts = lines.Select(text => text.Trim()).Where(text => text.Length > 0).ToArray();
        static bool Notice(string text) => text.Contains("纯音乐", StringComparison.Ordinal) && text.Contains("欣赏", StringComparison.Ordinal);
        return texts.Any(Notice) && texts.All(text => Notice(text) || Regex.IsMatch(text, @"\A[^:：]{1,16}[:：]"));
    }
}

// The line on show does not go back for a small step back of the position. A player's new report often puts the position a
// little behind the clock BrimDeck ran on from the last one (Apple Music's whole seconds, see MediaRouting.FromWholeSeconds); just
// after a new line had started, the line before it came back for a moment and the new one followed again, and a lyric area that
// follows the line's length (DeckSettings.MusicLyricAutoWidth) shrank back and grew again with them. A step back of StepBack
// seconds or more before the line's start is a seek, and shows the earlier line. New lyrics (another instance) start afresh.
public sealed class LyricHold
{
    public const double StepBack = 1.5;
    private Lyrics? _lyrics;
    private int _index = -1;

    // index: the line the position falls in (-1 before the first); the line to show.
    public int Index(Lyrics lyrics, int index, double seconds)
    {
        if (ReferenceEquals(lyrics, _lyrics) && index < _index && _index < lyrics.Lines.Count && seconds > lyrics.Lines[_index].Seconds - StepBack)
            return _index;
        _lyrics = lyrics; _index = index;
        return index;
    }
}

public sealed partial class LyricsService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Dictionary<string, Lyrics> _cache = new();
    private readonly LinkedList<string> _order = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    // A lyric reply is a few kilobytes; a larger one is given up rather than read into memory.
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 2;
    // appleLyricsFile keeps which Apple Music lyrics each song was matched to; none keeps it only in memory.
    // kugouLyricFolder: KuGou's lyric folder (KugouLyricFolder); none reads no lyrics KuGou keeps.
    public LyricsService(HttpClient? http = null, Func<IEnumerable<string>>? appleCacheRoots = null, string? appleLyricsFile = null,
        Func<string>? queMusicSettings = null, Func<string?>? kugouLyricFolder = null)
    {
        _kugouLyricFolder = kugouLyricFolder ?? (() => null);
        _appleCacheRoots = appleCacheRoots ?? AppleCacheRoots;
        _queMusicSettings = queMusicSettings ?? QueMusicSettingsFile;
        _appleLyricsFile = appleLyricsFile; _appleLyricsIds = LoadAppleLyricsIds(appleLyricsFile);
        _ownsHttp = http is null;
        // Redirects are followed by Get, only to HTTPS on the same host.
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    }
    public async Task<Lyrics> FindAsync(MediaTrack track, bool enabled, CancellationToken token)
    {
        // A track without an artist is told by its title and length (FitOf), so one without a length either is not looked up.
        if (!enabled || string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist) && track.Duration <= TimeSpan.Zero) return Lyrics.Empty;
        if (!track.IsQqMusic && !string.IsNullOrWhiteSpace(track.EmbeddedLyrics))
            return Lyrics.Parse(track.EmbeddedLyrics, null, track.Duration.TotalSeconds, track.Source);
        if (track.IsQueMusic)
        {
            var found = await FindQueMusic(track, token);
            if (found.Lyrics is { } local) return local;
            if (found.SongId.Length > 0) track = track with { SongId = found.SongId };
        }
        await _gate.WaitAsync(token);
        try
        {
            string cacheKey = string.Join("\n", track.Source, track.SongId, track.SongKey);
            // A third-party result for Apple Music is kept only until Apple Music caches its own lyrics, and one for KuGou until
            // KuGou keeps its own; those are looked for first.
            bool hit = _cache.TryGetValue(cacheKey, out var cached);
            var lyrics = track.IsKugou && (!hit || cached!.Source != KugouLocalSource) ? FindKugouLocal(track) : Lyrics.Empty;
            if (lyrics.Lines.Count > 0 || lyrics.Instrumental) { Remember(cacheKey, lyrics); return lyrics; }
            if (hit && (!track.IsAppleMusic || cached!.Source == AppleSource))
            { _order.Remove(cacheKey); _order.AddLast(cacheKey); return cached!; }
            // QQ tracks must stay within QQ, including when lookup fails or metadata is incomplete.
            if (track.IsQqMusic) lyrics = await FindQq(track, token);
            else if (track.IsAppleMusic)
            {
                lyrics = await FindApple(track, token);
                if (!lyrics.Synced || lyrics.Lines.Count == 0) lyrics = hit ? cached! : await FindAppleFallback(track, lyrics, token);
            }
            else
            {
                if (track.SongId.StartsWith("netease:", StringComparison.Ordinal))
                    lyrics = await FindNeteaseId(track.SongId[8..], track.Duration.TotalSeconds, token);
                // KuGou's songs ask KuGou first; for the others it is the last provider asked.
                if (track.IsKugou) lyrics = await FindKugou(track, token);
                if (lyrics.Lines.Count == 0 && !lyrics.Instrumental) lyrics = await FindLrclib(track, token);
                if (lyrics.Lines.Count == 0 && !lyrics.Instrumental) lyrics = await FindNetease(track, token);
                if (lyrics.Lines.Count == 0 && !lyrics.Instrumental && !track.IsKugou) lyrics = await FindKugou(track, token);
            }
            token.ThrowIfCancellationRequested();
            // An empty result can be a temporary provider failure. Do not keep it for the entire app session.
            if (lyrics.Lines.Count > 0 || lyrics.Instrumental) Remember(cacheKey, lyrics);
            return lyrics;
        }
        finally { _gate.Release(); }
    }
    private void Remember(string cacheKey, Lyrics lyrics)
    {
        _cache[cacheKey] = lyrics; _order.Remove(cacheKey); _order.AddLast(cacheKey);
        while (_order.Count > 80) { _cache.Remove(_order.First!.Value); _order.RemoveFirst(); }
    }
    private async Task<JsonElement?> Get(string url, CancellationToken token)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var address = new Uri(url);
                for (int redirects = 0; ; redirects++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, address);
                    request.Headers.UserAgent.ParseAdd("BrimDeck/0.1.0");
                    if (address.Host == "music.163.com") request.Headers.Referrer = new Uri("https://music.163.com/");
                    if (address.Host is "u.y.qq.com" or "c.y.qq.com") request.Headers.Referrer = new Uri("https://y.qq.com/");
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if ((int)response.StatusCode is >= 300 and < 400)
                    {
                        // Only to HTTPS on the same host, and only a few times: a lookup never ends up somewhere else.
                        if (redirects >= MaxRedirects || response.Headers.Location is not { } location) return null;
                        var next = location.IsAbsoluteUri ? location : new Uri(address, location);
                        if (next.Scheme != Uri.UriSchemeHttps || !string.Equals(next.Host, address.Host, StringComparison.OrdinalIgnoreCase)) return null;
                        address = next;
                        continue;
                    }
                    if (response.StatusCode == HttpStatusCode.NotFound) return null;
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaxResponseBytes) return null;
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    var body = await ReadLimitedAsync(stream, timeout.Token);
                    if (body is null) return null;
                    // As a stream, which skips a byte order mark the way reading the response directly did.
                    using var json = JsonDocument.Parse(new MemoryStream(body, writable: false));
                    return json.RootElement.Clone();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                bool temporary = ex is JsonException or OperationCanceledException || ex is HttpRequestException http &&
                    (http.StatusCode is null or HttpStatusCode.RequestTimeout || (int)http.StatusCode >= 500);
                if (attempt != 0 || !temporary) return null;
                await Task.Delay(200, token);
            }
        }
        return null;
    }
    // The reply, or null once it is larger than MaxResponseBytes, whatever its declared length said.
    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
    private static string Str(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static double Num(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        StringBuilder destination, int destinationLength, IntPtr version, IntPtr reserved, IntPtr sortHandle);
    // LCMAP_SIMPLIFIED_CHINESE.
    private const uint SimplifiedChinese = 0x02000000;
    private static string Simplify(string value)
    {
        if (value.Length == 0) return value;
        var result = new StringBuilder(value.Length + 1);
        return LCMapStringEx("zh-CN", SimplifiedChinese, value, -1, result, result.Capacity, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) > 0 ? result.ToString() : value;
    }

    private static string Normalize(string value) => Regex.Replace(value, @"[\s\p{P}\p{S}]", "").ToUpperInvariant();
    // Players and providers write one name in either script (李耀陽, 李耀阳); names are compared in simplified Chinese.
    private static string MatchText(string value) => Normalize(Simplify(value));

    // How well a provider's song fits the track; a lower one fits better. Title 0: the same title; 1: the same title once a
    // note at the end of either is removed ("(Live)", "【DJ版】", "- Remastered 2011", "feat. …"), that is another version of
    // the song, taken only when no result has the title itself. Artists 0: all the track's artists; 1: some of them.
    private readonly record struct Fit(int Title, int Artists, double Gap);
    // A song whose title marks it as without words ("(伴奏)", "Instrumental") is not the sung track (measured: an accompaniment
    // lasts as long as the song to the second, so the length does not tell them apart).
    private static readonly Regex Wordless = new(@"伴奏|纯音乐|instrumental|karaoke|off\s*vocal", RegexOptions.IgnoreCase);
    private static readonly Regex TrailingNote = new(@"\s*(?:\([^()]*\)|（[^（）]*）|\[[^\[\]]*\]|【[^【】]*】)\s*$");
    private static readonly Regex FeaturedNote = new(@"\s+(?:feat\.?|ft\.|featuring)\s.*$", RegexOptions.IgnoreCase);
    private static readonly Regex VersionNote = new(@"\s[-–—]\s.*(?:remaster|live|version|edit|mix|mono|stereo|acoustic|版|现场|重制)[^-–—]*$", RegexOptions.IgnoreCase);
    private static string BareTitle(string title)
    {
        string text = title.Trim();
        while (true)
        {
            string next = VersionNote.Replace(FeaturedNote.Replace(TrailingNote.Replace(text, ""), ""), "").Trim();
            // A title that is all note ("(Untitled)") stays as it is.
            if (next == text || next.Length == 0) return text;
            text = next;
        }
    }
    private static string[] ArtistNames(string artist) => [.. Regex.Split(artist, @"\s*(?:[/、,，&;；]|\s(?:feat\.?|ft\.|featuring)\s)\s*", RegexOptions.IgnoreCase)
        .Select(MatchText).Where(name => name.Length > 0).Distinct()];
    // seconds: the song's length at the provider. A track with no artist (some players report none) is told by its title and
    // length alone; FindAsync asks for none without a length.
    private static Fit? FitOf(MediaTrack track, string title, string artist, double seconds)
    {
        double gap = 0;
        if (track.Duration.TotalSeconds > 0 && (gap = Math.Abs(seconds - track.Duration.TotalSeconds)) > 2) return null;
        if (Wordless.IsMatch(title) && !Wordless.IsMatch(track.Title)) return null;
        string expected = MatchText(track.Title), found = MatchText(title);
        int titleFit = expected.Length > 0 && expected == found ? 0 : MatchText(BareTitle(track.Title)) is { Length: > 0 } bare && bare == MatchText(BareTitle(title)) ? 1 : -1;
        if (titleFit < 0) return null;
        var names = ArtistNames(track.Artist);
        if (names.Length == 0) return new(titleFit, 0, gap);
        var theirs = ArtistNames(artist);
        int shared = names.Count(name => theirs.Any(other => other.Contains(name, StringComparison.Ordinal) || name.Contains(other, StringComparison.Ordinal)));
        return shared == 0 ? null : new(titleFit, shared == names.Length ? 0 : 1, gap);
    }
    // The items that fit the track, the best first; items that fit equally keep their order.
    private static IEnumerable<T> Fitting<T>(MediaTrack track, IEnumerable<T> items, Func<T, (string Title, string Artist, double Seconds)> song) =>
        items.Select(item => (Item: item, Fit: song(item) is var (title, artist, seconds) ? FitOf(track, title, artist, seconds) : null))
            .Where(row => row.Fit is not null).OrderBy(row => row.Fit!.Value.Title).ThenBy(row => row.Fit!.Value.Artists).ThenBy(row => row.Fit!.Value.Gap)
            .Select(row => row.Item);
    // The words to search a provider with: the title, then the artist when the player reported one.
    private static string SearchWords(MediaTrack track) => (track.Title + " " + track.Artist).Trim();
    private async Task<Lyrics> FindNeteaseId(string id, double duration, CancellationToken token)
    {
        if (!Regex.IsMatch(id, @"\A[0-9]{1,16}\z")) return Lyrics.Empty;
        // lv, kv and tv name the lyric versions the caller already holds; the reply leaves out a version that is
        // not newer. lv=1 therefore received an empty lyric for every song still at its first version (measured).
        var data = await Get($"https://music.163.com/api/song/lyric?id={id}&lv=-1&kv=-1&tv=-1", token);
        if (data is not { ValueKind: JsonValueKind.Object } value) return Lyrics.Empty;
        // NetEase marks a song without words; its lyric then holds only credits and a notice.
        if (value.TryGetProperty("pureMusic", out var pure) && pure.ValueKind == JsonValueKind.True) return new([], true, "网易云音乐", true);
        return value.TryGetProperty("lrc", out var lrc) ? Lyrics.Parse(Str(lrc, "lyric"), null, duration, "网易云音乐") : Lyrics.Empty;
    }
    private async Task<Lyrics> FindLrclib(MediaTrack track, CancellationToken token)
    {
        string query = $"track_name={Uri.EscapeDataString(track.Title)}&artist_name={Uri.EscapeDataString(track.Artist)}";
        Lyrics Parse(JsonElement value) => Lyrics.Parse(Str(value, "syncedLyrics"), Str(value, "plainLyrics"), track.Duration.TotalSeconds, "LRCLIB",
            value.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True);
        // The exact lookup names the artist; a track without one is only searched for.
        if (track.Artist.Trim().Length > 0)
        {
            string duration = track.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            var exact = await Get($"https://lrclib.net/api/get?{query}&album_name={Uri.EscapeDataString(track.Album)}&duration={duration}", token);
            if (exact is { ValueKind: JsonValueKind.Object } row && FitOf(track, Str(row, "trackName"), Str(row, "artistName"), Num(row, "duration")) is { Title: 0 })
            { var lyrics = Parse(row); if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics; }
        }
        var search = await Get("https://lrclib.net/api/search?" + query, token);
        if (search is { ValueKind: JsonValueKind.Array } list)
            foreach (var item in Fitting(track, list.EnumerateArray().OrderByDescending(v => Str(v, "syncedLyrics").Length > 0), v => (Str(v, "trackName"), Str(v, "artistName"), Num(v, "duration"))))
            { var lyrics = Parse(item); if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics; }
        return Lyrics.Empty;
    }
    private async Task<Lyrics> FindNetease(MediaTrack track, CancellationToken token)
    {
        var search = await Get("https://music.163.com/api/search/get/web?type=1&limit=8&s=" + Uri.EscapeDataString(SearchWords(track)), token);
        if (search is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return Lyrics.Empty;
        static string Artists(JsonElement song) => song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array ? string.Join(" / ", artists.EnumerateArray().Select(a => Str(a, "name"))) : "";
        foreach (var song in Fitting(track, songs.EnumerateArray().Where(song => song.TryGetProperty("id", out _)), song => (Str(song, "name"), Artists(song), Num(song, "duration") / 1000)))
        {
            var lyrics = await FindNeteaseId(song.GetProperty("id").ToString(), track.Duration.TotalSeconds, token);
            if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics;
        }
        return Lyrics.Empty;
    }
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        foreach (var watcher in _appleWatchers.Values) watcher.Dispose();
        _kugouWatcher?.Dispose();
    }
}
