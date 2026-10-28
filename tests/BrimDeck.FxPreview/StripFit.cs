using System.IO;
using System.Windows;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;

// The collapsed capsule's system readings in the real row (cover, lyric, readings, rhythm bars, clock), offscreen at one display
// scale: whether each reading is drawn whole, which is where the row is wider than its region. Prints the strip's region, what it
// asks for (PreferredWidth) and the width of each reading; with a folder, saves the row. Not a test: numbers and a picture.
//   dotnet run -c Release --no-build --project tests/BrimDeck.FxPreview -- --strip-fit [<output folder>] [--scale 150] [--fixed <capsule width in DIPs>]
internal static partial class LiquidPages
{
    private static T? Find<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T found) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find<T>(VisualTreeHelper.GetChild(node, i)) is { } inner) return inner;
        return default;
    }

    public static int RunStripFit(string? folder, double scale, double? fixedWidth)
    {
        Scale = scale;
        var (app, deck, root, source) = Offscreen(animations: false);
        using var _ = source;
        var settings = app.Settings;
        settings.CapsuleClock = true; settings.CapsuleMonitor = true; settings.CapsuleMusic = true; settings.CapsuleSummary = false;
        settings.MusicText = CompactMusicText.Lyrics;
        if (fixedWidth is { } width) { settings.CapsuleAutoWidth = false; settings.CapsuleWidth = width; }
        foreach (var reading in settings.CompactReadings)
            reading.Visible = reading.Item is CompactMonitorItems.Columns or CompactMonitorItems.Cpu or CompactMonitorItems.Memory or CompactMonitorItems.Gpu
                or CompactMonitorItems.Download or CompactMonitorItems.Upload or CompactMonitorItems.Storage or CompactMonitorItems.Battery;
        deck.ApplySettings();
        var cover = Cover(); var track = Track() with { State = BrimDeck.Core.MediaState.Playing };
        var snapshot = Snapshot() with
        {
            Storage = [new StorageDrive(0, "SSD", [new StorageVolume("C:", "", 100UL << 30, 16UL << 30)])]
        };
        deck.OffscreenLowest = new BatteryReading(97, false, MemoryLevel.Normal, "MCHOSE A7", DeviceKind.Mouse);
        var palette = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.GlassDark)!);
        PanelTheme.Show(palette);
        Show(deck, root, Page.Capsule, track, cover, snapshot);
        // The readings that join with their first figure are admitted; the row is built again with them.
        for (int i = 0; i < 3; i++) { deck.OffscreenMonitor(snapshot); Settle(root, deck); }
        var strip = Find<MonitorStrip>(deck.Stage);
        Console.WriteLine($"display scale {Scale * 100:0} %, island {deck.Island.ActualWidth:0.##} x {deck.Island.ActualHeight:0.##}");
        if (strip is null) { Console.WriteLine("FAIL: no readings row"); return 1; }
        var widths = strip.ItemWidths();
        Console.WriteLine($"strip: region {strip.ActualWidth:0.##}, asks {strip.PreferredWidth:0.##}, gap {strip.Gap:0.##}, items {string.Join(" ", widths.Select(w => w.ToString("0.##")))}, carousel {(Find<ReadingsCarousel>(deck.Stage) is { Rotates: true } ? "scrolling" : "still")}");
        if (Find<ReadingsCarousel>(deck.Stage) is { } carousel) Console.WriteLine($"carousel: width {carousel.Width:0.##}, travel {carousel.Travel:0.##}, margin {carousel.Margin.Left:0.##}");
        bool still = fixedWidth is not null || Find<ReadingsCarousel>(deck.Stage) is not { Rotates: true };
        bool fits = strip.ActualWidth + .01 >= strip.PreferredWidth && still;
        Console.WriteLine(fits ? "PASS: every reading has its room" : $"FAIL: the readings ask {strip.PreferredWidth:0.##}, the region is {strip.ActualWidth:0.##}{(still ? "" : ", and they scroll in an auto-width row")}");
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
            LiquidPreview.Save(Tile(deck, root, palette.IsDark), Path.Combine(folder, $"strip-fit-{Scale * 100:0}{(fixedWidth is null ? "" : "-fixed")}.png"));
        }
        return fits ? 0 : 1;
    }
}
