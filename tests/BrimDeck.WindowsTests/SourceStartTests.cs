using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using BrimDeck.LiquidGlass;
using BrimDeck.Native;

// The capture made off the UI thread (SourceStart) when it is slow or fails: automatic liquid glass's first look is let go at
// its bound (SourceStart.Within, App.StartGlance: the exclusion released, the look left as shown) and a late answer is not
// reported; the glass waiting for a late worker is called back at the bound (and finds it late), not when the worker ends; an
// earlier attempt failing late does not report a newer one as failed; and a measure asked for while a worker is under way uses
// that worker's source, never a second one. The slow and failing workers are stand-ins (SourceStart.Create); no window shown.
internal static class SourceStartTests
{
    public static void Run(Action<string, bool> check)
    {
        var awareness = SetThreadDpiAwarenessContext(-4);
        var create = SourceStart.Create;
        try
        {
            Settle();
            Within(check);
            Late(check);
            StaleFailure(check);
            OneSource(check);
        }
        finally
        {
            SourceStart.Create = create;
            SourceStart.Bound = SourceStart.BoundMs;
            Settle();
            SourceStart.Take()?.Dispose();
            SetThreadDpiAwarenessContext(awareness);
        }
    }

    private static void Within(Action<string, bool> check)
    {
        // Never answered: let go at the bound, once, and an answer after that is not reported.
        var never = new TaskCompletionSource<SourceStart.Prepared>();
        var calls = new List<SourceStart.Prepared?>();
        var clock = Stopwatch.StartNew();
        long at = -1;
        SourceStart.Within(never.Task, 120, prepared => { calls.Add(prepared); at = clock.ElapsedMilliseconds; });
        Pump(400);
        never.SetResult(new(true, .5, 1));
        Pump(100);
        check($"first look: a measure not back within its bound is let go then ({at} ms for 120), once, its late answer not reported",
            calls.Count == 1 && calls[0] is null && at >= 110 && at < 400);

        // Answered in time: reported once, the bound passing after it changes nothing.
        var soon = new TaskCompletionSource<SourceStart.Prepared>();
        calls.Clear();
        SourceStart.Within(soon.Task, 120, prepared => calls.Add(prepared));
        soon.SetResult(new(true, .25, 1));
        Pump(300);
        check("first look: a measure back in time is reported once, with its brightness", calls.Count == 1 && calls[0]?.Luma == .25);
    }

    private static void Late(Action<string, bool> check)
    {
        SourceStart.Bound = 150;
        SourceStart.Create = () => { Thread.Sleep(500); return null; };
        int called = 0;
        bool lateWhenCalled = false;
        SourceStart.Prepare(Region(), measure: false);
        SourceStart.WhenDone(() => { called++; lateWhenCalled = SourceStart.Late; });
        Pump(300);
        bool atBound = called == 1 && lateWhenCalled && SourceStart.Pending && !SourceStart.TakeFailure();
        Settle();
        Pump(50);
        check($"capture start: the glass waiting for a worker past its bound is called back then and finds it late, not again when it ends (called {called})",
            atBound && called == 1);
        check("capture start: the late worker's failure is reported once it ends, once", SourceStart.TakeFailure() && !SourceStart.TakeFailure());
        SourceStart.Bound = SourceStart.BoundMs;
    }

    private static void StaleFailure(Action<string, bool> check)
    {
        if (DesktopSource.TryCreate() is not { } probe) { Console.WriteLine("SKIP: capture start's stale failure (no capture in this session)."); return; }
        probe.Dispose();
        int calls = 0;
        SourceStart.Create = () => Interlocked.Increment(ref calls) == 1 ? Fail(150) : DesktopSource.TryCreate();
        SourceStart.Prepare(Region(), measure: false);
        var measured = SourceStart.Prepare(Region(), measure: true);
        Settle();
        var source = SourceStart.Take();
        check($"capture start: a worker failing before a newer one succeeds does not report the newer as failed (made {measured.Result.Made})",
            measured.Result.Made && source is not null && !SourceStart.TakeFailure());
        source?.Dispose();
    }

    private static void OneSource(Action<string, bool> check)
    {
        int calls = 0;
        SourceStart.Create = () => { Interlocked.Increment(ref calls); Thread.Sleep(150); return DesktopSource.TryCreate(); };
        int made = DesktopSource.Made;
        var first = SourceStart.Prepare(Region(), measure: false);
        var measured = SourceStart.Prepare(Region(), measure: true);
        Settle();
        if (!first.Result.Made) { Console.WriteLine("SKIP: capture start's one source (no capture in this session)."); return; }
        var source = SourceStart.Take();
        check(FormattableString.Invariant($"capture start: a measure asked for while a worker is under way uses that worker's source (luma {measured.Result.Luma:0.000}; sources made {DesktopSource.Made - made})"),
            calls == 1 && DesktopSource.Made == made + 1 && measured.Result.Made && measured.Result.Luma is >= 0 and <= 1 && source is not null);
        source?.Dispose();
    }

    private static DesktopSource? Fail(int milliseconds) { Thread.Sleep(milliseconds); return null; }

    private static RECT Region() => BackdropLook.Region(WindowsHost.PrimaryScreen().Bounds, MonitorFromPoint(0, 1));

    // Until no worker is under way, the UI thread's messages handled meanwhile.
    private static void Settle()
    {
        var clock = Stopwatch.StartNew();
        while (SourceStart.Pending && clock.ElapsedMilliseconds < SourceStart.BoundMs + 3000) Pump(10);
        Pump(20);
    }

    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        while (clock.ElapsedMilliseconds < milliseconds);
    }

    [DllImport("user32")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32")] private static extern IntPtr MonitorFromPoint(long point, uint flags);
}
