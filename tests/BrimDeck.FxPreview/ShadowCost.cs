using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

// What the island's shadow costs while the panel opens and closes over liquid glass (MainWindow.LiquidShadow keeps it drawn):
// the real MainWindow, offscreen as in LiquidPages (never shown, GlassBackdrop deciding through its stand-in host), opened and
// closed with animations on, with its shadow and with ShadowHost collapsed (no shadow at all), the two taken in turn. Measured:
// the UI thread's and the process's CPU time over the cycles, in two ways. "layout": the source is never shown, so WPF draws
// nothing; this is the UI thread's share (animation clocks, layout, the shadow's cache being dropped and kept). "raster": each
// frame in which the island or its shadow changed is also drawn with RenderTargetBitmap (WPF's software rasterizer, the
// shadow's blur included): an upper bound for drawing it, which the app does on the GPU. A frame in which only the shadow
// fades while its picture is kept (the cache) only composes it and is not drawn. The panel is a preview, so the user's screen
// (a full-screen window in front) does not keep it from opening.
//   dotnet run -c Release --no-build --project tests/BrimDeck.FxPreview -- --shadow-cost [cycles]
// SHADOW_COST_DEBUG=1 runs one cycle with the shadow and lists its frames.
internal static class ShadowCost
{
    private const double Scale = 1.5;

