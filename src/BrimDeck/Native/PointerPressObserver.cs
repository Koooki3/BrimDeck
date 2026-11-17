using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace BrimDeck.Native;

// Observe presses only while a popup is open. Always pass input through to its
// destination, including windows owned by another process.
internal sealed class PointerPressObserver : IDisposable
{
    private readonly HookProc _callback;
    private nint _hook;
    private Dispatcher? _hookDispatcher;
    private int _disposed;
    public PointerPressObserver(Dispatcher dispatcher, Action<Point> pressed)
    {
        _callback = (code, message, data) =>
        {
            if (Volatile.Read(ref _disposed) == 0 && code >= 0 && message.ToInt32() is 0x0201 or 0x0204 or 0x0207 or 0x020B && !dispatcher.HasShutdownStarted)
            {
                var point = Marshal.PtrToStructure<POINT>(data);
                // A popup may close before this runs. Deliver this already observed
                // press so its owner can still collapse on the same outside click.
                dispatcher.BeginInvoke(DispatcherPriority.Input, () => pressed(new Point(point.X, point.Y)));
            }
            return CallNextHookEx(_hook, code, message, data);
        };
        // A popup can remain alive through its closing animation while OLE starts
        // on the UI thread. Hooks installed there would stall global mouse input
        // whenever OLE or SendInput waits. Keep this observer's pump independent.
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _hookDispatcher = Dispatcher.CurrentDispatcher;
            _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
            ready.Set();
            if (_hook == 0) return;
            try { Dispatcher.Run(); }
            finally { UnhookWindowsHookEx(_hook); _hook = 0; }
        }) { IsBackground = true, Name = "BrimDeck popup input" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _hook == 0) return;
        _hookDispatcher!.BeginInvokeShutdown(DispatcherPriority.Send);
    }
    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
