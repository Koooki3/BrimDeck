using System.Text.Json;
using BrimDeck.Core;

// The collapsed readings' table: migration from the old modes, presets, order, drop priority, widths and copies.
internal static class CompactReadingTests
{
    private const CompactMonitorItems Cpu = CompactMonitorItems.Cpu, Memory = CompactMonitorItems.Memory, Gpu = CompactMonitorItems.Gpu,
        Columns = CompactMonitorItems.Columns, Download = CompactMonitorItems.Download, Upload = CompactMonitorItems.Upload, All = CompactMonitorItems.All;

    internal static void Run(Action<string, bool> check)
    {
        Migration(check);
        Presets(check);
        Highlight(check);
        Layout(check);
        LevelColors(check);
        ReplacedChildren(check);
        Copies(check);
        PartOrder(check);
    }

    private static DeckSettings Read(string json) => SettingsMigrations.Read(json);
    private static bool Same(IEnumerable<CompactReading> readings, params CompactMonitorItems[] order) => readings.Select(r => r.Item).SequenceEqual(order);

    private static void Migration(Action<string, bool> check)
    {
        var full = Read("""{"AppVersion":"0.1.0","CompactMonitor":0,"CompactMonitorItems":3}""");
        check("Compact readings: old Full shows everything with figures in the default order",
            Same(full.CompactReadings, [.. CompactMonitor.Order]) && full.CompactReadings.All(r => r.Visible && r.ShowValue && r.Color is null)
            && full.CompactMonitor == CompactMonitorMode.Full && full.CompactMonitorItems == All);
        var brief = Read("""{"AppVersion":"0.1.0","CompactMonitor":1,"CompactMonitorItems":63}""");
        check("Compact readings: old Brief shows the three rings without figures",
            CompactMonitor.Visible(brief.CompactReadings) == CompactMonitor.Rings && CompactMonitor.Values(brief.CompactReadings) == CompactMonitorItems.None
            && brief.CompactMonitor == CompactMonitorMode.Brief && brief.CompactMonitorItems == CompactMonitor.Rings);
        var custom = Read("""{"AppVersion":"0.1.0","CompactMonitor":2,"CompactMonitorItems":62}""");
        check("Compact readings: old Custom keeps its readings with figures",
            CompactMonitor.Visible(custom.CompactReadings) == (All & ~Columns) && CompactMonitor.Values(custom.CompactReadings) == CompactMonitor.Rings
            && custom.CompactMonitor == CompactMonitorMode.Custom && custom.CompactMonitorItems == (All & ~Columns) && CompactMonitor.Preset(custom.CompactReadings) is null);
        var empty = Read("""{"AppVersion":"0.1.0","CompactMonitor":2,"CompactMonitorItems":0}""");
        check("Compact readings: old Custom with nothing chosen shows CPU", CompactMonitor.Visible(empty.CompactReadings) == Cpu && empty.CompactMonitorItems == Cpu);
        var unknownMode = Read("""{"AppVersion":"0.1.0","CompactMonitor":9}""");
        check("Compact readings: an unknown old mode reads as Full", CompactMonitor.Preset(unknownMode.CompactReadings) == CompactMonitorMode.Full);
        var defaults = new DeckSettings(); defaults.Normalize();
        check("Compact readings: defaults are Full", defaults.CompactMonitor == CompactMonitorMode.Full && CompactMonitor.Preset(defaults.CompactReadings) == CompactMonitorMode.Full
            && Same(defaults.CompactReadings, [.. CompactMonitor.Order]));

        var odd = Read("""{"AppVersion":"0.1.0","CompactMonitor":0,"CompactReadings":[{"Item":64,"Visible":true},null,{"Item":16,"Visible":true,"ShowValue":false,"Color":"#12ab3c"},{"Item":2,"Visible":false,"Color":"red"},{"Item":16,"Visible":false}]}""");
        check("Compact readings: unknown and repeated readings are dropped, missing ones join hidden",
            Same(odd.CompactReadings, Download, Cpu, Columns, Memory, Gpu, Upload) && odd.CompactReadings[0].Visible && !odd.CompactReadings[1].Visible
            && odd.CompactReadings.Skip(2).All(r => !r.Visible) && odd.CompactMonitorItems == Download && odd.CompactMonitor == CompactMonitorMode.Custom);
        check("Compact readings: colors are normalized or follow the palette", odd.CompactReadings[0].Color == "#12AB3C" || odd.CompactReadings[0].Color == "#12ab3c");
        check("Compact readings: an invalid color follows the palette", odd.CompactReadings[1].Color is null);
        var none = Read("""{"AppVersion":"0.1.0","CompactReadings":[{"Item":2,"Visible":false}]}""");
        check("Compact readings: with every reading off the strip shows CPU", none.CompactMonitorItems == Cpu && CompactMonitor.Visible(none.CompactReadings) == CompactMonitorItems.None);
        var broken = Read("""{"AppVersion":"0.1.0","CompactReadings":null}""");
        check("Compact readings: a missing table reads as the defaults", Same(broken.CompactReadings, [.. CompactMonitor.Order]));
    }

