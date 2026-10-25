using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.LiquidGlass;
using Vortice.Direct3D11;

// What liquid glass costs the process against the old blur (BRIMDECK_LIQUID=0), in three scenarios: the collapsed island idle
// with music playing (the equalizer and the progress ticking, the title scrolling), the open panel idle (the music page), and
// the panel opening and closing. The real MainWindow, offscreen as in LiquidPages (animations on, a playing track), its stage
// in a layered tool window placed beyond the virtual screen (WPF renders and reads it back as it does the real layered panel;
// nothing of it is on any monitor), or never shown with --hidden. Liquid glass is driven as GlassBackdrop drives it: a real
// DesktopSource and LiquidHost (Desktop Duplication, the renderer and its composition swap chain), followed frame by frame
// while the island's frame changes and polled (LiquidIdle) once it held still for StillFrames, the region placed as the island
// would be at the top of the primary monitor but at its top-left corner. The island's own redraws, which Desktop Duplication
// reports over the region although its pixels are left out, are made by a fake island: a 64 px tool window at the corner, 1/255
// opaque, left out of the capture, redrawn 30 times a second and replaced every 1.8 s (no window lives 2 s). Blur: GlassBackdrop
// decides "no liquid glass" (its stand-in host), so the island keeps its own surface, painted rim and ambient drift; DWM's
// blur is drawn in dwm.exe and is not in these numbers. The part both share (GlassBackdrop's Measure and its window's clip
// and region while following) is in neither, so the difference is liquid glass's own.
//   dotnet run -c Release --no-build --project tests/BrimDeck.FxPreview -- --perf [seconds] [rounds] [--hidden] [--only liquid|blur]
// Each round measures both ways in turn; medians over the rounds.
internal static class PerfCost
{
    private const int StillFrames = 12, Fake = 64;
    private static readonly int[] Order = [0, 1, 2];
    private static readonly string[] Names = ["collapsed idle, music", "open idle (music page)", "open/close animation"];

    private sealed record Sample(double CpuPct, double UiPct, double GpuMsPerS, double WpfFps, double Renders, double Ticks, double Captures, int Follows, double Seconds, double ProcessGpu);

