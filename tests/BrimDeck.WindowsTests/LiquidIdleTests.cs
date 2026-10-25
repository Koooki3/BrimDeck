using System.Diagnostics;
using System.Runtime.InteropServices;
using BrimDeck.Core;
using BrimDeck.LiquidGlass;
using Vortice.Direct3D11;
using Vortice.DXGI;

// What liquid glass costs while the island holds still and animates (a scrolling title, the equalizer), over a desktop that does
// not change: the island's own redraws are faked by a small window at the top of the primary monitor, left out of the capture as
// BrimDeck's windows are (DesktopSource.Exclude) and nearly invisible (1/255 opaque), redrawn 30 times a second. The idle poll
// (LiquidIdle) runs as GlassBackdrop runs it, with the measure of the backdrop read on every tick (BackdropLook): the glass may
// be drawn again at most once a second, and the poll's own work must stay under a budget. For comparison the poll as it was
// (a comparison out polled every SettleMs) runs over the same redraws first; both are printed.
internal static class LiquidIdleTests
{
    private const int Seconds = 4;
    // The poll's own work, in milliseconds of the thread per second (1.5 % of a core).
    private const double BudgetMs = 15;

    public static void Run(Action<string, bool> check)
    {
        var awareness = SetThreadDpiAwarenessContext(-4);
        try { Check(check); }
        finally { SetThreadDpiAwarenessContext(awareness); }
    }

    private static void Check(Action<string, bool> check)
    {
        using var source = DesktopSource.TryCreate();
        if (source is null) { Console.WriteLine("SKIP: liquid idle cost (no desktop capture)."); return; }
        // Half way down the primary monitor rather than at its top, where a BrimDeck already running would be animating.
        int sw = GetSystemMetrics(0), top = GetSystemMetrics(1) / 2;
        const int width = 342, height = 60, pad = 72;
        var region = new RECT(sw / 2 - width / 2 - pad, top - pad, sw / 2 + width / 2 + pad, top + height + pad);
        // The fake island: a static control as a popup over the island's place, layered at 1/255, left out of the capture.
        var window = CreateWindowEx(0x80000 | 0x8 | 0x80 | 0x8000000 | 0x20, "Static", "a", 0x80000000, sw / 2 - width / 2, top, width, height, 0, 0, 0, 0);
        if (window == 0) { Console.WriteLine("SKIP: liquid idle cost (no window)."); return; }
        try
        {
            SetLayeredWindowAttributes(window, 0, 1, 2);
            ShowWindow(window, 4);
            DesktopSource.Exclude(window);
            using var renderer = new LiquidRenderer(source.Device);
            using var target = source.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, width, height, 1, 1, BindFlags.RenderTarget));
            using var view = source.Device.CreateRenderTargetView(target);
            var frame = LiquidFrame.Preset(true, width, height, 30, 30, 0, 1.5f);
            void Render(ID3D11Texture2D background) => renderer.Draw(background, pad, frame, view);
            if (!source.Update(region, out _) || source.Texture is null) { Console.WriteLine("SKIP: liquid idle cost (capture refused)."); return; }

            // The desktop there must hold still for the check to mean anything: without the fake redraws first.
            var still = Phase(source, renderer, region, 0, Render, old: false);
            if (still.Renders > .5) { Console.WriteLine(FormattableString.Invariant($"SKIP: liquid idle cost (the desktop under the region changes by itself, {still.Renders:0.0} renders/s).")); return; }
            var before = Phase(source, renderer, region, window, Render, old: true);
            var after = Phase(source, renderer, region, window, Render, old: false);
            Console.WriteLine(FormattableString.Invariant($"liquid idle, still desktop without redraws: {still.Ticks:0} ticks/s, {still.Renders:0.0} renders/s, {still.WorkMs:0.0} ms/s, process {still.ProcessMs:0.0} ms/s"));
            Console.WriteLine(FormattableString.Invariant($"liquid idle, 30 fps self-redraw over a still desktop: before {before.Ticks:0} ticks/s, {before.Renders:0.0} renders/s, {before.WorkMs:0.0} ms/s, process {before.ProcessMs:0.0} ms/s; after {after.Ticks:0} ticks/s, {after.Renders:0.0} renders/s, {after.WorkMs:0.0} ms/s, process {after.ProcessMs:0.0} ms/s"));
            // The island followed frame by frame (an animation that moves nothing of the glass) at 60 frames a second.
            var followBefore = Phase(source, renderer, region, window, Render, old: true, follow: true, frame);
            var followAfter = Phase(source, renderer, region, window, Render, old: false, follow: true, frame);
            Console.WriteLine(FormattableString.Invariant($"liquid idle, following at 60 fps with the glass unchanged: before {followBefore.Renders:0.0} renders/s, {followBefore.WorkMs:0.0} ms/s, process {followBefore.ProcessMs:0.0} ms/s; after {followAfter.Renders:0.0} renders/s, {followAfter.WorkMs:0.0} ms/s, process {followAfter.ProcessMs:0.0} ms/s"));
            check(FormattableString.Invariant($"liquid idle: following frames that change nothing of the glass draw it at most once a second ({followAfter.Renders:0.0}/s)"), followAfter.Renders <= 1);
            check(FormattableString.Invariant($"liquid idle: following such frames costs under {BudgetMs} ms a second ({followAfter.WorkMs:0.0} ms)"), followAfter.WorkMs <= BudgetMs);
            check(FormattableString.Invariant($"liquid idle: the glass is drawn at most once a second while only the island redraws ({after.Renders:0.0}/s)"), after.Renders <= 1);
            check(FormattableString.Invariant($"liquid idle: the poll's work stays under {BudgetMs} ms a second ({after.WorkMs:0.0} ms, {after.Ticks:0} ticks)"), after.WorkMs <= BudgetMs);
        }
        finally { DestroyWindow(window); }
    }

    private static (double Ticks, double Renders, double WorkMs, double ProcessMs) Phase(DesktopSource source, LiquidRenderer renderer, RECT region, nint window, Action<ID3D11Texture2D> render, bool old, bool follow = false, LiquidFrame frame = default)
    {
        long drawn = 0;
        var idle = new LiquidIdle();
        var look = new BackdropLook();
        var clock = Stopwatch.StartNew();
        long start = (long)clock.Elapsed.TotalMilliseconds;
        idle.Start(start);
        // Settle the first seconds' fast polling (the island just stopped moving) before counting.
        long countFrom = start + LiquidIdle.IdleAfterMs + 200, end = countFrom + Seconds * 1000;
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
            if (now >= nextTick)
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
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
