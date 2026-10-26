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
using BrimDeck.LiquidGlass;
using Path = System.IO.Path;

// The real panel over liquid glass, drawn offscreen: BrimDeck's own App (constructed, never run: OnStartup does not run) and
// its own MainWindow, whose stage is moved into an HwndSource that is never shown, at 150 percent (VisualTreeHelper.SetRootDpi).
// The panel lays out for a 1920 x 1080 DIP monitor (MainWindow.UseOffscreen), shows fake data through the paths its real
// sources use (MainWindow.OffscreenMedia, OffscreenMonitor; the notices go into the real NoticeCenter's hub) and takes liquid
// glass as shown (OffscreenLiquidFrame: the island's surface steps aside and its painted rim with it, the shadow stays open).
// The island's whole tree, its shadow included, is drawn with RenderTargetBitmap and laid over the glass LiquidRenderer draws
// for the island's shape and place over a top-of-screen background (the island centred at the top, as the app places it).
// One sheet per background: the collapsed capsule, the music page with a cover and without, the system monitor, the recent
// notices and the AI usage page (empty: its settings entry), in liquid glass light and dark. Each page is checked: its content
// is drawn (not blank), the glass shows through it (a page that paints the island over, a black page, fails), its text and
// glyphs keep contrast against the glass under them, and the shadow shows below the island.
//   dotnet run -c Release --no-build --project tests/BrimDeck.FxPreview -- --liquid-pages <backgrounds folder> <output folder> [--scale 125]
// The backgrounds are *-150.png files (1800 x 450 device pixels); without any, grayui-150, chrome-dark-150 and video-150 are
// looked for by name. The display scale is 150 percent unless --scale (or BRIMDECK_PAGES_SCALE) gives another, in percent
// (100, 125, 150, 175): the same backgrounds are used at every scale, and the sheets of another scale than 150 are named
// pages-<background>-at<percent>.png.
// With --focus, the keyboard focus pass runs instead (LiquidPages.Focus.cs): every Tab stop of every page focused, its focus
// ring measured over the glass, the Tab order checked.
internal static partial class LiquidPages
{
    // Device pixels per DIP; set before the offscreen panel is made (Run), 1.5 for ShadowCost.
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
    private static int _failures;

    private enum Page { Capsule, MusicCover, MusicPlain, Monitor, Notices, Usage }
    private static readonly (Page Page, string Label)[] Pages =
    [
        (Page.Capsule, "collapsed capsule (music, cover)"), (Page.MusicCover, "music page, cover"), (Page.MusicPlain, "music page, no cover"),
        (Page.Monitor, "system monitor"), (Page.Notices, "recent notices (4)"), (Page.Usage, "AI usage (Claude, Codex; no readings yet)"),
    ];
    private static readonly string[] Looks = [PanelThemes.GlassLight, PanelThemes.GlassDark];

    // One page drawn: the panel's layer (premultiplied BGRA, the root's size in device pixels) and the island's rectangle in it.
    // Runs marks the pixels inside the theme's secondary (2) and tertiary (3) text runs (TextBlocks drawn in that brush).
    private sealed record Layer(byte[] Pixels, int Width, int Height, int X, int Y, int W, int H, double CornerTop, double CornerBottom, double Shoulder, bool ShadowShown, Color Text, bool HasText,
        byte[] Runs, Color Secondary, Color Tertiary, double Open, float[]? TextRects);

