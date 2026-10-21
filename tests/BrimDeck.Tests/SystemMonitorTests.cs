using BrimDeck.Core;

// The system monitor's pure logic. The SteadyRanking cases are ported from ftop's tests by Nongfsq
// (https://github.com/Nongfsq/ftop, MIT License; see src/BrimDeck/ThirdPartyNotices.txt).
static class SystemMonitorTests
{
    public static void Run(Action<string, bool> check)
    {
        Readings(check);
        Metrics(check);
        Folding(check);
        Ranking(check);
        Formatting(check);
        Layouts(check);
        Pages(check);
        Colors(check);
    }

    private static void Readings(Action<string, bool> check)
    {
        Reading<double> known = 2.5, missing = Reading<double>.Unavailable("no sensor");
        check("Monitor reading keeps its value", known.TryGet(out var value) && value == 2.5 && known.Reason == "");
        check("Monitor unavailable reading keeps its reason and no value", !missing.TryGet(out _) && missing.Reason == "no sensor" && missing.OrDefault == 0);
        check("Monitor default reading is unavailable, not zero", !default(Reading<double>).HasValue && default(Reading<double>).Reason.Length > 0);
        check("Monitor reading maps its value or keeps the reason", known.Map(x => x * 2).OrDefault == 5 && missing.Map(x => x * 2).Reason == "no sensor");
    }

    private static void Metrics(Action<string, bool> check)
    {
        check("Monitor processor usage from idle, kernel and user time", MonitorMetrics.Usage((0, 0, 0), (25, 50, 50)) is { } usage && Math.Abs(usage - .75) < 1e-9);
        check("Monitor processor usage skips an interval without time", MonitorMetrics.Usage((10, 10, 10), (10, 10, 10)) is null);
        check("Monitor processor usage skips counters that went back", MonitorMetrics.Usage((50, 100, 100), (10, 20, 20)) is null);
        check("Monitor rate per second", MonitorMetrics.Rate(1000, 3000, 2) == 1000);
        check("Monitor rate skips a counter reset", MonitorMetrics.Rate(3000, 1000, 1) is null && MonitorMetrics.Rate(1, 2, 0) is null);
        check("Monitor process CPU in percent of one core", MonitorMetrics.CpuPercent(0, 25_000_000, 1) == 250);
        var memory = new MemorySample(100, 50, 40, 100);
        check("Monitor memory level normal", memory.Level == MemoryLevel.Normal && memory.UsedFraction == .5);
        check("Monitor memory level elevated by use or commit", (memory with { Used = 85 }).Level == MemoryLevel.Elevated && (memory with { CommitUsed = 82 }).Level == MemoryLevel.Elevated);
        check("Monitor memory level critical", (memory with { Used = 95 }).Level == MemoryLevel.Critical && (memory with { CommitUsed = 95 }).Level == MemoryLevel.Critical);
        check("Monitor memory without a total reads zero shares", new MemorySample(0, 0, 0, 0) is { UsedFraction: 0, CommitFraction: 0 });
        var core = new CoreSample(0, 0, CoreTier.Performance, 1, .5, 2000d, 4000);
        check("Monitor frequency fraction", core.FrequencyFraction == .5 && (core with { FrequencyMhz = Reading<double>.Unavailable("x") }).FrequencyFraction is null
            && (core with { MaxFrequencyMhz = 0 }).FrequencyFraction is null && (core with { FrequencyMhz = 9000d }).FrequencyFraction == 1);
        var cpu = new CpuSample([core, core with { Id = 1, Group = 1, Usage = .1 }, core with { Id = 2, Group = 1, Usage = .3 }], 3000d);
        check("Monitor CPU usage is the cores' mean", Math.Abs(cpu.Usage - .3) < 1e-9 && cpu.GroupCount == 2 && cpu.Groups.Select(g => g.Count).SequenceEqual([1, 2]));
        check("Monitor CPU without cores", new CpuSample([], 0d) is { Usage: 0, GroupCount: 0, Groups.Count: 0 });
    }

