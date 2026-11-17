using System.Reflection;
using System.Windows.Threading;
using BrimDeck.Native;

internal static class RightDragChecks
{
    public static void Run(Action<string, bool> check)
    {
        // No synthetic mouse input, file operations or visible windows. Install
        // the real hook and prove its message pump survives a blocked UI thread.
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(RightFileDrag);
        for (int i = 0; i < 3; i++)
        {
            using var menuObserver = new PointerPressObserver(Dispatcher.CurrentDispatcher, _ => { });
            var menuDispatcher = (Dispatcher)typeof(PointerPressObserver).GetField("_hookDispatcher", fields)!.GetValue(menuObserver)!;
            check($"menu then right drag {i}: menu hook is not on UI thread", menuDispatcher.Thread != Thread.CurrentThread);
            using var menuAnswered = new ManualResetEventSlim();
            menuDispatcher.BeginInvoke(DispatcherPriority.Send, () => menuAnswered.Set());
            check($"menu then right drag {i}: menu pump answers while UI is blocked", menuAnswered.Wait(TimeSpan.FromSeconds(2)));
            using var drag = (RightFileDrag)Activator.CreateInstance(type, nonPublic: true)!;
            var dispatcher = (Dispatcher)type.GetField("_inputDispatcher", fields)!.GetValue(drag)!;
            var hook = (nint)type.GetField("_hook", fields)!.GetValue(drag)!;
            check($"right drag {i}: native hook installed off the UI thread", hook != 0 && dispatcher.Thread != Thread.CurrentThread);
            using var answered = new ManualResetEventSlim();
            dispatcher.BeginInvoke(DispatcherPriority.Send, () => answered.Set());
            check($"right drag {i}: input pump answers while UI thread cannot pump", answered.Wait(TimeSpan.FromSeconds(2)));
            menuObserver.Dispose();
            check($"menu then right drag {i}: menu closes without waiting for UI", menuDispatcher.Thread.Join(TimeSpan.FromSeconds(2)));
            // There was no physical press in this test: close immediately rather
            // than retaining a hook waiting for a release that will never arrive.
            type.GetField("_released", fields)!.SetValue(drag, true);
            drag.Dispose();
            check($"right drag {i}: disposal shuts down input thread independently", dispatcher.Thread.Join(TimeSpan.FromSeconds(2)));
            check($"right drag {i}: native hook released", (nint)type.GetField("_hook", fields)!.GetValue(drag)! == 0);
        }
    }
}