    private static void Presets(Action<string, bool> check)
    {
        var readings = CompactMonitor.Normalize([
            new() { Item = Upload, Color = "#112233" }, new() { Item = Gpu }, new() { Item = Columns, Visible = false },
            new() { Item = Cpu, ShowValue = false }, new() { Item = Download }, new() { Item = Memory, Color = "#445566" }]);
        var order = readings.Select(r => r.Item).ToArray();
        check("Compact readings: a reordered, partly chosen table matches no preset", CompactMonitor.Preset(readings) is null);
        CompactMonitor.Apply(readings, CompactMonitorMode.Brief);
        check("Compact readings: Brief keeps the order and the colors", readings.Select(r => r.Item).SequenceEqual(order)
            && readings[0].Color == "#112233" && readings[5].Color == "#445566");
        check("Compact readings: Brief shows the rings without figures", CompactMonitor.Preset(readings) == CompactMonitorMode.Brief
            && CompactMonitor.Visible(readings) == CompactMonitor.Rings && CompactMonitor.Values(readings) == CompactMonitorItems.None);
        CompactMonitor.Apply(readings, CompactMonitorMode.Full);
        check("Compact readings: Full keeps the order and the colors", readings.Select(r => r.Item).SequenceEqual(order) && readings[0].Color == "#112233");
        check("Compact readings: Full shows everything with figures", CompactMonitor.Preset(readings) == CompactMonitorMode.Full && CompactMonitor.Visible(readings) == All);
        var before = readings.Select(r => (r.Item, r.Visible, r.ShowValue, r.Color)).ToArray();
        CompactMonitor.Apply(readings, CompactMonitorMode.Custom);
        check("Compact readings: no preset (Custom) changes nothing", readings.Select(r => (r.Item, r.Visible, r.ShowValue, r.Color)).SequenceEqual(before));
        readings.First(r => r.Item == Gpu).ShowValue = false;
        check("Compact readings: a figure turned off matches no preset", CompactMonitor.Preset(readings) is null);
        CompactMonitor.Apply(readings, CompactMonitorMode.Brief);
        readings.First(r => r.Item == Download).Visible = true;
        check("Compact readings: a reading added to Brief matches no preset", CompactMonitor.Preset(readings) is null);
        readings.First(r => r.Item == Download).Visible = false;
        readings.First(r => r.Item == Columns).ShowValue = true;
        check("Compact readings: the hidden readings' figures do not change the preset", CompactMonitor.Preset(readings) == CompactMonitorMode.Brief);
        check("Compact readings: only the rings have a figure choice",
            CompactMonitor.HasValueChoice(Cpu) && CompactMonitor.HasValueChoice(Memory) && CompactMonitor.HasValueChoice(Gpu)
            && !CompactMonitor.HasValueChoice(Columns) && !CompactMonitor.HasValueChoice(Download) && !CompactMonitor.HasValueChoice(Upload));
    }

