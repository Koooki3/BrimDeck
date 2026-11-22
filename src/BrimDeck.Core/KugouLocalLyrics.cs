using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

// Lyrics KuGou keeps for the songs it played: a KRC file each in its lyric folder, the LyricPath in KuGou.ini (UTF-16, in
// %APPDATA%\KuGou8; measured: D:\KuGou\Lyric), named "Artist - Title-<hash>-<number>-<hex>.krc". The hash is the song's own
// (measured: the hash KuGou's song search lists for it), the one KuGou's full control reports; a song with it takes the file of
// that hash whatever its name says (measured: "DYvine - 琵琶曲" holds the lyrics of "琵琶曲 (1.1Xdj氛围版)"). A song without it
// takes a file named with its title and artist. The lyrics are KuGou's own, timed to the recording KuGou plays.
public sealed partial class LyricsService
{
    public const string KugouLocalSource = "酷狗音乐（本机）";
    private const int MaxKrcBytes = 2 * 1024 * 1024;
    // A KRC file is "krc1", then zlib data XORed with this key, repeated.
    private static readonly byte[] KrcKey = [0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69];
    private static readonly Regex KrcFileName = new(@"\A(?<artist>.+?) - (?<title>.+)-(?<hash>[0-9a-fA-F]{32})-[0-9]+-[0-9a-fA-F]+\z");
    private readonly Func<string?> _kugouLyricFolder;
    private FileSystemWatcher? _kugouWatcher;
    // Raised on a worker thread when KuGou writes a lyric file, which it may do after the song has started.
    public event Action? KugouLyricsWritten;

