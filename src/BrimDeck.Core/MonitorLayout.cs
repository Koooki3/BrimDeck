namespace BrimDeck.Core;

// The system monitor page's color schemes: ftop's three palettes by Nongfsq (https://github.com/Nongfsq/ftop, MIT License;
// see ThirdPartyNotices.txt), and Theme, which takes the colors of the panel theme in use (the app resolves it, as only it
// knows the theme). Saved as numbers, so new members go at the end.
public enum MonitorPalette { Sea, Graphite, Warm, Theme }

// The colors of one of ftop's palettes on a dark or a light panel theme. Theme has no fixed colors and gives Sea's here.
public sealed record MonitorColors(string Performance, string Efficiency, string Gpu, string Normal, string Warning, string Critical)
{
    public static MonitorColors For(MonitorPalette palette, bool dark = true)
    {
        var (performance, efficiency, gpu) = (palette, dark) switch
        {
            (MonitorPalette.Graphite, true) => ("#F3F4F6", "#9AA1AD", "#C9B98F"),
            (MonitorPalette.Graphite, false) => ("#1F2937", "#6B7280", "#8A6D2F"),
            (MonitorPalette.Warm, true) => ("#FF8468", "#FFC857", "#FF8FC4"),
            (MonitorPalette.Warm, false) => ("#DC4A2B", "#B9770A", "#B83280"),
            (_, true) => ("#5B9BFF", "#45D1BD", "#B294FF"),
            _ => ("#1F5FE0", "#0B8F7E", "#7A4FD6")
        };
        return dark ? new(performance, efficiency, gpu, "#4FD17F", "#F0B54A", "#FF6B5E") : new(performance, efficiency, gpu, "#1C9A50", "#B26A00", "#D23F31");
    }

    // The Theme scheme from a theme's colors (opaque "#RRGGBB"): the accent for the fastest cores, and for the slowest cores and
    // the GPU the first of the theme's other colors (cyan, pink, lime, amber in that order) that is clearly not a color already
    // taken, so a theme whose accent is its cyan still tells the kinds of core apart.
    public static MonitorColors FromTheme(string accent, IReadOnlyList<string> others, string normal, string warning, string critical)
    {
        const double Apart = 64;
        string Pick(params string[] taken) => others.FirstOrDefault(color => taken.All(other => ColorMath.Distance(color, other) >= Apart))
            ?? others.Where(color => !taken.Contains(color, StringComparer.OrdinalIgnoreCase)).DefaultIfEmpty(taken[^1]).First();
        string efficiency = Pick(accent), gpu = Pick(accent, efficiency);
        return new(accent, efficiency, gpu, normal, warning, critical);
    }

    // Core groups run from the performance color to the efficiency color in even steps, so a processor with any number of
    // kinds of core has a color for each.
    public string Core(int group, int groups) => ColorMath.Mix(Performance, Efficiency, groups > 1 ? Math.Clamp(group, 0, groups - 1) / (double)(groups - 1) : 0);
    public string Memory(MemoryLevel level) => level switch { MemoryLevel.Critical => Critical, MemoryLevel.Elevated => Warning, _ => Normal };

    public static string Name(MonitorPalette palette) => palette switch
    {
        MonitorPalette.Graphite => Loc.T("石墨", "Graphite"), MonitorPalette.Warm => Loc.T("暖色", "Warm"), MonitorPalette.Theme => Loc.T("跟随主题", "Theme"),
        _ => Loc.T("海洋", "Sea")
    };
}

// How the system monitor page fills the space it has, after ftop's layout ladder: a small page keeps the essentials, a
// large one shows more rather than bigger type. Sizes are DIPs of the page's content area, below its header.
// SideList: the process list takes a column at the right; otherwise, with room, it sits below the readings.
// CompactMetrics: memory, network and GPU share one line under the core columns instead of a row each.
public sealed record MonitorLayout(double Figure, double Body, double Caption, bool CompactMetrics, bool SideList, double ListWidth, int Rows, double RowHeight)
{
    public bool Processes => Rows > 0;
    // The most rows the list shows, however tall the page.
    public const int MaximumRows = 14;
    // Fewer rows than this are not worth a list.
    public const int MinimumRows = 2;
    // The list's own header: its title and the sort switch.
    public const double ListHead = 24;
    // The readings keep this much height when the list sits below them.
    public const double StackedReadings = 160;

    public static MonitorLayout For(double width, double height, bool processes)
    {
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        height = double.IsFinite(height) ? Math.Max(0, height) : 0;
        double figure = height < 120 ? 16 : height < 200 ? 20 : 24;
        double body = height < 120 ? 11 : 12, caption = height < 120 ? 10 : 10.5;
        double row = height < 160 ? 18 : 21;
        bool compact = height < 130;
        bool side = processes && width >= 560;
        double list = side ? Math.Clamp(Math.Round(width * .42), 220, 400) : width;
        double room = side ? height - ListHead : processes && height >= StackedReadings + ListHead + MinimumRows * row ? height - StackedReadings - ListHead : 0;
        int rows = Math.Clamp((int)Math.Floor(room / row), 0, MaximumRows);
        if (rows < MinimumRows) { rows = 0; side = false; list = 0; }
        return new(figure, body, caption, compact, side, list, rows, row);
    }
}
