using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core.Notices;

// Encrypts and decrypts the saved notices for the current user (Windows data protection in the app; tests use their own).
public interface INoticeProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

// Unreadable: the file is there but damaged (not BrimDeck's, not decryptable); "clear" replaces it. Unavailable: the file or
// its folder cannot be reached right now (in use, offline, a drive not mounted); BrimDeck reads it again later and writes
// nothing over it meanwhile (core-M4). Refused: the folder is a link or belongs to another account (sec-M1); nothing is read
// from or written to it.
public enum NoticeHistoryState { Empty, Loaded, Unreadable, Unavailable, Refused }

// The recent notices on disk: one encrypted file, written whole and atomically. The app batches changes before saving, so a
// task's progress does not write the file on every step. Only the notices' own fields are kept: no notification XML, attachments or pictures. A file that cannot be
// decrypted or read is left alone and reported; "clear" replaces it.
public sealed class NoticeHistory(string directory, INoticeProtector protector)
{
    private static readonly byte[] Magic = "BDN1"u8.ToArray();
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    // 500 notices take a few megabytes at most; a larger file is not BrimDeck's and is not read into memory (sec-L6).
    public const long MaxFileSize = 32L * 1024 * 1024;
    private const string TemporaryPattern = ".notices.*.tmp";
    // One writer at a time: the app saves in the background and once more on exit.
    private readonly object _write = new();
    // The order of the lists given to Save, taken when each list is copied: a list copied earlier never replaces one copied
    // later, even when its (background) write reaches the file last.
    private long _issued, _written;
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "notices.dat");

    private sealed class Contents
    {
        public int Version { get; set; } = 1;
        public List<Notice> Notices { get; set; } = [];
        public List<NoticeKnownApp> Apps { get; set; } = [];
    }

    public NoticeHistoryState Load(out List<Notice> notices, out List<NoticeKnownApp> apps)
    {
        notices = []; apps = [];
        try
        {
            if (!File.Exists(FilePath)) return Reachable(DirectoryPath) ? NoticeHistoryState.Empty : NoticeHistoryState.Unavailable;
            byte[] data;
            using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaxFileSize) return NoticeHistoryState.Unreadable;
                data = new byte[stream.Length];
                stream.ReadExactly(data);
            }
            if (data.Length < Magic.Length || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return NoticeHistoryState.Unreadable;
            var json = protector.Unprotect(data[Magic.Length..]);
            var contents = JsonSerializer.Deserialize<Contents>(json, Options);
            if (contents is null) return NoticeHistoryState.Unreadable;
            notices = contents.Notices ?? []; apps = contents.Apps ?? [];
            return NoticeHistoryState.Loaded;
        }
        // Not reachable now (in use by another program, a cloud file while offline, no access): read again later.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NoticeHistoryState.Unavailable;
        }
        catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException or NotSupportedException)
        {
            return NoticeHistoryState.Unreadable;
        }
    }

    // Whether the folder could hold the file now: its drive or network share is there. A folder that does not exist on a
    // drive that does is simply new (the first save creates it); one whose drive or share is missing is unmounted or offline,
    // and saving now would put a nearly empty list where the full one comes back.
    public static bool Reachable(string directory)
    {
        if (Directory.Exists(directory)) return true;
        try { return Path.GetPathRoot(Path.GetFullPath(directory)) is { Length: > 0 } root && Directory.Exists(root); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { return false; }
    }

    // The order for a list about to be saved; take it where the list is copied (see _issued).
    public long NextOrder() => Interlocked.Increment(ref _issued);

    // The list is serialized as it is: a save in the background is given copies no other thread changes (perf-7).
    // wanted is asked again just before writing, under the writer lock: a save that started before keeping notices was
    // switched off (or the folder changed) then writes nothing, instead of putting back the file Delete just removed.
    // order comes from NextOrder when the list was copied; without one, the list counts as copied now. durable waits until the
    // file is on the disk itself, for the last save on exit; the replace is atomic either way.
    public bool Save(IEnumerable<Notice> notices, IEnumerable<NoticeKnownApp> apps, Func<bool>? wanted = null, long? order = null, bool durable = false)
    {
        long own = order ?? NextOrder();
        var contents = new Contents { Notices = notices.ToList(), Apps = apps.ToList() };
        var json = JsonSerializer.SerializeToUtf8Bytes(contents, Options);
        var protectedData = protector.Protect(json);
        lock (_write)
        {
            if (wanted?.Invoke() == false || own < _written) return false;
            Directory.CreateDirectory(DirectoryPath);
            // A new name each time, created only if nothing has that name: an existing file or link there is never followed
            // and written through (sec-M1).
            var temporary = Path.Combine(DirectoryPath, TemporaryPattern.Replace("*", Guid.NewGuid().ToString("N"), StringComparison.Ordinal));
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Magic); stream.Write(protectedData);
                    if (durable) stream.Flush(true);
                }
                File.Move(temporary, FilePath, true);
            }
            catch
            {
                try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
            _written = own;
            return true;
        }
    }

    // Removes the file, for "clear" and when keeping notices is switched off. A save under way finishes first, so it cannot
    // put the file back afterwards. Temporary files a crash left behind go too.
    public void Delete()
    {
        lock (_write)
        {
            var paths = new List<string> { FilePath, FilePath + ".tmp" };
            try { if (Directory.Exists(DirectoryPath)) paths.AddRange(Directory.EnumerateFiles(DirectoryPath, TemporaryPattern)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (var path in paths)
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // A protector for tests and for platforms without data protection: it only marks the data, it does not hide it.
    public sealed class PlainProtector : INoticeProtector
    {
        public byte[] Protect(byte[] data) => [.. "PLAIN"u8, .. data];
        public byte[] Unprotect(byte[] data) => data.AsSpan().StartsWith("PLAIN"u8) ? data[5..] : throw new System.Security.Cryptography.CryptographicException("Not plain data.");
    }

    public static string Describe(NoticeHistoryState state) => state switch
    {
        NoticeHistoryState.Unreadable => Loc.T("已保存的通知无法读取，可以清空后重新开始。", "The saved notices could not be read. Clear them to start again."),
        NoticeHistoryState.Unavailable => Loc.T("暂时无法访问保存的通知（文件被占用，或文件夹所在的盘、网络位置不可用），稍后会自动重试；在此之前不会写入。",
            "The saved notices cannot be reached right now (the file is in use, or the folder's drive or network location is not available). BrimDeck tries again later and writes nothing until then."),
        NoticeHistoryState.Refused => Loc.T("该文件夹是链接，或属于其他账户，BrimDeck 不会在其中读写。请选择自己的文件夹。",
            "This folder is a link or belongs to another account, so BrimDeck neither reads nor writes there. Choose a folder of your own."),
        _ => ""
    };
}

// What changing where, or whether, the recent notices are kept does with the file (life-L1, core-L4). loaded: true when the
// old folder's file has been read into the list (or there was none), so the list in memory is the whole of it; false when it
// was never read (notices were off); null when it could not be read (damaged, unreachable, refused folder).
public enum NoticeHistoryStep
{
    // Remove the file in the old folder. After a Save, only once that save has written the new file.
    DeleteOld,
    // Move the file of the old folder (never read) into the new one, unless the new one has its own.
    MoveOld,
    // Read the new folder's file and add its notices, before anything is written there.
    Load,
    // Write the list.
    Save
}

public static class NoticeHistoryPlan
{
    public static bool SameFolder(string a, string b)
    {
        static string Full(string path) { try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; } }
        return string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<NoticeHistoryStep> For(string? oldFolder, string newFolder, bool oldKeep, bool newKeep, bool? loaded)
    {
        if (oldFolder is null) return [];
        // Not kept: nothing of the list stays on disk, wherever it was.
        if (!newKeep) return [NoticeHistoryStep.DeleteOld];
        if (SameFolder(oldFolder, newFolder))
            // Switched back on: the list was only in memory while off, and is written again.
            return !oldKeep && loaded == true ? [NoticeHistoryStep.Save] : [];
        // The list moves along: whatever the new folder holds is added first, and the old file goes once the new one is
        // written. A file never read moves as it is; one that could not be read stays where it is, untouched.
        return loaded switch
        {
            true => [NoticeHistoryStep.Load, NoticeHistoryStep.Save, NoticeHistoryStep.DeleteOld],
            false => [NoticeHistoryStep.MoveOld, NoticeHistoryStep.Load],
            null => [NoticeHistoryStep.Load]
        };
    }
}
