using System.Net.Http;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace BrimDeck.Core.ClipboardSync;

public sealed class DoubaoIdentity
{
    public string DeviceName { get; set; } = "BrimDeck " + Environment.MachineName;
    public string DeviceId { get; set; } = "";
    public Dictionary<string, string> Cookies { get; set; } = [];
}

public sealed class DoubaoDeviceUnavailableException(int syncState, bool supportsCopy) : Exception("The current Doubao device is not ready for clipboard sync.")
{
    public int SyncState { get; } = syncState;
    public bool SupportsCopy { get; } = supportsCopy;
}

public sealed class DoubaoRequestException(int code) : Exception($"Doubao clipboard request failed (code {code}).")
{
    public int Code { get; } = code;
}

public sealed record DoubaoDeviceState(int SyncState, bool SupportsCopy)
{
    public bool CanSync => SyncState == 1 && SupportsCopy;

    public static DoubaoDeviceState ParseCurrent(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid device list.");
        var current = data.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("is_current_device", out var own) && own.ValueKind == JsonValueKind.True).ToArray();
        if (current.Length != 1) throw new InvalidDataException("Missing or ambiguous current Doubao device.");
        if (!current[0].TryGetProperty("sync_enabled", out var state) || state.ValueKind != JsonValueKind.Number || !state.TryGetInt32(out int syncState) ||
            !current[0].TryGetProperty("is_support_copy", out var supported) || supported.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Missing current Doubao device capabilities.");
        return new(syncState, supported.GetBoolean());
    }
}

// One of the devices the account lists.
public sealed record DoubaoDevice(string Id, string Name, bool Current);

