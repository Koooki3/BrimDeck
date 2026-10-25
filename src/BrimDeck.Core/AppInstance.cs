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
}