    private static void Folding(Action<string, bool> check)
    {
        ProcessSample[] list =
        [
            new(1, "Chrome", 10, 300, "chrome.exe"), new(2, "Chrome", 30, 100, "CHROME.EXE"), new(3, "Code", 25, 900, "code.exe"),
            new(4, "System", 1, 50), new(5, "Chrome", 5, 200, "chrome.exe")
        ];
        var byCpu = ProcessSample.Folded(list, ProcessSort.Cpu);
        check("Monitor folding sums an app's processes", byCpu[0] is { Pid: 2, CpuPercent: 45, MemoryBytes: 600, Members.Count: 3 });
        check("Monitor folding ranks by CPU", byCpu.Select(p => p.Pid).SequenceEqual([2, 3, 4]));
        check("Monitor folded members are heaviest first", byCpu[0].Members.Select(m => m.CpuPercent).SequenceEqual([30d, 10, 5]));
        check("Monitor a lone process keeps no members", byCpu[1].Members.Count == 0 && byCpu[2].Id == "pid 4");
        var byMemory = ProcessSample.Folded(list, ProcessSort.Memory);
        check("Monitor folding ranks by memory", byMemory.Select(p => p.Pid).SequenceEqual([3, 1, 4]) && byMemory[1].MemoryBytes == 600);
        check("Monitor folding an empty list", ProcessSample.Folded([], ProcessSort.Cpu).Count == 0);
        var lists = new ProcessList(byCpu, byMemory);
        check("Monitor process list picks the ranking", lists.Ranked(ProcessSort.Memory) == byMemory && lists.Ranked(ProcessSort.Cpu) == byCpu);
    }

    private static void Ranking(Action<string, bool> check)
    {
        static IReadOnlyList<(string, double)> E(params (string, double)[] entries) => entries;
        var ranking = SteadyRanking.Cpu();
        bool steady = ranking.Step(E(("a", 20), ("b", 19), ("c", 5)), 0).SequenceEqual(["a", "b", "c"]);
        for (int turn = 0; turn < 20; turn++)
        {
            double first = turn % 2 == 0 ? 18 : 21;
            steady &= ranking.Step(E(("a", first), ("b", 39 - first), ("c", 5)), 1).SequenceEqual(["a", "b", "c"]);
        }
        check("Monitor ranking: close readings do not swap places", steady);

        ranking = SteadyRanking.Cpu();
        ranking.Step(E(("a", 20), ("b", 10), ("c", 5)), 0);
        bool lead = ranking.Step(E(("a", 20), ("b", 10), ("c", 60)), 1).SequenceEqual(["c", "a", "b"]);
        lead &= ranking.Step(E(("a", 20), ("b", 10), ("c", 5)), 1).SequenceEqual(["c", "a", "b"]);
        IReadOnlyList<string> order = [];
        for (int i = 0; i < 20; i++) order = ranking.Step(E(("a", 20), ("b", 10), ("c", 5)), 1);
        check("Monitor ranking: a clear lead takes its place at once and leaves slowly", lead && order.SequenceEqual(["a", "b", "c"]));

        ranking = SteadyRanking.Cpu();
        ranking.Step(E(("a", 20), ("b", 10)), 0);
        bool held = ranking.Step(E(("c", 90), ("b", 80), ("a", 1)), 1, hold: true).SequenceEqual(["a", "b", "c"]);
        held &= ranking.Step(E(("c", 90), ("b", 80)), 1, hold: true).SequenceEqual(["b", "c"]);
        held &= ranking.Step(E(("c", 90), ("b", 40)), 1).SequenceEqual(["c", "b"]);
        check("Monitor ranking: a held ranking keeps its order and puts arrivals last", held);

        ranking = SteadyRanking.Cpu();
        ulong seed = 7;
        double Next() { seed = unchecked(seed * 6_364_136_223_846_793_005 + 1_442_695_040_888_963_407); return (seed >> 40) % 60; }
        bool complete = true;
        for (int round = 0; round < 50; round++)
        {
            var entries = Enumerable.Range(0, 24).Select(i => ("p" + i, Next())).ToList();
            var result = ranking.Step(entries, 1);
            complete &= result.Count == entries.Count && result.ToHashSet().SetEquals(entries.Select(e => e.Item1));
        }
        var still = Enumerable.Range(0, 24).Select(i => ("p" + i, i * 4d)).ToList();
        for (int i = 0; i < 60; i++) order = ranking.Step(still, 1);
        var values = order.Select(id => double.Parse(id[1..]) * 4).ToList();
        bool settled = values.Select((value, index) => values.Take(index).All(above => value <= above * (1 + ranking.Relative) + ranking.Margin)).All(ok => ok);
        check("Monitor ranking: no entry stays below one it clearly beats", complete && settled);

        var samples = new List<ProcessSample> { new(1, "a", 5, 0, "a"), new(2, "b", 50, 0, "b") };
        var arranged = SteadyRanking.Cpu().Arrange(samples, s => s.CpuPercent, 0, false);
        check("Monitor ranking arranges samples", arranged.Select(s => s.Pid).SequenceEqual([2, 1]));
    }

