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
        Color? accent = null;
        try
        {
            var bytes = await Task.Run(() => ReadAsync(iconRef));
            if (bytes is not null) image = await Task.Run(() => Decode(bytes));
            if (image is not null) accent = AccentOf(image);
        }
        // A damaged or unsupported picture (WIC reports those as format, COM or overflow errors) leaves the initial instead.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException
                                   or FormatException or System.Runtime.InteropServices.COMException or OverflowException) { }
        // Anything else is a fault in BrimDeck: it goes to notices.log, and the icon is still settled below, so it is not
        // left loading for good.
        catch (Exception ex) { NoticeCenter.Log(ex); image = null; accent = null; }
        await dispatcher.InvokeAsync(() =>
        {
            _loading.Remove(iconRef);
            // A WSL icon that could not be read while its distribution was stopped is tried again later; one its running
            // distribution does not have is remembered as missing, like any other.
            if (image is null && WslDistribution(iconRef) is { } distro && !distributionRunning(distro)) return;
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
            return stored is not null ? ReadImageFile(stored) : null;
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
        if (WslDistribution(iconRef) is { } distro)
        {
            string icon = iconRef[(5 + distro.Length)..];
            if (!distributionRunning(distro)) return null;
            foreach (var path in WslCandidates(icon))
                if (ReadImageFile(@"\\wsl.localhost\" + distro + path.Replace('/', '\\')) is { } found) return found;
        }
        return null;
    }

    // The distribution of a "wsl:<distribution>:<icon>" reference, if it names one by a valid distribution name: the name
    // becomes part of a \\wsl.localhost path, which must not reach anywhere else.
    private static string? WslDistribution(string iconRef)
    {
        if (!iconRef.StartsWith("wsl:", StringComparison.Ordinal)) return null;
        int colon = iconRef.IndexOf(':', 4);
        return colon > 4 && NoticeIntake.IsDistroName(iconRef[4..colon]) ? iconRef[4..colon] : null;
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

    // Removes the icons kept in a notices folder (keeping notices switched off, or the folder changed). Only files named as
    // StoredPath names them are touched; the icons folder goes too once it is empty.
    public static void DeleteStored(string folder)
    {
        var icons = Path.Combine(folder, "icons");
        try
        {
            // Not through a link: it would lead to a folder BrimDeck did not fill.
            if (!Directory.Exists(icons) || (File.GetAttributes(icons) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var file in Directory.EnumerateFiles(icons, "*.png"))
                if (Path.GetFileNameWithoutExtension(file) is { Length: 24 } name && name.All(Uri.IsHexDigit)) File.Delete(file);
            if (!Directory.EnumerateFileSystemEntries(icons).Any()) Directory.Delete(icons);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

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

    // An icon larger than this on either side is not decoded: a small file can declare a huge picture.
    private const int MaxPixels = 1024;

    private static BitmapSource? Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        // Nothing is decoded yet: the frames' sizes come from their headers, and only the chosen frame is decoded, below.
        if (DecoderFor(stream, bytes) is not { } decoder) return null;
        // The frame closest to 64 pixels: icon files hold several sizes.
        var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 64) + (f.PixelWidth < 32 ? 1000 : 0)).FirstOrDefault();
        if (frame is null || frame.PixelWidth == 0 || frame.PixelHeight == 0 || frame.PixelWidth > MaxPixels || frame.PixelHeight > MaxPixels) return null;
        BitmapSource result = frame;
        if (Math.Max(frame.PixelWidth, frame.PixelHeight) > 96)
        {
            double scale = 96d / Math.Max(frame.PixelWidth, frame.PixelHeight);
            result = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }
        result = new FormatConvertedBitmap(result, PixelFormats.Pbgra32, null, 0);
        var copy = new WriteableBitmap(result);
        copy.Freeze();
        return copy;
    }

    // WPF's own decoder for the picture's format, told by its first bytes: PNG, ICO, JPEG or BMP. Other formats, and the
    // other image codecs installed in Windows, never see a notice's icon.
    private static BitmapDecoder? DecoderFor(Stream stream, ReadOnlySpan<byte> head)
    {
        const BitmapCreateOptions Options = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation;
        const BitmapCacheOption Cache = BitmapCacheOption.None;
        return head switch
        {
            [0x89, 0x50, 0x4E, 0x47, ..] => new PngBitmapDecoder(stream, Options, Cache),
            [0x00, 0x00, 0x01, 0x00, ..] => new IconBitmapDecoder(stream, Options, Cache),
            [0xFF, 0xD8, 0xFF, ..] => new JpegBitmapDecoder(stream, Options, Cache),
            [0x42, 0x4D, ..] => new BmpBitmapDecoder(stream, Options, Cache),
            _ => null
        };
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