    public static int Run(string[] args)
    {
        var numbers = args.Skip(1).Where(a => int.TryParse(a, out _)).Select(int.Parse).ToList();
        int seconds = numbers.Count > 0 ? numbers[0] : 6, rounds = numbers.Count > 1 ? numbers[1] : 3;
        bool hidden = args.Contains("--hidden");
        int only = Array.IndexOf(args, "--only") is int at and >= 0 && at + 1 < args.Length ? args[at + 1] == "liquid" ? 1 : 0 : -1;
        SetThreadDpiAwarenessContext(-4);
        var (app, deck, root, offscreen) = LiquidPages.Offscreen(animations: true);
        LiquidPages.UseLook(app, deck, root, PanelThemes.GlassDark);
        var track = LiquidPages.Track() with { State = MediaState.Playing, PositionAt = DateTimeOffset.UtcNow };
        deck.OffscreenMedia(track, LiquidPages.Cover());
        deck.SelectPage(DeckPage.Music, remember: false);
        deck.Preview(true, expanded: false);
        bool liquidOn = true;
        deck.OffscreenGlassBackdrop!.StandIn = () => liquidOn;
        deck.OffscreenLiquidFrame();
        LiquidPages.Settle(root, deck);
        HwndSource? shown = null;
        if (!hidden)
        {
            // The stage in a layered tool window beyond the virtual screen, as the real panel's window is layered.
            offscreen.RootVisual = null;
            int width = (int)Math.Ceiling(root.ActualWidth * 1.5), height = (int)Math.Ceiling(root.ActualHeight * 1.5);
            int x = (int)(SystemParameters.VirtualScreenLeft * 1.5) - width - 4000;
            shown = new HwndSource(new HwndSourceParameters("BrimDeck.Perf")
            {
                Width = width, Height = height, PositionX = x, PositionY = 0, UsesPerPixelOpacity = true,
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80 | 0x8000000,
            }) { RootVisual = root, SizeToContent = SizeToContent.Manual };
            VisualTreeHelper.SetRootDpi(root, new DpiScale(1.5, 1.5));
            ShowWindow(shown.Handle, 4);
            GetWindowRect(shown.Handle, out var placed);
            Console.WriteLine($"Stage shown in a layered tool window at {placed.Left},{placed.Top} .. {placed.Right},{placed.Bottom} (virtual screen {SystemParameters.VirtualScreenLeft * 1.5:0}..{(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth) * 1.5:0} at 150%)");
            LiquidPages.Settle(root, deck);
        }
        else Console.WriteLine("Stage never shown (--hidden): WPF lays out and animates but draws nothing.");

        // The music as the window's own _musicTick drives it (it does not run for a window never shown): TickMusic every 33 ms,
        // the collapsed title and the open page's lyric scrolling.
        var tickMusic = typeof(MainWindow).GetMethod("TickMusic", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var marqueeField = typeof(MainWindow).GetField("_compactMarquee", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var lyricField = typeof(MainWindow).GetField("_lyricCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var music = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(33) };
        music.Tick += (_, _) =>
        {
            tickMusic.Invoke(deck, null);
            (marqueeField.GetValue(deck) as MusicMarquee)?.SetScrolling(true);
            (lyricField.GetValue(deck) as MusicMarquee)?.SetScrolling(true);
        };
        music.Start();

        using var source = DesktopSource.TryCreate();
        if (source is null) { Console.WriteLine("FAIL: no desktop capture"); return 1; }
        using var host = new LiquidHost(source);
        // The monitor corner the island's region is placed at: the one whose desktop changed least in a short look (or
        // --corner <monitor*4 + corner>, corner 0..3: top-left, top-right, bottom-left, bottom-right).
        Collapsed = IslandRect(deck, root);
        deck.SetExpanded(true, instant: true); LiquidPages.Settle(root, deck);
        Open = IslandRect(deck, root);
        deck.SetExpanded(false, instant: true); LiquidPages.Settle(root, deck);
        Console.WriteLine($"Island in device pixels: collapsed {Collapsed}, open {Open}");
        var monitors = Monitors();
        int cornerAt = Array.IndexOf(args, "--corner");
        int pick = cornerAt >= 0 && cornerAt + 1 < args.Length ? int.Parse(args[cornerAt + 1], CultureInfo.InvariantCulture) : Stillest(source, monitors);
        Corner = pick % 4; Monitor = monitors[pick / 4];
        Console.WriteLine($"Region at corner {Corner} ({((Corner & 2) != 0 ? "bottom" : "top")}-{((Corner & 1) != 0 ? "right" : "left")}) of monitor {Monitor}");
        var pipe = new Pipe(deck, root, host);
        CyclesPerMs();
        GlassWindow? glassWindow = null;
        if (!hidden)
        {
            glassWindow = new GlassWindow(shown!.Handle);
            pipe.Attach(glassWindow);
            Console.WriteLine("Glass window: a composition target beyond the virtual screen beside the stage, its sprite showing the renderer's swap chain (liquid) or the host backdrop brush (blur), as GlassBackdrop's window.");
        }
        var state = deck.OffscreenGlassState;
        Console.WriteLine($"Glass: liquid shown {state.Shown}, surface aside {state.Aside}; animations {Soft.Enabled}; island {deck.Island.ActualWidth:0}x{deck.Island.ActualHeight:0} DIP collapsed");
        if (!state.Shown || !Soft.Enabled) { Console.WriteLine("FAIL: liquid glass or animations not on"); return 1; }

        var results = new Dictionary<(int Mode, int Scenario), List<Sample>>();
        var modes = only >= 0 ? new[] { only } : new[] { 1, 0 };
        // Warm-up: each scenario once each way (JIT, the open page's first layout, the capture's first frames).
        foreach (int mode in modes) foreach (int scenario in Order) Measure(deck, pipe, mode == 1, scenario, 2, v => liquidOn = v);
        for (int round = 0; round < rounds; round++)
            foreach (int mode in round % 2 == 0 ? modes : modes.Reverse().ToArray())
                foreach (int scenario in Order)
                {
                    var sample = Measure(deck, pipe, mode == 1, scenario, seconds, v => liquidOn = v);
                    (results.TryGetValue((mode, scenario), out var list) ? list : results[(mode, scenario)] = []).Add(sample);
                    Console.WriteLine(Inv($"  round {round + 1} {(mode == 1 ? "liquid" : "blur  ")} {Names[scenario],-24} cpu {sample.CpuPct,5:0.00} %  ui {sample.UiPct,5:0.00} %  gpu {sample.GpuMsPerS,6:0.00} ms/s (process, all engines {sample.ProcessGpu,6:0.0} ms/s)  wpf {sample.WpfFps,5:0.0} fps  glass renders {sample.Renders,5:0.0}/s  ticks {sample.Ticks,5:0.0}/s  captures {sample.Captures,5:0.0}/s  follows {sample.Follows}  gpu max call {pipe.MaxCallMs:0.00} ms, disjoint {pipe.Disjoints}"));
                }
        music.Stop();
        Console.WriteLine();
        Console.WriteLine($"Medians over {rounds} rounds of {seconds} s (process CPU in % of one core; GPU: our D3D11 work by timestamp queries, the capture's copies and comparison and the glass's drawing; WPF's own render thread GPU work and DWM's not included; process GPU: Windows' GPU Engine running time of this process, every engine summed, WPF included, DWM not):");
        Console.WriteLine("| scenario | mode | process CPU % | UI thread % | our D3D GPU ms/s | process GPU ms/s (all engines) | WPF frames/s | glass renders/s | poll ticks/s | captures/s |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (int scenario in Order)
            foreach (int mode in modes)
            {
                var r = results[(mode, scenario)];
                Console.WriteLine(Inv($"| {Names[scenario]} | {(mode == 1 ? "liquid" : "blur (BRIMDECK_LIQUID=0)")} | {Median(r.Select(x => x.CpuPct)):0.00} | {Median(r.Select(x => x.UiPct)):0.00} | {Median(r.Select(x => x.GpuMsPerS)):0.00} | {Median(r.Select(x => x.ProcessGpu)):0.0} | {Median(r.Select(x => x.WpfFps)):0.0} | {Median(r.Select(x => x.Renders)):0.0} | {Median(r.Select(x => x.Ticks)):0.0} | {Median(r.Select(x => x.Captures)):0.0} |"));
            }
        if (modes.Length == 2)
            foreach (int scenario in Order)
                Console.WriteLine(Inv($"liquid - blur, {Names[scenario]}: {Median(results[(1, scenario)].Select(x => x.CpuPct)) - Median(results[(0, scenario)].Select(x => x.CpuPct)):+0.00;-0.00} points of process CPU"));
        Console.WriteLine($"Fake island windows: longest life {FakeIsland.LongestMs} ms");
        glassWindow?.Dispose();
        shown?.Dispose();
        offscreen.Dispose();
        return 0;
    }

    // This process's GPU time as Windows counts it (\GPU Engine(pid_<pid>_*)\Running Time, every engine summed, 100 ns units):
    // WPF's render thread, our D3D11 work and the composition swap chain's copies alike, whatever device did it. Milliseconds.
    internal static class GpuCounter
    {
        private static nint _query, _counter;
        private static string _prefix = "";
        public static double Ms()
        {
            if (_query == 0)
            {
                if (PdhOpenQuery(null, 0, out _query) != 0 || PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Running Time", 0, out _counter) != 0) return double.NaN;
                _prefix = $"pid_{Environment.ProcessId}_";
            }
            if (PdhCollectQueryData(_query) != 0) return double.NaN;
            uint size = 0, count = 0;
            PdhGetRawCounterArray(_counter, ref size, ref count, 0);
            if (size == 0) return double.NaN;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetRawCounterArray(_counter, ref size, ref count, buffer) != 0) return double.NaN;
                long total = 0;
                int stride = Marshal.SizeOf<RawItem>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<RawItem>(buffer + i * stride);
                    if (Marshal.PtrToStringUni(item.Name) is { } name && name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)) total += item.FirstValue;
                }
                return total / 10_000d;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct RawItem { public nint Name; public uint Status, TimeLow, TimeHigh; public long FirstValue, SecondValue; public uint MultiCount; }
        [DllImport("pdh", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, nint user, out nint query);
        [DllImport("pdh", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern int PdhAddEnglishCounter(nint query, string path, nint user, out nint counter);
        [DllImport("pdh")] private static extern int PdhCollectQueryData(nint query);
        [DllImport("pdh", CharSet = CharSet.Unicode, EntryPoint = "PdhGetRawCounterArrayW")] private static extern int PdhGetRawCounterArray(nint counter, ref uint size, ref uint count, nint items);
    }

    internal static int Corner;
    internal static RECT Monitor;
    // The island collapsed and open, device pixels in the stage.
    internal static Rect Collapsed, Open;
    private static Rect IslandRect(MainWindow deck, FrameworkElement root)
    {
        var b = deck.Island.TransformToAncestor(root).TransformBounds(new Rect(deck.Island.RenderSize));
        return new Rect(b.X * 1.5, b.Y * 1.5, b.Width * 1.5, b.Height * 1.5);
    }

    // The monitors' rectangles in device pixels (this thread is per-monitor aware).
    private static List<RECT> Monitors()
    {
        var list = new List<RECT>();
        MonitorProc proc = (nint _, nint _, ref WinRect rect, nint _) => { list.Add(new RECT(rect.Left, rect.Top, rect.Right, rect.Bottom)); return true; };
        EnumDisplayMonitors(0, 0, proc, 0);
        GC.KeepAlive(proc);
        return list;
    }
    private delegate bool MonitorProc(nint monitor, nint dc, ref WinRect rect, nint data);
    [DllImport("user32")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc proc, nint data);

    // How often the desktop at each corner of each monitor changed, over a region of 360 x 200 px for 1.5 s each (no window
    // of ours there): the stillest corner, as monitor * 4 + corner.
    private static int Stillest(DesktopSource source, List<RECT> monitors)
    {
        var changes = new int[monitors.Count * 4];
        for (int i = 0; i < changes.Length; i++)
        {
            var m = monitors[i / 4];
            int corner = i % 4;
            if ((corner & 2) != 0) { changes[i] = int.MaxValue; continue; }
            // The open panel's region with the collapsed island 8 px from the corner, as Place puts it.
            int baseX = (corner & 1) != 0 ? m.Right - 8 - (int)Math.Round(Collapsed.Right) : m.Left + 8 - (int)Math.Round(Collapsed.X);
            int x = baseX + (int)Math.Round(Open.X) - 72, y = m.Top + (int)Math.Round(Open.Y) - 72;
            var region = new RECT(x, y, x + (int)Math.Round(Open.Width) + 144, y + (int)Math.Round(Open.Height) + 144);
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 1500)
            {
                if (source.Update(region, out bool changed) && changed && clock.ElapsedMilliseconds > 300) changes[i]++;
                Thread.Sleep(33);
            }
        }
        for (int i = 0; i < monitors.Count; i++)
            Console.WriteLine($"Desktop changes seen in 1.2 s at monitor {monitors[i]}'s corners (top-left, top-right, bottom-left, bottom-right): {string.Join(", ", changes.Skip(i * 4).Take(4))}");
        return Array.IndexOf(changes, changes.Min());
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static Sample Measure(MainWindow deck, Pipe pipe, bool liquid, int scenario, int seconds, Action<bool> setLiquid)
    {
        // The mode: the stand-in decides, the next frame shows it (the island's surface, rim and drift follow).
        setLiquid(liquid);
        deck.OffscreenLiquidFrame();
        pipe.Enabled = liquid;
        pipe.Off();
        deck.SetExpanded(scenario == 1);
        using var fake = new FakeIsland(pipe.Source);
        Wait(scenario == 2 ? 300 : 1500, fake);
        double gpu0 = GpuCounter.Ms();
        QueryProcessCycleTime(GetCurrentProcess(), out ulong cpu0);
        QueryThreadCycleTime(GetCurrentThread(), out ulong ui0);
        pipe.Mark();
        var counts0 = pipe.Counts();
        var wall = Stopwatch.StartNew();
        if (scenario == 2)
        {
            // Open and close, 1.1 s each, as ShadowCost; at least the seconds asked for.
            int cycles = Math.Max(1, (int)Math.Ceiling(seconds / 2.2));
            for (int i = 0; i < cycles; i++)
            {
                deck.SetExpanded(true); Wait(1100, fake);
                deck.SetExpanded(false); Wait(1100, fake);
            }
        }
        else Wait(seconds * 1000, fake);
        wall.Stop();
        QueryProcessCycleTime(GetCurrentProcess(), out ulong cpu1);
        double gpu1 = GpuCounter.Ms();
        QueryThreadCycleTime(GetCurrentThread(), out ulong ui1);
        double cpu = (cpu1 - cpu0) / CyclesPerMs(), ui = (ui1 - ui0) / CyclesPerMs();
        var counts = pipe.Counts();
        double s = wall.Elapsed.TotalSeconds;
        double gpu = pipe.GpuMs(counts0, out int follows);
        return new Sample(cpu / s / 10, ui / s / 10, gpu / s, (counts.Frames - counts0.Frames) / s, (counts.Renders - counts0.Renders) / s,
            (counts.Ticks - counts0.Ticks) / s, (counts.Captures - counts0.Captures) / s, follows, s, (gpu1 - gpu0) / s);
    }

    // GlassBackdrop's liquid glass, driven from the offscreen island: followed on every frame while its frame changes and for
    // StillFrames after, then polled at LiquidIdle's pace; each look at the desktop and each drawing timed on the GPU.
    private sealed class Pipe
    {
        private readonly MainWindow _deck;
        private readonly FrameworkElement _root;
        private readonly LiquidHost _host;
        private readonly DispatcherTimer _poll;
        private readonly ID3D11DeviceContext _context;
        private readonly Queue<(ID3D11Query Disjoint, ID3D11Query Start, ID3D11Query End)> _pending = new(), _free = new();
        private double _gpuMs;
        private long _frames, _captures, _gpuMarks;
        private int _follows, _still;
        private bool _following;
        private (Rect Bounds, double Opacity) _last;
        private int _baseX, _baseY;
        private bool _placed;
        private LiquidFrame _frame;
        private RECT _region;
        private int _pad;
        public bool Enabled;
        public DesktopSource Source => _host.Source;

        public Pipe(MainWindow deck, FrameworkElement root, LiquidHost host)
        {
            _deck = deck; _root = root; _host = host;
            _context = host.Source.Device.ImmediateContext;
            _poll = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(LiquidIdle.IdlePollMs) };
            _poll.Tick += (_, _) => Tick();
            CompositionTarget.Rendering += (_, _) => Frame();
        }

        public (long Frames, long Renders, long Ticks, long Captures) Counts() => (_frames, _host.Idle.Renders + _host.Idle.FrameRenders, _host.Idle.Ticks, _captures);

        // The mode changed: the swap chain's picture is stale, and the next frame starts following (as GlassBackdrop's Follow(0)).
        public void Off()
        {
            _poll.Stop(); _following = false;
            _host.Idle.Forget();
            _last = default;
            _glass?.Mode(Enabled);
        }

        private GlassWindow? _glass;
        public void Attach(GlassWindow glass) { _glass = glass; _host.Rebuilt += glass.Brush; _host.Ensure(); glass.Brush(_host.Renderer!); }

        private void Frame()
        {
            _frames++;
            var bounds = _deck.Island.TransformToAncestor(_root).TransformBounds(new Rect(_deck.Island.RenderSize));
            double opacity = 1;
            for (DependencyObject? node = _deck.Island; node is not null && !ReferenceEquals(node, _root); node = VisualTreeHelper.GetParent(node))
                if (node is UIElement element) opacity *= element.Opacity;
            var now = (new Rect(bounds.X * 1.5, bounds.Y * 1.5, bounds.Width * 1.5, bounds.Height * 1.5), opacity);
            bool changed = now != _last;
            _last = now;
            if (!_following)
            {
                if (!changed) return;
                _following = true; _follows++; _still = 0; _poll.Stop();
            }
            if (!changed && ++_still >= StillFrames)
            {
                _following = false;
                _host.Idle.Start(Environment.TickCount64);
                _poll.Interval = TimeSpan.FromMilliseconds(_host.Idle.Interval(false, _host.Renderer is { Measuring: true }, Environment.TickCount64));
                _poll.Start();
                return;
            }
            if (changed) _still = 0;
            if (now.Item2 < .002 || now.Item1.Width < 1) return;
            Place(now.Item1);
            // The window's sprite placed for this frame (the blur's or the glass's), as GlassBackdrop.Show does.
            _glass?.Show(now.Item1, now.Item2);
            if (!Enabled) return;
            _frame = _deck.OffscreenGlassBackdrop!.PreviewFrame();
            Timed(() => _host.Frame(_region, _pad, _frame, Environment.TickCount64));
            Backdrop();
        }

        // The region as GlassBackdrop takes it (the island's place, Pad around it), the collapsed island's left edge 8 px from
        // the primary monitor's left edge (over the fake island), its top as in the window at the top of the monitor.
        private void Place(Rect island)
        {
            if (!_placed)
            {
                _baseX = (Corner & 1) != 0 ? Monitor.Right - 8 - (int)Math.Round(Collapsed.Right) : Monitor.Left + 8 - (int)Math.Round(Collapsed.X);
                _baseY = Monitor.Top;
                _placed = true;
            }
            _pad = (int)Math.Ceiling(48 * 1.5);
            int x = _baseX + (int)Math.Round(island.X), y = _baseY + (int)Math.Round(island.Y);
            int w = Math.Max(1, (int)Math.Round(island.Width)), h = Math.Max(1, (int)Math.Round(island.Height));
            _region = new RECT(x - _pad, y - _pad, x + w + _pad, y + h + _pad);
        }

        private void Tick()
        {
            if (!Enabled || _following) { _poll.Stop(); return; }
            Timed(() => _host.Tick(_region, _pad, _frame, Environment.TickCount64));
            Backdrop();
            var interval = TimeSpan.FromMilliseconds(_host.Idle.Interval(false, _host.Renderer is { Measuring: true }, Environment.TickCount64));
            if (_poll.Interval != interval) _poll.Interval = interval;
        }

        // GlassBackdrop.ReportBackdrop with automatic liquid glass not chosen: the last measure of the backdrop read.
        private void Backdrop() => _host.Renderer?.TryBackdrop(out _);

        private void Timed(Func<bool> look)
        {
            _captures++;
            var q = _free.Count > 0 ? _free.Dequeue() : (_host.Source.Device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint)),
                _host.Source.Device.CreateQuery(new QueryDescription(QueryType.Timestamp)), _host.Source.Device.CreateQuery(new QueryDescription(QueryType.Timestamp)));
            _context.Begin(q.Item1);
            _context.End(q.Item2);
            look();
            _context.End(q.Item3);
            _context.End(q.Item1);
            // Sent now, so the closing timestamp is not left in the context until the next call's work flushes it.
            _context.Flush();
            _pending.Enqueue(q);
            Collect(wait: false);
        }

        private void Collect(bool wait)
        {
            while (_pending.Count > 0)
            {
                var q = _pending.Peek();
                QueryDataTimestampDisjoint data;
                ulong a = 0, b = 0;
                if (wait) { while (!_context.GetData(q.Disjoint, out data)) Thread.Yield(); _context.GetData(q.Start, out a); _context.GetData(q.End, out b); }
                else if (!_context.GetData(q.Disjoint, AsyncGetDataFlags.DoNotFlush, out data) || !_context.GetData(q.Start, AsyncGetDataFlags.DoNotFlush, out a) || !_context.GetData(q.End, AsyncGetDataFlags.DoNotFlush, out b)) return;
                if (!data.Disjoint && b >= a) { double ms = (b - a) * 1000.0 / data.Frequency; _gpuMs += ms; MaxCallMs = Math.Max(MaxCallMs, ms); }
                else if (data.Disjoint) Disjoints++;
                _gpuMarks++;
                _free.Enqueue(_pending.Dequeue());
            }
        }

        // GPU milliseconds since a mark (all queries answered first), and how many times following started since.
        private double _markGpu; private int _markFollows;
        public double GpuMs((long, long, long, long) since, out int follows)
        {
            Collect(wait: true);
            double ms = _gpuMs - _markGpu; follows = _follows - _markFollows;
            _markGpu = _gpuMs; _markFollows = _follows;
            return ms;
        }
        public void Mark() { Collect(wait: true); _markGpu = _gpuMs; _markFollows = _follows; MaxCallMs = 0; Disjoints = 0; }
        public double MaxCallMs; public int Disjoints;
    }

    // GlassBackdrop's window, beyond the virtual screen beside the stage's: a composition target whose sprite shows the
    // renderer's swap chain (liquid glass) or the host backdrop brush (the blur), placed and clipped on every followed frame.
    internal sealed class GlassWindow : IDisposable
    {
        private readonly HwndSource _window;
        private readonly Windows.UI.Composition.Compositor _compositor;
        private readonly Windows.UI.Composition.Desktop.DesktopWindowTarget _target;
        private readonly Windows.UI.Composition.SpriteVisual _liquid, _blur;
        private readonly Windows.UI.Composition.CompositionRoundedRectangleGeometry _clip, _blurClip;
        private static nint _queue;
        private bool _liquidMode = true;

        public GlassWindow(nint stage)
        {
            GetWindowRect(stage, out var rect);
            var options = new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), Type = 2, Apartment = 2 };
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is null) Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out _queue));
            _compositor = new Windows.UI.Composition.Compositor();
            _window = new HwndSource(new HwndSourceParameters("BrimDeck.Perf glass")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80 | 0x08000000 | 0x00200000,
                Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top, PositionX = rect.Left, PositionY = rect.Top,
            });
            int on = 1;
            DwmSetWindowAttribute(_window.Handle, 17, ref on, sizeof(int));
            _target = Target(_window.Handle);
            var root = _compositor.CreateContainerVisual(); root.RelativeSizeAdjustment = System.Numerics.Vector2.One;
            _blur = _compositor.CreateSpriteVisual();
            _blur.Brush = _compositor.CreateHostBackdropBrush();
            _blurClip = _compositor.CreateRoundedRectangleGeometry();
            _blur.RelativeSizeAdjustment = System.Numerics.Vector2.One;
            _blur.Clip = _compositor.CreateGeometricClip(_blurClip);
            _liquid = _compositor.CreateSpriteVisual();
            _clip = _compositor.CreateRoundedRectangleGeometry();
            _liquid.Clip = _compositor.CreateGeometricClip(_clip);
            root.Children.InsertAtTop(_blur);
            root.Children.InsertAtTop(_liquid);
            _target.Root = root;
            ShowWindow(_window.Handle, 4);
        }

        public void Brush(LiquidRenderer renderer)
        {
            var previous = _liquid.Brush;
            _liquid.Brush = renderer.CreateBrush(_compositor);
            previous?.Dispose();
        }

        public void Mode(bool liquid) { _liquidMode = liquid; _liquid.Opacity = liquid ? 1 : 0; _blur.Opacity = liquid ? 0 : 1; }

        public void Show(Rect island, double opacity)
        {
            var offset = new System.Numerics.Vector2((float)island.X, (float)island.Y);
            var size = new System.Numerics.Vector2((float)island.Width, (float)island.Height);
            if (_liquidMode)
            {
                _liquid.Offset = new System.Numerics.Vector3(offset, 0); _liquid.Size = size;
                _clip.Size = size; _clip.CornerRadius = new System.Numerics.Vector2(24); _liquid.Opacity = (float)opacity;
            }
            else { _blurClip.Offset = offset; _blurClip.Size = size; _blurClip.CornerRadius = new System.Numerics.Vector2(24); _blur.Opacity = (float)opacity; }
        }

        public void Dispose() { _target.Dispose(); _window.Dispose(); }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateDesktopWindowTarget(nint self, nint window, int topmost, out nint target);
        private Windows.UI.Composition.Desktop.DesktopWindowTarget Target(nint window)
        {
            var unknown = ((WinRT.IWinRTObject)_compositor).NativeObject.ThisPtr;
            var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var interop));
            try
            {
                var create = Marshal.GetDelegateForFunctionPointer<CreateDesktopWindowTarget>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(interop), 3 * IntPtr.Size));
                Marshal.ThrowExceptionForHR(create(interop, window, 0, out var target));
                try { return Windows.UI.Composition.Desktop.DesktopWindowTarget.FromAbi(target); }
                finally { Marshal.Release(target); }
            }
            finally { Marshal.Release(interop); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, Type, Apartment; }
        [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out nint controller);
        [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    }

    // The island's own redraws over the region, as Desktop Duplication reports them: a 64 px tool window at the primary
    // monitor's top-left corner, 1/255 opaque, left out of the capture, redrawn 30 times a second and made anew every 1.8 s.
    private sealed class FakeIsland : IDisposable
    {
        private readonly bool _on;
        private nint _window;
        private readonly Stopwatch _life = new();
        private int _redraw;
        private long _next;
        public static long LongestMs;
        public FakeIsland(DesktopSource? source) { _on = source is not null; Make(); }
        private void Make()
        {
            if (!_on) return;
            int x = (Corner & 1) != 0 ? Monitor.Right - Fake : Monitor.Left, y = (Corner & 2) != 0 ? Monitor.Bottom - Fake : Monitor.Top;
            _window = CreateWindowEx(0x80000 | 0x8 | 0x80 | 0x8000000 | 0x20, "Static", "a", 0x80000000, x, y, Fake, Fake, 0, 0, 0, 0);
            if (_window == 0) return;
            SetLayeredWindowAttributes(_window, 0, 1, 2);
            ShowWindow(_window, 4);
            if (!DesktopSource.Exclude(_window)) Console.WriteLine("NOTE: the fake island could not be left out of the capture");
            _life.Restart();
        }
        public void Step(long now)
        {
            if (_window == 0 || now < _next) return;
            _next = now + 33;
            if (_life.ElapsedMilliseconds >= 1800) { Destroy(); Make(); if (_window == 0) return; }
            SetLayeredWindowAttributes(_window, 0, (byte)(1 + (++_redraw & 1)), 2);
            SetWindowText(_window, (_redraw & 1) == 0 ? "a" : "b");
            InvalidateRect(_window, 0, true);
        }
        private void Destroy()
        {
            if (_window == 0) return;
            LongestMs = Math.Max(LongestMs, _life.ElapsedMilliseconds);
            DestroyWindow(_window); _window = 0;
        }
        public void Dispose() => Destroy();
    }

    // Processor cycles in a millisecond of this thread's own work (a short spin), to turn cycle counts into time.
    private static double _cyclesPerMs;
    private static double CyclesPerMs()
    {
        if (_cyclesPerMs > 0) return _cyclesPerMs;
        QueryThreadCycleTime(GetCurrentThread(), out ulong a);
        var spin = Stopwatch.StartNew();
        while (spin.Elapsed.TotalMilliseconds < 200) { }
        QueryThreadCycleTime(GetCurrentThread(), out ulong b);
        return _cyclesPerMs = (b - a) / spin.Elapsed.TotalMilliseconds;
    }
    [DllImport("kernel32")] private static extern bool QueryProcessCycleTime(nint process, out ulong cycles);
    [DllImport("kernel32")] private static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);
    [DllImport("kernel32")] private static extern nint GetCurrentProcess();

    private static double Median(IEnumerable<double> values) { var v = values.Order().ToList(); return v.Count == 0 ? double.NaN : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2; }

    // Runs the dispatcher for a while, the fake island redrawn on its own timer meanwhile.
    private static void Wait(double milliseconds, FakeIsland fake)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var redraw = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(15) };
        redraw.Tick += (_, _) => fake.Step(Environment.TickCount64);
        redraw.Start();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        redraw.Stop();
    }

    [StructLayout(LayoutKind.Sequential)] private struct WinRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] private static extern bool GetWindowRect(nint window, out WinRect rect);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentThread();
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(int exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32")] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(nint window, string text);
    [DllImport("user32")] private static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport("user32")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