    private static void Layout(Action<string, bool> check)
    {
        CompactMonitorItems[] order = [Upload, Gpu, Cpu, Columns, Download, Memory];
        var width = CompactMonitor.Widths(CompactMonitor.Rings, 12, 30, 14, 20, 50);
        var arranged = CompactMonitor.Arrange(order, All & ~Download, width, 10, _ => 1);
        check("Compact readings: the strip follows the table's order", arranged.Items.Select(i => i.Item).SequenceEqual([Upload, Gpu, Cpu, Columns, Memory]));
        check("Compact readings: each reading starts after the one before it", arranged.Items[0].X == 0 && arranged.Items[1].X == 60 && arranged.Items[2].X == 112
            && arranged.Items[3].X == 164 && arranged.Items[4].X == 194 && arranged.Total == 236);
        var half = CompactMonitor.Arrange(order, All, width, 10, item => item == Gpu ? .5 : 1);
        check("Compact readings: a leaving reading gives back its room in proportion", half.Items[2].X == 60 + 26);

        foreach (var space in new[] { 300.0, 200, 150, 100, 60, 40, 20 })
        {
            var fit = CompactMonitor.Fit(All, width, 10, space);
            check($"Compact readings: drop order at {space} is the fixed one", fit == Expected(space));
        }
        // Fit takes no order, so the table's order cannot change which readings leave; the arranged width matches Width.
        var fitted = CompactMonitor.Fit(All, width, 10, 200);
        check("Compact readings: the arranged width of what fits is its measured width in any order",
            Math.Abs(CompactMonitor.Arrange(order, fitted, width, 10, _ => 1).Total - CompactMonitor.Width(fitted, width, 10)) < 1e-9
            && Math.Abs(CompactMonitor.Arrange(CompactMonitor.Order, fitted, width, 10, _ => 1).Total - CompactMonitor.Width(fitted, width, 10)) < 1e-9);

        var bare = CompactMonitor.Widths(CompactMonitorItems.None, 12, 30, 14, 20, 50);
        var some = CompactMonitor.Widths(Cpu, 12, 30, 14, 20, 50);
        check("Compact readings: a ring without its figure is narrower", bare(Memory) == 14 && width(Memory) == 42 && some(Cpu) == 42 && some(Gpu) == 14
            && bare(Download) == 50 && bare(Columns) == 20);
        check("Compact readings: hiding figures shrinks the strip",
            CompactMonitor.Width(All, bare, 10) < CompactMonitor.Width(All, some, 10) && CompactMonitor.Width(All, some, 10) < CompactMonitor.Width(All, width, 10));
        check("Compact readings: hiding figures lets more readings fit", CompactMonitor.Fit(All, bare, 10, 200) == (All & ~Upload) && CompactMonitor.Fit(All, width, 10, 200) == (Cpu | Memory | Columns | Download));
        check("Compact readings: rings alone sit closer only without figures",
            CompactMonitor.RingsOnly(CompactMonitor.Rings, CompactMonitorItems.None) && !CompactMonitor.RingsOnly(CompactMonitor.Rings, Cpu) && !CompactMonitor.RingsOnly(All, CompactMonitorItems.None));

        var palette = MonitorColors.For(MonitorPalette.Sea);
        var colors = new StripColors(palette, [new() { Item = Memory, Color = "#123456" }, new() { Item = Cpu, Color = "#654321" }]);
        check("Compact readings: an own color replaces only the normal state", colors.Memory(MemoryLevel.Normal) == "#123456"
            && colors.Memory(MemoryLevel.Elevated) == palette.Warning && colors.Memory(MemoryLevel.Critical) == palette.Critical);
        check("Compact readings: without an own color a reading follows the palette", colors.Normal(Gpu) == palette.Gpu && colors.Normal(Cpu) == "#654321"
            && new StripColors(palette).Normal(Memory) == palette.Efficiency && colors.Core(1, 2) == palette.Core(1, 2));

        // Upload, GPU, download, columns and memory leave in turn; widths 50, 42, 50, 20, 42 and CPU 42, gaps 10.
        static CompactMonitorItems Expected(double space) => space switch
        {
            >= 296 => All, >= 236 => All & ~Upload, >= 184 => Cpu | Memory | Columns | Download, >= 124 => Cpu | Memory | Columns,
            >= 94 => Cpu | Memory, >= 42 => Cpu, _ => CompactMonitorItems.None
        };
    }

