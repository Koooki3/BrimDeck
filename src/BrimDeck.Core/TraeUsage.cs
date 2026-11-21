using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

// Trae (trae.ai, ProviderId.Trae) and Trae CN (trae.com.cn / trae.cn, ProviderId.TraeCn) are separate ByteDance AI IDEs
// with independent accounts and data folders under %APPDATA% (Trae / TRAE SOLO, and Trae CN / TRAE SOLO CN). Everything read
// lives in the current user's AppData folder, independent of where the app is installed.
//
// Sign-in. Each app persists its auth payload in User\globalStorage\storage.json under "iCubeAuthInfo://icube.cloudide",
// encoded with byteCrypto.js ("tc\x05\x10\x00\x00" + 32-byte salt + AES-128-CBC(SHA-512(plain) || plain), base64; older
// versions stored plain JSON). The payload holds { token, expiredAt, host, userId, account: { scope, ... } }. BrimDeck reads
// the token only and never refreshes or modifies it. SaaS (team/enterprise, account.scope == "saas") accounts do not use the
// personal entitlement usage endpoint and are reported as not yet supported.
//
// Quota. POST <host>/trae/api/v1/pay/ide_user_ent_usage {"require_usage":true} (international) or
// POST <host>/trae/api/v2/pay/ide_user_ent_usage {"require_usage":true,"req_source":1} (China) with
// Authorization: Cloud-IDE-JWT <token>. The token is sent only over HTTPS to trae.ai (international) or trae.cn (China).
//   - International (is_dollar_usage_billing): USD spend against basic_usage_limit (+ bonus_usage_limit when enabled), add-on
//     packages (product_type 2), and On-Demand PayGo (product_type 7).
//   - China (is_credits_billing): total credits across active entitlement packs (credits_limit / credits_amount), plus per-group
//     breakdowns (plan credits, monthly login credits, check-in rewards, welcome credits, add-on packs) when more than one exists.
//   - Legacy fallback: premium_model_fast_request_limit / premium_model_fast_amount when neither USD nor credits is present.
//
// Tokens. Both editions write every conversation turn to %APPDATA%\<folder>\ModularData\ai-agent\database.db (and its -wal),
// encrypted with SQLCipher 4 raw-key format (4096-byte pages, 80-byte reserved tail, AES-256-CBC, HMAC-SHA512, 2-iteration
// PBKDF2-HMAC-SHA512). BrimDeck replays committed WAL frames and decrypts pages in memory only, querying chat_turn via
// sqlite3_deserialize without writing any decrypted database to disk.
public static class TraeUsage
{
    public sealed record Edition(ProviderId Id, IReadOnlyList<string> Folders, string Host, string DefaultApi, string UsagePath, bool China);
    public static readonly Edition International = new(ProviderId.Trae, ["Trae", "TRAE SOLO"], "trae.ai", "https://api-sg-central.trae.ai", "/trae/api/v1/pay/ide_user_ent_usage", false);
    public static readonly Edition China = new(ProviderId.TraeCn, ["Trae CN", "TRAE SOLO CN"], "trae.cn", "https://api.trae.cn", "/trae/api/v2/pay/ide_user_ent_usage", true);
    public static Edition For(ProviderId id) => id == ProviderId.TraeCn ? China : International;

    public const string AuthKey = "iCubeAuthInfo://icube.cloudide";
    private const int PageSize = 4096, Reserved = 80;
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    // byteCrypto.js headers and XOR key halves (out-build/vs/base/common/byteCrypto.js).
    private static readonly byte[] AesHeader = [116, 99, 5, 16, 0, 0];
    private static readonly byte[] AesPrivateHeader = [18, 57, 32, 32, 2, 3];
    private static readonly byte[] Ope = [82, 9, 106, 213, 48, 54, 165, 56, 191, 64, 163, 158, 129, 243, 215, 251, 124, 227, 57, 130, 155, 47, 255, 135, 52, 142, 67, 68, 196, 222, 233, 203, 84, 123, 148, 50, 166, 194, 35, 61, 238, 76, 149, 11, 66, 250, 195, 78, 8, 46, 161, 102, 40, 217, 36, 178, 118, 91, 162, 73, 109, 139, 209, 37];
    private static readonly byte[] Ape = [31, 221, 168, 51, 136, 7, 199, 49, 177, 18, 16, 89, 39, 128, 236, 95, 96, 81, 127, 169, 25, 181, 74, 13, 45, 229, 122, 159, 147, 201, 156, 239, 160, 224, 59, 77, 174, 42, 245, 176, 200, 235, 187, 60, 131, 83, 153, 97, 23, 43, 4, 126, 186, 119, 214, 38, 225, 105, 20, 99, 85, 33, 12, 125];
    private static readonly byte[] Rpe = [191, 192, 216, 250, 122, 246, 220, 97, 31, 254, 98, 27, 8, 72, 71, 176, 135, 99, 96, 18, 127, 101, 203, 104, 211, 102, 191, 125, 37, 72, 150, 156, 51, 229, 121, 35, 17, 153, 141, 177, 110, 131, 150, 128, 172, 255, 254, 6, 18, 140, 55, 62, 236, 249, 135, 64, 135, 12, 117, 4, 89, 149, 168, 209];
    private static readonly byte[] Npe = [246, 204, 26, 232, 232, 70, 129, 109, 223, 146, 169, 242, 23, 241, 105, 145, 50, 196, 165, 42, 254, 120, 3, 54, 244, 207, 209, 85, 53, 6, 138, 106, 175, 148, 31, 204, 186, 186, 165, 182, 87, 142, 49, 10, 39, 110, 26, 154, 86, 56, 173, 125, 18, 64, 198, 225, 99, 99, 83, 82, 191, 134, 76, 170];
    // Trae's shared application SQLCipher 4 raw key for ModularData\ai-agent\database.db.
    private static readonly byte[] DbKey = Convert.FromHexString("3605f6691095a993f03d5009c918352ef5be31ae31e8f000212b81ff058da773");