public sealed class DoubaoSyncClient(DoubaoIdentity identity) : IClipboardSyncClient
{
    private readonly HttpClient _http = SyncTransport.CreateHttp();
    // The official client's limit for one picture.
    public const int MaxImageBytes = 10 * 1024 * 1024;
    // The picture hosts the official client sends the account's cookies to.
    private static readonly string[] CookieHosts = ["ime.doubao.com", "p0-ime-clip-private.byteimg.com", "p0-ime-clip2-private.byteimg.com"];
    public bool SupportsImages => true;
    // The values the official Windows client (1.0.0.23) sends, read from its program: 1 as it signs in, 2 as it clears the account.
    private const string SignedOut = "2";
    // Says what the service answered, without any content, for the sync log.
    public Action<string>? Trace { get; set; }
    // The picture travels inside the message itself when it is no larger than the service's threshold for that. A larger one
    // would go through the service's separate upload, which is not implemented: it is reported as too large, with the threshold.
    public async Task SendImageAsync(byte[] image, int width, int height, CancellationToken cancellation)
    {
        string type = image.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]) ? "image/png"
            : image.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? "image/jpeg" : throw new InvalidDataException("A PNG or JPEG picture is required.");
        if (image.Length > MaxImageBytes) throw new SyncImageTooLargeException();
        var token = await RequestAsync("/api/v2/clipboard/image/token", null, "{}"u8.ToArray(), cancellation, "application/json", false);
        long threshold = token.ValueKind == JsonValueKind.Object && token.TryGetProperty("upload_threshold", out var value) ? Number(value) : 0;
        Trace?.Invoke("doubao image token: upload_threshold=" + threshold + " image=" + image.Length);
        if (image.Length > threshold) throw new SyncImageTooLargeException(threshold);
        var data = await RequestAsync("/api/v2/stream/clipboard/rich/copy", null, ImageMessage(image, type, width, height), cancellation, "application/json", true);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("version_seq", out var version) || Number(version) <= 0) throw new InvalidDataException("Missing send receipt.");
    }
    private static long Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number
        : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : 0;
    public static byte[] ImageMessage(byte[] image, string type, int width, int height)
    {
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("payload_type", 3);
            json.WriteStartObject("content");
            json.WriteString("plain_content", ""); json.WriteString("formatted_content", "");
            json.WriteStartArray("images");
            json.WriteStartObject();
            json.WriteString("place_holder", ""); json.WriteString("uri", ""); json.WriteBase64String("payload", image);
            json.WriteString("mime_type", type); json.WriteNumber("width", width); json.WriteNumber("height", height); json.WriteNumber("file_size", image.Length);
            json.WriteEndObject();
            json.WriteEndArray(); json.WriteEndObject(); json.WriteEndObject();
        }
        return output.ToArray();
    }
    public async Task ConnectAsync(CancellationToken cancellation)
    {
        var state = await ReadCurrentDeviceAsync(cancellation);
        if (!state.CanSync) throw new DoubaoDeviceUnavailableException(state.SyncState, state.SupportsCopy);
    }
    public async Task<DoubaoDeviceState> ReadCurrentDeviceAsync(CancellationToken cancellation)
    {
        if (identity.DeviceId.Length == 0 || !identity.Cookies.ContainsKey("sessionid")) throw new InvalidDataException("Sign in to Doubao first.");
        var data = await RequestAsync("/api/v2/clipboard/config", null, null, cancellation);
        return DoubaoDeviceState.ParseCurrent(data);
    }
    // Turns this device's own switch on or off among the account's devices (the request's form checked against doubridge,
    // whose author ran it against the service: 1 is on, 2 off). The device's id and kind go back as the service listed them.
    public Task<DoubaoDeviceState> EnableAsync(CancellationToken cancellation) => SwitchAsync(true, cancellation);
    public async Task<DoubaoDeviceState> SwitchAsync(bool on, CancellationToken cancellation)
    {
        var devices = await RequestAsync("/api/v2/clipboard/config", null, null, cancellation);
        DoubaoDeviceState.ParseCurrent(devices);
        var own = devices.EnumerateArray().Single(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("is_current_device", out var current) && current.ValueKind == JsonValueKind.True);
        if (!own.TryGetProperty("device_id", out var id) || !own.TryGetProperty("device_type", out var kind)) throw new InvalidDataException("Missing current Doubao device.");
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            json.WriteStartArray("switches");
            json.WriteStartObject();
            json.WritePropertyName("target_device_id"); id.WriteTo(json);
            json.WriteNumber("sync_enabled", on ? 1 : 2);
            json.WritePropertyName("device_type"); kind.WriteTo(json);
            json.WriteString("device_name", identity.DeviceName);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteNumber("client_modify_timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            json.WriteEndObject();
        }
        await RequestAsync("/api/v2/clipboard/switch", null, output.ToArray(), cancellation, "application/json", false);
        return await ReadCurrentDeviceAsync(cancellation);
    }
    // Tells the service whether this device is signed in to the account (1) or has signed out (SignedOut): signed out, the
    // account's other devices stop listing it.
    public Task ReportSignInAsync(bool signedIn, CancellationToken cancellation) =>
        RequestAsync("/api/v2/auth/sync", new() { ["status"] = signedIn ? "1" : SignedOut }, [], cancellation, "", false);
    public async Task<IReadOnlyList<DoubaoDevice>> ListDevicesAsync(CancellationToken cancellation)
    {
        var data = await RequestAsync("/api/v2/clipboard/config", null, null, cancellation);
        if (data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid device list.");
        return [.. data.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("device_id", out _))
            .Select(d => new DoubaoDevice(d.GetProperty("device_id").ToString(),
                d.TryGetProperty("device_name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "",
                d.TryGetProperty("is_current_device", out var current) && current.ValueKind == JsonValueKind.True))];
    }
    // Tells the service that another device of the account has signed out, in that device's name: for one whose own
    // sign-in was lost (a reinstall, a test) and which can no longer say so itself.
    public Task RemoveDeviceAsync(DoubaoDevice device, CancellationToken cancellation) =>
        RequestAsync("/api/v2/auth/sync", new() { ["status"] = SignedOut, ["device_id"] = device.Id, ["device_name"] = device.Name }, [], cancellation, "", false);
    public const int MaxHtmlChars = 512 * 1024;
    // Text with its formatting: the plain text and the copied HTML fragment, as the official clients send a rich copy
    // (the message's form read from the official Windows client's program).
    public async Task SendRichAsync(string text, string html, CancellationToken cancellation)
    {
        SyncTransport.TextBytes(text);
        if (html.Length is 0 or > MaxHtmlChars) throw new InvalidDataException("Unsupported formatted text.");
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("payload_type", 2);
            json.WriteStartObject("content");
            json.WriteString("plain_content", text); json.WriteString("formatted_content", html);
            json.WriteStartArray("images"); json.WriteEndArray();
            json.WriteEndObject(); json.WriteEndObject();
        }
        var data = await RequestAsync("/api/v2/stream/clipboard/rich/copy", null, output.ToArray(), cancellation, "application/json", true);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("version_seq", out var version) || Number(version) <= 0) throw new InvalidDataException("Missing send receipt.");
    }
    public async Task SendAsync(string text, CancellationToken cancellation)
    {
        var bytes = SyncTransport.TextBytes(text);
        using var compressor = new Compressor(3);
        var data = await RequestAsync("/api/v2/stream/clipboard/text/copy", new() { ["text_length"] = text.EnumerateRunes().Count().ToString() }, compressor.Wrap(bytes).ToArray(), cancellation);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("version_seq", out var version) || Number(version) <= 0) throw new InvalidDataException("Missing send receipt.");
    }
    // The latest content another device sent, as the server describes it; null when there is none.
    private async Task<JsonElement?> LatestAsync(CancellationToken cancellation)
    {
        var data = await RequestAsync("/api/v2/clipboard/pull", new() { ["start_version_seq"] = "0" }, null, cancellation);
        if (!data.TryGetProperty("has_content", out var has) || has.ValueKind != JsonValueKind.True) return null;
        if (!data.TryGetProperty("from_device_id", out var sender) || sender.ToString() == identity.DeviceId) return null;
        return data.TryGetProperty("version_seq", out _) ? data : null;
    }
    public async Task<string?> PeekAsync(CancellationToken cancellation) => (await LatestAsync(cancellation))?.GetProperty("version_seq").ToString();
    public async Task<RemoteClip?> ReceiveAsync(string? known, CancellationToken cancellation)
    {
        if (await LatestAsync(cancellation) is not { } data) return null;
        string id = data.GetProperty("version_seq").ToString();
        if (id == known) return null;
        if (Trace is { } trace)
        {
            // The message's shape only: field names, kinds and lengths, and the host of an address.
            static string Shape(JsonElement value) => value.ValueKind switch
            {
                JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().Select(p => p.Name + ":" + Shape(p.Value))) + "}",
                JsonValueKind.Array => "[" + value.GetArrayLength() + (value.GetArrayLength() > 0 ? " " + Shape(value[0]) : "") + "]",
                JsonValueKind.String => value.GetString() is { } text && Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme.StartsWith("http") ? "url(" + url.Host + ")" : "str" + value.GetString()!.Length,
                _ => value.ValueKind.ToString()
            };
            trace("doubao pull " + id + ": " + Shape(data));
        }
        if (ReadImage(data) is { } picture)
        {
            var bytes = picture.Inline ?? await DownloadAsync(picture.Url!, cancellation);
            return bytes.Length is 0 or > MaxImageBytes ? null : new RemoteClip(id, "", bytes);
        }
        return ReadText(data) is { } text ? new RemoteClip(id, text, null, ReadHtml(data)) : null;
    }
    // The formatting of a rich copy, when it comes inside the message; one stored elsewhere is left out, and the text comes plain.
    public static string ReadHtml(JsonElement data) =>
        data.TryGetProperty("rich_text_payload", out var rich) && rich.ValueKind == JsonValueKind.Object &&
        rich.TryGetProperty("formatted_content", out var html) && html.ValueKind == JsonValueKind.String && html.GetString() is { Length: > 0 and <= 2 * MaxHtmlChars } value ? value : "";
    // A copied picture: the first of the message's pictures, inside the message as Base64 (alone or as a data: address) or
    // at an address to download. The thumbnail and icon the service adds are not used.
    public static (byte[]? Inline, Uri? Url)? ReadImage(JsonElement data)
    {
        if (!data.TryGetProperty("image_payload", out var pictures) || pictures.ValueKind != JsonValueKind.Array || pictures.GetArrayLength() == 0) return null;
        var first = pictures[0];
        if (first.ValueKind != JsonValueKind.Object) return null;
        string Text(string name) => first.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        string inline = Text("original_image_payload") is { Length: > 0 } original ? original : Text("payload");
        if (inline.Length > 0)
        {
            if (inline.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) inline = inline[(inline.IndexOf(',') + 1)..];
            if (inline.Length > MaxImageBytes / 3 * 4 + 4) throw new InvalidDataException("Image payload too large.");
            try { return (Convert.FromBase64String(inline), null); }
            catch (FormatException ex) { throw new InvalidDataException("Invalid image payload.", ex); }
        }
        string address = Text("original_url") is { Length: > 0 } url ? url : Text("uri");
        return Uri.TryCreate(address, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps && parsed.UserInfo.Length == 0 ? (null, parsed) : null;
    }
    private async Task<byte[]> DownloadAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (url.Port == 443 && CookieHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase)) request.Headers.Add("Cookie", CookieHeader);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await SyncTransport.ReadAsync(response, ct, MaxImageBytes);
    }
    private string CookieHeader => string.Join("; ", identity.Cookies.Where(p => p.Key is "sessionid" or "sessionid_ss" or "passport_csrf_token" or "passport_csrf_token_default").Select(p => p.Key + "=" + p.Value));
    // Plain text is Base64 of Zstandard of UTF-8. A phone's copy usually comes as rich text, whose plain form is the text itself;
    // its formatted form and the pictures inside it are left out.
    public static string? ReadText(JsonElement data)
    {
        if (data.TryGetProperty("rich_text_payload", out var rich) && rich.ValueKind == JsonValueKind.Object &&
            rich.TryGetProperty("plain_content", out var plain) && plain.ValueKind == JsonValueKind.String)
        {
            string content = plain.GetString()!;
            return content.Length == 0 || content.Length > SyncTransport.MaxRemoteTextBytes ? null : content;
        }
        if (!data.TryGetProperty("plain_text_payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
        if (payload.TryGetProperty("is_tos_load", out var external) && external.ValueKind == JsonValueKind.True) return null;
        if (!payload.TryGetProperty("plain_text", out var text) || text.ValueKind != JsonValueKind.String) return null;
        string value = text.GetString()!;
        if (value.Length > 2 * SyncTransport.MaxTextBytes) throw new InvalidDataException("Text payload too large.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException ex) { throw new InvalidDataException("Invalid text payload.", ex); }
        using var input = new MemoryStream(bytes);
        using var decoder = new DecompressionStream(input);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = decoder.Read(buffer)) > 0)
        {
            if (output.Length + count > SyncTransport.MaxTextBytes) throw new InvalidDataException("Decoded text too large.");
            output.Write(buffer, 0, count);
        }
        return output.Length == 0 ? null : SyncTransport.Utf8.GetString(output.ToArray());
    }
    // stamped: the copy requests also say when the copy was made.
    private async Task<JsonElement> RequestAsync(string path, Dictionary<string, string>? extra, byte[]? body, CancellationToken ct,
        string contentType = "application/octet-stream", bool stamped = true)
    {
        var query = new Dictionary<string, string> { ["device_name"] = identity.DeviceName, ["device_id"] = identity.DeviceId, ["x_device_type"] = "3", ["aid"] = "685343", ["device_platform"] = "windows", ["device_type"] = "windows", 
            // What the official Windows client 1.0.0.23 reports. A device reporting an older version code is told to update
            // on the account's other devices and cannot be turned on there.
            ["os"] = "windows", ["channel"] = "release", ["version_name"] = "1.0.0", ["version_code"] = "1000023", ["update_version_code"] = "1000023" };
        if (extra is not null) foreach (var pair in extra) query[pair.Key] = pair.Value;
        string url = "https://ime.doubao.com" + path + "?" + string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.Add("Cookie", CookieHeader);
        request.Headers.Add("X-Device-Type", "3");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body); if (contentType.Length > 0) request.Content.Headers.ContentType = new(contentType);
            if (stamped)
            {
                string now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                request.Headers.Add("copy-clipboard-updated-time", now); request.Headers.Add("client-copy-req-time", now);
            }
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // A pulled picture may come inside the answer itself.
        var raw = await SyncTransport.ReadAsync(response, ct, 24 * 1024 * 1024);
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int value))
            throw new InvalidDataException("Missing Doubao response code.");
        if (value != 0) throw new DoubaoRequestException(value);
        return document.RootElement.GetProperty("data").Clone();
    }
    public void Dispose() => _http.Dispose();
}