    // Every reading has a color per level (ReadingLevels): its own where set, else the color scheme's; the core columns have
    // two normal colors, one per kind of core.
    private static void LevelColors(Action<string, bool> check)
    {
        const MemoryLevel Normal = MemoryLevel.Normal, Elevated = MemoryLevel.Elevated, Critical = MemoryLevel.Critical;
        var palette = MonitorColors.For(MonitorPalette.Sea);
        var own = new StripColors(palette, [new() { Item = Memory, Color = "#123456", WarningColor = "#ABCDEF", CriticalColor = "#FEDCBA" },
            new() { Item = Cpu, Color = "#654321", WarningColor = "#111111" }, new() { Item = Download, CriticalColor = "#222222" }]);
        check("Level colors: a reading takes its own color at each level", own.Memory(Normal) == "#123456" && own.Memory(Elevated) == "#ABCDEF" && own.Memory(Critical) == "#FEDCBA");
        check("Level colors: every reading has levels, an unset one follows the palette", own.At(Cpu, Elevated) == "#111111" && own.At(Cpu, Critical) == palette.Critical
            && own.At(Download, Critical) == "#222222" && own.At(Download, Elevated) == palette.Warning && own.At(Gpu, Elevated) == palette.Warning && own.At(Upload, Normal) == palette.Efficiency);
        var none = new StripColors(palette);
        check("Level colors: without own colors every reading follows the palette at each level", CompactMonitor.Order.All(item => none.At(item, Elevated) == palette.Warning
            && none.At(item, Critical) == palette.Critical && none.At(item, Normal) == none.Default(item)) && none.Memory(Normal) == palette.Efficiency);

        // Each scheme's three main colors stand for CPU, memory and GPU; the core columns and the rates take the first two.
        var schemes = Enum.GetValues<MonitorPalette>().SelectMany(scheme => new[] { MonitorColors.For(scheme, true), MonitorColors.For(scheme, false) })
            .Append(MonitorColors.FromTheme("#112233", ["#445566", "#778899", "#AABBCC"], "#00FF00", "#FFFF00", "#FF0000")).ToList();
        check("Level colors: the defaults per scheme and item", schemes.All(scheme =>
        {
            var defaults = new StripColors(scheme);
            return defaults.Default(Cpu) == scheme.Performance && defaults.Default(Memory) == scheme.Efficiency && defaults.Default(Gpu) == scheme.Gpu
                && defaults.Default(Columns) == scheme.Performance && defaults.Default(Columns, 1) == scheme.Efficiency
                && defaults.Default(Download) == scheme.Performance && defaults.Default(Upload) == scheme.Efficiency
                && CompactMonitor.Order.All(item => defaults.DefaultAt(item, Elevated) == scheme.Warning && defaults.DefaultAt(item, Critical) == scheme.Critical);
        }));
        check("Level colors: own colors are drawn as the caller draws them",
            new StripColors(palette, [new() { Item = Memory, CriticalColor = "#FEDCBA" }], _ => "#000000").Memory(Critical) == "#000000");
        var warm = new StripColors(MonitorColors.For(MonitorPalette.Warm));
        check("Level colors: the defaults come from the color scheme", warm.Default(Cpu) == MonitorColors.For(MonitorPalette.Warm).Performance && warm.Default(Cpu) != none.Default(Cpu)
            && warm.Default(Columns, 1) == MonitorColors.For(MonitorPalette.Warm).Efficiency && new StripColors(MonitorColors.For(MonitorPalette.Sea, false)).At(Memory, Elevated) == MonitorColors.For(MonitorPalette.Sea, false).Warning);

        var cores = new StripColors(palette, [new() { Item = Columns, Color = "#FF0000", ExtraColors = ["#0000FF"] }]);
        check("Level colors: the core columns run from their first color to their second", cores.Core(0, 3) == ColorMath.Mix("#FF0000", "#0000FF", 0)
            && cores.Core(2, 3) == ColorMath.Mix("#FF0000", "#0000FF", 1) && cores.Normal(Columns, 1) == "#0000FF" && cores.Core(0, 1) == ColorMath.Mix("#FF0000", "#0000FF", 0));
        check("Level colors: a busy core takes the level's color", cores.Core(1, 3, Elevated) == palette.Warning && cores.Core(0, 3, Critical) == palette.Critical);
        check("Level colors: with only the first core color the others run part of the way to the palette's",
            new StripColors(palette, [new() { Item = Columns, Color = "#FF0000" }]).Core(1, 2) == ColorMath.Mix("#FF0000", palette.Efficiency, .45));
        check("Level colors: without own core colors the palette's run", none.Core(1, 3) == palette.Core(1, 3) && none.Default(Columns, 1) == palette.Efficiency
            && none.Default(Columns) == palette.Performance && none.DefaultAt(Columns, Elevated, 1) == palette.Warning);
        check("Level colors: only the core columns have two channels", CompactMonitor.Channels(Columns) == 2 && CompactMonitor.Order.Where(item => item != Columns).All(item => CompactMonitor.Channels(item) == 1));

        check("Level thresholds: shares", ReadingLevels.OfShare(.79) == Normal && ReadingLevels.OfShare(.8) == Elevated && ReadingLevels.OfShare(.919) == Elevated
            && ReadingLevels.OfShare(.92) == Critical && ReadingLevels.OfShare(1) == Critical);
        check("Level thresholds: rates", ReadingLevels.OfRate(9_999_999) == Normal && ReadingLevels.OfRate(10_000_000) == Elevated
            && ReadingLevels.OfRate(49_999_999) == Elevated && ReadingLevels.OfRate(50_000_000) == Critical);
        check("Level thresholds: memory in use matches the other shares", new MemorySample(100, 80, 0, 100).Level == Elevated && new MemorySample(100, 92, 0, 100).Level == Critical
            && new MemorySample(100, 10, 85, 100).Level == Elevated && new MemorySample(100, 10, 90, 100).Level == Critical && new MemorySample(100, 79, 79, 100).Level == Normal);
        var core = new CoreSample(0, 0, CoreTier.Performance, 1, .95, 2000d, 4000);
        var snapshot = new MonitorSnapshot(DateTimeOffset.Now, 2, new CpuSample([core, core with { Id = 1, Usage = .1 }], 3000d), new MemorySample(100, 85, 10, 100),
            new NetworkSample(60_000_000, 1_000), new GpuSample(.5, 0UL), Reading<ProcessList>.Unavailable("test"));
        check("Level of each reading from a snapshot", ReadingLevels.Of(Cpu, snapshot) == Normal && ReadingLevels.Of(Columns, snapshot) == Critical
            && ReadingLevels.Of(Memory, snapshot) == Elevated && ReadingLevels.Of(Gpu, snapshot) == Normal && ReadingLevels.Of(Download, snapshot) == Critical
            && ReadingLevels.Of(Upload, snapshot) == Normal && ReadingLevels.Of(Cpu, null) == Normal && ReadingLevels.Of(Memory, MonitorSnapshot.Empty(2, "test")) == Normal);

        var reading = new CompactReading { Item = Columns };
        reading.SetColor(Elevated, "#111111"); reading.SetColor(Critical, "#222222"); reading.SetColor(Normal, "#333333"); reading.SetColor(Normal, "#444444", 1);
        check("Level colors: each level and channel sets its own color", reading.WarningColor == "#111111" && reading.CriticalColor == "#222222" && reading.Color == "#333333"
            && reading.ExtraColors is ["#444444"] && reading.ColorAt(Normal, 1) == "#444444" && reading.ColorAt(Elevated, 1) == "#111111");
        reading.SetColor(Normal, null, 1);
        check("Level colors: clearing the last further channel leaves none", reading.ExtraColors is null && reading.Color == "#333333");

        var read = Read("""{"AppVersion":"0.1.0","CompactReadings":[{"Item":4,"Color":"#7fd1c4","WarningColor":"#aabbcc","CriticalColor":"nope"},{"Item":2,"WarningColor":"#112233","CriticalColor":"#445566","ExtraColors":["#000000"]},{"Item":1,"ExtraColors":["#0000ff","#00ff00"]}]}""");
        var memory = read.CompactReadings.First(r => r.Item == Memory); var cpu = read.CompactReadings.First(r => r.Item == Cpu); var columns = read.CompactReadings.First(r => r.Item == Columns);
        check("Level colors: normalized, an invalid one follows the palette, every reading keeps its level colors",
            memory.Color == "#7FD1C4" && memory.WarningColor == "#AABBCC" && memory.CriticalColor is null && cpu.WarningColor == "#112233" && cpu.CriticalColor == "#445566");
        check("Level colors: only the channels a reading has are kept", cpu.ExtraColors is null && columns.ExtraColors is ["#0000FF"]);
        var older = Read("""{"AppVersion":"0.1.0","CompactReadings":[{"Item":4,"Color":"#7FD1C4"}]}""").CompactReadings.First(r => r.Item == Memory);
        check("Level colors: a file without them follows the palette", older.Color == "#7FD1C4" && older.WarningColor is null && older.CriticalColor is null && older.ExtraColors is null);

        var settings = new DeckSettings { CompactReadings = CompactMonitor.Normalize([new() { Item = Memory, WarningColor = "#ABCDEF", CriticalColor = "#FEDCBA" },
            new() { Item = Columns, Color = "#123456", ExtraColors = ["#654321"], Visible = false }, new() { Item = Upload, ShowValue = false, WarningColor = "#999999" }]) };
        settings.Normalize();
        CompactMonitor.Apply(settings.CompactReadings, CompactMonitorMode.Brief); CompactMonitor.Apply(settings.CompactReadings, CompactMonitorMode.Full);
        var copy = settings.Copy(); copy.CompactReadings.First(r => r.Item == Columns).ExtraColors![0] = "#000000"; copy.CompactReadings.First(r => r.Item == Memory).WarningColor = "#000000";
        var kept = settings.CompactReadings.First(r => r.Item == Memory); var keptColumns = settings.CompactReadings.First(r => r.Item == Columns);
        var saved = SettingsMigrations.Read(JsonSerializer.Serialize(settings)).CompactReadings;
        check("Level colors: kept by presets, deep copies and saving", kept.WarningColor == "#ABCDEF" && kept.CriticalColor == "#FEDCBA" && keptColumns.ExtraColors is ["#654321"]
            && saved.First(r => r.Item == Memory).CriticalColor == "#FEDCBA" && saved.First(r => r.Item == Columns).ExtraColors is ["#654321"] && saved.First(r => r.Item == Upload).WarningColor == "#999999");

        settings.CompactReadings.First(r => r.Item == Gpu).Visible = false;
        var before = settings.CompactReadings.Select(r => (r.Item, r.Visible, r.ShowValue)).ToArray();
        CompactMonitor.ResetColors(settings.CompactReadings);
        check("Level colors: resetting clears every color and keeps the rest", settings.CompactReadings.All(r => r.Color is null && r.ExtraColors is null && r.WarningColor is null && r.CriticalColor is null)
            && settings.CompactReadings.Select(r => (r.Item, r.Visible, r.ShowValue)).SequenceEqual(before));
    }