    public static IReadOnlyList<string> DataFolders(string roaming, Edition edition) =>
        edition.Folders.Select(folder => Path.Combine(roaming, folder)).ToList();

    public static string StorageFile(string folder) => Path.Combine(folder, "User", "globalStorage", "storage.json");

    // The app's sign-in; never shown or logged.
    public sealed record SignIn(string Token, DateTimeOffset? ExpiresAt, string Api, string AccountId, bool Enterprise)
    {
        public bool Expired(DateTimeOffset now) => ExpiresAt is { } at && at <= now.AddMinutes(1);
        public override string ToString() => nameof(SignIn);
    }

    // Decrypts the base64 byteCrypto blob (or returns plain JSON as-is). Null when malformed or checksum fails.
    public static string? DecryptAuth(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.StartsWith('{')) return trimmed;
        byte[] raw;
        try { raw = Convert.FromBase64String(trimmed); }
        catch (FormatException) { return null; }
        if (raw.Length < 6 + 32 + 16 || (raw.Length - 38) % 16 != 0) return null;
        byte[] left, right;
        if (raw.AsSpan(0, 6).SequenceEqual(AesHeader)) { left = Ope; right = Ape; }
        else if (raw.AsSpan(0, 6).SequenceEqual(AesPrivateHeader)) { left = Rpe; right = Npe; }
        else return null;