    private static void Formatting(Action<string, bool> check)
    {
        check("Monitor percent", MonitorFormat.Percent(.374) == "37" && MonitorFormat.Percent(1.4) == "100" && MonitorFormat.Percent(double.NaN) == "0");
        check("Monitor process share", MonitorFormat.ProcessShare(250, 10) == "25" && MonitorFormat.ProcessShare(12, 8) == "1.5"
            && MonitorFormat.ProcessShare(5000, 8) == "100" && MonitorFormat.ProcessShare(5, 0) == "5.0");
        check("Monitor rates", MonitorFormat.Rate(84_200) == new MonitorFormat.Quantity("84", "KB/s") && MonitorFormat.Rate(1_240_000) == new MonitorFormat.Quantity("1.2", "MB/s")
            && MonitorFormat.Rate(2_500_000_000) == new MonitorFormat.Quantity("2.5", "GB/s") && MonitorFormat.Rate(999_600).Unit == "MB/s" && MonitorFormat.Rate(-5).Number == "0");
        check("Monitor gigabytes", MonitorFormat.Gigabytes(13_314_398_618) == "12.4" && MonitorFormat.GigabytesCompact(34_359_738_368) == "32"
            && MonitorFormat.GigabytesCompact(16_965_120_000) == "15.8");
        check("Monitor process memory", MonitorFormat.ProcessMemory(536_870_912) == "512M" && MonitorFormat.ProcessMemory(4_402_341_478) == "4.1G");
        check("Monitor gigahertz", MonitorFormat.Gigahertz(3210) == "3.21");
    }

    private static void Layouts(Action<string, bool> check)
    {
        var standard = MonitorLayout.For(580, 160, true);
        check("Monitor layout: a standard page lists processes beside the readings", standard is { SideList: true, Processes: true, CompactMetrics: false } && standard.Rows == 6);
        check("Monitor layout: narrow pages drop the list", MonitorLayout.For(440, 160, true) is { Processes: false, SideList: false, ListWidth: 0 });
        check("Monitor layout: tall narrow pages stack the list", MonitorLayout.For(440, 360, true) is { SideList: false, Processes: true, Rows: 8 });
        check("Monitor layout: short pages put the figures on one line", MonitorLayout.For(560, 100, true) is { CompactMetrics: true, Figure: 16 });
        check("Monitor layout: a switched-off list never shows", MonitorLayout.For(1200, 400, false) is { Processes: false });
        check("Monitor layout: rows are capped", MonitorLayout.For(1200, 2000, true).Rows == MonitorLayout.MaximumRows);
        check("Monitor layout: list width is bounded", MonitorLayout.For(1200, 300, true).ListWidth == 400 && MonitorLayout.For(560, 300, true).ListWidth == 235);
        check("Monitor layout: nonsense sizes", MonitorLayout.For(double.NaN, -5, true) is { Processes: false, Rows: 0 });
    }

