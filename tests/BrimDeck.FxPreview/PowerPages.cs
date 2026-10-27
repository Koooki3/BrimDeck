using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;

// The real panel's system monitor page with the whole machine's power and its own battery, and the collapsed capsule with the Power
// and Charge readings, in every built-in theme and look, offscreen (the same stage as the page sheets), with made-up readings that
// cover a laptop on battery with Battery saver, charging, with a low battery, a desktop (an estimate, no battery), and a machine
// with no power reading at all; several page sizes, and the narrowest the page and the row allow. Not a test: pictures to look at.
//   dotnet run -c Release --no-build --project tests/BrimDeck.FxPreview -- --power-pages <output folder> [--scale 150] [--themes dark,light]
//   [--sizes] adds the page at its smallest and largest.
internal static partial class LiquidPages
{
    private sealed record PowerScene(string Label, double Width, double Height, bool Laptop, MonitorSnapshot Snapshot);
    private sealed record StripScene(string Label, MonitorSnapshot Snapshot, bool Laptop, BatteryReading? Device = null);

    private static BatteryCell Cell(double remaining, double rate, bool charging = false, bool discharging = false)
        => new("fx", remaining, 60_000, 66_000, rate, charging, discharging);

    private static MonitorSnapshot Powered(MonitorSnapshot basis, PowerSample? power, string? why, BatterySample? battery, double[]? history = null)
    {
        var curve = history ?? (power is null ? [] : Enumerable.Range(0, 60).Select(i => power.Watts * (1 + .35 * Math.Sin(i / 4.0) + .15 * Math.Sin(i / 1.7)) * (i > 40 ? 1.25 : 1)).ToArray());
        // The system drive 83 % full (a warning level, as a drive's reading shows it): it is no battery, and must not look like one.
        return basis with
        {
            Storage = [new StorageDrive(0, "SSD", [new StorageVolume(Storage.WindowsLetter?.ToUpperInvariant() ?? "C:", "", 100UL << 30, 17UL << 30)])],
            Power = power is null ? Reading<PowerSample>.Unavailable(why ?? "") : Reading<PowerSample>.Of(power),
            Battery = battery is null ? Reading<BatterySample>.Unavailable(BatteryModel.NoBattery) : Reading<BatterySample>.Of(battery),
            PowerHistory = curve,
            PowerSummary = power is null ? null : new PowerSummary(curve.Max(), curve.Average(), 60)
        };
    }

    private static List<PowerScene> PowerScenes(MonitorSnapshot basis, bool sizes)
    {
        var parts = new PowerPart[] { new("cpu", "CPU", 27.1, "能量计（RAPL）"), new("gpu", "NVIDIA GeForce RTX 4060 Laptop GPU", 15.3, "nvidia-smi") };
        var estimate = new PowerSample(42.4, PowerOrigin.Estimated, PowerSource.Components, parts);
        var discharge = new PowerSample(23.4, PowerOrigin.Measured, PowerSource.Battery, parts);
        BatterySample B(double remaining, double rate, bool ac, bool charging, bool saver = false, PowerMode mode = PowerMode.Balanced) => BatteryModel.Build(new BatteryInputs(
            [Cell(remaining, rate, charging, !charging && rate < 0)], ac, saver, mode, WindowsRemaining: charging ? null : TimeSpan.FromMinutes(165))).OrDefault!;
        var scenes = new List<PowerScene>
        {
            new("laptop, on battery, Battery saver, 62 %, measured", 680, 300, true, Powered(basis, discharge, null, B(37_000, -23_400, false, false, saver: true, PowerMode.BestEfficiency))),
            new("laptop, charging, 84 %, 620 x 220 (the default page): one line shared", 620, 220, true, Powered(basis, estimate, null, B(50_400, 31_000, true, true))),
            new("laptop, charging, 84 %, 680 x 300: two lines", 680, 300, true, Powered(basis, estimate, null, B(50_400, 31_000, true, true, mode: PowerMode.BestPerformance))),
            new("laptop, low battery 12 %", 680, 300, true, Powered(basis, discharge with { Watts = 14.2 }, null, B(7_200, -14_200, false, false))),
            new("laptop, plugged in, 96 %", 680, 300, true, Powered(basis, estimate, null, B(57_600, 0, true, false, mode: PowerMode.BestPerformance))),
            new("desktop, an estimate, no battery", 620, 240, false, Powered(basis, estimate, null, null)),
            new("no power reading (a virtual machine)", 620, 240, false, Powered(basis, null, "这台电脑没有提供整机能量计、电池放电读数，也读不到 CPU 或显卡的功率", null)),
            new("laptop, narrow page 460 x 200", 460, 200, true, Powered(basis, discharge, null, B(37_000, -23_400, false, false))),
        };
        if (sizes)
        {
            scenes.Add(new("laptop, the smallest page 440 x 140", 440, 140, true, Powered(basis, discharge, null, B(37_000, -23_400, false, false))));
            scenes.Add(new("laptop, the largest page 1200 x 400", 1200, 400, true, Powered(basis, discharge, null, B(37_000, -23_400, false, false))));
        }
        return scenes;
    }

