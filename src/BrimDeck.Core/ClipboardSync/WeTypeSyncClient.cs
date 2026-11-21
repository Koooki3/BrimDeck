using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace BrimDeck.Core.ClipboardSync;

public sealed class WeTypeIdentity
{
    public string DeviceName { get; set; } = "BrimDeck " + Environment.MachineName;
    // This device's name as the paired devices show it; another device may have renamed it.
    public string DisplayName { get; set; } = "";
    public List<string> PeerNames { get; set; } = [];
    public string PrivateKey { get; set; } = "";
    public string DeviceCode { get; set; } = CreateDeviceCode();
    public string Uuid { get; set; } = Guid.NewGuid().ToString();
    public string ServerKey { get; set; } = "";
    public ulong Uin { get; set; }
    public bool Paired { get; set; }
    public static string CreateDeviceCode()
    {
        string prefix = "AND" + "BrimDeck;Windows;standalone"[..17] + Guid.NewGuid().ToString("N")[..12];
        return prefix + Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(prefix + "aI0wC6qT1nB3nN5r")));
    }
}

// The server answered, and this device is no longer in a group with another one: a paired device removed it.
public sealed class WeTypeUnpairedException() : IOException("WeType is not paired.");

// Independently implemented control client. Wire fields and protocol constants were checked
// against mkdir700/wx-ime-sdk; no external executable or SDK is loaded.
public sealed partial class WeTypeSyncClient(WeTypeIdentity identity, Action save) : IClipboardSyncClient
{
    private const string Version = "3.5.4.57423";
    private string Name => identity.DeviceName;
    private readonly HttpClient _http = SyncTransport.CreateHttp();
    private readonly HttpClient _objects = SyncTransport.CreateHttp(60);
    public bool SupportsImages => true;
    // Says what the service answered, without any content, for the sync log.
    public Action<string>? Trace { get; set; }
    private string _listed = "";
    // The other devices' lists show this one as a phone, and nothing in the requests changes that: the kind there (1; WeType
    // for Windows has 6) follows the identity the requests are signed with, which is a phone's. Saying 6 in the pairing's
    // description of the device, and then in every request, was accepted and left the list as it was (tried on 2026-11-16).
    private byte[]? _key;
    private long _offset;
    private uint _task = 1;
    private ulong _group;
    private HashSet<ulong> _members = [];
    private static readonly ECDomainParameters Domain = new(SecNamedCurves.GetByName("secp128r1"));
    private string DeviceInfo => "BrimDeck;Windows;standalone;BrimDeck;sdk 34;PHONE";
    private long Stamp => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _offset;

