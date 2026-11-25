using System.Security.AccessControl;
using System.Security.Principal;
using BrimDeck.Core;

// A settings file that cannot be opened when BrimDeck starts is neither taken for a first start nor replaced.
internal static class SettingsUnavailableTests
{
    private const string Version = "1.4.0";
    private const string Original = """{"AppVersion":"1.4.0","Language":"en-US","Width":987,"FromAnotherBuild":{"a":1}}""";

    public static void Run(string root, Action<string, bool> check)
    {
        int number = 0;
        SettingsStore New(string content = Original)
        {
            var store = new SettingsStore(Path.Combine(root, "unavailable-" + number++), Version);
            Directory.CreateDirectory(store.DirectoryPath);
            File.WriteAllText(store.FilePath, content);
            return store;
        }
        static FileStream Held(SettingsStore store, FileAccess access, FileShare share) => new(store.FilePath, FileMode.Open, access, share);
        // Whether the load is refused, and leaves the folder as it was.
        static bool Refused(SettingsStore store)
        {
            try { store.Load(); return false; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return !store.IsFirstStart && Directory.GetFiles(store.DirectoryPath).SequenceEqual([store.FilePath]); }
        }
        static bool Reads(SettingsStore store) => File.ReadAllText(store.FilePath) == Original && store.Load().Width == 987 && !store.IsFirstStart;

        var patience = SettingsStore.ReadPatience;
        try
        {
            SettingsStore.ReadPatience = TimeSpan.FromMilliseconds(120);
            foreach (var (what, access, share) in new[] { ("is held by a program that shares it with nobody", FileAccess.Read, FileShare.None),
                ("is open for writing in another program", FileAccess.ReadWrite, FileShare.ReadWrite),
                ("is held with only its deletion shared", FileAccess.Read, FileShare.Delete) })
            {
                var store = New(); bool refused;
                using (Held(store, access, share)) refused = Refused(store);
                check("settings whose file " + what + " are not replaced by defaults", refused);
                check("settings whose file " + what + " are read once it is free", Reads(store));
            }
            if (OperatingSystem.IsWindows())
            {
                var store = New();
                check("settings whose file this user may not read are not replaced by defaults", Denied(store, () => Refused(store)) && Reads(store));
            }

            SettingsStore.ReadPatience = TimeSpan.FromSeconds(30);
            var busy = New(); var held = Held(busy, FileAccess.Read, FileShare.None);
            var release = new Thread(() => { Thread.Sleep(60); held.Dispose(); }); release.Start();
            check("settings whose file is in use for a moment are read as soon as it is free", Reads2(busy));
            release.Join();
            static bool Reads2(SettingsStore store) => store.Load().Width == 987 && !store.IsFirstStart;

            // What cannot be understood, as opposed to opened, still starts with defaults and a copy; what is not there is a first start.
            foreach (var (what, content) in new[] { ("of zeros", new string((char)0, 4096)), ("cut short", Original[..40]), ("that is empty", "") })
            {
                var store = New(content); store.Load();
                check("a settings file " + what + " is kept aside and defaults start", store is { IsFirstStart: true, LoadWarning: not null }
                    && Directory.GetFiles(store.DirectoryPath, "settings.unreadable-*.json") is [var copy] && File.ReadAllText(copy) == content);
            }
            var fresh = new SettingsStore(Path.Combine(root, "unavailable-fresh"), Version); fresh.Load();
            check("no settings file is a first start", fresh is { IsFirstStart: true, LoadWarning: null });
        }
        finally { SettingsStore.ReadPatience = patience; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool Denied(SettingsStore store, Func<bool> during)
    {
        var file = new FileInfo(store.FilePath);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        var access = file.GetAccessControl(); access.AddAccessRule(rule); file.SetAccessControl(access);
        try { return during(); }
        finally { access = file.GetAccessControl(); access.RemoveAccessRule(rule); file.SetAccessControl(access); }
    }
}