    private static List<StripScene> StripScenes(MonitorSnapshot basis)
    {
        var estimate = new PowerSample(42.4, PowerOrigin.Estimated, PowerSource.Components, []);
        var discharge = new PowerSample(8.4, PowerOrigin.Measured, PowerSource.Battery, []);
        BatterySample B(double remaining, double rate, bool ac, bool charging, bool saver = false) => BatteryModel.Build(new BatteryInputs(
            [Cell(remaining, rate, charging, !charging && rate < 0)], ac, saver)).OrDefault!;
        return
        [
            new("on battery 62 % / mouse 83 %", Powered(basis, discharge, null, B(37_000, -8_400, false, false)), true, new(83, false, MemoryLevel.Normal, "MX Master 3S", DeviceKind.Mouse)),
            new("Battery saver 62 % / headphones 45 % charging", Powered(basis, discharge, null, B(37_000, -8_400, false, false, saver: true)), true, new(45, true, MemoryLevel.Normal, "WH-1000XM4", DeviceKind.Headphones)),
            new("charging 98 % / keyboard 98 %", Powered(basis, estimate, null, B(58_800, 31_000, true, true)), true, new(98, false, MemoryLevel.Normal, "MX Keys S", DeviceKind.Keyboard)),
            new("low 12 % / mouse 22 % (elevated)", Powered(basis, discharge with { Watts = 112.4 }, null, B(7_200, -14_200, false, false)), true, new(22, false, MemoryLevel.Elevated, "MX Master 3S", DeviceKind.Mouse)),
            new("a desktop: power only / keyboard 12 % (low)", Powered(basis, estimate, null, null), false, new(12, false, MemoryLevel.Critical, "F99 MAX", DeviceKind.Keyboard)),
        ];
    }

