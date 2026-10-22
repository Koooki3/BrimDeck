using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core.Notices;

// Encrypts and decrypts the saved notices for the current user (Windows data protection in the app; tests use their own).
public interface INoticeProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

public enum NoticeHistoryState { Empty, Loaded, Unreadable }

// The recent notices on disk: one encrypted file, written whole and atomically. The app batches changes before saving, so a
// task's progress does not write the file on every step. Only the notices' own fields are kept: no notification XML, attachments or pictures. A file that cannot be
// decrypted or read is left alone and reported; "clear" replaces it.
public sealed class NoticeHistory(string directory, INoticeProtector protector)
{
    private static readonly byte[] Magic = "BDN1"u8.ToArray();
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    // One writer at a time: the app saves in the background and once more on exit, and both use the same temporary file.
    private readonly object _write = new();
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
            if (!File.Exists(FilePath)) return NoticeHistoryState.Empty;
            var data = File.ReadAllBytes(FilePath);
            if (data.Length < Magic.Length || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return NoticeHistoryState.Unreadable;
            var json = protector.Unprotect(data[Magic.Length..]);
            var contents = JsonSerializer.Deserialize<Contents>(json, Options);
            if (contents is null) return NoticeHistoryState.Unreadable;
            notices = contents.Notices ?? []; apps = contents.Apps ?? [];
            return NoticeHistoryState.Loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException or NotSupportedException)
        {
            return NoticeHistoryState.Unreadable;
        }
    }

    // wanted is asked again just before writing, under the writer lock: a save that started before keeping notices was
    // switched off (or the folder changed) then writes nothing, instead of putting back the file Delete just removed.
    public void Save(IEnumerable<Notice> notices, IEnumerable<NoticeKnownApp> apps, Func<bool>? wanted = null)
    {
        var contents = new Contents { Notices = notices.Select(n => n.Copy()).ToList(), Apps = apps.ToList() };
        var json = JsonSerializer.SerializeToUtf8Bytes(contents, Options);
        var protectedData = protector.Protect(json);
        lock (_write)
        {
            if (wanted?.Invoke() == false) return;
            Directory.CreateDirectory(DirectoryPath);
            var temporary = FilePath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(Magic); stream.Write(protectedData); stream.Flush(true);
            }
            File.Move(temporary, FilePath, true);
        }
    }

    // Removes the file, for "clear" and when keeping notices is switched off. A save under way finishes first, so it cannot
    // put the file back afterwards.
    public void Delete()
    {
        lock (_write)
            foreach (var path in new[] { FilePath, FilePath + ".tmp" })
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
        _ => ""
    };
}
