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

    // An existing "notices" folder is used only while it is the current user's own (or the administrators' or the system's)
    // and not a link: one another account made could lead the writes elsewhere or swap the file (sec-M1).
    private static string Choose()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "notices");
        if (Directory.Exists(installed)) return Trusted(installed, unknown: false) ? installed : LegacyFolder;
        return Writable(AppContext.BaseDirectory) ? installed : LegacyFolder;
    }

    // Whether BrimDeck may read and write in the folder: it does not exist yet (BrimDeck creates it), or it is no link and is
    // owned by the current user, the administrators or the system. unknown answers for a drive without owners (FAT, exFAT).
    public static bool Trusted(string folder, bool unknown)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists) return true;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner) return unknown;
            return owner == WindowsIdentity.GetCurrent().User || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException) { return unknown; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
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

    // Checks the folder before anything is read from or written to it, and creates the default one private to the current
    // user. False when BrimDeck must leave the folder alone (see Trusted): a folder the user chose is reported, not used.
    // Only when notices are kept.
    public static bool Prepare(string folder, bool create = true)
    {
        bool isDefault = string.Equals(folder, DefaultFolder, StringComparison.OrdinalIgnoreCase);
        if (!Trusted(folder, unknown: !isDefault)) return false;
        if (!create || !isDefault || Directory.Exists(folder)) return true;
        try { Directory.CreateDirectory(folder); MakePrivate(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return true;
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
        if (!Move(legacy, folder)) return;
        try { if (Directory.Exists(legacy) && !Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Moves the saved notices and their icons from one folder into another that has no saved notices of its own (life-L1).
    // True when the file moved.
    public static bool Move(string from, string folder)
    {
        if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var file = Path.Combine(from, "notices.dat");
            var target = Path.Combine(folder, "notices.dat");
            if (!File.Exists(file) || File.Exists(target)) return false;
            Directory.CreateDirectory(folder);
            File.Move(file, target);
            var icons = Path.Combine(from, "icons");
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
            NoticeIcons.DeleteStored(from);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