    public static int RunPower(string folder, double scale, HashSet<string>? only, bool sizes, bool english = false)
    {
        Scale = scale;
        Directory.CreateDirectory(folder);
        var (app, deck, root, source) = Offscreen(animations: false);
        using var _ = source;
        var cover = Cover(); var track = Track(); var basis = Snapshot();
        var settings = app.Settings;
        if (english) { settings.Language = Loc.English; Loc.Use(Loc.English); }
        // The sampler is not running here, so the page would keep room for three drives that are not there.
        settings.CapsuleMonitor = true; settings.NotchMonitor = true; settings.MonitorStorage = false;
        foreach (var reading in settings.CompactReadings) reading.Visible = reading.Item is CompactMonitorItems.Cpu or CompactMonitorItems.Memory or CompactMonitorItems.Storage or CompactMonitorItems.Power or CompactMonitorItems.Charge or CompactMonitorItems.Battery;
        foreach (var theme in PanelThemes.BuiltIns.Where(theme => only is null || only.Contains(theme.Id)))
            foreach (var look in new[] { DaylightLook.Day, DaylightLook.Dusk, DaylightLook.Night }
                .Where(look => look == DaylightLook.Day || look == DaylightLook.Night && theme.File.Night is not null || look == DaylightLook.Dusk && theme.File.Dusk is not null))
            {
                var palette = PanelThemes.Resolve(theme, look);
                PanelTheme.Show(palette);
                var again = new ResourceDictionary();
                again.MergedDictionaries.Add(deck.Resources);
                again.MergedDictionaries.Add(app.Resources.MergedDictionaries.First(dictionary => dictionary.Contains("PanelSurface")));
                root.Resources = again;
                deck.ApplySettings();
                var tiles = new List<(string Label, BitmapSource Tile, int Column, int Row)>();
                var scenes = PowerScenes(basis, sizes);
                for (int i = 0; i < scenes.Count; i++)
                {
                    var scene = scenes[i];
                    BatteryProvider.ForceBattery = scene.Laptop;
                    DeckPages.SetSize(settings, DeckPage.Monitor, scene.Width, scene.Height);
                    deck.ApplySettings();
                    Show(deck, root, Page.Monitor, track, cover, scene.Snapshot);
                    tiles.Add((scene.Label, Tile(deck, root, palette.IsDark), i % 2, i / 2));
                }
                int row = (scenes.Count + 1) / 2;
                var strips = StripScenes(basis);
                for (int i = 0; i < strips.Count; i++)
                {
                    BatteryProvider.ForceBattery = strips[i].Laptop;
                    deck.OffscreenLowest = strips[i].Device;
                    deck.ApplySettings();
                    Show(deck, root, Page.Capsule, track, cover, strips[i].Snapshot);
                    // The row's readings join with their first figure; the snapshot is handed over again once the row is built with them.
                    deck.OffscreenMonitor(strips[i].Snapshot);
                    Settle(root, deck);
                    tiles.Add(("collapsed capsule: " + strips[i].Label, Tile(deck, root, palette.IsDark), i % 2, row + i / 2));
                }
                string name = theme.Id + (look == DaylightLook.Day ? "" : "-" + look.ToString().ToLowerInvariant());
                LiquidPreview.Save(Sheet($"{name} — power and battery at {Scale * 100:0} %", tiles), Path.Combine(folder, $"power-{name}-{Scale * 100:0}{(english ? "-en" : "")}.png"));
                Console.WriteLine($"Wrote power-{name}-{Scale * 100:0}{(english ? "-en" : "")}.png");
            }
        BatteryProvider.ForceBattery = null;
        return 0;
    }

