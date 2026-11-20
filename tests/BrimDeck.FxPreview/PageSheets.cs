using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using Path = System.IO.Path;

// The real panel drawn offscreen, for the page sheets (ThemePages, PowerPages, StripFit, ExpandJitter): BrimDeck's own App
// (constructed, never run: OnStartup does not run) and its own MainWindow, whose stage is moved into an HwndSource that is never
// shown, at the scale asked for (VisualTreeHelper.SetRootDpi). The panel lays out for a 1920 x 1080 DIP monitor
// (MainWindow.UseOffscreen) and shows fake data through the paths its real sources use (MainWindow.OffscreenMedia,
// OffscreenMonitor; the notices go into the real NoticeCenter's hub).
internal static partial class PageSheets
{
    // Device pixels per DIP; set before the offscreen panel is made.
    private static double Scale { get; set; } = 1.5;
    // The monitor the panel lays out for, device pixels: 1920 x 1080 DIPs with a 48 DIP taskbar at the bottom.
    private static Rect Monitor => new(0, 0, 1920 * Scale, 1080 * Scale);
    private static Rect Work => new(0, 0, 1920 * Scale, 1032 * Scale);

    // The scale asked for: --scale <percent> among the arguments, else BRIMDECK_PAGES_SCALE, else 150 percent; null when what
    // was given is not a scale (0.5 to 4, in percent or as a factor).
    internal static double? ScaleFrom(string[] args)
    {
        int at = Array.IndexOf(args, "--scale");
        string? given = at >= 0 && at + 1 < args.Length ? args[at + 1] : Environment.GetEnvironmentVariable("BRIMDECK_PAGES_SCALE");
        if (string.IsNullOrWhiteSpace(given)) return 1.5;
        if (!double.TryParse(given.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return null;
        if (value > 4) value /= 100;
        return value is >= .5 and <= 4 ? value : null;
    }
    private enum Page { Capsule, MusicCover, MusicPlain, Monitor, Notices, Usage }
    private static readonly (Page Page, string Label)[] Pages =
    [
        (Page.Capsule, "collapsed capsule (music, cover)"), (Page.MusicCover, "music page, cover"), (Page.MusicPlain, "music page, no cover"),
        (Page.Monitor, "system monitor"), (Page.Notices, "recent notices (4)"), (Page.Usage, "AI usage (Claude, Codex; no readings yet)"),
    ];
    // BrimDeck's own App (never run) and its MainWindow, whose stage lies in a source that is never shown, with the notices and
    // no updates.
    internal static (App App, MainWindow Deck, Grid Root, HwndSource Source) Offscreen(bool animations)
    {
        // BrimDeck's own App, made but never run. WPF raises its Startup on the first dispatcher pass whether it runs or not, so it
        // is marked offscreen (UseOffscreen) before anything here lets the dispatcher run: its OnStartup then starts nothing.
        // Its instance names are this process's own besides (AppInstance.ForTest): even an App that started could not reach the
        // user's BrimDeck.
        AppInstance.ForTest();
        var app = new App();
        app.InitializeComponent();
        var settings = new DeckSettings
        {
            Language = Loc.Chinese, Style = CompactStyle.Capsule, Animations = animations, NoticesEnabled = true, NoticesPage = true,
            UsagePage = true, MusicPage = true, MonitorPage = true, LyricsEnabled = true,
        };
        // BRIMDECK_PAGES_NARROW=1: every page at the narrowest width the panel allows (a narrow window, the least a user can make).
        if (Environment.GetEnvironmentVariable("BRIMDECK_PAGES_NARROW") == "1") settings.Width = settings.MusicWidth = settings.MonitorWidth = DeckPages.MinWidth;
        settings.Normalize();
        var deck = app.UseOffscreen(settings, Monitor, Work, Scale);

        // No update to mark: the gear shows plain (the state folder is new and empty, so nothing is remembered).
        app.Updates = new AppUpdates(new NoUpdates(), Path.Combine(Path.GetTempPath(), "BrimDeck.PageSheets." + Environment.ProcessId));
        AddNotices(app);
        // The window's stage, with the window's resources, names and inherited text settings, in a source that is never shown.
        // The panel looks resources up from the window too (FindResource), so the window keeps its dictionary and the root merges it.
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(deck.Resources);
        var stage = deck.Stage;
        deck.Content = null;
        var root = new Grid { Resources = resources, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = null };
        NameScope.SetNameScope(root, NameScope.GetNameScope(deck));
        root.Children.Add(stage);
        var source = new HwndSource(new HwndSourceParameters("BrimDeck.PageSheets") { Width = 64, Height = 64, WindowStyle = unchecked((int)0x80000000) })
        { RootVisual = root, SizeToContent = SizeToContent.Manual };
        VisualTreeHelper.SetRootDpi(root, new DpiScale(Scale, Scale));
        return (app, deck, root, source);
    }

    // Shows a theme on the offscreen stage.
    internal static void UseLook(App app, MainWindow deck, Grid root, string look)
    {
        PanelTheme.Show(PanelThemes.Resolve(PanelThemes.BuiltIn(look)!));
        // The app tells its windows' trees of the new theme's brushes (DynamicResource, PanelTheme.Publish); the stage no longer
        // lies in the window, so the root carries the theme's published dictionary itself, and a new root dictionary makes every
        // reference to its keys look again.
        var again = new ResourceDictionary();
        again.MergedDictionaries.Add(deck.Resources);
        again.MergedDictionaries.Add(app.Resources.MergedDictionaries.First(dictionary => dictionary.Contains("PanelSurface")));
        root.Resources = again;
    }

    private static void Show(MainWindow deck, Grid root, Page page, MediaTrack track, BitmapSource cover, MonitorSnapshot snapshot)
    {
        deck.OffscreenMedia(page == Page.MusicPlain ? track with { Id = "fx-plain" } : track, page == Page.MusicPlain ? null : cover);
        var shown = page switch { Page.Monitor => DeckPage.Monitor, Page.Notices => DeckPage.Notices, Page.Usage => DeckPage.Usage, _ => DeckPage.Music };
        deck.SelectPage(shown, remember: false);
        deck.Preview(true, expanded: page != Page.Capsule);
        deck.OffscreenMonitor(snapshot);
        Rescale(root);
        Settle(root, deck);
        // A relayout after the data arrived.
        Settle(root, deck);
    }

    // A visual added under the root after SetRootDpi (a page built when first shown, a template applied later) keeps the
    // system's scale rather than the root's: the scale is set again, through another, so the whole tree takes it and draws again.
    internal static void Rescale(Visual root)
    {
        VisualTreeHelper.SetRootDpi(root, new DpiScale(Scale == 1 ? 2 : 1, Scale == 1 ? 2 : 1));
        VisualTreeHelper.SetRootDpi(root, new DpiScale(Scale, Scale));
        // What was drawn at the old scale (a Border rounds its lines to device pixels when it draws) is drawn again.
        static void Redraw(DependencyObject node)
        {
            if (node is UIElement element) element.InvalidateVisual();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Redraw(VisualTreeHelper.GetChild(node, i));
        }
        Redraw(root);
    }

    // Runs what the panel queued (page capsule placement, renders at background priority) and lays the stage out at the
    // window's size, carrying the window's inherited text settings over to the root the stage now lies in.
    internal static void Settle(Grid root, MainWindow deck)
    {
        foreach (var property in new DependencyProperty[] { TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.ForegroundProperty,
            TextElement.FontWeightProperty, FrameworkElement.UseLayoutRoundingProperty, TextOptions.TextFormattingModeProperty,
            TextOptions.TextRenderingModeProperty, TextOptions.TextHintingModeProperty, FrameworkElement.LanguageProperty })
            root.SetValue(property, deck.GetValue(property));
        root.Width = deck.Width; root.Height = deck.Height;
        for (int i = 0; i < 3; i++)
        {
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { });
        }
        root.UpdateLayout();
    }

    private static BitmapSource Sheet(string name, List<(string Label, BitmapSource Tile, int Column, int Row)> tiles)
    {
        const int gap = 16, label = 26, title = 40;
        int columns = tiles.Max(t => t.Column) + 1, rows = tiles.Max(t => t.Row) + 1;
        var widths = Enumerable.Range(0, columns).Select(c => tiles.Where(t => t.Column == c).Max(t => t.Tile.PixelWidth)).ToArray();
        var heights = Enumerable.Range(0, rows).Select(r => tiles.Where(t => t.Row == r).Max(t => t.Tile.PixelHeight)).ToArray();
        int width = widths.Sum() + (columns + 1) * gap, height = title + heights.Sum() + rows * (label + gap) + gap;
        var visual = new DrawingVisual();
        var face = new Typeface("Segoe UI");
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20)), null, new Rect(0, 0, width, height));
            dc.DrawText(new FormattedText($"{name} — the real panel (MainWindow, offscreen) at {Scale * 100:0} %",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 18, Brushes.White, 1), new Point(gap, 10));
            foreach (var (text, tile, column, row) in tiles)
            {
                double x = gap + widths.Take(column).Sum() + column * gap, y = title + heights.Take(row).Sum() + row * (label + gap);
                dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 14, new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE6)), 1), new Point(x, y + 4));
                dc.DrawImage(tile, new Rect(x, y + label, tile.PixelWidth, tile.PixelHeight));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    internal static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ----- The data -----

    internal static MediaTrack Track()
    {
        var now = DateTimeOffset.UtcNow;
        // Made-up lines (no song's real lyrics), timed around the position.
        string lyrics = string.Join("\n",
            "[00:58.00]晚风穿过窗台的灯", "[01:04.00]把今天轻轻折好", "[01:10.50]Lights on the water, slow and wide", "[01:17.00]我们沿着河岸走到天亮", "[01:23.00]Every street sings back to us");
        return new MediaTrack
        {
            Id = "fx-cover", Source = "网易云音乐", Title = "海边的第七个夏天", Artist = "Fable Lights", Album = "Harbor Notes", EmbeddedLyrics = lyrics,
            State = BrimDeck.Core.MediaState.Paused, CanToggle = true, CanPrevious = true, CanNext = true, CanSeek = true, CanShuffle = true, CanRepeat = true,
            CanLike = true, Liked = true, Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(225), ReportedPosition = TimeSpan.FromSeconds(72), PositionAt = now,
        };
    }

    // A made-up album cover: saturated color fields and a sun.
    internal static BitmapSource Cover()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0x5E, 0x3A), Color.FromRgb(0x6A, 0x1B, 0x9A), 90), null, new Rect(0, 0, 300, 300));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x2E)), null, new Point(150, 140), 70, 70);
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x0E, 0x6B, 0xA8), Color.FromRgb(0x08, 0x3D, 0x77), 90), null, new Rect(0, 190, 300, 110));
            for (int i = 0; i < 6; i++) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xA9, 0x4D)), null, new Rect(90 + i * 6, 200 + i * 15, 120 - i * 12, 4));
        }
        var bitmap = new RenderTargetBitmap(300, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    internal static MonitorSnapshot Snapshot()
    {
        var random = new Random(7);
        var cores = new List<CoreSample>();
        for (int i = 0; i < 16; i++)
        {
            bool performance = i < 8;
            cores.Add(new CoreSample(i, performance ? 0 : 1, performance ? CoreTier.Performance : CoreTier.Efficiency, performance ? i + 1 : i - 7,
                Math.Round(.08 + random.NextDouble() * (performance ? .7 : .35), 2), Reading<double>.Of(performance ? 4200 : 3100), performance ? 5400 : 3900));
        }
        const ulong GB = 1UL << 30, MB = 1UL << 20;
        ProcessSample P(int pid, string name, double cpu, ulong memory, string? app = null) => new(pid, name, cpu, memory, app);
        var processes = new List<ProcessSample>
        {
            P(4120, "chrome.exe", 38.5, 2100 * MB, "chrome"), P(9312, "devenv.exe", 22.1, 1650 * MB, "devenv"), P(2208, "Code.exe", 12.4, 980 * MB, "code"),
            P(7781, "Teams.exe", 6.2, 720 * MB, "teams"), P(1440, "dwm.exe", 4.8, 210 * MB), P(5530, "BrimDeck.exe", 1.2, 96 * MB, "brimdeck"),
            P(6604, "explorer.exe", 0.9, 180 * MB), P(8892, "OneDrive.exe", 0.4, 140 * MB, "onedrive"),
        };
        var top = ProcessSample.Folded(processes, ProcessSort.Cpu);
        var byMemory = ProcessSample.Folded(processes, ProcessSort.Memory);
        double[] History(double level, double swing) => Enumerable.Range(0, 60).Select(i => Math.Clamp(level + swing * Math.Sin(i / 5.0) + (random.NextDouble() - .5) * swing, 0, 1)).ToArray();
        return new MonitorSnapshot(DateTimeOffset.Now, 16,
            Reading<CpuSample>.Of(new CpuSample(cores, Reading<double>.Of(3900))),
            Reading<MemorySample>.Of(new MemorySample(32 * GB, 19 * GB, 26 * GB, 48 * GB, 7 * GB)),
            Reading<NetworkSample>.Of(new NetworkSample(4_800_000, 320_000)),
            Reading<GpuSample>.Of(new GpuSample(.37, Reading<ulong>.Of(3 * GB + 400 * MB))
            {
                Engines = [new GpuEngine("3D", .37), new GpuEngine("Copy", .05), new GpuEngine("VideoDecode", .12)],
                SharedBytes = Reading<ulong>.Of(600 * MB), Name = "NVIDIA GeForce RTX 4070", DedicatedTotal = 12 * GB,
            }),
            Reading<ProcessList>.Of(new ProcessList(top, byMemory)))
        {
            DownHistory = History(.4, .3).Select(v => v * 8_000_000).ToArray(), UpHistory = History(.2, .15).Select(v => v * 1_000_000).ToArray(),
            CpuHistory = History(.35, .2), GpuHistory = History(.3, .2),
        };
    }

    // Four recent notices, received by the real hub as its sources would hand them over (nothing is shown as it arrives).
    private static void AddNotices(App app)
    {
        var hub = app.Notices.Hub;
        var now = DateTimeOffset.UtcNow;
        NoticeInput[] inputs =
        [
            new() { Origin = NoticeOrigin.Windows, AppKey = "windows:Microsoft.Outlook", AppName = "Outlook", InstanceKey = "fx-1", Title = "周报已提交", Body = "本周进度：液态玻璃第 8 轮，页面样张与检查", CreatedAt = now.AddMinutes(-42) },
            new() { Origin = NoticeOrigin.Cli, AppKey = "cli:brim-notify", AppName = "brim-notify", InstanceKey = "fx-2", Title = "构建完成", Body = "BrimDeck.sln：0 个警告，0 个错误", Category = NoticeCategory.Success, CreatedAt = now.AddMinutes(-18) },
            new() { Origin = NoticeOrigin.Windows, AppKey = "windows:MSTeams", AppName = "Microsoft Teams", InstanceKey = "fx-3", Title = "设计评审改到 15:30", Body = "会议室 4B · 请带上页面样张", CreatedAt = now.AddMinutes(-6) },
            new() { Origin = NoticeOrigin.Cli, AppKey = "cli:claude", AppName = "Claude Code", InstanceKey = "fx-4", Title = "正在运行测试", Body = "WindowsTests 212/350", Category = NoticeCategory.Task, Progress = .6, CreatedAt = now.AddMinutes(-1) },
        ];
        foreach (var input in inputs) hub.Receive(input, now);
        Console.WriteLine($"Notices in the hub: {hub.Notices.Count}");
    }

    private sealed class NoUpdates : IAppUpdateBackend
    {
        public string CurrentVersion => ApplicationVersion.Current;
        public bool CanInstall => false;
        public AppRelease? PendingRelease => null;
        public Task<AppRelease?> CheckAsync(CancellationToken cancellation) => Task.FromResult<AppRelease?>(null);
        public Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation) => Task.CompletedTask;
        public void PrepareRestart() { }
    }
}
