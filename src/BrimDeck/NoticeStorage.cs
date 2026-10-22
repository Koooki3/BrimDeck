using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BrimDeck;

// Where the recent notices are kept by default: a "notices" folder in BrimDeck's own install folder, so they stay on the drive
// BrimDeck was installed to instead of the system drive. An install folder BrimDeck cannot write to (Program Files) falls back
// to the data folder in %LOCALAPPDATA%, where earlier versions kept them; a file left there is moved over once.
internal static class NoticeStorage
{
    private static string? _default;

    public static string LegacyFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "notices");

    public static string DefaultFolder => _default ??= Choose();

    private static string Choose()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "notices");
        return Directory.Exists(installed) || Writable(AppContext.BaseDirectory) ? installed : LegacyFolder;
    }

    // Whether a file can be created in the folder; nothing is left behind.
    private static bool Writable(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".brimdeck-write-test-" + Environment.ProcessId);
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // Creates the default folder, private to the current user, before anything is written to it. Only when notices are kept.
    public static void Prepare(string folder)
    {
        if (!string.Equals(folder, DefaultFolder, StringComparison.OrdinalIgnoreCase) || Directory.Exists(folder)) return;
        try { Directory.CreateDirectory(folder); MakePrivate(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // A folder BrimDeck creates outside the user's profile (an install folder on another drive usually lets every signed-in
    // user change what is in it) is opened to the current user and the system only, without inheriting the parent's rights.
    // The file inside is encrypted for this user anyway; this keeps other accounts from reading, replacing or deleting it.
    public static void MakePrivate(string folder)
    {
        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user is null) return;
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags All = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(folder).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException) { }
    }

    // Moves the saved notices and icons from the old default folder into the new one, once, when the new one has none yet.
    public static void MoveLegacy(string folder)
    {
        var legacy = LegacyFolder;
        if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var file = Path.Combine(legacy, "notices.dat");
            var target = Path.Combine(folder, "notices.dat");
            if (!File.Exists(file) || File.Exists(target)) return;
            Directory.CreateDirectory(folder);
            File.Move(file, target);
            var icons = Path.Combine(legacy, "icons");
            if (Directory.Exists(icons))
            {
                var into = Path.Combine(folder, "icons");
                Directory.CreateDirectory(into);
                foreach (var icon in Directory.EnumerateFiles(icons, "*.png"))
                {
                    var moved = Path.Combine(into, Path.GetFileName(icon));
                    if (!File.Exists(moved)) File.Move(icon, moved); else File.Delete(icon);
                }
            }
            NoticeIcons.DeleteStored(legacy);
            if (Directory.Exists(legacy) && !Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