    // What reading the machine's power and battery costs the sampler: the sampler runs with and without it, one after the other in this
    // process, and the CPU time and memory of each window are printed. dotnet run ... -- --power-cost [seconds]
    public static int RunPowerCost(int seconds)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        void Pump(TimeSpan time)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = time };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        using var sampler = new SystemSampler(dispatcher, () => (null, null));
        MonitorSnapshot? last = null;
        sampler.Updated += snapshot => last = snapshot;
        var results = new List<(string Name, double Cpu, double Memory)>();
        void Window(string name, bool power, bool battery = false)
        {
            sampler.SetExtras(false, false, false, power);
            sampler.SetDemand(true, 1, false);
            Pump(TimeSpan.FromSeconds(6));
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
            var cpu = process.TotalProcessorTime;
            Pump(TimeSpan.FromSeconds(seconds));
            process.Refresh();
            double used = (process.TotalProcessorTime - cpu).TotalMilliseconds / seconds;
            results.Add((name, used, process.PrivateMemorySize64 / 1048576.0));
            Console.WriteLine($"{name,-44} CPU {used,6:0.0} ms/s   private {process.PrivateMemorySize64 / 1048576.0,6:0.0} MB   threads {process.Threads.Count}   power: {(last?.Power.TryGet(out var p) == true ? $"{p.Watts:0.0} W {p.Origin}" : "-")}");
        }
        // Everything loaded first (the power sources included, once), then the windows alternate so a slow drift in the machine's load
        // shows up as such: the sampler's own readings (the monitor page's: CPU, memory, GPU, network) without and with power and battery.
        Window("warm-up, with power and battery", true);
        Window("sampler without power and battery", false);
        Window("sampler with power and battery", true);
        Window("sampler without power and battery (again)", false);
        Window("sampler with power and battery (again)", true);
        sampler.SetDemand(false, 1, false);
        return 0;
    }

    // The memory of the power readings in the three states that matter, in a test process of its own (nothing of the user's is touched): power
    // off; power on with an NVIDIA adapter at work (the helper running); power off again, and after the adapter has gone idle. Private bytes and
    // working set of this process, and of the helper (nvidia-smi.exe) while it runs. --power-memory
    public static int RunPowerMemory()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        (double Private, double Working) Own() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh(); return (process.PrivateMemorySize64 / 1048576.0, process.WorkingSet64 / 1048576.0); }
        (int Count, double Private, double Working) Helper()
        {
            double p = 0, w = 0; int n = 0;
            foreach (var other in System.Diagnostics.Process.GetProcessesByName("nvidia-smi"))
            {
                using (other) { try { n++; p += other.PrivateMemorySize64 / 1048576.0; w += other.WorkingSet64 / 1048576.0; } catch (InvalidOperationException) { } }
            }
            return (n, p, w);
        }
        void Report(string state)
        {
            var own = Own(); var helper = Helper();
            Console.WriteLine($"{state,-52} BrimDeck private {own.Private,6:0.0}  working {own.Working,6:0.0}   helper x{helper.Count}: private {helper.Private,5:0.0}  working {helper.Working,5:0.0}");
        }
        using var pdh = new PdhQuery();
        pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
        var battery = new BatteryProvider(); battery.Sample();
        using var power = new PowerProvider(pdh);
        var busy = new List<GpuSample> { new(.5, Reading<ulong>.Unavailable("")) { Name = "NVIDIA GeForce RTX 4060 Laptop GPU" } };
        var idle = new List<GpuSample> { new(0, Reading<ulong>.Unavailable("")) { Name = "NVIDIA GeForce RTX 4060 Laptop GPU" } };
        double clock = 0;
        void Tick(bool wanted, List<GpuSample> gpus, int seconds)
        {
            for (int i = 0; i < seconds; i++) { clock += 1; power.Sample(pdh.Collect(), wanted, clock, gpus, default, true); Thread.Sleep(1000); }
        }
        // Everything the process loads once is loaded first (WinRT, counters, the first helper), so each state shows what it adds.
        Tick(true, busy, 4); Tick(false, busy, 2); Thread.Sleep(1500);
        Report("1 power off (nothing running)");
        Tick(true, idle, 4);
        Report("2 power on, the NVIDIA adapter idle (helper not started)");
        Tick(true, busy, 5);
        Report("3 power on, the NVIDIA adapter at work (helper running)");
        Tick(true, idle, 12);
        Report("4 the adapter idle for 12 s (helper ended by itself)");
        Tick(true, busy, 4);
        Report("5 at work again (helper started again)");
        Tick(false, busy, 1); Thread.Sleep(500);
        Report("6 power no longer wanted (leaving the page)");
        return 0;
    }

    // The helper must not outlive this process: it starts nvidia-smi, reads it, and then ends abruptly, as a crash would. --smi-crash
    public static int RunSmiCrash()
    {
        var smi = new NvidiaSmi();
        Thread.Sleep(2500);
        Console.WriteLine("reading: " + (smi.Read("NVIDIA GeForce RTX 4060 Laptop GPU", 0).TryGet(out var w) ? w.ToString("0.0") + " W" : "none") + "; ending without cleaning up");
        Console.Out.Flush();
        Environment.FailFast("smi-crash test");
        return 0;
    }

    // The colors the power and battery readings resolve to on every theme with the Theme color scheme (the default): what the strip and the
    // page draw with. --power-colors
    public static int RunPowerColors()
    {
        foreach (var theme in PanelThemes.BuiltIns)
            foreach (var look in new[] { DaylightLook.Day, DaylightLook.Night }.Where(look => look == DaylightLook.Day || theme.File.Night is not null))
            {
                var palette = PanelThemes.Resolve(theme, look);
                PanelTheme.Show(palette);
                var colors = MonitorThemeColors.For(MonitorPalette.Theme, new MonitorCustomColors()) with { Warning = MonitorThemeColors.Warning(new DeckSettings()), Critical = MonitorThemeColors.Critical(new DeckSettings()) };
                var style = PowerStyle.For(colors);
                Console.WriteLine($"{theme.Id,-24}{(look == DaylightLook.Night ? "night" : "     ")} {style.Look.Gauge,-8} normal {style.Normal} charge {style.Charge} saver {style.Saver} draw {style.Draw} | warning {style.Warning} critical {style.Critical} | cpu {colors.Performance} eff {colors.Efficiency}");
            }
        return 0;
    }

    // How long the UI thread takes to show the monitor page the first time (cold: nothing of the process warm yet) and again after leaving it
    // (warm), with the power lines asked for or not, in the offscreen panel; and how long the sampler takes to give its first snapshot and the
    // first with a power or battery figure. Each in a process of its own: --power-open on|off, --power-first on|off.
    public static int RunPowerOpen(bool power, bool prewarm = false)
    {
        var (app, deck, root, source) = Offscreen(animations: false);
        using var _ = source;
        var settings = app.Settings;
        settings.MonitorStorage = false; settings.MonitorPower = power;
        BatteryProvider.ForceBattery = true;
        var cover = Cover(); var track = Track();
        var scenes = PowerScenes(Snapshot(), false);
        var snapshot = scenes[0].Snapshot;
        DeckPages.SetSize(settings, DeckPage.Monitor, 680, 300);
        deck.ApplySettings();
        // As the app does once it has started: a pool thread builds the icons and compiles the rows' code while the UI has nothing to do.
        if (prewarm)
        {
            PowerWarm.Start(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            var idle = new System.Windows.Threading.DispatcherFrame();
            var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            wait.Tick += (_, _) => { wait.Stop(); idle.Continue = false; };
            wait.Start();
            System.Windows.Threading.Dispatcher.PushFrame(idle);
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Show(deck, root, Page.Monitor, track, cover, snapshot);
        double cold = watch.Elapsed.TotalMilliseconds;
        Show(deck, root, Page.Usage, track, cover, snapshot);
        watch.Restart();
        Show(deck, root, Page.Monitor, track, cover, snapshot);
        double warm = watch.Elapsed.TotalMilliseconds;
        // The page's own update with a new reading, once it is shown.
        watch.Restart();
        for (int i = 0; i < 20; i++) deck.OffscreenMonitor(snapshot);
        double update = watch.Elapsed.TotalMilliseconds / 20;
        Console.WriteLine($"power lines {(power ? "on " : "off")}{(prewarm ? " + warm-up" : "")}: first show {cold,7:0.0} ms, shown again {warm,6:0.0} ms, one update {update,5:0.00} ms");
        BatteryProvider.ForceBattery = null;
        return 0;
    }

    public static int RunPowerFirst(bool power)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        using var sampler = new SystemSampler(dispatcher, () => (null, null));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double first = -1, withFigure = -1;
        sampler.Updated += snapshot =>
        {
            if (first < 0) first = watch.Elapsed.TotalMilliseconds;
            if (withFigure < 0 && (snapshot.Power.HasValue || snapshot.Battery.HasValue)) withFigure = watch.Elapsed.TotalMilliseconds;
        };
        sampler.SetExtras(false, false, false, power);
        sampler.SetDemand(true, 1, false);
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Console.WriteLine($"power {(power ? "on " : "off")}: first snapshot {first,7:0} ms, first with a power or battery figure {(withFigure < 0 ? "never" : withFigure.ToString("0") + " ms")}");
        sampler.SetDemand(false, 1, false);
        return 0;
    }

    // The power block alone, step by step on the UI thread, cold and then warm, in a theme: the look and the icons' first use, building the
    // block with a reading, laying it out, and drawing it. --power-block <theme id>
    public static int RunPowerBlock(string themeId)
    {
        var (app, deck, root, source) = Offscreen(animations: false);
        using var _ = source;
        var theme = PanelThemes.BuiltIn(themeId) ?? PanelThemes.BuiltIn(PanelThemes.Dark)!;
        PanelTheme.Show(PanelThemes.Resolve(theme));
        BatteryProvider.ForceBattery = true;
        var snapshot = PowerScenes(Snapshot(), false)[0].Snapshot;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double Lap() { double t = watch.Elapsed.TotalMilliseconds; watch.Restart(); return t; }
        var colors = MonitorThemeColors.For(MonitorPalette.Theme, new MonitorCustomColors());
        var style = PowerStyle.For(colors);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen()) { foreach (var icon in Enum.GetValues<PowerIcon>()) PowerIcons.Draw(dc, icon, new System.Windows.Rect(0, 0, 13, 13), "#FFFFFF", style, 1.5); }
        double icons = Lap();
        var block = new PowerBlock(colors, new PowerOptions(style, 15, true), 12, 10.5, 22, 2) { Width = 360 };
        block.Set(snapshot, false);
        double build = Lap();
        block.Measure(new System.Windows.Size(360, 44)); block.Arrange(new System.Windows.Rect(0, 0, 360, 44)); block.UpdateLayout();
        double layout = Lap();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(540, 66, 144, 144, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(block);
        double first = Lap();
        block.Set(snapshot, false); bitmap.Render(block);
        double second = Lap();
        Console.WriteLine($"{theme.Id,-24} icons {icons,6:0.0}  build {build,6:0.0}  layout {layout,5:0.0}  first draw {first,6:0.0}  next update + draw {second,5:0.0} ms");
        BatteryProvider.ForceBattery = null;
        return 0;
    }

    // The power rows coming in: a block with motion on is given its first reading and drawn every 60 ms for a second; the share of ink in each
    // frame shows the dashes giving way to the figures, and how many frames the UI thread had to draw. --power-appear
    public static int RunPowerAppear()
    {
        var (app, deck, root, source) = Offscreen(animations: true);
        using var _ = source;
        BatteryProvider.ForceBattery = true;
        PanelTheme.Show(PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!));
        var snapshot = PowerScenes(Snapshot(), false)[0].Snapshot;
        var colors = MonitorThemeColors.For(MonitorPalette.Theme, new MonitorCustomColors());
        var block = new PowerBlock(colors, new PowerOptions(PowerStyle.For(colors), 15, true), 12, 10.5, 22, 2) { Width = 360 };
        var host = new System.Windows.Controls.Canvas { Width = 360, Height = 44, Background = System.Windows.Media.Brushes.Black };
        host.Children.Add(block);
        root.Children.Add(host);
        host.Measure(new System.Windows.Size(360, 44)); host.Arrange(new System.Windows.Rect(0, 0, 360, 44)); host.UpdateLayout();
        double Ink()
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(360, 44, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(host);
            var pixels = new byte[360 * 44 * 4]; bitmap.CopyPixels(pixels, 360 * 4, 0);
            double sum = 0; for (int i = 0; i < pixels.Length; i += 4) sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
            return sum / (360 * 44 * 3 * 255.0) * 100;
        }
        Console.WriteLine($"before the reading: {Ink():0.00} % ink (dashes)");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        block.Set(snapshot, motion: true);
        for (int step = 0; step <= 16; step++)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Console.WriteLine($"  {watch.Elapsed.TotalMilliseconds,6:0} ms: {Ink():0.00} % ink");
        }
        BatteryProvider.ForceBattery = null;
        return 0;
    }
}
