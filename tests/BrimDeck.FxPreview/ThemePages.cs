using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;

// The real panel's pages in every built-in theme and look, offscreen, over a plain backdrop (no glass composition): one sheet per
// look with the collapsed capsule, the music page with a cover and without, the system monitor, the recent notices and the AI
// usage page, for before-and-after comparisons of layout and type.
//   dotnet run -c Release --no-build --project tests/BrimDeck.FxPreview -- --theme-pages <output folder> [--scale 150] [--text-scale 125] [--themes dark,light]
// The sheets are named pages-<theme>[-night|-dusk]-<percent>[-text<percent>].png; the text size is Windows' (TextScale).
internal static partial class LiquidPages
{
    public static int RunThemes(string folder, double scale, HashSet<string>? only, double textScale = 1)
    {
        Scale = scale;
        TextScale.Override = textScale;
        string text = textScale == 1 ? "" : $"-text{textScale * 100:0}";
        Directory.CreateDirectory(folder);
        var (app, deck, root, source) = Offscreen(animations: false);
        using var _ = source;
        var cover = Cover(); var track = Track(); var snapshot = Snapshot();
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
                for (int row = 0; row < Pages.Length; row++)
                {
                    var (page, label) = Pages[row];
                    Show(deck, root, page, track, cover, snapshot);
                    tiles.Add((label, Tile(deck, root, palette.IsDark), 0, row));
                }
                string name = theme.Id + (look == DaylightLook.Day ? "" : "-" + look.ToString().ToLowerInvariant());
                string title = $"{name} at {Scale * 100:0} %" + (textScale == 1 ? "" : $", text {textScale * 100:0} %");
                LiquidPreview.Save(Sheet(title, tiles), Path.Combine(folder, $"pages-{name}-{Scale * 100:0}{text}.png"));
                Console.WriteLine($"Wrote pages-{name}-{Scale * 100:0}{text}.png");
            }
        return 0;
    }

    // What drawing each theme costs, per page: the stage of the real panel drawn into a bitmap `rounds` times, as the island's own
    // layer is drawn on every frame it changes, in CPU milliseconds (the process's processor time) per draw, and the managed
    // memory a theme's pass leaves. A relative picture of the material (gradient, pattern, outline, motifs): the live idle and
    // playing numbers are measured on the real app (pr-plan/themes-endfield/live).
    //   dotnet run -c Release --no-build --project tests/BrimDeck.FxPreview -- --theme-cost [--scale 150] [--rounds 120] [--themes id,id,...]
    public static int RunCost(double scale, HashSet<string>? only, int rounds)
    {
        Scale = scale;
        TextScale.Override = 1;
        var (app, deck, root, source) = Offscreen(animations: false);
        using var _ = source;
        var cover = Cover(); var track = Track(); var snapshot = Snapshot();
        var process = System.Diagnostics.Process.GetCurrentProcess();
        Console.WriteLine($"{"theme / look",-40}{"collapsed",10}{"music",10}{"monitor",10}{"usage",10}   ms of CPU per draw, {rounds} draws, {Scale * 100:0} %");
        foreach (var theme in PanelThemes.BuiltIns.Where(theme => only is null || only.Contains(theme.Id)))
            foreach (var look in new[] { DaylightLook.Day, DaylightLook.Night }.Where(look => look == DaylightLook.Day || theme.File.Night is not null))
            {
                var palette = PanelThemes.Resolve(theme, look);
                PanelTheme.Show(palette);
                var again = new ResourceDictionary();
                again.MergedDictionaries.Add(deck.Resources);
                again.MergedDictionaries.Add(app.Resources.MergedDictionaries.First(dictionary => dictionary.Contains("PanelSurface")));
                root.Resources = again;
                deck.ApplySettings();
                var costs = new List<double>();
                foreach (var page in new[] { Page.Capsule, Page.MusicCover, Page.Monitor, Page.Usage })
                {
                    Show(deck, root, page, track, cover, snapshot);
                    for (int i = 0; i < 8; i++) Tile(deck, root, palette.IsDark);
                    var samples = new List<double>();
                    for (int pass = 0; pass < 3; pass++)
                    {
                        process.Refresh();
                        var before = process.TotalProcessorTime;
                        for (int i = 0; i < rounds; i++) Tile(deck, root, palette.IsDark);
                        process.Refresh();
                        samples.Add((process.TotalProcessorTime - before).TotalMilliseconds / rounds);
                    }
                    costs.Add(samples.Order().ElementAt(1));
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Console.WriteLine($"{theme.Id + (look == DaylightLook.Night ? " (night)" : ""),-40}{costs[0],10:0.00}{costs[1],10:0.00}{costs[2],10:0.00}{costs[3],10:0.00}   managed {GC.GetTotalMemory(true) / 1048576.0:0.0} MB");
            }
        return 0;
    }

    // The island's part of the stage over a plain backdrop that suits the look (a light desktop under a light theme).
    private static BitmapSource Tile(MainWindow deck, System.Windows.Controls.Grid root, bool dark)
    {
        var island = deck.Island;
        var at = island.TranslatePoint(new Point(), root);
        int width = (int)Math.Ceiling(root.ActualWidth * Scale), height = (int)Math.Ceiling(root.ActualHeight * Scale);
        var stage = new RenderTargetBitmap(width, height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        stage.Render(deck.Stage);
        double pad = 16;
        var area = new Rect(Math.Max(0, at.X - pad), 0, Math.Min(root.ActualWidth - Math.Max(0, at.X - pad), island.ActualWidth + 2 * pad), Math.Min(root.ActualHeight, at.Y + island.ActualHeight + pad));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x2B, 0x2D, 0x33) : Color.FromRgb(0xD8, 0xDB, 0xE0)), null, new Rect(0, 0, area.Width, area.Height));
            dc.DrawImage(stage, new Rect(-area.X, -area.Y, width / Scale, height / Scale));
        }
        var tile = new RenderTargetBitmap((int)Math.Ceiling(area.Width * Scale), (int)Math.Ceiling(area.Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        tile.Render(visual);
        tile.Freeze();
        return tile;
    }
}