    public static int Run(int cycles)
    {
        var (app, deck, root, source) = LiquidPages.Offscreen(animations: true);
        using var _ = source;
        LiquidPages.UseLook(app, deck, root, PanelThemes.GlassDark);
        deck.OffscreenMedia(LiquidPages.Track(), LiquidPages.Cover());
        deck.SelectPage(DeckPage.Music, remember: false);
        // A preview (as the settings window shows the panel): what lies in the foreground on the user's screen (a full-screen
        // window hides the island) then does not decide whether the panel opens.
        deck.Preview(true, expanded: false);
        deck.OffscreenLiquidFrame();
        LiquidPages.Settle(root, deck);
        var state = deck.OffscreenGlassState;
        Console.WriteLine($"Glass: liquid shown {state.Shown}, surface aside {state.Aside}, shadow caster opaque {state.Caster}; animations {Soft.Enabled}");
        if (!state.Shown || !Soft.Enabled) { Console.WriteLine("FAIL: liquid glass or animations not on; nothing to measure"); return 1; }

        // Warm up (JIT, the first layout of the open page) once each way, then the measured rounds alternate.
        foreach (var raster in Debug ? new[] { true } : new[] { false, true })
        {
            Measure(deck, root, shadow: true, raster, 1);
            if (Debug) return 0;
            Measure(deck, root, shadow: false, raster, 1);
            var results = new Dictionary<bool, List<Round>> { [true] = [], [false] = [] };
            for (int round = 0; round < Rounds; round++)
                foreach (var shadow in new[] { true, false })
                    results[shadow].Add(Measure(deck, root, shadow, raster, cycles));
            Console.WriteLine($"--- {(raster ? "raster: each frame in which the island or its shadow changed drawn in software (RenderTargetBitmap)" : "layout: nothing drawn (the UI thread's share)")}; "
                + $"{cycles} open+close cycles a round, {Rounds} rounds each way, medians a cycle");
            foreach (var shadow in new[] { true, false })
            {
                var r = results[shadow];
                Console.WriteLine($"{(shadow ? "shadow   " : "no shadow")}: UI thread {Median(r.Select(x => x.Ui / cycles)):0.0} ms, process {Median(r.Select(x => x.Cpu / cycles)):0.0} ms"
                    + $" ({Median(r.Select(x => x.Cpu / x.Wall * 1000)):0.0} ms/s over the cycle), {r.Average(x => x.Frames) / cycles:0} frames"
                    + (raster ? $", {r.Average(x => x.Drawn) / cycles:0} drawn in {Median(r.Select(x => x.Draw / cycles)):0.0} ms ({Median(r.Select(x => x.Draw / Math.Max(1, x.Drawn))):0.00} ms a frame)" : "")
                    + $"; UI thread by round {string.Join(" / ", r.Select(x => (x.Ui / cycles).ToString("0")))}; shadow opacity once open {r.Min(x => x.OpenShadow):0.00}..{r.Max(x => x.OpenShadow):0.00}");
            }
            string Adds(Func<Round, double> of) { double with = Median(results[true].Select(of)), without = Median(results[false].Select(of)); return $"{(with - without) / without:+0%;-0%}"; }
            Console.WriteLine($"shadow adds: UI thread {Adds(x => x.Ui)}, process {Adds(x => x.Cpu)}" + (raster ? $", drawing {Adds(x => x.Draw)}" : ""));
        }
        return 0;
    }

    private sealed record Round(double Ui, double Cpu, double Wall, int Frames, int Drawn, double Draw, double OpenShadow);

    private static Round Measure(MainWindow deck, System.Windows.Controls.Grid root, bool shadow, bool raster, int cycles)
    {
        deck.ShadowHost.Visibility = shadow ? Visibility.Visible : Visibility.Collapsed;
        Wait(300);
        int frames = 0, drawn = 0;
        var draw = new Stopwatch();
        (Rect, double) last = default;
        void Frame(object? sender, EventArgs e)
        {
            frames++;
            if (!raster) return;
            // WPF draws again what changed: while the island moves or its shadow fades. At rest (the marquee, the equalizer) the
            // shadow is a kept picture and is not drawn again, so those frames are left out for both.
            // A fade of the shadow alone while its picture is kept (ShadowHost's BitmapCache) only composes that picture.
            var now = (deck.Island.TransformToAncestor(root).TransformBounds(new Rect(deck.Island.RenderSize)), deck.ShadowHost.Opacity);
            bool kept = deck.ShadowHost.CacheMode is not null && now.Item1 == last.Item1;
            if (Debug) Console.WriteLine($"  {Environment.TickCount64 % 100000} island {now.Item1} shadow {now.Item2:0.00} kept {kept}");
            if (now == last || kept) { last = now; return; }
            last = now;
            drawn++;
            draw.Start();
            int width = (int)Math.Ceiling(root.ActualWidth * Scale), height = (int)Math.Ceiling(root.ActualHeight * Scale);
            var bitmap = new RenderTargetBitmap(width, height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            bitmap.Render(deck.Stage);
            draw.Stop();
        }
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpu0 = process.TotalProcessorTime;
        double ui0 = ThreadMs();
        var wall = Stopwatch.StartNew();
        double openShadow = 0;
        CompositionTarget.Rendering += Frame;
        for (int i = 0; i < cycles; i++)
        {
            deck.SetExpanded(true);
            Wait(1100);
            openShadow = i == 0 ? deck.ShadowHost.Opacity : Math.Min(openShadow, deck.ShadowHost.Opacity);
            if (deck.Island.ActualHeight < 100) throw new InvalidOperationException($"the panel did not open (island {deck.Island.ActualWidth:0} x {deck.Island.ActualHeight:0})");
            deck.SetExpanded(false);
            Wait(1100);
        }
        CompositionTarget.Rendering -= Frame;
        wall.Stop();
        double ui = ThreadMs() - ui0;
        process.Refresh();
        double cpu = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
        deck.ShadowHost.Visibility = Visibility.Visible;
        return new Round(ui, cpu, wall.Elapsed.TotalMilliseconds, frames, drawn, draw.Elapsed.TotalMilliseconds, openShadow);
    }

    // The UI thread's own CPU time (kernel and user), milliseconds.
    private static double ThreadMs()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user);
        return (kernel + user) / 10_000d;
    }
    [System.Runtime.InteropServices.DllImport("kernel32")] private static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32")] private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    private const int Rounds = 7;
    private static readonly bool Debug = Environment.GetEnvironmentVariable("SHADOW_COST_DEBUG") == "1";
    private static double Median(IEnumerable<double> values) { var v = values.Order().ToList(); return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2; }

    private static void Wait(double milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
