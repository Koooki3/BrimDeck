using System.Diagnostics;
using System.Runtime.InteropServices;
using BrimDeck.LiquidGlass;
using BrimDeck.Native;

// Our windows are left out of Desktop Duplication only while liquid glass is in use (CaptureExclusion): held, the island's
// window is left out at once, a window of ours that can lie over the island and is shown later is left out as it appears (the
// WinEvent hook), and a pop-up of an ordinary window (the settings window's) is not; let go, every window is back in the
// capture and the hook is gone. Then the capture made off the UI thread (SourceStart): asking for it costs the UI thread next
// to nothing, the first look's measure comes back on its own, and the source it made is the one the glass opens (not made
// twice), the exclusion held with it and let go when it is disposed. Tiny never-activated tool windows in the top-left corner,
// each up for well under 2 s, always destroyed.
internal static class CaptureExclusionTests
{
    public static void Run(Action<string, bool> check)
    {
        var awareness = SetThreadDpiAwarenessContext(-4);
        try
        {
            Exclusion(check);
            Start(check);
        }
        finally { SetThreadDpiAwarenessContext(awareness); }
    }

    private static void Exclusion(Action<string, bool> check)
    {
        var life = Stopwatch.StartNew();
        nint island = 0, later = 0, owner = 0, popup = 0, after = 0;
        try
        {
            island = Tool(0);
            check("capture exclusion: nothing is held before liquid glass is used", CaptureExclusion.Users == 0 && !CaptureExclusion.Hooked && CaptureExclusion.Read(island) != true);
            check("capture exclusion: liquid glass shown leaves the island out and installs the hook",
                CaptureExclusion.Acquire([island]) && CaptureExclusion.Hooked && CaptureExclusion.Holds(island) && CaptureExclusion.Read(island) != false);
            later = Tool(0);
            // A pop-up of an ordinary window of ours, as the settings window's menus and tooltips are.
            owner = CreateWindowEx(0, "Static", "", 0x80000000, 0, 0, 8, 8, 0, 0, 0, 0);
            popup = CreateWindowEx(0x80 | 0x8000000, "Static", "", 0x80000000, 0, 0, 8, 8, owner, 0, 0, 0);
            ShowWindow(popup, 4);
            Backdrop.Pump();
            bool? laterRead = CaptureExclusion.Read(later), popupRead = CaptureExclusion.Read(popup);
            check($"capture exclusion: a window of ours shown over the island later is left out as it appears (read {laterRead})", CaptureExclusion.Holds(later) && laterRead != false);
            check($"capture exclusion: a pop-up of an ordinary window (the settings window's) stays in the capture (read {popupRead})", !CaptureExclusion.Holds(popup) && popupRead != true);
            CaptureExclusion.Release();
            bool? islandRead = CaptureExclusion.Read(island);
            laterRead = CaptureExclusion.Read(later);
            check($"capture exclusion: liquid glass gone puts every window back in the capture (read {islandRead}, {laterRead}) and removes the hook",
                CaptureExclusion.Users == 0 && !CaptureExclusion.Hooked && islandRead != true && laterRead != true && !CaptureExclusion.Holds(island) && !CaptureExclusion.Holds(later));
            after = Tool(0);
            Backdrop.Pump();
            check("capture exclusion: a window shown after that stays in the capture", !CaptureExclusion.Holds(after) && CaptureExclusion.Read(after) != true);
        }
        finally
        {
            while (CaptureExclusion.Users > 0) CaptureExclusion.Release();
            foreach (var window in new[] { island, later, popup, owner, after }) if (window != 0) DestroyWindow(window);
            Backdrop.Pump();
            check($"capture exclusion: the test's windows were up {life.ElapsedMilliseconds} ms", life.ElapsedMilliseconds < 2000);
        }
    }

