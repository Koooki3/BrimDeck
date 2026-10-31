using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

// How steady the open panel's content is while the panel opens (W-09b): the real MainWindow offscreen with animations on, sampled
// as fast as the dispatcher allows while SetExpanded(true) runs. For every sample that differs from the one before it logs where
// the page (ContentCanvas) and the toolbar's first button lie in device pixels, the island's own box, ExpandedContent's scale and
// opacity. The summary counts how often the content's place changed by whole device pixels after it had first appeared, the
// largest distance from where it ends up, and the frames in which it moved against the island's centre.
//   dotnet run -c Release --project tests/BrimDeck.FxPreview -- --expand-jitter [--scale 150] [--theme dark] [--page usage|music|monitor] [--verbose]
internal static partial class LiquidPages
{
    internal static int RunExpandJitter(string[] args)
    {
        int at = Array.IndexOf(args, "--scale");
        Scale = at >= 0 && double.TryParse(args[at + 1].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) ? (percent > 4 ? percent / 100 : percent) : 1.5;
        at = Array.IndexOf(args, "--theme");
        string theme = at >= 0 ? args[at + 1] : PanelThemes.Dark;
        at = Array.IndexOf(args, "--page");
        string page = at >= 0 ? args[at + 1] : "usage";
        bool verbose = args.Contains("--verbose");
        var (app, deck, root, source) = Offscreen(animations: true);
        using var _ = source;
        UseLook(app, deck, root, theme);
        deck.SelectPage(page switch { "music" => DeckPage.Music, "monitor" => DeckPage.Monitor, _ => DeckPage.Usage }, remember: false);
        deck.Preview(true, expanded: false);
        Settle(root, deck);
        if (args.Contains("--snap")) return Snap(deck, root);
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 400) Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });
        Console.WriteLine($"scale {Scale * 100:0} %, theme {theme}, page {page}, animations {Soft.Enabled}");

        var rows = new List<Row>();
        var clock = Stopwatch.StartNew();
        Row? last = null;
        void Sample()
        {
            var island = deck.Island;
            var corner = deck.ContentCanvas.TransformToAncestor(root).Transform(new Point(0, 0));
            var topLeft = island.TranslatePoint(new Point(0, 0), root);
            var row = new Row(clock.Elapsed.TotalMilliseconds, corner.X * Scale, corner.Y * Scale, topLeft.X * Scale, island.ActualWidth * Scale, island.ActualHeight * Scale,
                ContentScaleNow(deck), deck.ExpandedContent.Opacity, deck.ExpandedContent.ActualWidth);
            if (last is null || !row.SameAs(last)) { rows.Add(row); last = row; }
        }
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, _) => Sample();
        deck.SetExpanded(true);
        timer.Start();
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
        stop.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();

        var end = rows[^1];
        if (verbose)
            foreach (var r in rows)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{r.T,7:0.0} ms  page x {r.PageX,8:0.00} y {r.PageY,7:0.00}  island x {r.IslandX,8:0.00} w {r.IslandW,8:0.00} h {r.IslandH,8:0.00}  centre {r.IslandX + r.IslandW / 2,8:0.00}  scale {r.Scale:0.0000}  opacity {r.Opacity:0.000}  content w {r.ContentW:0.##}"));
        // Whole-pixel steps of the page's place once it is visible (opacity > .02): how many frames moved it, and the distance from its final place.
        var shown = rows.Where(r => r.Opacity > .02).ToList();
        int xMoves = 0, reversals = 0; double worst = 0, lastDelta = 0;
        for (int i = 1; i < shown.Count; i++)
        {
            double d = shown[i].PageX - shown[i - 1].PageX;
            if (Math.Abs(d) > .01) { xMoves++; if (lastDelta * d < 0) reversals++; lastDelta = d; }
            worst = Math.Max(worst, Math.Abs(shown[i].PageX - end.PageX));
        }
        // The page against the island's centre: the offset the content has inside the island at each frame, relative to the end.
        double centreDrift = shown.Count == 0 ? 0 : shown.Max(r => Math.Abs((r.PageX + 380 * Scale) - (r.IslandX + r.IslandW / 2) - ((end.PageX + 380 * Scale) - (end.IslandX + end.IslandW / 2))));
        int scaled = shown.Count(r => Math.Abs(r.Scale - 1) > 1e-6);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"samples {rows.Count}, visible frames {shown.Count}, page-x moves {xMoves}, direction reversals {reversals}, worst distance from final x {worst:0.00} px, centre drift vs island {centreDrift:0.00} px, frames with ContentScale != 1: {scaled}, final scale {end.Scale:0.0000}, final opacity {end.Opacity:0.000}"));
        return 0;
    }

    // The open page drawn at rest (ContentScale 1), then with the scale a few frames before the end of the opening has it: how many
    // pixels of the page differ, i.e. what the last frames of the scale and the step back to the identity redraw.
    private static int Snap(MainWindow deck, Grid root)
    {
        deck.SetExpanded(true, instant: true);
        Rescale(root);
        Settle(root, deck);
        byte[] Draw()
        {
            root.UpdateLayout();
            int w = (int)Math.Ceiling(root.ActualWidth * Scale), h = (int)Math.Ceiling(root.ActualHeight * Scale);
            var bitmap = new RenderTargetBitmap(w, h, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            bitmap.Render(deck.Stage);
            var pixels = new byte[w * h * 4]; bitmap.CopyPixels(pixels, w * 4, 0);
            return pixels;
        }
        // Before the fix the page carried a ScaleTransform (ContentScale); without one a stand-in is put on it, to measure the same steps.
        var stepped = deck.ExpandedContent.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        deck.ExpandedContent.RenderTransformOrigin = new Point(.5, 0);
        deck.ExpandedContent.RenderTransform = stepped;
        var rest = Draw();
        Console.WriteLine($"rest picture: {rest.Length / 4} pixels, {Enumerable.Range(0, rest.Length / 4).Count(i => rest[i * 4 + 3] > 0)} drawn, island {deck.Island.ActualWidth}x{deck.Island.ActualHeight}, page visibility {deck.ExpandedContent.Visibility}, scale now {1d}");
        Console.WriteLine($"ClearType hint on content: {System.Windows.Media.RenderOptions.GetClearTypeHint(deck.IslandContent)}, page opacity {deck.ExpandedContent.Opacity}");
        foreach (double scale in new[] { 1.0, .9999, .9992, .998, .99, .96 })
        {
            stepped.ScaleX = scale; stepped.ScaleY = scale;
            var frame = Draw();
            int differing = 0;
            for (int i = 0; i < rest.Length; i += 4)
                if (Math.Abs(rest[i] - frame[i]) > 24 || Math.Abs(rest[i + 1] - frame[i + 1]) > 24 || Math.Abs(rest[i + 2] - frame[i + 2]) > 24) differing++;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ContentScale {scale:0.0000}: {differing} pixels differ from the rest picture"));
        }
        stepped.ScaleX = 1; stepped.ScaleY = 1;
        // The page's own opacity: below 1 it is drawn as a group (text in grayscale), at 1 directly.
        foreach (double opacity in new[] { .9999, .999, .99, .9 })
        {
            deck.ExpandedContent.Opacity = opacity;
            var frame = Draw();
            int differing = 0;
            for (int i = 0; i < rest.Length; i += 4)
                if (Math.Abs(rest[i] - frame[i]) > 24 || Math.Abs(rest[i + 1] - frame[i + 1]) > 24 || Math.Abs(rest[i + 2] - frame[i + 2]) > 24) differing++;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ExpandedContent opacity {opacity:0.0000}: {differing} pixels differ from the rest picture"));
        }
        deck.ExpandedContent.Opacity = 1;
        return 0;
    }

    private static double ContentScaleNow(MainWindow deck) => deck.ExpandedContent.RenderTransform.Value.M11;

    private sealed record Row(double T, double PageX, double PageY, double IslandX, double IslandW, double IslandH, double Scale, double Opacity, double ContentW)
    {
        public bool SameAs(Row o) => PageX == o.PageX && PageY == o.PageY && IslandX == o.IslandX && IslandW == o.IslandW && IslandH == o.IslandH && Scale == o.Scale && Opacity == o.Opacity;
    }
}