    // KuGou's lyric folder as its settings name it; null when KuGou is not set up or the folder is gone.
    public static string? KugouLyricFolder()
    {
        try
        {
            var ini = new FileInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KuGou8", "KuGou.ini"));
            if (!ini.Exists || ini.Length > 1024 * 1024) return null;
            using var reader = new StreamReader(ini.FullName, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
            for (string? line; (line = reader.ReadLine()) is not null;)
            {
                int equals = line.IndexOf('=');
                if (equals < 0 || !line[..equals].Trim().Equals("LyricPath", StringComparison.OrdinalIgnoreCase)) continue;
                string folder = line[(equals + 1)..].Trim().Trim('"');
                return folder.Length > 0 && Path.IsPathFullyQualified(folder) && Directory.Exists(folder) ? folder : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    private Lyrics FindKugouLocal(MediaTrack track)
    {
        if (_kugouLyricFolder() is not { } folder) return Lyrics.Empty;
        WatchKugouFolder(folder);
        string hash = track.SongId.StartsWith("kugou:", StringComparison.Ordinal) ? track.SongId[6..] : "";
        (FileInfo File, Match Name)[] named;
        try
        {
            named = [.. new DirectoryInfo(folder).EnumerateFiles("*.krc").Where(file => file.Length <= MaxKrcBytes).OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => (file, KrcFileName.Match(Path.GetFileNameWithoutExtension(file.Name)))).Where(row => row.Item2.Success)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Lyrics.Empty; }
        // Without the hash a file's name is all there is to go by, and a name may leave out the version (see above): only the
        // title itself is taken, and the length the file records, when it records one (most do not, measured).
        var files = hash.Length > 0
            ? named.Where(row => row.Name.Groups["hash"].Value.Equals(hash, StringComparison.OrdinalIgnoreCase))
            : named.Where(row => FitOf(track, row.Name.Groups["title"].Value, row.Name.Groups["artist"].Value, track.Duration.TotalSeconds) is { Title: 0 });
        foreach (var (file, _) in files)
        {
            string? krc;
            try { krc = DecodeKrc(File.ReadAllBytes(file.FullName)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (krc is null || hash.Length == 0 && track.Duration > TimeSpan.Zero && KrcLength(krc) is { } length && Math.Abs(length - track.Duration.TotalSeconds) > 2) continue;
            var lyrics = ParseKrc(krc, track.Duration.TotalSeconds);
            if (lyrics.Lines.Count > 0 || lyrics.Instrumental) return lyrics;
        }
        return Lyrics.Empty;
    }

    // The KRC text, or null for a file that is not one.
    internal static string? DecodeKrc(byte[] data)
    {
        if (data.Length < 4 || data.Length > MaxKrcBytes || !data.AsSpan(0, 4).SequenceEqual("krc1"u8)) return null;
        var body = new byte[data.Length - 4];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)(data[i + 4] ^ KrcKey[i % KrcKey.Length]);
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(body), CompressionMode.Decompress);
            using var text = new MemoryStream();
            var chunk = new byte[16384];
            for (int read; (read = zlib.Read(chunk)) > 0;)
            {
                if (text.Length + read > MaxKrcBytes) return null;
                text.Write(chunk, 0, read);
            }
            return Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length).TrimStart('\uFEFF');
        }
        catch (InvalidDataException) { return null; }
    }

    // The song's length in seconds as the KRC's [total:] records it; null when it records none (0).
    private static double? KrcLength(string krc) =>
        Regex.Match(krc, @"^\[total:(\d{1,9})\]", RegexOptions.Multiline) is { Success: true } total && long.Parse(total.Groups[1].Value, CultureInfo.InvariantCulture) is > 0 and var ms
            ? ms / 1000.0 : null;

    // A KRC line is "[start,length]" in milliseconds, then its words, each after a "<offset,length,0>" tag. The panel shows whole
    // lines, so the lyrics are given to Lyrics.Parse as LRC, with a blank line where a line ends before the next one starts (Parse
    // keeps the blank only for a pause of Lyrics.Gap or longer). The [offset:] header is passed on to Parse as it is.
    internal static Lyrics ParseKrc(string krc, double duration)
    {
        var lines = new List<(long Start, long End, string Text)>();
        var lrc = new StringBuilder();
        foreach (string row in krc.Split('\n'))
        {
            string line = row.Trim();
            if (Regex.IsMatch(line, @"\A\[offset:[+-]?\d+\]\z", RegexOptions.IgnoreCase)) { lrc.Append(line).Append('\n'); continue; }
            var timed = Regex.Match(line, @"\A\[(\d{1,9}),(\d{1,9})\](.*)\z");
            if (!timed.Success) continue;
            long start = long.Parse(timed.Groups[1].Value, CultureInfo.InvariantCulture), length = long.Parse(timed.Groups[2].Value, CultureInfo.InvariantCulture);
            lines.Add((start, start + length, Regex.Replace(timed.Groups[3].Value, @"<\d+,\d+,\d+>", "").Trim()));
        }
        static string Stamp(long ms) => string.Create(CultureInfo.InvariantCulture, $"[{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}]");
        lines.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 0; i < lines.Count; i++)
        {
            lrc.Append(Stamp(lines[i].Start)).Append(lines[i].Text).Append('\n');
            if (lines[i].End > lines[i].Start && (i + 1 == lines.Count || lines[i + 1].Start > lines[i].End)) lrc.Append(Stamp(lines[i].End)).Append('\n');
        }
        return Lyrics.Parse(lrc.ToString(), null, duration, KugouLocalSource);
    }

    // The lyric folder is watched from the first lookup until the service is disposed, or until KuGou names another folder.
    private void WatchKugouFolder(string folder)
    {
        if (_kugouWatcher?.Path == folder) return;
        _kugouWatcher?.Dispose(); _kugouWatcher = null;
        try
        {
            var watcher = new FileSystemWatcher(folder, "*.krc") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            watcher.Created += (_, _) => KugouLyricsWritten?.Invoke();
            watcher.Changed += (_, _) => KugouLyricsWritten?.Invoke();
            watcher.Renamed += (_, _) => KugouLyricsWritten?.Invoke();
            watcher.EnableRaisingEvents = true;
            _kugouWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
    }
}
