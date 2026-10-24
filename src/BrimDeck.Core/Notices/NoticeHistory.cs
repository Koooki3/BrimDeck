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
// nothing over it meanwhile (core-M4). Denied: the file is there but this account may not read it (made by BrimDeck running
// as administrator, say); it is read again later like Unavailable, and "clear" may replace it (C-L15). Refused: the folder,
// or the file in it, is a link, or the folder is not the user's own (sec-M1, S1); nothing is read from or written to it.
public enum NoticeHistoryState { Empty, Loaded, Unreadable, Unavailable, Refused, Denied }

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
            // Checked again right before reading: the folder was checked when it was chosen, but may have been swapped for a
            // link since; and a notices.dat that is a link is never followed (it could lead to another account's share, S1).
            if (Linked(DirectoryPath)) return NoticeHistoryState.Refused;
            FileAttributes attributes;
            // Not found only counts as no file yet while the folder can be seen; a network share that failed for a moment
            // reports missing files too, and the full file must not be written over then (C-L3).
            try { attributes = File.GetAttributes(FilePath); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Reachable(DirectoryPath) ? NoticeHistoryState.Empty : NoticeHistoryState.Unavailable;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0 && Linked(FilePath)) return NoticeHistoryState.Refused;
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
        // This account may not read it: retrying alone may never help (C-L15).
        catch (UnauthorizedAccessException)
        {
            return NoticeHistoryState.Denied;
        }
        // Not reachable now (in use by another program, a cloud file while offline): read again later.
        catch (IOException)
        {
            return NoticeHistoryState.Unavailable;
        }
        catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException or NotSupportedException)
        {
            return NoticeHistoryState.Unreadable;
        }
    }

    // Whether the path is a link (symbolic link, junction or mount point): one attribute read, and only for a reparse point a
    // look at what it is. Other reparse points (OneDrive's files on demand, deduplicated files) are the file or folder itself
    // and lead nowhere else (L7); LinkTarget names a target for the two kinds that do. A path that cannot be read is no link;
    // what follows fails on it by itself. A reparse point that cannot be looked at counts as a link.
    public static bool Linked(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
        if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
        try { return ((attributes & FileAttributes.Directory) != 0 ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path)).LinkTarget is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return true; }
    }

    // An access mask with its generic rights (as an inherit-only entry may hold them) turned into the file rights they stand
    // for, so that a GENERIC_WRITE or GENERIC_ALL granted to others is seen as the write it is (L2).
    public static int SpecificRights(int mask)
    {
        const int GenericAll = 0x10000000, GenericWrite = 0x40000000, FileAll = 0x001F01FF, FileWrite = 0x00120116;
        if ((mask & GenericAll) != 0) mask |= FileAll;
        if ((mask & GenericWrite) != 0) mask |= FileWrite;
        return mask;
    }

    // Whether a name in a notices folder is one BrimDeck keeps there: the file and its temporary files, or in icons\ the stored
    // icons and their temporary files (M3).
    public static bool OwnName(string name, bool inIcons)
        => inIcons
            ? name.Length == 28 && name.EndsWith(".png", StringComparison.Ordinal) && name[..24].All(Uri.IsHexDigit)
                || name.StartsWith(".icon.", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal)
            : name is "notices.dat" or "notices.dat.tmp"
                || name.StartsWith(".notices.", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal);

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
    // order comes from NextOrder when the list was copied; without one, the list counts as copied now. The new file is on the
    // disk itself before it replaces the old one, so a power cut cannot leave a file of the new length full of zeros (C-L1);
    // the app saves in the background, and only the last save on exit waits for this.
    public bool Save(IEnumerable<Notice> notices, IEnumerable<NoticeKnownApp> apps, Func<bool>? wanted = null, long? order = null)
    {
        long own = order ?? NextOrder();
        // A transient notice is never written (Notice.Transient).
        var contents = new Contents { Notices = notices.Where(notice => !notice.Transient).ToList(), Apps = apps.ToList() };
        var json = JsonSerializer.SerializeToUtf8Bytes(contents, Options);
        var protectedData = protector.Protect(json);
        lock (_write)
        {
            if (wanted?.Invoke() == false || own < _written) return false;
            Directory.CreateDirectory(DirectoryPath);
            // The folder may have been swapped for a link since it was checked (S1).
            if (Linked(DirectoryPath)) return false;
            // A new name each time, created only if nothing has that name: an existing file or link there is never followed
            // and written through (sec-M1).
            var temporary = Path.Combine(DirectoryPath, TemporaryPattern.Replace("*", Guid.NewGuid().ToString("N"), StringComparison.Ordinal));
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Magic); stream.Write(protectedData);
                    stream.Flush(true);
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
            // Never inside a folder that became a link: the files where it leads are not BrimDeck's (S1).
            if (Linked(DirectoryPath)) return;
            var paths = new List<string> { FilePath, FilePath + ".tmp" };
            try { if (Directory.Exists(DirectoryPath)) paths.AddRange(Directory.EnumerateFiles(DirectoryPath, TemporaryPattern)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (var path in paths)
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // A protector for the tests (BrimDeck.Tests): it only marks the data, it does not hide it.
    internal sealed class PlainProtector : INoticeProtector
    {
        public byte[] Protect(byte[] data) => [.. "PLAIN"u8, .. data];
        public byte[] Unprotect(byte[] data) => data.AsSpan().StartsWith("PLAIN"u8) ? data[5..] : throw new System.Security.Cryptography.CryptographicException("Not plain data.");
    }

    public static string Describe(NoticeHistoryState state) => state switch
    {
        NoticeHistoryState.Unreadable => Loc.T("已保存的通知无法读取，可以清空后重新开始。", "The saved notices could not be read. Clear them to start again."),
        NoticeHistoryState.Unavailable => Loc.T("暂时无法访问保存的通知（文件被占用，或文件夹所在的盘、网络位置不可用），稍后会自动重试；在此之前不会写入。",
            "The saved notices cannot be reached right now (the file is in use, or the folder's drive or network location is not available). BrimDeck tries again later and writes nothing until then."),
        NoticeHistoryState.Denied => Loc.T("当前账户没有读取已保存通知的权限（可能是以管理员身份运行的 BrimDeck 创建的）。可以重试，或清空后重新开始。",
            "This account may not read the saved notices (BrimDeck running as administrator may have made the file). Try again, or clear them to start again."),
        NoticeHistoryState.Refused => Loc.T("该文件夹或其中的通知文件是链接，或文件夹属于其他账户，BrimDeck 不会在其中读写。请选择自己的文件夹。",
            "This folder, or the notices file in it, is a link, or the folder belongs to another account, so BrimDeck neither reads nor writes there. Choose a folder of your own."),
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
