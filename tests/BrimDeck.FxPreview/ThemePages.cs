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
