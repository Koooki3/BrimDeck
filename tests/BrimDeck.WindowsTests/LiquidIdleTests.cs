using System.Diagnostics;
using System.Runtime.InteropServices;
using BrimDeck.Core;
using BrimDeck.LiquidGlass;
using Vortice.Direct3D11;
using Vortice.DXGI;

// What liquid glass costs while the island holds still and animates (a scrolling title, the equalizer), over a desktop that does
// not change: a small opaque backdrop window of one color (Backdrop) in a corner of the primary monitor holds the desktop
// still under the region whatever else the screen shows, and the island's own redraws are faked by a small window over it, left
// out of the capture as BrimDeck's windows are (DesktopSource.Exclude) and nearly invisible (1/255 opaque), redrawn 30 times a
// second. Each phase makes its own fake island and destroys it as it ends, so no window of the test stays up longer than a
// phase (under 2 s). The idle poll (LiquidIdle) runs as GlassBackdrop runs it, with the measure of the backdrop read on every tick
// (BackdropLook): the glass may be drawn again at most once a second, and the poll's own work, and the whole process's time
// beyond what the test's own loop costs (the same loop with nothing polled), must stay under budgets. For comparison the poll
// as it was (a comparison out polled every SettleMs) runs over the same redraws first; both are printed. Also the poll's pace
// with the pointer resting on the island.
internal static class LiquidIdleTests
{
    // Each phase counts for a second after a short settling, and shows its backdrop only for that long (under 2 s).
    private const int Seconds = 1;
    // The poll's own work, in milliseconds of the thread per second (1.5 % of a core); the process's time beyond the test's own
    // loop (the graphics driver's threads submitting what the poll asks of the GPU included).
    private const double BudgetMs = 15, ProcessBudgetMs = 25;

    public static void Run(Action<string, bool> check)
    {
        var awareness = SetThreadDpiAwarenessContext(-4);
        try { Check(check); }
        finally { SetThreadDpiAwarenessContext(awareness); }
    }

    private static void Check(Action<string, bool> check)
    {
        // The pointer resting on the island changes nothing: after PointerRestMs the poll is at the idle pace, as with no pointer.
        var pace = new LiquidIdle();
        pace.Start(0);
        long late = LiquidIdle.IdleAfterMs + 500;
        check("liquid idle: a pointer moving over the island polls at PollMs", pace.Interval(LiquidIdle.PointerActive(true, late - 100, late), false, late) == LiquidIdle.PollMs);
        check("liquid idle: a pointer resting on the island polls at the idle pace", pace.Interval(LiquidIdle.PointerActive(true, late - LiquidIdle.PointerRestMs - 1, late), false, late) == LiquidIdle.IdlePollMs);

        using var source = DesktopSource.TryCreate();
        if (source is null) { Console.WriteLine("SKIP: liquid idle cost (no desktop capture)."); return; }
        const int width = 120, height = 24, pad = 24;
        // In the corner of the primary monitor, over a backdrop of one color (shown by each phase) that holds the desktop there still.
        var region = new RECT(10, 10, 10 + width + 2 * pad, 10 + height + 2 * pad);
        _island = new RECT(region.Left + pad, region.Top + pad, region.Left + pad + width, region.Top + pad + height);
        _islands = 0; _excluded = 0; _longestMs = 0;
        {
            using var renderer = new LiquidRenderer(source.Device);
            using var target = source.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, width, height, 1, 1, BindFlags.RenderTarget));
            using var view = source.Device.CreateRenderTargetView(target);
            var frame = LiquidFrame.Preset(true, width, height, 20, 20, 0, 1);
            bool Render(ID3D11Texture2D background) { renderer.Draw(background, pad, frame, view); return true; }
            bool captured = false;
            using (new Backdrop(Backdrop.Corner(200, 100), 90, 120, 150))
                for (int i = 0; i < 40 && !captured; i++) { captured = source.Update(region, out _) && source.Texture is not null; if (!captured) Thread.Sleep(25); }
            if (!captured) { Console.WriteLine("SKIP: liquid idle cost (capture refused)."); return; }