        var buf = new byte[128];
        byte[]? plain = null;
        try
        {
            SHA512.HashData(raw.AsSpan(6, 32), buf.AsSpan(0, 64));
            for (int i = 0; i < 64; i++) buf[64 + i] = (byte)(left[i] ^ right[i]);
            var derived = SHA512.HashData(buf);
            using var aes = Aes.Create();
            aes.Key = derived[..16];
            aes.IV = derived[16..32];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            plain = decryptor.TransformFinalBlock(raw, 38, raw.Length - 38);
            if (plain.Length < 64) return null;
            var payload = plain.AsSpan(64);
            var digest = SHA512.HashData(payload);
            return CryptographicOperations.FixedTimeEquals(digest, plain.AsSpan(0, 64)) ? Encoding.UTF8.GetString(payload) : null;
        }
        catch (CryptographicException) { return null; }
        finally
        {
            CryptographicOperations.ZeroMemory(buf);
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    // Encrypts JSON into the standard "tc\x05\x10\x00\x00" byteCrypto format; used by tests.
    internal static string EncryptAuth(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var salt = RandomNumberGenerator.GetBytes(32);
        var buf = new byte[128];
        SHA512.HashData(salt, buf.AsSpan(0, 64));
        for (int i = 0; i < 64; i++) buf[64 + i] = (byte)(Ope[i] ^ Ape[i]);
        var derived = SHA512.HashData(buf);
        var plain = new byte[64 + payload.Length];
        SHA512.HashData(payload, plain.AsSpan(0, 64));
        payload.CopyTo(plain, 64);
        using var aes = Aes.Create();
        aes.Key = derived[..16];
        aes.IV = derived[16..32];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
        var output = new byte[6 + 32 + cipher.Length];
        AesHeader.CopyTo(output, 0);
        salt.CopyTo(output, 6);
        cipher.CopyTo(output, 38);
        return Convert.ToBase64String(output);
    }

    // Reads the most recently updated valid sign-in across the edition's data folders (Trae and TRAE SOLO, or Trae CN and TRAE SOLO CN).
    public static SignIn? ReadSignIn(string roaming, Edition edition)
    {
        var files = DataFolders(roaming, edition).Select(StorageFile).Where(File.Exists)
            .OrderByDescending(path => { try { return File.GetLastWriteTimeUtc(path); } catch (IOException) { return DateTime.MinValue; } });
        foreach (var file in files)
        {
            try
            {
                using var doc = SharedFile.ReadJson(file);
                if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var raw = doc.RootElement.Get(AuthKey).Text();
                if (raw.Length == 0) continue;
                if (ParseSignIn(DecryptAuth(raw), edition) is { } signIn) return signIn;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return null;
    }

    public static SignIn? ParseSignIn(string? json, Edition edition)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var token = root.Get("token").Text();
            if (token.Length == 0) return null;
            var scope = root.Get("account").Get("scope").Text();
            var userId = root.Get("userId").Text();
            return new(token, root.Get("expiredAt").Date(), Api(root.Get("host").Text(), edition), userId,
                scope.Equals("saas", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException) { return null; }
    }

    // The token is sent only to the edition's own hosts over HTTPS; any other origin falls back to its public API.
    public static string Api(string host, Edition edition) =>
        Uri.TryCreate(host, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/"
        && (uri.Host.Equals(edition.Host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + edition.Host, StringComparison.OrdinalIgnoreCase))
            ? uri.GetLeftPart(UriPartial.Authority) : edition.DefaultApi;

    public static string RequestBody(Edition edition) => edition.China ? """{"require_usage":true,"req_source":1}""" : """{"require_usage":true}""";

    public sealed record Reading(List<UsageMetric> Metrics, string Plan);

    // Product types from ICubeUsageService ($l enum).
    private const int ProductFree = 0, ProductPro = 1, ProductPackage = 2, ProductPromoCode = 3,
        ProductProPlus = 4, ProductUltra = 6, ProductPayGo = 7, ProductLite = 8, ProductCnExpress = 100;
    private static readonly int[] PlanPriority = [ProductUltra, ProductProPlus, ProductPro, ProductLite, ProductCnExpress, ProductFree];

    public static Reading Parse(JsonElement root, Edition edition)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        if (root.Get("code").Number() is { } code && code != 0)
            throw new InvalidDataException(root.Get("message").Text());
        var rawPacks = root.Get("user_entitlement_pack_list");
        if (rawPacks.ValueKind != JsonValueKind.Array) throw new InvalidDataException();

        var packs = rawPacks.Items()
            .Where(p => p.ValueKind == JsonValueKind.Object && p.Get("is_hide").ValueKind != JsonValueKind.True && ProductType(p) != ProductPromoCode)
            .ToList();
        if (packs.Count == 0) throw new InvalidDataException();

        var primary = PrimaryPack(packs);
        var plan = PlanName(primary);
        bool dollarBilling = root.Get("is_dollar_usage_billing").ValueKind == JsonValueKind.True;
        bool creditsBilling = root.Get("is_credits_billing").ValueKind == JsonValueKind.True;

        if (dollarBilling || !creditsBilling && !edition.China && HasDollarQuota(packs))
            return ParseDollar(packs, primary, plan);
        if (creditsBilling || HasCreditsQuota(packs))
            return ParseCredits(packs, primary, plan);
        return ParseLegacyFastRequests(packs, primary, plan);
    }

    private static Reading ParseDollar(List<JsonElement> packs, JsonElement? primary, string plan)
    {
        var metrics = new List<UsageMetric>();
        if (primary is { } main)
        {
            var quota = main.Get("entitlement_base_info").Get("quota");
            var usage = main.Get("usage");
            var reset = ResetTime(main);
            if (Decimal(quota.Get("basic_usage_limit")) is > 0 and var basicLimit)
            {
                var basicUsed = Math.Max(0m, Decimal(usage.Get("basic_usage_amount")) ?? 0m);
                metrics.Add(new UsageMetric(MetricKind.Spend, "套餐额度")
                {
                    Id = "plan", Currency = "USD", Total = Math.Max(basicLimit, basicUsed), Amount = basicUsed, ResetAt = reset
                });
            }
            bool noBonus = quota.Get("no_bonus_quota").ValueKind == JsonValueKind.True;
            if (!noBonus && Decimal(quota.Get("bonus_usage_limit")) is > 0 and var bonusLimit)
            {
                var bonusUsed = Math.Max(0m, Decimal(usage.Get("bonus_usage_amount")) ?? 0m);
                metrics.Add(new UsageMetric(MetricKind.Spend, "赠送额度")
                {
                    Id = "bonus", Currency = "USD", Total = Math.Max(bonusLimit, bonusUsed), Amount = bonusUsed, ResetAt = reset
                });
            }
        }
        // Add-on usage packages (product_type 2).
        decimal pkgTotal = 0, pkgUsed = 0;
        DateTimeOffset? pkgExpiry = null;
        foreach (var pack in packs.Where(p => ProductType(p) == ProductPackage))
        {
            if (Decimal(pack.Get("entitlement_base_info").Get("quota").Get("basic_usage_limit")) is > 0 and var limit)
            {
                pkgTotal += limit;
                pkgUsed += Math.Max(0m, Decimal(pack.Get("usage").Get("basic_usage_amount")) ?? 0m);
                pkgExpiry = Earlier(pkgExpiry, ExpiryTime(pack));
            }
        }
        if (pkgTotal > 0)
            metrics.Add(new UsageMetric(MetricKind.Spend, "加油包")
            {
                Id = "package", Currency = "USD", Total = Math.Max(pkgTotal, pkgUsed), Amount = pkgUsed, ExpiresAt = pkgExpiry
            });

        // Pay-as-you-go / On-Demand Usage (product_type 7).
        foreach (var payGo in packs.Where(p => ProductType(p) == ProductPayGo))
        {
            var amount = Math.Max(0m, Decimal(payGo.Get("usage").Get("pay_go_amount")) ?? 0m);
            metrics.Add(new UsageMetric(MetricKind.Spend, "按量计费")
            {
                Id = "paygo", Currency = "USD", Amount = amount, Unlimited = true
            });
            break;
        }
        if (metrics.Count == 0) throw new InvalidDataException();
        return new(metrics, plan);
    }

    private static Reading ParseCredits(List<JsonElement> packs, JsonElement? primary, string plan)
    {
        decimal total = 0, used = 0;
        bool unlimited = false, hasAny = false;
        var buckets = new List<(string Id, string Label, decimal Total, decimal Used, bool Unlimited, DateTimeOffset? Reset, DateTimeOffset? Expires)>();

        foreach (var pack in packs)
        {
            var limit = Decimal(pack.Get("entitlement_base_info").Get("quota").Get("credits_limit"));
            if (limit is null or 0) continue;
            var spent = Math.Max(0m, Decimal(pack.Get("usage").Get("credits_amount")) ?? 0m);
            bool inf = limit.Value < 0;
            if (!inf && limit.Value <= 0) continue;
            hasAny = true;
            if (inf) unlimited = true; else total += limit.Value;
            used += spent;

            int type = ProductType(pack);
            bool isSub = type is ProductUltra or ProductProPlus or ProductPro or ProductLite or ProductCnExpress;
            var group = pack.Get("group_name").Text().Trim();
            var desc = pack.Get("display_desc").Text().Trim();
            string label = isSub ? "套餐积分" : group.Length > 0 ? group : desc.Length > 0 ? desc : "额外积分";
            string id = isSub ? "plan" : "pack:" + label;
            var reset = isSub ? ResetTime(pack) : null;
            var expires = isSub ? null : ExpiryTime(pack);

            int existing = buckets.FindIndex(b => b.Id == id);
            if (existing >= 0)
            {
                var prev = buckets[existing];
                buckets[existing] = (prev.Id, prev.Label, prev.Total + (inf ? 0 : limit.Value), prev.Used + spent,
                    prev.Unlimited || inf, Earlier(prev.Reset, reset), Earlier(prev.Expires, expires));
            }
            else
                buckets.Add((id, label, inf ? 0 : limit.Value, spent, inf, reset, expires));
        }

        if (!hasAny) throw new InvalidDataException();

        var metrics = new List<UsageMetric>();
        var mainReset = primary is { } p ? ResetTime(p) : null;
        if (unlimited)
            metrics.Add(new UsageMetric(MetricKind.Spend, "全部积分") { Id = "credits", Unit = "积分", Amount = Math.Round(used, 2), Unlimited = true, ResetAt = mainReset });
        else
            metrics.Add(new UsageMetric(MetricKind.Count, "全部积分") { Id = "credits", Unit = "积分", Total = Math.Max(total, Math.Round(used, 2)), Used = Math.Round(used, 2), ResetAt = mainReset });

        // Show per-bucket breakdown only when there are multiple distinct credit buckets.
        if (buckets.Count > 1)
        {
            foreach (var b in buckets)
            {
                if (b.Unlimited)
                    metrics.Add(new UsageMetric(MetricKind.Spend, b.Label) { Id = b.Id, Unit = "积分", Amount = Math.Round(b.Used, 2), Unlimited = true, ResetAt = b.Reset, ExpiresAt = b.Expires });
                else
                    metrics.Add(new UsageMetric(MetricKind.Count, b.Label) { Id = b.Id, Unit = "积分", Total = Math.Max(b.Total, Math.Round(b.Used, 2)), Used = Math.Round(b.Used, 2), ResetAt = b.Reset, ExpiresAt = b.Expires });
            }
        }
        return new(metrics, plan);
    }

    private static Reading ParseLegacyFastRequests(List<JsonElement> packs, JsonElement? primary, string plan)
    {
        decimal total = 0, used = 0;
        bool unlimited = false, hasAny = false;
        foreach (var pack in packs)
        {
            var limit = Decimal(pack.Get("entitlement_base_info").Get("quota").Get("premium_model_fast_request_limit"));
            if (limit is null or 0) continue;
            var spent = Math.Max(0m, Decimal(pack.Get("usage").Get("premium_model_fast_amount")) ?? 0m);
            hasAny = true;
            if (limit.Value < 0) unlimited = true; else total += limit.Value;
            used += spent;
        }
        if (!hasAny) throw new InvalidDataException();
        var reset = primary is { } p ? ResetTime(p) : null;
        var metric = unlimited
            ? new UsageMetric(MetricKind.Spend, "快速请求") { Id = "fast", Unit = "次", Amount = used, Unlimited = true, ResetAt = reset }
            : new UsageMetric(MetricKind.Count, "快速请求") { Id = "fast", Unit = "次", Total = Math.Max(total, used), Used = used, ResetAt = reset };
        return new([metric], plan);
    }

    private static bool HasDollarQuota(List<JsonElement> packs) =>
        packs.Any(p => Decimal(p.Get("entitlement_base_info").Get("quota").Get("basic_usage_limit")) is > 0 || ProductType(p) == ProductPayGo);

    private static bool HasCreditsQuota(List<JsonElement> packs) =>
        packs.Any(p => Decimal(p.Get("entitlement_base_info").Get("quota").Get("credits_limit")) is < 0 or > 0);

    private static int ProductType(JsonElement pack) =>
        pack.Get("entitlement_base_info").Get("product_type").Number() is { } value ? (int)value : -1;

    private static JsonElement? PrimaryPack(List<JsonElement> packs)
    {
        foreach (var type in PlanPriority)
            foreach (var pack in packs)
                if (ProductType(pack) == type) return pack;
        return packs.Count > 0 ? packs[0] : null;
    }

    private static string PlanName(JsonElement? primary)
    {
        if (primary is not { } pack) return "";
        return ProductType(pack) switch
        {
            ProductUltra => "Ultra",
            ProductProPlus => "Pro+",
            ProductPro => "Pro",
            ProductLite => "Lite",
            ProductCnExpress => "Express",
            ProductFree => "Free",
            _ => pack.Get("display_desc").Text().Trim()
        };
    }

    private static DateTimeOffset? ResetTime(JsonElement pack)
    {
        var end = EpochSeconds(pack.Get("entitlement_base_info").Get("end_time")) ?? EpochSeconds(pack.Get("expire_time"));
        return end is { } at ? at.AddSeconds(1) : null;
    }

    private static DateTimeOffset? ExpiryTime(JsonElement pack) =>
        EpochSeconds(pack.Get("expire_time")) ?? EpochSeconds(pack.Get("entitlement_base_info").Get("end_time"));

    private static DateTimeOffset? Earlier(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a <= b ? a : b;

    private static DateTimeOffset? EpochSeconds(JsonElement value)
    {
        if (value.Number() is not { } raw || raw <= 0) return null;
        try
        {
            return raw >= 1_000_000_000_000d
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)raw)
                : DateTimeOffset.FromUnixTimeSeconds((long)raw);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static decimal? Decimal(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? Math.Round(number, 4) :
        value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? Math.Round(number, 4) : null;

    // Local ModularData databases for the edition (ai-agent preferred, ai-chat fallback per data folder).
    public static IReadOnlyList<string> Databases(string roaming, Edition edition)
    {
        var list = new List<string>();
        foreach (var folder in DataFolders(roaming, edition))
        {
            foreach (var module in new[] { "ai-agent", "ai-chat" })
            {
                var candidate = Path.Combine(folder, "ModularData", module, "database.db");
                if (File.Exists(candidate)) { list.Add(candidate); break; }
            }
        }
        return list;
    }

    // Decrypts a SQLCipher 4 raw-key Trae database (merging committed WAL frames) entirely in memory.
    // Returns null if the file cannot be read, changes concurrently across retries, or fails HMAC authentication.
    public static byte[]? DecryptDatabase(string dbPath, byte[]? keyOverride = null)
    {
        var key = keyOverride ?? DbKey;
        if (key.Length != 32 || !File.Exists(dbPath)) return null;
        var walPath = dbPath + "-wal";

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var beforeDb = FileStamp(dbPath);
            var beforeWal = FileStamp(walPath);
            if (beforeDb is null) return null;
            byte[]? dbBytes = ReadShared(dbPath);
            byte[]? walBytes = beforeWal is not null ? ReadShared(walPath) : null;
            if (dbBytes is null || dbBytes.Length < PageSize || dbBytes.Length % PageSize != 0) return null;

            var afterDb = FileStamp(dbPath);
            var afterWal = FileStamp(walPath);
            if (afterDb != beforeDb || afterWal != beforeWal) continue;

            if (DecryptBytes(dbBytes, walBytes, key) is { } plain) return plain;
            // A concurrent checkpoint can cause an HMAC mismatch; retry once or twice before giving up.
        }
        return null;
    }

    internal static byte[]? DecryptBytes(byte[] dbBytes, byte[]? walBytes, byte[]? keyOverride = null)
    {
        var key = keyOverride ?? DbKey;
        if (key.Length != 32 || dbBytes.Length < PageSize || dbBytes.Length % PageSize != 0) return null;
        // Already plain SQLite (e.g., an unencrypted test fixture).
        if (dbBytes.AsSpan(0, 16).SequenceEqual(SqliteHeader)) { byte[] plain = [.. dbBytes]; LeaveWal(plain); return plain; }

        int totalPages = dbBytes.Length / PageSize;
        var walPages = new Dictionary<int, int>();
        if (walBytes is { Length: >= 32 })
        {
            if (!TryIndexWal(walBytes, walPages, out var walPageCount)) return null;
            if (walPageCount is { } committed) totalPages = committed;
        }
        if (totalPages <= 0) return null;

        byte[] GetCipherPage(int pno)
        {
            var buf = new byte[PageSize];
            if (walBytes is not null && walPages.TryGetValue(pno, out var walOffset))
                Buffer.BlockCopy(walBytes, walOffset, buf, 0, PageSize);
            else if ((long)(pno - 1) * PageSize + PageSize <= dbBytes.Length)
                Buffer.BlockCopy(dbBytes, (pno - 1) * PageSize, buf, 0, PageSize);
            return buf;
        }

        var page1 = GetCipherPage(1);
        var salt = new byte[16];
        for (int i = 0; i < 16; i++) salt[i] = (byte)(page1[i] ^ 0x3a);
        var hmacKey = Rfc2898DeriveBytes.Pbkdf2(key, salt, 2, HashAlgorithmName.SHA512, 32);
        CryptographicOperations.ZeroMemory(salt);

        var output = new byte[totalPages * PageSize];
        var macInput = new byte[PageSize - Reserved + 16 + 4];
        var macActual = new byte[64];
        var pageBuf = new byte[PageSize];
        try
        {
            using var hmac = new HMACSHA512(hmacKey);
            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;

            for (int pno = 1; pno <= totalPages; pno++)
            {
                var cipher = pno == 1 ? page1 : GetCipherPage(pno);
                int start = pno == 1 ? 16 : 0;
                int ivOffset = PageSize - Reserved;
                int macLen = ivOffset + 16 - start;

                Buffer.BlockCopy(cipher, start, macInput, 0, macLen);
                macInput[macLen] = (byte)(pno & 0xff);
                macInput[macLen + 1] = (byte)((pno >> 8) & 0xff);
                macInput[macLen + 2] = (byte)((pno >> 16) & 0xff);
                macInput[macLen + 3] = (byte)((pno >> 24) & 0xff);

                if (!hmac.TryComputeHash(macInput.AsSpan(0, macLen + 4), macActual, out _)
                    || !CryptographicOperations.FixedTimeEquals(macActual, cipher.AsSpan(ivOffset + 16, 64)))
                    return null;

                aes.IV = cipher[ivOffset..(ivOffset + 16)];
                using var dec = aes.CreateDecryptor();
                Array.Clear(pageBuf);
                int written = dec.TransformBlock(cipher, start, ivOffset - start, pageBuf, start);
                if (written != ivOffset - start) return null;
                if (pno == 1)
                {
                    SqliteHeader.CopyTo(pageBuf, 0);
                    int headerPageSize = (pageBuf[16] << 8) | pageBuf[17];
                    if (headerPageSize != PageSize || pageBuf[20] != Reserved) return null;
                    LeaveWal(pageBuf);
                }
                Buffer.BlockCopy(pageBuf, 0, output, (pno - 1) * PageSize, PageSize);
            }
            return output;
        }
        catch (CryptographicException) { return null; }
        finally
        {
            CryptographicOperations.ZeroMemory(hmacKey);
            CryptographicOperations.ZeroMemory(macInput);
            CryptographicOperations.ZeroMemory(macActual);
            CryptographicOperations.ZeroMemory(pageBuf);
        }
    }

    // Trae keeps its databases in WAL mode (header bytes 18 and 19 set to 2). The WAL frames are already merged into the image,
    // and SQLite refuses to open an in-memory image still marked WAL, so the copy is marked as a rollback-journal database.
    private static void LeaveWal(byte[] page1)
    {
        if (page1[18] == 2) page1[18] = 1;
        if (page1[19] == 2) page1[19] = 1;
    }

    // Encrypts a plain 4096-byte-page SQLite image (with reserved=80) into SQLCipher 4 raw-key format; used by tests.
    internal static byte[] EncryptBytes(byte[] plainDb, byte[]? keyOverride = null)
    {
        var key = keyOverride ?? DbKey;
        if (plainDb.Length < PageSize || plainDb.Length % PageSize != 0) throw new ArgumentException("Page-aligned SQLite image required.");
        int totalPages = plainDb.Length / PageSize;
        var rawSalt = RandomNumberGenerator.GetBytes(16);
        var hmacSalt = new byte[16];
        for (int i = 0; i < 16; i++) hmacSalt[i] = (byte)(rawSalt[i] ^ 0x3a);
        var hmacKey = Rfc2898DeriveBytes.Pbkdf2(key, hmacSalt, 2, HashAlgorithmName.SHA512, 32);

        var output = new byte[plainDb.Length];
        using var hmac = new HMACSHA512(hmacKey);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        for (int pno = 1; pno <= totalPages; pno++)
        {
            int pageOff = (pno - 1) * PageSize;
            int start = pno == 1 ? 16 : 0;
            int ivOff = PageSize - Reserved;
            var iv = RandomNumberGenerator.GetBytes(16);
            aes.IV = iv;
            var dest = new byte[PageSize];
            if (pno == 1) rawSalt.CopyTo(dest, 0);
            using var enc = aes.CreateEncryptor();
            enc.TransformBlock(plainDb, pageOff + start, ivOff - start, dest, start);
            iv.CopyTo(dest, ivOff);
            var macInput = new byte[ivOff + 16 - start + 4];
            Buffer.BlockCopy(dest, start, macInput, 0, ivOff + 16 - start);
            macInput[^4] = (byte)(pno & 0xff);
            macInput[^3] = (byte)((pno >> 8) & 0xff);
            macInput[^2] = (byte)((pno >> 16) & 0xff);
            macInput[^1] = (byte)((pno >> 24) & 0xff);
            hmac.ComputeHash(macInput).CopyTo(dest, ivOff + 16);
            dest.CopyTo(output, pageOff);
        }
        return output;
    }

    private static bool TryIndexWal(byte[] wal, Dictionary<int, int> pages, out int? pageCount)
    {
        pageCount = null;
        uint magic = ReadU32(wal, 0, true);
        if (magic is not (0x377f0682 or 0x377f0683)) return false;
        if (ReadU32(wal, 4, true) != 3007000 || ReadU32(wal, 8, true) != PageSize) return false;
        bool be = magic == 0x377f0683;
        uint s0 = 0, s1 = 0;
        WalSum(wal, 0, 24, be, ref s0, ref s1);
        if (s0 != ReadU32(wal, 24, true) || s1 != ReadU32(wal, 28, true)) return false;

        var pending = new Dictionary<int, int>();
        int frameSize = 24 + PageSize;
        for (int off = 32; off + frameSize <= wal.Length; off += frameSize)
        {
            if (!wal.AsSpan(off + 8, 8).SequenceEqual(wal.AsSpan(16, 8))) break;
            WalSum(wal, off, 8, be, ref s0, ref s1);
            WalSum(wal, off + 24, PageSize, be, ref s0, ref s1);
            if (s0 != ReadU32(wal, off + 16, true) || s1 != ReadU32(wal, off + 20, true)) break;

            uint pno = ReadU32(wal, off, true);
            uint commit = ReadU32(wal, off + 4, true);
            if (pno == 0 || pno == uint.MaxValue || commit == uint.MaxValue) return false;
            pending[(int)pno] = off + 24;
            if (commit > 0)
            {
                int committed = (int)commit;
                foreach (var (page, pos) in pending) pages[page] = pos;
                pending.Clear();
                pageCount = committed;
                foreach (var stale in pages.Keys.Where(k => k > committed).ToList()) pages.Remove(stale);
            }
        }
        return true;
    }

    private static uint ReadU32(byte[] b, int off, bool be) => be
        ? ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3]
        : b[off] | ((uint)b[off + 1] << 8) | ((uint)b[off + 2] << 16) | ((uint)b[off + 3] << 24);

    private static void WalSum(byte[] data, int off, int len, bool be, ref uint s0, ref uint s1)
    {
        for (int i = 0; i < len; i += 8)
        {
            s0 = unchecked(s0 + ReadU32(data, off + i, be) + s1);
            s1 = unchecked(s1 + ReadU32(data, off + i + 4, be) + s0);
        }
    }

    private static (long Length, DateTime WriteUtc)? FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static byte[]? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream((int)Math.Min(stream.Length, 256 * 1024 * 1024));
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // The model is the one the turn called (model_name, such as "Dola-Seed-2.0-Code__dollar__dev"), not the name TRAE shows for it
    // (display_model_name), which can name another model: TRAE shows Dola-Seed-2.0-Code as "Seed-2.1-Turbo".
    public const string TokenQuery =
        "SELECT CAST(id AS TEXT), session_id, COALESCE(created_at, updated_at), " +
        "COALESCE(" +
        "NULLIF(NULLIF(TRIM(json_extract(context, '$.persist_user_message_context.model_info.model_name')), ''), '-'), " +
        "NULLIF(NULLIF(TRIM(json_extract(context, '$.persist_user_message_context.model_info.config_name')), ''), '-'), " +
        "NULLIF(NULLIF(TRIM(json_extract(context, '$.persist_user_message_context.model_info.display_model_name')), ''), '-'), " +
        "NULLIF(NULLIF(TRIM(json_extract(context, '$.model_name')), ''), '-'), " +
        "NULLIF(NULLIF(TRIM(json_extract(context, '$.model')), ''), '-'), " +
        "''), " +
        "json_extract(context, '$.token_usage.prompt_tokens'), " +
        "json_extract(context, '$.token_usage.prompt_tokens_total'), " +
        "json_extract(context, '$.token_usage.input_tokens'), " +
        "json_extract(context, '$.token_usage.completion_tokens'), " +
        "json_extract(context, '$.token_usage.completion_tokens_total'), " +
        "json_extract(context, '$.token_usage.output_tokens'), " +
        "json_extract(context, '$.token_usage.cache_read_input_tokens'), " +
        "json_extract(context, '$.token_usage.cache_creation_input_tokens'), " +
        "json_extract(context, '$.token_usage.reasoning_tokens'), " +
        "json_extract(context, '$.token_usage.reasoning_output_tokens'), " +
        "json_extract(context, '$.token_usage.total_tokens') " +
        "FROM chat_turn WHERE json_valid(context) AND json_type(context, '$.token_usage') = 'object'";

    public static void ReadTokens(ProviderSnapshot snapshot, IDesktopSources desktop, string roaming, Edition edition, DateTimeOffset start, DateTimeOffset now)
    {
        var name = ProviderCatalog.Name(edition.Id);
        var databases = Databases(roaming, edition);
        snapshot.UsageAvailable = databases.Count > 0;
        if (!snapshot.UsageAvailable)
        {
            snapshot.UsageNote = Loc.T(Loc.Joined($"未找到本机 {name} 记录。"), $"No local {name} records were found.");
            return;
        }
        var rows = new List<string?[]>();
        bool complete = true;
        foreach (var database in databases)
        {
            var found = desktop.QueryTraeDatabase(database, TokenQuery);
            if (found is null) complete = false; else rows.AddRange(found);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<TokenEntry>();
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, session) in ParseTokens(rows, edition))
        {
            if (entry.Time < start || entry.Time > now || entry.Total <= 0) continue;
            if (!seen.Add(entry.Key)) continue;
            entries.Add(entry);
            if (session.Length > 0) sessions.Add(session);
        }
        entries.Sort((a, b) => a.Time.CompareTo(b.Time));
        snapshot.Entries = entries;
        snapshot.UsageComplete = complete;
        snapshot.UsageNote = Loc.T(Loc.Joined($"本机 {name} {start.LocalDateTime:yyyy-MM-dd} 起 · {sessions.Count} 个会话 · {entries.Count} 次模型请求"),
            $"Local {name} since {start.LocalDateTime:yyyy-MM-dd} · " + Loc.Count(sessions.Count, "session", "sessions") + " · " + Loc.Count(entries.Count, "model request", "model requests"));
        if (!complete) snapshot.UsageNote += Loc.T(" · 部分记录未能读取", " · Some records could not be read");
    }

    // Columns: 0=id, 1=session_id, 2=time, 3=model, 4=prompt, 5=prompt_total, 6=input, 7=completion, 8=completion_total,
    // 9=output, 10=cache_read, 11=cache_write, 12=reasoning, 13=reasoning_output, 14=total.
    public static IEnumerable<(TokenEntry Entry, string Session)> ParseTokens(IEnumerable<string?[]> rows, Edition edition)
    {
        var prefix = ProviderCatalog.Key(edition.Id) + ":";
        foreach (var row in rows)
        {
            if (row.Length < 15 || string.IsNullOrEmpty(row[0])) continue;
            if (!TryTimestamp(row[2], out var time)) continue;
            long? Num(int i) => double.TryParse(row[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value) && value >= 0 ? (long)Math.Clamp(value, 0, 1e15) : null;

            long? prompt = Num(4) ?? Num(6);
            long? promptTotal = Num(5);
            long? completion = Num(7) ?? Num(9);
            long? completionTotal = Num(8);
            long cacheRead = Num(10) ?? 0;
            long cacheWrite = Num(11) ?? 0;
            long reasoning = Num(12) ?? Num(13) ?? 0;
            long? total = Num(14);

            long rawInput = prompt ?? promptTotal ?? 0;
            long rawOutput = completion ?? completionTotal ?? 0;
            if (rawInput == 0 && rawOutput == 0 && cacheRead == 0 && cacheWrite == 0) continue;

            // Reconcile whether prompt/completion already include cache/reasoning against total_tokens when present.
            long input = rawInput;
            long output = rawOutput;
            if (total is > 0 and var t)
            {
                if (rawInput + rawOutput == t)
                {
                    if (cacheRead + cacheWrite <= rawInput) input = rawInput - cacheRead - cacheWrite;
                    else if (cacheRead <= rawInput) { input = rawInput - cacheRead; cacheWrite = 0; }
                }
                else if (rawInput + rawOutput + reasoning == t)
                {
                    output = rawOutput + reasoning;
                    if (cacheRead + cacheWrite <= rawInput) input = rawInput - cacheRead - cacheWrite;
                }
                else if (rawInput + cacheRead + cacheWrite + rawOutput + reasoning == t)
                {
                    output = rawOutput + reasoning;
                }
                else if (rawInput + cacheRead + cacheWrite + rawOutput == t)
                {
                    // prompt excludes cache; completion already includes reasoning.
                }
            }
            else if (cacheRead + cacheWrite <= rawInput)
            {
                input = rawInput - cacheRead - cacheWrite;
            }

            // Whole-turn totals (prompt_tokens_total / completion_tokens_total) supersede single-request counts when larger.
            if (promptTotal is > 0 && promptTotal.Value > rawInput) input += promptTotal.Value - rawInput;
            if (completionTotal is > 0 && completionTotal.Value > rawOutput) output += completionTotal.Value - rawOutput;

            var session = row[1] ?? "";
            var model = CleanModelName(row[3] ?? "");
            var key = prefix + (session.Length > 0 ? session + ":" : "") + row[0];
            yield return (new TokenEntry(key, time, model, Math.Max(0, input), cacheRead, cacheWrite, 0, Math.Max(0, output)), session);
        }
    }

    public static string CleanModelName(string raw)
    {
        var name = raw.Trim();
        if (name.Length == 0) return "trae-unknown";
        foreach (var suffix in new[] { "__dollar__dev", "__dev" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
                name = name[..^suffix.Length];
        return name;
    }

    private static bool TryTimestamp(string? text, out DateTimeOffset time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) && num > 0)
        {
            try
            {
                time = (num >= 1_000_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(num)
                    : DateTimeOffset.FromUnixTimeSeconds(num)).ToLocalTime();
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time)
            && (time = time.ToLocalTime()) != default;
    }
}
