using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

public sealed partial class LyricsService
{
    // Anonymous equivalent of the SDK lyric_new operation, requesting unencrypted JSON responses.
    private async Task<Lyrics> FindNeteaseNew(string id, double duration, CancellationToken token)
    {
        const string uri = "/api/song/lyric/v1";
        var json = JsonSerializer.Serialize(new { id, cp = false, tv = 0, lv = 0, rv = 0, kv = 0, yv = 0, ytv = 0, yrv = 0, e_r = false });
        var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"nobody{uri}use{json}md5forencrypt"))).ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes("e82ckenh8dichen8");
        var encrypted = aes.EncryptEcb(Encoding.UTF8.GetBytes($"{uri}-36cd479b6b5-{json}-36cd479b6b5-{digest}"), PaddingMode.PKCS7);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://interface.music.163.com/eapi/song/lyric/v1");
        request.Headers.UserAgent.ParseAdd("NeteaseMusic/9.0.0");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["params"] = Convert.ToHexString(encrypted) });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return Lyrics.Empty;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await ReadLimitedAsync(stream, timeout.Token);
            if (bytes is null) return Lyrics.Empty;
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Lyrics.Empty;
            return NeteaseLyric(document.RootElement, duration);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            return Lyrics.Empty;
        }
    }
}
