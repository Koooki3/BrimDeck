using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BrimDeck.Core;

// The user's sign-in to music.apple.com. Apple's lyric endpoint answers only with the media-user-token cookie
// that the site sets after sign-in.
public interface IAppleMusicAccount
{
    // "" when the user has not authorized BrimDeck, or the authorization has expired.
    string Token { get; }
    // Apple rejected this token.
    void Expire(string token);
}

// Lyrics for the Apple Music desktop app. With the user's authorization they come from Apple's catalog; without it,
// from the lyrics Apple Music itself cached for the same catalog song. Either way the song is found in Apple's catalog
// first, since the media session carries no catalog ID.
public sealed partial class LyricsService
{
    public const string AppleSource = "Apple Music";
    private const string AppleApi = "https://amp-api.music.apple.com";
    private readonly IAppleMusicAccount? _appleAccount;
    private readonly Func<IEnumerable<string>> _appleCacheRoots;
    private string _appleWebToken = "";
    private DateTimeOffset _appleWebTokenExpires;
    private (string Token, string Storefront) _appleStorefront = ("", "");
    // The retries after a song starts look up the same song again.
    private readonly Dictionary<string, AppleSong> _appleSongs = new();
    private sealed record AppleSong(string Id, bool HasLyrics);

    private async Task<Lyrics> FindApple(MediaTrack track, string cacheKey, CancellationToken token)
    {
        string user = _appleAccount?.Token ?? "";
        string storefront = user.Length > 0 ? await AppleStorefront(user, token) : "";
        if (storefront.Length == 0) { user = ""; storefront = await AppleCacheStorefront(token); }
        var song = await FindAppleSong(track, storefront, cacheKey, token);
        if (song is not { HasLyrics: true }) return Lyrics.Empty;
        if (user.Length > 0)
        {
            var (status, reply) = await AppleGet($"/v1/catalog/{storefront}/songs/{song.Id}/lyrics", user, token);
            if (Child(reply, "data") is { ValueKind: JsonValueKind.Array } rows && rows.GetArrayLength() > 0)
            {
                try
                {
                    if (Child(Child(rows[0], "attributes"), "ttml") is { ValueKind: JsonValueKind.String } ttml &&
                        ParseAppleTtml(ttml.GetString()!) is { Lines.Count: > 0 } lyrics) return lyrics;
                }
                catch (XmlException) { }
            }
            // Apple answers 404 both for a song without lyrics and for a rejected token; the account's storefront tells them apart.
            else if (status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { _appleStorefront = ("", ""); await AppleStorefront(user, token); }
        }
        return await FindAppleCache(song.Id, token);
    }

    // LRCLIB, QQ Music and NetEase in turn until one has synced lyrics; plain lyrics are used only when none has.
    private async Task<Lyrics> FindAppleFallback(MediaTrack track, Lyrics plain, CancellationToken token)
    {
        foreach (var find in new Func<MediaTrack, CancellationToken, Task<Lyrics>>[] { FindLrclib, FindQq, FindNetease })
        {
            var lyrics = await find(track, token);
            if (lyrics.Synced && lyrics.Lines.Count > 0) return lyrics;
            if (plain.Lines.Count == 0 && lyrics.Lines.Count > 0) plain = lyrics;
        }
        return plain;
    }

    // The storefront of the signed-in account, or "" when Apple rejects the token (the account then expires) or cannot be reached.
    private async Task<string> AppleStorefront(string user, CancellationToken token)
    {
        if (_appleStorefront.Token == user) return _appleStorefront.Storefront;
        var (status, reply) = await AppleGet("/v1/me/storefront", user, token);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { _appleAccount?.Expire(user); return ""; }
        string id = Child(reply, "data") is { ValueKind: JsonValueKind.Array } data && data.GetArrayLength() > 0 ? Str(data[0], "id") : "";
        if (!Regex.IsMatch(id, @"\A[a-z]{2}\z")) return "";
        _appleStorefront = (user, id);
        return id;
    }

    private async Task<AppleSong?> FindAppleSong(MediaTrack track, string storefront, string cacheKey, CancellationToken token)
    {
        string key = storefront + "\n" + cacheKey;
        if (_appleSongs.TryGetValue(key, out var known)) return known;
        if (track.Duration <= TimeSpan.Zero) return null;
        var (_, reply) = await AppleGet($"/v1/catalog/{storefront}/search?types=songs&limit=25&term=" + Uri.EscapeDataString(track.Title + " " + track.Artist), "", token);
        if (Child(Child(Child(reply, "results"), "songs"), "data") is not { ValueKind: JsonValueKind.Array } songs) return null;
        foreach (var song in songs.EnumerateArray())
        {
            if (Child(song, "attributes") is not { ValueKind: JsonValueKind.Object } a || !Regex.IsMatch(Str(song, "id"), @"\A[0-9]{1,16}\z") ||
                !Matches(track, Str(a, "name"), Str(a, "artistName"), Num(a, "durationInMillis") / 1000) ||
                track.Album.Length > 0 && MatchText(track, track.Album) != MatchText(track, Str(a, "albumName"))) continue;
            if (_appleSongs.Count >= 80) _appleSongs.Clear();
            return _appleSongs[key] = new(Str(song, "id"), Child(a, "hasLyrics") is { ValueKind: JsonValueKind.True });
        }
        return null;
    }

    // Requests to Apple's catalog carry the web player's token; user adds the signed-in account's token.
    private async Task<(HttpStatusCode? Status, JsonElement? Reply)> AppleGet(string path, string user, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            string web = await AppleWebToken(attempt > 0, token);
            if (web.Length == 0) return (null, null);
            using var request = new HttpRequestMessage(HttpMethod.Get, AppleApi + path);
            request.Headers.Authorization = new("Bearer", web);
            request.Headers.Add("Origin", "https://music.apple.com");
            if (user.Length > 0) request.Headers.Add("media-user-token", user);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                // Apple replaced the web player's token before it expired.
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                if (!response.IsSuccessStatusCode) return (response.StatusCode, null);
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var body = await ReadLimitedAsync(stream, timeout.Token);
                if (body is null) return (response.StatusCode, null);
                using var json = JsonDocument.Parse(body);
                return (response.StatusCode, json.RootElement.Clone());
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !token.IsCancellationRequested)
            { return (null, null); }
        }
    }

    // The token music.apple.com's own player uses, published in its script bundle. It lasts 70 days (measured).
    private async Task<string> AppleWebToken(bool renew, CancellationToken token)
    {
        if (!renew && _appleWebToken.Length > 0 && _appleWebTokenExpires > DateTimeOffset.UtcNow.AddHours(1)) return _appleWebToken;
        _appleWebToken = "";
        var script = Regex.Match(await GetText("https://music.apple.com/us/new", token), @"/assets/index[~-][\w~-]*\.js");
        if (!script.Success) return "";
        foreach (Match candidate in Regex.Matches(await GetText("https://music.apple.com" + script.Value, token), @"eyJ[\w-]+\.([\w-]+)\.[\w-]+"))
        {
            try
            {
                using var header = JsonDocument.Parse(Base64Url(candidate.Value[..candidate.Value.IndexOf('.')]));
                using var payload = JsonDocument.Parse(Base64Url(candidate.Groups[1].Value));
                if (Str(header.RootElement, "kid") != "WebPlayKid") continue;
                _appleWebToken = candidate.Value;
                _appleWebTokenExpires = DateTimeOffset.FromUnixTimeSeconds((long)Num(payload.RootElement, "exp"));
                return _appleWebToken;
            }
            catch (Exception ex) when (ex is FormatException or JsonException) { }
        }
        return "";
    }
    private static byte[] Base64Url(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '='));
    }
    // The script bundle is about 3 MB.
    private async Task<string> GetText(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return "";
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var body = await ReadLimitedAsync(stream, timeout.Token, 16 * 1024 * 1024);
            return body is null ? "" : Encoding.UTF8.GetString(body);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !token.IsCancellationRequested) { return ""; }
    }
    private static JsonElement? Child(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child) ? child : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        StringBuilder destination, int destinationLength, IntPtr version, IntPtr reserved, IntPtr sortHandle);
    private const uint SimplifiedChinese = 0x02000000;
    private static string SimplifyAppleText(string value)
    {
        if (value.Length == 0) return value;
        var result = new StringBuilder(value.Length + 1);
        return LCMapStringEx("zh-CN", SimplifiedChinese, value, -1, result, result.Capacity, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) > 0 ? result.ToString() : value;
    }

    private static IEnumerable<string> AppleCacheRoots()
    {
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) return [];
        return Directory.GetDirectories(packages, "AppleInc.AppleMusicWin_*")
            .Select(p => Path.Combine(p, "AC", "INetCache")).Where(Directory.Exists).ToArray();
    }

    private string[] AppleCacheFiles(string pattern)
    {
        // The cache's folders are hidden system folders, which enumeration skips by default.
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try { return _appleCacheRoots().SelectMany(root => Directory.EnumerateFiles(root, pattern, options)).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
    private static async Task<byte[]?> ReadAppleCacheFile(string path, CancellationToken token)
    {
        // Apple Music may be writing or removing the file.
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            return await ReadLimitedAsync(stream, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Without the account's storefront: the one Apple Music's cached catalog replies name most (cn for a Chinese account on a
    // Windows set to Taiwan, measured), otherwise the Windows region.
    private string _appleCacheStorefront = "";
    private async Task<string> AppleCacheStorefront(CancellationToken token)
    {
        if (_appleCacheStorefront.Length > 0) return _appleCacheStorefront;
        var counts = new Dictionary<string, int>();
        foreach (string path in AppleCacheFiles("*.json"))
            if (await ReadAppleCacheFile(path, token) is { } bytes)
                foreach (Match match in Regex.Matches(Encoding.UTF8.GetString(bytes), "/v1/catalog/([a-z]{2})/"))
                    counts[match.Groups[1].Value] = counts.GetValueOrDefault(match.Groups[1].Value) + 1;
        if (counts.Count == 0) return RegionInfo.CurrentRegion.TwoLetterISORegionName.ToLowerInvariant();
        return _appleCacheStorefront = counts.MaxBy(pair => pair.Value).Key;
    }

    // Apple Music writes the lyrics it shows to its web cache as ttmlLyrics*.json. Their lyric ID (AP_1592065224-zh-Hans)
    // starts with the catalog ID of the song they were written for; a reissued song can keep an older one and is missed here.
    private async Task<Lyrics> FindAppleCache(string id, CancellationToken token)
    {
        var lyricsId = new Regex($@"\A(?:AP|CP)_{id}(?:-|\z)");
        var plain = Lyrics.Empty;
        foreach (string path in AppleCacheFiles("ttmlLyrics*.json"))
        {
            try
            {
                if (await ReadAppleCacheFile(path, token) is not { } bytes) continue;
                using var json = JsonDocument.Parse(bytes);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || Str(root, "status") != "success" || !lyricsId.IsMatch(Str(root, "lyricsId"))) continue;
                var lyrics = ParseAppleTtml(Str(root, "ttml"));
                if (lyrics.Synced && lyrics.Lines.Count > 0) return lyrics;
                if (plain.Lines.Count == 0) plain = lyrics;
            }
            // A file Apple Music has not finished writing.
            catch (Exception ex) when (ex is JsonException or XmlException) { }
        }
        return plain;
    }

    public static Lyrics ParseAppleTtml(string ttml)
    {
        using var reader = XmlReader.Create(new StringReader(ttml), new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxResponseBytes });
        var xml = XDocument.Load(reader);
        XNamespace ns = "http://www.w3.org/ns/ttml";
        var body = xml.Root?.Element(ns + "body");
        if (body is null) return Lyrics.Empty;
        string Text(XElement row) => string.Concat(row.DescendantNodes().Select(n => n is XText t ? t.Value :
            n is XElement e && e.Name == ns + "br" ? "\n" : "")).Trim();
        var rows = body.Descendants(ns + "p").Where(row => Text(row).Length > 0).ToArray();
        if (rows.Length == 0) return Lyrics.Empty;
        double? Start(XElement row) => AppleTime((string?)row.Attribute("begin")) ??
            row.Descendants(ns + "span").Select(e => AppleTime((string?)e.Attribute("begin"))).FirstOrDefault(t => t.HasValue);
        // Lines without timing are shown as plain lyrics rather than given guessed times.
        if (rows.Any(row => Start(row) is null))
            return new(rows.Select(row => new LyricLine(0, Text(row))).ToArray(), false, AppleSource);
        var timed = rows.Select(row => (Start: Start(row)!.Value, End: AppleTime((string?)row.Attribute("end")), Text: Text(row)))
            .OrderBy(row => row.Start).ToArray();
        var lines = new List<LyricLine>();
        for (int i = 0; i < timed.Length; i++)
        {
            var row = timed[i]; lines.Add(new(row.Start, row.Text));
            // A line that ends before the next one starts leaves a gap, shown as a blank line.
            if (row.End is { } end && end > row.Start && (i == timed.Length - 1 || end < timed[i + 1].Start))
                lines.Add(new(end, ""));
        }
        return new(lines, true, AppleSource);
    }

    // TTML clock times: "1:02.5", "62.5s", "62500ms".
    private static double? AppleTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        double seconds = 0;
        if (value.EndsWith("ms", StringComparison.Ordinal))
        {
            if (!double.TryParse(value[..^2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds)) return null;
            seconds /= 1000;
        }
        else
        {
            if (value.EndsWith('s')) value = value[..^1];
            var parts = value.Split(':');
            if (parts.Length > 3) return null;
            foreach (string part in parts)
            {
                if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return null;
                seconds = seconds * 60 + number;
            }
        }
        return seconds;
    }
}