    private static void Start(Action<string, bool> check)
    {
        var bounds = WindowsHost.PrimaryScreen().Bounds;
        var region = BackdropLook.Region(bounds, MonitorFromPoint(0, 1));
        int made = DesktopSource.Made;
        var clock = Stopwatch.StartNew();
        var measure = SourceStart.Prepare(region, measure: true);
        double asked = clock.Elapsed.TotalMilliseconds;
        bool done = measure.Wait(SourceStart.BoundMs + 3000);
        long answered = clock.ElapsedMilliseconds;
        if (!done || !measure.Result.Made) { Console.WriteLine($"SKIP: capture made off the UI thread (no capture in this session, {answered} ms)."); return; }
        var result = measure.Result;
        check(FormattableString.Invariant($"capture start: asking for the first look holds the UI thread {asked:0.0} ms; measured off it (luma {result.Luma:0.000}) in {result.Milliseconds} ms, back after {answered} ms"),
            asked < 20 && result.Luma is >= 0 and <= 1);

        var life = Stopwatch.StartNew();
        nint island = Tool(0);
        try
        {
            bool ready = false;
            var first = DesktopSource.Open(island, 0, region, () => ready = true, out var opening);
            check("capture start: the glass opens the source the first look made, not a second one",
                first is not null && opening == DesktopSource.Opening.Opened && DesktopSource.Made == made + 1 && SourceStart.Take() is null);
            check("capture start: the opened source holds the exclusion", DesktopSource.OpenCount == 1 && CaptureExclusion.Users == 1 && CaptureExclusion.Holds(island));
            first?.Dispose();
            check("capture start: disposed, it lets the exclusion go", DesktopSource.OpenCount == 0 && CaptureExclusion.Users == 0 && !CaptureExclusion.Hooked);

            // No source waiting: the glass asks for one and shows the blur meanwhile; once it is made the glass opens it.
            clock.Restart();
            var none = DesktopSource.Open(island, 0, region, () => ready = true, out opening);
            double call = clock.Elapsed.TotalMilliseconds;
            while (SourceStart.Pending && clock.ElapsedMilliseconds < SourceStart.BoundMs + 3000) { Thread.Sleep(5); Backdrop.Pump(); }
            long made2 = clock.ElapsedMilliseconds;
            Backdrop.Pump();
            using var second = DesktopSource.Open(island, 0, region, () => ready = true, out var opened);
            check(FormattableString.Invariant($"capture start: without one waiting the glass is told to wait ({call:0.0} ms on the UI thread), the source made off it in {made2} ms, then opened"),
                none is null && opening == DesktopSource.Opening.Pending && call < 20 && second is not null && opened == DesktopSource.Opening.Opened && DesktopSource.Made == made + 2);
            // The callback is posted to the UI thread as the source is made, so it may come a pump or two after the source does.
            var callback = Stopwatch.StartNew();
            while (!ready && callback.ElapsedMilliseconds < 1000) { Backdrop.Pump(); Thread.Sleep(5); }
            check(FormattableString.Invariant($"capture start: the glass waiting is called back once the source is there ({callback.ElapsedMilliseconds} ms after it)"), ready);
        }
        finally
        {
            DestroyWindow(island);
            Backdrop.Pump();
        }
        check($"capture start: the test's window was up {life.ElapsedMilliseconds} ms", life.ElapsedMilliseconds < 2000 && DesktopSource.OpenCount == 0 && CaptureExclusion.Users == 0);
    }

    // A tiny tool window that never activates, topmost, in the top-left corner, shown.
    private static nint Tool(int x)
    {
        var window = CreateWindowEx(0x80 | 0x8000000 | 0x8, "Static", "", 0x80000000, x, 0, 8, 8, 0, 0, 0, 0);
        if (window == 0) throw new InvalidOperationException("No test window.");
        ShowWindow(window, 4); // SW_SHOWNOACTIVATE
        Backdrop.Pump();
        return window;
    }

    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(int exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32")] private static extern IntPtr MonitorFromPoint(long point, uint flags);
}
