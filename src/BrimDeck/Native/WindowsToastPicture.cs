using System.IO;
using System.Xml;
using System.Xml.Linq;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// The picture a Windows notification shows beside its text (QQ puts the contact's or the group's avatar there, as
// placement="appLogoOverride"). UserNotificationListener gives a notification's text only; Windows keeps the notification's whole
// toast in the user's notification database, which is read here, read only, for that one notification. Only a picture on a
// local drive is taken (NoticeIntake.IconRef); NoticeIcons checks it again when reading it.
internal static class WindowsToastPicture
{
    private static readonly DesktopSources Sqlite = new();

    private static string Database => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    // The picture's reference for the notification with this id from this application, or null.
    public static string? Find(uint id, string appUserModelId)
    {
        var rows = Sqlite.QueryDatabase(Database,
            $"SELECT n.Payload FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId WHERE n.Id = {id} AND h.PrimaryId = '{appUserModelId.Replace("'", "''")}' LIMIT 1");
        return rows is [{ } row, ..] && row[0] is { Length: > 0 } payload ? FromToast(payload) : null;
    }

    // The app logo override if there is one, else the first image of the toast's visual.
    internal static string? FromToast(string payload)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(payload), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var images = XDocument.Load(reader).Descendants("image").ToList();
            var image = images.FirstOrDefault(i => (string?)i.Attribute("placement") == "appLogoOverride") ?? images.FirstOrDefault(i => i.Attribute("placement") is null);
            return NoticeIntake.IconRef((string?)image?.Attribute("src"), NoticeOrigin.Windows, null);
        }
        catch (XmlException) { return null; }
    }
}
