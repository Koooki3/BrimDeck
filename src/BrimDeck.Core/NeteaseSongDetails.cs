using System.Net.Http;
using System.Text.Json;

namespace BrimDeck.Core;

// The song's own details that the player's log or CDP may leave out: the full-size album artwork, the complete
// artist list and the album name. NetEase's public song/detail reply has all three, keyed by the numeric song id
// that the player already reports (MediaTrack.SongId). This is separate from the lyric service: a song's artwork
// is needed even when its lyrics are disabled or local, and the detail must never change what is playing now.
public sealed record NeteaseSongDetails(string Id, string Name, string Artist, string Album, double DurationSeconds, string? CoverUrl)
{
    public static NeteaseSongDetails? Parse(JsonElement song, string requestedId)
    {
        if (song.ValueKind != JsonValueKind.Object) return null;
        if (!song.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number)) return null;
        // Only the requested song may fill the gap: a search/detail reply for another id must not relabel the playing one.
        if (number.ToString() != requestedId) return null;
        static string Str(JsonElement o, string key) => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string name = Str(song, "name");
        string artist = song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
            ? string.Join(" / ", artists.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object).Select(a => Str(a, "name")).Where(n => n.Length > 0)) : "";
        string album = "", cover = "";
        if (song.TryGetProperty("album", out var record) && record.ValueKind == JsonValueKind.Object)
        { album = Str(record, "name"); cover = Str(record, "picUrl"); }
        double duration = song.TryGetProperty("duration", out var ms) && ms.TryGetDouble(out var value) && value > 0 ? value / 1000 : 0;
        return new(requestedId, name, artist, album, duration, cover.Length == 0 ? null : cover);
    }
}

// Looks up song details by id and caches them. Lookups are silent and optional: the live player source still shows
// the song without them, so a failed or slow request must never hold up metadata, artwork or controls.
public sealed class NeteaseSongDetailService : IDisposable
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, NeteaseSongDetails> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<NeteaseSongDetails?>> _pending = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    public NeteaseSongDetailService(HttpClient? http = null)
        => _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

    // What is already known, for the synchronous metadata read on the UI thread.
    public NeteaseSongDetails? Known(string id)
    {
        lock (_gate) return _cache.TryGetValue(id, out var details) ? details : null;
    }

    public Task<NeteaseSongDetails?> GetAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || _disposed) return Task.FromResult<NeteaseSongDetails?>(null);
        Task<NeteaseSongDetails?> task;
        lock (_gate)
        {
            if (_cache.TryGetValue(id, out var known)) return Task.FromResult<NeteaseSongDetails?>(known);
            // Several sources can ask for the same song in one refresh; one request serves them all.
            if (!_pending.TryGetValue(id, out task!)) _pending[id] = task = FetchAsync(id);
        }
        return task;
    }

    private async Task<NeteaseSongDetails?> FetchAsync(string id)
    {
        try
        {
            using var response = await _http.GetAsync($"https://music.163.com/api/song/detail?ids=[{Uri.EscapeDataString(id)}]");
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            if (!document.RootElement.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return null;
            NeteaseSongDetails? found = null;
            foreach (var song in songs.EnumerateArray())
                if ((found = NeteaseSongDetails.Parse(song, id)) is not null) break;
            if (found is not null) lock (_gate) { _cache[id] = found; }
            return found;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
        finally { lock (_gate) _pending.Remove(id); }
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _cache.Clear(); }
        _http.Dispose();
    }
}
