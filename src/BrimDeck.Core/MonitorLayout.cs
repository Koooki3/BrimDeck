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

public enum ListPlacement { None, Side, Below }
// How memory and network are shown: both on one line, one line each, or as blocks with their second lines and sparklines.
public enum MetricsMode { Line, Lines, Blocks }

// How the system monitor page fills the space it has, after ftop's layout ladder: a small page keeps the essentials, a
// large one shows more rather than bigger type. Sizes are DIPs of the page's content area, below its header.
// The ladder has steps only: within a step the page stretches (the list's share, its rows and columns, the graph's height), so
// resizing the panel by dragging rebuilds the page only where it crosses a step.
// Metrics: how much room memory and network take (MetricsMode). ColumnLabels: each core column carries its figure underneath,
// as on ftop's large panel.
public sealed record MonitorLayout(double Figure, double Body, double Caption, MetricsMode Metrics, ListPlacement List, double RowHeight, bool ColumnLabels)
{
    public bool CompactMetrics => Metrics == MetricsMode.Line;
    public bool Processes => List != ListPlacement.None;
    // The most rows one column of the list shows, however tall the page, and the most columns a wide list has.
    public const int MaximumRows = 14, MaximumColumns = 2;
    // Fewer rows than this are not worth a list.
    public const int MinimumRows = 2;
    // The list's own header: its title and the sort switch.
    public const double ListHead = 26;
    // The readings keep this much height when the list sits below them.
    public const double StackedReadings = 170;
    // A side list takes this share of the width, within these bounds; a list this wide or wider has two columns.
    public const double ListShare = .42, ListMinimum = 220, ListMaximum = 560, TwoColumns = 470;

    public static MonitorLayout For(double width, double height, bool processes)
    {
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        height = double.IsFinite(height) ? Math.Max(0, height) : 0;
        double figure = height < 120 ? 16 : height < 200 ? 19 : 23;
        double body = height < 120 ? 11 : 12, caption = height < 120 ? 10 : 10.5;
        double row = height < 160 ? 19 : 22;
        var list = !processes ? ListPlacement.None
            : width >= 560 && height - ListHead >= MinimumRows * row ? ListPlacement.Side
            : height >= StackedReadings + ListHead + MinimumRows * row ? ListPlacement.Below : ListPlacement.None;
        // The core columns need height to read; memory and network give theirs first. Readings above a list have its fixed height.
        double readings = list == ListPlacement.Below ? StackedReadings : height;
        var metrics = readings < 150 ? MetricsMode.Line : readings < 250 ? MetricsMode.Lines : MetricsMode.Blocks;
        bool labels = height >= 270 && list != ListPlacement.Below;
        return new(figure, body, caption, metrics, list, row, labels);
    }

    // The rows and columns a list of this size shows.
    public static (int Rows, int Columns) ListGrid(double width, double height, double rowHeight)
    {
        if (!(rowHeight > 0) || !double.IsFinite(width) || !double.IsFinite(height)) return (0, 0);
        int rows = Math.Clamp((int)Math.Floor((height - ListHead) / rowHeight), 0, MaximumRows);
        if (rows < MinimumRows) return (0, 0);
        return (rows, width >= TwoColumns ? MaximumColumns : 1);
    }

    // The side list's width in a page of this width: its share, within its bounds, leaving the readings at least 260 DIP.
    public static double SideListWidth(double width) => Math.Clamp(Math.Round(width * ListShare), ListMinimum, Math.Min(ListMaximum, Math.Max(ListMinimum, width - 260)));
}
