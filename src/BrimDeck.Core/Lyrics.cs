using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

// A word of a timed line (Karaoke): the second it is sung, how many characters of the line it covers, and how long it is
// sung (0 where the lyric does not say: the word then lasts until the next one). The words keep the order they are sung,
// so each lights up as its time passes.
public sealed record LyricWord(double Time, int Count, double Duration = 0);
// How far a word-timed line is sung at a time (Karaoke): the characters the words cover so far, how fast the sweep moves
// through them (characters a second), and the characters it covers when it comes to rest at the end of the word being sung,
// where it waits for the next. The rate and the rest let a lyric area carry the sweep on between a player's reports, which
// come four times a second (MusicMarquee), so a character lights up as it is sung rather than the line jumping word by word.
public readonly record struct LyricSweep(double Sung, double Rate, double Rest);
// Lines without per-word times carry no Words (null): they are shown whole, with no Karaoke highlighting.
public sealed record LyricLine(double Seconds, string Text, IReadOnlyList<LyricWord>? Words = null);
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
        int index = FindIndex(seconds, hold);
        string[] Texts(int from, int count) => [.. Lines.Skip(from).Take(count).Select(line => line.Text)];
        if (index < 0) return new([], "♪", Texts(0, after));
        // LRC timestamps start a line; they do not specify how long it is sung.
        // Only an explicit blank line marks a gap before the next timestamp (Parse keeps only those of Gap or longer).
        string text = string.IsNullOrWhiteSpace(Lines[index].Text) ? "♪" : Lines[index].Text;
        int first = Math.Max(0, index - before);
        return new(Texts(first, index - first), text, Texts(index + 1, after));
    }
    // The line the position falls in (-1 before the first). hold keeps the line on show through a small
    // step back of the position (LyricHold), as the line the position fell in when it stepped back.
    internal int FindIndex(double seconds, LyricHold? hold)
    {
        int index = -1;
        for (int i = 0; i < Lines.Count && Lines[i].Seconds <= seconds; i++) index = i;
        return hold is null ? index : hold.Index(this, index, seconds);
    }
    // The current line and how far it is sung, word by word (Karaoke): the words whose time has passed and the part of the
    // word being sung, so the marquee can sweep the highlight through a character as it is sung. The words are the current
    // line's own; a line without per-word times, or none yet (an instrumental, the start of a gap), returns no words: the
    // line is drawn whole. A paused song reports no rate: the sweep stays where the song does.
    public (IReadOnlyList<LyricWord>? Words, LyricSweep Sweep) Karaoke(MediaTrack track, DateTimeOffset now, LyricHold? hold = null, bool simulate = false)
    {
        if (!CanSynchronize(track) || QqPreview && track.IsQqMusic) return (null, default);
        var (words, sweep) = Karaoke(track.Position(now).TotalSeconds + (track.IsAppleMusic ? AppleLead : 0), hold);
        if (words is null && simulate)
            (words, sweep) = SimulatedKaraoke(track.Position(now).TotalSeconds + (track.IsAppleMusic ? AppleLead : 0), track.Duration.TotalSeconds, hold);
        return (words, track.State == MediaState.Playing ? sweep : sweep with { Rate = 0 });
    }
    private readonly Dictionary<int, IReadOnlyList<LyricWord>> _simulatedWords = new();
    private (IReadOnlyList<LyricWord>? Words, LyricSweep Sweep) SimulatedKaraoke(double seconds, double duration, LyricHold? hold)
    {
        int index = FindIndex(seconds, hold);
        if (index < 0 || Instrumental || string.IsNullOrWhiteSpace(Lines[index].Text)) return (null, default);
        var line = Lines[index];
        double end = index + 1 < Lines.Count ? Lines[index + 1].Seconds : duration > line.Seconds ? duration : line.Seconds + 4.8;
        double span = Math.Max(0.75, end - line.Seconds);
        double progress = Math.Clamp((seconds - line.Seconds) / span, 0, 1);
        if (!_simulatedWords.TryGetValue(index, out var words))
            _simulatedWords[index] = words = new[] { new LyricWord(line.Seconds, line.Text.Length) };
        double sung = line.Text.Length * progress * progress * (3 - 2 * progress);
        double rate = line.Text.Length * 6 * progress * (1 - progress) / span;
        return (words, new(sung, rate, line.Text.Length));
    }
    public (IReadOnlyList<LyricWord>? Words, LyricSweep Sweep) Karaoke(double seconds, LyricHold? hold = null)
    {
        int index = FindIndex(seconds, hold);
        if (index < 0 || index >= Lines.Count || Lines[index].Words is not { Count: > 0 } words) return (null, default);
        return (words, Swept(words, seconds));
    }
    // A word covers its characters over the time it is sung — its own duration where the lyric gives one (yrc), otherwise
    // until the next word — so the sweep crosses the word a character at a time instead of lighting it whole. Before the
    // first word, and between one word's end and the next one's start, it rests where it stands.
    private static LyricSweep Swept(IReadOnlyList<LyricWord> words, double seconds)
    {
        int sung = 0, index = 0;
        while (index < words.Count && words[index].Time <= seconds) sung += words[index++].Count;
        if (index == 0) return new(0, 0, 0);
        var word = words[index - 1];
        double end = word.Duration > 0 ? word.Time + word.Duration : index < words.Count ? words[index].Time : 0;
        if (end <= word.Time) return new(sung, 0, sung);
        double part = Math.Clamp((seconds - word.Time) / (end - word.Time), 0, 1);
        return part >= 1 ? new(sung, 0, sung) : new(sung - (1 - part) * word.Count, word.Count / (end - word.Time), sung);
    }
    public static Lyrics Parse(string? lrc, string? plain, double duration, string source, bool instrumental = false)
    {
        var result = new List<LyricLine>();
        double offset = 0;
        var offsetMatch = Regex.Match(lrc ?? "", @"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase);
        if (offsetMatch.Success && double.TryParse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var ms)) offset = ms / 1000;
        foreach (var row in (lrc ?? "").Split('\n'))
        {
            if (ParseCredit(row, offset) is { } credit) { result.Add(credit); continue; }
            var stamps = Regex.Matches(row, @"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]");
            if (stamps.Count == 0) continue;
            // Word-timed LRC puts a <mm:ss.xx> tag before each word; the tags are dropped, and the words are
            // kept so the line can light up as it is sung (Karaoke). The same words are kept with every line a row stamps.
            var words = WordTimedRows(row[(stamps[^1].Index + stamps[^1].Length)..], out string text);
            foreach (Match stamp in stamps)
            {
                double time = int.Parse(stamp.Groups[1].Value) * 60 + int.Parse(stamp.Groups[2].Value);
                if (stamp.Groups[3].Success) time += double.Parse("0." + stamp.Groups[3].Value, CultureInfo.InvariantCulture);
                result.Add(new(Math.Max(0, time + offset), text, words));
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
    private static LyricLine? ParseCredit(string row, double offset)
    {
        if (!row.TrimStart().StartsWith('{')) return null;
        try
        {
            using var json = JsonDocument.Parse(row);
            var root = json.RootElement;
            if (!root.TryGetProperty("t", out var time) || !time.TryGetDouble(out double milliseconds) || !double.IsFinite(milliseconds)
                || !root.TryGetProperty("c", out var parts) || parts.ValueKind != JsonValueKind.Array) return null;
            var text = string.Concat(parts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object)
                .Select(p => p.TryGetProperty("tx", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() : "")).Trim();
            return text.Length == 0 ? null : new(Math.Max(0, milliseconds / 1000 + offset), text);
        }
        catch (JsonException) { return null; }
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
    // Word-timed LRC puts a <mm:ss.xx> tag before each word, its time on the song's clock and the characters it
    // covers of the line (the next tag's, or the line's end). Tags of empty words are dropped. The line's text is
    // the words it carries with its leading and trailing whitespace trimmed, and a word that held the trimmed text
    // covers as many characters as remain. Without tags the whole line is returned as its text and no words.
    private static IReadOnlyList<LyricWord>? WordTimedRows(string seed, out string text)
    {
        var matches = Regex.Matches(seed, @"<(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?>");
        if (matches.Count == 0) { text = seed.Trim(); return null; }
        var words = new List<LyricWord>();
        var builder = new StringBuilder();
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            string fragment = seed[(match.Index + match.Length)..(i + 1 < matches.Count ? matches[i + 1].Index : seed.Length)];
            if (fragment.Length == 0) continue;
            double time = int.Parse(match.Groups[1].Value) * 60 + int.Parse(match.Groups[2].Value);
            if (match.Groups[3].Success) time += double.Parse("0." + match.Groups[3].Value, CultureInfo.InvariantCulture);
            words.Add(new LyricWord(time, fragment.Length));
            builder.Append(fragment);
        }
        text = builder.ToString().Trim();
        // A line trimmed about its text is trimmed about its words as well, so they cover exactly the shown line:
        // the whitespace dropped at a side comes off the first (or last) word that carried it.
        if (words.Count > 0 && text.Length < builder.Length)
        {
            int droppedLead = 0;
            while (droppedLead < builder.Length && char.IsWhiteSpace(builder[droppedLead])) droppedLead++;
            int droppedTrail = 0;
            while (droppedTrail < builder.Length - droppedLead && char.IsWhiteSpace(builder[^(1 + droppedTrail)])) droppedTrail++;
            if (droppedLead > 0) words[0] = words[0] with { Count = Math.Max(0, words[0].Count - droppedLead) };
            if (droppedTrail > 0) words[^1] = words[^1] with { Count = Math.Max(0, words[^1].Count - droppedTrail) };
        }
        return words.Count > 0 ? words : null;
    }
    // NetEase yrc lyrics give a song word at a time. A row opens a line with its start and its whole length in
    // milliseconds, then each word with the time it is sung on the song's clock, how long it is sung and, after a
    // fixed third field, the word itself: [17400,2080](17400,300,0)被(17700,300,0)我. The words let a line light
    // up as it is sung (Karaoke). A row without a comma after its start is an ordinary LRC line, which some
    // replies mix into the yrc field.
    public static Lyrics ParseYrc(string? yrc, double duration, string source, bool instrumental = false)
    {
        if (string.IsNullOrWhiteSpace(yrc)) return new([], true, source, instrumental);
        double offset = 0;
        var offsetMatch = Regex.Match(yrc, @"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase);
        if (offsetMatch.Success && double.TryParse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var ms)) offset = ms / 1000;
        static bool Milliseconds(string value, out double seconds)
        {
            seconds = 0;
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long raw) && (seconds = raw / 1000d) >= 0;
        }
        var result = new List<LyricLine>();
        foreach (var row in yrc.Split('\n'))
        {
            if (ParseCredit(row, offset) is { } credit) { result.Add(credit); continue; }
            string line = row.Trim();
            if (line.Length == 0 || line[0] != '[' || line.IndexOf(']') < 0) continue;
            int close = line.IndexOf(']');
            string header = line[1..close];
            string tail = line[(close + 1)..];
            // A row without a comma holds an ordinary LRC line, kept without word timings.
            if (header.IndexOf(',') < 0)
            {
                var stamp = Regex.Match(line, @"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]");
                if (!stamp.Success) continue;
                double time = int.Parse(stamp.Groups[1].Value) * 60 + int.Parse(stamp.Groups[2].Value);
                if (stamp.Groups[3].Success) time += double.Parse("0." + stamp.Groups[3].Value, CultureInfo.InvariantCulture);
                result.Add(new(Math.Max(0, time + offset), tail.Trim()));
                continue;
            }
            // A word's token holds the time it is sung and its duration, with a fixed third field; the word follows
            // it, from the token's end up to the next token or the row's end. The duration is kept with the word, so the
            // sweep can cross it a character at a time rather than lighting it whole (LyricSweep).
            var words = new List<LyricWord>();
            var builder = new StringBuilder();
            for (int position = 0; ;)
            {
                if (tail.IndexOf('(', position) != position) break;
                int tokenEnd = tail.IndexOf(')', position + 1);
                if (tokenEnd < 0) break;
                var fields = tail[(position + 1)..tokenEnd].Split(',');
                if (fields.Length < 2 || !Milliseconds(fields[0], out double at)) break;
                Milliseconds(fields[1], out double sung);
                int next = tail.IndexOf('(', tokenEnd + 1);
                string fragment = next < 0 ? tail[(tokenEnd + 1)..] : tail[(tokenEnd + 1)..next];
                position = next < 0 ? tail.Length : next;
                if (fragment.Length == 0) continue;
                words.Add(new LyricWord(Math.Max(0, at + offset), fragment.Length, sung));
                builder.Append(fragment);
            }
            if (words.Count == 0) continue;
            string text = builder.ToString();
            // A trailing space a line draws at its end is dropped with the word that carried it, so the words
            // cover exactly the shown line.
            int trailing = text.Length - text.TrimEnd(' ', '\t').Length;
            if (trailing > 0) { text = text[..^trailing]; words[^1] = words[^1] with { Count = Math.Max(0, words[^1].Count - trailing) }; }
            int comma = header.IndexOf(',');
            if (comma < 0 || text.Length == 0 || !Milliseconds(header[..comma], out double start)) continue;
            result.Add(new(Math.Max(0, start + offset), text, words));
        }
        if (result.Count > 0)
            return InstrumentalNotice(result.Select(line => line.Text)) ? new([], true, source, true) : new(WithoutShortGaps([.. result.OrderBy(line => line.Seconds).Distinct()]), true, source, instrumental);
        return new([], true, source, instrumental);
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
    public LyricsService(HttpClient? http = null, Func<IEnumerable<string>>? appleCacheRoots = null, string? appleLyricsFile = null,
        Func<string>? queMusicSettings = null)
    {
        _appleCacheRoots = appleCacheRoots ?? AppleCacheRoots;
        _queMusicSettings = queMusicSettings ?? QueMusicSettingsFile;
        _appleLyricsFile = appleLyricsFile; _appleLyricsIds = LoadAppleLyricsIds(appleLyricsFile);
        _ownsHttp = http is null;
        // Redirects are followed by Get, only to HTTPS on the same host.
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    }
    public async Task<Lyrics> FindAsync(MediaTrack track, bool enabled, CancellationToken token, Action<Lyrics>? onAvailable = null)
    {
        if (!enabled || string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist)) return Lyrics.Empty;
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
            // A third-party result for Apple Music is kept only until Apple Music caches its own lyrics.
            bool hit = _cache.TryGetValue(cacheKey, out var cached);
            if (hit && (!track.IsAppleMusic || cached!.Source == AppleSource))
            { _order.Remove(cacheKey); _order.AddLast(cacheKey); return cached!; }
            var lyrics = Lyrics.Empty;
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
                    lyrics = await FindNeteaseId(track.SongId[8..], track.Duration.TotalSeconds, token, onAvailable);
                // KuGou's songs ask KuGou first; for the others it is the last provider asked.
                if (track.IsKugou) lyrics = await FindKugou(track, token);
                if (lyrics.Lines.Count == 0 && !lyrics.Instrumental) lyrics = await FindLrclib(track, token);
                if (lyrics.Lines.Count == 0 && !lyrics.Instrumental) lyrics = await FindNetease(track, token, onAvailable);
                if (lyrics.Lines.Count == 0 && !lyrics.Instrumental && !track.IsKugou) lyrics = await FindKugou(track, token);
            }
            token.ThrowIfCancellationRequested();
            // An empty result can be a temporary provider failure. Do not keep it for the entire app session.
            if (lyrics.Lines.Count > 0 || lyrics.Instrumental)
            {
                _cache[cacheKey] = lyrics; _order.Remove(cacheKey); _order.AddLast(cacheKey);
                while (_order.Count > 80) { _cache.Remove(_order.First!.Value); _order.RemoveFirst(); }
            }
            return lyrics;
        }
        finally { _gate.Release(); }
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
    private static string Normalize(string value) => Regex.Replace(value, @"[\s\p{P}\p{S}]", "").ToUpperInvariant();
    // Apple Music shows names in the storefront's script (李耀陽); the other providers may use another (李耀阳).
    private static string MatchText(MediaTrack track, string value) => Normalize(track.IsAppleMusic ? SimplifyAppleText(value) : value);
    private static bool Matches(MediaTrack track, string title, string artist, double seconds)
        => MatchText(track, track.Title) == MatchText(track, title) && Regex.Split(track.Artist, @"\s*[/、,，&;；]\s*").Where(a => a.Length > 0).All(a => MatchText(track, artist).Contains(MatchText(track, a), StringComparison.Ordinal))
            && (track.Duration.TotalSeconds <= 0 || Math.Abs(seconds - track.Duration.TotalSeconds) <= 2);
    private async Task<Lyrics> FindNeteaseId(string id, double duration, CancellationToken token, Action<Lyrics>? onAvailable = null)
    {
        if (!Regex.IsMatch(id, @"\A[0-9]{1,16}\z")) return Lyrics.Empty;
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = new List<Task<Lyrics>> { FindNeteaseNew(id, duration, race.Token), FindNeteaseOld(id, duration, race.Token) };
        var first = Lyrics.Empty;
        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                var result = await completed;
                pending.Remove(completed);
                token.ThrowIfCancellationRequested();
                if (result.Lines.Count == 0 && !result.Instrumental) continue;
                if (result.Lines.Any(line => line.Words is { Count: > 0 }))
                {
                    if (!first.Lines.Any(line => line.Words is { Count: > 0 })) { first = result; onAvailable?.Invoke(first); }
                    continue;
                }
                if (first.Lines.Count == 0 && !first.Instrumental)
                {
                    first = result;
                    onAvailable?.Invoke(first);
                }
            }
            return first;
        }
        finally
        {
            race.Cancel();
            foreach (var task in pending) _ = ObserveNeteaseRequest(task);
        }
    }
    private static async Task ObserveNeteaseRequest(Task<Lyrics> task)
    {
        try { await task; } catch (Exception) { /* Canceled or failed losing request. */ }
    }
    private async Task<Lyrics> FindNeteaseOld(string id, double duration, CancellationToken token)
    {
        var data = await Get($"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=1&yv=1&ytv=1&rv=1&yrv=1", token);
        return data is { ValueKind: JsonValueKind.Object } value ? NeteaseLyric(value, duration) : Lyrics.Empty;
    }
    // A NetEase lyric reply: the word-timed lyric wins over the plain one when it has words, its times being the
    // line's own. It is labelled yrc (the version asked for with yv) or klyric; both hold the word-timed rows
    // ParseYrc reads. The plain lrc is the fallback for a song without word timings.
    private Lyrics NeteaseLyric(JsonElement value, double duration)
    {
        // NetEase marks a song without words; its lyric then holds only credits and a notice.
        if (value.TryGetProperty("pureMusic", out var pure) && pure.ValueKind == JsonValueKind.True) return new([], true, "网易云音乐", true);
        static string WordTimed(JsonElement root, string name) => root.TryGetProperty(name, out var entry) ? Str(entry, "lyric") : "";
        string wordTimed = WordTimed(value, "yrc");
        if (wordTimed.Length == 0) wordTimed = WordTimed(value, "klyric");
        if (wordTimed.Length > 0)
        {
            var karaoke = Lyrics.ParseYrc(wordTimed, duration, "网易云音乐");
            if (karaoke.Lines.Count > 0 || karaoke.Instrumental) return karaoke;
        }
        return value.TryGetProperty("lrc", out var lrc) ? Lyrics.Parse(Str(lrc, "lyric"), null, duration, "网易云音乐") : Lyrics.Empty;
    }
    private async Task<Lyrics> FindLrclib(MediaTrack track, CancellationToken token)
    {
        string query = $"track_name={Uri.EscapeDataString(track.Title)}&artist_name={Uri.EscapeDataString(track.Artist)}";
        string duration = track.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var exact = await Get($"https://lrclib.net/api/get?{query}&album_name={Uri.EscapeDataString(track.Album)}&duration={duration}", token);
        Lyrics Parse(JsonElement value) => Lyrics.Parse(Str(value, "syncedLyrics"), Str(value, "plainLyrics"), track.Duration.TotalSeconds, "LRCLIB",
            value.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True);
        if (exact is { ValueKind: JsonValueKind.Object } row && Matches(track, Str(row, "trackName"), Str(row, "artistName"), Num(row, "duration")))
        { var lyrics = Parse(row); if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics; }
        var search = await Get("https://lrclib.net/api/search?" + query, token);
        if (search is { ValueKind: JsonValueKind.Array } list)
            foreach (var item in list.EnumerateArray().Where(v => Matches(track, Str(v, "trackName"), Str(v, "artistName"), Num(v, "duration"))).OrderByDescending(v => Str(v, "syncedLyrics").Length > 0))
            { var lyrics = Parse(item); if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics; }
        return Lyrics.Empty;
    }
    private Task<Lyrics> FindNetease(MediaTrack track, CancellationToken token) => FindNetease(track, token, null);
    private async Task<Lyrics> FindNetease(MediaTrack track, CancellationToken token, Action<Lyrics>? onAvailable)
    {
        var search = await Get("https://music.163.com/api/search/get/web?type=1&limit=8&s=" + Uri.EscapeDataString(track.Title + " " + track.Artist), token);
        if (search is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return Lyrics.Empty;
        foreach (var song in songs.EnumerateArray())
        {
            string artist = song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array ? string.Join(" / ", artists.EnumerateArray().Select(a => Str(a, "name"))) : "";
            if (!Matches(track, Str(song, "name"), artist, Num(song, "duration") / 1000) || !song.TryGetProperty("id", out var id)) continue;
            var lyrics = await FindNeteaseId(id.ToString(), track.Duration.TotalSeconds, token, onAvailable);
            if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics;
        }
        return Lyrics.Empty;
    }
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        foreach (var watcher in _appleWatchers.Values) watcher.Dispose();
    }
}
