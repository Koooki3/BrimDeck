using System.Net.Http;
using System.Text;

namespace BrimDeck.Core.ClipboardSync;

// What another device copied: text, with its HTML fragment when the service carries formatting, or an encoded picture
// (PNG or JPEG) with Text empty.
public sealed record RemoteClip(string Id, string Text, byte[]? Image = null, string Html = "");

public interface IClipboardSyncClient : IDisposable
{
    bool SupportsImages { get; }
    Task ConnectAsync(CancellationToken cancellation);
    Task SendAsync(string text, CancellationToken cancellation);
    // image: an encoded PNG.
    Task SendImageAsync(byte[] image, int width, int height, CancellationToken cancellation);
    // The id of the latest content another device sent, without downloading it; null when there is none.
    Task<string?> PeekAsync(CancellationToken cancellation);
    // The latest content another device sent; null when there is none, it is the one already known, or its kind is not synced.
    Task<RemoteClip?> ReceiveAsync(string? known, CancellationToken cancellation);
}

// The picture is larger than the service takes. Limit: the size it would have taken, in bytes, when the service names one.
public sealed class SyncImageTooLargeException(long limit = 0) : IOException("The image exceeds the service's size limit.")
{
    public long Limit { get; } = limit;
}

public static class SyncTransport
{
    public const int MaxTextBytes = 256 * 1024;
    // Text another device stored as an object rather than inline may be this long.
    public const int MaxRemoteTextBytes = 1024 * 1024;
    public const int MaxImageBytes = 16 * 1024 * 1024;
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static HttpClient CreateHttp(int seconds = 20) => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(seconds) };
    public static byte[] TextBytes(string text)
    {
        var bytes = Utf8.GetBytes(text);
        if (bytes.Length is 0 or > MaxTextBytes) throw new InvalidDataException("Text must contain 1–262144 UTF-8 bytes.");
        return bytes;
    }
    // PNG or JPEG, told by the first bytes: the two forms the services carry as pictures.
    public static bool IsPicture(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]);
    public static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken ct, int limit = 2 * 1024 * 1024)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Response is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
