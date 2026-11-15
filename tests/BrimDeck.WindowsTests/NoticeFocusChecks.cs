using BrimDeck.Core.Notices;
using BrimDeck.Native;

// The Win32 binding of NoticeAppWindows reads real windows without throwing. It never brings a window forward here.
internal static class NoticeFocusChecks
{
    public static void Run(Action<string, bool> check)
    {
        var system = new AppWindowSystem();
        var handles = system.TopLevelWindows();
        check("notice focus: the desktop has top-level windows", handles.Count > 0);
        int inspected = 0, own = 0;
        bool ownPathRight = true;
        foreach (var handle in handles)
        {
            if (system.Inspect(handle) is not { } info) continue;
            inspected++;
            if (info.Pid != Environment.ProcessId) continue;
            own++;
            ownPathRight &= string.Equals(system.ImagePath(info.Pid), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        check($"notice focus: windows are read without throwing ({inspected} read, {own} of this process)", inspected > 0 && ownPathRight);
        check("notice focus: a handle that is no window reads as gone", system.Inspect(0) is null && system.Inspect(0x7FFFFFF1) is null);
        check("notice focus: a program that is not running has no window to raise",
            !NoticeAppWindows.TryFocus(new AppProcessHint(["BrimDeck-No-Such-Program"], null), system, Environment.ProcessId));
    }
}
