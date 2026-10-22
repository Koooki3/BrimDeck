using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// The icon and accent color of a notice's application. Windows applications give their logo through the notification;
// it is kept in icons/ beside the saved notices, so the recent notices still show it after a restart. Local files are read
// as they are; icons inside WSL only while that distribution's agent runs, so that showing a notice never starts a
// distribution. Nothing is downloaded. Lookups run in the background; Loaded is raised on the UI thread for each icon that
// arrives later.
internal sealed class NoticeIcons(System.Windows.Threading.Dispatcher dispatcher, Func<string, Task<byte[]?>> windowsLogo, Func<string, bool> distributionRunning)
{
    private const int Capacity = 96;
    private readonly Dictionary<string, (BitmapSource? Image, Color? Accent)> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    public string Folder { get; set; } = "";
    public event Action<string>? Loaded;

    // The icon if it is known now; otherwise null, and Loaded follows once it is.
    public BitmapSource? Get(string? iconRef, out Color? accent)
    {
        accent = null;
        if (string.IsNullOrEmpty(iconRef)) return null;
        if (_cache.TryGetValue(iconRef, out var entry)) { Touch(iconRef); accent = entry.Accent; return entry.Image; }
        if (_loading.Add(iconRef)) _ = LoadAsync(iconRef);
        return null;
    }

    private void Touch(string key) { _order.Remove(key); _order.AddFirst(key); }

    private async Task LoadAsync(string iconRef)
    {
        BitmapSource? image = null;
        try
        {
            var bytes = await Task.Run(() => ReadAsync(iconRef));
            if (bytes is not null) image = await Task.Run(() => Decode(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException) { }
        Color? accent = image is null ? null : AccentOf(image);
        await dispatcher.InvokeAsync(() =>
        {
            _loading.Remove(iconRef);
            // A WSL icon that could not be read while its distribution was stopped is tried again later.
            if (image is null && iconRef.StartsWith("wsl:", StringComparison.Ordinal)) return;
            _cache[iconRef] = (image, accent); _order.AddFirst(iconRef);
            while (_order.Count > Capacity) { _cache.Remove(_order.Last!.Value); _order.RemoveLast(); }
            Loaded?.Invoke(iconRef);
        });
    }

    private async Task<byte[]?> ReadAsync(string iconRef)
    {
        if (iconRef.StartsWith("aumid:", StringComparison.Ordinal))
        {
            string appId = iconRef[6..];
            var stored = StoredPath(iconRef);
            var bytes = await windowsLogo(appId);
            if (bytes is not null)
            {
                if (stored is not null) TryWrite(stored, bytes);
                return bytes;
            }
            return stored is not null && File.Exists(stored) ? await File.ReadAllBytesAsync(stored) : null;
        }
        if (iconRef.StartsWith("file:", StringComparison.Ordinal))
        {
            // Checked again here, also for references from saved notices, and the drive must be a local one: a mapped
            // network drive would reach a server as well.
            string path = iconRef[5..];
            if (!NoticeIntake.IsLocalWindowsPath(path)) return null;
            try { if (new DriveInfo(path[..1]).DriveType is not (DriveType.Fixed or DriveType.Removable)) return null; }
            catch (ArgumentException) { return null; }
            return ThroughLinks(path) ? null : ReadImageFile(path);
        }
        if (iconRef.StartsWith("wsl:", StringComparison.Ordinal))
        {
            var rest = iconRef[4..];
            int colon = rest.IndexOf(':');
            if (colon <= 0) return null;
            string distro = rest[..colon], icon = rest[(colon + 1)..];
            if (!distributionRunning(distro)) return null;
            foreach (var path in WslCandidates(icon))
                if (ReadImageFile(@"\\wsl.localhost\" + distro + path.Replace('/', '\\')) is { } found) return found;
        }
        return null;
    }

    // An absolute path as given; an icon name in the usual theme and pixmap folders, at sizes WPF can read (PNG, not SVG).
    public static IEnumerable<string> WslCandidates(string icon)
    {
        if (!NoticeIntake.IsLinuxIcon(icon)) yield break;
        if (icon.StartsWith('/')) { yield return icon; yield break; }
        foreach (var size in new[] { "48x48", "64x64", "32x32", "128x128" })
            yield return $"/usr/share/icons/hicolor/{size}/apps/{icon}.png";
        yield return $"/usr/share/pixmaps/{icon}.png";
    }

    // Whether a folder on the way to the file is a link or junction; a folder link may lead to a network share.
    private static bool ThroughLinks(string path)
    {
        try
        {
            for (var folder = Path.GetDirectoryName(Path.GetFullPath(path)); folder is not null && folder.Length > 3; folder = Path.GetDirectoryName(folder))
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return true; }
    }

    private static byte[]? ReadImageFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > 2_000_000) return null;
            if (info.Extension.ToLowerInvariant() is not (".png" or ".ico" or ".jpg" or ".jpeg" or ".bmp")) return null;
            // Not through a link or junction, which could lead anywhere, a network share included.
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private string? StoredPath(string iconRef)
        => Folder.Length == 0 ? null : Path.Combine(Folder, "icons", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(iconRef)))[..24] + ".png");

    private static void TryWrite(string path, byte[] bytes)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == bytes.Length) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        // The frame closest to 64 pixels: icon files hold several sizes.
        var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 64) + (f.PixelWidth < 32 ? 1000 : 0)).FirstOrDefault();
        if (frame is null || frame.PixelWidth == 0) return null;
        BitmapSource result = frame;
        if (frame.PixelWidth > 96)
            result = new TransformedBitmap(frame, new ScaleTransform(96d / frame.PixelWidth, 96d / frame.PixelHeight));
        result = new FormatConvertedBitmap(result, PixelFormats.Pbgra32, null, 0);
        var copy = new WriteableBitmap(result);
        copy.Freeze();
        return copy;
    }

    // The icon's main color, made readable; null for icons without a clear color (white, grey or transparent ones).
    private static Color? AccentOf(BitmapSource image)
        => MusicVisuals.Accent(image) is SolidColorBrush { Color: var color } && !(color.R == color.G && color.G == color.B) ? color : null;

    // A color for an application without one: the same application always gets the same color from the shared palette.
    public static Color Fallback(string appKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(appKey));
        var palette = AppPresets.Palette.Take(9).ToArray();
        return (Color)ColorConverter.ConvertFromString(palette[hash[0] % palette.Length]);
    }

    // The accent of a notice: the user's color for the application, the icon's, or the fallback.
    public Color AccentFor(Notice notice, NoticeAppRule? rule)
    {
        if (rule?.AccentColor is { Length: > 0 } chosen) return (Color)ColorConverter.ConvertFromString(chosen);
        Get(notice.IconRef, out var accent);
        return accent ?? Fallback(notice.AppKey);
    }
}