    // WPF throws when a panel's child is replaced through its indexer ("指定的索引已经在使用"), which once left the collapsed
    // readings' table empty; the app's code removes the old child and inserts the new one instead.
    private static void ReplacedChildren(Action<string, bool> check)
    {
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "BrimDeck.sln"))) source = source.Parent;
        if (source is null) { check("Panel children: the source tree is found", false); return; }
        var replaced = Directory.EnumerateFiles(Path.Combine(source.FullName, "src", "BrimDeck"), "*.cs", SearchOption.AllDirectories)
            .Where(file => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(file), @"\.Children\[[^\]]+\]\s*=(?!=)")).Select(Path.GetFileName).ToList();
        check("Panel children: none is replaced through the indexer" + (replaced.Count > 0 ? " (" + string.Join(", ", replaced) + ")" : ""), replaced.Count == 0);
    }

    private static void Highlight(Action<string, bool> check)
    {
        var readings = CompactMonitor.Readings(CompactMonitorMode.Full, CompactMonitorItems.None);
        check("Compact readings: the defaults light Full", CompactMonitor.Preset(readings) == CompactMonitorMode.Full);
        readings.First(r => r.Item == Columns).Visible = false;
        check("Compact readings: one reading off lights no preset", CompactMonitor.Preset(readings) is null);
        CompactMonitor.Apply(readings, CompactMonitorMode.Brief);
        check("Compact readings: Brief after it lights Brief", CompactMonitor.Preset(readings) == CompactMonitorMode.Brief);
        readings.First(r => r.Item == Memory).Visible = false;
        check("Compact readings: Brief less a ring lights no preset", CompactMonitor.Preset(readings) is null);
        readings.First(r => r.Item == Memory).Visible = true; readings.First(r => r.Item == Memory).ShowValue = true;
        check("Compact readings: Brief with a figure lights no preset", CompactMonitor.Preset(readings) is null);
        var settings = new DeckSettings { CompactReadings = readings }; settings.Normalize();
        check("Compact readings: no preset is saved as Custom in the old key", settings.CompactMonitor == CompactMonitorMode.Custom);
        CompactMonitor.Apply(settings.CompactReadings, CompactMonitorMode.Full); settings.Normalize();
        check("Compact readings: Full is saved as Full in the old key", settings.CompactMonitor == CompactMonitorMode.Full && settings.CompactMonitorItems == All);
    }

    private static void Copies(Action<string, bool> check)
    {
        var settings = new DeckSettings();
        settings.CompactReadings = CompactMonitor.Normalize([new() { Item = Gpu, ShowValue = false, Color = "#ABCDEF" }, new() { Item = Upload }, new() { Item = Cpu }]);
        settings.Normalize();
        var copy = settings.Copy();
        copy.CompactReadings[0].Color = "#000000"; copy.CompactReadings.Reverse();
        check("Compact readings: Copy is deep", settings.CompactReadings[0].Item == Gpu && settings.CompactReadings[0].Color == "#ABCDEF");
        var read = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("Compact readings: the table survives saving and reading",
            read.CompactReadings.Select(r => (r.Item, r.Visible, r.ShowValue, r.Color)).SequenceEqual(settings.CompactReadings.Select(r => (r.Item, r.Visible, r.ShowValue, r.Color)))
            && read.CompactMonitor == CompactMonitorMode.Custom && read.CompactMonitorItems == (Gpu | Upload | Cpu));
        var untouched = new DeckSettings { CompactMonitor = CompactMonitorMode.Brief }.Copy();
        check("Compact readings: Copy keeps a table not yet built", CompactMonitor.Preset(untouched.CompactReadings) == CompactMonitorMode.Brief);
    }

    // The order of the collapsed parts: one per style, each part once, kept by copies and by saving.
    private static void PartOrder(Action<string, bool> check)
    {
        CompactPart[] standard = [CompactPart.Rings, CompactPart.Music, CompactPart.Monitor];
        var defaults = new DeckSettings();
        check("Compact order: defaults are rings, music, readings", defaults.NotchOrder.SequenceEqual(standard) && defaults.CapsuleOrder.SequenceEqual(standard));
        var odd = Read("""{"AppVersion":"0.1.0","NotchOrder":[2,9,2,0]}""");
        check("Compact order: unknown and repeated parts are dropped and missing ones added", odd.NotchOrder.SequenceEqual([CompactPart.Monitor, CompactPart.Rings, CompactPart.Music])
            && odd.CapsuleOrder.SequenceEqual(standard));
        var settings = new DeckSettings();
        settings.SetCompactOrder(CompactStyle.Capsule, [CompactPart.Music, CompactPart.Monitor, CompactPart.Rings]);
        var copy = settings.Copy(); copy.SetCompactOrder(CompactStyle.Capsule, standard);
        check("Compact order: each style keeps its own and Copy is deep", settings.CompactOrder(CompactStyle.Capsule)[0] == CompactPart.Music
            && settings.CompactOrder(CompactStyle.Notch).SequenceEqual(standard));
        var read = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("Compact order: survives saving and reading", read.CapsuleOrder.SequenceEqual([CompactPart.Music, CompactPart.Monitor, CompactPart.Rings]));
    }
}
