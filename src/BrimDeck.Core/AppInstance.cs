namespace BrimDeck.Core;

// The names BrimDeck's single instance goes by: the running BrimDeck holds Mutex (the installer waits on it too) and waits on
// Settings, which a second start sets (to open its settings) before it quits. A test harness that makes BrimDeck's App
// (FxPreview --liquid-pages) takes a scope of its own first (ForTest): the App's names are then its process's alone, so it can
// neither find nor signal the user's BrimDeck, and its OnStartup starts nothing at all.
public static class AppInstance
{
    private static string _scope = "";
    public static bool IsTest => _scope.Length > 0;
    public static void ForTest() => _scope = "Test." + Environment.ProcessId + ".";
    public static string Mutex => @"Local\BrimDeck.Instance." + _scope + Environment.UserName;
    public static string Settings => @"Local\BrimDeck.Settings." + _scope + Environment.UserName;
    // A second start with --exit sets Exit; the running BrimDeck then quits the way the tray's Exit does (panels power off, the settings
    // are written) and the second start waits for Mutex to be released. A forced kill would lose the settings change still waiting for its timer.
    public static int RequestExit(int seconds)
    {
        try { using var running = new System.Threading.Mutex(false, Mutex); if (running.WaitOne(0)) { running.ReleaseMutex(); return 3; } }
        catch (AbandonedMutexException) { return 0; }
        try { using var signal = EventWaitHandle.OpenExisting(Exit); signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { return 2; }
        try
        {
            using var held = new System.Threading.Mutex(false, Mutex);
            if (held.WaitOne(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120)))) { held.ReleaseMutex(); return 0; }
        }
        catch (AbandonedMutexException) { return 0; }
        return 2;
    }
    public static string Exit => @"Local\BrimDeck.Exit." + _scope + Environment.UserName;
}