    // BrimDeck's own App (never run) and its MainWindow, whose stage lies in a source that is never shown, with the notices and
    // no updates. Also used by ShadowCost (with animations on).
    internal static (App App, MainWindow Deck, Grid Root, HwndSource Source) Offscreen(bool animations)
    {
        // BrimDeck's own App, made but never run. WPF raises its Startup on the first dispatcher pass whether it runs or not, so it
        // is marked offscreen (UseOffscreen) before anything here lets the dispatcher run: its OnStartup then starts nothing.
        // Its instance names are this process's own besides (AppInstance.ForTest): even an App that started could not reach the
        // user's BrimDeck.
        AppInstance.ForTest();
        // LIQUID_R13: round 13 as it was (whole-leaf text regions, no halo), for the comparisons.
        if (Environment.GetEnvironmentVariable("LIQUID_R13") == "1") { MainWindow.GlassRegionsWhole = true; MainWindow.GlassHaloOff = true; }
        // LIQUID_SHADOW_R13: the island's shadow under liquid glass as before round 14 (the theme's one shadow), for the comparisons.
        if (Environment.GetEnvironmentVariable("LIQUID_SHADOW_R13") == "1") MainWindow.ShadowLookOff = true;
        var app = new App();
        app.InitializeComponent();
        var settings = new DeckSettings
        {
            Language = Loc.Chinese, Style = CompactStyle.Capsule, Animations = animations, NoticesEnabled = true, NoticesPage = true,
            UsagePage = true, MusicPage = true, MonitorPage = true, LyricsEnabled = true,
        };
        settings.Normalize();
        var deck = app.UseOffscreen(settings, Monitor, Work, Scale);
        if (!MotionEnvironment.Transparency) Console.WriteLine("NOTE: Windows transparency effects are off, so glass themes are not shown as glass; the pages are drawn opaque.");

        // No update to mark: the gear shows plain (the state folder is new and empty, so nothing is remembered).
        app.Updates = new AppUpdates(new NoUpdates(), Path.Combine(Path.GetTempPath(), "BrimDeck.LiquidPages." + Environment.ProcessId));
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
        var source = new HwndSource(new HwndSourceParameters("BrimDeck.LiquidPages") { Width = 64, Height = 64, WindowStyle = unchecked((int)0x80000000) })
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

    public static int Run(string backgrounds, string folder, double scale = 1.5)
    {
        Scale = scale;
        Console.WriteLine($"Display scale: {Scale * 100:0} %");
        var files = Directory.GetFiles(backgrounds, "*-150.png").Order().ToList();
        var wanted = new[] { "grayui-150.png", "chrome-dark-150.png", "video-150.png" };
        if (files.Count > 3 && wanted.All(name => files.Any(file => Path.GetFileName(file) == name)))
            files = wanted.Select(name => Path.Combine(backgrounds, name)).ToList();
        if (files.Count == 0) { Console.WriteLine("FAIL: no *-150.png backgrounds in " + backgrounds); return 1; }
        Directory.CreateDirectory(folder);
        var (app, deck, root, source) = Offscreen(animations: false);
        using var _ = source;

        var cover = Cover();
        var track = Track();
        var snapshot = Snapshot();
        using var device = LiquidPreview.CreateDevice();
        using var renderer = new LiquidRenderer(device);

        // Every page in both looks over each background: drawn again for each, since the island's shadow under liquid glass
        // follows what lies behind it (the glass's measure, MainWindow.OffscreenBackdrop).
        foreach (var file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            var decoded = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(Path.GetFullPath(file)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            int bw = decoded.PixelWidth, bh = decoded.PixelHeight;
            var background = new byte[bw * bh * 4];
            decoded.CopyPixels(background, bw * 4, 0);
            var tiles = new List<(string Label, BitmapSource Tile, int Column, int Row)>();
            for (int column = 0; column < Looks.Length; column++)
            {
                string look = Looks[column];
                UseLook(app, deck, root, look);
                if (!PanelTheme.GlassShown) Console.WriteLine($"NOTE: {look}: glass is not shown (transparency effects off or high contrast).");
                for (int row = 0; row < Pages.Length; row++)
                {
                    var (page, label) = Pages[row];
                    var layer = Draw(deck, root, page, track, cover, snapshot, (background, bw, bh), $"{name} {look} {page}");
                    var tile = Compose(device, renderer, background, bw, bh, layer, look == PanelThemes.GlassDark, $"{name} {look} {page}");
                    // LIQUID_TILES=<folder>: each tile saved on its own as well (the shadow's before and after crops).
                    if (Environment.GetEnvironmentVariable("LIQUID_TILES") is { Length: > 0 } tiled)
                    {
                        Directory.CreateDirectory(tiled);
                        LiquidPreview.Save(tile, Path.Combine(tiled, $"{name}-{look}-{page}{(Math.Abs(Scale - 1.5) < .001 ? "" : $"-at{Scale * 100:0}")}.png"));
                    }
                    tiles.Add(($"{label} · {(look == PanelThemes.GlassDark ? "liquid dark" : "liquid light")}", tile, column, row));
                }
            }
            var sheet = Sheet(name, tiles);
            string sheetName = Math.Abs(Scale - 1.5) < .001 ? $"pages-{name}.png" : $"pages-{name}-at{Scale * 100:0}.png";
            LiquidPreview.Save(sheet, Path.Combine(folder, sheetName));
            Console.WriteLine($"Wrote {sheetName}");
        }
        Console.WriteLine(_failures == 0 ? "PASS: all page checks" : $"{_failures} page checks failed");
        return _failures == 0 ? 0 : 1;
    }

    // ----- The panel -----

    // Shows a page with its data on the offscreen stage, laid out, the glass's frame taken (Draw, and the focus pass).
    private static void Show(MainWindow deck, Grid root, Page page, MediaTrack track, BitmapSource cover, MonitorSnapshot snapshot)
    {
        deck.OffscreenMedia(page == Page.MusicPlain ? track with { Id = "fx-plain" } : track, page == Page.MusicPlain ? null : cover);
        var shown = page switch { Page.Monitor => DeckPage.Monitor, Page.Notices => DeckPage.Notices, Page.Usage => DeckPage.Usage, _ => DeckPage.Music };
        deck.SelectPage(shown, remember: false);
        deck.Preview(true, expanded: page != Page.Capsule);
        deck.OffscreenMonitor(snapshot);
        Rescale(root);
        deck.OffscreenLiquidFrame();
        Settle(root, deck);
        // A relayout after the data arrived; the next frame of the glass takes the surface aside again.
        deck.OffscreenLiquidFrame();
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

    private static Layer Draw(MainWindow deck, Grid root, Page page, MediaTrack track, BitmapSource cover, MonitorSnapshot snapshot,
        (byte[] Pixels, int Width, int Height) background, string name)
    {
        Show(deck, root, page, track, cover, snapshot);
        // What lies behind the island where Compose places it (the top centre), as the glass measures it: the 2nd and 98th
        // percentiles of its luma (LiquidRenderer.Band; the renderer reads a downsample, so its band can be a little narrower).
        {
            int iw = (int)Math.Round(deck.Island.ActualWidth * Scale), ih = (int)Math.Round(deck.Island.ActualHeight * Scale);
            int iy = (int)Math.Round(deck.Island.TranslatePoint(new Point(), root).Y * Scale), ix = (int)Math.Round((background.Width - iw) / 2.0);
            var lumas = new List<float>(iw * ih);
            for (int by = iy; by < iy + ih; by++)
                for (int bx = Math.Max(0, ix); bx < Math.Min(background.Width, ix + iw); bx++)
                {
                    int d = (Math.Clamp(by, 0, background.Height - 1) * background.Width + bx) * 4;
                    lumas.Add((float)((.0722 * background.Pixels[d] + .7152 * background.Pixels[d + 1] + .2126 * background.Pixels[d + 2]) / 255));
                }
            lumas.Sort();
            float low = lumas.Count == 0 ? 0 : lumas[(int)(lumas.Count * .02)], high = lumas.Count == 0 ? 1 : lumas[Math.Min(lumas.Count - 1, (int)(lumas.Count * .98))];
            double tone = deck.OffscreenBackdrop(low, high);
            Settle(root, deck);
            Console.WriteLine(FormattableString.Invariant($"NOTE: {name}: backdrop band {low:0.00}..{high:0.00}, shadow tone {tone:0.00}: {deck.OffscreenShadowLook}"));
        }

        // The XAML's theme brushes (DynamicResource) follow the theme on the stage as in the window: the toolbar's clock.
        var clock = (deck.ClockLabel.Foreground as SolidColorBrush)?.Color;
        Report(clock == PanelTheme.ColorOf("textTertiary"), $"{PanelTheme.Current.Id} {page}: the panel's XAML follows the theme (clock {clock}, theme's tertiary text {PanelTheme.ColorOf("textTertiary")})");
        // The glass's own decision (GlassBackdrop through its stand-in host, as on screen): liquid glass shown, the island's surface
        // aside and clear, the shadow's caster opaque. An island painted over the glass (the surface not stepping aside) fails here
        // and in the pixel checks below.
        if (PanelTheme.GlassShown)
        {
            var state = deck.OffscreenGlassState;
            Report(state.Shown && state.Aside && state.Surface <= .01 && state.Caster,
                $"{PanelTheme.Current.Id} {page}: GlassBackdrop's decision: liquid glass shown {state.Shown}, surface aside {state.Aside}, surface opacity {state.Surface:0.00}, shadow caster opaque {state.Caster}");
        }
        var island = deck.Island;
        var at = island.TranslatePoint(new Point(), root);
        // As GlassBackdrop measures the island: its bounds in device pixels, the size and place rounded.
        int x = (int)Math.Round(at.X * Scale), y = (int)Math.Round(at.Y * Scale);
        int w = (int)Math.Round(island.ActualWidth * Scale), h = (int)Math.Round(island.ActualHeight * Scale);
        int width = (int)Math.Ceiling(root.ActualWidth * Scale), height = (int)Math.Ceiling(root.ActualHeight * Scale);
        // The stage, which lies at the root's top left (the source's root visual itself draws nothing into a RenderTargetBitmap).
        var bitmap = new RenderTargetBitmap(width, height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(deck.Stage);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var c = island.CornerRadius;
        Color secondary = PanelTheme.ColorOf("textSecondary"), tertiary = PanelTheme.ColorOf("textTertiary");
        return new Layer(pixels, width, height, x, y, w, h, c.TopLeft * Scale, c.BottomLeft * Scale, island.ShoulderRadius * Scale, deck.ShadowHost.Opacity > 0,
            PanelTheme.ColorOf("textPrimary"), page != Page.Capsule, Runs(root, deck.Stage, width, height, secondary, tertiary), secondary, tertiary,
            deck.GlassOpenness(), deck.GlassTextRegions() is { } regions ? [.. regions.SelectMany(r => new[] { (float)(r.Left * Scale), (float)(r.Top * Scale), (float)(r.Right * Scale), (float)(r.Bottom * Scale) })] : null);
    }

    // The secondary and tertiary text runs: every TextBlock shown whose brush (its own or inherited) is the theme's secondary
    // or tertiary text color, its bounds marked in device pixels of the root.
    private static byte[] Runs(Grid root, Visual stage, int width, int height, Color secondary, Color tertiary)
    {
        var runs = new byte[width * height];
        void Walk(DependencyObject node)
        {
            if (node is UIElement { IsVisible: false }) return;
            if (node is TextBlock text && text.Foreground is SolidColorBrush brush && (brush.Color == secondary || brush.Color == tertiary) && text.ActualWidth > 0)
            {
                byte kind = (byte)(brush.Color == secondary ? 2 : 3);
                var bounds = text.TransformToAncestor(root).TransformBounds(new Rect(text.RenderSize));
                int x0 = Math.Max(0, (int)Math.Floor(bounds.Left * Scale)), y0 = Math.Max(0, (int)Math.Floor(bounds.Top * Scale));
                int x1 = Math.Min(width, (int)Math.Ceiling(bounds.Right * Scale)), y1 = Math.Min(height, (int)Math.Ceiling(bounds.Bottom * Scale));
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++) runs[y * width + x] = kind;
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(stage);
        return runs;
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

    // ----- Over the glass -----

    private static BitmapSource Compose(Vortice.Direct3D11.ID3D11Device device, LiquidRenderer renderer, byte[] background, int bw, int bh, Layer layer, bool dark, string name)
    {
        // The desktop: the background, its last row repeated below it where an open panel reaches lower.
        int dw = bw, dh = Math.Max(bh, layer.Y + layer.H + (int)Math.Ceiling(40 * Scale));
        var desktop = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++) Buffer.BlockCopy(background, Math.Min(y, bh - 1) * bw * 4, desktop, y * dw * 4, dw * 4);
        // The island at the top centre, as on the monitor; the layer is shifted by as much.
        int gx = (int)Math.Round((dw - layer.W) / 2.0), gy = layer.Y, ox = layer.X - gx;
        // How far the panel is open, as the app gives it to the glass (MainWindow.GlassOpenness): its frost and calmer backdrop.
        var frame = LiquidFrame.Preset(dark, layer.W, layer.H, (float)layer.CornerTop, (float)layer.CornerBottom, (float)layer.Shoulder, (float)Scale)
            with { Open = (float)layer.Open, TextRects = layer.TextRects };
        if (Environment.GetEnvironmentVariable("LIQUID_DEBUG") == "1") Console.WriteLine($"NOTE: {name}: {layer.TextRects?.Length / 4 ?? -1} text regions, open {layer.Open:0.00}, island {layer.W}x{layer.H}: " + (layer.TextRects is { } tr ? string.Join(" ", Enumerable.Range(0, tr.Length / 4).Select(i => $"[{tr[i * 4]:0},{tr[i * 4 + 1]:0},{tr[i * 4 + 2]:0},{tr[i * 4 + 3]:0}]")) : ""));
        var glass = LiquidPreview.Render(device, renderer, desktop, dw, dh, (gx, gy, layer.W, layer.H), frame, (int)Math.Ceiling(48 * Scale));
        var under = (byte[])desktop.Clone();
        for (int y = 0; y < layer.H; y++)
            for (int x = 0; x < layer.W; x++)
                Over(glass, (y * layer.W + x) * 4, under, ((gy + y) * dw + gx + x) * 4);
        var composed = (byte[])under.Clone();
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                int lx = x + ox;
                if (lx < 0 || lx >= layer.Width || y >= layer.Height) continue;
                Over(layer.Pixels, (y * layer.Width + lx) * 4, composed, (y * dw + x) * 4);
            }
        Check(name, layer, glass, under, composed, dw, gx, gy);
        Body(name, layer, glass, under, desktop, dw, gx, gy);
        // The tile: the island and its shadow with a margin, the background around it.
        int margin = (int)Math.Round(36 * Scale);
        int tx = Math.Max(0, gx - margin), tw = Math.Min(dw, gx + layer.W + margin) - tx, th = Math.Min(dh, gy + layer.H + margin);
        var tile = new byte[tw * th * 4];
        for (int y = 0; y < th; y++) Buffer.BlockCopy(composed, (y * dw + tx) * 4, tile, y * tw * 4, tw * 4);
        return BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgra32, null, tile, tw * 4);
    }

    // Round 13b: the open panel's body between its text (the glass, not within 8 DIPs of any glyph run, where the page draws
    // nothing): its mean luma and luma std ("whiteness"), and its chroma against the backdrop's there. A note, not a check.
    private static void Body(string name, Layer layer, byte[] glass, byte[] under, byte[] desktop, int dw, int gx, int gy)
    {
        if (layer.Open < .5) return;
        int w = layer.W, h = layer.H, grow = (int)Math.Round(8 * Scale);
        // Near content: within 8 DIPs of anything the page draws (text, icons, covers, bars), whichever regions the glass covers.
        var sum2 = new int[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int drawn = layer.Pixels[((layer.Y + y) * layer.Width + layer.X + x) * 4 + 3] > 8 ? 1 : 0;
                sum2[(y + 1) * (w + 1) + x + 1] = drawn + sum2[y * (w + 1) + x + 1] + sum2[(y + 1) * (w + 1) + x] - sum2[y * (w + 1) + x];
            }
        var near = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - grow), x1 = Math.Min(w, x + grow + 1), y0 = Math.Max(0, y - grow), y1 = Math.Min(h, y + grow + 1);
                near[y * w + x] = sum2[y1 * (w + 1) + x1] - sum2[y0 * (w + 1) + x1] - sum2[y1 * (w + 1) + x0] + sum2[y0 * (w + 1) + x0] > 0;
            }
        double sum = 0, squares = 0, chroma = 0, back = 0; int n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (near[y * w + x] || glass[(y * w + x) * 4 + 3] < 250) continue;
                int l = ((layer.Y + y) * layer.Width + layer.X + x) * 4;
                if (layer.Pixels[l + 3] > 8) continue;
                int d = ((gy + y) * dw + gx + x) * 4;
                double luma = Lightness(under, d);
                sum += luma; squares += luma * luma; n++;
                chroma += Math.Max(under[d], Math.Max(under[d + 1], under[d + 2])) - Math.Min(under[d], Math.Min(under[d + 1], under[d + 2]));
                back += Math.Max(desktop[d], Math.Max(desktop[d + 1], desktop[d + 2])) - Math.Min(desktop[d], Math.Min(desktop[d + 1], desktop[d + 2]));
            }
        if (n < 100) { Console.WriteLine($"NOTE: {name}: body: too little of it between the text ({n} pixels)"); return; }
        double mean = sum / n, std = Math.Sqrt(Math.Max(0, squares / n - mean * mean));
        // Blotchiness (round 14): the glass alone's luma at middle frequencies (a box of 8 DIPs less one of 32), its std over
        // the panel inside 20 DIPs of its edge: patches of cover round the text show here, the backdrop's frosted forms too.
        var lum = new double[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) lum[y * w + x] = Lightness(under, ((gy + y) * dw + gx + x) * 4);
        var integral = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) integral[(y + 1) * (w + 1) + x + 1] = lum[y * w + x] + integral[y * (w + 1) + x + 1] + integral[(y + 1) * (w + 1) + x] - integral[y * (w + 1) + x];
        double Box(int x, int y, int r)
        {
            int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1), y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
            return (integral[y1 * (w + 1) + x1] - integral[y0 * (w + 1) + x1] - integral[y1 * (w + 1) + x0] + integral[y0 * (w + 1) + x0]) / ((x1 - x0) * (y1 - y0));
        }
        int fine = (int)Math.Round(4 * Scale), coarse = (int)Math.Round(16 * Scale), inset = (int)Math.Round(20 * Scale);
        double bs = 0, bq = 0; int bn = 0;
        for (int y = inset; y < h - inset; y += 2)
            for (int x = inset; x < w - inset; x += 2)
            {
                if (glass[(y * w + x) * 4 + 3] < 250) continue;
                double v = Box(x, y, fine) - Box(x, y, coarse);
                bs += v; bq += v * v; bn++;
            }
        double blotch = bn == 0 ? 0 : Math.Sqrt(Math.Max(0, bq / bn - bs / bn * (bs / bn)));
        Console.WriteLine(FormattableString.Invariant($"NOTE: {name}: body: {n * 100.0 / (w * h):0}% of the panel, mean luma {mean:0.0}, std {std:0.0}, chroma kept {chroma / Math.Max(back, 1):0.00}, blotchiness {blotch:0.00}"));
    }

    // Premultiplied source over an opaque destination.
    private static void Over(byte[] source, int s, byte[] target, int t)
    {
        double a = source[s + 3] / 255d;
        if (a <= 0) return;
        for (int c = 0; c < 3; c++) target[t + c] = (byte)Math.Clamp(Math.Round(source[s + c] + (1 - a) * target[t + c]), 0, 255);
    }

    // ----- Checks -----

    // A primary text pixel: its layer alpha at least Core (a glyph's core), or at least SoftCore standing in for small text;
    // the pixels of it wanted on a page with text, at 150 %.
    private const int Core = 230, SoftCore = 192, MinimumText = 20;

    private static void Check(string name, Layer layer, byte[] glass, byte[] under, byte[] composed, int dw, int gx, int gy)
    {
        int area = 0, drawn = 0, through = 0, changed = 0;
        var ratios = new List<double>();
        List<double> soft = [], secondary = [], tertiary = [];
        for (int y = 0; y < layer.H; y++)
            for (int x = 0; x < layer.W; x++)
            {
                // Inside the island's shape: where the glass is (nearly) whole.
                if (glass[(y * layer.W + x) * 4 + 3] < 250) continue;
                area++;
                int l = ((layer.Y + y) * layer.Width + layer.X + x) * 4, d = ((gy + y) * dw + gx + x) * 4;
                int alpha = layer.Pixels[l + 3];
                if (alpha >= 128) drawn++;
                int difference = Math.Max(Math.Abs(composed[d] - under[d]), Math.Max(Math.Abs(composed[d + 1] - under[d + 1]), Math.Abs(composed[d + 2] - under[d + 2])));
                if (difference <= 8) through++;
                if (difference > 24) changed++;
                // Text: drawn (nearly) opaque in the theme's primary text color (titles, names, figures); the cover, the plates, the
                // scroll thumb and the bars are other colors. Small text at a low scale has few pixels that opaque (its strokes
                // are a pixel or two wide, their cores antialiased): the next most covered ones (Soft) stand in when there are
                // too few (see below).
                if (alpha >= SoftCore)
                {
                    int r = composed[d + 2], g = composed[d + 1], b = composed[d];
                    int lr = layer.Pixels[l + 2] * 255 / alpha, lg = layer.Pixels[l + 1] * 255 / alpha, lb = layer.Pixels[l] * 255 / alpha;
                    if (Math.Abs(lr - layer.Text.R) <= 24 && Math.Abs(lg - layer.Text.G) <= 24 && Math.Abs(lb - layer.Text.B) <= 24)
                    {
                        double ink = Luminance(r, g, b), ground = Luminance(under[d + 2], under[d + 1], under[d]);
                        (alpha >= Core ? ratios : soft).Add((Math.Max(ink, ground) + .05) / (Math.Min(ink, ground) + .05));
                    }
                }
                // Secondary and tertiary text: inside their runs, the glyphs' cores (drawn at the brush's own alpha, in its color).
                if (layer.Runs[(layer.Y + y) * layer.Width + layer.X + x] is var kind and > 0 && alpha > 0)
                {
                    var color = kind == 2 ? layer.Secondary : layer.Tertiary;
                    int lr = layer.Pixels[l + 2] * 255 / alpha, lg = layer.Pixels[l + 1] * 255 / alpha, lb = layer.Pixels[l] * 255 / alpha;
                    if (Math.Abs(alpha - color.A) <= Math.Max(10, color.A / 12) && Math.Abs(lr - color.R) <= 24 && Math.Abs(lg - color.G) <= 24 && Math.Abs(lb - color.B) <= 24)
                    {
                        double ink = Luminance(composed[d + 2], composed[d + 1], composed[d]), ground = Luminance(under[d + 2], under[d + 1], under[d]);
                        (kind == 2 ? secondary : tertiary).Add((Math.Max(ink, ground) + .05) / (Math.Min(ink, ground) + .05));
                    }
                }
            }
        double share = area == 0 ? 0 : drawn / (double)area, clear = area == 0 ? 0 : through / (double)area, moved = area == 0 ? 0 : changed / (double)area;
        Report(area > 0 && share >= .005 && moved >= .005, $"{name}: the page is drawn ({share:P1} of the island covered by content, {moved:P1} changed from the glass alone)");
        Report(clear >= .35, $"{name}: the glass shows through the page ({clear:P1} of the island is the glass unchanged; a page painted over, as a black page, has none)");
        // The pixels wanted scale with the text's area (20 at 150 %): the same text has about 9 core pixels at 100 % for every
        // 20 at 150 %. Where the cores alone are fewer (a page with little primary text: the empty AI usage page's names, at
        // 100 %, has 10), the next most covered pixels join them (pages-100.log of round 9 failed there for that alone).
        int needed = Math.Max(MinimumText / 3, (int)Math.Round(MinimumText * Scale * Scale / 2.25));
        string sample = "";
        if (ratios.Count < needed && ratios.Count + soft.Count >= needed)
        {
            sample = $" (with {soft.Count} pixels covered {SoftCore * 100 / 255}..{Core * 100 / 255 - 1} %: only {ratios.Count} were nearly opaque)";
            ratios.AddRange(soft);
        }
        ratios.Sort();
        if (ratios.Count < needed && !layer.HasText) Console.WriteLine($"NOTE: {name}: no primary text on it (the collapsed row shows the cover, the rings and the equalizer); its contrast is not checked");
        else if (ratios.Count < needed) Report(false, $"{name}: text contrast: only {ratios.Count} pixels of the theme's text color found ({soft.Count} more partly covered; wanted {needed} at {Scale * 100:0} %)");
        else
        {
            double median = ratios[ratios.Count / 2], low = ratios[ratios.Count / 10];
            Report(median >= 4.5 && low >= 3, $"{name}: primary text against the glass under it: median {median:0.0}:1, 10th percentile {low:0.0}:1 over {ratios.Count} pixels{sample} (wanted 4.5:1 and 3:1)");
        }
        Comfort(name, layer, glass, under, composed, dw, gx, gy);
        // Secondary text wants 4.5:1 as the primary does; tertiary (the clock, captions, hints) 3:1, its 10th percentile 2:1.
        Contrast(name, "secondary", secondary, 4.5, 3);
        Contrast(name, "tertiary", tertiary, 3, 2);
        // The shadow: below the island's bottom edge, outside it, between the bottom corners, the composed image is darker than
        // the glass alone over the background (round 14: an ambient and a contact shadow, by size and backdrop, MainWindow.Shadow).
        {
            double darker = 0, lightness = 0; int count = 0;
            int y0 = gy + layer.H + (int)Math.Round(2 * Scale), y1 = gy + layer.H + (int)Math.Round(7 * Scale);
            for (int y = y0; y < y1; y++)
                for (int x = gx + layer.W / 4; x < gx + layer.W * 3 / 4; x++)
                {
                    int d = (y * dw + x) * 4;
                    if (d + 2 >= composed.Length) continue;
                    darker += (Lightness(under, d) - Lightness(composed, d)); lightness += Lightness(under, d); count++;
                }
            double mean = count == 0 ? 0 : darker / count;
            // A black shadow darkens by a share of what is there: over a near-black page there is little to darken. Wanted: a
            // share of the background's own lightness there: .22 black at about a third of full cover 2 to 7 DIPs out, and at most 3.
            double wanted = Math.Min(3, (count == 0 ? 0 : lightness / count) * .22 * .3);
            Report(mean >= wanted && mean > 0, $"{name}: the shadow shows below the island (darkens by {mean:0.0} levels on average 2 to 7 DIPs under it, wanted {wanted:0.0}; shadow host shown: {layer.ShadowShown})");
        }
    }

    // Reading comfort against an opaque theme: in the text's boxes (every pixel the page draws text in, grown by 3 DIPs), the
    // detail of what lies behind (mean luma gradient of the glass alone) over the detail of the text itself (the gradient of
    // what the page adds). On an opaque surface what lies behind has none: (text + behind) / text is 1. Wanted: at most 1.2
    // (round 11, D15: as comfortable to read as the default theme).
    private static void Comfort(string name, Layer layer, byte[] glass, byte[] under, byte[] composed, int dw, int gx, int gy)
    {
        int w = layer.W, h = layer.H, grow = (int)Math.Round(3 * Scale);
        var text = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int l = ((layer.Y + y) * layer.Width + layer.X + x) * 4;
                int alpha = layer.Pixels[l + 3];
                if (alpha < 64) continue;
                int lr = layer.Pixels[l + 2] * 255 / alpha, lg = layer.Pixels[l + 1] * 255 / alpha, lb = layer.Pixels[l] * 255 / alpha;
                bool primary = Math.Abs(lr - layer.Text.R) <= 24 && Math.Abs(lg - layer.Text.G) <= 24 && Math.Abs(lb - layer.Text.B) <= 24;
                if (primary || layer.Runs[(layer.Y + y) * layer.Width + layer.X + x] > 0) text[y * w + x] = true;
            }
        var box = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (text[y * w + x])
                    for (int yy = Math.Max(0, y - grow); yy <= Math.Min(h - 1, y + grow); yy++)
                        for (int xx = Math.Max(0, x - grow); xx <= Math.Min(w - 1, x + grow); xx++) box[yy * w + xx] = true;
        double L(byte[] p, int x, int y) { int d = ((gy + y) * dw + gx + x) * 4; return Lightness(p, d); }
        double behind = 0, signal = 0; int count = 0;
        for (int y = 0; y < h - 1; y++)
            for (int x = 0; x < w - 1; x++)
            {
                if (!box[y * w + x] || glass[(y * w + x) * 4 + 3] < 250 || glass[((y + 1) * w + x + 1) * 4 + 3] < 250) continue;
                double u = L(under, x, y), ux = L(under, x + 1, y), uy = L(under, x, y + 1);
                double c = L(composed, x, y) - u, cx = L(composed, x + 1, y) - ux, cy = L(composed, x, y + 1) - uy;
                behind += Math.Abs(ux - u) + Math.Abs(uy - u);
                signal += Math.Abs(cx - c) + Math.Abs(cy - c);
                count++;
            }
        if (count < 50 || signal <= 0) { Console.WriteLine($"NOTE: {name}: no text boxes to weigh reading comfort in"); return; }
        double ratio = (signal + behind) / signal;
        Report(ratio <= 1.1, FormattableString.Invariant($"{name}: reading comfort: behind the text {behind / count:0.00} levels/px of detail against the text's own {signal / count:0.000}, (text + behind) / text {ratio:0.000} (an opaque theme 1.000; wanted at most 1.1, round 12)"));
        // Masked contrast (round 12, A3): APCA's Lc of the text against the mean behind it, over sqrt(1 + (sigma / Sigma0)^2),
        // sigma the band-pass RMS (1 to 4 DIPs) of what lies behind, in linear Y; against the same on the opaque theme (the
        // Acrylic fallback, #2C2C2C dark, #F9F9F9 light, where sigma is 0).
        var lum = new double[w * h];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++) { int d = ((gy + yy) * dw + gx + xx) * 4; lum[yy * w + xx] = ApcaY(under[d + 2], under[d + 1], under[d]); }
        double[] Blur(int r)
        {
            var sum = new double[(w + 1) * (h + 1)];
            for (int yy = 0; yy < h; yy++)
                for (int xx = 0; xx < w; xx++) sum[(yy + 1) * (w + 1) + xx + 1] = lum[yy * w + xx] + sum[yy * (w + 1) + xx + 1] + sum[(yy + 1) * (w + 1) + xx] - sum[yy * (w + 1) + xx];
            var o = new double[w * h];
            for (int yy = 0; yy < h; yy++)
                for (int xx = 0; xx < w; xx++)
                {
                    int x0 = Math.Max(0, xx - r), x1 = Math.Min(w, xx + r + 1), y0 = Math.Max(0, yy - r), y1 = Math.Min(h, yy + r + 1);
                    o[yy * w + xx] = (sum[y1 * (w + 1) + x1] - sum[y0 * (w + 1) + x1] - sum[y1 * (w + 1) + x0] + sum[y0 * (w + 1) + x0]) / ((x1 - x0) * (y1 - y0));
                }
            return o;
        }
        var fine = Blur(Math.Max(1, (int)Math.Round(Scale))); var coarse = Blur((int)Math.Round(4 * Scale));
        double mean = 0, band = 0; int n = 0;
        for (int i = 0; i < w * h; i++)
            if (box[i] && glass[i * 4 + 3] >= 250) { mean += lum[i]; double b = fine[i] - coarse[i]; band += b * b; n++; }
        if (n == 0) return;
        mean /= n; double sigma = Math.Sqrt(band / n);
        double yt = ApcaY(layer.Text.R, layer.Text.G, layer.Text.B);
        double opaque = Apca(yt, yt > .4 ? ApcaY(0x2C, 0x2C, 0x2C) : ApcaY(0xF9, 0xF9, 0xF9));
        double s0 = double.TryParse(Environment.GetEnvironmentVariable("LIQUID_SIGMA0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : LiquidRenderer.DefaultSigma0;
        double masked = Apca(yt, mean) / Math.Sqrt(1 + sigma / s0 * (sigma / s0));
        // Round 13b: the light look (dark text) wants 75 % of the opaque theme's and APCA's body text Lc 75; the dark look 90 %.
        double share = yt > .4 ? .9 : .75;
        Report(masked >= opaque * share && masked >= 75, FormattableString.Invariant($"{name}: masked contrast: Lc {masked:0.0} (sigma {sigma:0.0000} linear Y, Sigma0 {s0:0.000}) against the opaque theme's {opaque:0.0}: {masked / opaque:P0} (wanted at least {share:P0} and Lc 75)"));
    }

    // APCA's Y and |Lc| (its published formula, SAPC 0.0.98G; written from the formula alone), as Liquid.hlsl has them.
    private static double ApcaY(int r, int g, int b)
    {
        double y = .2126729 * Math.Pow(r / 255.0, 2.4) + .7151522 * Math.Pow(g / 255.0, 2.4) + .0721750 * Math.Pow(b / 255.0, 2.4);
        return y < .022 ? y + Math.Pow(.022 - y, 1.414) : y;
    }
    private static double Apca(double text, double back)
    {
        double lc = back > text ? (Math.Pow(back, .56) - Math.Pow(text, .57)) * 1.14 : (Math.Pow(back, .65) - Math.Pow(text, .62)) * 1.14;
        return Math.Abs(lc) < .1 ? 0 : (Math.Abs(lc) - .027) * 100;
    }

    private static void Contrast(string name, string which, List<double> ratios, double wanted, double least)
    {
        if (ratios.Count < 20) { Console.WriteLine($"NOTE: {name}: {ratios.Count} pixels of {which} text found; its contrast is not checked"); return; }
        ratios.Sort();
        double median = ratios[ratios.Count / 2], low = ratios[ratios.Count / 10];
        Report(median >= wanted && low >= least, $"{name}: {which} text against the glass under it: median {median:0.00}:1, 10th percentile {low:0.00}:1, lowest {ratios[0]:0.00}:1 over {ratios.Count} pixels (wanted {wanted}:1 and {least}:1)");
    }

    private static double Lightness(byte[] pixels, int at) => .2126 * pixels[at + 2] + .7152 * pixels[at + 1] + .0722 * pixels[at];

    private static double Luminance(int r, int g, int b)
    {
        static double Channel(int v) { double c = v / 255.0; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        return .2126 * Channel(r) + .7152 * Channel(g) + .0722 * Channel(b);
    }

    private static void Report(bool ok, string text)
    {
        if (!ok) _failures++;
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + text);
    }

    // ----- The sheet -----

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
            dc.DrawText(new FormattedText($"{name} — the real panel (MainWindow, offscreen) over LiquidRenderer's glass at {Scale * 100:0} %, with its shadow",
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