    private static void Pages(Action<string, bool> check)
    {
        var settings = new DeckSettings();
        check("Pages: monitor sits between usage and music", DeckPages.Visible(settings).SequenceEqual([DeckPage.Usage, DeckPage.Monitor, DeckPage.Music]));
        check("Pages: a shown page resolves to itself", DeckPages.Resolve(settings, DeckPage.Monitor) == DeckPage.Monitor);
        settings.UsagePage = false;
        check("Pages: a hidden page falls back to the first shown one", DeckPages.Resolve(settings, DeckPage.Usage) == DeckPage.Monitor);
        settings.MonitorPage = false;
        check("Pages: the music page alone", DeckPages.Resolve(settings, DeckPage.Monitor) == DeckPage.Music);
        settings.MusicPage = false;
        check("Pages: with every page off the usage page says so", DeckPages.Resolve(settings, DeckPage.Music) == DeckPage.Usage && DeckPages.Visible(settings).Count == 0);
        check("Pages: opening choices", DeckPages.Opening(DefaultDeckPage.Monitor) == DeckPage.Monitor && DeckPages.Opening(DefaultDeckPage.Last) is null
            && DeckPages.Opening(DefaultDeckPage.Usage) == DeckPage.Usage && DeckPages.Opening(DefaultDeckPage.Music) == DeckPage.Music);
        var sizes = new DeckSettings { Width = 500, Height = 150, MusicWidth = 700, MusicHeight = 180, MonitorWidth = 600, MonitorHeight = 300 };
        check("Pages: the window holds the largest shown page", DeckPages.Largest(sizes) == (700, 300));
        sizes.MusicPage = false; sizes.MonitorPage = false;
        check("Pages: only shown pages size the window", DeckPages.Largest(sizes) == (Math.Max(500, sizes.MinimumWidth), 150));
        check("Pages: each page's own size", DeckPages.Size(sizes, DeckPage.Monitor) == (600, 300) && DeckPages.Size(sizes, DeckPage.Music) == (700, 180));
        var saved = new DeckSettings { MonitorWidth = 5000, MonitorHeight = double.NaN, MonitorInterval = 9, MonitorPalette = (MonitorPalette)42, MonitorSort = (ProcessSort)7, LastPage = (DeckPage)9 };
        saved.Normalize();
        check("Pages: monitor settings are normalized", saved is { MonitorWidth: 1200, MonitorInterval: 2, MonitorPalette: MonitorPalette.Sea, MonitorSort: ProcessSort.Cpu, LastPage: DeckPage.Usage }
            && saved.MonitorHeight == MonitorSizes.Default.Height);
        check("Pages: enum values stay saved as before", (int)DeckPage.Usage == 0 && (int)DeckPage.Music == 1 && (int)DeckPage.Monitor == 2 && (int)DefaultDeckPage.Monitor == 3);
        check("Pages: monitor quick sizes", MonitorSizes.For(PanelSize.Standard) == MonitorSizes.Default && MonitorSizes.For(PanelSize.Compact).Width < MonitorSizes.For(PanelSize.Spacious).Width);
    }

    private static void Colors(Action<string, bool> check)
    {
        check("Color mix ends and middle", ColorMath.Mix("#000000", "#FFFFFF", 0) == "#000000" && ColorMath.Mix("#000000", "#FFFFFF", 1) == "#FFFFFF"
            && ColorMath.Mix("#000000", "#FF8040", .5) == "#804020" && ColorMath.Mix("#102030", "#FFFFFF", double.NaN) == "#102030");
        var sea = MonitorColors.For(MonitorPalette.Sea);
        check("Monitor core colors run from performance to efficiency", sea.Core(0, 3) == sea.Performance && sea.Core(2, 3) == sea.Efficiency && sea.Core(0, 1) == sea.Performance && sea.Core(9, 2) == sea.Efficiency);
        check("Monitor palettes differ in light and dark", Enum.GetValues<MonitorPalette>().All(p => MonitorColors.For(p, true) != MonitorColors.For(p, false)));
        check("Monitor memory colors follow the level", sea.Memory(MemoryLevel.Critical) == sea.Critical && sea.Memory(MemoryLevel.Elevated) == sea.Warning && sea.Memory(MemoryLevel.Normal) == sea.Normal);
        check("Monitor palette names", Enum.GetValues<MonitorPalette>().Select(MonitorColors.Name).Distinct().Count() == 3);
    }
}
