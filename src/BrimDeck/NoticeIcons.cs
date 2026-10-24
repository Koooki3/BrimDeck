using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;
using BrimDeck.Protocol;

namespace BrimDeck;

// The icon and accent color of a notice's application. Windows applications give their logo through the notification;
// it is kept in icons/ beside the saved notices, so the recent notices still show it after a restart. Local files are read
// as they are; icons inside WSL only while that distribution's agent runs, so that showing a notice never starts a
// distribution. Nothing is downloaded. Lookups run in the background; Loaded is raised on the UI thread for each icon that
// arrives later.
internal sealed class NoticeIcons(System.Windows.Threading.Dispatcher dispatcher, Func<string, Task<byte[]?>> windowsLogo, Func<string, bool> distributionRunning)
{
    // Enough for the rows the notices page lists at once and the apps in the settings; an icon let go of is read again when
    // asked for, and only its own plate is filled then (MainWindow.NoticeIconLoaded), so nothing loops.
    private const int Capacity = 160;
    // Icons kept in icons/ at most; the least recently used go first (sec-L6: tray programs make up a new id each run).
    private const int StoredCapacity = 200;
    // Most recently used first; each entry knows its node, so using an icon costs no search (perf-11).
    private readonly Dictionary<string, (BitmapSource? Image, Color? Accent, LinkedListNode<string> Node)> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    // Parsed colors: the user's chosen ones and each application's fallback, asked for on every row the notices page draws.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Color> s_colors = new(StringComparer.Ordinal);
    private static readonly Color[] s_palette = AppPresets.Palette.Take(9).Select(hex => (Color)ColorConverter.ConvertFromString(hex)).ToArray();
    public string Folder { get; set; } = "";
    public event Action<string>? Loaded;

    // The icon if it is known now; otherwise null, and Loaded follows once it is.
    public BitmapSource? Get(string? iconRef, out Color? accent)
    {
        accent = null;
        if (string.IsNullOrEmpty(iconRef)) return null;
        if (_cache.TryGetValue(iconRef, out var entry))
        {
            _order.Remove(entry.Node); _order.AddFirst(entry.Node);
            accent = entry.Accent; return entry.Image;
        }
        if (_loading.Add(iconRef)) _ = LoadAsync(iconRef);
        return null;
    }

