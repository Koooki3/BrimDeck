using System.IO;
using System.Runtime.InteropServices;
using BrimDeck.Core.Notices;
using Microsoft.Win32;

namespace BrimDeck.Native;

// Windows' per-application banner switch (NoticeBanners): HKCU only, the ShowBanner value only. Enabled is read, never written.
// Nothing is restarted, neither the application nor Explorer.
internal sealed class NoticeBannerRegistry : IBannerRegistry
{
    private const string ShowBanner = "ShowBanner", Enabled = "Enabled";
    // Windows' own notification settings nest a desktop program's id ("{known folder}\folder\app.exe") as keys; deeper or more
    // keys than these are not looked for.
    private const int MostDepth = 6, MostKeys = 2000;

    private static string Path(string appId) => NoticeBanners.Usable(appId) ? NoticeBanners.SettingsKey + "\\" + appId : throw new ArgumentException("Unusable application id.", nameof(appId));

    public BannerReading Read(string appId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Path(appId));
            if (key is null) return new(false, null, null);
            return new(true, Dword(key, ShowBanner), Dword(key, Enabled));
        }
        catch (ArgumentException) { throw new IOException("Unusable application id."); }
    }

    // A value of another kind (a string someone wrote) reads as no value: Windows only honors DWORDs.
    private static int? Dword(RegistryKey key, string name) => key.GetValue(name) is int value && key.GetValueKind(name) == RegistryValueKind.DWord ? value : null;

    public void Write(string appId, int? showBanner)
    {
        try
        {
            if (showBanner is { } value)
            {
                using var key = Registry.CurrentUser.CreateSubKey(Path(appId), writable: true);
                key.SetValue(ShowBanner, value, RegistryValueKind.DWord);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(Path(appId), writable: true);
                key?.DeleteValue(ShowBanner, throwOnMissingValue: false);
            }
        }
        catch (ArgumentException) { throw new IOException("Unusable application id."); }
    }

    public void DeleteKeyIfEmpty(string appId)
    {
        try
        {
            string path = Path(appId);
            using (var key = Registry.CurrentUser.OpenSubKey(path))
                if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
            Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
    }

    // The applications Windows already keeps notification settings for: every key under SettingsKey that holds a value (a
    // nested desktop program's id is the path of keys down to it).
    public static List<string> Configured()
    {
        var found = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(NoticeBanners.SettingsKey);
            if (root is not null) Walk(root, "", 0, found);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return found;

        static void Walk(RegistryKey key, string path, int depth, List<string> found)
        {
            foreach (var name in key.GetSubKeyNames())
            {
                if (found.Count >= MostKeys) return;
                string id = path.Length == 0 ? name : path + "\\" + name;
                try
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub is null) continue;
                    if (sub.ValueCount > 0) found.Add(id);
                    if (depth + 1 < MostDepth && sub.SubKeyCount > 0) Walk(sub, id, depth + 1, found);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
    }

    // The Start menu's applications (shell:AppsFolder), by application id: the folder names each item by its id. Read through
    // the Shell's own folder object on a thread of its own, which COM needs single-threaded; empty when the Shell does not answer.
    public static List<string> StartMenu()
    {
        var found = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                if (Type.GetTypeFromProgID("Shell.Application") is not { } type) return;
                dynamic shell = Activator.CreateInstance(type)!;
                try
                {
                    dynamic? folder = shell.NameSpace("shell:AppsFolder");
                    if (folder is null) return;
                    foreach (dynamic item in folder.Items())
                    {
                        if (item.Path is not string id || id.Length == 0) continue;
                        lock (found) { if (found.Count >= MostKeys) break; found.Add(id); }
                    }
                }
                finally { Marshal.FinalReleaseComObject(shell); }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException) { }
        }) { IsBackground = true, Name = "BrimDeck Start menu apps" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // A Shell that hangs must not keep BrimDeck waiting; what it has listed by then is used.
        thread.Join(TimeSpan.FromSeconds(10));
        lock (found) return [.. found];
    }

    public static string LedgerPath(string dataFolder) => System.IO.Path.Combine(dataFolder, BannerLedger.FileName);

    // Gives every banner BrimDeck turned off back to Windows (the uninstaller runs BrimDeck.exe --restore-banners). Returns
    // whether everything recorded was restored.
    public static bool RestoreAll(string dataFolder)
    {
        string path = LedgerPath(dataFolder);
        if (BannerLedger.Load(path) is not { } ledger) return false;
        NoticeBanners.Reconcile(new HashSet<string>(), holding: false, ledger, new NoticeBannerRegistry(), () => ledger.Save(path));
        return ledger.Records.Count == 0;
    }
}
