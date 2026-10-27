using System.Runtime.InteropServices;
using BrimDeck.Core.Notices;
using Microsoft.Win32;

namespace BrimDeck.Native;

// Which of the time-kind applications (NoticeTimeSources) this PC has, for the read-only list in the settings. Only whether
// each is installed for the current user is looked up: the package list (no administrator rights needed) and, for classic
// Outlook, its registered program path. Nothing is read from the applications themselves.
internal static class TimeSourceDetection
{
    private static Task<IReadOnlyDictionary<string, bool>>? _detected;
    private static DateTimeOffset _detectedAt;

    // Installed, by source id. Looked up once and reused for ten minutes; the lookup runs in the background.
    public static Task<IReadOnlyDictionary<string, bool>> InstalledAsync()
    {
        if (_detected is null || DateTimeOffset.UtcNow - _detectedAt > TimeSpan.FromMinutes(10))
        {
            _detectedAt = DateTimeOffset.UtcNow;
            _detected = Task.Run(Detect);
        }
        return _detected;
    }

    private static IReadOnlyDictionary<string, bool> Detect()
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        Windows.Management.Deployment.PackageManager? manager = null;
        try { manager = new Windows.Management.Deployment.PackageManager(); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { }
        foreach (var source in NoticeTimeSources.All)
        {
            bool installed = false;
            if (source.PackageFamily is { } family && manager is not null)
                try { installed = manager.FindPackagesForUser("", family).Any(); }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException) { }
            if (!installed && source.Program is { } program) installed = HasAppPath(program);
            result[source.Id] = installed;
        }
        return result;
    }

    // Programs installed for all users or the current one register their path under App Paths.
    private static bool HasAppPath(string program)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + program);
                if (key?.GetValue(null) is string path && path.Length > 0) return true;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        return false;
    }
}