    private async Task LoadAsync(string iconRef)
    {
        BitmapSource? image = null;
        Color? accent = null;
        try
        {
            var bytes = await Task.Run(() => ReadAsync(iconRef));
            // Decoding and the costly part of finding the color (scaling to the sample) stay off the UI thread (perf-11);
            // the color is finished there, since it follows the theme.
            BitmapSource? sample = null;
            if (bytes is not null)
                (image, sample) = await Task.Run<(BitmapSource?, BitmapSource?)>(() => Decode(bytes) is { } decoded ? (decoded, Sample(decoded)) : (null, null));
            if (sample is not null) accent = AccentOf(sample);
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
            if (_cache.Remove(iconRef, out var earlier)) _order.Remove(earlier.Node);
            _cache[iconRef] = (image, accent, _order.AddFirst(iconRef));
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
    // The agent looks in the same places when it resolves an icon that is a link (LinuxIcons.Resolve).
    public static IEnumerable<string> WslCandidates(string icon)
        => NoticeIntake.IsLinuxIcon(icon) ? LinuxIcons.Candidates(icon) : [];

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
            if (!Directory.Exists(icons) || NoticeHistory.Linked(icons)) return;
            foreach (var file in Directory.EnumerateFiles(icons, "*.png"))
                if (Path.GetFileNameWithoutExtension(file) is { Length: 24 } name && name.All(Uri.IsHexDigit)) File.Delete(file);
            if (!Directory.EnumerateFileSystemEntries(icons).Any()) Directory.Delete(icons);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Written to a new file of a random name, then moved into place (sec-M1): a file or link someone placed at the predictable
    // name is replaced, never written through, and the icons folder must not be a link either. The time of last writing marks
    // when an icon was last used, for TrimStored.
    private static void TryWrite(string path, byte[] bytes)
    {
        string? temporary = null;
        try
        {
            var folder = Path.GetDirectoryName(path)!;
            // The notices folder itself is created by NoticeStorage.Prepare, private to the user; never here with whatever
            // rights its parent hands down (N1). Only the icons folder inside it is.
            if (!Directory.Exists(Path.GetDirectoryName(folder))) return;
            Directory.CreateDirectory(folder);
            if (NoticeHistory.Linked(folder)) return;
            var existing = new FileInfo(path);
            if (existing.Exists && (existing.Attributes & FileAttributes.ReparsePoint) == 0 && existing.Length == bytes.Length)
            {
                // win2 P5: marked at most once a day; a day's precision is plenty to tell the least recently used.
                var now = DateTime.UtcNow;
                if (NoticeIconStore.NeedsMark(existing.LastWriteTimeUtc, now)) existing.LastWriteTimeUtc = now;
                return;
            }
            temporary = Path.Combine(folder, ".icon." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            if (!existing.Exists) TrimStored(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Keeps the StoredCapacity most recently used icons; also removes temporary files a write left behind.
    private static void TrimStored(string folder)
    {
        var stored = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            if (file.Name.StartsWith(".icon.", StringComparison.Ordinal) && file.Extension == ".tmp" && file.LastWriteTimeUtc < DateTime.UtcNow.AddMinutes(-1)) file.Delete();
            else if (file.Extension == ".png" && Path.GetFileNameWithoutExtension(file.Name) is { Length: 24 } name && name.All(Uri.IsHexDigit)) stored.Add(file);
        }
        foreach (var file in stored.OrderByDescending(f => f.LastWriteTimeUtc).Skip(StoredCapacity)) file.Delete();
    }

    // An icon larger than this on either side is not decoded: a small file can declare a huge picture.
    private const int MaxPixels = 1024;

    // An icon file listing more pictures than this is not read: a small file can list a hundred thousand (sec-L6).
    internal const int MaxFrames = 64;

    // Whether an icon file's header lists at most MaxFrames pictures; other formats hold one.
    internal static bool FewFrames(ReadOnlySpan<byte> bytes)
        => bytes is not [0x00, 0x00, 0x01, 0x00, ..] || bytes.Length >= 6 && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) <= MaxFrames;

    private static BitmapSource? Decode(byte[] bytes)
    {
        if (!FewFrames(bytes)) return null;
        using var stream = new MemoryStream(bytes);
        // Nothing is decoded yet: the frames' sizes come from their headers, and only the chosen frame is decoded, below.
        if (DecoderFor(stream, bytes) is not { } decoder || decoder.Frames.Count > MaxFrames) return null;
        // The frame closest to 128 pixels: icon files hold several sizes, and the transparent margin trimmed below leaves less.
        var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 128) + (f.PixelWidth < 32 ? 1000 : 0)).FirstOrDefault();
        if (frame is null || frame.PixelWidth == 0 || frame.PixelHeight == 0 || frame.PixelWidth > MaxPixels || frame.PixelHeight > MaxPixels) return null;
        BitmapSource result = Trim(new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0));
        if (Math.Max(result.PixelWidth, result.PixelHeight) > 128)
        {
            double scale = 128d / Math.Max(result.PixelWidth, result.PixelHeight);
            result = new TransformedBitmap(result, new ScaleTransform(scale, scale));
        }
        var copy = new WriteableBitmap(result);
        copy.Freeze();
        return copy;
    }

    // Windows' application logos (a Store app's square logo especially) draw their mark small in the middle of a transparent
    // square, so on a plate the mark would be a few pixels. The square is cut to the drawn part with a little margin, kept square
    // and centred; a picture that already fills its square is left as it is.
    internal static BitmapSource Trim(BitmapSource source)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        if (w < 8 || h < 8) return source;
        var pixels = new byte[w * h * 4];
        source.CopyPixels(pixels, w * 4, 0);
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (pixels[(y * w + x) * 4 + 3] > 12)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0) return source;
        int drawnW = maxX - minX + 1, drawnH = maxY - minY + 1;
        if (drawnW >= w * .85 && drawnH >= h * .85) return source;
        int side = Math.Max(drawnW, drawnH);
        side = Math.Min(Math.Min(w, h), side + 2 * (int)Math.Ceiling(side * .06));
        int x0 = Math.Clamp((minX + maxX + 1) / 2 - side / 2, 0, w - side), y0 = Math.Clamp((minY + maxY + 1) / 2 - side / 2, 0, h - side);
        return new CroppedBitmap(source, new System.Windows.Int32Rect(x0, y0, side, side));
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

    // The icon scaled to the 16 by 16 pixels MusicVisuals.Accent averages, frozen, so the color is found from it at once.
    private static BitmapSource Sample(BitmapSource image)
    {
        var scaled = new TransformedBitmap(image, new ScaleTransform(16d / image.PixelWidth, 16d / image.PixelHeight));
        var sample = new WriteableBitmap(new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0));
        sample.Freeze();
        return sample;
    }

    // The icon's main color, made readable; null for icons without a clear color (white, grey or transparent ones).
    private static Color? AccentOf(BitmapSource image)
        => MusicVisuals.Accent(image) is SolidColorBrush { Color: var color } && !(color.R == color.G && color.G == color.B) ? color : null;

    // A color for an application without one: the same application always gets the same color from the shared palette.
    public static Color Fallback(string appKey)
        => Remembered("app\n" + appKey, key => s_palette[SHA256.HashData(Encoding.UTF8.GetBytes(key[4..]))[0] % s_palette.Length]);

    // The accent of a notice: the user's color for the application, the icon's, or the fallback.
    public Color AccentFor(Notice notice, NoticeAppRule? rule)
    {
        if (rule?.AccentColor is { Length: > 0 } chosen) return Remembered(chosen, text => (Color)ColorConverter.ConvertFromString(text));
        Get(notice.IconRef, out var accent);
        return accent ?? Fallback(notice.AppKey);
    }

    private static Color Remembered(string key, Func<string, Color> make)
    {
        if (s_colors.TryGetValue(key, out var known)) return known;
        if (s_colors.Count >= 512) s_colors.Clear();
        return s_colors[key] = make(key);
    }
}
