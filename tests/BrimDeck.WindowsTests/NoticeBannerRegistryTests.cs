using System.IO;
using BrimDeck.Core.Notices;
using BrimDeck.Native;
using Microsoft.Win32;

// The real registry side of "turn off other apps' notification banners", on an application id made up for the test (no
// application uses it); its key is removed afterwards whatever happens. Whether Windows then hides the banner is checked by hand.
internal static class NoticeBannerRegistryTests
{
    public static void Run(string root, Action<string, bool> check)
    {
        string appId = "BrimDeck.Tests.Banner." + Guid.NewGuid().ToString("N");
        string nested = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\BrimDeck.Tests." + Guid.NewGuid().ToString("N") + @"\tool.exe";
        string keyPath = NoticeBanners.SettingsKey + "\\" + appId;
        string nestedRoot = NoticeBanners.SettingsKey + "\\" + nested[..nested.IndexOf('\\', nested.IndexOf('\\') + 1)];
        string data = Path.Combine(root, "banner-data");
        try
        {
            var registry = new NoticeBannerRegistry();
            check("banners: a missing key reads as no key and no value", registry.Read(appId) == new BannerReading(false, null, null));
            var ledger = new BannerLedger();
            string ledgerPath = NoticeBannerRegistry.LedgerPath(data);
            var states = NoticeBanners.Reconcile(new HashSet<string>([appId, nested]), true, ledger, registry, () => ledger.Save(ledgerPath));
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                check("banners: turning off writes ShowBanner = 0 as a DWORD", states[appId] == BannerState.TurnedOff && key?.GetValueKind("ShowBanner") == RegistryValueKind.DWord
                    && (int?)key.GetValue("ShowBanner") == 0 && key.ValueCount == 1);
            check("banners: a desktop program's id becomes nested keys", states[nested] == BannerState.TurnedOff && registry.Read(nested).BannerOff);
            check("banners: Windows' notification settings list the apps it keeps, nested ones included",
                NoticeBannerRegistry.Configured() is var configured && configured.Contains(appId, StringComparer.OrdinalIgnoreCase) && configured.Contains(nested, StringComparer.OrdinalIgnoreCase));
            check("banners: the ledger file records them", File.Exists(ledgerPath) && BannerLedger.Load(ledgerPath)?.Find(appId) is { Previous: null, CreatedKey: true });
            check("banners: restoring everything from the ledger (the uninstaller's path) works", NoticeBannerRegistry.RestoreAll(data));
            check("banners: the keys BrimDeck created are gone again", Registry.CurrentUser.OpenSubKey(keyPath) is null && registry.Read(nested) is { KeyExists: false }
                && !File.Exists(ledgerPath));

            // A string value is not Windows' switch and reads as none; Enabled = 0 keeps the application untouched.
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) { key.SetValue("ShowBanner", "0", RegistryValueKind.String); key.SetValue("Enabled", 0, RegistryValueKind.DWord); }
            check("banners: only DWORD values are read", registry.Read(appId) is { KeyExists: true, ShowBanner: null, Enabled: 0, NotificationsOff: true });
            var untouched = new BannerLedger();
            states = NoticeBanners.Reconcile(new HashSet<string>([appId]), true, untouched, registry, () => true);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                check("banners: an application with notifications off is not written", states[appId] == BannerState.NotificationsOff && key?.GetValueKind("ShowBanner") == RegistryValueKind.String);
            check("banners: the Start menu lists applications by their ids", NoticeBannerRegistry.StartMenu() is { Count: > 0 } apps && apps.Any(NoticeBanners.Usable));

            // A ShowBanner of another kind is put back with its kind and data.
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) key.SetValue("ShowBanner", "yes", RegistryValueKind.ExpandString);
            var other = new BannerLedger();
            NoticeBanners.Reconcile(new HashSet<string>([appId]), true, other, registry, () => true);
            bool off = registry.Read(appId).BannerOff;
            NoticeBanners.Reconcile(new HashSet<string>(), false, other, registry, () => true);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                check("banners: a value of another kind is turned off and put back as it was", off && key?.GetValueKind("ShowBanner") == RegistryValueKind.ExpandString
                    && key.GetValue("ShowBanner", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string == "yes");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(nestedRoot, throwOnMissingSubKey: false);
        }
    }
}
