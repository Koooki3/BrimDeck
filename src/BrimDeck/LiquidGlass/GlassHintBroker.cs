using System.Runtime.InteropServices;

namespace BrimDeck.LiquidGlass;

// App-wide hints that the backdrop behind liquid glass may have changed (review v2 U12): one EVENT_OBJECT_NAMECHANGE hook for the
// whole process, out of context, instead of one per panel, installed with the first panel that shows glass and removed with the
// last. A title change of the window in front (a browser's tab or page) is told to every subscribed panel once; one window's
// title changes count once per TitleGap for all of them (a player's progress, a download's percentage or a chat's unread count
// retitle every second). Each panel still spaces its own soft bursts, and reads only one capture at a time.
// The hook's callback comes on the thread that installed it (the panels' UI thread); the list is guarded all the same.
internal static class GlassHintBroker
{
    public const int TitleGap = 3000;
    private static readonly object Gate = new();
    private static readonly List<Action> Subscribers = [];
    private static IntPtr _hook;
    private static WinEventProc? _callback;
    private static IntPtr _titleWindow;
    private static long _titleAt = long.MinValue / 2;

    // Whether the process-wide hook is installed now, and how many panels listen (for checks).
    internal static bool Hooked => _hook != IntPtr.Zero;
    internal static int Count { get { lock (Gate) return Subscribers.Count; } }

    public static void Subscribe(Action onTitle)
    {
        lock (Gate)
        {
            if (Subscribers.Contains(onTitle)) return;
            Subscribers.Add(onTitle);
            if (_hook != IntPtr.Zero) return;
            _callback ??= Title;
            _hook = SetWinEventHook(0x800C, 0x800C, IntPtr.Zero, _callback, 0, 0, 0x0002); // OUTOFCONTEXT | SKIPOWNPROCESS
        }
    }

    public static void Unsubscribe(Action onTitle)
    {
        lock (Gate)
        {
            Subscribers.Remove(onTitle);
            if (Subscribers.Count > 0 || _hook == IntPtr.Zero) return;
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private static void Title(IntPtr hook, uint winEvent, IntPtr window, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || window != GetForegroundWindow()) return;
        long now = Environment.TickCount64;
        Action[] told;
        lock (Gate)
        {
            if (window == _titleWindow && now - _titleAt < TitleGap) return;
            _titleWindow = window; _titleAt = now;
            told = [.. Subscribers];
        }
        foreach (var subscriber in told) subscriber();
    }

    private delegate void WinEventProc(IntPtr hook, uint winEvent, IntPtr window, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
}
