using BrimDeck.Core;

// A test harness never reaches the user's BrimDeck: once it takes its own scope (AppInstance.ForTest, as FxPreview --liquid-pages
// does before it makes BrimDeck's App), the instance lock and the settings signal are its process's own. Run last: the scope
// stays for the process.
internal static class AppInstanceTests
{
    public static void Run(Action<string, bool> check)
    {
        string user = Environment.UserName;
        check("app instance: the app's own names are the ones the installer waits on",
            !AppInstance.IsTest && AppInstance.Mutex == @"Local\BrimDeck.Instance." + user && AppInstance.Settings == @"Local\BrimDeck.Settings." + user);
        string userMutex = AppInstance.Mutex, userSettings = AppInstance.Settings;
        AppInstance.ForTest();
        string pid = Environment.ProcessId.ToString();
        check("app instance: a test harness's names are its process's own", AppInstance.IsTest && AppInstance.Mutex != userMutex
            && AppInstance.Settings != userSettings && AppInstance.Mutex.Contains(pid) && AppInstance.Settings.Contains(pid));
        // What App.OnStartup would do, under the test's names (the user's own lock and signal are never opened here): the lock is
        // free whether or not BrimDeck runs, and there is no settings signal to set, so nothing could open the user's settings.
        using (var mutex = new Mutex(true, AppInstance.Mutex, out bool first))
        {
            check("app instance: a test harness takes its own lock, BrimDeck running or not", first);
            if (first) mutex.ReleaseMutex();
        }
        check("app instance: a test harness finds no BrimDeck to signal", OperatingSystem.IsWindows() && !EventWaitHandle.TryOpenExisting(AppInstance.Settings, out _));
    }
}
