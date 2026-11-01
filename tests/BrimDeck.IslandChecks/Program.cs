using System.Diagnostics;
using System.Globalization;
using System.Reflection;
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

// The notice window's warm-up on the real MainWindow, offscreen (B8, 2026-11-01): IslandNotice.Prepare() shows the window once, far
// off screen and transparent, six seconds after the panel starts. Until 11-01 it threw a NullReferenceException there on every start
// (the layers drew before the island was laid out and asked for its form), left the window shown, and crash.log recorded it.
//   exit code 0: every check passed; 1: one failed (the first failure is printed).
//   --rows N: instead, how long the notices page takes to build, lay out and draw with N notices listed (the list is built whole, in one
//   frame; B8 task 6: only above 16 ms would it have to be built a couple of rows a frame again). Prints milliseconds, cold (the page's
//   first build in this process, which also pays for JIT and templates) and warm (median of 15 more), for N and for 8 notices.
internal static class Checks
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            int at = Array.IndexOf(args, "--rows");
            if (at >= 0 && at + 1 < args.Length) return Rows(int.Parse(args[at + 1], CultureInfo.InvariantCulture));
            AppInstance.ForTest();
            var app = new App();
            app.InitializeComponent();
            var settings = new DeckSettings { Language = Loc.English, Style = CompactStyle.Capsule, Animations = false, NoticesEnabled = true, NoticesPage = true };
            settings.Normalize();
            var deck = app.UseOffscreen(settings, new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1032), 1);
            app.Updates = new AppUpdates(new NoUpdates(), Path.Combine(Path.GetTempPath(), "BrimDeck.IslandChecks." + Environment.ProcessId));
            foreach (var style in new[] { CompactStyle.Capsule, CompactStyle.Notch, CompactStyle.Line })
            {
                settings.Style = style;
                deck.ApplySettings();
                var window = new IslandNotice(deck, app);
                try { window.Prepare(); }
                catch (Exception ex) { return Fail($"warm-up ({style}) threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { });
                if (window.IsVisible) return Fail($"warm-up ({style}) left the notice window shown");
                if (window.Covering) return Fail($"warm-up ({style}) left the notice window covering the island");
                Console.WriteLine($"PASS: the notice window warms up on a {style} island without throwing and ends hidden");
                window.Close();
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    private static NoticeInput N(int i) => new()
    {
        Origin = i % 2 == 0 ? NoticeOrigin.Windows : NoticeOrigin.Cli, AppKey = "app:" + i % 7, AppName = "App " + i % 7, InstanceKey = "m" + i,
        Title = $"Notice {i + 1}", Body = $"Body of notice {i + 1}, a line long enough to need its preview cut", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i - 1),
        Category = (i % 6) switch { 2 => NoticeCategory.Error, 4 => NoticeCategory.Warning, _ => NoticeCategory.Message }, Capabilities = NoticeCapabilities.OpenApp
    };

    private static int Rows(int rows)
    {
        const double scale = 1.5;
        AppInstance.ForTest();
        var app = new App();
        app.InitializeComponent();
        var settings = new DeckSettings { Language = Loc.English, Style = CompactStyle.Capsule, Animations = false, NoticesEnabled = true, NoticesPage = true, UsagePage = true, MusicPage = true, MonitorPage = true };
        settings.Normalize();
        var deck = app.UseOffscreen(settings, new Rect(0, 0, 1920 * scale, 1080 * scale), new Rect(0, 0, 1920 * scale, 1032 * scale), scale);
        app.Updates = new AppUpdates(new NoUpdates(), Path.Combine(Path.GetTempPath(), "BrimDeck.IslandChecks." + Environment.ProcessId));
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(deck.Resources);
        var stage = deck.Stage;
        deck.Content = null;
        var root = new Grid { Resources = resources, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = null };
        NameScope.SetNameScope(root, NameScope.GetNameScope(deck));
        root.Children.Add(stage);
        using var source = new HwndSource(new HwndSourceParameters("BrimDeck.IslandChecks") { Width = 64, Height = 64, WindowStyle = unchecked((int)0x80000000) }) { RootVisual = root, SizeToContent = SizeToContent.Manual };
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        PanelTheme.Show(PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!));
        deck.ApplySettings();
        void Settle()
        {
            foreach (var property in new DependencyProperty[] { TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.ForegroundProperty, TextElement.FontWeightProperty, FrameworkElement.UseLayoutRoundingProperty, TextOptions.TextFormattingModeProperty, TextOptions.TextRenderingModeProperty, TextOptions.TextHintingModeProperty, FrameworkElement.LanguageProperty })
                root.SetValue(property, deck.GetValue(property));
            root.Width = deck.Width; root.Height = deck.Height;
            root.UpdateLayout();
        }
        void Load(int count)
        {
            app.Notices.Hub.Clear();
            foreach (var input in Enumerable.Range(0, count).Select(N).OrderBy(i => i.CreatedAt)) app.Notices.Hub.Receive(input, input.CreatedAt!.Value);
            typeof(MainWindow).GetField("_freshNotices", NP)!.SetValue(deck, app.Notices.Hub.Notices.Take(12).Select(n => n.Id).ToHashSet());
        }
        var render = typeof(MainWindow).GetMethod("RenderNotices", NP)!;
        (double Build, double Layout, double Draw) Once()
        {
            var watch = Stopwatch.StartNew();
            render.Invoke(deck, [true]);
            double build = watch.Elapsed.TotalMilliseconds; watch.Restart();
            Settle();
            double layout = watch.Elapsed.TotalMilliseconds; watch.Restart();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            double draw = watch.Elapsed.TotalMilliseconds;
            return (build, layout, draw);
        }
        // The rows still to come are added a few a frame at background priority: waited for here, to see that every notice ends up listed
        // once, in order, and how long that takes (in dispatcher passes, not frames of a display).
        var pageRows = typeof(MainWindow).GetField("_pageRows", NP)!;
        (int Listed, bool InOrder, double Milliseconds) Filled(int expected)
        {
            var watch = Stopwatch.StartNew();
            while (((System.Collections.ICollection)pageRows.GetValue(deck)!).Count < expected && watch.ElapsedMilliseconds < 5000)
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });
            var ids = ((System.Collections.IEnumerable)pageRows.GetValue(deck)!).Cast<object>().Select(r => ((Notice)r.GetType().GetField("Item1")!.GetValue(r)!).Id).ToList();
            return (ids.Count, ids.SequenceEqual(app.Notices.Hub.Notices.Select(n => n.Id)), watch.Elapsed.TotalMilliseconds);
        }
        deck.SelectPage(DeckPage.Notices, remember: false);
        deck.Preview(true, expanded: true);
        Settle();
        foreach (int count in new[] { rows, 8 })
        {
            Load(count);
            Settle();
            var cold = Once();
            var filled = Filled(count);
            Console.WriteLine($"{(filled.Listed == count && filled.InOrder ? "PASS" : "FAIL")}: {count} notices listed {filled.Listed} rows, in the hub's order: {filled.InOrder}, the rest added in {filled.Milliseconds:F0} ms");
            if (filled.Listed != count || !filled.InOrder) return 1;
            var warm = Enumerable.Range(0, 15).Select(_ => Once()).ToList();
            double Median(Func<(double Build, double Layout, double Draw), double> pick) => warm.Select(pick).OrderBy(v => v).ElementAt(warm.Count / 2);
            Console.WriteLine($"{count,3} notices  cold: build {cold.Build:F1} layout {cold.Layout:F1} draw {cold.Draw:F1} = {cold.Build + cold.Layout + cold.Draw:F1} ms   warm (median of 15): build {Median(w => w.Build):F1} layout {Median(w => w.Layout):F1} draw {Median(w => w.Draw):F1} = {Median(w => w.Build + w.Layout + w.Draw):F1} ms");
        }
        return 0;
    }

    private static int Fail(string message) { Console.Error.WriteLine("FAIL: " + message); return 1; }

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