            // The test's own loop, with nothing polled, without and with the redraws: what the process spends on it is not the poll's.
            var quiet = Phase(source, renderer, region, false, Render, old: false, none: true);
            var harness = Phase(source, renderer, region, true, Render, old: false, none: true);
            // The desktop there holds still (the backdrop): without the fake redraws first.
            var still = Phase(source, renderer, region, false, Render, old: false);
            var before = Phase(source, renderer, region, true, Render, old: true);
            var after = Phase(source, renderer, region, true, Render, old: false);
            Console.WriteLine(FormattableString.Invariant($"liquid idle, the test's own loop with nothing polled: process {quiet.ProcessMs:0.0} ms/s, with 30 fps self-redraw {harness.ProcessMs:0.0} ms/s"));
            Console.WriteLine(FormattableString.Invariant($"liquid idle, still desktop without redraws: {still.Ticks:0} ticks/s, {still.Renders:0.0} renders/s, {still.WorkMs:0.0} ms/s, process {still.ProcessMs:0.0} ms/s"));
            Console.WriteLine(FormattableString.Invariant($"liquid idle, 30 fps self-redraw over a still desktop: before {before.Ticks:0} ticks/s, {before.Renders:0.0} renders/s, {before.WorkMs:0.0} ms/s, process {before.ProcessMs:0.0} ms/s; after {after.Ticks:0} ticks/s, {after.Renders:0.0} renders/s, {after.WorkMs:0.0} ms/s, process {after.ProcessMs:0.0} ms/s"));
            // The island followed frame by frame (an animation that moves nothing of the glass) at 60 frames a second.
            var followBefore = Phase(source, renderer, region, true, Render, old: true, follow: true, frame);
            var followAfter = Phase(source, renderer, region, true, Render, old: false, follow: true, frame);
            Console.WriteLine(FormattableString.Invariant($"liquid idle, following at 60 fps with the glass unchanged: before {followBefore.Renders:0.0} renders/s, {followBefore.WorkMs:0.0} ms/s, process {followBefore.ProcessMs:0.0} ms/s; after {followAfter.Renders:0.0} renders/s, {followAfter.WorkMs:0.0} ms/s, process {followAfter.ProcessMs:0.0} ms/s"));
            check(FormattableString.Invariant($"liquid idle: each phase's fake island is made, left out of the capture and destroyed with it ({_excluded}/{_islands}; the longest phase {_longestMs} ms, under 2 s)"),
                _islands == 5 && _excluded == _islands && _longestMs < 2000);
            check(FormattableString.Invariant($"liquid idle: the backdrop holds the desktop under the region still ({still.Renders:0.0} renders/s)"), still.Renders <= .5);
            check(FormattableString.Invariant($"liquid idle: following frames that change nothing of the glass draw it at most once a second ({followAfter.Renders:0.0}/s)"), followAfter.Renders <= 1);
            check(FormattableString.Invariant($"liquid idle: following such frames costs under {BudgetMs} ms a second ({followAfter.WorkMs:0.0} ms)"), followAfter.WorkMs <= BudgetMs);
            check(FormattableString.Invariant($"liquid idle: the glass is drawn at most once a second while only the island redraws ({after.Renders:0.0}/s)"), after.Renders <= 1);
            check(FormattableString.Invariant($"liquid idle: the poll's work stays under {BudgetMs} ms a second ({after.WorkMs:0.0} ms, {after.Ticks:0} ticks)"), after.WorkMs <= BudgetMs);
            check(FormattableString.Invariant($"liquid idle: the process spends under {ProcessBudgetMs} ms a second on the poll over a still desktop ({still.ProcessMs - quiet.ProcessMs:0.0} ms beyond the loop's)"), still.ProcessMs - quiet.ProcessMs <= ProcessBudgetMs);
            check(FormattableString.Invariant($"liquid idle: and while the island redraws ({after.ProcessMs - harness.ProcessMs:0.0} ms beyond the loop's)"), after.ProcessMs - harness.ProcessMs <= ProcessBudgetMs);
            check(FormattableString.Invariant($"liquid idle: and while following frames that change nothing ({followAfter.ProcessMs - harness.ProcessMs:0.0} ms beyond the loop's)"), followAfter.ProcessMs - harness.ProcessMs <= ProcessBudgetMs);
        }
    }

    // The fake island's place, and per run: islands made, those left out of the capture, the longest phase (its windows' life).
    private static RECT _island;
    private static int _islands, _excluded;
    private static long _longestMs;

    // The fake island: a static control as a popup over the island's place, layered at 1/255, left out of the capture.
    private static nint FakeIsland()
    {
        var window = CreateWindowEx(0x80000 | 0x8 | 0x80 | 0x8000000 | 0x20, "Static", "a", 0x80000000, _island.Left, _island.Top, _island.Width, _island.Height, 0, 0, 0, 0);
        if (window == 0) return 0;
        _islands++;
        SetLayeredWindowAttributes(window, 0, 1, 2);
        ShowWindow(window, 4);
        if (DesktopSource.Exclude(window)) _excluded++;
        return window;
    }

    // none: the loop and the redraws only, nothing captured or drawn.
    private static (double Ticks, double Renders, double WorkMs, double ProcessMs) Phase(DesktopSource source, LiquidRenderer renderer, RECT region, bool island, Func<ID3D11Texture2D, bool> render, bool old, bool follow = false, LiquidFrame frame = default, bool none = false)
    {
        var life = Stopwatch.StartNew();
        nint window = 0;
        try
        {
            using var backdrop = new Backdrop(Backdrop.Corner(200, 100), 90, 120, 150);
            if (island && (window = FakeIsland()) == 0) throw new InvalidOperationException("No fake island window.");
            return Measure(source, renderer, region, window, render, old, follow, frame, none);
        }
        finally
        {
            if (window != 0) DestroyWindow(window);
            _longestMs = Math.Max(_longestMs, life.ElapsedMilliseconds);
        }
    }

    private static (double Ticks, double Renders, double WorkMs, double ProcessMs) Measure(DesktopSource source, LiquidRenderer renderer, RECT region, nint window, Func<ID3D11Texture2D, bool> render, bool old, bool follow, LiquidFrame frame, bool none)
    {
        long drawn = 0;
        var idle = new LiquidIdle();
        var look = new BackdropLook();
        var clock = Stopwatch.StartNew();
        long start = (long)clock.Elapsed.TotalMilliseconds;
        // As if the island had held still for a while already (the idle pace), and a moment for the backdrop's arrival to settle.
        idle.Start(start - LiquidIdle.IdleAfterMs);
        long countFrom = start + 300, end = countFrom + Seconds * 1000;
        long nextTick = start, nextRedraw = start, ticks0 = 0, renders0 = 0;
        double work = 0;
        ulong cpu0 = 0;
        bool counting = false, shown = true;
        int redraw = 0;
        while (true)
        {
            long now = (long)clock.Elapsed.TotalMilliseconds;
            if (now >= end) break;
            if (!counting && now >= countFrom) { counting = true; ticks0 = idle.Ticks; renders0 = idle.Renders + idle.FrameRenders + drawn; work = 0; QueryProcessCycleTime(GetCurrentProcess(), out cpu0); }
            if (window != 0 && now >= nextRedraw)
            {
                // A new frame of the window each time, as the island's own redraws give the compositor (1 or 2 / 255 opaque).
                SetLayeredWindowAttributes(window, 0, (byte)(1 + (++redraw & 1)), 2);
                SetWindowText(window, (redraw & 1) == 0 ? "a" : "b");
                InvalidateRect(window, 0, true);
                UpdateWindow(window);
                nextRedraw += 33;
            }
            if (now >= nextTick && none) nextTick = now + 16;
            else if (now >= nextTick)
            {
                long t0 = Stopwatch.GetTimestamp();
                if (!follow) idle.Tick(source, region, render, now);
                // Following frames as GlassBackdrop.Step does, the glass unchanged: before, each frame captured and drew.
                else if (old) { source.Update(region, out _); render(source.Texture!); drawn++; }
                else idle.Frame(source, region, frame, render, now);
                if (look.Step(renderer, now, shown, out _)) shown = !shown;
                work += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                int interval = follow ? 16 : old
                    ? source.Comparing || renderer.Measuring ? LiquidIdle.SettleMs : idle.Interval(false, false, now)
                    : idle.Interval(false, renderer.Measuring, now);
                nextTick = now + interval;
            }
            while (PeekMessage(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
            Thread.Sleep(1);
        }
        // The whole process's processor time too: the graphics driver's own threads submit what the poll asks of the GPU.
        QueryProcessCycleTime(GetCurrentProcess(), out ulong cpu1);
        double process = (cpu1 - cpu0) / CyclesPerMs() / Seconds;
        return ((idle.Ticks - ticks0) / (double)Seconds, (idle.Renders + idle.FrameRenders + drawn - renders0) / (double)Seconds, work / Seconds, process);
    }

    // Processor cycles in a millisecond of this thread's own work (a short spin), to turn the process's cycles into time.
    private static double _cyclesPerMs;
    private static double CyclesPerMs()
    {
        if (_cyclesPerMs > 0) return _cyclesPerMs;
        QueryThreadCycleTime(GetCurrentThread(), out ulong a);
        var spin = Stopwatch.StartNew();
        while (spin.Elapsed.TotalMilliseconds < 50) { }
        QueryThreadCycleTime(GetCurrentThread(), out ulong b);
        return _cyclesPerMs = (b - a) / spin.Elapsed.TotalMilliseconds;
    }
    [DllImport("kernel32")] private static extern bool QueryProcessCycleTime(nint process, out ulong cycles);
    [DllImport("kernel32")] private static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);
    [DllImport("kernel32")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32")] private static extern nint GetCurrentThread();
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint W, L; public uint Time; public int X, Y; }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(int exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32")] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(nint window, string text);
    [DllImport("user32")] private static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport("user32")] private static extern bool UpdateWindow(nint window);
    [DllImport("user32")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
