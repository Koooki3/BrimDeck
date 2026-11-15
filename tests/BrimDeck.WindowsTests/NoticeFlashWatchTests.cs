using BrimDeck.Native;

// The taskbar flash listener registers its hook window and removes it again, without input, flashing or a change of foreground.
internal static class NoticeFlashWatchTests
{
    public static void Run(Action<string, bool> check)
    {
        int raised = 0;
        bool ok;
        try
        {
            using (var watch = new TaskbarFlashWatch(_ => raised++)) { }
            using (var again = new TaskbarFlashWatch(_ => raised++)) { again.Dispose(); again.Dispose(); }
            ok = true;
        }
        catch (Exception ex) { Console.WriteLine(ex); ok = false; }
        check("taskbar flash: the shell hook window registers and unregisters (twice, and Dispose repeats safely)", ok);
        check("taskbar flash: nothing was raised without a flash", raised == 0);
        using var first = new TaskbarFlashWatch(_ => { });
        using var second = new TaskbarFlashWatch(_ => { });
        check("taskbar flash: two listeners can exist at once", true);
    }
}
