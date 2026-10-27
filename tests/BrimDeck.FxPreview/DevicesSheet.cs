using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;
using Path = System.IO.Path;

// The devices page in every built-in theme and its night variant: the open panel at its default size (460 × 200) with no
// devices, one, three and six, six again on a large page, and the collapsed notch with the lowest battery reading; among the
// devices a low and an elevated battery, one charging, earbuds with their parts and a controller that reports no battery. Drawn
// with the app's own DevicesView and MonitorStrip inside the theme's island, at 100 percent and enlarged two times. Not a
// test: a picture to look at.
//   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --devices <folder>
// --devices-probe [seconds] reads the devices on this PC through the app's own sources (DeviceBatteries), read only, and
// prints them.
internal static class DevicesSheet
{
    private const double Width = 1500, Height = 1260;

    private static DeviceBattery D(string key, string name, DeviceKind kind, int? percent, DeviceSource source = DeviceSource.Bluetooth) => new("bt:" + key, name, kind, source, percent);
    private static readonly DeviceBattery Mouse = D("1", "MCHOSE A7 V2 Ultra+", DeviceKind.Mouse, 98);
    private static readonly IReadOnlyList<DeviceBattery> Three =
        [D("1", "MX Master 3S", DeviceKind.Mouse, 76, DeviceSource.Logitech), D("2", "F99 MAX", DeviceKind.Keyboard, 12, DeviceSource.Hid), D("3", "WH-1000XM4", DeviceKind.Headphones, 45) with { Charging = true }];
    private static readonly IReadOnlyList<DeviceBattery> Six =
    [
        D("1", "Razer Viper V3 Pro", DeviceKind.Mouse, 64, DeviceSource.Razer), D("2", "MX Keys S", DeviceKind.Keyboard, 22, DeviceSource.Logitech),
        D("3", "Arctis Nova 7", DeviceKind.Headphones, 100, DeviceSource.SteelSeries) with { Charging = true },
        D("4", "AirPods Pro", DeviceKind.Earbuds, 60, DeviceSource.Apple) with { Parts = [new(BatteryPart.Left, 80, false), new(BatteryPart.Right, 60, false), new(BatteryPart.Case, 30, true)], Coarse = true },
        D("5", "Xbox Wireless Controller", DeviceKind.Controller, null, DeviceSource.Gaming), D("6", "Surface Pen", DeviceKind.Pen, 9),
    ];
    private static readonly IReadOnlyList<DeviceBattery> Ten =
    [
        .. Six, D("7", "DualSense Wireless Controller", DeviceKind.Controller, 35, DeviceSource.Sony), D("8", "JBL Flip 6", DeviceKind.Speaker, 80),
        D("9", "VXE R1 Pro Max", DeviceKind.Mouse, 18, DeviceSource.Hid), D("10", "HyperX Cloud II Wireless", DeviceKind.Headphones, 57, DeviceSource.HyperX),
    ];

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var theme in PanelThemes.BuiltIns)
        {
            Draw(PanelThemes.Resolve(theme), Path.Combine(folder, theme.Id + "-devices.png"), theme.DisplayName);
            if (theme.File.Night is not null) Draw(PanelThemes.Resolve(theme, true), Path.Combine(folder, theme.Id + "-night-devices.png"), theme.DisplayName + " · 夜间");
        }
        // The English texts, which are longer, in the default dark and light themes.
        Loc.Use(Loc.English);
        foreach (var id in new[] { PanelThemes.Dark, PanelThemes.Light })
            Draw(PanelThemes.Resolve(PanelThemes.BuiltIn(id)!), Path.Combine(folder, id + "-devices-en.png"), id + " · English");
        Loc.Use(Loc.Chinese);
        Console.WriteLine("Wrote devices sheets to " + Path.GetFullPath(folder));
        return 0;
    }

    private static void Draw(PanelPalette palette, string file, string title)
    {
        PanelTheme.Show(palette);
        var root = new Canvas { Width = Width, Height = Height, Background = Backdrop() };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        var label = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        Canvas.SetLeft(label, 12); Canvas.SetTop(label, Height - 26); root.Children.Add(label);
        Place(root, Panel(palette, 460, 200, [], settled: true, "0"), 20, 0);
        Place(root, Panel(palette, 460, 200, [Mouse], true, "1"), 520, 0);
        Place(root, Panel(palette, 460, 200, Three, true, "3"), 1020, 0);
        Place(root, Panel(palette, 460, 200, Six, true, "6"), 20, 240);
        Place(root, Panel(palette, 700, 300, Six, true, "6 · 700×300"), 520, 240);
        Place(root, Panel(palette, 440, 140, Ten, true, "10 · 440×140 (最小)"), 20, 600);
        Place(root, Panel(palette, 460, 200, [], settled: false, "查找中"), 520, 600);
        Place(root, Panel(palette, 1200, 400, Ten, true, "10 · 1200×400 (最大)"), 20, 790);
        Place(root, Collapsed(palette), 1250, 250);
        Place(root, Collapsed(palette, charging: true), 1250, 300);
        root.UseLayoutRounding = true;
        root.Measure(new Size(Width, Height)); root.Arrange(new Rect(0, 0, Width, Height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var big = new RenderTargetBitmap((int)Width * 2, (int)Height * 2, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor); dc.DrawImage(bitmap, new Rect(0, 0, Width * 2, Height * 2)); }
        big.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(big));
        using var stream = File.Create(file); encoder.Save(stream);
    }

    private static void Place(Canvas canvas, UIElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); canvas.Children.Add(element); }

    // Dark at the left, light at the right, so glass shows over both.
    private static Brush Backdrop()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(0x1B, 0x1F, 0x3A), Color.FromRgb(0x6A, 0x2A, 0x86), 30), null, new RectangleGeometry(new Rect(0, 0, 760, Height))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3D)), null, new EllipseGeometry(new Point(240, 120), 90, 90)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF2)), null, new RectangleGeometry(new Rect(760, 0, Width - 760, Height))));
        var ink = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x36));
        for (int i = 0; i < 30; i++) group.Children.Add(new GeometryDrawing(ink, null, new RectangleGeometry(new Rect(800, 10 + i * 20, 420 + i * 37 % 200, 7))));
        return new DrawingBrush(group) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, Viewbox = new Rect(0, 0, Width, Height), ViewboxUnits = BrushMappingMode.Absolute };
    }

    // The open panel: its header (the page switch with the devices page chosen, the clock, the gear) and the page.
    private static UIElement Panel(PanelPalette palette, double width, double height, IReadOnlyList<DeviceBattery> devices, bool settled, string caption)
    {
        const double header = 36, side = 20, bottom = 16;
        var grid = new Grid { Width = width, Height = height };
        var pages = new Border { CornerRadius = new CornerRadius(palette.Effects.Pixel > 0 ? 0 : 8), Padding = new Thickness(2), Background = new SolidColorBrush(PanelTheme.ColorOf("switchFill")),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 6, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        string[] icons = ["M 4,21 L 4,16 M 9,21 L 9,13 M 14,21 L 14,15 M 19,21 L 19,10 M 3,12 L 9,6 L 14,10 L 21,3", "M 9,18 V 5.5 L 20,3.3 V 16", "M 2,12.5 H 6.5 L 9.5,5 L 14.5,19 L 17.5,12.5 H 22",
            "M 4.5,7 H 17 A 2,2 0 0 1 19,9 V 15 A 2,2 0 0 1 17,17 H 4.5 A 2,2 0 0 1 2.5,15 V 9 A 2,2 0 0 1 4.5,7 Z M 21.5,10.5 V 13.5 M 6.5,10.5 V 13.5 M 10,10.5 V 13.5"];
        for (int i = 0; i < icons.Length; i++)
        {
            bool on = i == 3;
            var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(icons[i]), Stroke = new SolidColorBrush(PanelTheme.ColorOf(on ? "textPrimary" : "textSecondary")), StrokeThickness = 1.65,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            row.Children.Add(new Border { Width = 28, Height = 24, CornerRadius = new CornerRadius(palette.Effects.Pixel > 0 ? 0 : 6), Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
                Background = on ? new SolidColorBrush(PanelTheme.ColorOf("fillSelected")) : Brushes.Transparent,
                Child = new Viewbox { Width = 16, Height = 16, Child = new Canvas { Width = 24, Height = 24, Children = { path } } } });
        }
        pages.Child = row; grid.Children.Add(pages);
        grid.Children.Add(new TextBlock { Text = caption == "0" ? "14:32" : "14:32", FontSize = 12, Foreground = new SolidColorBrush(PanelTheme.ColorOf("textTertiary")),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0) });
        var view = new DevicesView(DevicesLayout.For(width - 2 * side, height - header - bottom, devices.Count, null), MonitorColorsFor(palette), Devices.DefaultLowPercent)
        { Margin = new Thickness(side, header, side, bottom) };
        grid.Children.Add(view);
        view.Loaded += (_, _) => view.Update(devices, settled, motion: false);
        view.Update(devices, settled, motion: false);
        var island = ThemeSheet.Island(palette, width, height, new CornerRadius(0, 0, 16, 16), 16, grid);
        var stack = new StackPanel();
        stack.Children.Add(island);
        stack.Children.Add(new TextBlock { Text = caption + (devices.Count == 0 ? " (空)" : ""), FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(4, 2, 0, 0) });
        return stack;
    }

    private static MonitorColors MonitorColorsFor(PanelPalette palette) => MonitorThemeColors.For(MonitorPalette.Theme, new MonitorCustomColors()) with
    {
        Warning = PanelTheme.Readable(PanelTheme.WarningDefault), Critical = PanelTheme.Readable(PanelTheme.CriticalDefault)
    };

    // The collapsed notch showing CPU and the lowest battery.
    private static UIElement Collapsed(PanelPalette palette, bool charging = false)
    {
        var readings = CompactMonitor.Normalize(null);
        foreach (var reading in readings) reading.Visible = reading.Item is CompactMonitorItems.Cpu or CompactMonitorItems.Battery;
        var strip = new MonitorStrip(readings, new StripColors(MonitorColorsFor(palette)), 1, true, new StripPresence(), 1) { Width = 150 };
        strip.SetBattery(charging ? new BatteryReading(64, true, MemoryLevel.Normal, "MX Master 3S") : new BatteryReading(12, false, MemoryLevel.Critical, "F99 MAX"));
        strip.Update(MonitorSnapshot.Empty(8, "sheet"));
        return ThemeSheet.Island(palette, 220, 32, new CornerRadius(0, 0, 12, 12), 16, new Grid { Children = { strip }, VerticalAlignment = VerticalAlignment.Center });
    }

    // ----- The devices on this PC -----

    // The sources' cost in each demand, one after another in this process: CPU time and private memory over each window, with
    // nothing created at first (what BrimDeck costs while neither the page, the collapsed reading nor the notice wants devices).
    public static int Cost(int seconds)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        DeviceBatteries? batteries = null;
        void Window(string name, Action start)
        {
            start();
            Pump(TimeSpan.FromSeconds(5));
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            process.Refresh();
            var cpu = process.TotalProcessorTime; long memory = process.PrivateMemorySize64; long working = process.WorkingSet64;
            Pump(TimeSpan.FromSeconds(seconds));
            process.Refresh();
            double used = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            Console.WriteLine($"{name,-34} CPU {used,7:0} ms over {seconds} s ({used / (seconds * 10.0):0.000} % of one core)  private {process.PrivateMemorySize64 / 1048576.0,6:0.0} MB (Δ {(process.PrivateMemorySize64 - memory) / 1048576.0:+0.0;-0.0} MB)  working set {process.WorkingSet64 / 1048576.0:0.0} MB  threads {process.Threads.Count}");
        }
        Window("nothing (no DeviceBatteries)", () => { });
        Window("Watch (page or collapsed reading)", () => { batteries = new DeviceBatteries(Dispatcher.CurrentDispatcher); batteries.SetDemand(DeviceDemand.Watch); });
        Window("Listen (low battery notice only)", () => batteries!.SetDemand(DeviceDemand.Listen));
        Window("None (after the 10 s linger)", () => { batteries!.SetDemand(DeviceDemand.None); Pump(TimeSpan.FromSeconds(11)); });
        batteries?.Dispose();
        return 0;
    }
    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    public static int Probe(int seconds)
    {
        var batteries = new DeviceBatteries(Dispatcher.CurrentDispatcher);
        var started = DateTime.Now;
        batteries.Changed += () => Console.WriteLine($"[{(DateTime.Now - started).TotalSeconds:0.0}s] {batteries.All.Count} device(s), settled={batteries.Settled}");
        batteries.SetDemand(DeviceDemand.Watch);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        foreach (var device in batteries.All)
            Console.WriteLine($"  {device.Kind,-10} {Devices.Figure(device.Lowest),5} {(device.Charging ? "charging" : ""),-8} {device.Source,-11} {device.Name}  [{device.Key}{(device.Hardware is { } h ? " " + h : "")}]");
        batteries.SetDemand(DeviceDemand.None);
        batteries.Dispose();
        return 0;
    }
}