    public static byte[] Crypt(byte[] key, byte[] bytes, bool encrypt)
    {
        using var aes = Aes.Create(); aes.Key = key;
        return encrypt ? aes.EncryptEcb(bytes, PaddingMode.PKCS7) : aes.DecryptEcb(bytes, PaddingMode.PKCS7);
    }
    private static void Success(WeTypeWire value, int field = 1)
    {
        if (!value.Has(field) || value.GetNumber(field) != 0) throw new InvalidDataException("WeType rejected the request (" + value.GetNumber(field) + ").");
    }
    public async Task ConnectAsync(CancellationToken cancellation)
    {
        await InitializeAsync(cancellation);
        await GroupAsync(cancellation);
    }
    private async Task InitializeAsync(CancellationToken ct)
    {
        long start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var clock = await PacketAsync("/timestamp", 0, [], null, 0, "", ct);
        ulong serverTime = clock.GetNumber(1);
        if (serverTime == 0 || serverTime > long.MaxValue) throw new InvalidDataException("Missing server time.");
        _offset = (long)serverTime - (start + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 2;
        if (identity.PrivateKey.Length == 0)
        {
            identity.PrivateKey = Convert.ToHexString(BigIntegers.AsUnsignedByteArray(16, BigIntegers.CreateRandomInRange(BigInteger.One, Domain.N.Subtract(BigInteger.One), new SecureRandom())));
            save();
        }
        var scalar = new BigInteger(1, Convert.FromHexString(identity.PrivateKey));
        if (scalar.SignValue <= 0 || scalar.CompareTo(Domain.N) >= 0) throw new InvalidDataException("Invalid local identity.");
        var publicKey = Convert.ToHexString(Domain.G.Multiply(scalar).Normalize().GetEncoded(false));
        var body = new WeTypeWire().Text(1, publicKey).Text(2, identity.ServerKey).Number(3, identity.Uin == 0 ? 1UL : 0)
            .Text(4, identity.DeviceCode).Text(7, "").Text(8, DeviceInfo).Text(9, Name);
        if (identity.Uin == 0) body.Text(6, ""); else body.Number(5, identity.Uin);
        var handshake = await PacketAsync("/oauth_pubkey_v2", 0, body.Encode(), Encoding.ASCII.GetBytes("G7E1C8B0D7D7E4L3J2U9D6W0B2M8Q6O5"), 2147483646, "", ct);
        Success(handshake);
        var pointBytes = Convert.FromHexString(handshake.GetText(2));
        if (pointBytes.Length != 33 || pointBytes[0] != 4) throw new InvalidDataException("Invalid peer public key.");
        var point = Domain.Curve.DecodePoint(pointBytes);
        if (point.IsInfinity || !point.IsValid()) throw new InvalidDataException("Invalid peer point.");
        var agreement = new ECDHBasicAgreement(); agreement.Init(new ECPrivateKeyParameters(scalar, Domain));
        byte[] key = Encoding.ASCII.GetBytes(Convert.ToHexString(BigIntegers.AsUnsignedByteArray(16, agreement.CalculateAgreement(new ECPublicKeyParameters(point, Domain)))));
        if (identity.Uin == 0)
        {
            if (handshake.GetText(4).Length == 0) throw new InvalidDataException("Missing registration token.");
            var registration = await PacketAsync("/gen_uin_v2", 0,
                new WeTypeWire().Text(1, identity.DeviceCode).Text(2, handshake.GetText(2)).Text(4, identity.Uuid).Text(5, "").Encode(), key, 2147483645, handshake.GetText(4), ct);
            Success(registration); identity.Uin = registration.GetNumber(2);
            if (identity.Uin == 0) throw new InvalidDataException("Missing device identifier.");
        }
        identity.ServerKey = handshake.GetText(2); _key = key; save();
    }
    public async Task PairAsync(string code, CancellationToken ct)
    {
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) throw new ArgumentException("A six-digit pairing code is required.");
        await InitializeAsync(ct);
        var joined = await ApiAsync(31, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Text(3, code)
            .Message(5, new WeTypeWire().Text(1, Name).Number(2, 1)), ct);
        Success(joined);
        string bind = joined.GetText(2), peerName = joined.GetMessage(4).GetText(1);
        if (bind.Length == 0 || peerName.Length == 0) throw new InvalidDataException("Missing pairing transaction.");
        try
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                var result = await ApiAsync(38, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Text(3, bind), ct);
                Success(result);
                if (!result.Has(2)) throw new InvalidDataException("Missing pairing status.");
                if (result.GetNumber(2) == 1)
                {
                    var group = await GroupAsync(ct);
                    if (!group.Messages(2).Any(d => d.GetNumber(1) != identity.Uin && d.GetMessage(2).GetText(1) == peerName)) throw new InvalidDataException("Pairing peer mismatch.");
                    identity.Paired = true; save(); return;
                }
                if (result.GetNumber(2) != 0) throw new InvalidDataException("Pairing was declined or expired.");
                await Task.Delay(1500, ct);
            }
            throw new TimeoutException("Pairing confirmation timed out.");
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await ApiAsync(33, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Text(4, bind), cleanup.Token); } catch { }
            throw;
        }
    }
    public async Task<string> CreatePairingCodeAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        var result = await ApiAsync(30, new WeTypeWire().Number(1, identity.Uin).Number(2, 1)
            .Message(3, new WeTypeWire().Text(1, Name).Number(2, 1)), ct);
        Success(result);
        string code = result.GetText(2);
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) throw new InvalidDataException("Invalid pairing code.");
        return code;
    }
    public async Task<(string BindId, string Name)?> PollPairingCodeAsync(string code, CancellationToken ct)
    {
        var result = await ApiAsync(40, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Text(3, code), ct);
        if (!result.Has(1)) throw new InvalidDataException("Missing pairing state.");
        if (result.GetNumber(1) == 0) return null;
        if (result.GetNumber(1) != 1) throw new InvalidDataException("Pairing code expired.");
        string bind = result.GetText(2), name = result.GetMessage(4).GetText(1);
        if (bind.Length == 0 || name.Length == 0) throw new InvalidDataException("Missing pairing peer.");
        return (bind, name);
    }
    public async Task ConfirmPairingAsync(string bind, string name, CancellationToken ct)
    {
        var result = await ApiAsync(32, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Text(3, bind), ct);
        Success(result);
        ulong peer = result.GetMessage(3).GetNumber(1);
        var group = await GroupAsync(ct);
        if (peer == 0 || peer == identity.Uin || result.GetNumber(2) != _group ||
            !group.Messages(2).Any(d => d.GetNumber(1) == peer && d.GetMessage(2).GetText(1) == name))
            throw new InvalidDataException("Pairing peer mismatch.");
        identity.Paired = true; save();
    }
    // Takes this device out of its group, so the other devices stop listing it. Command 35 with UnbindReq{1 uin, 2 platform,
    // 3 unbind_uin} was read from WeType for Windows 3.0.2.44, which sends it when a device is removed from My devices.
    public async Task LeaveGroupAsync(CancellationToken ct)
    {
        if (identity.Uin == 0) throw new WeTypeUnpairedException();
        await InitializeAsync(ct);
        Success(await ApiAsync(35, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Number(3, identity.Uin), ct));
    }
    public async Task ReleasePairingCodeAsync(string code, CancellationToken ct) =>
        _ = await ApiAsync(33, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Text(3, code), ct);
    private async Task<WeTypeWire> GroupAsync(CancellationToken ct, bool clipboard = false, bool fileInvites = false)
    {
        ulong filter = 1UL | (clipboard ? 2UL : 0UL) | (fileInvites ? 64UL : 0UL);
        var request = new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Number(3, filter);
        if (clipboard) request.Number(4, 0);
        var response = await ApiAsync(34, request, ct);
        // 10020 with no group: this device is in no group any more (checked against wx-ime-sdk's session.rs).
        if (response.GetNumber(2) == 10020 && !response.Has(1)) throw new WeTypeUnpairedException();
        Success(response, 2);
        var group = response.GetMessage(1);
        var devices = group.Messages(2).Select(d => (Uin: d.GetNumber(1), Name: d.GetMessage(2).GetText(1))).ToList();
        _group = group.GetNumber(1); _members = devices.Select(d => d.Uin).ToHashSet();
        if (_group == 0 || !_members.Contains(identity.Uin) || _members.Count < 2) throw new WeTypeUnpairedException();
        if (Trace is { } trace)
        {
            string listed = string.Join("; ", group.Messages(2).Select(d => (d.GetNumber(1) == identity.Uin ? "self " : "") + "\"" + d.GetMessage(2).GetText(1) + "\" platform=" +
                d.GetMessage(2).GetNumber(2) + " version=" + d.GetMessage(2).GetText(4)));
            if (listed != _listed) { _listed = listed; trace("wetype devices: " + listed); }
        }
        string display = devices.First(d => d.Uin == identity.Uin).Name;
        var peers = devices.Where(d => d.Uin != identity.Uin && d.Name.Length > 0).Select(d => d.Name).ToList();
        if (display.Length > 0 && identity.DisplayName != display || !identity.PeerNames.SequenceEqual(peers))
        {
            if (display.Length > 0) identity.DisplayName = display;
            identity.PeerNames = peers; save();
        }
        return group;
    }
    // Reads the names again: another device may have renamed this one, or joined or left.
    public async Task RefreshNamesAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        await GroupAsync(ct);
    }
    public Task SendAsync(string text, CancellationToken ct) => PublishAsync(1, SyncTransport.TextBytes(text), ct);
    public async Task SendImageAsync(byte[] image, int width, int height, CancellationToken ct)
    {
        if (image.Length == 0 || !SyncTransport.IsPicture(image)) throw new InvalidDataException("A PNG or JPEG picture is required.");
        if (image.Length > SyncTransport.MaxImageBytes) throw new SyncImageTooLargeException();
        await GroupAsync(ct);
        ulong group = _group;
        var place = await ApiAsync(60, new WeTypeWire().Number(1, 6).Text(2, "{\"log_fully_custom_path\":\"" + Stamp + "\"}").Number(3, 3), ct);
        ulong limit = place.GetNumber(6);
        if (limit == 0) throw new InvalidDataException("Missing upload limit.");
        if ((ulong)image.Length > limit * 1024) throw new SyncImageTooLargeException();
        var upload = WeTypeObjects.CheckUrl(place.GetText(5));
        string download = place.GetText(4);
        WeTypeObjects.CheckUrl(download);
        byte[] key = RandomNumberGenerator.GetBytes(16);
        using (var request = new HttpRequestMessage(HttpMethod.Put, upload) { Content = new ByteArrayContent(WeTypeObjects.Crypt(key, image)) })
        {
            request.Content.Headers.ContentType = new("application/octet-stream");
            using var stored = await _objects.SendAsync(request, ct);
            if ((int)stored.StatusCode is not (200 or 201 or 204)) throw new HttpRequestException("The image was not stored (" + (int)stored.StatusCode + ").");
        }
        await PublishAsync(6, WeTypeObjects.ImageReference(download, image, key), ct, group);
    }
    private async Task PublishAsync(uint kind, byte[] bytes, CancellationToken ct, ulong expected = 0)
    {
        await GroupAsync(ct);
        if (expected != 0 && _group != expected) throw new InvalidDataException("The paired devices changed during the upload.");
        var response = await ApiAsync(36, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Number(4, _group)
            .Message(3, new WeTypeWire().Number(2, kind).Bytes(6, bytes).Text(9, Guid.NewGuid().ToString("N"))), ct);
        Success(response);
        if (response.GetNumber(2) == 0) throw new InvalidDataException("Missing send receipt.");
    }
    private async Task<(string Id, WeTypeWire Clip)?> LatestAsync(CancellationToken ct)
    {
        var group = await GroupAsync(ct, true, FileInviteChanged != null);
        FileInviteChanged?.Invoke(ReadIncomingFile(group));
        var clip = group.GetMessage(3);
        ulong sender = clip.GetNumber(7), version = clip.GetNumber(1), expiry = clip.GetNumber(4);
        if (version == 0 || sender == identity.Uin || expiry != 0 && expiry <= (ulong)(Stamp / 1000)) return null;
        if (!_members.Contains(sender)) throw new InvalidDataException("Unknown clipboard sender.");
        return ($"{_group}:{version}", clip);
    }
    public async Task<string?> PeekAsync(CancellationToken ct) => (await LatestAsync(ct))?.Id;
    public async Task<RemoteClip?> ReceiveAsync(string? known, CancellationToken ct)
    {
        if (await LatestAsync(ct) is not var (id, clip) || id == known) return null;
        var bytes = clip.GetBytes(6);
        if (bytes.Length == 0) return null;
        // 1: text. 2: long text stored as an object. 6: a picture stored as an encrypted object. Other kinds are not synced.
        switch (clip.GetNumber(2))
        {
            case 1:
                return bytes.Length > SyncTransport.MaxRemoteTextBytes ? null : new RemoteClip(id, SyncTransport.Utf8.GetString(bytes));
            case 2:
                var text = await ObjectAsync(WeTypeObjects.CheckUrl(SyncTransport.Utf8.GetString(bytes)), SyncTransport.MaxRemoteTextBytes, ct);
                return new RemoteClip(id, SyncTransport.Utf8.GetString(text));
            case 6:
                var (url, md5, key) = WeTypeObjects.ParseImageReference(bytes);
                var picture = WeTypeObjects.OpenImage(await ObjectAsync(url, SyncTransport.MaxImageBytes, ct), md5, key);
                if (!SyncTransport.IsPicture(picture)) throw new InvalidDataException("Unsupported image format.");
                return new RemoteClip(id, "", picture);
            default:
                return null;
        }
    }
    private async Task<byte[]> ObjectAsync(Uri url, int limit, CancellationToken ct)
    {
        using var response = await _objects.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        var bytes = await SyncTransport.ReadAsync(response, ct, limit);
        return bytes.Length == 0 ? throw new InvalidDataException("Empty object.") : bytes;
    }
    private async Task<WeTypeWire> ApiAsync(uint command, WeTypeWire body, CancellationToken ct)
    {
        if (_key is null) throw new InvalidOperationException("Connect first.");
        var headers = new WeTypeWire();
        foreach (var pair in new[] { ("vid", identity.Uin.ToString()), ("app_ver", Version), ("platform", "1"), ("device", DeviceInfo), ("channelid", ""), ("client_timestamp", Stamp.ToString()) })
            headers.Message(1, new WeTypeWire().Text(1, pair.Item1).Text(2, pair.Item2));
        var envelope = new WeTypeWire().Number(2, identity.Uin).Number(3, command).Message(4, headers).Message(5, body);
        var response = await PacketAsync("/api_v2", command, envelope.Encode(), _key, _task++, "", ct);
        if (!response.Has(6)) throw new InvalidDataException("Missing response body.");
        return response.GetMessage(6);
    }
    private async Task<WeTypeWire> PacketAsync(string path, uint command, byte[] plain, byte[]? key, uint task, string token, CancellationToken ct)
    {
        var bytes = key is null ? plain : Crypt(key, plain, true);
        var headers = new Dictionary<string, string> {
            ["Kb-OsType"]="1", ["Kb-Version"]=Version, ["Kb-SystemVersion"]="34", ["Kb-PackageType"]="2", ["Kb-TimeStamp"]=Stamp.ToString(),
            ["Kb-BodyMd5"]=Convert.ToHexString(MD5.HashData(bytes)), ["Kb-TraceId"]=Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            ["Kb-TaskId"]=task.ToString(), ["Kb-Scene"]="1", ["Kb-CompressionType"]="1", ["Use_DebugNet"]="0" };
        var order = new List<string> { "Kb-OsType", "Kb-Version", "Kb-Scene" };
        if (path == "/oauth_pubkey_v2") { headers["Kb-Uin"] = identity.Uin.ToString(); headers["Kb-DeviceCodeRestrictionV2"] = "1"; }
        if (path == "/gen_uin_v2") { headers["Kb-GenUinToken"] = token; headers["Kb-DeviceCodeRestrictionV2"] = "1"; }
        if (path == "/api_v2")
        {
            headers["Kb-Uin"] = identity.Uin.ToString(); headers["Kb-CmdId"] = "4814"; headers["Kb-SubCmdId"] = command.ToString();
            headers["Kb-SharedKeySuffix"] = Encoding.ASCII.GetString(key![28..]); order.AddRange(["Kb-CmdId", "Kb-SubCmdId"]);
        }
        order.AddRange(["Kb-TimeStamp", "Kb-BodyMd5"]);
        if (path == "/gen_uin_v2") order.Add("Kb-GenUinToken");
        if (path == "/api_v2") order.Add("Kb-Uin");
        order.AddRange(["Kb-TraceId", "Kb-TaskId"]);
        if (path != "/timestamp") headers["Kb-Sign"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(order.Select(k => headers[k])) + "aI0wC6qT1nB3nN5r")));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://wetype.weixin.qq.com" + path) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/octet-stream");
        foreach (var pair in headers) request.Headers.Add(pair.Key, pair.Value);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        bytes = await SyncTransport.ReadAsync(response, ct);
        if (key is not null) bytes = Crypt(key, bytes, false);
        var compression = response.Headers.TryGetValues("Kb-CompressionType", out var values) ? values.FirstOrDefault() : "1";
        if (compression == "2") bytes = WeTypeWire.Unsnappy(bytes);
        else if (compression is not ("1" or null or "")) throw new InvalidDataException("Unsupported packet compression.");
        return WeTypeWire.Parse(bytes);
    }
    public void Dispose() { _http.Dispose(); _objects.Dispose(); if (_key is not null) CryptographicOperations.ZeroMemory(_key); }
}
