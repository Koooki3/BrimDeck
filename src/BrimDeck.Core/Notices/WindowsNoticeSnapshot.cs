using System.Runtime.InteropServices;

namespace BrimDeck.Core.Notices;

// The conversion of one full reading of Windows' notification list (WindowsNoticeSource.ReadSnapshotAsync), apart from WinRT so
// that the loop can be driven with it in tests. Each notification is read on its own: Windows answers some of the properties of a
// notification with an error for some senders (UserNotification.AppInfo is E_NOTIMPL, NotImplementedException, for a toast sent
// by an unpackaged program under an unusual application id). A notification that cannot be read is left out of the notices (its
// id still counts as listed, so it is not read again and its leaving the list is seen) and the rest of the list is read as
// usual; before, the one exception failed the whole reading, every following reading failed on the same notification, and the
// source stayed Failed for as long as that toast sat in Windows' list.
public static class WindowsNoticeSnapshot
{
    // The errors that mean this notification cannot be read, as opposed to Windows failing as a whole (a reading that timed out,
    // access that was revoked, cancellation): those still fail the reading.
    public static bool Unreadable(Exception ex)
        => ex is COMException or NotImplementedException or InvalidCastException or ArgumentException or NullReferenceException or InvalidOperationException;

    public static WindowsNoticeReadSnapshot Build<T>(
        IReadOnlyList<T> notifications, Func<T, uint> idOf, Func<T, uint, NoticeInput?> read, IReadOnlySet<uint> known, CancellationToken token)
    {
        var ids = new List<uint>(notifications.Count);
        var seen = new HashSet<uint>();
        var inputs = new Dictionary<uint, NoticeInput?>();
        foreach (var notification in notifications)
        {
            token.ThrowIfCancellationRequested();
            uint id;
            try { id = idOf(notification); } catch (Exception ex) when (Unreadable(ex)) { continue; }
            if (!seen.Add(id)) continue;
            ids.Add(id);
            if (known.Contains(id)) continue;
            try { inputs[id] = read(notification, id); }
            catch (Exception ex) when (Unreadable(ex)) { inputs[id] = null; }
        }
        return new(ids, inputs);
    }
}
